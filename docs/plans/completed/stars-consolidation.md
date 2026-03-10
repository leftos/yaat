# STARS Consolidation

## Context

STARS consolidation allows a TCP (Terminal Control Position) to take control responsibility for one or more additional TCPs and their associated airspace. It determines which sector "owns" handoffs and tracks when not all positions are staffed — the core mechanism for top-down control in terminal facilities.

Without consolidation, CRC cannot correctly color-code owned vs. other tracks, and handoffs to unstaffed positions have nowhere to go. The track-operations plan (`docs/plans/track-operations.md`) identified this as a requirement.

### How Consolidation Works

The vNAS ARTCC config defines a **parent-child TCP hierarchy** via `parentTcpId` back-references on each TCP. Example from FAT (Fresno ATCT, `automaticConsolidation: true`):

```
1T (root — no parentTcpId)
├── 1F (parentTcpId → 1T)
│   └── 1S (parentTcpId → 1F)
│       └── 1H (parentTcpId → 1S)
1G (root — separate chain, no parentTcpId)
```

When only 1T is staffed, it consolidates all descendants: `1T CON: 1T 1*` (all of subset 1). When 1F opens, it peels off with its children: 1T keeps `1T, 1G`, while 1F gets `1F, 1S, 1H`.

Larger facilities (NCT/NorCal TRACON) have ~40 TCPs across 8 subsets with deep hierarchies.

**Two types of consolidation:**
- **Basic** — Transfers future handoffs only; existing tracks stay at old TCP
- **Full** — Transfers all tracks (current + future) instantly

**`automaticConsolidation`** — Facility-level flag in ARTCC config. When `true`, positions automatically receive child TCPs on activation. When `false`, activating a position only claims its own TCP.

### Current State

| Component | Status |
|-----------|--------|
| `Yaat.Sim.Tcp` record with `ParentTcpId` | Complete |
| `ArtccConfig.TcpConfig.ParentTcpId` | Parsed from JSON, never read |
| `StarsConsolidationItemDto` (CrcDtos.cs) | Defined with MessagePack attrs, never instantiated |
| `DtoConverter.MapTcp()` | Complete |
| `ArtccConfigService.GetFacilityTcps()` | Returns TCP list for a facility |
| `PositionRegistry.IsPositionAttended()` | Checks if a TCP is held by a CRC client |
| `CrcBroadcastService.BuildInitialData()` | No `StarsConsolidation` case |
| `CrcBroadcastService.BuildClientPayloads()` | No `StarsConsolidation` case |
| `StarsConfig` model | Missing `AutomaticConsolidation` field |

### Protocol Reference

CRC subscribes to `Topic { Name = "StarsConsolidation", FacilityId = "NCT" }`.
Server responds with `ReceiveStarsConsolidationItems(topic, List<StarsConsolidationItemDto>)`.
Each DTO describes one TCP's consolidation state: who owns it, what children it has.

vatsim-server-rs sends one `StarsConsolidationItemDto` per active position's TCP. Each item includes `Children = [self]` (stub — real hierarchy not fully implemented there either). Our implementation should be more complete.

### Scope

This plan covers two repos:
- **yaat-server** (`..\yaat-server\src\Yaat.Server\`) — All consolidation logic, broadcast, state management
- **yaat** (`X:\dev\yaat\src\Yaat.Sim\`) — No changes expected; Tcp record already has ParentTcpId

---

## Phase 1: Automatic Consolidation (Static from Config + Attended Positions)

Compute consolidation from the TCP hierarchy and currently-attended positions. Broadcast to CRC on subscription and whenever positions open/close. No manual consolidation commands.

### 1.1 Config Model — Parse `automaticConsolidation`

Add the missing field to `StarsConfig` so we know whether to auto-consolidate.

- [x] Add `AutomaticConsolidation` property to `StarsConfig` in `ArtccConfig.cs`:
  ```csharp
  [JsonPropertyName("automaticConsolidation")]
  public bool AutomaticConsolidation { get; set; }
  ```

### 1.2 Consolidation Computation — `ArtccConfigService`

Add methods to build the consolidation hierarchy from config + attended positions.

- [x] Add `GetConsolidationItems(string artccId, string facilityId, Func<Tcp, bool> isAttended)` → `List<ConsolidationItem>`:
  - Build a children map: `Dictionary<string, List<Tcp>>` keyed by `ParentTcpId`
  - Identify root TCPs (those with no `ParentTcpId`)
  - For each TCP in the facility:
    - Walk UP: Find the nearest attended ancestor (or self if attended). This is the `owner`.
    - Walk DOWN from each attended TCP: Collect all descendant TCPs that don't have an intermediate attended TCP. These are the `children`.
  - Return one `ConsolidationItem` per TCP
- [x] Define internal `ConsolidationItem` record:
  ```csharp
  record ConsolidationItem(
      Tcp Tcp,
      Tcp? Owner,
      List<Tcp> Children,
      bool BasicConsolidation);
  ```
- [x] Handle `automaticConsolidation: false` — When disabled, each attended TCP only consolidates itself (no child inheritance). Unattended TCPs have no owner.
- [x] Handle root TCPs with no parent — These are consolidation roots. If unattended and auto-consolidation is off, they're orphaned.
- [x] Add `GetDefaultConsolidation(string artccId, string facilityId, Tcp tcp)` → `List<Tcp>`:
  - Returns the list of TCPs that would be consolidated under `tcp` if `tcp` were the only attended position. Used for the `DefaultConsolidation` field in the DTO.

### 1.3 DTO Conversion

- [x] Add `ToStarsConsolidationItem()` method to `DtoConverter.cs`:
  - Maps `ConsolidationItem` → `StarsConsolidationItemDto`
  - Uses existing `MapTcp()` for each field
  - Populates `DefaultConsolidation` from `GetDefaultConsolidation()`

### 1.4 Initial Data on Subscription

When CRC subscribes to `StarsConsolidation`, send the full consolidation state.

- [x] Add `"StarsConsolidation"` case to `BuildInitialData()` in `CrcBroadcastService.cs`:
  - Extract `artccId` from the client's position registry entry
  - Call `GetConsolidationItems()` with `isAttended` using `PositionRegistry.IsPositionAttended()`
  - Convert each item via `DtoConverter.ToStarsConsolidationItem()`
  - Return `BuildPayload("ReceiveStarsConsolidationItems", topic, items)` or null if empty

### 1.5 Broadcast on Position Change

When a CRC client activates or deactivates a session, consolidation state changes for all clients in the same facility.

- [x] Add `BroadcastStarsConsolidationAsync()` method to `CrcBroadcastService.cs`:
  - Pattern follows `BroadcastOpenPositionsAsync()`: iterate all clients, filter by subscription, build per-client payload
  - For each client subscribed to `StarsConsolidation`:
    - Rebuild consolidation items for the client's facility
    - Send full replacement list (not incremental — consolidation state is small)
- [x] Call `BroadcastStarsConsolidationAsync()` from `CrcClientState` after:
  - `HandleActivateSession()` — position opened
  - `HandleDeactivateSession()` — position closed
  - Disconnect cleanup — position removed
- [x] Also call from `ScenarioLifecycleService` when scenario loads/unloads (atc positions change open-position set)

### 1.6 Handoff Redirection

When the RPO initiates a handoff to a consolidated-away TCP, redirect to the consolidation parent.

- [x] In `TrackCommandHandler.HandleHandoff()`:
  - After resolving the target TCP, check if it is consolidated under a different attended TCP
  - If so, redirect: set `HandoffPeer` to the consolidation parent, set `HandoffRedirectedBy` to the original target
  - If the target TCP is attended directly, no redirect needed
- [x] Same logic for `ProcessStarsCommand` handoff path (CRC-initiated handoffs)

### 1.7 Auto-Accept Suppression for CRC-Controlled TCPs

`TickProcessor.ProcessAutoAccept()` currently skips auto-accept only for the exact TCP a CRC client holds (`IsPositionAttended(tcp)`). With consolidation, a controller at TCP 1T who consolidates 1F, 1S, 1H should also prevent auto-accept for handoffs to those child TCPs — the human controller is responsible for accepting them.

- [x] Add `IsTcpControlledByCrc(Tcp tcp)` method to `PositionRegistry` (or extend `IsPositionAttended`):
  - Returns `true` if *any* active CRC client's primary or secondary position covers this TCP
  - "Covers" means: the TCP matches the client's own TCP, OR the TCP is consolidated under the client's TCP according to the current consolidation state
  - This requires access to consolidation state — either pass it in, or give PositionRegistry a way to query it
- [x] Update `TickProcessor.ProcessAutoAccept()` (lines 482-488):
  - Replace `_positionRegistry.IsPositionAttended(tcp)` with the new consolidation-aware check
  - When a CRC client controls a TCP (directly or via consolidation), the handoff must wait for the human to accept — no auto-accept
- [x] Handle secondary positions: CRC clients can open secondary STARS positions. A secondary position's TCP and its consolidated children should also suppress auto-accept.

### 1.8 Tests

- [x] Unit test: Build TCP hierarchy from ZOA FAT config (5 TCPs), verify consolidation items:
  - All attended → each TCP owns only itself
  - Only root attended → root consolidates all descendants
  - Middle TCP attended → splits hierarchy correctly
- [x] Unit test: `automaticConsolidation: false` → each TCP only consolidates itself
- [x] Unit test: Handoff redirection to consolidated-away TCP resolves to parent
- [x] Unit test: Multiple roots in same facility handled correctly
- [x] Unit test: Auto-accept suppressed for TCP consolidated under an active CRC position
- [x] Unit test: Auto-accept proceeds for TCP with no CRC controller (direct or via consolidation)
- [x] Unit test: Secondary CRC position also suppresses auto-accept for its consolidated TCPs
- [x] Integration test: Subscribe to StarsConsolidation topic, verify initial data matches expected DTOs

---

## Phase 2: Manual Consolidation Commands

Enable controllers and RPO to manually consolidate/deconsolidate TCPs during a session, overriding the automatic hierarchy.

### 2.1 Per-Room Consolidation State

Automatic consolidation computes state purely from config + attended positions. Manual consolidation introduces **mutable overrides** that persist for the room session.

- [x] Create `ConsolidationState` class in `Yaat.Server/Simulation/`:
  - Thread-safe (room-scoped, mutated on command, read on broadcast)
  - Stores manual overrides: `Dictionary<string, ManualOverride>` keyed by TCP ID
  - `ManualOverride` record: `string ReceivingTcpId, bool IsBasic`
  - `Consolidate(Tcp receiving, Tcp sending, bool basic)` — Add/replace override
  - `Deconsolidate(Tcp tcp)` — Remove override, revert to automatic
  - `Clear()` — Remove all overrides
  - `RemoveOverridesInvolving(string tcpId)` — Remove overrides where TCP is sender or receiver
  - `GetSnapshot()` — Thread-safe snapshot for testing/inspection
- [x] Add `ConsolidationState` property to `TrainingRoom`
- [x] Initialize on room creation; clear on scenario unload
- [x] Integrate with `GetConsolidationItems()` and `GetConsolidationOwner()` (optional `ConsolidationState?` parameter)
- [x] Pass `ConsolidationState` through `CrcBroadcastService`, `TickProcessor`, `TrackCommandHandler`, `CrcClientState.Stars`

### 2.2 Basic Consolidation

Transfers future handoffs to the receiving TCP but existing tracks stay at the sending TCP until manually moved.

- [x] Implement basic consolidation in `ConsolidationState.Consolidate(receiving, sending, basic: true)`:
  - Override: all handoffs to `sending` redirect to `receiving`
  - `sending` TCP's consolidation item shows `Owner = receiving, BasicConsolidation = true`
  - Existing tracks with `Owner.SectorId == sending` are NOT transferred
- [x] SSA indication: `BasicConsolidation = true` in the DTO tells CRC to show `*` prefix

### 2.3 Full Consolidation

Transfers all tracks (current + future) to the receiving TCP instantly.

- [x] Implement full consolidation in `ConsolidationState.Consolidate(receiving, sending, basic: false)`:
  - Same handoff redirection as basic
  - Additionally: iterate all aircraft in `SimulationWorld`, transfer ownership of tracks where `Owner` matches `sending` TCP to `receiving` TCP
  - No acceptance required by receiving TCP
- [x] Track transfer in `RoomEngine.TransferTracksForConsolidation()`:
  - Iterate world snapshot, mutate `Owner` on matching aircraft
  - Update `HandoffPeer` on in-progress handoffs targeting the sending TCP with `HandoffRedirectedBy`

### 2.4 RPO Commands

- [x] Add `CanonicalCommandType.Consolidate`, `ConsolidateFull`, `Deconsolidate`
- [x] Add parsed command records:
  - `record ConsolidateCommand(string ReceivingTcpCode, string SendingTcpCode, bool Full) : ParsedCommand`
  - `record DeconsolidateCommand(string TcpCode) : ParsedCommand`
- [x] Add ATCTrainer patterns:
  - `CON {receiving} {sending}` → Basic consolidation
  - `CON+ {receiving} {sending}` → Full consolidation
  - `DECON {tcp}` → Deconsolidate
- [x] Add to `CommandMetadata.AllCommands` and `CommandScheme.Default()`
- [x] Global command handling in `MainViewModel.HandleGlobalCommand()`
- [x] Server-side parsing in `CommandParser` + dispatch in `RoomEngine.HandleConsolidationCmd()`

### 2.5 CRC Command Support

CRC sends consolidation commands via `ProcessStarsCommand`.

- [x] Handle `StarsCommandType.MultiFunc` in `CrcClientState.Stars.CrcMultiFunc()`:
  - Parse D+/D- patterns for consolidation/deconsolidate
  - `SplitTcpCodes()` helper for parsing concatenated TCP codes
  - Resolve TCPs via `ArtccConfigService.FindTcpByCode()`
  - Call `ConsolidationState.Consolidate()` or `.Deconsolidate()`
  - Broadcast updated consolidation state
- [x] Handle display-active-consolidations command (`MULTIFUNC D+ENTER`):
  - CRC handles display client-side from the DTO data — server just needs correct DTOs (no server action needed)

### 2.6 Consolidation on Position Deactivation

When a position closes, its manual consolidations must be resolved.

- [x] `CleanUpConsolidationOverrides()` in `CrcClientState.Session.cs`:
  - Calls `ConsolidationState.RemoveOverridesInvolving()` for the deactivating position's TCP
  - Removes all overrides where the TCP is sender or receiver
- [x] Called from `HandleDeactivateSession()` and `BroadcastDisconnected()` (disconnect cleanup)
- [x] Broadcast updated state after cleanup (existing attendance broadcast triggers consolidation re-broadcast)

### 2.7 Tests

- [x] Unit test: Basic consolidation — future handoffs redirect, existing tracks stay
- [x] Unit test: Full consolidation — items show non-basic flag
- [x] Unit test: Deconsolidate — reverts to automatic hierarchy
- [x] Unit test: Position close — manual overrides cleaned up correctly
- [x] Unit test: Scenario unload — consolidation state cleared
- [x] Unit test: Manual override on top of automatic consolidation produces correct merged items
- [x] Unit test: Manual override with auto-consolidation disabled still applies
- [x] Unit test: Receiving TCP unattended — walks up to attended ancestor
- [x] Unit test: Consolidate replaces previous override
- [x] Unit test: Children list correctness — receiving TCP children updated, descendants follow override
- [x] Unit test: ConsolidationState unit tests (consolidate, deconsolidate, clear, removeInvolving, snapshot)
