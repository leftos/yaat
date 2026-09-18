# Client driver MCP server

`tools/Yaat.ClientDriver.Mcp/` is an MCP stdio server that lets an agent drive the **real** YAAT desktop client — and a running CRC — through Windows UI Automation: enumerate windows and controls, click, type, take a screenshot of a window, read the client log. It exists for UI-path bugs that a view-model test cannot reproduce (a menu item that does nothing, a dialog that never opens) and for comparing what CRC draws against what YAAT shows for the same aircraft.

Windows only (`net10.0-windows`, `System.Windows.Automation`); `EnableWindowsTargeting` keeps it building in the Linux CI solution build.

## Running it

`.mcp.json` at the repo root registers it for Claude Code as `yaat-client-driver`:

```json
{ "type": "stdio", "command": "dotnet", "args": ["run", "--project", "tools/Yaat.ClientDriver.Mcp", "--no-build"] }
```

- **Build it once per clone, and after changing it:** `dotnet build tools/Yaat.ClientDriver.Mcp`. `--no-build` is deliberate: stdout is the protocol channel, and a building `dotnet run` would write MSBuild output into it. With `--no-build`, nothing reaches stdout before the first JSON-RPC frame; all logging goes to stderr.
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
| `invoke(elementId)` | `InvokePattern` — buttons. |
| `click(elementId, button, doubleClick)` / `click_point(x, y, …)` | A real mouse click (`SendInput`). `click_point` takes screen coordinates, for surfaces UIA cannot see into. |
| `set_text(elementId, text)` / `send_keys(keys, focusElementId)` / `focus(elementId)` | Text entry: `ValuePattern` when the control supports it, SendKeys otherwise. `send_keys` takes SendKeys syntax (`{ENTER}`, `^a`, `%{F4}`). |
| `tail_yaat_log(appDataDir, lines)` | Tail of `<appDataDir>/yaat-client.log`. |
| `stop_process(pid)` | Closes, then kills, a client this server launched (matched by pid **and** start time) or any process named `Yaat.Client`. It never stops anything else — CRC included. |

Element ids (`e1`, `e2`, …) come from `list_windows` / `find_elements` / `dump_tree` and live as long as the server process. A described element reads `e2 | ControlType.Edit |  | id=CommandInput | enabled=True | rect=(2008,984 996x25)`.

## What the YAAT client exposes

- The main window's title is `YAAT`. Avalonia maps `x:Name` to the UIA AutomationId: `CommandInput` (the command box), `ConnectMenuItem`, `DisconnectMenuItem`. Menu items without an `x:Name` are found by Name (the header without its `_` accelerator).
- **Menus need real clicks.** `ExpandCollapsePattern` is unsupported on Avalonia menus, and a menu's popup is a *separate top-level window* whose items do not exist until the menu is open: `click` the menu, `list_windows(pid)` again, then `find_elements` under the popup.
- The native file dialog is not under the client's UIA windows; drive it with `send_keys("<path>{ENTER}")` once it has focus.
- A button that "does nothing" usually logged `Unhandled UI-thread exception (recovered)` — `tail_yaat_log` first.

## What CRC exposes

CRC is a WPF app (`CRC.exe`); its display windows are titled `CRC : <n>` or `CRC : <n> : <header>`. Its scopes (STARS, Tower Cab, ASDE-X, SAID, ERAM) are one OpenGL surface inside the window: UIA reaches CRC's windows, menus and dialogs but **not** tracks or datablocks. Read a scope with `screenshot`, act on it with `click_point`. An elevated CRC blocks injected input; the input tools then fail with an explicit "SendInput was blocked" error rather than reporting a click that never happened. The CRC path has not been exercised against a live CRC yet.

## Scripts

- `pwsh tools/Yaat.ClientDriver.Mcp/smoke.ps1` — protocol only: starts the server, checks every stdout line is JSON and every tool is listed. Opens no window.
- `pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1` — launches a real client for ~15 s, takes the foreground once, sends no input. `-WithInput` additionally types into the command box and moves the real mouse to open two menus. If CRC is running it lists and captures its first display window.
- `McpStdio.ps1` — the dot-sourced JSON-RPC-over-stdio plumbing both scripts share.
