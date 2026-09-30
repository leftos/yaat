# Async scenario load with a step-by-step progress display

Status: mapped, decided, prefetch designed and ruled; ready for briefs 1–6 in the brief split.

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

Briefs 1 and 2 shipped: `HttpCacheResult.RefreshFailed` (set after any network failure or timeout, also when nothing is cached, so null content plus `RefreshFailed` is "unreachable"), `AirportLayoutDownloader.FetchGeoJsonAsync` (returns `HttpCacheResult`), and `ScenarioResourceManifest` (`docs/scenario-loading-and-generation.md` § The resource manifest). Brief 3 shipped: `AirportGroundDataService.PrefetchAsync` and `ArtccConfigService.EnsureLoadedAsync` outcomes, single-flight (`docs/server-rooms-and-hub.md`); both outcomes carry the cached copy's last-changed time for the stale-copy warnings, whose wording is now "…, last changed {stamp}Z". Next: brief 4. Open for brief 4: the manifest does not record per airport whether it needs a full ground map (the map-warning ruling's primary / `Parking` / ground-coordinates test), so brief 4 adds that to the manifest or derives it itself.

## Prefetch design

Paths are yaat-server's (`D:/yaat-server/src/Yaat.Server/…`) unless they start with `src/Yaat.Sim` or `src/Yaat.Client`. No timing in this section is measured; the first cold-cache load under brief 4 measures every step (see **Measurement**).

### The load splits into prepare and commit

Today's `ScenarioLifecycleService.LoadScenarioSeededAsync` runs whole inside `room.GuardAsync`. It becomes two halves:

- **Prepare** (`PrepareScenarioAsync`, no gate held): read the scenario JSON into a resource manifest, fetch every resource the manifest names, then run `ScenarioLoader.Load` on a fresh `SimulationEngine`. None of this touches the room: `LoadSeeded` already builds its own engine, world and seeded RNG streams, and `room.ActiveSim` is assigned only inside `PopulateRoom`. It returns a `PreparedScenario` (engine, `ScenarioLoadResult`, seed, session start, the per-step outcomes).
- **Commit** (`CommitPreparedScenario`, under the gate, CPU only): `ExecuteUnloadScenario` for the previous scenario, `PopulateRoom`, `RecordSeededSessionSettings`, the CRC broadcasts, the weather re-apply and the DTO build, all as today.

The unload moves from the start of the load into the commit. A scenario whose JSON cannot be read therefore no longer empties the room: prepare fails at its first step and the room keeps running what it had. The previous scenario ticks on through the whole prepare.

`RoomEngine.LoadScenarioAsync` (and `LoadScenarioSeededAsync`, the headless soak's entry) keeps its signature and composes the two halves without the gate, with `LoadProgress.None` as the reporter, so the existing callers (91 matching lines across yaat-server's `src` and `tests`, definitions included) stay as they are. The hub goes through a new `RoomEngine.LoadScenarioGuardedAsync(json, rates…, progress)`, which holds the room's load flag, awaits prepare, then takes `Room.GuardAsync` for the commit alone. `TrainingHub.LoadScenario` stops wrapping the whole load in `GuardAsync`. `StartLiveSession` takes the same shape: its ARTCC ensure and the airport check move into prepare; `SetLiveTrafficEnabled`, the ceiling, the filter, `Resume` and the optional seek stay after the commit, under the gate, as today.

### Resources per room and per scenario

**Per room**: nothing. `CreateRoom` fetches nothing, and a room's weather is JSON the client already sent (`TrainingRoom.WeatherSourceJson`, re-applied in the commit with no fetch).

**Per process**: NavData and the CIFP files load once at server start (`ServerApp`), before any room exists. They are not load steps.

**Per scenario**, read from the scenario JSON by a new pure `ScenarioResourceManifest.FromJson(json)` in `src/Yaat.Sim/Scenarios/`, which deserializes with `ScenarioLoader`'s own options and reuses the loader's airport-selection rules, so the two cannot drift:

| Resource | Found from | Fetched by |
|---|---|---|
| The scenario's ARTCC config | top-level `artccId` | `ArtccConfigService.EnsureLoadedAsync` (vNAS `/api/artccs/{id}`, disk cache `artcc-{id}.json`) |
| Neighbour ARTCC configs | every distinct `atc[].artccId` that differs from `artccId` (the roster's cross-ARTCC entries, as `EnsureScenarioArtccConfigsLoadedAsync` reads them today) | the same, one call per ARTCC |
| Neighbouring centres' ERAM letters | each loaded config's `facility.neighboringFacilityIds` that are ARTCC ids | nested inside each config load (`ReadNeighborCenterNasIdsAsync`, already `Task.WhenAll`, 1-day disk TTL) |
| Primary airport layout | `primaryAirportId` | `AirportGroundDataService` (vNAS `/api/training/airports/{FAA}/map`, disk cache `cache/airports/{FAA}.geojson`) |
| Each aircraft's airport layouts | per `aircraft[]`: `airportId`, `flightPlan.departure`, `flightPlan.destination`; for a `Parking` spawn the loader's chain `airportId ?? primaryAirportId ?? flightPlan.departure`; for a `Coordinates` / `FixOrFrd` spawn the chain `airportId ?? departure ?? destination ?? primaryAirportId` (delayed aircraft included, as `WarmAircraftGroundLayouts` covers them today) | the same |
| VFR arrival generator targets | `vfrArrivalGenerators[].directTo` when `NavigationDatabase.TryResolveAirport` accepts it | the same |
| Airports named in presets | every `presetCommands[]` token that `NavigationDatabase.TryResolveAirport` accepts (an approach, landing or `DEST` at a field no flight plan names) | the same |
| CIFP procedure tables | every manifest airport (`GetSid` / `GetStar` / `GetApproach` parse lazily per airport on first access, a local file scan) | warmed in prepare so neither the load nor the first tick pays the scan; the caches are `ConcurrentDictionary`, so warming off the tick thread is safe |

Airport ids are keyed by `AirportLayoutDownloader.ToFaaCode`, so `KOAK` and `OAK` fetch once. IFR arrival generators and overflight generators fly to the primary airport (`SimulationEngine.Generators.cs` reads `scenario.PrimaryAirportId`) and add nothing. The scenario JSON itself is not a step: the ARTCC-tab path fetches it with `GetScenarioJsonById` before the difficulty prompt, and a file load has it already.

For a live session the manifest is the creator's ARTCC (`Room.CreatorArtccId`) and the requested primary airport.

### Order and concurrency

1. **Read**: deserialize and build the manifest. Nothing else starts until it succeeds.
2. **ARTCC configs** and **airport layouts** run concurrently with each other, and within each step every item runs concurrently (`Task.WhenAll`, the roster default already decided). A GeoJSON parse is CPU work on the thread pool, which the tick loop also runs on, so parses within one load run one airport at a time while the downloads overlap; widen that only once the measurement says the tick loop is unaffected.
3. **Build aircraft** starts when the layouts finish, whether or not the ARTCC step has: `ScenarioLoader.Load` reads layouts and NavData but no ARTCC config. The CIFP warm-up runs at its start.
4. **Set up the room** starts when steps 2 and 3 are both finished: it takes the gate and runs the commit, whose `InitializeTrackPositions`, `ConfigureBeaconCodePool` and `engine.InitializeFromArtcc` read the configs.
5. **Re-apply weather**, still inside the commit.
6. **Start live traffic** (live session only), after the commit, under the gate.

### Where the work runs and how the tick gate waits

Prepare runs on the hub invocation's thread-pool continuation, awaiting I/O, with the GeoJSON parses and `ScenarioLoader.Load` on pool threads. It never holds the room's gate, so `RoomTickLoopService.RunTickLoop`, which awaits each room's gate in turn (`room.EnterTickGateAsync`), never waits on a fetch: this room and every other room tick on through the whole prepare. The gate is taken once, for the commit, which is CPU only. The tick loop waits on it for as long as `PopulateRoom` and the DTO build take (unmeasured).

The ground-data and ARTCC services change so that the fetch is shared and reports how it went:

- `AirportGroundDataService.PrefetchAsync(airportId)` returns a `LayoutFetchOutcome` (`Loaded`, `LoadedFromStaleCopy`, `NoMap`, `Unreachable`, `Unparseable`, with the message) and stores the entry in the same `_cache` that `GetLayout` reads. Concurrent prefetches of one airport share one in-flight task (`ConcurrentDictionary<string, Task<…>>`); today's `_cache.GetOrAdd(faaCode, FetchGroundData)` can run its factory twice under a race. After a prefetch, `GetLayout` for that airport is a dictionary hit, so `ScenarioLoader.Load` keeps calling `IAirportGroundData.GetLayout` unchanged: the Yaat.Sim contract (`IAirportGroundData`, `ScenarioLoader.Load`'s signature) does not change. A cache miss that still reaches the blocking `FetchGroundData` logs a warning naming the airport and the caller's thread, so a gap in the manifest is visible in the server log.
- `HttpFileCache.GetOrRefreshAsync` (Yaat.Sim) gains `HttpCacheResult.RefreshFailed`, set when a network failure or timeout served the on-disk copy, so a stale copy can be told apart from a fresh one; `AirportLayoutDownloader` gains a `FetchGeoJsonAsync` that returns content, `NotFound` and `RefreshFailed` together. `GetGeoJsonAsync` stays for its other callers.
- `ArtccConfigService.EnsureLoadedAsync` returns an `ArtccFetchOutcome` (`Loaded`, `LoadedFromStaleCopy`, `NotFound`, `Unreachable`, `Unparseable`, plus the neighbour centres whose ERAM letter could not be read). Two defects are fixed with it, because concurrent fetches expose both: `EnsureLoadedAsync` stamps `_lastLoadedUtc` before its await, so a second caller within the window returns before the config exists and resolves every position against null (two rooms loading the same cold ARTCC hit this today); and `_configs` is a plain `Dictionary` written from async continuations while other threads read it. The fix is one shared in-flight task per ARTCC and a `ConcurrentDictionary` for `_configs`. The existing callers (`TrainingHub.GetArtccFacilityTree`, `RoomEngine.StartLiveSessionAsync`, `CrcClientState.Session`) ignore the outcome and compile unchanged.

### Progress event contract

One new server→client event, sent to the caller of `LoadScenario` / `StartLiveSession` only (captured as `ISingleClientProxy caller = Clients.Caller` before the first await, as `ExportRecording` does). Each event carries the **whole** step table, so a lost or reordered event cannot desync the client, and a `Sequence` lets it drop a stale one. Sends are fire-and-forget with a logged fault continuation: a caller who disconnected mid-load must not fail the load.

| Invoke string | Payload | `ServerConnection` event |
|---|---|---|
| `ScenarioLoadProgress` | `ScenarioLoadProgressDto` | `ScenarioLoadProgress` — the whole step table at each step change; caller-only; the last one has `IsComplete = true` and matches `LoadScenarioResult.Steps` |

DTOs, declared in `Dtos/TrainingDtos.cs` and, with the same JSON property names, in `src/Yaat.Client.Core/Services/ServerConnection.cs`, registered in `YaatHubJsonContext` (the event payload is not a method return type, so `HubJsonContractTests` will not catch a missing registration):

```
ScenarioLoadProgressDto(string LoadId, int Sequence, string ScenarioName, bool IsComplete, List<LoadStepDto> Steps)
LoadStepDto(string Id, string Label, string State, string? Detail, List<string> Problems)
```

`LoadScenarioResult` / `LoadScenarioResultDto` gain `List<LoadStepDto> Steps`, the final table, so the RPC result alone is enough to render the finished overlay. `LoadId` is a new `Guid` ("N" format) per load. `ScenarioName` is empty until the read step finishes.

`State` is one of six strings: `pending`, `running`, `done`, `warning` (finished, and something it needed is missing: the load went on without it; `Problems` says what), `failed` (the load stopped here), `notNeeded` (nothing to do, shown grey, never keeps the overlay open). Only `read`, `populate` and the live session's `artcc` and `live` can be `failed`; every missing resource is a `warning`, which is how decision 2's skipped step reaches the user. The overlay closes itself when the table is complete and every step is `done` or `notNeeded`; any `warning` or `failed` keeps it open until dismissed.

Steps, with their display text verbatim (`{…}` is filled in):

| Id | Label | `Detail` while running | `Detail` when finished |
|---|---|---|---|
| `read` | Read scenario | | `{scenario name}` |
| `artcc` | ARTCC configuration | `{done} of {total}: {ids still running}` | `{ids, comma-separated}` |
| `layouts` | Airport layouts | `{done} of {total}: {ids still running}` | `{done} of {total} airports` |
| `aircraft` | Build aircraft | | `{n} aircraft: {immediate} now, {delayed} delayed, {deferred} not placed` |
| `populate` | Set up the room | `Waiting for the room`, then `Replacing the current scenario` | `{student position callsign}`, or none |
| `weather` | Re-apply weather | | `{weather name}`; `notNeeded` with `No weather loaded` |
| `live` (live session only) | Start live traffic | | |

A live session's `aircraft` step is `notNeeded` with `A live session has no scripted aircraft`.

`Problems` texts:

- `read`, failed: `The scenario JSON could not be read: {parser message}. The room keeps its current scenario.`
- `artcc`, warning: `{ID}: not found on vNAS (HTTP 404). Positions in {ID} will not resolve.` · `{ID}: vNAS unreachable and nothing cached. Positions in {ID} will not resolve.` · `{ID}: vNAS unreachable; using the copy cached {yyyy-MM-dd HH:mm}Z.` · `{ID}: the configuration could not be read ({message}). Positions in {ID} will not resolve.` · `{ID}: no ERAM letter for neighbouring center {NBR}; a handoff to {NBR} answers SECTOR NOT ADAPTED.`
- `layouts`, warning: `{APT}: no ground map on vNAS. Aircraft at {APT} cannot taxi or park.` · `{APT}: vNAS unreachable and nothing cached.` · `{APT}: vNAS unreachable; using the copy cached {yyyy-MM-dd HH:mm}Z.` · `{APT}: the ground map could not be read ({message}).` (Which airports turn the step amber is open question 3.)
- `aircraft`, warning: each of `ScenarioLoadResult.Warnings` verbatim (for example `N123AB: No ground layout for SQL`). They also still reach the terminal as `Warning` lines, as today.
- `populate`, warning: `ATC position {id} not found in {artcc}; it won't appear in the controllers list` (today's text) · `Student position {id} not found in {artcc}; strips, beacon banks and the STARS display settings are not set up.` (today silent) · `Arrival generator {id} skipped: {reason}`, with today's log reasons `no primaryAirportId`, `no runway specified`, `runway {rwy} not found at {apt}` (today logged only).
- `populate`, failed: `The room could not be set up: {exception message}. The previous scenario was already unloaded; load again.` · `The room was closed while the scenario loaded.`
- `weather`, warning: `The room's weather could not be re-applied: {message}`.
- `live`, failed: today's refusal texts (`Live traffic is not enabled on this server`, `Position {id} not found in {artcc}`, `Unknown airport {id}`, the enable and filter messages).

The hub-contract rows and this section go into `docs/training-hub-contract.md` when brief 4 lands; the client handler into `docs/client-mainviewmodel.md` with brief 5.

### Failure handling per step

- **read**: fatal. `LoadScenarioResult.Success = false` with the problem as the first warning; nothing is fetched, the room is untouched.
- **artcc**: never fatal for a scenario load; the step turns `warning`, the load goes on, and whatever the missing config would have resolved shows up again as `populate` problems (unresolved positions). A config served from the stale disk copy is a warning too, since vNAS edits since then are missing. For a live session the creator's ARTCC is required: no config is a `failed` step and the refusal the live session returns today.
- **layouts**: never fatal. Each airport's outcome is kept; the aircraft that needed a missing layout are deferred by the loader exactly as today (`Parking (…)` deferral reasons), and those reasons appear under `aircraft`.
- **aircraft**: `ScenarioLoader.Load` does not throw on bad aircraft; it warns and defers. An exception from it is a bug and fails the load at this step with the room untouched, since the unload has not run.
- **populate**: an exception after the unload leaves the room without a scenario, as today, but now says so in the overlay and the result. The commit first checks the room is still registered; a room retired or force-closed during prepare is not repopulated.
- **weather**: warning only.

Each step logs its duration and outcome (`Scenario load {LoadId} step {Id}: {State} in {ms} ms`).

### A second load, a joiner, and the other lifecycle calls mid-load

`TrainingRoom` gains a load flag: `bool TryBeginLoad(string initials)` (an `Interlocked` compare-exchange), `void EndLoad()`, and `string? LoadingBy`. `LoadScenarioGuardedAsync` takes it before prepare and releases it in a `finally` after the commit and the `ScenarioLoaded` broadcast.

- **A second `LoadScenario` or `StartLiveSession` in the same room while one runs** (from any member; the loader's own client is already serialized by SignalR's one-invocation-per-connection default) is refused at once: `Success = false`, first warning `A scenario is already loading in this room (started by {initials}). Wait for it to finish.`
- **Unload, confirm unload, restart, rewind (`RewindTo`, `RewindFromSnapshot`) and `LoadRecording`** while the flag is held are refused with `The room is loading a scenario. Try again when it has loaded.`, in each method's failure DTO, or as a `HubException` for the ones returning `Task`. This is decision 3's guard: nothing can swap the scenario between prepare and commit. Commands (`SendCommand`) still reach the previous scenario while it runs; the commit's unload discards them with its tape.
- **`RetirePausedRoomsAsync`** skips a room whose flag is held, and the commit's registered-room check covers an admin force-close.
- **A client joining mid-load** gets today's `RoomStateDto`, built under the gate: the previous scenario (or none), since prepare holds no gate and changes no room state. It is already in the room group when the commit's `ScenarioLoaded` arrives, so it converges on the new scenario. Whether it, and the other members, also see the progress is open question 2.
- **The loader disconnecting mid-load**: the load runs to the end (no cancel), progress sends to the closed connection are dropped, and the room group still receives `ScenarioLoaded`.

### Determinism

Nothing new is recorded, and nothing needs to be. The load still draws its RNG streams in the same order: `ScenarioLoader.Load` on the fresh engine's `World.Rng`, then `PopulateRoom`'s draws. Only the wall-clock moment the draws happen moves. `sessionStartUtc` is read once when prepare begins and carried to the commit, and the recording stores it as today, so a replay reproduces the magnetic-model day.

The fetched resources do shape sim state: layouts decide spawn poses, deferrals, `GroundSpawnSnap` and every taxi; the ARTCC config decides positions, TCPs, beacon banks (so the beacon codes), strip bays, TDLS configs, coordination lists and autotrack owners; the neighbour letters decide ERAM handoff resolution. None of them is a recorded action, today or after this change. An exported recording already carries the ARTCC config and the referenced layouts (`artcc-config.json.br`, `layouts/`, `airport-geojson/`), which is what a client replay reads. The server's own restart, rewind and recording load re-read its live caches through `ReloadForRewindAsync`, so an edit on vNAS between a load and a rewind can make the rewind diverge. That exposure exists today and prefetch neither causes nor widens it; open question 6 asks whether to close it.

Prefetch makes the loader read the same layouts it reads today, from a warm cache rather than a cold one, so a live load and a reconstruction of it see identical inputs. The determinism test in the list below pins that.

### Does this close the MAIN.md secondary-airport line?

In part. The line's first remedy, "Pre-warm the layouts a scenario's arrivals can name at load", is done by the manifest, off the tick thread: every airport the scenario JSON names, including preset-only and VFR-generator airports that `WarmAircraftGroundLayouts` does not see today, is fetched before the commit. What stays open is an airport first named at runtime (a typed `ADD`, `DEST`, a CRC-filed `DA`/`VP`, an approach clearance to a field no flight plan names), which only the line's second remedy, a non-blocking miss, can close; that is open question 4. When brief 4 lands, the orchestrator narrows the MAIN.md line to that runtime case.

### Tests

Yaat.Sim (`tests/Yaat.Sim.Tests`):

- `ScenarioResourceManifestTests`: primary airport; each aircraft field; the `Parking` and `Coordinates` / `FixOrFrd` airport chains; `K`-prefix and case dedup (`KOAK` + `oak` fetch once); roster neighbour ARTCCs deduplicated, own ARTCC excluded; a VFR generator `directTo` that is an airport and one that is a fix; a preset airport token and a preset fix token; unreadable JSON reports the failure; an empty `aircraft` list.
- Manifest coverage over the corpus: for every scenario in `docs/atctrainer-scenario-examples/` and the test-data scenarios, `ScenarioLoader.Load` with a recording `IAirportGroundData` asks for no airport outside the manifest. This is the test that keeps the loader and the manifest from drifting.
- `HttpFileCache`: `RefreshFailed` is set when a network failure serves the disk copy, and not on a 404, a fresh GET or a disk-TTL hit.

Server (`tests/Yaat.Server.Tests`):

- `AirportGroundDataService.PrefetchAsync` over a fake handler: each outcome; two concurrent prefetches of one airport make one request; `GetLayout` after a prefetch makes none.
- `ArtccConfigService`: each outcome; a neighbour whose ERAM letter cannot be read is reported; two concurrent `EnsureLoadedAsync` calls for a cold ARTCC both find the config afterwards (fails today: write it first).
- The gate stays free during prepare: a fake handler holds a layout fetch on a `TaskCompletionSource`; while it is held, `room.EnterTickGateAsync` completes at once and a tick runs; releasing it completes the load.
- Unreadable JSON leaves the previous scenario loaded (fails today: write it first).
- A second load, and each refused lifecycle call, is refused while the flag is held, with the verbatim texts; the flag is released after success, after a failed read, and after an exception in the commit.
- A room removed during prepare is not repopulated.
- Progress: step order, `Sequence` strictly increasing, the last event equal to `result.Steps`; an ARTCC 404, a neighbour config 404, a missing ERAM letter, a layout 404 and a stale disk copy each produce their verbatim problem and state; an all-good load ends with no `warning`; the student-position and generator problems appear under `populate`; a live session's steps.
- Determinism: the same (scenario, seed, session start) through `LoadScenarioSeededAsync` gives a byte-identical snapshot 0 before and after the split; the existing recording replay and rewind suites stay green.

Client (`tests/Yaat.Client.Tests`):

- The overlay state: a stale `Sequence` is dropped; an event with another `LoadId` is ignored; all `done` / `notNeeded` closes it; a `warning` or `failed` keeps it open until dismissed; the RPC result's `Steps` alone renders the final table.
- `ScenarioLoadProgressDto` and `LoadStepDto` resolve through `YaatHubJsonContext`.

### Measurement

Each step's duration log gives the cold-cache numbers the roster-concurrency default waits on. Brief 4's gate runs one load with the airport and ARTCC disk caches emptied and quotes the per-step times from the log, and one load of the same scenario warm.

### Brief split

Steps 1–2 are Yaat.Sim and gate with `pwsh tools/test-all.ps1`; the rest are single-repo. Order 1 → 2 → 3 → 4 → 5; 6 is independent; 7 is the orchestrator's.

1. **Fetch outcomes in Yaat.Sim**: `HttpCacheResult.RefreshFailed`, `AirportLayoutDownloader.FetchGeoJsonAsync`, tests. Files: `src/Yaat.Sim/Data/HttpFileCache.cs`, `src/Yaat.Sim/Data/Airport/AirportLayoutDownloader.cs`, their tests.
2. **Scenario resource manifest**: `ScenarioResourceManifest`, the loader's airport-chain rules shared with it, the corpus coverage test. Files: `src/Yaat.Sim/Scenarios/ScenarioResourceManifest.cs` (new), `src/Yaat.Sim/Scenarios/ScenarioLoader.cs`, tests.
3. **Server fetch services**: `AirportGroundDataService.PrefetchAsync` with single-flight and the miss warning; `ArtccConfigService.EnsureLoadedAsync` outcome, single-flight, `ConcurrentDictionary`, neighbour-letter misses (the cold-ARTCC race test first). Files: `Data/AirportGroundDataService.cs`, `Data/ArtccConfigService.cs`, tests.
4. **Prepare / commit and progress on the server**: the split, the load flag and refusals, `LoadScenarioGuardedAsync`, the hub changes for `LoadScenario` / `StartLiveSession` / the refused lifecycle methods / the retirement sweep, the progress reporter and DTOs, `LoadScenarioResult.Steps`, the step duration logs and the cold/warm measurement. Files: `Simulation/ScenarioLifecycleService.cs`, `Simulation/RoomEngine.cs`, `Simulation/TrainingRoom.cs`, `Hubs/TrainingHub.cs`, `Dtos/TrainingDtos.cs`, tests. It also pins the loaded layouts and ARTCC configs on `TrainingRoom` for the reload paths and makes ARTCC configs stale-while-revalidate. Split (2026-09-30, on `feat/async-room-load`): **4a** the flag, prepare/commit, `LoadScenarioGuardedAsync`, the refusals and the retirement-sweep skip, no new DTOs; **4b** the DTOs, the reporter, the hub callback, `Steps`, `LoadingBy`, the step logs and the measurement; **4c** pinning on `TrainingRoom` and ARTCC stale-while-revalidate. Settled for the briefs: prepare reaches `PrefetchAsync` by the same cast `PopulateRoom` uses for `MarkPrimaryAirport` (the `IAirportGroundData` contract does not change); the "needs a full ground map" test (the map-warning ruling) goes into `ScenarioResourceManifest` beside the airport-chain rules brief 2 shared with the loader (4b, a `Yaat.Sim` change, so `test-all`); only the loader sees progress (the progress ruling); `Sequence` bumps on every table change, item counts included; a room closed during prepare releases the flag in the `finally` and sends no further progress; commands sent during prepare get no notice (they reach the previous scenario, as decided).
5. **Client overlay**: the DTOs, the `On` handler and the `YaatHubJsonContext` registration; a load-overlay state on `MainViewModel` fed by the event and by the result's `Steps`; the export overlay in `MainWindow.axaml` extended to the step list; the pre-send parse in `SendScenarioToServer`'s callers moved off the UI thread; the live-session path. Files: `src/Yaat.Client.Core/Services/ServerConnection.cs`, `src/Yaat.Client.Core/Services/YaatHubJsonContext.cs`, `src/Yaat.Client/ViewModels/MainViewModel.Scenario.cs`, `src/Yaat.Client/ViewModels/MainViewModel.LiveSession.cs`, `src/Yaat.Client/Views/MainWindow.axaml`, tests.
6. **Orphan room cleanup** (the decided default): remove the room when `CreateRoom` throws after `TrainingRoomManager.CreateRoom` registered it, or when the client's `JoinRoom` returns null. Files: `Hubs/TrainingHub.cs`, `src/Yaat.Client/ViewModels/MainViewModel.Rooms.cs`, tests.
7. **Docs and landing** (orchestrator): `docs/training-hub-contract.md`, `docs/server-rooms-and-hub.md` (the threading gotcha gains the load path), `docs/scenario-loading-and-generation.md` (server orchestration), `docs/client-mainviewmodel.md`, `docs/architecture.md`, `USER_GUIDE.md`, the changelog, and the MAIN.md line narrowed.

### Rulings

- **Layout read**: prefetch warms the shared cache (`AirportGroundDataService._cache`); `ScenarioLoader.Load` keeps calling `GetLayout`, and a miss still fetches, blocking, with a logged warning naming the airport.
- **Progress**: the loader gets the step overlay; the room gets a terminal line `{initials} is loading '{scenario name}'…` and `RoomStateDto` gains `LoadingBy`, so a joiner sees a load is running.
- **Map warning**: a missing map turns `layouts` amber for the primary airport and for any airport with a `Parking` or ground-coordinates spawn. An `OnRunway` spawn never needs a full ground map (the runways come from navdata), and TRACON and Center scenarios commonly depart from fields with no vNAS map, so it stays green; mapless airports that raise no warning are listed in `Detail` (`no map: SQL, HAF`).
- **Runtime misses**: an airport first named mid-session keeps the blocking fetch, logged by name; the MAIN.md secondary-airport line is narrowed to this case and revisited with the tick-performance test.
- **Other reload paths**: ARTCC configs are served stale-while-revalidate (a held config is used while a background refresh runs past the 30-minute TTL, as layouts already are), so a warm restart, rewind or session restore never waits on vNAS; `LoadRecording`, the one cold path (an imported recording's ARTCC and airports), runs the prepare half before its gate, without the overlay.
- **4c settled** (2026-09-30, from the 4c exploration; persistence by the user): 4c is two briefs — **4c-1** ARTCC stale-while-revalidate, case-insensitive ARTCC ids, a vNAS 404 worded apart from "unreachable" (`SourceRemoved` on the fetch outcomes); **4c-2** pinning, `LoadRecording`'s prepare off the gate, the `PopulateRoom` split. The pin is the ARTCC configs (own and neighbours) and the airport layouts a load used, held on `TrainingRoom`; the loader and engine read layouts through a pinned `IAirportGroundData` whose miss (a runtime-named airport) falls through to the live service and joins the pin, so a rewind reproduces it. The pin covers the reload paths (restart, rewind, rewind-from-snapshot, session restore, the export reconstruction, which copies the live room's pin), the recording export bundle and the checkpoint; the ~140 runtime `ArtccConfigService` readers stay live. A planned-restart checkpoint saves the configs (own and neighbours, fixing today's apply-after-reload ordering) and refetches layouts on restore. A room with no pin (a pre-4c checkpoint) falls back to the live caches, pins them and logs it. `LoadRecording` claims the load flag for its prepare and is the one path that replaces the pin; its commit is reload-shaped (`PopulateRoom` and the reload log only), not `CommitPreparedScenario`. Stale-while-revalidate returns the previous outcome while the refresh runs; a failed refresh keeps the held config. 4c-1, 4c-2a and 4c-2b landed on the feature branch (4c-2b review rulings: recording loads send no progress events; a commit that throws after the clear empties the room through `ClearRoomScenarioState`, the room half of the unload, and the hub sends `ScenarioUnloaded`; `MigrateRecording` fetches its recording's own resources; restore warms the live ARTCC caches so the student position registers after a restart; a v1 checkpoint fills its neighbour ARTCCs from the live caches). 4c-2 is split: **4c-2a** (the pin, the reload paths, the export and ground-layout readers, layouts keyed by FAA code and read once per entry) and **4c-2b**: `LoadRecordingAsync`/`LoadRecordingArchiveAsync` set the room's pin from their own prepare (today a recording of the same scenario JSON reuses the room's pin, since `ClearRoomForReload` keeps it), after which the scenario-JSON identity (`RoomResourcePin.Serves`/`ScenarioJson`) is deleted; the checkpoint saves every pinned config (`RoomResourcePin.ArtccConfigs`) and restore builds the pin from them plus the refetched layouts before `ReloadForRewindAsync`, deleting today's post-reload `ArtccConfig` override (`SessionPersistenceService` ~:297-303); a restore test through `RestoreRoomFromArchiveAsync`.
- **Pinning**: the commit stores the airport layouts and ARTCC configs the load used on `TrainingRoom`, and every reload path (restart, rewind, session restore) reads those rather than the live caches, so a rewind reproduces the live run; a vNAS correction reaches the room at its next scenario load.
