# godot-mcp ideas for the YAAT/CRC client driver

Date: 2026-10-02.

Sources: godot-mcp at `D:/godot-mcp` (`README.md`, `docs/TOOLS.md`, `docs/DECISIONS.md`, `docs/ARCHITECTURE.md`, `docs/csharp-runtime-tools.md`, `docs/plans/*.md`, `docs/research/2026-09-29-godot-mcp-survey.md`); yaat `docs/client-driver-mcp.md`, `docs/plans/client-driver-mcp-friction.md` (cited as friction #n), `docs/plans/client-driver-background.md`, `docs/plans/follow-video-montage.md`, `docs/crc-first-session.md`, `tools/montage/follow/README.md`, `docs/plans/HANDOFF.md`; Linear YAAT-114, 172, 173, 174, 220, 230, 237, 242, 245, 246; and the FOLLOW sampler's scratch scripts in `X:/dev/yaat.wt/montage-sampler/yaat/.tmp/sampler/` (`pipe.ps1`, `take.ps1`, `win.ps1`, `start-client.ps1`).

## Summary

The strongest evidence comes from the FOLLOW montage sampler (YAAT-239). Its agent did not use the MCP for the capture. It wrote four scripts around the automation pipe instead. `pipe.ps1` is a batch runner with hard-coded `sleep` steps. `take.ps1` records for a guessed `-Seconds` and starts WGC video and process audio by hand. `win.ps1` moves the window, reads its frame and client rectangles, and posts wheel notches.

`start-client.ps1` launches with environment variables that `launch_yaat` cannot pass, then sleeps 14 s. godot-mcp already has a tool for each of these jobs: `batch_drive`, `record_mark` with a real-time recording design, `wait_for`, launch profiles, and game tools.

The ranking puts the montage capture loop first, then UI-path bug repro (errors on every result, a state digest, window and modal reporting). CRC's OpenGL scopes can take only the pixel-level ideas: recording, baselines and change waits. Its WPF chrome takes the UIA ones.

Sizes are rough: **S** is MCP-only or one small pipe method (about 200 lines or fewer). **M** is a pipe method plus an MCP tool and tests (about 200–600 lines). **L** is a new helper process or subsystem.

Not borrowed: frame stepping at render-frame granularity (sim ticks run on the server, and client frames carry no meaning); gamepads; the headless scene tools; the `cs_*` reflection tools and `run_csharp`. App-defined tools (idea 4) give the same reach without arbitrary code in the client.

Quiet mode and the hidden desktop are also left out: the never-activated window already does that job for YAAT, and CRC cannot be launched that way. Random `stress_input` and `capture_input` replay are left out too, because the sim's own recordings already replay an action log.

## 1. Real-time recording owned by the driver (`record_start` / `record_mark` / `record_stop`)

- **godot-mcp:** `docs/plans/realtime-recording.md` (GMCP-6, every question ruled). `record_mark start`/`stop` works on any session. A helper exe (`godot-mcp-capture.exe`, a port of the user's `WgcCapture`) captures the window by the HWND that the bridge's hello reports, so no title search is needed.

  It crops to the client area from `DWMWA_EXTENDED_FRAME_BOUNDS` against `ClientToScreen`, records the game process's audio through WASAPI process loopback, and pipes into ffmpeg (NVENC, then libx264, then h264_mf).

  It writes Matroska while recording and remuxes to MP4 at stop. It refuses up front, with the fix, for a minimised window, a missing ffmpeg, or a recording already running. It warns when the measured fps falls below 90% of the target. The shipped Movie Maker route (`TOOLS.md` "Recording", `record_mark`) adds marks cut into clips and `dropIdle`.
- **YAAT problem:** the montage capture is step 4 of `follow-video-montage.md`, and the sampler's `take.ps1` does it by hand. It finds the window by `-Title "Radar View"`, runs two captures started with `Start-Process`, and stamps wall-clock marks into `<clip>-marks.json`. It uses a fixed `-Seconds` and a `Start-Sleep 60` for a mid-take screenshot.

  Its crop offset comes from `win.ps1 info` (`crop=1200:700:1:31` in `client-driver-mcp.md` "Recording a demo"). Captions must be aligned to the audio because live playback shifts pilot lines by 1–2 s (montage plan step 4, YAAT-83). Marks stamped with the sim time as well as the wall time would make that alignment mechanical.
- **Mapping:** feasible for both apps. WGC captures a GPU-drawn window that is covered or never activated, which the WGC spike measured for YAAT, and it should work for CRC's OpenGL windows too (unmeasured for CRC). The pipe's `list_windows` can return each window's HWND; for CRC, UIA's `NativeWindowHandle` gives it. The crop is the `win.ps1 info` arithmetic moved into the server.

  Audio comes from `--audio-pid`, the client pid (pilot TTS). Each mark records `{wallUtc, simSeconds}`, with `simSeconds` read from the view model over the pipe. `record_stop` returns the clip path, its duration and a contact sheet (the skill's `-Sheet`). Together with idea 3, "stop the take when the follower lands" becomes one call.
- **Size:** L if `WgcCapture` (1,355 lines) is ported into the repo. M if the MCP shells out to the skill's `WgcCapture.exe` when it is present. godot-mcp rejected that shortcut because its server is public. `leftos/yaat` is public too (`gh repo view`), so the shortcut would be a stopgap only.
- **Linear:** not covered. YAAT-246 covers only the stop condition.

## 2. `batch_drive`: steps, waits and assertions in one call

- **godot-mcp:** `batch_drive` (`TOOLS.md` "Replay with checks", decision 11). It runs 1–100 steps of `{tool, args}` or `{assert: property | expression | wait | no_errors | screenshot}`, server-side and in order. It stops at the first failure with `failedAt`, returns screenshots as paths only, and has a 300 s ceiling. `multi-process-drives.md` part D adds parallel groups across sessions, for example arming a wait in one app before acting in the other.
- **YAAT problem:** the sampler's `pipe.ps1` is a hand-written batch runner (`{"m":…}` steps plus `{"sleep":ms}`) that bypasses the MCP. `start-client.ps1` chains create-room and load-recording with sleeps of 500 ms, 3000 ms and 8000 ms. Friction #23 is a menu item that went stale between `find_elements` and `click`, a round-trip gap a batch closes. Every UI-path repro is a fixed sequence that today costs one tool call per step.
- **Mapping:** feasible as MCP-only work. The server calls its own tools in order, with pipe ids for YAAT and UIA ids for CRC. Assertions reuse the pipe's `wait_for` (selector conditions) and idea 3's sim-state conditions. A `screenshot` assertion depends on idea 9. A step may carry `pid`, so one batch can drive YAAT and CRC, which suits the "compare CRC with YAAT" use.
- **Size:** M.
- **Linear:** not covered.

## 3. Sim-state waits with godot's extras: `then`, a screenshot at the met moment, sim-time waits

- **godot-mcp:** `wait_for` (`TOOLS.md` "wait_for", decisions 11 and 22). A timeout is a result (`{met: false, last}`), not an error. `options.screenshot` captures the frame the condition held on. `{gameMs}`/`{frames}` wait on the game's own clock, so a pause is never a `timeoutMs`.

  `options.call` runs a method where the count starts. `step-until-render.md` §3.1 adds `options.then {call, timeScale}`, which acts in the frame the condition is met, with no round trip. `{uiChanged: true}` waits for an unnamed UI change and reports what appeared and disappeared.
- **YAAT problem:** YAAT-246 asks for a sim-state `wait_until` so that a take stops when the follower lands. Friction #17 (recurring in the A1 session) is that hitting a given sim second depends on tool-call latency. The sampler's `Start-Sleep 60` before its `A1-t60` screenshot is the same gap.
- **Mapping:** needs a pipe method with view-model access (`MainViewModel.Aircraft`, `ScenarioElapsedSeconds`, `IsPaused`, `TerminalEntries`), which is what YAAT-246 already specifies. The godot extras to fold in:
  - `{simSeconds: n}` as a condition, measured on the sim clock and not the wall clock.
  - `options.screenshot` taken on the UI thread in the poll that met the condition.
  - `then: {stopRecording | pause | simRate}`, run in that same poll.
  - `metAt: {simSeconds, wallUtc}` in the result.
  - `last` on a timeout, as a result rather than an error.

  For CRC, a UIA-polling `wait_for` (window exists, text equals) is S. A pixel-change wait on a scope region, "until the region changes" or "until it stops changing", answers friction #17 for the OpenGL scopes (see idea 9).
- **Size:** S on top of YAAT-246.
- **Linear:** core covered by YAAT-246 (and YAAT-114 item i). The extras above are not.

## 4. App-defined tools: the client marks its own automation commands

- **godot-mcp:** `list_game_tools` / `call_game_tool` (`TOOLS.md`, decision 26). The game marks methods with its own `[GodotMcpTool("what it does")]` attribute, matched by name in any namespace, with parameter descriptions from `[Description]`.

  The server exposes two fixed tools, never one dynamic MCP tool per game tool, because an agent's `tools:` line never sees a tool added later. Arguments are an object keyed by parameter name and checked against a JSON Schema before the call. Each listed tool carries `available: false` with its reason when it cannot run. A `Task` result is awaited.
- **YAAT problem:** framing a take needs view-model actions that UIA and pixels reach badly. The sampler zooms by posting wheel notches (`win.ps1 wheel`), there is no pan tool (friction #25), and the radar view is set by hand. The montage Framing decisions also need these settings, and each is a menu path or a typed command today:
  - video maps 590/594/1;
  - 1-minute RBL lines;
  - speech bubbles (an env var);
  - the sim rate, which resets to 1× on every reload;
  - data block positions;
  - `.rbl A B`;
  - solo mode (YAAT-247: a loaded solo recording comes up with solo off).
- **Mapping:** needs a pipe method pair (`list_app_tools`, `call_app_tool`) and an `[AutomationTool]` attribute in `src/Yaat.Client/Automation/`. Reflection runs over the marked methods on `MainViewModel`, `RadarViewModel` and the ground view model, on the UI thread. First tools:
  - `SetRadarView(lat, lon, rangeNm)` and `CenterOnAircraft(callsign)`;
  - `SetSimRate(n)`, `Pause()` and `Play()`;
  - `LoadRecording(path)`, the picker path without the menu;
  - `ShowVideoMaps(ids)`, `SetRbl(a, b)`, `SetDataBlockOffset(callsign, dir)` and `SetSoloMode(bool)`.

  Not applicable to CRC, which we do not own.
- **Size:** M for the registry and the first handful of tools. Each further tool is S.
- **Linear:** YAAT-114 item h ("direct view tools … needs a way into the client's view models … through the automation pipe") covers the radar and ground pan/zoom slice. The general registry is not covered.

## 5. Errors ride along on every result

- **godot-mcp:** decision 10's error feed and `TOOLS.md` "Rules every tool shares". Every runtime result carries `errors` (file, line, stack) raised while the call ran. The key is absent when there were none. `get_errors(since)` reads warnings too, by sequence cursor. `multi-process-drives.md` part E adds `errorsElsewhere` for errors that the other sessions of a batch raised.
- **YAAT problem:** `client-driver-mcp.md` says that a button which "does nothing" usually logged `Unhandled UI-thread exception (recovered)`, and the advice is to run `tail_yaat_log` first. In the ERAM session, File > Connect looked like a no-op until `tail_yaat_log` showed the window (friction, 2026-09-27 recurrences). A UI-path repro that misses a swallowed exception reaches the wrong conclusion.
- **Mapping:** needs a pipe change. The host keeps a ring of `AppLog` warnings, errors and the unhandled-exception handler's entries, each with a sequence number. Every pipe response carries the entries logged since the request started, and a `get_errors(since)` method returns the rest.

  A no-client-change fallback is for the MCP to read `yaat-client.log` from a remembered byte offset after each call. yaat-server's log (`server.log` in the sampler) could ride the same way once the server is a process session (idea 7). Not applicable to CRC, whose log is not ours. CRC's `%LOCALAPPDATA%\CRC` logs could still be tailed if useful (unmeasured).
- **Size:** S to M.
- **Linear:** not covered.

## 6. A state digest and its diff (`get_sim_state`, `keep`, `diff_snapshots`)

- **godot-mcp:** `get_game_state` and `diff_snapshots` (`TOOLS.md`, decision 27, `plans/state-digest.md`). Opted-in nodes return what a player reads off the screen, in one frame, and `keys` filters dotted paths. With `keep`, the read is held flattened per leaf, so `diff_snapshots {beforeId}` after an action names each changed value. `snapshot_subtree` does the same for a UI subtree.
- **YAAT problem:** UI-path repro and montage checks read state off screenshots. The sampler had to `get_tree` the Solo Training Mode checkbox to learn whether solo was on (`start-client.ps1`), which is YAAT-247's fault. The montage plan's client replay check compared the radar with `bug_bundle.py track --pair` at six sample times by eye.
- **Mapping:** needs view-model access through a pipe method. It returns `{simSeconds, paused, simRate, playback {mode, position, end}, solo, connected, room, aircraft: [{callsign, type, phase, altitude, groundSpeed, onGround, queue}], rblLines, openWindows}`, with `keys` to trim it.

  `keep` and `diff` live in the MCP. YAAT-246's conditions read the same digest, so build them once. For CRC, a UIA subtree snapshot and diff (names, values, enabled flags) is feasible for its WPF windows and blind to the scopes.
- **Size:** M (S once YAAT-246's view-model access exists).
- **Linear:** not covered as a tool. YAAT-246 needs the same access.

## 7. Launch profiles and process sessions (yaat-server, CRC, the client's env)

- **godot-mcp:** project profiles (`godot-mcp.json`, `TOOLS.md` "Project profiles") with strict parsing and named presets (`scene`, `userArgs`, `engineArgs`, `resolution`, `session`, `description`). `multi-process-drives.md` part B adds `run_process`: a non-game process declared in the profile (`command`, `cwd`, `env`, `ready: {output: regex, timeoutMs}`).

  It starts in a job with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so `dotnet run`'s child dies with it. It lists in `list_sessions` and stops by name. Part C adds `wait_for {output: regex}` on a session's captured lines.
- **YAAT problem:** `launch_yaat` takes only `appDataDir`, `exePath` and `waitSeconds`. The sampler therefore launched by hand to pass `YAAT_DEV_SOLO_SPEECH_BUBBLES=1` and `--autoconnect`, then slept 14 s (`start-client.ps1`). The voice pack (93 MB) has to be copied into the scratch app-data folder (montage step 4). Seeded `preferences.json` frames a take (montage plan "Framing"). Other gaps:
  - The server on `:5130` is started and tracked by hand (`server.pid`).
  - CRC has no launcher (friction #7, recurring).
  - Worktree sessions must pass absolute paths (friction #20).
- **Mapping:** feasible, MCP-only. A tracked `tools/Yaat.ClientDriver.Mcp/profiles.json` (or a section of `.mcp.json`-adjacent config) would hold presets:
  - `montage`: env, args, a seed folder copied into app data, voices linked or copied, window size;
  - `crc-oak-gnd`: the CRC exe and a profile name;
  - `server`: `dotnet run --project ../yaat-server/src/Yaat.Server`, ready on `Now listening on`.

  `launch_yaat {preset}`, `launch_crc {preset}` and `run_process {name}` each record what they started, which YAAT-172 needs anyway for cleanup at session end. Paths resolve against the caller's worktree.
- **Size:** M.
- **Linear:** partly covered. YAAT-172 has `launch_crc`/`launch_yaat` recording what they started, and YAAT-173 has friction #7/#20. Presets, the env pass-through, the ready-line wait and the yaat-server process session are not covered.

## 8. Input results report what the input opened and closed

- **godot-mcp:** `wait_for {uiChanged: true}` (`TOOLS.md`) compares the visible Controls, the focus owner and the top popup with a baseline that the first gesture takes. It returns `{appeared, disappeared, focus, popup}`.

  `plans/click-signals.md` goes further: each input result lists the signals the press fired (`fired`, `listenedOn`, `leftTree`) and warns when a press reached a disabled or paused control. Input results already name the control the press landed on (`pressedOn`, `releasedOn`) and the exact point aimed at (`aimedAt`).
- **YAAT/CRC problem:** a modal that an action opens goes unreported. Two Save Profile confirms stacked up unseen (friction #11, #12). `list_windows` misses owned windows: CRC's STARS window and FPE (#1), and YAAT's Connect to Server, Load Recording and Save Recording windows (#1 recurrences). A screenshot leaves out open dialogs (#13). `click` echoes the post-click rectangle, which reads like a failure (#15).
- **Mapping:** feasible for both. Over the pipe, the client diffs its `list_windows` (owned windows and overlay popups included) and the focused element across the click, inside one request. Over UIA, the MCP diffs the process's top-level and owned windows (`find_elements controlType=Window`) before and after.

  Each result gains `opened: [...]`, `closed: [...]`, `modal?` and `aimedAt` (the point actually clicked). Avalonia's routed `Click`/`Command` could later play godot's `fired` role, naming the command a click ran. The pipe's click result already names its semantic `action`.
- **Size:** S over the pipe, S to M over UIA.
- **Linear:** partly covered. YAAT-173 lists "modals opened by an action go unreported" and the owned-window miss. The shape of godot's `uiChanged` result and `aimedAt` is the borrowable part.

## 9. Screenshot baselines, compare, and pixel-change waits

- **godot-mcp:** `save_screenshot_baseline` / `compare_screenshot` (decision 11). A named PNG and its crop are stored together in a sidecar. The comparison counts pixels over a channel tolerance and returns a changed ratio, a bounding box and a red diff image. A mismatch is a result, and a size mismatch fails. `batch_drive` takes a `screenshot` assertion.
- **YAAT/CRC problem:** YAAT-242 (RBL readout deconfliction) and the montage Framing rule ("data blocks positioned so every line of text is readable") are visual checks with no guard. CRC's scopes can only be read as pixels (`client-driver-mcp.md` "What CRC exposes"), so "did the scope change after this command" and friction #17's "wait until changed" have no tool.
- **Mapping:**
  - YAAT: feasible over the pipe's `RenderTargetBitmap` stills, which are deterministic enough for a cropped region of a paused sim (unmeasured).
  - CRC: feasible on real-mode screen copies of a cropped scope region. Friction #5 (the copy shows whatever covers the window) argues for a WGC single-frame grab, which idea 1's helper could provide.
  - `wait_for {region, changed | stable, tolerance}` reuses the same compare.
- **Size:** M.
- **Linear:** not covered.

## 10. Frames at set sim times in one call (`capture_frames`) and a contact sheet

- **godot-mcp:** `capture_frames` (decision 15). It saves frames at a list of game-clock moments (`at`, or `{every, for}`) with no round trip per frame. It reports how late each one was (`late`) and which ones shared a frame. It returns paths only.
- **YAAT problem:** reviewing a take means checking framing and spacing at several moments before a full capture. The sampler took one still at `t60` by sleeping. The montage replay check sampled six times by hand. Each review clip has a "what to watch" moment (montage plan, clip tables).
- **Mapping:** needs a pipe method that polls sim time on the UI thread and renders `RenderTargetBitmap` stills at `atSimSeconds[]`. The MCP then writes the files and, optionally, a contact sheet (ffmpeg `tile`, as the `video-capture` skill does). For CRC, wall-time offsets and screen copies only.
- **Size:** S to M.
- **Linear:** not covered.

## 11. Sim control as one tool (`sim_control`: pause, resume, rate, seek, run-until-then-pause)

- **godot-mcp:** `frame_control` (`TOOLS.md` "Time", decision 11) handles pause, resume, step N and time scale. It returns the clock counters. `step-until-render.md` §3.2 adds `until`, which runs until a condition holds and leaves the game paused on that frame for every read tool.
- **YAAT problem:** the sim rate resets to 1× on every reload (montage step 4). The sampler presses the ▶ button by selector, `Button[Text="▶"]`. A scenario launched with `--scenario` starts paused and needs `UNPAUSE` or `SIMRATE n` typed over the pipe (`client-driver-mcp.md` "Running it"). A repro that must reach sim second N runs at 16× and stops by hand. Speech backs up at 16× (6 of 17 lines spoken), so lead-ins and takes need different rates.
- **Mapping:** needs a pipe method through the view model's own commands (the play and pause command, the sim-rate setter, the playback timeline seek). `{action: "run", rate: 16, until: <idea 3 condition>}` leaves the sim paused at the met point. Not applicable to CRC, which follows the server. This could also be built as app tools (idea 4). A dedicated tool keeps the result shape and refusals in one place, as godot-mcp keeps `frame_control` beside its game tools.
- **Size:** S to M.
- **Linear:** not covered.

## 12. Timeline watch: values over a window, including foreground and activation

- **godot-mcp:** `watch` (`TOOLS.md`, decision 29, `plans/timeline-watch.md`) samples property and expression tracks every frame beside any other tool, with `start`, `stop` and `run`. It returns change points with `first`, `last`, `min`/`max` and `minAt`/`maxAt`, and caps busy tracks.
- **YAAT problem:** YAAT-245 asks for a foreground monitor kept running beside a capture session to catch which call activates the never-activated window. YAAT-237 measured the radar redrawing at ~10 Hz while paused. A take's spacing (`.rbl` distance) and altitude over time are read today from `bug_bundle.py track` after the fact, and the live replay can drift from it (YAAT-83).
- **Mapping:** feasible. In the MCP, a foreground track via a `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` listener records `{wallUtc, hwnd, pid, title}` changes and the pipe call running at that moment. That is exactly the YAAT-245 diagnostic, and it also works with CRC in the session. Over the pipe, view-model tracks (rbl range, an aircraft's altitude and phase) are sampled on the UI thread each second of sim time.
- **Size:** M (the foreground track alone is S).
- **Linear:** the foreground diagnostic is suggested inside YAAT-245 and YAAT-237 but not as a tool. Value tracks are not covered.

## 13. Arguments checked by name, refusals that carry the fix, and a hang probe

- **godot-mcp:** `TOOLS.md` "Rules every tool shares". An unknown argument is refused with the list of arguments the tool takes, a wrongly typed one with its expected type, and a missing one by name. A node path that is not found names the deepest node that exists and its children.

  A text target that misses lists near misses. When a request times out, `HangProbe` (`ARCHITECTURE.md` Wire) pings with 2 s to answer. If the ping is silent, it reports the process's CPU, thread count, main-thread wait reason and last stderr lines.
- **YAAT/CRC problem:**
  - Friction #4: the bare "An error occurred invoking 'find_elements'", twice, mid-tree-change.
  - YAAT-237: a null `elementId` returns a bare error.
  - Friction #22: `invoke` reports "element disappeared mid-call" after a successful close.
  - A pipe call that blocks on a busy UI thread is reported only as a 30 s timeout (`client-driver-background.md` 6a-1).
- **Mapping:** feasible in the MCP. Wrap every tool in one exception filter that names the argument and the exception type, retry once on a UIA `ElementNotAvailableException`, and treat a target that vanished after an `invoke` as success. On a pipe timeout, `ping` the host and sample the client process: whether the UI thread is stuck or merely busy, plus the client log tail.
- **Size:** S.
- **Linear:** partly covered. YAAT-237 has the null-id case and YAAT-173 friction #4/#22. The probe on timeout is not covered.

## 14. Item targets for list controls (`{element, item: {text}}`, opening the popup first)

- **godot-mcp:** `TOOLS.md` "Drive input" and decision 25. `click {element, item {text | index}}` reaches items of an `OptionButton`, `PopupMenu`, `ItemList`, `TabBar` or `Tree`. It opens a closed dropdown with a real click first (`aimedAt.opened`). It refuses, with the numbers, an item that is scrolled out, disabled, collapsed or ambiguous, listing up to ten shown texts.
- **YAAT/CRC problem:** WPF ComboBox items cannot be picked because the popup closes between calls and items are named after the view-model type (friction #3, #9, and the recurrence in the ERAM session). An Avalonia combo item that has scrolled out of its popup cannot be clicked (#14). The documented lobby workaround types the ARTCC id into the ComboBox.
- **Mapping:** feasible for both. For CRC, use UIA `ExpandCollapsePattern.Expand`, then the item whose first descendant Text matches, then `SelectionItemPattern.Select`, all in one call and with no foreground needed. Over the pipe, the client selects the matching `ComboBoxItem` (the semantic click ladder already handles ComboBoxItem picks) after `ScrollIntoView`.
- **Size:** S to M.
- **Linear:** covered by YAAT-173 (friction #3/#14 `select_item`). godot's refusal set is the part to borrow.

## 15. Set with read-back

- **godot-mcp:** `set_property` / `set_node_properties` / `cs_set` convert the value, read it back and return `{before, after}`. When the read-back differs (clamped or read-only), they fail and restore the old value.
- **YAAT/CRC problem:** `set_text` on `FolderPathBox` reported success and did not take (#6). `set_text` in `PickerHost` reported "typed …" over an empty value (#21). Real-mode typing raced the user's keyboard (#10). A read-back would have caught each of these.
- **Mapping:** feasible. The pipe's `set_text` returns `{before, after}` and fails on a mismatch. The UIA path reads `ValuePattern` back.
- **Size:** S.
- **Linear:** covered by YAAT-173 (#6/#10/#21 ask for a read-back).

## 16. Load-adjusted timeouts in the driver

- **godot-mcp:** every ceiling counts load-adjusted time (`ARCHITECTURE.md` "Load-adjusted time", `TOOLS.md` rules), with a 5× wall-time backstop whose error text says how free the machine was.
- **YAAT problem:** `launch_yaat`'s wait and the pipe's 30 s request timeout are wall time. Navigation data takes about 10 s to load after launch (`client-driver-mcp.md` "Input modes"). A launch during a `test-all.ps1` run on the same machine can time out spuriously (unmeasured). yaat's `tools/gate.ps1` already has the clock.
- **Mapping:** feasible, MCP-only, by reusing the gate's load clock logic.
- **Size:** S to M.
- **Linear:** not covered. Low priority.

## 17. Drag and hover over the pipe

- **godot-mcp:** `drag` (press, motions carrying the held button, release, reporting `guiDragStarted`/`dropAccepted`), `hover` (waits for and returns the tooltip text and rect), and `mouse_button` for hand-built gestures.
- **YAAT problem:** drags and hover are listed as out of reach in-app (`client-driver-mcp.md` "Background driving"). The montage needs data blocks moved so every line is readable (Framing), and a radar pan is a drag (friction #25).
- **Mapping:** feasible over the pipe with the existing `SyntheticPointer` (press, moves, release on one `IPointer`). Not feasible on CRC's scopes in virtual mode, where real mode is the only route. App tools (idea 4: `SetDataBlockOffset`, `SetRadarView`) remove most of the need, so drag is only for UI-path bugs that are about the drag itself.
- **Size:** M.
- **Linear:** not covered.

## 18. Tool annotations and an agent sweep

- **godot-mcp:** every tool declares `readOnlyHint`/`destructiveHint`/`openWorldHint` (`ARCHITECTURE.md` "Tool annotations"). Tools that run app code to read get a `read` class override. The `godot-agent-sweep` skill keeps every agent file's `mcp__godot__*` list current.
- **YAAT problem:** the read-only agents (`Explore`, reviewers) cannot screenshot or read the client's tree unless their files list the driver's tools by hand. A background agent that drives the client (the sampler) is a candidate for a declared tool list.
- **Mapping:** feasible, MCP-only (attributes on the tool methods), plus a small sweep script or a section in the agent files.
- **Size:** S.
- **Linear:** not covered. Low priority.

## Already covered

- Sim-state `wait_until` so a capture stops when the follower lands: YAAT-246 and YAAT-114 item i. Idea 3 lists the godot extras it lacks.
- Mouse-wheel `scroll` for the radar and ground views: YAAT-114 item f.
- Radar and ground pan/zoom view tools through the pipe: YAAT-114 item h. Idea 4 generalises it.
- Native file and folder dialogs: YAAT-114 item g. Automation mode's injected picker and `queue_file_pick` already cover it for pipe-driven clients.
- A single-batch `SendInput` click, the CRC live check, `ElementRegistry` eviction, shot pruning, and the `tail_yaat_log` read cost: YAAT-114 items a–e.
- One driver per application (a lock) and cleanup of the processes a session started, `launch_crc` included: YAAT-172. godot-mcp's pattern of an owner list of server pid plus start time in a shared file, swept when stale (decision 10, `override.cfg`'s second line), fits the lock.
- The friction list (`set_window_bounds`, `select_item`, modal reporting, read-back after `set_text`, a screenshot `outDir`, worktree-relative defaults, the bare error message): YAAT-173. Ideas 8, 13, 14 and 15 add godot's result shapes.
- Running the MCP from an installed copy: YAAT-174.
- Graceful `stop_process`, and a coded error for a null click id: YAAT-237. godot's `stop_project` result (`killReason`, `quitMs`, `alreadyExited`) is the shape to copy.
- The client surfacing in the foreground: YAAT-245. Idea 12's foreground track is the diagnostic it asks for.
- The `live-check -WithInput` menu-count flake: YAAT-230.
- RBL label deconfliction: YAAT-242, a client change. Idea 9 would guard it.
- A shared pipe `list_windows` helper: YAAT-220.
- Already built in the driver from the same family of designs: selector `wait_for` (godot's `wait_for` shape), coded pipe errors with recovery hints, never-activated background driving (the counterpart of godot's quiet runs), and attaching to a hand-started automation-mode client by its discovery file (the counterpart of godot's arm/attach).
