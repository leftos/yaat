# Client driver MCP server

`tools/Yaat.ClientDriver.Mcp/` is an MCP stdio server that lets an agent drive the **real** YAAT desktop client — and a running CRC — through Windows UI Automation: enumerate windows and controls, click, type, take a screenshot of a window, read the client log. It exists for UI-path bugs that a view-model test cannot reproduce (a menu item that does nothing, a dialog that never opens) and for comparing what CRC draws against what YAAT shows for the same aircraft.

Windows only (`net10.0-windows`, `System.Windows.Automation`); `EnableWindowsTargeting` keeps it building in the Linux CI solution build.

## Running it

`.mcp.json` at the repo root registers it for Claude Code as `yaat-client-driver`:

```json
{ "type": "stdio", "command": "pwsh", "args": ["-NoProfile", "-File", "tools/Yaat.ClientDriver.Mcp/launch.ps1"] }
```

- **Build it once per clone, and after changing it:** `dotnet build tools/Yaat.ClientDriver.Mcp`. The launcher never builds: stdout is the protocol channel, and MSBuild output would corrupt it. Nothing reaches stdout before the first JSON-RPC frame; all logging goes to stderr.
- **It runs from a copy, not from `bin/`.** `launch.ps1` copies the build output to `%LOCALAPPDATA%/yaat/client-driver-mcp/run-<launcher pid>/` and starts `dotnet <copy>/Yaat.ClientDriver.Mcp.dll`. A server running out of `bin/` locks its own exe and dlls, and the solution build — which prek runs on every commit — then fails with `MSB3027`. Copies whose launcher has exited are pruned at the next launch. A running session keeps the build it started with; reconnect the server (`/mcp`) to pick up a rebuild.
- Claude Code reads `.mcp.json` at session start and asks once to approve a project server.
- The server's working directory is the repo root, so the relative defaults below resolve there.
- `launch_yaat` needs a built client: `dotnet build src/Yaat.Client`.

## Tools

| Tool | What it does |
|---|---|
| `launch_yaat(appDataDir, exePath, waitSeconds)` | Starts `Yaat.Client.exe` (only that file name) with `YAAT_APPDATA_DIR` pointed at a scratch directory (default `.tmp/client-driver/appdata`), so the developer's preferences and favorites stay untouched. Returns the pid, the windows and the log path. |
| `list_processes(nameContains)` | Finds processes to attach to — `Yaat.Client`, `CRC`. |
| `list_windows(pid)` | Every top-level UIA window of a process, with an element id each. |
| `dump_tree(elementId, maxDepth)` | Control-view tree under an element, one line per node (capped at 400 lines). |
| `find_elements(rootElementId, name, automationId, controlType)` | Descendants matching every criterion given (at least one is required), at most 50. |
| `get_value(elementId)` | `ValuePattern` value of a text box. |
| `screenshot(windowElementId, maxWidth)` | Foregrounds the window, captures its bounds, returns the PNG inline and saves it under `.tmp/client-driver/shots/`. |
| `set_input_mode(mode)` | `virtual` (the default) or `real`, for the whole server; see **Input modes** below. |
| `invoke(elementId)` | `InvokePattern` — buttons. |
| `click(elementId, button, doubleClick, modifiers)` / `click_point(x, y, …)` | A mouse click at the element's centre; `click_point` takes screen coordinates, for surfaces UIA cannot see into. `modifiers` (`shift`, `ctrl`, `shift+ctrl`) works in real mode only. |
| `set_text(elementId, text)` / `send_keys(keys, focusElementId)` / `focus(elementId)` | Text entry. `send_keys` takes SendKeys syntax (`{ENTER}`, `^a`, `%{F4}`); virtual mode accepts only plain characters, braced literals such as `{+}`, and `{ENTER}` `{ESC}` `{TAB}` `{BACKSPACE}`/`{BS}` `{DEL}` `{HOME}` `{END}` the arrows and `{F1}`–`{F12}`. |
| `tail_yaat_log(appDataDir, lines)` | Tail of `<appDataDir>/yaat-client.log`. |
| `stop_process(pid)` | Closes, then kills, a client this server launched (matched by pid **and** start time) or any process named `Yaat.Client`. It never stops anything else — CRC included. |

### Input modes

- **`virtual` (default).** Clicks and keys are posted as window messages (`WM_MOUSEMOVE`, `WM_LBUTTONDOWN`…, `WM_KEYDOWN`/`WM_KEYUP`, `WM_CHAR`) straight to the YAAT window. The real mouse pointer never moves and the foreground window never changes, so the developer can keep working while an agent drives the client. It also works when an elevated window holds the foreground, which makes Windows refuse `SetCursorPos`/`SendInput`. Screenshots use `PrintWindow`, so a covered window captures correctly. It reaches YAAT windows only; CRC needs `real`. Avalonia reads Shift/Ctrl from the keyboard state rather than from the message, so a modified click or a `^`/`%`/`+` key is refused, not sent unmodified. `set_text` focuses with a posted click, then types (End, one Backspace per character, the text). A posted click never uses UIA `SetFocus` or `ValuePattern`, since both can take the foreground.
- **`real`.** `SendInput` / SendKeys, as a user would: the cursor moves and the window is brought to the front, so a video shows the pointer. Typing refuses, rather than sending keys to whatever else is in front, when the target did not take the foreground.
- Every input tool's result ends with `(virtual)` or `(real)`. The client drops input while navigation data loads, for ~10 s after launch in both modes; wait for the status bar's "Navigation data loaded".

Element ids (`e1`, `e2`, …) come from `list_windows` / `find_elements` / `dump_tree` and live as long as the server process. A described element reads `e2 | ControlType.Edit |  | id=CommandInput | enabled=True | rect=(2008,984 996x25)`.

## What the YAAT client exposes

- The main window's title is `YAAT`. Avalonia maps `x:Name` to the UIA AutomationId: `CommandInput` (the command box), `ConnectMenuItem`, `DisconnectMenuItem`. Menu items without an `x:Name` are found by Name (the header without its `_` accelerator).
- **Menus need clicks.** `ExpandCollapsePattern` is unsupported on Avalonia menus, and a menu's items do not exist until it is open: `click` the menu, then `find_elements` under the **main** window, where UI Automation files the popup and its items; `list_windows` never lists the popup. At the Win32 level the popup is its own owned `WS_EX_TOOLWINDOW` window, and in virtual mode a click on one of its items is posted to that window.
- The native file dialog is not under the client's UIA windows; drive it with `send_keys("<path>{ENTER}")` once it has focus.
- A button that "does nothing" usually logged `Unhandled UI-thread exception (recovered)` — `tail_yaat_log` first.

## What CRC exposes

CRC is a WPF app (`CRC.exe`); its display windows are titled `CRC : <n>` or `CRC : <n> : <header>`. Its scopes (STARS, Tower Cab, ASDE-X, SAID, ERAM) are one OpenGL surface inside the window: UIA reaches CRC's windows, menus and dialogs but **not** tracks or datablocks. Read a scope with `screenshot`, act on it with `click_point`. An elevated CRC blocks injected input; the input tools then fail with an explicit "SendInput was blocked" error rather than reporting a click that never happened. The CRC path has not been exercised against a live CRC yet.

## Scripts

- `pwsh tools/Yaat.ClientDriver.Mcp/smoke.ps1` — protocol only: starts the server, checks every stdout line is JSON and every tool is listed. Opens no window.
- `pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1` — launches a real client for ~15 s and sends no input. `-Background` drives it in virtual mode (opens and closes two menus, types into the command box, `set_text` round trip) and asserts the foreground window and the cursor never change; `-WithInput` does the same in real mode. If CRC is running it lists and captures its first display window.
- `McpStdio.ps1` — the dot-sourced JSON-RPC-over-stdio plumbing both scripts share.
