# CRC Protocol Support

Status of yaat-server's support for the CRC WebSocket hub protocol.
Authoritative interface definitions: `X:\dev\towercab-3d-vnas\docs\repos\messaging-master\`

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
- [~] `GetSessions()` — nil-ack stub
- [~] `JoinSession(JoinSessionDto)` — nil-ack stub
- [~] `LeaveSession()` — nil-ack stub

### Position Management

- [~] `SetFrequencies(SetFrequenciesDto)` — nil-ack stub
- [~] `SetControllerInfo(string)` — nil-ack stub
- [x] `OpenSecondaryPosition(SecondaryPositionDto)` — registers secondary position, broadcasts OpenPositions
- [x] `CloseSecondaryPosition(SecondaryPositionDto)` — unregisters secondary position, broadcasts OpenPositions
- [x] `ActivateSecondaryPosition(SecondaryPositionDto)` — marks secondary as active
- [x] `DeactivateSecondaryPosition(SecondaryPositionDto)` — marks secondary as inactive
- [ ] `ChangeActiveEramPosition(ChangeActivePositionDto)` — ERAM-specific, deferred
- [x] `ChangeActiveStarsPosition(ChangeActivePositionDto)` — switches primary position identity
- [x] `UpdateAutoTrackAirports(UpdateAutoTrackAirportsDto)` — stores auto-track airport list per CRC client

### Topic Subscriptions

- [x] `Subscribe(Topic)` — adds topic, returns initial data via `CrcBroadcastService`
- [x] `Unsubscribe(Topic)` — removes topic

### STARS Commands

- [x] `ProcessStarsCommand(ProcessStarsCommandDto)` — IC, TC, Handoff, Implied (accept handoff, accept pointout, amend filed altitude, temp altitude, scratchpad 1/2, pointout, reject pointout, pilot reported altitude), MultiFunc (basic/full consolidation, deconsolidation), Coordination (stub by design — YAAT client handles via RD/RDACK)

### ERAM Commands

- [ ] `ProcessEramMessage(ProcessEramMessageDto)` — QN (leader line), QF (print FP), QL (quick look), RD (route display), QU (projected route). Planned for ERAM milestone.
- [ ] `SetEramSectorConfiguration(EramSectorConfigurationDto)` — sector config updates
- [ ] `ToggleEramDwellLock(aircraftId)` — dwell lock on target
- [ ] `ClearEramPointout(aircraftId, pointoutId)` — clear pointout
- [ ] `ClearOrDeleteEramCrrGroup(groupLabel)` — CRR groups
- [ ] `SetEramCrrGroupColor(groupLabel, CrrColor)` — CRR group colors

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
- [~] `RequestControllerInfo(callsign)` — nil-ack stub
- [~] `RequestClientInfo(callsign)` — nil-ack stub
- [~] `RequestClientVersion(callsign)` — nil-ack stub
- [~] `KillClient(victim, message?)` — nil-ack stub

### Navigation

- [x] `GenerateFrd(GeoPoint)` — returns empty string (reverse FRD lookup not yet implemented)

---

## Server → Client (server pushes these to CRC)

### Session Lifecycle

- [x] `SetSessionActive(bool)` — sent on activate/deactivate
- [ ] `HandleSessionStarted(SessionInfoDto)` — sent after StartSession completes
- [ ] `HandleSessionEnded(reason, isForcible)` — sent on session teardown
- [ ] `HandleFsdConnectionStateChanged(bool)` — FSD connection state

### Open Positions

- [x] `ReceiveOpenPositions(Topic, List<OpenPositionDto>)` — initial data on subscribe + activate/deactivate
- [ ] `DeleteOpenPositions(Topic, List<string>)` — position removal

### Flight Plans

- [x] `ReceiveFlightPlans(Topic, List<FlightPlanDto>)` — per-tick broadcast + initial data; now includes Clearance + HoldAnnotations + TdlsDumped from AircraftState
- [x] `DeleteFlightPlans(Topic, List<string>)` — aircraft removal

### STARS

- [x] `ReceiveStarsTracks(Topic, List<StarsTrackDto>)` — per-tick broadcast + initial data; VoiceType from AircraftState
- [x] `DeleteStarsTracks(Topic, List<string>)` — aircraft removal / coast-out
- [ ] `ReceiveStarsLineNumbers(Topic, List<StarsLineNumberDto>)` — subscribed but not streamed
- [ ] `DeleteStarsLineNumbers(Topic, List<string>)`
- [ ] `ReceiveStarsShortTermConflicts(Topic, List<StarsShortTermConflictDto>)`
- [ ] `DeleteStarsShortTermConflicts(Topic, List<string>)`
- [ ] `ReceiveStarsReadoutArea(StarsReadoutAreaDto)`
- [x] `ReceiveStarsCoordinationLists(Topic, List<StarsCoordinationListDto>)` — coordination channel broadcasts with status/expiry
- [x] `ReceiveStarsConsolidationItems(Topic, List<StarsConsolidationItemDto>)` — consolidation hierarchy broadcasts

### ERAM

- [x] `ReceiveEramTargets(Topic, List<EramTargetDto>)` — per-tick broadcast + initial data
- [x] `DeleteEramTargets(Topic, List<string>)` — aircraft removal
- [x] `ReceiveEramDataBlocks(Topic, List<EramDataBlockDto>)` — per-tick broadcast + initial data
- [x] `DeleteEramDataBlocks(Topic, List<string>)` — aircraft removal
- [ ] `ReceiveEramTracks(Topic, List<EramTrackDto>)`
- [ ] `DeleteEramTracks(Topic, List<string>)`
- [ ] `DeleteEramTargetHistoryEntries(Topic, List<string>)`
- [ ] `ReceiveEramRouteLines(Topic, List<EramRouteLineDto>)`
- [ ] `DeleteEramRouteLines(Topic, List<string>)`
- [ ] `ReceiveEramCrrGroups(Topic, List<EramCrrGroupDto>)`
- [ ] `DeleteEramCrrGroups(Topic, List<string>)`
- [ ] `ReceiveEramSectorConfiguration(Topic, EramSectorConfigurationDto)`
- [ ] `ReceiveEramShortTermConflicts(Topic, List<EramShortTermConflictDto>)`
- [ ] `DeleteEramShortTermConflicts(Topic, List<string>)`

### ASDEX

- [x] `ReceiveAsdexTargets(Topic, List<AsdexTargetDto>)` — per-tick broadcast + initial data
- [x] `DeleteAsdexTargets(Topic, List<string>)` — aircraft removal
- [x] `ReceiveAsdexTracks(Topic, List<AsdexTrackDto>)` — per-tick broadcast + initial data; VoiceType from AircraftState
- [x] `DeleteAsdexTracks(Topic, List<string>)` — aircraft removal
- [ ] `ReceiveAsdexSafetyLogicConfiguration(Topic, AsdexSafetyLogicConfigurationDto)`
- [ ] `ReceiveAsdexHoldBars(Topic, List<AsdexHoldBarDto>)`
- [ ] `ReceiveAsdexAlerts(Topic, List<AsdexAlertDto>)`
- [ ] `DeleteAsdexAlerts(Topic, List<string>)`
- [ ] `ReceiveAsdexTempDatas(Topic, List<AsdexTempDataDto>)`
- [ ] `DeleteAsdexTempDatas(Topic, List<string>)`
- [ ] `ReceiveAsdexTempDataPresets(Topic, List<AsdexTempDataPresetDto>)`
- [ ] `DeleteAsdexTempDataPresets(Topic, List<string>)`

### Tower Cab

- [x] `ReceiveTowerCabAircrafts(Topic, List<TowerCabAircraftDto>)` — per-tick broadcast + initial data; VoiceType from AircraftState
- [x] `DeleteTowerCabAircrafts(Topic, List<string>)` — aircraft removal

### Ground Targets

- [ ] `ReceiveGroundTargets(Topic, List<GroundTargetDto>)`
- [ ] `DeleteGroundTargets(Topic, List<string>)`

### Flight Strips

- [ ] `ReceiveStripItems(Topic, List<StripItemDto>)`
- [ ] `ReceiveFlightStripsState(Topic, FlightStripsStateDto)`

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

- [ ] `ReceiveNexradData(Topic, NexradDataDto)`

---

## Priority Roadmap

### Completed

- [x] Session lifecycle (EndSession, nil-ack stubs for JoinSession/LeaveSession/GetSessions)
- [x] Nil-ack stubs (SetFrequencies, SetControllerInfo, RequestControllerInfo, etc.)
- [x] Simple data handlers (GetRealName, GetVfrFlightPlanRemarks, SetVoiceType, TdlsDump, GenerateFrd)
- [x] Flight plan mutations (Create/Amend, RequestNewBeaconCode, SendClearance, HoldAnnotations)
- [x] Messaging pipeline (6 send handlers + CRC-to-CRC routing)
- [x] ASDEX management (13 handlers + room state)
- [x] Flight strips (7 handlers + room state)
- [x] Auto-track airports + secondary positions (6 handlers)
- [x] AircraftState fields (VoiceType, TdlsDumped, HoldAnnotation*, Clearance*)
- [x] BeaconCodePool (room-scoped octal code assignment)
- [x] DtoConverter updates (VoiceType, TdlsDumped, Clearance, HoldAnnotations)

### ERAM Milestone

- [ ] `ProcessEramMessage` — QN, QF, QL, RD, QU commands
- [ ] `SetEramSectorConfiguration`
- [ ] `ReceiveEramSectorConfiguration` broadcast

### Future

- [ ] STARS line numbers broadcast
- [ ] Short-term conflict detection (STARS + ERAM)
- [ ] ASDEX broadcast integration (temp data, presets, safety config)
- [ ] Flight strips broadcast integration
- [ ] Session lifecycle pushes (HandleSessionStarted, HandleSessionEnded, HandleFsdConnectionStateChanged)
- [ ] DeleteOpenPositions broadcast on disconnect

---

## Summary

| Category | Implemented | Stubbed | Missing |
|----------|:-----------:|:-------:|:-------:|
| Session management | 5 | 3 | 0 |
| Position management | 5 | 2 | 1 |
| Subscriptions | 2 | 0 | 0 |
| STARS commands | 1 (rich) | 0 | 0 |
| ERAM commands | 0 | 0 | 6 |
| Flight plan ops | 9 | 0 | 0 |
| Messaging | 6 | 0 | 0 |
| ASDEX management | 13 | 0 | 0 |
| Flight strips | 7 | 0 | 0 |
| Info requests | 1 | 4 | 0 |
| Navigation | 1 | 0 | 0 |
| **Client→Server total** | **50** | **9** | **7** |
| Server→Client broadcasts | 24 | 0 | 30+ |

**ProcessStarsCommand detail:** IC, TC, Handoff, Implied (9 sub-ops), MultiFunc (CON/DECON), Coordination (stub)
