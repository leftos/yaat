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
The airport layouts and ARTCC configs a room's scenario load used, held on the room so a restart, rewind, export or session restore rebuilds the simulation from the same data rather than the live vNAS caches. It is replaced only by the next scenario or recording load, and cleared by an unload. A recording archive load or a session restore seeds it from the archive's bundled layouts and missing-map airports, and reads only the airports the archive lacks from the live caches.
_Avoid_: snapshot (that is the simulation state, not its reference data)

**Bundled layout**:
An airport's parsed ground layout, with the source GeoJSON it was parsed from, written into a recording archive or a room checkpoint (`layouts/`, `airport-geojson/`, keyed by the layout's own airport ID). Every airport the room pinned is bundled, whether or not an aircraft referenced it, so a replay or restore runs on the graph its snapshots were taken on.

**Missing-map airport**:
An airport the room pinned with no ground map (vNAS had none for it, or its map could not be fetched or parsed), listed by FAA code in an archive's `MissingLayoutAirportIds` so a load pins it with no map too instead of fetching one live.

**Layout format version**:
The format an archive's bundled layouts were written in (`LayoutFormatVersion`, `RecordingArchive.CurrentLayoutFormatVersion`). A load reads bundled layouts only in the current format; in an older or newer one, or none recorded, those airports are pinned from the live layouts, while its missing-map airports are still kept.

**Resource manifest**:
What a scenario JSON will make the server fetch — its ARTCC, the roster's neighbouring ARTCCs and every airport it names — read without loading anything (`ScenarioResourceManifest`).

**Map-required airport**:
An airport in the resource manifest whose full ground map the load needs: the primary airport, and each airport an aircraft parks at or spawns on the ground at. A missing map there is a load warning; elsewhere it is not.

**Active end** (of a runway):
The end of a runway the room's active runways list; a pilot names a runway by its active end (the nearer when both are listed, the nearest end when neither is), except a runway the aircraft landed on or departs from, which keeps its assigned end (`RunwayCrossingEnd.Spoken`). The terminal messages, the "Holding Short 1R" phase name and status line, the warnings, the CROSS menu item and the AI Ground controller's CROSS name a runway the same way, and a typed end is echoed as typed.

**Active runways**:
The runway ends a room is using, per airport, each for departures, arrivals or both (`ActiveRunways`): room state, not the controller AI's single runway-in-use guess.

**Carried answer** (of the active runways):
The mentor's answer to the load prompt, or the last live `ARWY`, kept on the room per scenario (an answer of `NONE` is an empty list). A load of the same scenario starts on it; a restart starts on the list in use, and a rewind on what the session started on (`InitialActiveRunways`).

**ScenarioAnswers**:
The room's store of per-scenario answers (`TrainingRoom.ScenarioAnswers`): the carried active runways and the lowest position worked, each keyed by normalized scenario id; only a load reads them.

**Implied active runways**:
The runway ends a loaded scenario implies per airport, read from its aircraft (loaded phases, expected approach, parsed preset commands, never dispatched) and its arrival generators, else the primary airport's facility knowledge (`ImpliedActiveRunways.For`). With no sidecar naming runways it pre-fills the mentor's prompt; the room takes it only where it names every runway.

**Towered airport**:
An airport with a tower facility (`Atct`, `AtctTracon`, `AtctRapcon`) in the room's own ARTCC config, a scenario roster ARTCC's config or a geographic neighbour ARTCC's config (`ToweredAirports`); every other airport is untowered. An id matches in either spelling (`OAK`/`KOAK`) and, outside the CONUS, through the navigation data's FAA/ICAO pairing (`ANC`/`PANC`). It decides whether an airport-level `CVA` needs a runway, and rides the recording as `towered-airports.json.br`.

**Scenario sidecar**:
An authored per-scenario JSON file, `Data/ARTCCs/{ARTCC}/Scenarios/{scenario id}.json`, carrying settings the vNAS scenario lacks (its active runways, the lowest position a solo student works); unlike the per-airport ground sidecar.

**Pre-fill** (of the load prompt):
The implied active runways shown in the session-setup prompt's runway rows (`ActiveRunwaysSetupDto.Prefill`) for the mentor to edit or accept; a guess, not the room's list until OK sends it.

**Session-setup prompt**:
The questions a scenario load, or switching solo training on, puts to its client in the "Session setup" popup (`SessionSetupPromptDto`): the mentor's active runways and the solo student's positions it works. A section is absent when it does not apply or is already settled.

**Preset**:
A command the scenario gives one aircraft (`presetCommands`), dispatched at load or at its `timeOffset`, and scripted rather than spoken by the student: it does not count as the student's contact with the pilot.

**Runway spawn**:
An aircraft the scenario starts lined up on a runway (`OnRunway`), as opposed to a ground spawn at a stand or on a taxiway.
_Avoid_: runway departure (any departure ends up on a runway), on-runway aircraft

**Spawns win whole**:
At an airport that any loaded aircraft gives a runway, the implied active runways are exactly the ends those aircraft use; facility knowledge and the generic rule are not consulted there.

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

**RPO room**:
Any room not in solo training (`!SoloTrainingMode`), an instructor room with no pilot operator included: a person, not the sim, launches its departures and answers for its pilots.
_Avoid_: instructor room (as a contrast to RPO room; the code draws no line between them)

**RPO-only command**:
A command only a pilot operator may give, because it makes a pilot do something no controller instruction can, such as
FOLLOWF, CVAF, RFISF, RTISF or CLANDF. Solo training refuses them unless the scenario's recorded `SoloRpoCommandsAllowed`
flag is set (`DispatchContext.RefusesRpoOnly`).

**Kind**:
What sort of action a text is, decided once by the router before anything runs. Every text has exactly
one kind; a text with none is an error, never a default.
_Avoid_: category, verb type

**Arm**:
The router's body for one kind: either simulation logic, or a slot the host fills because the state
is still the host's.
_Avoid_: handler, branch, case

**Armed report**:
A pilot report the controller has requested (`REPORT` a pattern leg, a mile final, or a fix) that the sim holds until the pilot makes it or it is cancelled. Carried on the wire as `ArmedReports`.

**Scope**:
What the router resolves before an arm runs — nothing, a callsign, a present aircraft, or the acting
position. A property of the kind, not of the recorded text.
_Avoid_: target, addressee

**Sent without a selection**:
A typed verb the desktop client sends with an empty callsign whatever aircraft is selected, because the room or the issuing position (or, for GHOST and TIMER, its own argument) addresses it: `CommandScopes.SendsWithoutSelection`. A malformed one is refused with the parser's reason, never as a missing aircraft.
_Avoid_: global command (`CommandDefinition.IsGlobal` is a different question: the pilot never answers Unable)

**Speed stamp**:
The transmission id (`NavigationTarget.SpeedTransmissionId`) of the transmission that set a fix's crossing speed: a CFIX carrying a speed, a constrained DCTF's speed constraint, or a `DVIA SPD n FIX`. Null when the speed is the procedure's or there is none, so an altitude-only CFIX leaves it alone. A DVIA, CVIA or approach clearance in the same transmission keeps the speed; a later one gives the fix its published speed back, and crossing a stamped fix is never remembered as a published speed. The fix's crossing altitude has its own stamp, `CrossingTransmissionId`.
_Avoid_: crossing stamp (that one answers for the altitude only)

**Top altitude** (of a SID):
The altitude a bare `CVIA` climbs to, held in `AircraftProcedure.ClimbViaTargetFt`: the SID's ARTCC TDLS "EXC MAINT" altitude, else its TDLS initial altitude, else the highest altitude the SID publishes on the legs flown (CIFP does not code the charted top). `CVIA nnn` names it directly. An ATC-assigned one (`CVIA nnn`, a TDLS EXC MAINT) is what the check-in reports ("for nnn").
_Avoid_: ceiling (`SidViaCeiling` is only the assigned "except maintain" altitude, and a restriction ahead can lower the target below the top)

**Transmission id**:
The per-aircraft number (`CommandQueue.LastTransmissionId`) one transmission takes when it carries a CFIX, a constrained DCTF or a `DVIA SPD n FIX`, shared by every block that transmission queues and stamped on the fixes it constrains. A descend via compares it to tell a crossing restated in its own transmission, which it keeps, from an earlier one, which it supersedes. A DVIA or CVIA stamps it on the aircraft (`AircraftProcedure.StarViaTransmissionId` / `ClimbViaTransmissionId`) so a CFIX of the same transmission knows the via clearance has applied. Other transmissions take none.
_Avoid_: command id, sequence number (a transmission is the whole `;`/`,` text sent to one aircraft at once)

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

**Break-off** (of a follow):
A `HOLD` given to a `FOLLOWG` follower ends the follow: the aircraft brakes to rest and holds in position, and `RES` does not resume the follow. A follower inside a runway's holding position marking holds on the crossing it was cleared across and finishes it at rest; one that comes to rest with its tail still inside a runway reports not clear of it (`FollowingPhase.BreakOff`, `HoldingInPositionPhase.AfterFollowBreakOff`, docs/ground/navigator.md).

**Full-length band**:
The runway's hold-short bars within 200 ft along-track of the one nearest the requested threshold; the bars `RouteMaterialiser.OrderFullLengthLineupBars` treats as full-length lineup points, ranked by the aircraft's side of the runway (docs/ground/pathfinder.md, "Full-length lineup bar order").

**Given-up exit**:
A taxiway a landing aircraft has said "unable" to: it is skipped by every exit search while the aircraft rolls, for the rest of that landing, until an accepted `EXIT` names it again; once stopped on the runway the aircraft may still taxi to it (`PhaseList.GivenUpExitTaxiways`, docs/landing-and-runway-exit.md).
_Avoid_: missed exit (an exit passed without an instruction is not given up)

**Initial call-up**:
The first call a spawned aircraft makes on its own to get moving: a ground spawn's "ready to taxi" (or, for an uncleared IFR departure, its clearance request to the lowest position the student works), or an untowered runway spawn's release request to a radar student. Whether and when an aircraft makes it is decided once, at scenario load, as its `InitialCallupPlan` (`InitialCallupClassifier`, docs/solo-training-pilot-speech.md); an aircraft the loader did not arm never makes one.
_Avoid_: check-in (a check-in is the call on a frequency change), initial contact (an airborne check-in still follows a release request)

**Intersection departure**:
A takeoff from a taxiway's hold line on the runway, short of the full runway length, in place of the runway end's entry. A `CTO` given while holding short of a taxiway makes one: the aircraft is re-cleared onto that taxiway to the runway's hold line on it, and the readback names it ("cleared for takeoff runway 28R at echo"). A taxiway that does not reach the assigned runway keeps the hold (`TaxiwayHoldTakeoffOutcome`). Classified per runway end by `RunwayEntryPoint`.
_Avoid_: intersection takeoff (the AIM's term for the same departure; the code and docs say departure)

**Pre-clear**:
A clearance for a hold short still ahead of a rolling aircraft, set before it reaches the bar so it taxis through without stopping: `CROSS` for a runway crossing, a bare `RES` for the next taxiway or spot bar (`GroundCommandHandler.TryPreClearNextTaxiwayHoldShort`, docs/command-handlers.md). Only the one bar named is cleared; later bars stay armed.
_Avoid_: release (a release lifts a hold the aircraft is already stopped at)

**Spot line-up**:
The route a `TAXI … $spot` from the ramp is re-planned into: across the apron, a ~90° turn onto the spot's lane on the ramp side, and a slow pull onto the mark facing the movement-area taxiway the lane joins (`RampLaneReposition.TryPlanSpotLineUp`, docs/ground/pathfinder.md).
_Avoid_: ramp cut (a ramp cut is a free-space shortcut between lanes; a line-up decides which way the aircraft ends facing)

**Stored taxi route**:
The route a `TAXI` or `TAXIAUTO` given on the landing rollout keeps behind the instructed exit: held on `PhaseList.TaxiRoute` from acceptance, shown to the controller at once, and started when `RunwayExitPhase` completes the exit, or dropped (a go-around, a given-up or replaced exit, a stop with no exit ahead). The active route stays `Ground.AssignedTaxiRoute` (`GroundCommandHandler.RolloutTaxi.cs`, docs/landing-and-runway-exit.md).
_Avoid_: queued taxi (a queued `;` block waits for the exit to finish and routes from the hold-short)

**Tail-clear point**:
Where a runway exit or `CLRWY` stops the aircraft's reference point past the vacated runway's hold-short bar: half its length plus 10 ft along the graph, so every part is past the holding position marking (`RunwayTailClearance.PastBarFt`, docs/landing-and-runway-exit.md). Cut back to short of another runway's bar when a crossover is too short.

**Tail-inside**:
Stopped or moving with the tail still inside the vacated runway's holding position marking, because the exit's stop was cut back at another runway's bar: the aircraft is not clear of the runway and stays an occupant of it (`RunwayTailClearance.Stop.TailInsideLine`, `RunwayOccupancy.IsTailInsideRunwayState`, docs/landing-and-runway-exit.md "Crossovers too short to clear").
_Avoid_: stopped inside the holding distance (that is a named exit whose own bar is short of the standoff)

**Wingtip-clearance floor**:
The least distance from a taxiway hold-short holder's nose to the centreline of the taxiway it holds short of: the half-span of the widest aircraft the airport can take, judged by its widest runway, plus 25 ft (`HoldShortAnnotator.WingtipClearanceFloorFt`, docs/ground/hold-short-placement.md).
_Avoid_: setback (the setback is where the aircraft's centre stops; the floor is a minimum the nose must keep)

**Route-incomplete hold**:
Where a TAXI whose taxiways do not reach its destination ends: the aircraft taxis the route as issued and holds short of the one taxiway the route still needs, and the controller is told which (`HoldShortReason.RouteIncomplete`, docs/ground/pathfinder.md). Only a new TAXI that includes that taxiway moves it on. A TAXI with no destination that meets its last taxiway ends the same way, held short of that taxiway rather than in the intersection (`HoldShortPoint.EndsNoDestinationRoute`, docs/ground/hold-short-placement.md); `RES` and `CROSS` do not release it.
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

**Stand lead-in**:
A sidecar `standLeadIns` entry naming the taxiway vertex a parking stand's connector joins, for a stand the vNAS map draws no lead-in line for; it replaces the stand's nearest-edge connector at layout build and its vertex is the foot the stand's `StandDeparture` is classified from (SFO D12 joins A). A different thing from the implied lead-in, which is a route's uncleared approach taxiway.
_Avoid_: implied lead-in

**StandDeparture**:
Whether a stand is left by a push back, by taxiing out, or either (`PushBack` / `TaxiOut` / `Either`). The layout build stores the geometric answer on each parking node; an airport sidecar's `standDeparture` map overrides it by stand name and its `standDepartureAreas` rules by runway side; `StandDepartures.StandDepartureOf` is the one place it is read.

**Either stand**:
A stand where a tug push and a normal taxi-out are both normal (`StandDeparture.Either`): it gets precomputed push targets, a push off it carries no taxi-out RPO note, the client keeps the push entries, and the pilot AI still answers ready-to-taxi with TAXIAUTO.
_Avoid_: taxi-out stand (that one hides the push entries)

**Stand-departure area rule**:
A sidecar `standDepartureAreas` entry that classifies every stand on one side (left or right of the nose) of a runway end's extended centerline through its landing threshold, the first matching rule winning; a per-name `standDeparture` beats it and the layout's geometry yields to it (KOAK: right of 28R is the North Field, `TaxiOut`).

**Taxi-out stand**:
A stand whose aircraft park facing their way out, so they leave under their own power and get no precomputed push targets: its lead-in edge runs out within 90° of the stand's heading, or the sidecar says `TaxiOut`.
_Avoid_: power-out stand (a power-back is a different thing)

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

**Mid-edge start**:
Where a TAXI issued to an aircraft standing on a straight taxiway edge begins: the route is planned from both ends of the occupied edge and the one that does not drive back over it is kept, the end ahead winning ties; an aircraft on a fillet arc starts at the arc's end ahead (docs/ground/pathfinder.md).

**Turn about (from the far end / in place)**:
A taxi route that opens by reversing the aircraft on the taxiway edge it stands mid-way along, toward that edge's far node (`TaxiRoute.TurnAboutShape` / `TurnAboutTargetNodeId`; while unflown, `TaxiTurnAboutShape` / `TaxiTurnAboutTargetNodeId` on the training hub, which the ground view's overlay draws as sent). A type whose gear fits makes it on any clearance; one that does not refuses it on a controller's clearance, and on a scripted one makes it only when no route ahead resolves.
*From the far end* (`FromFarEnd`): the route re-planned from the far node won, with or without a free-space leg back to that node.
*In place* (`InPlace`): the route from the node ahead was kept, and its first segment drives the occupied edge backwards.
_Avoid_: U-turn (a U-turn reverses over a junction's fillets, not along the taxiway)

**Turn-about jog**:
The short arc that opens a turn about on a taxiway, turned against the reversal's sense at the type's turn-about radius (60° for an aircraft on the centreline), so that the reversal arc after it is centred on the centreline and the whole turn stays within about one radius either side of it (`PathPrimitiveBuilder.TurnAboutJogDeg`, docs/ground/navigator.md). A helicopter takes none, and nor does a reversal re-aimed past the bend.
_Avoid_: S-turn, offset (the jog is a single arc, not a lateral shift)

**Turn-entry point**:
The point a standard runway exit's turn begins, short of its branch point by R·tan(min(Δ, 120°)/2) for the category's main-gear turn radius R and the exit angle Δ (`RolloutBraking.TurnEntryLeadNm`). The rollout judges, plans, hands off and declares an exit missed against it; a high-speed exit has none and is judged at the branch.

**Offset line** (also *roll-out hold*):
The straight after a turn about's reversal, held on the taxiway edge's bearing the reversal rolled out on, a turning radius off the centreline (a diameter for a helicopter) on the inside of the turn the route makes at the node, to abeam that node, instead of steering back out to the centreline first.
`GroundNavigator.HoldsRollOutBearing` decides it and `TryLayTurnAboutRollOutLine` lays it (docs/ground/navigator.md). Among its conditions: a straight of at most six turning radii, no bar at the node, and the main gear inside the type's TDG half-width. The node turn from it is fitted to the offset so it exits on the outgoing centreline.

**Offset runway crossing**:
Two cleared taxiways with no junction of their own that both join one runway's centreline, from opposite sides, at most `MaxOffsetRunwayCrossingFt` (300 ft, a sim convention) apart along it, neither joining the runway at less than 60° off its line and neither join already having a taxiway straight across (OAK D and F across 15/33). `TAXI D F` routes D, across the runway, onto F as one crossing held short at D's bar (`SegmentExpander.TryRouteAcrossOffsetRunway`, docs/ground/pathfinder.md).
_Avoid_: runway taxi, detour (a detour is the connector bridge the crossing replaces)

**Operation** (solo scoring):
A takeoff or landing the solo evaluator's runway tracker detects and judges: a departure's roll start, or a landing's threshold crossing (a touch-and-go, stop-and-go, option or low approach included). Not the public `Training.OperationKind` debrief label (docs/solo-training-evaluation.md).

**Re-aim past the bend**:
A turn about's reversal aimed, with no jog, at a node on the leg out of the bend at its aim node, instead of turning about to that node, when the bend runs back against the reversal by more than 90° and the cut keeps the main gear within the type's TDG half-width of the two taxiways' centrelines (`GroundNavigator.ReAimPastTheBend`, docs/ground/navigator.md). Never past a bar.

**Square stop line**:
The straight after a turn about laid on the taxiway's centreline through a stop, from abeam the aircraft or from abeam the offset line's start when a hold short is issued on that line, and steered in its last look-ahead window at a point past the stop, so the aircraft stops on the centreline square to the bar (`GroundNavigator.LayStraightSquareToStop`, docs/ground/navigator.md).

**Stitched join** (of an offset runway crossing):
A taxiway's join of a runway centreline through a (taxiway end node, centreline node) pair linked by a `RWY…:link` edge (CMH E4 and F3 over 10L/28R), against a direct join where the taxiway edge meets the centreline itself; the 300 ft is measured between the two centreline nodes, side and obliqueness on the taxiway's own edge (`SegmentExpander.StitchedRunwayJoins`, docs/ground/pathfinder.md).

**Taxiway design group (TDG)**:
The FAA's grouping of aircraft by main-gear width and cockpit-to-main-gear distance (1A, 1B, 2A, 2B, 3–6), read from the FAA aircraft characteristics database; AC 150/5300-13B Table 4-2 sets each group's taxiway width, of which half (12.5 ft for 1A/1B, 17.5 for 2A/2B, 25 for 3/4, 37.5 for 5/6) is the pavement a turn about is checked against. The ground layout carries no TDG, so a taxiway is assumed to be of the aircraft's own group.

**Gear fit** (for a turn about):
Whether a type has room to turn about on a taxiway of its own TDG (`TurnAboutFit.Evaluate`, docs/ground/pathfinder.md): its pivot radius is R = max(MGW/2, 0.466 × wheelbase), MGW the main-gear width, and it fits when its outer main tyre (R + MGW/2) and its nose gear (√(R² + wheelbase²)) both stay within its TDG half-width. With either figure missing only a TDG 1A non-jet fits; a type with no record fits only as a piston or helicopter.
A non-jet whose wheelbase is 0.48 to 0.9 of its length is a taildragger (the FAA record marks no tailwheel; its wheelbase runs main gear to tailwheel): it pivots on a braked main wheel, R = MGW/2, and fits when its tail-swing radius √(wheelbase² + (MGW/2)²) plus MGW is within its taxiway's width. A non-jet whose wheelbase is 0.9 of its length or more has an unusable record and is judged by its category. A PA18, C120 or C140 fits; a C180, DHC2 or wide-track twin (PA31, C402) does not.
A type that does not fit refuses a controller's TAXI that would need a turn about, in either shape, at any heading: "Unable, no room to turn around on C, request a route ahead". A C172 or C208 fits; a C25A, AT76, B738, DH8D or CRJ2 does not.
_Avoid_: lined-up jet (neither the heading nor the category decides it)

**Fouler / fouling**:
An aircraft on the ground off a runway's pavement with a body part past that runway's hold line, so the runway is not clear for a departure's roll or an arrival's threshold crossing (`RunwayUseKind.Fouling`, docs/conflict-and-visual-detection.md). An approaching aircraft fouls with its nose more than 5 ft past the line; a leaving one fouls only when stopped or restricted with its tail still inside. A fouler is not on the runway: it draws an advisory, and makes an arrival go around only close to the centreline. The solo scorer counts it as a runway user at a takeoff's roll start or a landing's threshold crossing.

**Hold bar** (ASDE-X):
A safety-logic line CRC draws at a runway's edge across a taxiway entrance or a crossing runway, lit (Active) while the runway is in use and hidden (Suppressed) otherwise (`AsdexHoldBar`, docs/crc-display-state.md). Not the painted hold-short node of the ground layout: see *Hold-short node*.

**Arrival-alert inhibit** (of a tower position, ASDE-X):
A tower position a CRC display marked so the facility's arrival alerts are inhibited for it. It lapses 180 sim seconds after it was set and clears when the facility's runway configuration changes; it suppresses no alert (`AsdexSafetyLogicConfig.InhibitStartSeconds`, docs/crc-display-state.md).

**Superseded configuration** (ASDE-X runway configuration):
A runway configuration id an `ARWY` re-pick moved a facility away from, remembered for 10 sim seconds. A CRC push naming it inside that window comes from a display that has not yet seen the re-pick, so it is not a choice (`AsdexSafetyLogicConfig.SupersededConfigurationIds`).

**Hold line**:
The runway holding position marking (AIM 2-3-5.a.1). The code's "bar" is its graph node, a `RunwayHoldShort` node (`RunwayHoldBars`).

**Hold-line footprint**:
The body an aircraft carries past a runway's hold line for the turn-about check: nose and tail half its length ahead of and behind the main-gear position, both wingtips half its span abeam (`AircraftBody`, `HoldLineFootprint`). A part is past a line on the runway's pavement, or nearer its centreline than the runway's bars within 200 ft along the runway of one of them.
A turn about, from rest or rolling, that would carry any part past a line is not flown: the aircraft holds short and says "unable, no room to turn around on C, holding short of runway 28R" (docs/ground/navigator.md).

**Hold-short node** (also *hold-short bar*):
The ground layout's node at a runway's painted hold line (`RunwayHoldShort`, `RunwayHoldBars`), where a taxiing aircraft stops short. It is layout geometry, always there; the ASDE-X *Hold bar* is a separate synthesised line, one per such node, that lights only while the runway is in use.

**Holding distance**:
How far from a runway's centerline its holding position markings sit: its base separation S (*Standoff*), `GroundRunway.HoldShortStandoffFt`. An aircraft is clear of the runway only with its tail past a bar at this distance.

**Paint-paired bar**:
A runway hold-short node seated on the painted hold bar the tower-cab videomap draws on its taxiway, at that bar's distance from the centerline (D_paint) instead of the rule's D (`RunwayPaintPairing`, `GroundNode.PaintSegmentIndex`). It is always its runway's hold line, never a short bar (docs/ground/hold-short-placement.md).

**Runway rank**:
Who gives way to whom across a runway in the ground-conflict pass (`GroundConflictDetector.ComputeRank`): a runway user (taking off, landing, rolling out, lined up on the pavement) outranks an aircraft vacating the runway, which outranks everything else; the lower rank yields (7110.65 3-1-3, AIM 4-3-21.a).

**Runway user**:
An aircraft taking off, landing, rolling out, lined up on the pavement, or crossing it: the one that outranks an aircraft vacating the runway in the ground-conflict pass (`RunwayRank.RunwayUser`). The solo scorer also counts one on, crossing or past the hold line of a runway ahead of another's takeoff roll or landing threshold crossing as a "Runway not clear" finding (docs/solo-training-evaluation.md).

**Short bar**:
A runway hold-short node nearer the centreline than the runway's holding distance, a dead-end fallback where the taxiway ends or merges before reaching it (`GroundNode.IsShortBar` true, set by `RunwayCrossingDetector.MarkShortBars`); the runway's hold line is the node at S or D (docs/ground/hold-short-placement.md). A route that passes the holding distance without a bar keeps the short bar.

**Snap band**:
How far from the rule's centerline standoff D a painted bar's snap point may sit for the bar to pair with a crossing: 60 ft inside to 100 ft outside, and at least the runway's half-width plus 50 ft from the centerline (`RunwayPaintPairing.InBand`). A bar outside it is not that crossing's hold line (docs/ground/hold-short-placement.md).

**Standoff (S, D)**:
The hold-short placement rule (`HoldShortStandoff`). **S** is a runway's base separation: feet from its centreline to the nearest point of the hold line, from the authored `holdShortDistance` or the rule by width, length, ILS category and elevation. **D** is where the taxiway's centreline meets that line, `D = S + (W_twy / 2) · |cos θ|` for a taxiway at angle θ to the runway, so an angled taxiway's bar sits farther out than S (docs/ground/hold-short-placement.md).

**Vacating** (a runway):
The pilot sense, not the controller's: an aircraft on the ground leaving a runway whose tail is still over the strict paved half-width or inside the hold line (`RunwayOccupancy.IsVacating`), moving or stopped, until the tail is out. Moving, it has right of way over taxiing traffic; held, only against traffic coming head-on into its way out.

**Bridge progress** (of a detour connector):
What a connector search carries along a partial route so each bridge rule can judge the next move: the edge it arrived on, the feet used of the start runway, and whether it has touched the transition's first taxiway (`BridgeProgress` on `PartialRoute`). The search and the post-hoc filter judge with the same progress, and the search's closed-set key includes it.

**Continuation past a short bar**:
An uninstructed runway exit whose own bar is a dead-end fallback inside the holding distance carries on to the same runway's bar on the joining taxiway, e.g. OAK P → J's 28R bar (docs/landing-and-runway-exit.md).

**Exit ahead**:
A named exit the arrival can make, as the Exit left / Exit right flyouts list it (`ExitAheadDto`, the `ExitsAhead` field): a taxiway on a side that an `EL <twy>` / `ER <twy>` would be accepted for, with its distance rounded to 100 ft and a flag for the exit it means to take. Computed on the rollout and, as a forecast from the landing threshold, on the last miles of final (docs/landing-and-runway-exit.md).

**Exit capacity segment**:
The stretch of an exit taxiway between a landing runway's exit hold-short and the parallel runway's hold-short, capped by a sidecar `exitCapacity` rule; when it is full an arrival treats that exit as occupied (`ExitCapacitySegment`, docs/landing-and-runway-exit.md). A crossover segment is one such stretch shared by both runways' arrivals: either hold-short is an exit bar, and aircraft in both directions count against it.

**Arrival class**:
The group an arrival falls in for a sidecar `exitDirections` entry's `arrivalClass`: `ga` (a registration callsign, or an operator in the sidecar's `gaOperators`, which also pools it as non-gate for arrival parking) or `cargo` (an operator in the sidecar's `cargoOperators`); cargo is judged first (`ArrivalExitSide.Classify`).

**Parking remark**:
A flight-plan remark naming where an arrival parks (`/V/ GATE F14, 28R`, `/V/ PARKING SIGNATURE, 28R`); its stand, or a sidecar `parkingAreas` area, sets the arrival's default exit side ahead of its arrival class (`FlightPlanParking`).

**Forced rollout**:
The rollout of a CLANDF forced landing: graph exits only at 3–6 kt/s, else a stop short of the runway end, and, once stopped with no exit ahead, a backtrack to the exit behind without asking until any tower or ground command arrives (`PhaseList.ForcedRollout`, docs/landing-and-runway-exit.md).

**Runway-exit backstop**:
What `RunwayExitPhase` does with an aircraft stopped on the runway with no exit ahead: a forced rollout backtracks to the exit behind it; any other aircraft holds, waits for an occupied or full exit, or requests a back-taxi (docs/landing-and-runway-exit.md).

**Pushback hold**:
A scenario-scripted deferred command that would end an active pushback, or that the pushback would reject once one is held, waits until the pushback phase ends (`SimulationEngine.ProcessDeferredDispatches`, docs/ground/pushback.md).

**Taxi edge trail**:
The straight taxi edges a ground aircraft has driven, oldest first, kept to about 3,000 ft and sampled once per sim-second, so consecutive edges need not meet (`AircraftGroundOps.TaxiEdgeTrail`, docs/tick-loop.md).

**Lead's path**:
What a `FOLLOWG` follower joins: its lead's taxi edge trail with the gaps filled, the edge the lead is on, and the lead's remaining route: its assigned route, or, for a lead that is itself a follower, its driven route while its follow is current, never its stale assigned route (`FollowRoutePlanner`, docs/ground/navigator.md).

**Merge node**:
The node of the lead's path a `FOLLOWG` follower's goal-set search reaches first, where it joins the lead's path (`FollowRoutePlan.Joinable`, docs/ground/navigator.md); also called the merge point. A follower standing behind the lead on the edge the lead is on joins at that edge's start in the lead's direction, behind the follower, with no route to it.
The lead's edge into the merge (`LeadEdgeIntoMerge`) is the lead's path edge that ends at it, which the give-way stop keeps clear of with the lead's first edge out.

**Lead-in segment**:
The edge a `FOLLOWG` follower (or a follower's clearing route) stands mid-way along, facing the node its route starts at, put in front of the route as its first segment so the aircraft drives on from where it stands (`FollowRoutePlanner.LeadInSegment`, docs/ground/navigator.md).
_Avoid_: implied lead-in (an uncleared taxiway a TAXI to a stand drives), lead-in line (a stand's own approach line), lead's edge into the merge (that is the lead aircraft's edge, not the follower's)

**Follow route**:
The taxi route a `FOLLOWG` follower drives through its own navigator: its route to the merge node, then the lead's path from there (`FollowingPhase.FollowRoute`, docs/ground/navigator.md). It never replaces the follower's assigned route.
_Avoid_: assigned route (the one `FOLLOWG` leaves in place, which the follower does not drive)

**Clearing route**:
The taxi route a `FOLLOWG` follower drives off a runway when it has no follow route while inside that runway's hold line, its lead gone or no plan to join: to the nearest of the runway's bars ahead and on past it, then braked along to rest once its tail and wingtips are past the hold line (`FollowingPhase.ClearingRoute`, docs/ground/navigator.md).

**Driven route**:
The taxi route an aircraft actually drives: a follower's follow route or clearing route while a follow is its current phase, else its assigned route (`FollowingPhase.DrivenRouteOf`). The ground conflict detector reads every aircraft's route through it.

**Meet point**:
The first junction two aircraft's driven routes both reach within the 1,500 ft look-ahead (`GroundConflictDetector.FindSharedUpcomingNode`, the junction a `GIVEWAY` gives way short of), sent per aircraft as `MeetPoints` (`GroundMeetPointDto`: the other callsign, node id, position). It is the server's prediction of where routes meet, with no ETA test, and says nothing about who gives way.

**Stop gap**:
The nose-to-tail distance a `FOLLOWG` follower stops at behind its lead, keyed by both aircraft: about 100 ft behind a small or prop lead, 150 ft behind a large or 757-class jet, 250 ft behind a heavy or super, plus 100 ft for a small or helicopter follower behind a jet (`FollowGap.StopGapFt`, a judgement value).
_Avoid_: follow distance (the gap is nose to tail along the path, not centre to centre)

**Close-follow band**:
The stop gap plus 150 ft: inside it, behind a lead moving away, a `FOLLOWG` follower is no slower than the lead, with a walking-pace floor to close up (`FollowGap.CloseFollowBandFt`).

**Goal-set search**:
One A* pass to whichever of several goal nodes is cheapest to reach, rather than one search per goal (`AutoRouter.RunToGoals`, `TaxiPathfinder.FindRouteToNearestGoal`, docs/ground/pathfinder.md).

**Lead chain**:
The aircraft a `FOLLOWG` follower is never limited against by the ground conflict detector: its lead, that lead's lead while it is following too, and so on, read from each aircraft's current phase only (`GroundConflictDetector.IsInLeadChainOf`, docs/conflict-and-visual-detection.md). A sibling follower of the same lead is not in the chain.

**Closing** (a lead closing on its follower):
A lead in a follower's lead chain that the detector limits like any other aircraft because it is pushing toward the follower, or re-entering its own taxi edge trail with the follower standing on it (retracing, returning over a fillet arc, or turned about on its newest edge) (`GroundConflictDetector.ClosingOnFollower`).

**Commit point** (of a runway crossing):
The point past which an aircraft cleared across a runway is no longer held at the near line by the near-line gate: rolling, once it can no longer stop with its nose at the near holding-position marking even at the firm rate; at rest, once its nose is past the marking (`RunwayCrossingFarSideGate.IsCommitted`, docs/ground/navigator.md).

**Lost route**:
The follow route a `FOLLOWG` follower keeps after a lead re-route joins nothing, while it brakes to rest along it rather than drive a clearing route (`FollowingPhase.IsBrakingLostRoute`, docs/ground/navigator.md).

**Clear-of-runway end** (of a bare crossing):
Where a `TAXI` that crosses a runway with no destination ends: on the far taxiway with the aircraft's tail and nose both `RunwayTailClearance.MarginFt` past the far bar, instead of at the centreline join (`SegmentExpander.EndClearOfCrossedRunway`, `ClearOfRunwayEnds`, docs/ground/pathfinder.md).

**Far bar**:
The crossed runway's holding-position marking on the side the aircraft leaves by; a crossing is complete only when the whole aircraft is past it. The route search finds it on the final taxiway, bounded by cross-track from the runway centreline, not by distance along the taxiway (`SegmentExpander.FindFarBar`).

**Tail clear** (of a runway exit, crossing or `CLRWY` stop):
The point where every part of a leaving aircraft is past the runway holding-position marking (AIM 4-3-21.b). YAAT stops the aircraft `RunwayTailClearance.MarginFt` (10 ft) past that point, so its tail is clear with a margin rather than on the line (`RunwayTailClearance`, docs/landing-and-runway-exit.md).

**Tail clearance** (of a crossing):
The distance past the far bar the crossing phase carries the aircraft so its tail is clear of the runway: half the aircraft's length (`CrossingRunwayPhase.AppendTailClearance`). A bare crossing's end adds the nose to that test and a 10 ft margin.

**Near-line gate**:
The hold that keeps an aircraft cleared across a runway at the near holding-position marking while the far side lacks room for it to clear the runway, releasing it by itself when the room opens (`RunwayCrossingFarSideGate`, docs/ground/navigator.md).

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
A same-runway lead that comes after the follower in the landing order: on a shared leg by position along it, on different legs by leg order, and for a follower on base, final or an instrument approach by remaining path to the threshold (no tolerance); FOLLOW of such a lead is refused (docs/approach-and-pattern-geometry.md, *Sequence order*).

**Lead-ahead gate**:
The command-time checks that refuse a FOLLOW whose lead is not ahead of the follower: behind in sequence, outside the ±60° cone, on the ground, or bound for another airport (docs/approach-and-pattern-geometry.md, *Sequence refusals*; the full list is COMMANDS.md *FOLLOW refusals*).

**Downwind box**:
The area an upwind or crosswind follower accepts a lead with no runway in, outside the ±60° cone: along the downwind line from the downwind turn point to 3 NM past the base turn point, on the circuit side, tracking with the downwind (`CommandDispatcher.IsLeadInDownwindBox`, docs/approach-and-pattern-geometry.md).

**Judgement figure**:
A value or rule the aviation review set where 7110.65 and the AIM give no figure; the docs mark it so a later change knows it is a modelling choice, not a regulation (docs/approach-and-pattern-geometry.md, *FOLLOW rulings a change must respect*).

**Turn-out**:
A follower level with or ahead of its lead turning to the downwind heading with one call, holding an offset band, and turning base behind the lead once it has passed (`VfrFollowPhase`, docs/approach-and-pattern-geometry.md).

## Approach clearances

**Armed pattern runway**:
The runway an option clearance's pattern modifier names (`COPT MLT 28L` flown on 28R), held on `PhaseList.PatternRunway` until the circuit for it is built; a pattern entry or a FOLLOW join onto it satisfies the arming.

**Deferred pattern altitude**:
The altitude of that modifier (`COPT MLT 28L 20`), or the runway change's clear when it has none, held on `PhaseList.TransitionPatternAltitude` for the armed runway's circuit rather than the one being flown. A later altitude assignment replaces it.

**Held until established**:
A controller's altitude (CM, DM, forced altitude, `EXP <alt>`) given outside the published final approach fix to an aircraft cleared for an approach: the approach stays and the altitude is flown in place of the published altitudes until glideslope capture, until approach navigation sequences its next fix, or, when held above the glideslope, until the aircraft crosses the published FAF
(`ApproachClearance.AltitudeHeldUntilEstablished`, docs/phases.md, *Commands on an aircraft cleared for an approach*).

**Inside the FAF**:
Established on the final and no further out than a limit measured to the landing threshold. For the speed gate and the FOLLOW refusal the limit is the published FAF capped at 5 NM (`FinalApproachFix.IsInside`); for the altitude hold and the bare-go-around refusal it is the published FAF uncapped (`FinalApproachFix.IsInsidePublishedFaf`, docs/approach-and-pattern-geometry.md).

## Aircraft performance

**Crossover** (IAS/Mach crossover):
The altitude at which a constant Mach number and a constant indicated airspeed coincide. Above it the Mach number is the lower limit of the two, so a type's Mmo, not its Vmo, is the fastest it may fly; below it Vmo governs. It moves with the figures, so a refused speed names a Mach number above it and knots below.

**Mmo**:
A type's maximum operating Mach number (`AircraftPerformance.MaxOperatingMach`), null for a helicopter. With Vmo it is the fastest the type may be flown: a speed past it is refused as unable and physics holds every speed to it.

**Vmo**:
A type's maximum operating indicated airspeed in KCAS, from a type-certificate or OpenAP override figure or its pybada speed envelope class, raised to what the type's own profile schedule flies. See `docs/aircraft-performance.md`, Speed limits.

## Pilot speech

**Aviation floor**:
The content a varied spoken clause may never drop, add or reorder against its canonical clause (headings, runways, altitudes with their unit words, speeds, squawks, hold-short and crossing), checked by `AviationFloor.HoldsTemplate` for rule variant rows (the **value floor**) and `AviationFloor.Holds` for clause builders that only produce strings; a clause that fails it is spoken canonical (docs/plans/pilot-phraseology-variation.md).

**Builder variant set**:
The variant rows of a clause a readback builder renders itself instead of reading a rule (an altitude restriction, an expedite, a cross-fix restriction, an approach clearance, join or expect, a visual clearance, an airborne follow), a `BuilderVariantSet` in `PhraseologyRules.BuilderVariantSets`; the builder returns its canonical with its set and `VerbalizeBuilderClause` picks among the canonical and the band's rows.
_Avoid_: variant row (a rule's own `PilotVariantSet` row)

**Chained readback**:
A readback of two or more commands (a CAPP with a route or crossing before it included): it never takes a chatty row or the quiet "alright," / ", thanks" flavour, a quiet frequency speaks the normal-band words and a busy one keeps its terse rows on every clause. A single command, conditioned or not, is not chained.

**Check-in butt-in**:
A second solo pilot's initial-contact check-in keying up 3 s after the last transmission while the frequency still waits for the controller's answer to the first, on a seeded 10% draw (`SpokenFormDraws.DrawCheckInButtIn`); ground and departure first calls never do it. See [solo-training-pilot-speech.md](docs/solo-training-pilot-speech.md#check-in-butt-in).

**Comma-led capture**:
A spoken capture value that begins with ", " (the visual clearance's `{side}` ", left traffic" and `{follow}` ", follow the traffic"): the renderer attaches it to the text before it with no space, and an empty one leaves the next comma in place (`PhraseologyVerbalizer.Append`, `RenderVariantTemplate`).

**Dead-air share**:
The share of the last 60 s in which nobody was transmitting on the solo frequency, from the pilot and controller transmission intervals with overlaps counted once: 70% or more is quiet, 35% to under 70% normal, under 35% busy (`FrequencyActivityMeter.DeadAirShare`, docs/solo-training-pilot-speech.md).

**Effective band**:
The load band a pilot speaks at: the frequency's load band shifted by the pilot's trait, terse one step busier and chatty one quieter, clamped to quiet..busy (`PilotTraits.EffectiveBand`); the speech-rate nudge still uses the raw band (docs/solo-training-pilot-speech.md).

**Group form**:
A number said as grouped words ("eleven thousand", "two fifty") rather than as separate digits ("one one thousand", "two five zero"); a pilot readback speaks it only on a busy frequency, through a variant row's `{alt:group}` / `{spd:group}` capture, and never for a flight level (`PhraseologyVerbalizer.AltitudeGroupWords`, `SpeedGroupWords`, docs/solo-training-pilot-speech.md).

**Handler readback**:
A readback clause a command handler builds itself and returns on `CommandResult.PilotReadback` (`HandlerReadback`), because it names what the handler resolved (an approach clearance's approach); it may carry a builder variant set and the spoken captures (`Variants`, `Captures`) so a varied pilot picks its final clause from the band's rows.

**Held canonical**:
A clause spoken as its canonical text in every load band because it has no shorter form that keeps its meaning (a military route clearance, the Class B clearance, LAHSO, a cross-fix restriction with a speed, a circling approach clearance, CVA, airborne FOLLOW, PTAC); only the callsign position or an acknowledgement word may change.

**Load band**:
How busy the solo frequency is, in three steps (quiet, normal, busy), judged by the dead-air share of the last 60 s (`FrequencyLoadBand`, docs/solo-training-pilot-speech.md).

**Lowest position worked**:
The lowest position type a solo student works (`SimScenarioState.StudentLowestWorkedLevel`, CTR > APP > TWR > GND > DEL): the student works its own position and every type below it down to this one, answers the pilot calls those types take, and the sim plays the rest. The sidecar, the session-setup prompt or the session flyout's "Positions you work" row sets it, and `ScenarioAnswers` carries it per scenario.

**Pilot trait**:
How much a pilot says on the frequency regardless of load: terse (25%), normal (50%) or chatty (25%), derived from the callsign and the scenario seed and never stored. Terse speaks at the next busier load band, chatty at the next quieter, clamped to quiet..busy (`PilotTrait`, `PilotTraits.EffectiveBand`, docs/solo-training-pilot-speech.md).

**Readback gate**:
The hold on the solo frequency between a controller instruction and the pilot's queued reply (readback, "unable", contact/handoff readback or pattern-size reply): other pilots' calls wait until that reply is on air, replies to several instructions in instruction order, released 8 s after the frequency frees for that pilot if the reply never queues (`FrequencyState.ExpectReadback`). Not snapshotted.

**Simulated tower**:
The tower the sim plays without a word at a towered airport where the solo student does not work the tower and no AI tower covers it (`PilotContactRoster.TowerIsSimulatedAt`): no tower call reaches the student, it clears a departure the student transferred for takeoff once the runway is clear, clears arrivals to land and sends them around when the runway is not clear.

**Value floor**:
The aviation floor for a rule's variant rows, checked by value rather than by rendered text: a row must carry the rule pattern's captures in the same order and the same ordered keyword classes, and only a busy row may drop climb/descend or the word runway on a runway clearance (`AviationFloor.HoldsTemplate`, enforced at test time over every row, docs/solo-training-pilot-speech.md).

**Variant row**:
One alternative spoken wording of a rule's readback, a template over the rule pattern's own `{capture}` placeholders listed under a load band in the rule's `PilotVariantSet` (`PilotVariant`, `PhraseologyRule.PilotVariants`); a Varied pilot's seeded pick chooses among the band's renderable rows (docs/solo-training-pilot-speech.md).

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

**Layout**:
A saved, named window arrangement in the desktop client (View › Layout): window geometries, the pop-out windows, extra Radar/Ground windows, Aircraft List columns, favorite sets and the open Strips/vTDLS tabs; never radar, ground or terminal view settings.
_Avoid_: window profile (its old name); Profile (a YAAT Scope display file)

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

**Nudge**:
A range/bearing readout pushing an auto-placed data block (one at its default or deconflicted placement, never a manually dragged one) just far enough aside to stay readable, capped at the leader's maximum length; the block returns once the readout no longer needs the room (`RblReadoutPlacement`).

**App tool**:
A YAAT client method marked `[AutomationTool]` that the client-driver MCP lists (`list_app_tools`) and calls by name (`call_app_tool`): a setup action such as framing the radar or loading a recording, done directly rather than through the UI.

**Recording mark**:
A labelled moment in a client-driver recording (`record_mark`, or `wait_until`'s `stop_recording`), saved in `<clip>-marks.json` beside the MP4 with its wall time, its seconds into the clip and the scenario's sim seconds, so an edit can find the moment again.

**Situation**:
A named bucket of aircraft phases and state (Taxiing, Holding short, Final, IFR arrival…) that picks an aircraft menu's quick commands.
_Avoid_: phase (one situation spans several phases)

**Quick commands**:
The short, user-editable list of commands an aircraft's right-click menu shows first for its current situation; the rest sit under All Commands.
_Avoid_: favorites (the Favorite Commands submenu is the user's own typed commands, the same in every situation)

**Alias (menu alias)**:
The command verb a menu row shows beside its label, right-aligned and dimmed, so a mouse user learns what to type: the row's verb as the controller's command scheme spells it (the primary verb), `A / B` for a row sending two verbs and `A …` for three or more.

**All Commands**:
The aircraft menu's submenu holding every command for the aircraft, grouped by task category in one fixed order, each shown by its catalog predicate alone; the quick commands are a filtered pick from it.

**Task category**:
One of the seven groups All Commands sorts its commands into, in this order: Runway, Taxi, Vectors, Approach, Pattern, Pilot reports, Track and coordination (`MenuCategory`; `MenuCategories.Of` gives a catalog id's category, `Label` the group's header). Live traffic (Assume items) and Sim (Warp, Delete) are categories too, for the quick commands only. An id in none (Favorites, Command…, Note…, the spawn, point-menu and For-section items) is in `MenuCategories.Uncategorized`.

**Icon strip**:
The glyph buttons at the top of an aircraft menu, six to a row: every glyph-bearing quick command, grouped by task category (each group a strip segment under its category label); the rest show as text below it. A point menu opens with its own flat, unlabelled icon strip of the ground point items that apply. The Show quick command icons setting turns every strip off and lists those commands as text rows instead.

**State line**:
The dimmed, disabled row under an aircraft menu's title that says what the aircraft is doing now, as segments joined with ` · ` (`Approach · 3,000 ft · 180 kt · KOAK rwy 30`, `Taxiing · C · KOAK`, `Landing · runway 28R · 62 kt`); a segment with no value is left out, and with none left there is no row (`SharedMenuGroups.StateLineItem`, phase names from `PhaseDisplayNames`).

**Label line**:
The state line's row while an icon strip's icon is pointed at (or keyboard-focused): it shows the entry's name and the command it sends in place of the state line, scrolling as a marquee when too long, and is blank at rest on a menu with no state line (`QuickCommandLabelLine`).

**Quick-list visibility rule**:
A rule that hides a quick command in its situation by the server's situation flags or `NextCrossingRunway` (Cross only when a runway lies next on the taxi route, Cancel takeoff clearance only before V1); quick list only, All Commands keeps the entry (`AircraftCommandApplicability.Shows*`, applied by `QuickCommandResolver`).

**Quick-list widening**:
A rule that admits a quick command in a phase its catalog predicate leaves out, once a sim acceptance test proves the sim takes the command there (`AircraftCommandApplicability.Widens*`); quick list only.

**Catalog entry (menu catalog)**:
One command the aircraft menus can offer, with a stable ID (`<group>.<item>`, never reused), a label, a default flight-rules filter, an applicability predicate and a builder (`MenuCatalogEntry` in `Yaat.Client.Core/ContextMenus/`); quick commands are lists of these IDs.

**Category label**:
The small upper-case name (RUNWAY, TAXI, ..., LIVE TRAFFIC, SIM) with a tick and a trailing rule that heads a strip segment on the icon strip, ending in "…" when the segment is too narrow for it (`QuickCommandStrip`, text from `MenuCategories.Label`); a wrapped category's later rows and the uncategorised icons have none.

**Menu host**:
The surface that owns an aircraft menu (radar, ground, aircraft list) as a catalog entry's builder sees it (`IMenuHost`): sending the command text, and whatever popups or reads the entry needs.

**Runway entry (Taxi to runway)**:
A runway hold short the Taxi to runway flyout offers as one title line with its variants under it, reached by the shortest route from the aircraft: the **full-length entry** at the runway's end, or an **intersection entry** whose runway remaining (shown as `~N ft avail`) is at least the type's takeoff distance (`TaxiRouteRow`, `GroundViewModel.GetTaxiToRunwayChoices`); the client's own ranking, separate from `RunwayEntryPoint`, the Sim classifier behind a queued departure's `28R@E`.

**Chip**:
The small button under a flyout row that sends a variant of the row's command: a facing under a Push back to… target (`PUSH TE FACE N`) or another holding position under an Air taxi runway row (`ATXI 28R@E`). Its accessible name is its command.

**Click context (menu click)**:
What a right-click gives an aircraft menu (`MenuClick`): the aircraft the menu commands, the previously selected aircraft that sends the relative items (null when it is the clicked one), the clicked point on a point click (`MenuPoint`: a map position, a taxi node, a runway end; null on an aircraft click), and the list's selected rows (`[]` on the canvases).

**Command row**:
A clickable, templated context-menu row that sends a command: an optional glyph, the name, a detail, a distance and the command text (`MenuCommandRow`, drawn by `MenuCommandRowTemplate`); the Preset taxi route (the taxiways under the name) and Exit left / right rows are command rows; the Follow…, Give way to… and Hold short of… rows draw their own layouts.

**Glyph row**:
A clickable context-menu row led by a quick-command glyph, with a label, an optional dimmed note and the command text (`MenuGlyphRow`, drawn by `MenuGlyphRowTemplate`); a dimmed one stays clickable. The pattern entries' runway flyout rows are glyph rows, the glyph rotated to the runway's true course less 270°.

**Handoff code**:
The shortest code that names exactly one position to `HO` in the room's scenario (the TCP or ERAM code, else the callsign, else `callsign@code`), sent by the server on each `OnlineControllerDto` (`RoomEngine.SelectionCodeFor`) and by the Initiate handoff… flyout as the row's command; null with no scenario loaded.

**Hold short ladder**:
The Hold short of… designed flyout (`HoldShortLadder`): the aircraft's route from where it is to where it ends as a list of **stops**, each a bar along the route (runway bars two amber lines, taxiway bars teal dashed) with its name, where, distance and `HS` command, nearest first, under a line for the aircraft itself (`now, taxiing on D`) and over a dimmed, unclickable last row for where the route ends (`Spot 5 · route ends here`). Holding short, it starts after the held bar.

**Landable (runway end) / short runway end**:
A runway end is landable for a physical aircraft type when its landing distance available is at least 1.15 times the type's landing distance, and short otherwise (`RunwayLandability`); a type with no figure, a helicopter and a `VEH*` type are always landable. The pattern entries' flyout lists landable ends first and marks short ones "short".

**Designed flyout**:
A quick command's strip flyout built from rows with their own layout (glyph or badge, label, what it sends) instead of a copy of a context submenu; the same rows serve the entry's All Commands submenu. Cleared for takeoff, the pattern entries' runway flyout, Push back to…, Follow…, Give way to…, Hold short of…, Preset taxi route, Taxi to runway, Air taxi, Land, Exit left, Exit right, Cleared approach and Expect approach are designed flyouts.

**Detail row**:
A disabled, dimmed label row in a menu that sends nothing (`SharedMenuGroups.DetailRow`), such as the point menu's FRD, distance and bearing line; not a command row.

**Point menu**:
The menu a right-click on empty map (radar) or on a taxi node, runway threshold or runway surface (ground) opens for the selected aircraft: on the ground, an icon strip of the taxi items that apply, then the shared `point.*` items its state allows (Fly heading, Direct to, Hold, Taxi here, Taxi to runway, Push to, Custom taxi, Warp here), then the view's own point items (markers, Measure, FRD, Draw taxi route from the node).

**Primary verb**:
The first alias of a command in the controller's command scheme (`CommandPattern.PrimaryVerb`), the verb the command bar completes to and a menu row shows as its **alias**.

**Radar cue**:
What a pointed or focused menu row draws on the radar views (`RadarCue`, sent through `IMenuHost.SetRadarCue`): a solid ring on the aircraft the row names, a dashed bearing line to it from the commanded aircraft labelled with its clock position and miles, and a dashed ring on the aircraft the call resolves to when that is another one. It clears when the menu closes; the radar's counterpart of the **scope preview**, which the radar does not draw.

**Scope preview**:
What a pointed or focused Give way to… / Follow… row draws on the ground views (`ScopePreview`, sent through `IMenuHost.SetScopePreview`): a ring on the row's aircraft, the commanded aircraft's route solid and the row aircraft's dashed, and, for Give way to…, the **meet point** ringed in amber. A pointed or focused Hold short of… stop draws the bar boxed and the aircraft's route solid to it, with no ring. It clears when the menu closes and is not drawn on the radar.

**Staffed position**:
A position in the room's controller list that has a controller signed on (a live CRC client or a scenario auto-connect position, `OnlineControllerDto.IsActive`), as against a standby one. The Initiate handoff… and Point out… flyouts list only staffed positions.

**Strip flyout**:
The flyout a quick-command strip button opens, holding the items moved out of that command's menu item (`QuickCommandStrip`); items that follow live data stay current in it through `IStripFlyoutContent`.

**Strip segment**:
A run of one task category's icons on one icon-strip row (`QuickCommandStripSegment`); a row holds one or more segments, neighbouring partial groups sharing a row with a thin rule between, and packing is `QuickCommandStripLayout.Pack`. Not the sim-second **Segment** above.

**Tab strip**:
The row of tabs at the top of a designed flyout, each a runway or group, with a state beside its label: nothing when its rows are known, a spinner while they are searched, a dimmed `none` when there are none (`FlyoutTabStrip`, `FlyoutTabState`); the arrow keys move between tabs without wrapping; Taxi to runway has one tab per runway end.

**View section**:
The few canvas-only items a view adds to the shared aircraft menu, which the builder places last in All Commands after a separator, or at the root of the point menu (the radar's Display and Draw route, the ground's Display; the aircraft list has none); they have no catalog entry and never sit on a quick-command list.

**Menu session**:
The session settings an aircraft menu's predicates read (`MenuSession`): the user's initials, solo training mode and the VFR-commands-for-IFR mode.

**For section**:
The block at the top of an aircraft's menu, under its header, while another aircraft is selected: `For {selected} (selected)`, a line saying where the clicked aircraft is from the selected one, and the items sent as the selected aircraft (Report in sight and Follow in the air, Follow and Give way to on the ground), then `For {clicked}` over the clicked aircraft's own items (`SharedMenuGroups.AddForSection`).

**Traffic row**:
One other aircraft as a menu's traffic list shows it, seen from the aircraft the menu commands: in the air a `MenuAirborneTrafficRow` (a `MenuTrafficRow`: callsign and type, distance, clock position, altitude difference; plus direction of flight and pattern leg), on the ground a `MenuGroundTrafficRow` (callsign, type, distance in feet, state such as `taxiing on W · ahead`).

**Gone row**:
A Follow… or Give way to… row whose aircraft has left the ground, a Hold short of… stop the route has left, or a Preset taxi route row the host no longer returns, while the pointer holds the open flyout's list in place: dimmed, reading `gone`, disabled, skipped by the keyboard and sending nothing.

**Live refill**:
A strip flyout's once-a-second rebuild from the host's data while it is open (`FlyoutLiveTick`): rebuilt nearest first with the pointer outside, held in place with gone rows and teal-edged newcomers with the pointer inside (Follow…, Give way to…, Hold short of…, Preset taxi route).

## CRC hub connections

**Direct connection**:
A CRC hub socket identified by its own YAAT `access_token` (vEDST, Tower Cab 3D) rather than by a CRC negotiate: it never joins the lobby or attendance, and reaches a session only through `JoinSession`, with a primary of the same CID or, for Tower Cab 3D, a room session (docs/vatsim-auth.md).

**Primary**:
The CRC connection whose session a direct connection joins; the joiner reads the primary's room.

**Negotiated joiner**:
A direct connection that also negotiated first (Tower Cab 3D): it keeps its negotiate id as its connection token, so it can register for UDP entity updates, while its CID comes from its access token.

**Room session**:
The synthetic `room:<roomId>` session `GetSessions` offers a Tower Cab 3D connection whose CID has no CRC primary: joining it attaches the connection to the room its CID joined most recently (`TowerCabRooms`, docs/vatsim-auth.md).

**Tower Cab member**:
A Tower Cab 3D connection listed in Room Members (`ClientKind.TowerCab`, keyed by its CRC client id): it never counts toward attendance or OpenPositions, is never persisted, and a kick of its CID removes it with the CID's other connections.

## CRC driver hook

The startup hook in the private yaat-server repo that lets the client-driver MCP drive CRC with no real input; detail in yaat-server `docs/crc-driver-hook.md`.

**Breach**:
The state the hook enters when its fence finds CRC connected to a non-loopback environment: it brings every CRC window to the front and refuses every verb except `hello`, `status`, `profiles` and `dialog_list` (invariant 3 in yaat-server `docs/crc-driver-hook.md`).

**CBT veto**:
The hook's `WH_CBT` procedure on CRC's main thread returning 1 for `HCBT_ACTIVATE`, so no CRC window ever becomes the active or foreground window; it is what holds, because WPF resets `WS_EX_NOACTIVATE` after a window is created (yaat-server `docs/crc-driver-hook.md`, invariant 3).

**DCB**:
The STARS Display Control Bar, the button strip around the scope; the hook refuses a `click` inside it and allows no DCB F-keys, since they move the real cursor (yaat-server `docs/crc-driver-hook.md`, invariant 4).

**Epoch** (of the CRC driver hook):
The session, network client, hub connection and connected host CRC is on; when it changes, the hook sends Escape to every InputManager and closes the menus and dialogs it opened (yaat-server `docs/crc-driver-hook.md`, invariant 10).

**Fence**:
The check, `Fence.Check`, run in the same dispatcher operation as each input, that CRC is connected to a loopback environment; it fails closed, and a non-loopback find is a breach (yaat-server `docs/crc-driver-hook.md`, invariants 1 and 2).

**Slew**:
Selecting an ASDE-X target as clicking its icon does; `asdex_slew` calls `InputManager.HandleIconSlew` directly because the real hover reads the owner's cursor (yaat-server `docs/crc-driver-hook.md`).

## Client settings

**Field advisory flyout**:
Report field in sight… in RPO and in solo training: the user types the clock and miles of the destination field (or sets them on the clock dial and Miles stepper) and Enter sends `RFIS {clock} {miles}`; the true values are pre-filled and followed unless the solo pre-fill setting leaves them for the student, and the sim records any value, so the flyout never grades it (docs/client-context-menus.md).

**Import clash**:
An incoming named entry (a macro, a favorite set, a layout) that matches an existing one under Merge, resolved per entry by Skip, Overwrite or Rename.

**Room-only setting**:
A session setting with no default in Settings › Scenario defaults: it belongs to the room, and any RPO in it changes it only in the session flyout or on its own button (live traffic; releases). Scenario defaults lists them in its "Room only" card (USER_GUIDE.md "Scenario defaults").
_Avoid_: flyout-only setting (releases have their own button, not the flyout)

**Section link**:
A button in a Settings section that opens the section where a shared setting lives (its one home), such as Radar's font-size link to Appearance; it carries that setting's search aliases, so a search finds the setting at its home and at each link (docs/client-settings-and-menus.md).

**Session flyout**:
The gear flyout on the command input that shows and changes the room's live session settings for every RPO in it; Settings › Scenario defaults holds the user's defaults that a scenario load sends to it (docs/client-settings-and-menus.md).

**Settings bundle**:
A `.yaat-settings.zip` holding a `manifest.json` and one file per item type (preferences, macros, command verbs, favorites, grid layout, layouts), each in its single-item format; the Import / Export hub reads and writes it. Preferences travel only by an allowlist of Settings-section keys, each validated on import.
_Avoid_: settings backup, profile (a layout is not a bundle)

**Solo advisory flyout**:
Report traffic in sight… in a solo session: the student types the advisory as they would say it (clock, miles, direction, type, altitude, or a pattern or reporting-point form) and Enter sends the structured `RTIS`; its ghost is the grey hint under the box naming the next part, its Builder sets the same call by part, and no row sends anything (docs/client-context-menus.md).

**Solo advisory settings**:
The two client-only solo settings, "Pre-fill traffic advisories" (True values or Left for the student) and "Live advisory check" (grade the student's advisory as they type): a default in Settings › Scenario defaults › Solo training and a session copy in the session flyout that never leaves the client and reseeds from the default on a room change or a scenario or recording load (docs/client-settings-and-menus.md). Not the load prompt's **Pre-fill**.

## Tooling

**At-risk tests**:
The tests outside a sim-behaviour brief's file list that pin the behaviour it changes; the brief names each with a ruling (update the expectation, keep it green unedited, or stop and report), as the `yaat-nextup` profile's Brief shape says.

**Precompute cache**:
Per-airport results computed offline from static inputs and shipped with YAAT (`src/Yaat.Sim/Data/PrecomputeCache/{FAA}.json.br`): the parsed ground layout and the per-stand push targets. An entry is used only when its key matches the current inputs; otherwise the reader plans the result live (the client's Push back to… submenu does).

**Airport list**:
`src/Yaat.Sim/Data/PrecomputeCache/airports.txt`, the airports the precompute cache covers: those a training scenario names as `primaryAirportId` or on an aircraft's `airportId` that have a vNAS ground map. The maintainer tool computes and checks them, and `--refresh-airports` rebuilds the list.

**Airports in flight**:
How many airports a precompute tool compute run works on at once, each holding its parsed layout (`--airports-in-flight`, default 4); `--parallel` caps the stands planned at once across all of them.

**Algorithm hash**:
The part of a precompute cache key that names the code that produced an entry: a hash of the source files that feed the computation, generated at build time (`PrecomputeSourceHashes`). The layout and push-target halves have separate hashes, so a change to the push-target planner alone, or to an airport's sidecars, invalidates only push targets; the tug planner's `Tug*.cs` files are in the push set alone.

**Collection weight**:
A Sim test collection's measured seconds in `tests/Yaat.Sim.Tests/collection-weights.json`, keyed by the class full name for a default collection and by the name for a `[Collection]` one; `CollectionWeightOrderer` starts collections heaviest first, and a collection missing from the table weighs 0 and runs last.

**Design-group entry**:
One push-target set per stand per airplane design group (ADG I-VI), planned with the group's envelope; an aircraft reads the entry for its own group.

**Design-group envelope**:
A synthetic footprint (`ADG-I` to `ADG-VI`) for one airplane design group: the greatest length, wingspan (capped at the group's ceiling), wheelbase and main gear width over the group's fixed-wing FAA records, each taken separately (`DesignGroupEnvelopes`).

**Evergreen test**:
A recording test that pins behaviour which must keep holding on today's airport (a regression test for a sim, pathfinder or parser fix). It runs on today's layout (`TestData/<id>.geojson`) through `RecordingGroundData.Evergreen(why)` and is maintained as the layout changes; when a parser change renumbers nodes, its fixture's recorded `#id` TAXI commands and snapshot routes are remapped to today's ids.

**Golden (menu golden)**:
A committed text snapshot of an aircraft right-click menu for one view and one situation fixture (`tests/Yaat.Client.UI.Tests/Goldens/menu/{radar,ground,list}/<fixture>.txt`, written by `MenuTreeSnapshot`); `MenuGoldenTests` fails when a menu differs from its golden, and `YAAT_MENU_GOLDEN_REGENERATE=1` rewrites them.

**Layout set**:
The source files whose hash is a precompute key's layout algorithm hash: `Data/Airport` minus `Precompute/`, the `Tug*.cs` planner files and a few pathfinder/navigator helpers, plus a few files outside it (`PrecomputeLayoutSource` in `Yaat.Sim.csproj`).

**Live check**:
The re-fly and sweep of a cached push target when an aircraft's Push Back To menu opens: the stored moves are flown again with the aircraft's own outline and turn radius and swept against the parked or held aircraft about it (`PushTargetLiveCheck.Check`). The verdict is Clear, Blocked (by a named neighbour) or Unflyable.

**LPT**:
Longest-processing-time-first, the greedy schedule that hands the heaviest remaining job to the next free worker; `tools/analyze-test-schedule.py` simulates it over a TRX, and `CollectionWeightOrderer` makes the real run follow it.

**Offline check / online check**:
The precompute tool's `--check`. Offline, it compares each listed airport's committed entry key with the build-time source hashes, the sidecar hash and the layout format version; online (`--check --online`), it also compares the GeoJSON MD5 and NavData serial with what vNAS serves now. A missing or stale entry is a warning, never a failure.

**Push plan store**:
`PushPlanStore`, the plan of each push-back stand a parked jet or turboprop sits on (`PushTargetEntry` for the stand and its design group), made off the tick and read by AI Ground's push rule. A server room owns one for every engine it builds; a scenario load's prepare and a reload fill it, a spawn adds to it only while AI Ground is active, and a stand it lacks is planned on the tick.

**Push set**:
The source files whose hash is a precompute key's push-target algorithm hash: all of `Data/Airport` except `Precompute/`, so the tug planner included, plus the push-target `Precompute/` types and a few files outside it (`PrecomputePushTargetSource`); it moves whenever the layout hash does.

**Push-target seed**:
The shipped precompute entry for an aircraft's stand and design group (`PushTargetSeed`), which first fills the Push back to… submenu, live-checked, before the client's own live plan lands and replaces it in place; a stand with no current entry, and any held aircraft, show `Computing targets…` instead.

**Stay-put target**:
A push target planned from a held pose that ends within 10 ft and 10° of that pose (`PushTargetPlanner.StayPutDistanceFt`, `StayPutHeadingDeg`), such as a push to the spot the aircraft sits on; it would leave the aircraft where it is, so the held plan drops it.

**Ouroboros** (controller-voice ouroboros):
The speech sandbox's self-test (`--atc-ouroboros`): it synthesises controller transmissions from templates with a TTS voice, runs them through speech recognition and the phraseology mapper, and scores each case against the template's expected command (`tools/Yaat.SpeechSandbox/Corpus/atc-ouroboros-baseline.json`).

**SttOnly rule**:
A phraseology rule the speech mapper matches but `PhraseologyVerbalizer` never speaks: it covers how speech recognition writes a phrase (a misheard or split word), not how a pilot says it.

**Version-pinned test**:
A recording test that pins behaviour which only makes sense on the airport as recorded (a restored mid-taxi route, recorded node ids). It runs on the archive's bundled layout through `RecordingGroundData.VersionPinned(archivePath, why)`, and its fixture's ids are never remapped.

**Gate**:
`tools/gate.ps1` (the gate wrapper, which hands every call to the installed `gate.exe` and on a machine without one runs the command directly; called as `pwsh tools/gate.ps1 -Log <log> -TimeoutSeconds <seconds> -Slot heavy|light -- <command...>` from PowerShell or Bash): runs one build or test command with its whole output in a log under `.tmp/`, the tail on screen and the command's own exit status, and kills it (exit 124) when it stalls, passes its ceiling on the load-adjusted clock, or reaches the backstop.

**Stall**:
A gate run whose log has not grown and whose processes (with any MSBuild or compiler server started during the run) have used no CPU for 120 s (`-StallSeconds`); the gate kills it as hung (`gate: STALLED`).

**Stall watchdog**:
`tools/TestAllWatchdog.ps1`, run by `tools/test-all.ps1`: a test host that has used no CPU for 90 s, ahead of the gate's 120 s stall kill, is named, dumped with `dotnet-dump` into `.tmp/dumps/` and killed, and the run fails (`test-all: STALLED <name> pid <pid>`).

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

**Automation mode**:
The client's mode for being driven by an agent without disturbing the user, on when `YAAT_AUTOMATION=1` (`AutomationMode.IsEnabled`, `AutomationGate.SuppressActivation`).

Every window the client builds shows never-activated and stays in its Normal state; dialogs and message boxes open non-modal through `DialogPresenter` (message boxes through `MessageBoxPresenter`) with their owner disabled, popups draw inside their window, the client never activates itself or sets `Topmost`, and the global push-to-talk key hook and Discord Rich Presence are off (yaat-server `docs/client-driver-mcp.md`).

**Never-activated window**:
A window shown with `ShowActivated = false` that the client never activates afterwards, so it opens behind the user's foreground window without taking focus; automation mode shows every window this way.

**Cloaked window**:
A window the client has hidden from the desktop with a DWM cloak (`DWMWA_CLOAK`) while it keeps rendering, so window capture still records it; an automation-mode client cloaks every window before its first show when `YAAT_CLOAK=1` (`launch_yaat` `cloaked`), and the app tool `set_cloaked` toggles it on a running client (yaat-server `docs/client-driver-mcp.md`).

**Pipe host**:
The client's in-process automation endpoint (`src/Yaat.Client/Automation/AutomationHost.cs`), started on Windows in automation mode: a named pipe `yaat-automation-<pid>` open to the current user only, speaking line-delimited JSON requests `{id, method, params}` and answering a result or a coded error with a recovery hint; derived from Zafiro.Avalonia.Mcp (yaat-server `docs/client-driver-mcp.md`).

**Discovery file**:
`%TEMP%/yaat-automation/<pid>.json`, written by the pipe host on start and deleted on exit, naming the pid, pipe name, process name, start time and protocol version, so a driver finds every running client's pipe; files whose process is gone, or whose pid now runs another program, are swept on start.

**Injected file picker**:
The file picker an automation-mode client uses in place of the native dialog (`InjectedFilePickerService`, built by `FilePickerFactory`): each open or save call takes the oldest answer an agent queued with `queue_file_pick` (a path or a cancel), and fails at once on an empty queue, so no dialog ever opens (yaat-server `docs/client-driver-mcp.md`).
_Avoid_: file dialog (none opens)

**Coaching session**:
A live session in which the owner trains Claude as a solo student: Claude works a position in an automation-mode client, scans the traffic about every 10 sim seconds, and before each instruction pauses and explains its picture and the instruction it would give, which it sends only after the owner rules. The `atc-coaching` skill runs it; its logs and lessons live in `docs/plans/controller-ai/coaching/`.

**Pause trigger**:
An event that makes an automation-mode client pause its room's sim on its own, armed with the app tool `arm_pause_triggers` (`PauseTriggerMonitor`): a pilot's call or report (not a readback), a warning or a new conflict, a new aircraft, a taxiing aircraft stopping for 5 sim seconds, or an optional heartbeat. A coaching session's safety net between scans; each firing is logged for `get_pause_events`.

**Push long**:
A pushback that ends farther than the aircraft's own lead-in line, typically across an alley to the far spot or deeper along the taxiway, so it leaves room for an arrival turning into a neighbouring gate (`docs/plans/controller-ai/coaching/sfo-video-lessons.md` G8).

**West end** (SFO):
Taxiway Z from C to Z2, past the departure ends of runways 28, which a taxi route from the north field to runways 1 follows; Local owns it when landing the 10s, departing the 28s or when given, and Ground requests it by count ("once around the west end") (SFO ATCT SOP 3-1.a.vi; `sfo-video-lessons.md` S10).

**Corpus WER**:
The word error rate of the STT stage alone over a set of real recordings, pooled over every reference word (total substitutions, deletions and insertions divided by total reference words), not averaged per clip (`--corpus-wer`, docs/speech-recognition-pipeline.md).

**Node id**:
The pipe host's stable id for a window, popup or element, issued by its `NodeRegistry` and kept for as long as the element lives, so a driver can name the same element across calls.

**Ouroboros**:
A synthetic round trip through the speech pipeline: a known canonical command is rendered to speech with Piper, fed through Whisper, the rule mapper and the LLM fallback, and the recovered canonical is compared with the one it started from. `--ouroboros` speaks pilot readbacks; `--atc-ouroboros` speaks controller transmissions across every phraseology rule family and diffs each family's pass rate against a committed baseline (`tools/Yaat.SpeechSandbox`, docs/speech-recognition-pipeline.md).

**RTF** (real-time factor):
A TTS model's synthesis time divided by the duration of the audio it produced; below 1.0 it synthesises faster than the audio plays (`--tts-bench`, docs/speech-recognition-pipeline.md).

**Screen** (TTS benchmark):
The cheap first phase of `--tts-bench`: 3 readback lines, 1 voice, at 8, 4 and 2 threads, each count a cold load in its own process. A count passes at p90 first audio of 1.5 s or less and median RTF under 1.0; a model that passes or is marginal goes on to the full run (docs/speech-recognition-pipeline.md).

**Speech telemetry**:
Push-to-talk samples (audio, per-stage transcripts, scenario context) that opted-in users' clients upload to the official yaat-server, which stores them for developers to pull with `tools/speech_telemetry.py` (docs/speech-recognition-pipeline.md).

**WER** (word error rate):
The edits (substituted, deleted and inserted words) a transcript needs to match its reference, divided by the reference's word count, after both pass through `WerTextNormalizer` (`WerScorer`).

## Bug report privacy

**Identity ledger**:
The room's append-only record of everyone ever bound to it (creator, hub members, CRC controllers, pulled RPOs, Tower Cab viewers), keyed by CID and holding every set of initials and every name each was seen with; kept after they leave and across a restart, and the source of the participant list (`RoomIdentityLedger`, yaat-server).

**Participant list**:
The `LogParticipant` list, one per person in the identity ledger's first-seen order, that every artifact of a bug report bundle is pseudonymised with, so a person has one pseudonym in the server log, the recording and the client logs; the client builds a local one only when the server's is unavailable (`SessionLogAnonymizer.BuildParticipants`).

**Pseudonym**:
The stand-in for a person in a bug report bundle, `A0`, `A1`, … by the person's index in the participant list (`SessionLogAnonymizer.Pseudonym`); a person the list does not hold becomes `??`, never a pseudonym-shaped guess.

**Server name**:
The deployment name a server stamps into the recordings it exports (`Yaat:ServerName`, e.g. `yaat1.leftos.dev`), so a bug report bundle says whose server log to read; not the **Host** above, which is the simulation host.

## Weather

**Wind grid**:
A GFS forecast subset (UGRD, VGRD and TMP on isobaric levels over a lat/lon box) that live weather flies above its surface wind layer. It is named by the SHA-256 of its raw GRIB2 bytes (`WeatherProfile.WindGridHash`), so the weather text, a snapshot and a recording all refer to the same grid by that hash, and the bytes live once in `WindGridRegistry` and in the recording archive.
_Avoid_: GFS layer, forecast layer

**Wind grid pin**:
A hold on a registered wind grid that keeps `WindGridRegistry` from evicting it (the registry keeps at most eight unpinned grids). A Sim-hosted scenario, a loaded recording, a replay and a server room (its weather, tape, snapshots and pending upload) each own their pins, and releasing the last pin on a grid makes it evictable again.
_Avoid_: lock, resource pin (that holds a room's airport layouts and ARTCC configs)

## Facility data

**Sidecar**:
A JSON file under `src/Yaat.Sim/Data/ARTCCs/{ARTCC}/` that adds facility rules beside what the vNAS data supplies; the airport sidecar (`Airports/{airport}.json`) carries per-airport ground-routing overrides ([`ARTCC_CUSTOMIZATION.md`](ARTCC_CUSTOMIZATION.md)).

**Tower-cab videomap**:
The vNAS GeoJSON map CRC Tower Cab draws over the satellite image, named by the facility's `towerCabConfiguration.videoMapId`; `TowerCabVideoMap.Read` keeps its two-point line segments (painted hold-short bars), and `LayoutInputs.VideoMap` carries them to the layout build (null = none).
_Avoid_: video map (the radar's facility video maps are a different thing)

## Flight strips

**Auto-sorted rack**:
A rack flagged by `RACKSORT` so its strips stay in order: tail registrations, then airline callsigns, each A to Z top-down, keyless strips at the bottom, separators fixed in place and splitting the rack into runs that sort on their own. A move into it lands in its sorted slot.
_Avoid_: sorted rack (a rack can happen to be in order without the flag)

**Bay**:
One named strip area of a facility (Ground 1, Local) holding a fixed number of racks; vNAS configures it, and bay names are unique only within a facility.

**Rack**:
One column of strips in a bay, numbered from 1 in commands and from 0 in the engine; index 0 is its visual bottom and the newest strip stacks on top.

## Releases

**Feature showcase**:
A Markdown page with one short section and one screenshot per major feature a release introduces or reworks (`docs/releases/whats-new-next.md`, renamed `whats-new-<version>.md` at the cut). Every minor or major release ships one, and a revision release none; `prepare-release` step 2.11 checks it.

**Hotfix**:
A release that ships only the items in the Linear release `vHotfix`, ahead of `vNext` and without the rest of `main`: built from `hotfix/v<version>` branches in both repos, cut at the last release and holding cherry-picked (`-x`) commits, tagged `v<version>`, and deleted after the tag is pushed (`prepare-release`, "Hotfix or regular").

**vHotfix**:
The Linear release in the yaat pipeline holding the issues a hotfix will ship (`linear release add yaat --hotfix <ID>`). While it holds an open issue it fences the repo ahead of `vNext`; completing the hotfix turns it into the released `<version>`.
