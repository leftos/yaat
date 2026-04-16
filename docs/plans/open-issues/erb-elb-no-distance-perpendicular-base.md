# ERB/ELB without a distance should turn perpendicular to extended centerline

## Bug (user's report)

> ERB / ELB without a distance should have the aircraft turn perpendicular to the extended centerline so as to "enter base" from present distance, instead of aiming for a fixed distance base.

## Reproduction

- Recording (optional): `tests/Yaat.Sim.Tests/TestData/s2-oak3-vfr-sequencing-recording.yaat-bug-report-bundle.zip` — check if any ERB/ELB actions exist in the 13 recorded actions. If not, fabricate via unit-level tests.
- Repro: issue `ERB` or `ELB` without a distance arg to an aircraft in downwind/pattern; observe that the aircraft turns toward the pattern's standard base-turn point rather than flying a perpendicular base from its current position.

## Current behavior

`ERB`/`ELB` with a distance arg projects the base-entry point onto the extended centerline at that distance from the threshold. Without a distance arg, it falls back to `wp.BaseTurnLat/Lon` — the standard pattern turn point — which may be far from where the aircraft currently is, producing a diagonal leg rather than a perpendicular base.

## Suspected code

- `src/Yaat.Sim/Commands/PatternCommandHandler.cs:768-808` — `GetEntryPoint()`; the null-`finalDistanceNm` branch returns `wp.BaseTurnLat/Lon`. This is the branch to rework.
- `src/Yaat.Sim/Phases/Pattern/BasePhase.cs:28, 48-59` — `FinalDistanceNm`; if set, projects threshold point and stores as `_thresholdLat/Lon`; if null, uses standard `Waypoints.ThresholdLat/Lon`. The null path needs to produce a perpendicular-from-present base.
- `src/Yaat.Sim/Commands/DepartureCommandParser.cs:248-277` — parses ERB/ELB args; no-distance path returns command with `FinalDistanceNm = null`. No change needed at parse level — the null intent is preserved.

## Proposed geometry

When `FinalDistanceNm` is null:

1. Compute the runway extended centerline as a line through threshold at runway true course.
2. Compute the aircraft's current position's perpendicular foot on that centerline — call this `P`.
3. The base entry point is `P` itself (or slightly short of it, at a standard turn-anticipation offset).
4. The base leg heading is `runway_course ± 90°` (right base = runway + 90° turning back to final on the right side; mirror for left base).
5. The resulting base leg length equals the aircraft's current cross-track distance from the extended centerline — "enter base from present distance", matching the user's intent.

Edge cases:
- Aircraft already past the threshold (upwind of it) — treat as an error, same as current behavior for impossible ERB/ELB.
- Aircraft on the wrong side of the centerline for the requested base (ERB when aircraft is to the left) — either reject or interpret as "proceed perpendicular + fly through centerline" depending on existing convention; match whatever ERB-with-distance does today.

## Acceptance criteria

- ERB/ELB without distance: aircraft turns ~perpendicular to runway centerline from current position (tolerance: within 10°), then turns onto final once centerline is reached.
- ERB/ELB with distance: unchanged behavior — regression test required.
- Resulting base leg length equals current cross-track distance within tolerance.
- `docs/COMMANDS.md` updated for ERB and ELB (both Quick Reference and Detailed sections).
- `docs/yaat-vs-atctrainer.md` updated if behavior diverges from ATCTrainer.

## TDD note

Follow `docs/e2e-tdd-issue-debugging.md` for any replay portion; otherwise unit-level tests against `GetEntryPoint` / `BasePhase`.

- Create `tests/Yaat.Sim.Tests/Commands/ErbElbNoDistanceTests.cs`.
- Cover: ERB/ELB without distance from several aircraft offsets; ERB/ELB with distance (regression); both runway sides (left vs right base); aircraft already past threshold (error case).
- Aviation review: request `aviation-sim-expert`; include the FAA-local-reference reminder from CLAUDE.md. AIM 4-3-3 (traffic pattern geometry) is relevant.
