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

## Open decisions

1. Overlay (non-modal, the export pattern) or a modal dialog.
2. Whether steps can fail visibly: show a skipped or failed step (a missing ARTCC config or layout) and leave the dialog open until dismissed, rather than a green finish.
3. Move the layout and ARTCC fetches ahead of the tick gate (pre-parse the airport list, prefetch, then take the gate for the CPU-only populate), so a reload never stalls other rooms.
4. Fetch the roster's ARTCC configs concurrently (`Task.WhenAll`, as the neighbour letters already are); measure cold-cache time per step first.
5. Cancel: none, or a cancel that needs a cleanup path and a second parallel invocation per client.
6. Clean up the orphan room when `CreateRoom` throws or `JoinRoom` returns null (independent of the dialog).
7. Move the client's pre-send scenario parsing off the UI thread.
