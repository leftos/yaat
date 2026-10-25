# Coaching session logs

One file per session, named `NNN-YYYY-MM-DD-<scenario>.md` (`001-2026-10-25-oak-ground-easy.md`), written as the session runs by the `atc-coaching` skill. A log is a record in time, so it keeps its date and who ruled.

## Template

```markdown
# Session NNN — <scenario>, <position>

- Date: YYYY-MM-DD
- Trainer: owner; student: Claude
- Scenario: <name / file>, seed <n>, runways in use <...>, wind <...>
- Triggers armed: pilotCalls, warnings, newAircraft, groundStops; heartbeat <n|off>; scan every ~10 sim s

## Exchanges

### E1 — t=<sim s>, <trigger | initiative | miss>, <callsign>

- Picture: …
- Candidates: …
- Proposal: `<command>` — why …
- Verdict: approved | corrected to `<command>` | rejected — trainer's words …
- Result: sent/success, readback …
- Lesson: … (→ ground-lessons.md L<n>)

## Misses

- t=<sim s>: what the trainer saw that Claude did not; scan-checklist gap or trigger gap.

## Tool friction

- …
```
