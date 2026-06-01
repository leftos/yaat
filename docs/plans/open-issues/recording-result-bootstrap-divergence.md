# ApplyRecordingResult bypasses the scenario-bootstrap router

## Problem

The client has a "three scenario-activation paths, one router" invariant: the loader RPC
(`ApplyScenarioResult`), the other-client broadcast (`OnScenarioLoaded`), and the JoinRoom/reconnect
snapshot (`ApplyRoomState`) all funnel through **`ApplyScenarioBootstrap(ScenarioBootstrap)`** so a
scenario-derived field is applied identically on every path (see
[../../client-mainviewmodel.md](../../client-mainviewmodel.md)).

`ApplyRecordingResult` (`src/Yaat.Client/ViewModels/MainViewModel.Timeline.cs:529`) — the
recording-load / rewind path — does **not** go through that router. It re-implements the setup inline:
sets `ActiveScenarioId` / `ActiveScenarioName` / `ActiveScenarioPrimaryAirportId`, propagates the
primary airport to `Radar` + `_commandInput`, calls the 5-arg `ApplySimState(...)` directly (`:537`),
sets `_studentPositionType`, and rebuilds the `Aircraft` collection.

## Risk

A new scenario-derived field wired into `ApplyScenarioBootstrap` (or the `ScenarioBootstrap`
projection) will silently **not** apply after a recording load or a rewind — exactly the
"wire it into only one path" failure mode the router was created to prevent. The two code paths drift
independently, and the gap only surfaces as a replay/rewind-specific bug.

## Fix options

1. **Preferred:** project `RewindResultDto` into a `ScenarioBootstrap` and call
   `ApplyScenarioBootstrap`, then apply the recording-only extras (`ElapsedSeconds` / `IsPlayback` /
   `TapeEnd`) explicitly afterward. One router, no drift.
2. **Minimum:** if a full refactor is too risky, add a test asserting parity between
   `ApplyRecordingResult` and `ApplyScenarioBootstrap` for every shared field, so future divergence
   fails CI.

## Notes

- Client-only (`Yaat.Client`); no server or `Yaat.Sim` change.
- Surfaced by the doc-coverage drafting pass while writing
  [../../client-mainviewmodel.md](../../client-mainviewmodel.md) (which documents the three-path router
  and scopes the recording-load path out).
