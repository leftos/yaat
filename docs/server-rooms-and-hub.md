# Server Rooms, Tick Orchestration & the YAAT Hub Seam

> Read this before touching the hosted tick loop, `RoomEngine`, `TickProcessor`, `AircraftChangeTracker`, the
> `TrainingRoom*` types, or `TrainingHub`. It documents the yaat-server side of the `/hubs/training` (JSON) link: the one
> hosted loop that drives every room, the `RoomEngine` command-routing chain, room isolation, and the per-aircraft delta
> engine. The wire shape itself — the DTO pairs, the hub-method/broadcast catalogs, the three JSON source-gen contexts,
> and the canonical add-a-field checklist — lives in [training-hub-contract.md](training-hub-contract.md). For the
> per-aircraft physics step order inside `Yaat.Sim` see [tick-loop.md](tick-loop.md); for one command's journey through
> the parser and dispatcher see [command-pipeline.md](command-pipeline.md); for the parallel CRC (MessagePack) path see
> [crc-display-state.md](crc-display-state.md).
>
> All file paths below are in the yaat-server repo (`../yaat-server/src/Yaat.Server`) unless noted.

## Scope

```
SimulationHostedService   one PeriodicTimer @ 1 Hz, drives every room
        │  per room (if running):
        ▼
RoomEngine.ProcessPrePhysics → ProcessPhysics ×(SimRate × 4) → ProcessPostPhysics
        │  after the ALL-ROOMS loop:
        ▼
AircraftChangeTracker.DetectChanges  →  Broadcast{Training,Admin,Crc}Updates
```

`RoomEngine` is the per-room facade for everything else: commands, scenario lifecycle, recording, broadcast. The hub
(`TrainingHub`) is a thin RPC surface that resolves a connection to a `RoomEngine` and delegates.

## The hosted tick loop — `Simulation/SimulationHostedService.cs`

One `IHostedService` owns a single `PeriodicTimer(TimeSpan.FromSeconds(1))` (`:44`) — **one loop for all rooms**, not one
per room. Each wall-clock tick (`RunTickLoop`, `:92`):

1. Snapshots all rooms (`_rooms.GetAllRooms()`, `:100`) and stamps each room's continuous-pause clock via
   `UpdatePausedSince` (`:106`).
2. For each room with a running scenario (skips paused / no-scenario / no-engine, `:109`):
   - `simSeconds = Math.Max(1, (int)scenario.SimRate)` (`:117`).
   - For each sim-second: `ProcessPrePhysics()` once, then `ProcessPhysics(subDelta)` **`PhysicsSubTickRate` = 4** times
     (`subDelta = 0.25`, `:120`/`:128`), then `ProcessPostPhysics()` once, then `scenario.ElapsedSeconds += 1.0`
     (`:136`).
   - Records position history every 5 sim-seconds (10-entry ring, `:139`-`:149`), advances the weather timeline
     (`:153`) and applies playback actions in playback mode (`:164`).
   - **Ends with one `BroadcastSimState(room)` per processed wall-tick** (after the sim-second loop, in
     `ProcessRoomSecond`) so the client's elapsed clock stays live — the timeline label/scrubber and the base for
     the relative +15/−15 skips. The end-of-tape branch sets the paused state first, so that final tick's broadcast
     carries `IsPaused = true`. (Issue #209: previously elapsed only reached clients on pause/unpause/rewind/end.)
3. After the loop budget check (`TickBudgetMs = 800`, logs a warning if exceeded, `:181`).
4. **After the all-rooms loop**: `DetectChanges(allRooms)` (`:190`) then `await BroadcastUpdates(allRooms)` (`:191`),
   which fans out to training clients, admins, and CRC (`:224`).
5. Every minute (`PausedRetirementSweepInterval`, `:34`) runs `ScenarioLifecycleService.RetirePausedRoomsAsync` (`:196`)
   to evict rooms left paused past the threshold.

### Cadence gotcha: double cadence

Physics advances `SimRate` **sim-seconds per wall-clock tick** (`ElapsedSeconds += 1.0` inside the inner loop), but
`DetectChanges` + `BroadcastUpdates` run **once per wall-clock tick, after the all-rooms loop**. At `SimRate > 1`,
multiple sim-seconds elapse between broadcasts. Reading the tick body as "broadcast every sim-second" is wrong — that is
why broadcasts are deltas, and why a timing/rate feature must not assume one broadcast per sim-second. [tick-loop.md]
(tick-loop.md) covers the in-`Yaat.Sim` step order; the server adds the room loop and the post-loop broadcast on top.

## `RoomEngine` — the per-room facade (`Simulation/RoomEngine.cs`)

One `RoomEngine` per room. It **owns** its `TrainingRoom` (`Room`, `:66`) and `RecordingManager` (`Recording`, `:64`,
set by `RoomEngineFactory` right after construction) and exposes `World` (`:67`, delegates to the room's world) and
`FindAircraft` (`:786`). Everything else is a **shared stateless singleton** injected via the primary constructor
(`:27`-`41`): `TickProcessor`, the command handlers (`TrackCommandHandler`, `CoordinationCommandHandler`,
`StripCommandHandler`, `TdlsCommandHandler`, `FlightPlanCommandHandler`), `SimControlService`,
`ScenarioLifecycleService`, the broadcasters, and the ARTCC/ground data services. Per-room state lives on the
`TrainingRoom`, never on the singletons.

`BeginRoomScope()` (`:73`) opens a logging scope tagged with the room id so every log line within a hub call carries
`[roomId]`. `CreateTempReplayEngine` (`:371`) builds a throwaway engine on a synthetic room with
`IsBroadcastSuppressed = true` (`:380`) for snapshot generation / replay so it never leaks state to real clients.

### `SendCommandAsync` routing chain (`:515`)

A ~30-branch dispatch with order dependencies. After the scenario null-guard (`:519`), the `** ` force-override prefix
(`:529`), assignment enforcement (`:536`), and `AS <tcp>` extraction via `TrackCommandHandler.ExtractAsPrefix` (`:552`),
it parses once (`CommandParser.Parse`, `:554`) and dispatches. Key ordering:

- **Flight-plan amendment verbs are intercepted early** — `ChangeDestinationCommand` (`:563`), `CreateFlightPlanCommand`
  (`:597`), `CreateAbbreviatedFlightPlanCommand` (`:610`), `SetRemarksCommand` (`:623`) run before the generic chain so
  they route through `AmendFlightPlan` for recording + CRC strip push.
- **`SetActivePositionCommand` is special-cased**: it takes the `HandleSetActivePosition` branch only when
  `asOverrideTcp is null` (`:655`) — a bare `AS <tcp>` sets the connection's active position; `AS <tcp> <command>`
  instead resolves identity for the inner command.
- Then strip (`:659`), TDLS (`:663`), track (`:667`), coordination + global-coordination (`:671`/`:675`), global track
  ops (`:679`), global squawk (`:683`), taxi-all (`:687`), consolidation (`:691`), pause/unpause/simrate (`:695`-`:709`),
  ASDE-X all-alerts (`:716`), `ADD` aircraft (`:724`), ghost-track (`:733`), and finally the `else` →
  `HandleStandardCmd` → `CommandDispatcher` (`:753`).
- After dispatch, **successful** commands (except pause/unpause/simrate) call
  `Record(new RecordedCommand(...))` (`:758`), then `FlushTerminalEntries()` (`:765`) surfaces any queued SAY-class
  terminal entries even while paused.

Note the `result.Success` gate here is on the *outer* chain. The standard-command path's own recording behaviour
(including recording rejects) lives in `CommandDispatcher` / the standard handler — see
[command-pipeline.md](command-pipeline.md), which walks the standard branch in full.

### CRC-sourced command twins

CRC mutations are routed through the **same canonical command pipeline** as YAAT-client commands, via three twins, so
live and replay paths agree. Each prepends an `AS {tcp}` token to the recorded text so the controller's identity
round-trips on replay (built from `OwnerType`/`Subset`/`SectorId`, `:96`-`107`):

- `RecordAndDispatch(callsign, canonical, identity)` (`:86`) — track / coordination / ghost verbs. Records on success
  with `"AS {tcp} {canonical}"` (`:140`).
- `RecordAndDispatchStripAsync(callsign, canonical, crcClientId)` (`:153`) — strip verbs (no `AS` prefix; strips are not
  position-scoped on the ownership axis).
- `RecordAndDispatchFlightPlanAsync(...)` (`:195`) — CRC STARS-typed DA/VP creates. Spawns an **unsupported** track for
  an unknown callsign (`:254`) and rolls that spawn back on handler failure, gated on a `spawnedUnsupported` flag (`:314`)
  so a `DUP NEW ID` collision with a pre-existing aircraft doesn't delete it.

A handler that mutates room state without going through one of these (or `Record`) breaks replay silently.

## `TickProcessor` — `Simulation/TickProcessor.cs`

Stateless singleton; every method takes the `TrainingRoom`.

- **`ProcessPrePhysics`** (`:40`): `sim.TickPrePhysics()` then, for each newly-spawned aircraft, broadcasts
  `AircraftSpawned` and runs `AfterAircraftSpawned` (auto-strip / auto-TDLS); drains terminal entries; runs
  `ProcessDelayedHandoffs`.
- **`ProcessPhysics`** (`:71`): delegates straight to `sim.TickPhysics(delta)`.
- **`ProcessPostPhysics`** (`:80`): a fixed fan-out whose order matters. Notably
  `ProcessFlightPlanCreatorAutoTrack` runs **before** `ProcessDeferredAutoTrack` (`:88`-`:89`) so an explicit VP/DA
  controller wins over scenario `AutoTrackAirportIds` for the aircraft they just filed for. The rest in order:
  `ClearExpiredIdents`, `ProcessAutoAccept`, the two auto-tracks, `ProcessCoordinationTimers`, `ProcessTowerLists`,
  `ProcessVisualDetection`, `ProcessConflictAlerts`, `ProcessSoloTrainingEvaluation`, `ProcessPilotProactive`, the
  warnings/notifications/pilot-speech/readback/transmission broadcast drains, `ProcessApproachScores`, the auto-strip and
  auto-TDLS processors, and `ProcessAutoDelete` (`:82`-`:107`).

Several of these guard on `room.IsBroadcastSuppressed` before broadcasting (e.g. `ProcessCoordinationTimers` `:961`,
`ProcessConflictAlerts` `:1449`, `ProcessAutoDelete` `:1614`). A new broadcast from a tick-processor method must add the
same guard or it leaks replay/snapshot-engine state to real clients.

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
broadcast, after `DetectChanges` has already run. `Remove(callsign)` / `Clear()` (`:340`/`:342`) maintain the dictionary
as aircraft leave / the room resets.

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
  reach it carrying an empty `TickChanges` because `SimulationHostedService.DetectChanges` (`:212`) skips them when
  populating per-tick flags, so nothing aircraft-shaped is sent for them.
- **CRC connections** — `CrcBroadcastService.BroadcastUpdatesAsync` runs in the same after-the-loop, *un-gated* phase as
  `DetectChanges`/`BroadcastUpdates`, but it snapshots `room.World` itself rather than reading `TickChanges`. It must
  therefore skip `room.IsBroadcastSuppressed` rooms explicitly (alongside `scenario is null`) — otherwise a rewind /
  recording reload, which tears the world down and briefly repopulates it with the full initial scenario before restoring
  the target snapshot, leaks those transient aircraft to CRC as additive `ReceiveStarsTracks` adds that never get deleted
  (STARS ghost tracks; see [crc-display-state.md](crc-display-state.md) "Rewind / recording-load resync").

`ToTrainingDto(...)` is reused for both audiences and for the delayed-spawn DTO (`ToDelayedDto`, `:277`).

## `TrainingRoom` — the unit of isolation (`Simulation/TrainingRoom.cs`)

Each room owns its own `ActiveSim` / `ActiveScenario` / `World` (`:22`-`28`, falling back to a bare world when no
scenario is loaded), its `RoomEngine`, and a bag of per-room state: `ChangeTracker`, `TickChanges`, `StripState`,
`TdlsState`, `AsdexState`, `EramState`, `LineNumbers`, `AircraftAssignments` (callsign → connectionId), and
`ActivePositionByConnection` (`:57`-`69`). **Callsigns are per-room, not global** — `RoomEngine.FindAircraft` searches
only that room's `World.GetSnapshot()` (`:786`-`789`). There is no global aircraft lookup; reaching for one is a category
error. `UpdatePausedSince` (`:97`) stamps the continuous-pause clock that the retirement sweep reads; `IsAbandoned`
(`:76`) is true when no clients are connected.

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
- **`SendCommandAsync` is order-dependent.** FP amendment verbs (`ChangeDestination`, `SetRemarks`, `CreateFlightPlan`)
  are intercepted before the generic chain; `SetActivePosition` is special-cased on `asOverrideTcp is null`. Slot a new
  verb carefully, and most verbs that CRC can also issue need a `RecordAndDispatch*` twin or replays diverge.
- **Recording is woven into the command path.** Successful commands `Record(...)` the raw text + connection id; CRC twins
  prepend `AS {tcp}` so identity round-trips on replay. A handler that mutates state without recording breaks replay
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
