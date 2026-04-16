# RTIS should fail soft (pilot keeps looking) instead of hard

## Bug (user's report)

> RTIS fails hard instead of soft, meaning the command remains in the input buffer, instead of explaining to the RPO why the traffic isn't in sight (which it does), and then "looking..." meaning it will continue to look for that traffic.

## Reproduction

- Recording: `tests/Yaat.Sim.Tests/TestData/s2-oak3-vfr-sequencing-recording.yaat-bug-report-bundle.zip` (if a moment exists where RTIS failed). Otherwise, fabricate state directly.

## Current behavior

`RTIS {callsign}` → `DispatchReportTrafficInSight()` calls the acquisition check. On `!result.Acquired`, it returns `CommandResult(false, "Negative contact, ...")`. The failed result causes the command to stay stuck in the client's input buffer (per user report) and the aircraft makes no further attempt to look.

## Desired behavior

1. On first check: if traffic is acquired, pilot reports "tally / in sight" and command completes (existing success path).
2. On first check: if **not** acquired, pilot reports "unable, looking for traffic" with the reason (e.g., too far, out of view cone, blocked) and the command **persists** on the aircraft.
3. Each subsequent tick, re-check acquisition. When acquired, pilot reports "in sight" and the command completes.
4. The command can be canceled by a new RTIS for a different callsign, or an explicit cancel.

## Suspected code

- `src/Yaat.Sim/Commands/NavigationCommandHandler.cs:1055-1103` — `DispatchReportTrafficInSight()`; change the failure path to mark the command as "persistent looking" and return a success-but-pending result (or whatever pattern matches existing persistent commands; there is **no existing persistent command pattern** per triage, so a new one is needed).
- `src/Yaat.Sim/Commands/CommandDispatcher.cs:541` — dispatch entry for `ReportTrafficInSightCommand`.
- `src/Yaat.Sim/AircraftState.cs` — add fields to track "looking for traffic": callsign, started-at-tick, last-check-tick. Coordinate with `follow-implied-callsign-from-in-sight.md`: once acquired, set `LastReportedTrafficCallsign` there.
- Client side: ensure the command does **not** remain stuck in the input buffer when the aircraft responds with "looking". The client should clear the input on any pilot response, success or pending.

## Acceptance criteria

- Replay (or fabricated) test: issue RTIS when traffic isn't acquirable; assert pilot says "unable, looking"; tick forward until acquirable; assert pilot reports in sight and the persistent-look state clears.
- The client input buffer is cleared after RTIS is dispatched (regardless of immediate acquisition result).
- If RTIS is re-issued with a different callsign before the first resolves, the first is cleared and the second takes over.
- If the target aircraft leaves the simulation while pilot is still looking, the look state silently clears.

## TDD note

Follow `docs/e2e-tdd-issue-debugging.md`.

- Create `tests/Yaat.Sim.Tests/Commands/RtisSoftFailLookingTests.cs`.
- Aviation review: request `aviation-sim-expert`; include the FAA-local-reference reminder from CLAUDE.md. AIM 4-4-15 (visual separation and traffic acquisition phraseology) is relevant; the feedback-memory note "STT is pilot-side input" applies — pilot phraseology matters.
- The "persistent pending command" pattern is new to Yaat; the implementer should also check whether a similar pattern is needed elsewhere (e.g., "report leaving altitude") and coordinate a reusable primitive if so.
