# YAAT

Instructor/RPO client and training server for VATSIM air traffic control training. `Yaat.Sim` owns
the simulation; every other project is a way of driving or displaying it.

This glossary fixes the words that have caused real confusion. It is a glossary, not a spec — no
implementation detail, no decisions. Decisions live in [`docs/adr/`](docs/adr/).

## Simulation execution

**Sim-second**:
One second of simulated time, structured as PrePhysics, four physics sub-ticks, PostPhysics, and the
end-of-second steps. The unit every run advances by.
_Avoid_: tick (ambiguous between the sim-second and one physics sub-tick — say which)

**Spine**:
The single ordered definition of the simulation-affecting steps in a sim-second. There is exactly
one, it lives in `Yaat.Sim`, and every run iterates it rather than keeping its own list.
_Avoid_: pipeline, tick list, post-physics list

**Step**:
One member of the spine.
_Avoid_: stage, phase (phase means an aircraft's flight phase in this codebase and nothing else)

**Segment**:
One of the five parts of a sim-second the spine is entered by: begin, open, pre-physics, physics, post-physics,
end-of-second. A run that cannot advance a whole second at once (the sub-tick replay step) composes segments.
_Avoid_: phase, stage

**Host**:
The collaborator a run supplies to the spine: it provides each step's arguments and consumes each
step's results. One meaning only, reclaimed from six unrelated prior uses.
_Avoid_: sink, adapter, driver

**Run profile**:
What kind of run this is and, consequently, what is allowed to differ from any other run. Separate
from the host on purpose, so a step can ask its host for an argument without being able to ask it
whether this is a replay.
_Avoid_: mode, context, environment

**Run kind**:
A value of the run profile: live, replay, test, or soak.

**State-equivalence**:
The contract between runs: given the same inputs, every run kind produces the same world state.

**Oracle**:
The mechanical test of state-equivalence — it runs one scenario under several run kinds and compares
the resulting state, rather than relying on anyone having classified the steps correctly.

**Recorded input**:
A value the simulation consumes but does not derive, captured so a replay reproduces it rather than
recomputing it. Attendance is the first of these.

**Attendance**:
Which controller positions are currently being worked. Live, it derives from connections; everywhere
else it is a recorded input.
_Avoid_: staffing (staffing means which positions the controller AI has been configured to work)

**Wire projection**:
The server-side mapping between a simulation concept and the CRC wire records that carry it. The
projection is the server's; the concept is the simulation's.

**Coast**:
The interval during which a track that has gone away is still displayed before its delete is emitted.
Measured in sim-seconds.

**Joined session**:
A second connection to the CRC hub (such as vEDST) that attaches to a session another client (CRC) started, via `JoinSession`. It sees that session's position, room and active state but holds no position of its own, so it never counts toward attendance.

**Resource pin**:
The airport layouts and ARTCC configs a room's scenario load used, held on the room so a restart, rewind, export or session restore rebuilds the simulation from the same data rather than the live vNAS caches. It is replaced only by the next scenario or recording load, and cleared by an unload.
_Avoid_: snapshot (that is the simulation state, not its reference data)

**Resource manifest**:
What a scenario JSON will make the server fetch — its ARTCC, the roster's neighbouring ARTCCs and every airport it names — read without loading anything (`ScenarioResourceManifest`).

**Map-required airport**:
An airport in the resource manifest whose full ground map the load needs: the primary airport, and each airport an aircraft parks at or spawns on the ground at. A missing map there is a load warning; elsewhere it is not.

**Prepare / commit**:
The two halves of a scenario load. Prepare reads the manifest, fetches the resources and builds the aircraft without touching the room or holding its tick gate; commit swaps the prepared scenario into the room under the gate, CPU only.

**Load flag**:
The per-room marker (`TrainingRoom.LoadingBy`) a scenario, live-session or recording load holds from before its fetches until after its broadcast. While it is held a second load, unload, restart, rewind, recording load and room close are refused, so nothing can change the room between prepare and commit.

**Step table**:
The list of a scenario load's steps (read, ARTCC configuration, airport layouts, build aircraft, set up the room, re-apply weather, and for a live session start live traffic), each with a state, a detail line and its problems, sent whole to the loader in every progress event and drawn by the client's load overlay.

## Controller actions

**Action**:
One thing a controller did to the simulation — a typed command, a CRC keyboard entry, an AI position's
instruction — as one text, one issuer and one verdict. The unit the action log holds.
_Avoid_: command (a command is the text; an action is the text plus who issued it and what it did), event

**Action router**:
The single route every action takes, on every run kind, from text to effect. There is exactly one,
it lives in `Yaat.Sim`, and no entry point decides anything the router decides.
_Avoid_: dispatch chain, handler chain, command pipeline (the pipeline is the whole path from keyboard to aircraft)

**Kind**:
What sort of action a text is, decided once by the router before anything runs. Every text has exactly
one kind; a text with none is an error, never a default.
_Avoid_: category, verb type

**Arm**:
The router's body for one kind: either simulation logic, or a slot the host fills because the state
is still the host's.
_Avoid_: handler, branch, case

**Scope**:
What the router resolves before an arm runs — nothing, a callsign, a present aircraft, or the acting
position. A property of the kind, not of the recorded text.
_Avoid_: target, addressee

**Baked draw**:
A value a live action drew — a reaction delay, a spawned aircraft, a strip id — carried on its record
so every later run reuses it instead of drawing again.
_Avoid_: cached value, seed

**Derived record**:
A record of a state change no verb names, written by whatever made the change so that a later run
reproduces it.
_Avoid_: synthetic action, side-effect record

**Replay fidelity**:
The property that a recorded action reaches the same verdict when re-applied as it did live. A
difference is reported, never hidden by dropping the record.
_Avoid_: determinism (determinism is the same-seed, same-world property of the simulation itself)

## Ground movement

**Spot line-up**:
The route a `TAXI … $spot` from the ramp is re-planned into: across the apron, a ~90° turn onto the spot's lane on the ramp side, and a slow pull onto the mark facing the movement-area taxiway the lane joins (`RampLaneReposition.TryPlanSpotLineUp`, docs/ground/pathfinder.md).
_Avoid_: ramp cut (a ramp cut is a free-space shortcut between lanes; a line-up decides which way the aircraft ends facing)

**Wingtip-clearance floor**:
The least distance from a taxiway hold-short holder's nose to the centreline of the taxiway it holds short of: the half-span of the widest aircraft the airport can take, judged by its widest runway, plus 25 ft (`HoldShortAnnotator.WingtipClearanceFloorFt`, docs/ground/hold-short-placement.md).
_Avoid_: setback (the setback is where the aircraft's centre stops; the floor is a minimum the nose must keep)

**Route-incomplete hold**:
Where a TAXI whose taxiways do not reach its destination ends: the aircraft taxis the route as issued and holds short of the one taxiway the route still needs, and the controller is told which (`HoldShortReason.RouteIncomplete`, docs/ground/pathfinder.md). Only a new TAXI that includes that taxiway moves it on.
_Avoid_: explicit hold-short (that is the controller's `HS`; this one is the resolver's), partial route

**Implied lead-in**:
An uncleared movement-area taxiway a TAXI to a gate or spot may still drive, because the destination hangs off it: only apron follows it and no more than 1,000 ft of it is driven (`SegmentExpander.MaxImpliedLeadInFt`, docs/ground/pathfinder.md). The readback leaves it out.
_Avoid_: connector (a connector bridges two cleared taxiways), lead-out

**One-way lane**:
A taxiway carried by any span of the airport's sidecar `oneWayEdges`, whatever the span's direction or wake-class exemptions (`OneWayResolver.GetOneWayLaneTaxiways`, docs/ground/pathfinder.md). The only kind of uncleared movement-area taxiway the resolver may imply beyond a lead-in.

**Implied one-way lane**:
One one-way lane a gate or spot extension, a start leg or a ramp-confined route drives uncleared because the one-way rules leave it the only way (SFO `TAXI T A @B2` enters on M1), never past a runway holding position. The readback names it and the controller is told `M1 not in clearance` (`TaxiRoute.ImpliedLanes`, docs/ground/pathfinder.md).
_Avoid_: implied lead-in (a lead-in is the stand's own short approach, left out of the readback)

**Start leg**:
The auto-routed drive over nonmovement pavement from the start to the first cleared taxiway, used only when the hop-limited start bridge cannot reach it: at most 4,000 ft, at most one implied one-way lane, never to a runway holding position (`SegmentExpander.TryAutoRoutedStartLeg`, docs/ground/pathfinder.md).
_Avoid_: bridge (the start bridge is the short hop-limited search tried first), approach leg (the free-space drive to the route's first node)

**Ramp-confined route**:
The route of a plain TAXI naming only a gate or spot, issued inside the ramp: ramp taxilanes, at most one cut across the apron and one implied one-way lane, no other movement-area taxiway; with none the TAXI is refused and TAXIAUTO suggested (`RampLaneReposition.TryPlanRampConfinedRoute`, docs/ground/pathfinder.md).

**Roll-in**:
The last part of a direct stand cut: the apron crossing ends one fuselage length out on the stand's centreline (the approach point), and a second straight leg runs in on the stand heading so the aircraft parks lined up (`RampLaneReposition.RollInApproachNode`, docs/ground/pathfinder.md).
_Avoid_: line-up (a line-up faces a spot along its lane), pull-in

**Object-free half-width**:
How far either side of a movement-area taxiway's centreline an alley push (a push to a spot, stand or node) keeps the aircraft's whole outline, plus a 5 ft margin: 0.7 × the taxiway's design-group span ceiling + 10 ft (AC 150/5300-13B Table 4-1), the group derived from the airport's widest runway and the nearest parallel taxiway (`AirplaneDesignGroups.TaxiwayObjectFreeHalfWidthFt`, `TugTaxiwayClearance`, docs/ground/pushback.md).
_Avoid_: wingtip-clearance floor (that is a hold-short holder's nose-to-centreline minimum), OFA (the full object-free area is twice this)

**Across / alongside**:
The two ways a `PUSH <taxiway>` reaches its taxiway: across (the taxiway crosses the push line, so the tug pushes straight back to it) or alongside (the taxiway runs beside the stand, so the tug turns the aircraft onto its centreline, nose along it) — `TugPlan.TaxiwayApproach`, docs/ground/pushback.md. The readback says which.
_Avoid_: onto (both end on the taxiway)

**Nose-out taxiway**:
The movement-area taxiway a spot's lane joins, which a push or line-up onto the spot faces by default (`AirportGroundLayout.TryGetSpotOutboundTaxiway`); a `PUSH $spot` names it in its RPO note.
_Avoid_: exit taxiway (an exit leaves a runway)

**Straight-then-line**:
The preferred shape of a push off a stand onto a spot: straight back along the stand's lead-in line, a pivot onto the spot's lane timed to land tangent on it, then the pull forward onto the mark (the planner's T0 candidate, docs/ground/pushback.md).
_Avoid_: three-point turn (that turns the nose first, then reverses onto the line)

**Stepped push**:
The fallback push onto a spot's lane when no other shape keeps clear of a taxiway: the push-off, a short push straight, a push turn through part of the pivot, then the lane capture — turning a little at a time instead of swinging the aircraft round (the planner's T4 candidate, docs/ground/pushback.md).

**Overswing**:
A fouling push candidate whose nose swings more than the spot lane's own rotation + 10°, or more than 5° against the lane's turn; the alley clearance drops it while any other candidate remains (docs/ground/pushback.md).

**Swing band**:
The limit on how far a lane push may turn the nose: its running rotation stays within 100° past the turn to the final facing (the requested FACE/TAIL, else the lane's nose-out), or either way round for a requested facing more than 150° away; a candidate outside is dropped while another survives (docs/ground/pushback.md).

**Angled push-off**:
A push off a stand whose first move turns 15°, 30° or 45° with the tail away from a parked neighbour, tried when the straight push-off's shapes come too close to it (docs/ground/pushback.md).

**Extended straight**:
A straight-then-line push whose straight back is lengthened in 20 ft steps so the turn onto the lane happens clear of a parked neighbour, always turning the lane's way (docs/ground/pushback.md).

**Multi-point path**:
The last push shape tried past a parked neighbour: push straight past the lane, push turn toward it, pull forward, push adjust onto the lane, creep pull onto the mark (the planner's T5 candidate, docs/ground/pushback.md).

**Empty-stand footprint**:
The outline an aircraft of the pushing type would occupy parked on an empty neighbouring stand; push shapes that stay out of it rank first (docs/ground/pushback.md).

**Aimed line over a fillet**:
The straight a taxi flies in place of a fillet's curve when a node-aimed entry-alignment arc rolls out pointing at the fillet's far node from off the curve: from where the aircraft stands straight to that node (`GroundNavigator.InstallAimedLineOverFillet`, docs/ground/navigator.md). It survives a snapshot (`GroundNavigatorDto.OnAimedLineOverFillet`).
_Avoid_: lead-in (a lead-in is the along-tangent shortfall before a curve the aircraft is flying)

**Holding distance**:
How far from a runway's centerline its holding position markings sit: the map's `holdShortDistance`, else the width-based default (`RunwayCrossingDetector.HoldShortDistanceForWidth`). An aircraft is clear of the runway only with its tail past a bar at this distance.

**Continuation past a short bar**:
An uninstructed runway exit whose own bar is a dead-end fallback inside the holding distance carries on to the same runway's bar on the joining taxiway, e.g. OAK P → J's 28R bar (docs/landing-and-runway-exit.md).

**Exit capacity segment**:
The stretch of an exit taxiway between a landing runway's exit hold-short and the parallel runway's hold-short, capped by a sidecar `exitCapacity` rule; when it is full an arrival treats that exit as occupied (`ExitCapacitySegment`, docs/landing-and-runway-exit.md).

**Forced rollout**:
The rollout of a CLANDF forced landing: graph exits only at 3–6 kt/s, else a stop short of the runway end, and, once stopped with no exit ahead, a backtrack to the exit behind without asking until any tower or ground command arrives (`PhaseList.ForcedRollout`, docs/landing-and-runway-exit.md).

**Runway-exit backstop**:
What `RunwayExitPhase` does with an aircraft stopped on the runway with no exit ahead: a forced rollout backtracks to the exit behind it; any other aircraft holds, waits for an occupied or full exit, or requests a back-taxi (docs/landing-and-runway-exit.md).

**Pushback hold**:
A scenario-scripted deferred command that would end an active pushback, or that the pushback would reject once one is held, waits until the pushback phase ends (`SimulationEngine.ProcessDeferredDispatches`, docs/ground/pushback.md).

## Airborne following

**Free pursuit**:
A VFR follower trailing its lead along the lead's recorded ground path in `VfrFollowPhase`, rather than flying a pattern leg (docs/approach-and-pattern-geometry.md).

**Pending pursuit**:
A free pursuit armed on a go-around or closed-traffic climb whose follower accepted FOLLOW of a lead with no runway; it starts when the climb hands over to the upwind, and a cancelled follow disarms it (docs/approach-and-pattern-geometry.md).

**Climb-out gate**:
The hold on a pursuit that starts on the departure leg: runway heading and climb until past the departure end and at or above TPA − 300, the upwind's own crosswind-turn rule (docs/approach-and-pattern-geometry.md).

**Excursion (S-turn)**:
A pursuing follower's shallow turn 30° or 45° off the lead's track, to the pattern's outside, to lengthen a gap that is short; capped at an offset from the lead's track (docs/approach-and-pattern-geometry.md).

**Base widen**:
A follower on base flying 30° off its base heading away from the field, down to 1.5 turn radii from the extended centerline, to roll out farther behind its lead (`BaseFollowSpacing`, docs/approach-and-pattern-geometry.md).
_Avoid_: excursion (that is the pursuit's S-turn)

**Behind in sequence**:
A same-runway lead whose remaining path to the threshold is longer than the follower's by more than 0.5 NM (along-final distance, no tolerance, when both are on final); FOLLOW of such a lead is refused (docs/plans/follow-lead-ahead-study.md).

**Lead-ahead gate**:
The command-time checks that refuse a FOLLOW whose lead is not ahead of the follower: behind in sequence, outside the ±60° cone, on the ground, or bound for another airport (docs/plans/follow-lead-ahead-study.md).

**Turn-out**:
A follower level with or ahead of its lead turning to the downwind heading with one call, holding an offset band, and turning base behind the lead once it has passed (`VfrFollowPhase`, docs/approach-and-pattern-geometry.md).

## Live traffic

**Shadow**:
An aircraft spawned from a live real-world feed (SWIM/TAIS) that follows the feed rather than the simulation until someone assumes it (`AircraftState.IsShadow`, docs/live-traffic.md).
_Avoid_: live aircraft (ambiguous with a simulated aircraft in a live session), ghost

**Joined session**:
A direct CRC-hub connection (vEDST, no negotiate id) that has called `JoinSession` on a same-CID CRC session, its **primary**: it reads the primary's room, position and ERAM sector, registers no position of its own, and may call only an allowlist of hub methods (docs/crc-display-state.md).

## ERAM commands

**Message type descriptor**:
The short name of an ERAM command variant that CRC shows under `ACCEPT` when the command succeeds, e.g. `INTERIM ALT` for `QQ` (docs/eram/README.md).
_Avoid_: readback, echo

**FLID**:
Flight identification: the field of an ERAM command that names the aircraft, as a callsign, a computer ID (CID), a beacon code, or a click on the track.

**Implied command**:
An ERAM entry typed without a command ID, such as `<FLID>` alone to accept a handoff or `<sector> <FLID>` to start one; spelled out, it is `QN` (or `QZ`).

**Cofie**:
"Contents of field in error": the text of the field an ERAM error is about, placed in front of the error, as in `AB12 FORMAT`.

**`/OK`**:
The ERAM logic-check override: added to a command, it lets the command act on a track the sector doesn't own, or past a check it would otherwise fail.

**SRS**:
The FAA's ERAM EDSM software requirements specification (Vol 1 Book 2, the appendices) that `docs/eram/` (its [rulings.md](docs/eram/rulings.md) above all) cites by section and printed page; the maintainer keeps the PDF outside the repo.

**FDB / LDB**:
Full data block / limited data block: the two ERAM data block formats a sector sees for a track. An LDB is paired (the track has a flight plan) or unpaired.

**CERA**:
Controller-entered reported altitude: an altitude the controller types (`QR`, or `QQ R`) as the aircraft's reported altitude. It outranks Mode C in Field B/C and shows with a `#` until deleted.

**Field B / Field E**:
Data block fields named by the SRS: Field B is the assigned or interim altitude with the vertical-status character, Field E the time-shared slot for handoff/point-out sector, ground speed, destination and special codes.

**MCI**:
Mode C intruder: an untracked target reporting Mode C altitude, drawn with its own symbol and eligible for conflict alerts against tracked IFR aircraft.

## Radar display

**YAAT Scope**:
The planned instructor radar view that mixes STARS and ERAM presentation (docs/plans/yaat-scope/README.md); it replaces today's Radar view once it reaches parity.
_Avoid_: scope alone (the router's **Scope** is a different thing)

**Profile**:
A YAAT Scope display file (STARS, ERAM or Mixed) that sets the datablock template, the altitude notation, the zoom bands and the toolbar; a pref set is the same shape with one user's values.

**Zoom band**:
A range interval in a profile (e.g. up to 30 NM, 30–120 NM, beyond) that sets defaults such as other controllers' block level, vector length and history count; a manual change pins the value until AUTO.

**Swept position**:
An aircraft's ERAM position as of its last 12 s sweep (`EramSweptPosition` in yaat-server's `AircraftChangeTracker`): what CRC's ERAM display shows, and where a coverage coast and a QT without a location start from, rather than the live position.

**Dwell**:
ERAM's hover emphasis on a data block; in YAAT Scope, hovering expands the block in place, and clicking the callsign locks it open.

**Pilot lens**:
YAAT Scope overlays drawn from simulation truth rather than from what the controller's system shows: the aircraft's intended path, queued pilot commands, actual check-in state. Off by default.

**Scope entry**:
Data a controller types into their display's automation (ERAM `QQ`, a STARS scratchpad): it changes what the displays show and never moves the aircraft, unlike a pilot command.

**Situation**:
A named bucket of aircraft phases and state (Taxiing, Holding short, Final, IFR arrival…) that picks an aircraft menu's quick commands.
_Avoid_: phase (one situation spans several phases)

**Quick commands**:
The short, user-editable list of commands an aircraft's right-click menu shows first for its current situation; the rest sit under All Commands.

**Catalog entry (menu catalog)**:
One command the aircraft menus can offer, with a stable ID (`<group>.<item>`, never reused), a label, a default flight-rules filter, an applicability predicate and a builder (`MenuCatalogEntry` in `Yaat.Client.Core/ContextMenus/`); quick commands are lists of these IDs.

**Menu host**:
The surface that owns an aircraft menu (radar, ground, aircraft list) as a catalog entry's builder sees it (`IMenuHost`): sending the command text, and whatever popups or reads the entry needs.

**Host capability (menu host)**:
A family of menu-host members a surface serves (`MenuHostCapabilities`: input popup, list picker, warp, ground movement…); a catalog entry names the capabilities it needs (`Requires`) and is hidden on a host that lacks any of them, never disabled.

## CRC hub connections

**Direct connection**:
A CRC hub socket identified by its own YAAT `access_token` (vEDST, TowerCab 3D) rather than by a CRC negotiate: it never joins a room, the lobby or attendance, and reaches a session only through `JoinSession` with a primary of the same CID (docs/vatsim-auth.md).

**Primary**:
The CRC connection whose session a direct connection joins; the joiner reads the primary's room.

**Negotiated joiner**:
A direct connection that also negotiated first (TowerCab 3D): it keeps its negotiate id as its connection token, so it can register for UDP entity updates, while its CID comes from its access token.

## Tooling

**Golden (menu golden)**:
A committed text snapshot of an aircraft right-click menu for one view and one situation fixture (`tests/Yaat.Client.UI.Tests/Goldens/menu/{radar,ground,list}/<fixture>.txt`, written by `MenuTreeSnapshot`); `MenuGoldenTests` fails when a menu differs from its golden, and `YAAT_MENU_GOLDEN_REGENERATE=1` rewrites them.

**Gate**:
`tools/gate.ps1` (a copy of the canonical `~/.claude/tools/gate/gate.ps1`, called as `pwsh tools/gate.ps1 -Log <log> -TimeoutSeconds <seconds> -Slot heavy|light -- <command...>` from PowerShell or Bash): runs one build or test command with its whole output in a log under `.tmp/`, the tail on screen and the command's own exit status, and kills it (exit 124) when it stalls, passes its ceiling on the load-adjusted clock, or reaches the backstop.

**Stall**:
A gate run whose log has not grown and whose processes (with any MSBuild or compiler server started during the run) have used no CPU for 120 s (`-StallSeconds`); the gate kills it as hung (`gate: STALLED`).

**Load-adjusted clock**:
The clock a gate's ceiling (the `-TimeoutSeconds` of `tools/gate.ps1`) counts on: every few seconds it advances by the share of the machine other work left free, so it keeps wall time on an idle machine and slows while other agents load it.

**Backstop**:
The gate's last-resort kill at five times the ceiling in wall time (`gate: BACKSTOP`); with a low "machine free" figure the machine was busy, and the run is re-run once alone.

**Slot**:
A place machine-wide a gate must hold to run, of the kind its `-Slot` names, from one of two pools that never wait on each other: heavy slots and light slots. A gate that finds every slot of its kind held logs `gate: waiting for a heavy slot` (or `... light slot`) and waits; a gate inside another gate takes none and uses its parent's.

**Heavy slot**:
One of the `(logical processors - 1) / 4` slots (`heavy` in `%LOCALAPPDATA%\gate\slot-counts.json` overrides the count, live) for a gate whose command keeps many threads busy: a build, a `dotnet test` or `dotnet run` that builds first, `test-all.ps1`. Every yaat gate call is heavy.

**Light slot**:
One of the `(logical processors - 1) / 2` slots (`light` in `%LOCALAPPDATA%\gate\slot-counts.json` overrides the count, live) for a gate whose command keeps one or two threads busy: a `dotnet test --no-build` filtered to one class, a small script.

**Feature marker**:
`branch: feat/<name>` on a `docs/plans/MAIN.md` line; every item under that line lands on the `feat/<name>` branch (in yaat and yaat-server) instead of `main` (user-level `nextup`, §3 "Feature branches").

**Feature PR**:
The draft pull request from a marker's `feat/<name>` into `main`, one per repo, opened with the marker; CI runs on each push to it, and `/ship` Phase 2F merges it with `--rebase` once every line under the marker is done.

**Ouroboros**:
A synthetic round trip through the speech pipeline: a known canonical command is rendered to speech with Piper, fed through Whisper, the rule mapper and the LLM fallback, and the recovered canonical is compared with the one it started from. `--ouroboros` speaks pilot readbacks; `--atc-ouroboros` speaks controller transmissions across every phraseology rule family and diffs each family's pass rate against a committed baseline (`tools/Yaat.SpeechSandbox`, docs/speech-recognition-pipeline.md).

**Speech telemetry**:
Push-to-talk samples (audio, per-stage transcripts, scenario context) that opted-in users' clients upload to the official yaat-server, which stores them for developers to pull with `tools/speech_telemetry.py` (docs/speech-recognition-pipeline.md).
