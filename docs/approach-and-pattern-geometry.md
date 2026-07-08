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

**Size/altitude resolution.** `ResolveAuthoredOverrides` (`PatternGeometry.cs:152`) composes a command override
(e.g. TPA/PSIZE) over the airport-authored `GroundRunway` data: command wins, then authored data fills in.
Authored `PatternAltitudeAglFt` is interpreted as **feet AGL above field elevation** and translated to MSL via
`runway.ElevationFt`. When a size override is applied, only `BaseExtensionNm` scales **proportionally** by
`patternSize / defaultSize` (a smaller pattern has a tighter base leg); the crosswind turn stays anchored at the
DER. The resolved size is carried on `PatternWaypoints.PatternSizeNm` so the downwind/base descent geometry uses
the actual offset, not the bare category default. Pattern altitude defaults to
`runway.ElevationFt + CategoryPerformance.PatternAltitudeAgl(category)`.

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

### Cross-runway closed-traffic departure — `BuildCrossRunwayDepartureCircuit`

`CTO MRT 28R` from runway 33 ("cleared for takeoff rwy 33, make right traffic rwy 28R") departs one runway and
joins the pattern of another. `DepartureClearanceHandler.ApplyClosedTraffic` detects this (pattern runway ≠
takeoff runway) and builds the first circuit via `PatternBuilder.BuildCrossRunwayDepartureCircuit`:
`UpwindPhase` (waypoints from the **departure** runway) → `MidfieldCrossingPhase` (`BiasTurnToPatternSide=true`,
waypoints from the **pattern** runway) → `DownwindPhase`/`BasePhase`/`FinalApproachPhase`/`TouchAndGoPhase`
(pattern runway). Per AIM 4-3-2 the departure/upwind leg belongs to the departure runway; downwind/base/final
belong to the landing runway. The departure runway is carried on `PhaseList.DepartureRunway` (read by
`LineUpPhase`/`LinedUpAndWaitingPhase`/`TakeoffPhase`), while `AssignedRunway`/`PatternRunway` hold the pattern
runway (read by the circuit/final/landing phases). Subsequent circuits auto-cycle entirely on the pattern runway
(`BuildNextCircuit`, which reads `PatternRunway ?? AssignedRunway`). `MidfieldCrossingPhase.BiasTurnToPatternSide`
forces the initial join turn toward the assigned side (released once roughly pointed at the join target); it is
left `false` for arrival / wrong-side joins so their established shortest-turn behavior is unchanged.

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
- **Past-abeam descent target** (`ApplyPastAbeamDescentTargets`, `DownwindPhase.cs:379`): for a normal pattern,
  the mid-altitude target is **60 % of the way** from threshold elevation up to pattern altitude
  (`thresholdElev + (TPA − thresholdElev)·0.6`); the **altitude floor** for an extended downwind is the
  glideslope-intercept altitude at the diagonal distance `sqrt(patternSize² + baseExt²)` from the base-turn point
  to the threshold, via `GlideSlopeGeometry.FeetPerNm`.
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
   exceeds the along-track cap the helper returns `null` and the fixed fallback (path 3 below) handles the
   geometry. The `PatternEntryPhase` descends to the glideslope altitude at the join (`PatternCommandHandler.cs:454`),
   then `FinalApproachPhase` tracks the 3° slope inbound.
3. **Fixed fallback**: aligned aircraft *outside* the close-in distance (and explicit-distance `EF`) keep the
   fixed glideslope-TPA entry (`PatternAltitudeAgl / FeetPerNm`) plus the loop-feasibility check.

**Short-final guards (both return before the phase teardown, so the aircraft keeps its current approach — #228).**
An aircraft on very short final is *inside* the fixed entry point, so paths 1–3 would otherwise place the fixed
entry behind it and fly it outbound to re-enter (the "tour of the airspace" bug):
- **Same-runway continue (no-op):** if the aircraft is already in `FinalApproachPhase` for the requested runway,
  aligned + near the centerline + inside the standard entry distance, `TryEnterPattern` returns success
  ("continuing final") without rebuilding — preserving the live final / glideslope / clearance state
  (`PatternCommandHandler.cs`, right after the parallel-sidestep branch).
- **Never-outbound reject:** the loop-feasibility block also rejects "Unable, short final" when the aircraft is on
  the final approach course, genuinely short final (inside `MinimumPerpendicularBaseFinalDistanceNm`), and the
  fixed entry point lies farther outbound than the aircraft. A runway argument that already retargeted
  `AssignedRunway`/`DestinationRunway` is restored on this reject. This is the backstop for the cases the no-op
  doesn't cover (not on `FinalApproachPhase`, or a non-parallel runway switch from short final).

When the altitude-aware join is capped at along-track but the aircraft still cannot lose its altitude over the
cut-in + final at the category `PatternDescentRate`, `EF` succeeds but raises an `AircraftState.PendingWarnings`
entry ("unable to descend for straight-in … — too high") — a controller-facing advisory, not radio phraseology.
The angle envelope is an airmanship analogy to TBL 5-9-1, not a regulatory VFR-pattern mandate (AIM 4-3-3 does not
quantify an intercept angle).

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
> while the aircraft may still be up to 30° off the FAC; `FinalApproachPhase` does **not** start the glideslope
> descent (`_gsCaptured`) until the aircraft is laterally established — within `GsEstablishedHeadingDeg = 5°` of the
> FAC **and** within `GsEstablishedCrossTrackNm = 0.15 nm` of centerline (`IsLaterallyEstablishedForGs`). This
> reproduces "maintain until established on the localizer, cleared ILS" (AIM 5-4-7 / 7110.65 5-9-4): the aircraft
> holds the assigned altitude through the turn-on, then descends. The gate is bypassed when there is no approach
> clearance (pattern/visual turning final), for pattern traffic, for visual approaches (`VIS` prefix), and for
> forced intercepts (`InterceptCaptureAngleDeg > 30°`, which intentionally S-turn back and would otherwise be
> stranded high). The 91.117 250-kt cap and the level-off-then-intercept-from-below logic are unchanged.

**The three heading-diff checks (mag-var tolerance).** The capture/bust-through decision must tolerate the gap
between the published FAC, the runway-number heading, and the controller's assigned magnetic heading. A two-way
comparison reintroduces a false bust-through bug, so the effective diff
(`ComputeEffectiveHeadingDiff`, `InterceptCoursePhase.cs:231`) takes the **minimum** of:

1. **Aircraft true heading vs FAC** — `aircraftHeading.AbsAngleTo(FinalApproachCourse)`.
2. **Aircraft true heading vs runway-number heading** — derived by regex from `ApproachId` (`I12 → 120°`,
   `ILS28R → 280°`, `L04L → 40°`, `GetRunwayHeading`/`RunwayDesignatorRegex` at `InterceptCoursePhase.cs:338`/360),
   falling back to FAC if unparseable.
3. **Assigned magnetic heading vs runway-number heading** — both magnetic, so mag variation cancels (e.g. rwy 12
   at 150° mag: true heading ~163° vs FAC 130° = 33° fails, but assigned 150° vs rwy 120° = 30° passes).

In the anticipation branch, check #3 is gated on the aircraft having actually *reached* the assigned heading
(within 5° via `onAssignedHeading`) so a mid-turn aircraft isn't waved through prematurely
(`InterceptCoursePhase.cs:146`).

**The 30° bust-through gate.** `BustThroughAlignmentDeg = 30.0` (`InterceptCoursePhase.cs:41`). If the aircraft
crosses the centerline with the effective diff > 30°, `HandleBustThrough` clears the approach phases and
`ActiveApproach` and notifies "Unable, passing through localizer" (`InterceptCoursePhase.cs:363`). The same fires
on the `MaxElapsedSeconds = 180` safety timeout (flying parallel and never crossing).

**`ForcedIntercept` (PTACF / implied-PTAC in CAPPF).** When `ForcedIntercept` is set, `maxAlignmentDeg` is raised
to **180°**, which makes the bust-through branch unreachable: the aircraft captures the FAC at *any* angle,
overshoots laterally, and S-turns back under `FinalApproachPhase`. Don't assume the 30° gate is always active.

**Parallel-offset anchor.** For parallel-offset approaches (e.g. KDCA LDA-X 19, KCCR S19R) the FAC line does not
pass through the threshold. The phase measures cross-track against `ActiveApproach.FinalApproachAnchorLat/Lon` when
set, falling back to the threshold otherwise (`InterceptCoursePhase.cs:94`). `FinalApproachPhase` uses the same
anchor. Code that hard-codes the threshold breaks offset approaches.

**Speed anticipation.** Inside `SpeedAnticipationThresholdNm = 2.0` the phase decelerates to `1.3 × FAS`
(`InterceptSpeedFasMultiplier`), not FAS itself — at 250 kt the turn radius is too large and would overshoot
(`InterceptCoursePhase.cs:106`). `FinalApproachPhase` bleeds the rest to Vref closer in — at a **per-aircraft
distance**, not a fixed one (see the FAS-reduction-variety note below).

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

- **45°-offset leg**: after crossing the fix the aircraft flies the radial outbound (`InboundCourse + 180°`), then
  turns to the published `PtOutboundCourseDeg` (the 45° leg) once established and clear of the fix
  (`MinOutboundSeparationNm = 1.0`).
- **Distance cap with reserve** (`ProcedureTurnPhase.cs:144`/198): the 180° turn back to inbound begins when
  `distFromFix ≥ MaxOutboundDistanceNm − TurnRadiusReserveNm`, where `TurnRadiusReserveNm = 2.0`. Turning back
  early by the reserve keeps the **180° turn radius itself** inside protected airspace (AIM 5-4-9.a.3). The cap is
  checked on both the radial-outbound and PT-outbound legs.
- **200 KIAS clamp** (`ClampPtSpeed`, `ProcedureTurnPhase.cs:293`): `MaxPtIasKts = 200` is applied via
  `ControlTargets.SpeedCeiling` for the whole phase (AIM 5-4-9.a.3).
- **Lateral-intercept gate** (`TickInterceptInbound`, `ProcedureTurnPhase.cs:253`): the phase does not hand off
  until the aircraft is both heading-aligned *and* within `InterceptLateralToleranceNm = 1.0` cross-track of the
  inbound course — heading-only would pass a 5°-aligned aircraft with a 2 nm cross-track error to FinalApproach.
- The minimum altitude (`MinAltitudeFt`, from the PI leg's `AtOrAbove`) is held throughout, and the PT outbound
  leg continues until both the timer (`DefaultPtOutboundSeconds = 60`) expires *and* the altitude is met.

## Glideslope geometry — `GlideSlopeGeometry`

`GlideSlopeGeometry` (`src/Yaat.Sim/Phases/GlideSlopeGeometry.cs`) is the shared descent-profile helper used by
approach navigation, downwind/base descent, and final approach. Constants and helpers:

- `StandardAngleDeg = 3.0`, `HelicopterAngleDeg = 6.0`; `AngleForCategory(category)` returns 6° for helicopters,
  else 3°.
- `FeetPerNm(angle)` = `tan(angle) · 6076.12` (≈ 318 ft/nm for 3°).
- `AltitudeAtDistance(distNm, thresholdElevation, angle)` = `thresholdElevation + tan(angle) · distNm · 6076.12`.
- `RequiredDescentRate(groundSpeedKts, angle)` = `groundSpeedKts · tan(angle) · 101.269` (≈ GS × 5.3 fpm for 3°).

## Visual following — `AirborneFollowHelper` + `VfrFollowPhase`

`AirborneFollowHelper` (`src/Yaat.Sim/Phases/AirborneFollowHelper.cs`) provides speed/timing adjustments for any
aircraft with `Approach.FollowingCallsign` set. Pattern phases (`DownwindPhase`, `BasePhase`, `PatternEntryPhase`)
and `VfrFollowPhase` call into it each tick.

**Desired spacing** scales with the leader's category. Pattern-tight (`DesiredDistanceForLeader`,
`AirborneFollowHelper.cs:689`): Jet 3.0 / Turboprop 1.5 / Piston/Heli 1.0 nm. Free-flight (wider, used before the
follower is established on a leg, `FreeFlightDistanceForLeader`, `AirborneFollowHelper.cs:706`): Jet 3.5 / TP 2.0 /
Piston/Heli 1.5 nm. The jet 3.0 nm matches the FAA 7110.65 §5-5-4 same-runway radar minimum.

**Speed adjustment** (`ComputeAdjustedSpeedWithDesired`): the correction is
`(distance − desired) × SpeedGainPerNm (25 kt/nm)`, clamped to `±maxSpeedAdjustKts`. Pattern/entry/free-flight use
`MaxSpeedAdjustKts = 20`; final approach uses the tighter `MaxSpeedAdjustFinalKts = 10` so the follower can't blow
through the unstabilized-go-around gate (IAS > 1.3·Vref). The pattern-leg
phases gate this block on `Approach.FollowingCallsign` (not on `TargetSpeed` — physics snaps `TargetSpeed` to null
once the leg speed is reached, which would otherwise silently stop spacing for a settled follower) and **cap the
result at the leg baseline** (`Math.Min(adjusted, baseline)`): spacing only ever *slows* a follower below its leg
speed, never accelerates it above to chase a far lead — a too-far lead is handled laterally (extend / hold base turn).

**At-min-speed: extend, don't cut in.** When the follower is at min speed and inside half the desired distance, it
cannot open the gap by slowing any further. If the lead is **pattern-flow-ahead** (`IsLeadPatternFlowAhead` — same
runway, strictly later leg or an EXTENDED same leg) the follower still has a *lateral* option, so the helper returns
`minSpeed` and **does not cancel**: `DownwindPhase` then holds the base turn (`ShouldExtendDownwind` /
`ShouldHoldForLeadSequencing`) and extends until `CheckLeadLifecycle` releases the follow when the lead lands. Only
when there is no lateral option (free-flight follow, or a same/earlier-leg lead) is the follow cancelled with an
"unable to maintain separation" warning. Cancelling on a flow-ahead lead used to clear `FollowingCallsign`, drop the
hold, and turn the follower base *in front of* a still-airborne straight-in — a §3-10-3.a.1 same-runway separation
bust (a light twin, SRS Cat II, has **no** reduced-distance provision behind a Cat III jet: the jet must be on the
ground and clear before the follower crosses the threshold) and an AIM §4-3-4.b.4 cut-in.
Regression: `N342TFollowStraightInDownwindTests`.

**Structural-overtake break-off + go-around** (`ShouldBreakOffFollowForSpacing`): a follower whose own Vref exceeds
the lead's ground speed by more than `StructuralOvertakeMarginKts = 10` can never open the gap by slowing — speed
control alone is futile (e.g. a C210 told to follow a 56-kt C152). When that follower, on `BasePhase` or
`FinalApproachPhase`, closes inside `FollowBreakOffGapNm = 0.8` nm of the still-airborne lead while the pair is
still converging (`IsClosing`, range-rate < 0), it breaks off the follow and goes around — clearing the follow state
and triggering a go-around with an "unable to maintain separation" transmission — rather than overflying the lead it
was told to follow (AIM 4-3-3 NOTE 1; 0.8 nm sits above the same-runway 3,000 ft ≈ 0.5 nm Cat I minimum of 7110.65 §3-10-3). The
check runs *before* the speed block so it pre-empts the at-min-speed cancel above, which would only clear the follow
without going around.

**The pattern-leg-index ordering** (`PatternLegIndex`, `AirborneFollowHelper.cs:254`) is hard-coded:

`PatternEntryPhase = 0`, `Upwind = 1`, `Crosswind = 2`, `Downwind = 3`, `Base = 4`, `FinalApproach = 5`,
`Landing/TouchAndGo = 6`; non-pattern phases return null.

`IsLeadPatternFlowAhead` (lead strictly later leg — **plus the same-leg case where the lead is extending**)
and `IsLeadPatternFlowBehind` (lead earlier leg, or same leg broken by phase `ElapsedSeconds`) use this index, gated
on both aircraft being on the **same runway**:

- **`IsLeadPatternFlowBehind`** ⇒ the spacing helper returns the baseline (don't slow down for a lead that hasn't
  reached the follower's leg yet — pulling the follower to Vref produces multi-minute downwind extensions).
- **`IsLeadPatternFlowAhead`** ⇒ the follower may extend its leg to sequence, and the at-min-speed cancel in the
  speed loop is suppressed (it holds `minSpeed` and extends instead of cutting in — see **At-min-speed** above).
  - **Extended-leg exception:** a lead on the **same leg** but with `IsExtended` set by `EXT` (`IsExtendedPatternLeg`
    — Downwind, Crosswind, or Upwind) also counts as flow-ahead. The lead has explicitly deferred its progression, so
    it stays ahead in the landing sequence despite sharing the follower's leg index. Without this, a follower on the
    same downwind turned base at its fixed point and rolled out on final ahead of the aircraft it was told to follow.
    Generic same-leg pairs are still *not* flow-ahead — a lead merely outpacing the follower has not deferred its
    place in the sequence, so the follower keeps normal spacing rather than extending behind it.

**The lead lifecycle** (`CheckLeadLifecycle`) cancels a follow when:

1. the lead is no longer in the world (lookup returns null),
2. the lead has gone `IsOnGround`, or
3. the follower **loses visual contact** with the lead.

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

**Downwind extension caps.** `ShouldExtendDownwind` (proximity) holds the base turn
when the follower is inside `desired × 0.6`. `ShouldHoldForLeadSequencing` holds
while the follower's 1-D along-track sequence coordinate would not roll it out at least the desired distance behind
a leg-ahead lead. `DownwindPhase` bounds this hold **spatially** with `MaxFollowExtensionNm = 4.0`; the **temporal**
bound is the lead lifecycle (the lead lands, despawns, or is lost from sight). The two caps are complementary, not
redundant.

**Upwind/crosswind sequencing — remaining pattern path.** The downwind along-track hold does not transfer to the
`UpwindPhase`/`CrosswindPhase` legs: the upwind leg's along-track runs *opposite* the downwind sequence axis (its
heading is the runway heading, the reciprocal of downwind), so "further along upwind" means a *longer* path to
landing, not a shorter one — a follower that is behind the lead on upwind is geometrically committed to a *shorter*
downwind and would roll out on final *ahead* of the traffic it was told to follow. To sequence correctly on every
leg, `RemainingPatternPathNm` (`AirborneFollowHelper`) computes the remaining circuit distance to the threshold
(upwind → crosswind → downwind → base → final); it is monotone toward landing and *increases* when a leg is extended
(a longer upwind lengthens the downwind; a wider crosswind lengthens the base; a longer downwind lengthens the
final — the downwind term uses the aircraft's **actual** perpendicular offset so a widened pattern counts its longer
base). `ShouldHoldLegForRemainingPathSequencing` holds the current leg while `remaining(follower) < remaining(lead)
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
4-3-2.a.3.2 (the upwind leg is an explicit separation/sequencing leg) and 5-5-12.a.1 / 4-3-5 (the follower
maneuvers as necessary and advises ATC — it does not fly an *unrequested* turn on its own).

**Cross-runway FOLLOW re-sequences onto the lead's runway** (`CommandDispatcher.TryAirborneFollow` +
`IsLeadOnDifferentRunway`). Every spacing / leg-hold path above is gated on both aircraft sharing a runway, so a
follower flying a 28L pattern told to follow traffic landing 28R used to tag the target and sequence against
nothing. Instead, when the lead's `Phases.AssignedRunway` differs from the follower's, the follower's (wrong-runway)
pattern is dropped and a `VfrFollowPhase` is installed — its auto-join (`TryJoinLeadPattern` / `TryJoinLeadFinal`)
rebuilds the circuit or final on the **lead's** runway with the usual in-trail and intercept gates. Same-runway (or
unknown-runway) FOLLOW keeps the cheap in-place retarget.

**...but not from base or final.** The re-sequence is refused when the follower is already on `BasePhase` or
`FinalApproachPhase` ("Unable, established for runway {rwy} — vector or go around"). From there the follower is low
and close in, and swinging it onto a closely-spaced parallel would fly a low crossing of its original runway's final
approach course (AIM §4-3-3 FIG 4-3-3 note 7 — do not penetrate the parallel's final; §4-3-5 — no unexpected pattern
maneuvers). The controller re-sequences explicitly (`ELB`/`ERB`), vectors, or sends it around. Re-sequencing from
upwind / crosswind / downwind / pattern-entry is allowed.

> **Known gap (closely-spaced parallels).** `TryJoinLeadFinal` has no pattern-side gate (unlike `TryJoinLeadPattern`)
> and commits at up to `MaxFinalJoinCrossTrackNm = 1.0` nm cross-track — ~11× the 530 ft (0.087 nm) 28L/28R spacing —
> so an opposite-side follower can still intercept through the adjacent runway's final. 7110.65 §5-9-2 TBL 5-9-1 also
> caps intercepts at 20° (not 30°) inside 2 mi of the gate. Pre-existing for any straight-in join; the base/final
> refusal above removes the worst (low, close-in) case.

### `VfrFollowPhase` — free pursuit + auto-join

`VfrFollowPhase` (`src/Yaat.Sim/Phases/Pattern/VfrFollowPhase.cs`, built by `CommandDispatcher` for FOLLOW) keeps a
trail behind the lead — steering relative to the lead's **ground track**, not its instantaneous position — and matches
the lead's speed with free-flight spacing, leaving altitude untouched.

**Lateral trail-keeping — `AirborneFollowHelper.ComputeFreePursuitHeading`.** Each free-pursuit tick (after the speed
loop) the heading comes from a three-regime law keyed on `behindNm` (along-track gap behind the lead, from
`lead.TrueTrack`) vs the free-flight `desiredNm`, with a `TrailRegimeDeadbandNm = 0.3` deadband: **Approaching**
(gap > desired) lag-pursues an anchor `desiredNm` behind the lead (`ProjectPoint(lead.Position, TrueTrack.ToReciprocal(),
desiredNm)`), curving into trail; **Established** (within deadband) parallels `lead.TrueTrack` with a bounded cross-track
capture (`clamp(-crossNm × 30, ±12°)`) — the nose no longer points at the lead; **Too close** (gap < desired) parallels +
captures while the speed loop opens the gap, but if speed is saturated at the approach-speed floor it makes a sticky
±18° widen excursion toward the follower's offset side (hysteresis via `FollowWidenState`, serialized on
`VfrFollowPhaseDto`) until the gap recovers. Below `TrailMinLeadGroundSpeedKt = 35` the lead's track is unreliable, so it
degrades to pure pursuit (pointing at the lead). AIM §5-5-12.a.1 / §4-3-5.

When the lead is in a pattern, `TryJoinLeadPattern` (`VfrFollowPhase.cs:111`) rebuilds the follower's phase list with a
full circuit copied from the lead's runway/direction/altitude, gated on **three** conditions:

1. follower within `JoinRangeNm = 3.0` of the lead's downwind abeam point,
2. follower within `MaxJoinGapNm = 5.0` of the lead itself (guards against a stale pattern), and
3. follower on the **pattern side** of the runway centerline (`IsOnPatternSide`, `VfrFollowPhase.cs:315` — uses the
   positive-is-right cross-track convention).

On join it preserves `FollowingCallsign` so the pattern phases keep adjusting spacing, and skips `PatternEntryPhase`
if the follower is already established on the downwind leg.

**Straight-in lead — `TryJoinLeadFinal`.** When the lead is on a straight-in final/landing to a known runway but
has *no* pattern-leg waypoints to copy (e.g. an IFR aircraft that spawned directly onto `FinalApproachPhase`),
`TryJoinLeadPattern` returns null and `TryJoinLeadFinal` takes over. It sequences the follower onto that runway's
final once the in-trail spacing (`followerDist − leadDist`) is at least `requiredInTrail = max(SameRunwayInTrailFloorNm
= 1.5, WakeTurbulenceData.OnApproachWakeSeparationNm(lead → follower))` and the follower is aligned for a sane
intercept (`|cross-track| ≤ MaxFinalJoinCrossTrackNm = 1.0`, `intercept ≤ MaxFinalJoinInterceptDeg = 30°`, not inside
`MinFinalJoinDistNm = 0.5`). The in-trail floor keeps the follower genuinely behind the traffic (AIM §4-3-4.4 — no
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

The lead's runway is captured each tick into `_leadLandingRunway` (serialized in `VfrFollowPhaseDto`) while the
lead is airborne on final/landing, so if the lead touches down before the follower has rolled onto final, the
follower is still sequenced onto that runway's final (the lead-landed fallback in `OnTick`) instead of cancelling
the follow and levelling off over the field.

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
**precomputes per-(airport, runway) minimum intercept distances** from CIFP FAF positions at init:

```
approachGate  = max(FAF→threshold + 1nm, 5nm)
minIntercept  = approachGate + 2nm        (default 7.0nm when no data)
```

The **max intercept angle** is *not* precomputed here — `FinalApproachPhase` derives it at establishment from the
capture distance, reconstructing the gate as `minIntercept − 2nm` (`FinalApproachPhase.cs:953`–955):

```
distToGate = captureDistNm − approachGate
maxAngle   = (distToGate < 2nm) ? 20° : 30°    // TBL 5-9-1
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
| **PTAC** (present-heading intercept) | `ApproachCommandHandler.TryPtac` (`:395`) | `InterceptCoursePhase` (`ForcedIntercept = cmd.Forced`) → `FinalApproachPhase` → landing |
| Missed-approach hold | `ApproachCommandHandler` (`:1053`) | `ApproachNavigationPhase` → `HoldingPatternPhase` |
| **Pattern** (TPAT / pattern legs, SA/MNA/TB/EXT/OFL/OFR) | `PatternCommandHandler` (`:462`, `:486`) | `PatternEntryPhase` / `MidfieldCrossingPhase` / `TeardropReentryPhase` + circuit legs |
| **EF** (enter final) — parallel sidestep | `PatternCommandHandler.TryEnterPattern` → `ApplySidestep` | none — retargets the **active** `FinalApproachPhase` in place (`RetargetRunway`); only when the target is a parallel of the runway the aircraft is on final for, ≥ `MinSidestepAglFt` |
| **EF** — same-runway short-final continue | `PatternCommandHandler.TryEnterPattern` | none — redundant re-clearance while established on final inside the entry point returns "continuing final" and leaves the live phases untouched (#228) |
| **CVA** / **CVAF** (cleared visual approach; IFR-only; `Force` bypasses the RFIS field-in-sight gate) | `ApproachCommandHandler.TryClearedVisualApproach` (`:459`) | by angle off final: straight-in `FinalApproachPhase` (≤30°); angled-join `ApproachNavigationPhase` (one `INTCP` fix) → `FinalApproachPhase` (30–90°); IFR-visual pattern `PatternEntryPhase` → Downwind → Base → `FinalApproachPhase` (>90°) → landing. `ApproachId = "VIS<rwy>"`. Acquisition gate (§7-4-3.a): following → requires `HasReportedTrafficInSight` (and lead not a super); otherwise → requires `HasReportedFieldInSight`. `Force` (CVAF) sets the required flag |
| **FOLLOW** / **FOLLOWF** (VFR-only; `Force` bypasses the RTIS traffic-in-sight gate) | `CommandDispatcher.TryAirborneFollow` (`:2758`) | `VfrFollowPhase` |
| Holding (HOLD) | `NavigationCommandHandler` (`:876`) | `HoldingPatternPhase` |

## Footguns & pitfalls

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
  for this reason.
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
- **Pattern phases set `ManagesSpeed = true`; approach phases do NOT.** `DownwindPhase`, `BasePhase`,
  `PatternEntryPhase`, `TeardropReentryPhase`, and `VfrFollowPhase` all override `ManagesSpeed` to `true`, so
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
  current distance (keep `GlideSlopeGeometry.AngleForCategory` — don't hardcode 3°; the 6° helicopter path must survive),
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
