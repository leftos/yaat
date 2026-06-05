# Plan: Synchronize live command application with the tick loop

## Problem (root-caused)

Live command application and the server tick loop run **unsynchronized**, and the
live loop advances the clock differently from replay. Two concrete defects:

1. **1-tick replay-determinism edge.** The live server loop
   (`SimulationHostedService.RunTickLoop`, yaat-server) increments
   `scenario.ElapsedSeconds` at the **end** of each sim-second (after PostPhysics).
   Every other tick driver increments at the **start** (before PrePhysics):
   `SimulationEngine.TickOneSecond`, `SimulationEngine.ReplayOneSecond`, and the
   `RoomEngineTestHarness.Tick`. So for the "same" simulated second, live's
   `TickPrePhysics` (ProcessReleaseQueue / generators) runs with `ElapsedSeconds`
   one less than replay's. A tick-loop entry scheduled with `FireAt == ElapsedSeconds`
   ("fire ASAP" — e.g. Path B auto-spaced `REL`'s `i=0` `ScheduledRelease`) fires one
   tick differently live vs replay, shifting its `World.Rng` draw to a different stream
   position → downstream divergence. Commands with immediate effect (FH/CM writing
   `ControlTargets`) don't diverge.

2. **Latent thread-safety race.** `TrainingHub.SendCommand` → `RoomEngine.SendCommandAsync`
   runs on SignalR threads with **no lock** against `RunTickLoop`. A command can mutate
   scenario/world state mid-physics. The only locks in the tick path are TDLS-specific
   (`tdls.Gate`).

**Why the harness can't reproduce it:** the harness increments at the start (like
replay), so harness-"live" == replay. The divergence lives only between the real
server's end-increment loop and replay.

## Design (approved: lock + apply immediately, keep synchronous result)

- **Per-room async lock.** Add a `SemaphoreSlim(1,1)` to `TrainingRoom` (e.g. `TickGate`).
- **Tick loop holds it.** In `RunTickLoop`, acquire the room's gate around each room's
  per-second processing (PrePhysics → Physics×4 → PostPhysics + the in-loop weather/METAR/
  history/playback work). **Move `scenario.ElapsedSeconds += 1` to the TOP of the
  per-second loop** (before `ProcessPrePhysics`) so live matches replay/TickOneSecond.
- **Recorded mutators hold it.** Every async entry point that mutates recorded state
  acquires the gate around the mutation + `Record(...)`, then releases **before** async
  broadcasts (broadcasts read a post-mutation snapshot; a later tick mutating further is a
  benign display race, not a determinism one). Entry points: `SendCommandAsync` (584), the
  CRC command paths (140/179/336), spawn/delete (`HandleSpawnAircraftAsync`,
  `HandleDelete`), weather load/clear, flight-plan amend, setting changes — anything that
  calls `Record(...)`.
- Result: commands apply in a between-tick gap at a well-defined `ElapsedSeconds`; live and
  replay apply at the same tick-relative point. Synchronous `CommandResultDto` is preserved
  (caller waits at most one tick's processing time — single-digit ms).

### Increment-alignment ripple (verify)

Moving the live increment to the top relabels each sim-second by +1 vs today and shifts
every `ElapsedSeconds`-gated event (spawnDelays, generator intervals, preset timeOffsets,
position-history `%5`, weather-timeline `GetWeatherAt`, `MetarIssuer.Tick`,
`ApplyPlaybackActions`, `PlaybackEndSeconds`). Confirm each in-loop consumer still behaves;
this is a benign 1-second relabel (no external users / no recording-compat concern).

## Tests (TDD — reproduce first)

The existing harness `Tick()` increments at the start, so it already matches the fixed
behavior and can't show the bug. Reproduce by adding a **live-style** harness tick that
mimics the old server loop (increment at END), then:

1. **Reproducing test (red before fix):** run a Path-B auto-spaced `REL` scenario through
   the live-style end-increment ticks, record, then `RewindAsync`/replay and assert the
   released aircraft's `SpawnAtSeconds` (and downstream `World.Rng`) match. Diverges with
   the old loop; matches after aligning the increment.
2. **Lock/serialization test:** a command issued "mid-tick" (simulated) applies in the
   between-tick gap, never mid-physics; final state == replay.
3. Regression: full `test-all.ps1`.

## Files (yaat-server)

- `src/Yaat.Server/Simulation/TrainingRoom.cs` — add `TickGate` SemaphoreSlim.
- `src/Yaat.Server/Simulation/SimulationHostedService.cs` — hold gate per room; move
  `ElapsedSeconds += 1` to top of the per-second loop.
- `src/Yaat.Server/Simulation/RoomEngine.cs` — acquire gate around the recorded-mutation
  entry points (SendCommandAsync + CRC paths + spawn/delete/weather/FP).
- `tests/Yaat.Server.Tests/Harness/RoomEngineTestHarness.cs` — add live-style end-increment
  tick helper for reproduction.
- New `tests/Yaat.Server.Tests/CommandTickSyncTests.cs`.
- `docs/architecture.md` (both repos) + `CHANGELOG.md` (yaat).

## Empirical confirmation (2026-06-04)

Reproduced and pinned in `yaat-server/tests/Yaat.Server.Tests/CommandTickSyncTests.cs`
(uses `RoomEngineTestHarness.TickLive` — an end-increment tick helper added to the harness —
and `minimal-held-departures-two.json`):

- **`EndIncrementLiveTiming_DivergesFromReplay_OnPathBRelease`**: with end-increment ticks, the
  released `SWA500`'s `SpawnedAtSeconds` is **t=43** live vs **t=44** on replay — a confirmed
  **1-second divergence**. The i=0 `ScheduledRelease` (FireAt==now) fires one tick earlier live.
- **`StartIncrementTiming_MatchesReplay_OnPathBRelease`**: with start-increment ticks (the fixed
  timing), live and replay match exactly (spawn times + positions).
- The jitter VALUE is identical (same `World.Rng` stream position); only the `ElapsedSeconds` the
  spawn is computed against differs. So **positions only diverge if the released aircraft moves**
  (here it sits lined up) or if a downstream `World.Rng` consumer is shifted — the edge is real but
  subtle, matching the original "out of scope / track separately" framing.

**Testability gap (key):** the harness already increments at the START (so harness == replay and
the existing suite can't see the bug). The defect lives only in `SimulationHostedService.RunTickLoop`,
which no test drives. The robust fix should therefore **unify the per-second tick** — have the
server loop and the harness/engine share one start-increment tick path — so the increment is correct
by construction and testable, then add the per-room lock. A bare edit to `RunTickLoop:136` would fix
the behavior but stay unverified by the suite.

## Implementation status (2026-06-04)

**Part 1 — tick unification (DONE, the confirmed determinism fix):**
- Added `RoomEngine.AdvanceOneSecond()` — one per-second tick: increment-at-start + PrePhysics +
  physics sub-ticks + PostPhysics.
- `SimulationHostedService.RunTickLoop` now calls it (live server increments at start, matching replay).
- `RoomEngineTestHarness.Tick()` routes through it too, so the harness faithfully represents the
  server and `StartIncrementTiming_MatchesReplay` now guards the real server path.
- 720 server tests pass; characterization tests green.

**Part 2 — per-room lock (DONE, thread-safety + async-race robustness):**
- Added a non-reentrant `SemaphoreSlim` gate to `TrainingRoom` with `EnterTickGateAsync()` /
  `ExitTickGate()` (tick loop) and `GuardAsync(...)` helpers (mutations).
- `SimulationHostedService.RunTickLoop` holds the gate per room across the whole per-second
  processing (extracted into `ProcessRoomSecond`), so a command cannot interleave mid-physics.
- Acquired at the **outermost boundaries only**, never inside `RoomEngine`, so the non-reentrant
  semaphore never nests (the **footgun**: `RoomEngine` mutators and replay/playback callbacks like
  `Engine.AmendFlightPlan` run under an already-held gate; gating them would deadlock):
  - Every mutating `TrainingHub` SignalR method wraps its single `RoomEngine` mutation in
    `room.GuardAsync(...)` (broadcasts/manifest builds stay outside; `LoadRecording` gates only the
    load, not the chunk upload). `RequestNewBeaconCode`/`RequestFlightStripForAircraft`/`TakeControl`
    became `async` to gate.
  - `CrcClientState.HandleInvocation` gates the whole invocation dispatch (extracted into
    `DispatchInvocationAsync`) whenever a room is bound — one choke point covers all CRC mutations.
- Tests: `CommandTickSyncTests` Part 2 — the gate blocks a mutation while held and runs it after
  release, serializes concurrent mutations (never overlap), and is released when a mutation throws.

This was implemented to address the "PCM8702 didn't depart 28L, it was stuck" report: the departure
was stuck live but departed cleanly in deterministic replay/scenario-run
(`Issue2Pcm28LStuckTests`), pointing at the mid-physics command race as the only remaining
live↔replay divergence vector.

## Notes

- Builds on the landed REL determinism fix (`ReleaseJitterRng` + baked `SpawnJitterSeconds`).
  That fix made Path A deterministic; this fixes the Path B i=0 edge and the general property.
- No aviation behavior changes (mechanical timing/threading); aviation review not required,
  but verify scenarios behave via `test-all.ps1`.
