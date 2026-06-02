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
(`src/Yaat.Sim/FlightPhysics.cs:39`; three thinner overloads at `:24`, `:29`, `:34` forward with defaults). In order, per call
(`FlightPhysics.cs:93`):

1. Magnetic-declination cache refresh (see [Magnetic declination caching](#magnetic-declination-caching)).
2. Backward-compat IAS seed: an airborne aircraft with `IndicatedAirspeed <= 0` but `GroundSpeed > 0` copies GS into IAS (`FlightPhysics.cs:88`).
3. `UpdateNavigation` — sequence the next route waypoint, fire AT-fix triggers, compute the steering heading.
4. `UpdateDescentPlanning` / `UpdateClimbPlanning` — step-altitude planners for route constraints.
5. `UpdateSpeedPlanning` — proactive speed look-ahead for procedure speed restrictions.
6. `UpdateHeading` — turn toward `TargetTrueHeading`.
7. `UpdateAltitude` — climb/descend toward the resolved altitude goal.
8. `UpdateSpeed` — accelerate/decelerate toward `TargetSpeed`.
9. `AutoCancelSpeedAtFinal` — drop explicit ATC speed restrictions at 5 nm final.
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

`AircraftState.Targets` is **get-only** (`AircraftState.cs:117`) and restored in place via `ControlTargets.RestoreFrom(dto, ac.Targets)`
(`ControlTargets.cs:126`) — you cannot reassign `ac.Targets` from a snapshot, and `NavigationRoute` (a get-only list) restores via
`Clear()` + `Add` (`ControlTargets.cs:145`). See [aircraft-data-model.md](aircraft-data-model.md) for the get-only restore-in-place pattern.

### Field table

| Field | Type | Written by | Read by physics in | Lifecycle |
|---|---|---|---|---|
| `TargetTrueHeading` | `TrueHeading?` | `UpdateNavigation`, phases, `FH`/turn handlers | `UpdateHeading` | Set each tick by nav while a route exists; nulled when the route is exhausted (`UpdateNavigation`) |
| `PreferredTurnDirection` | `TurnDirection?` (Left/Right) | departure/turn handlers, phases | `UpdateHeading` (via `ResolveDirection`) | **Not** cleared per tick; cleared on heading snap, route exhaustion, or when a phase ends/is cleared (`Phase.OnEnd` + dispatcher phase-clear) |
| `TurnRateOverride` | `double?` deg/s | pattern phases, `TRATE` | `UpdateHeading`, `UpdateNavigation` anticipation | Persists; null = category default |
| `TargetAltitude` | `double?` ft MSL | `CM`/`DM` handlers, climb/descent planners, phases | `UpdateAltitude` (via `ResolveAltitudeGoal`) | **Self-nulls** on snap (±10 ft) |
| `AltitudeFloor` / `AltitudeCeiling` | `double?` ft MSL | "maintain VFR at/above" / "at/below" handlers | `ResolveAltitudeGoal` | Persist until cleared |
| `DesiredVerticalRate` | `double?` fpm (+ = climb) | climb/descent planners | `UpdateAltitude` | Nulled on altitude snap and on fix revert; null = category rate. (`EXPEDITE` does not write a value here — it sets `Procedure.IsExpediting`, which `UpdateAltitude` reads as a 1.5× rate multiplier, and nulls `DesiredVerticalRate` on clear) |
| `TargetSpeed` | `double?` KIAS | `SPD`/`SLOW` handlers, speed planner, Mach hold, phases | `UpdateSpeed` | **Self-nulls** on snap (±2 kt) |
| `DesiredDecelRate` | `double?` kt/s (+ = decel) | `LandingPhase` / `RunwayExitPhase` | `UpdateSpeed` **decel branch only** | Must be cleared on phase transition; null = category default |
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
  `UpdateAltitude` nulls `TargetAltitude` and `DesiredVerticalRate` and clears `IsExpediting` on the ±10 ft snap (`FlightPhysics.cs:820`);
  `UpdateSpeed` nulls `TargetSpeed` on the ±2 kt snap (`FlightPhysics.cs:982`). "My target vanished" is by design.
- **`AssignedX` is persistent UI/autopilot state.** `AssignedAltitude`/`AssignedSpeed`/`AssignedMagneticHeading` persist past the snap so the
  controller still sees the last assigned value on the datablock and the autopilot can hold it.

Heading is handled differently: `UpdateHeading` snaps `TrueHeading` to the goal and clears `PreferredTurnDirection` on the snap
(`FlightPhysics.cs:774`), but it does **not** null `TargetTrueHeading` (the nav step owns that lifecycle, nulling it only on route exhaustion).

## Airspeed frames: IAS / TAS / GroundSpeed / Mach

This is the load-bearing model the rest of physics rests on. (This material previously lived only in an archived rationale plan; it now lives here.)

- **`IndicatedAirspeed` (KIAS) is the single source of truth for airspeed** (`AircraftState.cs:106`). It is what ATC commands, what
  `TargetSpeed` targets, and the only stored airspeed value. Set it; everything else derives.
- **TAS is computed, never stored.** `WindInterpolator.IasToTas(ias, altitudeFt)` (`WindInterpolator.cs:105`) converts IAS→TAS via ISA
  compressible-flow relations (CAS→Mach→TAS). TAS rises with altitude even at constant IAS.
- **`GroundSpeed` is DERIVED on every read — it has no setter** (`AircraftState.cs:85`). On the ground it returns `IndicatedAirspeed` directly.
  Airborne it is `|TAS·(cos/sin heading) + WindComponents|`: TAS along `TrueHeading`, plus the cached wind vector, magnitude. You cannot "set
  ground speed," and `GS == IAS` is only true on the ground or in still air at sea level.
- **`WindComponents` (N, E knots) is cached during `UpdatePosition`** (`FlightPhysics.cs:1041`) so `AircraftState.GroundSpeed` can derive
  airborne GS without a `WeatherProfile` in hand. It is zero on the ground / with no weather. Note: both `GroundSpeed`'s getter and `UpdatePosition`
  build the airborne GS vector by projecting TAS along the same `TrueHeading` basis and adding the wind vector — so the displayed GS magnitude
  matches the position step. (`UpdatePosition` then derives `TrueTrack` from `atan2` of that wind-summed vector, which is why track diverges from
  heading in wind.)
- **Mach hold** recomputes equivalent IAS each tick: `UpdateSpeed` reads `TargetMach`, calls `WindInterpolator.MachToIas(mach, alt)`
  (`WindInterpolator.cs:129`), and writes the result into `TargetSpeed` so a constant-Mach cruise descends in IAS as it climbs
  (`FlightPhysics.cs:887`). Below 10,000 ft the Mach-derived IAS is still capped at 250 unless waived.

`WindInterpolator` also provides `TasToIas` (`:117`, the inverse — used for resolving cruise TAS to an IAS), `IasToMach` (`:142`),
`ComputeWindCorrectionAngle` (`:184`), and `GetWindComponents` (`:86`). Wind layers are vector-interpolated by altitude
(`GetWindAt`, `:34`) so the 0/360 boundary is handled correctly.

## Heading integration (`UpdateHeading`)

`UpdateHeading(aircraft, cat, deltaSeconds)` (`FlightPhysics.cs:749`):

1. If `TargetTrueHeading` is null → zero the bank angle and return.
2. **Ground no-pivot guard**: if `IsOnGround && GroundSpeed < StationaryGroundSpeedKts` (0.1 kt, `:747`) → zero bank and return. A parked
   aircraft cannot rotate on a stale target heading. Airborne helicopters at GS 0 (hover) are exempt because `IsOnGround` is false.
3. `diff = NormalizeAngle(goal − current)` (shortest-path, `:773`). If `|diff| < HeadingSnapDeg` (0.5°, `:12`) → snap `TrueHeading` to the goal,
   **clear `PreferredTurnDirection`**, zero bank, return.
4. Otherwise `turnRate = TurnRateOverride ?? AircraftPerformance.TurnRate(...)`, `maxTurn = turnRate × deltaSeconds`, direction from
   `ResolveDirection(diff, PreferredTurnDirection)` (`:1720` — preferred direction overrides shortest path), `turnAmount = min(|diff|, maxTurn)`.
5. Bank angle: `BankAngle = atan(TAS_kts × turnRate × BankAngleCoeff) × 180/π`, signed by turn direction.
   `BankAngleCoeff = π/180 × 1.6878 / 32.174 ≈ 0.0009146` (`:734`) — the kt→ft/s and ft/s²-gravity unit fold.

## Lateral navigation & turn anticipation (`UpdateNavigation`)

`UpdateNavigation(aircraft, weather)` (`FlightPhysics.cs:107`) drives `TargetTrueHeading` toward the head of `NavigationRoute`:

- **Sequencing threshold.** Fly-over and terminal waypoints sequence within `NavArrivalNm` (0.5 nm, `:15`). Fly-by waypoints with a following
  leg use turn anticipation: `ComputeAnticipationDistanceNm(GS, turnRate, legBearing, nextLegBearing)` = `R·tan(θ/2)`, where `R = GS / turnRate`
  (rad/s), capped at 5 nm, 0 for turns < 1° (`:546`). The sequencing threshold becomes `max(anticipation, NavArrivalNm)`.
- **Abeam sequencing.** Inside the anticipation zone, the waypoint is not popped on distance — it is popped when the along-track distance along
  the *next* leg bearing goes non-negative (`GeoMath.AlongTrackDistanceNmRaw >= 0`, `:149`), i.e. the aircraft has passed abeam.
- **Arc-blended steering.** While in the anticipation zone but not yet sequencing, the steering heading is the tangent to the inscribed turn
  circle (`ComputeArcBlendedHeading`, `:574`), not the straight bearing to the waypoint — so fly-by turns are smooth.
- **Wind correction angle.** When airborne with weather, the final steering heading is `bearing + WCA` where
  `WCA = WindInterpolator.ComputeWindCorrectionAngle(bearing, TAS, windFrom, windSpeed)` (`:230`) — the aircraft crabs so it tracks a straight
  ground path, not a downwind pursuit curve.
- **Fix-constraint apply/revert.** On sequencing, `ApplyFixConstraints` (`:619`) applies the next fix's altitude/speed restriction (gated by
  SID-via / STAR-via mode), and `NavigationTarget.Revert*` fields restore the prior target/assigned alt+speed when sequencing past a constrained
  fix. `FrdArrivalNm` (1.5 nm, `:16`) and `GroundArrivalNm` (0.05 nm, `:17`) are the FRD-point and ground-entity arrival thresholds used by the
  queue triggers, not by route sequencing.

`PreferredTurnDirection` is **not** cleared in `UpdateNavigation` (`:234` comment) — only on heading snap or route exhaustion — so departure
direction bias (`TRDCT`/`TLDCT`) survives until the initial turn completes.

## Vertical integration (`UpdateAltitude`) + step planners

`UpdateAltitude(aircraft, cat, deltaSeconds)` (`FlightPhysics.cs:801`):

- On the ground → `VerticalSpeed = 0`, return.
- `goal = ResolveAltitudeGoal(aircraft)` (`:865`) clamps `TargetAltitude` between `AltitudeFloor` and `AltitudeCeiling`; if there is no
  `TargetAltitude` it synthesizes one only when the aircraft is below a floor or above a ceiling (with the snap deadband).
- `|diff| < AltitudeSnapFt` (10 ft, `:13`) → snap `Altitude`, zero VS, **null `TargetAltitude` and `DesiredVerticalRate`, clear `IsExpediting`**.
- Rate = `|DesiredVerticalRate|` if set, else `AircraftPerformance.ClimbRate`/`DescentRate(...)`. `IsExpediting` multiplies by **1.5×** (`:846`).
  `change = min(|diff|, rate/60 × delta)`; `VerticalSpeed` is signed by climb/descend.

**Step climb/descent planners** run *before* the integrators (`UpdateClimbPlanning` `:352`, `UpdateDescentPlanning` `:246`). Each scans the route
for the next altitude-constrained fix, resolves the constraint via `ResolveAltitudeRestriction` (`:691`), writes `TargetAltitude`, and computes
the `DesiredVerticalRate` needed to hit it at the fix: `requiredFpm = altDelta / timeMinutes`, capped at **2× the standard category rate**
(`:333`). Descent planning is suppressed under `SidViaMode`; climb planning under `StarViaMode` — the SID-via / STAR-via split decides which
planner owns the route. Both also activate (outside via mode) whenever the route carries any explicit altitude restriction.

## Speed integration (`UpdateSpeed`) + look-ahead planning

`UpdateSpeed(aircraft, cat, deltaSeconds)` (`FlightPhysics.cs:881`) is a layered cascade. The layers run in this exact order; getting the order
wrong silently lets one layer stomp or lose to another:

1. **Mach hold** — if `TargetMach` set and airborne, write `MachToIas(...)` into `TargetSpeed` (capped 250 below 10k unless waived) (`:887`).
2. **Floor/ceiling self-target** — if `TargetSpeed` is null and IAS violates a `SpeedFloor`/`SpeedCeiling`, set `TargetSpeed` to the breached
   bound (`:899`). The 91.117 cap also clamps the *effective* floor/ceiling here below 10k.
3. **Auto altitude-band schedule** — if `TargetSpeed` is null, airborne, **not** `HasExplicitSpeedCommand`, `ActiveApproach` is null, the current
   phase does **not** have `ManagesSpeed == true`, and the aircraft is climbing/descending toward a target altitude → set `TargetSpeed` to
   `AircraftPerformance.DefaultSpeed(...)`, honoring an active `SpeedCeiling` (`:925`). This is the layer the approach/pattern phases suppress.
4. **Ground `SpeedLimit` clamp** — `goal = min(goal, Ground.SpeedLimit)` when on the ground (`:961`); this is the ground-conflict cap from
   [tick-loop.md](tick-loop.md)'s `GroundConflictDetector`.
5. **14 CFR 91.117** — `goal = min(goal, 250)` when airborne below 10,000 ft MSL and not `IsSpeedLimitWaived` (`:966`).
6. **`SpeedCeiling` continuous clamp** — `goal = min(goal, SpeedCeiling)` again, so even a non-procedural `TargetSpeed` (auto schedule,
   pre-ceiling controller assignment) cannot escape the cap (`:975`).
7. **Snap + integrate** — `|diff| < SpeedSnapKts` (2 kt, `:14`) → snap IAS, **null `TargetSpeed`**. Otherwise accelerate at
   `AircraftPerformance.AccelRate` or decelerate at `DesiredDecelRate ?? AircraftPerformance.DecelRate` (`:1000`). `DesiredDecelRate` is honored
   **only on the deceleration branch** — it is ignored when accelerating.

**Look-ahead planning** (`UpdateSpeedPlanning`, `:458`) runs before the integrator and pre-sets `TargetSpeed` so the aircraft *arrives* at a
procedure speed restriction at the constrained fix rather than reacting after it: it computes change-time vs time-to-fix and starts decel only
when within 10% of the change time (accel starts immediately). It is fully suppressed when `HasExplicitSpeedCommand`, `SpeedRestrictionsDeleted`,
or `TargetMach` is set.

**`AutoCancelSpeedAtFinal`** (`:1402`) runs right after `UpdateSpeed`: at ≤ 5 nm from the assigned-runway threshold it clears `TargetSpeed`,
`HasExplicitSpeedCommand`, `SpeedFloor`, and `SpeedCeiling` — but only for *explicit ATC* speed restrictions (gated on
`HasExplicitSpeedCommand`), per 7110.65 §5-7-1.a.2.d. Phase-managed approach speeds (FAS) are left alone.

The **AIM 5-4-1 NOTE 2 procedural-speed memory**: when the route is exhausted, `UpdateNavigation` publishes the last procedure speed
(`Procedure.LastProcedureSpeedKts`) as a `SpeedCeiling` (`:192`) so the auto schedule cannot accelerate the aircraft above the last published
speed — unless an explicit ATC speed is active.

## Position integration (`UpdatePosition`)

`UpdatePosition(aircraft, deltaSeconds, weather)` (`FlightPhysics.cs:1009`) advances lat/lon with a flat-earth approximation
(`NmPerDegLat = 60`, longitude scaled by `cos(lat)`):

- **Ground branch** (`:1014`): re-enforce `Ground.SpeedLimit` on IAS, move along `Ground.PushbackTrueHeading ?? TrueHeading` at
  `IAS / 3600` nm/s, and set `TrueTrack = TrueHeading` (track follows heading directly; GS = IAS on the ground).
- **Airborne branch** (`:1034`): `TAS = IasToTas(IAS, alt)`; ground-speed vector = `TAS·(cos/sin heading) + wind`; **cache `WindComponents`**;
  `TrueTrack = atan2(gsE, gsN)`; displace by the full GS vector. This is where wind makes track diverge from heading.

## Performance constants — the two-tier lookup

Production code **does not read `CategoryPerformance` directly** for profile-covered fields. The entry point is `AircraftPerformance.*`
(`src/Yaat.Sim/AircraftPerformance.cs`):

1. **Per-type profile** — `AircraftProfileDatabase.Get(aircraftType)` returns the `AircraftProfiles.json` profile, which carries
   altitude-breakpoint values interpolated by `InterpolateByAltitude` (`AircraftPerformance.cs:44`) and may be adjusted by an
   `IProfileCorrectionAdapter` (default pass-through; an installable adapter, e.g. Eurocontrol, can correct climb/approach/pattern speeds and
   climb rates at runtime via `SetProfileCorrectionAdapter`).
2. **Category fallback** — when no profile exists for the type, `AircraftPerformance.*` falls back to the validated `CategoryPerformance` switch.

So **editing the `CategoryPerformance` switch alone does not change behavior for a typed aircraft that has a profile.** Category determination is
`AircraftCategorization.Categorize(aircraftType)` (`AircraftCategory.cs:34`): strips the `H/J/S` wake prefix (`AircraftState.StripTypePrefix`),
looks up the type, then tries `AircraftSiblingMap.TryResolve`, and finally **falls back to `AircraftCategory.Jet`** for unknown types.

### The validated category table (supersedes CLAUDE.md's 3-category summary)

There are **four** categories — Jet, Turboprop, Piston, **Helicopter** (`AircraftCategory.cs:6`). All values are
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

`DefaultSpeed(cat, altitude)` (`AircraftCategory.cs:836`) — the auto-schedule speed by altitude band:

| Category | Speed schedule (KIAS by altitude) |
|---|---|
| Jet | < 10k → 250 · < 18k → 280 · < 28k → 290 · ≥ 28k → 280 |
| Turboprop | < 10k → 200 · < 24k → 250 · ≥ 24k → 270 |
| Piston | < 10k → 110 · ≥ 10k → 120 |
| Helicopter | < 10k → 100 · ≥ 10k → 120 |

Snap thresholds are constants on `FlightPhysics`: heading `HeadingSnapDeg = 0.5°`, altitude `AltitudeSnapFt = 10 ft`, speed
`SpeedSnapKts = 2 kt` (`FlightPhysics.cs:12`).

`CategoryPerformance` carries many more constants used by the pattern, ground, and rollout subsystems — pattern geometry (`PatternSizeNm`,
`CrosswindExtensionNm`, `BaseExtensionNm`, `PatternTurnRate`, `PatternDescentRate`, `DownwindSpeed`, `BaseSpeed`), holding (`MaxHoldingSpeed`),
and the full taxi/exit/crossing speed set (`TaxiSpeed`, `TaxiDecelRate`, `RolloutDecelRate`, `ExitTurnOffSpeed`, …). Those are owned by the
pattern-geometry, ground, and landing docs; this doc owns the airborne kinematics rows above plus the shared `DefaultSpeed` schedule.

## The command-queue half (`UpdateCommandQueue`) — summary only

`UpdateCommandQueue(aircraft, deltaSeconds, aircraftLookup)` (`FlightPhysics.cs:1062`) evaluates the `CommandQueue` after position integration.
Full depth is in [command-pipeline.md](command-pipeline.md) and [phases.md](phases.md); the physics-relevant facts:

- **While a phase is active** (`Phases.CurrentPhase != null`), block *advancement* and untriggered-block *application* are skipped — phases own
  `ControlTargets`. Only conditional triggers are still watched (`ApplyReadyConditionalBlocks`, `:1143`), so e.g. `SPD 210 UNTIL 10` can fire its
  block mid-approach (`:1076`).
- **Trigger types** (`IsTriggerMet`, `:1264`): `ReachAltitude`, `ReachFix`, `InterceptRadial`, `ReachFrdPoint`, `GiveWay`, `DistanceFinal`,
  `OnHandoff`, `AtGroundEntity`, `EnteringHoldingAfterExit`, `AfterRunwayCrossing`.
- **The three `Notify*` hooks** are the *only* way a queued block fires while a phase owns control: `NotifyFixSequenced` (`:1513`, from route/
  approach sequencing), `NotifyGroundEntityReached` (`:1555`, from `TaxiingPhase`), `NotifyPhaseAdvanced` (`:1629`, from `PhaseRunner`). Forget the
  hook and a sequential compound like `TAXI…;CTO` sits untouched until the next user dispatch.

## Magnetic declination caching

The first thing `Update` does is refresh `aircraft.Declination` from the WMM — but only when the aircraft has moved more than a 0.02° position
box (≈ 1.2 nm of latitude, `FlightPhysics.cs:55`). WMM is a degree-12 spherical-harmonic evaluation and, at 4 Hz per aircraft, dominates per-tick
physics cost; declination changes slowly enough that sub-nm motion can reuse the cached value.

If `Position` is non-finite or out of range (`|lat| > 90`, `|lon| > 180`), the WMM update is **skipped and logged** (`:72`) rather than throwing
— a `Geo.Coordinate` ctor would otherwise crash the tick. The previously cached value is kept. `DeclinationCachePosition` is `[JsonIgnore]`
(`AircraftState.cs:63`), so the first tick after a snapshot restore always runs the full WMM eval to re-warm the cache.

## Footguns & gotchas

- **`IndicatedAirspeed` is the only airspeed source of truth; `GroundSpeed` has no setter.** It is recomputed on every read from TAS + cached
  `WindComponents`. Trying to "set ground speed," or assuming `GS == IAS` airborne, is wrong — they diverge with altitude (TAS) and wind.
- **`TargetSpeed` and `TargetAltitude` self-null on arrival** (±2 kt / ±10 ft). "My target vanished" is by design. `AssignedSpeed`/
  `AssignedAltitude` persist for the UI/autopilot; `TargetX` is the transient physics goal. Don't conflate them.
- **`UpdateSpeed` is a fixed 7-layer cascade** (Mach hold → floor/ceiling self-target → auto altitude-band schedule (suppressed by
  `ActiveApproach` OR `ManagesSpeed`) → ground `SpeedLimit` clamp → 91.117 250-kt cap (unless `IsSpeedLimitWaived`) → continuous `SpeedCeiling`
  clamp → snap+integrate). Add a new speed influence at the wrong layer and it silently loses to or stomps another.
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
- **`DesiredDecelRate` is honored only on the deceleration branch of `UpdateSpeed` and ignored when accelerating.** It must be cleared on phase
  transition or firm braking leaks into the next phase. See [landing-and-runway-exit.md](landing-and-runway-exit.md).
- **Magnetic declination is cached with a 0.02° box; a non-finite/out-of-range `Position` SKIPS the WMM update (logged) and keeps the stale
  value** rather than throwing — so an upstream bug can leave declination subtly stale without an obvious crash. `DeclinationCachePosition` is
  `[JsonIgnore]`, so the first tick after a snapshot restore re-runs the full WMM eval.
- **`UpdatePosition` uses a flat-earth approximation** (`NmPerDegLat = 60`, longitude scaled by `cos(lat)`); fine per-tick but not a great-circle
  step — don't reuse it for long-range geometry.
- **While a phase is active, `UpdateCommandQueue` skips block advancement.** Conditional triggers still fire, but the three `Notify*` hooks are
  the only way a queued block fires during a phase. Forget the hook and a sequential compound sits untouched.
