# Airborne Approach & Pattern Geometry

> Read this before touching anything under `src/Yaat.Sim/Phases/Approach/` or `src/Yaat.Sim/Phases/Pattern/`, the
> traffic-pattern builders (`PatternGeometry`, `PatternBuilder`), `AirborneFollowHelper`, `GlideSlopeGeometry`,
> `HoldingEntryCalculator`, or `ApproachEvaluator`/`ApproachScore`. It documents the *geometry* of the airborne
> approach and traffic-pattern phases — the coordinate sign conventions, the leg state machines, the intercept
> math, holding/PT geometry, and visual following.

## Scope — and what this is NOT

This doc owns the **geometry and the per-tick decision logic** of the airborne phases that fly an aircraft from
vectors / pattern entry down to the seam where the ground rollout takes over. It deliberately does not restate:

- **The phase contract** (`OnStart`/`OnTick`/`CanAcceptCommand`, `CommandAcceptance`, `PhaseRunner` lifecycle,
  auto-append, the four-step snapshot contract) — see [phases.md](phases.md). Its phase catalog lists these phases
  and carries a short "Procedure turns and transition selection" subsection.
- **How a command reaches a handler** (parsing, partial-callsign resolution, dispatch, the queue/trigger machinery)
  — see [command-pipeline.md](command-pipeline.md) and [command-handlers.md](command-handlers.md). This doc only
  names the construction call-sites (which handler builds which phase) and defers the wiring to those docs.
- **Per-tick ordering** — `OnTick` runs inside the physics sub-tick *before* `FlightPhysics.Update`; phases write
  `ControlTargets` and physics consumes them next. See [tick-loop.md](tick-loop.md).
- **The integration math** (turn rate, bank angle, IAS/TAS/GS) and validated performance constants — see
  [flight-physics.md](flight-physics.md).
- **The ground half of an arrival** — `LandingPhase` rollout, `RunwayExitPhase`, the node-based ground stack, and
  the `FinalApproachPhase` final-approach-course alignment ramp — see
  [landing-and-runway-exit.md](landing-and-runway-exit.md). `FinalApproachPhase` is the **seam**: every approach and
  pattern sequence in this doc converges onto it, and it is where the airborne geometry hands off to the rollout.
- **Lateral-alignment go-around** (`FinalApproachPhase.CheckLateralAlignmentGate`, issue #412 follow-up): inside
  1 nm (along-track) of the **live assigned runway's** landing threshold, an aircraft more than 0.08 nm off its
  guidance line (the assigned centerline outright when the approach chain targets a *different* runway — the
  stale-datum case; the ramp-lerped FAC/anchor line when it targets the assigned one, so published offset
  approaches judge against the line they intend), tracking within 45° of the course (established, not mid-join),
  and whose extended ground track will not cross the line by the threshold (`sin(track-off) < |xte|/dist`), goes
  around after 3 s — spoken "going around, we're not lined up", terminal names the runway and offset (7110.65
  §3-10-5.d "aligned with the wrong surface", the ARV automation of §5-14-9; AIM 5-5-5.a.1(b)/(a.2)). The 45°
  establishment window means only a nearby **parallel or small-angle offset** wrong surface is caught — a
  crossing/diverging wrong runway keeps the track outside the window. Commanded tight joins are exempt by
  construction: the establishment window skips base-to-final rollouts and retarget sweeps,
  `RetargetRunway`/`StartWithLateralGateGrace` hold the gate off 30 s (released early once established on
  course) after a sidestep or the #292 low-approach runway change, and CLANDF suppresses it entirely. Marginal
  by-the-threshold realignments are left to `LandingPhase.CheckStabilizationGate` to judge.

## Coordinate primitives you must trust

All airborne geometry is built on three `GeoMath` primitives (`src/Yaat.Sim/GeoMath.cs`). Their sign conventions
are load-bearing; a flipped sign silently sends the aircraft to the wrong side.

| Primitive | Signature | Convention |
|---|---|---|
| `SignedCrossTrackDistanceNm` | `(point, ref, heading)` | **Positive = RIGHT of the reference heading**, negative = left (`GeoMath.cs:143`). Computed as `dist · sin(bearingToPoint − heading)`. |
| `AlongTrackDistanceNm` | `(point, ref, heading)` | **Positive = AHEAD** along the heading, negative = behind (`GeoMath.cs:159`). Computed as `dist · cos(bearingToPoint − heading)`. |
| `BearingTo` | `(from, to)` | Initial great-circle bearing, 0–360° true (`GeoMath.cs:30`). |
| `ProjectPoint` | `(from, heading, distNm)` | Projects a new lat/lon along a heading (`GeoMath.cs:69`). Flat-earth approximation (`NmPerDegLat`). |

Both signed primitives are derived from a single bearing/distance pair: `SignedCrossTrackDistanceNm` takes the
`sin` component, `AlongTrackDistanceNm` takes the `cos`. There are `LatLon`-overloaded forms and `…Raw` forms that
take a raw bearing instead of a typed `TrueHeading`.

**Why this matters:** the entire pattern leg state machine is built on along-track and cross-track measurements
(not waypoint-arrival distance), and the pattern-side / deconfliction / lateral-offset / intercept logic all
depend on getting the right-of-heading sign right per `Left`/`Right` pattern.

## Traffic pattern construction — `PatternGeometry.Compute`

`PatternGeometry.Compute` (`src/Yaat.Sim/Phases/PatternGeometry.cs:168`) builds a `PatternWaypoints` from the
runway, aircraft category, pattern direction, and optional size/altitude overrides. The six waypoints are:

`DepartureEnd` → `CrosswindTurn` → `DownwindStart` / `DownwindAbeam` → `BaseTurn` → `Threshold`,

plus five per-leg headings (`Upwind`, `Crosswind`, `Downwind`, `Base`, `Final`).

The **turn-offset sign** is the inverse of what you might guess from the pattern name (`PatternGeometry.cs:180`):

```
turnOffset = (direction == Left) ? -90.0 : +90.0
crosswindHeading = runwayHeading + turnOffset
baseHeading      = downwindHeading + turnOffset   // downwind = runway reciprocal
```

A **left** pattern (all turns left) uses a **−90°** offset; a **right** pattern uses **+90°**. The waypoints are
then projected:

- `CrosswindTurn` = the **departure end of the runway (DER)**. The upwind length is governed by runway
  geometry, not pattern size: per AIM 4-3-2 the crosswind turn is commenced **beyond the departure end of the
  runway within 300 ft of pattern altitude**, so a smaller pattern keeps the same at-the-DER upwind (and never
  turns crosswind while still over the runway). `UpwindPhase` enforces that gate (see below).
- `DownwindStart` = crosswind turn + `patternSize` perpendicular (along `crosswindHeading`).
- `DownwindAbeam` = threshold + `patternSize` perpendicular — the canonical on-downwind reference point.
- `BaseTurn` = downwind abeam + `BaseExtensionNm` along the downwind heading.

**Which "threshold".** `Compute` takes the authored `GroundRunway` (a required parameter — pass `null` only
when no airport map is loaded), and the arrival waypoints — `Threshold`, `DownwindAbeam`, and therefore
`BaseTurn` — are built on `LandingThreshold.Resolve`, i.e. the **landing** threshold. `DepartureEnd` and
`CrosswindTurn` deliberately stay on the **pavement** end: AIM 4-3-2 anchors the crosswind turn beyond the
*departure* end, and pre-threshold pavement is usable for takeoff in either direction (AIM 2-3-3.h.2). At
KSJC 30L (2,537 ft displaced) that is a 0.42 nm shift in the arrival half of the pattern and none in the
departure half. Full datum table: [`landing-and-runway-exit.md`](landing-and-runway-exit.md#displaced-thresholds-which-datum).

**Size/altitude resolution.** `ResolveAuthoredOverrides` (`PatternGeometry.cs:152`) composes a command override
(e.g. TPA/PSIZE) over the airport-authored `GroundRunway` data: command wins, then authored data fills in.
Authored `PatternAltitudeAglFt` is interpreted as **feet AGL above field elevation** and translated to MSL via
`runway.AirportElevationFt` — but it is the airport's *established* pattern altitude, and the AIM 4-3-3.a
category rule is applied to it rather than taking it verbatim: turbine aircraft (Jet/Turboprop) fly
**authored + 500** (AIM 4-3-3.a.2), helicopters stay at or below their absolute **500 AGL** (AIM 4-3-3.a.3),
props fly it as published. With no authored value the category defaults apply (prop 1,000; turbine 1,500;
helicopter 500). A command TPA override wins verbatim for every category — every altitude is flyable, so unlike
the pattern-size flyability floor there is no clamp.

**Past-abeam descent target.** The downwind's past-abeam descent and `BasePhase`'s circuit-entry target both aim
at the **glideslope-intercept altitude at the base-to-final rollout point** — the base extension actually flown
plus one turn radius from the threshold (`GlideSlopeGeometry.AltitudeAtDistance`), capped at the current
altitude. This replaced the old fixed fractions (60% of TPA on downwind, 50% on a waypoint-less base entry),
which stopped tracking the glide path once the flyability floor decoupled pattern width from TPA. The
extended-downwind `_altitudeFloor` uses the same rollout-distance expression (the aircraft flies base + final,
not the base-turn-to-threshold diagonal). AIM FIG 4-3-2 key 2: pattern altitude to abeam, then a continuous
descent. When a size override is applied, only `BaseExtensionNm` scales **proportionally** by
`patternSize / defaultSize` (a smaller pattern has a tighter base leg); the crosswind turn stays anchored at the
DER. The resolved size is carried on `PatternWaypoints.PatternSizeNm` so the downwind/base descent geometry uses
the actual offset, not the bare category default. Pattern altitude defaults to
`runway.AirportElevationFt + CategoryPerformance.PatternAltitudeAgl(category)`.

**Two elevation datums.** `RunwayInfo.ElevationFt` is the active end's **landing threshold** elevation, from the
CIFP (see [navigation-database.md](navigation-database.md#runway-end-elevations-come-from-the-cifp)); a glidepath
is referenced to it, because that is what the TCH is measured above (AIM 5-4-5.b.3).
`RunwayInfo.AirportElevationFt` is the **field**, one value for the airport, and pattern altitude uses it —
AIM 4-3-3 recommends one TPA above the field and the Chart Supplement publishes one value, so the pattern must
not tilt with the runway. Level runways make the two identical, which is why the split only shows on a sloped
field: KASE's thresholds are 140 ft apart. `GoAroundHelper.ResolvePatternAltitude` and the IFR visual downwind in
`ApproachCommandHandler` are pattern altitudes too and use the airport datum; the glidepath sites
(`FinalApproachPhase`, `ApproachNavigationPhase`, `VfrFollowPhase`, pattern *final* entry altitudes) use the
threshold.

**Authored data needs a resolved ground layout.** `ResolveAuthoredOverrides` reads the airport's authored
`patternSize`/`patternAltitude` from a `GroundRunway` — resolved from the **context's** ground layout
(`ctx.GroundLayout` / `DispatchContext.GroundLayout`, which falls back to the assigned-runway airport when the
per-aircraft `Ground.Layout` is unset), not the raw `aircraft.Ground.Layout`. The auto-cycle (`PhaseRunner`) uses
the resolved layout so an airborne pattern aircraft with no cached layout still gets the authored low TPA instead
of reverting to the category default and flying a long, climb-bound upwind (issue #210).

**Runway deconfliction.** `ApplyRunwayDeconfliction` (`PatternGeometry.cs:248`) shrinks the pattern size if the
downwind leg would encroach on a neighboring runway. For each other runway it:

1. Skips the same physical runway and any runway that **physically crosses** this one (`RunwaysCross` —
   segment-intersection of the two centerlines, `PatternGeometry.cs:326`). Converging runways that meet beyond
   their endpoints do not count as crossing.
2. Computes the signed cross-track of the other runway's midpoint relative to this runway's centerline, then flips
   the sign so positive always means "on the pattern side" (Left ⇒ negate). Negative ⇒ opposite side ⇒ no conflict.
3. If the pattern-side distance is inside `patternSize + RunwayBufferNm` (0.15 nm), shrinks the size to
   `crossTrackDist − RunwayBufferNm`, but only if the result stays ≥ `MinPatternSizeNm` (0.4 nm) — otherwise it
   leaves the size alone (a viable pattern can't be fit, so don't bother).

**Flyability floor (issue #412).** After the override/deconfliction resolution — and before the
`sizeRatio`/`BaseExtensionNm` scaling, so the final-approach length grows with the pattern —
`PatternGeometry.Compute` clamps the size to `MinFlyablePatternSizeNm(aircraftType, category, windSpeedKt)`:
`BasePhase.TurnRadiusNm(downwindSpeed + wind) + TurnRadiusNm(baseSpeed + wind)`, using the **per-type** leg speeds
(`AircraftPerformance.DownwindSpeed`/`BaseSpeed`). The downwind→base and base→final turns each consume one turn
radius of lateral room, so any narrower pattern is geometrically forced through the final approach course — at OAK
a PAY3 given the authored 0.5 nm 28L pattern rolled out on the parallel 28R final (AIM FIG 4-3-3 key 7 prohibits
that track; AIM 4-3-3.b sizes the pattern by aircraft performance). The floor **wins over authored data, PSIZE,
and deconfliction alike** — a wide downwind overlying a neighboring runway beats overshooting onto a parallel's
final. Authored/deconflicted sizes are widened silently; an explicitly commanded PS/PATTSIZE below the floor gets a
spoken pilot "unable, can't turn it that tight" transmission from `TrySetPatternSize` (AIM 4-4-1.b — the pilot
informs ATC when unable; AIM 4-3-5 — no unexpected pattern maneuvers; there is no codified numeric-width
instruction in 7110.65 §3-8-1, which is why PS needs a non-standard response).
`Compute` therefore takes the aircraft type and current wind speed (`AircraftState.WindSpeedKts`) — wind inflates
the planning speeds because the turn triggers anticipate on ground speed.

## Pattern leg state machine — `PatternBuilder.BuildCircuit`

`PatternBuilder.BuildCircuit` (`src/Yaat.Sim/Phases/PatternBuilder.cs:28`) builds the phase sequence from a
`PatternEntryLeg` (`Upwind`/`Crosswind`/`Downwind`/`Base`/`Final`). Each entry leg drops the legs the aircraft
has already passed:

| Entry leg | Phase sequence (before Final/Landing) |
|---|---|
| `Upwind` | Upwind → Crosswind → Downwind → Base |
| `Crosswind` | Crosswind → Downwind → Base |
| `Downwind` | Downwind → Base |
| `Base` | Base (carries `FinalDistanceNm`) |
| `Final` | *(none)* |

Every sequence ends with `FinalApproachPhase` then a landing phase (`HelicopterLandingPhase` for helicopters,
`TouchAndGoPhase` when `touchAndGo`, else `LandingPhase`). `BuildNextCircuit` (`PatternBuilder.cs:84`) builds the
next full circuit from upwind for auto-cycling traffic; `UpdateWaypoints` (`PatternBuilder.cs:101`) re-points the
waypoints of all pending/active pattern phases (used when the pattern is resized live).

### Runway transitions — `BuildRunwayTransitionCircuit`

Two commands move an aircraft from one runway's pattern into another's: a cross-runway takeoff clearance
(`CTO MRT 28R` from runway 33, "cleared for takeoff rwy 33, make right traffic rwy 28R" —
`DepartureClearanceHandler.ApplyClosedTraffic` detects pattern runway ≠ takeoff runway) and an `MLT`/`MRT` with a
runway argument issued while the aircraft is on an active pattern leg (`TryChangePatternDirection`, below). Both
build the first circuit through `PatternBuilder.BuildRunwayTransitionCircuit(flownRunway, patternRunway, …)`, which
picks one of two shapes by `RunwayGeometry.AreCloseParallels` (same airport, headings within 5°, centerlines within
0.25 nm — AIM §5-4-19's side-step envelope, shared with the `EF` sidestep):

- **Close parallels** (OAK 28R → 28L): `UpwindPhase` → `CrosswindPhase` on *transition* waypoints
  (`PatternGeometry.ComputeTransition`) → `DownwindPhase` (`RejoinTrack = true`) → `BasePhase` →
  `FinalApproachPhase` → terminator on the pattern runway. The transition waypoints are the pattern runway's
  geometry with the crosswind turn anchored at whichever of the two departure ends projects farther along the pattern
  runway's heading, so the aircraft continues the upwind it is on and turns crosswind only once it has cleared
  **both** departure ends (AIM 4-3-2); the downwind start follows from that turn point. No midfield crossing.
- **Crossing runways** (33 → 28R): `UpwindPhase` (waypoints from the **departure** runway) → the shared field-crossing
  prefix (`PatternBuilder`: `MidfieldCrossingPhase` with `InitialTurn` toward the pattern side, waypoints from the **pattern**
  runway, `CrossAtPatternAltitude` — a departure never left the pattern, so AIM 4-3-3.a's entry height does not apply →
  `DownwindPhase` with `RejoinTrack`) / `BasePhase` / `FinalApproachPhase` / terminator (pattern runway). The same prefix
  serves `TryEnterPattern`'s wrong-side entry and the MLT/MRT wrong-side switch, where it inserts `TeardropReentryPhase`
  only when the crossing altitude is actually above the pattern altitude (a jet entering at field + 1,500 ft; never with
  a controller-assigned pattern altitude). Per
  AIM 4-3-2 the departure/upwind leg belongs to the departure runway; downwind/base/final belong to the landing runway.

For a takeoff clearance the departure runway is carried on `PhaseList.DepartureRunway` (read by
`LineUpPhase`/`LinedUpAndWaitingPhase`/`TakeoffPhase`), while `AssignedRunway`/`PatternRunway` hold the pattern
runway (read by the circuit/final/landing phases). Subsequent circuits auto-cycle entirely on the pattern runway
(`BuildNextCircuit`, which reads `PatternRunway ?? AssignedRunway`) — which is why every writer of `AssignedRunway`
that changes the runway (`TryChangePatternDirection`, `ApplyRebuiltPatternChain`) writes `PatternRunway` too; a stale
one built the next circuit on the runway the aircraft had been told to leave.

**Armed pattern runway — `COPT/TG/SG/LA MLT <rwy> [alt]`.** The option clearances take the `CTO` pattern modifier with a
runway (`DepartureCommandParser.ParsePatternModifierArgs` is the shared token parse). `TrySetupTouchAndGo`/`…StopAndGo`/
`…LowApproach`/`…ClearedForOption` stamp `Phases.PatternRunway` (and the direction / altitude override) while
`AssignedRunway` stays the runway the clearance is flown on; `PhaseRunner`'s auto-cycle sees the two differ after the
terminator (or a pattern go-around) and builds `BuildRunwayTransitionCircuit(AssignedRunway → PatternRunway)` instead of
`BuildNextCircuit`, then moves `AssignedRunway` across. A pre-issued clearance (`DCT VPCOL; ERD 28R` then `COPT MLT 28L`)
carries the runway/altitude on `PendingLandingClearance` and consumes them when the entry builds its circuit. An
unplanned go-around voids the clearance the modifier rode on: `PhaseRunner.VoidArmedPatternRunwayOnGoAround` resets
`PatternRunway` to the runway being flown, builds the plain next circuit there and warns the RPO to re-issue
(AIM 5-5-5.a.6 — the pilot requests clearance for specific action after a missed approach); `OTG` is an explicit
standalone instruction and still fires after a go-around. The option readback appends " (crossing midfield)" when the
armed runway is not a close parallel, since that transition joins through a `MidfieldCrossingPhase`.
`PhaseClearSummary` labels from `AssignedRunway` only, so an armed modifier never relabels the approach in flight. The
handlers share one carrier for the modifier (`OptionPatternModifier`: direction, runway, altitude) and one body
(`TrySetupOptionClearance`); the parser's `PatternModifierArgs` is a separate, parse-only result type. A pre-issued
modifier naming a runway that is not a close parallel warns "… will cross midfield" when the entry builds its circuit.
A `FOLLOW` rebuilds the phase list but carries an armed `PatternRunway` across (`VfrFollowPhase`, same airport only) and
warns that the follower will leave the sequence after its next terminator; when the follow's runway is the armed one
the arming is satisfied. The modifier's runway/altitude tokens resolve through
`Commands/Arguments/` (`RunwayArgument`, `AltitudeArgument`, `CommandArgumentResolver` walking the overload shapes
`[]`, `[Runway]`, `[Altitude]`, `[Runway, Altitude]`): where `Runway` and `Altitude` are both candidates a 1–2 digit token
binds as the runway and an altitude needs 3+ digits; once the runway slot is bound only `Altitude` remains and the
usual shorthand applies (`MLT 15 15`).
Guard: `OptionClearancePatternModifierTests`. `OTG MLT 28L` reaches the same transition through the queue instead
(`BlockTriggerType.AfterCycleTerminator`, see docs/flight-physics.md), firing `MLT 28L` on the fresh upwind.
`MidfieldCrossingPhase.InitialTurn` biases the initial join turn (released once roughly pointed at the join target);
it is null for arrival / wrong-side joins so their established shortest-turn behavior is unchanged.

**`MLT`/`MRT` on an active pattern leg** (`PatternCommandHandler.BuildActiveLegChain`). The runway the aircraft is
flying is read from the active leg's own waypoints (`ResolveFlownRunway` matches the leg's threshold waypoint against BOTH ends of every runway — cross-track ≤ 0.05 nm
from the centreline, along-track within a window clamped to half the pavement so a short strip cannot answer with its
reciprocal, nearest end wins — and returns `ForApproach(end)`; `NavigationDatabase.GetRunways` yields one `RunwayInfo` per
pavement anchored at one end), not from the PhaseList metadata. With a runway switch the aircraft transitions leg to leg
instead of re-entering the pattern from outside it:

| Active leg | Same side | Opposite side |
|---|---|---|
| Upwind | transition circuit above (either direction) | transition circuit above |
| Crosswind | same-side rebuild | wrong-side midfield crossing via `BuildFieldCrossingPrefix` (shared with `TryEnterPattern`: crossing → `TeardropReentryPhase` for jet/turboprop entry crossings → downwind with `RejoinTrack`) |
| Downwind, close parallels | same-side rebuild with `RejoinTrack` (the parallel's downwind is laterally offset) | **crossover at midfield**: the old runway's `DownwindPhase` with `ExitAtMidfield` → `MidfieldCrossingPhase` (`CrossAtPatternAltitude`, `InitialTurn` toward the field = the old pattern's turn sense) → new `DownwindPhase` (`RejoinTrack`) → … Past midfield already: the chain starts at the crossing |
| Downwind, other pairs | same-side rebuild with `RejoinTrack` | wrong-side midfield crossing (unchanged) |
| Base | same-side rebuild | wrong-side midfield crossing (unchanged) |

`CrossAtPatternAltitude` keeps an in-pattern crossover at TPA for every category. The entry crossing (wrong-side
`TryEnterPattern`, crossing-runway departures) puts large/turbine aircraft at the higher of their own TPA and field
elevation + 1,500 ft (AIM 4-3-3.a.2: "not less than 1,500 feet AGL or 500 feet above the established pattern
altitude" — the turbine TPA already is the established altitude + 500, so adding 500 again had jets crossing at
2,000 ft AGL). A controller-assigned pattern altitude (`Pattern.AltitudeOverrideFt`) is flown as assigned by every category (AIM 4-4-7.b),
so the turbine floor applies only to an unassigned TPA. Midfield triggers lead by `DownwindPhase.MidfieldLeadNm` (0.05 nm), not the 0.3 nm leg tolerance — on
OAK's 0.9 nm runways the larger lead started the crossover at the departure end. Midfield is
`PatternGeometry.MidfieldAlongTrackNm`: the along-track (threshold origin, downwind heading — the axis
`DownwindPhase` measures every trigger on) of the midpoint between the downwind start and the abeam point, i.e. the
point `MidfieldCrossingPhase` steers to. The abeam point itself sits at along-track ≈ 0, so half *its* along-track
is the threshold, not midfield; the "midfield downwind" reminder and `ExitAtMidfield` both use `MidfieldAlongTrackNm`.
Guards: `ParallelRunwayMltFromUpwindTests`, `ParallelRunwayMltFromDownwindTests`, `Issue7MltCrossRunwayWrongSideTests`
(all on the OAK 28R/28L recording), `IssueCrossRunwayCtoMrtTests` (33 → 28R keeps the crossing).

**The critical model: legs complete on along-track / cross-track, NOT waypoint arrival.** A pattern leg phase does
not "arrive at" the next waypoint — it measures the aircraft's projection onto the leg axis and fires when the
along-track or cross-track crosses a threshold. Editing the waypoints without understanding this trigger model
produces aircraft that overshoot or turn early.

### Downwind leg — `DownwindPhase`

`DownwindPhase` (`src/Yaat.Sim/Phases/Pattern/DownwindPhase.cs`) flies the downwind reciprocal heading at pattern
altitude. Its triggers are all measured as along-track distance from the threshold along the downwind heading
(`AlongTrackToleranceNm = 0.3`):

- **Abeam detection / descent start** (`DownwindPhase.cs:199`): when `aircraftAlongTrack ≥ _abeamAlongTrack − tol`,
  it sets `_pastAbeam`, calls `ApplyPastAbeamDescentTargets`, and begins decelerating from `DownwindSpeed` toward
  `BaseSpeed`.
- **Past-abeam descent target** (`ApplyPastAbeamDescentTargets`): for a normal pattern, the target is the
  **glideslope-intercept altitude at the base-to-final rollout point** — the base extension actually flown plus
  one turn radius from the threshold (`GlideSlopeGeometry.AltitudeAtDistance`), capped at the current altitude.
  The **altitude floor** for an extended/held downwind is recomputed per tick from the aircraft's own along-track
  position (`min(TPA, glide altitude at position + turn radius)`), so a long extension levels back at pattern
  altitude rather than pinning to the nominal base-turn geometry.
- **Base-turn trigger / completion** (`DownwindPhase.cs:286`): completes when
  `aircraftAlongTrack ≥ _baseTurnAlongTrack − tol`.
- **Midfield broadcast** (`DownwindPhase.cs:166`): at half the abeam along-track, if no landing clearance, the
  pilot reminds the controller (solo voices it as delayed pilot speech; RPO mode raises a `PendingWarnings` entry).

**Short approach (SA).** `ApplyShortApproach` (`DownwindPhase.cs:305`) compresses `_baseTurnAlongTrack` to
`_abeamAlongTrack + ShortApproachBaseExtensionNm`, clamped via `Math.Max(compressed, currentAlongTrack)` so the
aircraft never reverses backward to an already-passed base-turn point. When SA is **armed before** the leg
activates, `OnStart` sets `_pastAbeam = true` to suppress the normal abeam descent trigger and begins descending
immediately. `RemoveShortApproach` (MNA, `DownwindPhase.cs:342`) restores the original base-turn from the
waypoints; if the aircraft has already flown past it, completion next tick is correct (you can't un-shorten an
already-flown pattern).

**Lateral offset (OFL/OFR).** While `LateralOffset` is non-null, `OnTick` overrides `TargetTrueHeading` via
`PatternLateralOffsetHelper.ComputeTargetHeading` referenced from the downwind abeam point (which is on the
downwind track, not the runway centerline), then holds a parallel track once acquired (`DownwindPhase.cs:151`).
Downstream completion logic still uses along-track and is unaffected by the perpendicular dogleg.

### Base leg — `BasePhase`

`BasePhase` (`src/Yaat.Sim/Phases/Pattern/BasePhase.cs`) turns onto the base heading and descends. **Final-turn
initiation** is cross-track based, not along-track: it completes when the cross-track from the extended centerline
drops to within the turn radius (`crossTrack ≤ turnRadiusNm`, `BasePhase.cs:165`), where
`turnRadiusNm = groundSpeed / (turnRate · 62.832)` floored at `MinTurnRadiusNm = 0.15`. This produces a
geometrically correct 90° arc that rolls out on the centerline.

The descent target depends on `FinalDistanceNm` (set by ELB/ERB or by short-approach base entry):

- **With `FinalDistanceNm`** (`BasePhase.cs:78`): the 90° base→final turn translates the aircraft one turn radius
  further along the final, so the **rollout distance is `finalDist + turnRadiusNm`**. The phase aims for the 3°
  glideslope altitude at that rollout distance — `min(currentAltitude, gsAlt)` so it never climbs to capture — and
  computes a descent rate to make it (clamped between the category default and 1500 fpm).
- **Without** (wrong-side / midfield-crossing entry, `BasePhase.cs:107`): the aircraft is already at TPA and the
  final distance is unknown up front, so it falls back to the halfway-between-pattern-and-threshold heuristic.

A follower on base also judges its spacing behind the traffic it follows every tick and may widen the base, turn out to the downwind heading, or go around: see **Spacing on base — `BaseFollowSpacing`** under *Visual following*. A widen holds the present altitude while it lasts and then re-plans the descent from where the aircraft is (`PlanDescent`, the same planning `OnStart` does), to the 3° glidepath at the longer rollout.

### Spacing turns — `MakeTurnPhase` (`L270`/`R270`/`L360`/`R360`/`P270`)

`MakeTurnPhase` (`src/Yaat.Sim/Phases/Tower/MakeTurnPhase.cs`) is a generic, pattern-*un*aware executor: it turns
`TargetDegrees` in a fixed `Direction`, then hands off to whatever phase was queued next. It has no idea it's in a
pattern — it just counts cumulative `|Δheading|` until it's turned enough, holding a `PreferredTurnDirection` bias so
`FlightPhysics` never snaps to the short way (`ResolveDirection` obeys the bias unconditionally). `ComputeExitHeading`
is the only geometry it knows: a 360 rolls out on the start heading; a **270 rolls out 90° *opposite* the turn sense**
(`start ± 90`).

That exit rule is the footgun behind `P270`. `P270` ("plan a 270 for spacing at the next pattern turn") wants the
aircraft to reach the *same* course a normal 90° pattern turn would — but the long way, to burn time behind traffic.
Because a normal pattern turn is 90° *in* the pattern direction, and a 270 rolls out 90° *opposite* its own sense, the
planned 270 must be flown **opposite** the traffic-pattern direction (right traffic → left 270; left traffic → right
270). Turning the pattern's own way instead rolls out 180° off, on the next leg's reciprocal — the aircraft turns
straight toward final (the short way) and points back down the downwind. `TryPlan270`
(`PatternCommandHandler.cs`) derives the direction; assert on the *exit heading* (`== FinalHeading`), not just the
`Direction` label — the label-only test is exactly what let the wrong-way bug ship. The manual `L270`/`R270` verbs take
the direction the controller typed and are not subject to this derivation.

## Pattern entry & re-entry

### Entry classification — `PatternEntryPhase.ClassifyDownwindEntry`

`PatternEntryPhase` (`src/Yaat.Sim/Phases/Pattern/PatternEntryPhase.cs`) navigates to the entry point (descending
to pattern altitude, decelerating to downwind speed) and completes when its `NavigationRoute` drains. The
`PatternEntryKind` (rendered as status text) is classified by `ClassifyDownwindEntry` (`PatternEntryPhase.cs:240`)
from the angular delta between aircraft track and the downwind course, combined with which side of the centerline
the aircraft is on:

| Angular delta | Pattern-side test | Kind |
|---|---|---|
| ≤ 20° | — | `Direct` |
| 20°–60° | on pattern side (within `CenterlineEpsilonNm`) | `FortyFive` |
| 20°–60° | clearly wrong side | `Midfield` |
| > 60° | clearly pattern side | `Midfield` |
| > 60° | otherwise | `Crosswind` |

`CenterlineEpsilonNm = 0.25` is a hysteresis band: aircraft straddling the extended centerline are treated as
on-pattern-side so the classification doesn't whipsaw tick-to-tick. The signed pattern-side distance flips the
runway-heading cross-track by the pattern sign (`Right ⇒ +1`, `Left ⇒ −1`).

### Present-position downwind join — `PatternCommandHandler.IsAtOrPastDownwindEntry`

A downwind entry (`ERD`/`ELD`) normally routes through a `PatternEntryPhase` targeting the abeam entry point with a
lead-in chosen by `ChooseDownwindLeadIn` — a 45° midfield intercept (AIM 4-3-3) vs a straight-in join to the extended
downwind, scored by total heading change with a penalty on any single turn > 120°. **Both lead-in candidates project
behind the abeam point**, so an aircraft already alongside the downwind — at/past the entry point along-track (with
`PresentPositionJoinAlongTrackToleranceNm = 0.25` slack), no further than ~1 nm past the base turn
(`PresentPositionJoinBeyondBaseTurnNm`), and laterally within `PresentPositionJoinLateralWidthFactor = 1.5` × pattern
width of the downwind track — would be commanded a U-turn back up the downwind: the "about-face onto a parallel offset
track" of issue #352. Such an aircraft skips the entry entirely: the circuit's `DownwindPhase` gets `RejoinTrack =
true` (the same bounded-intercept re-capture used by wrong-side crossings) and it joins from present position.
Aircraft further out along the extended downwind are **arrivals**, not pattern members, and still fly the normal
45°/extended entry — that far-out geometry is exactly where turning around to enter properly is correct.

### Teardrop re-entry — `TeardropReentryPhase`

For **turboprop/jet** aircraft entering from the wrong side, `PatternCommandHandler` inserts a
`MidfieldCrossingPhase` and then a `TeardropReentryPhase` (`PatternCommandHandler.cs:484`). Pistons and helicopters
cross at TPA and drop straight into downwind — no teardrop. `TeardropReentryPhase`
(`src/Yaat.Sim/Phases/Pattern/TeardropReentryPhase.cs`) builds a three-waypoint outbound-then-inbound descent that
rejoins downwind at the abeam point via a 45° intercept:

1. **Outbound anchor** = abeam + `CrosswindHeading` × outbound distance (Jet 3.0 / TP 2.5 / else 2.0 nm).
2. **45° lead-in** = abeam + reverse-45°-entry heading × lead-in distance (Jet 2.0 / TP 1.5 / else 1.0 nm).
3. **Abeam** = the downwind abeam point itself.

The aircraft enters from `MidfieldCrossingPhase` at the large/turbine crossing altitude of **TPA + 500 ft** (AIM
4-3-3.1.b / AC 90-66B). The route waypoints carry `At` altitude restrictions that step it down across the three
points: **TPA + 250**, **TPA + 50**, then **TPA** (`TeardropReentryPhase.cs:69`). (The class doc-comment and the
debug log describe the band loosely as "TPA+500 → TPA"; the actual per-waypoint restrictions are +250/+50/+0 — the
+500 is where the aircraft *starts*, handed in by `MidfieldCrossingPhase`.) After the route drains, `DownwindPhase`
takes over with the aircraft already tracking the 45° intercept course.

### Final entry distance (EF) — `PatternCommandHandler`

`EF` ("enter final", `PatternEntryLeg.Final`, no explicit distance) places the join point on the extended
centerline through one of three paths in `TryEnterPattern`:

1. **Close-in aligned** (`isCloseInFinal`, `PatternCommandHandler.cs:158`): aircraft inside the standard
   glideslope-TPA intercept distance, within the close-in angle envelope (`MaxCloseInFinalAngleOffDeg`: 30° at
   ≥2 nm / 20° inside 2 nm / 45° helicopters), and able to descend over the path. It anchors the entry at the
   aircraft's **current position** (a straight-in; `useAircraftPositionAsEntry`).
2. **Altitude-aware "make straight-in"** (`ComputeAltitudeAwareFinalEntryDistanceNm`): aircraft *outside* that
   angle envelope (a diagonal/base join) and on the approach side. The aircraft **descends immediately on the
   diagonal cut-in** toward the runway and joins final as **close to the threshold as it can** while still reaching
   the glideslope altitude by the join — a shortcut, not a fixed base. The helper walks candidate join distances
   outward from the category minimum final (`MinimumPerpendicularBaseFinalDistanceNm`: jet/TP 2.0, piston 1.0,
   heli 0.5 nm) and takes the **first (closest)** at which the aircraft, descending at the category
   `PatternDescentRate` over the diagonal `sqrt(alongGap² + crossTrack²)`, can lose enough altitude to be on the
   3° (6° heli) glideslope at the join. So a low aircraft (or any aircraft with a long diagonal to descend on)
   shortcuts to the minimum final; a higher one — needing more descent room — joins a longer final. It is
   **capped at the aircraft's along-track-outbound distance** so `EF` never routes the aircraft outbound / behind
   its present position (which also makes the loop check inapplicable — the reverse-to-a-far-entry geometry can't
   form — so it is skipped when this distance is set, `PatternCommandHandler.cs:232`). When even the minimum final
   exceeds the along-track cap the helper returns `null`, and the pattern retarget (path 3) or the fixed fallback
   (path 4) handles the geometry. The `PatternEntryPhase` descends to the glideslope altitude at the join
   (`PatternCommandHandler.cs:454`), then `FinalApproachPhase` tracks the 3° slope inbound.
3. **Pattern retarget** (`isPatternRetarget`): a *crossing* aircraft — one outside the close-in angle envelope
   and **not** already tracking outbound — that is *inside* the category minimum final. The diagonal join above
   has already bailed (its along-track cap is inside the minimum final), and the fixed entry (path 4) lies behind
   it, so a straight-in is impossible. `EF` degrades to a **base entry**: `effectiveEntryLeg = Base`,
   `effectiveFinalDistanceNm = ` the aircraft's along-track, `useAircraftPositionAsEntry = true` — the
   ERB-no-distance shape, so `PatternBuilder.BuildCircuit` yields `BasePhase → FinalApproachPhase → terminator`
   from the present position with no `PatternEntryPhase` (#284). Gates: the target centerline is still *ahead*
   (cross-track is closing, not overshot), the along-track exceeds `MinPatternRetargetFinalNm` (¼ nm for
   pistons/helicopters per AIM FIG 4-3-2 note 3; jets/turboprops keep the straight-in floor, which makes their
   retarget window empty so they reject), and the base-descent budget (below) is not `Infeasible`. The base leg's **side is derived from the aircraft's signed cross-track**, not
   from `InferDefaultPatternDirection` — an aircraft north of the 28L centerline flies a *right* base to 28L
   regardless of 28L's default left pattern. Aircraft already in `FinalApproachPhase` for the requested runway are
   excluded (mid roll-out their heading reads as "crossing"; the same-runway continue below owns them).
4. **Fixed fallback**: aligned aircraft *outside* the close-in distance (and explicit-distance `EF`) keep the
   fixed glideslope-TPA entry (`PatternAltitudeAgl / FeetPerNm`) plus the loop-feasibility check.

**Short-final guards (all return before the phase teardown, so the aircraft keeps its current approach — #228).**
An aircraft on very short final is *inside* the fixed entry point, so the paths above would otherwise place the
fixed entry behind it and fly it outbound to re-enter (the "tour of the airspace" bug):
- **Same-runway continue (no-op):** if the aircraft is already in `FinalApproachPhase` for the requested runway,
  near the centerline (`EstablishedFinalCrossTrackNm`) and inside the standard entry distance, `TryEnterPattern`
  returns success ("continuing final") without rebuilding — preserving the live final / glideslope / clearance
  state (`PatternCommandHandler.cs`, right after the parallel-sidestep branch). The angle tolerance spans the
  whole base-to-final roll-out (`EstablishedFinalAngleOffDeg` = 90°), so a mid-turn `EF` is not mistaken for a
  crossing aircraft.
- **Never-outbound reject** (loop-feasibility block, "Unable, short final"). Two sibling conditions, both
  requiring the aircraft to be on the approach side with the fixed entry point behind it:
  - `onShortFinalInsideEntry` — *aligned* (within the close-in angle envelope), near the centerline, inside
    `MinimumPerpendicularBaseFinalDistanceNm`. An aligned aircraft merely *offset* from the centerline is **not**
    short final; it re-intercepts via the far entry, which is the normal way back onto a final it is parallelling.
  - `crossingFinalInsideFloor` — *crossing* the final approach course, not tracking outbound, inside the category
    floor, and not retargetable (path 3 declined it: too steep for its category, too high, or already past the
    centerline). This is the #284 backstop.

  "Tracking outbound" means the ground track lies within `OutboundTrackToleranceDeg` (60°) of the runway
  reciprocal — the downwind leg (AIM §4-3-2.c.4), plus the 45° downwind entry of §4-3-3. That is the only
  aircraft for which an entry point farther from the threshold is the rest of the pattern rather than a reversal;
  a textbook 90° base leg sits a comfortable 30° outside the cone. Upwind and crosswind traffic never reaches
  either condition — it is on the departure side of the threshold, so its along-track-outbound is negative and the
  `acAlongOutbound > 0` term excludes it. A runway argument that already retargeted `AssignedRunway`/
  `DestinationRunway` is restored on this reject.

**Rejects never destroy the phase chain.** `aircraft.Phases.Clear(ctx)` runs only after every reject path,
immediately before the new chain is built. Clearing earlier meant a rejected entry (e.g. `ERB` "too close for
base") left the aircraft with a skipped, dead chain and no current phase at all, flying straight ahead.

**Runway changes void the landing clearance.** A clearance names a runway (7110.65 §3-10-5), so a pattern entry
whose runway argument differs from `ClearedRunwayId` nulls `LandingClearance` + `ClearedRunwayId` (and therefore
also the `touchAndGo` terminator choice). Same-runway re-entry keeps it. Mirrors `TryChangePatternDirection`
(MRT/MLT). `ApplySidestep` returns before this and deliberately *transfers* the clearance — the instrument
approach clearance authorizes the parallel (§4-8-7).

**Wrong-side has a deadband for a same-runway MLT/MRT, not for entries.** `TryChangePatternDirection` decides
through `IsOnWrongSideForPattern`: the signed offset toward the pattern side must fall below
`-WrongSidePatternDeadbandNm` (0.1 nm) before a `MidfieldCrossingPhase` is inserted, so an aircraft essentially on
the extended centerline (climbing out on upwind after a go-around, offset ≈ 0) is *not* wrong-side and rebuilds
standard closed traffic (upwind → crosswind → downwind) instead of banking across the field at 600 ft. The inline
check in `TryEnterPattern` (downwind/base entries) is still `patternSideOffset < 0` with no deadband — deliberately
left alone; the two are meant to differ. A command that also *switches runways* does not turn on the deadband at all:
the aircraft is on a leg of the runway it is leaving, and the transition table above picks the manoeuvre from that leg
(the side only chooses between the crossover and the same-side rebuild on the downwind). Guards:
`BugN500mMltUpwindTurnsLeftImmediateTests` (−0.021 nm on the upwind centerline, bare `MLT`, must not cross) and
`ParallelRunwayMltFromUpwindTests` (the 28R upwind is −0.165 nm off 28L, `MLT 28L` must not cross either).

**Pre-issued clearances fold into the same `standingClearance`.** A `CLAND`/`TG`/`SG`/`LA`/`COPT` issued while the
entry that would build the approach is still *queued* is stored on `AircraftPattern.PendingLandingClearance`
(the entry's runway resolved at issue time, so a contradicting runway is rejected there rather than dropped here).
`TryEnterPattern` **peeks** at it where `standingClearance` is computed — it has to be resolved before `touchAndGo`,
which picks the circuit's terminal phase from the clearance and is passed into `PatternBuilder.BuildCircuit`. The slot
is only **consumed** (`ConsumePendingLandingClearance`) at the build site, past every reject path, so an entry
rejected with "unable, too close for base" doesn't eat the controller's clearance. A live `Phases.LandingClearance`
still wins; the pre-arm only fills the gap. Consumption also voids a clearance whose runway disagrees with the circuit
that built (RPO warning, §3-10-5), which is what makes an orphaned pre-arm safe after a vector drops the queued entry,
and installs the exact `StopAndGoPhase`/`LowApproachPhase` terminal that `PatternBuilder` cannot build on its own.

When the altitude-aware join is capped at along-track but the aircraft still cannot lose its altitude over the
cut-in + final at the category `PatternDescentRate`, `EF` succeeds but raises an `AircraftState.PendingWarnings`
entry ("unable to descend for straight-in … — too high") — a controller-facing advisory, not radio phraseology.
The angle envelope is an airmanship analogy to TBL 5-9-1, not a regulatory VFR-pattern mandate (AIM 4-3-3 does not
quantify an intercept angle).

**Base-descent budget** (`PatternCommandHandler.EvaluateBaseDescent`, used by the `ERB`/`ELB`-no-distance gate and
the pattern retarget gate; the present-position downwind join's simpler excess-above-TPA check shares its speed source and
rate ceiling but has no marginal tier — its fallback is just the longer entry maneuver, not a refusal). It is the predicate of the descent `BasePhase`
actually flies, so gate and phase cannot disagree: speed = the standing `SPD` assignment if any, else
`AircraftPerformance.BaseSpeed(type, category)` (never the category constant — a C208 Caravan is not a Dash-8),
floored at 60 kt (`BasePhase.PlannedSpeedKt`); rollout = along-track + `BasePhase.TurnRadiusNm`; target = the
glideslope altitude at rollout (`GlideSlopeGeometry.AltitudeAtDistance`, TCH included); only the altitude *above* that
target has to be shed, over the base leg (the aircraft's cross-track — final rides the glideslope and is not descent
room; the `EF` diagonal walk above keeps the normal `PatternDescentRate` because `PatternEntryPhase` descends at the
default physics rate, not at a base-leg maximum). The ceiling is `BasePhase.MaxDescentRateFpm` = min(`CategoryPerformance.MaxPatternDescentRate`
(Jet 2000 / TP 1500 / piston 1500 / heli 1000 — the terminal-configuration ceiling, distinct from the normal-profile
`PatternDescentRate`), the rate at `MaxPatternDescentAngleDeg` (jet 6.5° / TP + piston 7.5° / heli 8°) for the planned
speed) — drag limits the path *angle*, so slowing an aircraft never buys descent room. Required fpm ≤ ceiling →
`Feasible`; ≤ 1.5× → `Marginal` (accepted, plus a `PendingWarnings` entry "… high for base to … — may need S-turns or a
go-around"; per AIM 4-4-1.b the pilot requests an amendment rather than refusing the leg, and the too-high-at-MAP auto
go-around is the fallback); beyond → `Infeasible` (`ERB`: "Unable, too high for base"). `BasePhase` plans its own
descent from the same helpers — planned speed, `AltitudeAtDistance` target, `MaxDescentRateFpm` clamp — and sizes it
over its actual cross-track at start rather than the nominal pattern width (#401).

## Approach intercept — `InterceptCoursePhase`

`InterceptCoursePhase` (`src/Yaat.Sim/Phases/Approach/InterceptCoursePhase.cs`) flies the assigned intercept
heading until the aircraft captures the final approach course (FAC), then hands off to `FinalApproachPhase`. It is
built by JFAC (vectored), the **implied-PTAC** branch of CAPP (on present heading, no nav route), and PTAC — see
construction call-sites below.

**Turn anticipation.** Rather than waiting to cross the centerline, the phase computes a lead distance equal to
the turn radius (`turnRadiusNm = groundSpeed / (turnRate · 62.832)`) and begins the capture turn when
`crossTrack ≤ leadDistNm` (`InterceptCoursePhase.cs:139`), provided the intercept is legal. This prevents lateral
overshoot and hands the (possibly still-turning) aircraft to `FinalApproachPhase` early so the turn-on completes
under lateral tracking. There is also an already-on-course fast path
(`crossTrack < AlreadyOnCourseThresholdNm = 0.15`) and a sign-flip centerline-crossing detector
(`InterceptCoursePhase.cs:169`) as the fallback when anticipation didn't fire.

> **Glideslope descent waits for lateral establishment, not capture.** The early/anticipated capture hands off
> while the aircraft may still be a full category gate off the FAC (30°, 45° for a helicopter);
> `FinalApproachPhase` does **not** start the glideslope
> descent (`_gsCaptured`) until the aircraft is laterally established — within `GsEstablishedHeadingDeg = 5°` of the
> FAC **and** within `GsEstablishedCrossTrackNm = 0.15 nm` of centerline (`IsLaterallyEstablishedForGs`). This
> reproduces "maintain until established on the localizer, cleared ILS" (AIM 5-4-7 / 7110.65 5-9-4): the aircraft
> holds the assigned altitude through the turn-on, then descends. The gate is bypassed when there is no approach
> clearance (pattern/visual turning final), for pattern traffic, for visual approaches (`VIS` prefix), and for
> forced intercepts (`InterceptCaptureAngleDeg` beyond the category's own bust-through gate — those intentionally
> S-turn back and would otherwise be stranded high; a helicopter force-captured at 35° is inside its 45° envelope,
> joins cleanly, and so stays behind the gate). The 91.117 250-kt cap and the level-off-then-intercept-from-below
> logic are unchanged.

**The three heading-diff checks (mag-var tolerance).** The capture/bust-through decision must tolerate the gap
between the published FAC, the runway-number heading, and the controller's assigned magnetic heading. A two-way
comparison reintroduces a false bust-through bug, so the effective diff
(`ComputeEffectiveHeadingDiff`, `InterceptCoursePhase.cs:231`) takes the **minimum** of:

1. **Aircraft true heading vs FAC** — `aircraftHeading.AbsAngleTo(FinalApproachCourse)`.
2. **Aircraft *magnetic* heading vs runway-number heading** — both magnetic. The runway is parsed from `ApproachId` by
   `RunwayIdentifier.FromApproachId` (`I12 → 120°`, `ILS28R → 280°`, `L04L → 40°`, `I29RY → 290°` — the vNAS
   multiple-approach letter after the designator is ignored; `RunwayNumberHeading()` re-derives it per call, nothing
   is cached or snapshotted). A runway number is a magnetic figure: comparing it with the *true* heading (the
   pre-#429 code) was 13° too lenient at KFAT and too strict under west variation. Absent when the id carries no
   runway (`VDM-A`).
3. **Assigned magnetic heading vs runway-number heading** — both magnetic, so mag variation cancels (e.g. rwy 12
   at 150° mag: true heading ~163° vs FAC 130° = 33° fails, but assigned 150° vs rwy 120° = 30° passes). The
   heading is the phase's own `AssignedInterceptHeading`, captured by the install site (CAPP implied-PTAC, PTAC,
   JFAC/JLOC) *before* the clearance nulls `Targets.AssignedMagneticHeading` — reading the live target here is a
   dead check, because the approach clears it before the first tick (issue #429: `FH 260` onto `I29RY` busted at
   32.9° with both leniencies silently inert).

Checks #2 and #3 are proxies for the FAC and apply **only on an aligned straight-in**: when the magnetic FAC is more than
`AlignedFinalToleranceDeg` (10°) from the runway-number heading — an LDA/SDF/LOC-BC offset final — the effective diff is
check #1 alone, otherwise `Math.Min` would authorise a cut far outside TBL 5-9-1 against the course actually flown.

In the anticipation branch, check #3 is gated on the aircraft having actually *reached* the assigned heading
(within 5° via `onAssignedHeading`) so a mid-turn aircraft isn't waved through prematurely
(`InterceptCoursePhase.cs:146`).

**The bust-through gate is per-category.** `InterceptAngleLimits.BeyondGateAngleForCategory` returns 30°, or
45° for `AircraftCategory.Helicopter` — TBL 5-9-1's "2 miles or more" row reads "30 degrees (45 degrees for
helicopters)". If the aircraft crosses the centerline with the effective diff beyond its category gate,
`HandleBustThrough` clears the approach phases and `ActiveApproach` and the pilot reports it like any other
refusal (`PilotResponder.BuildUnable` → `RouteSoloOrRpoTransmission`: TTS "unable, passing through the localizer"
in solo mode, an amber warning "unable, passing through the localizer — I29RY." for an RPO, green when pilot
speech is shown) — never the grey Response channel. The `MaxElapsedSeconds = 180` safety timeout (flying parallel and
never crossing) drops the clearance the same way but says **"unable to intercept the localizer, request vectors"** — an
aircraft that never reached the course must not report passing through it.
Helicopters do reach this phase — PTAC/CAPP/JAPP (`ApproachCommandHandler`) and JFAC (`NavigationCommandHandler`)
all build `InterceptCoursePhase → HelicopterLandingPhase` for rotorcraft — so a hard-coded 30° here refuses a
legal 30–45° helicopter cut.

> `InterceptAngleLimits` (`src/Yaat.Sim/Phases/InterceptAngleLimits.cs`) is the single home for TBL 5-9-1, used
> by the bust-through gate, the `ApproachScore` legality verdict, and the forced-intercept glideslope bypass.
> `PatternCommandHandler.MaxCloseInFinalAngleOffDeg` deliberately does **not** route through it: it gives
> helicopters 45° at any distance, because it is an airmanship analogy for VFR pattern entry rather than the
> regulatory vectoring limit (see the pattern-entry section above).

**`ForcedIntercept` (PTACF / implied-PTAC in CAPPF).** When `ForcedIntercept` is set, `maxAlignmentDeg` is raised
to **180°**, which makes the bust-through branch unreachable: the aircraft captures the FAC at *any* angle,
overshoots laterally, and S-turns back under `FinalApproachPhase`. Don't assume the category gate is always active.

**Parallel-offset anchor.** For parallel-offset approaches (e.g. KDCA LDA-X 19, KCCR S19R) the FAC line does not
pass through the threshold. The phase measures cross-track against `ActiveApproach.FinalApproachAnchorLat/Lon` when
set, falling back to the threshold otherwise (`InterceptCoursePhase.cs:94`). `FinalApproachPhase` uses the same
anchor. Code that hard-codes the threshold breaks offset approaches.

**Speed anticipation.** Inside `SpeedAnticipationThresholdNm = 2.0` the phase decelerates to `1.3 × FAS`
(`InterceptSpeedFasMultiplier`), not FAS itself — at 250 kt the turn radius is too large and would overshoot
(`InterceptCoursePhase.cs:106`). `FinalApproachPhase` bleeds the rest to Vref closer in — at a **per-aircraft
distance**, not a fixed one (see the FAS-reduction-variety note below).

**Uncontrolled long-final schedule.** With no ATC speed, `FinalApproachPhase` runs three stages ahead of the Vref
bleed, each fired once at the kinematic trigger that lands the bleed on its reach gate
(`TickPreConfigurationStages` / `TickApproachFlapStage`): **clean → approach-flap** (`FinalApproachSpeedSchedule`:
clean ≈ Vref+70 capped 240 heavy / 220 large / 210 regional jet, approach-flap = `max(1.3·Vref, min(clean−25,
Vref+45))`, settled by a per-callsign 9 ± 1.5 nm gate for jets, 8 ± 1 nm turboprops, no stage for pistons), then
**→ 1.3·Vref** by `ConfigReachGateNm` (5 nm), then **→ Vref** by the per-aircraft reach gate. Stages are additive on
Vref like airline flap schedules, so a B744 (Vref 157) flies ~227 → 204 → 204 → 157 while an E75L (126) flies
~196 → 171 → 164 → 126 — nobody at 250 on final, nobody at 180 at 10 nm, spread by type. The `FlapSet` latch
persists in `FinalApproachPhaseDto` (seeded from `ConfigSet`/`FasSet` on older snapshots). `OnFinal` spawns and the
generator in-trail spacing ceiling (`ArrivalSpacingManager.ScheduledFinalSpeedKts`) evaluate the same schedule at
the spawn / current distance. Authority: 7110.65 §5-7-1.a.3(d) (keep aircraft clean as long as circumstances
permit), §5-7-1.c/d (approach clearance cancels assigned speed; pilots fly their own approach speeds), §5-7-3.c and
AIM 4-4-12 (210 / 170 kt jet floors), 14 CFR §91.117 (250 below 10,000).

> **FAS reduction is per-aircraft.** `FinalApproachPhase`'s two-stage decel (config `1.3·Vref`, then Vref) no
> longer settles every aircraft at the same fixed distance. When the scenario has variety enabled, each aircraft's
> reach gate is lazily assigned on first final approach (`FinalApproachPhase.EffectiveFasReachGateNm` →
> `FinalApproachSpeedVariety.ComputeReachGateNm`, a deterministic right-skewed distribution over callsign: floor
> 2.0 NM competent, median ~3.0, cap 5.0) and stored on `AircraftApproachState.FinalApproachFasReachGateNm`; both
> gates then slide outward together preserving the current offsets. This reproduces the live-network spread where
> pilots slow to Vref anywhere from tight-and-competent out to a draggy early slow-down that compresses the arrival
> stream.
>
> The enable flag is `SimScenarioState.FinalApproachSpeedVarietyEnabled` (threaded via `PhaseContext`): **off by
> default**, turned on by the server for every live session, and captured in the recording's snapshots so replays
> reproduce the same variety while pre-feature recordings (flag off) replay with the original uniform ~2.0 NM
> floor. Gating on the scenario flag — not a bare per-callsign hash — is what keeps existing recordings replaying
> byte-for-byte (replay re-simulates from the scenario JSON, so an ungated hash would retroactively alter them).
> See [flight-physics.md](flight-physics.md) and the `FinalApproachPhase` constant comments.

**Capture & intercept legality.** `Capture` records the capture distance/angle on the clearance
(`InterceptCaptureDistanceNm`/`InterceptCaptureAngleDeg`) and runs `CheckInterceptLegality` against the approach
gate (see scoring below). VFR, visual (`VIS…`), and pattern traffic skip the §5-9-1 check.

## Approach-fix navigation — `ApproachNavigationPhase`

`ApproachNavigationPhase` (`src/Yaat.Sim/Phases/Approach/ApproachNavigationPhase.cs`) flies a CIFP fix sequence
(IAF → IF → FAF) and completes at the last fix, handing off to `FinalApproachPhase`. Key behaviors:

- **Fly-by anticipation vs fly-over** (`ApproachNavigationPhase.cs:57`): a fly-by fix with a following fix uses
  `FlightPhysics.ComputeAnticipationDistanceNm` to start the turn early; inside the anticipation zone it sequences
  once the aircraft is along-track *past* the waypoint toward the next fix. A fly-over fix
  (`IsFlyOver`) sequences only on physical arrival within `FixArrivalThresholdNm = 0.5`.
- **Continuous descent** (`ApplyContinuousDescentTarget`, `ApproachNavigationPhase.cs:151`): each tick it sets
  `TargetAltitude` to the published 3°/6° glideslope altitude at the current distance from threshold, **bounded
  below** by the highest remaining `AtOrAbove`/`At`/`GlideSlopeIntercept` constraint (and the lower bound of a
  `Between`), and **bounded above** by the current altitude — the aircraft never climbs to capture the profile from
  below. The controller's `AssignedAltitude` caps it.
- Remaining fixes are appended to `NavigationRoute` with their speed restrictions so
  `FlightPhysics.UpdateSpeedPlanning` can look ahead; altitude is owned by the continuous-descent path, not the
  route.

## Holding patterns — `HoldingPatternPhase` + `HoldingEntryCalculator`

### Entry-sector classification — `HoldingEntryCalculator`

`HoldingEntryCalculator.ComputeEntry` (`src/Yaat.Sim/Phases/HoldingEntryCalculator.cs:17`) picks the AIM 5-3-8
entry (Direct / Teardrop / Parallel) from `theta = (aircraftHeading − inboundCourse) mod 360`:

| Hold direction | θ < 110° | 110° ≤ θ < 250° | θ ≥ 250° |
|---|---|---|---|
| **Right** (standard) | Direct | Teardrop | Parallel |
| **Left** (non-standard) | Parallel | Teardrop | Direct |

> **Note on the "70°" comment.** The class doc-comment calls this "the 70-degree sector rule," referring to the
> AIM teardrop sector being centered 70° off the holding side. The *code* uses **110°/250°** boundaries (the
> standard ±70°-from-the-non-holding-direction sector layout expressed as θ relative to the inbound course). Trust
> the 110/250 boundaries in the code, not the literal "70" in the comment.

### The hold state machine

`HoldingPatternPhase` (`src/Yaat.Sim/Phases/Approach/HoldingPatternPhase.cs`) runs a **seven-state** machine
(`HoldState`): `NavigatingToFix → EntryOutbound → EntryReturn → TurnToOutbound → Outbound → TurnToInbound →
Inbound`, then loops. It **never self-completes** unless `MaxCircuits` is set (1 for hold-in-lieu of procedure
turn); most RPO commands exit via `ClearsPhase`, while CM/DM/Speed/Mach are allowed without leaving the hold.

A CIFP hold leg's (HA/HF/HM) course field is the **inbound** holding course. The hold-in-lieu (`BuildHoldInLieuPhase`) and the missed-approach hold (`ExtractMissedApproachHold`) convert it to true with the published-course declination above, taking the navaid of the preceding leg that ends at the same fix when the hold leg names none: KCCR S19R's `HM REJOY 223.6` → 240.6°T (CCR E017), KACV R01's `HF SEGVE 012.7` → 030°T (no navaid, KACV E017), KMOD I28R's `HF ZELAT 288.1` → 304°T (IMOD, KMOD E016). `InboundCourse` is an int, rounded.

- **Leg timing** is minute-based (`IsMinuteBased`, `LegLength × 60 s`) or distance-based
  (`dist ≥ LegLength`) (`HoldingPatternPhase.cs:155`).
- **Triple-drift outbound wind correction** (`ComputeOutboundHeading`, `HoldingPatternPhase.cs:339`): per AIM
  5-3-8(j)(8)(c), the outbound heading subtracts **3× the inbound wind-correction angle** so the inbound track
  stays on course. Naive "fly the reciprocal" is wrong and the tests catch it.
- **Predictive outbound timer** (`ComputeOutboundSeconds`, `HoldingPatternPhase.cs:290`): for minute-based holds,
  the outbound time is sized so the resulting inbound *ground distance* matches the target inbound duration at the
  current inbound groundspeed (a headwind inbound is a tailwind outbound). Clamped to
  `[MinOutboundSeconds 20, MaxOutboundSeconds 300]`. Distance-based holds are unchanged.
- **Entry geometry**: teardrop offset is `TeardropOffsetDeg = 30°` from the outbound heading
  (sign flips with hold direction, `HoldingPatternPhase.cs:204`); parallel flies the outbound heading and turns
  back toward the fix on `EntryReturn`. Decelerates to `AircraftPerformance.HoldingSpeed` on arrival.

## Procedure turns — `ProcedureTurnPhase`

`ProcedureTurnPhase` (`src/Yaat.Sim/Phases/Approach/ProcedureTurnPhase.cs`) flies an AIM 5-4-9 course reversal
anchored at a published fix, built from a CIFP PI leg in `ApproachCommandHandler`. **Six-state** machine
(`PtState`): `NavigateToFix → Outbound → TurnToPtOutbound → PtOutbound → TurnToInbound → InterceptInbound`.

- **Inbound course** (`ApproachCommandHandler.ResolveProcedureTurnInboundCourse`): the course of the leg into the PT fix — first a course leg after the PI leg in its own transition (KACK S24: `CF ACK 239.6`), then the common legs, FAF-role first (KCCR S19R: `CF CCR 190.6`) — never the final approach course. The final course is the fallback only when no such leg carries a course (debug-logged).
- **Magnetic to true**: every published course (PT inbound, the PI leg's PT heading, hold legs, the final approach course in `FinalApproachCourseExtractor`) converts with `NavigationDatabase.GetPublishedCourseDeclination(navaid, airport, fix)`: the recommended navaid's station declination (CIFP navaid records, sections D and PN, cols 75–79), then the airport's variation of record, then the modelled declination — never the aircraft's live `Declination` (AIM 1-1-17b.5(j)). KCCR S19R via COLLI: CCR E017 → inbound 207.6°T, PT heading 072.6°T, final 188.7°T. A localizer is not in the navaid table, so a localizer-referenced course takes the airport's variation, which matches the localizer record at KMOD and KCCR.
- **45°-offset leg**: after crossing the fix the aircraft flies the radial outbound (`InboundCourse + 180°`), then
  turns to the published `PtOutboundCourseDeg` (the 45° leg) once established and clear of the fix
  (`MinOutboundSeparationNm = 1.0`). A PI leg with no course falls back to 45° off the outbound course, away from the turn back (warning logged).
- **Turn back**: a true 180° to `PtOutboundCourseDeg + 180°`, complete within 5°, which is a 45° intercept of the inbound course from the maneuvering side.
- **Distance cap with reserve** (`ProcedureTurnPhase.cs:144`/198): the 180° turn back to inbound begins when
  `distFromFix ≥ MaxOutboundDistanceNm − TurnRadiusReserveNm`, where `TurnRadiusReserveNm = 2.0`. Turning back
  early by the reserve keeps the **180° turn radius itself** inside protected airspace (AIM 5-4-9.a.3). The cap is
  checked on both the radial-outbound and PT-outbound legs.
- **200 KIAS clamp** (`ClampPtSpeed`, `ProcedureTurnPhase.cs:293`): `MaxPtIasKts = 200` is applied via
  `ControlTargets.SpeedCeiling` for the whole phase (AIM 5-4-9.a.3).
- **Inbound intercept** (`TickInterceptInbound`): the aircraft steers onto the inbound course line through the fix with `CourseLineSteering.HeadingToward` (`src/Yaat.Sim/Phases/CourseLineSteering.cs`) — the course corrected 25° per nm of cross-track error, capped at a 45° cut — so it converges on the line from either side instead of homing on the fix.
- **Established gate** (`TickInterceptInbound`): the phase hands off once the aircraft is heading-aligned *and* either within 5° of the inbound course as seen from the fix (a VOR's half-scale deflection) or within 0.3 nm cross-track of it — the 0.3 nm keeps the gate from shrinking to nothing near the fix.
- **After the turn** the approach fixes are the procedure's common legs (`BuildApproachFixes`), each with its own restriction and role, not the transition's copy of the anchor: KCCR S19R flies HUKVI (≥1,500) and the FAF CCR (≥1,100) instead of the COLLI transition's CCR at ≥4,000. `ApproachNavigationPhase` receives the turn's `InboundJoin` (`PostTurnJoin`, snapshotted) and skips the leading fixes that lie farther before the anchor, along the inbound course, than the aircraft (FAWNE when the turn hands off inside it). When the common legs lack the PT fix (KCHS D21) the anchor is not added back.
- The minimum altitude (`MinAltitudeFt`, from the PI leg's `AtOrAbove`) is held throughout, and the PT outbound
  leg continues until both the timer (`DefaultPtOutboundSeconds = 60`) expires *and* the altitude is met.

## Glideslope geometry — `GlideSlopeGeometry`

`GlideSlopeGeometry` (`src/Yaat.Sim/Phases/GlideSlopeGeometry.cs`) is the shared descent-profile helper used by
approach navigation, downwind/base descent, and final approach. Constants and helpers:

- `StandardAngleDeg = 3.0`, `HelicopterAngleDeg = 6.0`; `AngleForCategory(category)` returns 6° for helicopters,
  else 3°.
- `FeetPerNm(angle)` = `tan(angle) · 6076.12` (≈ 318 ft/nm for 3°).
- `AltitudeAtDistance(distNm, thresholdElevation, crossingHeightFt, angle)` =
  `thresholdElevation + crossingHeightFt + tan(angle) · distNm · 6076.12`. The `(distNm, thresholdElevation, category)`
  overload is what production calls — it pairs `AngleForCategory` with `CategoryPerformance.WheelCrossingHeightFt` so
  the two can't be mismatched. Pass a **wheel** crossing height, not a published TCH (AIM 1-1-9.d.6/.7); see
  [landing-and-runway-exit.md](landing-and-runway-exit.md#touchdown-aiming-point).
- `RequiredDescentRate(groundSpeedKts, angle)` = `groundSpeedKts · tan(angle) · 101.269` (≈ GS × 5.3 fpm for 3°).

## Visual following — `AirborneFollowHelper` + `VfrFollowPhase`

`AirborneFollowHelper` (`src/Yaat.Sim/Phases/AirborneFollowHelper.cs`) provides speed/timing adjustments for any
aircraft with `Approach.FollowingCallsign` set. Pattern phases (`DownwindPhase`, `BasePhase`, `PatternEntryPhase`)
and `VfrFollowPhase` call into it each tick.

**Desired spacing** scales with the leader's category. Pattern-tight (`AirborneFollowHelper.DesiredDistanceForLeader`): Jet 3.0 / Turboprop 1.5 / Piston/Heli 1.0 nm. Free-flight (wider, used before the follower is established on a leg, `AirborneFollowHelper.FreeFlightDistanceForLeader`): Jet 3.5 / TP 2.0 / Piston/Heli 1.5 nm. The jet 3.0 nm matches the FAA 7110.65 §5-5-4 same-runway radar minimum.

**Speed adjustment** (`ComputeAdjustedSpeedWithDesired`): the correction is
`(distance − desired) × SpeedGainPerNm (25 kt/nm)`, clamped to `±maxSpeedAdjustKts`. Pattern/entry/free-flight use
`MaxSpeedAdjustKts = 20`; final approach uses the tighter `MaxSpeedAdjustFinalKts = 10` so the follower can't blow
through the unstabilized-go-around gate (IAS > 1.3·Vref). The pattern-leg
phases gate this block on `Approach.FollowingCallsign` (not on `TargetSpeed` — physics snaps `TargetSpeed` to null
once the leg speed is reached, which would otherwise silently stop spacing for a settled follower) and **cap the
result at the leg baseline** (`Math.Min(adjusted, baseline)`): spacing only ever *slows* a follower below its leg
speed, never accelerates it above to chase a far lead — a too-far lead is handled laterally (extend / hold base turn).

**Pre-final spacing on a kept approach** (`AirborneFollowHelper.ApplyPreFinalSpacing`): an approach follower spaces by speed before `FinalApproachPhase` sets its FAS, in `InterceptCoursePhase` (once its approach speed is set, never at ≥ 2 NM cross-track), `ApproachNavigationPhase` (not on a published missed) and `FinalApproachPhase` before `_fasSet` (every active follow, pattern and visual followers included). The baseline is the lead's IAS clamped to [floor, ceiling], so the follower settles at the desired distance instead of closing on a slower lead; the result is `max(min(adjusted, ceiling), floor)` and nothing is written when floor ≥ ceiling. Floor: the approach speed plus the wind additive (bare FAS with no assigned runway). Ceiling: 1.3 × the bare approach speed on the intercept; on the approach's fixes a latch of `TargetSpeed ?? IAS` at the first spacing tick, replaced by each fix speed and cleared under an explicit speed (re-latched after it); on final before FAS the latest speed the phase's schedule wrote, else the entry latch, kept across an S-turn resume. Both latches are snapshotted (`SpacingCeilingKts`, optional, no schema bump). Gates: an active follow, no explicit ATC speed, not `LateralInterceptOnly`. A lead on the ground restores the ceiling (`CheckLeadLifecycleRestoringSpacing`); inside the 60 s stabilization window nothing is written; a follow that ends any other way keeps the spaced speed. While following, `FlightPhysics.UpdateSpeedPlanning` never accelerates toward an at-or-below fix limit. The minimum-speed "unable" cancel on a kept approach clears only the follow; an IFR follower on a kept approach (before final, or in `FinalApproachPhase` with an `ActiveApproach`) also gets `{Callsign} visual separation terminated — radar separation required`. Tests: `FollowPreFinalSpacingTests`.

**At-min-speed: extend, don't cut in.** When the follower is at min speed and inside half the desired distance, it
cannot open the gap by slowing any further. If the lead is **pattern-flow-ahead** (`IsLeadPatternFlowAhead` — same
runway, strictly later leg or a same leg the lead is *holding out*) the follower still has a *lateral* option, so the helper returns
`minSpeed` and **does not cancel**: `DownwindPhase` then holds the base turn (`ShouldHoldForLeadSequencing`) and
extends until the projected threshold ETAs release it (see *Downwind sequencing by projection* below). Only
when there is no lateral option (free-flight follow, or a same/earlier-leg lead) is the follow cancelled with an
"unable to maintain separation" warning. Cancelling on a flow-ahead lead used to clear `FollowingCallsign`, drop the
hold, and turn the follower base *in front of* a still-airborne straight-in — a §3-10-3.a.1 same-runway separation
bust (a light twin, SRS Cat II, has **no** reduced-distance provision behind a Cat III jet: the jet must be on the
ground and clear before the follower crosses the threshold) and an AIM §4-3-4.b.4 cut-in.
Regression: `N342TFollowStraightInDownwindTests`.

**Structural-overtake break-off + go-around** (`ShouldBreakOffFollowForSpacing`): a follower whose own Vref plus the gust additive exceeds the lead's indicated airspeed by more than `StructuralOvertakeMarginKts = 10` (`IsStructuralOvertake`; IAS against IAS, since both fly the same final into the same wind) can never open the gap by slowing — speed control alone is futile (e.g. a C210 told to follow a 56-kt C152). When that follower, on `BasePhase` or `FinalApproachPhase`, closes inside `FollowBreakOffGapNm = 0.8` nm of the still-airborne lead while the pair is still converging (`IsClosing`, range-rate < 0), it breaks off the follow and goes around — clearing the follow state and triggering a go-around with an "unable to maintain separation" transmission — rather than overflying the lead it was told to follow (AIM 4-3-3 NOTE 1; 0.8 nm sits above the same-runway 3,000 ft ≈ 0.5 nm Cat I minimum of 7110.65 §3-10-3). The check runs *before* the speed block so it pre-empts the at-min-speed cancel above, which would only clear the follow without going around.

**Spacing on base — `BaseFollowSpacing`** (`src/Yaat.Sim/Phases/Pattern/BaseFollowSpacing.cs`). A follower already on `BasePhase` is judged every tick, after the structural-overtake go-around and before the follow-speed step, when its lead is airborne, landing the same runway (`IsSameRunway`: same end at the same airport), and either on final, in its landing (the runway-occupancy term only), or on base ahead of it (less `RemainingPatternPathNm` left to fly). "On final" is by phase (`FinalApproachPhase`) or by geometry, whatever phase flies it (`IsOnFinalByGeometry`: on the approach side of the threshold, within `OnFinalMaxCrossTrackNm = 0.5` of the extended centerline, tracking within `OnFinalMaxTrackOffDeg = 30` of the runway heading, not going around), so a lead on an ILS in `ApproachNavigationPhase` or `InterceptCoursePhase` counts. The projection: the follower flies its remaining base to the final-turn point and the quarter-circle turn (`BasePhase.TurnRadiusNm`) at its ground speed; the lead flies its remaining path to the threshold (`LeadRemainingPathNm`: the pattern path on base, the distance out on final) at `ProjectedLeadSpeedKts` (the slower of its ground speed and approach speed, floored at 40 kt — the same projection the downwind hold uses). The spacing gap is how far the lead is then ahead along the final; the requirement is `PatternSpacingNm` (the category pattern spacing, raised to the on-approach wake minimum); the runway-occupancy check asks that the follower cross the threshold no sooner than `RunwayClearanceSeconds` after the lead. The decision:

- **Keep** while the spacing gap is within `KeepMarginNm = 0.3` of the requirement and the occupancy check holds (the margin applies to the spacing gap only, never to runway occupancy). The speed spacing does the rest.
- **Widen** when the follower is still behind but short: it flies `WidenOffBaseDeg = 30°` off the base heading away from the field (`PatternOutsideWidenSide`, never toward a parallel) until the gap is met or it reaches the widen floor, `WidenFloorTurnRadii = 1.5` turn radii from the extended centerline, and then flies the base heading again. The floor has no 1 nm minimum, so a default piston circuit (about 0.75 nm wide) still has room to widen a small shortfall instead of turning out. The widen is chosen only when it can build the gap: `WidenGapGainNm` gives, from cross-track xt down to the floor, a final longer by (xt − floor)·tan 30° plus a leg longer by (xt − floor)·(1/cos 30° − 1), the leg term scaled by lead speed / follower speed (it delays the follower, and the lead gains that time at its own speed); and its path must not meet a parallel runway's final (`WidenMeetsParallelFinal`). Each tick of a widen re-runs the break-off test, so a widen that can no longer build the gap breaks off at once. While widening the follower holds its present altitude, since the glidepath it was descending to belongs to the shorter final; when the widen ends the base re-plans its descent to the 3° glidepath at the new rollout. No transmission: widening a base is a minor pattern adjustment (AIM §4-3-5). `BasePhase.FollowWidenActive` is snapshotted on `BasePhaseDto` (nullable, false when absent).
- **Break off** when the follower would roll out level with or ahead of the lead, is already inside the floor, a widen would meet a parallel's final, or no widen can build the gap. The follower installs `VfrFollowPhase` with the pattern return to its own circuit (`CommandDispatcher.InstallVfrFollowPhase`, which returns the installed phase and keeps the landing clearance) and asks it to open with the turn-out to the downwind heading (`RequestTurnOut`; see *Turn-out* under `VfrFollowPhase` below), which makes the one call. The break-off itself says nothing.
- **Go around** instead of breaking off when the break-off comes late: less than `LateBreakOffMarginNm = 0.4` nm of base left before the final-turn point (cross-track minus turn radius) with the lead not yet abeam leaves no room to turn out.
- A **structural overtake** (`IsStructuralOvertake`: the follower's Vref plus the gust additive above the lead's IAS by more than `StructuralOvertakeMarginKts`) never breaks off here; the structural go-around above keeps it, as it runs first. It may still widen.

**The pattern-leg-index ordering** (`AirborneFollowHelper.PatternLegIndex`) is hard-coded: `PatternEntryPhase = 0`, `Upwind = 1`, `Crosswind = 2`, `Downwind = 3`, `Base = 4`, `FinalApproach = 5`, `Landing/TouchAndGo = 6`. A lead on an instrument approach (`InterceptCoursePhase`, `ApproachNavigationPhase`) or on final by geometry counts as 5; a go-around that re-enters the pattern and a same-runway closed-traffic takeoff climb count as 1; other non-pattern phases return null.

**Sequence order.** For a same-runway pair, "is the lead ahead" has one answer, used per tick and by the FOLLOW refusal (`IsLeadAheadAcrossLegs` for pairs on different legs):
- Same leg: plain position, no tolerance — on upwind, crosswind or downwind progress along the leg, on the other legs remaining path to the threshold (`SharedLegOrderNm`), since an aircraft extended past its turn point has more path left the farther out it flies.
- Different legs, follower on base, final, final by geometry or an instrument approach (leg 4 or 5): remaining path to the threshold (`SequenceRemainingPathNm`; an aircraft on an approach's fixes by the legs it still has to fly, one intercepting by straight-line distance, one on final by along-final distance), the lead ahead when its path is no longer than the follower's. Base, final and the approach converge on one final, so an extension cannot flip this order; a 6 NM straight-in follows a Cessna on close base, and a close base is not ahead of a 10 NM final. A lead on a pattern entry stays behind a base follower; for a leg-5 follower (final, final by geometry, an instrument approach) it is ordered by its entry path below.
- Different legs otherwise: leg order, the later leg ahead however far it is extended. Remaining path across the outbound legs was tried and rejected: an extension adds path, so the order flipped the moment the lead turned onto its next leg.
Distance, not time: a closer, slower lead is ahead (7110.65 §3-8-1, AIM §4-3-4.d).

`IsLeadPatternFlowAhead` (lead strictly later leg — **plus the same-leg case where the lead is holding the leg out**) and `IsLeadPatternFlowBehind` (the lead behind in the sequence order above) use this index, gated on both aircraft being on the **same runway**:

- **`IsLeadPatternFlowBehind`** ⇒ the spacing helper returns the baseline (don't slow down for a lead that hasn't
  reached the follower's leg yet — pulling the follower to Vref produces multi-minute downwind extensions).
- **`IsLeadPatternFlowAhead`** ⇒ the follower may extend its leg to sequence, and the at-min-speed cancel in the
  speed loop is suppressed (it holds `minSpeed` and extends instead of cutting in — see **At-min-speed** above).
  - **Held-leg exception (`IsLeadHoldingSharedLeg`):** a lead on the **same leg** that is *holding it out* also counts
    as flow-ahead — it has deferred its progression, so it stays ahead in the landing sequence despite sharing the
    follower's leg index. "Holding out" is any deferral, recognized two ways:
    - `IsExtended` set by `EXT` on Downwind, Crosswind, or Upwind (`IsExtendedPatternLeg`), and
    - for the **downwind**, the lead still on the leg past its OWN base-turn point (`DownwindPhase.HasReachedBaseTurnPoint`,
      evaluated against the lead's own geometry). This catches a lead holding the downwind for a reason other than a live
      `EXT`: its own follow-hold behind other traffic (a follow chain), a proximity hold, or an `EXT` that was cleared
      when the lead was itself told to follow (`CommandDispatcher.TryAirborneFollow` clears `IsExtended` on a follow).

    Without this, a follower on the same downwind turned base at its fixed point and rolled out on final ahead of the
    aircraft it was told to follow (AIM §4-3-5, broken sequence). Generic same-leg pairs — a lead merely progressing
    ahead, *before* its base turn — are still *not* flow-ahead: it turns base at its own point first, so the follower
    keeps normal spacing rather than extending behind it.

**The lead lifecycle** (`CheckLeadLifecycle`) cancels a follow when:

1. the lead is no longer in the world (lookup returns null),
2. the lead has gone `IsOnGround`, or
3. the follower **loses visual contact** with the lead.

**On an involuntary cancel the follower holds its present vector — it does not turn on its own**
(`CancelFollowHoldingLeg`). The pilot's only outstanding instruction was to follow, so once following
ends it is not authorized to turn; if it is on an Upwind/Crosswind/Downwind leg it sets that leg's
`IsExtended`, continuing straight and levelling at the glideslope-intercept floor until the controller
turns it (`TB`) or re-sequences it (AIM §5-5-12.a.2 — advise ATC and maintain, don't maneuver on your
own). This covers the lost-visual (case 3), lead-despawned (case 1), and the at-min-speed
"unable to maintain separation" cancels. **The landing exception (case 2) is the one that does *not*
hold:** when the lead reaches the ground the follower is number one, so it keeps bare `ClearFollowState`
and continues its own approach (turns base, lands). Base/Final are committed to the approach and
free-flight legs have nothing to hold, so those continue regardless. A controller command that
supersedes the follow (vector / new approach) and a Base/Final go-around break-off also use bare
`ClearFollowState` — the new instruction or the go-around governs.

**A growing gap never cancels a follow.** A lead that merely outpaces the follower is *increasing* separation and
removing the overtake risk; the sequence is still valid. That is a controller efficiency concern to re-sequence, not
a follower safety trigger, and "unable to catch up" is not a transmission a real pilot makes. The only
self-generated cancel the AIM authorizes is loss of visual contact (AIM §5-5-12.a.2 / §4-4-14 NOTE — the pilot
reports when it *cannot maintain visual contact* or cannot accept the responsibility). An earlier
monotonic-gap-growth watchdog (30 s grace / 0.1 nm tolerance) cancelled good follows — e.g. a VFR transition holding
a clean 1.1 nm trail behind a faster arrival — and is gone, along with its `FollowBestGapNm` /
`FollowRunawaySeconds` state and the `BuildUnableToCatchUp` transmission.

**Loss of visual is maintained-contact, not re-acquisition** (`VisualDetection.TryMaintainTrafficContact`, wrapped by
`VisualAcquisition`). It checks **only** the weather obstruction — a BKN/OVC layer lying between the two aircraft —
and skips *every* geometric check: acquisition range, forward hemisphere, and bank occlusion. This mirrors
`TryMaintainAirportContact` and its rationale: those geometric checks model the problem of *finding* unknown traffic
in a wide sky, and FOLLOW already gates on the pilot having called the traffic in sight (the RTIS gate). Re-applying
them every tick produces false "lost sight" reports as the follower banks through its own pattern turns, as the lead
slides aft of the 3/9 line, or while the follower lag-pursues a lead that is still opening. On loss of visual the
pilot transmits `BuildLostSightOfTraffic` ("lost sight of the traffic").

**Downwind sequencing by projection.** `FOLLOW` is a sequencing instruction — 7110.65 §3-8-1 lists it beside
`EXTEND DOWNWIND` as a separate instruction; the pilot who accepts it owns the in-trail spacing (AIM §4-4-14.b,
§5-5-12.a), adjusts by extending rather than orbiting (AIM §4-3-5), and the traffic stops being a factor once it is in
the landing phase (AIM §4-4-14.a.2 NOTE), not once it has cleared the runway. The follower turns base when the traffic
has passed and the spacing will work out — the pilot-side mirror of §3-10-6 *anticipating separation*.
`ShouldHoldForLeadSequencing(ctx, wp)` therefore holds the base turn behind a pattern-flow-ahead lead until **all** of:

1. the lead is aft of the follower's 3-9 line (`IsFlowAheadLeadForwardOfWingline`: body-frame relative bearing beyond
   ±90° with a few degrees of margin, so the base leg is flown behind it — 14 CFR §91.113(g), restated for non-towered
   fields in AIM §4-3-4.d) — judged in the follower's own frame, not on the downwind axis, so a drifted or extended
   downwind does not distort it. `DownwindPhase` applies this gate unconditionally; a short approach (`SA`) waives only
   the two projection gates below;
2. projecting both aircraft along `RemainingPatternPathNm` at their present speeds, the follower crosses the threshold
   at least `RunwayClearanceSeconds(leadCategory)` (runway occupancy, threshold to clear: 60 s jet / 45 turboprop /
   40 piston-heli — simulation allowances anchored to §5-5-4.j.2's 50 s average, not a published per-category figure)
   after the lead crosses it, so the runway is clear when the follower arrives (§3-10-3; when either aircraft is
   Category III only "clear of the runway" qualifies, §3-10-3.a.1);
3. if the lead would still be airborne on final when the follower rolls out, the follower rolls out at least the
   category desired distance behind it (the in-trail spacing, applied to the *projected* geometry).

Every projection term errs toward holding: the follower is projected at the faster of its present and approach speeds
along a path shortened by the two corners it will cut turning base and final (`PatternCornerCutNm`), the lead at the
slower of its present and approach speeds (floored at 40 kt). The old rule compared *instantaneous* along-track
positions against the desired distance and ignored the base leg the follower still had to fly — a C172 told to follow
an LJ60 on a 1.3 nm final therefore flew a 3 nm final and turned base at the jet's touchdown
(`FollowStraightInJetBaseTurnTests`). `ShouldExtendDownwind` (proximity, `desired × 0.6` straight-line) now applies only
to a lead that is *not* flow-ahead — a lead on final is on a reciprocal track, so its straight-line gap is diverging
geometry, not trail spacing. `DownwindPhase` bounds the hold **spatially** with `MaxFollowExtensionNm = 4.0`; the
**temporal** bound is the lead lifecycle (the lead lands, despawns, or is lost from sight). The two caps are
complementary, not redundant.

**Upwind/crosswind sequencing — remaining pattern path.** The downwind along-track hold does not transfer to the
`UpwindPhase`/`CrosswindPhase` legs: the upwind leg's along-track runs *opposite* the downwind sequence axis (its
heading is the runway heading, the reciprocal of downwind), so "further along upwind" means a *longer* path to
landing, not a shorter one — a follower that is behind the lead on upwind is geometrically committed to a *shorter*
downwind and would roll out on final *ahead* of the traffic it was told to follow. To sequence correctly on every
leg, `RemainingPatternPathNm` (`AirborneFollowHelper`) computes the remaining circuit distance to the threshold
(upwind → crosswind → downwind → base → final); it is monotone toward landing and *increases* when a leg is extended
(a longer upwind lengthens the downwind; a wider crosswind lengthens the base; a longer downwind lengthens the
final — the downwind term uses the aircraft's **actual** perpendicular offset so a widened pattern counts its longer
base). A lead still joining the pattern (leg 0) is measured along its entry route, straight lines with no corner-cut credit, then the leg formula at the join: a `PatternEntryPhase` through its lead-in while `PTN-LEADIN` is still in its route, then its entry point, the joined leg read from the phase after the entry (`EntryJoinedLeg`; the entry's `Kind` only when none follows); a `MidfieldCrossingPhase` through the midfield point, then the teardrop fixes and `DownwindAbeam` when a `TeardropReentryPhase` is queued next; a `TeardropReentryPhase` through the fixes left in its route. A straight-in entrant has no pattern waypoints and is measured from the runway (`FinalRemainingNm`, the larger of the along-final and straight-line distances to the threshold). `ShouldHoldLegForRemainingPathSequencing` holds the current leg while `remaining(follower) < remaining(lead)
+ desired`, so the follower extends its upwind/crosswind leg (chasing) until it is a full `desired` of remaining
path behind — converging without ever having to overtake the lead on the shared leg, because the lead is
simultaneously shrinking its own remaining. Gated (like every other follow path) by `IsLeadPatternFlowBehind` so a
follower never extends a leg to fall in behind traffic that is actually behind it.

**A follower never self-turns to break the hold.** Once told to follow, an aircraft does not turn off its leg on
its own — it keeps flying the current leg until it is genuinely sequenced behind (the hold clears and it turns
normally) or the controller issues a turn. The shared `MaxFollowExtensionNm = 4.0` is therefore an *advisory*
threshold, not a forced turn: past it the pilot transmits a **one-shot** "extending {upwind/crosswind/downwind}
behind the traffic, unable to turn — request instructions" (`PilotResponder.BuildFollowExtendingUnableToTurn`,
latched per phase and snapshot-serialized as `FollowExtensionWarningIssued`) and continues on the leg. This applies
on `DownwindPhase` too — the old cap-forced base turn is replaced by the same continue-and-advise behavior. AIM
4-3-2.c.2 (the upwind leg is an explicit separation/sequencing leg) and 5-5-12.a.1 / 4-3-5 (the follower
maneuvers as necessary and advises ATC — it does not fly an *unrequested* turn on its own).

**Pattern-aware FOLLOW install** (`CommandDispatcher.TryAirborneFollow` — issue #352). When the follower is *not*
already on a pattern leg for the lead's runway (no phase at all, a non-pattern phase, or a cross-runway pattern) and
the lead is **established toward a known runway** (`IsEstablishedTowardRunway`: entry / crossing / teardrop / pattern
leg / final / landing / touch-and-go with `AssignedRunway` set), FOLLOW is treated as a runway-sequencing instruction:
the dispatcher builds the follower a downwind entry to the **lead's** runway (the `TryEnterPattern` machinery,
present-position join included) and lets the `DownwindPhase`/`AirborneFollowHelper` holds do the spacing. Free pursuit
cannot express "continue the downwind, extend, turn base behind" — `ComputeFreePursuitHeading` immediately parallels
the lead's track, which from a downwind-shaped geometry is a ~180° about-face onto a parallel offset track (the #352
report). The join side (`ChooseFollowJoinDirection`) is the **runway's established circuit**: the lead's
`TrafficDirection` first, then the runway's natural direction (parallel-runway inference — 28R with 28L present flies
right traffic), then the follower's own side, then FAA-default left (AIM §4-3-3). The circuit must win over the
follower's momentary side — joining on whatever side the follower occupies can build opposing circuits for one runway
and, on close parallels, descends a base leg across the neighboring runway's final approach course (AIM §4-3-3
FIG 4-3-3 note 7). A follower on the wrong side for the chosen circuit takes the published midfield-crossing entry at
TPA (AIM §4-3-3.1.b) via `TryEnterPattern`'s wrong-side path. For a lead landing a *different* runway this models an
implied runway change — 7110.65 §3-8-1's codified phraseology for traffic on another runway is a traffic advisory, not
FOLLOW; the re-sequence is a deliberate trainer affordance.

Two paths still install `VfrFollowPhase` free pursuit: a genuinely **free-flight lead** (no runway to sequence onto),
and a lead on final with the follower already positioned in the **final-approach corridor**
(`CanJoinLeadFinalDirectly`: ≥ `FollowDirectFinalJoinMinAlongFinalNm = 1.5` out along the final course, cross-track
≤ `FollowDirectFinalJoinMaxCrossTrackNm = 2.5` and ≤ the along-final distance [a 45° cone], and no parallel runway's
final in between). There the direct in-trail join is the right shape — a full circuit would loop an aircraft that is
effectively number two on the approach. The corridor test is **position-only**: the instantaneous track is unreliable
(the follower may be mid-turn when the FOLLOW arrives). Same-runway pattern-leg FOLLOW keeps the cheap in-place
retarget once the sequence refusal below has passed.

**Sequence refusals** (`RouteFollow`: `DepartingLeadRefusal`, `MissedApproachFollowerRefusal`, `LeadGoingAroundRefusal`, then the runwayless and cross-runway refusals, then `FollowSequenceRefusal`). A refusal changes nothing: phase list, landing clearance, `FollowingCallsign` and an extended downwind are all kept.
- A follower on an instrument approach told to follow a lead whose assigned runway is another runway at the same airport (`ApproachCrossRunwayRefusal`, after the off-pattern guards and before the base/final cross-runway refusal) is refused "Unable, on approach for runway {rwy}, {T} is landing runway {rwy2}, request vectors" (7110.65 §7-4-3.c.2). An IFR follower (`HasFlightPlan` and not VFR) is refused anywhere on the approach; a VFR follower, or one with no flight plan, only when established on final (along-final ≥ 0, within `AirborneFollowHelper.OnFinalMaxCrossTrackNm` of the centreline) inside `ApproachGateDatabase.InsideFafLimitNm` — the published FAF distance to the landing threshold or 5 NM, whichever is closer (§5-7-1.b.4); otherwise it falls through to the re-sequence onto the lead's runway. A follower or lead with no assigned runway is not refused.
- A departing lead (airborne in takeoff, initial climb or a departure procedure, not a closed-traffic climb) is refused from any follower: "Unable, {T} is departing, request vectors". FOLLOW is arrival sequencing (7110.65 §3-8-1, §7-6-7.a).
- A follower on upwind, crosswind, downwind, base, final or an instrument approach refuses a same-runway lead that is behind in the sequence order above, or that has no sequence leg (a missed approach, a hold): "Unable, on {upwind|crosswind|downwind|base|final|approach} for runway {rwy}, {T} is not ahead of us, request vectors" (AIM §5-5-12.a.2). An approach follower that passes every refusal keeps its approach (below).
- A lead still on a pattern entry is accepted from upwind, crosswind or downwind (it may join downwind ahead, and controllers issue exactly this) and refused from base or final, where getting behind a 45° entrant would take a 360 (AIM §4-3-5). From an instrument approach it is measured, not refused: accepted when its entry path is no longer than the follower's remaining path, otherwise refused with the approach text above; per tick a leg-5 follower keeps ordering it by that path. A follower on a pattern entry is never refused.

**FOLLOW from a go-around or a closed-traffic climb keeps the climb** (`GoAroundPhase` and the airborne closed-traffic `TakeoffPhase` answer FOLLOW `Allowed`; `CommandDispatcher.FollowRefusalOrRoute`). A re-entering go-around and a same-runway closed-traffic climb are leg 1 (`PatternLegIndex`, AIM §4-3-2.c.2) as follower and lead; an accepted lead sets only `Approach.FollowingCallsign`, keeping the phase instance, `TargetAltitude`, the 400 ft `NoTurnAgl` heading gate, `Targets` and `NextLandingFullStop`, so a refusal leaves the chain untouched in the dry run and the real dispatch alike. A lead behind is refused "Unable, on the go-around for runway {rwy}, {T} is not ahead of us, request vectors" or "Unable, on upwind for runway {rwy}, …" from the climb; an entry lead is accepted, flow-behind per tick until it joins. Two aircraft with no circuit waypoints (two go-arounds, a go-around and a closed climb) order by along-track distance from the threshold on the follower's runway heading (`SharedLegOrderNm`), at command time and per tick alike. Refusal order: departing lead, then `MissedApproachFollowerRefusal` (an IFR follower on its own missed — a non-reentering go-around or the published missed, `IsOnOwnMissedApproach` — "Unable, on the missed approach, request vectors", 7110.65 §4-8-9.a, §4-8-11.e.1), then `LeadGoingAroundRefusal` (a lead in a non-reentering go-around or on its missed, from any follower: "Unable, {T} is going around, request vectors", AIM §4-4-14.a.2 NOTE), then the runwayless and cross-runway refusals. A VFR follower in a non-reentering go-around whose lead flies a circuit on its runway is converted in place to a pattern climb-out on the lead's side (`GoAroundPhase.RetargetForPatternClimbOut` via `PatternCommandHandler.TryChangePatternDirection`), dropping its landing clearance (§4-8-11.e.2, §3-10-5), and is then leg 1; with no such circuit it falls back to the chain-clearing install. A runwayless lead from a climb gets the ±60° cone, plus the downwind box when the follower's side is known (persistent MLT/MRT, else its traffic direction), answering the same before and after the climb hands to `UpwindPhase`; a runwayless lead queued for another runway re-sequences through `FollowOntoQueuedRunway`, as from the upwind. An accepted runwayless lead keeps the climb and arms a **pending pursuit** on it (`IPendingPursuitClimb.PursuesRunwaylessLeadAfterClimb`, snapshotted, disarmed by `ClearFollowState` and by a same-runway re-FOLLOW); when the climb hands over, `PhaseRunner` starts the free pursuit with the circuit's pattern return (`VfrFollowPhase.BuildFollowPatternReturn`, then `InstallVfrFollowPhase`); a turn crosswind armed during the takeoff roll (`TurnCrosswindArmed`) is dropped there, since FOLLOW, the later instruction, supersedes it (7110.65 §2-1-5). A pursuit that starts on the departure leg carries a **climb-out gate** (`VfrFollowPhase.ClimbOutGate`, `FollowClimbOutGate`): from the climb hand-over, from FOLLOW of a runwayless lead on the upwind, and from `InstallFollow` when the follower is on its upwind, an airborne closed-traffic climb or a go-around (with no circuit, the gate is the runway's own departure end, true heading and `ResolvePatternAltitudeFt − 300`). While it holds, the pursuit flies the upwind heading and the upwind's speed schedule (the downwind baseline unless a speed was assigned, slowed for a lead close ahead) and leaves the altitude to the controller; it clears past the departure end at or above TPA − 300, or at a lower assigned altitude once reached (`UpwindPhase.PastDepartureEndAtTurnAltitude`, the upwind's own crosswind-turn rule, AIM §4-3-2.c.1, FIG 4-3-2 keys 4–5). The turn point is past the departure end, as the figures' key 5 puts it, not AIM 4-3-2.c.1's "at least 1/2 mile beyond the departure end" for the departure leg: the figure is the operational turn rule, and the altitude half usually binds later. Both climbs run `CheckLeadLifecycle` while following, clearing only the follow; an IFR follower that loses its lead also gets "{cs} visual separation terminated — radar separation required". A closed-traffic climb onto a close parallel of its pattern runway (`DepartureRunway` set, same airport, `RunwayGeometry.AreCloseParallels`; depart 28R for 28L traffic) is leg 1 as well, ordered by along-track distance from the pattern runway's threshold (its transition upwind already flies on the pattern runway's frame), refused "Unable, on upwind for runway {pattern rwy}, …", and its climb-out gate is the farther of the two runways' departure ends along the pattern runway's heading (the end the transition upwind turns crosswind past, `PatternGeometry.ComputeTransition`, so the turn never crosses either runway's departure path; AIM §4-3-2.c.3, FIG 4-3-3 key 5), on the flown runway's heading, at the pattern runway's TPA − 300. A closed-traffic climb onto a runway crossing its pattern runway (same airport, not a close parallel; depart 28R for 33 traffic) is a **pattern entrant** (leg 0) as follower and lead through the airborne `TakeoffPhase` and its transition `UpwindPhase` (`AirborneFollowHelper.IsCrossingTransitionClimb`), until `MidfieldCrossingPhase` hands it to the pattern runway's downwind. It is detected from the phase list — a closed-traffic climb whose next upwind is followed by a `MidfieldCrossingPhase`, or an upwind whose threshold lies more than 0.05 NM off the pattern runway's extended centreline — never from `DepartureRunway` alone, which is never cleared, so a later upwind reads leg 1. Its frame is the queued crossing's pattern-runway waypoints (`CrossingTransitionWaypoints`, used by `SequenceWaypoints` and `FirstPatternWaypoints`, so a pursuit's pattern return carries the pattern runway's side and altitude); its remaining path (`EntryRemainingPathNm`) is a straight line to the crossing's midfield target then the pattern runway's downwind, no corner credit. As a follower it is never refused by the same-runway sequence check (`SequenceRefusalPosition` null), the cone says "Unable, on pattern entry for runway {pattern rwy}, {T} is not ahead of us, request vectors", and its transition upwind is not extended by the remaining-path leg hold (it spaces by speed until the crossing hands to the downwind). As a lead it is not departing: accepted from upwind, crosswind and downwind followers (flow-behind until it joins), refused from base and final. Its climb-out gate is the **flown** runway's own departure end and heading (not `PatternGeometry.TransitionDepartureEnd`, which may pick the pattern runway's end) at the pattern runway's TPA − 300 (`VfrFollowPhase.CrossingTransitionGate`, AIM §4-3-2.c.1, FIG 4-3-2/4-3-3 keys 4–5); once in `MidfieldCrossingPhase` there is no gate. A closed-traffic climb whose pattern runway is at another airport is a departing lead.

**...but not from base or final.** The re-sequence is refused when the follower is already on `BasePhase` or `FinalApproachPhase` ("Unable, on {base|final} for runway {rwy}, request vectors to follow {T}"). From there the follower is low and close in, and swinging it onto a closely-spaced parallel would fly a low crossing of its original runway's final approach course (AIM §4-3-3 FIG 4-3-3 note 7 — do not penetrate the parallel's final; §4-3-5 — no unexpected pattern maneuvers). The controller re-sequences explicitly (`ELB`/`ERB`), vectors, or sends it around. Re-sequencing from upwind / crosswind / downwind / pattern-entry is allowed.

**A lead with no runway, from a pattern leg** (`TryRouteRunwaylessLead`, `TryFollowFromPatternLeg`). A follower on a pattern leg with an assigned runway refuses a lead on the ground ("Unable, {T} is on the ground") unless the lead is rolling out on the follower's own runway, and a lead whose runway or filed destination is another airport ("Unable, {T} is inbound to {APT}, request vectors"; `LeadBoundElsewhereRefusal`, airports compared with `AirportIdsMatch`, so HWD 28R is not OAK 28R). From final it refuses ("Unable, on final for runway {rwy}, request vectors to follow {T}"); from every other pattern leg (a pattern entry, crossing or teardrop, upwind, crosswind, downwind, base) it refuses a lead that is not ahead within ±60° of track ("Unable, on {leg} for runway {rwy}, {T} is not ahead of us, request vectors", `{leg}` being "pattern entry" for the three entry phases; `ConeRefusalPosition`). An upwind or crosswind follower also accepts a lead inside the **downwind box** (`IsLeadInDownwindBox`): along the downwind line from the downwind turn point to 3 NM past the base turn point, between half the downwind offset and the offset plus 1.5 NM from the extended centerline on the circuit side, tracking within ±90° of the downwind heading — traffic it falls in behind by flying its normal circuit, with no 360 (AIM §4-3-5); a lead astern on the runway line stays refused. The three limits are judgement figures (aviation review 2026-09-30). Approach and pursuit followers do not take this path; they have their own guards, below.

**Guards for a follower off the pattern legs** (`GroundOrElsewhereLeadRefusal`, `RunwaylessLeadConeRefusal`, after the pattern-leg path in `RouteFollow`). A follower on an instrument approach, in a `VfrFollowPhase` pursuit (a turn-out included) or outside the pattern refuses a lead on the ground ("Unable, {T} is on the ground"), except that a pursuit accepts a lead rolling out on its own runway (its `FollowPatternReturn.Runway`, else the assigned runway; the lead lifecycle ends that follow next tick and the pursuit returns to its circuit). An approach follower refuses every ground lead and keeps its approach: accepting would tear the approach down for a follow the lifecycle ends a tick later, leaving no phase. It refuses a lead bound for another airport with the pattern legs' text when its own airport (the assigned runway's airport, else the filed destination) is known and differs; an unknown airport on either side refuses nothing. A `FOLLOW` of the lead a pursuit is already flying is acknowledged and changes nothing (same phase, path, pattern return and turn-out), even when that lead has since drifted outside the cone. Otherwise an approach or pursuit follower refuses a runwayless lead outside the ±60° cone — "Unable, on approach for runway {rwy}, {T} is not ahead of us, request vectors" from an approach, "Unable, {T} is not ahead of us, request vectors" from a pursuit or an approach with no assigned runway — except that an approach follower also accepts a lead whose straight-line distance to its threshold is shorter than its own remaining path (`AirborneFollowHelper.FollowerRemainingPathNm`), which covers traffic ahead in sequence but behind the track during a course reversal (aviation review 2026-09-30). A refused `FOLLOWF` leaves traffic-in-sight as it was; only an accepted one marks it. A lead with a pattern entry queued for another runway (`PatternCommandHandler.QueuedPatternEntry`) is refused from base or final, the same low, close-in re-sequence the cross-runway refusal covers; from any other leg FOLLOW builds a pattern entry to the queued runway, as the cross-runway re-sequence does, on the side the queued entry names (ERx right, ELx left), then for an entry that names no side (EF) the lead's `TrafficDirection`, then the runway default. The queued entry's side comes first because a runwayless lead's phase direction can only be left over from an earlier circuit. Otherwise (nothing queued, or an entry for the follower's own runway) it pursues the lead with a `FollowPatternReturn` (its own runway, circuit side and pattern altitude). An unknown lead runway is never read as "same runway": the in-place retarget would leave a follower on base only a speed cap, which can only slow it and so moves nothing when it is already slower and far enough back.

**A new lead during a pursuit** (`CommandDispatcher.PursuitNewLeadRoute`, after the same-lead acknowledgement and before the cone). A pursuit that left its circuit from base (`FollowPatternReturn.FromBase`), or one turning out, compares a new runway-bearing airborne lead on that circuit (`VfrFollowPhase.NewLeadCircuit`: the turn-out's circuit while turning out, else the from-base return). A lead landing another runway is refused first, "Unable, {T} is landing runway {rwy2}, request vectors" (7110.65 §7-4-3.c.2); a lead on a pattern entry other than a straight-in (`AirborneFollowHelper.IsStraightInEntry`: an entry that joins the final) is refused "Unable, {T} is not ahead of us, request vectors"; otherwise the lead is ahead when its `SequenceRemainingPathNm` is no longer than the follower's own path (`VfrFollowPhase.FollowerPathToThresholdNm`: the shortest path to the threshold from its final-frame position at its base turn radius), no tolerance, and is refused with the same not-ahead text when it is longer or infinite (no sequence leg). An accepted lead is retargeted in place (`UpdateTarget`): the pattern return is kept, no downwind entry is built, and a running turn-out ends. A straight-in entrant is compared by path because it is already flying toward the final the follower joins, so falling in behind it takes only an extension or S-turns (AIM §4-3-3 NOTE 1, 7110.65 §3-8-1; aviation review 2026-09-30). A pursuit that did not leave from base keeps table H: a runway-bearing lead gets the downwind entry, a runwayless one the cone; ground leads keep the rule above.

**FOLLOW from an approach keeps the approach** (`InterceptCoursePhase`, `ApproachNavigationPhase`, `ProcedureTurnPhase`, and a `HoldingPatternPhase` with `IsHoldInLieu` accept it; `CommandDispatcher.KeepApproachFollow`). A same-runway lead that passes the refusals, or a runwayless lead inside the cone, sets only `Approach.FollowingCallsign`: phase list, landing clearance, turn overrides, altitude and speed targets and traffic-in-sight are kept (7110.65 §7-2-1.a.2 — a follow is pilot-applied visual separation, not a visual approach; §4-8-11.a). A procedure-turn or hold-in-lieu follower is measured by the straight line to its reversal fix plus the approach path from there (`FollowerRemainingPathNm`); a lead on one has no sequence leg. The published missed approach is not the approach: an `ApproachNavigationPhase` with `IsMissedApproach` has no sequence leg, an IFR follower on its own missed refuses "Unable, on the missed approach, request vectors" and a VFR one is re-sequenced with no landing clearance (§4-8-11.e, §4-8-9); a lead going around or on its missed is refused from an approach ("Unable, {T} is going around, request vectors"), and if it goes around during a kept-approach follow the follow ends, the approach continues and the RPO sees "{cs} follow of {T} ended — {T} went around". An IFR follower (`AirborneFollowHelper.IsIfrFollower`: a flight plan and not VFR) that loses sight of its lead outside a visual approach also gives the RPO "{cs} visual separation terminated — radar separation required". A repeat FOLLOW of the same lead is acknowledged unchanged while that lead still lands the follower's runway or has none. Only the VFR cross-runway re-sequence from outside the FAF still tears the approach down: `ClearPhaseChainKeepingClearance` releases the turn overrides and re-arms the assigned altitude, drops the landing clearance (§3-10-5.a/c; a clearance with no `ClearedRunwayId` counts as one for the previous `AssignedRunway`) and tells the RPO "{cs} {summary} cancelled by FOLLOW, landing clearance RWY {oldRwy} cancelled".

FOLLOW during a **wrong-side entry** (`MidfieldCrossingPhase` / `TeardropReentryPhase`) is additive for the same
runway: both phases accept `Follow`, run `CheckLeadLifecycle` + the free-flight spacing speed loop each tick
(mirroring `PatternEntryPhase`), and the `DownwindPhase` the entry feeds runs all the sequencing holds. Clearing the
crossing to free-pursue instead would discard the wrong-side entry (#352).

### FOLLOW rulings a change must respect

The reasons behind the behaviour above, with the alternatives that were tried or weighed and rejected. "Judgement figure" marks a value or rule the aviation review set where 7110.65 and the AIM give no figure.

- **Every refusal is a spoken pilot "unable".** Accepting a follow commits the pilot to maneuver as necessary to stay in trail (AIM §5-5-12.a.1, §4-4-14.b), and the pilot tells the controller promptly when it cannot accept that responsibility "for any reason" (AIM §5-5-12.a.2, §4-4-14.b NOTE). A lead the follower could reach only by a 360 or another major maneuver is refused (AIM §4-3-5), and the wording puts the reason first, then the request.
- **`FOLLOWF` is `FOLLOW` with the traffic-in-sight report folded in, nothing else.** The phase gate reads `FollowForce` as `Follow` (`CommandDispatcher.PhaseGateType`), so `FOLLOWF` keeps the pattern leg, the final and the landing clearance, and meets every refusal `FOLLOW` does. Rejected: a separate canonical type that no phase listed, which cleared the chain and its landing clearance and bypassed every refusal.
- **The ±60° cone is for runwayless leads only.** A same-runway lead is judged by sequence order, never by bearing: from a 3 NM final a lead on downwind abeam the threshold bears about 14° off the follower's track, so the cone would call it ahead.
- **The downwind box, not a cone on the downwind heading**, for upwind and crosswind followers. The track cone alone refused traffic on the downwind line the follower joins behind by flying its own circuit; a cone on the downwind heading accepted a lead directly astern, since upwind and downwind are antiparallel. The box's three limits are judgement figures.
- **No command-time ahead gate for a follower outside the pattern with a runway-bearing lead.** It is expected to maneuver into the sequence (7110.65 §7-6-7.a; AIM §5-5-12.a.1), and falling in behind is the pattern entry's job.
- **A pattern-entry lead is measured, not refused, from an instrument approach**: an approach follower 8–10 NM out can fall in trail behind a 45° entrant (AIM §5-5-12.a.1; 7110.65 §3-8-1). The entry path's straight lines with no corner-cut credit are a judgement figure, as is the crossing-runway climb's straight line to its midfield target.
- **"Inside the FAF"** is along-final distance at or below the published FAF distance or 5 NM, whichever is closer to the runway, 5 NM with no FAF: a judgement figure reading 7110.65 §5-7-1.b.4. An `InterceptCoursePhase` follower not yet on the final is outside.
- **An IFR follower is one with a flight plan that is not VFR**; no flight plan counts as VFR (14 CFR §91.173; 7110.65 §4-8-11.d).
- **A runwayless lead also keeps the approach** (judgement figure): a lead with no runway gives no landing sequence, and an IFR follow is pilot-applied visual separation that never leaves the cleared approach (7110.65 §7-2-1.a.2, §7-4-3). Only segments of the cleared approach are kept (§4-8-11.a): a plain en-route or published hold is not one and keeps the install (judgement figure). The hold-in-lieu is an explicit flag (`HoldingPatternPhase.IsHoldInLieu`), never inferred from a circuit limit. A procedure-turn or hold-in-lieu follower's path takes no credit for the outbound leg or the turn (judgement figure).
- **No command-time gap refusal for a lead already on final** when the approach is kept: the running checks (pre-final spacing, `FinalApproachPhase`'s S-turn, the structural break-off, the "unable to maintain separation" cancel) handle it.
- **Loss of sight.** An IFR follower's loss tells the RPO "visual separation terminated — radar separation required" (7110.65 §7-2-1, §4-8-11.c); a VFR follower gives only its own "lost sight" call (AIM §5-5-12.a.2; no RPO line, a judgement figure on §4-8-11.d.2/d.4). A lead that lands ends the follow with no visual-separation line, since traffic in its landing phase is no longer a factor (AIM §4-4-14.a.2 NOTE).
- **A lead that goes around during a kept-approach follow** ends the follow with no pilot transmission, since the lead is still in sight (judgement figure). It does not fall back to `FinalApproachPhase`'s baseline spacing, because a re-sequencing lead can end up behind the follower (judgement figure). A lead already going around is refused from an approach (judgement figure; AIM §5-5-12.a.1).
- **Pre-final spacing only slows the follower**, floored at the category approach speed plus the wind additive (judgement figures; 7110.65 §5-7-1.d; AIM §5-5-11.b.4, §5-5-9.a.2–3; the additive is manufacturer technique, and AIM §5-4-23.e makes spacing the pilot's). The intercept's 1.3 × approach-speed ceiling is a judgement figure. The floor uses the assigned runway's heading, not the final course, so it does not jump at the hand-off onto an LDA or SDF final. Nothing is written inside the 60 s stabilization window (judgement figure), and the fix-speed ceiling is re-latched after an explicit speed clears (7110.65 §5-7-4.a NOTE). Rejected: a speed baseline at the ceiling instead of the lead's IAS, which settled about 1.6 NM inside the desired gap and could not match a slower lead.
- **Climb followers keep the climb.** Two aircraft with no circuit waypoints (two go-arounds, a go-around and a closed climb) order by along-track distance from the threshold, a judgement figure. A runwayless lead accepted from a climb arms the pending pursuit instead of starting one from a few hundred feet.

### `VfrFollowPhase` — free pursuit + auto-join

`VfrFollowPhase` (`src/Yaat.Sim/Phases/Pattern/VfrFollowPhase.cs`, built by `CommandDispatcher` for FOLLOW) follows in trail behind the lead along its recorded ground path (below) and matches the lead's speed. Altitude is untouched unless the pursuit carries a `FollowPatternReturn` (it started from a pattern leg): then `OnStart` targets pattern altitude, or the lower of the present and pattern altitude when it started from base, and drops the base leg's glideslope descent (AIM §4-3-3). When such a pursuit ends (lead lost or despawned, landed with no sane final join, spacing lost), `ReturnToPattern` re-enters the pattern for the follower's own runway on its own side, or for the landed lead's runway when the lead is down and its runway was captured (`LifecycleReturn`) ("{cs} follow ended, re-entering {right|left} traffic runway {rwy}"). The re-entry is a downwind entry (a follower on the far side of a close parallel takes the midfield crossing, as any wrong-side entry does), or an upwind entry when the follower is past the threshold and flying the runway's way (`ReturnEntryLeg`), so it continues upwind, crosswind and downwind (AIM §4-3-3) instead of turning back onto the final. `TryEnterPattern` builds a fresh phase list, so an armed `PatternRunway` (a `COPT MLT 28L` flown on the 28R circuit) is carried into the re-entry by `KeepArmedPatternRunway`. `PatternReturn` (with `FromBase`) is snapshotted in `VfrFollowPhaseDto`, null for older snapshots. A return's pattern altitude is read from the circuit the follower was flying, and otherwise, as for a turn-out to a runway other than the return's, from `ResolvePatternAltitudeFt` (an authored or commanded override, else field elevation plus the category's pattern height).

**Lateral trail-keeping — `AirborneFollowHelper.ComputeFreePursuitHeading`.** The phase records the lead's ground path (`LeadPathTrail`: a sample every 0.05 NM, the last 4 NM, seeded from the lead's `PositionHistory` on the first tick and snapshotted as `VfrFollowPhaseDto.LeadPath`) and measures the gap **along that path**. The desired gap is the pattern spacing `DesiredDistanceForLeader` raised to the on-approach wake minimum for a lead in or bound for the pattern, `RequiredFinalInTrailNm` for a lead on a straight-in final, and the free-flight spacing otherwise. Once spaced, the follower points its nose at the lead while the lead is on a straight leg (the path to it within 15° of the lead's track) and through the lead's turns steers at a point on the path ahead, so it turns where the lead turned rather than cutting the corner (AIM §5-5-12.a.1 and §4-4-14.b: in trail). **Too close** (along-path gap short of desired by more than ~0.1 NM), it slows to its speed floor and flies a shallow S-turn excursion (AIM §4-3-5): 30° off the lead's track, 45° when short by more than 0.3 NM (turboprops and jets 30° only), to the pattern's outside, never toward the final or a parallel final (a side with a parallel centerline within the cap + 0.5 NM is closed; both closed means speed alone), capped at max(1.0 NM, 3 turn radii) off the lead's track (1.5 NM for turboprops and jets). It ends at desired + 0.1 NM, the side latched in `FollowWidenState` (serialized), and the pilot says "S-turning for spacing behind the traffic" at most once a minute. While more than 0.3 NM off the path the target speed is capped at the lead's, so an ended excursion does not speed back in. At the cap without the gap it holds parallel at the floor and **extends** past the lead's base turn point, turning base once the gap (the lead's path from its base start plus the extension) is met. At 2 NM past that point (`BaseExtensionLimitNm`), or nearing the final, the follow ends: the follower installs a held extended downwind for the lead's runway and direction and calls "unable to follow the traffic, extending downwind, request base turn" (AIM §5-5-12.a.2); `TB`, `ERB`/`ELB` or `EXT` then work as on any downwind. Below `TrailMinLeadGroundSpeedKt = 35` the lead's track is unreliable and it degrades to pure pursuit.

Why the pursuit builds spacing this way. A follower close behind a lead often sits at its speed floor already (the S2-OAK-3 case held 0.86–0.91 nm behind a piston), so no gap gate can be met by speed alone; the spacing has to be built laterally, starting as soon as the gap is short rather than waiting for the speed floor, until the follower can point its nose at the lead and follow in a chain. The S-turn is shallow and never a 360 or a 90° turn-out, because an unannounced maneuver in the pattern must stay minor (AIM §4-3-5). There is one gap target (the join's pattern spacing plus ~0.1 nm hysteresis, measured along the lead's recorded path), so the excursion, the base join and the chain agree on when the follower is spaced. Once spaced it flies the lead's ground track through the lead's turns, because pure pursuit cuts the corner and loses up to ~0.3 nm; in trail is what AIM §5-5-12.a.1 asks. At the extension limit it keeps flying the extended leg and asks for a base turn rather than re-entering by a downwind entry, which would reverse it against the downwind flow (AIM §4-3-5).

**Turn-out to the downwind heading** (`TryStartTurnOut`, `TickTurnOut`). A follower level with or ahead of its lead cannot drop behind a lead flying the same speed, and continuing to the final ahead of it is cutting in (AIM §4-3-4). The S-turn only lengthens a gap that already exists; a negative gap needs a major maneuver, which a pilot may fly after advising the controller (AIM §4-3-5). The turn-out is that maneuver: an "extend downwind" (7110.65 §3-8-1) flown after the fact, in the pattern flow (AIM §4-3-3), whose gain grows with the time it is held. It is not a go-around (flown from final, and a controller instruction) and not a self-initiated 360 (the controller's tool under 7110.65 §3-8-1, with a fixed gain that fails for a lead farther out). The S-turn's "no 360, no 90° turn-out" binds only the unannounced S-turn, not this announced turn-out. A later controller command (a 360 or 270, a re-sequence, a base turn) overrides it.

- **Trigger**, checked each pursuit tick: the lead is airborne on base or final to a runway (final by phase or by geometry, `IsOnFinalByGeometry`, straight-in finals included) and the follower is on that runway's pattern side within `TurnOutRangeNm = 5` of its threshold; and either the follower is level or ahead by path (its shortest path to the threshold, a turn to final in the pattern direction at `BasePhase.TurnRadiusNm`, `ShortestPathToThresholdNm`, is no longer than the lead's remaining path), or the S-turn excursion has stalled alongside the lead (at the offset cap, more than `StallShortfallNm = 0.1` short of the gap, and the gap grew by less than `StallMinGrowthNm = 0.05` over `StallWindowSeconds = 20`, `ParallelHoldStalled`), or a base break-off requested it (`RequestTurnOut`). A requested turn-out skips the 5 nm range (the range still gates the per-tick trigger) and is consumed before the base, pattern and final joins, so a break-off never flips straight into a join. The circuit it rejoins is the pursuit's own pattern return when that names the lead's runway, else the lead's runway in the lead's traffic direction (the runway default without one).
- **One call** on the tick it starts: "turning downwind for spacing behind the traffic, request base turn." (`PilotResponder.BuildTurningDownwindForSpacing`, see [`pilot-phraseology.md`](pilot-phraseology.md)). It says "behind the traffic", not "the traffic is behind us", because the lead may still be behind or level when the follower turns.
- **The turn**: to the runway's reciprocal (the downwind heading), the way that first moves the follower away from the final centerline (`ReversalTurn`): from base, 90° against the pattern direction; from a heading parallel to the final, 180° turning outward, at the category's pattern bank. It levels at the circuit's pattern altitude, climbing back from a base descent if need be (an assigned altitude stands), at approach speed. The S-turn excursion and the stall window are dropped.
- **Offset band**: from a floor at the excursion's offset cap (`ExcursionLimitsFor`: max(1.0 nm, 3 turn radii), 1.5 nm for turboprops and jets) to a ceiling `TurnOutBandWidthNm = 1.0` beyond it. Below the floor it flies `TurnOutCorrectionDeg = 30°` outward of the downwind heading, above the ceiling 30° inward (`TurnOutOffsetCorrection`), unless the correction would carry it toward a parallel runway's final (`CorrectionMeetsParallelFinal`).
- **Exit to base**, once the reversal is done and both hold (`ShouldExitTurnOut`): the lead has passed abeam (nearer the threshold along the final), and the gap a base turned now would roll out with, by `BaseFollowSpacing`'s own projection with runway occupancy (`ProjectedBaseGapNm`), is at least the pattern spacing plus `TurnOutExitMarginNm = 0.1`; and the base turn crosses no parallel runway's final. Judging by the base's own projection means the base it installs never breaks off again. It then turns base in the pattern direction from where it is: `PatternBuilder.BuildCircuit(Base, present along-final distance, pattern altitude)`, still following, carried clearances reapplied.
- **Distance limit**: `TurnOutMaxExtensionNm = 2.0` along the final past the turn-out point, or `TurnOutMaxAlongFinalNm = 6.0` from the threshold, without the exit → the follow ends into a held extended downwind for the controller's base turn, with an RPO note and no second call (the no-room rule above).
- **Lead lands, goes around or leaves the final** (or is lost) → the follow ends and the follower, already on a pattern-side downwind, re-enters the circuit as a downwind (`ReturnToPattern`) with its landing clearance, turns base at the circuit's normal base point, and says nothing.

The turn-out state (`FollowTurnOut`: circuit and start point), a pending request and the stall window are snapshotted on `VfrFollowPhaseDto` as nullable fields; older snapshots restore without them.

When the lead is in a pattern, `VfrFollowPhase.TryJoinLeadPattern` rebuilds the follower's phase list with a full circuit copied from the lead's runway/direction/altitude, gated on **three** conditions:

1. follower within `JoinRangeNm = 3.0` of the lead's downwind abeam point,
2. follower within `MaxJoinGapNm = 5.0` of the lead itself (guards against a stale pattern), and
3. follower on the **pattern side** of the runway centerline (`VfrFollowPhase.IsOnPatternSide`, which uses the positive-is-right cross-track convention).

On join it preserves `FollowingCallsign` so the pattern phases keep adjusting spacing, and skips `PatternEntryPhase`
if the follower is already established on the downwind leg.

**Lead on base — `TryJoinLeadBase`.** Runs before `TryJoinLeadPattern` while the lead flies `BasePhase`, which records where its base began (`BasePhase.StartPoint`, `BasePhaseDto.StartLat/StartLon`; an old snapshot falls back to the lead's position). The follower joins that base when it is on the pattern side, its path does not cross a parallel's final, it is not past the lead's base line, the turn at the entry is ≤ 120°, it is within 5 NM of the entry, and the straight-line gap is at least the desired spacing. It flies a base entry to the start point and then `PatternBuilder.BuildCircuit(Base, …)` on the lead's runway, direction and final-turn distance, entering at the lowest of the lead's pattern altitude, its own category's and (from a base-leg pursuit) its present target; a controller-assigned altitude is kept. Every landing-family clearance the follower holds (land, touch-and-go, option, stop-and-go, low approach) is carried onto this and every other join (`InstallJoinedCircuit`). Retargeting FOLLOW to another lead drops the old lead's path and base. Joining the lead's own base at its start point puts the follower on the same runway, direction and final-turn distance as the lead, so the normal pattern spacing keeps the distance by speed alone; this holds for every pursuit, a FOLLOW from a clean start included. The follower's position is judged against the lead's base line, not the start point itself, because a pattern-entry waypoint completes 0.5 nm out (`FlightPhysics.NavArrivalNm`).

**Straight-in lead — `TryJoinLeadFinal`.** When the lead is on a straight-in final/landing to a known runway but
has *no* pattern-leg waypoints to copy (e.g. an IFR aircraft that spawned directly onto `FinalApproachPhase`),
`TryJoinLeadPattern` returns null and `TryJoinLeadFinal` takes over. It sequences the follower onto that runway's
final once the in-trail spacing (`followerDist − leadDist`) is at least `requiredInTrail = max(SameRunwayInTrailFloorNm
= 1.5, WakeTurbulenceData.OnApproachWakeSeparationNm(lead → follower))` and the follower is aligned for a sane
intercept (`|cross-track| ≤ MaxFinalJoinCrossTrackNm = 1.0`, `intercept ≤ MaxFinalJoinInterceptDeg = 30°`, not inside
`MinFinalJoinDistNm = 0.5`). The join is also refused when a
near-parallel runway's extended centerline lies laterally **between** the follower and the target centerline
(`JoinCapturePathCrossesParallelFinal`, heading delta ≤ 10° against **either stored end** — navdata stores each
physical runway oriented to an arbitrary end, e.g. KOAK stores 10L/10R, so a stored-orientation-only test silently
never fires): the 1.0 nm cross-track allowance dwarfs closely-spaced parallel separation (OAK 28L/28R ≈ 0.165 nm), and
capturing through the adjacent final approach course at low altitude
violates AIM §4-3-3 FIG 4-3-3 note 7. The in-trail floor keeps the follower genuinely behind the traffic (AIM §4-3-4.4 — no
cutting in front, since 1.5 > 0) and at the 7110.65 §3-10-3 same-runway minimum for a light single behind same/lighter
traffic, rising to the CWT wake minimum (TBL 5-5-2) for a heavier lead. (FOLLOW itself is rejected at command time when
the lead is a super — visual separation prohibited, 7110.65 §7-2-1.) `SequenceOntoFinal` builds `PatternEntryPhase →
FinalApproachPhase → LandingPhase` for the lead's runway with the follower's own category and `FollowingCallsign`
preserved, but sets **no** `LandingClearance` — the follower descends on the glideslope behind the lead and holds for a
separate `CLAND`, going around at minimums if never cleared.

The leading `PatternEntryPhase` is load-bearing: `FinalApproachPhase.OnStart` needs `ctx.Runway`, which `PreTick`
only populates from the new `AssignedRunway` on the tick *after* the phase-list swap. Routing through
`PatternEntryPhase` (which tolerates a null runway at start and aligns the follower onto the extended centerline)
defers `FinalApproachPhase` to a valid-runway tick — the same reason the pattern auto-join path begins with a
`PatternEntryPhase`.

**Lead landed — `TrySequenceBehindLandedLead`.** The lead's runway is captured each tick into `_leadLandingRunway` (serialized in `VfrFollowPhaseDto`) while the lead is airborne on final/landing, so a lead that touches down before the follower has rolled onto final still names the runway to sequence onto, instead of the follow ending with the follower levelling off over the field. The follower joins that final (`SequenceOntoFinal`) only through `TryJoinLeadFinal`'s geometry gates without its in-trail gate, since the lead is down and spacing is moot (`CanSequenceOntoLandedFinal`): on the approach side of the threshold at least `MinFinalJoinDistNm = 0.5` out, cross-track ≤ `MaxFinalJoinCrossTrackNm = 1.0`, intercept ≤ `MaxFinalJoinInterceptDeg = 30°`, and no parallel runway's final crossed (AIM §4-3-3 FIG 4-3-3 note 7). Joining a final from beyond the threshold is never acceptable: without these gates measured runs reversed over the runway and reached final 0.05 and 0.21 nm out. When a gate fails the follow ends (the lead is on the ground) and the follower re-enters the pattern for the landed runway (`LifecycleReturn` → `ReturnToPattern`): from past the threshold on the pattern side, an upwind entry into the crosswind and downwind (AIM §4-3-3); from the far side of a parallel, the midfield crossing; never a direct join of the final. An armed pattern runway survives the re-entry.

**On-final spacing S-turn — `FinalApproachPhase.ShouldAutoSTurnForSpacing`.** Once a follower is established on final
behind its traffic, the existing speed loop is the spacing tool inside the FAS gate, but a follower that catches up to
less than the pattern-tight desired while still **outside `STurnSpacingFloorNm = 5.0`** self-initiates a single shallow
`STurnPhase` (`Count = 1`) for spacing, then resumes a fresh `FinalApproachPhase` (the resume is a new instance — its
`OnStart` re-derives geometry; the pilot-decision go-around roll is guarded by `_goAroundRolled` so it doesn't re-fire,
and `SkipInterceptCheck` avoids re-scoring). A `STurnSpacingCooldownSeconds = 45` cooldown (carried onto the resume,
serialized on `FinalApproachPhaseDto`) prevents stacking. Inside 5 nm nothing changes — the aircraft is committed to the
stabilized approach / go-around logic. AIM §4-3-5; 7110.65 §5-7-1.b.4 (no maneuvering-for-spacing inside the FAF).

## Approach scoring — `ApproachEvaluator` + `ApproachScore`

`ApproachScore` (`src/Yaat.Sim/Phases/ApproachScore.cs`) captures intercept metrics at establishment (angle,
distance, glideslope deviation, speed, forced flag, §5-9-1 legality flags) and the landing timestamp.
`FinalApproachPhase` creates it when the aircraft is **established on the localizer** (cross-track <
`InterceptCrossTrackThresholdNm = 0.1` and heading diff < `InterceptHeadingThresholdDeg = 15°`), preferring the
capture distance/angle that `InterceptCoursePhase` recorded over the stricter establishment values.

**§5-9-1 legality** is anchored by `ApproachGateDatabase` (`src/Yaat.Sim/Data/ApproachGateDatabase.cs`), which
**precomputes per-(airport, runway) FAF→threshold distances** from CIFP FAF positions at init and finishes the
gate at read time:

```
approachGate  = max(FAF→threshold + thresholdDisplacement + 1nm, 5nm)
minIntercept  = approachGate + 2nm        (default 7.0nm when no data)
```

The displacement term is a read-time argument because the table is built at startup, before any airport map
exists — its stored FAF distance therefore runs to the **pavement** end. The P/CG defines the gate against the
**landing** threshold ("no closer than 5 miles from the landing threshold"), and both callers measure the
aircraft's distance to the landing threshold too, so they pass
`LandingThreshold.DisplacementFt(runway, ctx.GroundLayout) / GeoMath.FeetPerNm`. Pass 0 and the runway reads
as undisplaced, which is the no-layout fallback.

The **max intercept angle** is *not* precomputed here — `FinalApproachPhase` derives it at establishment from the
capture distance, reconstructing the gate as `minIntercept − 2nm` (`FinalApproachPhase.cs:953`–955):

```
distToGate = captureDistNm − approachGate
farGate    = (category == Helicopter) ? 45° : 30°   // TBL 5-9-1 "2 miles or more" row
maxAngle   = (distToGate < 2nm) ? 20° : farGate     // the 20° row applies to every category
```

`ApproachEvaluator` (`src/Yaat.Sim/Phases/ApproachEvaluator.cs`) is the per-room tracker. `RecordEstablishment`
computes separation to the closest preceding established same-runway aircraft (using live snapshot positions when
still airborne); `RecordLanding` stamps the landing time and grades. The **demerit rubric** (`ComputeGrade`,
`ApproachEvaluator.cs:100`): forced +3, illegal angle +2, illegal distance +2, GS > 500 ft +2 / > 300 ft +1; then
`0 demerits & |GS| ≤ 100 ⇒ A`, `0 ⇒ B`, `1 ⇒ C`, `2 ⇒ D`, else `F`. `BuildReport` aggregates per-runway stats
(arrival rate, avg time between landings, min separation) and an overall grade.

## Construction call-sites

Which command handler builds which phase (parsing/dispatch live in
[command-pipeline.md](command-pipeline.md) / [command-handlers.md](command-handlers.md)):

| Command | Handler | Phases built |
|---|---|---|
| **JFAC** / **JLOC** (join localizer / final approach course — **lateral only**) | `NavigationCommandHandler.DispatchJfac` (`:906`) | `InterceptCoursePhase` (no `ForcedIntercept`) → `FinalApproachPhase` → landing, with `ApproachClearance.LateralInterceptOnly = true` |
| **CAPP** (cleared approach) — implied PTAC (on vectors, no nav route) | `ApproachCommandHandler.TryClearedApproach` (`:143`) | `InterceptCoursePhase` (`ForcedIntercept = cmd.Force`) → `FinalApproachPhase` → landing |
| **CAPP** — published procedure | `ApproachCommandHandler.TryClearedApproach` (`:189`+) | optional `ProcedureTurnPhase` / hold-in-lieu `HoldingPatternPhase` → `ApproachNavigationPhase` → `FinalApproachPhase` → landing |
| **JAPP** (join approach) | `ApproachCommandHandler.TryJoinApproach` (`:330`) | `ApproachNavigationPhase` (+ HILPT hold) → `FinalApproachPhase` → landing |
| **PTAC** (present-heading intercept) | `ApproachCommandHandler.TryPtac` (`:413`) | `InterceptCoursePhase` (`ForcedIntercept = cmd.Forced`) → `FinalApproachPhase` → landing |
| Missed-approach hold | `ApproachCommandHandler` (`:1053`) | `ApproachNavigationPhase` → `HoldingPatternPhase` |
| **Pattern** (TPAT / pattern legs, SA/MNA/TB/EXT/OFL/OFR) | `PatternCommandHandler` (`:462`, `:486`) | `PatternEntryPhase` / `MidfieldCrossingPhase` / `TeardropReentryPhase` + circuit legs |
| **EF** (enter final) — parallel sidestep | `PatternCommandHandler.TryEnterPattern` → `ApplySidestep` | none — retargets the **active** `FinalApproachPhase` in place (`RetargetRunway`); only when the target is a parallel of the runway the aircraft is on final for, ≥ `MinSidestepAglFt` |
| **EF** — same-runway short-final continue | `PatternCommandHandler.TryEnterPattern` | none — redundant re-clearance while established on final inside the entry point returns "continuing final" and leaves the live phases untouched (#228) |
| **CVA** / **CVAF** (cleared visual approach; IFR-only; `Force` bypasses the RFIS field-in-sight gate) | `ApproachCommandHandler.TryClearedVisualApproach` (`:459`) | by angle off final: straight-in `FinalApproachPhase` (≤30°); angled-join `ApproachNavigationPhase` (one `INTCP` fix) → `FinalApproachPhase` (30–90°); IFR-visual pattern `PatternEntryPhase` → Downwind → Base → `FinalApproachPhase` (>90°) → landing. `ApproachId = "VIS<rwy>"`. Acquisition gate (§7-4-3.a): following → requires `HasReportedTrafficInSight` (and lead not a super); otherwise → requires `HasReportedFieldInSight`. `Force` (CVAF) sets the required flag |
| **FOLLOW** / **FOLLOWF** (VFR-only; `Force` bypasses the RTIS traffic-in-sight gate) | `CommandDispatcher.TryAirborneFollow` | `VfrFollowPhase` |
| Holding (HOLD) | `NavigationCommandHandler` (`:876`) | `HoldingPatternPhase` |

**An approach clearance cancels the assigned speed and restates a standing in-trail reduction.** `TryClearedApproachCore`
(`CAPP`), `TryPtac` (`PTAC`), `TryJoinApproachCore` (`JAPP`) and `TryClearedVisualApproach` (`CVA`) all null `Targets.TargetSpeed` (7110.65 §5-7-1.d) and then call `RestateStandingInTrailReduction`. When the
same-runway protection pass is holding the arrival to a reduction (`Approach.SameRunwayProtectionCeilingKts` set), the
reduction outlives the clearance — the pass re-stamps its `SpeedCeiling` every tick — so the controller says it:
`NCT → UAL123: maintain 180 knots (in-trail spacing, restated with the approach clearance)`
(`SameRunwayArrivalProtection.RestatementLine`; the speaker is the track owner, else the simulated approach controller).
§5-7-1.c requires a previously assigned speed to be restated with the approach clearance if it is to stay in force, and
AIM 4-4-12.g otherwise has the pilot making their own speed adjustments. The line is plain, with no "until (fix)", and it
writes nothing to the aircraft. It is silent when nothing stands, and when the latched instruction is the simulated tower's
"reduce to final approach speed" (`SameRunwayProtectionFasInstructed`) — that instruction carries no figure to restate.

## Footguns & pitfalls

- **Low approach → land a diverging runway (`CLAND <B>` during `LA`, #292) does NOT reuse `TryEnterPattern`.**
  `TryEnterPattern` rebuilds the whole `PhaseList`, which would wipe the in-progress low pass. Instead
  `PatternCommandHandler.TryRetargetLowApproachToRunway` keeps the running `LowApproachPhase` (put into
  *retarget mode* via `EnableRetargetToDifferentRunway`), reassigns `AssignedRunway` to B only after the low
  approach has cached A's threshold/heading (so the low pass still flies runway A), and *appends*
  `PatternEntryPhase(B) → FinalApproachPhase(B) → LandingPhase(B)`. In retarget mode the `LowApproachPhase`
  completes at the last feasible turn point — a **gate** a short distance (`RetargetFinalGateNm`) out on B's
  final — or the low-pass floor, whichever comes first. The gate is deliberately *short* (0.5 nm): for a
  diverging pair that shares a corner (KOAK 28R/33) the aircraft on A's final is always well left of B's final
  (cross-track ≥ 0.5 nm even at A's threshold), so B is only ever reachable as a tight, curved, short-final
  intercept — never a 1 nm straight-in. A short gate lets the aircraft fly the low approach down A and turn late
  and low. `EvaluateLowApproachRetargetFeasibility` gates it (piston/helicopter only, divergence 15–90°, no
  intersection, not past B's final, turn completable at the 3°/s execution rate) and runs **before any state
  mutation** so a reject leaves the low approach intact.
- **`JFAC`/`JLOC` is lateral-only — `FinalApproachPhase` must not descend until `CAPP`.** `DispatchJfac` sets
  `ApproachClearance.LateralInterceptOnly = true` and keeps the assigned altitude/speed. `FinalApproachPhase` checks
  that flag (`OnStart` skips the approach decel; `OnTick` holds the assigned altitude and skips the glideslope /
  go-around / landing logic) so the aircraft tracks the localizer level until `CAPP` clears the flag (a fresh
  clearance from `BuildClearance` defaults it false → descent authorized). The whole `InterceptCourse → FinalApproach`
  chain is identical to `CAPP`'s; only the flag distinguishes "joined the localizer" from "cleared for the approach."
- **Pattern legs do NOT complete on waypoint arrival.** `DownwindPhase` completes on along-track past the base-turn
  point; `BasePhase` completes when cross-track from the extended centerline ≤ turn radius. Editing the waypoints
  without understanding the along-track / cross-track trigger model produces aircraft that overshoot or turn early.
- **`SignedCrossTrackDistanceNm` is positive = RIGHT of the reference heading.** Pattern-side, deconfliction,
  lateral-offset, and intercept all depend on the sign per `Left`/`Right` pattern. A flipped sign silently sends
  the aircraft to the wrong side.
- **Crosswind turn offset is inverted from the pattern name.** A *left* pattern uses turnOffset **−90°**, a *right*
  pattern uses **+90°** (`PatternGeometry.cs:180`). Don't "fix" the sign to match the name.
- **`AirborneFollowHelper.GetAdjustedSpeed` MUST be fed the phase's fixed baseline speed** (`DownwindSpeed` /
  `BaseSpeed`), never the previous tick's `TargetSpeed`. Feeding the output back compounds the ±`MaxSpeedAdjustKts`
  clamp every tick and lets IAS escape the stabilized-approach gate. Every pattern `OnTick` re-derives the baseline
  for this reason. Pre-final spacing uses the lead's clamped IAS as its baseline for the same reason, and its ceilings are latched once, never re-derived from the previous tick (the final's entry latch can take the intercept's last spaced speed: a one-time, conservative step down).
- **`InterceptCoursePhase` computes the heading diff three ways and takes the min** (current-vs-FAC,
  current-vs-runway-number-heading-from-regex, assigned-magnetic-vs-runway-number), specifically to tolerate
  magnetic variation. A two-way comparison reintroduces the false bust-through bug.
- **`ForcedIntercept` (PTACF / implied-PTAC in CAPPF) silently raises the capture gate from 30° to 180°**, making
  the bust-through branch unreachable — the aircraft captures at any angle and S-turns back under
  `FinalApproachPhase`. Don't assume the 30° gate is always active.
- **Parallel-offset approaches don't terminate at the threshold.** `InterceptCoursePhase` and `FinalApproachPhase`
  measure cross-track against `ApproachClearance.FinalApproachAnchorLat/Lon` when set, falling back to the threshold
  otherwise. Code that hard-codes the threshold breaks offset approaches (KDCA LDA-X 19, KCCR S19R).
- **Holding entry sectors are 110°/250°, not 70°.** The `HoldingEntryCalculator` comment says "70-degree sector
  rule," but the code branches on `< 110` and `< 250`. Trust the code.
- **Holding outbound carries a TRIPLE-drift wind correction** (AIM 5-3-8(j)(8)(c)) and the outbound timer is
  predictive (sized so the inbound ground distance matches the target at inbound groundspeed). Naive "fly the
  reciprocal for one minute" is wrong; the tests catch it.
- **`ProcedureTurnPhase` turns back early by `TurnRadiusReserveNm` (2 nm)** before `MaxOutboundDistanceNm` so the
  180° turn radius itself stays inside protected airspace (AIM 5-4-9.a.3), clamps IAS to 200 KIAS via
  `SpeedCeiling`, and gates hand-off on a 1 nm lateral-intercept tolerance (not heading alone).
- **A growing gap never cancels a follow.** The only self-generated cancel is loss of visual contact (a cloud deck
  between the pair); the lead landing or despawning also ends it. The bound on a slow ahead-lead is the spatial
  `MaxFollowExtensionNm = 4.0` advisory cap — past it the pilot advises once and keeps flying the leg.
- **`PatternWaypoints.FromSnapshot` infers `Direction` from the downwind-abeam cross-track sign** for snapshots
  predating the explicit `Direction` field (`PatternGeometry.cs:97`). Don't "simplify" it away or old recordings
  replay with the wrong pattern hand.
- **Short approach sets `_pastAbeam = true` in `DownwindPhase.OnStart`** to suppress the normal abeam descent
  trigger, and clamps the new base-turn to `max(compressed, currentAlongTrack)` so the aircraft never reverses to
  an already-passed turn point. MNA restoring the original base-turn can leave it behind the aircraft — completing
  next tick is correct.
- **TeardropReentry's per-waypoint altitudes are TPA+250 / TPA+50 / TPA**, not "+500 → TPA." The +500 is the
  *entry* altitude handed in by `MidfieldCrossingPhase`; the class comment / log describe the band loosely.
- **Pattern phases — and `FinalApproachPhase` — set `ManagesSpeed = true`; the other approach phases do NOT.** `DownwindPhase`, `BasePhase`,
  `PatternEntryPhase`, `TeardropReentryPhase`, `VfrFollowPhase` and `FinalApproachPhase` (`Phases/Tower/`) all override `ManagesSpeed` to `true`, so
  `FlightPhysics`' auto speed schedule is suppressed and the phase owns `TargetSpeed`. The **approach** phases
  (`InterceptCoursePhase`, `ApproachNavigationPhase`, `HoldingPatternPhase`, `ProcedureTurnPhase`) leave
  `ManagesSpeed` at its default `false` — auto speed is instead suppressed because `ActiveApproach` is set (see
  [tick-loop.md](tick-loop.md) step 7). A pattern phase that forgets to set `TargetSpeed` leaves the aircraft at
  whatever speed it had (see phases.md "ManagesSpeed is contagious").
- **A speed command inside 5 nm of the threshold is *Rejected*, never `ClearsPhase`.** `FinalApproachPhase.CanAcceptCommand`
  returns `Rejected("unable, inside 5 nm final")` for the whole speed family (`SPD`/`RFAS`/`RNS`/`DSR`/Mach) within
  `SpeedCommandFinalGateNm = 5 nm` (cached `DistanceToThresholdNm`) — the pilot says "unable" and the established ILS
  final stays intact. Clearing the phase tore down an established `FinalApproach → Landing` chain (SWA4587 leveled off
  on OAK ILS 30 and couldn't re-land). Footgun: there are **two independent 5 nm gates** — the handler-level one in
  `FlightCommandHandler` rejects `SpeedCommand` *only* (it reads `cmd.Force`), so `RFAS`/`RNS`/`DSR`/Mach relied entirely
  on the phase-level gate; the phase-level branch must reject the whole family, not just `SPD`. `SPEEDF` stays
  always-Allowed; outside 5 nm the family is additive. Tests: `Swa4587RfasRejectsInsideGateTests`,
  `PhaseAcceptanceAuditTests.FinalApproachPhase_SpeedFamily_RejectedInsideFiveNm`.
- **The no-landing-clearance warning has two *separate* latches — don't collapse them.** `_noClearanceFlashIssued`
  drives the red `NoLndgClnc` datablock flash (`AircraftState.NoLandingClearanceWarningActive`) and arms **earlier** for
  controller reaction time: 2 nm on a visual (`NoClearanceFlashDistNm`) or `MAP + 1000 ft` on an instrument approach.
  `_noClearanceWarningIssued` drives the AI pilot's verbal "short final" callout at realistic timing: 1 nm visual
  (`NoClearanceWarningDistNm`) or `MAP + 1000 ft`. `FinalApproachPhase` is the **only** writer of the flash flag
  (re-asserts it every tick), so it is cleared in the single phase-exit hook `FinalApproachPhase.OnEnd` — never in the
  individual go-around paths (a manual `GA` rebuild once left the flash stuck on forever). Both latches are
  snapshot-serialized. Test: `GoAroundClearsNoLandingClearanceFlashTests`.
- **Pattern re-entry picks its terminal phase from `LandingClearance`, not `TrafficDirection`.** `PhaseList.TrafficDirection`
  is overloaded (turn-side geometry *and*, historically, touch-and-go intent), and `PatternCommandHandler.TryEnterPattern`
  stamps it on every EF/ERB/ELB/… rebuild to preserve the turn side — so choosing the rebuilt circuit's terminal from that
  field silently converted a landing into a touch-and-go on the *second* entry (turn side now non-null). Branch on
  `aircraft.Phases.LandingClearance`: `ClearedToLand` → full-stop `LandingPhase`; `ClearedForOption`/`TouchAndGo`/`StopAndGo`/`LowApproach`
  → touch-and-go family; no clearance → fall back to `TrafficDirection is not null` (closed-traffic work). A touch-and-go
  is authorized only by `TG`/`COPT`/`SG`/`LA`, never as a side effect of re-entering the pattern. Tests:
  `PatternEntryPreservesLandingClearanceTests`, `N713UpErbLandingClearanceTests`.
- **Re-inserting a phase re-runs its `OnStart`.** `PhaseList.AdvanceToNext`/`Start`/`SkipTo` always call `OnStart` when a
  phase becomes current (only snapshot restore sets `CurrentIndex` without it), so an in-final maneuver that does
  `InsertAfterCurrent([maneuver, resumeFinal])` re-fires the resume phase's `OnStart` side effects. All three in-final
  maneuver paths (`ClonePatternPhase` for `L360`/`R360`, `TryMakeSTurns` for `MLS`/`MRS`, and the automatic spacing
  S-turn) resume via `FinalApproachPhase.CloneForResume()` = `new() { SkipInterceptCheck = true, _goAroundRolled = true }` —
  it realigns geometry from `ctx.Runway`/`ActiveApproach` and consumes the one-shot solo go-around RNG roll so a maneuver
  can't add a second roll. Without it a 360/S-turn on final advanced straight into `LandingPhase` far out, which descends
  at the category rate with no glideslope tracking → touchdown ~2,600 ft short. Defense-in-depth:
  `LandingPhase.ApplyGlidepathFloor` clamps the pre-flare descent target to the per-category glidepath altitude at the
  current distance (keep the `AltitudeAtDistance(dist, elev, category)` overload — don't hardcode 3° or a zero crossing
  height; the 6° helicopter path must survive and the floor has to be the same path FinalApproachPhase flew),
  releasing at `FlareEntryAgl`. Tests: `OakLandingShortAfter360Tests`, `MlsOnFinalResumesApproachTests`,
  `LandingPhaseGlidepathFloorTests`.
- **`JFAC`/`JLOC` is a *relaxed armed join*, distinct from `ForcedIntercept`.** `JFAC`/`JLOC` is usually appended to a
  vector (`FH 220, JLOC`) and must not deviate from it — the aircraft flies the assigned heading and joins the localizer
  at *any* cut when it intercepts, never busting through. `DispatchJfac` sets `InterceptCoursePhase.RelaxedJoin`, which
  bypasses the 30° `BustThroughAlignmentDeg` gate like `ForcedIntercept` but stays **passive** (no pre-LOC steering; turns
  only at capture) — keep the two flags distinct. Only `PTAC` expects a controller-limited intercept. **A bare same-approach
  `CAPP` on an established JFAC/JLOC join upgrades in place** via `CommandDispatcher.TryUpgradeLateralJoinInPlace` (early
  short-circuit before phase-clearing): it flips `LateralInterceptOnly` false and cancels the assigned speed
  (7110.65 §5-7-1) with no teardown/rebuild, so there is no spurious "…cancelled by CAPP" warning. `CAPPF`/`AT`/`DCT`/altitude/different-approach
  still rebuild. The glideslope-established bypass is scoped to `PTACF` via `ApproachClearance.ForcedInterceptCapture` (not
  the old steep-capture-angle proxy); `InterceptCaptureAngleDeg` now only feeds approach scoring.
