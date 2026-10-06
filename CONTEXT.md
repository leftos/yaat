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

**Active runways**:
The runway ends a room is using, per airport, each for departures, arrivals or both (`ActiveRunways`): room state, not the controller AI's single runway-in-use guess.

**Carried answer** (of the active runways):
The mentor's answer to the load prompt, or the last live `ARWY`, kept on the room per scenario (an answer of `NONE` is an empty list). A load of the same scenario starts on it; a restart starts on the list in use, and a rewind on what the session started on (`InitialActiveRunways`).

**Carrier** (of the active runways):
The room's store of carried answers, one per normalized scenario id (`TrainingRoom.FindCarriedActiveRunways` / `CarryActiveRunways`); only a load reads it.

**Implied active runways**:
The runway ends a loaded scenario implies per airport, read from its aircraft (loaded phases, expected approach, parsed preset commands, never dispatched) and its arrival generators, else the primary airport's facility knowledge (`ImpliedActiveRunways.For`). With no sidecar it pre-fills the mentor's prompt; the room takes it only where it names every runway.

**Scenario sidecar**:
An authored per-scenario JSON file, `Data/ARTCCs/{ARTCC}/Scenarios/{scenario id}.json`, carrying settings the vNAS scenario lacks (its active runways); unlike the per-airport ground sidecar.

**Pre-fill** (of the load prompt):
The implied active runways shown in the prompt's rows (`ActiveRunwaysPrefill`) for the mentor to edit or accept; a guess, not the room's list until OK sends it.

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

**Scope**:
What the router resolves before an arm runs — nothing, a callsign, a present aircraft, or the acting
position. A property of the kind, not of the recorded text.
_Avoid_: target, addressee

**Sent without a selection**:
A typed verb the desktop client sends with an empty callsign whatever aircraft is selected, because the room or the issuing position (or, for GHOST and TIMER, its own argument) addresses it: `CommandScopes.SendsWithoutSelection`. A malformed one is refused with the parser's reason, never as a missing aircraft.
_Avoid_: global command (`CommandDefinition.IsGlobal` is a different question: the pilot never answers Unable)

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

**Given-up exit**:
A taxiway a landing aircraft has said "unable" to: it is skipped by every exit search while the aircraft rolls, for the rest of that landing, until an accepted `EXIT` names it again; once stopped on the runway the aircraft may still taxi to it (`PhaseList.GivenUpExitTaxiways`, docs/landing-and-runway-exit.md).
_Avoid_: missed exit (an exit passed without an instruction is not given up)

**Initial call-up**:
The first call a spawned aircraft makes on its own to get moving: a ground spawn's "ready to taxi" (or its clearance request to a delivery student), or an untowered runway spawn's release request to a radar student. Whether and when an aircraft makes it is decided once, at scenario load, as its `InitialCallupPlan` (`InitialCallupClassifier`, docs/solo-training-pilot-speech.md); an aircraft the loader did not arm never makes one.
_Avoid_: check-in (a check-in is the call on a frequency change), initial contact (an airborne check-in still follows a release request)

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

**Offset line** (also *roll-out hold*):
The straight after a turn about's reversal, held on the taxiway edge's bearing the reversal rolled out on, a turning radius off the centreline (a diameter for a helicopter) on the inside of the turn the route makes at the node, to abeam that node, instead of steering back out to the centreline first.
`GroundNavigator.HoldsRollOutBearing` decides it and `TryLayTurnAboutRollOutLine` lays it (docs/ground/navigator.md). Among its conditions: a straight of at most six turning radii, no bar at the node, and the main gear inside the type's TDG half-width. The node turn from it is fitted to the offset so it exits on the outgoing centreline.

**Re-aim past the bend**:
A turn about's reversal aimed, with no jog, at a node on the leg out of the bend at its aim node, instead of turning about to that node, when the bend runs back against the reversal by more than 90° and the cut keeps the main gear within the type's TDG half-width of the two taxiways' centrelines (`GroundNavigator.ReAimPastTheBend`, docs/ground/navigator.md). Never past a bar.

**Square stop line**:
The straight after a turn about laid on the taxiway's centreline through a stop, from abeam the aircraft or from abeam the offset line's start when a hold short is issued on that line, and steered in its last look-ahead window at a point past the stop, so the aircraft stops on the centreline square to the bar (`GroundNavigator.LayStraightSquareToStop`, docs/ground/navigator.md).

**Taxiway design group (TDG)**:
The FAA's grouping of aircraft by main-gear width and cockpit-to-main-gear distance (1A, 1B, 2A, 2B, 3–6), read from the FAA aircraft characteristics database; AC 150/5300-13B Table 4-2 sets each group's taxiway width, of which half (12.5 ft for 1A/1B, 17.5 for 2A/2B, 25 for 3/4, 37.5 for 5/6) is the pavement a turn about is checked against. The ground layout carries no TDG, so a taxiway is assumed to be of the aircraft's own group.

**Gear fit** (for a turn about):
Whether a type has room to turn about on a taxiway of its own TDG (`TurnAboutFit.Evaluate`, docs/ground/pathfinder.md): its pivot radius is R = max(MGW/2, 0.466 × wheelbase), MGW the main-gear width, and it fits when its outer main tyre (R + MGW/2) and its nose gear (√(R² + wheelbase²)) both stay within its TDG half-width. With either figure missing only a TDG 1A non-jet fits; a type with no record fits only as a piston or helicopter.
A non-jet whose wheelbase is 0.48 to 0.9 of its length is a taildragger (the FAA record marks no tailwheel; its wheelbase runs main gear to tailwheel): it pivots on a braked main wheel, R = MGW/2, and fits when its tail-swing radius √(wheelbase² + (MGW/2)²) plus MGW is within its taxiway's width. A non-jet whose wheelbase is 0.9 of its length or more has an unusable record and is judged by its category. A PA18, C120 or C140 fits; a C180, DHC2 or wide-track twin (PA31, C402) does not.
A type that does not fit refuses a controller's TAXI that would need a turn about, in either shape, at any heading: "Unable, no room to turn around on C, request a route ahead". A C172 or C208 fits; a C25A, AT76, B738, DH8D or CRJ2 does not.
_Avoid_: lined-up jet (neither the heading nor the category decides it)

**Holding distance**:
How far from a runway's centerline its holding position markings sit: the map's `holdShortDistance`, else the width-based default (`RunwayCrossingDetector.HoldShortDistanceForWidth`). An aircraft is clear of the runway only with its tail past a bar at this distance.

**Continuation past a short bar**:
An uninstructed runway exit whose own bar is a dead-end fallback inside the holding distance carries on to the same runway's bar on the joining taxiway, e.g. OAK P → J's 28R bar (docs/landing-and-runway-exit.md).

**Exit ahead**:
A named exit the arrival can make, as the Exit left / Exit right flyouts list it (`ExitAheadDto`, the `ExitsAhead` field): a taxiway on a side that an `EL <twy>` / `ER <twy>` would be accepted for, with its distance rounded to 100 ft and a flag for the exit it means to take. Computed on the rollout and, as a forecast from the landing threshold, on the last miles of final (docs/landing-and-runway-exit.md).

**Exit capacity segment**:
The stretch of an exit taxiway between a landing runway's exit hold-short and the parallel runway's hold-short, capped by a sidecar `exitCapacity` rule; when it is full an arrival treats that exit as occupied (`ExitCapacitySegment`, docs/landing-and-runway-exit.md).

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

**Stop gap**:
The nose-to-tail distance a `FOLLOWG` follower stops at behind its lead, keyed by both aircraft: about 100 ft behind a small or prop lead, 150 ft behind a large or 757-class jet, 250 ft behind a heavy or super, plus 100 ft for a small or helicopter follower behind a jet (`FollowGap.StopGapFt`, a judgement value).
_Avoid_: follow distance (the gap is nose to tail along the path, not centre to centre)

**Close-follow band**:
The stop gap plus 150 ft: inside it, behind a lead moving away, a `FOLLOWG` follower is no slower than the lead, with a walking-pace floor to close up (`FollowGap.CloseFollowBandFt`).

**Goal-set search**:
One A* pass to whichever of several goal nodes is cheapest to reach, rather than one search per goal (`AutoRouter.RunToGoals`, `TaxiPathfinder.FindRouteToNearestGoal`, docs/ground/pathfinder.md).

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

**All Commands**:
The aircraft menu's submenu holding every command for the aircraft in one fixed order, each shown by its catalog predicate alone; the quick commands are a filtered pick from it.

**Icon strip**:
The up to ten glyph buttons (two rows of five) at the top of an aircraft menu: the glyph-bearing quick commands in list order; the rest show as text below it. A point menu opens with its own icon strip of the ground point items that apply.

**State line**:
The dimmed, disabled row under an aircraft menu's title that says what the aircraft is doing now, as segments joined with ` · ` (`Approach · 3,000 ft · 180 kt · KOAK rwy 30`, `Taxiing · C · KOAK`, `Landing · runway 28R · 62 kt`); a segment with no value is left out, and with none left there is no row (`SharedMenuGroups.StateLineItem`, phase names from `PhaseDisplayNames`).

**Label row**:
The line above an icon strip's icons that names the entry under the pointer (or keyboard focus) with the command it sends, or reads "Quick commands" when none is pointed at.

**Quick-list visibility rule**:
A rule that hides a quick command in its situation by the server's situation flags or `NextCrossingRunway` (Cross only when a runway lies next on the taxi route, Cancel takeoff clearance only before V1); quick list only, All Commands keeps the entry (`AircraftCommandApplicability.Shows*`, applied by `QuickCommandResolver`).

**Quick-list widening**:
A rule that admits a quick command in a phase its catalog predicate leaves out, once a sim acceptance test proves the sim takes the command there (`AircraftCommandApplicability.Widens*`); quick list only.

**Catalog entry (menu catalog)**:
One command the aircraft menus can offer, with a stable ID (`<group>.<item>`, never reused), a label, a default flight-rules filter, an applicability predicate and a builder (`MenuCatalogEntry` in `Yaat.Client.Core/ContextMenus/`); quick commands are lists of these IDs.

**Menu host**:
The surface that owns an aircraft menu (radar, ground, aircraft list) as a catalog entry's builder sees it (`IMenuHost`): sending the command text, and whatever popups or reads the entry needs.

**Runway entry (Taxi to runway)**:
A runway hold short the Taxi to runway submenu offers as one row, reached by the shortest route from the aircraft: the **full-length entry** at the runway's end, or an **intersection entry** whose runway remaining (shown as `~N ft avail`) is at least the type's takeoff distance (`TaxiRouteRow`, `GroundViewModel.GetTaxiToRunwayChoices`); the client's own ranking, separate from `RunwayEntryPoint`, the Sim classifier behind a queued departure's `28R@E`.

**Lazy submenu**:
A menu submenu whose items are built when it first opens, holding one disabled placeholder until then, for an entry too costly to build with every menu (`LazySubmenu`; Taxi to runway).

**Click context (menu click)**:
What a right-click gives an aircraft menu (`MenuClick`): the aircraft the menu commands, the previously selected aircraft that sends the relative items (null when it is the clicked one), the clicked point on a point click (`MenuPoint`: a map position, a taxi node, a runway end; null on an aircraft click), and the list's selected rows (`[]` on the canvases).

**Command row**:
A clickable, templated context-menu row that sends a command: an optional badge, the name, a detail, a distance and the command text (`MenuCommandRow`, drawn by `MenuCommandRowTemplate`); the Hold short of… and Follow… / Give way to… rows are command rows.

**Glyph row**:
A clickable context-menu row led by a quick-command glyph, with a label, an optional dimmed note and the command text (`MenuGlyphRow`, drawn by `MenuGlyphRowTemplate`); a dimmed one stays clickable. The pattern entries' runway flyout rows are glyph rows, the glyph rotated to the runway's true course less 270°.

**Landable (runway end) / short runway end**:
A runway end is landable for a physical aircraft type when its landing distance available is at least 1.15 times the type's landing distance, and short otherwise (`RunwayLandability`); a type with no figure, a helicopter and a `VEH*` type are always landable. The pattern entries' flyout lists landable ends first and marks short ones "short".

**Detail row**:
A disabled, dimmed label row in a menu that sends nothing (`SharedMenuGroups.DetailRow`), such as the point menu's FRD, distance and bearing line; not a command row.

**Point menu**:
The menu a right-click on empty map (radar) or on a taxi node, runway threshold or runway surface (ground) opens for the selected aircraft: on the ground, an icon strip of the taxi items that apply, then the shared `point.*` items its state allows (Fly heading, Direct to, Hold, Taxi here, Taxi to {end}, Push to, Custom taxi, Warp here), then the view's own point items (markers, Measure, FRD, Draw taxi route from the node).

**View section**:
The few canvas-only items a view adds to the shared aircraft menu, which the builder places after Squawk and before Favorite Commands (the radar's Display and Draw route, the ground's Display; the aircraft list has none); they have no catalog entry and never sit on a quick-command list.

**Menu session**:
The session settings an aircraft menu's predicates read (`MenuSession`): the user's initials, solo training mode and the VFR-commands-for-IFR mode.

**For section**:
The block at the top of an aircraft's menu, under its header, while another aircraft is selected: `For {selected} (selected)`, a line saying where the clicked aircraft is from the selected one, and the items sent as the selected aircraft (Report in sight and Follow in the air, Follow and Give way to on the ground), then `For {clicked}` over the clicked aircraft's own items (`SharedMenuGroups.AddForSection`).

**Traffic row**:
One other aircraft as a menu's traffic list shows it, seen from the aircraft the menu commands: in the air a `MenuTrafficRow` (callsign and type, distance, clock position, altitude difference), on the ground a `MenuGroundTrafficRow` (callsign, type, distance in feet, state such as `taxiing on W · ahead`).

## CRC hub connections

**Direct connection**:
A CRC hub socket identified by its own YAAT `access_token` (vEDST, TowerCab 3D) rather than by a CRC negotiate: it never joins a room, the lobby or attendance, and reaches a session only through `JoinSession` with a primary of the same CID (docs/vatsim-auth.md).

**Primary**:
The CRC connection whose session a direct connection joins; the joiner reads the primary's room.

**Negotiated joiner**:
A direct connection that also negotiated first (TowerCab 3D): it keeps its negotiate id as its connection token, so it can register for UDP entity updates, while its CID comes from its access token.

## Client settings

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

## Tooling

**At-risk tests**:
The tests outside a sim-behaviour brief's file list that pin the behaviour it changes; the brief names each with a ruling (update the expectation, keep it green unedited, or stop and report), as the `yaat-nextup` profile's Brief shape says.

**Golden (menu golden)**:
A committed text snapshot of an aircraft right-click menu for one view and one situation fixture (`tests/Yaat.Client.UI.Tests/Goldens/menu/{radar,ground,list}/<fixture>.txt`, written by `MenuTreeSnapshot`); `MenuGoldenTests` fails when a menu differs from its golden, and `YAAT_MENU_GOLDEN_REGENERATE=1` rewrites them.

**Ouroboros** (controller-voice ouroboros):
The speech sandbox's self-test (`--atc-ouroboros`): it synthesises controller transmissions from templates with a TTS voice, runs them through speech recognition and the phraseology mapper, and scores each case against the template's expected command (`tools/Yaat.SpeechSandbox/Corpus/atc-ouroboros-baseline.json`).

**SttOnly rule**:
A phraseology rule the speech mapper matches but `PhraseologyVerbalizer` never speaks: it covers how speech recognition writes a phrase (a misheard or split word), not how a pilot says it.

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

**Automation mode**:
The client's mode for being driven by an agent without disturbing the user, on when `YAAT_AUTOMATION=1` (`AutomationMode.IsEnabled`, `AutomationGate.SuppressActivation`).

Every window the client builds shows never-activated and stays in its Normal state; dialogs and message boxes open non-modal through `DialogPresenter` (message boxes through `MessageBoxPresenter`) with their owner disabled, popups draw inside their window, the client never activates itself or sets `Topmost`, and the global push-to-talk key hook and Discord Rich Presence are off (docs/client-driver-mcp.md).

**Never-activated window**:
A window shown with `ShowActivated = false` that the client never activates afterwards, so it opens behind the user's foreground window without taking focus; automation mode shows every window this way.

**Cloaked window**:
A window the client has hidden from the desktop with a DWM cloak (`DWMWA_CLOAK`) while it keeps rendering, so window capture still records it; an automation-mode client cloaks every window before its first show when `YAAT_CLOAK=1` (`launch_yaat` `cloaked`), and the app tool `set_cloaked` toggles it on a running client (docs/client-driver-mcp.md).

**Pipe host**:
The client's in-process automation endpoint (`src/Yaat.Client/Automation/AutomationHost.cs`), started on Windows in automation mode: a named pipe `yaat-automation-<pid>` open to the current user only, speaking line-delimited JSON requests `{id, method, params}` and answering a result or a coded error with a recovery hint; derived from Zafiro.Avalonia.Mcp (docs/client-driver-mcp.md).

**Discovery file**:
`%TEMP%/yaat-automation/<pid>.json`, written by the pipe host on start and deleted on exit, naming the pid, pipe name, process name, start time and protocol version, so a driver finds every running client's pipe; files whose process is gone, or whose pid now runs another program, are swept on start.

**Injected file picker**:
The file picker an automation-mode client uses in place of the native dialog (`InjectedFilePickerService`, built by `FilePickerFactory`): each open or save call takes the oldest answer an agent queued with `queue_file_pick` (a path or a cancel), and fails at once on an empty queue, so no dialog ever opens (docs/client-driver-mcp.md).
_Avoid_: file dialog (none opens)

**Node id**:
The pipe host's stable id for a window, popup or element, issued by its `NodeRegistry` and kept for as long as the element lives, so a driver can name the same element across calls.

**Ouroboros**:
A synthetic round trip through the speech pipeline: a known canonical command is rendered to speech with Piper, fed through Whisper, the rule mapper and the LLM fallback, and the recovered canonical is compared with the one it started from. `--ouroboros` speaks pilot readbacks; `--atc-ouroboros` speaks controller transmissions across every phraseology rule family and diffs each family's pass rate against a committed baseline (`tools/Yaat.SpeechSandbox`, docs/speech-recognition-pipeline.md).

**Speech telemetry**:
Push-to-talk samples (audio, per-stage transcripts, scenario context) that opted-in users' clients upload to the official yaat-server, which stores them for developers to pull with `tools/speech_telemetry.py` (docs/speech-recognition-pipeline.md).

## Facility data

**Sidecar**:
A JSON file under `src/Yaat.Sim/Data/ARTCCs/{ARTCC}/` that adds facility rules beside what the vNAS data supplies; the airport sidecar (`Airports/{airport}.json`) carries per-airport ground-routing overrides ([`ARTCC_CUSTOMIZATION.md`](ARTCC_CUSTOMIZATION.md)).

## Releases

**Feature showcase**:
A Markdown page with one short section and one screenshot per major feature a release introduces or reworks (`docs/releases/whats-new-next.md`, renamed `whats-new-<version>.md` at the cut). Every release ships one; `prepare-release` Step 5d checks it.

**Reel release**:
The Linear release `vNext reels` in the yaat pipeline, holding the sizzle reels for what `vNext` ships. It never fences work and never blocks a cut; at the cut it is renamed `<version> reels` and a fresh one opens.

**Sizzle reel**:
A short captioned video showing a major feature a release introduces or reworks, made from scripted scenes replayed in the client and recorded with the client driver, after the release is cut, in a session with no builds running (the FOLLOW reel: docs/plans/follow-video-montage.md). Reel issues live in the reel release, never in `vNext`.
