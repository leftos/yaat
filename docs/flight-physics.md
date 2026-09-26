# Flight Physics: Kinematics, Airspeed Frames & Performance Constants

> Read this before touching `FlightPhysics`, `ControlTargets`, `AircraftPerformance`, `CategoryPerformance`, `WindInterpolator`, or any
> kinematics field on `AircraftState` (`IndicatedAirspeed`, `GroundSpeed`, `TrueHeading`, `TrueTrack`, `Altitude`, `VerticalSpeed`, `BankAngle`,
> `Declination`). This doc owns the integration *math* inside each per-tick physics step and the validated per-category constant table. It does
> **not** own the step *order* or tick cadence — that lives in [tick-loop.md](tick-loop.md). Link out for ordering; do not restate it here.

`FlightPhysics` is a static, per-aircraft analog integrator. Each sub-tick it nudges one `AircraftState` toward its `ControlTargets` (heading,
altitude, speed) and advances its position by the resulting ground-speed vector. There is no autopilot object and no node-based flight path: the
aircraft chases a small set of scalar targets, and "reaching" a target is detected by a snap threshold, after which the transient target self-nulls.

## Scope and what this doc does NOT own

- **Step ORDER and tick cadence** — [tick-loop.md](tick-loop.md). The server drives 1 Hz wall-clock ticks, each split into PrePhysics → Physics ×4
  (0.25 s sub-ticks) → PostPhysics; `FlightPhysics.Update` runs once per aircraft per sub-tick. This doc covers the math *inside* each step.
- **The command-queue trigger machinery** — summarized in [The command-queue half](#the-command-queue-half-updatecommandqueue--summary-only) below, detailed in
  [command-pipeline.md](command-pipeline.md) and [phases.md](phases.md).
- **Post-touchdown braking / virtual-segment rollout kinematics** — [landing-and-runway-exit.md](landing-and-runway-exit.md). This doc covers the
  shared `ControlTargets.DesiredDecelRate` contract and the `CategoryPerformance` ground-speed constants; the analog rollout itself lives there.
- **Per-tick taxi steering** — [ground/navigator.md](ground/navigator.md).
- **The full `AircraftState` field set / satellites / snapshot projections** — [aircraft-data-model.md](aircraft-data-model.md) and
  [snapshots-and-replay.md](snapshots-and-replay.md). This doc covers only the kinematics fields and the `ControlTargets` read/write boundary.

## `FlightPhysics.Update` — what runs

The public entry is `FlightPhysics.Update(aircraft, deltaSeconds, aircraftLookup, weather, soloTrainingMode, rpoShowPilotSpeech)`
(`src/Yaat.Sim/FlightPhysics.cs`, `FlightPhysics.Update`; three thinner overloads forward with defaults). In order, per call
(`FlightPhysics.cs`, `FlightPhysics.Update`):

1. Magnetic-declination cache refresh (see [Magnetic declination caching](#magnetic-declination-caching)).
2. Backward-compat IAS seed: an airborne aircraft with `IndicatedAirspeed <= 0` but `GroundSpeed > 0` copies GS into IAS (`FlightPhysics.cs`, `FlightPhysics.Update`).
3. `UpdateNavigation` — sequence the next route waypoint, fire AT-fix triggers, compute the steering heading.
4. `UpdateDescentPlanning` / `UpdateClimbPlanning` — step-altitude planners for route constraints.
5. `UpdateSpeedPlanning` — proactive speed look-ahead for procedure speed restrictions.
6. `UpdateHeading` — turn toward `TargetTrueHeading`.
7. `UpdateAltitude` — climb/descend toward the resolved altitude goal.
8. `UpdateSpeed` — accelerate/decelerate toward `TargetSpeed`.
9. `AutoCancelSpeedAtFinal` — release explicit ATC speed restrictions at 5 nm final (capping a hand-vectored inbound so it can't re-accelerate).
10. `UpdatePosition` — advance lat/lon by the ground-speed vector.
11. `UpdateCommandQueue` — evaluate `CommandBlock` triggers / advance the queue.
12. `UpdateGiveWayResume` — release a ground give-way hold when the target has passed.
13. `PilotObservationUpdater.Update` — re-check pending visual acquisitions (see the speech docs).

The numbered "8/10-step" list in [tick-loop.md](tick-loop.md) is the canonical ordering reference; the steps above are the bodies it points at.

## The ControlTargets contract

`ControlTargets` (`src/Yaat.Sim/ControlTargets.cs`) is the read/write boundary between the things that *decide* what an aircraft should do
(command handlers, phases) and the thing that *makes it happen* (`FlightPhysics`). The contract is one-directional: **handlers and phases write
`ControlTargets`; physics reads it and writes back to the kinematics fields.** Handlers and phases never move the aircraft directly (the
`ApplyForce*`/`WARP` sim-control bypasses in the dispatcher are the documented exception — see [command-pipeline.md](command-pipeline.md)).

`AircraftState.Targets` is **get-only** (`AircraftState.cs`, `AircraftState.Targets`) and restored in place via `ControlTargets.RestoreFrom(dto, ac.Targets)`
(`ControlTargets.cs`, `ControlTargets.RestoreFrom`) — you cannot reassign `ac.Targets` from a snapshot, and `NavigationRoute` (a get-only list) restores via
`Clear()` + `Add` (`ControlTargets.cs`, `ControlTargets.RestoreFrom`). See [aircraft-data-model.md](aircraft-data-model.md) for the get-only restore-in-place pattern.

### Field table

| Field | Type | Written by | Read by physics in | Lifecycle |
|---|---|---|---|---|
| `TargetTrueHeading` | `TrueHeading?` | `UpdateNavigation`, phases, `FH`/turn handlers | `UpdateHeading` | Set each tick by nav while a route exists; nulled when the route is exhausted (`UpdateNavigation`) |
| `PreferredTurnDirection` | `TurnDirection?` (Left/Right) | departure/turn handlers, phases | `UpdateHeading` (via `ResolveDirection`) | **Not** cleared per tick; cleared on heading snap, route exhaustion, or when a phase ends/is cleared (`Phase.OnEnd` + dispatcher phase-clear) |
| `TurnRateOverride` | `double?` deg/s | pattern phases, `TRATE` | `UpdateHeading`, `UpdateNavigation` anticipation | Persists; null = category default |
| `TargetAltitude` | `double?` ft MSL | `CM`/`DM`/`EXP <alt>` handlers, climb/descent planners, phases | `UpdateAltitude` (via `ResolveAltitudeGoal`) | **Self-nulls** on snap (±10 ft) — so a command that needs to know "is a climb/descent running" must ask `ResolveAltitudeGoal`, not this field |
| `AltitudeFloor` / `AltitudeCeiling` | `double?` ft MSL | "maintain VFR at/above" / "at/below" handlers | `ResolveAltitudeGoal` | Persist until cleared |
| `DesiredVerticalRate` | `double?` fpm (+ = climb) | phases (glidepath, flare, initial climb), generators and live-traffic seeding, the instructor | `UpdateAltitude` | Nulled on altitude snap and on fix revert; null = category rate. A set value is flown verbatim — expedite never scales a commanded rate (7110.65 §4-5-7 NOTE 4). (`EXPEDITE` does not write a value here — it sets `Procedure.IsExpediting`, which scales only the profile-rate branch via `CategoryPerformance.ExpediteVerticalRate`. `NORM`/CM/DM/snap clear it) |
| `PlannedVerticalRate` | `double?` fpm (+ = climb) | climb/descent planners only | `UpdateAltitude` (behind `DesiredVerticalRate`) | **Transient**: `Update` nulls it before the planners run every tick, so it exists only while a constrained fix is in the route — a vector that clears the route releases it on the next tick (issue #429: a `CFIX` rate outlived `FH` and held an A319 at 730 fpm through `DM`/`EXP`). Flown verbatim like a phase rate; not in `ControlTargetsDto` (re-derived after restore) |
| `TargetSpeed` | `double?` KIAS | `SPD`/`SLOW` handlers, speed planner, Mach hold, phases | `UpdateSpeed` | **Self-nulls** on snap (±2 kt), except a target above the 91.117 limit, which stands at the cap |
| `DesiredDecelRate` | `double?` kt/s (+ = decel) | `LandingPhase` / `RunwayExitPhase` / `PushbackPhase` (the towbar rate) | `UpdateSpeed` **decel branch only** | Must be cleared on phase transition; null = category default |
| `DesiredAccelRate` | `double?` kt/s (+ = accel) | `PushbackPhase` only (`CategoryPerformance.TugAccelRate`, 0.3 kt/s: a tow picks up speed at the towbar rate, not the aircraft's breakaway rate) | `UpdateSpeed` **accel branch only** | Re-published every tick by the phase and nulled in its `OnEnd`; null = category default |
| `SpeedFloor` / `SpeedCeiling` | `double?` KIAS | floor/ceiling handlers, AIM 5-4-1 procedural memory | `UpdateSpeed`, `UpdateSpeedPlanning`, `ApplyFixConstraints` | Persist; enforced continuously |
| `AssignedMagneticHeading` | `MagneticHeading?` | `FH`/`TL`/`TR`/`FPH`/`PTAC` | (UI/autopilot only — not consumed by the integrator) | Persists for the UI until re-vectored |
| `AssignedAltitude` | `double?` ft MSL | `CM`/`DM` | (UI/autopilot only) | Persists for the UI |
| `AssignedSpeed` | `double?` KIAS | `SPD`/`SLOW`/`RFAS` | (UI/autopilot only) | Persists for the UI |
| `HasExplicitSpeedCommand` | `bool` | `SPD` handler | guards the auto speed schedule + planner + `AutoCancelSpeedAtFinal` | Cleared on bare altitude commands |
| `HasExplicitTurnRate` | `bool` | `TRATE` handler | prevents pattern phases overwriting `TurnRateOverride` | Cleared on `TRATE` (no arg), Warp, phase-clear |
| `TargetMach` | `double?` | Mach handlers | `UpdateSpeed` (recomputes equiv. IAS) | Persists until a new speed command |
| `NavigationRoute` | `List<NavigationTarget>` | nav handlers, approach phases | `UpdateNavigation` and the planners | Front-popped on sequencing |

### `Assigned*` vs `Target*` — the snap-then-null lifecycle

This is the single most important `ControlTargets` distinction:

- **`TargetX` is the transient physics goal.** `TargetAltitude` and `TargetSpeed` **self-null the moment the goal is reached** —
  `UpdateAltitude` nulls `TargetAltitude` and `DesiredVerticalRate` and clears `IsExpediting` on the ±10 ft snap (`FlightPhysics.cs`, `FlightPhysics.UpdateAltitude`);
  `UpdateSpeed` nulls `TargetSpeed` on the ±2 kt snap (`FlightPhysics.cs`, `FlightPhysics.ArriveAtGoal`). "My target vanished" is by design. A
  `TargetSpeed` above the 14 CFR 91.117 limit is the exception: it stays standing at the cap until the uncapped speed is reached.
- **`AssignedX` is persistent UI/autopilot state.** `AssignedAltitude`/`AssignedSpeed`/`AssignedMagneticHeading` persist past the snap so the
  controller still sees the last assigned value on the datablock and the autopilot can hold it.

Heading is handled differently: `UpdateHeading` snaps `TrueHeading` to the goal and clears `PreferredTurnDirection` on the snap
(`FlightPhysics.cs`, `FlightPhysics.UpdateHeading`), but it does **not** null `TargetTrueHeading` (the nav step owns that lifecycle, nulling it only on route exhaustion).

## Airspeed frames: IAS / TAS / GroundSpeed / Mach

This is the load-bearing model the rest of physics rests on. (This material previously lived only in an archived rationale plan; it now lives here.)

- **`IndicatedAirspeed` (KIAS) is the single source of truth for airspeed** (`AircraftState.cs`, `AircraftState.IndicatedAirspeed`). It is what ATC commands, what
  `TargetSpeed` targets, and the only stored airspeed value. Set it; everything else derives.
- **ON THE GROUND the same field carries GROUNDSPEED (wheel speed)** — taxi speeds, rollout coast/exit speeds, and braking rates
  are all ground-frame quantities, the gear resists lateral wind (track = heading, no vector sum), and the ASI is only meaningful
  at roll speeds. Wind and density enter at exactly the frame flips, owned by `GroundFrame`: the takeoff roll integrates
  groundspeed and rotates when `GroundFrame.IasForGroundSpeed` (TAS = GS + headwind, density-corrected) reaches Vr — a 20 kt
  headwind lifts off ~20 kt of groundspeed earlier and shortens the roll by the v² law — and touchdown converts
  `TAS − headwind` into wheel speed (`GroundFrame.EnterGround`). Every phase that flips `IsOnGround` with meaningful speed must
  route through these helpers. `AircraftState.HeadwindKts` derives from the cached `WindComponents` (now cached on the ground too).
  The roll's acceleration is not a constant: `GroundRollProfile` (`GroundRollProfile.cs`) ramps it from the category's
  idle-thrust rate to the type's steady rate over the category's spool time (`a(t) = idle + (steady − idle)·min(t/spool, 1)`, the
  FCTM "stabilise ~40 % N1, release, advance to takeoff thrust" technique), and `TakeoffPhase` / `StopAndGoPhase` / `TouchAndGoPhase`
  each own a roll clock (`RollElapsedSeconds`, snapshotted) and add `SpeedAt(tEnd) − SpeedAt(tStart)` per sub-tick, so the discrete
  roll tracks the closed form exactly. Everything that predicts a roll — `SameRunwaySeparation.WillBeFlying`,
  `PrecedingDepartureBlock`, `RejectedTakeoff.CanOverfly/CanStopShort/ArrivalTimeSeconds` — projects on the same profile
  (`TimeAtSpeed` places an aircraft on the ramp from the speed it is making; a live-traffic shadow's measured acceleration is a
  `GroundRollProfile.Constant`), so the predictors and the integrator never disagree. A jet reaches 145 kt in 31 s / ~3,560 ft
  (29 s / ~3,550 ft with the old constant 5 kt/s — the distance is unchanged, the first five seconds are what moved).
- **TAS is computed, never stored.** `WindInterpolator.IasToTas(ias, altitudeFt)` (`WindInterpolator.cs`, `WindInterpolator.IasToTas`) converts IAS→TAS via ISA
  compressible-flow relations (CAS→Mach→TAS). TAS rises with altitude even at constant IAS.
- **`GroundSpeed` is DERIVED on every read — it has no setter** (`AircraftState.cs`, `AircraftState.GroundSpeed`). On the ground it returns `IndicatedAirspeed` directly.
  Airborne it is `|TAS·(cos/sin heading) + WindComponents|`: TAS along `TrueHeading`, plus the cached wind vector, magnitude. You cannot "set
  ground speed," and `GS == IAS` is only true on the ground or in still air at sea level.
- **`WindComponents` (N, E knots) is cached during `UpdatePosition`** (`FlightPhysics.cs`, `FlightPhysics.UpdatePosition`) so `AircraftState.GroundSpeed` can derive
  airborne GS without a `WeatherProfile` in hand. It is cached on the ground as well (feeding `HeadwindKts` for the frame flips); zero with no weather. Note: both `GroundSpeed`'s getter and `UpdatePosition`
  build the airborne GS vector by projecting TAS along the same `TrueHeading` basis and adding the wind vector — so the displayed GS magnitude
  matches the position step. (`UpdatePosition` then derives `TrueTrack` from `atan2` of that wind-summed vector, which is why track diverges from
  heading in wind.)
- **Mach hold** recomputes equivalent IAS each tick: `UpdateSpeed` reads `TargetMach`, calls `WindInterpolator.MachToIas(mach, alt)`
  (`WindInterpolator.cs`, `WindInterpolator.MachToIas`), and writes the result into `TargetSpeed` so a constant-Mach cruise descends in IAS as it climbs
  (`FlightPhysics.cs`, `FlightPhysics.UpdateSpeed`). The Mach-derived IAS is still clamped to `RegulatorySpeedLimit` — 250 below 10,000 ft, 200 under a Class B shelf.

`WindInterpolator` also provides `TasToIas` (the inverse — used for resolving cruise TAS to an IAS), `IasToMach`,
`ComputeWindCorrectionAngle`, and `GetWindComponents`. Wind layers are vector-interpolated by altitude
(`WindInterpolator.GetWindAt`) so the 0/360 boundary is handled correctly.

## Heading integration (`UpdateHeading`)

`UpdateHeading(aircraft, cat, deltaSeconds)` (`FlightPhysics.cs`, `FlightPhysics.UpdateHeading`):

1. If `TargetTrueHeading` is null → zero the bank angle and return.
2. **Ground no-pivot guard**: if `IsOnGround && GroundSpeed < StationaryGroundSpeedKts` (0.1 kt) → zero bank and return. A parked
   aircraft cannot rotate on a stale target heading. Airborne helicopters at GS 0 (hover) are exempt because `IsOnGround` is false.
3. `diff = NormalizeAngle(goal − current)` (shortest-path). If `|diff| < HeadingSnapDeg` (0.5°) → snap `TrueHeading` to the goal,
   **clear `PreferredTurnDirection`**, zero bank, return.
4. Otherwise `turnRate = TurnRateOverride ?? AircraftPerformance.TurnRate(...)`, `maxTurn = turnRate × deltaSeconds`, direction from
   `ResolveDirection(diff, PreferredTurnDirection)` (preferred direction overrides shortest path), `turnAmount = min(|diff|, maxTurn)`.
5. Bank angle: `BankAngle = atan(TAS_kts × turnRate × BankAngleCoeff) × 180/π`, signed by turn direction.
   `BankAngleCoeff = π/180 × 1.6878 / 32.174 ≈ 0.0009146` — the kt→ft/s and ft/s²-gravity unit fold.

## Lateral navigation & turn anticipation (`UpdateNavigation`)

`UpdateNavigation(aircraft, weather)` (`FlightPhysics.cs`, `FlightPhysics.UpdateNavigation`) drives `TargetTrueHeading` toward the head of `NavigationRoute`:

- **Sequencing threshold.** Fly-over and terminal waypoints sequence within `NavArrivalNm` (0.5 nm). Fly-by waypoints with a following
  leg use turn anticipation: `ComputeAnticipationDistanceNm(GS, turnRate, legBearing, nextLegBearing)` = `R·tan(θ/2)`, where `R = GS / turnRate`
  (rad/s), capped at 5 nm, 0 for turns < 1°. The sequencing threshold becomes `max(anticipation, NavArrivalNm)`.
- **Abeam sequencing.** Inside the anticipation zone, the waypoint is not popped on distance — it is popped when the along-track distance along
  the *next* leg bearing goes non-negative (`GeoMath.AlongTrackDistanceNmRaw >= 0`), i.e. the aircraft has passed abeam.
- **Arc-blended steering.** While in the anticipation zone but not yet sequencing, the steering heading is the tangent to the inscribed turn
  circle (`FlightPhysics.ComputeArcBlendedHeading`), not the straight bearing to the waypoint — so fly-by turns are smooth.
- **Wind correction angle.** When airborne with weather, the final steering heading is `bearing + WCA` where
  `WCA = WindInterpolator.ComputeWindCorrectionAngle(bearing, TAS, windFrom, windSpeed)` — the aircraft crabs so it tracks a straight
  ground path, not a downwind pursuit curve.
- **Fix-constraint apply/revert.** On sequencing, `ApplyFixConstraints` applies the next fix's altitude/speed restriction (gated by
  SID-via / STAR-via mode), and `NavigationTarget.Revert*` fields restore the prior target/assigned alt+speed when sequencing past a constrained
  fix. `FrdArrivalNm` (1.5 nm) and `GroundArrivalNm` (0.05 nm) are the FRD-point and ground-entity arrival thresholds used by the
  queue triggers, not by route sequencing.

`PreferredTurnDirection` is **not** cleared in `UpdateNavigation` — only on heading snap or route exhaustion — so departure
direction bias (`TRDCT`/`TLDCT`) survives until the initial turn completes.

## Vertical integration (`UpdateAltitude`) + step planners

`UpdateAltitude(aircraft, cat, deltaSeconds)` (`FlightPhysics.cs`, `FlightPhysics.UpdateAltitude`):

- On the ground → `VerticalSpeed = 0`, return.
- `goal = ResolveAltitudeGoal(aircraft)` clamps `TargetAltitude` between `AltitudeFloor` and `AltitudeCeiling`; if there is no
  `TargetAltitude` it synthesizes one only when the aircraft is below a floor or above a ceiling (with the snap deadband).
- `|diff| < AltitudeSnapFt` (10 ft) → snap `Altitude`, zero VS, **null `TargetAltitude` and `DesiredVerticalRate`, clear `IsExpediting`**.
- Rate = `|DesiredVerticalRate ?? PlannedVerticalRate|` if either is set (a phase's rate wins) — flown **verbatim**: per 7110.65 §4-5-7 NOTE 4 a phase/planner-commanded rate is the
  restriction and expedite never scales it. Else `AircraftPerformance.ClimbRate`/`DescentRate(...)`, where `IsExpediting` applies
  `CategoryPerformance.ExpediteVerticalRate`: climb **×1.15**, descent **×2.0**, clamped to per-category caps (jet 4,000 / TP 2,500 /
  piston climb 900, descent 1,500 / helo 1,500/1,200 fpm) with a descent floor (jet 2,500 / TP 1,500 / piston+helo 1,000), never
  reducing the base rate.
- **Level-off taper** (AIM 4-4-10.4): in free flight (no active phase, no `DesiredVerticalRate`/`PlannedVerticalRate`), the rate tapers inside the last
  1,000 ft of the goal — `min(rate, max(500, 1.5 × |diff|))` fpm — so Mode C winds down instead of cutting from full rate to level in
  one tick. Phases are exempt end-to-end: approach/landing fly profile-rate segments below 1,000 ft AGL that must not be flattened.
  `change = min(|diff|, rate/60 × delta)`; `VerticalSpeed` is signed by climb/descend.
- **Guard pattern for global `UpdateAltitude` changes.** Anything that reshapes the vertical profile must be scoped
  `profileRate && Phases?.CurrentPhase is null` — the taper's own guard. Phases own their vertical paths end-to-end (approach
  and landing fly profile-rate segments below 1,000 ft AGL), and physics must never reinterpret them: an unguarded taper
  floated turboprop touchdowns several hundred feet long (`TouchdownPointTests`) and broke SID-window and `CAPP` phase tests.

**Step climb/descent planners** run *before* the integrators (`UpdateClimbPlanning`, `UpdateDescentPlanning`). Each scans the route
for the next altitude-constrained fix, resolves the constraint via `ResolveAltitudeRestriction`, writes `TargetAltitude`, and computes
the `PlannedVerticalRate` needed to hit it at the fix: `requiredFpm = altDelta / timeMinutes`, capped at **2× the standard category rate**. Descent planning is suppressed under `SidViaMode`; climb planning under `StarViaMode` — the SID-via / STAR-via split decides which
planner owns the route. Both also activate (outside via mode) whenever the route carries any explicit altitude restriction.

## Speed integration (`UpdateSpeed`) + look-ahead planning

`UpdateSpeed(aircraft, cat, deltaSeconds)` (`FlightPhysics.cs`, `FlightPhysics.UpdateSpeed`) is a layered cascade. The layers run in this exact order; getting the order
wrong silently lets one layer stomp or lose to another:

Layers 1, 2 and 5 all clamp against the public `FlightPhysics.RegulatorySpeedLimit(aircraft)`, resolved once at the top of the
method. It takes the aircraft alone and has four arms: `double.MaxValue` on the ground or at or above 10,000 ft; `double.MaxValue` for an
aircraft whose `MilitaryRoute.SpeedLimitWaived` is set and which is **not** under a Class B shelf (the AP/1B waiver reaches 91.117(a) only);
otherwise the cap — **200 kt** when `AirspaceDatabase.IsUnderClassBShelf` puts the aircraft laterally inside a Class B footprint but below its
floor (91.117(c)), else **250 kt** (91.117(a)) — raised to `max(cap, AircraftPerformance.MinimumSafeSpeedKts)` for a type with
`AircraftPerformance.IsSpeedLimitWaived` (91.117(d): a floor under the cap, never a removal of it).

1. **Mach hold** — if `TargetMach` set and airborne, write `MachToIas(...)` into `TargetSpeed`, clamped to the regulatory limit.
2. **Floor/ceiling/91.117 self-target** (`FlightPhysics.BoundsCorrectionTarget`) — if `TargetSpeed` is null, mint a target that corrects the bound
   the aircraft is violating. Branch order is load-bearing: a `SpeedFloor` it is under, then a `SpeedCeiling` it is over, then 91.117 itself.
   The regulatory limit clamps the *effective* floor and ceiling, so a `SpeedFloor` above the cap never holds the aircraft up there. The
   regulatory arm is airborne-only: an aircraft with no target whose IAS exceeds the limit by more than `SpeedSnapKts` slows to it at the
   ordinary decel rate, with no transmission — pilots comply with 91.117 without notification (7110.65 §5-7-2.a NOTE 1, §5-7-3.f NOTE 1).
   Because the ceiling branch runs first, an aircraft over both lands on `min(cap, SpeedCeiling)`.
3. **Auto altitude-band schedule** — if `TargetSpeed` is null, airborne, **not** `HasExplicitSpeedCommand`, `ActiveApproach` is null, the current
   phase does **not** have `ManagesSpeed == true`, and the aircraft is climbing/descending toward a target altitude → set `TargetSpeed` to
   `AircraftPerformance.DefaultSpeed(...)`, honoring an active `SpeedCeiling`. This is the layer the approach/pattern phases suppress.
4. **Ground `SpeedLimit` clamp** — `goal = min(goal, Ground.SpeedLimit)` when on the ground; this is the ground-conflict cap from
   [tick-loop.md](tick-loop.md)'s `GroundConflictDetector`.
5. **14 CFR 91.117** — `goal = min(goal, regulatoryLimit)`: 250 below 10,000 ft, 200 under a Class B shelf. The layer first
   records `heldShortByRegulatoryLimit = goal > regulatoryLimit` — the cap holding the aircraft short of the speed it was told to fly — for
   layer 7.
6. **`SpeedCeiling` continuous clamp** — `goal = min(goal, SpeedCeiling)` again, so even a non-procedural `TargetSpeed` (auto schedule,
   pre-ceiling controller assignment) cannot escape the cap.
7. **Snap + integrate** — `|diff| < snapWindow` → `ArriveAtGoal` snaps IAS and **nulls `TargetSpeed` unless
   `heldShortByRegulatoryLimit`**: a target above the regulatory limit stays standing at the cap, whichever clamp the aircraft actually
   settles on (a lower `SpeedCeiling` included), and is nulled only when the uncapped target is reached — so the aircraft takes its assigned
   or restored speed back up when the cap lifts (leaving the shelf, climbing through 10,000 ft; 220 → 250 → 280 as a ceiling and then the
   cap unwind). A target at or under the limit that a `SpeedCeiling` or the ground `SpeedLimit` stops retires on arrival. Only ATC ends a
   speed assignment (7110.65 §5-7-4), and a pilot must not fly an ATC speed that exceeds 91.117 (AIM 4-4-12.i; 200 kt beneath Class B,
   AIM 4-4-12.j and 4-4-12.k NOTE). The window is `SpeedSnapKts` (2 kt) airborne but
   `rate × deltaSeconds` on the ground, so a taxiing aircraft never snaps further than the one sub-tick it would have integrated anyway
   (2 kt is two whole seconds of taxi acceleration). Otherwise accelerate/decelerate at `SpeedChangeRate`: airborne
   `DesiredAccelRate ?? AircraftPerformance.AccelRate` / `DesiredDecelRate ?? AircraftPerformance.DecelRate`; on the ground
   `DesiredAccelRate ?? CategoryPerformance.TaxiAccelRate` / `DesiredDecelRate ?? CategoryPerformance.TaxiDecelRate` — physics is the only
   integrator of ground speed; ground phases and the navigator only publish targets. Each override is honored **only on its own branch**:
   `DesiredDecelRate` is ignored when accelerating and `DesiredAccelRate` when decelerating. A tug move publishes both
   (`CategoryPerformance.TugAccelRate` 0.3 kt/s / `TugDecelRate` 1.0 kt/s), so nothing on a towbar starts or stops at the taxi rates.

**Look-ahead planning** (`FlightPhysics.UpdateSpeedPlanning`) runs before the integrator and pre-sets `TargetSpeed` so the aircraft *arrives* at a
procedure speed restriction at the constrained fix rather than reacting after it: it computes change-time vs time-to-fix and starts decel only
when within 10% of the change time (accel starts immediately). It is fully suppressed when `HasExplicitSpeedCommand`, `SpeedRestrictionsDeleted`,
or `TargetMach` is set.

A `CFIX <fix> <speed>` "cross at" restriction is an ATC-assigned speed that **persists past the fix** (7110.65 §5-7-1.d): the
crossed restriction is published as a `ControlTargets.SpeedCeiling` (in `UpdateNavigation` at fix sequencing, and in the `DEPART`
block's `ApplyAction`), so a bare vector does not cancel it — only an approach / climb-via / descend-via clearance does. Fix-derived
target speeds are floored at the type's approach speed (`ClampFixSpeedToApproachFloor` → `AircraftPerformance.ApproachSpeed`,
140/110/75/70 default) so no procedure restriction can command an unflyable speed. Multi-`CFIX` presets compose into one sequential
`;` compound (`SimulationEngine.DispatchPresetCommands`), so each subsequent `CFIX` becomes a deferred "at &lt;previous fix&gt;" queue block.

**`AutoCancelSpeedAtFinal`** runs right after `UpdateSpeed`: for an aircraft **inbound to land**
(`ApproachCommandHandler.IsInboundToLand`) and on final (`IsOnFinal` — pattern traffic flies its whole circuit inside 5 nm) at
≤ 5 nm from the assigned runway's **landing** threshold it releases the *explicit ATC* speed
restriction (gated on `HasExplicitSpeedCommand`), per 7110.65 §5-7-1.b.4 — the controller can no longer adjust the speed, so
the pilot owns the approach speed. Both the distance and the on-final test read `LandingThreshold.Resolve(runway, aircraft.Ground.Layout)`,
the same datum `SPD`'s 5 nm-final rejection uses, so the command and the gate that releases it agree on where the five miles
start; on a displaced runway that is downfield of the pavement end (KSJC 30L: 0.42 nm), and with no layout it is the pavement
threshold. The choice of datum is a judgement call — see
[landing-and-runway-exit.md](landing-and-runway-exit.md#displaced-thresholds-which-datum). **It never lets the aircraft accelerate:**
- If a phase owns speed (active approach, pattern leg, or final — `ActiveApproach != null` or `CurrentPhase.ManagesSpeed`), the
  assignment is cleared outright (`TargetSpeed`/`HasExplicitSpeedCommand`/`SpeedFloor`/`SpeedCeiling` nulled); the phase already
  drives the approach speed and never re-accelerates, so no cap is needed.
- Otherwise (a hand-vectored inbound with the auto speed schedule live), the last-assigned speed is retained as a
  `SpeedCeiling = min(last assigned, current IAS)` so the aircraft holds it or eases down to its natural approach speed
  (continuing any in-progress reduction to a lower assigned speed) but can never be pushed back up toward the descent default.

Departures and go-arounds are not inbound to land and keep their assigned speed; a forced assignment (`SPEEDF`/`SPEEDN`, flagged
by `Targets.SpeedOverridesFinalGate`) is also exempt. The retained ceiling is cleared when the aircraft transitions to a
climb-out (`GoAroundPhase`, `TouchAndGoPhase`, `StopAndGoPhase`, `LowApproachPhase` clear it in `OnStart`).

The **AIM 5-4-1 NOTE 2 procedural-speed memory**: when the route is exhausted, `UpdateNavigation` publishes the last procedure speed
(`Procedure.LastProcedureSpeedKts`) as a `SpeedCeiling` so the auto schedule cannot accelerate the aircraft above the last published
speed — unless an explicit ATC speed is active.

## Position integration (`UpdatePosition`)

`UpdatePosition(aircraft, deltaSeconds, weather)` (`FlightPhysics.cs`, `FlightPhysics.UpdatePosition`) advances lat/lon with a flat-earth approximation
(`NmPerDegLat = 60`, longitude scaled by `cos(lat)`):

- **Ground branch**: re-enforce `Ground.SpeedLimit` on IAS, move along `Ground.PushbackTrueHeading ?? TrueHeading` at
  `IAS / 3600` nm/s, and set `TrueTrack = TrueHeading` (track follows heading directly; GS = IAS on the ground).
- **Airborne branch**: `TAS = IasToTas(IAS, alt)`; ground-speed vector = `TAS·(cos/sin heading) + wind`; **cache `WindComponents`**;
  `TrueTrack = atan2(gsE, gsN)`; displace by the full GS vector. This is where wind makes track diverge from heading.

## Performance constants — the two-tier lookup

Production code **does not read `CategoryPerformance` directly** for profile-covered fields. The entry point is `AircraftPerformance.*`
(`src/Yaat.Sim/AircraftPerformance.cs`):

1. **Per-type profile** — `AircraftProfileDatabase.Get(aircraftType)` returns the `AircraftProfiles.json` profile, which carries
   altitude-breakpoint values interpolated by `InterpolateByAltitude` (`AircraftPerformance.cs`, `AircraftPerformance.InterpolateByAltitude`) and may be adjusted by an
   `IProfileCorrectionAdapter` (default pass-through; an installable adapter, e.g. Eurocontrol, can correct climb/approach/pattern speeds and
   climb rates at runtime via `SetProfileCorrectionAdapter`). A committed **override layer**
   (`AircraftProfileOverrides.json`) merges authoritative per-type corrections on top — overridden fields bypass the correction adapter, and a
   type with no base profile (e.g. SF50) gets a `CategoryPerformance.BaselineProfile(cat)` base. See [`aircraft-performance.md`](aircraft-performance.md).
2. **Category fallback** — when no profile (and no override) exists for the type, `AircraftPerformance.*` falls back to the validated `CategoryPerformance` switch.

So **editing the `CategoryPerformance` switch alone does not change behavior for a typed aircraft that has a profile.** Category determination is
`AircraftCategorization.Categorize(aircraftType)` (`AircraftCategory.cs`, `AircraftCategorization.Categorize`): strips the `H/J/S` wake prefix (`AircraftState.StripTypePrefix`),
looks up the type, then tries `AircraftSiblingMap.TryResolve`, and finally **falls back to `AircraftCategory.Jet`** for unknown types.

### The validated category table (supersedes CLAUDE.md's 3-category summary)

There are **four** categories — Jet, Turboprop, Piston, **Helicopter** (`AircraftCategory.cs`, `AircraftCategory`). All values are
aviation-sim-expert-validated against the AIM / FAA 7110.65. Airborne kinematics constants from `CategoryPerformance`:

| Constant (method) | Jet | Turboprop | Piston | Helicopter |
|---|---|---|---|---|
| `TurnRate` (deg/s, enroute) | 2.5 | 3.0 | 3.0 | 5.0 |
| `ClimbRate` (fpm, < 10k / ≥ 10k) | 2500 / 1800 | 1500 / 1200 | 700 / 500 | 1200 / 800 |
| `DescentRate` (fpm) | 1800 | 1200 | 500 | 800 |
| `AccelRate` (kt/s) | 2.5 | 1.5 | 1.0 | 2.0 |
| `DecelRate` (kt/s) | 3.5 | 2.5 | 2.0 | 3.0 |
| `InitialClimbSpeed` (KIAS) | 180 | 130 | 80 | 60 |
| `InitialClimbRate` (fpm) | 3000 | 1800 | 800 | 1200 |
| `RotationSpeed` Vr (kt) | 150 | 110 | 65 | 0 |
| `GroundAccelRate` (kt/s, takeoff roll at takeoff thrust) | 5.0 | 4.0 | 2.7 | 2.0 |
| `GroundAccelIdleRate` (kt/s, at brake release) | 1.0 | 0.8 | 0.5 | 2.0 |
| `GroundAccelSpoolSeconds` (s, idle → takeoff thrust) | 5 | 4 | 3 | 0 |

`DefaultSpeed(cat, altitude)` (`AircraftCategory.cs`, `CategoryPerformance.DefaultSpeed`) — the auto-schedule speed by altitude band:

| Category | Speed schedule (KIAS by altitude) |
|---|---|
| Jet | < 10k → 250 · < 18k → 280 · < 28k → 290 · ≥ 28k → 280 |
| Turboprop | < 10k → 200 · < 24k → 250 · ≥ 24k → 270 |
| Piston | < 10k → 110 · ≥ 10k → 120 |
| Helicopter | < 10k → 100 · ≥ 10k → 120 |

Snap thresholds are constants on `FlightPhysics`: heading `HeadingSnapDeg = 0.5°`, altitude `AltitudeSnapFt = 10 ft`, speed
`SpeedSnapKts = 2 kt` (`FlightPhysics.cs`, `FlightPhysics.SpeedSnapKts`).

`CategoryPerformance` carries many more constants used by the pattern, ground, and rollout subsystems — pattern geometry (`PatternSizeNm`,
`CrosswindExtensionNm`, `BaseExtensionNm`, `PatternTurnRate`, `PatternDescentRate`, `MaxPatternDescentRate`, `MaxPatternDescentAngleDeg`, `DownwindSpeed`, `BaseSpeed`), holding (`MaxHoldingSpeed`),
and the full taxi/exit/crossing speed set (`TaxiSpeed`, `TaxiDecelRate`, `RolloutDecelRate`, `ComfortableExitDecelRate`, `TouchAndGoDecelRate`, `ExitTurnOffSpeed`, …). Those are owned by the
pattern-geometry, ground, and landing docs; this doc owns the airborne kinematics rows above plus the shared `DefaultSpeed` schedule.

## The command-queue half (`UpdateCommandQueue`) — summary only

`UpdateCommandQueue(aircraft, deltaSeconds, aircraftLookup)` (`FlightPhysics.cs`, `FlightPhysics.UpdateCommandQueue`) evaluates the `CommandQueue` after position integration.
Full depth is in [command-pipeline.md](command-pipeline.md) and [phases.md](phases.md); the physics-relevant facts:

- **While a phase is active** (`Phases.CurrentPhase != null`), block *advancement* and untriggered-block *application* are skipped — phases own
  `ControlTargets`. Only conditional triggers are still watched (`FlightPhysics.ApplyReadyConditionalBlocks`), so e.g. `SPD 210 UNTIL 10` can fire its
  block mid-approach.
- **Trigger types** (`FlightPhysics.IsTriggerMet`): `ReachAltitude`, `ReachFix`, `InterceptRadial`, `ReachFrdPoint`, `GiveWay`, `DistanceFinal`,
  `OnHandoff`, `AtGroundEntity`, `EnteringHoldingAfterExit`, `AfterRunwayCrossing`, `AfterCycleTerminator` (`OTG`: latches on a
  touch-and-go / stop-and-go / low approach / go-around phase and fires once the aircraft has left it and is airborne; a full-stop landing marks it `TriggerMissed` and `DiscardMissedCycleTerminatorBlocks` drops it and its chain remainder (`DiscardChainRemainder`) with an "unable — landed full stop" warning at the next queue update; P/CG UNABLE, 7110.65 §3-8-2).
- **The three `Notify*` hooks** are the *only* way a queued block fires while a phase owns control: `NotifyFixSequenced` (from route/
  approach sequencing), `NotifyGroundEntityReached` (from `TaxiingPhase`), `NotifyPhaseAdvanced` (from `PhaseRunner`). Forget the
  hook and a sequential compound like `TAXI…;CTO` sits untouched until the next user dispatch.

## Magnetic declination caching

The first thing `Update` does is refresh `aircraft.Declination` from the WMM (`FlightPhysics.RefreshDeclinationCache`, public so the
live-traffic kinematics that bypass `Update` can share it) — but only when the aircraft has moved more than a 0.02° position box (≈ 1.2 nm of
latitude). WMM is a degree-12 spherical-harmonic evaluation and, at 4 Hz per aircraft, dominates per-tick
physics cost; declination changes slowly enough that sub-nm motion can reuse the cached value. Behind that per-aircraft gate, `MagneticDeclination`
itself memoizes results on a process-wide 0.02° grid (evaluated at cell centres, so the value never depends on evaluation order) — see
[weather-and-wind.md](weather-and-wind.md#magnetic-declination-magneticdeclinationcs).

If `Position` is non-finite or out of range (`|lat| > 90`, `|lon| > 180`), the WMM update is **skipped and logged** (`FlightPhysics.RefreshDeclinationCache`) rather than throwing
— a `Geo.Coordinate` ctor would otherwise crash the tick. The previously cached value is kept. `DeclinationCachePosition` is `[JsonIgnore]`
(`AircraftState.cs`, `AircraftState.DeclinationCachePosition`), so the first tick after a snapshot restore always runs the full WMM eval to re-warm the cache.

## Footguns & gotchas

- **`IndicatedAirspeed` is the only airspeed source of truth; `GroundSpeed` has no setter.** It is recomputed on every read from TAS + cached
  `WindComponents`. Trying to "set ground speed," or assuming `GS == IAS` airborne, is wrong — they diverge with altitude (TAS) and wind.
- **`TargetSpeed` and `TargetAltitude` self-null on arrival** (±2 kt / ±10 ft). "My target vanished" is by design. `AssignedSpeed`/
  `AssignedAltitude` persist for the UI/autopilot; `TargetX` is the transient physics goal. Don't conflate them. The one exception is a
  `TargetSpeed` above the 14 CFR 91.117 limit, which stays standing at the cap (the 91.117 bullet below).
- **`UpdateSpeed` is a fixed 7-layer cascade** (Mach hold → floor/ceiling/91.117 self-target (`BoundsCorrectionTarget`) → auto altitude-band
  schedule (suppressed by `ActiveApproach` OR `ManagesSpeed`) → ground `SpeedLimit` clamp → 91.117 cap (`RegulatorySpeedLimit`: 250 below
  10,000 ft, 200 under a Class B shelf; sets the held-short flag) → continuous `SpeedCeiling` clamp → snap+integrate (`ArriveAtGoal`)). Add a
  new speed influence at the wrong layer and it silently loses to or stomps another.
- **14 CFR 91.117 bends a speed assignment; it never cancels it.** A `TargetSpeed` above `RegulatorySpeedLimit` is kept when the aircraft
  arrives at the cap and nulled only when the uncapped target is reached, so the aircraft takes its assigned or restored speed back up when
  the cap lifts — leaving a Class B shelf, climbing through 10,000 ft. Only ATC ends an assignment (7110.65 §5-7-4; §5-7-4.a NOTE: "resume
  normal speed" does not relieve 91.117), and the pilot complies with 91.117 without notification (§5-7-2.a NOTE 1) and must not fly an ATC
  speed that exceeds it (AIM 4-4-12.i; 200 kt beneath Class B, AIM 4-4-12.j and 4-4-12.k NOTE). The other half is the re-bite: an airborne
  aircraft with **no** target whose IAS is over the limit by more than `SpeedSnapKts` gets a self-target at `min(cap, SpeedCeiling)` and
  slows at the ordinary rate, silently. Consequences, all reviewed and intended:
  - **A null `TargetSpeed` is not the only "speed command done" signal.** A chained `SPD` completes on
    `TargetSpeed is null || FlightPhysics.IsSpeedAssignmentHeldAtRegulatoryLimit(aircraft)` (`UpdateBlockCompletion`): `SPD 210; H 090` under
    a shelf advances once the aircraft has settled at 200 with 210 still standing. The settled test is against
    `min(limit, SpeedCeiling)`, not the assignment, so an aircraft still slowing toward the cap keeps the chain waiting.
  - **`InitialClimbPhase` writes its climb speed only when `TargetSpeed is null`**, so a retained assignment keeps it quiet for as long as
    the assignment stands.
  - **The generator stream's `RestoreManagedSpeed` skips while a target stands** — its own capped restore included; the standing target
    does the re-acceleration.
  - **`TargetSpeed` can show the regulatory limit on an aircraft nobody assigned a speed** (data block, aircraft list, snapshot): that is
    the re-bite's self-target, and it nulls once the aircraft is down to the limit.
  - **The sim tolerates limit + 2 kt indefinitely** — the re-bite threshold is the snap window, and an aircraft inside it has no target to
    fly.
  - **An approach clearance still clears a retained target** (`ApproachCommandHandler`; 7110.65 §5-7-1.d / AIM 4-4-12.g), and the
    final-approach speed schedule overwrites whatever stands before touchdown.
- **A `SpeedCeiling` is a one-way ratchet under a `ManagesSpeed` phase.** The floor/ceiling self-target drags IAS down to the ceiling,
  the snap nulls `TargetSpeed`, and with the auto schedule suppressed nothing raises IAS again when the ceiling rises or is removed —
  the aircraft holds the ceiling speed until the phase writes its own target. `FinalApproachPhase` writes none before its deceleration
  stages, so whoever stamps a ceiling on an aircraft on a long final must also restore its speed (the generator stream's
  `RestoreManagedSpeed`, `docs/scenario-loading-and-generation.md`). `RNS` closes it for itself: `FlightCommandHandler.ApplyResumeNormalSpeed` writes the scheduled final-approach speed back for an aircraft in `FinalApproachPhase` outside `ArrivalSpacingManager.SpeedRestoreGateNm` and more than `SpeedRestoreDeadbandKts` slow (AIM 4-4-12.f.1). When the aircraft is on that profile and either test fails, nothing is handed back — re-accelerating it a few miles before the phase slows it again is what §5-7-1's lead ("Avoid adjustments requiring alternate decreases and increases") and §5-7-1.a.3(e) rule out, and AIM 4-4-12.f scopes "resume normal speed" to before an approach clearance — and the instructor's answer says so: `Resume normal speed — already on its final approach speed profile, no change` instead of the plain `Resume normal speed` every other case gets (the pilot readback is the same either way; `ResumeNormalSpeedOnFinalTests`). Both restores are one-shot writes, and one write is enough under a lower regulatory cap (a Class B shelf's 200 kt, 91.117(c)): the restored target stays standing at the cap and the aircraft takes it up once the cap lifts (the 91.117 bullet above).
- **There are FOUR aircraft categories — Jet, Turboprop, Piston, Helicopter.** CLAUDE.md's summary lists only the first three; the Helicopter
  column is real and aviation-reviewed. Unknown ICAO types fall back to **Jet** (after the sibling-map attempt).
- **Constants are NOT read from `CategoryPerformance` directly in production.** `AircraftPerformance.*` is the entry point: per-type profile with
  altitude-breakpoint interpolation + `IProfileCorrectionAdapter`, falling back to `CategoryPerformance` only when no profile exists. Editing the
  `CategoryPerformance` switch alone won't change behavior for a typed aircraft that has a profile.
- **`PreferredTurnDirection` is intentionally NOT cleared each tick in `UpdateNavigation`** — only on heading snap (`UpdateHeading`) or route
  exhaustion. Clearing it per tick would stomp departure-phase direction bias (`TRDCT`/`TLDCT`) before the initial turn completes.
- **`UpdateHeading` refuses to rotate a ground aircraft below `StationaryGroundSpeedKts` (0.1 kt)** — the last defense against a parked aircraft
  pirouetting on a stale `TargetTrueHeading`. Airborne helicopters at GS 0 (hover) are exempt because `IsOnGround` is false.
- **Step climb/descent and speed look-ahead planners run BEFORE the integrators and overwrite `TargetAltitude`/`TargetSpeed` from upcoming route
  constraints.** The *speed* look-ahead (`UpdateSpeedPlanning`) self-suppresses when an explicit ATC command is active
  (`HasExplicitSpeedCommand` / `SpeedRestrictionsDeleted` / `TargetMach` guards). The *altitude* planners (`UpdateClimbPlanning` /
  `UpdateDescentPlanning`) have **no** explicit-command guard — they overwrite `TargetAltitude` whenever the route carries an altitude constraint
  (or via mode is on), so a bare `CM`/`DM` altitude you expect to stick can be re-derived by a planner the moment the route has a constrained fix.
- **`DesiredDecelRate` is honored only on the deceleration branch of `UpdateSpeed` and ignored when accelerating; `DesiredAccelRate` is the
  mirror.** Both must be cleared on phase transition or firm braking (or a tug's 0.3 kt/s crawl) leaks into the next phase. See
  [landing-and-runway-exit.md](landing-and-runway-exit.md) and [ground/pushback.md](ground/pushback.md).
- **Magnetic declination is cached with a 0.02° box; a non-finite/out-of-range `Position` SKIPS the WMM update (logged) and keeps the stale
  value** rather than throwing — so an upstream bug can leave declination subtly stale without an obvious crash. `DeclinationCachePosition` is
  `[JsonIgnore]`, so the first tick after a snapshot restore re-runs the full WMM eval.
- **`UpdatePosition` uses a flat-earth approximation** (`NmPerDegLat = 60`, longitude scaled by `cos(lat)`); fine per-tick but not a great-circle
  step — don't reuse it for long-range geometry.
- **While a phase is active, `UpdateCommandQueue` skips block advancement.** Conditional triggers still fire, but the three `Notify*` hooks are
  the only way a queued block fires during a phase. Forget the hook and a sequential compound sits untouched.
