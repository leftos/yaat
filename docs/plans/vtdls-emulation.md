# Plan: vTDLS emulation (Pre-Departure Clearance, v1)

## Context

YAAT already emulates CRC's vStrips end-to-end:

- Shared client lib `src/Yaat.Client.Strips/` hosts a single `VStripsView` UserControl + `VStripsViewWindow` pop-out.
- WASM web app `tools/Yaat.VStrips.Web/` publishes to `..\yaat-server\src\Yaat.Server\wwwroot\vstrips\` and is served at `/vstrips/`.
- yaat-server owns the state (`FlightStripState` on `TrainingRoom`), handlers (`StripCommandHandler` + `StripMutations`), broadcaster (`StripBroadcaster`), CRC translator (`StripCommandTranslator`), and snapshot persistence.
- YAAT Client supports multi-facility Strips tabs via `MainViewModel.StripsEntries : ObservableCollection<VStripsDockEntryViewModel>` with per-facility pop-out windows keyed on `"VStripsView:{facilityId}"`.

We want the same shape for vTDLS so the controller-facing experience matches what real vNAS users see, and so the same multi-facility / dock / pop-out / browser-access affordances exist for both tools.

**Status of pre-work that's already landed:**
- ✅ Standalone Yaat.VStrips desktop app deleted (`4841c228`). Only the in-client tab and the WASM web app remain.
- ✅ Phase 0 — vTDLS reference docs cached to `docs/vtdls/` (`dfd35cc8`). `docs/vtdls/vtdls.md` is verbatim upstream; `docs/vtdls/README.md` is the curated index + design-decision summary.

**Scoping decisions** (from interview + Phase 0 review):
- **v1 message scope**: PDC (Pre-Departure Clearance) only. The DCL ("Departure Clearance") *list* is part of v1 — it's the Pending-status view, not a separate message type. CPDLC is permanently out of scope (upstream confirms VATSIM does not support CPDLC).
- **State**: New parallel `TdlsState` per `TrainingRoom`. Per-facility flat lists keyed by Status (no bay/rack model — Phase 0 confirmed real vTDLS uses two flat lists, DCL and PDC). The existing `ClearanceDto` on the flight plan stays as the authoritative clearance fact; `TdlsState` is the message envelope (status / facility / sequence / timestamps / last-sent payload). They link by `AircraftId` *and* `Cid` (PDCs can be queued for a pilot before they connect — `Cid` is the durable identifier).
- **Pilot flow**: TDLS-silent — no voice readback after WILCO. Pilot state advances internally; no `PendingPilotTransmissions` entry.
- **TDLS config source**: All TDLS facility configuration (which facilities have TDLS enabled, available SIDs/transitions, per-SID+transition default PDC field values, mandatory fields, dropdown options) is loaded from the vNAS data-api — same pattern as ARTCC config (`data-api.vnas.vatsim.net/api/artccs/{id}`) and airport ground maps (`data-api.vnas.vatsim.net/api/training/airports/{FAA}/map`). YAAT does NOT define any TDLS option locally; the Facility Engineer's configuration is authoritative.
- **Naming**: User-facing string is "vTDLS" (lowercase v, uppercase TDLS). Code uses `VTdls` (mirrors `VStrips`): `Yaat.Client.Tdls`, `Yaat.VTdls.Web`, route `/vtdls/`, wwwroot `wwwroot/vtdls/`, viewmodels `VTdlsViewModel` / `VTdlsDockEntryViewModel`.
- **Multi-facility**: YAAT Client gets a vTDLS tab with multi-instance support per facility, identical to Strips — collection-driven dock entries, per-facility geometry key `"VTdlsView:{facilityId}"`. Plus a parent-facility *consolidated* selector that aggregates unstaffed child TDLS facilities into one view (upstream behavior).

## RPO observability (added 2026-05-26)

For instructor (RPO) usage:
- **All TDLS items are visible to every room member.** The vTDLS tab/window shows Pending (DCL) AND Sent (PDC) items by default — no role-based filtering. RPOs can review what a student has already issued without scrubbing logs.
- **Every TDLS PDC send emits a TerminalBroadcast** (Phase 2.2). The broadcast message is callsign + a human-readable summary of every non-null ClearanceDto field (Expect / SID / Transition / Climbout / Climbvia / Maintain / ContactInfo / DepFreq / LocalInfo). This means the instructor's terminal log shows TDLS activity inline with voice commands. Kind: new `Tdls` entry kind (preferred) or `System` with a `[TDLS]` prefix.

## Out of scope (v1)

- Full DCL data-link uplink, ATIS broadcast, taxi via TDLS — explicitly not modeled.
- CPDLC — VATSIM doesn't support it; upstream marks it not simulated. The CPDLC list panel is shown empty in the UI for layout parity but never populates.
- Voice readback after TDLS WILCO. CTO (takeoff clearance) readback is unchanged — TDLS does NOT suppress takeoff clearance readback.
- Mimicking pixel-perfect real-vTDLS UI. We adopt the existing Strips visual language (dark, monospace, dense), with two list panels + footer status, but no attempt to match upstream chrome exactly.
- Editing TDLS configuration. YAAT consumes the vNAS Facility Engineer's config read-only. Authoring TDLS config happens on the vNAS Data Admin site.

## Reference cache

Single source of truth for what real vTDLS does:
- `docs/vtdls/vtdls.md` — full upstream manual, verbatim.
- `docs/vtdls/README.md` — index + decision crib sheet aligned to this plan's phases.

Every design call below traces back to a section in `docs/vtdls/vtdls.md`. When the plan and the cache disagree, the cache wins; update the plan.

## Architecture mirror

```
vStrips                                       vTDLS
─────────────────────────────────────────────────────────────────
tools/Yaat.VStrips.Web/             →   tools/Yaat.VTdls.Web/
src/Yaat.Client.Strips/             →   src/Yaat.Client.Tdls/
src/Yaat.Client.Strips/Views/VStrips/VStripsView.axaml
                                    →   src/Yaat.Client.Tdls/Views/VTdls/VTdlsView.axaml
…/VStripsViewWindow.axaml           →   …/VTdlsViewWindow.axaml
src/Yaat.Client/ViewModels/MainViewModel.Strips.cs
                                    →   src/Yaat.Client/ViewModels/MainViewModel.Tdls.cs
.../VStripsDockEntryViewModel.cs    →   .../VTdlsDockEntryViewModel.cs
yaat-server/.../Simulation/FlightStripState.cs
                                    →   yaat-server/.../Simulation/TdlsState.cs
yaat-server/.../Simulation/StripCommandHandler.cs
                                    →   yaat-server/.../Simulation/TdlsCommandHandler.cs
yaat-server/.../Simulation/StripBroadcaster.cs
                                    →   yaat-server/.../Simulation/TdlsBroadcaster.cs
yaat-server/.../Simulation/StripCommandTranslator.cs
                                    →   yaat-server/.../Simulation/TdlsCommandTranslator.cs
yaat-server/.../Simulation/StripMutations.cs
                                    →   yaat-server/.../Simulation/TdlsMutations.cs
yaat-server/.../Dtos/CrcDtos.Strips.cs
                                    →   yaat-server/.../Dtos/CrcDtos.Tdls.cs
yaat-server/.../Hubs/CrcClientState.Strips.cs
                                    →   yaat-server/.../Hubs/CrcClientState.Tdls.cs
yaat-server/.../wwwroot/vstrips/    →   yaat-server/.../wwwroot/vtdls/
docs/flight-strips.md               →   docs/vtdls.md
                                    NEW: src/Yaat.Sim/Data/Vnas/TdlsConfig.cs
                                    NEW: src/Yaat.Sim/Data/Vnas/TdlsConfigLoader.cs
```

## Canonical commands

Phase-transparent verbs (Phase 0 settled the shape — no bays, no move, no separate "cancel"):

| Verb       | Meaning                                                | Notes |
|------------|--------------------------------------------------------|-------|
| `TDLSQ`    | Queue a new PDC item for callsign (→ Pending / DCL list) | Emitted internally by auto-gen when a flight plan is filed at a TDLS facility; rarely used by controllers directly |
| `TDLSS`    | Send the queued PDC (Pending → Sent / PDC list)        | Snapshots the resolved `ClearanceDto` into the item envelope, schedules silent auto-WILCO, broadcasts |
| `TDLSW`    | WILCO (Sent → Wilco)                                   | Auto-fires from the WILCO scheduler. Controller-manual form is the F12 "force ack" path |
| `TDLSDUMP` | Dump a TDLS item (any → removed)                       | Terminal — once dumped, the item cannot be re-added. Maps to upstream's "Dump" button + F4. Pilot must now be cleared by voice |

Dropped from the original plan after Phase 0 review:
- ~~`TDLSC` (Cancel)~~ — upstream has no "cancel" verb. Pre-send, the controller closes the editor without sending (no state change); the entry stays in DCL until Send/Dump/expire/depart. Post-send, "A PDC cannot be amended once it has been sent" (upstream verbatim) — only Dump is available.
- ~~`TDLSM` (Move)~~ — no bay/rack model. Items live in flat per-facility lists; ordering is by timestamp (or filed time).

All retained verbs are added to `CanonicalCommandType` + `CommandRegistry.All` + `CommandScheme.Default()` (completeness tests enforce this).

## Data model (high-level)

`TdlsState` (per `TrainingRoom`):

```csharp
public sealed class TdlsState
{
    object Gate;
    ConcurrentDictionary<string, TdlsItemRecord> Items;   // key: ItemId
    ConcurrentDictionary<string, TdlsConfig> Configs;     // key: FacilityId — loaded from vNAS data-api
    int NextItemId;
    void InitializeFromArtcc(ArtccConfigRoot artcc, TdlsConfigCache cache);  // wires up per-facility configs
    void Reset();
}

public sealed record TdlsItemRecord(
    string Id,                  // "TDLS_{n}"
    string AircraftId,          // callsign — may not be active in sim yet (pre-filed)
    string? Cid,                // VATSIM CID; durable across aircraft despawn/respawn
    string FacilityId,          // TDLS facility (resolved from departure airport)
    TdlsStatus Status,          // Pending | Sent | Wilco
    int Sequence,               // monotonic per-state, for sort stability
    DateTime CreatedUtc,
    DateTime? SentUtc,
    DateTime? WilcoUtc,
    DateTime ExpiresUtc,        // CreatedUtc + 2 hours per upstream
    ClearanceDto? SentPayload   // null while Pending; snapshot of ClearanceDto at TDLSS time
);

public enum TdlsStatus { Pending = 0, Sent = 1, Wilco = 2 }
```

Note: there is no "Dumped" status — the item is removed from `Items` on Dump and a separate `Dumped: HashSet<(facility, callsign)>` records the lockout so the auto-generator does not re-create it during the same session. The hashset persists in snapshot state.

`TdlsConfig` (per facility, loaded from vNAS data-api at scenario load):

```csharp
public sealed record TdlsConfig(
    string FacilityId,
    string? ParentFacilityId,        // when set, this facility's items appear in parent's consolidated view if parent is the staffed one
    IReadOnlyList<string> AirportIds, // airports served by this TDLS facility
    IReadOnlyList<TdlsFieldDef> Fields, // 9 fields (Expect, SID, Transition, Climbout, Climbvia, Maintain, ContactInfo, DepFreq, LocalInfo)
    IReadOnlyList<TdlsSidConfig> Sids  // each SID lists its transitions + per-(SID, transition) field defaults
);

public sealed record TdlsFieldDef(string Key, bool Mandatory, IReadOnlyList<string> Options);
public sealed record TdlsSidConfig(string SidId, IReadOnlyList<TdlsTransitionConfig> Transitions);
public sealed record TdlsTransitionConfig(string TransitionId, IReadOnlyDictionary<string, string?> DefaultFieldValues);
```

The exact data-api endpoint + payload shape is settled in Phase 1.0 by probing the live API (see that phase). The shape above is the *internal* model; the on-wire DTO can be a 1:1 mapping or aggregated under the existing ARTCC config — Phase 1.0 decides.

## Implementation phases

Each phase is one or more small commits, each independently green-build / green-test. Commit prefixes per CLAUDE.md (`add:` / `docs:` / `fix:`). Tests added TDD-first per phase.

### ~~Phase 0 — Cache vTDLS reference docs~~ ✅ done

Committed at `dfd35cc8`. `docs/vtdls/` cached. Findings folded into the rest of this plan.

### Phase 1.0 — vNAS data-api TDLS config probe & loader

**Goal**: Find where the vNAS data-api exposes TDLS configuration, build a loader, integrate with the existing config-load path. This phase is *exploratory first, code second*: it's possible TDLS config lives inside the ARTCC config response, in which case the loader is just an enrichment of `ArtccConfigService`; if it's a separate endpoint, we add a `TdlsConfigDownloader` analogous to `AirportLayoutDownloader`.

**1.0.1** ✅ `docs(tdls): probe vNAS data-api for TDLS config endpoint` (committed `65da3764`)
- Findings in `docs/vtdls/README.md` under "Data-api integration". TL;DR: TDLS lives **inside** the existing `/api/artccs/{ID}` ARTCC config response on per-facility `tdlsConfiguration` nodes — no separate endpoint. Wire format matches `..\vatsim-vnas\data\Facilities\Tdls*.cs` exactly. ARTCC IDs must be uppercase.

**1.0.2** ✅ `add(tdls): TdlsConfig DTOs in ArtccConfig.cs + ZOA fixture` (TBD this commit)
- Extends `src/Yaat.Sim/Data/Vnas/ArtccConfig.cs` with `TdlsConfig`, `TdlsSidConfig`, `TdlsSidTransitionConfig`, `TdlsClearanceValueConfig` classes; adds `[JsonPropertyName("tdlsConfiguration")] TdlsConfig? TdlsConfiguration` to `FacilityConfig`. Property naming follows the wire format (`InitialAlts`, not "Maintain" — UI does the rename).
- No separate `TdlsConfigLoader` needed; `ArtccConfigService` (which already caches ARTCC responses) handles the embedded TDLS subtree.
- Fixture: `tests/Yaat.Sim.Tests/TestData/artcc-zoa-snapshot.json` captured via `python tools/refresh-artcc-snapshot.py --artcc ZOA --out tests/Yaat.Sim.Tests/TestData/artcc-zoa-snapshot.json`.
- Tests: `tests/Yaat.Sim.Tests/Data/ArtccTdlsConfigParseTests.cs` — walks the facility tree, asserts 5 TDLS facilities (OAK/RNO/SFO/SJC/SMF), drills into SFO + OAK shape (mandatory flags, default SID/transition resolution, nullable-default round-trip).

**1.0.3** `add(tdls): wire TdlsConfig into TrainingRoom on scenario load`
- Modify `src/Yaat.Server/Simulation/SimulationEngine.cs` (or wherever scenario load currently calls `ArtccConfigService.Load…`) — walk the facility tree of the loaded ARTCC and hand every `tdlsConfiguration != null` node to `TdlsState.InitializeFromArtcc`.
- Tests: integration via existing scenario-load tests + a new `tests/Yaat.Server.Tests/TdlsScenarioLoadTests.cs` asserting that loading a ZOA scenario populates `room.TdlsState.Configs` with the five NCT child facilities (OAK/RNO/SFO/SJC/SMF).

### Phase 1 — Server data model (no handlers)

**1.1** `add(tdls): TdlsItemRecord, TdlsState, room wiring`
- Create `src/Yaat.Server/Simulation/TdlsState.cs` per the data-model section above. No bays. `Items: ConcurrentDictionary<string, TdlsItemRecord>` keyed by ItemId; per-facility / per-status views are computed via LINQ on `Items` rather than stored separately. `Dumped: HashSet<(facility, callsign)>` lockout. `NextItemId: int`.
- Modify `src/Yaat.Server/Simulation/TrainingRoom.cs`: add `public TdlsState TdlsState { get; } = new();` next to `StripState`.
- Tests: `tests/Yaat.Server.Tests/TdlsStateInitTests.cs` — empty after construction; `InitializeFromArtcc` is idempotent; `Reset` clears Items + Dumped + NextItemId.

**1.2** `add(tdls): CRC + SignalR DTOs + enums`
- Create `src/Yaat.Server/Dtos/CrcDtos.Tdls.cs`:
  - `TdlsStatus { Pending = 0, Sent = 1, Wilco = 2 }` (no Cancelled).
  - `TdlsItemDto { Id, AircraftId, Cid, FacilityId, Status, Sequence, CreatedUtc, SentUtc?, WilcoUtc?, ExpiresUtc, SentPayload?: ClearanceDto }`. MessagePack `[Key(N)]` indexed.
  - `TdlsStateDto { Items: List<TdlsItemDto>, Dumped: List<DumpEntryDto>, ConfigsByFacility: Dict<string, TdlsConfigDto> }`.
  - CRC invocation wrappers: `SendTdlsClearanceDto`, `DumpTdlsItemDto`, `RequestFullTdlsStateDto`.
- Tests: `tests/Yaat.Server.Tests/TdlsDtoSerializationTests.cs` — round-trip MessagePack for each DTO; `Cid` and `SentPayload?` serialize correctly when null.

**1.3** `add(tdls): snapshot capture + restore for TdlsState`
- Modify `src/Yaat.Server/Simulation/Persistence/RoomStateSnapshotDto.cs` — add `TdlsStateSnapshotDto? Tdls` with `Items`, `Dumped`, `NextItemId`. Configs are NOT persisted (re-fetched from data-api on restore).
- Modify `src/Yaat.Server/Simulation/Persistence/RoomStateSnapshotMapper.cs` — `CaptureTdls(room.TdlsState)`, `RestoreTdls(room.TdlsState, dto.Tdls)`; wire into `Capture` and `Restore`. Restore re-runs `InitializeFromArtcc` to repopulate Configs.
- Tests: extend `tests/Yaat.Server.Tests/SessionPersistenceTests.cs` with `Roundtrip_PreservesTdlsState` (Pending / Sent / Wilco items + Dumped lockout + sequence) and `Restore_RehydratesConfigsFromDataApi` (Configs are not in the snapshot blob but reappear after restore).
- Older snapshots have no `Tdls` field → null → restore no-ops → empty `TdlsState`. Additive; no migrator entry needed unless we add a versioned subtree.

### Phase 2 — Canonical commands, handler, broadcaster

**2.1** `add(tdls): canonical TDLS command types in Yaat.Sim`
- Create `src/Yaat.Sim/Commands/TdlsCommands.cs` (parsed record types).
- Modify `src/Yaat.Sim/Commands/CanonicalCommandType.cs` — add `TdlsQueue`, `TdlsSend`, `TdlsWilco`, `TdlsDump`. (No Cancel, no Move, no Delete — Dump *is* the removal.)
- Modify `CommandRegistry.cs`, `CommandScheme.cs`, `CommandSchemeParser.cs`, `CommandParser.cs`, `CommandDescriber.cs`.
- Modify `TrackEngine.cs` (or wherever `IsStripCommand` lives) — add `IsTdlsCommand` so `RoomEngine.SendCommandAsync` routes them before the dispatcher (same bypass as strips, phase-transparent).
- Tests: `tests/Yaat.Sim.Tests/TdlsCommandParserTests.cs`. Existing `CommandSchemeCompletenessTests` auto-covers enum / registry / scheme completeness.

**2.2** `add(tdls): TdlsCommandHandler + TdlsMutations + TdlsBroadcaster + TdlsCommandTranslator`
- Create `src/Yaat.Server/Simulation/TdlsMutations.cs` — stateless helpers: `NewItemId`, `QueuePending`, `MarkSent(itemId, clearancePayload)`, `MarkWilco(itemId)`, `Dump(itemId)`, `Expire(itemId)`, `BuildFullState`.
- Create `src/Yaat.Server/Simulation/TdlsCommandHandler.cs` — handlers for Queue / Send / Dump. Each: validate facility config exists, mutate via `TdlsMutations`, broadcast.
  - **Queue** (TDLSQ): rejects if (facility, callsign) is in Dumped lockout. Idempotent if already Pending for same aircraft+facility.
  - **Send** (TDLSS): requires a Pending entry; snapshot the resolved `ClearanceDto` (validated against `TdlsConfig` — mandatory fields set, values are in the dropdown). On success → Status=Sent, SentUtc=now, SentPayload=clearance, schedule auto-WILCO. **Also emit a `TerminalBroadcast` to the room summarizing the sent PDC** (callsign + every non-null field of the ClearanceDto) so RPOs see in their terminal log exactly what the student issued. New `TerminalEntryKind` value (or reuse `System` with a `[TDLS]` prefix — decide per existing convention).
  - **Dump** (TDLSDUMP): removes the item from `Items`, adds (facility, callsign) to Dumped. Broadcasts a `TdlsItemRemovedDto`.
  - **WILCO**: see Phase 3.
- Create `src/Yaat.Server/Simulation/TdlsBroadcaster.cs` — `BroadcastItemsAsync`, `BroadcastFullStateAsync`, `BroadcastRemovalAsync`, `SendInitialStateToClientAsync`. SignalR (`TdlsItemsChanged`, `TdlsStateChanged`, `TdlsItemRemoved`) + CRC topic publish. Topic name in a single constant for easy swap pending Phase 2.3 capture.
- Create `src/Yaat.Server/Simulation/TdlsCommandTranslator.cs` — CRC MessagePack DTOs → canonical command strings.
- Modify `src/Yaat.Server/YaatHost.cs` — `AddSingleton<TdlsBroadcaster>(); AddSingleton<TdlsCommandHandler>();`.
- Modify `src/Yaat.Server/Simulation/RoomEngine.cs` — `IsTdlsCommand` branch in `SendCommandAsync` + `RecordAndDispatchTdlsAsync`.
- Tests: `TdlsCommandHandlerTests.cs` (Queue/Send/Dump happy paths + rejections), `TdlsCommandTranslatorTests.cs`, `TdlsBroadcasterTests.cs`.

**2.3** `add(tdls): CRC inbound dispatch for TDLS items` *(conditional — confirm Phase 1.0)*
- If CRC has a dedicated TDLS surface beyond `SendClearance`/`HandleTdlsDump`:
  - Create `src/Yaat.Server/Hubs/CrcClientState.Tdls.cs` (partial of `CrcClientState`): `HandleSendTdlsClearance`, `HandleDumpTdlsItem`, `HandleRequestFullTdlsState`.
  - Modify `src/Yaat.Server/Hubs/CrcClientState.cs` — switch arms.
  - Tests: `tests/Yaat.Server.Tests/CrcTdlsDispatchTests.cs`.
- If CRC has no dedicated TDLS topic and works exclusively via the existing flight-plan editor: drop this commit and reuse `HandleSendClearance` + `HandleTdlsDump` as the inbound path. Document the decision in `docs/vtdls.md` Phase 9.1.

### Phase 3 — Pilot ack pipeline + lifecycle (TTL, re-send on reconnect)

**3.1** `add(tdls): silent auto-WILCO + ClearanceDto application without voice readback`
- Refactor `AircraftState` to expose `ApplyClearance(ClearanceDto dto, bool viaTdls)`. Existing voice-clearance path passes `viaTdls: false` (unchanged); new TDLS path passes `true`.
- Audit `src/Yaat.Sim/Pilot/PilotResponder.cs`, `PilotProactive.cs`, `PilotRequestTracker.cs` for any place that enqueues a readback off a clearance event; route through the new `ApplyClearance` so `viaTdls: true` suppresses the `PendingPilotTransmissions.Add(...)` for the clearance readback only. CTO / takeoff readback is unaffected.
- Create `src/Yaat.Server/Simulation/TdlsWilcoScheduler.cs` — `Dictionary<itemId, scheduledUtc>`. Ticked from `TickProcessor.ProcessPostPhysics`.
- Modify `src/Yaat.Server/Simulation/TdlsCommandHandler.cs` — `HandleAutoWilcoAsync(room, itemId)`: idempotent; if `Status != Sent` no-op; else set `Status = Wilco`, `WilcoUtc = now`, `ac.Voice.TdlsDumped = true`, broadcast.
- Modify `src/Yaat.Server/Hubs/CrcClientState.FlightPlan.cs` — split `HandleSendClearance` so the TDLS-sourced send routes through the new path that schedules the silent WILCO.
- Scenario config: add `SimScenarioState.TdlsWilcoDelaySeconds` (default ~3s — real FMS auto-ack is near-instant; chose 3s so the controller sees a brief transient rather than instant flip).
- Tests: `tests/Yaat.Server.Tests/TdlsWilcoTests.cs` — TDLSS → Sent; auto-tick advances to Wilco + TdlsDumped + ClearanceDto persisted; `PendingPilotTransmissions` stays empty for the clearance; subsequent CTO still produces a voice readback.

**3.2** `add(tdls): manual TDLSW (controller force-wilco)`
- Modify `TdlsCommandHandler.cs` — `HandleTdlsWilcoAsync` (manual). Same effects as auto-WILCO; cancels any pending scheduler entry.
- Tests: extend `TdlsCommandHandlerTests.cs`.

**3.3** `add(tdls): 2-hour TTL expiry + reconnect re-send`
- Modify `src/Yaat.Server/Simulation/TickProcessor.cs` — `ProcessTdlsExpiry(room)`: iterate `room.TdlsState.Items`, remove those with `ExpiresUtc < now`, broadcast removals. Idempotent.
- Modify the SignalR `RequestFullTdlsState` handler so a CRC client reconnecting (or a YAAT-client vTDLS tab opening) receives the full current `TdlsStateDto` — including each item's `SentPayload`. That implicitly satisfies upstream's "If a pilot disconnects and then reconnects before departure, a new copy of their PDC is automatically sent to them" without a dedicated re-send mechanism: the YAAT pilot/CRC client repopulates from the state snapshot.
- For aircraft despawn/respawn within a session: items keyed on `Cid` (not transient ItemId) survive even if the AircraftState is briefly removed. The Dumped lockout also keys on (facility, callsign) so re-spawn cannot bypass it.
- Tests: `tests/Yaat.Server.Tests/TdlsExpiryTests.cs` (TTL on Pending and Sent), `tests/Yaat.Server.Tests/TdlsReconnectTests.cs` (RequestFullState round-trips a Sent item with payload intact).

### Phase 4 — Auto-generation rules

**4.1** `add(tdls): auto-create Pending entry on filed flight plan`
- Trigger model: when a flight plan is filed (via VP/DA/FP command, scenario load, or CRC AmendFlightPlan) whose `Departure` airport is served by a TDLS-configured facility in `room.TdlsState.Configs`, emit `TDLSQ <callsign>` automatically. Hook point: `RoomEngine` / `FlightPlanCommandHandler` post-success — wherever an authoritative flight-plan-changed event already fires.
- Idempotent: if a Pending entry already exists for the (facility, callsign) pair, do nothing. If the entry was previously Dumped (lockout), do nothing.
- Trigger is NOT driven by student-position type. The DCL list belongs to the facility; whoever is staffing that facility (TWR/GND/CD/DEL/Center top-down) sees its entries via the per-facility tab.
- Pre-filed flight plans (a CID with a flight plan but no active AircraftState) also generate Pending entries when the flight plan is recorded — matches upstream's "If a pilot pre-files prior to connecting to the network, their flight plan is displayed in the DCL list, even if the pilot is not yet connected."
- Modify `TdlsMutations.cs` — `RequestPdcForFlightPlan(facility, aircraftId, cid)`.
- Tests: `tests/Yaat.Server.Tests/TdlsAutoGenerationTests.cs` — flight plan at TDLS airport → Pending entry; flight plan at non-TDLS airport → no entry; dumped (facility, callsign) → no re-creation; re-file → idempotent; pre-filed without aircraft → still generates.

### Phase 5 — Shared `Yaat.Client.Tdls` library

**5.1** `add(tdls): Yaat.Client.Tdls project scaffold`
- Create `src/Yaat.Client.Tdls/Yaat.Client.Tdls.csproj` (mirror `Yaat.Client.Strips.csproj`). `[InternalsVisibleTo]` for `Yaat.Client`, `Yaat.Client.Core`, `Yaat.Client.Tests`, `Yaat.Client.UI.Tests`, `Yaat.VTdls.Web`. Reference `Yaat.Sim` only.
- Modify `yaat.slnx` — add the project.

**5.2** `add(tdls): transport + DTO mirrors + viewmodels`
- Create `src/Yaat.Client.Tdls/Services/ITdlsTransport.cs`, `BrowserTdlsTransport.cs` (owns its own `HubConnection` to `/hubs/training`), `TdlsDtos.cs` (JSON-side DTO copy — separate from MessagePack server DTOs so the WASM linker stays slim), `YaatTdlsHubJsonContext.cs`.
- Create `src/Yaat.Client.Tdls/ViewModels/VTdlsViewModel.cs`, `TdlsDclListViewModel.cs`, `TdlsPdcListViewModel.cs`, `TdlsItemViewModel.cs`, `TdlsFlightPlanEditorViewModel.cs`, `VTdlsCanonicalBuilder.cs` (emits `TDLSQ` / `TDLSS` / `TDLSW` / `TDLSDUMP`).
- The `TdlsFlightPlanEditorViewModel` wraps the resolved `ClearanceDto` + the facility's `TdlsConfig` and exposes per-field dropdowns. Validates mandatory fields client-side and gates the Send button.
- Tests: `tests/Yaat.Client.Tests/VTdlsViewModelTests.cs`, `VTdlsCanonicalBuilderTests.cs`, `TdlsFlightPlanEditorViewModelTests.cs` (mandatory-field gating).

**5.3** `add(tdls): VTdlsView + VTdlsViewWindow Avalonia controls`
- Create `src/Yaat.Client.Tdls/Views/VTdls/VTdlsView.axaml` (UserControl):
  - **Header**: active facility indicator (click = facility menu — includes the parent-consolidation option), system status indicators, Zulu clock.
  - **Body**: three list panels side-by-side — DCL (Pending), PDC (Sent + Wilco), CPDLC (empty / "not simulated" label).
  - **Footer**: clearance-type status ("CLEARANCE TYPE: PDC" / "MANDATORY FIELD NOT SET") + Zulu clock.
  - **Flight plan editor** (opens on selecting an item from DCL): inline window at the bottom of the view, mirroring upstream's `flight-plan.png`. Nine field dropdowns + Send + Cancel + Dump buttons.
  - **Key commands**: F4 Dump, F10 Cancel-editor (close without send), F12 Send, Ctrl+Alt+→/← cycle facilities, ↑/↓ navigate list, Tab/` swap active list, Enter open-selected.
- Create `src/Yaat.Client.Tdls/Views/VTdls/VTdlsViewWindow.axaml` (Window). Window uses `WindowGeometryHelper(this, vm.Preferences, $"VTdlsView:{facilityId}", w, h).Restore()`. First-time per-facility windows inherit global Topmost from `"VTdlsView"`.
- Reuse `MonoFont`, `SubtleTextBrush`, FluentTheme dark from `src/Yaat.Client/App.axaml`. No DataGrid — two `ItemsControl`s for the lists.
- Tests: `tests/Yaat.Client.UI.Tests/VTdlsViewTests.cs` — render smoke + key-binding emission (F12 → TDLSS, F4 → TDLSDUMP) + facility cycling.

### Phase 6 — `Yaat.VTdls.Web` WASM browser app

**6.1** `add(tdls): Yaat.VTdls.Web scaffold + CopyToServerWwwroot`
- Create `tools/Yaat.VTdls.Web/` (mirror `tools/Yaat.VStrips.Web/`): csproj, `App.axaml(.cs)`, `MainView.axaml(.cs)`, `Program.cs`, `runtimeconfig.template.json`, `wwwroot/index.html`, `wwwroot/main.js`. Title strings → "vTDLS".
- csproj's `CopyToServerWwwroot` target points to `..\yaat-server\src\Yaat.Server\wwwroot\vtdls\`. References `Yaat.Client.Tdls`.
- Modify `yaat.slnx` — add the project.
- Smoke: `dotnet publish -c Release` then browse `http://localhost:5000/vtdls/` after Phase 8.1.

### Phase 7 — Yaat.Client desktop integration

**7.1** `add(tdls): MainViewModel.TdlsEntries + VTdlsDockEntryViewModel`
- Create `src/Yaat.Client/ViewModels/MainViewModel.Tdls.cs` (mirror `MainViewModel.Strips.cs`). `TdlsEntries : ObservableCollection<VTdlsDockEntryViewModel>`; `OpenTdlsEntryForFacilityAsync`, `CloseTdlsEntry`.
- Create `src/Yaat.Client/ViewModels/VTdlsDockEntryViewModel.cs` (mirror `VStripsDockEntryViewModel`). `TabTitle = $"vTDLS ({facility})"`.
- Modify `MainViewModel.cs` — declare collection + subscribe to `CollectionChanged`; bootstrap student-entry next to the existing Strips student-entry path.
- Tests: `tests/Yaat.Client.Tests/MainViewModelTdlsEntriesTests.cs`.

**7.2** `add(tdls): MainWindow tab + pop-out + View menu submenu`
- Modify `src/Yaat.Client/Views/MainWindow.axaml.cs` — mirror Strips block: `_tdlsTabItems`, `_tdlsWindows`, `WireTdlsEntryWindows`, `AttachTdlsEntry`, `DetachTdlsEntry`, `OpenTdlsEntryWindow`, `CloseTdlsEntryWindow`, `RebuildTdlsSubmenu`, `OnNewTdlsTabSubmenuOpened`.
- Modify `src/Yaat.Client/Views/MainWindow.axaml` — add `TdlsSubmenu` MenuItem under View next to `StripsSubmenu`.
- Geometry key `$"VTdlsView:{facilityId}"` — distinct from `"VStripsView:..."`. Easy to mis-copy.
- Tests: `tests/Yaat.Client.UI.Tests/MainWindowTdlsTabTests.cs`.

### Phase 8 — Server deployment

**8.1** `add(tdls): YaatHost static-file mapping for /vtdls/`
- Modify `src/Yaat.Server/YaatHost.cs` — duplicate the `/vstrips/` block (redirect `/vtdls` → `/vtdls/`, `MapFallbackToFile("vtdls/{**path:nonfile}", "vtdls/index.html")`). Shared WASM content-type provider — no changes.

**8.2** `add(tdls): Dockerfile explicit COPY lines`
- Modify `src/Yaat.Server/Dockerfile`:
  - `COPY extern/yaat/src/Yaat.Client.Tdls/Yaat.Client.Tdls.csproj extern/yaat/src/Yaat.Client.Tdls/`
  - `COPY extern/yaat/tools/Yaat.VTdls.Web/Yaat.VTdls.Web.csproj extern/yaat/tools/Yaat.VTdls.Web/`
  - `RUN dotnet restore extern/yaat/tools/Yaat.VTdls.Web/Yaat.VTdls.Web.csproj`
  - Source copies + `RUN dotnet publish extern/yaat/tools/Yaat.VTdls.Web/Yaat.VTdls.Web.csproj -c Release -p:YaatServer=/src` so `wwwroot/vtdls/` exists before server publish. Place these next to the existing VStrips lines.
- Per `feedback_yaat_server_dockerfile_explicit_copies`, the cached restore layer skips silently if these aren't explicit.

**8.3** `deploy-to-droplet.ps1` — no changes (path-agnostic; confirm by inspection).

### Phase 9 — Documentation + aviation review + changelog

**9.1** `docs(tdls): docs/vtdls.md`
- Create `docs/vtdls.md` — exact structural mirror of `docs/flight-strips.md`: overview, list types table, auto-gen trigger, server state model, vNAS data-api config integration, commands table, CRC protocol integration, CRC → canonical translation, view test pointers, parent-consolidation behavior, key commands.

**9.2** `docs(tdls): USER_GUIDE.md + COMMANDS.md + cheatsheet`
- Modify `USER_GUIDE.md` — vTDLS section: how to open the tab/window, file flight plan → DCL → Send → Wilco lifecycle, dump semantics, browser access at `/vtdls/`, key commands.
- Modify `COMMANDS.md` — TDLSQ / TDLSS / TDLSW / TDLSDUMP entries in Quick Reference + Detailed Documentation.
- Modify `docs/command-cheatsheet.json` (and regenerate the HTML cheatsheet) — per `feedback_cheatsheet_json_sync`.

**9.3** `docs(tdls): aviation-realism review`
- Invoke `aviation-realism-review` (skill) against the auto-WILCO timing + the silent-pilot rule + the lifecycle (2-hour TTL, activate-on-departure removal, dump terminality). PDC content/phraseology follows AIM 5-2-2 and FAA Order 7210.3. Append findings to `docs/vtdls.md` under "Phraseology compliance notes". Local FAA refs at `.claude/reference/faa/aim/`.

**9.4** `docs(tdls): CHANGELOG entry`
- Modify `CHANGELOG.md` under `## Unreleased` — single bullet describing the vTDLS PDC feature (multi-facility tab + pop-out + browser app at `/vtdls/` + silent WILCO + Dump). Match house tone — no "comprehensive" / "robust" adjectives.

## Phase ordering / DAG

```
Phase 0 (docs cache) ✅
        ▼
Phase 1.0.1 (probe data-api) ✅
Phase 1.0.2 (DTOs + ZOA fixture) ✅
Phase 1.0.3 (wire into TrainingRoom) ✅
Phase 1.1 (TdlsState) ✅
Phase 1.2 (CRC + SignalR DTOs) ✅
Phase 1.3 (snapshot capture/restore) ✅
Phase 2.1 (canonical TDLS commands in Yaat.Sim) ✅
Phase 2.2 (TdlsCommandHandler + Broadcaster + RPO terminal broadcast) ✅
Phase 2.3 (CRC inbound) — SKIPPED; CRC has no dedicated TDLS surface
Phase 3.1 + 3.3 (auto-WILCO scheduler + TTL expiry) ✅ — ApplyClearance(viaTdls) refactor deferred
Phase 3.2 (manual TDLSW) ✅ — landed alongside 2.2 (handler includes manual WILCO)
Phase 4.1 (auto-gen Pending on filed flight plan) ✅
                         ─── server build green ───
Phase 5.1 (Yaat.Client.Tdls scaffold) ✅
Phase 5.2 (transport + viewmodels)
Phase 5.3 (VTdlsView + VTdlsViewWindow)
Phase 6.1 (Yaat.VTdls.Web WASM)
Phase 7.1 (MainViewModel.TdlsEntries + dock VM)
Phase 7.2 (MainWindow tab + pop-out + View menu)
                         ─── client+web build green ───
Phase 8.1 (YaatHost /vtdls/ route) ✅
Phase 8.2 (Dockerfile COPY lines)
                         ─── docker build green ───
Phase 9.1 (docs/vtdls.md)
Phase 9.2 (USER_GUIDE + COMMANDS + cheatsheet)
Phase 9.3 (aviation-realism review)
Phase 9.4 (CHANGELOG) ✅ — server-side bullet landed; UI bullet TBD
```

Each phase ends in a green build (`dotnet build -p:TreatWarningsAsErrors=true`) + green tests (`pwsh tools/test-all.ps1` per `feedback_test_all_for_full_suite`). Phases 5–7 can build in client-isolation against a fake transport before Phase 8 lights up the real server route.

The second STOP (after Phase 1.0.1) is for human review of the data-api endpoint shape before the loader is coded — same rationale as the Phase 0 STOP: a wrong assumption about config shape would cascade through Phase 1.x and Phase 4.

## Critical files to modify or create

- `docs/vtdls/` ✅ (Phase 0, done)
- `src/Yaat.Sim/Data/Vnas/TdlsConfig.cs` + `TdlsConfigLoader.cs` (new, Phase 1.0.2)
- `src/Yaat.Server/Simulation/TdlsState.cs` (new, Phase 1.1)
- `src/Yaat.Server/Dtos/CrcDtos.Tdls.cs` (new, Phase 1.2)
- `src/Yaat.Server/Simulation/Persistence/RoomStateSnapshotMapper.cs` (modify, Phase 1.3)
- `src/Yaat.Sim/Commands/CanonicalCommandType.cs` (modify, Phase 2.1)
- `src/Yaat.Sim/Commands/CommandRegistry.cs` + `CommandScheme.cs` (modify, Phase 2.1)
- `src/Yaat.Server/Simulation/TdlsCommandHandler.cs` (new, Phase 2.2)
- `src/Yaat.Server/Simulation/TdlsBroadcaster.cs` (new, Phase 2.2)
- `src/Yaat.Server/Simulation/RoomEngine.cs` (modify, Phase 2.2)
- `src/Yaat.Server/Hubs/CrcClientState.FlightPlan.cs` (modify — split TDLS-vs-voice clearance handling, Phase 3.1)
- `src/Yaat.Sim/Pilot/PilotResponder.cs` + `PilotProactive.cs` + `PilotRequestTracker.cs` (audit + guard via `ApplyClearance(viaTdls)`, Phase 3.1)
- `src/Yaat.Server/Simulation/TickProcessor.cs` (modify — `ProcessTdlsWilcoQueue` + `ProcessTdlsExpiry` + `ProcessAutoPdcGeneration`, Phases 3.1 + 3.3 + 4.1)
- `src/Yaat.Client.Tdls/` (new project tree, Phases 5.1–5.3)
- `tools/Yaat.VTdls.Web/` (new project tree, Phase 6.1)
- `src/Yaat.Client/ViewModels/MainViewModel.Tdls.cs` + `VTdlsDockEntryViewModel.cs` (new, Phase 7.1)
- `src/Yaat.Client/Views/MainWindow.axaml(.cs)` (modify — TDLS submenu + tab/window wiring, Phase 7.2)
- `src/Yaat.Server/YaatHost.cs` (modify — `/vtdls/` static-file mapping, Phase 8.1)
- `src/Yaat.Server/Dockerfile` (modify — explicit COPY + restore + publish for the two new csprojs, Phase 8.2)
- `docs/vtdls.md`, `USER_GUIDE.md`, `COMMANDS.md`, `docs/command-cheatsheet.json`, `CHANGELOG.md` (Phase 9)

## Reused utilities (no new code)

- `WindowGeometryHelper` (already handles per-facility keys; just pass `$"VTdlsView:{facilityId}"`)
- `MonoFont`, `SubtleTextBrush`, FluentTheme dark — Avalonia resource dictionaries already in `App.axaml`
- `AirportLayoutDownloader` / `ArtccConfigService` pattern — the TDLS config loader follows the same download + cache-on-disk + conditional-freshness shape
- `TickProcessor` post-physics chain — TDLS work slots in next to `ProcessAutoArrivalStrips`
- `StripBroadcaster`'s CRC + SignalR dual-transport pattern — mirror directly
- Existing `FlightPlanDto.TdlsDumped` flag — set by the silent-WILCO path; no new flag needed
- `RoomEngine.RecordAndDispatchStripAsync` pattern — add a sibling `RecordAndDispatchTdlsAsync`

## Risks / open questions

Resolved by Phase 0:
- ✅ Bay model — confirmed: no bays/racks, flat per-facility DCL/PDC lists.
- ✅ Lifecycle — confirmed: 2-hour TTL, activate-on-departure removal, Dump is terminal, no cancel-after-send.
- ✅ PDC fields — confirmed: nine fields, FE-configured options, mandatory-field enforcement.
- ✅ Multi-facility consolidation — confirmed: parent facility view aggregates unstaffed children.

Still open:
1. **vNAS data-api TDLS config endpoint** — Phase 1.0.1 probe will settle whether TDLS config is embedded in the ARTCC config response or served by a separate endpoint. The loader's shape depends on this.
2. **CRC ↔ server TDLS topic name + DTO field order** — provisional. Phase 2.3 is conditional on confirming whether CRC has a dedicated TDLS surface; if not, the existing `SendClearance` + `HandleTdlsDump` flows are sufficient. Best resolution: capture a real CRC↔vTDLS frame from a live VATSIM session.
3. **Auto-WILCO timing** — defaulted to ~3s (real FMS auto-ack is near-instant). Exposed via `SimScenarioState.TdlsWilcoDelaySeconds`.
4. **Voice-suppression audit** — exhaustive list of pilot-side places that watch for "controller issued clearance → readback" must be triple-checked. Cleanest fix is one chokepoint (`ApplyClearance(dto, viaTdls)`); the audit confirms there are no out-of-band paths. Track as a Phase 3.1 acceptance check.

## Verification

End-to-end after Phase 9 lands:

1. Local: `pwsh tools/test-all.ps1` — green across both repos.
2. Local server: `dotnet run --project src/Yaat.Server`. From a browser, open `http://localhost:5000/vtdls/` — vTDLS web app loads, auto-joins room for CID.
3. YAAT Client: `dotnet run --project src/Yaat.Client`. Load a departure-heavy scenario at a TDLS-configured facility (e.g. KBOS). View → vTDLS submenu lists facilities; open a second facility's tab; pop it out; verify per-facility geometry persists across restarts.
4. File a flight plan for N123AB out of KOAK → DCL list shows a Pending item automatically (OAK is one of five TDLS-configured ATCTs under ZOA's NCT TRACON). Select the item → flight plan editor opens with nine field dropdowns pre-populated from the FE-defined defaults. Press F12 → item moves to PDC list, status Sent, ClearanceDto snapshotted; after ~3s → status Wilco, `Voice.TdlsDumped == true`, no `PendingPilotTransmissions` entry created.
5. CTO N123AB → voice readback still fires (TDLS does not suppress takeoff).
6. F4 on the PDC item → item disappears from the list; re-creating the flight plan does NOT re-generate the entry (Dumped lockout).
7. Wait 2+ hours sim time on a Sent item → item expires and disappears from the PDC list (TTL).
8. Open NCT TRACON (parent of OAK/SFO/SJC/SMF/RNO) with none of those five staffed → consolidated view shows entries from all five child facilities. Staff one child → its entries disappear from the parent view and the child's tab populates.
9. `prepare-restart` cycle: confirm `RoomStateSnapshotDto.Tdls` round-trips Pending + Sent items + Dumped lockout across the restart. Configs reload from data-api (not from snapshot).
10. Reconnect a CRC client to an existing room with active TDLS items: the client's `RequestFullTdlsState` returns every item including each Sent item's `SentPayload`.
11. Docker: `docker compose build && docker compose up -d` on yaat-server. Hit `https://{droplet}/vtdls/` — same behavior as localhost.

Aviation review (Phase 9.3) signs off PDC phraseology against AIM 5-2-2 before tagging the release.
