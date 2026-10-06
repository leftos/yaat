# Driving an Avalonia 12 app in the background on Windows 11: research notes

Scope: primary sources only (Microsoft Learn, Microsoft engineers' own posts and repos, and Avalonia source at tag `12.1.3`, the latest 12.x release at research time, 2026-10-01). Avalonia line numbers refer to tag `12.1.3`. "Not documented" means no primary source states it; behaviour then has to be measured on the machine.

Avalonia source base URL: `https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/`

---

## 1. Capturing a window that is not in the foreground or not visible

### Windows.Graphics.Capture (WGC), `GraphicsCaptureItem` from an HWND

- **Creating an item for an HWND without a picker**: `IGraphicsCaptureItemInterop::CreateForWindow` "Targets a single window for the creation of a graphics capture item." (Windows 10 1903+). https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow
- **(a) Behind other windows: yes.** Robert Mikhayelyan (Microsoft, owner of the WGC samples): "Windows.Graphics.Capture provides per-window capture without having to worry about occlusion." https://github.com/robmikh/Win32CaptureSample/issues/24#issuecomment-699200255
- **(b) Off-screen: works only while the app keeps drawing.** Same comment: "There are still some limitations around applications that decide to stop drawing under certain conditions. For example, many applications stop drawing when they are off-screen, so you would get stale content in that situation." Avalonia stops rendering only on minimize (see below); nothing in its Win32 backend stops rendering for off-screen windows.

  Whether DWM keeps composing an off-screen window into the capture: not documented. Related: the DirectComposition architecture page says "The composition engine can detect when a particular window is fully occluded and avoid wasting CPU and graphics processing unit (GPU) resources composing for the window." https://learn.microsoft.com/en-us/windows/win32/directcomp/architecture-and-components
- **(c) Minimized: no new frames from Avalonia.** WGC's behaviour for minimized windows is not documented. Avalonia stops rendering when minimized, `Window.HandleWindowStateChanged` (`src/Avalonia.Controls/Window.cs` ~L687): `if (state == WindowState.Minimized) { StopRendering(); } else { StartRendering(); }`. So even if WGC delivered frames, the content would be frozen at the last frame drawn before minimizing.
- **(d) DWM-cloaked (`DWMWA_CLOAK`): composition continues; WGC behaviour not documented.** `DWMWA_CLOAK`: "Cloaks the window such that it is not visible to the user. The window is still composed by DWM." https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute.

  Raymond Chen (Microsoft): a cloaked window "still has the `WS_VISIBLE` window style, its coordinates are still within the bounds of the monitor, it still gets `WM_PAINT` messages". https://devblogs.microsoft.com/oldnewthing/20200302-00/?p=103507. No primary source says whether WGC returns a cloaked window's content or a blank/stale frame.
- **(e) Another virtual desktop: not documented.** Windows on non-current virtual desktops are cloaked by the shell. Raymond Chen lists "windows that belong to non-current virtual desktops" among the cloaked cases (same post).

  From 2017: "To switch to a virtual desktop, the system shows the windows that belong to the virtual desktop and hides the windows that do not belong to the virtual desktop. Note that the windows still all belong to the same desktop". https://devblogs.microsoft.com/oldnewthing/20171002-00/?p=97116. So (e) reduces to (d), and WGC's behaviour there is not documented.
- **Popups (separate HWNDs)**: `GraphicsCaptureSession.IncludeSecondaryWindows` (Windows 11 24H2, 10.0.26100+): "Secondary Windows are considered to be windows that have either the WS_POPUP or WS_EX_TOOLWINDOW styles that intersect the main window ... The windows are drawn into the texture the app receives and are clipped if they go outside the bounds of the main top level window."

  Default false. https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.includesecondarywindows. Alternatively, Avalonia's `Win32PlatformOptions.OverlayPopups`: "Embeds popups to the window when set to true. The default value is false." (`src/Windows/Avalonia.Win32/Win32PlatformOptions.cs` L104-107).
- **Yellow capture border (visible to the user)**: the screen-capture overview says "a yellow notification border is drawn by the system around the actively captured item." https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture.

  `GraphicsCaptureSession.IsBorderRequired` can be set to false only after consent: "your app must get consent from the user by calling GraphicsCaptureAccess.RequestAccessAsync, passing in the value GraphicsCaptureAccessKind.Borderless ... you must declare the graphicsCaptureWithoutBorder capability in your app's package manifest." https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired.

  What applies to an unpackaged Win32 tool is not documented. If the window is off-screen, the border is drawn off-screen too.

### `PrintWindow` with `PW_RENDERFULLCONTENT`

- **`PW_RENDERFULLCONTENT` is not documented.** The current `PrintWindow` page lists only `PW_CLIENTONLY`. https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow
- The documented mechanism is app-cooperative: "The application that owns the window referenced by hWnd processes the PrintWindow call and renders the image in the device context ... The application receives a WM_PRINT message or, if the PW_PRINTCLIENT flag is specified, a WM_PRINTCLIENT message." (same page)
- **Avalonia does not handle `WM_PRINT`/`WM_PRINTCLIENT`.** `WindowImpl.AppWndProc.cs` handles `WM_PAINT` (L694) but neither print message.

  With the default composition modes the window has no redirection surface: `WindowImpl.cs` L149-150 sets `UseRedirectionBitmap = surfaceFactory is null || glPlatform is null || !surfaceFactory.RequiresNoRedirectionBitmap;` and L970 adds `WS_EX_NOREDIRECTIONBITMAP` when it is false. `WS_EX_NOREDIRECTIONBITMAP`: "The window does not render to a redirection surface.

  This is for windows that do not have visible content or that use mechanisms other than surfaces to provide their visual." https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles.

  Default `CompositionMode` is `WinUIComposition, DirectComposition, RedirectionSurface` (`Win32PlatformOptions.cs` L132-134). So classic `PrintWindow` has nothing to copy, and the undocumented `PW_RENDERFULLCONTENT` path is the only one that might work. Its behaviour for (a)-(e) is not documented.

### Does a minimized window keep rendering?

- **Avalonia: no.** `StopRendering()` on `WindowState.Minimized` (above). DirectComposition/Skia are below that call, so nothing is submitted.
- Also from Avalonia: restoring from minimized activates the window. `WindowImpl.cs` L310: `ShowWindow(value, value != WindowState.Minimized); // If the window is minimized, it shouldn't be activated`. `ShowWindow(state, activate: true)` ends with `SetFocus(_hwnd); SetForegroundWindow(_hwnd);` (L1312-1316).

**Verdict 1:** WGC is the right tool for occluded windows (documented by a Microsoft engineer). Off-screen works with Avalonia because Avalonia keeps rendering, but popups get repositioned on-screen (see §3). Minimized gives no new content (Avalonia's own `StopRendering`).

For cloaked windows and other virtual desktops, WGC's behaviour is not documented and must be measured. `PrintWindow` is a dead end for Avalonia: the flag is undocumented, the window has no redirection bitmap and Avalonia has no `WM_PRINT` handler.

---

## 2. Input without focus

### UI Automation patterns

- **The UIA specs set no focus or foreground precondition on any of these patterns.** The implementer guidelines for Invoke, Value, Toggle, SelectionItem, ExpandCollapse and ScrollItem list methods with no focus requirement. The only stated precondition (Value): "A control should have its IsEnabled property set to TRUE and its ITextProvider::IsReadOnly property set to FALSE before allowing a call to ... SetValue." https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementingvalue.

  Invoke: "Invoke is an asynchronous call and must return immediately without blocking. This behavior is particularly critical for controls that, directly or indirectly, launch a modal dialog when invoked." https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementinginvoke. That the patterns work on a never-activated window follows from the docs saying nothing about focus and from Avalonia's code below. No page states it outright.
- **How UIA reaches Avalonia**: a server-side provider answers `WM_GETOBJECT`. "This function is called by a control when it receives the WM_GETOBJECT message, to provide UI Automation with the UI Automation provider for the control." https://learn.microsoft.com/en-us/windows/win32/api/uiautomationcoreapi/nf-uiautomationcoreapi-uiareturnrawelementprovider.

  Avalonia does this in `WindowImpl.AppWndProc.cs` L940-946 (`WM_GETOBJECT` with `uiaRootObjectId` → `UiaReturnRawElementProvider(_hwnd, wParam, lParam, node)`). Nothing there checks activation.
- **Pattern dispatch in Avalonia 12** (`src/Windows/Avalonia.Win32.Automation/AutomationNode.cs` L89-105): ExpandCollapse, Invoke, RangeValue, Scroll, Selection, SelectionItem, Toggle and Value are exposed when the peer implements the matching `Avalonia.Automation.Provider` interface. `ScrollItem => this` is exposed on every element, and `IScrollItemProvider.ScrollIntoView()` calls `Peer.BringIntoView()` (`AutomationNode.Scroll.cs` L25-28).
- **Every provider call is marshalled synchronously to the UI thread**: `void IInvokeProvider.Invoke() => InvokeSync((AAP.IInvokeProvider x) => x.Invoke());` and `InvokeSync` does `Dispatcher.UIThread.InvokeAsync(action).Wait()` (AutomationNode.cs L197-205). So Avalonia's Invoke is not the "return immediately" call the spec asks for. A click handler that blocks the UI thread blocks the UIA client. `ShowDialog` returns a `Task`, so an awaited dialog does not block.
- **Which peers implement which providers** (class declarations in `src/Avalonia.Controls/Automation/Peers/`, plus the `OnCreateAutomationPeer` overrides):

| Pattern | Avalonia 12 peers |
|---|---|
| Invoke | `ButtonAutomationPeer` (`Button.PerformClick()`, after `EnsureEnabled()`), `SplitButtonAutomationPeer` |
| Value | `TextBoxAutomationPeer` (`SetValue(v) => Owner.Text = value`), `ComboBoxAutomationPeer`, `AutoCompleteBoxAutomationPeer`, `CalendarAutomationPeer`, `DatePickerAutomationPeer`, `TimePickerAutomationPeer` |
| Toggle | `ToggleButtonAutomationPeer` (also CheckBox and RadioButton, which derive from ToggleButton), `ToggleSplitButtonAutomationPeer`, `MenuItemAutomationPeer` (only when `ToggleType != None`; L86 hides it otherwise) |
| SelectionItem | `ListItemAutomationPeer` (used by `ListBoxItem`, so also `ComboBoxItem`, and by `TabItem`), `TreeViewItemAutomationPeer`, `RadioButtonAutomationPeer`, `CalendarDayButtonAutomationPeer` |
| ExpandCollapse | `ComboBoxAutomationPeer` (`Expand() => Owner.IsDropDownOpen = true`), `ExpanderAutomationPeer`, `AutoCompleteBoxAutomationPeer`, `SplitButtonAutomationPeer` |
| ScrollItem | every element (`BringIntoView`) |
| Selection / Scroll / RangeValue | `SelectingItemsControlAutomationPeer` (ListBox, ComboBox), `PipsPager`, `Calendar` / `ItemsControl`, `ScrollViewer` / `RangeBase`, `Slider`, `ProgressBar`, `NumericUpDown` |

  Gaps that matter: a plain `MenuItem` exposes **no Invoke** pattern. `TreeViewItem` exposes **no ExpandCollapse**. Driving menus without focus therefore needs mouse messages or in-process code.

### `PostMessage(WM_KEYDOWN / WM_CHAR / WM_LBUTTONDOWN)` into Avalonia's Win32 backend

- **Avalonia reads keyboard input from the messages themselves**, with no focus check: `WM_KEYDOWN` → `TryCreateRawKeyEventArgs(RawKeyEventType.KeyDown, ...)` (AppWndProc L231-233). `WM_CHAR` → `RawTextInputEventArgs` when `wParam >= 32 && !_ignoreWmChar` (L261-276). `_ignoreWmChar` is set when the preceding `WM_KEYDOWN` was handled (L975-991), so a posted `WM_CHAR` after a handled posted `WM_KEYDOWN` is dropped.
- **Modifiers come from the real keyboard state**, not from the message: `WindowsKeyboardDevice.Modifiers` calls `GetKeyboardState` (`src/Windows/Avalonia.Win32/Input/WindowsKeyboardDevice.cs` L11-40). Raymond Chen on the same point: "even if you manage to post the input messages into the target window's queue, that doesn't update the keyboard shift states." https://devblogs.microsoft.com/oldnewthing/20050530-11/?p=35513. So posted Ctrl+X or Shift+X will not carry the modifier.
- **Routing**: `KeyboardDevice.ProcessRawEvent` sends key events to `FocusedElement ?? e.Root.FocusRoot` (`src/Avalonia.Base/Input/KeyboardDevice.cs` L238). With nothing focused, keys go to the window root. Focus the target control first (UIA `SetFocus`, or a posted click).
- **Mouse**: `WM_LBUTTONDOWN` is processed unless `IsMouseInPointerEnabled` (AppWndProc L278-305). That flag is `_wmPointerEnabled && IsMouseInPointerEnabled()` (WindowImpl.cs L333). Avalonia never calls `EnableMouseInPointer` (no reference in `Win32Platform.cs`), so posted mouse messages are processed, with the position taken from `lParam`.
- **A posted click on a normal window calls `SetFocus(_hwnd)`**: `shouldTakeFocus = ShouldTakeFocusOnClick;` then `SetFocus(_hwnd)` (L287, L966-969). `PopupImpl` overrides it to false (`PopupImpl.cs` L32). `SetFocus`: "It also activates either the window that receives the focus or the parent of the window that receives the focus." https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setfocus.

  Whether activating a window on a background thread brings it to the foreground is not documented there. Because `SetFocus` only applies within the calling thread's queue ("The window must be attached to the calling thread's message queue"), the expected result is thread-local activation, but that needs measuring.
- `WM_KEYDOWN` doc: "Posted to the window with the keyboard focus when a nonsystem key is pressed." https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-keydown. That describes where the system routes real input. It is not a check Avalonia makes.

**Verdict 2:** UIA patterns carry no documented focus requirement, and Avalonia's provider never checks activation. Invoke (buttons), Value (TextBox/ComboBox), Toggle, SelectionItem (list, tab and combo items) and ExpandCollapse (ComboBox, Expander) are implemented.

MenuItem Invoke and TreeViewItem ExpandCollapse are not. Posted `WM_KEYDOWN`/`WM_CHAR`/`WM_LBUTTONDOWN` do reach Avalonia's input pipeline unfocused, but modifiers come from the real keyboard state, and a posted click calls `SetFocus` on the window, which activates it.

---

## 3. Showing and using the window without activating it

- **`Window.ShowActivated`** (default `true`): "Gets or sets a value that indicates whether a window is activated when first shown." (`Window.cs` L149-153, L402-408). It flows to `PlatformImpl?.Show(ShowActivated, modal)` (L1090).

  In Win32, `ShowWindow(state, activate)` uses `SW_SHOWNOACTIVATE` when not activating and skips `SetFocus`/`SetForegroundWindow` (WindowImpl.cs L1288-1316). `SW_SHOWNOACTIVATE`: "similar to SW_SHOWNORMAL, except that the window is not activated." https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow
- **Launcher-side lever**: "nCmdShow ... is ignored the first time an application calls ShowWindow, if the program that launched the application provides a STARTUPINFO structure." (ShowWindow page). A launcher can pass `STARTF_USESHOWWINDOW` + `SW_SHOWNOACTIVATE`/`SW_SHOWMINNOACTIVE`. This does not stop Avalonia's own `SetForegroundWindow` call when `ShowActivated` is true.
- **Why it matters even from a background launcher**: `SetForegroundWindow` is allowed when, among other cases, "The calling process was started by the foreground process." https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow. An app launched by a foreground terminal or tool can therefore take the foreground.
- **Remaining activation paths in Avalonia 12** (each calls `SetForegroundWindow` via `WindowImpl.Activate()`, L658-660):
  - When a modal dialog closes: `owner!.Activate();` (`Window.cs` ~L1110), unconditionally.
  - Clicking a window disabled by a modal child with no dialog child → `Activate()` (~L1250).
  - Restoring from minimized: `ShowWindow(value, value != WindowState.Minimized)` (WindowImpl.cs L310).
  - Closing a child that was active → `SetActiveWindow(_parent._hwnd)` (L1337-1341).
  - Dialogs: `ShowDialog` goes through the same `Show(ShowActivated, modal)`, so a dialog needs `ShowActivated = false` set on the dialog instance itself.
- **`WS_EX_NOACTIVATE`**: "A top-level window created with this style does not become the foreground window when the user clicks it ... The window should not be activated through programmatic access or via keyboard navigation by accessible technology, such as Narrator. To activate the window, use the SetActiveWindow or SetForegroundWindow function.

  The window does not appear on the taskbar by default." https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles. So the style does **not** stop Avalonia's explicit `SetForegroundWindow` calls above. Avalonia has no public property for this style on `Window`; it would have to be added through `GetWindowLong`/`SetWindowLong` on the platform handle. That is not documented by Avalonia.
- **Popups**:
  - Win32 popups are always non-activating: `PopupImpl.Show`: "Popups are always shown non-activated." → `SW_SHOWNOACTIVATE`. `WM_MOUSEACTIVATE` returns `MA_NOACTIVATE` (`PopupImpl.cs` L25-32, L89-90).
  - Light-dismiss closing is driven by parent deactivation and focus loss: `WindowDeactivated`, `WindowBaseDeactivated` and `WindowLostFocus` all `Close()` when `IsLightDismissEnabled` (`Popup.cs` L992-1034).
  - In a never-activated window, no `Deactivated` ever fires (it is raised only from `WM_ACTIVATE` `WA_INACTIVE`, AppWndProc L37-58), so popups do not auto-close from activation changes. They close through their own logic (selection, Escape, outside click inside the app), or through `WM_KILLFOCUS` → `LostFocus` (L877-885) if the window had taken focus.
  - `Popup.TakesFocusFromNativeControl` (default true) calls `popupHost.TakeFocus()` on open (Popup.cs L590-591). `PopupImpl.TakeFocus` acts on the parent chain (L163+).
  - Opening and closing in a never-activated window is not documented by Avalonia; it needs measuring.
- **Off-screen windows pull popups on-screen.** `ManagedPopupPositioner.GetBounds()` picks the screen that contains the anchor or parent, and when none does it falls back to `?? screens.FirstOrDefault()`, constraining the popup to that screen's `WorkingArea` (`src/Avalonia.Controls/Primitives/PopupPositioning/ManagedPopupPositioner.cs` L110-130).

  A ComboBox dropdown or menu of an off-screen window will therefore appear on a real monitor, in front of the user. `OverlayPopups = true` (Win32PlatformOptions) keeps popups inside the window.

**Verdict 3:** set `ShowActivated = false` on every window and dialog, and use `OverlayPopups = true`. Even then, Avalonia 12 calls `SetForegroundWindow` when a modal dialog closes, when a window is restored from minimized, and on clicks into a modal-disabled owner. `WS_EX_NOACTIVATE` does not block those explicit calls. Popups are non-activating by design and do not auto-dismiss in a never-activated window. Whether they open and close correctly there is not documented.

---

## 4. Running on a separate desktop (`CreateDesktop` + `STARTUPINFO.lpDesktop`)

- **Launching onto it is documented**: "If a desktop name was specified in the lpDesktop member of the STARTUPINFO structure that was used when the process was created, the thread connects to the specified desktop." https://learn.microsoft.com/en-us/windows/win32/winstation/thread-connection-to-a-desktop.

  `CreateDesktop` "Creates a new desktop, associates it with the current window station of the calling process". https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createdesktopw
- **It never takes the user's input or foreground**: "only one of these desktops at a time is active. This active desktop, also known as the input desktop, is the one that is currently visible to the user and that receives user input." https://learn.microsoft.com/en-us/windows/win32/winstation/desktops
- **UIA from the default desktop: blocked by the mechanism.** "Window messages can be sent only between processes that are on the same desktop. In addition, the hook procedure of a process running on a particular desktop can only receive messages intended for windows created in the same desktop." (Desktops page).

  UIA's server-side handshake is `WM_GETOBJECT` (§2), so a UIA client on `Default` cannot reach a provider on another desktop. No UIA page addresses desktops explicitly, so this is inferred from the mechanism and not documented as such. The documented workaround shape is a helper process launched onto the same hidden desktop that does the UIA work and reports back over IPC.
- **GPU / DirectComposition rendering on a non-input desktop: not documented.** The DirectComposition architecture page says "The DWM constructs one large visual tree for each desktop in a session." https://learn.microsoft.com/en-us/windows/win32/directcomp/architecture-and-components. That implies per-desktop trees but does not say whether a non-input desktop's tree is composed or presented, or whether DXGI/ANGLE device creation succeeds there.
- **WGC or `PrintWindow` of a window on another desktop: not documented.** `CreateForWindow` takes any HWND, but nothing states cross-desktop support. `PrintWindow` relies on the target answering a message (`WM_PRINT`), which the same-desktop rule above blocks.

**Verdict 4:** launching onto a hidden desktop is documented, and it guarantees no focus or foreground theft. Driving it with UIA from the default desktop is blocked by the window-message rule, so a helper process on the hidden desktop would have to do it. GPU rendering, DirectComposition and capture on a non-input desktop are not documented and must be measured before relying on them.

---

## 5. `Avalonia.Headless` / `Avalonia.Headless.XUnit`

- **Purpose and scope** (Avalonia docs): "The headless platform runs Avalonia without a visible window ... It provides the full Avalonia control tree, layout, styling, and data binding, but replaces the real windowing and rendering backends with in-memory implementations."

  And: "The headless platform is also useful outside of testing. If you need to render controls without a visible window (for example, server-side image generation, PDF export, or batch processing), enable the Skia renderer with `UseHeadlessDrawing = false`". https://docs.avaloniaui.net/docs/concepts/headless/
- **Pixel rendering**: `AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })` (docs, and https://docs.avaloniaui.net/docs/deployment/docker). `CaptureRenderedFrame`: "Triggers a renderer timer tick and captures last rendered frame."

  It throws unless the app was "initialized with '.UseSkia()' and disabled 'UseHeadlessDrawing'" (`src/Headless/Avalonia.Headless/HeadlessWindowExtensions.cs` L20-40). `FrameBufferFormat` defaults to `Rgba8888`.
- **Input simulation** (`HeadlessWindowExtensions.cs`): `KeyPress`/`KeyRelease`/`KeyPressQwerty`/`KeyReleaseQwerty`, `KeyTextInput` ("If you need to simulate text input to a TextBox or a similar control, please use KeyTextInput"), `MouseDown`/`MouseMove`/`MouseUp`/`MouseWheel`, `TouchBegin/Move/End`, `DragDrop`, `SetRenderScaling`.

  Each runs `Dispatcher.UIThread.RunJobs()` plus `ForceRenderTimerTick()` before and after the input (L171-195). Modifiers are passed explicitly (`RawInputModifiers`), unlike Win32 posting.
- **Timers and dispatcher**:
  - The render timer is a real `DispatcherTimer`: "A render timer implementation for headless environments that uses a DispatcherTimer to schedule ticks on the UI thread. Can be controlled with ForceTick method." (`HeadlessRenderTimer.cs`). `ShouldRenderOnUIThread` defaults to `true` in headless, and `false` uses `SleepLoopRenderTimer` (AvaloniaHeadlessPlatform.cs L40-44, L104-109).
  - The headless platform binds no custom dispatcher implementation (only clipboard, cursor, settings, icon loader, keyboard, render loop and windowing; L46-56). The standard Avalonia dispatcher therefore runs a normal main loop, and `DispatcherTimer`s fire in real time.
  - `ForceRenderTimerTick(count)` exists for deterministic stepping.
  - Docs: "Use `Dispatcher.UIThread.RunJobs()` to flush the dispatcher queue".
- **XUnit integration**: `[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]`, `[AvaloniaFact]`/`[AvaloniaTheory]` "sets up the UI thread". Isolation is `PerTest` (default, "Recreates Application and Dispatcher for each test") or `PerAssembly`, and "Concurrent test execution is not supported." https://docs.avaloniaui.net/docs/concepts/headless/headless-xunit
- **Stubs and limits** (`HeadlessPlatformStubs.cs`, `HeadlessWindowImpl.cs`):
  - Clipboard, cursor, icon, font manager and screens are stubs.
  - `HeadlessWindowImpl.TryGetFeature` returns only `IClipboard` and `IScreenImpl` (L285-298), so there is no storage provider: file pickers are unavailable.
  - `OverlayPopups` defaults to **true** in headless ("TODO13: Change the default to false").
  - There is no OS window, hence **no UIA provider**: driving must happen in-process through the extension methods or the control tree.
- **Running a real app for minutes (SignalR, audio)**: not documented as a supported scenario. The docs mention "server-side image generation, PDF export, or batch processing" and Docker. From the source: networking and audio are not Avalonia subsystems and are unaffected. The dispatcher and render timers are the normal ones.

  The app has to be hosted in-process (a host that calls the app's `BuildAvaloniaApp` with `UseHeadless` and drives it on the UI thread), because there is no external automation surface. Platform features the app uses beyond the stubs fail or return null: file dialogs, native menus, tray icon, and the Win32-specific `TryGetPlatformHandle`.

**Verdict 5:** headless gives Skia frame capture (`UseHeadlessDrawing = false` + `CaptureRenderedFrame`), modifier-correct keyboard, mouse, touch and drag-drop simulation, and a real dispatcher with real-time timers. Nothing is ever shown, so focus theft is impossible by construction.

Running a full networked app interactively for minutes is not documented, but nothing in the source prevents it. The costs: the driver must be in-process (no UIA), file dialogs are missing, and popups are overlay-only by default.

---

## 6. Per-process audio capture

- **`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`**: "Process loopback activation, allowing for the inclusion or exclusion of audio rendered by the specified process and its child processes." https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-audioclient_activation_type.

  Parameters: `TargetProcessId`, "The ID of the process for which the render streams, and the render streams of its child processes, will be included or excluded" (Windows 10 build 20348+). https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params
- Sample page: "only audio from the specified process, and its children, will be captured ... the capture is not tied to a specific audio endpoint ... If the processes whose audio will be captured does not have any audio rendering streams, then the capturing process receives silence." https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/
- **Window state**: the API is keyed by process ID and has no window parameter. Nothing ties it to visibility, focus, minimization, virtual desktop or desktop object. No page says so explicitly ("regardless of window state" is not documented as a sentence). Capture still requires the target to actually render to an audio stream. The user will hear that audio unless it is routed elsewhere, and whether muting the app's session volume also silences the loopback stream is not documented.

**Verdict 6:** yes. Process-loopback capture is keyed by PID (plus child processes), not by window, so window state does not enter into it. The target must render real audio streams, and that audio is audible to the user unless redirected (redirection is not documented).

---

## Overall recommendation (from the sources above)

1. **No-focus-theft in-process option**: Avalonia.Headless with Skia (`UseHeadlessDrawing = false`), driven in-process. Frames come from `CaptureRenderedFrame` and audio from process loopback. The price is losing UIA and file dialogs.
2. **Real Win32 window option**:
   - Set `ShowActivated = false` on every window and dialog, and `OverlayPopups = true`.
   - Keep the window un-minimized and either off-screen or occluded, and capture it with WGC (`IncludeSecondaryWindows` on 24H2+).
   - Drive it through UIA patterns, plus posted mouse messages where patterns are missing (MenuItem, TreeViewItem expand).
   - Avalonia's unconditional `owner.Activate()` after a modal dialog, and restoring from minimized, will still call `SetForegroundWindow`. This needs a code change in the app or must be measured.
3. **Hidden desktop**: the strongest isolation, but it needs a UIA helper on that desktop. GPU and capture behaviour there is undocumented and must be measured first.
