# Off-graph ground spawn + multi-turn taxi robustness

## Status

**Branch:** `worktree-declarative-scribbling-lake` (at `X:\dev\yaat\.claude\worktrees\declarative-scribbling-lake`)

**Committed on this branch** (newest last):

- [x] `f1cdfed` — refactor: `PathPrimitiveSlowTurn` primitive added (low-speed tight-arc maneuvers, nose-wheel max deflection, 25 ft radius)
- [x] `f8c608f` — fix #142: graph-follow `LineUpPhase` with pivot fallback (`LineUpGeometry` module classifies pose as Aligned / Pivot / Fault)
- [x] `eaf5612` — refactor: extract `GeoMath.FootOfPerpendicular` + `GeoMath.SegmentsIntersect`
- [x] `1da6715` — feat: pure-pursuit steering on straight taxi segments (`GroundNavigator.TickStraight` steers toward a look-ahead point on the segment line instead of bearing-to-target-node, so off-segment aircraft converge onto the line)
- [x] `b7f3d38` — feat: ingress segment for off-graph taxi/pushback *(superseded — see revert below)*
- [x] `5261ff9` — feat: 1:1 aircraft silhouette in `Yaat.LayoutInspector` tick overlay (`--tick-aircraft-length-ft`, `--tick-aircraft-wingspan-ft`; fuselage + wings + tailplane drawn at feet-to-pixels scale; 10 px floor)
- [x] `4ff7a39` — revert `b7f3d38` (ingress resolver) — rationale in commit message
- [x] `dc12009` — feat: snap off-graph ground spawns onto nearest taxi edge (`GroundSpawnSnap` + `AirportGroundLayout.FindNearestTaxiEdge`, runs in `ScenarioLoader` after heading derivation, before any tick)
- [x] `73786a8` — chore: add M2 multi-turn test + LI opacity tweak + this handoff plan
- [x] **Item 1 (this commit)** — feat: navigator lookahead tangent-entry slow-turn synthesis (see "Item 1 — done this session" below)

## Context

This started from issue #142 (shallow LineUp at SFO 01R). Fixing #142 revealed several layers of upstream problems in how off-graph ground spawns get routed onto the taxi graph. The chain of investigations and fixes landed the commits above. The work is **not done** — the M2 multi-turn taxi test uncovered deeper navigator-robustness gaps that motivated rethinking the original approach (see "Current open pathology" below).

**Key scenario that drives this work:** SFO-2 VFR Transitions preset spawns UAL859 at `(37.606822, -122.382064)` heading `104°` magnetic (≈ 117.85° true) — 35 ft SW of graph node 2185 — then fires `TAXI A1 1R` as a preset command. There's no graph edge connecting the spawn point to the graph, so the pathfinder's start-node approach caused the aircraft to cut diagonally across terrain.

## What worked this session

### 1. Ground spawn snap (`dc12009`) — replaces the earlier ingress resolver approach

`GroundSpawnSnap.Apply` runs at **scenario load time**, before any tick fires, so a paused-at-load scenario displays the snapped pose from the start (no visible teleport on first play). It:

- Finds the nearest **straight** taxi edge via `AirportGroundLayout.FindNearestTaxiEdge` (filters out `GroundArc` fillets, runway centerlines, ramp connectors).
- Snaps aircraft position to the foot-of-perpendicular on that edge.
- Rotates heading to the edge bearing direction closest to the original heading (out of the two directions along the edge).
- Skips aircraft that are airborne (`IsOnGround == false`) or more than 200 ft from any eligible edge (logs a warning in that case — surfaces scenario-author typos).

Hooked into `ScenarioLoader.LoadAircraft` at the "Coordinates" / "FixOrFrd" ground-spawn branch, **after** heading derivation so the scenario's intended heading is the tiebreaker for which edge direction to pick.

**UAL859 result:** takeoff phase starts at t=71 (vs t>120 before), airborne at t=98. Aircraft taxis the full 74 ft of A1 at heading 117.85°, holds short aligned with A1 (heading 117.85°), LineUpPhase picks the Pivot path cleanly.

7 unit tests in `tests/Yaat.Sim.Tests/GroundSpawnSnapTests.cs` cover edge filtering (arc/runway/ramp exclusion), foot-of-perpendicular snap, heading rotation (forward and reverse edge directions), airborne skip, and beyond-threshold no-op.

### 2. LayoutInspector 1:1 silhouette (`5261ff9`)

`--tick-aircraft-length-ft` / `--tick-aircraft-wingspan-ft` (defaults 110 ft) render a fuselage + wings + tailplane silhouette at feet-to-pixels scale. Makes ground-ops debugging much easier — the aircraft's wingtips and nose show where it actually is relative to taxiway edges, runway hold-shorts, and parked traffic. (The inspector-template opacity tweak is a follow-up on this commit.)

## What failed and why

### Ingress resolver (`b7f3d38`, reverted as `4ff7a39`)

The first attempt was to prepend a **virtual ingress segment** (or replace the first route segment with a mid-edge foot-of-perpendicular) in `TryTaxi` / `TryPushback`:

```csharp
IngressPlan plan = TaxiIngressResolver.Resolve(layout, startNode, acLat, acLon, firstRouteSegment);
TaxiIngressResolver.Apply(route, plan);
```

This solved the cross-terrain diagonal but **introduced its own failure mode**: the aircraft would arrive at the ingress segment's end aligned with the ingress bearing (58° in UAL859's case), then need to realign onto the first real taxi segment. When that first segment is short (the 19 ft virtualHS past node 2185 on A1), the aircraft couldn't rotate enough to match the taxi bearing (118°) before the virtual HS triggered, and LineUpPhase inherited a degenerate pose.

Side-by-side trace confirmed the regression:

| State | HS hdg | LineUp duration | Takeoff begins |
|---|---|---|---|
| f8c608f + pure-pursuit (no ingress) | 64.86° | 27 s | t=80 |
| +ingress (`b7f3d38`) | **47.69°** | **46 s** | t=99 |

The ingress approach papered over the symptom (diagonal terrain-cutting) with a new primitive that had its own short-edge problems. The snap approach (`dc12009`) addresses the root cause (off-graph start pose) directly without introducing intermediate segments.

### Along-track arrival in `TickStraight` (unwritten attempt)

While the ingress resolver was committed, a follow-up attempt aimed to replace `GroundNavigator.TickStraight`'s overshoot watchdog (`distNm > PrevDistToTarget && PrevDistToTarget < OvershootDetectionNm`) with an along-track-progress check using `GeoMath.FootOfPerpendicular`:

```csharp
var (_, _, alongNm, _) = GeoMath.FootOfPerpendicular(ac, segFrom, target);
bool alongTrackArrived = alongNm >= edgeLengthNm - arrivalThresholdNm;
```

**Failed** because `alongTrackArrived` fires whenever the aircraft's perpendicular projection onto the segment line is past the target — **regardless of how far off-segment the aircraft physically is**. An aircraft mid-turn with a large perpendicular offset to the next segment would trigger false arrivals. Five tests regressed (`IssueAmxTaxiOvershootTests.AMX669_HoldsShortOf1L_WithReasonableHeading`, two `Issue133Rwy28rTakeoffTests.N172SP_*`, two `DiagonalLineup28rTests.*`). An additive approach (keep overshoot watchdog, add along-track as supplemental) made all tests green but provided only marginal improvement — the mechanism was redundant with the existing overshoot in the cases that mattered.

**Decision:** dropped. The user's guidance was "I was hoping for something more robust than our previous overshoot detection, but not these failed attempts" — meaning arrival detection is a separate future improvement, not a band-aid for the off-graph spawn problem. Deferred.

## Original pathology — addressed by Item 1 (kept for historical context)

`SfoM2MultiTurnTaxiTests` exercised this: spawn an aircraft ~20 ft off M2 at SFO node 1529 area, issue `TAXI M2 A A1 1R` + `CTO 1R`. The snap works, the aircraft taxis M2 → A correctly, but at the A1 bend around node 507 it used to:

1. **Not use the `2185/2186` fillet arc** — the route is constructed as 14 segments including `2186 → 507 → 2185` as two consecutive **straights through the apex** of the A1 bend (see seg 11 + seg 12 in the runtime log). The fillet arc `2186 → 2185` (`TaxiwayNames=[A1]`, radius 74 ft, length 0.0194 nm) is in the graph but ignored by the pathfinder. **Item 2 addresses this root cause.**
2. **Overshoot the 90° turn at node 507** — aircraft enters the bend at the `cornerSpeed(90°) = 15` kt constraint, but physics (`GroundTurnRate = 20°/s`) gives a natural 72 ft turn radius. The 507→2185 segment is only 75 ft. Physics cannot complete the turn in the available space. **Item 1 fixed this.**
3. **Spiral around node 877** — after overshooting, the aircraft ends up past node 2185 with target 877 (the 1R hold-short, 74 ft further along A1). Navigator steers toward 877 but can never slow to 0 within the tight turn radius, so it orbits at taxi-corner speed for 30+ seconds before LineUp finally fires with a degenerate pose. **Item 1's lookahead synthesis prevents this by engaging a tangent-entry arc before the corner at geometry-appropriate radius/speed.**

### Root causes identified

**Cause A — `TaxiPathfinder.WalkTaxiway` unconditionally prefers straights over arcs** (`src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs:727-749`):

```csharp
// Prefer straight edges over junction arcs — arcs are for transitions between taxiways,
// not for continuing along the same taxiway.
var candidateEdges = straightCandidates.Count > 0 ? straightCandidates : arcCandidates;
```

The comment's claim is only half-true. `FilletArcGenerator` at `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs:536-542` intentionally creates **same-taxiway fillet arcs** (like 2186↔2185 at the A1 bend around node 507) in addition to cross-taxiway transition arcs. The pathfinder's blanket "prefer straight" rule discards same-taxiway fillets whenever a straight exists on the taxiway at that node.

**Cause B — Navigator has no SlowTurn synthesis** (`src/Yaat.Sim/Phases/Ground/PathPrimitiveBuilder.cs:44-60`): one segment → one primitive. No logic inserts a `PathPrimitiveSlowTurn` between two consecutive straights when the heading change exceeds what `CornerSpeedForAngle` + physics can handle in the available downstream length. `PathPrimitiveBuilder.BuildSlowTurn` exists but is only called from `LineUpPhase`.

The navigator **does** lookahead (`BuildSpeedConstraints` at `GroundNavigator.cs:535-611` forward-walks the route, computes per-turn speed constraints, backward-propagates kinematic decel). It just uses a speed-table model that assumes a fillet arc will be present to actually execute the turn. When the arc is missing (Cause A), the speed table alone cannot save the turn.

**Cause C — `Yaat.LayoutInspector --pathfinder` diverges from runtime** (`tools/Yaat.LayoutInspector/Commands/QueryCommand.cs:108-114`): LI's invocation of `TaxiPathfinder.ResolveExplicitPath` doesn't pass `DestinationRunway`, `ExplicitHoldShorts`, or `AirportId`. Runtime (`GroundCommandHandler.cs:246-257`) does. Without `DestinationRunway`, `WalkTaxiway`'s `effectiveHint` logic (lines 762-799) never fires, and `PickBestStartEdge` picks arbitrary direction at multi-edge junctions like node 1331 (both A1 edges ambiguous). Not a pathfinder bug — just missing CLI flags. **This matters because it masked the real bug** — LI showed a different wrong answer, making it look like an LI quirk rather than pathfinder evidence.

## Plan for next session (ordered)

### Item 1 — Navigator SlowTurn synthesis — **DONE this session**

Implemented not as at-node synthesis (original sketch) but as **lookahead tangent-entry synthesis**: the aircraft slows and begins the turn *before* the corner node, arcs tangent to the incoming segment, passes through the corner vicinity, and exits tangent to the outgoing segment. The original at-node sketch (which would have orbited inside the corner starting at node arrival) was superseded once a trajectory render showed the aircraft "realising" the corner only at node arrival — visibly un-pilot-like.

**Final design** (in `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs`):

1. **`PlanSynthesisLookahead`** (called from `SetupSegment`) forward-scans the route (capped at 500 ft) for the next transition needing synthesis. Criteria: turn angle ≥ 45° AND natural arc length > `0.5 × postCornerSegmentLength`.
2. When found, walks **backward** through collinear straight segments (bearing diff < 5°) accumulating incoming room.
3. **Dynamic radius/speed**: picks the largest radius that fits `0.8 × min(availIncomingFt, availOutgoingFt) / tan(θ/2)`, floored at `NoseWheelTurnRadiusFt`. Speed derives from `v = r × ω` (same `GroundTurnRate` the `CornerSpeed` calibration uses, so lateral acceleration stays consistent with natural turns), clamped to `[SlowTurnSpeedKts, CornerSpeedForAngle(θ)]`. For SFO M2 (both sides 75 ft): r = 60 ft, v = 12.4 kt — effectively a soft fillet. Only pathological geometry drops to the 3 kt / 25 ft floor, which emits a Warning.
4. `PlannedSynthesis` record caches the pre-built slow-turn primitive + tangent entry point + chosen speed iff the trigger lands in the current segment; otherwise defers to a later `SetupSegment`.
5. **`ComputeTargetSpeed`** adds a pre-trigger brake constraint using `plan.ChosenSpeedKts` at `distToTarget - tangentInset`, so the aircraft decelerates to the chosen entry speed (not the global 3 kt floor) by the tangent point.
6. **`TickStraight`** fires the trigger when `distToTarget ≤ tangentInset` AND the aircraft is within `max(3 ft, 0.15 × chosenRadius)` of the planned tangent-entry point (strict-geometry check; scales with radius so piston aircraft with small radii don't trip false warnings). Engaging = swap `_currentPrimitive` to the pre-built slow-turn. Missing = log warning and skip synthesis; pure-pursuit carries on.
7. Suppresses normal arrival detection while a plan is active so the loose 91 ft threshold doesn't fire before the tangent inset (25–60 ft).
8. On arc completion `TickSlowTurn` returns `ArrivedAtNode` normally; `TaxiingPhase` advances the route index and sets up the outgoing segment's straight, which pure-pursuit steers cleanly to the next node.

**Aviation review**: approved by `aviation-sim-expert`. Anchors: AIM 4-3-18 §2 (PIC authority over taxi mechanics — no FAA rule prescribes taxi turn radius/speed), AIM 4-3-18 §5 (blast exposure), 7110.65 3-7-2 (ATC issues route, not turn mechanics). No ground-separation rule applies behind a slow aircraft, so dynamic radius (keeping speeds ~10–15 kt in the common case) is a realism win, not just an aesthetics one.

**Verification**: `SfoM2MultiTurnTaxiTests` — arc engages at 0.84 ft from tangent entry at gs=13.5 kt, rotates 89.7° at v=12.4 kt constant, exits at heading 117.85° (exact A1 bearing) with zero off-line error, accelerates on A1. Taxi 42 s (was 80+ s pre-fix). Full 3161-test suite green, zero warnings. Tightened `SfoM2MultiTurnTaxiTests` time assertion from `>= 60s` (pre-fix trip-wire) to paired `30s ≤ taxi ≤ 75s` bounds that catch both shortcutting and spiral regression.

**Key files changed**:
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — `PlannedSynthesis` record, `_plannedSynthesis` field, `PlanSynthesisLookahead`, trigger constraint in `ComputeTargetSpeed`, mid-segment trigger in `TickStraight`, arrival-threshold suppression, supporting constants.
- `tests/Yaat.Sim.Tests/Simulation/SfoM2MultiTurnTaxiTests.cs` — assertion polarity fix.

### Item 2 — Pathfinder: prefer same-taxiway arc iff shortcut

**Why second**: once the navigator is robust (Item 1), pathfinder-level arc preference becomes a pure optimization. If the pathfinder stops preferring the apex-detour, the aircraft taxis via the fillet arc naturally and doesn't need the navigator's SlowTurn rescue.

**Rule per conversation**: prefer a same-taxiway arc *only when it's a shortcut along the same walk path*. Formally: walk without the arc would visit nodes `[... X Y Z ...]`, and the arc connects two of those nodes non-adjacently (e.g., `X` and `Z`), skipping `Y`. Then use the arc. If the arc endpoints aren't both nodes the walk would reach, don't use it.

**Implementation sketch** (`TaxiPathfinder.WalkTaxiway`):
1. Walk the taxiway using only straights (current behaviour) — collect the resulting node sequence.
2. For each same-taxiway arc incident to any node in that sequence, check if its other endpoint is *also* in the sequence, at a non-adjacent position.
3. If yes, replace the intermediate segments with the arc.

Alternative / simpler: at each node, if there's a same-taxiway arc, peek ahead along the straight walk to see if the arc's other endpoint is visited within N steps; if yes, take the arc.

**Tests**:
- Add an LI `--pathfinder` flag parity test or inline unit test that exercises the M2→A→A1→1R route on SFO and asserts the arc 2186↔2185 is used.
- Existing airport lifecycle tests (`Oak*`, `Sfo28r*`) must stay green — these don't depend on any same-taxiway fillet bypasses and shouldn't change.

**Files to touch**:
- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` (`WalkTaxiway` candidate selection)
- Tests

### Item 3 — `Yaat.LayoutInspector --pathfinder` runtime parity

**Do this alongside or after Item 2** — without the runtime-parity flags, any future diagnostic on pathfinder behaviour (including verifying Item 2 landed correctly) is handicapped.

**Scope**:
- Add CLI options to `tools/Yaat.LayoutInspector/CliOptions.cs`: `--pf-dest-rwy <runway>`, `--pf-hold-shorts <taxiway>@<runway>[,...]` (or similar), `--pf-airport <icao>`.
- Pass them through to `ExplicitPathOptions` in `QueryCommand.cs:108-114` and `HtmlRenderCommand.cs:48` (for `--html-route`).
- Add a convenience: when `--pf-dest-rwy` is set, the tool can also auto-populate `--pf-airport` from the layout (since the layout has an `AirportId`).

**Tests**: LI is a CLI tool — tests are typically ad-hoc. Just verify by running both LI and the runtime on the M2→A→A1→1R scenario and confirming they produce identical routes.

**Files to touch**:
- `tools/Yaat.LayoutInspector/CliOptions.cs`
- `tools/Yaat.LayoutInspector/UsageText.cs`
- `tools/Yaat.LayoutInspector/Commands/QueryCommand.cs`
- `tools/Yaat.LayoutInspector/Commands/HtmlRenderCommand.cs`

## What to do FIRST in the next session

1. Read this file and `git log --oneline f1cdfed..HEAD` on `worktree-declarative-scribbling-lake`.
2. `git status` — confirm the uncommitted files are still on disk:
   - `tools/Yaat.LayoutInspector/inspector-template.html` (opacity tweak)
   - `tests/Yaat.Sim.Tests/Simulation/SfoM2MultiTurnTaxiTests.cs` (E2E test)
3. Decide whether to commit the two uncommitted items as a single housekeeping commit before starting Item 1. Both are independent of the upcoming work.
4. Run the M2 test to confirm it still passes and still exhibits the spiraling pathology (the assertions are deliberately lax enough to pass while the bug is present — it asserts airborne-within-budget, not turn-quality). For a clean before/after, inspect `.tmp/sfo-m2-multiturn.csv` around ticks 93–127.
5. Start Item 1 (navigator SlowTurn synthesis).

## Diagnostic commands

```bash
# Full SFO M2 E2E + CSV
timeout 120 dotnet test tests/Yaat.Sim.Tests \
    --filter "FullyQualifiedName~SfoM2MultiTurnTaxiTests" \
    --logger "console;verbosity=detailed" 2>&1 | tee .tmp/test-m2.log

# Render the trajectory (silhouette + runway highlight)
dotnet run --project tools/Yaat.LayoutInspector -- \
    tests/Yaat.Sim.Tests/TestData/sfo.geojson \
    --ticks .tmp/sfo-m2-multiturn.csv \
    --html .tmp/sfo-m2-multiturn.html \
    --html-runway 01R \
    --tick-aircraft-length-ft 110 --tick-aircraft-wingspan-ft 117

# Query the pathology directly
dotnet run --project tools/Yaat.LayoutInspector -- \
    tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 507   # apex of A1 bend
dotnet run --project tools/Yaat.LayoutInspector -- \
    tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 2185  # A1 tangent (east side)
dotnet run --project tools/Yaat.LayoutInspector -- \
    tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 2186  # A1 tangent (west side)

# The pathfinder tool's output will diverge from runtime until Item 3 lands.
# Runtime trace (for the M2→A→A1→1R scenario) is logged under [TryTaxi] / [NavV2]
# categories — enable them in the test via SimLogBuilder.EnableCategory.
```

## Deferred / future

- **Arrival detection redesign** — the along-track attempt failed. The user wants something better than the current overshoot watchdog (distance-increasing-while-close at 182 ft). Deferred; not a blocker for the current work. Whatever emerges should probably also handle the Fibonacci-spiral case cleanly (aircraft physically past target should never spiral, regardless of segment geometry).
- **`NoseOutSpeedKts` separate from `ArcSpeedKts`** in `LineUpPhase` — perceptible-latency fix for long Aligned-path lineups. Out of scope here; may be revisited after Item 1 since SlowTurn synthesis could make some "Aligned with long waste-straight" cases unnecessary.
- **`WasteFractionThreshold` retuning** (20% → 10%, or add `dHdg > 15°` always-pivot trigger). Only worth revisiting if Item 1 doesn't already produce clean LineUp poses for the pathological cases.

## File inventory

**Committed on the branch** (newest first):
- `dc12009`: `src/Yaat.Sim/Data/Airport/GroundSpawnSnap.cs` (new), `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` (+`FindNearestTaxiEdge`), `src/Yaat.Sim/Scenarios/ScenarioLoader.cs` (hook), `tests/Yaat.Sim.Tests/GroundSpawnSnapTests.cs` (new, 7 tests).
- `4ff7a39`: revert of `b7f3d38` (removes `TaxiIngressResolver.cs` + its wiring + its tests).
- `5261ff9`: `tools/Yaat.LayoutInspector/{CliOptions.cs, Commands/HtmlRenderCommand.cs, HtmlRenderer.cs, inspector-template.html}`, `tests/Yaat.Sim.Tests/Simulation/Issue142SfoRwy01rShallowLineupTests.cs` (+`Diagnostic_TraceUal859TaxiApproachToHoldShort`).
- `1da6715`: `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` (pure-pursuit in `TickStraight`), `tests/Yaat.Sim.Tests/GroundNavigatorStraightPursuitTests.cs` (new, 5 tests).
- `eaf5612`: `src/Yaat.Sim/GeoMath.cs` (+`FootOfPerpendicular`, `SegmentsIntersect`), `src/Yaat.Sim/Data/Airport/RunwayIntersectionCalculator.cs` (uses GeoMath), `src/Yaat.Sim/Data/Airport/TaxiwayGraphBuilder.cs` (uses GeoMath), `tests/Yaat.Sim.Tests/GeoMathFootOfPerpendicularTests.cs` (+11 tests).
- `f8c608f`: `src/Yaat.Sim/Phases/Tower/LineUpPhase.cs` (rewritten), `src/Yaat.Sim/Phases/Tower/LineUpGeometry.cs` (new), `tests/Yaat.Sim.Tests/LineUpGeometryTests.cs`, `LineUpPhaseTests.cs`, `Simulation/Issue142SfoRwy01rShallowLineupTests.cs`.
- `f1cdfed`: `src/Yaat.Sim/Phases/Ground/PathPrimitive*.cs` (adds `PathPrimitiveSlowTurn` + builder), `TickSlowTurn` in `GroundNavigator.cs`, related tests.

**Uncommitted on disk**:
- `tools/Yaat.LayoutInspector/inspector-template.html` — non-highlighted opacity 0.1 → 0.25
- `tests/Yaat.Sim.Tests/Simulation/SfoM2MultiTurnTaxiTests.cs` — new E2E test, 2 Fact methods (one assertion test, one diagnostic CSV writer). Currently uses verbose `SimLogBuilder.EnableCategory` — the user may want to dial that down (or keep as-is for debugging during Item 1 work).

**Key read-only reference files for the next session** (do NOT modify during Item 1/2):
- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs:704+` — `WalkTaxiway` (candidate selection bug is at 727-749)
- `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs:536-542` — confirms same-taxiway fillet arcs are intentional
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs:535-611` — `BuildSpeedConstraints` lookahead logic (for reference when designing SlowTurn synthesis trigger)
- `src/Yaat.Sim/Phases/Ground/PathPrimitiveBuilder.cs:44-60` — `FromSegment` (where SlowTurn insertion would integrate) + line 120+ `BuildSlowTurn` factory
- `src/Yaat.Sim/AircraftCategory.cs:463,489,531,544,561` — `GroundTurnRate`, `LineUpTurnRadiusFt`, `TaxiCornerSpeed`, `TaxiTightCornerSpeed`, `CornerSpeedForAngle`, `MinGroundTurnRadiusFt` constants

**Do NOT delete this plan file yet** — it stays until the three items are committed and green.
