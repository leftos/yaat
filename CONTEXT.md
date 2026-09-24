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

**Aimed line over a fillet**:
The straight a taxi flies in place of a fillet's curve when a node-aimed entry-alignment arc rolls out pointing at the fillet's far node from off the curve: from where the aircraft stands straight to that node (`GroundNavigator.InstallAimedLineOverFillet`, docs/ground/navigator.md). It survives a snapshot (`GroundNavigatorDto.OnAimedLineOverFillet`).
_Avoid_: lead-in (a lead-in is the along-tangent shortfall before a curve the aircraft is flying)

## Live traffic

**Shadow**:
An aircraft spawned from a live real-world feed (SWIM/TAIS) that follows the feed rather than the simulation until someone assumes it (`AircraftState.IsShadow`, docs/live-traffic.md).
_Avoid_: live aircraft (ambiguous with a simulated aircraft in a live session), ghost
