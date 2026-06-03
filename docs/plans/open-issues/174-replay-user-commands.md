# Issue #174 — Replay should also replay user-sent commands

> **Status:** initial investigation (seed plan; not yet implemented).
> **Labels:** enhancement, replay. **Source:** Discord thread, filed 2026-06-03.
> **Decision:** surface replayed commands via **server-side `TerminalBroadcast`** (real-time, all
> clients, matches live mode).

## Symptom

Replay reconstructs aircraft state from snapshots, but the controller's recorded commands never
reappear in the terminal during playback — you see aircraft move without seeing the commands that
moved them.

## Root cause (confirmed)

Replay is **server-side**. `RecordingManager` (yaat-server) drives
`ReplayRange` / `ReplayFromStartTo(…, ApplyRecordedAction)` for rewinds, and during real-time
forward playback `ApplyPlaybackActions(currentElapsed)` pumps actions through `ApplyRecordedAction`
(`src/Yaat.Server/Simulation/RecordingManager.cs:295–307`). `RecordedCommand`
(`src/Yaat.Sim/Simulation/RecordedAction.cs`) carries `ElapsedSeconds, Callsign, Command, Initials,
ConnectionId` and is applied to the sim — but **no `TerminalBroadcast` is emitted**, so clients
never render the command line.

The client already renders live commands as `[Command] AP SIA31: …` via
`MainViewModel.AddTerminalEntry` (`MainViewModel.Aircraft.cs`), fed by the server's
`TerminalBroadcast`. The replay path just needs to feed the same channel.

## Key files

- `src/Yaat.Server/Simulation/RecordingManager.cs` — `ApplyRecordedAction`, `ApplyPlaybackActions`
  (line ~295), and the rewind paths (`ReplayRange`/`ReplayFromStartTo`, lines ~164/170/263).
- `src/Yaat.Sim/Simulation/RecordedAction.cs` — `RecordedCommand` record.
- Server room terminal-broadcast helper (the one behind live `[Command]` lines) — confirm the exact
  method to reuse.
- `src/Yaat.Client/ViewModels/MainViewModel.Aircraft.cs` — `AddTerminalEntry` (client render path,
  no change expected).

## Approach (server-side, per decision)

When `ApplyRecordedAction` processes a `RecordedCommand` during **forward** playback, emit a
synthetic `TerminalBroadcast` (Kind `Command`, the recorded `Initials`/`Callsign`/`Command`) to the
room. **Do not** flood the terminal during bulk rewind replays — `ReplayRange`/`ReplayFromStartTo`
reconstruct state across a span and would dump hundreds of lines. Surface commands only on the
real-time forward-playback pump (or backfill a bounded recent window after a rewind). Confirm the
exact seam and the broadcast helper before wiring.

## Verification

- Replay a known recording (e.g. the SFO bundle) and assert clients receive `[Command]` terminal
  entries at the recorded timestamps.
- Rewind and resume; confirm commands are not duplicated or spammed across the bulk replay.

## Open questions

- Decide rewind behavior: suppress entirely during bulk replay, or backfill the last N seconds of
  commands so the terminal isn't empty after a seek.
- Confirm whether non-`Command` recorded actions (AmendFlightPlan, weather, spawns) should also
  surface, or only `RecordedCommand` for this issue.
