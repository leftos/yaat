# OAK north-field taxi spinning — handoff

## Status

- Failing E2E test landed in commit `cce8cf34` as the contract
- 2 `[Theory]` assertions skipped (no-spin + forward-progress); 2 diagnostic `[Fact]`s active
- Bundle installed at `tests/Yaat.Sim.Tests/TestData/oak-northfield-taxi-spinning-recording.yaat-bug-report-bundle.zip`

## Repro

```powershell
timeout 60 dotnet test tests/Yaat.Sim.Tests --filter "FullyQualifiedName~OakNorthField" 2>&1 | tee .tmp/repro.log
```

The diagnostic facts dump per-tick state with `GroundCommandHandler` / `TaxiPathfinder` / `GroundNavigator` debug logging enabled, so the trace lands in test output without re-running the bundle.

When ready to iterate on the fix, un-skip the assertions in `tests/Yaat.Sim.Tests/Simulation/OakNorthFieldTaxiSpinTests.cs` (remove the `Skip = ...` arg from both `[Theory]` attributes) and treat them as the contract:

- `TaxiOut_DoesNotSpinNearlyFullCircle`: ≤320° absolute heading rotation, ≤200° signed, in 30 s after the TAXI
- `TaxiOut_MakesForwardProgress`: ≥500 ft displacement in 60 s after the TAXI

## What's broken

Two north-field aircraft from the S1-OAK-P practical-exam bundle:

| Callsign | Parking | TAXI                  | Movement after 60s | Cumulative abs / signed (30s) |
| -------- | ------- | --------------------- | ------------------ | ----------------------------- |
| EDG320   | SIG4    | `TAXI D C B HS 28R`   | 340 ft (need ≥500) | 454° / 36°                    |
| TWY801   | GA3     | `TAXI C B HS 28R`     | 74 ft (need ≥500)  | 600° / **−600°**              |

EDG320 partially recovers because Issue 1's entry-alignment slow-turn (commit `40ddba26`) handles the heading-snap component of its symptom. **TWY801's spin is the deep bug** — it stays stuck on the ramp doing two near-complete revolutions in 60 s.

## Investigation surface

User TAXI commands flow through `TaxiPathfinder.ResolveExplicitPath -> WalkTaxiway -> BridgeToTaxiway -> BfsToTaxiway` (NOT `FindRoute` / A*). See `project_taxi_pathfinder_two_paths.md` in `C:\Users\Leftos\.claude\projects\X--dev-yaat\memory\`.

LayoutInspector confirms north-field parking edges are fillet-arc pairs:

```powershell
$OAK = "$env:LOCALAPPDATA\yaat\cache\airports\OAK.geojson"
dotnet run --project tools/Yaat.LayoutInspector -- $OAK --node 641   # SIG4
dotnet run --project tools/Yaat.LayoutInspector -- $OAK --node 621   # GA3
dotnet run --project tools/Yaat.LayoutInspector -- $OAK --taxiway D
dotnet run --project tools/Yaat.LayoutInspector -- $OAK --taxiway C
dotnet run --project tools/Yaat.LayoutInspector -- $OAK --html .tmp/oak-northfield.html
```

Both parking nodes have **two parallel RAMP edges** to two different junction nodes, both fillet-tagged `phase-d-shorten` / `phase-d-shorten-direct`:

- SIG4 (641) → 1332/1333, both bearing 218.7°
- GA3 (621) → 1222/1224, both bearing 209.1°

The previous `HasFlipFreeFirstStep` work covered the GA3/GA7 spawn cluster (see `OakGaSpawnTurnAroundTests`) but apparently doesn't cover this case. Per `project_fillet_arc_natural_forward.md`: fillet arcs have a natural-forward bezier direction; reverse-traversal flips tangents 180° at endpoints, which is the most likely root cause class here.

## Candidate fix surfaces

In rough order of likelihood:

1. `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` — `BfsToTaxiway` (~line 2766), `BridgeToTaxiway` (~line 2140). Extend `HasFlipFreeFirstStep` scoring to the SIG4-cluster ramps. Look at how the GA3/GA7 fix selects between the two parallel RAMP edges; the same logic should apply here but apparently doesn't fire for SIG4.
2. `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` — `WalkTaxiway` (~line 1759). Start-node bridge selection might be picking the reverse-traversed arc.
3. `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs` — `TickStraight` (~lines 608-748). Only relevant if the route is geometrically valid but pure-pursuit still orbits — unlikely given the symptom matches the fillet-pair pattern.

## Files to read first

- `docs/e2e-tdd-issue-debugging.md` — workflow for replay-based debugging
- `docs/architecture.md` — file roles for ground/taxi
- Memory:
  - `project_taxi_pathfinder_two_paths.md`
  - `project_fillet_arc_natural_forward.md`
  - `project_chord_chain_aggregate_turn.md`
  - `project_fillet_arc_investigation.md`
  - `project_fillet_cleanup_root_cause.md`
- `tests/Yaat.Sim.Tests/Simulation/OakGaSpawnTurnAroundTests.cs` — closest analog (parking-spawn fillet-arc family). The GA3/GA7 fix template lives here.
- `tests/Yaat.Sim.Tests/Simulation/Swa1089SpawnTaxiSpinTests.cs` — full-replay assertion pattern.

## Risks / regressions to guard

- `OakGaSpawnTurnAroundTests` (existing GA3/GA7 fix) must keep passing. If the fix is anywhere in `BfsToTaxiway`/`BridgeToTaxiway` scoring, run that test as a guard.
- `OakTaxiJcSpinTests` / `project_chord_chain_aggregate_turn.md` — different code (`EffectiveTurnAngleAt` in `GroundNavigator`), low conflict risk, but run as a guard.
- Broad taxi suite: `OakAllExitsTests`, `OakCross28RHoldShortTests`, `OakFullLifecycleTests`, `Skw3078TaxiEAtoB10RouteTests`, `IssueFllDal880TaxiBacktrackBTests`, `SfoM2MultiTurnTaxiTests`, `IssueAmxTaxiOvershootTests`. Run `pwsh tools/test-all.ps1` before declaring done.

## What landed this session (Issue 1 — partially related)

Commit `40ddba26`: GroundNavigator now injects a `PathPrimitiveSlowTurn` from the aircraft's current pose to the segment start tangent when:

1. Route entry only (`route.CurrentSegmentIndex == 0`)
2. Heading delta > 90°
3. Segment length > 2 × alignment chord (so short segments aren't dominated by the displacement)

This handles the heading-snap component of the user's complaint and is what makes EDG320 partially recover. TWY801's deeper routing bug is what this plan is about.
