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
- [ ] `EndSession()` — should unregister position from `PositionRegistry` and broadcast updated controller list
- [ ] `GetSessions()` — returns list of active sessions
- [ ] `JoinSession(JoinSessionDto)` — join existing session (observer mode)
- [ ] `LeaveSession()` — leave joined session

### Position Management

- [ ] `SetFrequencies(SetFrequenciesDto)` — CRC sends this after StartSession; safe to stub
- [ ] `SetControllerInfo(string)` — safe to stub
- [ ] `OpenSecondaryPosition(SecondaryPositionDto)` — dual-console; not needed for training
- [ ] `CloseSecondaryPosition(SecondaryPositionDto)` — not needed for training
- [ ] `ActivateSecondaryPosition(SecondaryPositionDto)` — not needed for training
- [ ] `DeactivateSecondaryPosition(SecondaryPositionDto)` — not needed for training
- [ ] `ChangeActiveEramPosition(ChangeActivePositionDto)` — not needed for training
- [ ] `ChangeActiveStarsPosition(ChangeActivePositionDto)` — not needed for training
- [ ] `UpdateAutoTrackAirports(UpdateAutoTrackAirportsDto)` — auto-track config from CRC

### Topic Subscriptions

- [x] `Subscribe(Topic)` — adds topic, returns initial data via `CrcBroadcastService`
- [x] `Unsubscribe(Topic)` — removes topic

### STARS Commands

- [x] `ProcessStarsCommand(ProcessStarsCommandDto)` — IC, TC, Handoff, Implied (accept/ack pointout)

### ERAM Commands

- [ ] `ProcessEramMessage(ProcessEramMessageDto)` — QN (leader line), QF (print FP), QL (quick look), RD (route display), QU (projected route). Planned for ERAM milestone.
- [ ] `SetEramSectorConfiguration(EramSectorConfigurationDto)` — sector config updates
- [ ] `ToggleEramDwellLock(aircraftId)` — dwell lock on target
- [ ] `ClearEramPointout(aircraftId, pointoutId)` — clear pointout
- [ ] `ClearOrDeleteEramCrrGroup(groupLabel)` — CRR groups
- [ ] `SetEramCrrGroupColor(groupLabel, CrrColor)` — CRR group colors

### Flight Plan Operations

- [ ] `CreateFlightPlan(CreateOrAmendFlightPlanDto)` — CRC/RPO creates a new flight plan
- [ ] `AmendFlightPlan(CreateOrAmendFlightPlanDto)` — CRC/RPO amends existing flight plan
- [ ] `RequestNewBeaconCode(aircraftId)` — generate unique beacon code
- [ ] `SendClearance(aircraftId, ClearanceDto)` — issue clearance
- [ ] `TdlsDump(aircraftId)` — TDLS data dump
- [ ] `SetHoldAnnotations(aircraftId, HoldAnnotationsDto)` — hold annotations
- [ ] `DeleteHoldAnnotations(aircraftId)` — remove hold annotations
- [ ] `GetVfrFlightPlanRemarks(aircraftId)` — VFR remarks lookup
- [ ] `SetVoiceType(aircraftId, VoiceType)` — voice type

### Messaging

- [ ] `SendRadioMessage(message)` — SAY command (radio transmission)
- [ ] `SendPrivateMessage(to, message)` — DM to controller
- [ ] `SendAtcMessage(message)` — ATC message
- [ ] `SendBroadcastMessage(message)` — broadcast to all
- [ ] `SendVnasBroadcastMessage(message)` — vNAS broadcast
- [ ] `SendWallopMessage(message)` — WALLOP

### ASDEX

- [ ] `EditAsdexDbFields(EditAsdexDbFieldsDto)`
- [ ] `TagAsdexTarget(facilityId, aircraftId)`
- [ ] `TerminateAsdexTrack(facilityId, trackId)`
- [ ] `SuspendAsdexTrack(facilityId, trackId)`
- [ ] `UnsuspendAsdexTrack(facilityId, trackId)`
- [ ] `InhibitAsdexAlerts(facilityId, aircraftId)`
- [ ] `EnableAllAsdexAlerts(facilityId)`
- [ ] `AddAsdexTempData(facilityId, AsdexTempDataDto)`
- [ ] `DeleteAsdexTempData(facilityId, tempDataId)`
- [ ] `SaveAsdexTempDataPreset(facilityId, AsdexTempDataPresetDto)`
- [ ] `ToggleAsdexTempDataPreset(facilityId, tempDataPresetId)`
- [ ] `DeleteAsdexTempDataPreset(facilityId, tempDataPresetId)`
- [ ] `UpdateAsdexSafetyLogicConfiguration(facilityId, AsdexSafetyLogicConfigurationDto)`

### Flight Strips

- [ ] `CreateStripItem(CreateStripItemDto)`
- [ ] `UpdateStripItem(UpdateStripItemDto)`
- [ ] `DeleteStripItem(DeleteStripItemDto)`
- [ ] `MoveStripItem(MoveStripItemDto)`
- [ ] `RequestFullFlightStripsState(facilityId)`
- [ ] `RequestFlightStrip(facilityId, aircraftId)`
- [ ] `RequestBlankStrip(facilityId)`

### Information Requests

- [ ] `GetRealName(callsign)` — safe to stub
- [ ] `RequestControllerInfo(callsign)` — safe to stub
- [ ] `RequestClientInfo(callsign)` — safe to stub
- [ ] `RequestClientVersion(callsign)` — safe to stub
- [ ] `KillClient(victim, message?)` — safe to stub

### Navigation

- [ ] `GenerateFrd(GeoPoint)` — generate FRD string from coordinates

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

- [x] `ReceiveFlightPlans(Topic, List<FlightPlanDto>)` — per-tick broadcast + initial data
- [x] `DeleteFlightPlans(Topic, List<string>)` — aircraft removal

### STARS

- [x] `ReceiveStarsTracks(Topic, List<StarsTrackDto>)` — per-tick broadcast + initial data
- [x] `DeleteStarsTracks(Topic, List<string>)` — aircraft removal / coast-out
- [ ] `ReceiveStarsLineNumbers(Topic, List<StarsLineNumberDto>)` — subscribed but not streamed
- [ ] `DeleteStarsLineNumbers(Topic, List<string>)`
- [ ] `ReceiveStarsShortTermConflicts(Topic, List<StarsShortTermConflictDto>)`
- [ ] `DeleteStarsShortTermConflicts(Topic, List<string>)`
- [ ] `ReceiveStarsReadoutArea(StarsReadoutAreaDto)`
- [ ] `ReceiveStarsCoordinationLists(Topic, List<StarsCoordinationListDto>)`
- [ ] `ReceiveStarsConsolidationItems(Topic, List<StarsConsolidationItemDto>)`

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
- [x] `ReceiveAsdexTracks(Topic, List<AsdexTrackDto>)` — per-tick broadcast + initial data
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

- [x] `ReceiveTowerCabAircrafts(Topic, List<TowerCabAircraftDto>)` — per-tick broadcast + initial data
- [x] `DeleteTowerCabAircrafts(Topic, List<string>)` — aircraft removal

### Ground Targets

- [ ] `ReceiveGroundTargets(Topic, List<GroundTargetDto>)`
- [ ] `DeleteGroundTargets(Topic, List<string>)`

### Flight Strips

- [ ] `ReceiveStripItems(Topic, List<StripItemDto>)`
- [ ] `ReceiveFlightStripsState(Topic, FlightStripsStateDto)`

### Messaging

- [ ] `ReceiveServerMessage(string)`
- [ ] `ReceiveServerError(string)`
- [ ] `ReceivePrivateMessage(TextMessageDto)`
- [ ] `ReceiveAtcMessage(TextMessageDto)`
- [ ] `ReceiveWallopMessage(TextMessageDto)`
- [ ] `ReceiveBroadcastMessage(TextMessageDto)`
- [ ] `ReceiveVnasBroadcastMessage(TextMessageDto)`
- [ ] `ReceiveRadioMessage(RadioMessageDto)`

### NEXRAD

- [ ] `ReceiveNexradData(Topic, NexradDataDto)`

---

## Priority Roadmap

### Now (quick fixes)

- [ ] `EndSession` — unregister position, broadcast updated controller list
- [ ] `SetFrequencies` — explicit nil-ack (CRC sends this on every connect)

### Next (training-critical)

- [ ] `CreateFlightPlan` / `AmendFlightPlan` — RPO and student flight plan management
- [ ] `SendRadioMessage` — SAY command (radio transmissions)
- [ ] `RequestNewBeaconCode` — beacon code generation

### ERAM Milestone

- [ ] `ProcessEramMessage` — QN, QF, QL, RD, QU commands
- [ ] `SetEramSectorConfiguration`
- [ ] `ReceiveEramSectorConfiguration` broadcast

### Future

- [ ] Flight strips pipeline (Create/Update/Delete/Move + broadcast)
- [ ] STARS line numbers broadcast
- [ ] Short-term conflict detection (STARS + ERAM)
- [ ] ASDEX management methods
- [ ] Messaging pipeline (private, ATC, broadcast, WALLOP)
- [ ] Secondary positions / position switching

---

## Summary

| Category | Implemented | Missing |
|----------|:-----------:|:-------:|
| Session management | 4 | 4 |
| Position management | 0 | 9 |
| Subscriptions | 2 | 0 |
| STARS commands | 1 | 0 |
| ERAM commands | 0 | 6 |
| Flight plan ops | 0 | 9 |
| Messaging | 0 | 6 |
| ASDEX management | 0 | 13 |
| Flight strips | 0 | 7 |
| Info requests | 0 | 5 |
| Navigation | 0 | 1 |
| **Client→Server total** | **7** | **60** |
| Server→Client broadcasts | 14 | 40+ |
