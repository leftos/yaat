# CRC Protocol Support

Status of yaat-server's support for the CRC WebSocket hub protocol.
Authoritative interface definitions: `X:\dev\vatsim-vnas\messaging\`

> **vatsim-server-rs is read-only.** Its stubs don't mean a feature isn't needed — YAAT needs
> full two-way interaction. Evaluate mutation-capable methods against vNAS messaging/data interfaces.

## Legend

- [x] **Implemented** — handler exists, processes request, returns correct response
- [~] **Stub** — handler returns nil-ack without processing (CRC won't crash, but feature doesn't work)
- [ ] **Missing** — no handler; falls through to default nil-ack in `HandleInvocation`

---

## Client → Server (CRC calls these on the hub)

### Session Management

- [x] `GetServerConfiguration()` → returns UDP port (6809)
- [x] `StartSession(StartSessionDto)` → registers position, returns `SessionInfoDto`
- [x] `ActivateSession()` → sends `ReceiveOpenPositions` + `SetSessionActive(true)` + ack
- [x] `DeactivateSession()` → sends `ReceiveOpenPositions` + `SetSessionActive(false)` + ack
- [x] `EndSession()` — unregisters position, clears active flag, broadcasts updated positions
- [x] `GetSessions()` — nil-ack; confirmed unused by CRC (only referenced from source-generated SignalR proxy; vNAS multi-session API not invoked by the public CRC build)
- [x] `JoinSession(JoinSessionDto)` — nil-ack; confirmed unused by CRC (same)
- [x] `LeaveSession()` — nil-ack; confirmed unused by CRC (`ClientSession.Disconnect()` uses `EndSession`, never `LeaveSession`)

### Position Management

- [x] `SetFrequencies(SetFrequenciesDto)` — stores transmit/receive lists on `CrcClientState`; no rebroadcast (CRC fire-and-forget)
- [x] `SetControllerInfo(string)` — stores per-client; surfaced to peers via `RequestControllerInfo`
- [x] `OpenSecondaryPosition(SecondaryPositionDto)` — registers secondary position, broadcasts OpenPositions, tracks entity ID
- [x] `CloseSecondaryPosition(SecondaryPositionDto)` — unregisters + emits `DeleteOpenPositions` for the secondary entity
- [x] `ActivateSecondaryPosition(SecondaryPositionDto)` — marks secondary as active
- [x] `DeactivateSecondaryPosition(SecondaryPositionDto)` — marks secondary as inactive
- [x] `ChangeActiveEramPosition(ChangeActivePositionDto)` — clone of STARS analogue via shared `HandleChangeActivePosition` body
- [x] `ChangeActiveStarsPosition(ChangeActivePositionDto)` — switches primary position identity; emits `DeleteOpenPositions` for old entity if changed
- [x] `UpdateAutoTrackAirports(UpdateAutoTrackAirportsDto)` — stores auto-track airport list per CRC client

### Topic Subscriptions

- [x] `Subscribe(Topic)` — adds topic, returns initial data via `CrcBroadcastService`
- [x] `Unsubscribe(Topic)` — removes topic

### STARS Commands

- [x] `ProcessStarsCommand(ProcessStarsCommandDto)` — IC, TC, Handoff, Implied (accept handoff, accept pointout, amend filed altitude, temp altitude, scratchpad 1/2, pointout, reject pointout, pilot reported altitude), MultiFunc (basic/full consolidation, deconsolidation), Coordination (stub by design — YAAT client handles via RD/RDACK)

### ERAM Commands

- [x] `ProcessEramMessage(ProcessEramMessageDto)` — QN (leader/VCI), QF (FP readout), QL (quick look), RD (route display), QU (projected route), QT (track init/drop), QZ (interim alt), QQ (assigned / local-interim / procedure alt), QS (scratchpad), QP (pointout initiate / accept), QR (aliased to RD). Unknown verbs return `FORMAT`.
- [x] `SetEramSectorConfiguration(EramSectorConfigurationDto)` — per-sector storage + broadcast
- [x] `ToggleEramDwellLock(aircraftId)` — toggles `AircraftState.IsDwellLocked`
- [x] `ClearEramPointout(aircraftId, pointoutId)` — ownership-checked (receiving sector only) clear of both R-side and D-side flags on the matching `EramPointoutState`
- [~] `ClearOrDeleteEramCrrGroup(groupLabel)` — nil-ack stub; creation path unresolved (no dedicated hub method; live CRC trace needed)
- [~] `SetEramCrrGroupColor(groupLabel, CrrColor)` — nil-ack stub; blocked on creation path

### Flight Plan Operations

- [x] `CreateFlightPlan(CreateOrAmendFlightPlanDto)` — creates/updates aircraft state from FP fields
- [x] `AmendFlightPlan(CreateOrAmendFlightPlanDto)` — amends aircraft state from FP fields
- [x] `RequestNewBeaconCode(aircraftId)` — assigns next available code from BeaconCodePool
- [x] `SendClearance(aircraftId, ClearanceDto)` — stores clearance fields on AircraftState
- [x] `TdlsDump(aircraftId)` — sets TdlsDumped flag
- [x] `SetHoldAnnotations(aircraftId, HoldAnnotationsDto)` — stores hold annotation fields
- [x] `DeleteHoldAnnotations(aircraftId)` — clears hold annotation fields
- [x] `GetVfrFlightPlanRemarks(aircraftId)` — returns aircraft remarks
- [x] `SetVoiceType(aircraftId, VoiceType)` — sets VoiceType on aircraft state

### Messaging

- [x] `SendRadioMessage(message)` — routes to YAAT terminal + CRC clients in room
- [x] `SendPrivateMessage(to, message)` — routes to YAAT terminal + target CRC client
- [x] `SendAtcMessage(message)` — routes to YAAT terminal + CRC clients in room
- [x] `SendBroadcastMessage(message)` — routes to YAAT terminal + CRC clients in room
- [x] `SendVnasBroadcastMessage(message)` — routes to YAAT terminal + CRC clients in room
- [x] `SendWallopMessage(message)` — routes to YAAT terminal + CRC clients in room

### ASDEX

- [x] `EditAsdexDbFields(EditAsdexDbFieldsDto)` — updates scratchpad fields on aircraft
- [x] `TagAsdexTarget(facilityId, aircraftId)` — nil-ack (all targets associated in sim)
- [x] `TerminateAsdexTrack(facilityId, trackId)` — marks track as terminated
- [x] `SuspendAsdexTrack(facilityId, trackId)` — marks track as suspended
- [x] `UnsuspendAsdexTrack(facilityId, trackId)` — clears suspended flag
- [x] `InhibitAsdexAlerts(facilityId, aircraftId)` — sets alert inhibited flag
- [x] `EnableAllAsdexAlerts(facilityId)` — clears all inhibited alerts
- [x] `AddAsdexTempData(facilityId, AsdexTempDataDto)` — stores temp data item
- [x] `DeleteAsdexTempData(facilityId, tempDataId)` — removes temp data item
- [x] `SaveAsdexTempDataPreset(facilityId, AsdexTempDataPresetDto)` — stores preset
- [x] `ToggleAsdexTempDataPreset(facilityId, tempDataPresetId)` — toggles preset active state
- [x] `DeleteAsdexTempDataPreset(facilityId, tempDataPresetId)` — removes preset
- [x] `UpdateAsdexSafetyLogicConfiguration(facilityId, AsdexSafetyLogicConfigurationDto)` — acknowledged (config storage deferred)

### Flight Strips

- [x] `CreateStripItem(CreateStripItemDto)` — stores strip item in room state
- [x] `UpdateStripItem(UpdateStripItemDto)` — acknowledged (field update deferred)
- [x] `DeleteStripItem(DeleteStripItemDto)` — removes strip item
- [x] `MoveStripItem(MoveStripItemDto)` — acknowledged (bay layout deferred)
- [x] `RequestFullFlightStripsState(facilityId)` — returns empty state
- [x] `RequestFlightStrip(facilityId, aircraftId)` — creates strip from aircraft data
- [x] `RequestBlankStrip(facilityId)` — creates blank strip

### Information Requests

- [x] `GetRealName(callsign)` — returns stored real name from session
- [x] `RequestControllerInfo(callsign)` — replies via `ReceivePrivateMessage(from=callsign, text=<controllerInfo>)`
- [x] `RequestClientInfo(callsign)` — replies with client name/version/rating/connect-time
- [x] `RequestClientVersion(callsign)` — replies with "ClientName Version" one-liner
- [x] `KillClient(victim, message?)` — supervisor-rating check; pushes `HandleSessionEnded(isForcible=true)` and closes the socket

### Navigation

- [x] `GenerateFrd(GeoPoint)` — returns empty string (reverse FRD lookup not yet implemented)

---

## Server → Client (server pushes these to CRC)

### Session Lifecycle

- [x] `SetSessionActive(bool)` — sent on activate/deactivate
- [x] `HandleSessionStarted(SessionInfoDto)` — pushed after StartSession response (`CrcClientState.Session.cs:84,602-613`)
- [x] `HandleSessionEnded(reason, isForcible)` — pushed on EndSession (`CrcClientState.Session.cs:563,615-626`); `isForcible=true` path awaits KillClient (Bucket B)
- [x] `HandleFsdConnectionStateChanged(bool)` — pushed on StartSession as `true` (`CrcClientState.Session.cs:83,590-600`); yaat-server has no FSD bridge, so never flips

### Open Positions

- [x] `ReceiveOpenPositions(Topic, List<OpenPositionDto>)` — initial data on subscribe + activate/deactivate
- [x] `DeleteOpenPositions(Topic, List<string>)` — wired into `EndSession`, `CloseSecondaryPosition`, WebSocket disconnect (primary + secondary entity IDs), and primary position change when the entity ID differs

### Flight Plans

- [x] `ReceiveFlightPlans(Topic, List<FlightPlanDto>)` — per-tick broadcast + initial data; now includes Clearance + HoldAnnotations + TdlsDumped from AircraftState
- [x] `DeleteFlightPlans(Topic, List<string>)` — aircraft removal

### STARS

- [x] `ReceiveStarsTracks(Topic, List<StarsTrackDto>)` — per-tick broadcast + initial data; VoiceType from AircraftState
- [x] `DeleteStarsTracks(Topic, List<string>)` — aircraft removal / coast-out
- [x] `ReceiveStarsLineNumbers(Topic, List<StarsLineNumberDto>)` — per-tick broadcast (`CrcBroadcastService.cs` + `StarsLineNumberAssigner.cs`)
- [x] `DeleteStarsLineNumbers(Topic, List<string>)` — aircraft removal
- [x] `ReceiveStarsShortTermConflicts(Topic, List<StarsShortTermConflictDto>)` — event-driven; detector is `ConflictAlertDetector` in Yaat.Sim, fan-out at `CrcBroadcastService.BroadcastConflictAlertsAsync`
- [x] `DeleteStarsShortTermConflicts(Topic, List<string>)` — paired with detector
- [x] `ReceiveStarsReadoutArea(StarsReadoutAreaDto)` — direct-push helper on `CrcClientState` (`SendStarsReadoutAreaAsync`); no command-handler consumers yet
- [x] `ReceiveStarsCoordinationLists(Topic, List<StarsCoordinationListDto>)` — coordination channel broadcasts with status/expiry
- [x] `ReceiveStarsConsolidationItems(Topic, List<StarsConsolidationItemDto>)` — consolidation hierarchy broadcasts

### ERAM

- [x] `ReceiveEramTargets(Topic, List<EramTargetDto>)` — per-tick broadcast + initial data
- [x] `DeleteEramTargets(Topic, List<string>)` — aircraft removal
- [x] `ReceiveEramDataBlocks(Topic, List<EramDataBlockDto>)` — per-tick broadcast + initial data
- [x] `DeleteEramDataBlocks(Topic, List<string>)` — aircraft removal
- [x] `ReceiveEramTracks(Topic, List<EramTrackDto>)` — per-tick + initial data; per-client DTO conversion so `OnFrequencySectorIds` reflects each subscriber's sector
- [x] `DeleteEramTracks(Topic, List<string>)` — aircraft removal
- [ ] `DeleteEramTargetHistoryEntries(Topic, List<string>)` — history is a UDP stream per vNAS convention; deferred (Bucket F)
- [x] `ReceiveEramRouteLines(Topic, List<EramRouteLineDto>)` — event-driven (QU / RD); initial data on subscribe
- [x] `DeleteEramRouteLines(Topic, List<string>)` — paired with QU/RD toggle-off
- [ ] `ReceiveEramCrrGroups(Topic, List<EramCrrGroupDto>)` — CRR creation path unresolved
- [ ] `DeleteEramCrrGroups(Topic, List<string>)` — same
- [x] `ReceiveEramSectorConfiguration(Topic, EramSectorConfigurationDto)` — event-driven after `SetEramSectorConfiguration`
- [ ] `ReceiveEramShortTermConflicts(Topic, List<EramShortTermConflictDto>)` — needs en-route STCA detector (Bucket F)
- [ ] `DeleteEramShortTermConflicts(Topic, List<string>)` — paired with detector

### ASDEX

- [x] `ReceiveAsdexTargets(Topic, List<AsdexTargetDto>)` — per-tick broadcast + initial data
- [x] `DeleteAsdexTargets(Topic, List<string>)` — aircraft removal
- [x] `ReceiveAsdexTracks(Topic, List<AsdexTrackDto>)` — per-tick broadcast + initial data; VoiceType from AircraftState
- [x] `DeleteAsdexTracks(Topic, List<string>)` — aircraft removal
- [x] `ReceiveAsdexSafetyLogicConfiguration(Topic, AsdexSafetyLogicConfigurationDto)` — initial data + event-driven (`CrcBroadcastService.cs` + `CrcClientState.Asdex.cs`)
- [x] `ReceiveAsdexHoldBars(Topic, List<AsdexHoldBarDto>)` — empty-list initial broadcast on subscribe; no geometry source yet (hold-bar polylines live in ASDEX video maps, not config)
- [ ] `ReceiveAsdexAlerts(Topic, List<AsdexAlertDto>)` — requires safety-logic detector (deferred)
- [ ] `DeleteAsdexAlerts(Topic, List<string>)` — paired with detector
- [x] `ReceiveAsdexTempDatas(Topic, List<AsdexTempDataDto>)` — initial + event-driven (`CrcClientState.Asdex.cs`)
- [x] `DeleteAsdexTempDatas(Topic, List<string>)`
- [x] `ReceiveAsdexTempDataPresets(Topic, List<AsdexTempDataPresetDto>)` — initial + event-driven
- [x] `DeleteAsdexTempDataPresets(Topic, List<string>)`

### Tower Cab

- [x] `ReceiveTowerCabAircrafts(Topic, List<TowerCabAircraftDto>)` — per-tick broadcast + initial data; VoiceType from AircraftState
- [x] `DeleteTowerCabAircrafts(Topic, List<string>)` — aircraft removal

### Ground Targets

- [x] `ReceiveGroundTargets(Topic, List<GroundTargetDto>)` — per-tick broadcast (`CrcBroadcastService.cs` + `DtoConverter.ToGroundTarget`)
- [x] `DeleteGroundTargets(Topic, List<string>)`

### Flight Strips

- [x] `ReceiveStripItems(Topic, List<StripItemDto>)` — `StripBroadcaster.cs` fan-out on every strip mutation
- [x] `ReceiveFlightStripsState(Topic, FlightStripsStateDto)` — initial state on subscribe + full broadcasts on change

### Messaging

- [x] `ReceiveServerMessage(string)` — helper method available on CrcClientState
- [x] `ReceiveServerError(string)` — helper method available on CrcClientState
- [x] `ReceivePrivateMessage(TextMessageDto)` — routed from SendPrivateMessage
- [x] `ReceiveAtcMessage(TextMessageDto)` — routed from SendAtcMessage
- [x] `ReceiveWallopMessage(TextMessageDto)` — routed from SendWallopMessage
- [x] `ReceiveBroadcastMessage(TextMessageDto)` — routed from SendBroadcastMessage
- [x] `ReceiveVnasBroadcastMessage(TextMessageDto)` — routed from SendVnasBroadcastMessage
- [x] `ReceiveRadioMessage(RadioMessageDto)` — routed from SendRadioMessage

### NEXRAD

- [x] `ReceiveNexradData(Topic, NexradDataDto)` — opt-in WMS fetch (`Nexrad:Enabled=true`) from NOAA opengeo `conus_cref_qcd`, 5 min cache, 5 min background refresh broadcast; empty-sentinel by default or when the room carries a preset `WeatherProfile`

---

## Priority Roadmap

### Completed

- [x] Session lifecycle (EndSession, nil-ack stubs for JoinSession/LeaveSession/GetSessions)
- [x] Session lifecycle pushes (HandleSessionStarted, HandleSessionEnded, HandleFsdConnectionStateChanged)
- [x] Nil-ack stubs (SetFrequencies, SetControllerInfo, RequestControllerInfo, etc.)
- [x] Simple data handlers (GetRealName, GetVfrFlightPlanRemarks, SetVoiceType, TdlsDump, GenerateFrd)
- [x] Flight plan mutations (Create/Amend, RequestNewBeaconCode, SendClearance, HoldAnnotations)
- [x] Messaging pipeline (6 send handlers + CRC-to-CRC routing)
- [x] ASDEX management (13 handlers + room state + temp data/preset/safety-config broadcasts)
- [x] Flight strips (7 handlers + room state + ReceiveStripItems/FlightStripsState broadcasts)
- [x] Ground targets broadcast (per-tick)
- [x] Auto-track airports + secondary positions (6 handlers)
- [x] AircraftState fields (VoiceType, TdlsDumped, HoldAnnotation*, Clearance*)
- [x] BeaconCodePool (room-scoped octal code assignment)
- [x] DtoConverter updates (VoiceType, TdlsDumped, Clearance, HoldAnnotations)
- [x] STARS line numbers broadcast (per-tick)
- [x] STARS short-term conflict detection + broadcast

### Bucket B — Session/Info quick wins (done)

- [x] Capture missing `StartSession` fields (`ClientName`, `ClientVersion`, `SysUid`, `ControllerInfo`, `NetworkRating`, `ConnectedAtUtc`) in `ParseStartSessionArgs`
- [x] `CrcClientManager.FindByPositionCallsign` helper (shared by info requests + KillClient)
- [x] `SetControllerInfo` — real storage handler (unblocks `.atis` dot-command)
- [x] `RequestControllerInfo` / `RequestClientInfo` / `RequestClientVersion` — reply via `ReceivePrivateMessage`
- [x] `SetFrequencies` — storage-only handler
- [x] `ChangeActiveEramPosition` — clone of STARS analogue; shared body in `HandleChangeActivePosition`
- [x] `KillClient` — supervisor-rating check + forcible session end
- [x] `DeleteOpenPositions` — wired into WebSocket disconnect, `CloseSecondaryPosition`, position-change

### Bucket C — Easy broadcast stubs (done)

- [x] `ReceiveNexradData` — empty-sentinel broadcast on subscribe
- [x] `ReceiveStarsReadoutArea` — direct-push send helper
- [x] `ReceiveAsdexHoldBars` — empty-list broadcast on subscribe

### Bucket D — ERAM milestone MVP (done)

- [x] `Hubs/CrcClientState.Eram.cs` scaffolding + dispatch entries
- [x] `AircraftState` ERAM fields: `IsDwellLocked`, `IsVci`, `EramLeaderDirection/Length`, `EramInterimAltitude`, `LocalInterimAltitude`, `ProcedureAltitude`, `ControllerEnteredAltitude`
- [x] `TrainingRoom.EramState` (`EramRoomState`) — sector config, route lines, quicklook sets, RD toggles
- [x] `CrcVisibilityTracker.IsVisibleOnEram` predicate (currently mirrors STARS; en-route/approach split is a future refinement)
- [x] `ProcessEramMessage` MVP verbs: QN, QF, QL, RD, QU
- [x] `SetEramSectorConfiguration` handler + per-sector broadcast
- [x] `ToggleEramDwellLock` handler
- [x] `ReceiveEramTracks` / `DeleteEramTracks` per-tick stream
- [x] `ReceiveEramSectorConfiguration` event-driven broadcast
- [x] `ReceiveEramRouteLines` / `DeleteEramRouteLines` (driven by QU/RD)

### Bucket E — ERAM expansion (partial)

- [x] Mutation verbs: QT, QZ, QQ, QS (QR aliased to RD)
- [x] QP (pointout initiate/accept) — wired via `DispatchQp`; initiate form creates a new `EramPointoutState`, accept form flips `IsAcknowledged`
- [x] `ClearEramPointout` — real handler in `CrcClientState.cs` (receiver-sector ownership check + flip both cleared flags)
- [ ] `ReceiveEramCrrGroups` / `DeleteEramCrrGroups` + `SetEramCrrGroupColor` + `ClearOrDeleteEramCrrGroup`
  - CRR group creation path unresolved — dispatched as nil-ack stubs; prototype test-only hook requires a live CRC wire trace

### Bucket F — Deferred (upstream-blocked)

- [x] Real NEXRAD fetch — opt-in `Nexrad:Enabled=true` wires `WmsNexradProvider` + `NexradRefreshHostedService` (5 min cadence, NOAA opengeo WMS, preset-weather gate)
- [ ] `ReceiveAsdexAlerts` / `DeleteAsdexAlerts` — needs `AsdexSafetyLogicDetector` in Yaat.Sim
- [ ] ERAM short-term conflict detection + broadcast (clone STARS STCA once validated)
- [ ] NEXRAD real fetch (NOAA WMS integration; replaces empty-sentinel)
- [ ] ERAM target history UDP stream (+ `DeleteEramTargetHistoryEntries`) — vNAS sends over UDP, not SignalR
- [ ] `AsdexHoldBarDto` dynamic `Status` from safety logic (geometry sourced from ASDEX video maps)
- [ ] Remaining Bucket E items (QP pointouts, CRR lifecycle)

---

## Summary

| Category | Implemented | Stubbed | Missing |
|----------|:-----------:|:-------:|:-------:|
| Session management | 8 | 0 | 0 |
| Position management | 9 | 0 | 0 |
| Subscriptions | 2 | 0 | 0 |
| STARS commands | 1 (rich) | 0 | 0 |
| ERAM commands | 4 | 2 | 0 |
| Flight plan ops | 9 | 0 | 0 |
| Messaging | 6 | 0 | 0 |
| ASDEX management | 13 | 0 | 0 |
| Flight strips | 7 | 0 | 0 |
| Info requests | 5 | 0 | 0 |
| Navigation | 1 | 0 | 0 |
| **Client→Server total** | **65** | **2** | **0** |
| Server→Client broadcasts | 42 | 0 | 11 |

**Client→Server:** 2 remaining stubs are ERAM CRR group-color + CRR delete, blocked on the unresolved CRR creation path (no creation hub method; requires a live CRC wire trace).

**Server→Client broadcasts remaining (11):** ERAM CRR groups Receive/Delete (2), ERAM STCA Receive/Delete (2), ERAM target history UDP Delete (1), ASDEX alerts Receive/Delete (2), plus detector work to populate existing empty broadcasts — AsdexAlerts, ASDEX safety-logic dynamic hold-bar status, ERAM STCA. All are upstream-blocked.

**ProcessStarsCommand detail:** IC, TC, Handoff, Implied (9 sub-ops), MultiFunc (CON/DECON), Coordination (stub)
