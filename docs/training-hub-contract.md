# Training Hub Wire Contract (Client ↔ Server)

> Read this before adding a hub method, adding or changing a DTO field that crosses `/hubs/training`, or wiring a new
> server→client broadcast. It documents the JSON wire shape between `ServerConnection.cs` (yaat) and `TrainingHub.cs`
> (yaat-server), the three source-generated JSON contexts, and the one checklist that keeps a new `AircraftUpdated`
> field from silently never updating. For the server-side internals behind the contract — the hosted tick loop, the
> `RoomEngine` routing chain, room isolation, and the delta engine — see [server-rooms-and-hub.md](server-rooms-and-hub.md).
> For one command's full journey through the parser and dispatcher see [command-pipeline.md](command-pipeline.md); for
> broadcast *timing* see [tick-loop.md](tick-loop.md); for the parallel CRC (MessagePack) protocol see
> [crc-display-state.md](crc-display-state.md).

## Authentication

`/hubs/training` is `[Authorize]`-gated (mentor-or-instructor connection gate). Clients present a YAAT session
token: the desktop supplies it via the SignalR `AccessTokenProvider` (→ `access_token` query), the WASM apps via
the same-origin `yaat_session` cookie. The server reads the controller's CID/rating from the token claims, so
room/scenario methods that used to take a `cid`/`accessKey` argument no longer do. See
[vatsim-auth.md](vatsim-auth.md) for the full flow.

## What this contract is

The YAAT client talks to yaat-server over a standard ASP.NET Core SignalR connection using the **JSON** hub protocol on
`/hubs/training`. There is **no shared assembly and no generated wire artifact**. The two ends are two separate sets of
C# `record` types — `ServerConnection.cs` (client, in `Yaat.Client.Core`) and `TrainingDtos.cs` (server) — that the
SignalR `JsonHubProtocol` happens to serialize compatibly. The only binding is **matching JSON property names**. Nothing
at compile time enforces that the two repos agree; the contract is verified by reading both files side by side. That holds for DTO fields; a hub method's **arguments** are positional and their count is checked (see [Changing a hub method's parameters](#changing-a-hub-methods-parameters)).

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

SignalR's JSON protocol matches a DTO's fields by **property name** (the contexts set `PropertyNameCaseInsensitive = true`), so the
positional/order difference is harmless. (A hub method's own arguments are different: they bind by position.) A side-by-side diff "screams mismatch" to a newcomer — it is not. **The
invariant is property-name + type compatibility, not positional or name identity.** When you add a field you must add it
to *both* records; whether the order matches is irrelevant to the wire, but every server field needs a client
counterpart of a JSON-compatible type or it deserializes to the client default.

`AircraftStateDto` is produced by `DtoConverter.ToTrainingDto`. Note that the server maps
`Assigned*` fields onto the wire `Assigned*` slots — e.g. `ac.Targets.AssignedMagneticHeading` →
`AssignedHeading`, not the transient `Target*` physics goal. The wire DTO is the autopilot/UI
projection, not the raw physics state. The same assigned-vs-actual split applies to the transponder: `ac.Transponder.Code`
→ `BeaconCode` (the reported/current code) and `ac.Transponder.AssignedCode` → `AssignedBeaconCode` (the ATC-assigned
code) cross as **two** fields, so the radar datablock can flag a beacon-code mismatch when the pilot has not yet squawked
the assigned code.

`IsIdenting` is a third transponder field, carrying `ac.Transponder.IsIdenting` verbatim so YAAT's own radar can flash
the `ID` token for the ~18 s the sim keeps the ident alive. Do **not** confuse it with the CRC-side
`StarsTrackDto.TransponderMode`, which `DtoConverter.MapTransponderMode` renders as the flags string `"A, C, Identing"` —
that is a different field on a different wire for a different consumer. The training-hub `TransponderMode` stays the raw
`"C"`/`"Standby"` mode string. **`IsIdenting` must stay in `TrainingDtoFingerprint`**: it is transient state that flips
twice on an otherwise-static aircraft, so without it neither the on- nor the off-edge is ever broadcast and the flash
never starts or stops (`ChangeDetectionTests.IsIdenting_Change_TriggersTrainingDtoFlag_OnBothTransitions` guards this).

`SpeedMax` (bool) is `ac.Targets.SpeedMaxHeld`, true while SMAX (maintain maximum forward speed) is in force; `AssignedSpeed`/`AssignedMach` then carry its live figure, and the client shows `MAX` before it in the ASpd column and `SMAX` in the EuroScope tag. It is part of the change-tracker fingerprint, so a change of the mode alone broadcasts.

`TargetSpeedKts` carries `ac.Targets.TargetSpeed` (the transient physics goal, null when none) — the one `Target*` value on the wire, and it is not `AssignedSpeed`. It exists so the client can run the sim's own parked-or-held classification (`GroundConflictDetector.IsParkedOrHeld`, whose at-rest test excludes a `LiningUp` aircraft creeping at 2 kt by its target speed) for the Ground view's push-route preview; it is already part of `TrainingDtoFingerprint`.

**Heading frame mismatch — `Heading` is TRUE, `AssignedHeading` is MAGNETIC.** The DTO's bare `Heading` field carries
`ac.TrueHeading.Degrees` (in `ToTrainingDto`) — true heading — while `AssignedHeading` carries
`ac.Targets.AssignedMagneticHeading?.Degrees` — magnetic. Client-side, `AircraftModel` reconstructs typed
`TrueHeading`/`MagneticHeading?` structs on receipt; to display the live heading in magnetic (what controllers read), convert at
the aircraft's position via `Heading.ToMagnetic(MagneticDeclination.GetDeclination(Position))`. Both heading structs expose
`ToDisplayString()` (3-digit zero-padded, north = `360`, never `000`) — use it for every UI/readback heading instead of
hand-rolling `:F0`/`:D3` on `.Degrees`.

**`TowbarTrueHeadingDeg` (true, nullable) is the tug's pose.** Present only while a `PushbackPhase` drives the aircraft:
the true heading from the nose gear out along the towbar to the tug, from `ac.Ground.TowbarTrueHeading`
(`PushbackPhase.SteerTowbar`'s bicycle model, [ground/pushback.md](ground/pushback.md)). Null means no tug is attached.
It is in `TrainingDtoFingerprint` on purpose — it is the one thing that moves as the tug swings through a turn, and
without it the client's tug would freeze where the first update left it (`ChangeDetectionTests.TowbarTrueHeading_Change…`).
The client keeps it as `AircraftModel.TowbarHeading` and `GroundRenderer.DrawTug` draws from it.

## Hub method catalog (client → server)

Every row is `ServerConnection` wrapper → the **string literal** passed to `InvokeAsync` → server hub method. The
string literal is the contract; the C# method names on either side are cosmetic. **Several string literals diverge from
both the wrapper name and the hub method's own semantics** — grep for the string, not the method:

| Client wrapper (`ServerConnection.cs`) | Invoke string | Server method (`TrainingHub.cs`) |
|---|---|---|
| `CreateRoomAsync(initials, artccId, kind)` | `CreateRoom` | `CreateRoom(initials, artccId, kind)` — CID/rating from token claims; `artccId` must be a permitted ARTCC (token home + operator grants) or it throws `HubException` |
| `GetMyPermittedArtccsAsync()` | `GetMyPermittedArtccs` | `GetMyPermittedArtccs()` — home ARTCC first, then operator grants |
| `JoinRoomAsync(roomId, initials, artccId, kind)` | `JoinRoom` | `JoinRoom(roomId, initials, artccId, kind)` → `RoomStateDto?`; `null` when the room is gone, or was retired while the join ran (the connection leaves the group and any CRC client bound to the room is unbound) |
| `LeaveRoomAsync()` | `LeaveRoom` | `LeaveRoom()` |
| `CloseRoomAsync(roomId)` | `CloseRoom` | `CloseRoom(roomId)` — the client calls it when its `JoinRoom` right after `CreateRoom` throws; only the room's creator (CID), while no scenario load holds the room and no other connection is a member, else it throws `HubException` (`Room {roomId} does not exist`, `Only the room's creator can close it`, `The room is loading a scenario. Try again when it has loaded.`, `The room has other members; leave it instead`); removes the room with the same teardown as abandoned-room cleanup and evicts (`RoomRetired` `The room's creator closed it.`) any connection that joined meanwhile. A `CreateRoom` that throws after registering the room closes it the same way (`RoomRetired` `The room could not be created.`) before rethrowing |
| `GetActiveRoomsAsync()` | `GetActiveRooms` | `GetActiveRooms()` — lists only the rooms whose creator's or loaded scenario's ARTCC is the caller's home or an operator-granted ARTCC, plus the rooms the caller's CID is a member of, or every room when VATSIM auth is off (`TrainingRoomManager.GetListedRoomInfos`, under the registry lock); each `TrainingRoomInfoDto` carries `ArtccId`, the room's label (scenario's ARTCC, else the creator's), next to `CreatorArtccId`; joining by id, invite or a mentor's pull is not filtered (the rule is shared with `GET /feed/rooms`, see [vatsim-auth.md](vatsim-auth.md)) |
| `FindRoomForMyCidAsync()` | `FindRoomForMyCid` | `FindRoomForMyCid()` — CID from token claims |
| `LoadScenarioAsync(json, …rates)` | `LoadScenario` | `LoadScenario(...)` → `LoadScenarioResult`; mentor/instructor-gated. Holds the room's load flag for the whole load (see **While a scenario loads**), sends the caller `ScenarioLoadProgress` events, and returns the final step table in `Steps` (see **Scenario load progress**) |
| `ExportRoomAsScenarioAsync()` | `ExportRoomAsScenario` | `ExportRoomAsScenario()` → `ScenarioExportResultDto(Json, Name, AircraftCount, Flags, DeniedReason)`; mentor/instructor-gated (throws), then `DeniedReason` "Not in a room" / "No scenario is loaded" / "The room has no aircraft to export" with `Json` null. `Flags` lists each aircraft needing review (callsign + reason); the scenario itself is built by `ScenarioExporter` in `Yaat.Sim` |
| `GetScenarioJsonByIdAsync(id)` | `GetScenarioJsonById` | `GetScenarioJsonById(id)` — gated by the scenario's ARTCC (caller's permitted set), then caller rating |
| `GetScenariosAsync()` | `GetScenarios` | `GetScenarios()` — filtered by caller rating |
| `UnloadScenarioAircraftAsync()` | `UnloadScenarioAircraft` | `UnloadScenarioAircraft()` `:1024` |
| `ConfirmUnloadScenarioAsync()` | `ConfirmUnloadScenario` | `ConfirmUnloadScenario()` `:1068` |
| `RestartScenarioAsync()` | `RestartScenario` | `RestartScenario()` — re-runs the loaded scenario from t=0 with a new RNG seed; **not** mentor-gated (see below) |
| `SendCommandAsync(callsign, command, initials)` | `SendCommand` | `SendCommand(...)` `:1124` |
| `AssumeLiveTrafficAsync(request)` | `AssumeLiveTraffic` | `AssumeLiveTraffic(AssumeLiveTrafficRequestDto)` → `AssumeLiveTrafficResultDto` — bulk `ASSUME` of live-traffic shadows (All / WithinRadius / Selected); one room gate around a per-callsign `RoomEngine.SendCommandAsync(..., "ASSUME", ...)` loop, so each hand-off is its own recorded command. The mode enum (`AssumeLiveTrafficMode`) lives in `Yaat.Server.LiveTraffic`, not `Yaat.Server.Dtos`: `CrcWireContractTests` requires every enum in `Dtos` to exist in the CRC contract |
| `SendChatAsync(initials, message)` | `SendChat` | `SendChat(...)` `:1952` |
| `AmendFlightPlanAsync(callsign, dto)` | `AmendFlightPlan` | `AmendFlightPlan(...)` `:1900` |
| `RequestNewBeaconCodeAsync(callsign)` | `RequestNewBeaconCode` | `RequestNewBeaconCode(callsign)` `:1922` |
| `RequestFlightStripForAircraftAsync(callsign)` | `RequestFlightStripForAircraft` | `RequestFlightStripForAircraft(callsign)` `:1942` |
| `SetAutoAcceptDelayAsync(seconds)` | `SetAutoAcceptDelay` | `SetAutoAcceptDelay(seconds)` `:1225` |
| `SetAutoDeleteModeAsync(mode)` | `SetAutoDeleteMode` | `SetAutoDeleteMode(mode)` `:1268` |
| `SetDepartureAutoDeleteDistanceAsync(distanceNm)` | `SetDepartureAutoDeleteDistance` | `SetDepartureAutoDeleteDistance(distanceNm)` — returns `CommandResultDto`; null turns it off, a value outside 1–500 nm is refused with the reason and no broadcast |
| `SetValidateDctFixesAsync(validate)` | `SetValidateDctFixes` | `SetValidateDctFixes(...)` `:1814` |
| `SetRpoShowPilotSpeechAsync(enabled)` | `SetRpoShowPilotSpeech` | `SetRpoShowPilotSpeech(...)` `:1836` |
| `SetSoloTrainingModeAsync(enabled)` → `SessionSetupPromptDto?` | `SetSoloTrainingMode` | `SetSoloTrainingMode(...)` → `SessionSetupPromptDto?` — switched on, the caller's reply carries the prompt's worked-positions section (its `ActiveRunways` section is always null) when the lowest position worked is not settled; null otherwise, for switching off, an unchanged setting and no room. Switching solo on also seeds the level (`StudentLowestWorkedSeeding`) |
| `SetStudentLowestWorkedLevelAsync(level)` | `SetStudentLowestWorkedLevel` | `SetStudentLowestWorkedLevel(level)` → `CommandResultDto` — `level` is a level code (`DEL`, `GND`, `TWR`, `APP`, `CTR`). Refused with the reason outside solo training, with no scenario, for an unknown code, for a student position with no type and for a level above the student's own. The answer is carried as the room's `ScenarioAnswers` entry for the scenario; a changed level is recorded as a `StudentLowestWorkedLevel` setting change and broadcast as `SessionSettingsChanged`, an unchanged one is carried without either |
| `SetSoloPacingRatesAsync(…)` | `SetSoloPacingRates` | `SetSoloPacingRates(...)` `:1880` |
| `SetAutoClearedToLandAsync(enabled)` | `SetAutoClearedToLand` | `SetAutoClearedToLand(...)` `:1319` |
| `SetAutoCrossRunwayModeAsync(mode)` | `SetAutoCrossRunwayMode` | `SetAutoCrossRunwayMode(...)` `:1345` (an undefined `AutoCrossRunwayMode` is a `HubException`) |
| `SetAutoPullUpToParallelAsync(enabled)` | `SetAutoPullUpToParallel` | `SetAutoPullUpToParallel(...)` |
| `SetAutoGoAroundOnOccupiedRunwayAsync(enabled)` | `SetAutoGoAroundOnOccupiedRunway` | `SetAutoGoAroundOnOccupiedRunway(...)` |
| `SetAutoArrivalSpacingOnOccupiedRunwayAsync(enabled)` | `SetAutoArrivalSpacingOnOccupiedRunway` | `SetAutoArrivalSpacingOnOccupiedRunway(...)` |
| `SetAutoRejectTakeoffOnOccupiedRunwayAsync(enabled)` | `SetAutoRejectTakeoffOnOccupiedRunway` | `SetAutoRejectTakeoffOnOccupiedRunway(...)` |
| `SetLiveTrafficEnabledAsync(enabled)` | `SetLiveTrafficEnabled` | `SetLiveTrafficEnabled(...)` — returns `CommandResultDto`; refused while `SimRate > 1` (the client reverts its checkbox) |
| `SetLiveTrafficCeilingFtAsync(ceilingFt)` | `SetLiveTrafficCeilingFt` | `SetLiveTrafficCeilingFt(...)` — 0 = automatic |
| `RewindToAsync(elapsedSeconds)` | **`RewindTo`** | `RewindTo(...)` `:2007` |
| `RewindFromSnapshotAsync(…)` | `RewindFromSnapshot` | `RewindFromSnapshot(...)` `:2031` |
| `TakeControlAsync()` | `TakeControl` | `TakeControl()` `:2092` |
| `GoLiveAsync()` | `GoLive` | `GoLive()` → `CommandResultDto`; live session only (`TakeControl` + `Resume`) — refused with "Not a live session" otherwise |
| `StartLiveSessionAsync(LiveSessionRequestDto)` | `StartLiveSession` | `StartLiveSession(...)` → `LoadScenarioResult` (same shape, load flag, progress events and broadcast fan-out as `LoadScenario`, its step table ending in a `live` step; a refusal is `Success=false` with the reason as the first warning); mentor/instructor-gated. `LoadScenarioResult` / `ScenarioLoadedDto` / `RoomStateDto` carry `IsLiveSession` (a property of the loaded scenario, not a session setting) |
| `SeekLiveTrafficAsync(feedUtc)` | `SeekLiveTraffic` | `SeekLiveTraffic(...)` → `CommandResultDto`; live session only — stands the room at a feed instant inside the raw-log window (DVR), dropping every non-shadow aircraft; refused with the reason outside the window or past the two-replay cap. `LiveSessionRequestDto.StartUtc` does the same at session start |
| `GetLiveTrafficWindowAsync()` | `GetLiveTrafficWindow` | `GetLiveTrafficWindow()` → `LiveTrafficWindowDto` (retained span, active/max replays, or why there is none). `LiveTrafficStatusDto` also carries `FeedTimeUtc`, `BehindSeconds` (null while live), `Preparing` |
| `GetArtccFacilityTreeAsync(artccId)` | `GetArtccFacilityTree` | `GetArtccFacilityTree(...)` → `FacilityTreeDto?` (facility tree with `PositionSummaryDto`s; `AirportId` when the facility id is an airport, `PrimaryAirportId` from its first STARS area, `Airports` from its STARS configuration) |
| `GetTimelineInfoAsync()` | `GetTimelineInfo` | `GetTimelineInfo()` |
| `GetTerminalLogAsync()` | `GetTerminalLog` | `GetTerminalLog()` → `List<TerminalBroadcastDto>` (recorded terminal stream; client repopulates the terminal after a recording load) |
| `ExportRecordingAsync(clientVersion, clientBuildKind, anonymize)` | `ExportRecording` | `ExportRecording(clientVersion, clientBuildKind, anonymize)` (stream). `anonymize` is required: true for a bug report bundle, which replaces the people in the action and terminal logs with the room's pseudonyms (`RecordingPseudonymizer`, from the list `GetSessionServerLog` returns); false for Save Recording, which keeps the real initials |
| `LoadRecordingAsync(bytes)` | `LoadRecording` | `LoadRecording(stream)` → `RewindResultDto`. Drains the upload, then holds the room's load flag like a scenario load: fetches the recording scenario's ARTCC configs and layouts before taking the tick gate, sends no progress events, and replaces the room's resource pin. The room takes the tape's session settings (its t≤0 setting changes applied at load, before any preset dispatches), carried in `RewindResultDto.SessionSettings` (null on a rewind's result), and `SessionSettingsChanged` goes to the room group just before `RecordingLoaded`. A recording past 16 distinct wind grids or 512 KB decompressed per grid is refused before the room is cleared, as a failure `RewindResultDto` carrying "Recording refused: it carries {n} wind grids…" or "Recording refused: wind grid {first 16 hash chars}… is larger than 524,288 bytes.", and the room keeps its scenario |
| `AddBookmarkAsync(timeSeconds, name, initials)` | `AddBookmark` | `AddBookmark(...)` — adds a shared timeline bookmark, broadcasts `BookmarksChanged` |
| `RenameBookmarkAsync(id, name)` | `RenameBookmark` | `RenameBookmark(id, name)` — any RPO may rename any bookmark |
| `DeleteBookmarkAsync(id)` | `DeleteBookmark` | `DeleteBookmark(id)` — any RPO may delete any bookmark |
| `GetSessionServerLogAsync()` | `GetSessionServerLog` | `GetSessionServerLog()` → `SessionServerLogDto(Text, Participants)`: the room-scoped server log, anonymized, and the `LogParticipant` list it was anonymized with, so the client scrubs its own logs to the same pseudonyms. The list is the room's identity ledger in first-seen order (a person's pseudonym is their index), so it holds people who have left; each `LogParticipant` carries `Cid`, `RealName`, `Initials` and, for a person seen under more than one, `OtherInitials` and `OtherRealNames` (camelCase on the wire), all of one pseudonym. The list carries raw CIDs and names and goes only to a caller in that room; not in a room → empty text and list |
| `ExportSurfaceTempDataAsync()` | `ExportSurfaceTempData` | `ExportSurfaceTempData()` — mentor/instructor only; the room's ASDE-X / SAAB SAID drawn geometry as committable ARTCC sidecar JSON, one entry per facility |
| `ResetSurfaceTempDataAsync()` | `ResetSurfaceTempData` | `ResetSurfaceTempData()` — mentor/instructor only; discards drawn geometry (server-side too) and re-seeds every facility from the committed sidecars, returning the facility count |
| `GetAirportGroundLayoutAsync(id)` | `GetAirportGroundLayout` | `GetAirportGroundLayout(id)` |
| `GetFacilityVideoMapsAsync(…)` | `GetFacilityVideoMaps` | `GetFacilityVideoMaps(...)` `:2545` |
| `GetFacilityVideoMapsForArtccAsync(…)` | `GetFacilityVideoMapsForArtcc` | `GetFacilityVideoMapsForArtcc(...)` `:2547` |
| `GetApproachReportAsync()` | `GetApproachReport` | `GetApproachReport()` `:1689` |
| `GetSessionReportAsync()` | `GetSessionReport` | `GetSessionReport()` `:1702` |
| `LoadWeatherAsync(json, reconstructMetars)` | `LoadWeather` | `LoadWeather(json, reconstructMetars)` `:1531`; weather naming a grid the room does not pin yet is refused once the room holds `TrainingRoom.MaxRoomWindGrids` (16) grids, or after it closed, as `CommandResultDto(false, msg)` with the same text as `UploadWindGrid`'s refusal and no `HubException`; a success also rebuilds the room's wind grid pins and consumes a pending upload when the weather names a grid |
| `ClearWeatherAsync()` | `ClearWeather` | `ClearWeather()` `:1566` |
| `UploadWindGridAsync(grib2)` | `UploadWindGrid` | `UploadWindGrid(byte[]? grib2)` `:1594` → `string`, the grid's SHA-256 hash, which the weather JSON names as `windGridHash`; called by `MainViewModel.Weather.cs` before `LoadWeather`, with the grid `GfsGridFetcher` fetched for Load Live Weather or the one a saved weather file embeds (a `HubException` becomes the GFS terminal warning and the weather loads on the FD layers); any room member, inside a room only. The server decodes and hashes the bytes itself, registers the grid and pins it to the room as its pending upload (a later upload replaces it; a weather load naming any grid consumes it). Refusals are a `HubException`: "Not in a room", "No wind grid bytes were sent.", "The wind grid is {n:N0} bytes; the server accepts at most 524,288 bytes." (`WindGridRegistry.MaxGridBytes`, 512 KB), "This room already holds {n} wind grids; load weather without a new grid or start a new session." (`TrainingRoom.MaxRoomWindGrids`, = `WindGridRegistry.MaxRoomGrids`, 16; `TrainingRoom.WindGridRefusal`, checked before the decode and again when `SetPendingWindGrid` keeps the grid), "The wind grid could not be decoded: {reason}", "The room closed before the wind grid could be kept for it." (`TrainingRoom.ClosedRoomWindGridRefusal`) |
| `GetWindGridAsync(hash)` | `GetWindGrid` | `GetWindGrid(string hash)` `:1650` → `byte[]`, a copy of the grid's raw GRIB2 bytes; called by `MainViewModel.Weather.cs` Save Weather (`WeatherSaveGrid.PrepareAsync`) when the weather names a grid this client holds no bytes for (a member who did not load the weather, or the loader after a restart); any room member, inside a room only, and only a hash in `TrainingRoom.PinnedWindGridHashes`. Refusals are a `HubException`: "Not in a room", and "This room does not hold that wind grid." for a hash that is not 64 lowercase hex characters (or is null), is not pinned by the room, or names a grid the registry no longer holds (the last also logs a warning) |
| `LoadArrivalGeneratorsAsync(json)` | `LoadArrivalGenerators` | `LoadArrivalGenerators(json)` `:1671` |
| `AssignAircraftAsync(callsigns, conn)` | `AssignAircraft` | `AssignAircraft(...)` `:1092` |
| `UnassignAircraftAsync(callsigns)` | `UnassignAircraft` | `UnassignAircraft(...)` `:1104` |
| `GetAircraftAssignmentsAsync()` | `GetAircraftAssignments` | `GetAircraftAssignments()` `:1116` |
| `GetAccessibleFacilitiesAsync()` | `GetAccessibleFacilities` | `GetAccessibleFacilities()` `:3099` |
| `GetFlightStripsConfigForFacilityAsync(id)` | `GetFlightStripsConfigForFacility` | `GetFlightStripsConfigForFacility(id)` `:3123` |
| `GetAccessibleTdlsFacilitiesAsync()` | `GetAccessibleTdlsFacilities` | `GetAccessibleTdlsFacilities()` |
| `GetTdlsFacilityViewAsync(id)` | `GetTdlsFacilityView` | `GetTdlsFacilityView(id)` — one `TdlsConfigDto` per member facility; a consolidated parent returns its children's |
| `RequestFullTdlsStateAsync()` | `RequestFullTdlsState` | `RequestFullTdlsState()` |
| `GetOnlineControllersAsync()` | `GetOnlineControllers` | `GetOnlineControllers()` → `List<OnlineControllerDto>` for the caller's room (empty outside one): the live CRC clients plus the scenario's auto-connect positions (`OnlineControllerService.GetForRoom`). `OnlineControllerDto(Callsign, Name, Frequency, FacilityId, FacilityName, PositionType, Tcp, RadioName, RealName, IsActive, IsScenarioPosition, HandoffCode)`: `Frequency` is preformatted MHz (`121.800`), `Tcp` is `{Subset}{SectorId}` or null, `IsActive` is staffed against standby. `HandoffCode` is the shortest code that names exactly that position to `HO` (`RoomEngine.SelectionCodeFor`: the TCP or ERAM code, else the callsign, else `callsign@code`), null when no scenario is loaded or the position does not resolve to a track owner; the Initiate handoff… flyout sends it. The Controllers tab and the coordination flyouts (`MainViewModel.Controllers.cs`) read this one query |
| `AdminAuthenticateAsync(password)` | `AdminAuthenticate` | `AdminAuthenticate(password)` `:2556` |
| `AdminGetRoomsAsync()` | **`AdminGetScenarios`** | `AdminGetScenarios()` `:2577` |
| `AdminSetRoomFilterAsync(roomId)` | **`AdminSetScenarioFilter`** | `AdminSetScenarioFilter(roomId)` `:2587` |
| `GetCrcLobbyClientsAsync()` | `GetCrcLobbyClients` | `GetCrcLobbyClients()` `:2618` |
| `PullCrcClientAsync(id)` | `PullCrcClient` | `PullCrcClient(id)` `:2640` (clears a kick of the client's CID once bound; false when a kick unbinds it again during the pull) |
| `KickCrcClientAsync(id)` | `KickCrcClient` | `KickCrcClient(id)` `:2726` (mentor/instructor only; a client with a CID is kicked as `KickMember` kicks its CID, one with no CID is only unbound) |
| `KickMemberAsync(cid)` | `KickMember` | `KickMember(cid)` `:2790` (kicks the whole CID: every training-hub, CRC and Tower Cab 3D connection of it leaves the room and the CID is banned from it until a `PullRpo`/`PullCrcClient` clears the ban; refuses the caller's own CID and the creator; returns false for a CID with nothing in the room; serialized with pulls per room) |
| `GetCrcRoomMembersAsync()` | `GetCrcRoomMembers` | `GetCrcRoomMembers()` `:2906` |
| `GetRpoLobbyClientsAsync()` | `GetRpoLobbyClients` | `GetRpoLobbyClients()` (mentor-only) |
| `PullRpoAsync(connectionId)` | `PullRpo` | `PullRpo(connectionId)` (mentor-only; invites/re-admits + auto-joins; clears the kick block) |
| heartbeat loop (`RunHeartbeat`) | `Heartbeat` | `Heartbeat()` `:2552` (no-op) |

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

### Mentor/instructor gating throws, it doesn't return a failure DTO

Eight methods call `RequireMentorOrInstructor()` (`TrainingHub.cs:97`) — `CreateRoom`, `LoadScenario`,
`UnloadScenarioAircraft`, `ConfirmUnloadScenario`, `KickMember`, `GetRpoLobbyClients`, `PullRpo`, `ExportRoomAsScenario`. Unlike the
"Not in a room" sentinel above, the guard **throws a `HubException`** ("This action requires a mentor or
instructor.") *before* any room lookup, so the client sees a thrown exception rather than a `Success == false`
DTO. `HubException` is the one exception type SignalR relays verbatim regardless of `EnableDetailedErrors`.
`CreateRoom` throws the same way when the requested `artccId` isn't in the caller's permitted set (token home ARTCC
plus operator grants — see [vatsim-auth.md](vatsim-auth.md) § ARTCC gate); the client's `CreateRoomAsync` raises a
terminal warning for it.

Callers must therefore catch, and — this is the bug that reached users — must make the rejection *visible*.
Setting only `StatusText` puts it in small gray text at the bottom of the window, which reads as "nothing
happened"; the client now also raises a terminal warning (`MainViewModel.ReportScenarioActionFailure`) and
disables the controls up front via `CanLoadScenario`/`CanUnloadScenario`.

`RestartScenario` is deliberately **not** in that list: it re-runs the same scenario, so unlike unload it can
neither strand the room nor switch scenarios, and a non-mentor working alone can retry without an instructor.
Note the gate is a **rating tier**, not the RPO position — a mentor working an RPO position is unaffected (the
client flag is `IsNonMentor`; see [`vatsim-auth.md`](vatsim-auth.md) for the lobby-and-pull flow).

### While a scenario loads

`LoadScenario`, `StartLiveSession` and `LoadRecording` each hold the room's **load flag** (`TrainingRoom.TryBeginLoad`) from before their fetches until after their `ScenarioLoaded` / `RecordingLoaded` broadcast, and announce it with `RoomLoadingChanged`. While it is held:

- A second `LoadScenario` or `StartLiveSession` in the room returns `Success = false` at once with first warning `A scenario is already loading in this room (started by {initials}). Wait for it to finish.`, no progress events and an empty `Steps`.
- `UnloadScenarioAircraft`, `ConfirmUnloadScenario` and `CloseRoom` throw `HubException` `The room is loading a scenario. Try again when it has loaded.`; `RestartScenario` (`CommandResultDto`), `RewindTo`, `RewindFromSnapshot` and `LoadRecording` (`RewindResultDto`) return their failure DTO with the same text. Each checks before it waits on the tick gate and again inside it, since a call can wait on the gate while a load commits.
- `SendCommand` still reaches the scenario the room is running; the load's commit discards that scenario, commands and all.

A load that fails returns `Success = false` with the failed step's problem as its first warning (the texts are in **Scenario load progress** below: an unreadable JSON leaves the room running what it had, a failure after the unload leaves it empty), and sends no `ScenarioLoaded`. `RoomStateDto.LoadingBy` carries the initials of whoever holds the flag (null otherwise), so a joiner sees a load is running.

Once the scenario is read, the room group gets the terminal line `{initials} is loading '{name}'…`, sent directly and not recorded in the outgoing scenario's terminal log. A recording load that fails after it cleared the room leaves the room empty and sends the group `ScenarioUnloaded`.

## Server → client broadcast catalog

Every `On<T>` handler is registered in `ServerConnection.ConnectAsync` (`ServerConnection.cs:110`-`172`). Event string →
payload DTO → the `ServerConnection` C# event it re-raises:

| Invoke string | Payload | `ServerConnection` event |
|---|---|---|
| `AircraftUpdated` | `AircraftDto` | `AircraftUpdated` (delta **and** delayed-countdown channel — see below) |
| `AircraftSpawned` | `AircraftDto` | `AircraftSpawned` |
| `AircraftDeleted` | `string` callsign | `AircraftDeleted` |
| `SimulationStateChanged` | `(bool, int, double, bool, double)` | `SimulationStateChanged` |
| `TerminalBroadcast` | `TerminalBroadcastDto` (`PilotKind`: see below) | `TerminalEntryReceived` |
| `PilotTransmissionBroadcast` | `PilotTransmissionBroadcastDto` (its trailing `SpeechRateFactor` is the load factor from `PilotSpeechRate.LoadFactor`: 1.1 when the frequency is busy, else 1.0) | `PilotTransmissionReceived` — the client multiplies its Speech Speed setting by `SpeechRateFactor` (`PilotSpeechRateNudge.Apply`) |
| `RoomMemberChanged` | `RoomMemberChangedDto` (carries `RoomMemberDto` — see **Room membership** below) | `RoomMemberChanged` — **not** the client's only source of members; `ApplyRoomState` seeds from `RoomStateDto.Members` because this push can beat the join response |
| `CrcLobbyChanged` | `CrcLobbyChangedDto` | `CrcLobbyChanged` |
| `RpoLobbyChanged` | `RpoLobbyChangedDto` | `RpoLobbyChanged` (waiting non-mentor RPOs + kicked users pending re-admit) |
| `CrcRoomMembersChanged` | `CrcRoomMembersChangedDto` | `CrcRoomMembersChanged` |
| `WeatherChanged` | `WeatherChangedDto` (`Name`, `WindLayers`, `Precipitation`, `Metars`, `SourceJson`, then `WindGridCycleUtc` and `WindGridForecastHours`: the GFS run and forecast hour of the grid the weather flies on, read from the registered `GriddedWindField`, both null when it names none) | `WeatherChanged` — a load's own broadcast carries that load's weather JSON in `SourceJson` (`RoomEngine` sets `WeatherSourceJson` before broadcasting); the client keys the METAR header's winds-aloft line on it |
| `ArrivalGeneratorsChanged` | `ArrivalGeneratorsChangedDto` | `ArrivalGeneratorsChanged` |
| `HeldDeparturesChanged` | `HeldDeparturesChangedDto` (carries `RundownDto`) | `HeldDeparturesChanged` |
| `ActiveRunwaysChanged` | `ActiveRunwaysChangedDto(ByAirport)` — airport → the ends' tokens (`30`, `D28L`, `A28R`) | `ActiveRunwaysChanged` — sent when a live `ARWY` sets or clears an airport's list, and whenever the list differs from the last one sent: each tick beside the timers (a played-back `ARWY`), and at the landing of a scenario load, restart, rewind or recording load. Seeded on join via `RoomStateDto.ActiveRunways` |
| `TimersChanged` | `TimersChangedDto` | `TimersChanged` |
| `BookmarksChanged` | `BookmarksChangedDto` (full shared-bookmark list) | `BookmarksChanged` — also seeded on join via `RoomStateDto.Bookmarks` |
| `LiveTrafficStatusChanged` | `LiveTrafficStatusDto` (feed configured / connected, last-message age, tracks in the room scope) | `LiveTrafficStatusChanged` — also seeded on join via `RoomStateDto.LiveTrafficStatus`; immediate on feed/count change, throttled to 5 s for age churn (`ShadowTrafficSync.BroadcastStatusIfChanged`) |
| `ConflictAlertsChanged` | `ConflictAlertsChangedDto` (full active terminal-CA pair list) | `ConflictAlertsChanged` — also seeded on join via `RoomStateDto.ConflictAlerts`. Signature-guarded in `TickProcessor` so it fires only when a pair opens or clears, not every sim-second. Terminal CA only; ERAM STCA is not routed here. Carries no separation values — the client derives them per frame from live positions |
| `AtpaResultsChanged` | `AtpaResultsChangedDto` (full list of `AtpaPairDto(Callsign, LeadCallsign, AllowedSeparationNm, ConeState)`) | `AtpaResultsChanged` — also seeded on join via `RoomStateDto.AtpaResults`. Computed once per room per wall-tick by `AtpaEvaluator` in `RoomTickLoopService.DetectChanges` (cached on `TrainingRoom.AtpaResults`, shared with the CRC pass) and signature-guarded on follower→lead, allowed separation and cone state — the **actual** separation is deliberately not on the wire: the client recomputes it per frame from the two positions, as for CA. `ConeState` is the shared `Yaat.Sim.Data.Vnas.AtpaConeState` enum (same precedent as `StarsDatablockColor`). Every pair is sent regardless of the volume's TCP adaptation; the client shows them all |
| `PositionDisplayChanged` | `PositionDisplayConfigDto(MapGroupMapIds, MapGroupTcpCodes, UnderlyingAirports, TcpCode, ActivePosition, ScratchpadMaxLength)`; `ScratchpadMaxLength` is the most characters the student facility's STARS scratchpads hold (`ScratchpadRuleEngine.MaxScratchpadLength`: 4 where it allows four-character scratchpads, else 3), 0 from a room restored from an older checkpoint; `ActivePosition` is a nullable `ActivePositionDto(Callsign, Name, RadioName, Frequency, FacilityId, FacilityName, PositionType, HandoffCode)`: `Frequency` is preformatted MHz (`135.100`), `PositionType` is `Artcc`, `Tracon`, `Atct` or `Other`, `HandoffCode` is the code that names exactly that position to `HO` (`RoomEngine.SelectionCodeFor`) | `PositionDisplayChanged` — sent to the caller after a standalone `AS`, not while replaying, with `ActivePosition` built by `ArtccConfigService.BuildActivePositionDto` for the owner the `AS` selected (a TCP shared by several positions names that one). The same config rides `LoadScenarioResult`, `ScenarioLoadedDto` and `RoomStateDto` as `PositionDisplayConfig`, built for the scenario's student position. `ActivePosition` is null when no position of the owner's callsign resolves in the ARTCC config, and for a room restored from an older checkpoint until the next scenario load; a student with no STARS TCP gets no config at all. Client: `MainViewModel.ActivePosition`, read by the coordination flyouts, and `MainViewModel.ScratchpadMaxLength`, which the Scratchpad box checks at through `IMenuHost.ScratchpadMaxLength` (0 or less reads as 3) |
| `ScenarioLoaded` | `ScenarioLoadedDto` (its trailing `ArtccId` is the room's label after the load) | `ScenarioLoaded` (+ `StripsConfigChanged`) |
| `ScenarioLoadProgress` | `ScenarioLoadProgressDto(LoadId, Sequence, ScenarioName, IsComplete, Steps)`, each step a `LoadStepDto(Id, Label, State, Detail, Problems)` | Sent to the **caller** of `LoadScenario` / `StartLiveSession` only, with the whole step table at every change (`Sequence` strictly increasing; drop a stale one). The last event has `IsComplete = true` and equals `LoadScenarioResult.Steps`, also after a failure. Step ids, labels, states and texts: **Scenario load progress** below. Client: `MainViewModel.OnScenarioLoadProgress` → `LoadOverlayViewModel` |
| `RoomLoadingChanged` | `string?` — the initials of the member whose load holds the room's load flag; null once it is released | `RoomLoadingChanged` — sent to the **room group** when a `LoadScenario`, `StartLiveSession` or `LoadRecording` takes the load flag, and again with null when the load ends on any path (success, unreadable JSON, exception, room closed). Fire-and-forget; a failed send is logged. Also seeded on join via `RoomStateDto.LoadingBy`. While it is non-null the client disables Load, Start Live Session, Unload, Restart and every rewind and shows `Loading a scenario (by {initials})…` |
| `ScenarioUnloaded` | `ScenarioUnloadedDto(ArtccId)` — the room's label after the unload (the creator's) | `ScenarioUnloaded` (+ `StripsConfigChanged(null)`) |
| `ScenarioRestarted` | `ScenarioRestartedDto(Aircraft, ActiveRunways)` — the room's post-restart manifest (live aircraft + re-queued delayed spawns) and the active runways the restart kept | `ScenarioRestarted` — handlers **replace** their aircraft list, never merge, and replace their active runways. The restart tears the world down under `IsBroadcastSuppressed`, so no `AircraftDeleted` is sent for the abandoned run and `AircraftUpdated` only adds or updates; without this payload every aircraft that did not survive stays on the client frozen in place. Group-scoped so every room member re-syncs, not just whoever hit Restart. CRC's equivalent is `RoomEngine.ResyncCrcAfterReload` |
| `ScenarioRewound` | `List<AircraftDto>` — the post-rewind manifest | `ScenarioRewound` — same additive-stream repair as `ScenarioRestarted`, for `RewindTo` / `RewindFromSnapshot`. Sent **`GroupExcept` the caller**: that client applies the identical manifest from `RewindResultDto.Aircraft` instead, so including it would rebuild its list twice. Unlike a restart the client must **keep** its bookmarks — they are timeline-global and `RewindAsync` carries them across the reload |
| `RecordingLoaded` | `RewindResultDto` — the full load result, not just a manifest | `RecordingLoaded` — the third world-replacing reload, and the widest: a recording swaps the scenario *and* enters playback, so scenario identity and tape bounds are needed alongside the aircraft. Clients feed it to the same `ApplyRecordingResult` the loader uses (which applies `SessionSettings`), then fetch `GetTerminalLog` for the recording's tape. `OthersInGroup` — the loader already applied this DTO as its RPC result. Bookmarks arrive separately via `BookmarksChanged`, which `RoomEngine.LoadRecording*Async` fires after seeding them from the archive |
| `AircraftAssignmentsChanged` | `AircraftAssignmentsDto` | `AircraftAssignmentsChanged` |
| `SessionSettingsChanged` | `SessionSettingsDto` | `SessionSettingsChanged` — also sent once at the end of every rewind, seek and hybrid replay, and once per playback second in which a tape setting change altered a setting the DTO carries (`SoloRpoCommandsAllowed` and `LiveTrafficFeedTimeUtc` send nothing). Skipped while broadcasts are suppressed; a failed send logs `SessionSettingsChanged could not be sent to room {RoomId}` |
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

- **`SimulationStateChanged` is sent every advancing tick**, not only on discrete state changes. The hosted tick
  loop broadcasts it once per processed wall-second per active (non-paused) room so the client's elapsed clock —
  the timeline label, scrubber position, and the base for the relative +15/−15 skips — stays live. It is also sent
  on the discrete events (pause/unpause, sim-rate change, rewind-complete, end-of-tape). The `double elapsed`
  argument carries `scenario.ElapsedSeconds`.
- **`TerminalBroadcastDto.PilotKind`** (a string, the last field; JSON `pilotKind`) tells a pilot's own call from a readback. On a solo pilot transmission's line (`TickProcessor.BroadcastPilotTransmissions`, through `ITrainingBroadcast.BroadcastPilotTerminalEntry`) it is the `PilotTransmissionKind` name: `"Readback"`, `"Proactive"` (a request), `"Report"` or `"SayReadback"`. A line from `TickProcessor.BroadcastPilotReadbacks` carries `"SayReadback"`.

  It is null on every other line, including the RPO `PilotSpeech` line. The terminal `Kind` stays `SayPilot` / `SayReadback` as before, and the client still collapses both to `TerminalEntryKind.Say`; it keeps the value on `TerminalEntry.PilotKind`.

  It is live only: `RecordedTerminalEntry` does not store it, so `GetTerminalLog` and a recording's terminal history return it null.

## Scenario load progress

`LoadScenario` and `StartLiveSession` report their steps to the caller alone. The hub captures `Clients.Caller` before its first await and hands `ScenarioLoadReporter` (yaat-server `Simulation/ScenarioLoadReporter.cs`) a sink that sends `ScenarioLoadProgress` fire-and-forget: a failed send (the loader disconnected) is logged and never fails the load. Every event carries the **whole** step table, so a lost or reordered event cannot desync the client:

- `ScenarioLoadProgressDto(LoadId, Sequence, ScenarioName, IsComplete, Steps)`: `LoadId` is a new `Guid` ("N" format) per load; `Sequence` goes up by one at every change of the table, item counts included; `ScenarioName` is empty until the `read` step finishes.
- `LoadStepDto(Id, Label, State, Detail, Problems)`: `State` is one of `pending`, `running`, `done`, `warning` (finished, and something it needed is missing: the load went on without it, `Problems` says what), `failed` (the load stopped here) or `notNeeded` (nothing to do).
- A finished step is never reopened. Once a step fails or the load ends, the table is complete: one last event with `IsComplete = true` goes out and nothing follows it. `LoadScenarioResult.Steps` is that same table, so the RPC result alone is enough to render the finished overlay. An unexpected exception fails the step the load had reached (the running one, else the next pending one) with `The load failed: {message}.`, then completes the table.
- A load refused before it started (not in a room, the load flag held elsewhere) sends no events and returns an empty `Steps`. A recording load, and the paths no client watches (`RoomEngine.LoadScenarioAsync` / `LoadScenarioSeededAsync` / `StartLiveSessionAsync`, restore, rewind), report to `ScenarioLoadReporter.None()`, which sends nothing; the unwatched scenario loads still return their table in `Steps`.

  On the server `LoadScenarioResult.Steps` and `RoomStateDto.LoadingBy` are positional members; the client declares them as `init` properties (`LoadScenarioResultDto.Steps` defaults to empty, `RoomStateDto.LoadingBy` to null).

  `LoadScenarioResult` carries the room's active runways as loaded (`ActiveRunways`) and the questions the loader's client is asked at load as `SessionSetup` (`SessionSetupPromptDto?`, a required member on the server and the client; null when nothing is asked, and on a failed load). The prompt has two sections, each null when it does not apply or is already settled.

  `SessionSetupPromptDto.ActiveRunways` (`ActiveRunwaysSetupDto(Prefill, Assigned)`) is non-null only in an RPO room whose scenario has no sidecar naming runways and no carried answer: `Prefill` is the implied guess that pre-fills the mentor's prompt, by airport.

  `Assigned` (airport → `RunwayUseCountsDto(Departures, Arrivals, GeneratorArrivalRunways)`, from `ScenarioRunwayUse.CountAssigned`) says what the scenario already decides about runways: how many of its loaded aircraft (immediate and delayed) already have a runway, once each at that runway's airport, as departures when they start on the ground and arrivals when airborne. An aircraft's runway is its spawn runway, else its expected approach, else its first runway-bearing preset command, read without dispatching. At the primary airport it also lists the runway designators the scenario's arrival generators feed.

  An airport with neither is absent. `RoomStateDto.ActiveRunways` is a required member on the server and on the client.

  `SessionSetupPromptDto.WorkedPositions` (`WorkedPositionsSetupDto(Own, Prefill, Choices)`, every value a level code) is non-null only in a solo room whose student position has a type above delivery and whose lowest position worked is not settled (no carried answer and no valid sidecar `studentLowestPosition`).

  `Own` is the student's own level, `Prefill` the scenario's current lowest level else `Own`, `Choices` the levels from `Own` down to `DEL`, highest first. `SetSoloTrainingMode` returns the same prompt shape, with only this section, when solo training is switched on mid-session.

  `ToweredAirports` (airport ids as the ARTCC configs spell them, ordinal-sorted) is the room's towered set: the airports with a tower facility (`Atct`, `AtctTracon`, `AtctRapcon`) in the scenario's own, its roster's or a geographic neighbour's ARTCC config; any other airport is untowered. It is a required, non-null member of `LoadScenarioResult`, `RoomStateDto`, `ScenarioLoadedDto` and `RewindResultDto` (a rewind's or recording load's result and the `RecordingLoaded` broadcast) on the server and the client, empty on a failed load or with no scenario; it lands in `MainViewModel.RoomToweredAirports` (cleared when the room is left). Airport ids match in either spelling (`OAK`/`KOAK`) and, outside the CONUS, through the navigation data's FAA/ICAO index (`ANC`/`PANC`).

  Every other payload that replaces a client's scenario carries the room's list too, as a required member on both sides: `ScenarioLoadedDto.ActiveRunways` (the other members' copy of a load), `RewindResultDto.ActiveRunways` (a rewind's or recording load's result and the `RecordingLoaded` broadcast; empty on a failure) and `ScenarioRestartedDto.ActiveRunways`. A client replaces its copy with each.

  A restart keeps the list in use when it was asked for, whatever set it, and never prompts; only the loader's `LoadScenarioResult` can ask.

The steps, in display order, with their text verbatim (`{…}` is filled in; `ScenarioLoadSteps` holds every string):

| Id | Label | `Detail` while running | `Detail` when finished |
|---|---|---|---|
| `read` | Read scenario | | `{scenario name}` |
| `artcc` | ARTCC configuration | `{done} of {total}: {ids still running}` | `{ids, comma-separated}`; `notNeeded` when the scenario names no ARTCC |
| `layouts` | Airport layouts | `{done} of {total}: {ids still running}` | `{airports with a layout} of {total} airports`, then `; no map: {ids}` for mapless airports that raise no warning; `notNeeded` when the scenario names no airport |
| `aircraft` | Build aircraft | | `{n} aircraft: {immediate} now, {delayed} delayed, {deferred} not placed`; a live session's is `notNeeded` with `A live session has no scripted aircraft` |
| `populate` | Set up the room | `Waiting for the room`, then `Replacing the current scenario` | the student position's callsign, or none |
| `weather` | Re-apply weather | | `{weather name}`; `notNeeded` with `No weather loaded` |
| `live` (live session only) | Start live traffic | | |

`Problems` texts:

- `read`, failed: `The scenario JSON could not be read: {parser message}. The room keeps its current scenario.` Nothing is fetched and the room is untouched.
- `artcc`, warning (a live session's step **fails** instead when the creator's ARTCC config is missing): `{ID}: not found on vNAS (HTTP 404). Positions in {ID} will not resolve.` · `{ID}: vNAS unreachable and nothing cached. Positions in {ID} will not resolve.` · `{ID}: vNAS unreachable; using the copy cached {yyyy-MM-dd HH:mm}Z.`

  The step can also read `{ID}: no longer on vNAS (HTTP 404); using the copy cached {yyyy-MM-dd HH:mm}Z.` · `{ID}: the configuration could not be read ({cause}). Positions in {ID} will not resolve.` · `{ID}: no ERAM letter for neighbouring center {NBR}; a handoff to {NBR} answers SECTOR NOT ADAPTED.` A stale copy with no timestamp reads `using the copy cached at an unknown time.`
- `layouts`, warning: at any airport, `{APT}: vNAS unreachable; using the copy cached {yyyy-MM-dd HH:mm}Z.` · `{APT}: no longer on vNAS (HTTP 404); using the copy cached {yyyy-MM-dd HH:mm}Z.` · `{APT}: the ground map could not be read ({cause}).`

  At a map-required airport only (`ScenarioResourceManifest.MapRequiredAirportIds`: the primary airport and each `Parking` or ground-spawn airport), `{APT}: no ground map on vNAS. Aircraft at {APT} cannot taxi or park.` · `{APT}: vNAS unreachable and nothing cached.` Any other airport with no map joins the `no map:` detail and keeps the step green.
- `aircraft`, warning: each of the loader's `ScenarioLoadResult.Warnings` verbatim (for example `N123AB: No ground layout for SQL`).
- `populate`, warning: `ATC position {id} not found in {artcc}; it won't appear in the controllers list` · `Student position {id} not found in {artcc}; strips, beacon banks and the STARS display settings are not set up.` · `Arrival generator {id} skipped: {reason}`, the reason one of `no primaryAirportId`, `no runway specified`, `runway {rwy} not found at {apt}`. These and the `aircraft` warnings are also the result's `Warnings` and reach the terminal as `Warning` lines.
- `populate`, failed: `The room could not be set up: {message}. The previous scenario was already unloaded; load again.` · `The room was closed while the scenario loaded.` (the room was retired or force-closed during the fetches; it is not repopulated).
- `weather`, warning: `The room's weather could not be re-applied: {message}` (`the weather load gave no reason` when there is no message).
- `live`, failed: `Live traffic is not enabled on this server` · `Position {id} not found in {artcc}` · `Unknown airport {id}` · the enable, filter and seek failures (`Live traffic could not be enabled`, `Bad live-traffic filter`, `The feed cannot start there` when the call gives no message).

Each finished step logs `Scenario load {LoadId} step {Id}: {State} in {ms} ms` on the server. The client's overlay closes itself when the table is complete and every step is `done` or `notNeeded`; a `warning` or `failed` step keeps it open until the user closes it ([client-mainviewmodel.md](client-mainviewmodel.md)).

## Client-version gate (off-hub, before connecting)

`GET /api/client-requirements` → `ClientRequirementsDto(MinimumClientVersion, RecommendedClientVersion)`.
Served by `ClientRequirementsHandler` from `Yaat:ClientVersions` in yaat-server's `appsettings.json`, and
registered ahead of the `/api/{**path}` catch-all so it isn't swallowed by the CRC stub.

It is **unauthenticated and deliberately not a hub method**. The clients this exists to warn are the ones a
hub payload would break, so the answer must be reachable before the SignalR handshake and before VATSIM
sign-in. `ClientRequirementsDto` is **append-only on both sides** for the same reason: a client that cannot
deserialize this response has no way to learn why it was refused.

`ClientVersionGate.EvaluateAsync` (client, `Yaat.Client.Core`) runs it at the top of `AttemptConnectAsync`:

- below `MinimumClientVersion` → connection refused with an explicit "update, then reconnect" message
- below `RecommendedClientVersion` → connects, sets `MainViewModel.ServerUpdateNotice` (dismissible banner)
- otherwise → no-op

Comparison is `Yaat.Sim.ClientVersions`: numeric `major.minor.patch` only, prerelease/metadata suffix
ignored. Every failure path **fails open** — unreachable server, malformed body, unparseable version, or a
server predating the endpoint (whose catch-all returns `[]`) all yield "no objection". A false block leaves
a user with no way forward. The gate is one-way: it refuses clients that are too old, never a server older than the client. An older server accepts the connection, and only a method whose parameter list changed fails, with the logged Warning described in [Changing a hub method's parameters](#changing-a-hub-methods-parameters).

## Changing a hub method's parameters

A hub method's arguments are **positional**, and SignalR's JSON protocol checks their count and types against the server's method before the hub runs. A call that does not match (a client sending 3 arguments to a 2-parameter method, or the reverse) never reaches the hub: the client gets a `HubException` with the bare text `Failed to invoke '<Method>' due to an error on the server.`, and every other call on the connection keeps working. Adding or removing a parameter therefore breaks each client/server pair that straddles the change, so the client and the server must be built from the same version. The release client auto-updates and a source-built server is whatever was last pulled, so a stale source build is the usual case.

yaat-server logs the refusal as a Warning from `BindingFailureLoggingHubProtocol` (target and invocation id, never the arguments); the framework itself logs it only at Debug, under a category the shipped settings cap at Warning ([logging.md](logging.md#hub-calls-rejected-before-the-hub-runs)). A Development server also sends SignalR's detailed error, e.g. `Invocation provides 3 argument(s) but target expects 2.`, to the client.

Bumping the gate is release work, not day-to-day work: `Recommended` moves every release, `Minimum` only when a cycle breaks older clients. See `.claude/skills/prepare-release/SKILL.md` step 2.6.

## Room membership

`RoomMemberDto(Cid, Initials, ArtccId, Kind, JoinedAtUtc, ConnectionId)` is the single member shape across three
surfaces: `RoomStateDto.Members` (the join seed), `RoomMemberChangedDto.Members` (the push), and
`TrainingRoomInfoDto.Members` (the room list **and** `GET /admin/status`). All three are built by
`TrainingRoomManager.BuildMemberDtos` so occupancy never disagrees between them.

**The two repos declare this record separately, on purpose.** Hoisting it into `Yaat.Sim` to
de-duplicate would drag wire-serialization concerns into the simulation library, break the pattern every
other training DTO follows, and has nowhere to put the client copy's display-only members (`KindLabel`,
`JoinedAtText`) — which the server has no business carrying. A cross-repo reflection test is no better:
`Yaat.Client.Core` pulls Avalonia Desktop, DataGrid, Fonts, and Velopack, so referencing it would put a
desktop UI stack in the server's test build.

What the duplication actually risks is silent drift — the repos never meet at compile time, so a field
renamed or retyped on one side costs nothing at build and simply fails to bind at runtime. Both repos
therefore carry an identical `RoomMembershipWireShapeTests` that pins the constructor signature of
`RoomMemberDto` and `TrainingRoomInfoDto` as a literal string. Changing either DTO fails both suites; the
fix is to update the signature **and** the sibling repo, never to edit one expectation until it goes green.

**A member is a connection, not a person.** vStrips and vTDLS join over this same hub with
`ClientKind.VStrips` / `ClientKind.VTdls`, so one controller running the desktop client plus a strips tab is
**two** members sharing a CID and initials. `ConnectionId` is what distinguishes them; `Kind` is what makes a
count interpretable. Any surface that renders a member count without the kind will report a forgotten browser
tab as an occupied room — that was the pre-provenance behavior, and it made `/admin/status` unusable for
deciding whether a deploy would interrupt anyone.

**`Kind` `towercab` is a viewer on the CRC hub, not a training-hub connection.** Tower Cab 3D (a negotiated joiner attached to the room through a `room:<roomId>` CRC session, or through a joined CRC session) is listed with `Kind = ClientKind.TowerCab`, its user's CID and initials (the CID when the user has no member of their own), the room's ARTCC, and `ConnectionId` = its CRC client id; the DTO signature is unchanged. It is never counted toward attendance or `OpenPositions`, never assignable, never persisted (it reattaches on its own after a planned restart), and does not make its CID a member of the room. Its Kick button sends `KickMember` the row's CID, as every row's does: the kick removes the whole CID from the room (this viewer with it) and bans it.

Two consequences worth knowing before touching this area:

- **The join-time `RoomMemberChanged` push is unreliable for the joiner.** The server broadcasts it to the room
  group from inside `JoinRoom`, and the caller is already in that group, so it can arrive before the client has
  applied the `RoomStateDto` result and assigned `ActiveRoomId` — where `OnRoomMemberChanged`'s room guard drops
  it. `MainViewModel.ApplyRoomState` therefore seeds `RoomMembers` from `RoomStateDto.Members` directly. Do not
  make the panel depend on the push alone.
- **Strips/vTDLS-only rooms auto-pause.** `ScenarioLifecycleService.PauseIfUnattended` pauses a room once no
  member has `ClientKind.Main` and no CRC client is bound (`CrcClientManager.GetClientsForRoom`). It runs from
  the non-abandoned branch of `HandleClientLeft` and from the CRC disconnect path in `CrcWebSocketHandler`. It
  never starts the abandoned-room cleanup timer — a browser tab legitimately holds the room open — and never
  auto-resumes.

## Three JSON source-gen contexts and the WASM failure mode

SignalR's `JsonHubProtocol` calls `JsonSerializer.Serialize<object>(...)` on every argument and payload. Under a default
.NET 10 **WASM** publish, reflection-based metadata is disabled to keep the bundle slim, so any type without compile-time
metadata throws `JsonSerializerIsReflectionDisabled` **at first use, with no compile error**. The desktop client falls
through to reflection and works fine — so a forgotten registration is invisible until someone runs the browser client.

`HubJsonContractTests` (`tests/Yaat.Client.Tests`) closes part of that gap: it reflects over every public `Task<T>` method on `ServerConnection` (each one an `InvokeAsync<T>` wrapper) and fails the build when a Core-owned return type is missing from `YaatHubJsonContext`.

It covers **Core return types only** — broadcast (`.On<T>`) payloads, method arguments, and the Strips/Tdls contexts aren't reflectable from the method surface and stay unguarded, apart from the `ScenarioLoadProgress` payload: named facts pin `ScenarioLoadProgressDto` and `LoadStepDto` in the context and deserialize the server's load shapes (`Steps`, `LoadingBy`) into the client records.

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
   `TrainingDtoFingerprint` (`AircraftChangeTracker.cs:340`).
2. **Delayed-spawn countdowns** — every delayed-queue entry is broadcast on `AircraftUpdated` **unconditionally every
   tick** (its `Delayed (Ns)` status changes each second), bypassing the delta gate
   (`TrainingBroadcastService.cs:191`-`196`).

**Full state** (the seed a joining/reconnecting client gets) is carried separately: `RoomStateDto.AllAircraft`,
`ScenarioLoadedDto.AllAircraft`, `LoadScenarioResult.AllAircraft`, and `RewindResultDto.Aircraft` all carry the complete
manifest, built by `DtoConverter.ToTrainingDto` for every aircraft regardless of fingerprint.

The same three scenario-activation DTOs — `RoomStateDto`, `ScenarioLoadedDto`, `LoadScenarioResult` — also carry the
generator editor's sources: `AircraftGenerators`, `VfrArrivalGenerators`, `OverflightGenerators` and `Positions`
(`ScenarioPositionDto`, from `ScenarioLifecycleService.BuildPositionDtos`). All three read the **live**
`SimScenarioState` generator lists, so a set replaced through `LoadArrivalGenerators` is what a later joiner receives,
not the scenario's authored set. On `RoomStateDto` the four are required members (empty lists with no scenario);
`ArrivalGeneratorsChanged` is the live push and carries the generators only, since positions are fixed for a scenario.
`RoomStateDto.ArtccId` is the room's label, set in `BuildRoomState` by the rule `TrainingRoomInfoDto.ArtccId` uses (`TrainingRoomManager.ListingArtccOf`: the loaded scenario's ARTCC, else the creator's), never null from the server; the client titles the joined room with it, not with `CreatorArtccId`.
The label changes when a scenario loads or unloads, so the payloads that change it carry the new one and the client rebuilds the title `({label}) {initials}'s Room` from it, keeping the creator's initials from `RoomStateDto`: `LoadScenarioResult.ArtccId` (set after the commit, for `LoadScenario` and `StartLiveSession`), `ScenarioLoadedDto.ArtccId`, `RewindResultDto.ArtccId` (the room label, not the scenario's, in a recording load's result and `RecordingLoaded` broadcast), `ScenarioUnloadedDto.ArtccId` and `UnloadScenarioResultDto.ArtccId` (set only when the solo unload ran). The new members are optional trailing ones (default null); a null or blank label leaves the title unchanged. The browser strips transport binds `ScenarioUnloaded` as the empty `BrowserScenarioUnloadedDto`.
`JoinRoom` builds its `RoomStateDto` under the room's tick gate (`room.GuardAsync`): `BuildRoomState` enumerates live
room collections (the generator lists, the manifest, timers, bookmarks) that a tick or a concurrent hub mutation
rewrites in place.

This split is the source of the most common wire bug — see the checklist below.

## Session-settings fan-out

The 25 session-settings fields are duplicated across **four** DTOs and must move in lockstep: `LoadScenarioResult`, `RoomStateDto`, `ScenarioLoadedDto` and `SessionSettingsDto` (all in yaat-server `Dtos/TrainingDtos.cs`) — with the same set on the client side. `RewindResultDto.SessionSettings` is a fifth carrier of the same `SessionSettingsDto` (a recording load's result; null on a rewind's).

The fields: `AutoDeleteOverride`, `EffectiveAutoDeleteMode`, `DepartureAutoDeleteDistanceNm`, `AutoAcceptDelaySeconds`, `AutoClearedToLand`, `AutoCrossRunwayMode` (the `Yaat.Sim.Data.Airport.AutoCrossRunwayMode` enum: Off / All / InactiveOnly), `AutoPullUpToParallel`, `AutoGoAroundOnOccupiedRunway`, `AutoRejectTakeoffOnOccupiedRunway`, `AutoArrivalSpacingOnOccupiedRunway`, `ValidateDctFixes`, `SoloTrainingMode`.

The rest are `SoloParkingInitialCallupRatePercent`, `SoloArrivalGeneratorRatePercent`, `SoloGoAroundProbabilityPercent`, `HasSoloParkingInitialCallupSource`, `HasSoloArrivalGeneratorSource`, `RpoShowPilotSpeech`, `CommandRunDelayMinSeconds`, `CommandRunDelayMaxSeconds`, `LiveTrafficEnabled`, `LiveTrafficCeilingFt`, `LiveTrafficFilter`, and the two level codes.

The level codes are `StudentLowestWorkedLevel` (the lowest position type the solo student works; null when unset or with no scenario) and `StudentOwnLevel` (the level of the student's own position; null with no typed student position).

Both are nullable `string?` level codes (`DEL`, `GND`, `TWR`, `APP`, `CTR`) with a null default, read from the active scenario (`StudentLowestWorkedSeeding.WireLevel` / `WireOwnLevel`), so the `FromRoomCopy` path with no scenario sends null; they are not in `RoomSessionSettings` (the level is a per-scenario answer, carried in `ScenarioAnswers`).

The four DTOs feed three different paths — initial join (`RoomStateDto`), scenario load (`LoadScenarioResult` / `ScenarioLoadedDto`), and live update (`SessionSettingsDto`). Add a setting to fewer than all four and it silently drops on whichever path you missed.

There is a further place a room-level session setting has to be listed: `RoomSessionSettings`
(`yaat-server: …/Simulation/RoomSessionSettings.cs`), the room-level copy that survives a scenario restart or rewind.
The hub DTOs above all read the active `SimScenarioState` and fall back to that room copy when no scenario is loaded.
Miss it and the setting reverts to its default the next time a controller restarts the scenario, while the client's
settings flyout goes on showing the old value — see
[scenario-loading-and-generation.md](scenario-loading-and-generation.md#session-settings-belong-to-the-room-not-the-scenario-object).

## Streaming methods don't use `InvokeAsync`

`ExportRecording` and `LoadRecording` move large payloads as chunked `IAsyncEnumerable<byte[]>` (16 KB chunks; see
`ServerConnection.ChunkBytes`, `ServerConnection.cs:563`) via `StreamAsync` / a streamed parameter, not `InvokeAsync<T>`.
Export progress rides a **separate** broadcast side-channel, `ExportRecordingProgress` (a server→client `(int, int)`
event), not the stream itself. Copying the `InvokeAsync<T>` wrapper pattern for a large recording payload will fail.

## Checklist: adding an `AircraftUpdated` field

This is the canonical version of the add-a-field flow. [server-rooms-and-hub.md](server-rooms-and-hub.md) links here
rather than restating it.

**Decided shape, not converted yet:** `AircraftStateDto` becomes `required` init properties server-side and `AircraftDto` plain init properties client-side, replacing the defaulted positional constructor parameters (about 100 on the server DTO, against the no-optional-parameters rule).

The defaults buy nothing: `DtoConverter.ToTrainingDto` has many callers but there is one `new AircraftStateDto(` site (inside `ToTrainingDto`), and System.Text.Json fills a missing constructor parameter either way. Until the pair is converted, step 1 below still adds a defaulted parameter; the conversion rewrites this checklist.

1. **Add to `AircraftStateDto`** (`../yaat-server/.../Dtos/TrainingDtos.cs`) — a new constructor param with a default
   so older positional call sites still compile.
2. **Add to `AircraftDto`** (`src/Yaat.Client.Core/Services/ServerConnection.cs`) — **same JSON property name**, a
   JSON-compatible type, and a default. Order need not match the server.
3. **Map it in `DtoConverter.ToTrainingDto`** (`../yaat-server/.../Simulation/DtoConverter.cs`) so the value is
   actually written onto the wire DTO.
4. **Add it to `TrainingDtoFingerprint`** and capture it in `CaptureTrainingDto`
   (`../yaat-server/.../Simulation/AircraftChangeTracker.cs`). **Skip this step and the field round-trips on
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

**Documented exception to step 4 — `AsdexFix`.** The `AsdexFix` field (the ASDE-style Ground View / Aircraft List
fix, resolved server-side by `DtoConverter.ResolveGroundFix` from the airport's ASDE-X/SAID config) is deliberately
**not** in `TrainingDtoFingerprint`. It is a pure function of `Destination`, `Route`, and `Scratchpad1` (all already
fingerprinted) plus the ARTCC config and `AircraftState.AirportId` (both static for an aircraft's lifetime), so any
runtime change to the resolved fix is already driven by a fingerprinted input and rebroadcasts correctly. Adding it
would force `ArtccConfigService` + the ARTCC id into `CaptureTrainingDto` for zero behavioral gain. This is safe
*only* because the derived field's dynamic inputs are themselves fingerprinted — the general rule (step 4) still holds
for any field whose inputs are not.

**Same exception — `AutoScratchpad1`.** The STARS destination fallback (`DtoConverter.ResolveAutoScratchpad1FromService`
→ `AutoScratchpadResolver`) follows the identical pattern and is likewise absent from `TrainingDtoFingerprint`. Its
dynamic inputs are `Scratchpad1`, `WasScratchpad1Cleared`, `Departure`, and `Destination`; the STARS facility config and
the student area's `ShowDestination*` adaptation are fixed for the room's lifetime. `Departure`/`Destination`/
`Scratchpad1` were already fingerprinted — **`WasScratchpad1Cleared` was added to `TrainingDtoFingerprint` specifically
to complete that input set**, because clearing SP1 suppresses the fallback and would otherwise not rebroadcast. If you
extend the resolver to depend on a new input, fingerprint that input too.

**Coverage facts that look like gaps but are not.**

- `GroundSpeed` is a computed property (airborne: a function of IAS, wind, altitude, and heading; on the ground: IAS) and is
  fingerprinted, so `IndicatedAirspeed`, `Mach`, `WindDirection`, and `WindSpeed` co-vary with it — none needs its own field.
- `Situation` (`Yaat.Sim.Situation.AircraftSituation`, a number on the wire, `Unknown = 0`, members append-only) is the stored `AircraftSituationState.Current`: the aircraft's situation for the context-menu quick commands, from its phase type (a 360/270 or S-turns takes the phase it resumes), live-traffic flag, flight rules and inbound predicates.

  It is written once a second by the Sim's `Situation` spine step (`SituationClassifier.Classify(ac, simTime, previous)`, with hysteresis at the distance and track edges) and is `Unknown` until the aircraft's first step.

  Several of its inputs (track against the destination bearing, distance) are not fingerprinted, so the situation itself is (`TrainingDtoFingerprint.Situation`): a change of situation alone rebroadcasts the aircraft. The client carries it as `AircraftModel.Situation`.
- `SituationFlags` (`[Flags] Yaat.Sim.Situation.SituationFlags`, a number on the wire, `None = 0`, members append-only) is the stored `AircraftSituationState.Flags`, computed in the same `Situation` step by `SituationFlagCalculator` (`None` until the aircraft's first step).

  Its members are `NearingDepartureHoldLine` = 1, `HoldShortIsDepartureRunway` = 2, `InsideFinalApproachFix` = 4, `RolloutDecelerating` = 8, `HasReportedFieldInSight` = 16, `HasReportedTrafficInSight` = 32, `HasTakeoffClearance` = 64, `PastV1` = 128 and `ApproachClearedForDescent` = 256. `HasTakeoffClearance` means cleared for takeoff; a line-up-and-wait clearance does not count.

  `PastV1` means on the takeoff, airborne or rolling at or above V1, the test `RejectedTakeoff.IsAtOrPastV1` shares with the takeoff-cancel handler. `ApproachClearedForDescent` means an approach clearance with descent on it, not a JFAC/JLOC lateral intercept, and not since ended by a go-around, the missed approach or the hold at its missed-approach fix.

  Several of its inputs (the taxi route, phase internals, the layout distances) are not fingerprinted and the reported-in-sight flags latch, so the flags are fingerprinted as one slot (`TrainingDtoFingerprint.SituationFlags`): a change of flags alone rebroadcasts the aircraft.

  The client carries them as `AircraftModel.SituationFlags` and exposes them to the context menus as `IMenuAircraft.SituationFlags`; only the quick lists read them, All Commands keeps every entry whatever they say.
- `NextCrossingRunway` (a string, an `AircraftStateDto` / `AircraftDto` field) is the stored `AircraftSituationState.NextCrossingRunway`, computed in the same `Situation` step by `SituationFlagCalculator.NextCrossingRunway`: while the situation is Taxiing, Holding on ground or Rollout/exit, the runway whose bar is the first uncleared one on the taxi route, as the display designator of the end to name in `CROSS` ("28R").
  The end is the room's active end of the runway, else the end nearest the aircraft (`SituationFlagCalculator.NextCrossingRunway` takes the room's `ActiveRunways`).

  It is null in every other situation, with no taxi route, when the next uncleared bar is the departure runway's or a taxiway's, at an intersection-departure hold-short of the assigned runway, and while the previous runway is not yet crossed (7110.65 §3-7-2.c): while a runway already cleared to cross lies ahead of that bar, or while the aircraft is still on the pavement of a runway whose bar is behind it on its taxi route.

  A cleared bar of the runway the aircraft is on (rolling out, exiting or clearing it) does not count as an uncrossed runway. It reads the unfingerprinted taxi route, so it has its own fingerprint slot (`TrainingDtoFingerprint.NextCrossingRunway`).

  The client carries it as `AircraftModel.NextCrossingRunway` / `IMenuAircraft.NextCrossingRunway`, from which the quick list's Cross entry takes its runway.
- `ExitsAhead` (a list of `ExitAheadDto`, `null` by default, an `AircraftStateDto` / `AircraftDto` field) is the stored `AircraftSituationState.ExitsAhead`, computed in the same `Situation` step (`SimulationEngine.ExitsAheadOf`): the named exits the arrival can make, one `ExitAheadDto` (taxiway, `ExitSide`, `DistanceFt` rounded to 100 ft, `Planned`, `TurnOffAngleDeg`) per taxiway and side, left side first and each side nearest first.

  `TurnOffAngleDeg` (JSON `turnOffAngleDeg`) is the exit's turn-off angle from the runway's direction of travel in whole degrees (0 along the runway, 90 square, over 90 bending back; 90 where the layout cannot measure it); the flyout draws its glyph from it and shows it in the row's tooltip.

  It is populated in two phases only. While `LandingPhase` rolls out it is `LandingPhase.ListExitsAhead`, the very search an accepted `EL <twy>` / `ER <twy>` runs, with distances from the aircraft and `Planned` on the exit the aircraft is braking for.

  On final it is `FinalApproachExitForecast.ListExitsAhead`, the same search from the projected touchdown, with distances from the landing threshold and `Planned` on the controller's named exit; it is listed only while a full-stop landing follows, the aircraft is on final for its assigned runway between 1 and 5 nm from the threshold, and the type is fixed-wing.

  It is null in every other phase (`RunwayExitPhase` included), under `CLANDF`, and without the runway's layout or hold-short data; it is empty when a list applies and no exit is makeable. Both are read-only, so the step changes no tick.

  The list changes with position and speed, so it is fingerprinted by value (`TrainingDtoFingerprint.ExitsAhead`, an `ExitsAheadFingerprint` over the rows; an empty list is not equal to null; a row's turn-off angle is part of the value), and the snapshot (`AircraftSituationStateDto.ExitsAhead`) writes it only when it is not null, each row with its `turnOffAngleDeg`.

  The client carries it as `AircraftModel.ExitsAhead` / `IMenuAircraft.ExitsAhead`; the Exit left / Exit right flyouts read it as the menu opens (and once a second while the strip's flyout is open) and never recompute it; the runway-exit search behind the final flyout's "Exit at" box runs in `Yaat.Sim` (`LandingPhase.ListRunwayExits`), not in the client.
- `MeetPoints` (a list of `GroundMeetPointDto(Callsign, NodeId, Position)`, `null` by default, an `AircraftStateDto` / `AircraftDto` field after `ExitsAhead`; JSON `meetPoints`: `[{callsign, nodeId, position:{lat, lon}}]` or null) is the stored `AircraftSituationState.MeetPoints`, computed once a second in the same `Situation` step (`SimulationEngine.TickSituation`) by `GroundConflictDetector.MeetCandidates` and `MeetPointsOf`: one row per other on-ground aircraft at the same airport whose driven route meets this aircraft's.

  The meeting point is the first junction both driven routes (`FollowingPhase.DrivenRouteOf`) reach within the 1,500 ft look-ahead (`GroundConflictDetector.FindSharedUpcomingNode`). A `GIVEWAY` hold judges its own junction over the whole of both routes (`GroundConflictDetector.GiveWayJunctionOf`), so a row is a preview, not the node the hold waits at. There is no ETA test, so a row says where two routes meet, not who gets there first, who gives way or whether either must slow. Pairs whose current-segment from-nodes are more than 3,000 ft apart are skipped.

  `NodeId` is the junction's id in the airport's ground layout and `Position` its position. The list is null for an aircraft that is airborne, parked or driving no route, and when no other aircraft's route meets its own.

  Ruling: the Give way "meets your route" point is this server-predicted point, not a client-side intersection of routes, so the client reads `MeetPoints` and never recomputes it. The CA / ATPA note above keeps the actual separation off `AircraftDto` because the client recomputes it per frame from positions; this is the stated exception, an `AircraftDto` field the server computes because the client holds no other aircraft's driven route.

  The taxi routes are not fingerprinted, so it is fingerprinted by value (`TrainingDtoFingerprint.MeetPoints`, a `MeetPointsFingerprint` over the rows; null equals null only), and the snapshot (`AircraftSituationStateDto.MeetPoints`) writes it only when it is not null, with no schema bump.

  The client carries it as `AircraftDto.MeetPoints` and `AircraftModel.MeetPoints` (observable); nothing draws it yet.
- `ArmedReports` (an `ArmedReportsDto(Legs, FinalMiles, Fix)`, `null` by default, the last `AircraftStateDto` / `AircraftDto` field, after `PatternDistanceNm`; JSON `armedReports`: `{legs, finalMiles, fix}` or null) is the deferred pilot reports `REPORT` armed, built by `ArmedReportsDto.From(ac.Approach)` in `DtoConverter`; null when nothing is armed.

  `legs` is the numeric `ArmedReportLegs` flags: Crosswind 1, Downwind 2, Midfield 4, Base 8, Final 16 (0 with none); `finalMiles` is the armed N-mile-final distance (an integer, null when none) and `fix` the armed at-fix report's fix name (null when none). `AircraftApproachState` stays the one home of the armed fields; the DTO is a read-only projection of them.

  It has its own `TrainingDtoFingerprint` slot (`ArmedReports`, compared by value as a record), because the sim clears reports on the tick, not only on commands: the mile-final and fix reports clear themselves when they fire and a full-stop landing clears them all.

  There is no snapshot field (the armed state already lives in the approach state's snapshot) and no `YaatHubJsonContext` registration (the record is nested in `AircraftDto`). The client carries it as `AircraftModel.ArmedReports` / `IMenuAircraft.ArmedReports`.
- `TaxiTurnAboutShape` (a string, `null` by default) is `AircraftGroundOps.TaxiTurnAboutShape` by name: `"FromFarEnd"` or `"InPlace"` while the assigned taxi route starts with a turn about on the taxiway the aircraft stood on (`TaxiRoute.TurnAboutShape`) and the aircraft is still on that first segment, null when none is pending. A far-end route with no leg back stops sending it once the aircraft has turned about and reached the target, though it is still on segment 0.

  `TaxiTurnAboutTargetNodeId` (an int, `null` by default) is `AircraftGroundOps.TaxiTurnAboutTargetNodeId`: the occupied edge's far node the turn about turns the aircraft toward (`TaxiRoute.TurnAboutTargetNodeId`), null when none is pending.

  Both go null when the turn-about leg completes with nothing else on the DTO changing (the `TaxiRoute` string is the same), so each has its own fingerprint slot (`TrainingDtoFingerprint.TaxiTurnAboutShape`, `TaxiTurnAboutTargetNodeId`).

  The client parses the name into `AircraftModel.TaxiTurnAboutShape` (an unknown name logs a warning and reads as `None`) and carries `AircraftModel.TaxiTurnAboutTargetNodeId`; the ground overlay draws exactly the sent shape from the sent node ([ground-rendering.md](ground-rendering.md)).
- `StandDeparture` (a string, `null` by default) is how the aircraft leaves the stand it is parked on, by `StandDeparture` name: `"PushBack"`, `"TaxiOut"` or `"Either"` (a tug push and a taxi-out are both normal), from `ParkedStandDeparture.Of(ac, ac.Ground.Layout)` ([ground/pushback.md](ground/pushback.md), "Stand departure").

  It is null when the aircraft is not in `AtParkingPhase`, or its parking spot names no parking stand or helipad of its layout.

  Like `AsdexFix` it is a derived field fingerprinted by its inputs rather than its value, so `CaptureTrainingDto` never computes it per tick: the phase name and `ParkingSpot` are already fingerprinted, the airport sidecars are fixed for the process, and the layout's identity has its own slot (`TrainingDtoFingerprint.GroundLayoutAirportId`).

  The client parses the name into `AircraftModel.StandDeparture` / `IMenuAircraft.StandDeparture` (an unknown name logs a warning and reads as null), and `AircraftCommandApplicability.CanPushBack` hides the push entries for `TaxiOut` only.
- `TrueTrack` (a double, degrees true), `IsManeuvering` (a bool), `PatternLeg` (`PatternEntryLeg?`), `PatternSide` (`PatternDirection?`) and `PatternDistanceNm` (a double?) are the last five `AircraftStateDto` / `AircraftDto` fields, after `StandDeparture`: the inputs of the traffic-advisory matcher that the other fields do not carry.

  They exist so a client can build a `TrafficAdvisoryCandidate` and run the sim's own `TrafficAdvisoryMatcher` ([solo-training-evaluation.md](solo-training-evaluation.md)).

  `DtoConverter` builds them from the sim: `TrueTrack` is `ac.TrueTrack.Degrees`, `IsManeuvering` is `TrafficAdvisoryMatcher.IsManeuvering(ac.BankAngle)` (a bool, so a turn's changing bank does not rebroadcast the aircraft), and the pattern triple is `TrafficAdvisoryCandidate.PatternPositionOf(ac)`: the sim's classification of the active pattern or final phase against the assigned runway (leg, side, distance to the threshold in NM).

  All three pattern fields are null when the aircraft is not in a pattern, and `PatternSide` is null on final. They are sent as numbers and enums, never re-derived on the client. The candidate's `BaseAircraftType` is not on the wire: the client derives it from `AircraftType` (`AircraftState.StripTypePrefix`), and `AssignedRunwayId` is not a separate field.

  Each has its own `TrainingDtoFingerprint` slot (`TrueTrack`, `IsManeuvering`, `PatternLeg`, `PatternSide`, `PatternDistanceNm`, appended after `GroundLayoutAirportId`): the maneuvering flag flips on a bank change alone, and the pattern triple changes on a phase change that may leave every other slot standing.

  The client lands them as `AircraftModel.TrueTrack`, `IsManeuvering` and `PatternPosition` (a `PatternPosition` record built by `AircraftModel.ToPatternPosition`, null unless both the leg and the distance are sent).
- `HasTaxiwayHoldShortAhead` (a bool, right after `HasActiveTaxiRoute` in `AircraftStateDto` / `AircraftDto`) is `FollowingPhase.DrivenRouteOf(aircraft)?.HasTaxiwayHoldShortAhead()` (`DtoConverter`): the route the aircraft drives is incomplete and its next uncleared hold short ahead is a taxiway or spot bar, the one a bare `RES` pre-clears (`TaxiRoute.NextUnclearedHoldShortAhead`, `TaxiRoute.IsTaxiwayClassHoldShort`). For a `FOLLOWG` follower the driven route is its follow route, which carries no hold short, so the flag is false for followers. The client lands it on `AircraftModel` / `IMenuAircraft.HasTaxiwayHoldShortAhead`, and `AircraftCommandApplicability.CanResumeTaxi` offers "Resume taxi" to a rolling aircraft on it. It is its own `TrainingDtoFingerprint` field (`AircraftChangeTracker`), since the route's cleared bars are not fingerprinted, so clearing a bar rebroadcasts the aircraft.
- `ResumeTaxiRefused` (a bool, right after `HasTaxiwayHoldShortAhead` in `AircraftStateDto` / `AircraftDto`) is `GroundCommandHandler.RefusesResumeTaxi(aircraft)` (`DtoConverter`): a `HOLD` broke off a `FOLLOWG` follow and nothing comes after the hold it ends in, held short ahead of it included, so the sim refuses `RES`.

  The client lands it on `AircraftModel` / `IMenuAircraft.ResumeTaxiRefused`, and `AircraftCommandApplicability.CanResumeTaxi` and `CanResumeFromHoldShort` hide "Resume taxi" on it. It is its own `TrainingDtoFingerprint` slot (`AircraftChangeTracker`), since the phase sequence carries phase names only.
- `PendingPilotRequest` (a `PendingPilotRequestDto(Kind, ResponseState, LastPilotLine, FirstRequestedAtSeconds, LastRequestedAtSeconds)`, `null` by default, after `ArmedReports`; JSON `pendingPilotRequest`: `{kind, responseState, lastPilotLine, firstRequestedAtSeconds, lastRequestedAtSeconds}` or null) is the aircraft's `PilotPendingRequest` while it is open (`IsOpen`: response state `None` or `Standby`), built by `DtoConverter.BuildPendingPilotRequest`. It is null when the aircraft has no request or the request was satisfied, denied or superseded.

  `kind` is the `PilotPendingRequestKind` name (`"Taxi"`, `"Takeoff"`, `"Landing"`, `"Approach"`, `"AirspaceEntry"`, `"Clearance"`, `"Release"`, `"Pushback"`), `responseState` the `PilotPendingRequestResponseState` name (`"None"` or `"Standby"`), `lastPilotLine` the terminal form of the pilot's last asking, and the two times are scenario-elapsed seconds of the first and the last asking.

  `HasMadeInitialContact` (a bool, `false` by default, the last field) is `AircraftState.HasMadeInitialContact`: the pilot has made initial contact on the frequency.

  A request opens, goes to standby and closes, and the pilot makes contact, with nothing else on the aircraft changing, so each has its own `TrainingDtoFingerprint` slot (`PendingPilotRequest`, compared by value as the wire record; `HasMadeInitialContact`). There is no snapshot field of its own and no `YaatHubJsonContext` registration (the record is nested in `AircraftDto`). The client carries both as `AircraftModel.PendingPilotRequest` and `AircraftModel.HasMadeInitialContact` (observable); nothing draws them yet.
- `SmartStatus` / `SmartStatusSeverity` derive entirely from fingerprinted `AircraftState` inputs (`AircraftStatusView.FromState`
  → `AircraftStatusDescriber.Describe`); the only non-`ac` inputs (`IsDelayed`, `IsAutoClearedToLand`, the latter the scenario's per-aircraft
  `IsAutoClearedToLandFor`, true under a simulated tower) are broadcast parameters `CaptureTrainingDto` cannot see, and they only matter for moving aircraft. No signature threading is needed.
- `HasStoredTaxiRoute` (a bool, right after `HasActiveTaxiRoute` in `AircraftStateDto` / `AircraftDto`) is true while the aircraft has no incomplete active route but `PhaseList.TaxiRoute` holds one a `TAXI`/`TAXIAUTO` on the landing rollout stored behind the exit (`DtoConverter`). Until the exit completes and the route starts, the `TaxiRoute` string and `TaxiDestination` are read from that stored route (`AircraftState.ActiveOrStoredTaxiRoute`), so the strip, the ground view and the status text show it from the moment the TAXI is accepted. The client lands it on `AircraftModel` / `IMenuAircraft.HasStoredTaxiRoute`: the ground view draws the route, and `AircraftCommandApplicability.CanGiveWayTo` / `ShowsGiveWay` offer "Give way to…" on it. It has no fingerprint slot of its own: it co-varies with the `TaxiRoute` string.
- `ActiveApproachId`, `HasActiveTaxiRoute`, `PointoutToTcpCode` are covered by motion or co-vary with `TaxiRoute` /
  `PointoutStatus`.

**Auditing the fingerprint.** One `ChangeDetectionTests` test per candidate field: mutate *only* that `ac.*` source on an
otherwise-static aircraft and assert `DtoChangeFlags.TrainingDto` fires. A failing test is a genuine gap — a stationary
(stopped, parked, holding, phantom) aircraft would never rebroadcast that field. `AircraftState.WindComponents` has an
`internal` setter and cannot be assigned from the server test assembly; drive IAS instead to exercise the `GroundSpeed`
path. The fingerprint is a `readonly record struct` with compiler-generated equality: add a positional field to both the
struct and the `CaptureTrainingDto` call in the same order. `DtoConverter.IsAutoDeletePending` is `internal` so the
fingerprint captures the exact DTO value.

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
- **Beacon code is two fields, not one.** `AircraftTransponder` has `AssignedCode` (what ATC filed/recycled) and `Code`
  (what the pilot is actually squawking); filing a plan or `RequestNewBeaconCode` only writes `AssignedCode`, and the
  pilot keeps squawking the old `Code` until an `SQ` command. The training DTO carries both: `BeaconCode` ← `Code`
  (reported — radar datablock / squawk flyout / duplicate detection) and `AssignedBeaconCode` ← `AssignedCode` (the
  flight-plan squawk the strip and the Flight Plan Editor's BCN box show). Reading `BeaconCode` where you meant the
  assigned code shows `0000` until the pilot squawks — that was the N263FY FPE bug. A third field, `CommandedSquawkVfr`
  ← `Transponder.CommandedSquawkVfr`, is the RPO-display latch that suppresses the radar's assigned-vs-reported mismatch
  flash after a `SQVFR`/`SQV` or `SQ 1200` (released when a new code is assigned, on `SQ` any other code, or on a bare `SQ`); it is in `TrainingDtoFingerprint` so the flash turns
  off / back on live.
