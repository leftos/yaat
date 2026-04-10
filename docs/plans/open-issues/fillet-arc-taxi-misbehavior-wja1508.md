# Fillet-arc taxi misbehavior — diagnose via WJA1508 SFO 28R exit

## Context

A user reported that **WJA1508 landed SFO 28R, didn't slow down enough to make its exit, slightly overshot the branch point, and made a ~120° turn instead of a smooth ~90° exit at taxiway D**. The reproducer is `X:\Downloads\S1-SFO-2 _ Ground Control 28_01.yaat-bug-report-bundle.zip` (v4 archive, 496 s, RngSeed 91127251).

The user believes this is **not a one-off WJA1508 problem**. They suspect a **systemic regression in the fillet arc subsystem** that was enabled in production on Apr 7 (`a172f32 — enable fillet arcs in production, multi-strategy pathfinder`). Their words:

> "Aircraft are taking turns too fast, or are taking fillet arcs unnecessarily instead of staying on the straight edges that would keep them on the same taxiway."

So WJA1508 is the concrete reproducer, but the *real* target is the fillet/taxi behavior class. The fix may live in any of:

- `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` (radius selection, coincident-node merge, junction-arc creation)
- `src/Yaat.Sim/Data/Airport/CubicBezier.cs` (`MaxSafeSpeedKts`, `MinRadiusOfCurvatureFt`, lookahead `ClosestT`)
- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` (`GroundArc.MaxSafeSpeedKts`, `MatchesTaxiway`, `SharesTaxiway`)
- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` (`CostFewestTurns` junction-arc penalty, multi-strategy `Math.Max` scoring, `WalkTaxiway` straight-vs-arc selection)
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` (`SetupSegment` arc speed-constraint construction, backward propagation, `ComputeArcSteering` lookahead, heading-error `speedFraction` interaction with `arcSpeedLimit`)

### Recent regression candidates (read before changing anything)

| Commit | Date | Why it matters |
|--------|------|-----------------|
| `a172f32` | Apr 7 | **Enabled fillet arcs in production**, introduced 3-strategy pathfinder (FewestTurns / Shortest / Fastest, scored via `Math.Max`). Most likely regression source. |
| `1c9db3f` | Apr 7 | Added fillet arcs at runway threshold nodes (preserve threshold + stub edges). Directly affects the SFO 28R → D branch. |
| `b23fb0c` | Apr 7 | Iterative coincident-node merge + bezier control-point translation + cached-distance recompute. Could create phantom geometry near tightly-packed exits. |
| `ba00cf6` | Apr 1 | Removed GroundNavigator turn anticipation, replaced by fillet arcs. Old "turn-ahead" safety net is gone. |

### Why the existing tests don't catch it

`tests/Yaat.Sim.Tests/Simulation/IssueSfo28rExitTests.cs` covers the same scenario but only asserts on **SKW3398** with a `≤75 s exit duration` check. WJA1508's exit in the new recording finishes in ~16 s — so the existing assertion sleeps through the bug. Several other tests (`Sfo28rAllExitsTests`, `OakAllExitsTests`, `RunwayExitSpeedTests`, `RunwayExitDoubleDecelTests`, `ElTHighSpeedExitTests`, `ExitKOvershootTests`, `SfoRunwayExitTests`, `IssueAmxTaxiOvershootTests`) cover adjacent behaviors. **None of them assert** that:

1. Aircraft never exceeds the arc-derived `MaxSafeSpeedKts` while on a `GroundArc`,
2. The aircraft's heading change rate stays within `CategoryPerformance.GroundTurnRate` during a fillet,
3. The route selected by the pathfinder does not include a `TaxiwayNames.Length > 1` junction arc when an all-same-taxiway alternative exists.

These gaps are the assertions we will add.

## Investigation plan (Phase 1 — diagnostic only, no code edits)

Per the chosen scope: **diagnose first, then targeted fix**. Do not guess the root cause — let the diagnostic point at it.

### 1. Land the recording

```bash
cp "X:/Downloads/S1-SFO-2 _ Ground Control 28_01.yaat-bug-report-bundle.zip" \
   tests/Yaat.Sim.Tests/TestData/sfo-28r-wja1508-fillet-overshoot-recording.yaat-bug-report-bundle.zip
pwsh tools/migrate-recordings-v4.ps1   # idempotent; archive is already v4
```

Do not overwrite `issue-sfo-28r-exit-recording.yaat-bug-report-bundle.zip` — different RNG seed, different bug, backs SKW3398's regression test.

### 2. Snapshot the SFO 28R → D corner with the layout inspector

Capture the *current* fillet geometry at the D branch into a `.tmp/` file so later changes can be compared:

```bash
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 1552 > .tmp/sfo-28r-d-branch-1552.txt
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 1555 > .tmp/sfo-28r-d-branch-1555.txt
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --exits 28R   > .tmp/sfo-28r-exits.txt
dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --taxiway D  > .tmp/sfo-twy-d.txt
```

These tell us the **as-built** radius, tangent points, and any junction arcs at the corner *after* `FilletArcGenerator.Apply`. If the radius is wildly larger than `FilletArcGenerator` is supposed to emit at this corner, the bug is in radius selection or coincident-node merging. If a junction arc spans `RWY28R · D`, that's the fillet the aircraft is following on exit — note its `MinRadiusOfCurvatureFt` and computed `MaxSafeSpeedKts(20 deg/s) = ω × R`.

### 3. Diagnostic-only test

Add a new file `tests/Yaat.Sim.Tests/Simulation/Sfo28rWja1508FilletOvershootTests.cs`. Reuse existing helpers — do **not** reimplement them:

- `RecordingLoader.Load()` (`tests/Yaat.Sim.Tests/Helpers/RecordingLoader.cs`) — handles bundles transparently
- `TestVnasData.EnsureInitialized()` + `TestVnasData.NavigationDb`
- `TestAirportGroundData.GetLayout("SFO")`
- `SimLogBuilder.CreateForTest(output).InitializeSimLog()` (pattern at `IssueSfo28rExitTests.cs:34`)
- `NearestNodeHelper.Log(output, ..., layout)` (`tests/Yaat.Sim.Tests/Helpers/NearestNodeHelper.cs`)
- `engine.Replay(recording, 0)` then `engine.ReplayOneSecond()` in a loop. Never `TickOneSecond` — that skips recorded actions (`docs/e2e-tdd-issue-debugging.md` §5).

The diagnostic `[Fact]` must:

1. Replay from `t=0`. When WJA1508 first enters `LandingPhase`, switch into **per-tick (`ReplayOneSecond`) detail mode** for the next 60 seconds.
2. Each detail tick, log:
   - `phase` name, `IsOnGround`
   - `IndicatedAirspeed`, `GroundSpeed`, `TrueHeading.Degrees`
   - `(Lat, Lon)` and along-track distance from the 28R threshold (use `GeoMath.AlongTrackDistanceNm` with the 28R true heading from `--exits 28R`)
   - `Targets.TargetSpeed`
   - `Phases.RequestedExit` (should be `none` — default exit)
   - `Phases.ResolvedExit` (taxiway, hold-short id, full path node ids) and live `distToBranch` to `BranchPointNode`
   - **Whether the navigator's current segment is a `GroundEdge` (straight) or a `GroundArc` (fillet)** — from `GroundNavigator` state. If `GroundArc`, also log `MinRadiusOfCurvatureFt`, `MaxSafeSpeedKts(GroundTurnRate)`, `TaxiwayNames`, and `t` along the bezier (via `bezier.ClosestT`).
   - The active `arcSpeedLimit` and heading-error `speedFraction` from `GroundNavigator.Tick`
   - `NearestNodeHelper.Log(...)` while on the ground
3. After the loop, also dump:
   - Whether **any** segment in WJA1508's planned route is a junction arc (`TaxiwayNames.Length > 1`) — and if so, which.
   - **Maximum heading change rate observed** (deg/sec, ticked) and the tick at which it occurred. Compare against `CategoryPerformance.GroundTurnRate(jet) = 20 deg/s`.
   - **Maximum (`actualGroundSpeed - arcMaxSafeSpeed`)** observed while on a `GroundArc`. Positive = aircraft was over the arc speed limit.
   - **Heading change at the branch tick** (Landing→RunwayExit transition or arc entry) versus the runway heading.
4. End with `Assert.Fail(...)` so xunit prints all output. This first run is *purely* to tell us which subsystem misbehaved.

Run with output captured:

```bash
dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj \
  --filter "FullyQualifiedName~Sfo28rWja1508FilletOvershoot" \
  -v detailed 2>&1 | tee .tmp/wja1508-fillet-diag.log
```

(Per `feedback_validation_output.md`: tee long-running test output to `.tmp/`. Per `feedback_use_logging_to_debug.md`: don't speculate — let the log speak.)

### 4. Read the log and pick a hypothesis

Decision tree based on what the log shows. **Stop and confirm with the user before applying any fix.**

| Diagnostic finding | Implicated subsystem | Likely fix surface |
|---|---|---|
| `actualGroundSpeed >> MaxSafeSpeedKts` while on the fillet arc, AND `arcSpeedLimit` was set high or `_speedConstraints` was empty | `GroundNavigator.SetupSegment` (constraint not added or not back-propagated), or `GroundArc.MaxSafeSpeedKts` formula | `GroundNavigator.cs:154–222`, `AirportGroundLayout.cs:245` |
| `speedFraction` stayed at 1.0 because the aircraft was aligned with the *bezier tangent* the whole time, even though `MinRadiusOfCurvatureFt` was tight | Heading-error scaling is not the right speed governor on arcs | `GroundNavigator.cs:285–287` — for arc segments, `targetSpeed` should be `min(arcSpeedLimit, ...)` ignoring `speedFraction` (or `speedFraction` should derive from local curvature, not heading error) |
| `MinRadiusOfCurvatureFt` is much larger than `FilletArcGenerator.SelectMaxRadius` would emit for this corner | Coincident-node merge (`b23fb0c`) widened the bezier, or radius selection regressed | `FilletArcGenerator.cs:506–589` (merge), `FilletArcGenerator.cs:221+` (`SelectMaxRadius`) |
| The route included a `RWY28R · D` junction arc whose departure tangent doesn't align with D's centerline (so the aircraft "lands" on D pointed wrong) | Bezier tangent / endpoint alignment after merge | `FilletArcGenerator.cs:265+` (Phase C bezier creation) |
| Pathfinder picked a junction arc when a same-taxiway straight-edge route was available | Multi-strategy `Math.Max` scoring promotes a junction-arc route on Distance/Time | `TaxiPathfinder.cs:295–393` (`FindRoutes`), `TaxiPathfinder.cs:531–564` (cost functions). Specifically: `CostFewestTurns` lines 545–548 penalize *all* junction arcs uniformly, but the `Math.Max` blend can still let them win. |
| Branch handoff happens with `distToBranch` near the 0.02 nm guard floor (LandingPhase ran out of room) | LandingPhase / fillet handoff interaction (the simpler hypothesis from the original plan still possible) | `LandingPhase.cs:529` |

Open the diagnostic log, classify the failure, then move to Phase 2.

## Phase 2 — Targeted fix (after diagnosis)

Once the offending subsystem is known, the fix is surgical and TDD-driven:

1. **Convert the diagnostic into a failing assertion test** in the same file. Concrete assertions to add (subset will activate based on the diagnosed cause):
   - `maxArcSpeedExcess <= 0` — never overspeed an arc.
   - `maxHeadingChangeRateDegPerSec <= GroundTurnRate(jet) * 1.05` — within 5% of the category limit.
   - `wja1508.Route.None(s => s is GroundArc { TaxiwayNames.Length: > 1 })` if the user-corroborated intent is "stay on D the whole way through", *or* a weaker variant.
   - `headingChangeAtBranch <= 100 degrees` — catches the 120° overshoot.
2. **Confirm the test fails** against current code.
3. **Apply the fix** in *one* subsystem only. No drive-by changes elsewhere (per `feedback_no_optional_params.md` and global "no speculative features" rule).
4. **Confirm the failing test now passes.**
5. **Run the full Sim test suite** and confirm no regression in:
   - `IssueSfo28rExitTests` (SKW3398),
   - `Sfo28rAllExitsTests` and `OakAllExitsTests` (every-exit smoothness),
   - `SfoRunwayExitTests`, `RunwayExitSpeedTests`, `RunwayExitDoubleDecelTests`, `ElTHighSpeedExitTests`, `ExitKOvershootTests`, `IssueAmxTaxiOvershootTests`,
   - `FilletPathfindingTests`, `NavigatorArcSteeringTests`, `CubicBezierTests`,
   - `GroundConflictConvergenceTests`, `OakGroundE2ETests`, `SfoLineupDiagonalTests`.
6. **Aviation-sim-expert review** of the fix (per CLAUDE.md "Aviation Realism — MANDATORY", taxi turn behavior is in scope).
7. `dotnet build -p:TreatWarningsAsErrors=true` clean. `prek run` clean.

### A note on regression coverage

Whichever subsystem we fix, **also add at least one synthetic unit test** (no recording) that locks in the invariant — for example:

- If the fix is in `GroundArc.MaxSafeSpeedKts` or `SetupSegment`: a unit test that constructs a `GroundArc` with a known radius, sets up `GroundNavigator`, ticks it, and asserts the aircraft never exceeds `ω × R`.
- If the fix is in pathfinder scoring: a unit test on a synthetic 2-taxiway intersection where staying straight is the cheaper route, asserting the chosen route contains no junction arc.

This is the durable regression net — recordings drift over time, synthetic tests don't.

## Critical files

**Read (Phase 1, no edits):**
- `src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs` (Apply, FilletNode, MergeCoincidentNodes, SelectMaxRadius)
- `src/Yaat.Sim/Data/Airport/CubicBezier.cs` (Evaluate, ClosestT, RadiusOfCurvatureFt, ArcLength)
- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` (`GroundArc.MaxSafeSpeedKts`, `GroundArc.MatchesTaxiway`, `GroundArc.SharesTaxiway`)
- `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` (FindRoutes, CostFewestTurns/Shortest/Fastest, WalkTaxiway)
- `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` (SetupSegment, Tick, ComputeArcSteering)
- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs` (StartExitNavigation)
- `src/Yaat.Sim/Phases/Tower/LandingPhase.cs` (lines 400–545, 685–717 — handoff guard)
- `src/Yaat.Sim/AircraftCategory.cs` (`CategoryPerformance` jet constants)
- `docs/landing-and-runway-exit.md` (design invariants)
- `docs/e2e-tdd-issue-debugging.md` (TDD workflow + bundle handling + `ReplayOneSecond` vs `TickOneSecond`)
- `tests/Yaat.Sim.Tests/Simulation/IssueSfo28rExitTests.cs` (pattern to mirror; do not edit)
- `tests/Yaat.Sim.Tests/Simulation/Sfo28rAllExitsTests.cs`, `RunwayExitSpeedTests.cs`, `RunwayExitDoubleDecelTests.cs`, `ElTHighSpeedExitTests.cs`, `ExitKOvershootTests.cs`, `SfoRunwayExitTests.cs`, `IssueAmxTaxiOvershootTests.cs`, `FilletPathfindingTests.cs`, `NavigatorArcSteeringTests.cs`, `CubicBezierTests.cs` (existing regression surface)
- Recent commits: `a172f32`, `1c9db3f`, `b23fb0c`, `ba00cf6`

**Create (Phase 1):**
- `tests/Yaat.Sim.Tests/TestData/sfo-28r-wja1508-fillet-overshoot-recording.yaat-bug-report-bundle.zip` (copy of the bundle)
- `tests/Yaat.Sim.Tests/Simulation/Sfo28rWja1508FilletOvershootTests.cs` (diagnostic + later assertion test)
- `.tmp/sfo-28r-d-branch-{1552,1555}.txt`, `.tmp/sfo-28r-exits.txt`, `.tmp/sfo-twy-d.txt`, `.tmp/wja1508-fillet-diag.log` (diagnostic artifacts; gitignored)

**Edit (Phase 2, only after diagnosis confirms which one):**
- Exactly one of: `FilletArcGenerator.cs`, `CubicBezier.cs`, `AirportGroundLayout.cs` (`GroundArc.*`), `TaxiPathfinder.cs`, `GroundNavigator.cs`, or `LandingPhase.cs`. **Stop and confirm with the user before editing** so the chosen surface matches the diagnostic.
- Possibly one new synthetic unit test file under `tests/Yaat.Sim.Tests/` to lock in the invariant.

## Verification

- [ ] Recording copied to `tests/Yaat.Sim.Tests/TestData/sfo-28r-wja1508-fillet-overshoot-recording.yaat-bug-report-bundle.zip`.
- [ ] Layout inspector outputs for nodes 1552 / 1555, 28R exits, and taxiway D captured to `.tmp/`.
- [ ] Diagnostic test runs, output teed to `.tmp/wja1508-fillet-diag.log`, dumps per-tick state through landing + exit, and surfaces all four max-metrics (arc-speed excess, heading-rate, junction-arc-in-route flag, branch-tick heading change).
- [ ] **Stop point: present diagnostic findings, get user confirmation on the implicated subsystem before editing source.**
- [ ] Failing assertion test added (subset of: arc-speed never exceeded, heading rate within `GroundTurnRate`, no junction arc in route, branch heading change ≤100°).
- [ ] Failing assertion test confirmed failing on current code.
- [ ] Targeted fix applied in exactly one subsystem.
- [ ] Synthetic regression unit test added that locks in the invariant.
- [ ] Failing assertion test now passes.
- [ ] Full `dotnet test tests/Yaat.Sim.Tests` suite passes — in particular all existing fillet/exit/taxi tests listed above.
- [ ] `dotnet build -p:TreatWarningsAsErrors=true` clean.
- [ ] `prek run` clean (format + analyzers, no flags to `dotnet format`).
- [ ] Aviation-sim-expert sanity check (taxi turn behavior, fillet geometry, ground turn rates).
- [ ] If the fix changes behavior visible to users (e.g., taxi paths or exit timing), note it in `docs/yaat-vs-atctrainer.md` and update `docs/architecture.md` if the file tree changed.
