# Server Rooms, Tick Orchestration & the YAAT Hub Seam

> Read this before touching the hosted tick loop, `RoomEngine`, `TickProcessor`, `AircraftChangeTracker`, the `TrainingRoom*` types, `TrainingHub`, or the scenario load's prepare/commit path and the room's resource pin. It documents the yaat-server side of the `/hubs/training` (JSON) link: the one hosted loop that drives every room, how a scenario load runs beside it, the `RoomEngine` command-routing chain, room isolation, and the per-aircraft delta engine.
>
> The wire shape itself — the DTO pairs, the hub-method/broadcast catalogs, the scenario-load progress contract, the three JSON source-gen contexts, and the canonical add-a-field checklist — lives in [training-hub-contract.md](training-hub-contract.md).
>
> For the per-aircraft physics step order inside `Yaat.Sim` see [tick-loop.md](tick-loop.md); for one command's journey through the parser and dispatcher see [command-pipeline.md](command-pipeline.md); for the parallel CRC (MessagePack) path see [crc-display-state.md](crc-display-state.md).
>
> All file paths below are in the yaat-server repo (`../yaat-server/src/Yaat.Server`) unless noted.

## Scope

```
RoomTickLoopService   one PeriodicTimer @ 1 Hz, drives every room
        │  per room (if running):
        ▼
RoomEngine.AdvanceLiveSecond ×SimRate  =  SimulationEngine.RunSecond(LiveRoomHost)   (the spine — docs/tick-loop.md)
        │  after the ALL-ROOMS loop:
        ▼
AircraftChangeTracker.DetectChanges  →  Broadcast{Training,Admin,Crc}Updates
```

`RoomEngine` is the per-room facade for everything else: commands, scenario lifecycle, recording, broadcast. The hub
(`TrainingHub`) is a thin RPC surface that resolves a connection to a `RoomEngine` and delegates.

## The hosted tick loop — `Simulation/RoomTickLoopService.cs`

One `IHostedService` owns a single `PeriodicTimer(TimeSpan.FromSeconds(1))` (`:44`) — **one loop for all rooms**, not one
per room. Each wall-clock tick (`RunTickLoop`, `:92`):

1. Snapshots all rooms (`_rooms.GetAllRooms()`, `:100`) and stamps each room's continuous-pause clock via
   `UpdatePausedSince` (`:106`).
2. For each room with a running scenario (skips paused / no-scenario / no-engine, `:109`):
   - `simSeconds = Math.Max(1, (int)scenario.SimRate)`.
   - For each sim-second: `room.Engine.AdvanceLiveSecond()` — the whole spine (`SimulationEngine.RunSecond`, see
     [tick-loop.md](tick-loop.md)) under the room's `LiveRoomHost`: the clock increment, playback pre-tick actions,
     pre-physics, **`SimulationEngine.PhysicsSubTickRate` = 4** physics sub-ticks of 0.25 s, the post-physics list, and the
     host's end-of-second bookkeeping — the weather-timeline advance, `EnsureLiveMetarIssuer` + METAR issuance
     (broadcast only when a station re-issues), and playback post-tick actions (position-history sampling is a sim
     step, `AircraftState.PositionHistorySampleSeconds` / `PositionHistoryCapacity`). The headless soak host
     (`Simulation/Headless/HeadlessRoom`) and the test harness call the same method, so every live-kind run evolves the
     same way; reconstruction runs the same spine under a `ReconstructionHost`.
   - **Ends with one `BroadcastSimState(room)` per processed wall-tick** (after the sim-second loop, in
     `ProcessRoomSecond`) so the client's elapsed clock stays live — the timeline label/scrubber and the base for
     the relative +15/−15 skips. The end-of-tape branch sets the paused state first, so that final tick's broadcast
     carries `IsPaused = true`. (Issue #209: previously elapsed only reached clients on pause/unpause/rewind/end.)
3. After the loop budget check (`TickBudgetMs = 800`, logs a warning if exceeded, `:181`).
4. **After the all-rooms loop**: `await DetectChangesAsync(allRooms, ct)` (`:138`) then `await BroadcastUpdates(allRooms)` (`:139`), which fans out to training clients, admins, and CRC (`:242`).

   `DetectChangesAsync` takes each room's tick gate around that room's snapshot and diff, as a separate acquisition from `ProcessRoomSecond`'s (so the tick budget does not time it and a paused room still gets its detection pass): a hub-side `ChangeTracker.Remove`/`Clear`, which runs under the gate, can then never land mid-pass and have the pass re-add the removed aircraft (pin: `ChangeDetectionTests.DetectChangesPass_WhileTheRoomGateIsHeld_WaitsForIt`).

   The broadcast stays outside the gate. `DetectChangesAsync` also runs `AtpaEvaluator.EvaluateRoom` per un-suppressed room — one `AtpaProcessor.Process` per wall-tick cached on `TrainingRoom.AtpaResults` for both the CRC pass and the signature-guarded `AtpaResultsChanged` training push (`TrainingRoom.LastBroadcastAtpaSignature`).
5. Every minute (`PausedRetirementSweepInterval`, `:34`) runs `ScenarioLifecycleService.RetirePausedRoomsAsync` (`:196`) to evict rooms left paused past the threshold, skipping a room whose scenario load holds its load flag until the next sweep.

### Cadence gotcha: double cadence

Physics advances `SimRate` **sim-seconds per wall-clock tick** (`ElapsedSeconds += 1.0` inside the inner loop), but
`DetectChanges` + `BroadcastUpdates` run **once per wall-clock tick, after the all-rooms loop**. At `SimRate > 1`,
multiple sim-seconds elapse between broadcasts. Reading the tick body as "broadcast every sim-second" is wrong — that is
why broadcasts are deltas, and why a timing/rate feature must not assume one broadcast per sim-second. [tick-loop.md]
(tick-loop.md) covers the in-`Yaat.Sim` step order; the server adds the room loop and the post-loop broadcast on top.

### Threading gotcha: one thread, no blocking I/O

`RunTickLoop` iterates **all rooms on one thread inside one stopwatch**, so one room's slow lookup delays every room and
surfaces as a `TickBudgetMs` overrun. Nothing reachable from `TickPhysics` — phases, handlers, the `Ground.Layout`
fallback — may block on the network. The layout path is built around that constraint:

- A scenario load, a live-session start and a recording load fetch nothing under the room's tick gate: their prepare runs with no gate held, and the gate is taken once, for the CPU-only commit (see **Scenario load: prepare, commit and the load flag** below).

  The tick loop's `room.EnterTickGateAsync` therefore never waits on vNAS, and every room, the loading one included, keeps ticking through the fetches. The loop waits only for the commit's `PopulateRoom` and result build; the `populate` step's duration log says how long that was.
- `AirportLayoutDownloader` negative-caches a confirmed origin 404 for `NotFoundTtl` (6 h). `HttpFileCache.GetOrRefreshAsync`
  returns `HttpCacheResult(Content, NotFound, RefreshFailed)`, and only a genuine 404 latches — a network failure or timeout retries.
- `AirportGroundDataService` serves an expired entry (`CacheTtl`, 30 min) stale while a background refetch re-parses it; the tick thread never waits on the TTL refresh. Every fetch of one airport — `PrefetchAsync`, a `GetLayout` miss, the TTL refresh — shares one in-flight task, re-checked under the lock, so an airport is never fetched twice at once.

  `PrefetchAsync` returns a `LayoutFetchOutcome` (`Loaded`, `LoadedFromStaleCopy` with the cached copy's last-changed time, `NoMap`, `Unreachable`, `Unparseable`); an `Unreachable` entry is retried by the next prefetch.

  A refresh that ends `Unreachable` (a throw included) or `Unparseable` while a layout is held keeps that layout and its source GeoJSON, reporting it as `LoadedFromStaleCopy` with the held copy's fetch time and the failure as its `StaleReason` (so the load report counts it as a cached copy, and it refreshes on the next `CacheTtl` expiry like a disk stale copy) and logging "keeping the held layout".

  This is the rule `ArtccConfigService` follows for a held config; a `NoMap` (vNAS answered 404, nothing on disk) still replaces it.

  A refetch whose GeoJSON is ordinal-equal to the cached copy keeps the already-parsed layout instance (rooms pin it; nothing mutates a layout) and is answered before the parse gate, renewing only the fetch time. A `GetLayout` miss still fetches blocking and logs a warning naming the airport and the thread ("was not prefetched" or "is still being prefetched").
- `ArtccConfigService.EnsureLoadedAsync` shares one in-flight load per ARTCC (a second caller for a cold ARTCC waits for the config instead of returning before it exists) over a `ConcurrentDictionary`, and returns an `ArtccFetchOutcome` (`Loaded`, `LoadedFromStaleCopy` with `CachedCopyUtc` and a `StaleReason` — `Unreachable` or `RemovedFromVnas` for an HTTP 404 — `NotFound`, `Unreachable`, `Unparseable`, plus the neighbour centres left with no ERAM letter).

  ARTCC ids are case-insensitive: the id is upper-cased once on entry (URL, cache file `artcc-ZOA.json`, outcome), and the scenario loader upper-cases a scenario's `artccId`.

  Only a cold ARTCC waits.

  With a config held, the configs are stale-while-revalidate like the airport layouts: past `ConfigTtl` the call returns the last good outcome at once and starts one shared background refresh.

  A refresh that throws keeps the held config and lets the next call retry, one that ends `Unreachable` keeps the held config and is reused for `UnreachableRetryBackoff` (2 min, from the load's start) before the next call refreshes again (a cold ARTCC still retries at once), any other outcome is kept for `ConfigTtl`, and the held config is replaced only by a successful load.
- Prepare fetches every airport the scenario JSON names (`ScenarioResourceManifest.AirportIds`: presets and VFR generator targets included) into the shared cache before the loader runs, so the loader's `GetLayout` calls are dictionary hits. `ScenarioLifecycleService.WarmAircraftGroundLayouts` then resolves, inside the commit and through the room's pin, every airport a loaded aircraft references (departure, destination, spawn — delayed aircraft included), so no tick pays a first read of them.
- `NavigationDatabase.GetSid/GetStar/GetApproach` do not walk the supplementary prior-cycle CIFP chain for an airport whose
  current cycle lists no procedures of that kind (that chain models version drift; a procedure-less field would otherwise pay
  a burst of full file scans per probed route token). The chain still walks when no current cycle is loaded.

The ground-data service caches null entries on purpose: the per-sub-tick `aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft)` fallback then costs a dictionary hit, and it is also the self-heal path once a background fetch lands.

Accepted residual risk: a first-ever fetch on the tick thread for an airport the scenario JSON never names (a typed `ADD` or `DEST`, a CRC-filed `DA`/`VP`, an approach clearance to a field no flight plan names) — one-time, bounded, logged with the airport's name, and fast on the negative cache. The layout it finds joins the room's resource pin, so a rewind reads the same one.

## Scenario load: prepare, commit and the load flag

A scenario load is two halves (`Simulation/ScenarioLifecycleService.cs`), composed by `RoomEngine`:

- **Prepare** (`PrepareScenarioAsync`; `PrepareScenarioWithFreshSeedAsync` draws a random seed and takes the room clock's instant as the session start) touches no room and holds no gate. It reads the JSON into a `ScenarioResourceManifest` and fails there, before anything is fetched, when the JSON cannot be read, so the room keeps running what it had.

  Then it fetches the manifest's ARTCC configs (`ArtccConfigService.EnsureLoadedAsync`) and airport layouts (`AirportGroundDataService.PrefetchAsync`) concurrently, the two groups at once and every item within a group at once; the GeoJSON parses of one load go one airport at a time through a per-load `SemaphoreSlim` parse gate, since they are CPU work on the thread pool the tick loop also runs on, while the downloads overlap.

  Once the layouts are in it pins them, warms the CIFP SID/STAR/approach tables of the manifest's airports and runs `ScenarioLoader.Load` on a fresh `SimulationEngine`, while the ARTCC configs may still be arriving (the loader reads layouts and navdata, no config). It returns a `PreparedScenario`: the engine and loader result, the seed and session start, the room's resource pin (below), the fetch outcomes and the step reporter.
- **Commit** (`CommitPreparedScenario`) runs under the room's tick gate and is CPU only.

  It first checks the room is still the registered room of its id; one retired or force-closed during the prepare is left alone and the load fails with `The room was closed while the scenario loaded.` Otherwise it unloads the current scenario, stores the pin on the room, runs `PopulateRoom` (positions, beacon banks, strip bays, TDLS and coordination from the pinned configs), records the seeded session settings, sends the CRC broadcasts, re-applies the room's weather and builds the result.

  An exception after the unload fails the load at the `populate` step and leaves the room empty; it does not propagate.

The RNG streams draw in one fixed order (the loader on the fresh engine's `World.Rng`, then `PopulateRoom`'s draws), and the session start is read once when prepare begins and carried to the commit, so (scenario, seed, session start) reproduces a load; only the wall-clock moment of the draws depends on the fetches.

`RoomEngine.LoadScenarioAsync` / `LoadScenarioSeededAsync` / `StartLiveSessionAsync` compose the halves with no gate and no load flag, for a caller that owns the room outright (tests, the headless soak) or already holds the gate.

The hub goes through `LoadScenarioGuardedAsync` and `StartLiveSessionGuardedAsync`, which run the prepare, then take `Room.GuardAsync` for the commit alone, then run the hub's `afterCommit` (the `ScenarioLoaded` broadcast; for a live session also `SessionSettingsChanged`) after the gate is released and before the flag is.

A live session's prepare also checks the feed, the creator's ARTCC config (required: without it the `artcc` step fails), the position and the airport, and its gated section adds the live-traffic enable, ceiling, filter, `Resume` and the optional seek.

**The load flag.** `TrainingRoom.TryBeginLoad(initials, out holder)` is an `Interlocked` compare-exchange on `LoadingBy`; `EndLoad` releases it. `RoomEngine.RunUnderLoadFlagAsync` claims it before the prepare and releases it in a `finally` after `afterCommit`, broadcasting `RoomLoadingChanged` with the initials on the claim and null on the release.

While it is held, a second load is refused at once (`TrainingRoom.AlreadyLoadingRefusal`), and unload, confirm-unload, restart, both rewinds, `LoadRecording` and `CloseRoom` are refused with `TrainingRoom.LoadInProgressRefusal`, checked before the hub waits on the tick gate and again inside it.

This is what keeps anything from swapping the scenario between prepare and commit. `ScenarioLifecycleService.RetirePausedRoomsAsync` skips a room whose flag is held, and the commit's registration check covers an admin force-close or the abandoned-room timer. The texts and the wire side are in [training-hub-contract.md](training-hub-contract.md#scenario-load-progress).

**Progress.** A `ScenarioLoadReporter` (`Simulation/ScenarioLoadReporter.cs`, carried on `ScenarioLoadRequest` and `PreparedScenario`; `ScenarioLoadSteps` holds its ids, labels and texts) keeps the load's step table under a lock, since the ARTCC and layout fetches report from concurrent continuations, and hands each change to a sink the hub builds from `Clients.Caller`; a failed send is logged and never fails the load.

`ScenarioLoadReporter.None()` serves the unguarded, recording, restore and rewind paths. `RunUnderLoadFlagAsync` catches any exception from prepare or commit, fails the step the load had reached and completes the table, so the loader's last event always has `IsComplete`. Each step logs `Scenario load {LoadId} step {Id}: {State} in {ms} ms`.

### The resource pin

A room's **resource pin** (`Simulation/RoomResourcePin.cs`, `TrainingRoom.ResourcePin`) holds the ARTCC configs (the scenario's own and each neighbour its ATC roster names) and the airport layouts its load used. Prepare builds it after the fetches (`RoomResourcePin.Create`, an ARTCC whose config did not load is left out); the commit stores it before `PopulateRoom`; an unload clears it.

Layouts live in a `PinnedAirportGroundData` over the live service, keyed by FAA code (`KOAK` and `OAK` share one entry) and read from the live cache once per entry (`AirportGroundDataService.GetLayoutAndSource`), so a layout and its GeoJSON never come from different map versions.

An airport first named after the load falls through to the live service once and joins the pin; a config miss does not. A vNAS refresh swaps the live cache's entry for a new object and the pin keeps the old one, so a vNAS correction reaches the room at its next scenario load.

Readers of the pin:

- `ReloadForRewind` / `ReloadForRewindAsync`: warm restart, rewind, rewind-from-snapshot, session restore and the export reconstruction (`RoomEngine.CreateTempReplayEngine`, handed the live room's pin) rebuild the loader and engine on the pin and never wait on vNAS. A room with no pin (restored from a checkpoint that saved no ARTCC config) first loads the scenario's configs and layouts into the live caches, then pins those and logs it.
- The ground-layout hub endpoint (`RoomEngine.GetAirportGroundLayout`), `ExportRoomAsScenario`, the recording export's ARTCC config and layout bundle, the headless soak room's bundle (`HeadlessRoom.GroundData`) and the checkpoint's configs. Every other runtime `ArtccConfigService` reader (CRC handlers, position lookups, the facility tree) stays on the live caches.

**Recording loads** (`RoomEngine.LoadRecordingGuardedAsync` / `LoadRecordingArchiveGuardedAsync`) are the one path besides a scenario load that replaces the pin. They claim the load flag, fetch their scenario's resources off the tick gate (`ScenarioLifecycleService.PrepareResourcesAsync`, no progress events), then under the gate check the room is still registered, clear it, set the new pin and reload (`ReloadRecording`); the `RecordingLoaded` broadcast goes out before the flag is released.

A commit that throws after the clear leaves the room empty the way an unload does (`ClearRoomScenarioState`, then `ResyncEngineStateAsync`), and the hub sends `ScenarioUnloaded`. The v1 recording migration (`RecordingManager.MigrateToV2Async`, run by `tools/Yaat.RecordingUpgrader`) replays a v1 recording on its own scenario's freshly fetched resources, never the calling room's pin.

**Checkpoints.** A planned-restart checkpoint saves every pinned config (`artcc-configs.json.br`, checkpoint version 2).

Restore warms the live ARTCC caches (the position registry and the CRC broadcasts read them there), refetches the layouts and pins the archived configs, filling any manifest ARTCC the archive lacks from the live cache (`RoomResourcePin.FromArchive`; a version 1 checkpoint saved the scenario's own config alone), before `ReloadForRewindAsync`. See [session-persistence.md](session-persistence.md).

**Active runways.** The room carries the mentor's answer per scenario (`TrainingRoom.FindCarriedActiveRunways` / `CarryActiveRunways`, keyed by the normalized scenario id): the load prompt's answer or the last live `ARWY` (`RoomHost.OnActiveRunwaysChanged`; a replayed `ARWY` is not carried). An entry is an answer, so `NONE` is an empty list and an unanswered scenario has none. A load of the scenario already carried keeps its entry, any other load or an unload drops it.

`SimScenarioState.InitialActiveRunways` is what the session started on, kept like the RNG seed. `ScenarioLifecycleService.Reload` seeds the runways by `ReloadKind`: `Restart` starts on the list in use when it was asked for, whatever set it and a rewind's included, never on the carried answer (the restart passes `scenario.ActiveRunways` as the `ReloadStart`'s value and sends it back in `ScenarioRestartedDto.ActiveRunways`); `Rewind`, `RecordingLoad` and `CheckpointRestore` start on the `InitialActiveRunways` the `ReloadStart` carries (`ReloadStart(RngSeed, SessionStartUtc, InitialActiveRunways)`, `ReloadStart.Of(scenario)` for a live one). A load reads the carried answer first. With no value (no answer on a load, or a recording or checkpoint that stored no start value) the scenario's sidecar seeds them, else `ImpliedActiveRunways.RoomDefault`.

The sidecar is looked up per load (`PrepareResourcesAsync` returns it in `PreparedResources.Sidecar`; a session restore uses `FindScenarioSidecar`) and kept on `TrainingRoom.ActiveScenarioSidecar` for the reloads and the export reconstruction. A load reports only its own ARTCC's sidecar warnings (`ScenarioSidecarLoadResult.WarningsFor`); every other ARTCC's (`WarningsOutside`) are logged once per process.

`TickProcessor.BroadcastActiveRunwaysIfChanged` compares the room's list with `TrainingRoom.LastBroadcastActiveRunways` and sends `ActiveRunwaysChanged` on change; it runs in the per-second `PerSecondBroadcasts` step and after a load, restart, rewind or recording load lands.

## `RoomEngine` — the per-room facade (`Simulation/RoomEngine.cs`)

One `RoomEngine` per room. It **owns** its `TrainingRoom` (`Room`, `:66`) and `RecordingManager` (`Recording`, `:64`,
set by `RoomEngineFactory` right after construction) and exposes `World` (`:67`, delegates to the room's world) and
`FindAircraft` (`:786`). Everything else is a **shared stateless singleton** injected via the primary constructor
(`:27`-`41`): `TickProcessor`, `SimControlService`,
`ScenarioLifecycleService`, the broadcasters, and the ARTCC/ground data services. Per-room state lives on the
`TrainingRoom`, never on the singletons.

`BeginRoomScope()` opens a logging scope tagged with the room id so every log line within a hub call carries `[roomId]`. `CreateTempReplayEngine(scenario, start, pin, sidecar)` builds a throwaway engine on a synthetic room with `IsBroadcastSuppressed = true` for snapshot generation / replay, so it never leaks state to real clients; the synthetic room runs on the resource pin it is handed (the exporting room's, or the one a migrated recording's own prepare fetched), and starts from the `ReloadStart` and sidecar it is handed.

### `SendCommandAsync` — policy, the router, the echo

After the scenario null-guard, the `** ` force-override prefix and assignment enforcement, the command goes to the
engine's `ActionRouter` with the room's `LiveRoomHost` answering (`IssueLive`): every verb — aviation, track,
coordination, strip, TDLS, spawn, flight plan, the clock — is one `ArmTable` row, the same row a Sim replay and a
server reconstruction run, and the router records the text with its verdict, accepted or not. A command typed while
the room plays a tape back takes control first (`RecordingManager.TakeControl`: the tape is cut at the current second
and the room returns to its own run kind), so the router records it and the host answers it as a fresh action.

The room's remaining part is the terminal echo: `Command` (or `Strip` for a strip verb, so the client's strip-channel
toggle hides all routine strip traffic in one click) plus `Response` / `Error`; a global or position-scoped command
echoes with no callsign. `FlushTerminalEntries()` then surfaces the SAY-class and spawn lines the sim queued, even while
paused. The full route is walked in [command-pipeline.md](command-pipeline.md).

### CRC-sourced command entry points

CRC mutations are routed through the **same router** as typed commands, so live and replay paths agree. The entry
points prepend an `AS {tcp}` token where the controller's identity must round-trip on replay
(`TrackResolver.AsPrefixCode(identity)`: `C{sector}` for ERAM, `{subset}{sector}` for STARS, else the callsign):

- `RecordAndDispatch(callsign, canonical, identity)` — track / coordination / ghost / reposition verbs, as `AS {tcp} {canonical}`.
- `RecordAndDispatchStrip(callsign, canonical, crcClientId)` — strip verbs under the CRC client's id (no `AS` prefix; strips
  are not position-scoped on the ownership axis).
- `RecordAndDispatchFlightPlan(callsign, canonical, identity, parsed, clickPosition)` — CRC STARS-typed DA/VP creates.
  STARS creates an unsupported data block for an unknown callsign, so one is issued first as `AS {tcp} GHOST {callsign}
  {lat} {lon}` (0,0 without a click) and a `DROP` is issued for it if the plan is refused — both recorded actions in
  their own right, so a replay places and removes the same block. Echoes the command and its verdict under
  `[CRC] {tcp}` initials and returns the two readout lines (`FlightPlanEcho.Build`).

A handler that mutates room state without going through one of these (or `Record`) breaks replay silently.

### Attendance — derived live, recorded, replayed

Which vNAS positions a CRC client is working is the first *recorded input* (ADR 0003): the sim consumes it but never
derives it. `PositionRegistry` stays the socket table (`RegisterCrcPosition` / `SetCrcPositionActive` /
`SetCrcPositionRoom` from the CRC session lifecycle) and `PositionRegistry.AttendedPositionIds(roomId)` is the
derivation — the active entries in the room. `RoomEngine.SyncAttendance()` compares that set with the engine's
`SimulationEngine.Attendance` and, only when they differ, issues a `RecordedAttendanceChange` through
`IssueDerived` with the live host, so the record is applied and appended like any other derived record. It runs in
the live host's end-of-second recorded-actions slot (`LiveRoomHost.ApplyRecordedActions` when the room is not
playing a tape back — the very slot a replay applies the record in, so a snapshot at second N carries every record
stamped at or before N; under the room's tick gate, so the engine's set needs no lock), after a scenario load /
restart / rewind reload, and on return to live (`TakeControl`, `LeavePlayback`, the session restore); it is skipped
while the room is replaying or playing a tape back, when the log is the authority. A room nobody attends records
nothing.

Readers of the engine's set: `SimulationEngine.TickAutoAccept` / `TickPointoutTimeout`
(`Attendance.IsTcpControlledByCrc`), `TickDelayedHandoffs` (`Attendance.ConsolidationOwnerOf`), and in Yaat.Sim the
`Consolidate` arm and the handoff / point-out `ConsolidationRedirect`. Readers that stay on the registry, on
purpose — live handler-time questions that never run on a reconstruction, and the registry is what the set is
derived from: the STARS `HO` shorthand's `ILL POS` validation (`CrcClientState.Stars.cs`), the secondary-display
track drop (`CrcClientState.Secondary.cs`), the `StarsConsolidation` wire projection (`CrcBroadcastService`), and
`RoomAiStaffing` (the controller AI ticks live only). A new tick-reachable attendance read goes through the engine's
`Attendance`, never the registry.

## `TickProcessor` — `Simulation/TickProcessor.cs`

Stateless singleton; every method takes the `TrainingRoom`. It keeps **no list**: the order its bodies run in is the
spine's (`SpineOrder` in Yaat.Sim, [tick-loop.md](tick-loop.md)), and `RoomHost` — the base of `LiveRoomHost` and
`ReconstructionHost` — maps each host step and consumer onto one `internal` method here:

- **Pre-physics**: `HandlePrePhysicsResult` broadcasts each newly-spawned aircraft (the spawn hooks — the PDC auto-queue
  and the strip auto-print — are the engine's `SimulationEngine.AfterAircraftSpawned`, run before the result reaches
  the host), and records generator spawns *after* their autotrack so the recorded snapshot carries the
  owner; `BroadcastTerminalEntries` takes the spine's drain; `ProcessDelayedHandoffs`; `SyncLiveTraffic` runs
  `ShadowTrafficSync.Sync` last — the pre-physics mutator of the aircraft set (see [live-traffic.md](live-traffic.md)).
- **Post-physics**: no ATC pass of its own (auto-accept, the point-out timeout, the two autotrack passes, the coordination timers and the tower lists are Yaat.Sim spine steps now, `SimulationEngine.TrackAutomation` / `SimulationEngine.Coordination.cs`, and the first three read the recorded `Attendance`).

  It runs the consumers of the engine's detectors (`BroadcastConflictAlerts`, `BroadcastEramConflictAlerts`; the ASDE-X alert diff goes through `RoomHost.OnAsdexAlertsChanged` → `ICrcBroadcast.BroadcastAsdexAlertsAsync`, with no `TickProcessor` body) and `ProcessSoloTrainingEvaluation`.

  Then come the drain consumers (`BroadcastWarnings` / `Notifications` / `PilotSpeech` / `PilotReadbacks` / `PilotTransmissions`, `ProcessApproachScores`), `HandleAutoDeleted` (reached through `RoomHost.OnAutoDeleted` with the aircraft `SimulationEngine.TickAutoDelete` removed; it tears down each callsign's assignment and change-tracker entry, then broadcasts the delete), and the rundown / live-traffic-status / timers "broadcast if changed" tail.

  The strip auto-print, the deferred strip dispatch and the four TDLS steps are Sim steps (`SimulationEngine.Strips.cs` / `.Tdls.cs`); the room only pushes what the change trackers drained (`RoomHost.OnStripsChanged` / `OnTdlsChanged` → `StripBroadcaster` / `TdlsBroadcaster.BroadcastChanges`).

  (`SimulationEngine.TickDeferredAutoTrack` claims a departure only once it first appears on STARS — i.e. crosses the acquisition floor, `FieldElevationResolver.IsBelowDisplayFloor` — so a track is never owned before it is displayed; `TickFlightPlanCreatorAutoTrack` runs before it so an explicit VP/DA controller wins over scenario `AutoTrackAirportIds` for the aircraft they just filed for.)

`TickAutoDelete` removes, each second: an aircraft whose queued `DEL` fired (`Ground.PendingAutoDelete`, which overrides `AutoDeleteExempt`); a landed aircraft stuck at a layout-less airport; a generated overflight past its exit radius (stamped `CompletionReason.Transited`).

It also removes an airborne departure filed from the primary airport farther than the session's `DepartureAutoDeleteDistanceNm` from the airport reference point (stamped `Departed` unless already stamped, e.g. `HandedOff`), and whatever the effective `OnLanding`/`Parked` mode selects.

The departure distance is off by default (null) and is set with the hub's `SetDepartureAutoDeleteDistance` (1–500 nm). It applies in every arrival mode, `Never` included, and to tracked aircraft. It ignores `AutoDeleteExempt`, because spawn sets that on every ground-started aircraft.

It skips an aircraft a controller kept with `NODEL` (`Ground.NoDeleteRequested`, set by the bare verb and by the `NODEL` modifier on `TAXI`/`LAND`/`CLAND`/`EXIT`), a local flight filed back to the primary airport, and live-traffic shadows, whose lifetime the feed owns.

Per-step timing lives on the engine: attach a dictionary to `SimulationEngine.TickTimings` and every spine step records under its `StepId` name, plus the segment rollups and one `Physics` bucket per sub-tick (the soak runner's `--timings`, `ReconstructionBenchmarkTests`, and an export's temp reconstruction room forwarding the live engine's sink; `docs/tick-loop.md` "The step trace" has the bucket list).

Several of these guard on `room.IsBroadcastSuppressed` before broadcasting (e.g. `BroadcastConflictAlerts`, `HandleAutoDeleted`'s CRC disconnect). A new broadcast from a tick-processor method must add the same guard or it leaks replay/snapshot-engine state to real clients.

## `AircraftChangeTracker` — the delta engine (`Simulation/AircraftChangeTracker.cs`)

Per-room (held on `TrainingRoom.ChangeTracker`), **single-threaded** — accessed only from the sequential tick loop, no
locking. `DetectChanges(ac)` (`:210`) captures a set of fingerprint `readonly record struct`s, compares each to the
stored last-sent value using compiler-generated structural equality, updates the stored value, and returns a
`DtoChangeFlags` bitmask (`:8`). **The first call for a callsign returns `DtoChangeFlags.All`** (`:237`) so a freshly
spawned aircraft seeds every topic.

The fingerprint structs (`:28`-`191`) — one per broadcast topic (except `EramDataBlock`, which is gated by a plain
`bool EramDataBlockSent` latch on `AircraftLastSent`, not a fingerprint struct — it fires once after the first send):

| Struct | Drives | Flag |
|---|---|---|
| `StarsTrackFingerprint` | STARS track DTO (position, beacon, owner/handoff/pointout, shared display state) | `StarsTrack` |
| `FlightPlanFingerprint` | CRC flight-plan DTO (filed type, route, beacon, …) | `FlightPlan` |
| `EramTargetFingerprint` | ERAM target (the data-block flag is the `EramDataBlockSent` bool, not a struct) | `EramTarget` (+ `EramDataBlock`) |
| `AsdexTargetFingerprint` / `AsdexTrackFingerprint` | ASDE-X primary target / full track | `AsdexTarget` / `AsdexTrack` |
| `TowerCabFingerprint` | Tower-Cab target | `TowerCab` |
| `GroundTargetFingerprint` | ground target | `GroundTarget` |
| **`TrainingDtoFingerprint`** | the YAAT-client `AircraftStateDto` | **`TrainingDto`** |

`TrainingDtoFingerprint` (`:145`) is the one that gates the YAAT-client `AircraftUpdated` channel. `CaptureTrainingDto`
(`:497`) fills it from the same `AircraftState` accessors `DtoConverter.ToTrainingDto` reads. A field that is on the
wire DTO but **not** in this struct will broadcast on initial join (the full manifest carries it) yet never update live.

`ExternalStarsFingerprint` (`:76`) is computed differently: duplicate-beacon and ATPA values aren't derivable from
`AircraftState` alone, so they are compared in a **separate pass** (`UpdateExternalStarsState`, `:315`) during the CRC
broadcast, after `DetectChanges` has already run. `Remove(callsign)` / `Clear()` maintain the dictionary
as aircraft leave / the room resets; every caller runs under the room's tick gate, which the detection pass also holds.

## `TrainingBroadcastService` — the fan-out (`Simulation/TrainingBroadcastService.cs`)

Implements `ITrainingBroadcast` (`Simulation/ITrainingBroadcast.cs`). Two parallel audiences:

- **Room SignalR group** — `room.GroupName` (`"room:{RoomId}"`). `BroadcastTrainingUpdates` (`:165`) iterates each room's
  snapshot and sends `AircraftUpdated` only when `room.TickChanges[callsign]` has the `TrainingDto` flag (`:184`); then
  sends every delayed-queue entry **unconditionally** (`:191`), because each entry's `Delayed (Ns)` countdown changes
  every tick.
- **Admin connections** — `BroadcastAdminUpdates` (`:200`) / `BroadcastToAdmins` (`:157`) send directly to admin
  connection ids (which join no room group), respecting each admin's single-room filter. **An aircraft event that fans
  out to the room group must also reach admins** or admin displays desync; deletes additionally hit CRC (see the
  three-layer delete rule in the yaat-server CLAUDE.md). The event-driven broadcast methods (`BroadcastAircraftSpawned`,
  `BroadcastAircraftDeleted`, `BroadcastSimState`, `BroadcastWeatherChanged`, the terminal/pilot-transmission broadcasts)
  early-return on `room.IsBroadcastSuppressed`; the per-tick `BroadcastTrainingUpdates` guards each room the same way, but
  the admin path (`BroadcastAdminUpdates` / `BroadcastRoomToAdmin`) only guards on `scenario is null` — suppressed rooms
  reach it carrying an empty `TickChanges` because `RoomTickLoopService.DetectChangesAsync` (`:207`) skips them when
  populating per-tick flags, so nothing aircraft-shaped is sent for them.
- **CRC connections** — `CrcBroadcastService.BroadcastUpdatesAsync` runs in the same after-the-loop, *un-gated* phase as
  `BroadcastUpdates` (only the detection pass before it is gated), but it snapshots `room.World` itself rather than reading `TickChanges`. It must
  therefore skip `room.IsBroadcastSuppressed` rooms explicitly (alongside `scenario is null`) — otherwise a rewind /
  recording reload, which tears the world down and briefly repopulates it with the full initial scenario before restoring
  the target snapshot, leaks those transient aircraft to CRC as additive `ReceiveStarsTracks` adds that never get deleted
  (STARS ghost tracks; see [crc-display-state.md](crc-display-state.md) "Rewind / recording-load resync").

`ToTrainingDto(...)` is reused for both audiences and for the delayed-spawn DTO (`ToDelayedDto`, `:277`).

## `TrainingRoom` — the unit of isolation (`Simulation/TrainingRoom.cs`)

Each room owns its own `ActiveSim` / `ActiveScenario` / `World` (`:22`-`28`, falling back to a bare world when no
scenario is loaded), its `RoomEngine`, and a bag of per-room state: `ChangeTracker`, `TickChanges`, `StripState`,
`TdlsState`, `AsdexState`, `EramState`, `LineNumbers`, `AircraftAssignments` (callsign → connectionId), and
`PositionSelections` (the Yaat.Sim map of connectionId → the position a bare `AS` selected; the room owns it for
its lifetime and every engine it creates reads it — see the yaat repo's `command-pipeline.md` § one identity). **Callsigns are per-room, not global** — `RoomEngine.FindAircraft` searches
only that room's `World.GetSnapshot()` (`:786`-`789`). There is no global aircraft lookup; reaching for one is a category
error. `UpdatePausedSince` (`:97`) stamps the continuous-pause clock that the retirement sweep reads; `IsAbandoned`
(`:76`) is true when no clients are connected.

**Session settings outlive the scenario.** `SessionSettings` (`RoomSessionSettings`) holds the room's copy of everything a controller can toggle mid-session — the auto-* flags, `ValidateDctFixes`, solo mode and pacing, the auto-accept and command-run delays, the auto-delete override, the departure auto-delete distance, the dynamic-METAR intent.

A load, restart, or rewind builds a **new** `SimScenarioState`, and `ScenarioLifecycleService` seeds it with `room.SessionSettings.ApplyTo(scenario)` at both construction sites. The scenario stays the runtime source of truth (every gate and DTO reads it, falling back to the room copy only when no scenario is loaded); `SimControlService` writes both. See [scenario-loading-and-generation.md](scenario-loading-and-generation.md#session-settings-belong-to-the-room-not-the-scenario-object).

**The load flag and the resource pin are room state.** `LoadingBy` (claimed by `TryBeginLoad`, released by `EndLoad`) and `ResourcePin` live on the room rather than the scenario, since a load holds the flag before any new scenario exists and every reload reads the pin; see **Scenario load: prepare, commit and the load flag** above.

**Members are connections, not people.** `Members` is keyed by SignalR connection id and each `RoomMember` carries
`Kind` (`ClientKind.Main` / `VStrips` / `VTdls`) and `JoinedAtUtc`. vStrips and vTDLS browser tabs join over the same
hub, so one controller can hold several members at once — `HasYaatClientMember` is the predicate that asks whether
any of them can actually work traffic. Between that and `CrcClientManager.GetClientsForRoom`,
`ScenarioLifecycleService.PauseIfUnattended` pauses a room the moment its last YAAT client and CRC client are gone
while browser tabs remain: the sim has nobody to serve. It is called from the **non-abandoned** branch of
`HandleClientLeft` and from `CrcWebSocketHandler`'s disconnect path. It deliberately does not touch `CleanupCts` —
a tab is a legitimate viewer, so room retirement stays governed by `IsAbandoned` and the paused-retirement sweep —
and it does not auto-resume.

**A room its creator never joined is closed, not left to the timer.** `TrainingHub.CreateRoom` registers the room first and then does the rest (`CompleteRoomCreationAsync`: the engine, the CRC lobby binding, the room-available notices, the terminal line). When anything there throws, it closes the room and rethrows the original exception, logging rather than throwing when the close itself fails.

The close (`CloseOwnRoomAsync`) takes the caller out of the membership, unbinds every CRC client bound to the room, runs `ScenarioLifecycleService.CloseRoom` (reached through `RoomTickLoopService.CloseRoom`), which cancels the abandoned-room cleanup timer and runs the same `RemoveRoomState` teardown the abandoned-room and paused-room retirement use.

Only then does it evict any other connection that joined meanwhile (`RoomRetired` `The room could not be created.`) and take the caller out of the group, so a failing group call cannot leave the room registered.

When the client's own `JoinRoom` right after a successful create throws, it calls the hub's `CloseRoom(roomId)`, which runs the same close (`RoomRetired` `The room's creator closed it.`) but only for the room's creator (`CreatorCid`), not while a scenario load holds the room, and not while another connection is a member. A `JoinRoom` that returns null found the room already gone, so there is nothing to close.

**Join gate & kick block.** Two per-room CID sets govern who may `JoinRoom`, both consulted by the pure
`TrainingHub.CanJoinRoomCore(isMentorOrInstructor, kind, kicked, invited, restored, alreadyMember, crcBound)`:
`InvitedCids` — CIDs a mentor pulled in as RPOs, the allow-list that lets a limited (non-mentor "main") client join;
and `KickedCids` — CIDs kicked from this room, a block-list. `KickedCids` is checked **first** and returns `false`
for **everyone**, including mentors/instructors (who otherwise bypass the gate) — so a kicked user can't self-rejoin
from the room list; `JoinRoom` throws a kicked-specific `HubException`. `KickMember` calls `room.RecordKick(cid)`
(adds to `KickedCids`, drops any stale `InvitedCids`/`RestoredMemberCids` entry) and refuses to kick the room's
`CreatorCid`. A kicked user re-enters only when an instructor pulls them: `PullRpo` calls `room.ClearKick(cid)` for
the puller's room. A kicked user (mentor or RPO) surfaces in the global RPO lobby (`BuildRpoLobby` +
`TrainingRoomManager.IsCidKickedFromAnyRoom`) so the instructor who kicked them can pull them back.

## `TrainingRoomManager` — registry (`Simulation/TrainingRoomManager.cs`)

All registry mutations are under one `_lock` (`:8`) guarding a triple index: `_rooms` (roomId → room), `_clientRooms`
(connectionId → roomId), `_cidToRooms` (CID → set of roomIds), plus `_adminFilters`. Membership lifecycle:
`CreateRoom` (`:22`), `JoinRoom` (`:107`), `LeaveRoom` (`:121`, removes the CID mapping only when no other member of that
room shares the CID), `RemoveRoom` (`:147`). `GetRoomForCid` (`:86`) resolves CRC clients to their room via the JWT
`sub` CID. Room ids are 8-char alphanumeric (`GenerateRoomId`, `:352`).

**Thread-safety boundary:** the registry is lock-guarded for hub-callback threads, but the per-room tick body
(`RoomEngine` / `TickProcessor` / `AircraftChangeTracker`) runs lock-free on the sequential tick loop. Don't mutate a
room's `World` or its `ChangeTracker` from a hub callback thread expecting tick-loop safety.

## `TrainingHub` — the RPC surface (`Hubs/TrainingHub.cs`)

Identity comes only from the session-token claims (`CallerCid`, `CallerRating`, `CallerArtcc`, `CallerIsMentorOrInstructor`).
`CreateRoom` validates the client's `artccId` against `CallerPermittedArtccs` (`ArtccAccessPolicy`: token home ARTCC plus
operator grants from `Data/artcc-grants.json`) and stores it normalised as `CreatorArtccId` — the ARTCC `GetScenarios` lists
and `StartLiveSession` resolves positions in — so a room's ARTCC is always one its creator may work. `GetScenarioJsonById`
re-checks the canonical scenario's `artccId` against the same set before the rating gate. See
[vatsim-auth.md](vatsim-auth.md) § ARTCC gate.

A thin hub that resolves the caller to a `RoomEngine` via `ResolveEngine(connectionId)` (`:1340`) — which routes admins
through their single-room filter and regular clients through `GetRoomForClient` — then opens a `BeginRoomScope` and
delegates (e.g. `SendCommand`, `:508`). Methods return a failure DTO (e.g. `CommandResultDto(false, "Not in a room")`)
or early-return rather than throw when the engine resolves to null. The CID auto-join push `RoomAvailableForCid`
(`:257`/`:281`) notifies a registered SignalR connection when a same-CID sibling makes a room available. The full
method-string → hub-method catalog and the server→client event catalog are in
[training-hub-contract.md](training-hub-contract.md) — that doc owns the wire shape; treat the code as source of truth
over the hand-written list in the yaat-server CLAUDE.md, which is already stale (e.g. `CreateRoom` now takes `kind`,
`SendCommand` takes `initials`, and `SpawnAircraft`/`DeleteAircraft` are `SendCommand`-routed verbs, not hub methods).

## Adding an `AircraftUpdated` field

The canonical checklist (DTO → DTO → `DtoConverter` → `TrainingDtoFingerprint` → client consume → source-gen) lives in
[training-hub-contract.md](training-hub-contract.md#checklist-adding-an-aircraftupdated-field). The server-specific step
to not skip is **step 4**: add the field to `TrainingDtoFingerprint` and `CaptureTrainingDto` in
`AircraftChangeTracker.cs`, or it round-trips on join but never updates live. If the field belongs to a different display
topic (STARS / ASDE-X / ERAM / Tower-Cab / ground), it goes in *that* topic's fingerprint struct, not
`TrainingDtoFingerprint`.

## Pitfalls

- **Double cadence.** Physics advances `SimRate` sim-seconds per wall-clock tick (`ElapsedSeconds += 1.0` in the inner
  loop), but `DetectChanges` + `BroadcastUpdates` run once per wall-clock tick after the all-rooms loop. At `SimRate > 1`
  multiple sim-seconds elapse between broadcasts.
- **Fingerprints gate broadcasts.** A new `AircraftStateDto` field not added to `TrainingDtoFingerprint` appears on
  initial subscribe (first `DetectChanges` returns `All`) but never updates live. The struct's structural equality is
  what detects change.
- **`SendCommandAsync` has no routing of its own.** A new verb gets a `RecordedCommandKind` and an `ArmTable` row in
  Yaat.Sim (its body is the engine's — `IActionHost` carries consumers only, no `Apply*` slot); a CRC handler that can issue it goes through
  `RecordAndDispatch*` so the same row runs and the text is recorded.
- **Recording is the router's.** Every routed command is recorded with its verdict (`RecordedCommand.Accepted`) — typed
  or CRC-sourced; the CRC entry points prepend `AS {tcp}` so identity round-trips on replay. A handler that mutates state without recording breaks replay
  silently.
- **Callsigns are per-room.** `TrainingRoom` owns its `World`/`ActiveSim`; `FindAircraft` searches only that room. There
  is no global aircraft lookup.
- **`IsBroadcastSuppressed` gates almost every broadcast** (set on temp replay rooms via `CreateTempReplayEngine`).
  Tick-processor and broadcast methods check it; forgetting the guard on a new broadcast leaks replay-engine state to
  real clients.
- **Registry lock ≠ tick-loop safety.** `TrainingRoomManager` mutations are under one `_lock`, but the per-room tick body
  runs lock-free and `AircraftChangeTracker` does no locking. Don't touch a room's `ChangeTracker` or `World` from a hub
  callback thread.
- **Two broadcast audiences.** Room SignalR group *and* admin connections. A new aircraft event must fan out to both or
  admins desync; deletes additionally hit CRC.
- **Delayed-spawn entries bypass the delta gate.** They broadcast on `AircraftUpdated` every tick unconditionally — don't
  assume all `AircraftUpdated` traffic is delta-gated.
