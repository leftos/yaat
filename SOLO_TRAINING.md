# Solo Training Guide

Solo Training is YAAT's single-student mode. You work the traffic as the controller, and YAAT simulates the pilots: readbacks, pilot-initiated calls, optional spoken audio, and a few built-in safeguards that make common training misses visible.

This guide is written for students who may never have worked as an RPO in YAAT, ATCTrainer, or a similar tool. It explains the operating model first, then points to the [User Guide](USER_GUIDE.md) for UI details and the [Command Reference](COMMANDS.md) for complete syntax.

## Table of Contents

- [Quick Links](#quick-links)
- [The Mental Model](#the-mental-model)
- [If You Have Never Used An RPO Tool](#if-you-have-never-used-an-rpo-tool)
- [Start a Solo Session](#start-a-solo-session)
- [Recommended Setup: CRC for the Scope, YAAT for Pilot Control](#recommended-setup-crc-for-the-scope-yaat-for-pilot-control)
- [Scenario Setup And Pacing](#scenario-setup-and-pacing)
- [Working The Frequency](#working-the-frequency)
- [First Session Flow](#first-session-flow)
- [Pilot-Initiated Requests](#pilot-initiated-requests)
- [Solo-Specific Command Habits](#solo-specific-command-habits)
- [Built-In Safeguards](#built-in-safeguards)
- [Session Report](#session-report)
- [Scenario Author Notes](#scenario-author-notes)
- [Procedural References](#procedural-references)

## Quick Links

- [Getting Started](GETTING_STARTED.md) - installation, identity setup, and first scenario load.
- [User Guide: Command Bar](USER_GUIDE.md#command-bar) - where to type commands and how feedback appears.
- [User Guide: Loading a Scenario](USER_GUIDE.md#loading-a-scenario) - ARTCC scenarios, local files, and Scenario Setup.
- [User Guide: Connecting CRC for Students](USER_GUIDE.md#connecting-crc-for-students) - CRC environment setup details.
- [Command Reference](COMMANDS.md) - every command, alias, argument, and example.
- [Command Reference: Solo Training Command Differences](COMMANDS.md#solo-training-command-differences) - solo/RPO mode differences.

## The Mental Model

In a normal instructor/RPO session, a human RPO plays each aircraft. The student issues ATC instructions, the RPO types YAAT commands, and the RPO speaks or simulates the pilot responses.

In Solo Training, YAAT removes the separate RPO:

- You still control the aircraft by issuing YAAT commands.
- The simulator generates pilot readbacks and pilot requests.
- The pilot does not invent a plan or self-clear through your traffic. The existing aircraft phases, command dispatcher, and scenario data still decide what the aircraft can do.
- The Session Report adds coaching and scoring, but it is separate from controller-authentic alerts.

Think of Solo Training as a training frequency with simulated pilots, not as an autopilot that solves the session. If a departure needs taxi, takeoff, a heading, a climb, a frequency change, or a handoff, you still issue those instructions.

## If You Have Never Used An RPO Tool

YAAT commands are how you tell the simulator what the pilot was instructed to do. A command line normally has two parts:

```
<aircraft> <instruction>
```

The aircraft can be the full callsign or a unique partial callsign:

```
N123AB FH 270
123AB FH 270
```

If you have already selected an aircraft in the list, radar view, or ground view, you can omit the callsign:

```
FH 270
```

Canonical commands are compact. `FH 270` means fly heading 270, `CM 050` means climb and maintain 5,000 ft, and `SPD 210` means maintain 210 knots. These are the same kinds of terse entries an RPO would type quickly while listening to a student.

Solo Training also accepts many typed ATC-style instructions after the callsign. That lets a student practice phrasing without memorizing every YAAT shorthand on day one. The canonical command reference still matters because it is exact, fast, and covers every supported action.

After you press **Enter**, check the terminal. `RSP` tells you whether YAAT accepted the command. `SAY` is the simulated pilot transmission, which may arrive a few seconds later because the radio queue prevents pilots from talking over each other.

## Start a Solo Session

1. Open **Settings > Scenarios** and enable **Solo training mode**. This is your saved default for future scenario loads.
2. Optional: open **Settings > Speech** and enable Solo pilot voice. If TTS is off, pilot transmissions still appear in the terminal.
3. Load traffic from **Scenario > Load Scenario...**. Solo Training works with ARTCC scenarios from vNAS and local ATCTrainer-format scenario files.
4. If **Scenario Setup** appears, choose the difficulty and any solo workload pacing controls.
5. Work the traffic from the command bar and watch both command feedback and pilot transmissions in the terminal.
6. Open **Scenario > Session Report** during or after the run to review score, active issues, coaching notes, and runway/approach outcomes.

After a scenario is loaded, the gear button next to the command bar controls live session settings, including the active Solo Training Mode toggle and any workload pacing sliders. These settings affect the room session, not only your local window.

## Recommended Setup: CRC for the Scope, YAAT for Pilot Control

In a real training or live session, you would not have YAAT's radar view, ground view, aircraft list, or flight-strip editors. You would have a CRC scope and the procedural information your facility makes available. Practicing with the full YAAT UI gives you flight-plan tables, route fixes, ground-truth aircraft positions, and trajectory previews that hide gaps in scan, judgment, and procedure recall.

Solo Training is most useful when you mirror the real working environment: drive the radar from CRC and use YAAT only for pilot interaction. YAAT acts as the simulated RPO; CRC is your scope.

### One-Time CRC Setup

1. Install CRC normally and sign in once with your VATSIM credentials so the per-user config exists.
2. In YAAT, open **Tools > Configure CRC Environments**. This writes (or updates) CRC's `DevEnvironments.json` with the YAAT environments. The hosted server entry is **YAAT1** (`https://yaat1.leftos.dev`); the local-development entry is **YAAT Local** (`http://localhost:5000`).
3. Restart CRC so it picks up the new environment list.
4. In CRC's environment selector, choose **YAAT1** (or **YAAT Local** if you are running a server on this machine), then connect with your VATSIM credentials.

If you skip step 2, the [User Guide: Connecting CRC for Students](USER_GUIDE.md#connecting-crc-for-students) section covers the manual `DevEnvironments.json` edit and the PowerShell setup script.

### Per-Session Layout

Once CRC is connected:

1. Load a scenario in YAAT (Solo Training mode on).
2. In YAAT, click the **Pop Out** button at the top right of the terminal panel. The terminal becomes its own window so you can keep it on top of CRC.
3. Minimize or move the rest of the YAAT main window off-screen. You will not need its radar view, ground view, or aircraft list during the session. Keep the **Session Report** window open if you want live coaching feedback alongside CRC.
4. Position the popped-out terminal where you can see both the YAAT pilot replies (`SAY`) and CRC's scope.
5. From CRC, take the position the scenario expects. CRC will see and own the simulated traffic that YAAT is feeding the room.

### What You Use Each Tool For

| Tool | Use it for |
|------|------------|
| CRC | Radar (STARS / ERAM), Tower Cab, flight strips, scratchpads, leader lines, datablock interaction, conflict alerts. This is your operating picture. |
| YAAT terminal | Issue commands as the simulated RPO. Read pilot transmissions (`SAY`) and command results (`RSP`). Acknowledge pilot requests. |
| YAAT Session Report (optional) | Live coaching, scoring, missed advisories, runway and approach outcomes. Treat this as debrief material, not as a real-time control aid. |

The YAAT command bar is the only YAAT UI you should rely on during the session. Treat the radar view, ground view, and aircraft list as scenario-author and debugging tools, not student aids - they expose information a real controller would not have on the scope.

### Working CRC With Solo YAAT

Inside this layout, the workflow is:

- The student manipulates CRC normally - track, accept handoffs, scratchpad, etc. CRC interactions that YAAT supports are reflected back into the simulation.
- Anything the student would say to a pilot becomes a YAAT command in the popped-out terminal: callsign plus a canonical command or an ATC-style instruction (Solo Training accepts both).
- Pilot readbacks and pilot-initiated calls land in the terminal as `SAY` lines. Optional Solo pilot voice (Settings > Speech) lets these play out loud.
- Avoid the YAAT radar view, ground view, and aircraft list for control decisions. If you need them at all - usually for setup or debriefing - bring the YAAT main window back, then minimize it again.

If a student insists on using the YAAT radar instead of CRC, the session is no longer a faithful CRC practice run. Use that as a training cue, not as a workflow.

## Scenario Setup And Pacing

Scenario Setup appears only when there is something to choose. A scenario with multiple difficulty levels shows **Difficulty**. In Solo Training, scenarios with matching workload sources can also show:

- **Parking initial call-up interval** - how often parked aircraft make their first ready-to-taxi call.
- **Arrival generator rate** - what percentage of generated arrivals should spawn.
- **Pilot go-around probability** - 0–100% chance that each AI aircraft entering final approach spontaneously goes around. Rolls once per approach; default 0 (existing behavior unchanged). Set a global default under **Settings > Scenarios**; override per-scenario from the scenario setup dialog or the live session settings flyout (per-scenario value persists either way).

Use these as workload controls. A lower parking interval releases parked aircraft faster; a higher interval spaces them out; **Paused** stops new parking call-ups until you raise the interval again. A lower arrival generator rate reduces arrival workload without editing the scenario file. Raise pilot go-around probability to practice handling unexpected aborts.

These controls do not make a scenario official curriculum. Facility training staff still own scenario design and training objectives. YAAT documents how existing ARTCC and local scenarios behave in solo mode.

## Working The Frequency

The terminal separates two different things:

| Channel | Meaning |
|---------|---------|
| `RSP` | Immediate command result: parsed, accepted, rejected, or applied. |
| `SAY` | What the simulated pilot says on frequency after the radio queue has room. |

For example:

```
N123AB DM 050
```

YAAT returns an immediate `RSP` confirming the descent if the command is accepted. The pilot readback arrives later as `SAY`, serialized with other aircraft so multiple pilots do not speak over each other.

You can type canonical commands:

```
N123AB FH 270
N123AB CM 050
N123AB CAPP ILS28R
```

In Solo Training, you can also type ATC-style instructions after the callsign:

```
N123AB fly heading two seven zero
N123AB climb and maintain five thousand
N123AB cleared ILS runway two eight right approach
```

YAAT tries normal command parsing first. If that fails in Solo Training, the natural-language mapper tries to convert the instruction into a canonical command. When exact command syntax matters, use the [Command Reference](COMMANDS.md).

## First Session Flow

A simple IFR departure session usually looks like this:

1. A parked aircraft calls ready to taxi.
2. Issue pushback or taxi instructions.
3. Clear the aircraft to cross or hold short as needed.
4. Clear the aircraft for takeoff.
5. Assign heading, altitude, speed, or navigation instructions.
6. Transfer the aircraft with `CT` when it is ready for the next controller.

A simple VFR pattern or inbound session may add traffic advisories, field-in-sight calls, pattern instructions, landing clearance, or frequency change approval.

Do not wait for the sim to solve the next control action. Solo pilots can call, read back, remind, and refuse unsafe or impossible instructions, but they still need you to control them.

## Pilot-Initiated Requests

Solo pilots make calls when the scenario and phase call for them. Common examples:

- Parked departures call ready to taxi after the initial delay.
- Airborne arrivals or VFR inbound aircraft call with position and request.
- VFR pattern aircraft request closed traffic and may remind tower if landing clearance is still missing.
- Aircraft clear of the runway report clear after exiting.

Requests remain pending until you handle the underlying need. `STBY` and `ROGER` acknowledge the call, but they do not satisfy a taxi, departure, landing, approach, or airspace-entry request. A pilot told to stand by waits longer before following up.

`STBY` and `ROGER` can still establish two-way communications for Class C airspace when targeted at that aircraft. That is separate from satisfying the request.

## Solo-Specific Command Habits

Most commands work the same in Solo Training and RPO mode, but a few matter more because the Session Report can score the actual controller action.

| Situation | Use in Solo Training | RPO shortcut to avoid |
|-----------|----------------------|-----------------------|
| Issue traffic information | `RTIS <clock> <miles> <direction> <type> [altitude]` — or a VFR form (`RTIS NR 2 C172`, `RTIS BASE R 2 28R M20P`, `RTIS OVER VPCOL C172`) | `RTIS <callsign>`, bare `RTIS`, `RTISF` |
| Issue field information | `RFIS <clock> <miles>` | bare `RFIS`, `RFISF` |
| Issue a safety alert | `SAFAL <clock> <miles> [L\|R] [C\|D]` | No forced shortcut |
| Issue a wake advisory | `CWT`, `CTO ... CWT`, or `CLAND [NODEL] CWT` | No forced shortcut |
| Clear VFR through Class B | `CLBRV` / `CBRV` / `BRAVO` | No forced shortcut |
| Acknowledge without a maneuver | `STBY` / `ROGER` | No forced shortcut |
| Use visual follow | structured `RTIS` first, then `FOLLOW` or `CVA ... FOLLOW` | forced traffic-in-sight shortcuts |

The full syntax and behavior details live in [Command Reference: Solo Training Command Differences](COMMANDS.md#solo-training-command-differences), [Pattern Commands](COMMANDS.md#pattern-commands), and [Approach Control Commands](COMMANDS.md#approach-control-commands). Structured `RTIS` accepts the radar clock form or any of the three VFR descriptive forms (relative position, pattern leg, landmark); the altitude is optional and matching is tolerant, so a within-tolerance-but-imprecise call still counts with a low-severity coaching note. Structured `RFIS` is the solo-mode way to give field-position information for visual approaches; it records proof and lets the pilot acquire the field normally. Bare `RFIS` and `RFISF` stay RPO-only shortcuts. `CWT` is phase-transparent and records caution-wake-turbulence proof; when you use bare `CWT`, the report applies it only when there is exactly one current wake-advisory context for that aircraft.

## Built-In Safeguards

Solo Training keeps several aviation-facing safeguards active:

- VFR aircraft hold outside Class B airspace until cleared with `CLBRV`.
- VFR aircraft hold outside Class C airspace until two-way communications are established.
- Aircraft on a published approach warn near DA/MDA if no landing clearance exists, then go around at minimums if still uncleared.
- Aircraft on approaches without published minimums keep the legacy short-final warning and 200 ft AGL no-clearance go-around.
- Live solo maneuver clearances that the aircraft cannot accept produce an `unable` pilot readback when YAAT has a reason.

These safeguards are training aids. They make missed clearances and airspace gates visible on the frequency; they do not replace normal control.

## Session Report

Open **Scenario > Session Report** during a room session. In Solo Training Mode, it updates live with:

- score and grade,
- active safety or separation issues,
- coaching notes,
- event timeline,
- approach outcomes,
- runway statistics,
- per-aircraft debrief on the **Aircraft** tab — one row per aircraft that has passed through your airspace, with operation, filed route, spawn/completion times, status, finding counts by severity, and a one-line coaching note. Click **Show on Timeline** to filter the main timeline marker rail to that callsign and rewind to its spawn time.

The main window's rewind timeline bar also shows color-coded markers for findings (red Safety, amber Warning, blue Coach) and grey ticks for each dispatched command. Click a marker to rewind to that moment; hover for details.

The Session Report is not the same as CRC conflict alerts. CRC alerts remain controller-facing alerts. Session Report rows are coaching and debrief material generated from YAAT's scoring model.

Current scoring covers IFR radar separation, Class B/C VFR-related separation where applicable, Class C outer-area IFR/VFR advisory service using YAAT's 20 NM training approximation, same-runway, reciprocal, intersecting-runway, converging-runway, and wake events, structured traffic-advisory proof, safety-alert proof, wake-advisory proof, field-in-sight proof, visual-follow states, and related runway/approach outcomes. VFR/VFR and other no-minima proximity cases can appear as Advisory / Visual findings rather than Separation findings. A VFR-on-top (OTP) aircraft is scored as a VFR party for separation and never receives an IFR-separation finding. ARTCC `WakeDirectives` custom data can adjust wake scoring for documented local waivers and facility wake-advisory directives without adding new student commands.

The report uses accepted solo-mode commands as evidence, which is why structured `RTIS`, `SAFAL`, and `RFIS` matter for scored advisory, safety-alert, and visual-approach proof. Advisory proof is only required for aircraft still in the student's service: they have made initial contact, have not been transferred away with `CT` or `FCA`, and are not owned by another student position when position ownership is available.

## Scenario Author Notes

Existing ARTCC and local scenarios can work in Solo Training without changes. They become more useful when they include:

- parking spawns for departure practice,
- arrival generators for sustained arrival practice,
- expected approaches and destination/runway data,
- handoff and frequency data for `CT`, `FCA`, and controller-target readbacks,
- difficulty levels for progressive workload,
- representative VFR traffic near Class B/C shelves when practicing airspace entry and communication.

YAAT should document these expectations, but official scenario design belongs to each ARTCC's training staff. Avoid treating generic solo scenarios as facility curriculum unless the facility has reviewed and adopted them.

## Procedural References

Solo Training behavior is grounded in the local FAA references bundled with the repo. The most relevant sections for this guide are:

- AIM 3-2-4 for Class C airspace entry and communication context.
- AIM 4-2-2 and 4-2-3 for radio technique and contact procedures.
- AIM 4-4-7 and 7110.65 2-4-3 for clearance acknowledgement and readback expectations.
- 7110.65 2-1-6 for safety alerts.
- 7110.65 2-1-21, 7-8-2, and 7-9-5 for traffic advisory context.
- 7110.65 7-6-7 for VFR sequencing/follow behavior.
- 7110.65 7-8-4 for Class C two-way communications.
- 7110.65 7-9-2 for VFR operations in Class B airspace.
