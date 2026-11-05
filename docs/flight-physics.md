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
| `TargetSpeed` | `double?` KIAS | `SPD`/`SLOW` handlers, speed planner, Mach hold, phases | `UpdateSpeed` | **Self-nulls** on snap (±2 kt), except a target above the speed limit (91.117 or the type's Vmo/Mmo), which stands at the cap |
| `DesiredDecelRate` | `double?` kt/s (+ = decel) | `LandingPhase` / `RunwayExitPhase` / `PushbackPhase` (the towbar rate) | `UpdateSpeed` **decel branch only** | Must be cleared on phase transition; null = category default |
| `DesiredAccelRate` | `double?` kt/s (+ = accel) | `PushbackPhase` only (`CategoryPerformance.TugAccelRate`, 0.3 kt/s: a tow picks up speed at the towbar rate, not the aircraft's breakaway rate) | `UpdateSpeed` **accel branch only** | Re-published every tick by the phase and nulled in its `OnEnd`; null = category default |
| `SpeedFloor` / `SpeedCeiling` | `double?` KIAS | floor/ceiling handlers, AIM 5-4-1 procedural memory | `UpdateSpeed`, `UpdateSpeedPlanning`, `ApplyFixConstraints` | Persist; enforced continuously. A phase that clears a controller bound equal to `AssignedSpeed` (go-around, low approach, procedure turn, military route, AR anchor) ends that assignment with it (`FlightCommandHandler.EndBoundWithTheAssignment`), so a `SPD n+` / `SPD n-` bound is not left behind as an exact speed; the procedure turn clears only a 200 kt ceiling it imposed itself (`ProcedureTurnPhaseDto.CeilingImposed`), so an exact `SPD 200` survives the turn |
| `AssignedMagneticHeading` | `MagneticHeading?` | `FH`/`TL`/`TR`/`FPH`/`PTAC` | (UI/autopilot only — not consumed by the integrator) | Persists for the UI until the aircraft leaves the vector (direct, route and procedure joins, approach clearances, hold start) or is re-vectored |
| `AssignedAltitude` | `double?` ft MSL | `CM`/`DM`, `FA`, `CFIX`, `CAPP`/`PTAC` (crossing altitude, except a CFIX whose bound the assigned altitude already lies strictly beyond, which keeps it; PTAC always), `CTO`, military-route clearances, `WARP`, live-traffic assume | (UI/autopilot only) | Persists for the UI |
| `AssignedSpeed` | `double?` KIAS | `SPD`/`SLOW`/`RFAS` | (UI/autopilot only) | Persists for the UI |
| `HasExplicitSpeedCommand` | `bool` | `SPD`, `MACH`, `RFAS`, `SPDN` handlers | guards the auto speed schedule + `AutoCancelSpeedAtFinal`; the planner under it plans only controller-set crossings | Cleared by `RNS`, `RPS`, `DSR`, `DVIA`/`CVIA`, an approach clearance and the §5-7-1.b.4 auto-cancel on final; a new speed replaces it; an altitude clearance (`CM`, `DM`, VFR at-or-above/below) keeps it |
| `HasExplicitTurnRate` | `bool` | `TRATE` handler | prevents pattern phases overwriting `TurnRateOverride` | Cleared on `TRATE` (no arg), Warp, phase-clear |
| `SpeedCommandIsControllerIssued` | `bool` | `SPD`, `SPEEDF`, `MACH`, `RFAS`, `SPDN` when the dispatch is not scenario-scripted | generator stream restore (`SimulationEngine.Generators`), same-runway arrival protection | Cleared where a command clears `HasExplicitSpeedCommand` (`RNS`, `RPS`, `DSR`, `DVIA`/`CVIA`, an approach clearance, the §5-7-1.b.4 auto-cancel), not where a phase does |
| `SpeedOverridesFinalGate` | `bool` | `SPEEDF`, `SPDN` (set); a plain `SPD`, `MACH`, `RFAS`, `RNS`, `DSR`, a `CFIX` or constrained `DCTF` with a speed clear it | `AutoCancelSpeedAtFinal`, `FlightCommandHandler.SpeedRevertForbidden` | A forced speed survives the §5-7-1.b.4 auto-cancel on final |
| `SpeedLimitsLifted` | `bool` | `SPDN`, `SPDN RFAS` (set) | `UpdateSpeed` (`EffectiveSpeedLimit`, `IsSpeedAssignmentHeldAtSpeedLimit`) | Lifts 91.117 (the 200 kt Class B shelf included) and Vmo/Mmo. Cleared by `SPD`, `SPEEDF`, `MACH`, `RFAS`, `DSR`, `RNS`, `RPS`, `DVIA`/`CVIA`, an approach clearance, `CFIX` or a constrained `DCTF` with a speed, and `WARP`; kept by a refused speed, an altitude-only `CFIX`, `CM`/`DM`, a phase change and landing. Always set with `SpeedOverridesFinalGate`; written to the snapshot only when true |
| `SpeedMaxHeld` | `bool` | `SMAX` (set, `FlightCommandHandler.ArmSpeedMax`; re-armed past the route-order-last crossing fix with a controller-set speed, the one fix whose `NavigationTarget.RevertSpeedMax` is set by `NavigationCommandHandler.PlaceSpeedMaxRevert`; every earlier crossing speed is flown first, and a later transmission's `SPD`, `MACH`, `RNS`, `RPS`, `DSR`, `SPDN` or `RFAS` clears the re-arm via `NavigationCommandHandler.CancelEarlierCrossingSpeeds`) | `HoldMaximumForwardSpeed` (start of `UpdateSpeed`), `SpeedLimitWithin` | SMAX mode flag: each tick, airborne, `HoldMaximumForwardSpeed` writes the type's maximum forward speed, a margin under the redline (AIM 4-4-12.c; 7110.65 5-7-1.g NOTE 1), into `AssignedSpeed`/`TargetSpeed` (Vmo - 10 kt, 0.9 × Vne for a helicopter, `FlightPhysics.SpeedMaxKts`) or `TargetMach` (Mmo - 0.02, where its IAS equivalent is below that speed; B738 330 kt / M.80), only when the mode changes at the IAS/Mach crossover. Skips 91.117 and stays under Vmo/Mmo, which still bound `SPD`/`MACH` refusals. Cleared by `SPD`, `SPEEDF`, `MACH`, `SPDN`, `RFAS`, `RNS`, `RPS`, `DSR`, `DVIA`/`CVIA`, an approach clearance, `CFIX` or a constrained `DCTF` with a speed, `WARP`, the final-gate auto-cancel, touchdown (`FlightCommandHandler.CancelAssignedSpeedAtTouchdown`), and any other act that cancels the assigned speed; written to the snapshot only when true |
| `TargetMach` | `double?` | Mach handlers | `UpdateSpeed` (recomputes equiv. IAS) | Persists until a new speed command, a handover to an IAS (`FlightPhysics.HandOverMachHold`), or `FlightCommandHandler.EndMachHoldAndSpeedMax` (`WARP`, a hold reaching its fix, a crossing speed kept by a same-transmission speed instruction); a `CFIX` or constrained `DCTF` with a speed ends only SMAX's hold (`FlightCommandHandler.EndSpeedMaxHold`) and leaves the Mach live until `FlightPhysics.PlanCrossingUnderMach` ends it at the crossing; an altitude-only `CFIX` keeps it. A `CFIX` or constrained `DCTF` speed that replaced the hold has it restored past the fix (`NavigationTarget.RevertMach`, see Mach hold below) |
| `NavigationRoute` | `List<NavigationTarget>` | nav handlers, approach phases | `UpdateNavigation` and the planners | Front-popped on sequencing |

### `Assigned*` vs `Target*` — the snap-then-null lifecycle

This is the single most important `ControlTargets` distinction:

- **`TargetX` is the transient physics goal.** `TargetAltitude` and `TargetSpeed` **self-null the moment the goal is reached** —
  `UpdateAltitude` nulls `TargetAltitude` and `DesiredVerticalRate` and clears `IsExpediting` on the ±10 ft snap (`FlightPhysics.cs`, `FlightPhysics.UpdateAltitude`);
  `UpdateSpeed` nulls `TargetSpeed` on the ±2 kt snap (`FlightPhysics.cs`, `FlightPhysics.ArriveAtGoal`). "My target vanished" is by design. A
  `TargetSpeed` above the speed limit (14 CFR 91.117 or the type's Vmo/Mmo) is the exception: it stays standing at the cap until the uncapped speed is reached.
  An exact assignment held short by the cap is also what the limit arm of `BoundsCorrectionTarget` returns, so the aircraft flies it again once the cap lifts even after the target has gone (exclusions under layer 2 of the `UpdateSpeed` cascade).
- **Touchdown ends the assignment.** A full-stop landing, touch-and-go, stop-and-go or helicopter landing calls `FlightCommandHandler.CancelAssignedSpeedAtTouchdown`, which clears `AssignedSpeed`, `TargetMach`, the floor/ceiling, `HasExplicitSpeedCommand`, `SpeedMaxHeld` and the procedure's last-published-speed memory. The touchdown tick keeps the flare's own target, so the landing keeps braking. A go-around before touchdown keeps its assignment.
- **`AssignedX` is persistent UI/autopilot state.** `AssignedAltitude`/`AssignedSpeed`/`AssignedMagneticHeading` persist past the snap (the heading until the aircraft leaves the vector) so the
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
- **A Mach hold is handed over to an IAS at or below FL240** (`FlightPhysics.HandOverMachHold`, first in `UpdateSpeed`, gated by `IsHandOverMachHold`; airborne, not SMAX's hold, not a helicopter). Level at or below FL240 (`MachHandOverAltitudeFt`) it hands over at once, as does a descent that ends there.

  On a descent cleared to end below FL240 it hands over when the aircraft reaches FL240 or the Mach's IAS equivalent reaches `MachHandOverThresholdKts` (the lower of Vmo less 10 kt and the profile's FL100 descent speed), whichever comes first; a descent cleared to end at or above FL240 keeps the Mach. The IAS is the Mach's own at that tick, no higher than Vmo less 10 kt (`MachHandOverKts`), so a high Mach keeps its IAS and a slow one is never sped up.

  It clears `TargetMach` and writes the IAS into `TargetSpeed`, and into `AssignedSpeed` only when the Mach was an explicit speed command (`HasExplicitSpeedCommand`; live traffic seeds a Mach nobody assigned, which gets a target speed only) and no controller crossing speed is still ahead on the route (the assigned speed stays the crossing's until its fix). The speed then stands behind 91.117 and the envelope.
- **A controller crossing speed ends a Mach hold at its own point** (`FlightPhysics.PlanCrossingUnderMach`, from `UpdateSpeedPlanning`; published speeds under a Mach are not planned). A `CFIX` or constrained `DCTF` with a speed leaves a held Mach live (SMAX's hold ends at once, its Mach stays), so the IAS follows the Mach until the earliest of: for a crossing below the speed flown (the larger of the IAS and the Mach's IAS), the slowdown point; for a crossing above it, the Mach's IAS reaching the crossing or the acceleration point; at or below FL240 with the window not yet open, a handover to the Mach's own IAS (no higher than Vmo less 10 kt) that slows only at the slowdown point. Then `TargetMach` and SMAX end and the crossing IAS is flown. A constrained `DCTF` restated with a same-transmission `DVIA` leaves the Mach live too.
- **A `HOLD` under a Mach keeps the Mach to the holding fix**, where `HoldingPatternPhase` ends the Mach (and SMAX) and the aircraft slows to holding speed; a `MACH` given after the `HOLD` ends at the fix likewise. Past the fix the Mach comes back (`RevertMach`).
- **`NavigationTarget.RevertMach` / `RevertMachExplicit` restore a Mach hold past a fix**, beside `RevertSpeed`/`RevertAssignedSpeed` and `RevertSpeedMax`. A `CFIX` with a speed (`NavigationCommandHandler.DispatchCrossFix`) or a constrained `DCTF` with speeds (`FlightCommandHandler.ApplyConstrainedForceDirectTo`) leaves the Mach hold live (`EndSpeedMaxHold`; `PlanCrossingUnderMach` ends it at the crossing) and records it on the fix in place of a plain speed revert: `RevertMach` is the Mach, `RevertMachExplicit` whether it was an explicit speed command.

  A CFIX that restates the speed on the fix already holding the revert keeps it. A constrained `DCTF` puts it on the last fix whose speed it sets, altitude-constrained or not (a Mach restored from a crossing the DCTF dropped included), and suppresses the plain IAS revert while a Mach is to be restored. A later crossing speed moves a pending revert to the route-order-last fix with a controller-set speed (`NavigationCommandHandler.PlaceMachRevert`, as `PlaceSpeedMaxRevert` does for SMAX), so earlier crossings are flown first; `DEPART` carries it with the fix.

  A later transmission's `SPD`, `SPEEDF`, `MACH`, `SMAX`, `RNS`, `RPS`, `DSR`, `SPDN` or `RFAS` clears it and every other speed revert (`NavigationCommandHandler.CancelEarlierCrossingSpeeds`, which also gives the crossing's published speed back), as do a via clearance (`SupersedeEarlierCrossingRestrictions`) and an approach clearance (`DropSpeedReverts`).

  The same transmission's instruction after the crossing (`CFIX X 80 250, SPD 280`) leaves the crossing speed in force and lands on the route as the one pending revert (`PlaceSameTransmissionRevert`): `RevertResume` (`SpeedResumeKind`, RNS/RPS/DSR, applied past the fix by `FlightCommandHandler.ApplySpeedResume`), `RevertSpeedMax`, `RevertMach`, or `RevertSpeed`/`RevertAssignedSpeed` with `RevertSpeedFloor`/`RevertSpeedCeiling`; a bound is restored past the fix only when captured, so a live bound with no capture survives.

  Past the fix `FlightPhysics.RestoreSpeedPastFix` restores it (`RestoreMachPastFix`) with the saved explicit flag and clears the speed floor and ceiling. Above FL240 the Mach comes back as `TargetMach`. At or below FL240, or on a descent below it whose Mach already reaches the handover threshold (`RestoredMachHandsOver`), it comes back as the IAS the handover would give, capped at the FL100 descent speed and Vmo less 10 kt (`RestoredMachIas`): `AssignedSpeed` only if the Mach was explicit, `TargetSpeed` either way (A320 `MACH .78` restored at FL200 settles on 290 kt).

  A crossing speed written with a resume (`RevertResume`) never becomes a ceiling at its fix, even where the reverts are dropped instead of applied. A constrained `DCTF` writes its plain revert on the last fix whose speed it sets, altitude-constrained or not; the altitude revert stays on the last constrained fix. Within one transmission the last speed act wins: `CFIX X 020 180, SPEEDF 160` keeps the forced revert, `SPEEDF 160, CFIX X 020 180` does not.

  No revert is applied in a hold or inside the 5 nm final gate (`FlightCommandHandler.SpeedRevertForbidden`; 7110.65 §5-7-1.b.3 and b.4): `RestoreSpeedPastFix` clears the passed fix's reverts and every other revert on the route (`DropSpeedReverts`) instead. A hold is `HoldingPatternPhase`, `VfrHoldPhase` or an `AirspaceBoundaryHoldPhase` orbit, not a level-off under a shelf; the gate is `FinalGateNm` from the landing threshold on final, unless a forced speed holds the aircraft past it.

  A route change that drops a fix before it is crossed applies the fix's speed reverts as if it were crossed (AIM 4-4-10.g; 7110.65 §5-7-1.b-d), so `SPD 300; CFIX PMD 110 250; DCTF HEC` goes back to 300 kt: `NavigationCommandHandler.TakePendingSpeedReverts` collects them before the route is cleared or truncated and `ApplyTakenSpeedReverts` calls `RestoreSpeedPastFix` on each once the command's new route and procedure are in place, so an RNS or RPS resumes against them (`ClearRouteApplyingSpeedReverts` for a plain clear). DCT, DCTF, TLDCT/TRDCT (a direct-to onto a later fix of the active SID or STAR applies the skipped fixes' reverts with the procedure still active, `FlightCommandHandler.ApplySkippedSpeedReverts`), a constrained DCTF with no speed constraints, heading vectors, JRADO, JRADI, JAWY (both halves), JARR (against the new STAR), JFAC/JLOC and the MTR route load do so, as does the start of a 360/270 turn (`MakeTurnPhase`, before `ManeuverSpeedController` captures the speed, so the turn resumes to the speed the crossing replaced and a re-armed SMAX holds after it); MLS/MRS S-turns leave the crossing in force and its revert fires at the fix.
  A constrained DCTF with crossing speeds applies the dropped crossings' reverts at once, on the emptied route, and what they restored (a plain speed, a Mach hold, SMAX armed again) is what its own last speed fix reverts to (`PlaceSpeedMaxRevert` for SMAX); a Mach restored as an IAS below the handover altitude is carried as a Mach revert, and an RNS, RPS or DSR given with a dropped crossing is carried to that last fix (`PlaceResumeRevert`). After `SPD 300; CFIX PMD 110 250` a DCTF crossing HEC at 11,000 ft and 210 kt goes back to 300 kt past HEC. `DEPART` keeps its fix, and so an SMAX re-arm on it; `WARP` drops the reverts silently. An SMAX re-arm on a dropped fix is armed at once, since no later crossing can carry it.
  A constrained DCTF onto consecutive fixes of the active STAR, in order, keeps the STAR (`FlightCommandHandler.KeepStarForConstrainedDirect`): the fixes before the first are dropped and their reverts applied, the crossings are stamped on the STAR's own fixes (`StampConstrainedDirectFix`), and `NavigationCommandHandler.PlaceCrossingSpeedReverts` places the speed revert on the last fix whose speed it sets. A pending revert on a kept fix the command does not constrain moves past that fix. With a DVIA in the same transmission the descend via is kept and either order leaves the same state (no reverts captured, the crossing speed assigned); alone it ends the via and keeps the STAR.

  A fix carrying `RevertMach` skips the crossed-speed ceiling or floor a plain crossing publishes past it, since the restored Mach decides the speed there. The fields are serialized nullable with no schema bump ([snapshots-and-replay.md](snapshots-and-replay.md)).

`WindInterpolator` also provides `TasToIas` (the inverse — used for resolving cruise TAS to an IAS), `IasToMach`,
`ComputeWindCorrectionAngle`, and `GetWindComponents`. Wind layers are vector-interpolated by altitude
(`WindInterpolator.GetWindAt`) so the 0/360 boundary is handled correctly.

Airborne, `FlightPhysics` samples the wind at the aircraft's own position (`WindSamplePoint(Position, Declination)` overloads of `GetWindAt` / `GetWindComponents`) for the crab angle and the groundspeed vector: where the weather carries a GFS grid covering the aircraft, the winds above the METAR surface layer come from the grid's column there, otherwise from the layers as before.

The ground samples stay on the layers (no position). See [weather-and-wind.md](weather-and-wind.md#sampling-the-grid-at-a-position).

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
  (rad/s), capped at 5 nm, 0 for turns < 1°. The sequencing threshold becomes `max(anticipation, NavArrivalNm)`. `FlightPhysics.SequencingThreshold` computes it for the sequencer and the planners, so a fix sequences that far *before* the fix itself. `FlightPhysics.SequencingDistanceNm` (the route distance to the fix less that threshold, never below zero) is where the speed planner ends a deceleration and the climb and descent planners aim their required rate.
- **Abeam sequencing.** Inside the anticipation zone, the waypoint is not popped on distance — it is popped when the along-track distance along
  the *next* leg bearing goes non-negative (`GeoMath.AlongTrackDistanceNmRaw >= 0`), i.e. the aircraft has passed abeam.
- **Arc-blended steering.** While in the anticipation zone but not yet sequencing, the steering heading is the tangent to the inscribed turn
  circle (`FlightPhysics.ComputeArcBlendedHeading`), not the straight bearing to the waypoint — so fly-by turns are smooth.
- **Wind correction angle.** When airborne with weather, the final steering heading is `bearing + WCA` where
  `WCA = WindInterpolator.ComputeWindCorrectionAngle(bearing, TAS, windFrom, windSpeed)` with the wind sampled at the aircraft's position — the aircraft crabs so it tracks a straight
  ground path, not a downwind pursuit curve.
- **Fix-constraint apply/revert.** On sequencing, `FlightPhysics.ApplySequencedFixConstraints` applies the next fix's altitude/speed restriction (a published speed below the present one is left to the speed planner's deceleration window in every mode, 7110.65 5-7-1.g NOTE 3 and AIM 5-4-1.a.2(a)(3) NOTE 2, while a controller-set crossing speed under an assigned speed, an altitude, an acceleration and an "at or above" floor stay immediate; `ApplyViaClearanceFixConstraints` for the first constrained fix of a DVIA, CVIA or a CFIX restated with a via clearance, see the look-ahead paragraph below; gated by
  SID-via / STAR-via mode), and `NavigationTarget.Revert*` fields restore the prior target/assigned alt+speed when sequencing past a constrained
  fix. The altitude reverts are captured only when going back continues the aircraft's direction of flight (`NavigationCommandHandler.AltitudeRevertReversesTravel`); otherwise none is stored and the crossing altitude stays past the fix. Outside a via mode, when `AssignedAltitude` already lies strictly beyond a fix's at-or-above or at-or-below bound, or strictly inside its window (equal to the bound does not count; `NavigationCommandHandler.CrossingBoundMetByClearance`, via `FlightPhysics.ClearedAltitudeMeetingBound`), the climb and descent planners and `ApplyFixConstraints` (`ApplyFixAltitudeConstraint`) target the assigned altitude instead of resolving the bound (`PlanToClearedAltitude`): while the aircraft is still on the wrong side of the bound (below an at-or-above, above an at-or-below, outside a window) it flies `BoundCrossingRate`, the rate that reaches the bound by the point the fix sequences (`SequencingDistanceNm`) clamped to between the standard rate and twice it; past the bound, the profile rate. `FrdArrivalNm` (1.5 nm) and `GroundArrivalNm` (0.05 nm) are the FRD-point and ground-entity arrival thresholds used by the
  queue triggers, not by route sequencing.

`PreferredTurnDirection` is **not** cleared in `UpdateNavigation` — only on heading snap or route exhaustion — so departure
direction bias (`TRDCT`/`TLDCT`) survives until the initial turn completes.

**Route exhausted: direct to the destination (decided, not built yet).** A US route field never names its own airports, so "last fix, then direct the field" is implied (`NIMI5 OAK V6 SAC` to KSAC ends at the SAC VORTAC). Today the route-exhausted branch clears the target heading and the aircraft flies on straight; only `OnCourseDeparture` heads for the destination.

The decided order in that branch: a pending approach clearance wins; an FM or VM last leg flies its heading; otherwise an IFR aircraft with a cleared destination goes direct to the airport at once (its clearance limit, 7110.65 §4-7-1.a, AIM 4-4-3.a) at its last assigned altitude (AIM 5-4-1.b), whatever the distance and even with an assigned runway or an expected visual.

About 5 minutes out the solo pilot asks for further clearance, at 3 minutes it slows to holding speed (AIM 5-3-8.a.4), and over the field it holds right turns inbound on its arrival course (1-minute legs at or below 14,000 ft, 1.5 above) and reports "holding over [airport], [altitude]" (AIM 5-3-8.a.3/a.6). A VFR aircraft goes direct and calls before a Class C/D boundary (AIM 3-2-5.a.3), with no hold. With no destination it keeps flying straight.

The destination comes from the pilot's own cleared copy, snapshotted at spawn or when an RPO command issues a route or clearance, never from the scope-editable flight plan (14 CFR 91.123; the same pilot-side pattern as the live-traffic filed altitude, [live-traffic.md](live-traffic.md#design-decisions)). A clearance limit short of the destination is not modelled until a command sets one (7110.65 §4-6-1).

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
the `PlannedVerticalRate` needed to hit it at the sequencing point (`SequencingDistanceNm`), so a crossing is met by the time the fix sequences: `requiredFpm = altDelta / timeMinutes`, capped at **2× the standard category rate**. In the last seconds the time is floored at one physics tick, so the required rate holds instead of jumping to 2× standard. Descent planning is suppressed under `SidViaMode`; climb planning under `StarViaMode` — the SID-via / STAR-via split decides which
planner owns the route. Both also activate (outside via mode) whenever the route carries an altitude restriction in force (`AltitudeRestrictionInForce`, by who wrote it, `CifpAltitudeRestriction.Source`):

- A controller's crossing (CFIX, constrained DCTF) always.
- A pattern-entry or teardrop point while that phase flies.
- A published approach altitude once an approach clearance is active, or a deferred (pending) one once the aircraft is past its connecting fix. Until then the aircraft keeps its last assigned altitude; past it the approach's floors below the assigned altitude pull the aircraft down, and a ceiling above it never makes it climb. A direct onto the active procedure that skips the connecting fix moves it to the direct's fix (AIM 5-4-7.f).
- A published SID or STAR altitude only under a climb via or descend via, or on a SID departure with no altitude at all (the no-data fallback treated as climb via).

A maintain (`DM`/`CM`, a `CTO` altitude, a TDLS maintain value) with no `CVIA`/`DVIA` leaves the published altitudes on the route but out of force, so the aircraft levels at its assigned altitude; published speeds still apply.

The climb scan walks the route in order and ends at the first restriction that fixes the altitude before a later window: one needing a descent (more than `AltitudeSnapFt` below the aircraft), or an already-met "At" / glideslope-intercept restriction. A higher window past it therefore starts no climb before that fix.

## Speed integration (`UpdateSpeed`) + look-ahead planning

`UpdateSpeed(aircraft, cat, deltaSeconds)` (`FlightPhysics.cs`, `FlightPhysics.UpdateSpeed`) is a layered cascade. The layers run in this exact order; getting the order
wrong silently lets one layer stomp or lose to another:

Layers 1, 2 and 5 all clamp against the speed limit, resolved once at the top of the method; its 91.117 half is the public `FlightPhysics.RegulatorySpeedLimit(aircraft)`. That takes the aircraft alone and has four arms: `double.MaxValue` on the ground, or at or above 10,000 ft outside the descent lead below; `double.MaxValue` for an
aircraft whose `MilitaryRoute.SpeedLimitWaived` is set and which is **not** under a Class B shelf (the AP/1B waiver reaches 91.117(a) only);
otherwise the cap — **200 kt** when `AirspaceDatabase.IsUnderClassBShelf` puts the aircraft laterally inside a Class B footprint but below its
floor (91.117(c)), else **250 kt** (91.117(a)) — raised to `max(cap, AircraftPerformance.MinimumSafeSpeedKts)` for a type with
`AircraftPerformance.IsSpeedLimitWaived` (91.117(d): a floor under the cap, never a removal of it).

**Descent lead (250 before 10,000 ft).** Above 10,000 ft an aircraft descending to a cleared altitude below it (`FlightPhysics.LowestClearedAltitude`: the assigned or target altitude, or a descend-via's floor or route bottom) gets the 250 cap early, so it crosses 10,000 ft at or below 250 + `SpeedSnapMarginKts` (the 2 kt snap). `RegulatorySpeedLimit` returns 250 once the time to 10,000 ft is within 1.1× the time to slow from the speed the aircraft is flying toward (the highest of IAS, `TargetSpeed`, the floor and the auto-schedule speed) at the type's airborne decel rate. `AssignedSpeed` (`SPD 280`) stays on record behind the cap and is flown again after the descent levels above 10,000 ft or the cap otherwise lifts.

`DescentSpeedLimitBandApplies` is the one judge of "this aircraft is subject to the lead", shared with the backstop, and exempts SPDN, SMAX, the military-route waiver and, as above, 91.117(d) types (cap = `max(250, MinimumSafeSpeedKts)`). A crossing speed above 250 holds the lead off only when the fix's crossing floor is at or above 10,000 ft (At, AtOrAbove, a Between's lower bound; never AtOrBelow) and that altitude is in force (descend via, or a controller `CFIX` crossing), until the fix is passed.

**Backstop.** When even full deceleration cannot make it, `UpdateAltitude` levels the descent at 10,000 ft until IAS is within `SpeedSnapMarginKts` of the cap, then continues to the cleared altitude without a re-clearance. It is derived each tick and persists nothing. Lead and backstop use the same decel rate: the one toward the cleared altitude, before the level-off taper. The descent schedule's 10,000 ft breakpoint is unchanged. `FlightPhysics` logs the lead and the level-off at Debug.

The speed limit the layers hold is `FlightPhysics.EffectiveSpeedLimit(aircraft)`: the lower of `RegulatorySpeedLimit` and `EnvelopeSpeedLimit`, the type's own maximum — the lower of Vmo and Mmo's IAS equivalent at the present altitude (`AircraftPerformance.MaxOperatingIasKts`), so a climb past the IAS/Mach crossover lowers it as the air thins.

`EnvelopeSpeedLimit` is airborne-only and unlimited for a type with no known Vmo/Mmo. Both are recomputed every tick. While `Targets.SpeedLimitsLifted` is set (SPDN) the effective limit is `double.MaxValue`, so neither 91.117 nor Vmo/Mmo binds. While `Targets.SpeedMaxHeld` is set (SMAX) `SpeedLimitWithin` drops `RegulatorySpeedLimit` and returns the envelope limit alone, so the aircraft flies past 91.117 but never past Vmo/Mmo.

1. **Mach hold** — if `TargetMach` set and airborne, write `MachToIas(...)` into `TargetSpeed`, clamped to the speed limit, so a Mach hold cannot run the IAS past Vmo; a descending hold is handed over to an IAS before this layer runs.
2. **Floor/ceiling/speed-limit self-target** (`FlightPhysics.BoundsCorrectionTarget`) — if `TargetSpeed` is null, mint a target that corrects the bound
   the aircraft is violating. Branch order is load-bearing: a `SpeedFloor` it is under, then a `SpeedCeiling` it is over, then the speed limit itself (91.117 or Vmo/Mmo).
   The speed limit clamps the *effective* floor and ceiling, so a `SpeedFloor` above the cap never holds the aircraft up there. The
   limit arm is airborne-only: an aircraft with no target whose IAS exceeds the limit by more than `SpeedSnapKts` slows to it at the
   ordinary decel rate, with no transmission — pilots comply with 91.117 without notification (7110.65 §5-7-2.a NOTE 1, §5-7-3.f NOTE 1).
   Because the ceiling branch runs first, an aircraft over both lands on `min(cap, SpeedCeiling)`.
   The limit arm returns the exact ATC assignment instead of the cap when one stands behind it (`HoldsAssignedSpeedBehindTheLimit`: `HasExplicitSpeedCommand`, an `AssignedSpeed`, and the assignment is not a standing floor or ceiling), so the aircraft slows to the cap now and flies the assignment again when the cap lifts. Excluded: a Mach hold (`TargetMach`), SMAX, and `SPD n+` / `SPD n-` bounds, which are a floor or a ceiling rather than a speed to fly.
3. **Auto altitude-band schedule** (`FlightPhysics.ApplyAutoSpeedSchedule`) — if `TargetSpeed` is null, airborne, **not** `HasExplicitSpeedCommand`, `ActiveApproach` is null, the current
   phase does **not** have `ManagesSpeed == true`, and the aircraft is climbing/descending toward a target altitude → set `TargetSpeed` to
   `AircraftPerformance.DefaultSpeed(...)`, honoring an active `SpeedCeiling` and the type's `EnvelopeSpeedLimit`. This is the layer the approach/pattern phases suppress.
4. **Ground `SpeedLimit` clamp** — `goal = min(goal, Ground.SpeedLimit)` when on the ground; this is the ground-conflict cap from
   [tick-loop.md](tick-loop.md)'s `GroundConflictDetector`.
5. **Speed limit** — `goal = min(goal, speedLimit)`: 250 below 10,000 ft (and above it within the descent lead) and 200 under a Class B shelf (14 CFR 91.117), or the type's Vmo/Mmo where that is lower. The layer first
   records `heldShortBySpeedLimit = goal > speedLimit` — the cap holding the aircraft short of the speed it was told to fly — for
   layer 7.
6. **`SpeedCeiling` continuous clamp** — `goal = min(goal, SpeedCeiling)` again, so even a non-procedural `TargetSpeed` (auto schedule,
   pre-ceiling controller assignment) cannot escape the cap.
7. **Snap + integrate** — `|diff| < snapWindow` → `ArriveAtGoal` snaps IAS and **nulls `TargetSpeed` unless
   `heldShortBySpeedLimit`**: a target above the speed limit stays standing at the cap, whichever clamp the aircraft actually
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
when within 10% of the change time (accel starts immediately). The time to fix runs to the fix's sequencing threshold (`SequencingThreshold`), not the fix itself, so the aircraft is at the published speed when the fix sequences. It is fully suppressed when `SpeedRestrictionsDeleted`
is set; under `HasExplicitSpeedCommand` or `TargetMach` it plans only the controller-set crossings (below; under a Mach, `PlanCrossingUnderMach`). The planner flies the speeds on the route whatever the via mode: a level STAR spawn carries the STAR's published speeds with `StarViaMode` off (`ArrivalRouteResolver.ApplyStarSpeedRestrictions`) and no altitudes, so a STAR's published speeds apply without `DVIA` (AIM 5-4-1.a.2(a)(3) NOTE 1; 7110.65 4-5-7 NOTE).

**A published speed never outranks an assigned one, and the last one crossed is remembered** (7110.65 §5-7-1.c, §5-7-4; AIM 4-4-12.f, 5-4-1 NOTE 2). `ApplyFixSpeedConstraint` skips a fix's speed while `HasExplicitSpeedCommand` is set unless a controller set that fix's speed (`NavigationTarget.SpeedTransmissionId` or a speed revert, `IsControllerSetSpeed`: a CFIX with a speed, a constrained DCTF, `DVIA SPD n FIX`), the rule `UpdateSpeedPlanning` keeps: under an assigned speed it plans only the controller-set crossings (a crossing on the route's first fix included), flies the assigned speed until the slowdown point and then crosses at the crossing speed, and a published fix neither is planned nor hides a controller crossing after it. At fix sequencing `RecordCrossedPublishedSpeed` stores the fix's "at" or "at or below" published speed in `AircraftProcedure.LastCrossedPublishedSpeedKts` whatever speed the aircraft was flying; an "at or above" speed is a minimum and leaves it alone. Under a via clearance (`AircraftProcedure.ProcedureSpeedsInForce`, true whenever the aircraft flies a SID or STAR in via mode, including a SID departure under a `CTO` altitude whose published altitudes are out of force) a controller-set crossing speed ends at its fix: what is recorded and published is the via procedure's own speed at that fix (`NavigationCommandHandler.ViaProcedureSpeedAt`, read before the fix leaves the route), a fix publishing none records nothing, and `MoveAssignedSpeedPastControllerCrossing` clears `AssignedSpeed` or moves it to the next controller crossing (an explicit ATC speed and SMAX's re-arm keep theirs); with no via clearance the crossing speed holds until ATC ends it. The memory ends with the procedure (a vector or `ClearActiveProcedure`, joining a STAR, a SID becoming active, an approach clearance). `RNS`, `RPS`, `DVIA`, `CVIA` and an approach clearance cancel the assigned speed; on a STAR (not for the approach clearance) the remembered speed becomes the `SpeedCeiling` again (`FlightCommandHandler.RestoreCrossedPublishedCeiling`), so the aircraft does not speed up before the next restricted fix. `DVIA`, `CVIA` and a CFIX restated with a via clearance apply the first constrained fix through `FlightPhysics.ApplyViaClearanceFixConstraints`: a lower "at" or "at or below" speed there is not started at once (the aircraft keeps its speed and the planner slows it at the minimum distance, 7110.65 5-7-1.g NOTE 3, AIM 5-4-1.a.2(a) NOTE 2) but is still remembered as the last published speed, while a higher speed or an "at or above" floor applies at once; a later fix's speed is handled as the previous fix sequences (`ApplySequencedFixConstraints`: a lower speed, a controller-set crossing speed under an assigned speed included, is left to the planner's deceleration window, so the aircraft holds its speed until the slowdown point, under a Mach hold too; a higher speed and an "at or above" floor apply at once; a published speed under an ATC-assigned Mach is not applied). A fix's speed reaches `TargetSpeed` from the clearance (`ApplyViaClearanceFixConstraints`), from sequencing (`ApplySequencedFixConstraints`) and from the planner (`UpdateSpeedPlanning`). The spawn path keeps `ApplyFixConstraints`. `RNS` and `RPS` also call `FlightPhysics.ApplyNextPublishedSpeed`, which targets the first route fix's speed at once only when it is above the present speed or an "at or above" minimum (the minimum becomes the floor); a lower speed is left to the planner's deceleration timing.

**The procedure's `SpeedFloor` outlives a via clearance and a resume.** The minimum crossed at a published "at or above" speed is written only with no explicit ATC speed in force. `CancelAssignedSpeedKeepingProcedureFloor` (used by `DVIA`, `CVIA`, `RNS`, `RPS`) keeps it while a SID or STAR is active; the planner (`PlannedConstraintSpeed`/`SetPlannedSpeed`) drops it when the pilot slows for a lower "at" or "at or below" speed ahead, not under an explicit speed, where the crossing speed is clamped to the floor. A heading or direct command that applies calls `FlightCommandHandler.EndPublishedSpeedFloor` (from `CommandDispatcher.EndingPublishedSpeedFloor`), which clears the floor and `LastProcedureSpeedKts` unless `HasExplicitSpeedCommand`. The approach clearance's `CancelAssignedSpeed` clears the floor too.

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

The same gate ends a controller crossing speed on a fix still ahead within `FinalGateNm` of the threshold (`CancelControllerCrossingSpeedsInsideFinalGate`): the fix loses that restriction and its speed reverts, a published speed stays, and `ReleaseSpeedAssignmentAtGate` then applies the no-acceleration release above, as for an explicit `SPD`. The gate runs with no explicit speed when such a route exists. `CFIX` with a speed and a constrained `DCTF` with speeds are refused inside the gate with `SPD`'s text (`RefuseInsideFinalGate`); altitude-only ones are accepted.

Departures and go-arounds are not inbound to land and keep their assigned speed; a forced assignment (`SPEEDF`/`SPEEDN`, flagged
by `Targets.SpeedOverridesFinalGate`) is also exempt. The retained ceiling is cleared when the aircraft transitions to a
climb-out (`GoAroundPhase`, `TouchAndGoPhase`, `StopAndGoPhase`, `LowApproachPhase` clear it in `OnStart`).

The **AIM 5-4-1 NOTE 2 procedural-speed memory**: when the route is exhausted, `UpdateNavigation` publishes the last procedure speed
(`Procedure.LastProcedureSpeedKts`) as a `SpeedCeiling` so the auto schedule cannot accelerate the aircraft above the last published
speed — unless an explicit ATC speed is active.

## Position integration (`UpdatePosition`)

`UpdatePosition(aircraft, deltaSeconds, weather)` (`FlightPhysics.cs`, `FlightPhysics.UpdatePosition`) advances lat/lon with a flat-earth approximation
(`NmPerDegLat = 60`, longitude scaled by `cos(lat)`):

- **Ground branch**: re-enforce `Ground.SpeedLimit` on IAS (instantly: a ground-conflict rule that slows rather than stops floors its own limit to a brake-feasible value, see [ground/navigator.md](ground/navigator.md)), move along `Ground.PushbackTrueHeading ?? TrueHeading` at
  `IAS / 3600` nm/s, and set `TrueTrack = TrueHeading` (track follows heading directly; GS = IAS on the ground).
- **Airborne branch**: `TAS = IasToTas(IAS, alt)`; ground-speed vector = `TAS·(cos/sin heading) + wind` (sampled at the aircraft's position); **cache `WindComponents`**;
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

### The validated category table

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
| `LowApproachLevelOffAgl` (ft AGL, a low approach levels off here before the threshold; default 150) | 150 | 100 | 100 | 50 |

`LowApproachLevelOffAgl` is an owner-approved judgement call, not a published figure; the behaviour it serves is AIM 4-3-12.a (see `LowApproachPhase`).

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

The ground-turn constants the navigator steers with ([ground/navigator.md](ground/navigator.md)) are `GroundTurnRate`, the gear-limited yaw ceiling (12/16/20/30 °/s jet/turboprop/piston/helicopter), and two radii.

`MainGearTurnRadiusFt` is the comfortable one and `TightTurnFloorRadiusFt` (15/12/8/8 ft) the tightest a corner rounds to, both the path radius of the main-gear axle midpoint, not of the inner main gear: a jet's 15 ft is ~74° of nose-wheel steering on a B737-800, its inner main gear ~5.6 ft from the turn centre.

`GroundYawRateOnRadius(cat, groundSpeedKts, radiusFt)` is the yaw a ground turn on a given radius can hold, ω = v/R capped at `GroundTurnRate` (a helicopter holds the full rate at any speed); `GroundYawRateAtSpeed` is it on `MainGearTurnRadiusFt`.

`TurnAboutSpeedKts(cat, radiusFt)` is the inverse, v = ω·r, the pivot speed of a turn about on a taxiway at the type's turn-about radius (`TurnAboutFit`: max(MGW/2, 0.466 × wheelbase) from the FAA record, see [ground/navigator.md](ground/navigator.md#entry-alignment-threshold)). For a type without those figures that radius is `TightTurnFloorRadiusFt`, at ≈ 1.86/2.0/1.65/2.5 kt jet/turboprop/piston/helicopter.

## The command-queue half (`UpdateCommandQueue`) — summary only

`UpdateCommandQueue(aircraft, deltaSeconds, aircraftLookup)` (`FlightPhysics.cs`, `FlightPhysics.UpdateCommandQueue`) evaluates the `CommandQueue` after position integration.
Full depth is in [command-pipeline.md](command-pipeline.md) and [phases.md](phases.md); the physics-relevant facts:

- **While a phase is active** (`Phases.CurrentPhase != null`), block *advancement* and untriggered-block *application* are skipped — phases own
  `ControlTargets`. Only conditional triggers are still watched (`FlightPhysics.ApplyReadyConditionalBlocks`), so e.g. `SPD 210 UNTIL 10` can fire its
  block mid-approach.
- **Trigger types** (`FlightPhysics.IsTriggerMet`): `ReachAltitude`, `ReachFix`, `InterceptRadial`, `ReachFrdPoint`, `GiveWay`, `DistanceFinal`,
  `OnHandoff`, `AtGroundEntity`, `EnteringHoldingAfterExit`, `AfterRunwayCrossing`, `AfterCycleTerminator` (`OTG`: latches on a
  touch-and-go / stop-and-go / low approach / go-around phase and fires once the aircraft has left it and is airborne; a full-stop landing marks it `TriggerMissed` and `DiscardMissedCycleTerminatorBlocks` drops it and its chain remainder (`DiscardChainRemainder`) with an "unable — landed full stop" warning at the next queue update; P/CG UNABLE, 7110.65 §3-8-2).
- **A trigger's `TrueRadial` is a true bearing from the fix** (`InterceptRadial`, `ReachFrdPoint`). A controller's radial (`JRADO`, `JRADI`, `AT {fix}{radial}`, `AT {fix}{radial}{dist}`) is magnetic, so the block's builder converts it with `NavigationCommandHandler.RadialToTrue`, which goes through `RadialConversion` (shared with `FrdResolver`): the station's variation of record (`NavigationDatabase.GetPublishedCourseDeclination`, P/CG RADIAL, AIM 1-1-17b.5(j)(1)), else the modelled declination at the fix, rounded to whole degrees.

  The radial as issued stays in `IssuedRadial`, which the FRD miss warning names (`Missed condition at OAK R090 D010`); JAWY's segment course is already true and leaves it null. `InterceptRadial` fires within 3° of `TrueRadial` (`RadialInterceptDeg`); a JRADO then holds the radial converted with that same variation as its true heading. An FRD point is projected along the unrounded true bearing.
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
— a `Geo.Coordinate` ctor would otherwise crash the tick. The previously cached value is kept. `DeclinationCachePosition` is `[JsonIgnore]` on `AircraftState` but carried by
`AircraftSnapshotDto.DeclinationCachePosition`, so a restored aircraft keeps its cache position and does not re-run the WMM until it leaves the box (a cold cache restores as `null` and warms on the first tick).

## Footguns & gotchas

- **`IndicatedAirspeed` is the only airspeed source of truth; `GroundSpeed` has no setter.** It is recomputed on every read from TAS + cached
  `WindComponents`. Trying to "set ground speed," or assuming `GS == IAS` airborne, is wrong — they diverge with altitude (TAS) and wind.
- **`TargetSpeed` and `TargetAltitude` self-null on arrival** (±2 kt / ±10 ft). "My target vanished" is by design. `AssignedSpeed`/
  `AssignedAltitude` persist for the UI/autopilot; `TargetX` is the transient physics goal. Don't conflate them. The one exception is a
  `TargetSpeed` above the 14 CFR 91.117 limit, which stays standing at the cap (the 91.117 bullet below).
- **`UpdateSpeed` is a fixed 7-layer cascade** (Mach hold → floor/ceiling/speed-limit self-target (`BoundsCorrectionTarget`) → auto altitude-band
  schedule (suppressed by `ActiveApproach` OR `ManagesSpeed`) → ground `SpeedLimit` clamp → speed-limit cap (`EffectiveSpeedLimit`: 91.117's 250 below
  10,000 ft and 200 under a Class B shelf, or the type's Vmo/Mmo where lower; sets the held-short flag) → continuous `SpeedCeiling` clamp → snap+integrate (`ArriveAtGoal`)). Add a
  new speed influence at the wrong layer and it silently loses to or stomps another.
- **14 CFR 91.117 and the type's Vmo/Mmo bend a speed assignment; they never cancel it.** A `TargetSpeed` above the speed limit (`EffectiveSpeedLimit`) is kept when the aircraft
  arrives at the cap and nulled only when the uncapped target is reached, so the aircraft takes its assigned or restored speed back up when
  the cap lifts — leaving a Class B shelf, climbing through 10,000 ft. Only ATC ends an assignment (7110.65 §5-7-4; §5-7-4.a NOTE: "resume
  normal speed" does not relieve 91.117), and the pilot complies with 91.117 without notification (§5-7-2.a NOTE 1) and must not fly an ATC
  speed that exceeds it (AIM 4-4-12.i; 200 kt beneath Class B, AIM 4-4-12.j and 4-4-12.k NOTE). The other half is the re-bite: an airborne
  aircraft with **no** target whose IAS is over the limit by more than `SpeedSnapKts` gets a self-target at `min(cap, SpeedCeiling)` and
  slows at the ordinary rate, silently. Consequences, all reviewed and intended:
  - **A null `TargetSpeed` is not the only "speed command done" signal.** A chained `SPD` completes on
    `TargetSpeed is null || FlightPhysics.IsSpeedAssignmentHeldAtSpeedLimit(aircraft)` (`UpdateBlockCompletion`): `SPD 210; H 090` under
    a shelf advances once the aircraft has settled at 200 with 210 still standing. The settled test is against
    `min(limit, SpeedCeiling)`, not the assignment, so an aircraft still slowing toward the cap keeps the chain waiting.
  - **`InitialClimbPhase` writes its climb speed only when `TargetSpeed is null`**, so a retained assignment keeps it quiet for as long as
    the assignment stands.
  - **The generator stream's `RestoreManagedSpeed` skips while a target stands** — its own capped restore included; the standing target
    does the re-acceleration.
  - **`TargetSpeed` can show the speed limit on an aircraft nobody assigned a speed** (data block, aircraft list, snapshot): that is
    the re-bite's self-target, and it nulls once the aircraft is down to the limit.
  - **The sim tolerates limit + 2 kt indefinitely** — the re-bite threshold is the snap window, and an aircraft inside it has no target to
    fly.
  - **A speed past the type's Vmo/Mmo is refused at the command, not capped.** `SPD`, `SPEEDF`, `MACH`, `CFIX` and a constrained `DCTF` crossing speed that the type can fly neither at its present altitude nor at its assigned one answer unable and change nothing (`FlightCommandHandler.RefuseUnflyableSpeed`; COMMANDS.md).

    One it can fly at the assigned altitude is accepted and this cap holds it until the aircraft gets there. A scenario preset speed is never refused, only capped. `SPDN` sets `SpeedLimitsLifted`, which lifts the whole limit, 91.117 and Vmo/Mmo both.
  - **An approach clearance still clears a retained target** (`ApproachCommandHandler`; 7110.65 §5-7-1.d / AIM 4-4-12.g), and the
    final-approach speed schedule overwrites whatever stands before touchdown.
- **A `SpeedCeiling` is a one-way ratchet under a `ManagesSpeed` phase.** The floor/ceiling self-target drags IAS down to the ceiling, the snap nulls `TargetSpeed`, and with the auto schedule suppressed nothing raises IAS again when the ceiling rises or is removed — the aircraft holds the ceiling speed until the phase writes its own target.

  `FinalApproachPhase` writes none before its deceleration stages, so whoever stamps a ceiling on an aircraft on a long final must also restore its speed (the generator stream's `RestoreManagedSpeed`, `docs/scenario-loading-and-generation.md`).

  `RNS` and `RPS` close it for themselves: `FlightCommandHandler.ResumeSpeed` (behind `ApplyResumeNormalSpeed` and `ApplyResumePublishedSpeed`) writes the scheduled final-approach speed back for an aircraft in `FinalApproachPhase` outside `ArrivalSpacingManager.SpeedRestoreGateNm` and more than `SpeedRestoreDeadbandKts` slow (AIM 4-4-12.f.1).

  When the aircraft is on that profile and either test fails, nothing is handed back, and the instructor's answer says so: `Resume normal speed — already on its final approach speed profile, no change` instead of the plain `Resume normal speed` every other case gets (the pilot readback is the same either way; `ResumeNormalSpeedOnFinalTests`).

  Re-accelerating it a few miles before the phase slows it again is what §5-7-1's lead ("Avoid adjustments requiring alternate decreases and increases") and §5-7-1.a.3(e) rule out, and AIM 4-4-12.f scopes "resume normal speed" to before an approach clearance.

  Both restores are one-shot writes, and one write is enough under a lower regulatory cap (a Class B shelf's 200 kt, 91.117(c)): the restored target stays standing at the cap and the aircraft takes it up once the cap lifts (the 91.117 bullet above).
- **There are FOUR aircraft categories — Jet, Turboprop, Piston, Helicopter.** A summary that lists only the first three is incomplete; the Helicopter
  column is real and aviation-reviewed. Unknown ICAO types fall back to **Jet** (after the sibling-map attempt).
- **Constants are NOT read from `CategoryPerformance` directly in production.** `AircraftPerformance.*` is the entry point: per-type profile with
  altitude-breakpoint interpolation + `IProfileCorrectionAdapter`, falling back to `CategoryPerformance` only when no profile exists. Editing the
  `CategoryPerformance` switch alone won't change behavior for a typed aircraft that has a profile.
- **`PreferredTurnDirection` is intentionally NOT cleared each tick in `UpdateNavigation`** — only on heading snap (`UpdateHeading`) or route
  exhaustion. Clearing it per tick would stomp departure-phase direction bias (`TRDCT`/`TLDCT`) before the initial turn completes.
- **`UpdateHeading` refuses to rotate a ground aircraft below `StationaryGroundSpeedKts` (0.1 kt)** — the last defense against a parked aircraft
  pirouetting on a stale `TargetTrueHeading`. Airborne helicopters at GS 0 (hover) are exempt because `IsOnGround` is false.
- **Step climb/descent and speed look-ahead planners run BEFORE the integrators and overwrite `TargetAltitude`/`TargetSpeed` from upcoming route
  constraints.** The *speed* look-ahead (`UpdateSpeedPlanning`) self-suppresses under `SpeedRestrictionsDeleted`; under `HasExplicitSpeedCommand` or `TargetMach` it plans only controller-set crossings. The *altitude* planners (`UpdateClimbPlanning` /
  `UpdateDescentPlanning`) have **no** explicit-command guard — they overwrite `TargetAltitude` whenever the route carries an altitude restriction in force
  (a controller's crossing, or a published one under a via), so a bare `CM`/`DM` altitude you expect to stick can be re-derived by a planner the moment the route has a constrained fix.
- **A fix sequences before the fix itself.** The sequencing threshold (turn anticipation or `NavArrivalNm`) puts the "crossing" of a fix up to several nm short of it, so route-distance logic (the speed planner's deceleration end, restriction applies, reverts) is measured to that point, not to the fix position.
- **`DesiredDecelRate` is honored only on the deceleration branch of `UpdateSpeed` and ignored when accelerating; `DesiredAccelRate` is the
  mirror.** Both must be cleared on phase transition or firm braking (or a tug's 0.3 kt/s crawl) leaks into the next phase. See
  [landing-and-runway-exit.md](landing-and-runway-exit.md) and [ground/pushback.md](ground/pushback.md).
- **Magnetic declination is cached with a 0.02° box; a non-finite/out-of-range `Position` SKIPS the WMM update (logged) and keeps the stale
  value** rather than throwing — so an upstream bug can leave declination subtly stale without an obvious crash. `DeclinationCachePosition` is
  `[JsonIgnore]` on the state but snapshotted, so a restore keeps it.
- **`UpdatePosition` uses a flat-earth approximation** (`NmPerDegLat = 60`, longitude scaled by `cos(lat)`); fine per-tick but not a great-circle
  step — don't reuse it for long-range geometry.
- **While a phase is active, `UpdateCommandQueue` skips block advancement.** Conditional triggers still fire, but the three `Notify*` hooks are
  the only way a queued block fires during a phase. Forget the hook and a sequential compound sits untouched.
