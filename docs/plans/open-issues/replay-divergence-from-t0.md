# Replay-from-t=0 diverges from recorded snapshots

## Context

YAAT bug-bundle recordings (`*.yaat-bug-report-bundle.zip`, v4 archive format) bundle
three things needed to reconstruct a session deterministically: (1) the scenario
JSON + RNG seed + weather, (2) the timestamped action log, and (3) per-second
state snapshots captured during the original playthrough. The promise stamped on
`docs/e2e-tdd-issue-debugging.md` is that `engine.Replay(recording, T)` re-runs
the scenario from `t=0`, applies actions in order, ticks physics deterministically,
and lands on the same state the snapshot at time `T` recorded.

That promise is broken today. For the KFB7 bundle
(`tests/Yaat.Sim.Tests/TestData/kfb7-capp-hilpt-recording.yaat-bug-report-bundle.zip`,
S3-NCTC-3 / Area C Complete) we hit the divergence at the *very first* aircraft
of interest:

| State | Recording snapshot at t=1180 | `engine.Replay(recording, 1180)` |
|---|---|---|
| Position | (37.7387, -121.3361) — ~3 nm NW of MOD | (37.7991, -121.3530) — ~7 nm NNW of MOD |
| TrueHeading | 110° (SE, lined up on DCT MOD) | 44.9° (NE, completely off route) |
| NavigationRoute | `[MOD]` | `[LIN, LIN]` |
| Phases | null | null |

By the time the recording's CAPP I28R fires at t=1183, the test environment is
on a different aircraft state with different inputs, so the resulting CAPP
clearance routing doesn't match what the user actually saw.

This forced the KFB7 hold-in-lieu fix
(`tests/Yaat.Sim.Tests/Simulation/IssueKfb7CappHilptMissingTests.cs`) to use
**hybrid replay** (snapshot restore + `ReplayRange` from snapshot time forward)
instead of the documented full-replay pattern. Hybrid replay is supposed to be
the exception for fixes that change pre-T behaviour (per `docs/e2e-tdd-issue-debugging.md`
§5b "Canonical case for hybrid: WAIT presets"). Today it's the only thing that
works for *any* recording with non-trivial controller actions.

## Root cause

`SimulationEngine.ReplayCommand` (`src/Yaat.Sim/Simulation/SimulationEngine.cs:1604`)
is the action-applier wired into `ReplayTo` / `ReplayRange` by default. It
explicitly skips two whole categories of recorded commands:

- `IsTrackCommand` (line 1642): track ownership, handoffs, accept, drop, redirect,
  scratchpads — anything in `TrackCommandHandler`. Skipped because they're
  "server-only" (the engine in Yaat.Sim doesn't own multi-controller track
  state).
- `IsCoordinationCommand` (line 1648): RD / RDH / RDR / RDACK / RDAUTO. Same
  reason.

For KFB7 the very first action in the timeline (`t=134 KFB7 AS 3Y ACCEPT`)
is a track-acceptance command. That's how the controller takes ownership of
the inbound aircraft from the next-position-up. With the command silently
skipped during replay, the aircraft is never accepted, which means many
downstream commands targeted at it (`DCTF`, `DM`, `EAPP`, `FH`, `CAPP`, `GA`,
`DCT MOD`, `CAPP I28R`) are processed in a slightly different order and
context, or rejected, or applied to an aircraft whose internal track
association is wrong. The errors compound silently — there's no log warning
when a recorded action is skipped, and no diagnostic that the resulting state
diverges from any captured snapshot.

The ScheduleWakeup-style "ReadSnapshotAt + RestoreFromSnapshot + ReplayRange"
pattern works because it sidesteps all of `t=0..startTime`, restoring the exact
state the user saw and only re-running actions from the snapshot time forward.
But it doesn't fix the underlying problem; it just papers over it.

## What "robust replay" should look like

Two reasonable directions, in increasing scope:

### Option A — Make track/coordination replays succeed (small)

Add a default action-applier path for `Yaat.Sim.Tests` that processes track and
coordination commands enough to keep `aircraft.Track`/scratchpad state consistent
during replay, instead of dropping them on the floor. The skip is justified
*for the server replay path* (which has its own action applier), but tests
running in `Yaat.Sim.Tests` get the default applier and need the in-engine
shadow of track state.

Concretely: introduce `Yaat.Sim.Simulation.TrackReplayApplier` that mirrors the
state mutations from `TrackCommandHandler` without the SignalR fan-out. Wire it
in as the default for `ReplayCommand` when `IsTrackCommand` is true, instead
of `return;`.

Verify with the same KFB7 bundle: `engine.Replay(recording, 1180)` should land
on the snapshot's NavigationRoute=[MOD], position (~37.74, -121.34), heading 110°.

### Option B — Snapshot-backed replay verification (broader)

Even with Option A, replay can drift from a snapshot for any number of reasons:
unrelated bug fixes after the recording was made, physics constant tweaks,
dispatch-order reorders, RNG consumption changes. Today this is invisible and
caught only when a downstream test assertion happens to fail.

A robust replay would: at every snapshot timestamp, read the captured snapshot
out of the archive and **diff it against the engine's current state**. Produce
a structured warning when key fields drift (position by >X nm, heading by >Y°,
NavigationRoute fix list, AssignedAltitude / AssignedHeading, ActiveStarId, etc.).
The goal isn't to enforce bit-equality — the recording is allowed to be stale —
but to *surface* the divergence so authors of recording-based tests don't write
assertions against state that doesn't actually reproduce.

Could be opt-in: `engine.Replay(recording, T, verifySnapshots: true)` returns a
list of `(timestamp, fieldDiffs[])` that tests can inspect.

## Reproducer

```bash
python tools/bug_bundle.py snapshot \
  tests/Yaat.Sim.Tests/TestData/kfb7-capp-hilpt-recording.yaat-bug-report-bundle.zip \
  --at 1180 --callsign KFB7 --out .tmp/kfb7-recorded-1180.json
```

Then run a test along the lines of:

```csharp
var recording = RecordingLoader.Load("…/kfb7-capp-hilpt-recording.yaat-bug-report-bundle.zip");
var engine = BuildEngine();
engine.Replay(recording, 1180);
var ac = engine.FindAircraft("KFB7");
// Assert state matches .tmp/kfb7-recorded-1180.json — fails today.
```

Currently `ac.Targets.NavigationRoute` is `[LIN, LIN]` (replay) vs `[MOD]`
(recorded). Fix Option A and the route, position, heading should all match.

## Out of scope

- Snapshot schema migration — `SnapshotSchemaMigrator` already handles versioning
  on the snapshot side. The drift here is purely in replay-vs-snapshot, not
  snapshot-format-vs-code.
- The hybrid replay pattern itself stays useful for the documented "WAIT preset"
  case (fixes that intentionally change pre-T behaviour). Option A just makes
  full replay the default again for everything else.
- Server-side replay — `Yaat.Server` has its own action applier that handles
  track/coordination via the room engine. This issue is specifically about the
  in-engine `Yaat.Sim` default applier used by tests.

## References

- `src/Yaat.Sim/Simulation/SimulationEngine.cs:1604` — `ReplayCommand` and the
  `IsTrackCommand` / `IsCoordinationCommand` skip
- `src/Yaat.Sim/Commands/TrackCommandHandler.cs` — what state the skipped track
  commands would mutate
- `tests/Yaat.Sim.Tests/Simulation/IssueKfb7CappHilptMissingTests.cs` — current
  workaround using hybrid replay
- `docs/e2e-tdd-issue-debugging.md` §5b — documented hybrid-replay pattern,
  intended for the narrower "WAIT preset" use case
