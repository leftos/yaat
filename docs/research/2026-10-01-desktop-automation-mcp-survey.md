# Survey: desktop-automation MCP servers for WPF / Avalonia / Windows apps, and how they avoid focus stealing

Research date: 2026-10-01. Star counts and last-push dates come from `gh api repos/<owner>/<repo>` run that day. Code claims come from shallow clones of each repo's default branch, made the same day. Each claim names the file it came from.

Goal: borrow ideas for `yaat-client-driver` (UIA-based, out of process), whose main problem is that it steals focus and raises the YAAT window while the user is working. We want to drive and screenshot the app, and record 1920x1080 video of the radar with app audio, while the window stays in the background or hidden.

## 1. Comparison table

| # | Project | Stars / last push | Lang | Licence | How it reaches the UI | Input | Screenshot | Background / no-focus? |
|---|---|---|---|---|---|---|---|---|
| 1 | [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) | 7591 / 2026-09-30 | Python | MIT | UIA out of process | Real cursor (`uia.SetCursorPos`, `uia.Click`), `SetForegroundWindow` + `AttachThreadInput` to switch apps | Whole screen: dxcam, then mss, then pillow | **No.** Foreground by design |
| 2 | [mediar-ai/terminator](https://github.com/mediar-ai/terminator) | 1646 / 2026-06-02 | Rust | MIT | UIA out of process | UIA patterns, plus `SendInput` and `SetForegroundWindow` as fallbacks; `save_focus_state` / `restore_focus_state` | `xcap` monitor capture | **Partly.** The README says it runs "in the background", but the code activates the window and restores focus afterwards |
| 3 | [shanselman/FlaUI-MCP](https://github.com/shanselman/FlaUI-MCP) | 103 / 2026-07-08 | C# | MIT | FlaUI (UIA3) out of process | `Invoke` pattern first, then `Mouse.Click`; keys need `element.Focus()` and then `Keyboard.Type` | Normal capture, or opt-in `background:true` using `PrintWindow(PW_RENDERFULLCONTENT)` with a blank-frame fallback | **Screenshots only.** Keyboard "is focus-dependent" |
| 4 | [AutomateThePlanet/appium-novawindows-driver](https://github.com/AutomateThePlanet/appium-novawindows-driver) (WinAppDriver successor for Appium) | 79 / 2026-08-27 | TS | Apache-2.0 | UIA via PowerShell | `SendInput` via koffi; `trySetForegroundWindow` | `Graphics.CopyFromScreen` of the window rect | **No** |
| 5 | [microsoft/WinAppDriver](https://github.com/microsoft/WinAppDriver) | 4052 / 2025-04-14 | C# | MIT | UIA | WebDriver actions with real input | Screen | **No.** Has long-open focus issues (#1062, #1449, #1947); effectively unmaintained |
| 6 | [dev-willbird1936/Desktop-Computer-Use (DCU)](https://github.com/dev-willbird1936/Desktop-Computer-Use) | 7 / 2026-07-28 | C# | MIT | UIA out of process on a dedicated STA thread | UIA patterns first, then `PostMessage` fallbacks; a **FocusGuard** restores the foreground window, focus and caret | `PrintWindow(PW_RENDERFULLCONTENT)` on a worker with a timeout | **Yes, explicitly** (best effort) |
| 7 | [Ding-Ding-Projects/lowlevel-computer-use-mcp](https://github.com/Ding-Ding-Projects/lowlevel-computer-use-mcp) (fork of codingmachineedge's) | 4 / 2026-09-11 | Python | none declared | Win32 HWND messages | `PostMessage` / `WM_CHAR` / `WM_SETTEXT`; foreground input is refused unless `confirm_focus_disruption:true` | `PrintWindow`; mp4 recording via ffmpeg | **Yes**, plus a **hidden Win32 desktop** (`CreateDesktop`) |
| 8 | [Winter-And-You-Gone/ScreenShotTool-MCP](https://github.com/Winter-And-You-Gone/ScreenShotTool-MCP) | small | Node / PS | n/a | Win32 + UIA | `PostMessage` with `noActivate:true`; launch with suppression (`HWND_BOTTOM`, restore the previous foreground for at least 8 s) | `PrintWindow` by default | **Yes** (best effort). Warns that capture takes 1-5 s and blocks the target's render thread |
| 9 | [adirh3/AvaloniaMcp](https://github.com/adirh3/AvaloniaMcp) | 12 / 2026-03-11 | C# | MIT | **App-embedded**: `.UseMcpDiagnostics()` starts a named pipe `avalonia-mcp-{pid}` plus a discovery file | In process: `ICommand.Execute`, `RaiseEvent(KeyEventArgs)`, property sets | **In process `RenderTargetBitmap.Render(target)`** | **Yes for input** (no OS input at all); capture needs a live window |
| 10 | [SuperJMN/Zafiro.Avalonia.Mcp](https://github.com/SuperJMN/Zafiro.Avalonia.Mcp) | 4 / 2026-09-22 | C# | MIT | **App-embedded** `Zafiro.Avalonia.Mcp.AppHost` on named pipe `zafiro-avalonia-mcp-{PID}`; Avalonia 11.3.17+ and 12.x | In process: `Button.ClickEvent`, synthetic `PointerPressedEventArgs` / `PointerReleasedEventArgs`, `KeyEventArgs`, `fill_form` | `RenderTargetBitmap`; `start_recording` takes RTB frames on a timer and returns a contact-sheet PNG (not video) | **Yes for input**; has a headless preview mode |
| 11 | [AvaloniaUI DevTools MCP (`avdt mcp`)](https://docs.avaloniaui.net/tools/developer-tools/mcp) | closed source | C# | **Commercial** (Avalonia Plus, "not included with the Community license") | App-embedded `AvaloniaUI.DiagnosticsSupport`; HTTP on 29414 or a named pipe | `input`, `action` (Focus / Enable / BringIntoView) | `screenshot` of any element | Presumably in process; not verifiable |
| 12 | [plop44/SnoopWpfMcp](https://github.com/plop44/SnoopWpfMcp) | 11 / 2025-08-03 | C# | MIT, plus Ms-PL (Snoop submodule) | **Injected DLL** (Snoop's injector) into a WPF process; named pipes | AutomationPeer invoke in process on the Dispatcher | WPF `RenderTargetBitmap.Render(mainWindow)` | **Yes for input** (in process); WPF only |
| 13 | [Avalonia.Headless](https://github.com/AvaloniaUI/Avalonia/tree/master/src/Headless) (+ `Avalonia.Headless.Vnc`) | in core, 31.6k | C# | MIT | Replaces the windowing platform | `topLevel.KeyPress/KeyTextInput/MouseDown/MouseMove/MouseWheel`: raw input through the input manager | `CaptureRenderedFrame()` with `UseSkia()` + `UseHeadlessDrawing=false` | **Fully invisible**: no HWND at all |

Projects found but not examined in depth: [locomorange/uiautomation-mcp](https://github.com/locomorange/uiautomation-mcp) (28 stars, C#, MIT; its README lists desktop screenshot and "Focus" window management, so it is a foreground design), [skuzadev/wpfpilot-mcp](https://github.com/skuzadev/wpfpilot-mcp), [lpmwfx/gui-mcp](https://github.com/lpmwfx/gui-mcp) (Rust, EUPL-1.2; "All operations work on background windows" via `PrintWindow` + `PostMessage`), [HamdanProfessional/windows-control-mcp](https://github.com/HamdanProfessional/windows-control-mcp) (`PrintWindow` + posted messages; its README says this "works on Chromium/WebView2 apps; native Win32 apps may ignore posted clicks"), [FlaUI/FlaUI.WebDriver](https://github.com/FlaUI/FlaUI.WebDriver).

## 2. Per-project evidence (quotes)

### Windows-MCP (CursorTouch)
- `src/windows_mcp/desktop/service.py:596-640`: `win32gui.SetForegroundWindow(target_handle)`, `AllowSetForegroundWindow(-1)`, `win32process.AttachThreadInput(...)`. At `:702` it calls `uia.SetCursorPos(x, y)`, so clicks move the real cursor.
- Issues: #369 "PowerShell tool spawns a visible console window that steals focus (missing CREATE_NO_WINDOW)" (closed); #419 "`switch_app()` reports success without verifying the window reached the foreground".
- Verdict: the most popular project, and fully foreground. Nothing to borrow for our focus problem, except one small point: child processes use `CREATE_NO_WINDOW`.

### terminator (mediar-ai)
- README: "**Doesn't take over your cursor or keyboard** - runs in the background without interrupting your work".
- Code disagrees in part. `crates/terminator/src/platforms/windows/input.rs:115-123` uses `SendInput` for clicks. `element.rs:863-899` calls `BringWindowToTop`, `SetForegroundWindow` and `ShowWindow(SW_RESTORE)`. `input.rs:171/250` `save_focus_state()` / `restore_focus_state()` ("Save the current focus state including focused element and caret position… Caret position is only saved if the focused element supports TextPattern2"). Typing takes a `restore_focus: bool` (`element.rs:1150-1160`).
- So the approach is "take focus, act, give it back". The flicker remains.

### FlaUI-MCP (Scott Hanselman)
- README: "`windows_screenshot` supports an optional `background: true` argument when a window `handle` is provided. This uses native background capture when available and falls back to the normal screenshot path if Windows returns a blank frame."
- README: "Keyboard input is focus-dependent. When you use `windows_send_keys`, the tool focuses the supplied `ref` first when possible, but Windows still sends keys to the active keyboard focus."
- `src/FlaUI.Mcp/Core/NativeWindowCapture.cs:46-59`: `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`, then `IsBlankOrNearlyBlank(bitmap)`.
- `Tools/ClickTool.cs:73-111`: `Patterns.Invoke` when supported, otherwise `Mouse.Click` (real cursor).

### NovaWindows driver (Appium)
- `lib/commands/app.ts:51`: `$graphics.CopyFromScreen(...)` (screen pixels, so an occluded window captures whatever covers it). `app.ts:133/145/183/338`: `trySetForegroundWindow`. `lib/winapi/user32.ts:290+`: `SendInput`.
- Open issue #46 "feat: window-specific screenshots".

### Desktop Computer Use (DCU)
- README: "controls desktop applications without moving your real cursor, stealing focus, or making the target application count itself as focused."
- `src/ShadowUse/Safety/FocusGuard.cs`: "captures the user's foreground window, keyboard-focus HWND and caret owner before an action; afterwards, if the target app grabbed any of them (UIA Invoke and posted clicks can make apps SetFocus internally…), puts everything back. Restore uses the AttachThreadInput trick…". It attaches to both the "thief" thread and the original thread, then calls `SetForegroundWindow` and `SetFocus`.
- `Capture/ScreenshotService.cs:12,69-93`: `PrintWindow(PW_RENDERFULLCONTENT)` on a bounded worker. "PrintWindow sends WM_PRINT/WM_PRINTCLIENT to hwnd and blocks until it's [done]".
- Known limits (README): "Modifier shortcuts do not always fire because posted messages do not update system-level modifier state"; "A message click can rarely make an application activate itself"; "The UIA text fallback can briefly foreground some applications"; "Minimized windows do not have a capturable backing surface."

### lowlevel-computer-use-mcp (hidden desktop)
- README: "run full native GUI applications on an invisible Windows desktop… isolated Win32 desktops via `CreateDesktop` and `SwitchDesktop`". "`screenshot` uses `PrintWindow` so the window is captured even if it's behind others, minimized, or on an off-screen desktop."
- Policy worth copying: "Foreground mouse, typing, hotkeys, window activation, and interactive desktop switches are focus-protected. They return `focus_protected: true` unless the call includes `confirm_focus_disruption: true`."
- Caveat: "message-based input is ignored by some apps (raw input / DirectInput / physical-key-state checks)."
- Not verified: whether a GPU-composited Avalonia window (ANGLE / DirectComposition) renders at all on a non-interactive desktop.

### ScreenShotTool-MCP
- README: `launch_app noActivate` "pushes the new window to `HWND_BOTTOM` and restores the user's original foreground window… keeps monitoring for at least 8 seconds… uses the Alt-key trick to get around the `SetForegroundWindow` restriction."
- It also warns: "PrintWindow sends WM_PRINT, the target must respond synchronously… may block the target application's render thread", at 1-5 s per capture. This rules out per-frame `PrintWindow` for video.

### AvaloniaMcp (adirh3)
- README: "This starts a named-pipe server inside your app at startup. It has zero UI impact — all introspection happens on-demand via the pipe." "All visual tree operations are marshaled to the Avalonia dispatcher thread".
- `src/AvaloniaMcp.Diagnostics/Handlers/InteractionHandler.cs:25-28`: a click runs `button.Command.Execute(button.CommandParameter)` when there is a command. `:142` `control.RaiseEvent(keyDown)`, which is only the Enter that `input_text` sends, not general key input ([2026-10-01-in-app-automation-pipe-protocols.md](./2026-10-01-in-app-automation-pipe-protocols.md)). `:216-217` `new RenderTargetBitmap(pixelSize); rtb.Render(target);`.
- `DiagnosticServer.cs:73-79`: `NamedPipeServerStream(..., MaxAllowedServerInstances, ...)`. Discovery file at `%TEMP%/avalonia-mcp/{pid}.json`.

### Zafiro.Avalonia.Mcp
- README: "lets AI agents inspect, interact with, and capture a running Avalonia UI application in real time". It supports Avalonia 11.3.17+ and 12.x, and offers CSS-like selectors (`Button[Content="Save"]`, `#SaveBtn`, `[dc:'x => x.IsValid']`), structured errors, `wait_for`, `subscribe`/`poll_events` (`property_changed`, `window_opened`, `focus_changed`), and `fill_form`.
- `src/Zafiro.Avalonia.Mcp.AppHost/Handlers/InputHandler.cs:209-233` `SimulatePointerClick`: builds `new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, …)` and raises `PointerPressedEventArgs` / `PointerReleasedEventArgs` on the control. `:94` `button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))`.
- `Handlers/ScreenshotHandler.cs:44-50`: RTB render of the target visual, or the last `PopupRoot` if there is one, so popups are captured. `Handlers/FrameRecorder.cs:40-74`: `DispatcherTimer` frames into `RenderTargetBitmap`, composed into a contact sheet. README: "recordings return one labelled contact-sheet PNG (no GIF/video)".
- README: "In headless mode, `screenshot` is best-effort; prefer the tree/text/layout tools when native pixel rendering is unavailable."

### Avalonia DevTools MCP (official, paid)
- Docs: "MCP is a paid feature and is not included with the Community license." It needs `AvaloniaUI.DiagnosticsSupport` and `.WithDeveloperTools()`. Transport is "HTTP and Named Pipes" (`DeveloperToolsProtocol.CreateNamedPipe(string)`). Tools include `tree`, `search`, `screenshot`, `input`, `action`.
- The protocol is undocumented and closed, so there is nothing to borrow beyond the tool surface. Reliability reports exist ([AvaloniaPro discussion #19](https://github.com/AvaloniaUI/AvaloniaPro/discussions/19): attach timeouts on 2.2.0-beta2).

### SnoopWpfMcp
- README: "MCP server discovers and injects into target WPF processes. Injected `WpfInspector` provides UI inspection via Named Pipes".
- `MCP/WpfInspector/Inspector.cs:289-300`: WPF `RenderTargetBitmap.Render(mainWindow)`. `:229` `Application.Current?.Dispatcher.Invoke(...)` with AutomationPeer commands.
- Relevant only as proof of the same pattern in WPF. We own the app, so injection is unnecessary.

### Avalonia.Headless
- `src/Headless/Avalonia.Headless/HeadlessWindowExtensions.cs` (master): `CaptureRenderedFrame(this TopLevel)` "Triggers a renderer timer tick and captures last rendered frame". `GetLastRenderedFrame` throws unless the app was built with "'.UseSkia()' and disabled 'UseHeadlessDrawing'". Input goes through `KeyPress`, `KeyTextInput`, `MouseDown/Move/Up/Wheel`, `TouchBegin`, `DragDrop`, `SetRenderScaling`.
- Rendering is pumped by hand: `RunJobsAndRender` loops `dispatcher.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();`. Real-time video therefore needs our own 30/60 Hz pump.
- `Avalonia.Headless.Vnc`: `StartWithHeadlessVncPlatform(host, port, password, args)` serves the invisible app over VNC, so a human can still look.
- Avalonia docs ([custom-rendering](https://github.com/avaloniaui/avalonia-docs/blob/main/docs/graphics-animation/custom-rendering.md)) say: "`RenderTargetBitmap` uses software rendering. Controls that rely on GPU-specific rendering paths… may not render correctly" and "`RenderTargetBitmap.Render` requires the target control to be attached to a visible window. If you need to render controls without displaying a window… use the headless platform with the Skia renderer".

### Platform APIs referenced
- `Window.ShowActivated` exists in Avalonia (`src/Avalonia.Controls/Window.cs:152-153`, default `true`), as does `ShowInTaskbar`.
- Windows Graphics Capture (WGC): `GraphicsCaptureSession.IsBorderRequired` exists, but disabling the yellow border needs consent through `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` and the `graphicsCaptureWithoutBorder` capability ([MS Learn](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired)). On 24H2, frames arrive only when content changes ([Composition-Win32-Samples #142](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/issues/142)), so the encoder must not use frame arrival as its clock.
- Per-process audio: `ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK` captures "only audio from the specified process, and its children" ([ApplicationLoopback sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)).

## 3. What the survey shows

1. **No out-of-process tool fully avoids focus problems.** The best ones (DCU, lowlevel-computer-use, ScreenShotTool-MCP, gui-mcp) use `PostMessage` input with `PrintWindow` capture, plus a focus guard that **restores** focus afterwards. Each documents leaks: posted keys miss modifier state, "a message click can rarely make an application activate itself", the UIA text fallback "can briefly foreground", and minimized windows have no surface.
2. **Every Avalonia- or WPF-specific tool (AvaloniaMcp, Zafiro, DevTools MCP, SnoopWpfMcp) runs inside the app**, behind a named pipe. Input is a routed event or command raised on the UI thread and screenshots are `RenderTargetBitmap`. No OS input is involved, so nothing can steal focus. None of them records real video.
3. **`PrintWindow` cannot drive video.** It is synchronous, can block the target's render thread, and takes 1-5 s per frame on some apps.

## 4. Ideas worth borrowing, ranked for "no focus stealing, no visible window, real-time 1080p radar video with app audio"

**Rank 1: in-app offscreen radar renderer feeding the recorder directly.** Video and screenshots never touch the window. YAAT's radar already renders with SkiaSharp from a snapshot on a separate thread ("two-thread snapshot split", `docs/radar-rendering.md`). The client could keep a second `SKSurface` at a fixed 1920x1080, independent of window size and visibility, draw the same snapshot into it on a 30/60 Hz clock, and pipe BGRA frames to ffmpeg (stdin or a named pipe). For audio, either tap the client's own TTS/audio output in process, or capture the client PID with process-loopback WASAPI. The window can then be minimized, hidden or never shown. This is the only option where the output resolution does not depend on the window. Cost: only the radar is recorded, not surrounding chrome or popups, unless those are composed in too. Not measured: the cost of rendering at 1080p30.

**Rank 2: an in-app automation endpoint (AvaloniaMcp / Zafiro pattern) that replaces UIA for input.** A named pipe in `Yaat.Client` started by a builder extension, with a discovery file per PID and all work marshalled to `Dispatcher.UIThread`. Input options:
- (a) Semantic: `ICommand.Execute`, setting `TextBox.Text`, or calling `MainViewModel` methods directly. This is the most robust.
- (b) Synthetic routed events: `PointerPressed`/`PointerReleased` and `KeyEventArgs` via `RaiseEvent` (Zafiro `InputHandler.cs:209-233`).
- (c) Untested idea: feed `RawPointerEventArgs`/`RawKeyEventArgs` into the window's input pipeline, the way `Avalonia.Headless` does, so hit-testing and focus logic behave as with real input.

Screenshots use `RenderTargetBitmap`, whose docs require a window attached and visible but not foreground, and which renders in software. Not checked: how it handles the radar's custom Skia draw operation. Show the window with `ShowActivated=false` behind other windows or on a second monitor, and it never needs activation. Worth copying from Zafiro: selectors, `wait_for`, event subscriptions, structured error codes, and popup-root-aware screenshots. This approach removes the focus problem for driving the app and for still screenshots. It does not solve video by itself.

**Rank 3: Avalonia.Headless with Skia, plus the VNC option, as a separate launch mode.** No HWND exists at all. Frames come from `CaptureRenderedFrame()` and input from `KeyPress`/`MouseDown`, which run through the real input manager. A human can watch over `StartWithHeadlessVncPlatform`. Drawbacks: we would pump the render clock for real-time video; Win32-specific features (UIA, real windows that CRC-comparison tooling expects) disappear; RTB/headless rendering is software. Zafiro itself warns its headless "screenshot is best-effort". It suits deterministic guide captures (compare `tools/Yaat.GuideCapture`) more than live recording.

**Rank 4: keep the out-of-process UIA driver but adopt DCU's techniques**, for the CRC side or anything we do not own:
- UIA patterns (`Invoke`, `Value`, `Toggle`) before any coordinate input, and never `SetForegroundWindow`.
- A `FocusGuard` that snapshots and restores foreground, focus and caret via `AttachThreadInput` around each action.
- `PrintWindow(PW_RENDERFULLCONTENT)` stills on a timed worker, with a blank-frame check that falls back to an error (FlaUI-MCP, DCU).
- Video via WGC of the occluded window, accepting the yellow border unless the app gets borderless consent; WGC delivers frames only on change, so pace the encoder independently.
- A `confirm_focus_disruption` gate: any tool that would foreground the app refuses unless explicitly asked (lowlevel-computer-use-mcp).
- `CREATE_NO_WINDOW` for every child process (Windows-MCP #369).

This reduces focus theft but does not eliminate it; every project using it documents leaks.

**Rank 5: a hidden Win32 desktop (`CreateDesktop`).** It is fully invisible, but untested for a GPU-composited Avalonia app, and WGC/DWM composition on a non-input desktop is unverified. Treat it as a fallback only.

**Not recommended:** the official Avalonia DevTools MCP. It is paid (Avalonia Plus) with a closed protocol, and the in-app endpoint above gives the same capability under our control.

Suggested combination: Rank 1 for video, Rank 2 for driving and still screenshots of YAAT, and Rank 4's guard and gate for the CRC window we do not own.
