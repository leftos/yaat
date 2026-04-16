# FOLLOW breaks when leader transitions to pattern entry

## Bug (user's report)

> N436MS reported unable to catch up to N9225L on a FOLLOW after 25L entered downwind. I don't think following as the leading aircraft transitions into pattern entry is working correctly.

## Reproduction

- Recording: `tests/Yaat.Sim.Tests/TestData/s2-oak3-vfr-sequencing-recording.yaat-bug-report-bundle.zip`
- Scenario: S2-OAK-3 (1) | VFR Sequencing
- Follower: **N436MS**
- Leader: **N9225L** (transitioning into pattern at OAK 25L)
- Approximate time: TBD — write a diagnostic test that logs `FollowingCallsign`, leader phase, leader/follower speed, and gap distance each tick; find the moment the follower stops closing.

## Suspected code

- `src/Yaat.Sim/Commands/CommandDispatcher.cs:1553-1604` — `TryAirborneFollow()` gates on leader being in `DownwindPhase` / `BasePhase` / `FinalApproachPhase`. No branch for `PatternEntryPhase`.
- `src/Yaat.Sim/Phases/Pattern/PatternEntryPhase.cs` — does not consult `FollowingCallsign` or call `AirborneFollowHelper.GetAdjustedSpeed()`, so a follower whose leader enters this phase gets no speed adjustment.
- `src/Yaat.Sim/Phases/AirborneFollowHelper.cs:51-100` — `GetAdjustedSpeed()`; pattern phases use this but PatternEntryPhase does not.
- `src/Yaat.Sim/Phases/Pattern/VfrFollowPhase.cs:69-100` — `OnTick()` has a 30s gap-runaway cancel. Confirm this isn't firing spuriously when leader is in PatternEntryPhase.

## Acceptance criteria

- While the leader is in `PatternEntryPhase`, the follower continues to adjust speed to close the gap (same rule as when the leader is established on downwind).
- No spurious `VfrFollowPhase` gap-runaway cancellation during a normal PatternEntryPhase → pattern transition.
- Test asserts: at time T (leader in PatternEntryPhase), follower's commanded speed is leader-adjusted (e.g., ≥ leader_gs + closure_delta when behind), and the follow state remains active.

## TDD note

Follow `docs/e2e-tdd-issue-debugging.md`.

- Create `tests/Yaat.Sim.Tests/Simulation/FollowBreaksOnLeaderPatternEntryTests.cs`.
- `RecordingLoader.Load(RecordingPath)` handles the `.yaat-bug-report-bundle.zip` transparently.
- Use hybrid replay with snapshots (`RecordingLoader.OpenArchive` + `ReadSnapshotAt`) if full replay from t=0 diverges — see `docs/e2e-tdd-issue-debugging.md` §5b.
- Aviation review: request the `aviation-sim-expert` agent to confirm VFR follow/pattern-entry behavior against AIM 4-3-3 (traffic pattern operations). Include the FAA-local-reference reminder from CLAUDE.md.
