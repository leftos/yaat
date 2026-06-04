# Training Hub Wire Contract (Client ↔ Server)

> Read this before adding a hub method, adding or changing a DTO field that crosses `/hubs/training`, or wiring a new
> server→client broadcast. It documents the JSON wire shape between `ServerConnection.cs` (yaat) and `TrainingHub.cs`
> (yaat-server), the three source-generated JSON contexts, and the one checklist that keeps a new `AircraftUpdated`
> field from silently never updating. For the server-side internals behind the contract — the hosted tick loop, the
> `RoomEngine` routing chain, room isolation, and the delta engine — see [server-rooms-and-hub.md](server-rooms-and-hub.md).
> For one command's full journey through the parser and dispatcher see [command-pipeline.md](command-pipeline.md); for
> broadcast *timing* see [tick-loop.md](tick-loop.md); for the parallel CRC (MessagePack) protocol see
> [crc-display-state.md](crc-display-state.md).

## What this contract is

YAAT Client talks to yaat-server over a standard ASP.NET Core SignalR connection using the **JSON** hub protocol on
`/hubs/training`. There is **no shared assembly and no generated wire artifact**. The two ends are two separate sets of
C# `record` types — `ServerConnection.cs` (client, in `Yaat.Client.Core`) and `TrainingDtos.cs` (server) — that the
SignalR `JsonHubProtocol` happens to serialize compatibly. The only binding is **matching JSON property names**. Nothing
at compile time enforces that the two repos agree; the contract is verified by reading both files side by side.

```
Yaat.Client  ──InvokeAsync<T>("HubMethod", args)──►  TrainingHub      (client → server)
Yaat.Client  ◄──On<T>("EventName", payload)───────  Broadcast layer  (server → client)
                         /hubs/training (JSON)
```

The two ends live in:

- **Client** — `src/Yaat.Client.Core/Services/ServerConnection.cs`: every `InvokeAsync` wrapper, every `On<T>` handler
  (wired in `ConnectAsync`, `ServerConnection.cs:110`), and the client copies of the DTO records (from
  `ServerConnection.cs:723`).
- **Server** — `../yaat-server/src/Yaat.Server/Hubs/TrainingHub.cs` (the hub methods) and
  `../yaat-server/src/Yaat.Server/Dtos/TrainingDtos.cs` (the server DTO records). The server→client payloads are built by
  `../yaat-server/src/Yaat.Server/Simulation/DtoConverter.cs` and fanned out by `TrainingBroadcastService.cs`.

## The `AircraftStateDto` ↔ `AircraftDto` pair (the central trap)

The single most-edited wire object is the per-aircraft state. It has a **different C# type name and a different field
order in each repo**, yet it is the *same* wire object:

| | Server | Client |
|---|---|---|
| Type | `AircraftStateDto` (`TrainingDtos.cs:3`) | `AircraftDto` (`ServerConnection.cs:723`) |
| `Remarks` position | constructor param 24 (right after `IsOnGround`, `TrainingDtos.cs:27`) | param 19, with a `= ""` default (`ServerConnection.cs:742`) |

SignalR's JSON protocol matches by **property name** (the contexts set `PropertyNameCaseInsensitive = true`), so the
positional/order difference is harmless. A side-by-side diff "screams mismatch" to a newcomer — it is not. **The
invariant is property-name + type compatibility, not positional or name identity.** When you add a field you must add it
to *both* records; whether the order matches is irrelevant to the wire, but every server field needs a client
counterpart of a JSON-compatible type or it deserializes to the client default.

`AircraftStateDto` is produced by `DtoConverter.ToTrainingDto` (`DtoConverter.cs:620`). Note that the server maps
`Assigned*` fields onto the wire `Assigned*` slots — e.g. `ac.Targets.AssignedMagneticHeading` →
`AssignedHeading` (`DtoConverter.cs:644`), not the transient `Target*` physics goal. The wire DTO is the autopilot/UI
projection, not the raw physics state.

## Hub method catalog (client → server)

Every row is `ServerConnection` wrapper → the **string literal** passed to `InvokeAsync` → server hub method. The
string literal is the contract; the C# method names on either side are cosmetic. **Several string literals diverge from
both the wrapper name and the hub method's own semantics** — grep for the string, not the method:

| Client wrapper (`ServerConnection.cs`) | Invoke string | Server method (`TrainingHub.cs`) |
|---|---|---|
| `CreateRoomAsync(cid, initials, artccId, kind)` | `CreateRoom` | `CreateRoom(cid, initials, artccId, kind)` `:110` |
| `JoinRoomAsync(roomId, cid, initials, artccId, kind)` | `JoinRoom` | `JoinRoom(...)` `:139` |
| `LeaveRoomAsync()` | `LeaveRoom` | `LeaveRoom()` `:186` |
| `GetActiveRoomsAsync()` | `GetActiveRooms` | `GetActiveRooms()` `:217` |
| `FindRoomForMyCidAsync(cid)` | `FindRoomForMyCid` | `FindRoomForMyCid(cid)` `:228` |
| `LoadScenarioAsync(json, …rates)` | `LoadScenario` | `LoadScenario(...)` `:297` |
| `GetScenarioJsonByIdAsync(id, key)` | `GetScenarioJsonById` | `GetScenarioJsonById(id, key)` `:336` |
| `GetScenariosAsync(accessKey)` | `GetScenarios` | `GetScenarios(accessKey)` `:365` |
| `ValidateTrainingKeyAsync(artccId, key)` | `ValidateTrainingKey` | `ValidateTrainingKey(...)` `:397` |
| `UnloadScenarioAircraftAsync()` | `UnloadScenarioAircraft` | `UnloadScenarioAircraft()` `:449` |
| `ConfirmUnloadScenarioAsync()` | `ConfirmUnloadScenario` | `ConfirmUnloadScenario()` `:460` |
| `SendCommandAsync(callsign, command, initials)` | `SendCommand` | `SendCommand(...)` `:508` |
| `SendChatAsync(initials, message)` | `SendChat` | `SendChat(...)` `:896` |
| `AmendFlightPlanAsync(callsign, dto)` | `AmendFlightPlan` | `AmendFlightPlan(...)` `:847` |
| `RequestNewBeaconCodeAsync(callsign)` | `RequestNewBeaconCode` | `RequestNewBeaconCode(callsign)` `:869` |
| `RequestFlightStripForAircraftAsync(callsign)` | `RequestFlightStripForAircraft` | `RequestFlightStripForAircraft(callsign)` `:886` |
| `SetAutoAcceptDelayAsync(seconds)` | `SetAutoAcceptDelay` | `SetAutoAcceptDelay(seconds)` `:524` |
| `SetAutoDeleteModeAsync(mode)` | `SetAutoDeleteMode` | `SetAutoDeleteMode(mode)` `:545` |
| `SetValidateDctFixesAsync(validate)` | `SetValidateDctFixes` | `SetValidateDctFixes(...)` `:771` |
| `SetRpoShowPilotSpeechAsync(enabled)` | `SetRpoShowPilotSpeech` | `SetRpoShowPilotSpeech(...)` `:792` |
| `SetSoloTrainingModeAsync(enabled)` | `SetSoloTrainingMode` | `SetSoloTrainingMode(...)` `:811` |
| `SetSoloPacingRatesAsync(…)` | `SetSoloPacingRates` | `SetSoloPacingRates(...)` `:830` |
| `SetAutoClearedToLandAsync(enabled)` | `SetAutoClearedToLand` | `SetAutoClearedToLand(...)` `:563` |
| `SetAutoCrossRunwayAsync(enabled)` | `SetAutoCrossRunway` | `SetAutoCrossRunway(...)` `:581` |
| `RewindToAsync(elapsedSeconds)` | **`RewindTo`** | `RewindTo(...)` `:907` |
| `RewindFromSnapshotAsync(…)` | `RewindFromSnapshot` | `RewindFromSnapshot(...)` `:925` |
| `TakeControlAsync()` | `TakeControl` | `TakeControl()` `:943` |
| `GetTimelineInfoAsync()` | `GetTimelineInfo` | `GetTimelineInfo()` |
| `ExportRecordingAsync()` | `ExportRecording` | `ExportRecording()` `:961` (stream) |
| `LoadRecordingAsync(bytes)` | `LoadRecording` | `LoadRecording(stream)` `:1015` |
| `MigrateRecordingAsync(json)` | `MigrateRecording` | `MigrateRecording(json)` `:990` |
| `GetServerLogPathAsync()` | `GetServerLogPath` | `GetServerLogPath()` `:1009` |
| `GetAirportGroundLayoutAsync(id)` | `GetAirportGroundLayout` | `GetAirportGroundLayout(id)` |
| `GetFacilityVideoMapsAsync(…)` | `GetFacilityVideoMaps` | `GetFacilityVideoMaps(...)` `:1098` |
| `GetFacilityVideoMapsForArtccAsync(…)` | `GetFacilityVideoMapsForArtcc` | `GetFacilityVideoMapsForArtcc(...)` `:1103` |
| `GetApproachReportAsync()` | `GetApproachReport` | `GetApproachReport()` `:646` |
| `GetSessionReportAsync()` | `GetSessionReport` | `GetSessionReport()` `:659` |
| `LoadWeatherAsync(json, reconstructMetars)` | `LoadWeather` | `LoadWeather(json, reconstructMetars)` `:599` |
| `ClearWeatherAsync()` | `ClearWeather` | `ClearWeather()` `:616` |
| `LoadArrivalGeneratorsAsync(json)` | `LoadArrivalGenerators` | `LoadArrivalGenerators(json)` `:628` |
| `AssignAircraftAsync(callsigns, conn)` | `AssignAircraft` | `AssignAircraft(...)` `:476` |
| `UnassignAircraftAsync(callsigns)` | `UnassignAircraft` | `UnassignAircraft(...)` `:488` |
| `GetAircraftAssignmentsAsync()` | `GetAircraftAssignments` | `GetAircraftAssignments()` `:500` |
| `GetAccessibleFacilitiesAsync()` | `GetAccessibleFacilities` | `GetAccessibleFacilities()` `:1411` |
| `GetFlightStripsConfigForFacilityAsync(id)` | `GetFlightStripsConfigForFacility` | `GetFlightStripsConfigForFacility(id)` `:1435` |
| `GetAccessibleTdlsFacilitiesAsync()` | `GetAccessibleTdlsFacilities` | `GetAccessibleTdlsFacilities()` |
| `GetTdlsConfigForFacilityAsync(id)` | `GetTdlsConfigForFacility` | `GetTdlsConfigForFacility(id)` |
| `RequestFullTdlsStateAsync()` | `RequestFullTdlsState` | `RequestFullTdlsState()` |
| `AdminAuthenticateAsync(password)` | `AdminAuthenticate` | `AdminAuthenticate(password)` `:1114` |
| `AdminGetRoomsAsync()` | **`AdminGetScenarios`** | `AdminGetScenarios()` `:1125` |
| `AdminSetRoomFilterAsync(roomId)` | **`AdminSetScenarioFilter`** | `AdminSetScenarioFilter(roomId)` `:1135` |
| `GetCrcLobbyClientsAsync()` | `GetCrcLobbyClients` | `GetCrcLobbyClients()` `:1166` |
| `PullCrcClientAsync(id)` | `PullCrcClient` | `PullCrcClient(id)` `:1183` |
| `KickCrcClientAsync(id)` | `KickCrcClient` | `KickCrcClient(id)` `:1218` |
| `KickMemberAsync(cid)` | `KickMember` | `KickMember(cid)` `:1243` |
| `GetCrcRoomMembersAsync()` | `GetCrcRoomMembers` | `GetCrcRoomMembers()` `:1288` |
| heartbeat loop (`RunHeartbeat`) | `Heartbeat` | `Heartbeat()` `:1110` (no-op) |

`SpawnAircraft` / `DeleteAircraft` are **not** standalone hub methods (the legacy yaat-server CLAUDE.md list is stale on
this). They are routed through `SendCommand` as parsed verbs — `SpawnNowCommand`, `SpawnDelayCommand`, `DeleteCommand` —
see the routing chain in [server-rooms-and-hub.md](server-rooms-and-hub.md). `CreateRoom` now takes a `kind` argument and
`SendCommand` takes `initials`; both also diverge from that stale list.

### The "Not in a room" sentinel

Most hub methods resolve the caller's engine via `ResolveEngine(connectionId)` (`TrainingHub.cs:1340`) and, when it
returns null, **return a failure DTO rather than throw** — e.g. `SendCommand` returns
`new CommandResultDto(false, "Not in a room")` (`TrainingHub.cs:513`). Setters that return `void`/`Task` simply
early-return. Clients must check `Success`, not rely on exceptions. `ResolveEngine` also routes admins through their
single-room filter (`GetAdminFilter`); an admin with no filter set resolves to null.

## Server → client broadcast catalog

Every `On<T>` handler is registered in `ServerConnection.ConnectAsync` (`ServerConnection.cs:110`-`172`). Event string →
payload DTO → the `ServerConnection` C# event it re-raises:

| Invoke string | Payload | `ServerConnection` event |
|---|---|---|
| `AircraftUpdated` | `AircraftDto` | `AircraftUpdated` (delta **and** delayed-countdown channel — see below) |
| `AircraftSpawned` | `AircraftDto` | `AircraftSpawned` |
| `AircraftDeleted` | `string` callsign | `AircraftDeleted` |
| `SimulationStateChanged` | `(bool, int, double, bool, double)` | `SimulationStateChanged` |
| `TerminalBroadcast` | `TerminalBroadcastDto` | `TerminalEntryReceived` |
| `PilotTransmissionBroadcast` | `PilotTransmissionBroadcastDto` | `PilotTransmissionReceived` |
| `RoomMemberChanged` | `RoomMemberChangedDto` | `RoomMemberChanged` |
| `CrcLobbyChanged` | `CrcLobbyChangedDto` | `CrcLobbyChanged` |
| `CrcRoomMembersChanged` | `CrcRoomMembersChangedDto` | `CrcRoomMembersChanged` |
| `WeatherChanged` | `WeatherChangedDto` | `WeatherChanged` |
| `ArrivalGeneratorsChanged` | `ArrivalGeneratorsChangedDto` | `ArrivalGeneratorsChanged` |
| `HeldDeparturesChanged` | `HeldDeparturesChangedDto` (carries `RundownDto`) | `HeldDeparturesChanged` |
| `PositionDisplayChanged` | `PositionDisplayConfigDto` | `PositionDisplayChanged` |
| `ScenarioLoaded` | `ScenarioLoadedDto` | `ScenarioLoaded` (+ `StripsConfigChanged`) |
| `ScenarioUnloaded` | *(none)* | `ScenarioUnloaded` (+ `StripsConfigChanged(null)`) |
| `AircraftAssignmentsChanged` | `AircraftAssignmentsDto` | `AircraftAssignmentsChanged` |
| `SessionSettingsChanged` | `SessionSettingsDto` | `SessionSettingsChanged` |
| `KickedFromRoom` | `string` | `KickedFromRoom` |
| `RoomRetired` | `string` reason | `RoomRetired` |
| `ExportRecordingProgress` | `(int, int)` | `ExportRecordingProgress` |
| `FlightStripsStateChanged` | `FlightStripsStateDto` | `FlightStripsStateChanged` |
| `StripItemsChanged` | `List<StripItemDto>` | `StripItemsChanged` |
| `TdlsItemChanged` / `TdlsItemRemoved` / `TdlsStateChanged` | `TdlsItemDto` / `TdlsItemRemovedDto` / `TdlsStateDto` | matching events |
| `RoomAvailableForCid` | `string` roomId | `RoomAvailableForCid` |
| `ServerRestarting` | `(DateTime, string, int)` | `ServerRestarting` |
| `ServerRestartReady` | *(none)* | `ServerRestartReady` |
| `ServerRestartComplete` | `List<RestoredRoomInfoDto>` | `ServerRestartComplete` |

## Three JSON source-gen contexts and the WASM failure mode

SignalR's `JsonHubProtocol` calls `JsonSerializer.Serialize<object>(...)` on every argument and payload. Under a default
.NET 10 **WASM** publish, reflection-based metadata is disabled to keep the bundle slim, so any type without compile-time
metadata throws `JsonSerializerIsReflectionDisabled` **at first use, with no compile error**. The desktop client falls
through to reflection and works fine — so a forgotten registration is invisible until someone runs the browser client.

`HubJsonContractTests` (`tests/Yaat.Client.Tests`) closes part of that gap: it reflects over every public `Task<T>`
method on `ServerConnection` (each one an `InvokeAsync<T>` wrapper) and fails the build when a Core-owned return type is
missing from `YaatHubJsonContext`. It covers **Core return types only** — broadcast (`.On<T>`) payloads, method
arguments, and the Strips/Tdls contexts aren't reflectable from the method surface and stay unguarded.

The fix is a `[JsonSerializable]` registration in one of **three** source-generated contexts, inserted at the head of the
resolver chain in the `AddJsonProtocol` callback inside `ServerConnection.ConnectAsync` (`ServerConnection.cs:103`-`106`),
at chain positions **0 / 1 / 2**:

| Position | Context | Assembly | Owns |
|---|---|---|---|
| 0 | `YaatHubJsonContext` | `Yaat.Client.Core` | room state, aircraft, scenario, weather, CRC, session-settings, reports (`YaatHubJsonContext.cs`) |
| 1 | `YaatStripsHubJsonContext` | `Yaat.Client.Strips` | `FlightStripsStateDto`, `StripItemDto`, `FlightStripsConfigDto`, `AccessibleFacilityDto`, `CommandResultDto` (`YaatStripsHubJsonContext.cs`) |
| 2 | `YaatTdlsHubJsonContext` | `Yaat.Client.Tdls` | `TdlsItemDto`, `TdlsStateDto`, `TdlsConfigDto`, … (`YaatTdlsHubJsonContext.cs`) |

The three contexts own **disjoint** DTO sets, split by assembly so the WASM client can ship the Strips/Tdls views
without taking a runtime dependency on Core. `IJsonTypeInfoResolver` chains are consulted per type, so each registration
is self-contained; the chain falls through to the default reflection resolver afterward, which is what keeps the desktop
client working and covers the few primitives source-gen still passes through. Because Strips/Tdls payloads cross the
**same** `/hubs/training` connection, the real contract surface is larger than the Core context's `[JsonSerializable]`
list suggests — `CommandResultDto`, for instance, is registered in the Strips context, not Core. Those payloads are
documented in [flight-strips.md](flight-strips.md) and [vtdls.md](vtdls.md).

## Delta vs full envelope — `AircraftUpdated` is overloaded

`AircraftUpdated` is **not** a "one aircraft spawned" event. New aircraft are announced via `AircraftSpawned`, but
`AircraftUpdated` carries two other kinds of traffic:

1. **Per-tick deltas** — `TrainingBroadcastService.BroadcastTrainingUpdates` sends `AircraftUpdated` for a callsign
   **only when** the tick's change flags include `DtoChangeFlags.TrainingDto` (`TrainingBroadcastService.cs:184`). Those
   flags come from `AircraftChangeTracker.DetectChanges`, which diffs the current state against the last-broadcast
   `TrainingDtoFingerprint` (`AircraftChangeTracker.cs:145`, `:497`).
2. **Delayed-spawn countdowns** — every delayed-queue entry is broadcast on `AircraftUpdated` **unconditionally every
   tick** (its `Delayed (Ns)` status changes each second), bypassing the delta gate
   (`TrainingBroadcastService.cs:191`-`196`).

**Full state** (the seed a joining/reconnecting client gets) is carried separately: `RoomStateDto.AllAircraft`,
`ScenarioLoadedDto.AllAircraft`, `LoadScenarioResult.AllAircraft`, and `RewindResultDto.Aircraft` all carry the complete
manifest, built by `DtoConverter.ToTrainingDto` for every aircraft regardless of fingerprint.

This split is the source of the most common wire bug — see the checklist below.

## Session-settings fan-out

Roughly 13 session-settings fields are duplicated across **four** DTOs and must move in lockstep:
`LoadScenarioResult` (`TrainingDtos.cs:105`), `RoomStateDto` (`:182`), `ScenarioLoadedDto` (`:213`), and
`SessionSettingsDto` (`:241`) — with the same set on the client side. The fields:
`AutoDeleteOverride`, `EffectiveAutoDeleteMode`, `AutoAcceptDelaySeconds`, `AutoClearedToLand`, `AutoCrossRunway`,
`ValidateDctFixes`, `SoloTrainingMode`, `SoloParkingInitialCallupRatePercent`, `SoloArrivalGeneratorRatePercent`,
`SoloGoAroundProbabilityPercent`, `HasSoloParkingInitialCallupSource`, `HasSoloArrivalGeneratorSource`,
`RpoShowPilotSpeech`. The four DTOs feed three different paths — initial join (`RoomStateDto`), scenario load
(`LoadScenarioResult` / `ScenarioLoadedDto`), and live update (`SessionSettingsDto`). Add a setting to fewer than all
four and it silently drops on whichever path you missed.

## Streaming methods don't use `InvokeAsync`

`ExportRecording` and `LoadRecording` move large payloads as chunked `IAsyncEnumerable<byte[]>` (16 KB chunks; see
`ServerConnection.ChunkBytes`, `ServerConnection.cs:563`) via `StreamAsync` / a streamed parameter, not `InvokeAsync<T>`.
Export progress rides a **separate** broadcast side-channel, `ExportRecordingProgress` (a server→client `(int, int)`
event), not the stream itself. Copying the `InvokeAsync<T>` wrapper pattern for a large recording payload will fail.

## Checklist: adding an `AircraftUpdated` field

This is the canonical version of the add-a-field flow. [server-rooms-and-hub.md](server-rooms-and-hub.md) links here
rather than restating it.

1. **Add to `AircraftStateDto`** (`../yaat-server/.../Dtos/TrainingDtos.cs:3`) — a new constructor param with a default
   so older positional call sites still compile.
2. **Add to `AircraftDto`** (`src/Yaat.Client.Core/Services/ServerConnection.cs:723`) — **same JSON property name**, a
   JSON-compatible type, and a default. Order need not match the server.
3. **Map it in `DtoConverter.ToTrainingDto`** (`../yaat-server/.../Simulation/DtoConverter.cs:620`) so the value is
   actually written onto the wire DTO.
4. **Add it to `TrainingDtoFingerprint`** and capture it in `CaptureTrainingDto`
   (`../yaat-server/.../Simulation/AircraftChangeTracker.cs:145`, `:497`). **Skip this step and the field round-trips on
   initial join — because the full manifest carries it — but never updates live**, because `DetectChanges` only sets
   `DtoChangeFlags.TrainingDto` when a *fingerprinted* field changes. This is the field-round-trips-but-never-updates
   bug. (If the field is a non-`AircraftDto` topic — STARS/ASDE-X/ERAM/Tower-Cab — it belongs in a *different*
   fingerprint struct; see the struct list in [server-rooms-and-hub.md](server-rooms-and-hub.md).)
5. **Consume it client-side** wherever the `AircraftUpdated`/spawn/manifest handlers land it (typically a sub-VM or the
   radar/aircraft-list projection).
6. **Register the type for source-gen** if you introduced a *new* DTO (not just a field on an existing one): add a
   `[JsonSerializable]` entry to the matching context (Core / Strips / Tdls) or the WASM client throws
   `JsonSerializerIsReflectionDisabled` at runtime.
7. **Docs** — update `COMMANDS.md` / `docs/architecture.md` per the CLAUDE.md conventions if the field is user-visible.

## Pitfalls

- **Server type name ≠ client type name for the same wire object.** `AircraftStateDto` (server) and `AircraftDto`
  (client) are one wire object with mismatched field order; the wire is property-name based. Don't "fix" the apparent
  mismatch by reordering — the contract is name + type compatibility.
- **The invoke string is the contract; the C# method name is cosmetic.** `AdminGetRoomsAsync` invokes
  `AdminGetScenarios`; `AdminSetRoomFilterAsync` invokes `AdminSetScenarioFilter`; `RewindToAsync` invokes `RewindTo`.
  Grep for the string literal, not the method name.
- **Round-trips on join but never updates live.** A new `AircraftDto` field added on both repos and mapped in
  `DtoConverter` will appear on initial subscribe (the full manifest carries everything) yet stay frozen until you add
  the field to `TrainingDtoFingerprint` (step 4). The fingerprint struct's compiler-generated structural equality is
  what detects change — a field not in the struct is invisible to the delta.
- **`AircraftUpdated` is overloaded.** It is both the per-tick delta channel and the delayed-spawn countdown channel.
  Delayed entries are sent every tick unconditionally, bypassing the delta gate — don't assume all `AircraftUpdated`
  traffic is delta-gated.
- **Session settings fan out across four DTOs.** Add a setting to fewer than all four (`LoadScenarioResult`,
  `RoomStateDto`, `ScenarioLoadedDto`, `SessionSettingsDto`) on both repos and the value drops on the join, load, or
  live-update path you missed.
- **WASM dies silently on a missing source-gen registration.** The desktop client falls through to reflection; the WASM
  client throws `JsonSerializerIsReflectionDisabled` on first use. New cross-boundary types need a `[JsonSerializable]`
  entry in the right context.
- **Streaming methods are not `InvokeAsync`.** `ExportRecording` / `LoadRecording` are chunked `IAsyncEnumerable<byte[]>`
  with `ExportRecordingProgress` as a separate event channel.
- **The strip/TDLS surface is larger than the Core context suggests.** Strip and TDLS DTOs cross the same connection but
  are registered in the Strips/Tdls contexts and documented in [flight-strips.md](flight-strips.md) /
  [vtdls.md](vtdls.md).
