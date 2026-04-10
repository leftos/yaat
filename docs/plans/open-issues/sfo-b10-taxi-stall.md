# SFO Ground `TAXI A @B10` — Stall + Wrong-Direction Bugs

## Context

User reports two ground-taxi bugs at SFO (scenario *S1-SFO-2 | Ground Control 28/01*) via bug report bundle at `X:\Downloads\S1-SFO-2 _ Ground Control 28_01.yaat-bug-report-bundle.zip`:

1. **SKW3078** was given `TAXI E A @B10` after landing on 28R, "started taxiing the wrong way and eventually stopped".
2. **DAL2581** was given `TAXI A @B10`, "taxied the right way but still came to a stop without any conflicts ahead of it".

> **Related open plan — read before starting:** `docs/plans/open-issues/fillet-arc-taxi-misbehavior-wja1508.md`. That plan targets the **same bundle** (same `RngSeed 91127251`, same scenario) for a different aircraft (WJA1508, 28R → D exit overshoot) and already names the fillet-arc rewrite as the suspected systemic regression. The B10 stall here likely shares that root cause. **Coordinate: do not duplicate diagnostic work.** If the WJA1508 plan is already in flight, this plan should collapse into a second set of regression assertions on top of the same fix rather than a parallel investigation.

Evidence gathered from snapshots (every 5 s) in the recording:

| Time | SKW3078 | DAL2581 |
|---|---|---|
| t=816 | cmd `TAXI E A @B10` from runway exit (37.61974, −122.37920) | — |
| t=820 → 870 | Route 81 segs; advances 0 → 16; ends stopped at (37.61535, −122.38049), hdg 208° | Still landing |
| t=870 → 1075 | **Stalled ~200s** at seg 16, gs=0, no conflict, no HS | — |
| t=1076 | cmd re-issued `TAXI A @B10`; new route **143 segs** (vs 81) | — |
| t=1080 → 1150 | Moves **northwest** hdg 300° (away from B10!), cur 0 → 14, then stops at (37.61911, −122.38080) with `GroundSpeedLimit=5`, later `=0` | — |
| t=1179 | — | cmd `TAXI A @B10` from runway exit (37.61973, −122.37921) |
| t=1180 → 1260 | stalled | Route 81 segs identical to SKW3078's first route; advances 0 → 16; ends at exactly (37.61535, −122.38049) — **same stall point** |

**Identical 81-seg route both aircraft receive:** `E → A → M1 → M3 → RAMP → node 932` (parking B10). **Stall segment is #16** = `{FromNodeId: 1235, ToNodeId: 1238, TaxiwayName: A}` — an **arc** edge. Neither endpoint is a HoldShort, Spot, or Parking — both are plain `TaxiwayIntersection` nodes. `AssignedTaxiRoute.HoldShortPoints` is empty on both aircraft. Aircraft heading (208°) is aligned with the bearing to node 1238 (~207°).

Relevant node geometry (via Layout Inspector):
- Node 1235 at (37.615237, −122.380561), edges: `1236 via A·T5 [arc]`, `1232 via A [arc]`, `1238 via A [arc]`, `1239 via T5B·A [arc]` — 4 arc edges meeting.
- Edge 1235→1238: 0.0123 nm, `[arc]`.
- Parking B10 at (37.61270762690429, −122.38583176998074, heading 99°) — type `parking` in `sfo.geojson`.

**Recent ground-subsystem history** (via `git log -- src/Yaat.Sim/Phases/Ground/ src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs src/Yaat.Sim/Commands/GroundCommandHandler.cs`) is dense and highly relevant — this bug is almost certainly a regression from the fillet-arc rewrite series:

- `a23b738` add: fillet arc infrastructure — `IGroundEdge`, `GroundArc`, `FilletArcGenerator`, navigator arc following
- `a172f32` feat: enable fillet arcs in production, multi-strategy pathfinder
- `baeb0a3` ref: replace circular arcs with cubic bezier curves in fillet system
- `cfabf2b` ref: `IsRunwayCenterline` rename, navigator arc awareness, fillet investigation
- `2c2ab49` fix: merge coincident fillet nodes, rework `LineUpPhase` to analog navigation
- `a0fa1dd` test: add `CubicBezier` math and navigator arc steering unit tests
- `ba00cf6` ref: remove ground navigator turn anticipation (replaced by fillet arcs)
- `8de3970` fix: taxi-to-spot lands in HoldingInPosition, not AtParking

`ComputeArcSteering` itself was introduced in `a23b738`; turn-anticipation was removed in `ba00cf6`. Node 1235's four edges are *all* `[arc]` in the layout inspector, and the stall segment 16 is an arc. This plan should **explicitly diff these commits** against the suspect code paths before guessing, per the *"Don't tunnel-vision on the symptom"* feedback memory.

Two hypothesized defects:

1. **Stall on arc segment (both aircraft):** On segment 16 (an arc edge on A), `GroundNavigator.Tick` computes `targetSpeed=0` and the aircraft freezes without reaching `NodeArrivalThresholdNm` (0.015 nm). Candidate causes inside `GroundNavigator.cs`: `ComputeArcSteering` (`src/Yaat.Sim/Phases/Ground/GroundNavigator.cs:337`) returning a near-zero `maxSpeedKts` at a bezier endpoint despite the `MinRadiusOfCurvatureFt` floor; **or** `_currentNodeRequiredSpeed` at line 111 becoming 0 from a spurious large turn angle between two arc edges meeting at 1238; **or** a speed constraint in the forward walk (`_speedConstraints`, lines 136–197) that never clears. The `stalledAtThreshold` short-circuit (`line 249`) does *not* fire because the aircraft is still ~121 ft from node 1238 (threshold 91 ft).

2. **Wrong direction after re-issue (SKW3078):** `TaxiPathfinder.PickBestStartEdge` (`src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs:983`) picks the starting edge for taxiway A using *closest destination-node to parking B10* (lines 1004–1020). That tie-breaker ignores the aircraft's **current heading / forward direction of travel**, so when the controller re-issues from mid-taxiway the pathfinder can choose the edge that loops back the way the aircraft came. `GroundCommandHandler.ResolveParkingRoute` does not pass aircraft heading as a hint.

Intended outcome: a failing regression test replaying this recording, a collaborative root-cause pass with the user once diagnostic output is in hand, and then minimal fixes that make the test pass without regressing existing SFO ground coverage.

## Approach

TDD workflow per `docs/e2e-tdd-issue-debugging.md`. Use the bundle directly (RecordingLoader handles `.yaat-bug-report-bundle.zip`). Investigate both bugs in one test class since they share a recording and a scenario. **Diagnostic pass first — do not write a fix until tick-by-tick logs have been reviewed with the user.**

### Step 0 — Read the fillet-arc rewrite first

Before touching any ground code, read the recent commits that rewrote the ground-navigation stack. The stall is on an arc edge at an arc-only intersection — if the bug was introduced by the rewrite, the diff will be faster to read than a tick-by-tick trace.

- [ ] `git show a23b738 -- src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` (arc-following added)
- [ ] `git show ba00cf6 -- src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` (turn anticipation removed)
- [ ] `git show baeb0a3 -- src/Yaat.Sim/Data/Airport/` (cubic bezier replacement)
- [ ] `git show 2c2ab49` (coincident fillet-node merge, analog LineUpPhase)
- [ ] `git log -p -- src/Yaat.Sim/Data/Airport/CubicBezier.cs src/Yaat.Sim/Data/Airport/GroundArc.cs | head -400` (bezier `RadiusOfCurvatureFt`, `MinRadiusOfCurvatureFt`, `ClosestT`, `Evaluate`)
- [ ] `docs/landing-and-runway-exit.md` — callout for GroundNavigator design constraints
- [ ] Search tests for arc-steering coverage already in place: `tests/**/CubicBezier*Tests.cs`, `tests/**/NavigatorArc*Tests.cs`

Goal: come out of Step 0 knowing whether the stall hypothesis is "bezier curvature explodes near endpoint" vs "turn-angle recomputation between two arcs" vs "forward walk back-propagates 0 through an arc" — and whether the wrong-direction regression was already fixed once (`e7aeef1 fix: correct walk direction on multi-segment taxi routes to runway`) and has come back in a new shape.

### Step 1 — Add recording to TestData

- [ ] `cp "X:\Downloads\S1-SFO-2 _ Ground Control 28_01.yaat-bug-report-bundle.zip" tests/Yaat.Sim.Tests/TestData/issue-sfo-b10-taxi-stall-recording.yaat-bug-report-bundle.zip`
- [ ] `pwsh tools/migrate-recordings-v4.ps1` (manifest shows v4 already; script is idempotent — just confirm).

### Step 2 — Create diagnostic test class

Create `tests/Yaat.Sim.Tests/Simulation/IssueSfoB10TaxiStallTests.cs` mirroring the style of `SfoTaxiToParkingStuckTests.cs` (`tests/Yaat.Sim.Tests/Simulation/SfoTaxiToParkingStuckTests.cs:24`):

- Class XML doc: what scenario, what aircraft, what went wrong, timeline.
- Private const `RecordingPath = "TestData/issue-sfo-b10-taxi-stall-recording.yaat-bug-report-bundle.zip"`.
- `LoadRecording()` → `RecordingLoader.Load(RecordingPath)`.
- `BuildEngine()` → `TestVnasData.EnsureInitialized()`, `TestAirportGroundData`, null-return if SFO layout absent, `SimLogBuilder.CreateForTest(output).InitializeSimLog()`.
- Single SFO layout reference from `new TestAirportGroundData().GetLayout("SFO")` for `NearestNodeHelper.Log`.

### Step 3 — Diagnostic `[Fact]`s (no assertions yet)

Three diagnostic facts, each reading `.AssignedTaxiRoute.CurrentSegmentIndex`, the current `Segment`, `IsHeld`, `GroundSpeedLimit`, `GroundSpeed`, `TrueHeading.Degrees`, phase list, and `NearestNodeHelper.Log(...)`. Use `engine.ReplayOneSecond()` (not `TickOneSecond`) so any mid-recording actions still apply. Log every second from the command timestamp for ~90 s, plus a snapshot every 10 s:

- [ ] `Diagnostic_SKW3078_FirstTaxi_StallPoint` — `engine.Replay(recording, 816)` then 90 s tick loop. Expected to capture the 0 → 16 progression and stall; want to see which tick first reports `GroundSpeed=0` on segment 16 and **what the log line shows for brakingLimit / arcSpeedLimit / _currentNodeRequiredSpeed at that tick**. Raise `SimLog` minimum level to `Debug` so the trace logs from `GroundNavigator.Tick` (line 314) appear in xunit output — they already report all the numbers we need.
- [ ] `Diagnostic_SKW3078_Reissue_Direction` — `engine.Replay(recording, 1076)` then 90 s. Log heading each tick and bearing-to-B10 (`GeoMath.BearingTo`). Record the first 5 segments of the new 143-seg route and the taxiway of each.
- [ ] `Diagnostic_DAL2581_Taxi_StallPoint` — `engine.Replay(recording, 1179)` then 90 s. Confirm it follows the same 81-seg route and stalls at seg 16.

Every diagnostic `[Fact]` returns silently on null recording/engine (no `Assert.Skip`).

### Step 4 — Review output with user

**Stop here.** Post the `ReplayOneSecond` trace lines to the user. Together, identify:
- Which term dominates `targetSpeed = Math.Min(...)` at the stall tick (arcSpeedLimit? brakingLimit from `_currentNodeRequiredSpeed`? a forward `_speedConstraints` entry? `GroundSpeedLimit` from conflict detector?).
- Whether the stall is at the segment entry (just set up) or mid-segment.
- For the re-issue bug: which of the 4 candidate edges out of node 1235/nearest-node was chosen, and whether `destinationHint` was non-null.

### Step 5 — Convert diagnostics to failing regression tests

Based on the joint diagnosis, replace the free-form diagnostic loops with explicit assertions that fail on `main`:

- [ ] `SKW3078_TaxiAtoB10_ReachesParkingB10OrAtLeastAdvancesPastSegment16` — after `engine.Replay(816)` then tick ≤ 600 s, assert `CurrentSegmentIndex > 16` OR `ac.Phases?.CurrentPhase is AtParkingPhase`.
- [ ] `DAL2581_TaxiAtoB10_ReachesParkingB10OrAtLeastAdvancesPastSegment16` — same for DAL2581 at t=1179.
- [ ] `SKW3078_RetaxiAtoB10_InitialHeadingTowardB10` — after `engine.Replay(1076)` then tick 30 s, assert `|NormalizeAngleDiff(ac.TrueHeading.Degrees − bearingToB10)| < 90` (aircraft should be pointing generally toward B10, not away from it). Also assert segment count of the new route is within a sane ratio of the original 81 (`≤ 120` is a generous ceiling that still catches the 143 observed).

Run `dotnet test --filter FullyQualifiedName~IssueSfoB10` and confirm all three FAIL on the current code.

### Step 6 — Fix, verify, cleanup

Only after Steps 3–5 deliver failing evidence, fix the defects. Likely fix surfaces (confirm with user before editing):

- **Wrong-direction:** In `GroundCommandHandler.ResolveParkingRoute` pass `aircraft.TrueHeading` down to the pathfinder, then in `TaxiPathfinder.PickBestStartEdge` (`src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs:983`) prefer the candidate whose departure bearing is within ~90° of aircraft heading when a valid heading is supplied. Mirror the same fix into `PickBestWalkEdge` (line 1058) if Step 4 shows the wrong direction is picked deeper in the walk instead of at the start.
- **Stall:** depends on Step 4. Candidates: (a) clamp `ComputeArcSteering` returning `maxSpeedKts` to at least the category min taxi speed, (b) recompute `_currentNodeRequiredSpeed` using fillet-arc-aware bearings, (c) if `_speedConstraints` holds a stale 0 entry, make sure forward walk terminates past a hold-short only *after* including one legitimate entry and not back-propagates it to the current segment.

Verify:
- [ ] `dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --filter FullyQualifiedName~IssueSfoB10` — all three green.
- [ ] `dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --filter "FullyQualifiedName~Taxi|FullyQualifiedName~Ground|FullyQualifiedName~SfoHold|FullyQualifiedName~Exit"` — no regressions in sibling ground/exit/taxi suites (`OakGroundE2ETests`, `SfoTaxiToParkingStuckTests`, `SfoHoldShortTaxiwayTests`, `SfoGroundSpeedUntilTests`, `IssueAmxTaxiOvershootTests`, `IssueSfo28rExitTests`, `ExitRightTaxiwaySelectionTests`, `TaxiAirborneRejectionTests`).
- [ ] `dotnet build -p:TreatWarningsAsErrors=true` clean.
- [ ] `prek run` clean.

## Critical files

| Path | Why |
|---|---|
| `tests/Yaat.Sim.Tests/Simulation/IssueSfoB10TaxiStallTests.cs` | **New** — diagnostic + regression tests |
| `tests/Yaat.Sim.Tests/TestData/issue-sfo-b10-taxi-stall-recording.yaat-bug-report-bundle.zip` | **New** — copied bundle |
| `tests/Yaat.Sim.Tests/Simulation/SfoTaxiToParkingStuckTests.cs` | Style reference for RecordingLoader + BuildEngine pattern |
| `tests/Yaat.Sim.Tests/Helpers/NearestNodeHelper.cs` | `Describe` / `Log` for tick-by-tick node context |
| `tests/Yaat.Sim.Tests/Helpers/RecordingLoader.cs` | Handles `.yaat-bug-report-bundle.zip` transparently |
| `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` | `SetupSegment` (:57), `Tick` (:241), `ComputeArcSteering` (:337) — stall candidates |
| `src/Yaat.Sim/Phases/Ground/TaxiingPhase.cs` | Owns segment advancement + hold-short insertion |
| `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` | `PickBestStartEdge` (:983), `PickBestWalkEdge` (:1058), `FindRoute` (:277) — wrong-direction candidate |
| `src/Yaat.Sim/Commands/GroundCommandHandler.cs` | `ResolveParkingRoute` — where heading hint needs to be threaded in |
| `src/Yaat.Sim/Commands/GroundCommandParser.cs` | Confirms `@B10` parses as `DestinationParking` (`ParseTaxiTokens`, :205) |
| `tools/Yaat.LayoutInspector/` | Used for node 1235/1238/20/1241… inspection |

## Verification

Happy-path end-to-end:

1. `cp` bundle into TestData, then `pwsh tools/migrate-recordings-v4.ps1` (idempotent).
2. `dotnet test tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj --filter FullyQualifiedName~IssueSfoB10` — three diagnostic tests run, output traces show the stall + wrong-direction behavior.
3. Review trace with user, diagnose root cause.
4. Convert diagnostics to failing assertions; re-run filter — three failures on main.
5. Apply targeted fix(es). Re-run filter — three greens.
6. Re-run broader ground/taxi filter plus `dotnet build -p:TreatWarningsAsErrors=true`.
7. Optional cross-check with Layout Inspector: `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/sfo.geojson --node 1235` and `--path 1235 A` to confirm the fixed route matches the expected shortest A-path from the runway-exit area to node 932 (B10).
