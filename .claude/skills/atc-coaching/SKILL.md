---
name: atc-coaching
description: Run a controller-AI coaching session in which the owner is the trainer and Claude works a position in a live YAAT client as a solo student. Use when the owner says "coaching session", "let's train", "you're the student", "session N", or asks to resume a coaching session; also when writing up a session's lessons into docs/plans/controller-ai/coaching/.
---

# ATC coaching session (owner = trainer, Claude = student)

The owner trains Claude the way a student is trained. Claude works the position in a real YAAT client over the automation pipe, scans the traffic continuously at a human pace, and stops to talk before every instruction. The owner rules on each proposal. Every exchange is recorded, and the lessons become controller-AI rules later (`docs/plans/controller-ai/coaching/README.md`, `04-ground-brain.md`, YAAT-514).

## Ground rules

- **Solo-training forms only.** The room runs in solo mode, and its dispatcher refuses the RPO-only forms. Never use `CLANDF`, `CAPPF`, `CVAF`, `FOLLOWF`, `RTISF`, `RFISF`, or a bare-callsign `RTIS`/`RFIS`. Read COMMANDS.md "Solo Training Command Differences" before the first session of a context.
- **Never send before the trainer rules.** Every instruction is proposed first, while the sim is paused. Claude sends it only after the owner approves it or corrects it, then resumes.
- **Scan like a controller, at a human pace.** While the sim runs, look at the picture about every 10 sim seconds. Don't just wait for a pause trigger. The triggers are a safety net, not the scan.
- **Sessions run at sim rate 1.** The student works in real time, and a pause trigger is exact only at 1x.
- **Phraseology and procedure come from the local references:** 7110.65 and AIM under `.claude/reference/faa/`, and OAK knowledge in `src/Yaat.Sim/Data/FacilityOps/KOAK.json`. Cite the paragraph when explaining a choice.
- **Driver rules:** one driver per application, and close what you opened. See yaat-server's `docs/client-driver-mcp.md`, its "Rules for a driving session", and its "Coaching a student" for the tools' answers.

## Setup

The feature pair is `X:\dev\yaat.wt\feat-controller-ai-coaching\{yaat,yaat-server}`. Use absolute paths throughout: the MCP's relative defaults resolve against the main checkout.

1. Build the client in the yaat worktree:
   `pwsh tools/gate.ps1 -Log .tmp/build-client.log -TimeoutSeconds 600 -Slot heavy -- dotnet build src/Yaat.Client -c Release -p:TreatWarningsAsErrors=true`.
2. Start the server from the yaat-server worktree in the background, on `http://localhost:5130`:
   `dotnet run --project src/Yaat.Server -c Release`.
3. Copy the scenario the trainer picked into `<yaat wt>\.tmp\coaching\scenario\` (alone in that folder).
   Write `<yaat wt>\.tmp\coaching\appdata\preferences.json` with `lastScenarioFolder` set to that folder, keeping any other keys.
4. Launch the client with `launch_yaat`:
   - `exePath`: the worktree's `src/Yaat.Client/bin/Release/net10.0/Yaat.Client.exe`
   - `appDataDir`: `<yaat wt>\.tmp\coaching\appdata`
   - `cloaked: false`, so the owner watches the window

   Wait until the client log (`tail_yaat_log`) shows "Navigation database built". `wait_until`'s `log_matches` reads the terminal, not this log.
5. Call these app tools in order: `connect("http://localhost:5130")`, then `create_room("ZOA")`, then `set_solo(true)`. Solo goes on before the scenario loads, so the load sets the room up for a solo student.
6. Load the scenario: Scenario › Load Scenario › Local Files, its only item, then Load. A session-setup prompt may follow, asking for active runways or the positions the student works.

   The prompt is an overlay inside MainWindow, not a window of its own. `list_windows` does not show it, so find its buttons with `find_elements` under `MainWindow`. Answer it as the trainer chose.
7. Confirm the student position with `get_airport_context("OAK")`: `position.callsign` is `OAK_GND`, and `solo.enabled` is true. The scenario must not set `SoloRpoCommandsAllowed`, which would let solo accept the RPO-only forms; check the scenario file before loading it.
8. Select the Ground View tab. Call `center_ground("OAK", 4)` (zoom 1 shows the whole field too small to work), then `screenshot` the ground view, so the owner and Claude start from the same picture.
9. Call `arm_pause_triggers(pilotCalls: true, warnings: true, newAircraft: true, groundStops: true, heartbeatSeconds: 0)`. Start the session log (below).

## The loop

```
play
repeat:
  wait_until mode=any [ {kind: paused}, {kind: sim_seconds, atLeast: now + 10} ]
  if paused:     get_pause_events(lastEventSeq) → handle each event (below)
  else (scan):   get_traffic("OAK", activeOnly: true) + read_terminal(lastSeq) → check the picture
                 if something needs action → pause, then handle it as an initiative
```

The scan checklist is applied on every scan. It is what a good student watches:
- **An unanswered call:** any `request`, or a pilot line not yet answered.
- **Converging taxiers:** two aircraft whose taxi routes share a taxiway or intersection ahead, or a `meet` point.
- **Hold-shorts and runway crossings:** an aircraft nearing a hold-short (`holdShortAhead`, `nextCrossing`), or an aircraft nearing its departure runway hold-short. Nearing the departure hold-short means it is due for a transfer to tower.
- **Wrong state:** an aircraft stopped, holding, off its route, or on the wrong taxiway.
- **Arrivals:** an arrival vacating the runway, or one that will call for taxi-in.
- **Runway status:** the runways in use, and whether anything changed.

When an aircraft needs a closer look, read `get_aircraft(callsign)`. When geometry matters, take a ground-view `screenshot` after `center_ground`.

**Handling a pause:**
1. Read the event and the picture: `get_traffic`, `read_terminal`, and `get_aircraft` for the aircraft involved.
2. Tell the trainer, in this order:
   - **The picture:** who is where and doing what, in two or three lines.
   - **Candidates ranked:** every aircraft that needs something now, and why this one is first.
   - **The choice and why:** the instruction, and the reason for its route, runway, sequencing or wording. Cite 7110.65, the AIM or KOAK.json where it applies.
   - **The exact command:** the YAAT text to send, for example `SWA1234 TAXIAUTO 30`. Name the solo form.
3. Stop and wait for the trainer. Do not unpause.
4. After the ruling, send the command with `send_command`. Check `sent` and `success`. Read the readback with `read_terminal`, and check that its `pilotKind` is `Readback`. A refusal is reported to the trainer, not retried with a different form on a guess.
5. Log the exchange, then `play`.

**Taking initiative** follows the same steps as handling a pause. Say "Initiative:" first, and say what the scan saw that a trigger did not.

When the trainer flags something Claude missed, log it as a miss. Note whether a scan should have caught it (a checklist gap) or a trigger should have (a trigger gap).

## Records

- **Session log:** write one per session at `docs/plans/controller-ai/coaching/sessions/NNN-YYYY-MM-DD-<scenario>.md`, using the template in `sessions/README.md`. Append each exchange as it happens, not at the end.
- **Lessons:** at the end of the session, distill each verdict that taught something into `docs/plans/controller-ai/coaching/ground-lessons.md`. Write it as a rule, map it to the GroundBrain rule, KOAK.json entry or new rule it implies, and link back to the session entry.
- **Tool friction:** append it to yaat-server's `docs/plans/client-driver-mcp-friction.md`.
- Commit the records on the feature branch at the end of the session.

## Teardown

Run `disarm_pause_triggers`, then `stop_process` the client, then stop the server you started. Leave the trainer's own applications alone.
