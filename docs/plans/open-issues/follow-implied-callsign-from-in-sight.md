# FOLLOW should default to last in-sight callsign

## Bug (user's report)

> The FOLLOW command requires a callsign, but if an aircraft has reported a callsign in sight, it should be implied (last one mentioned to be in sight).

## Reproduction

Not tied to a specific moment in the recording — a behavior-spec change. Use the recording bundle only if a convenient repro exists; otherwise fabricate via unit-level tests on `TryAirborneFollow` with a pre-seeded `AircraftState`.

- Recording (optional): `tests/Yaat.Sim.Tests/TestData/s2-oak3-vfr-sequencing-recording.yaat-bug-report-bundle.zip`

## Suspected code

- `src/Yaat.Sim/AircraftState.cs:225-226` — currently has `HasReportedTrafficInSight` (bool). Add `LastReportedTrafficCallsign` (string?) alongside it. **Coordinate with `rtis-soft-fail-looking.md` — that brief also wants persistent traffic state.** Whichever ships first defines the field; the other reuses it.
- `src/Yaat.Sim/Commands/NavigationCommandHandler.cs:1055-1103` — `DispatchReportTrafficInSight()`; on successful acquisition, store the callsign on the aircraft in addition to setting the bool.
- `src/Yaat.Sim/Commands/CommandDispatcher.cs:1553-1560` — `TryAirborneFollow()`; when the FOLLOW command has no callsign arg, default to `aircraft.LastReportedTrafficCallsign` if set. Fail with a clear message ("Unable, no traffic in sight") if neither is provided.
- FOLLOW parser — locate by grepping for `FollowCommand`. Make the callsign arg optional at parse time so a bare `FOLLOW` is accepted and reaches the handler. Check `src/Yaat.Sim/Commands/PatternCommandParser.cs` or `DepartureCommandParser.cs`.
- `docs/COMMANDS.md` — update the FOLLOW entry (both Quick Reference and Detailed sections).
- `docs/yaat-vs-atctrainer.md` — note the divergence if ATCTrainer still requires the callsign.

## Acceptance criteria

- After `{callsign} in sight` is reported, a subsequent bare `FOLLOW` targets that callsign.
- If no prior in-sight, bare `FOLLOW` returns a clear error without crashing the command input.
- Explicit `FOLLOW {callsign}` still works, overriding the stored last-in-sight.
- The stored value updates when a new, different in-sight is reported.

## TDD note

Follow `docs/e2e-tdd-issue-debugging.md` for any replay portion. For the unit-level spec:

- Create `tests/Yaat.Sim.Tests/Commands/FollowImpliedCallsignTests.cs` (or similar location matching existing command tests).
- Cover: implied-from-TI, implied-from-TALLY, implied-from-RTIS, explicit override, no-prior-error, newer-TI-overrides-older.
- Aviation review: request `aviation-sim-expert`; include the FAA-local-reference reminder from CLAUDE.md. AIM 4-4-15 (visual separation) and 7110.65 §7-4-1 (visual separation) are relevant.
