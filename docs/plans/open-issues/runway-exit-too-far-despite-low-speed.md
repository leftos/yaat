# Runway exit too far despite low speed (OAK 25L picks J instead of G/H)

## Bug (user's report)

> N9225L exited all the way at J for some reason, instead of G or H, when it was clearly going slow enough to exit at either of those exits earlier.

## Reproduction

- Recording: `tests/Yaat.Sim.Tests/TestData/s2-oak3-vfr-sequencing-recording.yaat-bug-report-bundle.zip`
- Scenario: S2-OAK-3 (1) | VFR Sequencing
- Aircraft: **N9225L**
- Airport / runway: **OAK / 25L**
- Approximate time: late in the recording (488s total). Use a diagnostic test that logs, each tick during rollout: groundspeed, `ComfortableBrakingMultiplier` target, currently-resolved candidate exit, distance to candidate, and nearest ground-layout node (use `NearestNodeHelper` per §5 of the TDD doc).

## Suspected code

- `src/Yaat.Sim/Phases/Tower/LandingPhase.cs:795-889` — `ResolveNextCandidate()` is where an exit is chosen. Line 448-642 `TickRollout()` runs the main loop. Line ~564 uses `ComfortableBrakingMultiplier = 1.5` for defaults.
- `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs:235-333` — `TryFindExitAhead()`; continuous exit search with progressive relaxation (taxiway → side → any); excludes occupied nodes.
- `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs:816-976` — `FindAdjacentHoldShort()`; BFS from centerline node. Scoring at line 941:
  ```
  score = totalDist + parkingBias + anglePenalty - highSpeedBonus
  ```
  where `parkingBias = AverageNearestParkingDistanceNm * ParkingProximityWeight` (line 917), `anglePenalty = 10.0` if exit >100° and no explicit preference (928), `highSpeedBonus = 0.15` if exit ≤45° and no explicit preference (938).
- `docs/landing-and-runway-exit.md` — read this first. Design is analog (non-node-based). Covers braking strategy and anti-patterns.

## Investigation

1. `dotnet run --project tools/Yaat.LayoutInspector -- tests/Yaat.Sim.Tests/TestData/oak.geojson --exits 25L` — list G/H/J candidates with angles and distances.
2. Replay bundle to rollout; for each tick log groundspeed, chosen exit, and the components of the score (distance, parkingBias, anglePenalty, highSpeedBonus).
3. Hypothesis to test: parking-proximity bias dominates when parking is near J, overriding the earlier-exit preference even when G/H are well within comfortable braking. If so, the fix is either to down-weight `ParkingProximityWeight` once the aircraft is already slow enough for an earlier exit, or to gate parking-bias on "exit not already comfortably reachable".
4. Memory `feedback_exit_toward_parking.md` documents a user-validated bias toward parking-side. The fix must preserve *side* bias without over-penalizing closer exits on the wrong side.

## Acceptance criteria

- When a VFR aircraft is rolling out at low speed on OAK 25L with G and H comfortably reachable, the chosen exit is **G or H** (whichever matches the existing side-toward-parking bias), **not J**.
- Scenarios where J was the correct choice (e.g., aircraft still fast, G/H occupied, or explicit EL instruction) still pick J. Test must include a "still picks J when appropriate" case to avoid regressions.

## TDD note

Follow `docs/e2e-tdd-issue-debugging.md`. Ground-exit bugs in particular:

- Create `tests/Yaat.Sim.Tests/Simulation/OakRunwayExitTooFarTests.cs`.
- Use `NearestNodeHelper` for per-tick ground context (§5 of the TDD doc).
- Use `ReplayOneSecond()` after `Replay()` so actions are re-applied as physics ticks.
- Aviation review: request the `aviation-sim-expert` agent for runway-exit realism; include the FAA-local-reference reminder from CLAUDE.md. Consider references in AIM 4-3-20 (runway exit procedures).
- Visual verification: use LayoutInspector `--html` with the recorded tick CSV to visually confirm the aircraft's path against the expected exit.
