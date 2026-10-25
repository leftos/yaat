# Controller-AI coaching playground (trainer + Claude-as-student)

## Context

The owner wants to train the controller AI the way a student is trained: live sessions in YAAT where the owner is the trainer and Claude works a position as a solo student. Every pilot call and every moment Claude would take initiative pauses the sim; Claude explains its read of the picture and the instruction it would give; the owner approves or corrects; Claude sends it and resumes. The lessons are recorded so they can later become rules in the deterministic brains (`docs/plans/controller-ai/04-ground-brain.md`, YAAT-514's coaching loop).

Decided in the interview: **OAK Ground** first; **auto-pause, trainer approves, then Claude sends**; the owner watches **Claude's launched client, visible** (uncloaked, never-activated); **build the core observation tools before session 1**. Commands are **solo-session forms only** (no `CLANDF`/`CAPPF`/`RTISF`/`FOLLOWF`, no bare-callsign `RTIS`): the room runs in solo mode, whose dispatcher refuses RPO-only forms (`DispatchContext.RefusesRpoOnly`), and the runbook says so with a pointer to COMMANDS.md "Solo Training Command Differences".

Linear: project "Controller-AI coaching", feature parent YAAT-1164; items YAAT-1165 (wire), YAAT-1166 (observation tools), YAAT-1167 (`send_command`), YAAT-1168 (pause triggers), YAAT-1169 (runbook, records, dry run), YAAT-1170 (driver docs after YAAT-1163).

branch: feat/controller-ai-coaching (feature pair `../yaat.wt/feat-controller-ai-coaching/yaat` and `/yaat-server`).

The client-driver MCP and its docs (`tools/Yaat.ClientDriver.Mcp/`, `docs/client-driver-mcp.md`, `docs/plans/client-driver-mcp-friction.md`) move to yaat-server under YAAT-1163; the in-app automation pipe (`src/Yaat.Client/Automation/`) stays here. The edits this plan makes to the MCP's `wait_until` description and to the driver docs therefore go to their yaat-server home once YAAT-1163 lands; until then the client-side `wait_until` kinds work through the MCP unchanged, since it forwards conditions unvalidated.

## What the student needs that the driver lacks today

Earlier agent sessions drove the UI (menus, dialogs, recordings, radar framing). A student has to *watch traffic continuously and react*, which needs:

| Need | Today | Gap |
|---|---|---|
| Whole picture as data (who, where, phase, route, taxi route, hold-short ahead, queue, meet points, conflicts) | screenshots only | `get_traffic`, `get_aircraft` |
| Hear the radio, in order, without missing lines | `wait_until log_matches` (one-shot, Message-only) | `read_terminal(sinceSequence)` |
| Know a *call* from a *readback* | not possible: server sends Proactive/Report/Readback all as `SayPilot`, client collapses to `Say` | pilot transmission kind on `TerminalBroadcastDto` (cross-repo) |
| Who is waiting on me / who has checked in | server-only (`PendingPilotRequest`, `HasMadeInitialContact`) | project both onto `AircraftDto` (cross-repo) |
| Pause the moment a pilot calls or something needs initiative, even between my tool calls | agent-held `wait_until` → gaps, 600 s cap | persistent client-side **pause triggers** + `paused` wait condition + event log |
| Airport context (wind, altimeter, METAR, runways in use, my position/frequency, solo settings) | scattered VM state, no tool | `get_airport_context` |
| See the ground picture the trainer sees | no ground framing tool | `center_ground` (+ existing `screenshot`) |
| Send an instruction and learn if it was accepted | `set_text` + `{ENTER}`, result invisible | `send_command` returning the dispatch result |

## Implementation (orchestrated through `implementer` briefs, `plan-execution`)

### 1. Wire additions (cross-repo, DTO projection only — thin server)
- yaat-server: `TerminalBroadcastDto` gains `PilotKind` (string?, `Proactive`/`Report`/`Readback`/`SayReadback`, null for non-pilot lines), set where `TickProcessor.cs:~337-357` drains solo transmissions. `AircraftDto` gains `PendingPilotRequest` (kind + last pilot line + since-seconds, null when none) and `HasMadeInitialContact`, projected in `DtoConverter`.
- yaat client: mirror both DTOs (`src/Yaat.Client.Core/Services/ServerConnection.cs:1144`, `:1901`); `TerminalEntry` keeps `PilotKind`; `AircraftModel` gets the two fields via `UpdateFromDto`.
- Docs: `docs/training-hub-contract.md`. Gate: `pwsh tools/test-all.ps1` (Yaat.Sim untouched, but both DTO halves change).

### 2. Observation app tools — new `src/Yaat.Client/Automation/Tools/AutomationTools.Coaching.cs`
Pattern: `[AutomationTool]` methods returning `AppToolOutcome.Done(JSON)` exactly like `GetFraming` (`AutomationTools.Radar.cs:112-141`, Web JSON options); gates `NotInRoom`/`NoScenario` (`AutomationTools.cs:42-66`); `FindAircraft` for callsigns.
- `get_traffic(airport)` — one compact row per visible aircraft (reuse `MainViewModel.IsAircraftVisible`, `MainViewModel.cs:1149`); `airport` "" = all, else on-ground at/near it plus arrivals to it. Row: callsign, type, CWT, rules, dep/dest, lat/lon, heading, GS, alt, onGround, phase, situation, smartStatus, parkingSpot, currentTaxiway, taxiRoute, taxiDestination, holdKind/isHeld, hasHoldShortAhead, nextCrossingRunway, runwayQueue (runway/position/intersection), assigned/departure runway, meetPoints, conflictPeer, pendingCommands, pendingPilotRequest, hasMadeInitialContact.
- `get_aircraft(callsign)` — the full row plus route, cruise alt, remarks, squawk, exitsAhead, phaseSequence, owner/handoff, scratchpads, armedReports.
- `read_terminal(sinceSequence)` — entries above the cursor (`IAutomationState.TerminalEntriesSince`), oldest first, non-history: sequence, simSeconds, kind, pilotKind, callsign, initials, message; plus `cursor` (newest sequence).
- `get_airport_context(airport)` — METAR + `WeatherDisplayInfo` (`MainViewModel.Weather.cs:232-317`), `RoomActiveRunways` (`MainViewModel.ActiveRunways.cs:40`), `ActivePosition` (callsign, radio name, frequency, type), solo mode + pacing settings (`MainViewModel.cs:384-406`), sim time/rate/paused.
- `center_ground(target, zoom)` — centre the primary ground view (`GroundViewModel.ViewCenterLat/Lon/ViewZoom`, `GroundViewModel.cs:511-526`) on a callsign or the airport; implementer measures `ViewZoom` units and documents them.

### 3. `send_command(text)` app tool
Extract the tail of `MainViewModel.SendCommandAsync` (`MainViewModel.cs:2635-2884`) into a method that returns an outcome (target callsign, canonical text, `CommandResultDto` success/message, or the local refusal from the parse/VFR/compound gates); the `[RelayCommand]` keeps calling it so Enter behaves identically. The tool returns that outcome as JSON. The readback arrives later on the terminal (`read_terminal`).

### 4. Pause triggers + `paused` condition + event log
- `arm_pause_triggers(pilotCalls, warnings, newAircraft, groundStops, heartbeatSeconds)` / `disarm_pause_triggers()` / `get_pause_events(sinceSequence)`. Held in a `PauseTriggerMonitor` (automation-only, not saved) that evaluates on terminal entries and on each aircraft update batch, calls `IAutomationState.PauseAsync()` and appends `{sequence, trigger, callsign, simSeconds, detail}`.
  - `pilotCalls`: pilot line with `PilotKind` Proactive or Report.
  - `warnings`: terminal Warning/Error, or `ConflictPeerCallsign` becoming set.
  - `newAircraft`: an aircraft appears.
  - `groundStops`: an on-ground aircraft with an active taxi route, a hold, or a runway-queue slot goes from moving (> 1 kt) to stopped for ≥ 5 sim s.
  - `heartbeatSeconds`: pause every N running sim seconds (0 = off), a scan point.
- `wait_until` gains condition `paused` (`WaitUntilParams.cs:52-61` KindNames + `WaitUntilHandler.EvaluateOne`), and `then` gains `unpause`.
- **The student scans; the triggers are the safety net.** A good scan is taught to students, so the student watches continuously at a human cadence rather than waiting on triggers: `play` → `wait_until` (`mode: any`: `paused`, or `sim_seconds` about 10 s ahead) → on a pause, `get_pause_events` and handle it; otherwise `get_traffic` + `read_terminal` and check the picture (converging taxiers, an aircraft nearing a hold-short or runway, one stopped or off its route, a call left unanswered) → pause and talk only when the scan finds something to act on → after the owner rules, `send_command` → `play`. The heartbeat trigger stays optional, off by default. `get_traffic` rows stay compact enough to read every scan.
- Update the `wait_until` `[Description]` in the MCP's `Tools/PipeTools.cs` (yaat-server after YAAT-1163; the MCP passes conditions through unvalidated, so the live MCP works before its rebuild).

### 5. Coaching runbook, records, docs
- Project skill `.claude/skills/atc-coaching/SKILL.md`: session start (yaat-server from the paired worktree on :5130; `dotnet build src/Yaat.Client` in the worktree; `launch_yaat` with absolute `exePath`/`appDataDir`, uncloaked; `connect`; `create_room ZOA`; load the chosen OAK scenario via `lastScenarioFolder`; `set_solo true`; confirm the student position is OAK GND; `arm_pause_triggers`), the loop above, the explanation format (picture → candidates ranked → choice and why → exact command), solo-only command rule, logging each exchange, and teardown (stop client + server).
- `docs/plans/controller-ai/coaching/README.md` (what this is, how it feeds 04-ground-brain/YAAT-514), `sessions/NNN-<date>-<scenario>.md` per session (each pause: trigger, picture, my reasoning, proposal, trainer verdict, lesson), `ground-lessons.md` (distilled rules, each mapped to a GroundBrain rule or a `KOAK.json` knowledge entry, or "new rule").
- The driver docs in their yaat-server home after YAAT-1163: `client-driver-mcp.md` (new tools, `paused`, `unpause`, the coaching loop) and `client-driver-mcp-friction.md` (appended per session); glossary entries ("pause trigger", "coaching session"), architecture Task Index row for "add an automation app tool or wait_until condition", controller-ai README link.
- Linear: **add** an issue for this playground (Programmes / controller-AI), linked to YAAT-514, with the `branch: feat/controller-ai-coaching` feature marker per `~/.claude/docs/plan-operations.md`.

## Tests
- `tests/Yaat.Client.UI.Tests/Automation/AppToolsTests.cs`: extend `ListAppTools_ReportsEveryToolWithItsParameters`; one test per tool (shape, gates, unknown callsign, empty cursor); `send_command` refusal and success paths through a fake connection.
- `AutomationWaitUntilTests.cs`: `paused` holds/doesn't; `unpause` action. New `PauseTriggerMonitorTests`: each trigger fires once per event, readbacks don't fire `pilotCalls`, disarm stops it, heartbeat cadence. `IAutomationState` fakes (`GetSimTimeTests.cs:45`, `AutomationWaitUntilTests.cs:578`, `AppToolsTests.cs:1615`) updated if the interface grows.
- Server: DTO projection tests for `PilotKind`, `PendingPilotRequest`, `HasMadeInitialContact`.

## Verification
1. Gates per brief; the landing gate `pwsh tools/test-all.ps1` (ceiling 900, heavy) on the worktree.
2. Live dry run (me, before session 1): server from `yaat.wt\coaching\yaat-server`, client from the worktree build, OAK ground scenario in solo; arm triggers; confirm a stand call pauses the sim with a `pilotCalls` event, `get_traffic` shows the caller with `pendingPilotRequest`, `send_command "<CS> TAXIAUTO 30"`-style instruction returns success, the readback appears in `read_terminal` with `PilotKind=Readback` and does not pause, and an RPO-only form (`CLANDF`) is refused by solo. Friction goes in the friction log.
3. Hand over for session 1 with the client window visible on the owner's desktop.
