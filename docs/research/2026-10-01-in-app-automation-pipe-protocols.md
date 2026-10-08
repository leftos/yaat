# In-app automation pipe: protocols of Zafiro.Avalonia.Mcp, AvaloniaMcp and Avalonia DevTools MCP

Research date: 2026-10-01. Sources: shallow clones made that day of [SuperJMN/Zafiro.Avalonia.Mcp](https://github.com/SuperJMN/Zafiro.Avalonia.Mcp) at `b68c80a0` (last commit 2026-09-22) and [adirh3/AvaloniaMcp](https://github.com/adirh3/AvaloniaMcp) at `6cdde636` (last commit 2026-03-11); Avalonia source at tag `12.1.3`; the NuGet flat-container and registration APIs; the local `avalonia/12.1.0` package in the NuGet cache.

Line numbers below refer to those commits. This note builds on [2026-10-01-desktop-automation-mcp-survey.md](./2026-10-01-desktop-automation-mcp-survey.md) and the decisions now in [client-driver-mcp.md](../client-driver-mcp.md) ("Background driving: the automation pipe"); it does not repeat what they establish (why in-process, `ShowActivated = false`, `OverlayPopups`, RTB needing a visible window).

Short permalink prefixes used below: **Z** = `https://github.com/SuperJMN/Zafiro.Avalonia.Mcp/blob/b68c80a02429c8329e4d68925720677b207c1a66/`, **A** = `https://github.com/adirh3/AvaloniaMcp/blob/6cdde6364dfa1285204a074b49d491510170e164/`, **AV** = `https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/`. A citation `Z src/X.cs:10-20` means the URL Z + `src/X.cs#L10-L20`.

## 1. Transport and discovery

### Zafiro.Avalonia.Mcp

Activation: `AppBuilder.UseMcpDiagnostics(configure?)` registers an `AfterSetup` callback that starts one `DiagnosticServer` per process and stops it on `ProcessExit` and Ctrl+C (Z `src/Zafiro.Avalonia.Mcp.AppHost/DiagnosticExtensions.cs:15-42`). There is no env-var switch of its own; the README suggests `#if DEBUG` around the call (Z `README.md:70`).

Transport is `Auto` (named pipe on desktop, TCP loopback on Android), `NamedPipe` or `Tcp` (Z `src/Zafiro.Avalonia.Mcp.AppHost/McpDiagnosticsOptions.cs`); TCP binds `IPAddress.Loopback` on an ephemeral port (Z `src/Zafiro.Avalonia.Mcp.AppHost/Transport/TcpLoopbackTransport.cs:10-16`).

Pipe: name `zafiro-avalonia-mcp-{PID}` (Z `src/Zafiro.Avalonia.Mcp.AppHost/Discovery/DiscoveryWriter.cs:14`), created as `new NamedPipeServerStream(name, InOut, MaxAllowedServerInstances, Byte, Asynchronous)` in an accept loop, one task per client (Z `src/Zafiro.Avalonia.Mcp.AppHost/Transport/NamedPipeTransport.cs:24-50`).

Discovery: `%TEMP%/zafiro-avalonia-mcp/{PID}.json` (Android: external cache dir) (Z `src/Zafiro.Avalonia.Mcp.AppHost/Discovery/DiscoveryWriter.cs:21-28,46`), with fields `pid`, `pipeName`, `processName`, `startTime`, `protocolVersion` (default `"1.0.0"`), `transport` (`pipe`/`tcp`), `endpoint`, `packageId` (Z `src/Zafiro.Avalonia.Mcp.Protocol/Models/DiscoveryInfo.cs`). The file is deleted on `Dispose` only, so a crashed process leaves a stale file.

Framing: UTF-8, one JSON object per line, strictly request → response on each connection: the server reads a line, awaits the dispatch, writes one line (Z `src/Zafiro.Avalonia.Mcp.AppHost/DiagnosticServer.cs:38-51`). It is not JSON-RPC 2.0 but is shaped like it. Request: `{"method": string, "params": object?, "id": string}` (Z `src/Zafiro.Avalonia.Mcp.Protocol/Messages/DiagnosticRequest.cs:6-16`).

Response: `{"id", "result"?, "error"?: string, "errorInfo"?: {"message","code","suggested"?,"details"?}}`, camelCase, nulls omitted (Z `src/Zafiro.Avalonia.Mcp.Protocol/Messages/DiagnosticResponse.cs:6-35`, `.../DiagnosticError.cs:12-16`, `src/Zafiro.Avalonia.Mcp.Protocol/ProtocolSerializer.cs`).

Because a connection is serial, a long `poll_events` or `wait_for` blocks every other request on that connection; the tool side serialises with a send lock and a 30 s default timeout per call (Z `src/Zafiro.Avalonia.Mcp.Tool/Connection/AppConnection.cs:54-113`).

Auth/security: none. No `PipeSecurity` and no `PipeOptions.CurrentUserOnly` is passed, so Windows applies the default pipe DACL: "full control to the LocalSystem account, administrators, and the creator owner … read access to members of the Everyone group and the anonymous account" ([Microsoft Learn, Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)).

A duplex client needs write access, so in practice the same user or an administrator can drive the app; nothing restricts which process of that user connects. `PipeOptions.CurrentUserOnly` exists to restrict the server to clients "created by the same user" ([PipeOptions](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0)).

### AvaloniaMcp (adirh3)

Activation: `AppBuilder.UseMcpDiagnostics(pipeName?)` starts the server immediately (not in `AfterSetup`), installs an Avalonia log sink that records `Binding` warnings, and writes `%TEMP%/avalonia-mcp/{PID}.crash.txt` on unhandled exceptions (A `src/AvaloniaMcp.Diagnostics/DiagnosticExtensions.cs:15-48`).

Pipe: default name `avalonia-mcp-{PID}`, same `NamedPipeServerStream` arguments as Zafiro, no ACL (A `src/AvaloniaMcp.Diagnostics/DiagnosticServer.cs:48-51,76-81`). Discovery `%TEMP%/avalonia-mcp/{PID}.json` with `pid`, `pipeName`, `processName`, `startTime`, `protocolVersion` (`"0.4.0"`), `diagnosticsVersion` (A `src/AvaloniaMcp.Diagnostics/DiagnosticServer.cs:258-284`).

Framing: newline-delimited JSON, serial per connection. Request `{"method", "params": object}` with **no request id** (A `src/AvaloniaMcp.Diagnostics/Protocol/DiagnosticRequest.cs:6-13`); response `{"success": bool, "data": any, "error": string?}` (A `src/AvaloniaMcp.Diagnostics/Protocol/DiagnosticResponse.cs:6-19`, built by hand at `DiagnosticServer.cs:182-188`).

A 120 s server-side handler timeout guards a frozen UI thread (A `src/AvaloniaMcp.Diagnostics/DiagnosticServer.cs:108,153-170`). Errors are free text only.

### Avalonia DevTools MCP (official)

Closed source and paid (Avalonia Plus). The app side is `AvaloniaUI.DiagnosticsSupport` plus `.WithDeveloperTools()`; the MCP side is `avdt mcp`, licensed through `AVALONIA_TOOLS_LICENSE_KEY` ([docs](https://docs.avaloniaui.net/tools/developer-tools/mcp)).

Its tools are `attach-to-app`, `attach-to-file`, `detach`, `tree` (by `nodeId`), `ancestors`, `search` (type name or `x:Name`), `screenshot`, `props`, `set-prop`, `styles`, `pseudo-class`, `resources`, `assets`, `open-asset`, `input`, `action` (same page). The wire format, selectors and input method are not published, so the rest of this note covers the two open projects only. `AvaloniaUI.DiagnosticsSupport` is on NuGet up to 2.2.3 (flat-container index, 2026-10-01).

## 2. Message set

### Zafiro: 54 method names, 52 registered

`ProtocolMethods` lists the names (Z `src/Zafiro.Avalonia.Mcp.Protocol/ProtocolMethods.cs`); `RequestDispatcher` registers the handlers (Z `src/Zafiro.Avalonia.Mcp.AppHost/Handlers/RequestDispatcher.cs:12-93`). `pointer_press`, `pointer_release` and `pointer_move` are declared but have no handler, so they return "Unknown method" (no match for them anywhere under `Handlers/`). Parameters, read from each handler's `TryGetProperty` calls:

| Group | Method | Params |
|---|---|---|
| Connection | `ping`, `list_windows` | none. `list_windows` returns `{title,type,width,height,isActive}` per `desktop.Windows` (Z `.../Handlers/ListWindowsHandler.cs:15-52`) |
| Tree | `get_tree` | `selector?`, `depth` (default 10), `treeKind` `Visual`/`Logical`/`Merged` (Z `.../Handlers/TreeHandler.cs:19-89`) |
| | `search` | `query` (substring of type name, `Name`, TextBlock text, header or string content), `limit` (20), `scopeNodeId?` (Z `.../Handlers/SearchHandler.cs:17-92`) |
| | `get_ancestors`, `get_screen_text` (`visibleOnly`), `get_interactables`, `get_snapshot` (`detail` smart/verbose, `visibleOnly`) | `selector?` |
| Properties | `get_properties` (`propertyNames`), `get_property_values` (`propertyName`), `explain_property` (`propertyName`, `includeCandidates`, `maxDepth`), `set_property` (`propertyName`, `value`), `get_styles` (`includeInactive`, `propertyNames`) | `selector` |
| Pseudo-classes | `get_pseudo_classes`, `set_pseudo_class` | `selector`, `pseudoClass`, `isActive` |
| Input | `click`, `tap` (alias) | `selector` |
| | `key_down`, `key_up` | `selector`, `key` (Avalonia `Key` enum name), `modifiers` (`ctrl+shift`…), `text?` (`key_down` only) |
| | `text_input` | `selector`, `text`, `pressEnter` |
| Actions | `action` | `selector`, `action` = `focus`/`enable`/`disable`/`bringintoview` (Z `.../Handlers/ActionHandler.cs:35-75`) |
| Interaction | `select_item` (`index`/`text`), `toggle` (`state`), `set_value` (number), `scroll` (`direction`, `amount`) | `selector` |
| Wait | `wait_for` | `query`, `condition` = `exists`/`not_exists`/`visible`/`enabled`/`text_equals`/`text_contains`/`count_equals`, `value?`, `timeoutMs` (5000, clamped 100-30000) (Z `.../Handlers/WaitForHandler.cs:24-32,60-101`) |
| | `click_and_wait` | `selector`, `waitQuery`, `waitCondition`, `waitValue`, `timeoutMs` |
| | `click_by_query` | `query`, `role?`, `occurrence` |
| Composite | `fill_form` | `fields[]` (`selector` + `value`/`checked`/`select`/`number`/`secret`), `submit?`; per-field errors do not abort the batch (Z `.../Handlers/FillFormHandler.cs:14-38`) |
| Capture | `screenshot` | `selector?` |
| | `start_recording`, `stop_recording` | `selector?`, `fps`, `maxDurationSec`, `maxCells`, `maxSheetDimension` (contact sheet, not video) |
| MVVM | `get_datacontext`, `get_bindings`, `find_view_source`, `get_xaml`, `diff_tree` (`action`), `find_by_datacontext` (`predicate`), `get_item` (`index`/`text`/`value`/`path`/`dcMatch`), `get_validation_errors`, `get_command_info`, `get_layout_info` | `selector` |
| Global state | `get_focus`, `get_active_window`, `get_open_dialogs` | none. `get_open_dialogs` counts a window as a dialog when `Owner` is set (Z `.../Handlers/OpenDialogsHandler.cs:8-28`) |
| Resources | `get_resources` (`onlySelf`), `list_assets`, `open_asset` (`assetUrl`) | |
| Events | `subscribe` | `events[]` of `property_changed`/`window_opened`/`window_closed`/`focus_changed`, `filter {nodeId?, property?}`; max 32 subscriptions, 1000-event ring buffer, 5 min idle TTL (Z `src/Zafiro.Avalonia.Mcp.AppHost/Events/EventBus.cs:19-55`, `.../Handlers/SubscribeHandler.cs:21-60`) |
| | `poll_events` | `subscriptionId`, `timeoutMs` (30000, max 60000), long-poll (Z `.../Handlers/PollEventsHandler.cs:25-42`) |
| | `unsubscribe` | `subscriptionId` |

Error shape: `errorInfo.code` is one of `MISSING_SELECTOR`, `NO_MATCH`, `AMBIGUOUS_SELECTOR`, `AMBIGUOUS_PROPERTY`, `STALE_NODE`, `INVALID_PARAM`, `INVALID_SELECTOR`, `UNSUPPORTED_OPERATION`, `TIMEOUT`, `INTERNAL`, plus preview/launch codes (Z `src/Zafiro.Avalonia.Mcp.Protocol/Messages/DiagnosticErrorCodes.cs:7-23`).

The dispatcher maps exception types onto codes with a recovery hint: `SelectorParseException` → `INVALID_SELECTOR` with `details.position`, `KeyNotFoundException` → `STALE_NODE`, `TimeoutException` → `TIMEOUT`, `ArgumentException` → `INVALID_PARAM` (Z `.../Handlers/RequestDispatcher.cs:146-192`).

Older handlers still return `new { error = "..." }`, and the dispatcher guesses the code from the wording ("not found"/"stale" → `STALE_NODE`, else `INTERNAL`) (same file `:202-230`). An ambiguous selector's hint names `:nth(0)` to `:nth(n-1)` (`:248-257`).

Inconsistencies seen in the source: `wait_for` and `search` do not use the selector engine; each has its own substring matcher over type name, `Name` and text (Z `.../Handlers/WaitForHandler.cs:129-152`, `.../Handlers/SearchHandler.cs:60-92`), so `wait_for` cannot take the selector a `click` just used.

`wait_for` timing out is returned as a *success* payload `{success:false, error:"Timeout…"}`, which the legacy-error bridge then turns into `INTERNAL`, not `TIMEOUT` (`WaitForHandler.cs:34-35` with `RequestDispatcher.cs:219-222`).

The event bus hooks `PropertyChanged` on every visual of a window once, the first time it sees the window, and rescans only for new windows every second (Z `src/Zafiro.Avalonia.Mcp.AppHost/Events/EventBus.cs:201-242`), so visuals created later (a new datagrid row, a re-templated panel) never publish `property_changed`.

### AvaloniaMcp: 19 methods

Dispatch table (A `src/AvaloniaMcp.Diagnostics/DiagnosticServer.cs:226-252`): `list_windows`, `get_visual_tree`, `get_logical_tree`, `find_control` (`name`/`typeName`/`text`, `maxResults` 20), `get_focused_element`, `get_control_properties` (`propertyNames`), `get_data_context` (`expandProperty`), `get_applied_styles`, `get_resources`, `get_binding_errors`, `click_control`.

It goes on with `set_property` (`propertyName`, `value` as string, converted for string/bool/int/double/float/enum/Thickness), `input_text` (`text`, `pressEnter`), `invoke_command` (`commandName` on the DataContext, `parameter` string), `take_screenshot` (`controlId?`, `windowIndex`).

It ends with `wait_for_property` (`propertyName`, `expectedValue`, `timeoutMs` 30000, `pollIntervalMs` 500), `get_scroll_info`/`scroll` (same handler), `get_scrollable_items`, `ping`.

Every control is addressed by `controlId`. There is no key-with-modifiers request and no event subscription. The survey's "`RaiseEvent(KeyEventArgs)`" for AvaloniaMcp is only the Enter key that `input_text` raises when `pressEnter` is set (A `src/AvaloniaMcp.Diagnostics/Handlers/InteractionHandler.cs:135-143`).

## 3. Selectors and element identity

### Zafiro

Grammar (Z `src/Zafiro.Avalonia.Mcp.Protocol/Selectors/SelectorParser.cs:6-32`): `selectorList := path ("," path)*`; `path := compound (combinator compound)*` with `>>` or whitespace = descendant and `>` = child; `compound := (Type | "*")? ("#" id)? filter*`; `#123` is a node id, `#Name` is `[Name=Name]`.

`filter := "[" attr op value "]" | "[dc:'<C# predicate>']" | ":" pseudo("(" arg ")")?`; ops `=`, `*=`, `^=`, `$=`, all case-insensitive (Z `src/Zafiro.Avalonia.Mcp.AppHost/Selectors/SelectorEngine.cs:200-207`).

Examples from the parser: `Button:has-text("Sign in"):enabled`, `ListBoxItem[dc.Id=42]`, `ListBox >> ListBoxItem:nth(2)`, `*[role=button]:nth(0)`.

Attribute paths: `Name`, `AutomationId` (`AutomationProperties.GetAutomationId`), `Text` (TextBox/TextBlock text, or string header/content), `Role`, otherwise a reflected public property path on the control, or on the DataContext with `dc.` (Z `.../Selectors/SelectorEngine.cs:210-250`).

Pseudo-classes: `:visible`, `:hidden`, `:enabled`, `:disabled`, `:focused` (keyboard focus within), `:checked`, `:has-text(s)`, `:role(r)`, `:nth(i)` applied over the whole path's matches (`:282-296`, `:140-146`).

A type matches by exact name on the type or any base type, then by **substring** of the runtime type name, so `Button` also matches `ButtonSpinner` and a custom `RadarButtonBar` (`:340-349`). Roles are a fixed map (checkbox, radio, togglebutton, button, textbox, combobox, tab, listitem, menuitem, slider, else the lower-cased type name) (`:351-365`).

`[dc:'…']` predicates compile C# through Roslyn scripting with a 200 ms limit per evaluation (Z `.../Selectors/RoslynDataContextPredicateEvaluator.cs:9-27`); that is why the AppHost package depends on `Microsoft.CodeAnalysis.CSharp.Scripting`.

Identity across calls: every returned node carries an integer `nodeId` from a process-wide registry of weak references, assigned on first sight and reused for the same visual (Z `src/Zafiro.Avalonia.Mcp.AppHost/Handlers/NodeRegistry.cs:28-38,70-75`). A node that has been collected, or detached from the tree (no visual parent and not a `Window`), resolves as stale with the hint to re-query (`:59-68`).

Since v2 every action takes a selector string; `#42` is the way to reuse a node id (Z `AGENTS.md:40`). A selector resolving to more than one element fails with `AMBIGUOUS_SELECTOR` (Z `src/Zafiro.Avalonia.Mcp.AppHost/Selectors/SelectorRequestHelper.cs:18-37`).

Scope: selectors walk `GetVisualDescendants()` of every root (Z `.../Selectors/SelectorEngine.cs:122-123`). Roots are the desktop lifetime's `Windows`, any TopLevel holding keyboard focus, and every TopLevel that has become visible since the registry's static constructor ran (a class handler on `IsVisibleProperty` for `TopLevel`) (Z `.../Handlers/NodeRegistry.cs:17-26,84-111`).

That third source is how separate-HWND `PopupRoot`s (menus, flyouts, combo drop-downs, context menus) enter the tree, and there are tests for it (Z `test/Zafiro.Avalonia.Mcp.Tests/Handlers/PopupRootExposureTests.cs`).

Owned windows and dialogs are ordinary entries of `desktop.Windows`, so they are included. With `OverlayPopups = true`, which YAAT's automation mode will set, Avalonia hosts a popup as an `OverlayPopupHost` inside the window's `PopupOverlayLayer` instead of a `PopupRoot` (AV `src/Avalonia.Controls/Primitives/OverlayPopupHost.cs#L169-L186`), so popups are plain descendants of their window and no root tracking is needed.

### AvaloniaMcp

`controlId` is either `#Name` (`FindControl` then a descendant scan) or `TypeName` / `TypeName[i]`, the i-th exact type-name match across all windows in visual-tree order (A `src/AvaloniaMcp.Diagnostics/Handlers/ControlResolver.cs:21-70`).

There is no AutomationId, text or attribute matching in `controlId`, and no node ids: identity is re-resolved from the string on every call, so `Button[3]` shifts when the tree changes. Roots are `desktop.Windows` only (`:72-77`), so separate-HWND popups are invisible to it.

## 4. How input is raised

Both projects run every handler inside `Dispatcher.UIThread.InvokeAsync` and never touch OS input, so neither moves the cursor or activates a window. Neither documents behaviour for an inactive window, and neither needs focus for its main paths, because they bypass the input manager.

### Zafiro `click`

Semantic first (Z `src/Zafiro.Avalonia.Mcp.AppHost/Handlers/InputHandler.cs:40-185`): a `TextBlock` is redirected to its nearest Button/MenuItem/ListBoxItem/TabItem/ComboBoxItem/TreeViewItem ancestor (`:42-56`); a disabled element or a command whose `CanExecute` is false is refused (`:62-72`); `ToggleButton` flips `IsChecked` (`:74-78`).

A `Button` runs its `Command`, else opens its `Flyout`, else raises `Button.ClickEvent` (`:80-96`); a `MenuItem` handles check/radio toggling, raises `MenuItem.ClickEvent` and closes the menu (`:98-105,235-265`); list, tab, tree and other `SelectingItemsControl` items are selected by setting `SelectedIndex`/`IsSelected` (`:109-168`).

Only when none applies does it call `Focus()` and raise a synthetic `PointerPressedEventArgs`/`PointerReleasedEventArgs` pair at the control's **centre**, left button, no modifiers (`:170-177,208-233`), and it then reports `UNSUPPORTED_OPERATION` "the click result could not be verified" even though the events were raised. There is no way to click at a given point, with the right button, with modifiers, or twice.

### Zafiro keys and text

`key_down`/`key_up` build a `KeyEventArgs` with `Key` and `KeyModifiers` parsed from `ctrl/control`, `shift`, `alt`, `meta/win/cmd` and call `element.RaiseEvent` on the selector's element (Z `.../Handlers/InputHandler.cs:303-379,439-459`). `key_down` with `text` sets `TextBox.Text` outright (`:309-317`); `text_input` sets `TextBox.Text` (or the first descendant TextBox's) and optionally raises an Enter `KeyDown` (`:407-436`).

Consequences, from Avalonia 12.1.3's `KeyboardDevice.ProcessRawEvent`: real key input walks `KeyBindings` from the focused element up through its visual parents **before** raising `KeyDown` (AV `src/Avalonia.Base/Input/KeyboardDevice.cs#L261-L294`), and characters arrive as a separate `TextInputEvent` (same file `#L300-L311`).

A `RaiseEvent(KeyDown)` therefore skips every `KeyBinding` (window shortcuts) and never types a character; setting `Text` replaces the whole content, moves no caret and raises no `TextInput`, so `TextInput`-driven logic (autocomplete, per-keystroke handlers) never sees the text.

Modifiers carried on the `KeyEventArgs` do work for handlers that read `e.KeyModifiers`, including TextBox's own gestures: `KeyGesture.Matches` compares `keyEvent.KeyModifiers` (AV `src/Avalonia.Base/Input/KeyGesture.cs#L158-L160`), not the OS keyboard state that limits posted messages.

### AvaloniaMcp

`click_control` handles `Button` only: execute `Command` if `CanExecute`, else call the protected `Button.OnClick` by reflection; any other control returns `clicked:false` "pointer simulation is not supported" (A `src/AvaloniaMcp.Diagnostics/Handlers/InteractionHandler.cs:14-44`).

`input_text` sets `Text` on a TextBox, AutoCompleteBox or the first descendant TextBox, plus an optional Enter `KeyDown` (`:80-143`). `set_property` sets any registered `AvaloniaProperty` (`:46-78`); `invoke_command` runs an `ICommand` property of the control's DataContext by name (`:145-189`).

### What Avalonia 12.1 allows beyond both (checked against source, not tried)

- **Typing that behaves like typing:** `TextInputEventArgs` is public with a settable `Text` (AV `src/Avalonia.Base/Input/TextInputEventArgs.cs`), and `TextBox.OnTextInput` inserts at the caret with no focus check, only `IsReadOnly` (AV `src/Avalonia.Controls/TextBox.cs#L1178-L1193`). Raising `InputElement.TextInputEvent` on the target TextBox types without focus and runs the app's `TextInput` handlers.
- **Shortcuts:** `KeyBinding.TryHandle(KeyEventArgs)` and `KeyGesture.Matches` are public (AV `src/Avalonia.Base/Input/KeyBinding.cs#L34`), so a host can repeat `KeyboardDevice`'s binding walk from the target up its visual parents, then `RaiseEvent` the `KeyDown`, and reproduce real key routing with explicit modifiers.
- **Pointer at a point, any button:** `PointerPressedEventArgs` takes a `clickCount` (AV `src/Avalonia.Base/Input/PointerEventArgs.cs#L172-L179`), and a right-button release on the source control raises `ContextRequested` through `Control.OnPointerReleased` (AV `src/Avalonia.Controls/Control.cs#L467-L478`).

  A synthetic right-click can therefore open a context menu; hit-testing to find the control under a point is the host's job. Pointer capture, hover and enter/leave do not happen, because the `MouseDevice` is bypassed.
- **Raw injection through the input manager** (the Avalonia.Headless route, AV `src/Headless/Avalonia.Headless/HeadlessWindowImpl.cs#L302-L383`) needs private API on a real window.

  `IInputManager` and `RawKeyEventArgs` are `[PrivateApi]` (AV `src/Avalonia.Base/Input/IInputManager.cs#L11-L12`, `src/Avalonia.Base/Input/Raw/RawKeyEventArgs.cs#L11-L12`), the concrete `InputManager` is `internal` (AV `src/Avalonia.Base/Input/InputManager.cs#L11`), and `TopLevel.InputRoot` is `internal` (AV `src/Avalonia.Controls/TopLevel.cs#L140`).

  Compiling against them takes `AvaloniaAccessUnstablePrivateApis=true`, which emits warning `AVA3001` (`buildTransitive/AvaloniaPrivateApis.targets` in the Avalonia 12.1.0 package), so under YAAT's warnings-as-errors it needs a justified `NoWarn`, and it breaks on Avalonia upgrades.
- **Focus on an inactive window:** `WindowBase` sets the focus scope on activation and only flips `IsActive` on deactivation (AV `src/Avalonia.Controls/WindowBase.cs#L332-L354`), so focus inside an inactive window is kept. Whether `Focus()` from code makes Win32 activate the window was not checked; the paths above never need focus.
- **Outside the reach of any in-process route:** YAAT's process-wide SharpHook key hook (`App.GlobalKeyHookEnabled`, see the plan's map) sees OS keyboard input only. A feature bound through it (push-to-talk) cannot be driven over the pipe, only through real-mode input.

## 5. Screenshots

Zafiro: with a selector, render that element; without one, render the **last `PopupRoot`** among the roots if any popup is open, else the first root (Z `src/Zafiro.Avalonia.Mcp.AppHost/Handlers/ScreenshotHandler.cs:34-35`). So a no-selector screenshot with a menu open returns the menu alone, not the window with the menu over it.

Capture is `new RenderTargetBitmap(new PixelSize(bounds.Width, bounds.Height))` → `Render(target)` → PNG → base64, returned as `{nodeId,targetType,data,mimeType,width,height,sizeBytes}` (`:43-65`). AvaloniaMcp is the same, targeting a control or `windows[windowIndex]`, and refuses a zero-size target (A `src/AvaloniaMcp.Diagnostics/Handlers/InteractionHandler.cs:191-231`).

Both use the one-argument constructor, which is 96 DPI (AV `src/Avalonia.Base/Media/Imaging/RenderTargetBitmap.cs#L18-L19`), so on a 150 % monitor the image is at logical resolution, two-thirds of what the user sees. `Render` is `ImmediateRenderer.Render(ctx, visual)` (`#L47-L51`), which repaints the visual subtree on the UI thread and needs neither the compositor nor window activation.

With `OverlayPopups = true` a window capture includes its open popups, because they are descendants. Neither project documents a visibility requirement of its own. Zafiro says only that headless screenshots are "best-effort" (survey); Avalonia's documented requirement is the one the survey quotes (attached to a visible window). Not checked: how YAAT's radar `ICustomDrawOperation` Skia drawing comes out under `ImmediateRenderer` into an RTB.

## 6. Licence, packages, Avalonia version

| | Licence | NuGet | Avalonia | Other dependencies of the in-app part |
|---|---|---|---|---|
| Zafiro.Avalonia.Mcp | MIT, © 2026 José Manuel Nieto (Z `LICENSE`) | `Zafiro.Avalonia.Mcp.AppHost` and `Zafiro.Avalonia.Mcp.Protocol` 0.1.10 (published 2026-09-01), `Zafiro.Avalonia.Mcp.Tool` (dnx tool). 49 AppHost versions from 0.0.8 to 0.1.10 (flat-container index, 2026-10-01), with a breaking v2 of the protocol in between (Z `MIGRATION-v2.md`) | Compiled against `Avalonia [11.3.17, )`; samples and tests on 12.0.2 (Z `Directory.Build.props`); README: "11.3.17+ and 12.x". Its property-provenance adapter reads Avalonia value-store internals by reflection (Z `README.md:265`, `src/Zafiro.Avalonia.Mcp.AppHost/Provenance/AvaloniaDiagnosticsAdapter.cs`) | `Microsoft.CodeAnalysis.CSharp`, `.Common` and `.CSharp.Scripting` 4.14.0 (Roslyn, in the shipped client), `System.Text.Json` 9.0.5; TFMs net8.0/net10.0 |
| AvaloniaMcp | MIT, © 2026 adirh3 (A `LICENSE`) | `AvaloniaMcp.Diagnostics` and `AvaloniaMcp` 0.4.0 | `Avalonia 11.2.*` (A `src/AvaloniaMcp.Diagnostics/AvaloniaMcp.Diagnostics.csproj`); README "Avalonia 11.2+"; nothing in the repo exercises 12. Last commit 2026-03-11 | none; TFMs net8.0/net9.0/net10.0 |
| Avalonia DevTools MCP | Commercial (Avalonia Plus) | `AvaloniaUI.DiagnosticsSupport` 2.2.3 + `avdt` tool | 11 and 12 (licence variable differs by version, per docs) | closed |

Referencing Zafiro's AppHost from `Yaat.Client` works mechanically (NuGet resolves Avalonia to our 12.1), but it brings Roslyn scripting into the client and its installer, an always-reflective provenance adapter, and a fast-moving 0.x package. Copying is allowed under MIT with the copyright notice kept.

The parts worth copying are small: `SelectorParser.cs` (405 lines, no Avalonia dependency), `SelectorEngine.cs` (395 lines, without the Roslyn evaluator), `NodeRegistry.cs` (200), the semantic `Click` (~150 of `InputHandler.cs`), the error codes and the envelope (line counts from `wc -l` on the clone).

## 7. Options for YAAT's protocol

Constraints carried in from the plan: the outside MCP keeps `launch_yaat`, `tail_yaat_log`, `stop_process`, `set_input_mode` and the CRC path, and its YAAT tools switch to the pipe. Agents and docs already know today's tool names and their element-id workflow. YAAT has custom canvases (radar, ground) where a click means a position, not a control; a command box whose behaviour depends on keystrokes; window shortcuts; context menus; and modifier clicks.

### Option C (recommended): our own small host, using Zafiro's envelope and a subset of its selector grammar, with today's tool names plus `wait_for`

How it works: `Yaat.Client` gets an `Automation/` host, started only when the env var is set, on pipe `yaat-automation-{PID}` with `PipeOptions.CurrentUserOnly`. The discovery file goes under `YaatPaths` rather than `%TEMP%`, though `launch_yaat` already knows the PID.

The wire format is Zafiro's: one JSON line per message, `{id,method,params}` → `{id,result}` or `{id,errorInfo:{code,message,suggested,details}}`, with its error codes. Methods mirror the MCP tools 1:1: `list_windows`, `find_elements`, `dump_tree`, `get_value`, `click`, `click_point`, `invoke`, `set_text`, `send_keys`, `focus`, `screenshot`, plus `wait_for`.

`find_elements` keeps its `name`/`automationId`/`controlType` arguments and also accepts a `selector` in Zafiro's grammar minus `dc:` predicates (no Roslyn), using exact type matching rather than substring, so `Button` does not match `RadarButtonBar`.

Results carry Zafiro-style registry ids, which the MCP passes back as today's `elementId`. `wait_for` takes the same selector and conditions as Zafiro (`exists`, `not_exists`, `visible`, `enabled`, `text_equals`, `text_contains`, `count_equals`), and a timeout returns `TIMEOUT`. Input:
- `click` is semantic first (Zafiro's ladder), then a synthetic pointer pair carrying button, `clickCount` and modifiers.
- `click_point` takes window-relative coordinates in screenshot pixels, hit-tests to the deepest control, and raises the pointer pair there, so the radar and ground views and right-click context menus work.
- `send_keys` parses the existing SendKeys syntax: printable runs become `TextInputEvent`s, and keys become a `KeyBinding` walk then `KeyDown`/`KeyUp` with explicit modifiers.
- `set_text` sets `Text`, or types it when `typed:true`.

`screenshot` renders the window (popups included through `OverlayPopups`) at `RenderScaling` × 96 DPI. Event subscriptions are left out until a task needs them; `wait_for` covers the common case.

Worst case in use: we own and test about 1,000-1,500 lines, mostly copied, and keep the pointer, key and binding emulation correct across Avalonia upgrades. An agent cannot drive anything reached only through OS input: SharpHook's push-to-talk, native dialogs (the file picker is already a separate open decision), drag gestures that need pointer capture, and hover or tooltip states.

The tree is Avalonia's visual tree, not UIA's, so `controlType` values change (`TextBox`, not `Edit`), and the client-driver docs and skills that name UIA types need a pass.

### Option A: adopt Zafiro nearly verbatim (reference the AppHost, or run its MCP server beside ours)

How it works: `Yaat.Client` references `Zafiro.Avalonia.Mcp.AppHost` and calls `UseMcpDiagnostics()` only when the env var is set. The simplest form registers `Zafiro.Avalonia.Mcp.Tool` as a second MCP server in `.mcp.json`, and our MCP drops its YAAT tools apart from launch, log and stop.

Agents get all 52 methods, the full selector language with `dc:` predicates, `fill_form`, subscriptions, binding and style inspection, contact-sheet recordings, and Zafiro's agent instructions page, at no code cost.

Worst case in use: the exact gaps YAAT needs most are Zafiro's weakest paths. There is no click at a point, no right-click, no modifier click and no double-click, so radar, ground, datablock and context-menu interactions are impossible; the generic pointer fallback reports `UNSUPPORTED_OPERATION` even when it fired. Keys skip `KeyBindings` and never type characters, and text arrives as a whole-`Text` replacement, so command-box autocomplete and window shortcuts cannot be exercised.

`wait_for` cannot reuse a selector. A no-selector screenshot with a menu open shows only the menu, and every image is at 96 DPI. Roslyn ships inside the client. The pipe has no ACL beyond the default. Two MCP servers expose overlapping names (`list_windows`, `click`, `screenshot`), which confuses agents. Fixing any of this means forking, and that converges on option C with more code to carry.

### Option B: minimal set mapped 1:1 onto today's tools, no selectors, no `wait_for`

How it works: the pipe exposes exactly today's YAAT tools with today's arguments. `find_elements` keeps exact name, AutomationId and type matching; ids are registry ids. The input rules match option C (semantic click, point click, typed keys with modifiers, scaled screenshot), but there is no selector parser, no `wait_for` and no events. The MCP side only swaps its backend; tool descriptions and docs barely change.

Worst case in use: agents keep polling with `get_value`/`find_elements` loops and sleeps to wait for UI state, which is the slow, flaky pattern in the friction log. Every action needs a separate find, because an element cannot be named in the action itself. Ambiguous matches come back as lists rather than structured `AMBIGUOUS` errors with a hint. It is the cheapest to build, but adding `wait_for` and selectors later means changing the protocol twice.

### Ranking

1. **C**: it keeps the agent-facing surface stable, takes the parts of Zafiro that are good (envelope, error codes, selector grammar, semantic click, node registry) and replaces the parts that fail YAAT's cases (point and modifier clicks, real key routing and typing, DPI, ACL, Roslyn).
2. **B**: a valid first step if C's scope is too large for one landing, as long as the envelope and error codes are Zafiro's from day one, so `wait_for` and selectors can be added without a protocol change.
3. **A**: only worth it if broad inspection (styles, bindings, provenance) matters more than driving YAAT's canvases and command box, and the plan says it does not.

## References

- [SuperJMN/Zafiro.Avalonia.Mcp @ b68c80a0](https://github.com/SuperJMN/Zafiro.Avalonia.Mcp/tree/b68c80a02429c8329e4d68925720677b207c1a66): transport, discovery, protocol, selectors, handlers cited above
- [adirh3/AvaloniaMcp @ 6cdde636](https://github.com/adirh3/AvaloniaMcp/tree/6cdde6364dfa1285204a074b49d491510170e164): diagnostics server and handlers cited above
- [Avalonia DevTools MCP docs](https://docs.avaloniaui.net/tools/developer-tools/mcp): official tool list and licensing
- [AvaloniaUI/Avalonia @ 12.1.3](https://github.com/AvaloniaUI/Avalonia/tree/12.1.3): `KeyboardDevice`, `KeyGesture`, `KeyBinding`, `TextBox`, `Control`, `PointerEventArgs`, `RenderTargetBitmap`, `OverlayPopupHost`, `WindowBase`, `TopLevel`, `IInputManager`, `RawKeyEventArgs`, Headless
- [NuGet: Zafiro.Avalonia.Mcp.AppHost](https://www.nuget.org/packages/Zafiro.Avalonia.Mcp.AppHost), [AvaloniaMcp.Diagnostics](https://www.nuget.org/packages/AvaloniaMcp.Diagnostics), [AvaloniaUI.DiagnosticsSupport](https://www.nuget.org/packages/AvaloniaUI.DiagnosticsSupport): versions and dependency ranges
- [Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights), [PipeOptions.CurrentUserOnly](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0): pipe ACL defaults
