# Client driver without focus stealing

The `yaat-client-driver` MCP drives YAAT.Client without showing a window in front of the user or taking focus, so agents can work while the user uses the desktop. This is the top priority (user 2026-10-01). It comes before the FOLLOW montage recordings resume. Index line: [MAIN.md](./MAIN.md), at the top of **Bug reports and feature requests**. Friction log: [client-driver-mcp-friction.md](./client-driver-mcp-friction.md).

## Map (exploration 2026-10-01, main @ 439e01b7)

- **The MCP itself foregrounds only in real mode.**
  - `NativeInput.TryForeground` (`SetForegroundWindow`) is called from three real-mode paths: click, typing (`FocusForRealTyping`) and screenshot (`InspectTools` ~:142).
  - Real-mode input also moves the cursor and calls `SendInput`/`SendKeys` (friction #10).
  - UIA `SetFocus` (`InputTools.FocusElement`) and `ValuePattern.SetValue` also foreground Avalonia windows.
  - No `ShowWindow`, `BringToFront` or `AttachThreadInput` call exists.
- **Virtual mode is the default.**
  - Input is `PostMessageW` to the HWND, with UIA used to find elements and invoke patterns. Screenshots are `PrintWindow(PW_RENDERFULLCONTENT)` for YAAT windows.
  - It touches neither the cursor nor the foreground.
  - Its limits:
    - Modifier keys are refused, because Avalonia reads `GetKeyboardState`.
    - CRC (WPF/OpenGL) needs real mode.
    - The native file picker (`PickerHost.exe`) is outside the client's UIA tree.
    - Target finding needs a visible, non-iconic, non-cloaked window with an on-monitor UIA rect (`RequireVirtualTarget`, `TopmostWindowOver`, `CentreOf`, `WindowCapture.EnsureArea`).
- **`launch_yaat` launches a normal, visible, activated window.** It runs `Process.Start` with only `YAAT_APPDATA_DIR`.
- **The client activates itself in places, whatever the MCP does:**
  - `WindowGeometryHelper.ApplyGeometry` → `Activate()` on non-startup applies;
  - `RestoreAndActivate` on profile apply;
  - pop-outs re-opening;
  - `FocusActiveCommandInput`, driven by `MainViewModel.RequestCommandInputFocus`;
  - `WindowGroupRaiser`'s `Topmost` pulse;
  - `CenterOwner` dialogs.

  The client has no launch switch for a minimised, off-screen, non-activating or headless start. Its CLI takes only `--autoconnect` and `--scenario`.
- **A no-window precedent already exists.**
  - `tools/Yaat.GuideCapture` runs the real `App`/`MainWindow` on `UseHeadless(UseHeadlessDrawing=false).UseSkia()`. It drives the window through `HeadlessUnitTestSession`, captures with `CaptureRenderedFrame()`, and boots an in-process server.
  - `tests/Yaat.Client.UI.Tests` use `Avalonia.Headless.XUnit` input helpers.
  - The process-wide SharpHook key hook (`App.GlobalKeyHookEnabled`, `Program.cs` ~:115) must be off in any headless host.
- **The montage depends on this.** WGC video needs a composited window, so the capture path needs a visible-but-never-activated window or a headless frame stream.

## Candidate approaches (from the map; to be checked against the research)

- **(a) A never-activated window, posted input and `PrintWindow`/WGC.**
  - Client: a background switch (non-activating show, suppress the self-`Activate()` calls, skip the geometry clamp).
  - MCP: `launch_yaat` passes the switch, and target finding works for an off-screen or behind window.
  - This is the smallest change.
  - It depends on whether Avalonia keeps rendering and exposing UIA rects when parked off-screen, minimised or cloaked.
- **(b) A separate Windows desktop.** It gives full isolation, but the MCP must run on that desktop, and GPU rendering there is unverified. Experiment only.
- **(c) A headless automation host.**
  - The client runs on Avalonia headless, and the MCP talks to it over a pipe: Avalonia headless input, a visual or automation-peer tree, and `CaptureRenderedFrame`.
  - It never touches the desktop, but it is a second backend for every tool.
  - It does not cover CRC or real Win32 behaviour, and file dialogs need an injected picker.
- **(d) DWM cloak.** Only the owning process can cloak its window, so it is a client switch like (a), and today's target finding treats cloaked windows as not shown.

## Survey of other desktop-automation MCPs (2026-10-01)

Full report with citations: [docs/research/2026-10-01-desktop-automation-mcp-survey.md](../research/2026-10-01-desktop-automation-mcp-survey.md).

**No tool that drives the app from outside its process fully avoids focus stealing.**
- Windows-MCP, terminator, WinAppDriver, NovaWindows and FlaUI-MCP all foreground the window or move the real cursor.
- DCU and ScreenShotTool get closest. They use UIA patterns, posted messages, `PrintWindow` and a foreground save/restore guard, but they document leaks.

**The tools that never steal focus run inside the app:**
- AvaloniaMcp, Zafiro.Avalonia.Mcp (Avalonia 12) and the official Avalonia DevTools MCP for Avalonia; SnoopWpfMcp for WPF.
- Each one runs a named pipe inside the app, with a discovery file per process ID.
- The work runs on the UI thread: commands or view-model calls first, then synthetic pointer and key events.
- Screenshots use `RenderTargetBitmap`. Avalonia documents that it needs a visible window, which need not be the active one.
- None of them records video.

**Video limits.** `PrintWindow` is too slow to drive video (measured at 1–5 s per capture). Windows Graphics Capture skips minimised windows, draws a yellow border unless the user consents to borderless capture, and on 24H2 sends a frame only when the content changes.

**Ideas the survey ranks highest:**
1. Record the radar view from inside the client. A second, fixed 1920×1080 Skia surface, drawn from the same snapshot on a 30/60 Hz clock, is piped to ffmpeg. Audio comes from the client itself or from per-process loopback capture. The window can then be hidden.
2. Add an automation pipe inside the client, on the AvaloniaMcp/Zafiro pattern (selectors, `wait_for`, event subscriptions, structured errors, screenshots that include popups). It replaces UIA plus posted messages for YAAT.
3. Harden the outside-the-app driver, DCU-style, for CRC, which we do not own.

## Win32 and Avalonia facts (research 2026-10-01)

Full report with citations to Avalonia 12.1.3 source and Microsoft Learn: [docs/research/2026-10-01-background-window-automation.md](../research/2026-10-01-background-window-automation.md).

- **Capture.**
  - WGC captures a window that is behind other windows.
  - A minimised Avalonia window stops rendering (`Window.cs`: `StopRendering()`), and restoring it calls `SetForegroundWindow`.
  - What WGC returns for a cloaked window or one on another virtual desktop is not documented.
  - WGC draws a yellow border unless borderless consent is granted (packaged capability).
  - `PrintWindow` cannot capture Avalonia: its window uses `WS_EX_NOREDIRECTIONBITMAP` and has no `WM_PRINT` handler.
- **Input without focus.**
  - UIA patterns need no focus.
  - Avalonia implements Invoke, Value, Toggle, SelectionItem, ExpandCollapse and ScrollItem on the common controls. A plain `MenuItem` has no Invoke, and every provider call blocks on the UI thread.
  - Posted mouse and key messages reach Avalonia unfocused, with two limits: modifiers come from the real keyboard state, and a posted click calls `SetFocus` on the window.
- **Not activating.**
  - `ShowActivated = false` maps to `SW_SHOWNOACTIVATE`.
  - Avalonia itself still calls `Activate()` after any modal dialog closes, on restore from minimised, and on a click into a modally disabled owner.
  - A popup of an off-screen window is moved onto a real monitor (`ManagedPopupPositioner`) unless `OverlayPopups = true`.
- **Separate desktop.** UIA cannot cross desktops (`WM_GETOBJECT`), and GPU rendering there is not documented.
- **Headless.**
  - Avalonia.Headless gives Skia frames (`CaptureRenderedFrame`), simulated input with explicit modifiers, and a real dispatcher and timers.
  - It has no UIA and no storage provider.
  - Long interactive runs are not documented as a supported use.
- **Audio.** Per-process loopback (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`) captures by process ID whatever the window state.

## Decisions (user 2026-10-01)

- **Driving: an automation pipe inside the client**, on the AvaloniaMcp/Zafiro pattern.
  - When an env var is set, the client hosts a named-pipe automation endpoint. It offers the element tree, invoke/set/click/type raised on the UI thread, `RenderTargetBitmap` stills and `wait_for`.
  - In that mode the window is shown never-activated (`ShowActivated = false`, dialogs included), `OverlayPopups` is on, and the client's own `Activate()`/`Topmost` calls are suppressed.
  - The MCP's YAAT tools switch to the pipe. Real mode stays for the Win32 input path. CRC keeps the outside-the-app driver.
- **Montage video: WGC of a never-activated window.** A real window, kept behind other windows, captured with Windows Graphics Capture; client audio by per-process loopback. It needs a launch that never activates or minimises (a minimised Avalonia window stops rendering), and the yellow border handled (borderless consent, or a crop).
- **Feature branch** `feat/client-driver-background`.
- **One switch, `YAAT_AUTOMATION=1`** (user 2026-10-01): it turns on both the pipe endpoint and the never-activated window; `launch_yaat` always sets it.
- **Pipe protocol: our own host in Zafiro's format** (user 2026-10-01; research: [docs/research/2026-10-01-in-app-automation-pipe-protocols.md](../research/2026-10-01-in-app-automation-pipe-protocols.md)). About 1,000–1,500 lines, mostly copied from Zafiro.Avalonia.Mcp (MIT):
  - Zafiro's line-delimited `{id, method, params}` JSON with coded errors and recovery hints, a pipe per process with a discovery file per PID, the pipe restricted to the current user.
  - Zafiro's CSS-like selectors without the C# predicates (no Roslyn in the client), stable element ids across calls, popups and owned windows in the tree.
  - Today's tool names one for one, plus `wait_for` over the same selectors.
  - Click: the meaningful action first (command, toggle, select), then a synthetic click with button, modifiers and double-click; `click_point` in window-relative coordinates for the radar and ground views.
  - Keys: walk the window's `KeyBindings` for shortcuts, typed characters as text events at the caret (so per-keystroke autocomplete runs). No Avalonia private input API (AVA3001 under warnings-as-errors).
  - Screenshots by `RenderTargetBitmap` at the monitor's real scale, popups included through `OverlayPopups`.
  - Out of reach in-app, kept on the outside driver or left out: push-to-talk through the SharpHook hook, drags, hover.
- **File picker: an injected picker in automation mode** (user 2026-10-01): the client's storage-provider calls go to a pipe-answered picker, so the agent's load/save tool passes a path and no dialog opens.
- **Activation: suppress all and use no modals** (user 2026-10-01): one gate skips every client self-`Activate()`/`Topmost` pulse in the map's list (geometry apply, profile restore, pop-out reopen, command-input focus, `WindowGroupRaiser`, `CenterOwner`), and dialogs show non-modal in automation mode so Avalonia never calls `owner.Activate()`.

- **Dialogs: one `ShowModeless<T>` helper** (user 2026-10-01): every `ShowDialog` site (24: 16 in `MainWindow.axaml.cs`, the rest in `CommandInputView`, `FavoritesEditorWindow`, `FavoritesBarView`, `ManageWindowProfilesDialog`, `SettingsWindow`) goes through it; in automation mode it disables the owner by hand, shows with `Show(owner)` and returns a `Task<T>` completed on close, otherwise it calls `ShowDialog` as today.
- **Picker answers: a `queue_file_pick` tool** (user 2026-10-01): the agent queues paths (or an explicit cancel); the next picker call takes the head; a picker call on an empty queue fails at once with a clear error.
- **Discord Rich Presence off in automation mode** (user 2026-10-01), beside the global key hook.
- **Settled from the plan, no question needed:** MCP ids stay `eN`, mapped to pipe node ids; a YAAT pid with a live pipe routes to the pipe, everything else (CRC) keeps UIA unchanged; `set_input_mode` does not affect pipe-routed calls; the tool descriptions and docs move from UIA control types (`Edit`) to Avalonia ones (`TextBox`).

## Pipe host map (exploration 2026-10-01, main @ 8b148cd3)

- **Host:** `src/Yaat.Client/Automation/` (not Client.Core, which the web front-ends consume). `Program.Main` sets `App.AutomationEnabled` from `YAAT_AUTOMATION` beside `GlobalKeyHookEnabled`/`DiscordRichPresenceAvailable` (~:115-116); `BuildAvaloniaApp` adds `Win32PlatformOptions { OverlayPopups = true }` under the flag; the host starts in `App.OnFrameworkInitializationCompleted` after `desktop.MainWindow = new MainWindow()` (~:87), so a headless test constructs it explicitly.
- **Self-activation sites:** `WindowGeometryHelper.ApplyGeometry` (~:313-316) and its `Topmost` sets (~:309, 485, 657); `WindowActivationExtensions.RestoreAndActivate` and its six callers; `MainWindow.FocusActiveCommandInput` (~:3769-3779) fed by `MainViewModel.RequestCommandInputFocus`; `WindowGroupRaiser`'s pulse (~:145-146); first-open `Topmost` in `VTdlsViewWindow`/`VStripsViewWindow`. `Issue360MinimizedWindowRestoreTests` touches `Activate()` and needs the gate off.
- **Picker:** `IFilePickerService` (Core) already returns strings; 13 `new AvaloniaFilePickerService(...)` sites become one factory, and `SpeechDebugWindow` (~:166) stops calling `StorageProvider` directly.
- **Zafiro files to copy** (clone in `.tmp/research/zafiro/src/`, MIT, attribution header): the selector parser/AST, the protocol envelope and error codes, `DiscoveryInfo`, `DiagnosticServer`, `NamedPipeTransport`, `DiscoveryWriter`, `SelectorEngine`, `NodeRegistry`/`NodeInfoBuilder`, the tree, `list_windows` and `wait_for` handlers, the request dispatcher, and the semantic click ladder from `InputHandler`; drop the Roslyn `dc:` predicates. Rewrite the screenshot, the key/text path and `click_point`. The MCP side copies the protocol types (no `Yaat.Client` reference) and models its pipe client on Zafiro's `AppConnection`/`TransportClient`.
- **MCP seam:** no per-process target abstraction today; `ElementRegistry` resolves `eN` to UIA elements. Put an element-target behind the registry (UIA element or pipe pid + node id) and route each tool by "pipe connected for this pid". `launch_yaat` sets `YAAT_AUTOMATION=1` and waits for the discovery file or a `ping` instead of a UIA window.
- **Tests:** `tests/Yaat.Client.UI.Tests` (Avalonia.Headless.XUnit, `UseHeadlessDrawing = true`) can test transport, tree, selectors, click and type in-process; screenshots need a Skia headless host (`UseHeadlessDrawing = false` + `UseSkia()`, as GuideCapture), a separate assembly since the attribute is per-assembly. No MCP tests project exists; `live-check.ps1 -Background` asserts the foreground and cursor never change.
- **Hotspots:** `MainWindow.axaml.cs` (dialog and activation sites) and `MainViewModel.cs` (focus event); no overlap with the context-menu feature's open work.

## Briefs

1. Automation-mode gate and never-activated window: `AutomationMode`, `Program`/`App`, the activation sites, Discord and key hook off. Proving: a UI test with the mode on calls `ApplyGeometry`, `RestoreAndActivate` and the raiser and asserts no activation, plus a source test that no raw `.Activate()` sits outside the gate.
2. `ShowModeless<T>` and the 24 dialog sites. Proving: a UI test that a result-returning dialog in automation mode leaves the owner disabled, returns its value on close and never activates the owner.
3. Pipe host skeleton: transport, discovery, protocol, node registry, selectors, tree, `list_windows`, `ping`. Proving: a headless UI test connects over the pipe and asserts `list_windows`, the tree and an ambiguous-selector error.
4. Input, screenshot, `wait_for`: semantic click then synthetic pointer, window-relative `click_point`, `KeyBindings` walk plus text events, `set_text`, `focus`, `RenderTargetBitmap` at the real scale. Proving: typing into the command input runs autocomplete, a window shortcut fires, a radar `click_point` lands, a non-empty PNG.
5. Injected file picker and `queue_file_pick`'s pipe method. Proving: with the mode on, an open call returns the queued path, an empty queue fails at once, no `StorageProvider` call.
6. MCP routing, `launch_yaat`, docs (`docs/client-driver-mcp.md` and its four wrong lines, the Task Index row, the glossary terms "automation mode", "never-activated window", "injected picker"). Proving: `live-check.ps1 -Background` in pipe mode with zero foreground or cursor change.

## Open decisions

1. The WGC border (consent versus a crop margin) and whether 24H2's "frame only on change" breaks a steady frame rate: **decided after a spike** (user 2026-10-01) that measures both on a never-activated YAAT window, before the montage capture step is briefed.

## Docs this item corrects

`docs/client-driver-mcp.md` is wrong in four places:
- ~:36 says screenshots foreground the window; only real mode does.
- ~:56 and the `send_keys` tool description (`InputTools.cs` ~:135) give a file-dialog recipe that fails, because the picker runs in `PickerHost.exe` (friction #21).
- ~:61 says sim-rate dropdown clicks fail; they work (friction #26).

Task Index row for `docs/architecture.md`: "make the client driver work without a visible or foreground window" → `docs/client-driver-mcp.md` → `NativeInput.cs` → `Tools/InputTools.cs` → `Tools/InspectTools.cs` → `WindowCapture.cs` → `Tools/ProcessTools.cs` → client `Program.cs`, `App.axaml.cs`, `WindowGeometryHelper.cs`, `WindowGroupRaiser.cs`, `MainWindow.axaml.cs` → precedent `tools/Yaat.GuideCapture`.
