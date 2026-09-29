# Async scenario load with a step-by-step progress display

Status: draft. The map below is from a read-only exploration of both repos (2026-09-28); the open decisions at the end go to the user before any brief.

## What happens today

- **Create Room** (`MainViewModel.Rooms.CreateRoomAsync` → hub `CreateRoom` + `JoinRoom`) is in-memory and fast. It fetches nothing, so it needs no progress steps. On a failure after `TrainingRoomManager.CreateRoom` registers the room, or when `JoinRoom` returns null, the room is left behind as an orphan.
- **Load Scenario** (`MainViewModel.Scenario.SendScenarioToServer` → hub `LoadScenario`, also `StartLiveSession`) is one long hub call. The client parses the scenario JSON on the UI thread before the await (`ScenarioSetupPlan.Create`, `ScenarioIdentity.ResolveFromJson`, `FilterByDifficulty`), and afterwards rebuilds the aircraft list on the UI thread. No busy state is shown beyond the command disabling itself.
- **Server order** (`ScenarioLifecycleService.LoadScenarioSeededAsync`), all inside `room.GuardAsync`, which holds the room's tick gate:
  1. Unload the previous scenario.
  2. Parse the scenario and build aircraft (`ScenarioLoader.Load`). Inside it, **airport layouts are fetched synchronously** (`AirportGroundDataService.FetchGroundData`, `GetAwaiter().GetResult()` in a `GetOrAdd`, 30 s HTTP timeout each, one airport after another, cached per server).
  3. ARTCC configs for the scenario's ARTCC and the roster's neighbours (`EnsureScenarioArtccConfigsLoadedAsync`, a sequential `foreach`, 15 s timeout, 30 min TTL); neighbour ERAM letters are already fetched in parallel.
  4. Populate the room (layout warm, spawn, presets, generators, strips, TDLS, coordination broadcasts).
  5. Re-apply the room's weather (no fetch).
  6. Build the result DTOs.
- NavData and CIFP load once at server start and are not load steps.
- **A reload into a running room stalls every room's tick** while the fetches run: the global tick loop awaits the room's gate.
- **Silent failures**: an ARTCC config 404, timeout or parse error, a neighbour config failure, and a missing airport layout are logged and the load reports success. The user finds out later (no positions, no strips, SECTOR NOT ADAPTED). A thrown exception leaves the room empty, since the previous scenario was already unloaded.
- **Precedents to reuse**: the caller-only progress callback `ExportRecordingProgress` (`TrainingHub.ExportRecording` captures `Clients.Caller`; the client's `ServerConnection` event → `MainViewModel.Timeline.OnExportRecordingProgress` → `Dispatcher.UIThread`), and the export overlay in `MainWindow.axaml` (status text and a progress bar over the main panel).
- SignalR runs one invocation per client connection at a time, so a cancel call from the same connection would queue behind the running `LoadScenario`.

## Shape (pending the decisions below)

- A `ScenarioLoadProgress(LoadStepDto)` callback to the caller only. It is threaded as an `IProgress<LoadStepDto>` from `TrainingHub.LoadScenario` / `StartLiveSession` through `RoomEngine.LoadScenarioAsync` into `ScenarioLifecycleService`, and emitted at about six stages: unload, parse and spawn, airport layouts (the running airport named), ARTCC configs (the running ARTCC named), populate, finish. Each step carries a state: running, done, skipped or failed, with a reason.
- A client overlay listing the stages with a tick, a warning or a cross per stage, built from the export overlay.
- The hub contract change goes into `docs/training-hub-contract.md`; the client handler into `docs/client-mainviewmodel.md`.

## Decisions (user 2026-09-28)

1. **Overlay**, not a modal dialog: the export overlay, extended to list the stages. A second load while one runs is refused.
2. **Failed steps show and the overlay stays open** until dismissed: a skipped or failed step (a missing ARTCC config or layout) shows a warning with its reason, never a green finish.
3. **Prefetch ahead of the tick gate**: pre-parse the scenario's airport and ARTCC list, fetch outside `GuardAsync`, then take the gate for the CPU-only populate, so a reload never stalls other rooms. A load or unload that slips in between prefetch and populate is guarded.
4. **No cancel.**

Defaults taken without asking:
- Fetch the roster's ARTCC configs concurrently (`Task.WhenAll`, as the neighbour letters already are), once cold-cache time per step is measured.
- Clean up the orphan room when `CreateRoom` throws or `JoinRoom` returns null.
- Move the client's pre-send scenario parsing off the UI thread.

Next: a design pass for the prefetch (how `ScenarioLoader.Load` takes prefetched layouts instead of calling `GetLayout`, and what that does to determinism and the Yaat.Sim contract), then briefs.
