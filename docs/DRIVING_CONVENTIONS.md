# YAAT driving conventions

Agents drive the real YAAT desktop client, and a running CRC, through `yaat-client-driver`, the MCP stdio server in `tools/Yaat.ClientDriver.Mcp/` (registered in `.mcp.json`): a client it launches is driven over the client's own automation pipe (`src/Yaat.Client/Automation/`, switched on by `YAAT_AUTOMATION=1`), and everything else through Windows UI Automation. [`client-driver-mcp.md`](client-driver-mcp.md) is the main doc for the server, its tools and the pipe; [`plans/client-driver-mcp-friction.md`](plans/client-driver-mcp-friction.md) is the friction log the tools were built from. These are the lessons that shaped the server and the pipe, written down so the next change to either keeps them. The comment under each heading (`<!-- rule: slug -->`, with `scope: project` when the rule means nothing outside this repo) is the rule's identity for the user-level `conventions-sync` skill, which trades driving lessons with the owner's other projects that build an MCP server or an in-app hook for agents to drive an app through; a new rule may be written without one, and the next sync assigns it. This repo takes every section of the rulebook. The rules it declines are the ones about gamepad device ids, pointer marking for hover and drag gestures, CDP and webview arguments, and a hook injected per run (dial-in, arming, a shared hook file, keeping it out of commits, compiling it out of release builds), since the pipe is compiled into the client and gated at run time; the sync ledger holds each with its reason.

## Server plumbing

### Run the server from a per-launch shadow copy, never from its build folder
<!-- rule: run-server-from-copy -->

The launcher copies the build output to `%LOCALAPPDATA%/yaat/client-driver-mcp/run-<launcher pid>/` and starts `dotnet <copy>/Yaat.ClientDriver.Mcp.dll`. A server running out of `bin/` locks its own exe and dlls, and the solution build that prek runs on every commit then fails with `MSB3027`. Copies whose launcher is gone are pruned at the next launch (a copy is kept only while a `pwsh` with that pid lives). A running session keeps the build it started with; reconnecting the server (`/mcp`) picks up a rebuild. The launcher never builds; with no build output it exits 1 naming the build command on stderr.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\launch.ps1:5-11, 24-50; D:\yaat\docs\client-driver-mcp.md:15-16; D:\yaat\CLAUDE.md:25. Seen: MSB3027 on commit while a session held the server (documented).

### Keep stdout for JSON-RPC frames only, and never build in the launcher
<!-- rule: stdio-protocol-only -->

Nothing reaches stdout before the first JSON-RPC frame: the launcher writes its errors with `[Console]::Error` and never builds (MSBuild output would corrupt the channel), and the server routes every log level to stderr (`LogToStandardErrorThreshold = LogLevel.Trace`).
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Program.cs:8-16; launch.ps1:11, 27; D:\yaat\docs\client-driver-mcp.md:15. Seen: documented.

### Make every refusal name the command that clears it
<!-- rule: refusals-name-the-fix -->

A refusal for a missing prerequisite names the command that clears it: no build output (the launcher names the build command), no client exe (`dotnet build src/Yaat.Client`), no pipe answering (`is Yaat.Client built from this branch? See tail_yaat_log`), a client that exited before its window opened (`read the log with tail_yaat_log`), ffmpeg missing (`winget install Gyan.FFmpeg, then restart Claude Code so the client-driver MCP server starts with the new PATH`), an elevated target (`run this server elevated too`). In the C# MCP SDK only an `McpException`'s message reaches the agent; any other exception becomes `An error occurred invoking '<tool>'.`, so a refusal is thrown as an `McpException`. `launch_yaat` checks the exe and every `env` entry before it starts anything, so a refused call ran nothing and the agent fixes the call instead of debugging the client.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\ProcessTools.cs:71-93, 277, 389, 394; Recording\FfmpegPipeline.cs:55; NativeInput.cs:751-752; D:\yaat\docs\client-driver-mcp.md:86; D:\yaat\docs\plans\client-driver-mcp-friction.md:26; tests RecordToolsTests.cs:61. Seen: documented.

### Count every ceiling in load-adjusted time, with a wall-time backstop and a stall kill
<!-- rule: load-adjusted-timeouts -->

Other agents' builds and test runs load the machine, and a fixed wall-clock timeout turns that load into false failures. Run every ceiling the server enforces (the pipe's connect and request timeouts, `launch_yaat`'s `waitSeconds`, `batch_drive`'s cap, a caller's `timeoutMs`) on a clock that keeps wall time on an idle machine and advances by the machine's free share while other work loads it (sampled once a second with `GetSystemTimes`, the server's own work subtracted, never below 5%). Every ceiling also ends at 5x its value in wall time (the backstop), and a child process (the recorder, ffmpeg) that neither writes a log line nor uses CPU for 120 s is killed as stalled; the error says which of the three fired, with the load figures (`did not finish within 300 s of load-adjusted time (wall 812 s, machine free 37% on average)`). Durations of an action are not ceilings, and pings and kill graces stay in wall time. In another project the integration lanes ran at 15-35% load-adjusted time against wall on a loaded machine (measured there). For agents this means a timeout is never a pause: holding for a span of sim time is a `wait_until` on `sim_seconds`.
Source: godot-mcp's architecture doc and changelog; Windows docs for GetSystemTimes. Seen: 0 here (seeded from godot-mcp).

### Match every answer to its request, and drop the connection on any unclean request
<!-- rule: cancel-timed-out-requests -->

The pipe client keeps one request in flight per connection and matches each answer by its id. A request that does not complete cleanly (a broken pipe, the client's own timeout, the caller's cancel, an unparsable or `null` line, or an answer carrying another request's id) leaves the framing unknown, so the connection is disposed and the next request opens a fresh one. A cancel in particular can land after the request is on the wire; the host still answers it, and that late line would be read as the next request's answer. Dropping the connection is also the cancel: the client's dispatcher cancels the request running for a connection that goes, so a timed-out wait stops in the client and the next call finds it free. Timeouts: 5 s to connect, 30 s per request.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Pipe\PipeClient.cs:11-28, 60-72, 151-156, 174-196; tests PipeClientTests.cs:71-136; D:\yaat\src\Yaat.Client\Automation\AutomationDispatcher.cs:57-101. Seen: documented.

### Bound every wait on both sides, and return its outcome as data with the last values seen
<!-- rule: waits-bounded-outcome-as-data -->

The host clamps every wait (`wait_for` 100-30000 ms; `wait_until` 100-600000 ms, because a montage take runs about 315 s) and the server waits for the answer for the clamped wait plus 5 s, never the default 30 s request timeout, so the transport never cuts a full-length wait short. `wait_until` answers a timeout as a result, not an error (`not met in <N> ms at sim <S> s` plus each condition's last value and each `then` action's outcome); `batch_drive` returns JSON `{passed, steps, failedAt}` and never an MCP error, so the agent reads `passed`; where a timeout is an error (`wait_for`), its `TIMEOUT` carries the selector's last match count. An agent can then tell "never matched" from "matched the wrong count".
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\PipeTools.cs:25-47, 70-82; Tools\BatchTools.cs:38-50, 77-86; D:\yaat\src\Yaat.Client\Automation\Protocol\ErrorDetails.cs:30-34; tests WaitForAndFilePickTests.cs:138 (`WaitFor_LongTimeout_NotCutByClientTimeout`). Seen: documented.

### Own the processes you start: identify them by handle and start time, kill them on a failed launch
<!-- rule: own-launched-processes -->

The server keeps live `Process` handles for what it launched, because Windows reuses pids. `stop_process` acts only on a pid whose start time matches the launched handle, or on a process named `Yaat.Client`, and never stops CRC or anything else. `launch_yaat` starts only a file named `Yaat.Client.exe`, rejects an `env` entry that overwrites a variable the tool owns (`YAAT_APPDATA_DIR`, `YAAT_AUTOMATION`, `YAAT_CLOAK`, any case), and links its deadline into every pipe call so one stalled connect cannot outlive it. However the launch wait ends without a window (an early exit, the deadline, a cancel, a coded host error), the process tree is killed and its pid forgotten in both the launch table and the pipe directory, while the scratch app-data folder is kept for the log; a client that died is reported by its exit code, not by the timeout that raced it, and the server refuses to kill its own pid.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\ProcessTools.cs:37-38, 76-81, 186-241, 298-325, 339-394, 435-510; tests LaunchYaatTests.cs:522-712. Seen: documented.

### Name the client on every call and keep no default one
<!-- rule: no-default-session -->

One server process serves one Claude Code session, and several agents in that session share it and cannot be told apart, so a default target one agent set (the client the last pipe call reached) would send another agent's keys, waits and app-tool calls to its client. `launch_yaat` returns the client's pid and every tool that reaches a client takes it; with one live client the pid may be left out, with several the call is refused listing them, and the refusal and every pid argument's description tell agents to pass the pid from the first call. Calls to one client go one at a time over its pipe, while two clients are driven at once.
Source: godot-mcp's decision record, which replaced a default-session design. Seen: 0 here (seeded from godot-mcp).

### Reach app tools through two fixed MCP tools, never one MCP tool each
<!-- rule: fixed-tool-surface -->

The server exposes the client's app tools through two fixed tools (`list_app_tools`, `call_app_tool`), never one MCP tool per app tool, because an agent's `tools:` line never sees a tool added later.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\AppTools.cs:26-58; D:\yaat\docs\client-driver-mcp.md:41-42; D:\yaat\docs\research\2026-10-02-godot-mcp-ideas-for-client-driver.md:46-47. Seen: documented.

### Keep element ids in one server-lifetime registry shared by both backends, and batch by selector
<!-- rule: one-id-registry -->

MCP tool classes are constructed per call, so the id registry is a singleton: an id read from one tool must resolve in the next. UI Automation elements and in-app pipe nodes draw from one `eN` counter, so an id names exactly one backend and a tool that cannot drive it refuses it; a gone element gets one wording whichever backend it came from. Ids go stale between calls (a menu item found by `find_elements` was gone by the `click`), so `batch_drive` addresses elements by selector and refuses `nodeId`.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\ElementRegistry.cs:10-20, 87-89; Tools\BatchTools.cs:14-18, 36; D:\yaat\docs\plans\client-driver-mcp-friction.md:43. Seen: friction #23.

### Harden the recording shell-out: probe the encoder by encoding, pass paths through the environment
<!-- rule: recording-shellout-hardening -->

`h264_nvenc` is chosen only when a 0.1 s encode of a generated 256x256 picture succeeds, otherwise `libx264`: an ffmpeg build lists nvenc without a GPU, and a 64x64 probe fails "Frame Dimension less than the minimum supported value" on an RTX 4090, which would push every machine onto libx264. The pipeline `recorder … | ffmpeg …` runs under cmd, which expands `%NAME%` even inside quotes but never rescans what it expanded, so every path goes in as a quoted `"%YAAT_REC_<NAME>%"` set on the cmd process and a `%`, `!` or `&` in a folder name stays literal. A pipeline that dies within 300 ms of the start is reported with its exit code and the recorder's last log lines.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Recording\FfmpegPipeline.cs:57-63, 146-160, 211-224, 258; Recording\RecordingSession.cs:123; D:\yaat\docs\client-driver-mcp.md:43. Seen: measured once (RTX 4090 probe).

### Smoke-test the server over stdio exactly as registered
<!-- rule: test-server-over-stdio -->

`smoke.ps1` starts the server exactly as `.mcp.json` does, runs initialize → tools/list → one harmless call, and fails when any stdout line is not a JSON-RPC frame or a promised tool is missing; it opens no window and sends no input.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\smoke.ps1:1-8, 51-58; D:\yaat\docs\client-driver-mcp.md:99. Seen: documented.

### Test the server against the real in-app host, and live-check that the desktop never moved
<!-- rule: test-against-real-host -->

The MCP's tests run its pipe client against a real headless `AutomationHost` (Avalonia.Headless), not a mock of the protocol; a stub pipe host covers answers the real one cannot emit (a `null` line, a foreign id), and a test seam (`IProcessStarter`) drives launches without a real exe. `live-check.ps1 -Background` launches a real client and asserts after every step that the foreground window and the cursor position are unchanged, that every client window carries `WS_EX_NOACTIVATE` and sits below the launch foreground in Z order, and that minimising a console in front of the client never hands it the foreground. The live check takes the foreground for about 5 s, so ask before running it on someone's desktop.
Source: D:\yaat\docs\architecture.md:164; D:\yaat\tests\Yaat.ClientDriver.Mcp.Tests\StubPipeHost.cs; D:\yaat\tools\Yaat.ClientDriver.Mcp\live-check.ps1:10-19, 106-150; D:\yaat\docs\client-driver-mcp.md:100. Seen: documented.

## Driving an app

### Drive an app you own from inside its process
<!-- rule: drive-owned-app-in-process -->

A survey of desktop-automation MCPs found that no tool driving an app from outside its process fully avoids stealing focus (Windows-MCP, terminator, WinAppDriver, NovaWindows and FlaUI-MCP all foreground the window or move the cursor); the ones that never do run a named pipe inside the app. So a YAAT client with a live pipe is driven over it, and everything else (CRC, a client started without automation mode) falls back to UI Automation; the route is chosen per pid by "is there a live pipe", and the input-mode setting does not touch pipe calls.
Source: D:\yaat\docs\plans\client-driver-background.md:50-63, 99-102, 120; D:\yaat\docs\client-driver-mcp.md:3, 89. Seen: researched (survey 2026-10-01).

### Drive the real client, and write down what each lesser route cannot see
<!-- rule: real-app-over-frontend-only -->

Drive the YAAT client itself over its automation pipe for anything that touches the client's own state. The UI Automation fallback (CRC, or a client started without `YAAT_AUTOMATION=1`) is a lesser route, and the driving doc says exactly what it lacks, so a run that "shows nothing" is not mistaken for a bug: through UI Automation the client's errors do not come back with the call (read `tail_yaat_log`), a file dialog is the native picker the driver cannot fill, virtual input reaches YAAT windows only, a cloaked client is out of reach, and CRC's scopes are one OpenGL surface whose tracks and data blocks UI Automation cannot see. In another project a frontend-only route (a plain browser on the dev frontend) silently lacked the backend, so content, saved settings and live polling were missing, and that fact reached the docs five months after the route was first documented.
Source: D:\yaat\docs\client-driver-mcp.md:3, 56, 66-67, 89-90, 95; towercab-3d's project instructions and history on driving routes. Seen: 0 here (seeded from towercab-3d).

### Launch isolated, start driving only when the hook answers, and close what you opened
<!-- rule: launch-isolated-lifecycle -->

`launch_yaat` points `YAAT_APPDATA_DIR` at a scratch folder (default `.tmp/client-driver/appdata`) so the developer's preferences, favorites and log stay untouched; the client reads preferences only at startup, so anything the session needs from them (a default ARTCC, a scenario folder, the audio output device) is written into that folder's `preferences.json` before the launch. The app is ready when the pipe answers `list_windows` with a window, not when the process starts. Single-instance apps get one driving agent at a time (not yet enforced by the server); what an agent started it stops at the end, with any server it started, and an app the user already had running is left alone. The server's relative defaults resolve against its own checkout, so a worktree session passes absolute paths (screenshots had landed in the main checkout and the wrong build was launched).
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\ProcessTools.cs:41-68, 139-157, 396-411; D:\yaat\docs\client-driver-mcp.md:18, 22-25, 31, 71; D:\yaat\docs\plans\client-driver-mcp-friction.md:39-40. Seen: friction #19, #20 (open).

### Build before launch, and build before stopping on a restart
<!-- rule: build-before-launch -->

Every `launch_yaat` runs a prep first: it builds `src/Yaat.Client` when the client exe is missing or older than its sources (the newest tracked source time against the output, plus a stamp touched after each green build so a non-compile change does not rebuild every run), naming the build log in the result. A red build refuses the launch with the parsed compiler errors and the configuration built.

A restart builds first, then stops, then launches, so a red build leaves the old client running. A missing assembly first shows as misleading startup errors, so under a red build those symptom lines are dropped and the result reports the cause.

Building beside a running app is safe only when the app loaded its assemblies into memory. The desktop client does not: it holds its exe and DLLs, so a build into the same output folder fails with `MSB3027` as the server's own did, and the prep refuses naming the running client's pid rather than failing inside MSBuild. A project can route the build through its own wrapper (its gate script).
Source: godot-mcp's decision record and changelog fixes. Seen: 0 here (seeded from godot-mcp).

### Show windows with WS_EX_NOACTIVATE and at the bottom of the Z order
<!-- rule: quiet-by-default -->

`ShowActivated = false` shows a window with `SW_SHOWNOACTIVATE`, which leaves it activatable: when the window in front of it is minimized, Windows hands the foreground to the next top-level window in Z order, which can be the client. Only `WS_EX_NOACTIVATE` (added through Avalonia's Win32 window-styles callback) stops that. Separately, a client launched from a process that holds the foreground right (an agent's shell under the focused app) is created above every window and paints over the user's app while focus stays put, so each window is moved to the bottom of the Z order before its first show and an owned dialog placed just above its owner. Real input stays out because the window never takes it: a real click does not activate an automation-mode client, so a person cannot type into it. Out of sight is opt-in and set before the first show too: `launch_yaat cloaked: true` makes the client DWM-cloak every window before it shows, and a failed cloak ends the client so no uncloaked window reaches the desktop.
Source: D:\yaat\src\Yaat.Client.Core\Views\AutomationGate.cs:19-22, 35-44, 83-113; D:\yaat\docs\client-driver-mcp.md:87, 90; D:\yaat\tests\Yaat.ClientDriver.Mcp.Tests\AutomationClientZOrderTests.cs:33, 63; D:\yaat\tests\Yaat.Client.UI.Tests\Automation\AutomationNoActivateStyleTests.cs:30-39. Seen: documented; live-check asserts it.

### Say "hands off" before driving a window on the user's desktop
<!-- rule: say-hands-off-first -->

When a run drives a window on the user's own desktop with input their mouse and keyboard can disturb, tell the user "hands off" before driving it, and make that a step of every driving session: any input from them breaks a run. That is real mode (`set_input_mode real`: `SendInput` and SendKeys, which CRC always needs), where a route box got `E THE` in front of its value because the user was typing; posted virtual input is not fully out of reach either, since Avalonia reads Shift and Ctrl from the real keyboard. A client driven over its automation pipe, never activated, needs no warning.
Source: towercab-3d's project instructions and app-check procedure; D:\yaat\docs\plans\client-driver-mcp-friction.md:19-22; D:\yaat\docs\client-driver-mcp.md:56-57, 95. Seen: 0 here (seeded from towercab-3d).

### Default to posted background input, refuse what it cannot deliver, and post it like a real mouse
<!-- rule: posted-input-hygiene -->

The default input mode posts window messages to the app's window: the cursor and the foreground never change, a covered window still gets input, and it works while an elevated window holds the foreground. Avalonia reads Shift and Ctrl from the real keyboard state, not the message, so a modified click or `^`/`%`/`+` key is refused rather than sent unmodified; control characters (dropped below 32) and half surrogate pairs are refused too, and the whole string is checked before anything is posted, so a refusal types nothing. Each posted press follows a posted move of its own (Avalonia answers a move with `TrackMouseEvent`, which posts `WM_MOUSELEAVE` at once because the real cursor is elsewhere, and a press after that leave has no pointer over it); a double click is two posted clicks (no `CS_DBLCLKS`; Avalonia counts clicks itself); and two separate clicks near each other wait out the double-click time plus 50 ms, or a text box selects a word that the following keys replace.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\NativeInput.cs:36-38, 88-90, 310-311, 370-410, 423-424; Tools\InputTools.cs:30-36; D:\yaat\docs\client-driver-mcp.md:56. Seen: live once (set_text typing ahead of old text right after send_keys focused the same box).

### Verify the foreground is really held before real input, and say why when it is not
<!-- rule: verify-foreground-before-real -->

Real input (`SendInput`, SendKeys) goes to whatever holds the foreground, so the server asks for it, sleeps 100 ms and checks `GetForegroundWindow` (SetForegroundWindow's own result is not proof), with a popup's owner standing in for the popup. If the target did not take it, the click or keystrokes are refused naming the process that holds the foreground, rather than typed into another app; a `SendInput` the system blocks (an elevated target) fails with an explicit error instead of reporting a click that never happened. Real input still races the person at the desk: a route box got `E THE` in front of its value because the user was typing.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\NativeInput.cs:150-206, 751-752; Tools\InputTools.cs:468, 610, 661; D:\yaat\docs\plans\client-driver-mcp-friction.md:12, 17, 19-22. Seen: friction #10, #12.

### Set per-monitor DPI awareness first, and give every coordinate its unit
<!-- rule: one-coordinate-space -->

The server's very first statement sets per-monitor-v2 DPI awareness, warning on stderr if refused: without it Windows virtualises coordinates on scaled monitors and UI Automation rectangles stop matching screen pixels, so screen-coordinate tools document their unit as physical pixels. `click_point` with a pipe window id takes DIPs from that window's client area, and through UI Automation it takes screen pixels. A pipe screenshot is not in either unit: it renders at the window's render scale (its pixel size is the DIP size times `RenderScaling`), so a point read off it is divided by that scale before it is clicked; aim at an element where there is one.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Program.cs:8-16; Tools\InputTools.cs:104-114; D:\yaat\docs\client-driver-mcp.md:49; D:\yaat\src\Yaat.Client\Automation\Handlers\ScreenshotHandler.cs:17. Seen: documented.

### Resolve the target and check what the press will hit before pressing anything
<!-- rule: refuse-before-pressing -->

Check an element target before any event: a disabled or hidden control is refused (`ELEMENT_DISABLED`), and a selector several controls match is refused (`AMBIGUOUS_SELECTOR`) rather than taking the first, since tree order is not screen order. Where a click falls back to a synthetic pointer press, or `click_point` aims at a point, the control the client itself hit-tests at that point must be the target or its child, or the press is not sent and the error names what covers it with both rects (an overlay popup or a tooltip over the centre counts; a centre outside the window is refused as off-screen, not covered). A text target matches the exact shown text, trimmed and case-sensitive, with no substring fallback ("Play" would press "Play Again" on the wrong screen). Results say what was really hit (pressed on, released on), the first thing to read when a click seems to do nothing.
Source: godot-mcp's decision record and changelog fixes; D:\yaat\src\Yaat.Client\Automation\Handlers\ClickHandler.cs:99-109. Seen: 0 here (seeded from godot-mcp).

### Check what an action did: re-list windows and read values back
<!-- rule: check-action-effects -->

Avalonia menus support neither Invoke nor ExpandCollapse and their items do not exist until the menu is open, so the menu is clicked and its items found under the main window, where UI Automation files the popup; a UIA `list_windows` never listed it, nor owned windows such as Connect to Server, Load Recording, or CRC's STARS display and flight plan editor (found with `find_elements controlType=Window`). A modal an action opened went unreported: two Save Profile confirms stacked up unseen while the agent concluded "save did nothing". `set_text` reported success on a folder box that reverted the path and on a native dialog that stayed empty; only `get_value` showed it. After an input, list windows again and read the value back before judging the result; a target that vanished because the action closed it (a dialog's Open button) may be success.
Source: D:\yaat\docs\client-driver-mcp.md:65; D:\yaat\docs\plans\client-driver-mcp-friction.md:7, 12, 16, 26, 37, 41-42; D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\InspectTools.cs:181-184; D:\yaat\docs\research\2026-10-02-godot-mcp-ideas-for-client-driver.md:100. Seen: friction #1 recurred in three sessions; #6, #11, #21, #22.

### Wait on the app's own state and batch fixed sequences; never sleep
<!-- rule: wait-for-conditions -->

Element conditions use `wait_for` (100 ms poll inside the app); simulation conditions use `wait_until` on the app's own state (aircraft phase, on ground, landed, a log line, the scenario clock), also polled every 100 ms, which can take its screenshot and run its `then` actions (pause, set rate, stop the recording) at the very poll the condition held. Without it, hitting a given sim second depended on tool-call latency and timed checks needed background timers because the harness blocks foreground sleeps. A fixed sequence goes through `batch_drive` (1-100 steps, `assert wait` defaulting to 1000 ms, `assert no_errors`, first failure stops it, 300 s cap), which replaced a hand-written runner with hard-coded 500 ms, 3000 ms and 8000 ms sleeps and closes the round-trip gap in which ids went stale.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\PipeTools.cs:42-47, 70-82; Tools\BatchTools.cs:14-55; D:\yaat\docs\client-driver-mcp.md:38-40, 78; D:\yaat\docs\plans\client-driver-mcp-friction.md:32, 37; D:\yaat\docs\research\2026-10-02-godot-mcp-ideas-for-client-driver.md:24. Seen: friction #17, recurred.

### Wait out the navigation-data load, and every other load, on the signal that it finished
<!-- rule: wait-after-heavy-transition -->

After its window appears the client drops input for about 10 s while navigation data loads, in both input modes; reads in that span describe a half-loaded client and input is lost. Wait for the status bar's "Navigation data loaded" before driving; the 10 s describes the runs seen, not a settle time the client guarantees. Other loads carry their own signal: `load_recording` answers only once the room has loaded the recording, and an app tool that depends on a load reads unavailable until it ends (`set_video_map` until the maps load, `center_radar` until the radar has restored its saved settings), so wait on that availability, never a sleep.
Source: D:\yaat\docs\client-driver-mcp.md:42, 58. Seen: documented.

### Prefer the app's own named actions over UI paths for setup and framing
<!-- rule: hook-over-scraping -->

Setup that is a long or fragile UI path (connect, create a room, load a recording, seek, centre the radar, set video maps, place range lines) is an app tool the agent calls by name with `call_app_tool`, each listed with whether it can run now and why not. Framing by posting mouse-wheel notches had no pan and drifted between takes, and a dropdown that opened off the window edge could not be clicked. State is read the same way: `get_framing` and `get_sim_time` answer with values, never text scraped off the window.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\AppTools.cs:26-58; D:\yaat\docs\client-driver-mcp.md:41-42, 71, 77, 85; D:\yaat\docs\research\2026-10-02-godot-mcp-ideas-for-client-driver.md:46-47; D:\yaat\docs\plans\client-driver-mcp-friction.md:45. Seen: friction #25.

### Force rare conditions through an app tool or a dev switch, and know what it replaces
<!-- rule: force-conditions-dev-panel -->

Do not wait for a rare real condition (a moment in a scenario, a recorded situation, a solo session's behaviour) to test what shows it: an app tool forces it from the agent (`seek`, `load_recording`, `set_solo`, `set_sim_rate`), and a `YAAT_DEV_*` environment switch passed through `launch_yaat`'s `env` turns on a dev-only behaviour (`YAAT_DEV_SOLO_SPEECH_BUBBLES=1` shows speech bubbles in a solo session). Know what each replaces: `clear_rbls` clears the primary radar's lines only, unlike `.norbl`; `prepare_take` replaces the primary radar's RBLs and writes no saved radar settings beyond the map toggles' own; `center_radar` is not saved to the scenario's settings. In another project a weather panel's Apply replaced all weather, clouds included, so a run that applied rain over an existing overcast lost the clouds.
Source: D:\yaat\docs\client-driver-mcp.md:31, 42; D:\yaat\src\Yaat.Client\ViewModels\MainViewModel.Aircraft.cs:175; towercab-3d's project instructions and a rendering design. Seen: 0 here (seeded from towercab-3d).

### Capture from inside the app; a screen copy shows whatever is on top
<!-- rule: what-capture-misses -->

The pipe's screenshot is rendered by the client itself (`RenderTargetBitmap` at the window's render scale) whatever covers it, and with overlay popups on it includes open menus and popups. The outside routes each missed something: a real-mode screen copy of one window showed another window stacked on it with nothing in the result saying so; a virtual capture of the main window left out its open dialogs; and a real-mode capture brings the window forward, which closes an open combo popup.
Source: D:\yaat\docs\client-driver-mcp.md:37; D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\InspectTools.cs:150-157, 384; D:\yaat\docs\plans\client-driver-mcp-friction.md:11, 28, 33. Seen: friction #5, #13, #18.

### Record input on the client's clock, and keep every drive replayable
<!-- rule: record-and-replay -->

A clip shows what happened, not how to make it happen again. Record both a person's input events and injected ones on the client's own clock (merging server-logged gestures with client-logged events put two clocks a round trip apart, in another project) and stream them in batches (every 250 ms or 100 events), so a capture survives a crash and replays through the pipe's input methods; marks taken on the scenario clock (`record_mark`) line a replay up with its clip. A random stress run draws with SplitMix64 rather than `System.Random(seed)`, which is not stable across .NET versions, so a failing seed replays exactly.
Source: godot-mcp's architecture doc and changelog fixes; .NET docs on System.Random seeding. Seen: 0 here (seeded from godot-mcp).

### Record by window handle on a steady clock, never minimize the window, and mark with the app's clock
<!-- rule: record-steady-never-minimize -->

Windows Graphics Capture delivers a frame only when the window redraws (one in 20 s on a static tab), so the recorder's timer writes a steady rate and ffmpeg stamps each frame with its arrival time (`-fps_mode vfr`), and a clip whose recorder fell behind still plays in real time; the stop reports frames dropped. A minimized Avalonia window stops rendering and restoring it calls `SetForegroundWindow`, so a minimized or hidden window freezes the clip on its last frame; hide it by DWM-cloaking instead (WGC still captures a cloaked window at 30 fps). Audio is the process's own loopback, and routing it to a virtual cable keeps the speakers silent while the recording still hears it. Each `record_mark` stores wall time, clip seconds (null before the first frame) and the app's scenario clock over the pipe (null after 2 s without an answer), because live playback shifted pilot lines 1-2 s against the wall clock.
Source: D:\yaat\docs\client-driver-mcp.md:43-44, 75, 78, 90; D:\yaat\docs\plans\client-driver-background.md:65, 77-78, 103; D:\yaat\tools\Yaat.ClientDriver.Mcp\Recording\FfmpegPipeline.cs:69-73; Tools\RecordTools.cs:88-93; D:\yaat\docs\research\2026-10-02-godot-mcp-ideas-for-client-driver.md:16-17. Seen: measured (WGC spike; 969 samples over 15 s with no visible cloaked window).

### Answer file dialogs before the action that opens them
<!-- rule: queue-file-answers -->

The native picker runs in `PickerHost.exe`, outside the app's windows: posted typing left it empty and real-mode keys were refused because the foreground belonged to PickerHost. In automation mode the app opens no dialog; the agent queues one answer (a path or a cancel) per dialog with `queue_file_pick` before the action, each dialog takes the oldest, and one opened on an empty queue fails at once.
Source: D:\yaat\docs\client-driver-mcp.md:46, 66; D:\yaat\docs\plans\client-driver-mcp-friction.md:41; D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\PipeTools.cs:198-203. Seen: friction #21.

### Name what the driver cannot reach, and the route for each
<!-- rule: name-out-of-reach-ui -->

The pipe reaches only what the client draws and handles. Each surface out of its reach is named, and refused as such rather than failing obscurely: push-to-talk through the SharpHook key hook (a `send_keys` holding the push-to-talk binding is refused with `UNSUPPORTED_OPERATION`, "Push-to-talk is out of reach in automation mode", and no key is sent), and the native file picker (the client opens none in automation mode; `queue_file_pick` answers it). CRC's scopes are one OpenGL surface UI Automation cannot see into: read them with `screenshot` and act on them with `click_point`. An agent that does not know this burns turns trying to click a native dialog.
Source: D:\yaat\src\Yaat.Client\Automation\Handlers\SendKeysHandler.cs:166-188; D:\yaat\docs\client-driver-mcp.md:66, 89, 95; towercab-3d's project instructions. Seen: 0 here (seeded from towercab-3d).

### Send the client's logs to one file and read its tail
<!-- rule: unified-log-tail -->

Launched by `launch_yaat`, the client writes its log to `yaat-client.log` in the scratch app-data folder, whose path the launch returns, and agents read it with `tail_yaat_log` rather than pulling it all into context. Log a frame-timing line there only when the frame rate drops below a floor (50 fps in another project), so a slowdown is diagnosable from the file without noise. The client's file logger flushes every line (`AutoFlush`), so a line missing from the tail was not logged; a logger that buffers needs its flush interval allowed for before a line is called absent.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\ProcessTools.cs:112, 264-296; D:\yaat\src\Yaat.Client.Core\Logging\FileLoggerProvider.cs:29-30; towercab-3d's dev wrapper and logger code. Seen: 0 here (seeded from towercab-3d).

### Log driving friction after every session and tick it off when fixed
<!-- rule: friction-log-per-session -->

Every session that uses the driver appends to a friction log: the tool and arguments, what happened against what was needed, and the workaround, with recurrences noted against earlier entry numbers; a fix ticks the entry off. The log drove the design: the in-app pipe, `wait_until`, `batch_drive`, `queue_file_pick` and the app tools each answer numbered entries.
Source: D:\yaat\docs\plans\client-driver-mcp-friction.md:1-3, 26, 37; D:\yaat\docs\research\2026-10-02-godot-mcp-ideas-for-client-driver.md:24, 32, 100. Seen: 26 entries over three sessions.

## App-side hook

### Turn the hook on with one environment switch read once at startup
<!-- rule: one-env-switch -->

`YAAT_AUTOMATION=1` (exactly "1") turns on both the pipe endpoint and never-activated windows, and turns off the process-wide key hook and Discord presence; the launch tool always sets it. The flag writes through to the shared activation gate, so window code and the flag can never disagree. A dependent switch (`YAAT_CLOAK=1`) without it makes the client log the error and exit 2 before any native start-up, and a failed cloak exits 3 so no uncloaked window reaches the desktop. The code ships in release builds and is gated only at run time; the pipe is Windows-only and restricted to the current user.
Source: D:\yaat\src\Yaat.Client\Automation\AutomationMode.cs:7-54; D:\yaat\src\Yaat.Client\Program.cs:45-47, 124-127, 155-177; D:\yaat\src\Yaat.Client\App.axaml.cs:92-107; D:\yaat\docs\client-driver-mcp.md:90; D:\yaat\docs\plans\client-driver-background.md:105. Seen: documented.

### Put every activating or out-of-process UI path behind one seam, pinned by source tests
<!-- rule: one-gate-source-pinned -->

The app activated itself from many places (geometry apply, profile restore, pop-out reopen, command-input focus, a Topmost pulse, centre-on-owner dialogs); one gate suppresses them all in automation mode, and a source-scanning test fails on any raw `.Activate()` outside it. Avalonia itself calls `owner.Activate()` after a modal closes, and any visible Win32 `WindowState` change calls `SetFocus` and `SetForegroundWindow` (found by decompiling 12.1), so dialogs show non-modal through one presenter that disables the owner by hand, windows stay Normal, and `OverlayPopups = true` draws popups inside their window so they never take activation and appear in captures. Native file pickers go through one factory that returns a queue-answered picker in automation mode, with source tests that no picker is built and no `StorageProvider` touched elsewhere.
Source: D:\yaat\docs\plans\client-driver-background.md:21-27, 86-89, 114-118, 125, 134-135, 146; D:\yaat\src\Yaat.Client\Program.cs:218-224; D:\yaat\tests\Yaat.Client.UI.Tests\AutomationModeSourceTests.cs:113, 488-494. Seen: the WindowState path surfaced in a review fix round.

### Start the endpoint from the code that owns what it exposes, and remove only your own file
<!-- rule: install-owner-cleanup-own -->

The automation host is started by the app's own start-up, which owns what it hands over: it reads the windows, the simulation state and the app tools through providers over the live desktop and main view model, so a replaced view model is picked up without restarting the host. Its discovery file is keyed on the client's own pid, so its cleanup deletes only that file; the start-up sweep skips its own pid and removes another client's file only when that client is gone (a dead pid, or a pid now held by another program), so a second client never deletes a live one's endpoint.
Source: D:\yaat\src\Yaat.Client\App.axaml.cs:94-103; D:\yaat\src\Yaat.Client\Automation\AutomationHostFactory.cs:20-46; Transport\DiscoveryFile.cs:26, 64-76, 160-176; towercab-3d's hook install code. Seen: 0 here (seeded from towercab-3d).

### Advertise the endpoint with an atomic per-pid discovery file in a fixed place
<!-- rule: discovery-file-per-pid -->

The host listens on `yaat-automation-<pid>` (`PipeOptions.CurrentUserOnly`) and writes `%TEMP%/yaat-automation/<pid>.json` with the pipe name, process name, start time and protocol version, through a temp file and a move. It is deliberately not under the app-data folder, so the driver finds a client whatever `YAAT_APPDATA_DIR` either process runs with. On start it sweeps stale files (dead pid, a pid now held by another program, unparsable, orphaned temp), and the driver re-checks the live process name before trusting a file and pings before caching a connection.
Source: D:\yaat\src\Yaat.Client\Automation\AutomationHostFactory.cs:9-14; Transport\DiscoveryFile.cs:13-125; Transport\NamedPipeTransport.cs:58-66; D:\yaat\tools\Yaat.ClientDriver.Mcp\Pipe\PipeDirectory.cs:57-145. Seen: documented.

### Link the wire types into the driver as source, and version them
<!-- rule: shared-wire-types -->

The protocol folder has no Avalonia dependency, so the MCP compiles the same files (`<Compile Include="..\..\src\Yaat.Client\Automation\Protocol\*.cs" />`) rather than referencing the client or copying the types, and the two sides cannot drift in shape. `ping` and the discovery file report the protocol version (1.4.0), bumped as methods and fields are added. The driver does not yet compare versions; a skewed client shows as a deserialize failure, which still carries the client's errors.
Source: D:\yaat\tools\Yaat.ClientDriver.Mcp\Yaat.ClientDriver.Mcp.csproj:77; D:\yaat\src\Yaat.Client\Automation\Protocol\ProtocolVersion.cs:3-7; D:\yaat\docs\architecture.md:350; D:\yaat\docs\client-driver-mcp.md:86. Seen: documented.

### Expose the pipe as one typed protocol that hands over live state
<!-- rule: one-typed-window-global -->

Keep the pipe's whole surface one typed protocol: its methods (`ProtocolMethods`) and their request and result types live in one folder, checked by the compiler on both sides and documented in one place. Through it, hand over the client's live state itself, a get and a set of named view-model values, plus a few task helpers, rather than wrapping each value in a method or an app tool of its own, so a value no tool reads yet is one call away instead of a missing feature.
Source: towercab-3d's hook type file. Seen: 0 here (seeded from towercab-3d).

### Name each feature's app tools in its design
<!-- rule: design-names-hook-surface -->

A new feature's design (an issue plan under `docs/plans/`) names the app tools and pipe reads that will let an agent verify it (settings list, get, set and reset validated against the control's range; a mode's start, stop and state), marked `[AutomationTool]` beside the rest, so the pipe grows with the features instead of after them.
Source: four feature designs in towercab-3d. Seen: 0 here (seeded from towercab-3d).

### Declare app tools with an attribute that names their availability check
<!-- rule: opt-in-by-name -->

An app tool is a public method marked `[AutomationTool(name, description, availability)]`; the availability method returns why the tool cannot run now, or null, and the pipe asks it before every call and shows it in the list. Signatures are validated at discovery (returns `Task<AppToolOutcome>`, every parameter required, `string`/`int`/`double`/`bool`, described), and arguments are checked before availability. Availability guards races as well as states: radar framing is unavailable until the radar has restored the scenario's saved settings, which would otherwise overwrite a centre set now. The list is read from the running client, so it never offers a tool the running build lacks.
Source: D:\yaat\src\Yaat.Client\Automation\AutomationToolAttribute.cs:3-16; D:\yaat\src\Yaat.Client\Automation\Tools\AutomationTools.cs:8-13, 41-73, 96-110; D:\yaat\docs\architecture.md:347; D:\yaat\tools\Yaat.ClientDriver.Mcp\Tools\AppTools.cs:33-43. Seen: documented.

### Answer every request with a coded error and a hint, cancel it when its connection goes
<!-- rule: coded-errors-never-throw -->

The dispatcher turns every failure (malformed JSON, missing id or method, unknown method, handler error or exception) into a response with a stable code (`NO_MATCH`, `AMBIGUOUS_SELECTOR`, `STALE_NODE`, `ELEMENT_DISABLED`, `TIMEOUT`, `INTERNAL`, …), a message, a suggested fix and typed details; an unknown method's hint lists the registered methods, and codes may be added but never respelled. Handlers take a cancellation token: a disconnect or host stop cancels the running request (the one case left unanswered, since nobody is left), and a request sent before the previous one answers is held and answered in order.
Source: D:\yaat\src\Yaat.Client\Automation\AutomationDispatcher.cs:15-19, 57-101; Protocol\AutomationErrorCodes.cs:6-24; Protocol\ErrorDetails.cs:1-40; D:\yaat\docs\client-driver-mcp.md:85. Seen: documented.

### Keep a sequenced ring of the client's errors, and append each call's share to every tool result
<!-- rule: error-feed-on-results -->

The app keeps its last 200 Error and Critical log entries with sequence numbers; each response carries the entries logged from the call's start until its answer was ready, oldest first, at most 20, the handler's own failure included, with a count of those left out by the cap or dropped by the ring. Capture is by time window, so an error another thread logs during the call comes back too; warnings are not attached.

A call-tool filter in the server collects, per tool call (an `AsyncLocal` collector), the client errors every pipe answer carried and appends one text block, `Client logged N error(s) during this call:` with ` (M not shown)` when entries were capped or dropped, then one `- [Level] Category: message (exception)` line each, on a success and an error result alike; an `McpException` is rethrown with the block appended, and any other exception after pipe calls is logged and answered as the SDK's error text plus the block, so a protocol-skew failure still shows the client's errors. The lesson: a button that "does nothing" had usually logged `Unhandled UI-thread exception (recovered)`, and File > Connect looked like a no-op until the log was read.
Source: D:\yaat\src\Yaat.Client\Automation\AutomationDispatcher.cs:103-118, 152-161; D:\yaat\src\Yaat.Client.Core\Logging\RecentErrorLog.cs:13; D:\yaat\src\Yaat.Client\Automation\Protocol\AutomationResponse.cs:19, 32-40; D:\yaat\tools\Yaat.ClientDriver.Mcp\Pipe\PipeCallErrors.cs:11-16, 103-157; D:\yaat\docs\client-driver-mcp.md:67, 86; tests ClientErrorsTests.cs:48-97. Seen: recurring (ERAM session 2026-09-27).

### Fail loudly when what the pipe reads is not ready
<!-- rule: hook-throws-when-unready -->

A pipe read whose subject is absent answers a coded error naming why, never empty data: `get_sim_time` is `UNSUPPORTED_OPERATION` ("The main window is not up yet, so there is no simulation state to read.") until the main window is up, and every app tool reads unavailable with its reason until then, so an agent reading too early gets a cause, not a zero clock.
Source: D:\yaat\src\Yaat.Client\Automation\Handlers\GetSimTimeHandler.cs:8, 18-32; D:\yaat\docs\client-driver-mcp.md:41, 85; towercab-3d's hook code. Seen: 0 here (seeded from towercab-3d).

### Convert every write by the declared type and read it back
<!-- rule: verify-writes-read-back -->

A tool that writes a value reads it back and fails when the client kept something else. `set_text` reported success on a folder box that reverted the path, and only `get_value` showed it; a read-back after every write catches a race (the `E THE` route box) instead of submitting it. So a write converts its JSON by the declared type of the property or parameter (app-tool arguments already bind as `string`, `int`, `double` or `bool`), sets it, reads it back, and when the read-back differs (a coercing binding, a control that reverts) puts the old value back and fails with the before and after values. In another project an engine's setters failed silently on a wrong type, and C# turned a Dictionary into a zero vector.
Source: godot-mcp's decision record; D:\yaat\docs\plans\client-driver-mcp-friction.md:19-22. Seen: 0 here (seeded from godot-mcp).

### Never block the UI thread, and bound what runs there
<!-- rule: never-block-main-thread -->

Pipe handlers do their UI work on the UI thread, so a handler that blocked there on work queued for later would deadlock the client. Each wait poll is bounded by the time left, so a busy UI thread cannot make an answer late, and heavy work (PNG encode, base64, `wait_until`'s then-actions) runs off it.
Source: D:\yaat\src\Yaat.Client\Automation\AutomationDispatcher.cs:142-149; D:\yaat\docs\plans\client-driver-background.md:145; D:\yaat\docs\client-driver-mcp.md:85. Seen: documented.

### Click by running the control's own action first, the synthetic pointer only when there is none
<!-- rule: semantic-click-first -->

A click runs Avalonia's own path through the public automation peers (button invoke → `PerformClick`, toggle) and sends a synthetic press and release only when no meaningful action exists; doing both fired a button wired with a `Click` handler and a `Command` twice. The result names which ran. Typing is raised as text-input events at the caret so per-keystroke autocomplete runs, and shortcuts as routed `KeyDown`/`KeyUp` so the app's own handlers see them; Avalonia's private input API is avoided (it raises `AVA3001` under warnings-as-errors and breaks on upgrades).
Source: D:\yaat\docs\plans\client-driver-background.md:110-111, 138-144; D:\yaat\docs\research\2026-10-01-in-app-automation-pipe-protocols.md:105-112. Seen: documented.

### Size captures by the window's own render scale, not a global DPI
<!-- rule: backing-store-ratio -->

The pipe's screenshot renders at the window's own `TopLevel.RenderScaling` (pixel size = DIP size × scale, at 96 × scale DPI), not a machine-wide DPI figure, because each window's scale follows its monitor. In another project a canvas at a device pixel ratio of 2 measured 1596x800 against a GUI texture of 3192x1600, which drew overlay labels at half size and position.
Source: D:\yaat\src\Yaat.Client\Automation\Handlers\ScreenshotHandler.cs:17, 114-120; towercab-3d's read-back code and a measured high-DPI bug. Seen: 0 here (seeded from towercab-3d).
