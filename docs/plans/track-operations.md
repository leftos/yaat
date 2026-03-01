# Milestone 4: STARS Track Operations

## Context

M0–M3 are complete (proof of concept, scenario loading, tower, ground). M5 (approach control) is planned next. Currently, all aircraft are untracked from CRC's perspective — `StarsTrackDto.Owner`, `HandoffPeer`, and `Pointout` are always null. The RPO controls aircraft purely through the command bar with no track ownership model.

M4 adds STARS track ownership, handoffs, point-outs, and related operations. This makes aircraft appear as tracked/owned targets in CRC, enables handoff flows between positions, and adds all documented ATCTrainer/VICE track commands. ERAM track operations are deferred to a later milestone.

### Design Decisions

**Both RPO and CRC can initiate track operations.** The RPO uses typed commands (HO, ACCEPT, TRACK, etc.) via the command bar. CRC clients send `ProcessStarsCommand` with `StarsCommandType.Handoff/InitiateControl/TerminateControl`. Both paths mutate the same track state on `AircraftState`.

**CRC clients only for atc[] positions.** The scenario's `atc[]` entries declare which positions exist, but don't simulate AI controllers. A real CRC client must log into each position for human-driven handoffs. A single auto-accept timer (configurable in YAAT Client Settings) auto-accepts handoffs to unattended positions after N seconds.

**STARS only.** Track ownership uses STARS types (`TrackOwner` with `TrackOwnerType.Stars`, `Tcp`, `StarsPointout`). ERAM track operations and `ProcessEramMessage` handling are deferred.

**Track state lives in Yaat.Sim.** `AircraftState` gets ownership fields (`Owner`, `HandoffPeer`, etc.) so phases, triggers, and command dispatch can reference ownership. The server populates CRC DTOs from these fields.

**RPO identity from ARTCC config.** The scenario's `studentPositionId` (a vNAS ULID) is resolved against `ArtccConfigService` to build the RPO's `TrackOwner` (callsign, facilityId, subset, sectorId).

**RPO can act as any position.** The RPO defaults to the student position but can emulate any `atc[]` position using the `AS` command:
- **Standalone** `AS 2B` — sets the RPO's active position persistently (until changed or reset)
- **Prefix** `N135BS AS 2B HO 3Y` — acts as 2B for this one command only (no persistent change)
- Resolution order: per-command `AS` prefix > persistent active position > student position default
- The server tracks `ActivePosition` per client connection on the scenario session

**TCP-based handoff addressing.** The RPO types `HO 2B` (subset 2, sector B) to handoff to Boulder approach. This matches real STARS keyboard entry. ProcessStarsCommand from CRC uses the same TCP resolution via `ParameterString`.

**Full vNAS protocol for ProcessStarsCommand.** Parse `ProcessStarsCommandDto` exactly as vNAS defines it. This maximizes CRC compatibility.

**Spawn with owner + delayed handoff initiation.** Aircraft with `autoTrackConditions` spawn with `Owner` set to the specified position. `handoffDelay` controls when the handoff to the student is **initiated** (data block starts flashing): a value of 0 means immediate, 240 means 4 minutes after spawn. This paces scenario difficulty — the student receives aircraft gradually. The auto-accept timer (from YAAT Client Settings) is separate and controls how long an unattended position waits before auto-accepting an inbound handoff. If `handoffDelay` is absent (field not present in JSON, deserialized as null), the aircraft is owned by the ATC position with no handoff to the student — it must be manually handed off or tracked.

**StarsConsolidation required.** Without `StarsConsolidationItemDto`, CRC cannot correctly color-code owned vs. other tracks. Include this topic.

### Scope — All Documented Commands

| CanonicalCommandType | ATCTrainer | VICE | Args | Effect |
|---|---|---|---|---|
| SetActivePosition | AS | AS | TCP code | Set RPO active position (standalone) or per-command override (prefix) |
| TrackAircraft | TRACK | — | none | Set Owner to RPO (initiate control) |
| DropTrack | DROP | — | none | Clear Owner (terminate control) |
| InitiateHandoff | HO | — | TCP code | Set HandoffPeer to target |
| AcceptHandoff | ACCEPT, A | — | none | Accept pending inbound handoff |
| CancelHandoff | CANCEL | — | none | Retract pending outbound handoff |
| AcceptAllHandoffs | ACCEPTALL | — | none | Accept all pending inbound handoffs (global) |
| InitiateHandoffAll | HOALL | — | TCP code | Handoff all RPO-owned aircraft to target |
| PointOut | PO | — | TCP code | Point out to target TCP |
| Acknowledge | OK | — | none | Acknowledge pointout/coordination |
| Annotate | ANNOTATE, AN, BOX | — | none | Toggle annotation flag on track |
| Scratchpad | SCRATCHPAD, SP | — | text | Set scratchpad text |
| TemporaryAltitude | TEMPALT, TA, TEMP, QQ | — | altitude | Set temporary altitude |
| Cruise | CRUISE, QZ | — | altitude | Set cruise altitude |
| FlightStrip | STRIP | — | TBD | Flight strip management (deferred to later) |
| OnHandoff | ONHO, ONH | — | none | Mark aircraft as on-handoff status |
| FrequencyChange | — | FC | none | Approve frequency change |
| ContactTcp | — | CT | TCP code | Tell pilot to contact TCP |
| ContactTower | — | TO | none | Tell pilot to contact tower |

Commands whose effect is pilot-side (FC, CT, TO) set a flag on `AircraftState` and generate a terminal notification. Pilot AI (M8+) will consume these flags. STRIP is deferred — flight strip management is a separate feature.

### Work Spans Three Codebases

- **Yaat.Sim** (`X:\dev\yaat\src\Yaat.Sim\`) — Track ownership types, AircraftState fields, new CanonicalCommandType values, parsed command records, dispatch logic
- **Yaat.Client** (`X:\dev\yaat\src\Yaat.Client\`) — ATCTrainer/VICE patterns, CommandMetadata, client UI, settings
- **yaat-server** (`X:\dev\yaat-server\src\Yaat.Server\`) — Position registry, scenario loading, CRC DTO population, ProcessStarsCommand handling, StarsConsolidation, auto-accept timer

---

## Chunk 1: Track Ownership Data Model (Yaat.Sim)

Foundation types and AircraftState fields. No server or client changes yet.

- [x] Create `TrackOwner.cs` in Yaat.Sim root:
  - `record TrackOwner(string Callsign, string? FacilityId, int? Subset, string? SectorId, TrackOwnerType OwnerType)`
  - Factory methods: `CreateStars(callsign, facilityId, subset, sectorId)`, `CreateNonNas(callsign)`
  - `bool IsNasPosition => OwnerType is Stars or Eram`
- [x] Create `TrackOwnerType.cs` enum: `Other`, `Eram`, `Stars`, `Caats`, `Atop`
- [x] Create `Tcp.cs`:
  - `record Tcp(int Subset, string SectorId, string Id, string? ParentTcpId)`
  - Equality by `Id` only
  - `ToString()` returns `$"{Subset}{SectorId}"`
- [x] Create `StarsPointout.cs`:
  - `class StarsPointout { Tcp Recipient, Tcp Sender, StarsPointoutStatus Status }`
  - `bool IsPending`, `bool IsAccepted`, `bool IsRejected`
- [x] Create `StarsPointoutStatus.cs` enum: `Pending`, `Accepted`, `Rejected`
- [x] Add to `AircraftState.cs`:
  - `TrackOwner? Owner { get; set; }` — current controlling position
  - `TrackOwner? HandoffPeer { get; set; }` — pending handoff target
  - `TrackOwner? HandoffRedirectedBy { get; set; }` — who redirected the handoff
  - `StarsPointout? Pointout { get; set; }` — active STARS pointout
  - `string? Scratchpad1 { get; set; }` — scratchpad field 1
  - `string? Scratchpad2 { get; set; }` — scratchpad field 2
  - `int? TemporaryAltitude { get; set; }` — temporary altitude (hundreds of feet)
  - `bool IsAnnotated { get; set; }` — annotation/box flag
  - `bool FrequencyChangeApproved { get; set; }` — FC flag for pilot AI
  - `string? ContactPosition { get; set; }` — position callsign pilot should contact (CT/TO)
  - `bool OnHandoff { get; set; }` — on-handoff status flag
  - `DateTime? HandoffInitiatedAt { get; set; }` — when current handoff was initiated (for auto-accept timing)
  - `int? AssignedAltitude { get; set; }` — assigned altitude from autoTrackConditions.clearedAltitude (hundreds of feet)

---

## Chunk 2: RPO Track Commands — Types & Parsing (Yaat.Sim + Yaat.Client)

Add all new command types, parsed command records, ATCTrainer/VICE patterns, and metadata. No server-side handling yet.

- [x] Add to `CanonicalCommandType.cs` (new section `// Track operations`):
  - `SetActivePosition`
  - `TrackAircraft`, `DropTrack`, `InitiateHandoff`, `AcceptHandoff`, `CancelHandoff`
  - `AcceptAllHandoffs`, `InitiateHandoffAll`
  - `PointOut`, `Acknowledge`, `Annotate`
  - `Scratchpad`, `TemporaryAltitude`, `Cruise`
  - `OnHandoff`, `FrequencyChange`, `ContactTcp`, `ContactTower`
- [x] Add to `ParsedCommand.cs` (new records):
  - `record TrackAircraftCommand : ParsedCommand`
  - `record DropTrackCommand : ParsedCommand`
  - `record InitiateHandoffCommand(string TcpCode) : ParsedCommand`
  - `record AcceptHandoffCommand : ParsedCommand`
  - `record CancelHandoffCommand : ParsedCommand`
  - `record AcceptAllHandoffsCommand : ParsedCommand`
  - `record InitiateHandoffAllCommand(string TcpCode) : ParsedCommand`
  - `record PointOutCommand(string TcpCode) : ParsedCommand`
  - `record AcknowledgeCommand : ParsedCommand`
  - `record AnnotateCommand : ParsedCommand`
  - `record ScratchpadCommand(string Text) : ParsedCommand`
  - `record TemporaryAltitudeCommand(int AltitudeHundreds) : ParsedCommand`
  - `record CruiseCommand(int AltitudeHundreds) : ParsedCommand`
  - `record OnHandoffCommand : ParsedCommand`
  - `record FrequencyChangeCommand : ParsedCommand`
  - `record ContactTcpCommand(string TcpCode) : ParsedCommand`
  - `record ContactTowerCommand : ParsedCommand`
- [x] Add to `AtcTrainerPreset.cs`:
  - `TRACK` → TrackAircraft (no args)
  - `DROP` → DropTrack (no args)
  - `HO {tcp}` → InitiateHandoff (1 arg)
  - `ACCEPT` / `A` → AcceptHandoff (no args)
  - `CANCEL` → CancelHandoff (no args)
  - `ACCEPTALL` → AcceptAllHandoffs (no args, IsGlobal)
  - `HOALL {tcp}` → InitiateHandoffAll (1 arg, IsGlobal)
  - `PO {tcp}` → PointOut (1 arg)
  - `OK` → Acknowledge (no args)
  - `ANNOTATE` / `AN` / `BOX` → Annotate (no args)
  - `SCRATCHPAD {text}` / `SP {text}` → Scratchpad (1 arg)
  - `TEMPALT {alt}` / `TA {alt}` / `TEMP {alt}` / `QQ {alt}` → TemporaryAltitude (1 arg)
  - `CRUISE {alt}` / `QZ {alt}` → Cruise (1 arg)
  - `ONHO` / `ONH` → OnHandoff (no args)
- [x] Add to `VicePreset.cs`:
  - `FC` → FrequencyChange (no args)
  - `CT{tcp}` → ContactTcp (1 arg, concatenated)
  - `TO` → ContactTower (no args)
  - Plus VICE equivalents for all ATCTrainer commands above (where applicable; VICE may not have all)
- [x] Add to `CommandMetadata.cs` `AllCommands` list:
  - One `CommandInfo` per new CanonicalCommandType (label, sample arg, IsGlobal for ACCEPTALL/HOALL)
- [x] Verify `CommandSchemeCompletenessTests` pass (`dotnet test`)
- [x] Update `docs/command-aliases-reference.md`:
  - Move commands from "not implemented" section to their proper section
  - Add VICE column entries

---

## Chunk 3: Position Infrastructure (yaat-server)

Resolve vNAS positions to TrackOwner instances. Build a position registry to track which CRC clients and the RPO map to which positions.

- [ ] Extend `ArtccConfigService`:
  - `TrackOwner? ResolvePosition(string artccId, string positionId)` — resolves a vNAS ULID to a `TrackOwner` with callsign, facilityId, subset (from StarsConfiguration), sectorId
  - `Tcp? GetTcpForPosition(string artccId, string positionId)` — returns the TCP linked to a position via `StarsConfiguration.TcpId`
  - `TrackOwner? ResolveTcpCode(string artccId, string facilityId, string tcpCode)` — resolves a TCP code like "2B" (subset=2, sectorId="B") to a `TrackOwner` by finding the matching TCP and its owning position
  - `List<Tcp> GetFacilityTcps(string artccId, string facilityId)` — returns all TCPs for a facility (for StarsConsolidation)
  - `Tcp? FindTcpByCode(string artccId, string facilityId, int subset, string sectorId)` — find TCP by subset+sectorId
- [ ] Create `PositionRegistry.cs` (singleton service in yaat-server):
  - `RegisterCrcPosition(string connectionId, string artccId, string positionId)` — called on CRC `StartSession`
  - `UnregisterCrcPosition(string connectionId)` — called on CRC disconnect
  - `RegisterTrainingPosition(string scenarioId, string artccId, string positionId)` — called on scenario load (for RPO)
  - `UnregisterTrainingPosition(string scenarioId)` — called on scenario leave
  - `TrackOwner? GetPositionOwner(string positionId)` — resolve ULID to TrackOwner
  - `bool IsPositionAttended(Tcp tcp)` — true if a CRC client is logged into this TCP
  - `TrackOwner? GetStudentPosition(string scenarioId)` — returns the RPO's TrackOwner
  - Thread-safe (ConcurrentDictionary)
- [ ] Wire `PositionRegistry` into DI in `Program.cs`
- [ ] Update `CrcClientState.HandleStartSession()` to call `PositionRegistry.RegisterCrcPosition()`
- [ ] Update CRC disconnect to call `PositionRegistry.UnregisterCrcPosition()`

---

## Chunk 4: Scenario Track Initialization (yaat-server)

Process scenario JSON fields that were previously ignored: `atc[]`, `studentPositionId`, `autoTrackConditions`.

- [ ] Fix `AutoTrackConditions` model in `ScenarioModels.cs` (yaat-server):
  - Add `string? ScratchPad` property (missing — real scenarios use it extensively)
  - Change `HandoffDelay` from `int` to `int?` (null = no handoff to student; 0 = immediate handoff; >0 = delayed handoff)
- [ ] Update `ScenarioLoader.Load()`:
  - Resolve `scenario.StudentPositionId` via `ArtccConfigService.ResolvePosition()` → store on `ScenarioSession.StudentPosition` (new field, type `TrackOwner?`)
  - Register student position via `PositionRegistry.RegisterTrainingPosition()`
  - For each `scenario.Atc[]` entry:
    - Resolve `positionId` via `ArtccConfigService.ResolvePosition()` → store as `ScenarioSession.AtcPositions` (new field, `List<ResolvedAtcPosition>`)
    - `ResolvedAtcPosition` captures: `ScenarioAtc` source data + resolved `TrackOwner` + resolved `Tcp`
  - Collect distinct `artccId` values from `atc[]` entries; call `ArtccConfigService.EnsureLoadedAsync()` for each (scenarios may reference cross-ARTCC positions, e.g., ZOA + ZLA)
- [ ] Update `ScenarioLoader.LoadAircraft()`:
  - If `autoTrackConditions` is present:
    - Resolve `autoTrackConditions.PositionId` → `TrackOwner`
    - Set `AircraftState.Owner` to that TrackOwner
    - If `autoTrackConditions.HandoffDelay` is not null AND `scenario.StudentPositionId` is set:
      - If `HandoffDelay == 0`: immediately set `AircraftState.HandoffPeer` to the student's TrackOwner and set `HandoffInitiatedAt`
      - If `HandoffDelay > 0`: queue a delayed handoff initiation (fire after N seconds of scenario time)
    - If `autoTrackConditions.HandoffDelay` is null: aircraft stays owned by ATC position with no automatic handoff to student
    - If `autoTrackConditions.ScratchPad` is set: store on `AircraftState.Scratchpad1`
    - If `autoTrackConditions.ClearedAltitude` is set: parse and store on `AircraftState.AssignedAltitude` (string → int hundreds of feet)
  - If `autoTrackConditions` is absent: aircraft spawns unowned (`Owner = null`)
- [ ] Update `ScenarioSession`:
  - Add `TrackOwner? StudentPosition` property
  - Add `List<ResolvedAtcPosition> AtcPositions` property
  - Add `TimeSpan AutoAcceptDelay` property (received from client)
  - Add `ConcurrentDictionary<string, TrackOwner> ActivePositionByConnection` — per-client active position override (set by standalone `AS` command, defaults to StudentPosition)
- [ ] Auto-accept timer logic in `SimulationHostedService`:
  - Each tick, check all aircraft with non-null `HandoffPeer`:
    - If `HandoffPeer` target position is unattended (`!PositionRegistry.IsPositionAttended(tcp)`)
    - And elapsed time since handoff initiation >= `ScenarioSession.AutoAcceptDelay`
    - Then: complete the handoff (set `Owner = HandoffPeer`, clear `HandoffPeer`, clear `HandoffInitiatedAt`)
  - `HandoffInitiatedAt` is already on `AircraftState` (Chunk 1)
- [ ] Update `ScenarioSession` leave/cleanup to call `PositionRegistry.UnregisterTrainingPosition()`
- [ ] Add training hub method: `SetAutoAcceptDelay(int seconds)` — stores on the active scenario session
- [ ] `autoTrackAirportIds` on `ScenarioAtc`: when an aircraft spawns from a listed airport with no explicit `autoTrackConditions`, auto-set `Owner` to that ATC position's TrackOwner
  - Match uses FAA airport IDs (e.g., "SJC", "OAK") against `AircraftState.Departure`; if departure is ICAO format ("KSJC"), strip the "K" prefix for matching
- [ ] Handle null `scenario.PrimaryAirportId` gracefully — some scenarios don't set it

---

## Chunk 5: CRC Track State Broadcasting (yaat-server)

Populate ownership fields in CRC DTOs. Implement StarsConsolidation topic.

- [ ] Update `DtoConverter.ToStarsTrack()`:
  - `Owner` = `ac.Owner` mapped to CRC `TrackOwner` DTO (MessagePack)
  - `HandoffPeer` = `ac.HandoffPeer` mapped
  - `HandoffRedirectedBy` = `ac.HandoffRedirectedBy` mapped
  - `Pointout` = `ac.Pointout` mapped to CRC `StarsPointout` DTO
  - `Scratchpad1` = `ac.Scratchpad1`
  - `Scratchpad2` = `ac.Scratchpad2`
  - Note: CRC `TrackOwner` DTO is MessagePack `[Key(N)]`; Yaat.Sim `TrackOwner` is a plain record. Need a mapping helper.
- [ ] Update `DtoConverter.ToFlightPlan()`:
  - Set `AssignedAltitude` from `ac.TemporaryAltitude` if set, else from `ac.Targets.TargetAltitude`
- [ ] Update `DtoConverter.ToEramDataBlock()`:
  - `Format` should now respect ownership: `Fdb` if owned by the subscribing sector or handed-off to it, `PairedLdb` if owned by another sector, `UnpairedLdb` if unowned
  - This requires per-client context in the converter (which sector is this CRC client on?)
- [ ] Update training `AircraftStateDto` and `AircraftDto`:
  - Add: `Owner` (callsign string or null), `OwnerSectorCode` (e.g., "2B" or null), `HandoffPeer` (callsign string or null), `HandoffPeerSectorCode`, `PointoutStatus` (string or null), `Scratchpad1`, `Scratchpad2`, `TemporaryAltitude`, `IsAnnotated`
- [ ] Implement StarsConsolidation topic (`ReceiveStarsConsolidation`):
  - When a CRC client subscribes to `StarsConsolidation(facilityId)`:
    - Build `StarsConsolidationItemDto` list from ARTCC config TCP tree
    - For each TCP: determine if it's owned by the subscribing position (based on `PositionRegistry`)
    - Send initial data dump via `ReceiveStarsConsolidation`
  - On position change (CRC client joins/leaves): re-broadcast consolidation to affected clients
- [ ] Ensure CRC `TrackOwner` DTO in `CrcDtos.cs` matches vNAS MessagePack shape:
  - `[Key(0)] string Callsign`
  - `[Key(1)] string? FacilityId`
  - `[Key(2)] int? Subset`
  - `[Key(3)] string? SectorId`
  - `[Key(4)] string OwnerType` (string enum: "Other", "Eram", "Stars", etc.)
- [ ] Ensure CRC `StarsPointout` DTO in `CrcDtos.cs`:
  - `[Key(0)] CrcTcp Recipient`
  - `[Key(1)] CrcTcp Sender`
  - `[Key(2)] string Status` (string enum: "Pending", "Accepted", "Rejected")
- [ ] Ensure CRC `Tcp` DTO (`CrcTcp`) in `CrcDtos.cs`:
  - `[Key(0)] int Subset`
  - `[Key(1)] string SectorId`
  - `[Key(2)] string Id`
  - `[Key(3)] string? ParentTcpId`

---

## Chunk 6: RPO Track Command Handling (yaat-server)

Server-side dispatch for all track commands received from the training hub.

- [ ] Add track command handling in `CommandParser.cs` / `ServerCommands.cs`:
  - Track commands are NOT dispatched through `CommandDispatcher` (they don't build `CommandBlocks`). Instead, the server handles them directly in `SimulationHostedService.HandleTrackCommand()` (new method).
  - Pattern: `SendCommand("callsign", "TRACK")` → server detects track command type → calls `HandleTrackCommand()` instead of `CommandDispatcher`
- [ ] `AS` prefix and identity resolution:
  - Before dispatching any track command, extract `AS {tcp}` prefix from the canonical string if present
  - Resolve effective identity: per-command AS prefix > session's `ActivePositionByConnection[connectionId]` > `ScenarioSession.StudentPosition`
  - **AS (standalone)**: `SendCommand(null, "AS 2B")` → resolve TCP, store in `ScenarioSession.ActivePositionByConnection[connectionId]`. Return success with terminal message "Now acting as {position callsign}"
  - **AS (prefix)**: `SendCommand("N135BS", "AS 2B HO 3Y")` → strip `AS 2B`, resolve identity to 2B, dispatch `HO 3Y` with that identity. Do NOT update the persistent active position.
- [ ] `HandleTrackCommand()` implementations:
  - All ownership-sensitive commands use `effectivePosition` (resolved from AS prefix > active position > student position)
  - **TRACK**: Validate aircraft has no Owner. Set `Owner = effectivePosition`. Return success/error.
  - **DROP**: Validate aircraft is owned by effectivePosition. Clear `Owner`. Clear `HandoffPeer` if set. Return success/error.
  - **HO {tcp}**: Validate aircraft is owned by effectivePosition. Resolve TCP code → `TrackOwner` via `ArtccConfigService.ResolveTcpCode()`. Set `HandoffPeer`. Set `HandoffInitiatedAt`. Return success/error.
  - **ACCEPT**: Find pending inbound handoff (aircraft where `HandoffPeer` matches effectivePosition). Set `Owner = effectivePosition`. Clear `HandoffPeer`. Clear `HandoffInitiatedAt`. Return success/error.
  - **CANCEL**: Validate aircraft has pending outbound handoff from effectivePosition. Clear `HandoffPeer`. Clear `HandoffInitiatedAt`. Return success/error.
  - **ACCEPTALL**: For all aircraft where `HandoffPeer` matches effectivePosition: accept each. Return count.
  - **HOALL {tcp}**: For all aircraft where `Owner` matches effectivePosition: initiate handoff to TCP. Return count.
  - **PO {tcp}**: Validate aircraft is owned by effectivePosition. Resolve TCP → `Tcp`. Set `Pointout = new StarsPointout(targetTcp, effectiveTcp, Pending)`. Return success.
  - **OK**: Validate aircraft has a pending pointout TO effectivePosition. Set `Pointout.Status = Accepted`. Return success.
  - **ANNOTATE/AN/BOX**: Toggle `IsAnnotated` flag. Return success.
  - **SP {text}**: Set `Scratchpad1 = text`. Return success.
  - **TA/QQ {alt}**: Parse altitude → hundreds. Set `TemporaryAltitude`. Return success.
  - **CRUISE/QZ {alt}**: Parse altitude → feet. Set `CruiseAltitude`. Return success.
  - **ONHO/ONH**: Toggle `OnHandoff` flag. Return success.
  - **FC**: Set `FrequencyChangeApproved = true`. Generate terminal broadcast "frequency change approved". Return success.
  - **CT {tcp}**: Resolve TCP → position callsign. Set `ContactPosition`. Generate terminal broadcast "contact {position} {frequency}". Return success.
  - **TO**: Resolve tower position from scenario. Set `ContactPosition`. Generate terminal broadcast "contact tower {frequency}". Return success.
- [ ] Error handling: all track commands return `CommandResultDto` with success/error + message

---

## Chunk 7: ProcessStarsCommand Handling (yaat-server)

Handle CRC-initiated track operations via the STARS command protocol.

- [ ] Add `ProcessStarsCommandDto` deserialization to `SignalRMessageParser`:
  - Already recognized as a hub method target; currently stubbed. Parse the MessagePack payload into:
    - `FromSector: SectorSpec` (facilityId, subset?, sectorId)
    - `Type: StarsCommandType` (enum: Handoff, InitiateControl, TerminateControl, Implied, etc.)
    - `ParameterString: string?`
    - `ClickedItem1: StarsClickedItem?` (trackId, location)
    - `ClickedItem2: StarsClickedItem?`
    - `InvertNumericKeypad: bool`
  - Create C# records for `StarsCommandType` enum, `StarsClickedItem`, `SectorSpec`
- [ ] Resolve `ClickedItem.TrackId` to aircraft:
  - StarsTrackDto uses `Id = "CALLSIGN{callsign}"` format. Extract callsign from TrackId by stripping the "CALLSIGN" prefix.
  - Look up aircraft in `SimulationWorld`
- [ ] Handle `StarsCommandType.InitiateControl`:
  - Resolve `FromSector` → `TrackOwner` via position registry
  - Set `aircraft.Owner = fromSectorOwner`
  - Return `StarsCommandProcessingResultDto` (success)
- [ ] Handle `StarsCommandType.TerminateControl`:
  - Validate `FromSector` matches current `Owner`
  - Clear `Owner`, clear `HandoffPeer` if set
  - Return success
- [ ] Handle `StarsCommandType.Handoff`:
  - If aircraft has no pending handoff AND `FromSector` matches `Owner`:
    - **Initiate**: Resolve `ParameterString` → target `TrackOwner`. Set `HandoffPeer`. Set `HandoffInitiatedAt`.
  - If aircraft has pending handoff AND `FromSector` matches `HandoffPeer` (CRC user is the target):
    - **Accept**: Set `Owner = HandoffPeer`. Clear `HandoffPeer`. Clear `HandoffInitiatedAt`.
  - If aircraft has pending handoff AND `FromSector` matches `Owner`:
    - **Retract**: Clear `HandoffPeer`. Clear `HandoffInitiatedAt`.
  - If aircraft has pending handoff AND `FromSector` is a third party:
    - **Redirect**: Set `HandoffRedirectedBy = fromSector`. Redirect `HandoffPeer` to new target from `ParameterString`.
  - Return appropriate result
- [ ] Handle `StarsCommandType.Implied`:
  - If aircraft has pending handoff to `FromSector`: treat as Accept
  - If aircraft has pending pointout to `FromSector`: treat as Acknowledge (set `Pointout.Status = Accepted`)
  - Otherwise: return error/no-op
- [ ] Return `StarsCommandProcessingResultDto`:
  - Create this DTO in `CrcDtos.cs` matching vNAS shape
  - MessagePack serialized, sent as InvocationResponse
- [ ] Handle unsupported `StarsCommandType` values gracefully (ACK with no-op)

---

## Chunk 8: Client UI & Settings (Yaat.Client)

Display track ownership in the training client. Auto-accept delay setting.

- [ ] Update `ServerConnection.AircraftDto`:
  - Add properties matching new training DTO fields: `Owner`, `OwnerSectorCode`, `HandoffPeer`, `HandoffPeerSectorCode`, `PointoutStatus`, `Scratchpad1`, `TemporaryAltitude`, `IsAnnotated`
- [ ] Update `AircraftModel`:
  - Add `[ObservableProperty]` fields: `_owner`, `_ownerSectorCode`, `_handoffPeer`, `_handoffPeerSectorCode`, `_pointoutStatus`, `_scratchpad1`, `_temporaryAltitude`, `_isAnnotated`
  - Update `UpdateFrom(AircraftDto)` to populate these
- [ ] Add DataGrid columns in `MainWindow.axaml`:
  - "Owner" column — shows `OwnerSectorCode` (e.g., "2B") or `Owner` callsign if no sector code
  - "HO" column — shows `HandoffPeerSectorCode` or empty; color-code pending in vs. pending out
  - "SP" column — shows `Scratchpad1` if set
  - "TA" column — shows `TemporaryAltitude` if set (formatted as altitude)
- [ ] Auto-accept delay setting:
  - Add `AutoAcceptDelaySeconds` to `UserPreferences` (default: 5 seconds, range 0–60)
  - Add UI in `SettingsWindow.axaml`: numeric input with label "Auto-accept handoff delay (seconds)"
  - On scenario load: send `SetAutoAcceptDelay(seconds)` to server
  - On settings change while scenario is active: re-send to server
- [ ] Add `SetAutoAcceptDelay` hub method to `ServerConnection`

---

## Chunk 9: Integration & Testing

End-to-end verification and tests.

- [ ] Unit tests in `tests/Yaat.Sim.Tests/`:
  - `TrackOwnerTests`: construction, factory methods, equality
  - `StarsPointoutTests`: status transitions
  - Command parsing tests for all new command types
- [ ] Unit tests in `tests/Yaat.Client.Tests/`:
  - `CommandSchemeCompletenessTests` already enforces coverage — just ensure it passes
  - Verify ATCTrainer and VICE patterns parse correctly for all new commands
- [ ] Integration test scenarios (manual or automated in yaat-server):
  - Scenario loads with `atc[]` and `autoTrackConditions` → aircraft spawn with correct Owner
  - RPO types `TRACK` → Owner appears in CRC
  - RPO types `HO 2B` → HandoffPeer set, CRC shows flashing data block
  - Auto-accept fires after delay for unattended position
  - CRC client sends `ProcessStarsCommand(Handoff)` → handoff accepted
  - RPO types `DROP` → Owner cleared, CRC shows untracked
  - RPO types `SP TEST` → Scratchpad1 appears in CRC data block
  - RPO types `ACCEPTALL` → all pending inbound handoffs accepted
- [ ] Update `USER_GUIDE.md` with all new commands and track operations UI
- [ ] Update `docs/command-aliases-reference.md` — remove "not implemented" notes

---

## Definition of Done

- [ ] Aircraft spawn with correct Owner from `autoTrackConditions`
- [ ] Handoff to student is initiated per `handoffDelay` (0 = immediate, N = after N seconds, null = no auto-handoff)
- [ ] Auto-accept timer completes handoffs to unattended positions
- [ ] RPO can `AS {tcp}` to set active position (standalone and prefix)
- [ ] RPO can TRACK/DROP/HO/ACCEPT/CANCEL aircraft (using effective position)
- [ ] RPO can PO/OK/SP/TA/QQ/CRUISE aircraft
- [ ] ACCEPTALL and HOALL batch operations work
- [ ] CRC displays correct Owner, HandoffPeer, Pointout, Scratchpad in STARS data blocks
- [ ] CRC clients can ProcessStarsCommand for Handoff, InitiateControl, TerminateControl
- [ ] StarsConsolidation topic correctly reports TCP ownership
- [ ] Training client DataGrid shows Owner, Handoff, Scratchpad, TempAlt columns
- [ ] Auto-accept delay is configurable in Settings
- [ ] All new commands have ATCTrainer patterns, VICE patterns (where applicable), and CommandMetadata
- [ ] `CommandSchemeCompletenessTests` pass
- [ ] USER_GUIDE.md and command-aliases-reference.md updated
