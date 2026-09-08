# Queued command dimensions — the declared table

**Shipped 2026-09-08.** Kept as the record of what each verb declares and why; delete once it has been
stable for a release or two (git history is the archive).

## What was wrong

`CommandDescriber.GetQueuedCommandDimension` was a hand-maintained `or` chain — pattern entries, approaches,
holds, and (since #422) `MLT`/`MRT` — and every other verb fell through `ClassifyCommand` to `Immediate` →
`None`. Meanwhile `CommandBlock.Dimensions` is built from `GetCommandDimension`, which reports every tower and
ground verb as `All`. So in `SplitBlockNonConflicting` the block reported a conflict in aggregate while every
one of its commands tested as non-conflicting, and the whole block survived the supersede. That is the
#336 / #422 shape, and it was live for ~50 verbs.

## What shipped

- `CommandDefinition.QueuedDimension`, a `required` property threaded through the three registry factories
  (`Bare`, `Cmd`, `PatternEntry`), so all 255 entries declare one and the compiler rejects a new command that
  does not.
- `CommandDimension.Ground` — a fourth axis outside `AllAirborne`, so `All` keeps its stored value semantics
  and the clear-everything fast path tests `AllAirborne`. Surface verbs queue as `Ground` and, since the
  2026-09-08 follow-up, fire as `Ground` too (the exit verbs and `ATXI` included); `GO`, `CTOPP` and helicopter
  `LAND`, which fired `None` and so cleared the whole queue, fire `All`; `APT` and `FOLLOW`, the same defect, fire
  `Lateral`. A registry-wide sweep in
  `QueuedCommandDimensionTests` pins queued ⊆ fired for every command type.
- `GetQueuedCommandDimension` is now a registry lookup, plus the two instance-sensitive verbs (`EXP`,
  `CFIX`) whose dimension depends on which optional argument was given, and it throws rather than
  defaulting to `None` for an unregistered verb.
- `QueuedCommandDimensionTests` — 38 cases; 13 fail against the previous classifier.

Aviation review (2026-09-08) confirmed or corrected every judgment row; its rulings are folded into the table
below and the citations kept in the Notes column.

## Behaviour changes (62 of 255 verbs)

Every row below changed. All but five are `None → something`: a queued command that used to survive every
supersede now yields to the command that replaced its plan.

| Command | Was | Now | Why |
|---|---|---|---|
| `Expedite` | `None` | `INSTANCE` | instance-sensitive: bare `EXP` is a rate modifier, `EXP <alt>` is vertical |
| `ClearedApproachForce` | `None` | `Lateral` | a lateral plan a fresh vector replaces |
| `ClearedVisualApproachForce` | `None` | `Lateral` | a lateral plan a fresh vector replaces |
| `ClimbVia` | `Vertical` | `Lateral | Vertical` | match the fired side (procedure route + constraints) |
| `CrossFix` | `Lateral` | `INSTANCE` | §4-2-5.b NOTE 1 — a restated altitude cancels the restriction |
| `DepartFix` | `Lateral` | `Lateral | Vertical` | match the fired side rather than introduce fresh drift |
| `DescendVia` | `Vertical` | `Lateral | Vertical` | match the fired side (procedure route + constraints) |
| `Follow` | `None` | `Lateral` | a lateral plan a fresh vector replaces |
| `FollowForce` | `None` | `Lateral` | a lateral plan a fresh vector replaces |
| `JoinApproachForce` | `None` | `Lateral` | a lateral plan a fresh vector replaces |
| `PositionTurnAltitudeClearance` | `Lateral` | `Lateral | Vertical` | §5-9-4.c.2 — PTAC assigns an altitude to maintain |
| `PositionTurnAltitudeClearanceForce` | `None` | `Lateral | Vertical` | §5-9-4.c.2 — PTAC assigns an altitude to maintain |
| `ChangeDestination` | `None` | `Lateral` | `ClearArrivalProcedureState` wipes `NavigationRoute` (§4-2-5.a.3) |
| `AssignRunway` | `None` | `Lateral | Ground` | no ground guard; airborne it re-assigns the arrival (§3-10-5.c) |
| `BreakConflict` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `ClearRunway` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `CrossRunway` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `ExitLeft` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `ExitRight` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `ExitTaxiway` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `FollowGround` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `GiveWay` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `HoldPosition` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `HoldShort` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `Pushback` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `Resume` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `Taxi` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `TaxiAll` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `TaxiAuto` | `None` | `Ground` | §3-7-2 surface clearance — a vector must not reach it |
| `AirTaxi` | `None` | `Ground` | §3-11-1.c defers to §3-7-2 taxi phraseology |
| `ClearedTakeoffPresent` | `None` | `Ground` | §3-11-2.a helicopter takeoff; handler requires `IsOnGround` |
| `Land` | `None` | `Lateral | Ground` | §3-11-6 covers both an airborne arrival and a surface point |
| `Cancel270` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `CircleAirport` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `ExtendPattern` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `MakeLeft270` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `MakeLeft360` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `MakeLeftSTurns` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `MakeNormalApproach` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `MakeRight270` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `MakeRight360` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `MakeRightSTurns` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `MakeShortApproach` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `OffsetLeftPattern` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `OffsetRightPattern` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `Plan270` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `TurnBase` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `TurnCrosswind` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `TurnDownwind` | `None` | `Lateral` | §3-8-1 SEQUENCE/SPACING — adjusts the horizontal path flown |
| `CancelLandingClearance` | `None` | `Lateral` | cancellation parity with `CTL` |
| `CancelTakeoffClearance` | `None` | `Ground` | cancellation parity with `CTO` |
| `ClearedForOption` | `None` | `Lateral` | P/CG OPTION APPROACH is strictly broader than `CTL` |
| `ClearedForTakeoff` | `None` | `Ground` | §5-8-2.a requires the heading before departure — sharing `Lateral` would self-delete |
| `ClearedToLand` | `None` | `Lateral` | a lateral plan a fresh vector replaces |
| `ForceLanding` | `None` | `Lateral` | a lateral plan a fresh vector replaces |
| `Go` | `None` | `Ground` | handler requires `StopAndGoPhase` — the aircraft is stopped on the runway |
| `GoAround` | `None` | `Lateral` | a lateral plan a fresh vector replaces |
| `LandAndHoldShort` | `None` | `Lateral | Ground` | §3-10-5.b — a landing clearance whose payload is a surface constraint |
| `LineUpAndWait` | `None` | `Ground` | §3-9-4 runway-occupancy instruction |
| `LowApproach` | `None` | `Lateral` | §3-8-1/§3-8-2 — a landing clearance, not an annotation |
| `StopAndGo` | `None` | `Lateral` | §3-8-1/§3-8-2 — a landing clearance, not an annotation |
| `TouchAndGo` | `None` | `Lateral` | §3-8-1/§3-8-2 — a landing clearance, not an annotation |

## Full declared table

**ASDE-X** (13) — all `None`

**Altitude / Speed** (10)

- `Speed` (6): `DeleteSpeedRestrictions`, `ForceSpeedFinal`, `Mach`, `ReduceToFinalApproachSpeed`, `ResumeNormalSpeed`, `Speed`
- `Vertical` (2): `ClimbMaintain`, `DescendMaintain`
- `INSTANCE` (1): `Expedite`
- `None` (1): `NormalRate`

**Approach** (31)

- `Lateral` (16): `ClearedApproach`, `ClearedApproachForce`, `ClearedApproachStraightIn`, `ClearedVisualApproach`, `ClearedVisualApproachForce`, `Follow`, `FollowForce`, `HoldingPattern`, `JoinAirway`, `JoinApproach`, `JoinApproachForce`, `JoinApproachStraightIn`, `JoinFinalApproachCourse`, `JoinRadialInbound`, `JoinRadialOutbound`, `JoinStar`
- `None` (9): `ExpectApproach`, `ListApproaches`, `Report`, `ReportFieldInSight`, `ReportFieldInSightForced`, `ReportTrafficInSight`, `ReportTrafficInSightForced`, `SafetyAlert`, `WakeAdvisory`
- `Lateral | Vertical` (5): `ClimbVia`, `DepartFix`, `DescendVia`, `PositionTurnAltitudeClearance`, `PositionTurnAltitudeClearanceForce`
- `INSTANCE` (1): `CrossFix`

**Broadcast** (8) — all `None`

**Consolidation** (3) — all `None`

**Coordination** (8) — all `None`

**Data Operations** (6) — all `None`

**Display Operations** (3) — all `None`

**Flight Plan** (6)

- `None` (5): `CancelIfr`, `CreateAbbreviatedFlightPlan`, `CreateFlightPlan`, `CreateVfrFlightPlan`, `SetRemarks`
- `Lateral` (1): `ChangeDestination`

**Ground** (16)

- `Ground` (15): `BreakConflict`, `ClearRunway`, `CrossRunway`, `ExitLeft`, `ExitRight`, `ExitTaxiway`, `FollowGround`, `GiveWay`, `HoldPosition`, `HoldShort`, `Pushback`, `Resume`, `Taxi`, `TaxiAll`, `TaxiAuto`
- `Lateral | Ground` (1): `AssignRunway`

**Heading** (6) — all `Lateral`

**Helicopter** (3)

- `Ground` (2): `AirTaxi`, `ClearedTakeoffPresent`
- `Lateral | Ground` (1): `Land`

**Hold** (6) — all `Lateral`

**Navigation** (10)

- `Lateral` (9): `AppendDirectTo`, `AppendForceDirectTo`, `ClearedIntoMilitaryRoute`, `ClearedOutOfMilitaryRoute`, `ClearedToConductRefueling`, `DirectTo`, `ForceDirectTo`, `TurnLeftDirectTo`, `TurnRightDirectTo`
- `Vertical` (1): `MaintainMilitaryRouteAltitudes`

**Pattern** (27)

- `Lateral` (26): `Cancel270`, `CircleAirport`, `EnterFinal`, `EnterLeftBase`, `EnterLeftCrosswind`, `EnterLeftDownwind`, `EnterRightBase`, `EnterRightCrosswind`, `EnterRightDownwind`, `ExtendPattern`, `MakeLeft270`, `MakeLeft360`, `MakeLeftSTurns`, `MakeLeftTraffic`, `MakeNormalApproach`, `MakeRight270`, `MakeRight360`, `MakeRightSTurns`, `MakeRightTraffic`, `MakeShortApproach`, `OffsetLeftPattern`, `OffsetRightPattern`, `Plan270`, `TurnBase`, `TurnCrosswind`, `TurnDownwind`
- `None` (1): `PatternSize`

**Queue** (2) — all `None`

**Sim Control** (23) — all `None`

**Strip Operations** (17) — all `None`

**Tower** (13)

- `Lateral` (8): `CancelLandingClearance`, `ClearedForOption`, `ClearedToLand`, `ForceLanding`, `GoAround`, `LowApproach`, `StopAndGo`, `TouchAndGo`
- `Ground` (4): `CancelTakeoffClearance`, `ClearedForTakeoff`, `Go`, `LineUpAndWait`
- `Lateral | Ground` (1): `LandAndHoldShort`

**Track Operations** (30) — all `None`

**Transponder** (9) — all `None`

**vTDLS** (5) — all `None`
