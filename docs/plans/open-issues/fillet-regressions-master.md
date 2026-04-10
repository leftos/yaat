# Master plan — Fillet-arc regressions (SFO + OAK)

Consolidates four open-issue plans into one investigation. Supersedes:

- `docs/plans/open-issues/fillet-arc-taxi-misbehavior-wja1508.md` (Plan A — SFO 28R runway exit overshoot)
- `docs/plans/open-issues/sfo-b10-taxi-stall.md` (Plan B — SFO mid-taxi stall + wrong-direction reissue)
- `docs/plans/open-issues/fillet-arcs-sfo-at6-t6b.md` (Plan C — SFO generation-time arc symmetry)
- `docs/plans/open-issues/oak-g-d-missing-fillet-arc.md` (Plan D — OAK G/D missing fillet arc → 180° stall)

After this master plan is approved and implementation lands, delete all four originals.

## Context

Four user-reported bugs at SFO and OAK all land in the fillet-arc subsystem enabled in production on Apr 7 (`a172f32`). Rather than chase them in parallel and duplicate diagnostic work, they are investigated together because the evidence points at a shared regression boundary.

**Why combine:**

1. **Plans A and B replay the same recording.** `X:\Downloads\S1-SFO-2 _ Ground Control 28_01.yaat-bug-report-bundle.zip`, `RngSeed 91127251`, same scenario — WJA1508 is the landing/exit bug, SKW3078/DAL2581 are the ground-taxi bugs inside the same sim world. One bundle copy, one engine build, one replay loop services both investigations.
2. **All three plans implicate the same Apr 7 commit cluster:** `a172f32` (enable fillet arcs + multi-strategy pathfinder), `1c9db3f` (threshold-node fillets), `b23fb0c` (iterative coincident-node merge with bezier adjustment), `ba00cf6` (remove GroundNavigator turn anticipation), `baeb0a3` (cubic bezier replacement), `2c2ab49` (coincident fillet-node merge + analog LineUpPhase), `a23b738` (fillet arc infrastructure).
3. **Shared code surface:** verified via exploration —
   - Plans A, B, C, D all touch `FilletArcGenerator.cs` (`Apply:27`, `FilletNode:149`, `MergeCoincidentNodes:506`, `RemoveRedundantArcs:696`, `RecordTangentPoint:806`, `SelectMaxRadius:880`).
   - Plans A, B, and D additionally share `GroundNavigator.cs` (`SetupSegment:57`, `Tick:241`, `ComputeArcSteering:337`, `_speedConstraints:41`, `_currentNodeRequiredSpeed:39`), `CubicBezier.cs` (`RadiusOfCurvatureFt:98`, `MinRadiusOfCurvatureFt:128`, `ClosestT:168`), `AirportGroundLayout.cs` (`GroundArc:192`, `GroundArc.MaxSafeSpeedKts:245`), and `TaxiPathfinder.cs`.
4. **Unified hypothesis worth testing first:** all four symptoms are consistent with a single defect in `FilletArcGenerator.MergeCoincidentNodes` (`b23fb0c`) or the generator's pair-processing order at *dense intersections*:
   - At 28R/D branch (Plan A), a mistranslated bezier control point produces an arc whose `MinRadiusOfCurvatureFt` is looser than intended → `MaxSafeSpeedKts` too high → WJA1508 takes the fillet too fast and the tangent misaligns → ~120° snap instead of ~90°.
   - At node 1235 with 4 incoming arc edges (Plan B), an over-aggressive merge squashes a bezier endpoint near-degenerate → `MinRadiusOfCurvatureFt` collapses → `MaxSafeSpeedKts ≈ 0` → stall.
   - At A/T6 / A/T6B (Plan C), the same merge removes one of the two expected A↔Txx arcs as "degenerate/duplicate/redundant" at `:564–571`, leaving the asymmetric 1-arc topology the user observed.
   - At OAK G/D/C junction #1208 (Plan D), the same missing-pair problem: 6 edges post-fillet but no arc from G-northbound into D-westbound. Pathfinder forces the aircraft through a `RAMP · G` arc whose tangent starts 180° opposite the aircraft's heading → `_currentNodeRequiredSpeed=0` → permanent stall. Plan D explicitly names H4 (pair never generated because earlier fillet consumed the straight edge) as the leading candidate — identical to Plan C's hypothesis.
   This is a hypothesis, not a conclusion — Phase 1 confirms or rejects it before any code change.

**Intended outcome:** one diagnostic pass that produces evidence for all four bugs, a root-cause confirmation with the user, and then the minimum number of surgical fixes (ideally one, possibly up to four) with durable regression tests covering both recording-driven E2E and synthetic-geometry unit levels.

5. **Plan D confirms the bug is cross-airport.** OAK G/D/C junction #1208 has 6 edges post-fillet but no arc from G-northbound into D-westbound. The stall mechanism (`_currentNodeRequiredSpeed=0` due to 180° turn angle) matches Plan B's SFO node 1235 stall exactly. Plan D also identifies a **defensive fix** needed in `GroundNavigator.cs`: floor `_currentNodeRequiredSpeed` to a minimum taxi crawl speed (e.g. 1 kt) so that missing arcs never permanently deadlock the sim. This defensive fix is separate from the generator fix. Two skipped tests (`OAK_FullGroundSequence_NoOverlapAndSIG1Reached`, `RerouteFrom28R_ExitSideHoldShort_NotAddedAsCrossing`) are blocked by this bug and must be re-enabled after fix.

**Additional enhancement — parking-approach straight edge preservation:** When creating fillet arcs on edges leading into parking spots, the generator should preserve a straight-edge segment at the parking end long enough for the aircraft to fit. The minimum straight length should be inferred from the geometry:
- **Aircraft length estimate:** distance from the parking spot to the nearest taxiway intersection, clamped to a reasonable max fuselage length (e.g. ~230 ft for heavies).
- **Aircraft wingspan estimate:** inferred from the spacing between neighboring parking spots.
- The fillet arc's tangent point on the parking-approach edge must sit far enough back that the remaining straight segment ≥ estimated aircraft length + buffer.
This ensures the aircraft can physically fit on the straight portion before the curve begins, preventing unrealistic tight-turn entries into gates. Investigate whether this is already handled during Phase 1 (check `FilletArcGenerator` for parking-edge awareness); if not, add it as a Phase 3 item alongside the bug fixes.

**Non-goals:** rewriting the fillet subsystem, touching pathfinding scoring unless Phase 1 proves it causal, reworking LandingPhase beyond the minimum required to coexist with the fix, adding feature flags / migration shims.

## Phase 1 — Unified diagnostic (no code edits to src/)

### 1.1 Land the shared recording once

- [ ] `cp "X:/Downloads/S1-SFO-2 _ Ground Control 28_01.yaat-bug-report-bundle.zip" tests/Yaat.Sim.Tests/TestData/sfo-s1-ground-control-28-01-recording.yaat-bug-report-bundle.zip`
  - One neutral filename, not Plan A's `sfo-28r-wja1508-fillet-overshoot-recording.*` and not Plan B's `issue-sfo-b10-taxi-stall-recording.*`. Both investigations load the same file.
  - Do **not** overwrite `tests/Yaat.Sim.Tests/TestData/issue-sfo-28r-exit-recording.yaat-bug-report-bundle.zip` — different scenario, backs `IssueSfo28rExitTests`.
- [ ] `pwsh tools/migrate-recordings-v4.ps1` — idempotent, bundle is already v4, just confirm.

### 1.2 Layout snapshots at all three trouble corners

All captured into `.tmp/` (gitignored) in one batch so we can diff before/after any future fix:

- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --exits 28R > .tmp/sfo-28r-exits.txt`
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --taxiway D > .tmp/sfo-twy-d.txt`
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 1552 > .tmp/sfo-28r-d-branch-1552.txt` (Plan A, 28R→D fillet)
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 1555 > .tmp/sfo-28r-d-branch-1555.txt` (Plan A)
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 1235 > .tmp/sfo-b10-node-1235.txt` (Plan B, 4-arc intersection)
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 1238 > .tmp/sfo-b10-node-1238.txt` (Plan B)
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --taxiway T6 > .tmp/sfo-twy-t6.txt` (Plan C)
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --taxiway T6A > .tmp/sfo-twy-t6a.txt` (Plan C)
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --taxiway T6B > .tmp/sfo-twy-t6b.txt` (Plan C)
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/oak.geojson --node 1208 > .tmp/oak-gdc-node-1208.txt` (Plan D, 6-edge junction)
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/oak.geojson --taxiway G > .tmp/oak-twy-g.txt` (Plan D)
- [ ] `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/oak.geojson --taxiway D > .tmp/oak-twy-d.txt` (Plan D)

For each trouble corner, record: surviving arc ids, `TaxiwayNames`, `MinRadiusOfCurvatureFt`, computed `MaxSafeSpeedKts(GroundTurnRate)`, tangent-point node ids, and whether any arc spans two different taxiway names (junction arc).

If the LayoutInspector does not already expose an `--applyFillets false` toggle to diff pre-merge geometry (Plan C's "with vs without fillets" requirement — see `tools/Yaat.LayoutInspector/LayoutAnalyzer.cs:19` `applyFillets` flag), add one as the **only** allowed src edit in Phase 1. This is a read-only diagnostic tool, not production code.

### 1.3 One diagnostic test file per scenario, shared helpers

Create `tests/Yaat.Sim.Tests/Simulation/SfoS1GroundControl28FilletDiagnosticTests.cs`. One test class, three `[Fact]` methods that all share the same `BuildEngine()` + recording. Mirror the style of `tests/Yaat.Sim.Tests/Simulation/IssueSfo28rExitTests.cs:34` and `tests/Yaat.Sim.Tests/Simulation/SfoTaxiToParkingStuckTests.cs:24`.

Reuse, do not reimplement:

- `RecordingLoader.Load()` (handles `.yaat-bug-report-bundle.zip` transparently)
- `TestVnasData.EnsureInitialized()` + `TestVnasData.NavigationDb`
- `new TestAirportGroundData().GetLayout("SFO")` for `NearestNodeHelper.Log`
- `SimLogBuilder.CreateForTest(output).InitializeSimLog()` at **Debug** level so `GroundNavigator.Tick` trace lines (`GroundNavigator.cs:314`) surface every term of `targetSpeed = Math.Min(...)`
- `engine.Replay(recording, 0)` then `engine.ReplayOneSecond()` — **never** `TickOneSecond` (skips recorded actions, `docs/e2e-tdd-issue-debugging.md` §5)
- `NearestNodeHelper.Log(output, ..., layout)` every tick

All three facts end with `Assert.Fail(...)` so xunit prints their captured output. All three return silently on null recording / null layout (no `Assert.Skip`).

**Fact 1 — `Diagnostic_WJA1508_28R_D_Exit_Overshoot`** (replaces Plan A diagnostic):

1. `engine.Replay(recording, 0)` then tick until WJA1508 enters `LandingPhase`. Switch to per-tick detail mode for 60 s.
2. Each detail tick log: phase, `IsOnGround`, IAS/GS/hdg, `(lat,lon)` with along-track distance from 28R threshold via `GeoMath.AlongTrackDistanceNm`, `Targets.TargetSpeed`, `Phases.RequestedExit`/`ResolvedExit`, live `distToBranch` to `BranchPointNode`, whether current `GroundNavigator` segment is `GroundEdge` vs `GroundArc`, and when on an arc: `MinRadiusOfCurvatureFt`, `MaxSafeSpeedKts(GroundTurnRate)`, `TaxiwayNames`, bezier `t` via `ClosestT`, active `arcSpeedLimit`, heading-error `speedFraction`.
3. Max-metrics dump: `maxArcSpeedExcess = max(actualGS - arcMaxSafeSpeed)`, `maxHeadingRateDegPerSec`, `headingChangeAtBranch`, whether any planned route segment is a junction arc (`TaxiwayNames.Length > 1`).

**Fact 2 — `Diagnostic_SKW3078_DAL2581_B10_TaxiStall`** (replaces Plan B diagnostics; combines SKW3078 initial taxi, SKW3078 reissue, and DAL2581 taxi into one fact since they all share the same engine):

1. `engine.Replay(recording, 0)` then let it run until `t=816` (SKW3078 cmd). Per-tick detail mode for the next 90 s.
2. Log per tick for SKW3078: `AssignedTaxiRoute.CurrentSegmentIndex`, current segment endpoints + `TaxiwayName` + edge kind (`GroundEdge`/`GroundArc`), `IsHeld`, `GroundSpeedLimit`, `GroundSpeed`, `TrueHeading.Degrees`, `brakingLimit`/`arcSpeedLimit`/`_currentNodeRequiredSpeed` (all visible from `GroundNavigator.Tick` trace at Debug level), `NearestNodeHelper.Log`. Expected to see stall on segment 16 = edge 1235→1238 (taxiway A arc).
3. Continue through `t=1076` (SKW3078 reissue). Log new route's first 5 segments + taxiways + initial heading vs bearing-to-B10. Expected to see wrong direction (heading ~300° away from B10).
4. Continue through `t=1179` (DAL2581 cmd). Same 90 s per-tick loop. Expected identical 81-seg route and identical stall at seg 16.
5. Max-metrics dump per aircraft: stall tick, which term dominated `Math.Min` at stall (arc limit / braking / forward constraint / ground-speed-limit), segment index at stall, position and distance to segment endpoint, whether `_speedConstraints` was empty / back-propagating a zero.

**Fact 3 — `Diagnostic_FilletGeneration_SFO_A_T6_Symmetry`** (replaces Plan C diagnostic — **no recording**, pure in-process):

1. Load `sfo.geojson` through the normal parse pipeline (fillets applied in `GeoJsonParser.cs:244-248` step 8).
2. For each of A/T6, A/T6A, A/T6B: find all arcs whose `TaxiwayNames` contain `"A"` and the stub name. Log count, endpoints, `MinRadiusOfCurvatureFt`, which A-side (west/east) each arc's A-end tangent sits on (distinguishable by longitude relative to the junction node).
3. Max-metrics dump: `(expected 2, actual N)` per junction. Per the user's observation, expect A/T6A=2, A/T6=1, A/T6B=1.
4. **Optional targeted logging shim** (allowed in Phase 1 only because it is scoped + reverted before fix lands): temporarily log inside `FilletArcGenerator.FilletNode` (`:149`), gated on the three SFO junction node ids only, capturing edge collection count at `:157-164` (especially `GroundEdge` vs `GroundArc` skipped counts — Plan C H4), pair classification at `:193-235`, Phase C emission at `:267-322` with `tanNodeA.Id == tanNodeB.Id` drops at `:276`, and post-merge removal counts from `MergeCoincidentNodes` at `:564/:568/:571`. This shim **must be deleted** before Phase 3.

**Fact 4 — `Diagnostic_FilletGeneration_OAK_GD_MissingArc`** (replaces Plan D diagnostic — **no recording**, pure in-process):

1. Load `oak.geojson` through the normal parse pipeline (fillets applied).
2. At node #1208: enumerate all edges, log each edge's `TaxiwayNames`, type (`GroundEdge`/`GroundArc`), destination node, distance, and **bezier tangent direction at #1208** for arcs.
3. Check: does an arc exist from G-northbound (tangent ~0°) into D-westbound (tangent ~210°)? Per Plan D's observation, expect NO — confirming the missing pair.
4. Log the same `FilletNode` shim data as Fact 3 (gated on OAK #1208 node id) to capture which pairs were generated/dropped and why.
5. Dump: `(expected: arc G-north→D-west, found: <list of actual arcs at #1208>)`.

Run all four facts in one invocation with output teed:

```bash
dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj \
  --filter "FullyQualifiedName~FilletDiagnostic" \
  -v detailed 2>&1 | tee .tmp/fillet-master-diag.log
```

(Per `feedback_validation_output.md`: tee long runs to `.tmp/`. Per `feedback_use_logging_to_debug.md`: don't speculate — read the log.)

### 1.4 Classification decision — one table, five rows

Read `.tmp/fillet-master-diag.log` + the Phase 1.2 layout snapshots together and classify each bug against the unified hypotheses. **Stop here and walk the table through with the user before touching any src/ file.**

| Bug | Dominant evidence | Suspected subsystem | Fix surface (Phase 3) |
|---|---|---|---|
| WJA1508 28R→D overshoot | `maxArcSpeedExcess > 0` on 28R→D arc, or `MinRadiusOfCurvatureFt` wildly different from `FilletArcGenerator.SelectMaxRadius` intent, or `headingChangeAtBranch ≥ 100°`, or junction arc in route when same-taxiway alt exists | `FilletArcGenerator` (radius/merge) *or* `GroundNavigator` arc-speed path *or* `TaxiPathfinder` strategy scoring | One of: `FilletArcGenerator.cs`, `GroundNavigator.cs`, `AirportGroundLayout.cs` (`GroundArc.MaxSafeSpeedKts`), `CubicBezier.cs`, `TaxiPathfinder.cs`, `LandingPhase.cs` |
| SKW3078/DAL2581 B10 stall | Which `Math.Min` term is 0 at stall. If `arcSpeedLimit≈0` with seemingly-normal radius: `ComputeArcSteering` floor bug. If `_currentNodeRequiredSpeed=0`: turn-angle recomputation between two arcs. If `_speedConstraints` holds stale 0: forward walk back-propagation. If radius actually *is* near-zero: `MergeCoincidentNodes` produced degenerate bezier at node 1235 | `GroundNavigator.cs:337` (`ComputeArcSteering`), `:111` (`_currentNodeRequiredSpeed`), `:136-197` (forward walk) *or* `FilletArcGenerator.MergeCoincidentNodes` | `GroundNavigator.cs` *or* `FilletArcGenerator.cs` |
| SKW3078 reissue wrong direction | `PickBestStartEdge` picked the edge whose departure bearing is >90° away from `aircraft.TrueHeading`; `ResolveParkingRoute` passed no heading hint | `TaxiPathfinder.PickBestStartEdge` (and possibly `PickBestWalkEdge`), `GroundCommandHandler.ResolveParkingRoute` | `TaxiPathfinder.cs`, `GroundCommandHandler.cs:295` |
| A/T6 & A/T6B single-arc (SFO) | FilletNode shim shows one of: pair never added to `plannedArcs` (H4, arc-skip at `:157-164` due to adjacent node processing order), Phase C `tanNodeA.Id == tanNodeB.Id` drop at `:276`, `MergeCoincidentNodes` degenerate/duplicate/redundant removal at `:564-571`, or tangent-distance per-edge collapse at `:806` | `FilletArcGenerator` topology — one of the above | `FilletArcGenerator.cs` |
| OAK G/D #1208 missing arc | Same mechanism as A/T6 row — pair from G-north into D-west never generated. FilletNode shim for #1208 will show which edges were skipped as arcs. G/C was likely filleted first, consuming the G-north straight edge; when G/D is processed, no `GroundEdge` remains for G-north and the pair is silently dropped (H4) | `FilletArcGenerator` topology — same as C row | `FilletArcGenerator.cs` |

**Critical decision point:** do any rows above share a fix surface? If yes (most likely B-stall + C-symmetry both living in `MergeCoincidentNodes`, or A-overshoot + B-stall both living in `ComputeArcSteering`), then that single fix satisfies both bugs and Phase 3 is one edit, not two. If no, Phase 3 is two or three targeted edits in different files.

Write the chosen classification + fix surfaces into a new `## Root cause` section at the bottom of this plan file before proceeding.

## Phase 2 — Failing regression tests (TDD)

Only after Phase 1.4 classification, *before* any production fix. Per `feedback_tdd_workflow.md`.

### 2.1 Convert diagnostics to assertions

Replace the `Assert.Fail(...)` tails in `SfoS1GroundControl28FilletDiagnosticTests.cs` with explicit invariants. Activate only the subset that Phase 1.4 proved.

**For WJA1508:**

- [ ] `maxArcSpeedExcess <= 0` — aircraft never exceeds arc-derived `MaxSafeSpeedKts` while on a `GroundArc`.
- [ ] `maxHeadingChangeRateDegPerSec <= CategoryPerformance.GroundTurnRate(jet) * 1.05` — within 5 % of category limit.
- [ ] `headingChangeAtBranch <= 100` — catches the reported ~120° snap.
- [ ] (Conditional on Phase 1.4): `wja1508.Route.All(s => s is not GroundArc { TaxiwayNames.Length: > 1 })` — no junction arc when same-taxiway alternative exists.

**For SKW3078 / DAL2581:**

- [ ] `SKW3078_TaxiAtoB10_ReachesOrAdvancesPastSeg16` — after `engine.Replay(0)` and tick through `t=816+600`, `CurrentSegmentIndex > 16` OR `CurrentPhase is AtParkingPhase`.
- [ ] `DAL2581_TaxiAtoB10_ReachesOrAdvancesPastSeg16` — same invariant, `t=1179+600`.
- [ ] `SKW3078_RetaxiAtoB10_InitialHeadingTowardB10` — after reissue at `t=1076` + 30 s, `|NormalizeAngleDiff(TrueHeading - bearingToB10)| < 90` and new route segment count ≤ 120.

**For A/T6 & A/T6B (generation-time, SFO):**

- [ ] `SFO_FilletArcs_TTerminalStubs_EachHaveTwoArcs` — in `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs` (existing file) or alongside `FilletPathfindingTests.cs` (has `LoadSfo()` helper at `:24-32`). For each of A/T6, A/T6A, A/T6B: assert exactly 2 fillet arcs tagged with both `"A"` and the stub, one on each A-side of the junction.

**For OAK G/D (generation-time, OAK):**

- [ ] `OAK_FilletArcs_GDJunction_HasArcFromGNorthToDWest` — load `oak.geojson`, apply fillets, assert an arc exists at #1208 from G-northbound into D-westbound. This is the single missing pair Plan D identifies.
- [ ] **Re-enable skipped tests** (remove `Skip = "Blocked by missing fillet arc at OAK G/D junction"`) after the fix lands:
  - `OakGroundE2ETests.OAK_FullGroundSequence_NoOverlapAndSIG1Reached`
  - `OakCross28RHoldShortTests.RerouteFrom28R_ExitSideHoldShort_NotAddedAsCrossing`

### 2.2 Confirm all activated assertions fail on `main`

```bash
dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj \
  --filter "FullyQualifiedName~SfoS1GroundControl28Fillet|FullyQualifiedName~TTerminalStubs" \
  2>&1 | tee .tmp/sfo-fillet-master-red.log
```

Do not proceed to Phase 3 until every activated assertion is confirmed red.

## Phase 3 — Targeted fix(es), one commit per bug

Edit **only** the surfaces Phase 1.4 identified. No drive-by cleanup, no speculative improvements (`feedback_no_optional_params.md`, global "no speculative features" rule).

**Commit policy:** one commit per distinct bug. If Phase 1.4 shows two bugs collapse to a single fix surface (most likely: B-stall + C-symmetry both in `MergeCoincidentNodes`, or A-overshoot + B-stall both in `ComputeArcSteering`), those become **one** commit covering both bugs. The number of commits equals the number of distinct root-cause surfaces, not the number of reported symptoms. Each commit lands with its own failing-test-first trail intact.

- [ ] Delete the Phase 1.3 Fact 3 `FilletNode` logging shim if it was added. (Done in the first fix commit, not as a separate hygiene commit.)
- [ ] For each distinct root-cause surface, in order — simplest isolated first, most entangled last:
  1. Apply the minimum fix needed to turn that surface's red assertions green.
  2. Rerun the surface's filter — its red assertions green, existing sibling tests still green.
  3. Commit. Subject ≤72 chars, imperative, prefixed `fix:`. Body names the bug(s) the commit closes and the file(s) touched. Never amend a prior commit in this sequence.
  4. Only then move to the next surface. This keeps bisectability if a later fix reveals an earlier fix's side effect.
- [ ] If fix diagnosis indicated "heading hint missing" (SKW3078 reissue), thread `aircraft.TrueHeading` from `GroundCommandHandler.ResolveParkingRoute` (`src/Yaat.Sim/Commands/GroundCommandHandler.cs:295`) down to `TaxiPathfinder.PickBestStartEdge`. Mirror into `PickBestWalkEdge` only if Phase 1.4 showed the wrong-direction pick happens mid-walk, not at route start. No optional parameters — make the heading required so the compiler forces every caller (`feedback_no_optional_params.md`).
- [ ] **Defensive fix (separate commit):** In `GroundNavigator.cs`, floor `_currentNodeRequiredSpeed` to a minimum taxi crawl speed (e.g. 1–2 kt) so that missing arcs or 180° turns never permanently deadlock the sim. This is a safety net independent of the generator fix — even when all arcs are correctly generated, the navigator should never set speed=0 on a taxi route segment that isn't a hold-short or parking. Identified in Plan D as needed regardless of root cause.

## Phase 4 — Synthetic regression coverage

Durable invariants the recording cannot guarantee (recordings drift; synthetics don't).

- [ ] Whichever `FilletArcGenerator` path was fixed, add a synthetic 3-edge T-intersection unit test in `FilletArcGeneratorTests.cs` that reproduces the exact topology the fix addresses, with no reliance on `sfo.geojson`.
- [ ] Whichever `GroundNavigator` / `GroundArc` path was fixed, add a synthetic unit test that constructs a `GroundArc` with a known radius, ticks the navigator, and asserts `actualGroundSpeed <= arcMaxSafeSpeed` throughout. Pattern exists in `NavigatorArcSteeringTests.cs` (8 existing facts — extend, don't duplicate).
- [ ] If `PickBestStartEdge` was fixed, add a synthetic 4-edge node test asserting that an aircraft heading 90° picks an east-departing edge even when a west-departing edge is geodesically closer to the destination.

## Phase 5 — Full verification

- [ ] `dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --filter "FullyQualifiedName~SfoS1GroundControl28Fillet|FullyQualifiedName~TTerminalStubs|FullyQualifiedName~Fillet|FullyQualifiedName~NavigatorArc|FullyQualifiedName~CubicBezier"` — all fillet + arc + new tests green.
- [ ] `dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --filter "FullyQualifiedName~Sfo28r|FullyQualifiedName~OakAllExits|FullyQualifiedName~RunwayExit|FullyQualifiedName~ElTHighSpeedExit|FullyQualifiedName~ExitK|FullyQualifiedName~SfoRunwayExit|FullyQualifiedName~IssueAmxTaxi|FullyQualifiedName~IssueSfo28r|FullyQualifiedName~SfoTaxi|FullyQualifiedName~SfoHold|FullyQualifiedName~SfoGroundSpeedUntil|FullyQualifiedName~ExitRightTaxiway|FullyQualifiedName~TaxiAirborneRejection|FullyQualifiedName~OakGroundE2E|FullyQualifiedName~GroundConflictConvergence|FullyQualifiedName~SfoLineupDiagonal"` — sibling regression surface green (union of Plan A's, Plan B's, and Plan C's listed suites).
- [ ] `pwsh tools/test-all.ps1` — full build + test across yaat and yaat-server.
- [ ] `dotnet build -p:TreatWarningsAsErrors=true` clean.
- [ ] `prek run` clean.
- [ ] `aviation-sim-expert` review of the fix per CLAUDE.md "Aviation Realism — MANDATORY" (taxi turn behavior, fillet geometry, ground turn rates). Include the local FAA references callout.
- [ ] `dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --filter "FullyQualifiedName~OakGroundE2E|FullyQualifiedName~OakCross28R"` — re-enabled OAK tests pass (previously skipped due to missing fillet arc at G/D #1208).
- [ ] Visual check: run the client, load a SFO scenario, confirm A/T6 and A/T6B both show arcs on both sides of A (matching A/T6A) and route an aircraft from the A/D intersection to gate D15 via A→T6B without a U-turn. Load an OAK scenario and confirm G/D junction #1208 has the G-north→D-west arc.
- [ ] If user-visible behavior changed (taxi paths, exit timing), update `docs/yaat-vs-atctrainer.md` and `docs/architecture.md` if the file tree moved.

## Phase 6 — Plan hygiene

- [ ] Delete the four superseded plan files:
  - `docs/plans/open-issues/fillet-arc-taxi-misbehavior-wja1508.md`
  - `docs/plans/open-issues/sfo-b10-taxi-stall.md`
  - `docs/plans/open-issues/fillet-arcs-sfo-at6-t6b.md`
  - `docs/plans/open-issues/oak-g-d-missing-fillet-arc.md`
- [ ] Move this master plan into `docs/plans/open-issues/sfo-fillet-regressions-master.md` (implementation phase only — not now).
- [ ] Delete the master plan file once Phase 5 is complete (per project convention: delete plan after implementing).

## Critical files

**Read (Phase 1 — no edits except the LayoutInspector `--applyFillets` flag if missing):**

- `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` — `Apply:27`, `FilletNode:149`, `MergeCoincidentNodes:506`, `RemoveRedundantArcs:696`, `RecordTangentPoint:806`, `SelectMaxRadius:880`
- `src/Yaat.Sim/Data/Airport/CubicBezier.cs` — `Evaluate:25`, `Derivative:42`, `SecondDerivative:56`, `RadiusOfCurvatureFt:98`, `MinRadiusOfCurvatureFt:128`, `ArcLength:147`, `ClosestT:168`
- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` — `GroundArc:192`, `GroundArc.MaxSafeSpeedKts:245`, `MatchesTaxiway:227`, `SharesTaxiway:252`
- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` — `FindRoutes:295`, cost functions, `WalkTaxiway`, `PickBestStartEdge:983`, `PickBestWalkEdge:1058`
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — `SetupSegment:57`, `Tick:241`, `ComputeArcSteering:337`, `_speedConstraints:41`, `_currentNodeRequiredSpeed:39`
- `src/Yaat.Sim/Commands/GroundCommandHandler.cs` — `ResolveParkingRoute:295`
- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` — `StartExitNavigation`
- `src/Yaat.Sim/Phases/Tower/LandingPhase.cs` — lines 400–545, 685–717 handoff guard
- `src/Yaat.Sim/AircraftCategory.cs` — `CategoryPerformance` jet constants
- `docs/landing-and-runway-exit.md` — GroundNavigator design invariants
- `docs/e2e-tdd-issue-debugging.md` — TDD workflow + bundle handling + `ReplayOneSecond` vs `TickOneSecond`
- `tests/Yaat.Sim.Tests/TestData/oak.geojson` — OAK airport layout (Plan D)
- Existing tests: `tests/Yaat.Sim.Tests/Simulation/IssueSfo28rExitTests.cs` (pattern mirror), `SfoTaxiToParkingStuckTests.cs` (BuildEngine pattern), `FilletArcGeneratorTests.cs`, `FilletPathfindingTests.cs`, `NavigatorArcSteeringTests.cs`, `CubicBezierTests.cs`, `Sfo28rAllExitsTests.cs`, `SfoRunwayExitTests.cs`, `RunwayExitSpeedTests.cs`, `RunwayExitDoubleDecelTests.cs`, `ElTHighSpeedExitTests.cs`, `ExitKOvershootTests.cs`, `IssueAmxTaxiOvershootTests.cs`
- Skipped tests to re-enable: `OakGroundE2ETests.OAK_FullGroundSequence_NoOverlapAndSIG1Reached`, `OakCross28RHoldShortTests.RerouteFrom28R_ExitSideHoldShort_NotAddedAsCrossing`
- Git commits: `a172f32`, `1c9db3f`, `b23fb0c`, `ba00cf6`, `baeb0a3`, `2c2ab49`, `cfabf2b`, `a23b738`, `a0fa1dd`

**Create (Phase 1):**

- `tests/Yaat.Sim.Tests/TestData/sfo-s1-ground-control-28-01-recording.yaat-bug-report-bundle.zip` — copied bundle (one, not two)
- `tests/Yaat.Sim.Tests/Simulation/SfoS1GroundControl28FilletDiagnosticTests.cs` — four diagnostic facts in one class (SFO Facts 1–3 + OAK Fact 4)
- `.tmp/fillet-master-diag.log`, `.tmp/sfo-28r-exits.txt`, `.tmp/sfo-twy-d.txt`, `.tmp/sfo-28r-d-branch-{1552,1555}.txt`, `.tmp/sfo-b10-node-{1235,1238}.txt`, `.tmp/sfo-twy-{t6,t6a,t6b}.txt`, `.tmp/oak-gdc-node-1208.txt`, `.tmp/oak-twy-{g,d}.txt`
- Possibly `tools/Yaat.LayoutInspector/` `--applyFillets` CLI switch if missing (diagnostic-only, not production)

**Edit (Phase 3 — only the surfaces Phase 1.4 proved):**

- Up to three of: `FilletArcGenerator.cs`, `GroundNavigator.cs`, `AirportGroundLayout.cs`, `CubicBezier.cs`, `TaxiPathfinder.cs`, `GroundCommandHandler.cs`. Expectation: probably one, possibly two, at most three. Confirm surface with user before editing each.
- `tests/Yaat.Sim.Tests/Simulation/SfoS1GroundControl28FilletDiagnosticTests.cs` — convert `Assert.Fail` to real assertions
- `tests/Yaat.Sim.Tests/FilletArcGeneratorTests.cs` — add synthetic T-intersection symmetry test and any Phase 4 regressions
- `tests/Yaat.Sim.Tests/NavigatorArcSteeringTests.cs` — extend with Phase 4 arc-speed invariant test

## Verification

- [ ] Single recording landed as `sfo-s1-ground-control-28-01-recording.yaat-bug-report-bundle.zip`.
- [ ] Layout snapshots for all four trouble corners (SFO 28R/D, SFO node 1235, SFO A/T6/T6A/T6B, OAK #1208) captured in `.tmp/`.
- [ ] Four diagnostic facts run as one test class, output teed to `.tmp/fillet-master-diag.log`, all metric sets dumped (WJA1508 arc-speed/heading/junction-arc/branch-heading; B10 stall dominant-term + both aircraft; SFO A/T6 symmetry counts; OAK #1208 missing-pair confirmation).
- [ ] **Stop point:** Classification table walked through with user. `## Root cause` section written into this plan. User approves fix surface(s) before any src/ edit.
- [ ] Failing assertions added, confirmed red on `main`.
- [ ] Targeted fix(es) applied in the approved surface(s) only.
- [ ] Phase 1.3 Fact 3 logging shim deleted.
- [ ] All red assertions now green.
- [ ] Phase 4 synthetic regression tests added.
- [ ] Phase 5 test filters + `test-all.ps1` + `dotnet build -p:TreatWarningsAsErrors=true` + `prek run` all clean.
- [ ] aviation-sim-expert review complete.
- [ ] Visual + pathfinding checks pass at SFO A/T6, A/T6B, A→T6B→D15, and OAK G/D #1208.
- [ ] Previously skipped OAK tests (`OAK_FullGroundSequence`, `OakCross28RHoldShortTests`) re-enabled and passing.
- [ ] Phase 6 plan-hygiene sweep: four superseded plans deleted, this plan either moved to `docs/plans/open-issues/` and then deleted after implementation, or deleted directly.
