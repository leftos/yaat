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

### Threading gotcha: one thread, no blocking I/O

`RunTickLoop` iterates **all rooms on one thread inside one stopwatch**, so one room's slow lookup delays every room and
surfaces as a `TickBudgetMs` overrun. Nothing reachable from `TickPhysics` — phases, handlers, the `Ground.Layout`
fallback — may block on the network. The layout path is built around that constraint:

- `AirportLayoutDownloader` negative-caches a confirmed origin 404 for `NotFoundTtl` (6 h). `HttpFileCache.GetOrRefreshAsync`
  returns `HttpCacheResult(Content, NotFound)`, and only a genuine 404 latches — a network failure or timeout retries.
- `AirportGroundDataService` serves an expired entry (`CacheTtl`, 30 min) stale while a single background `Task.Run` refetches
  and re-parses it; the tick thread never waits on the TTL refresh.
- `ScenarioLifecycleService.WarmAircraftGroundLayouts` resolves every airport a loaded scenario references (departure,
  destination, spawn — delayed aircraft included) on the hub thread at load time.
- `NavigationDatabase.GetSid/GetStar/GetApproach` do not walk the supplementary prior-cycle CIFP chain for an airport whose
  current cycle lists no procedures of that kind (that chain models version drift; a procedure-less field would otherwise pay
  a burst of full file scans per probed route token). The chain still walks when no current cycle is loaded.

The ground-data service caches null entries on purpose: the per-sub-tick `aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft)`
fallback then costs a dictionary hit, and it is also the self-heal path once a background fetch lands. Accepted residual risk:
a first-ever fetch on the tick thread for an airport no scenario aircraft references (a mid-flight `DEST` to an unseen airport,
an exotic generator target) — one-time, bounded, and fast on the negative cache.

## `RoomEngine` — the per-room facade (`Simulation/RoomEngine.cs`)

One `RoomEngine` per room. It **owns** its `TrainingRoom` (`Room`, `:66`) and `RecordingManager` (`Recording`, `:64`,
set by `RoomEngineFactory` right after construction) and exposes `World` (`:67`, delegates to the room's world) and
`FindAircraft` (`:786`). Everything else is a **shared stateless singleton** injected via the primary constructor
(`:27`-`41`): `TickProcessor`, the handlers the router's host slots call (`CoordinationCommandHandler`,
`StripCommandHandler`, `TdlsCommandHandler`), `SimControlService`,
`ScenarioLifecycleService`, the broadcasters, and the ARTCC/ground data services. Per-room state lives on the
`TrainingRoom`, never on the singletons.

`BeginRoomScope()` (`:73`) opens a logging scope tagged with the room id so every log line within a hub call carries
`[roomId]`. `CreateTempReplayEngine` (`:371`) builds a throwaway engine on a synthetic room with
`IsBroadcastSuppressed = true` (`:380`) for snapshot generation / replay so it never leaks state to real clients.

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

## `TickProcessor` — `Simulation/TickProcessor.cs`

Stateless singleton; every method takes the `TrainingRoom`. It keeps **no list**: the order its bodies run in is the
spine's (`SpineOrder` in Yaat.Sim, [tick-loop.md](tick-loop.md)), and `RoomHost` — the base of `LiveRoomHost` and
`ReconstructionHost` — maps each host step and consumer onto one `internal` method here:

- **Pre-physics**: `HandlePrePhysicsResult` broadcasts each newly-spawned aircraft and runs `AfterAircraftSpawned`
  (auto-strip / auto-TDLS), and records generator spawns *after* their autotrack so the recorded snapshot carries the
  owner; `BroadcastTerminalEntries` takes the spine's drain; `ProcessDelayedHandoffs`; `SyncLiveTraffic` runs
  `ShadowTrafficSync.Sync` last — the pre-physics mutator of the aircraft set (see [live-traffic.md](live-traffic.md)).
- **Post-physics**: the ATC passes (`ProcessAutoAccept`, `ProcessPointoutAutoAck`, `ProcessFlightPlanCreatorAutoTrack`
  **before** `ProcessDeferredAutoTrack` so an explicit VP/DA controller wins over scenario `AutoTrackAirportIds` for the
  aircraft they just filed for, `ProcessCoordinationTimers`, `ProcessTowerLists`), the consumers of the engine's
  detectors (`BroadcastConflictAlerts`, `BroadcastEramConflictAlerts`), `ProcessAsdexAlerts`,
  `ProcessSoloTrainingEvaluation`, the drain consumers (`BroadcastWarnings` / `Notifications` / `PilotSpeech` /
  `PilotReadbacks` / `PilotTransmissions`, `ProcessApproachScores`, `ProcessDeferredStripDispatches`), the auto-strip and
  TDLS processors, `ProcessAutoDelete`, `ProcessSurfaceCoast`, and the rundown / live-traffic-status / timers
  "broadcast if changed" tail. `ProcessDeferredAutoTrack` claims a departure only once it first appears on STARS —
  i.e. crosses the acquisition floor (`FieldElevationResolver.IsBelowDisplayFloor`), not the instant its wheels leave
  the ground — so a track is never owned before it is displayed.

Per-step timing lives on the engine: attach a dictionary to `SimulationEngine.TickTimings` and every spine step records
under its `StepId` name (the soak runner's `--timings`, `ReconstructionBenchmarkTests`).

Several of these guard on `room.IsBroadcastSuppressed` before broadcasting (e.g. `ProcessCoordinationTimers`,
`BroadcastConflictAlerts`, `ProcessAutoDelete`). A new broadcast from a tick-processor method must add the
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
  reach it carrying an empty `TickChanges` because `RoomTickLoopService.DetectChanges` (`:212`) skips them when
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
`PositionSelections` (the Yaat.Sim map of connectionId → the position a bare `AS` selected; the room owns it for
its lifetime and every engine it creates reads it — see the yaat repo's `command-pipeline.md` § one identity). **Callsigns are per-room, not global** — `RoomEngine.FindAircraft` searches
only that room's `World.GetSnapshot()` (`:786`-`789`). There is no global aircraft lookup; reaching for one is a category
error. `UpdatePausedSince` (`:97`) stamps the continuous-pause clock that the retirement sweep reads; `IsAbandoned`
(`:76`) is true when no clients are connected.

**Session settings outlive the scenario.** `SessionSettings` (`RoomSessionSettings`) holds the room's copy of everything
a controller can toggle mid-session — the auto-* flags, `ValidateDctFixes`, solo mode and pacing, the auto-accept and
command-run delays, the auto-delete override, the dynamic-METAR intent. A load, restart, or rewind builds a **new**
`SimScenarioState`, and `ScenarioLifecycleService` seeds it with `room.SessionSettings.ApplyTo(scenario)` at both
construction sites. The scenario stays the runtime source of truth (every gate and DTO reads it, falling back to the room
copy only when no scenario is loaded); `SimControlService` writes both. See
[scenario-loading-and-generation.md](scenario-loading-and-generation.md#session-settings-belong-to-the-room-not-the-scenario-object).

**Members are connections, not people.** `Members` is keyed by SignalR connection id and each `RoomMember` carries
`Kind` (`ClientKind.Main` / `VStrips` / `VTdls`) and `JoinedAtUtc`. vStrips and vTDLS browser tabs join over the same
hub, so one controller can hold several members at once — `HasYaatClientMember` is the predicate that asks whether
any of them can actually work traffic. Between that and `CrcClientManager.GetClientsForRoom`,
`ScenarioLifecycleService.PauseIfUnattended` pauses a room the moment its last YAAT client and CRC client are gone
while browser tabs remain: the sim has nobody to serve. It is called from the **non-abandoned** branch of
`HandleClientLeft` and from `CrcWebSocketHandler`'s disconnect path. It deliberately does not touch `CleanupCts` —
a tab is a legitimate viewer, so room retirement stays governed by `IsAbandoned` and the paused-retirement sweep —
and it does not auto-resume.

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
  Yaat.Sim (and an `IActionHost` slot only while its state is the room's); a CRC handler that can issue it goes through
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
