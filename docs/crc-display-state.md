# CRC Display State & Broadcast

> Read this before touching `CrcClientState`, `CrcBroadcastService`, `CrcVisibilityTracker`, `DtoConverter`, or any `CrcDtos*.cs` file. Adding fields to ERAM/STARS/ASDEX/TowerCab data without following the contract here leads to silent gaps where mid-session subscribers don't see state. For the consolidation hierarchy and per-track shared display state (STARS/ERAM pointouts, consolidation ownership) that rides these topics, see [track-sharing-and-consolidation.md](track-sharing-and-consolidation.md).

## Architecture in one diagram

```
                    ┌────────────────────────────────────────┐
                    │ AircraftState  +  RoomEngine (Sim)     │
                    └─────────────────┬──────────────────────┘
                                      │
                  ┌───────────────────┼─────────────────────┐
                  ▼                   ▼                     ▼
          DtoConverter.To*     CrcVisibilityTracker    PositionRegistry
          (per-DTO mapping)    (visibility + coast)    (TCP attendance)
                  │                   │                     │
                  └───────┬───────────┴──────────┬──────────┘
                          ▼                      ▼
                  CrcBroadcastService.BroadcastUpdates / Broadcast<Topic>
                                  │
                                  ▼   (MessagePack, SignalR Receive*)
                       per-CrcClientState subscribers
```

CRC clients connect over a separate WebSocket (not the YAAT SignalR hub). YAAT's own clients use JSON; CRC uses MessagePack. The same `RoomEngine` / `AircraftState` feeds both.

## `CrcClientState` — per-connection identity & subscription

(`yaat-server: src/Yaat.Server/Hubs/CrcClientState*.cs`)

Split across many partial classes (`CrcClientState.cs`, `CrcClientState.Session.cs`, `CrcClientState.Stars.cs`, `CrcClientState.Eram.cs`, `CrcClientState.Asdex.cs`, `CrcClientState.FlightPlan.cs`, `CrcClientState.Strips.cs`, `CrcClientState.Messaging.cs`, `CrcClientState.Secondary.cs`, `CrcClientState.Info.cs`).

Per-connection fields:

- `_clientId` — GUID prefix used as a token.
- `_cid` — VATSIM CID (resolved during the negotiate handshake from `CrcNegotiateTokenStore`).
- `_artccId`, `_currentPositionId` — position context.
- `_displayName`, `_realName` — controller identity.
- `_isActive` — session activation flag.
- `_subscribedTopics`, `_subscriptions` — subscription state.
- `_roomEngine` — the room this client is attached to (`null` while in lobby).
- `_sysUid` — signed 32-bit hash, **not** the CID. CRC computes this client-side; we just trust it.

## Topics

A topic is a subscription channel keyed by `(Name, FacilityId, Subset?, SectorId?)`. Subscribers receive `Receive<Topic>` (updates) and `Delete<Topic>` (removals). Categories:

- **Position / Consolidation**: `OpenPositions`, `StarsConsolidation`.
- **STARS**: `StarsTracks`, `StarsLineNumbers`, `StarsShortTermConflicts` (ATPA), `StarsCoordination`.
- **ERAM**: `EramTargets`, `EramDataBlocks`, `EramTracks`, `EramSectorConfiguration`, `EramRouteLines`, `EramCrrGroups`.
- **ASDEX**: `AsdexTargets`, `AsdexTracks`, `AsdexTempData`, `AsdexTempDataPresets`, `AsdexSafetyLogicConfiguration`, `AsdexHoldBars`, `AsdexAlerts`.
- **SAAB SAID** (CRC 2.17 surface display — ASDE-X family, minus alerts/safety-logic/hold-bars): `SaabSaidTargets`, `SaabSaidTracks`, `SaabSaidTempData`, `SaabSaidTempDataPresets`. The track DTO adds a `HasFlightPlanData` flag (Key 2); facility config is the nested `saidConfiguration.saabConfiguration` shape and carries no range/ceiling (defaults to the ASDE-X 15 nm / 1500 ft).
- **Tower Cab / Ground**: `TowerCabAircraft`, `GroundTargets`.
- **Flight data**: `FlightPlans`, `FlightStrips`.
- **Weather**: `NexradData`.

## Broadcast triggers — `CrcBroadcastService.cs`

Two broadcast modes:

### Periodic — `BroadcastUpdates`

Runs once per sim-second after PostPhysics ([tick-loop.md](tick-loop.md)). For each aircraft:

1. `CrcVisibilityTracker.Evaluate` decides STARS visibility, coast phase, ASDEX airport membership, Tower Cab visibility, ground-target status.
2. `DtoChangeFlags` from per-aircraft change tracking (duplicate beacon codes, ATPA, etc.) coalesce with fingerprint changes.
3. For each subscription that overlaps, emit either an update payload or a delete.

### Event-driven

Triggered by specific state changes:

| Helper | When it fires |
|---|---|
| `BroadcastOpenPositionsAsync` | RegisterPosition / SetPositionActive / disconnect |
| `BroadcastStarsConsolidationAsync` | position registry changes (TCP attendance) |
| `BroadcastConflictAlertsAsync` | ATPA detector add/clear |
| `BroadcastCoordinationAsync` | coordination list / tower list changes |
| `BroadcastDeletesAsync` / `BroadcastStarsDeletesAsync` | aircraft departure |
| `BroadcastEramSectorConfigurationAsync` | targeted facility update |
| `BroadcastNexradDataAsync` | weather pull |
| `BroadcastToTopicSubscribersAsync` | generic by topic + facility filter |
| `BroadcastToCrcClientsAsync` | room-wide with optional client/callsign filter |

### Rewind / recording-load resync

A timeline scrub (`RewindAsync` / `RewindFromSnapshotAsync`) or recording load (`LoadRecordingAsync` / `LoadRecordingArchiveAsync`) clears the world and rebuilds it at the target time. Because CRC is **additive**, the `RoomEngine` wrappers around those four operations capture the callsigns present *before* the reload and, on success, call `BroadcastDeletesAsync` for each (`ResyncCrcAfterReload`) so tracks not active at the target time don't linger on STARS / Tower Cab. They also re-broadcast `BroadcastOpenPositionsAsync` + `BroadcastStarsConsolidationAsync` so the owning-sector display matches the restored state. Aircraft present at the target time are re-added by the next periodic broadcast — the reload clears the change tracker, forcing a full re-send. `BroadcastDeletesAsync` also purges that callsign's `CrcVisibilityTracker` state, so a wholesale world swap doesn't leave stale "visible on STARS / Tower Cab" flags.

### Initial data on subscribe

`CrcClientState.HandleSubscribe` calls `BuildInitialData(topic, _roomEngine)` so newly-subscribed clients see the current snapshot of that topic, not just future deltas. **If you forget to extend the initial-data builder when adding a field, mid-session subscribers won't see it until the next change.** This is the biggest footgun — see "Adding fields" below.

## DTO mapping — `DtoConverter.cs`

Pure transformations from internal state to wire DTOs:

- `ToStarsTrack(ac)` — `AircraftState` → `StarsTrackDto` (coast phase, ATPA targets, owner, line, scratchpads, temp alt, cruise, …).
  - `StarsTrackDto.TpaType` (Key 30, CRC's `RemoteTpaType`) is **reserved for the automatic ATPA cone** — it is set only from the server-computed `AtpaResult` cone state via `MapAtpaTpaType`. The instructor's manual `JRING`/`CONE` (`ac.Stars.TpaType` + `TpaSize`) is **not** mapped here: it is an instructor-only overlay drawn on YAAT's own radar (`AircraftStateDto.TpaType`/`TpaSize` → `TargetRenderer`), never projected to the student's CRC. CRC controllers draw their own manual TPA graphics locally and sync them via shared track state.
- `ToFlightPlan(ac)` — filed alt/route/clearance.
- `ToEramTarget` / `ToEramTrack` / `ToEramDataBlock` — position-based ERAM symbology.
- `ToAsdexTarget` / `ToAsdexTrack` — airport range/altitude envelope filtering.
- `ToTowerCab(ac, fieldElev)` — AGL gate.
- `ToStarsConsolidationItem(tcp, …)` — TCP hierarchy plus defaults.
- `ToShortTermConflict`, `ToStarsCoordinationList`, `ToTowerListDto` — multi-aircraft state.

Wire encoding is MessagePack with `[Key(N)]`-attributed records; payloads are written via `MessagePackWriter` and pushed through SignalR `Receive*` invocations.

## WebSocket lifecycle — `CrcWebSocketHandler.cs`

1. **Negotiate** — client POSTs an empty body; server returns `{}\x1e`. The CID is supplied via a separate negotiate-token endpoint and stored in `CrcNegotiateTokenStore`.
2. **Connect** — accept WebSocket; resolve `(cid, room) → RoomEngine`; create `CrcClientState`; register in `CrcClientManager`.
3. **Handshake** — first inbound byte is `{` (handshake JSON); consume and ack with `{}\x1e`.
4. **Receive loop** — parse MessagePack frames as SignalR invocations; dispatch to handlers (`HandleStartSession`, `HandleSubscribe`, `HandleProcessStarsCommand`, …).
5. **Disconnect** — clean up primary + secondary positions, unregister from `CrcClientManager`, broadcast consolidation/lobby state.

`HandleStartSession` parses a 12-element MessagePack array: `[ArtccId, FacilityId, PositionId, Role, Rating, ClientName, ClientVersion, RealName, ControllerInfo, SysUid, EramType?, IsPseudoController]`.

## `CrcVisibilityTracker.cs`

Per-aircraft state machine (`AircraftCrcState`):

- `IsVisibleOnStars` + `IsCoasting` — STARS floor is AGL ≥ 100 ft; on exit, hysteresis to field elevation; coast for 5 s before delete; re-ascent during coast cancels deletion. Airborne ghosts auto-resolve.
- `VisibleAsdexAirports` — per-airport, range + altitude ceiling per ASDEX config; ±600 ft hysteresis at the ceiling; tracks newly-visible / removed transitions.
- `IsVisibleOnTowerCab` — within 20 nm and ≤ 4000 ft AGL.
- `IsVisibleAsGroundTarget` — inverse of STARS for non-ghost aircraft below threshold.

Room-isolation is enforced here: visibility uses the room's airport list (subscribed from ARTCC config).

## `CrcSessionLifecycle.cs` — the right way to mutate position state

Two helpers that wrap `PositionRegistry` mutations with the required broadcasts:

- `RegisterPosition(...)` — register + broadcast `OpenPositions`.
- `SetPositionActive(...)` — flip active flag + broadcast `StarsConsolidation` + `OpenPositions`.

**Always go through these.** Calling `PositionRegistry` directly from `HandleStartSession` / `HandleActivateSession` / `HandleDeactivateSession` skips the broadcasts and leaves peer clients with stale consolidation/lobby state. See the `project_crc_session_lifecycle` memory.

## Flight strips — slightly different

Strip mutations from CRC flow:

```
CRC strip DTO → StripCommandTranslator.Build*Canonical → canonical command string
              → RoomEngine.RecordAndDispatchStripAsync → same path as terminal-typed strip commands
```

Strips are **item-based** (bay/rack/index), not aircraft-owned. Mutations replay through the same recorded-command path, so history stays divergence-free.

`BuildFlightStripsData` emits the full `FlightStripsStateDto` on subscribe — bay/printer layout — so mid-session subscribers don't see a pile of unparented strip items.

See [flight-strips.md](flight-strips.md) for the full strip architecture.

## `TrackOwner` & how ownership broadcasts

`TrackOwner` is `(Callsign, FacilityId, Subset, SectorId, OwnerType)` (MessagePack `[Key 0..4]`). It rides along inside `StarsTrackDto.Owner` / `HandoffRedirectedBy` — there is **no separate ownership topic**. Ownership changes coalesce with the next `StarsTracks` update.

## Adding fields to display state — the contract

When you add a field that should appear on a CRC client, you must touch **all** of these:

1. **DTO** — add the field to the appropriate `*Dto` record with the next free `[Key(N)]`.
2. **`DtoConverter.To<X>`** — populate from `AircraftState` or room state.
3. **Change tracking** — extend `DtoChangeFlags` (or the relevant change tracker) so periodic broadcasts notice when the field changes.
4. **Initial-data builder** — `BuildInitialData` for that topic, so subscribers joining mid-session see the current value.
5. **Snapshot** — wire `ToSnapshot` / `FromSnapshot` if it's per-aircraft state. Don't defer with "runtime-only" unless the field genuinely re-derives from other state on the next tick.
6. **Visibility tracker** — only if the field affects visibility / scope (room isolation, per-facility, etc.).

Skip step 4 and you'll see the field update for clients that were subscribed when it changed, but newly-subscribed clients won't see it. Skip step 5 and the field disappears across snapshot round-trips. The `feedback_serialize_display_state` memory was written after we hit this exact bug.

## Pitfalls

- **Don't call `PositionRegistry` from session handlers.** Use `CrcSessionLifecycle` so the broadcasts fire.
- **CRC's delta keypress is U+0080, not backtick.** Normalize once at the parser boundary; don't add per-handler workarounds. (See `project_crc_delta_wire_encoding`.)
- **A delta-prefixed implied entry is an interfacility handoff, not a scratchpad.** After normalization, a leading backtick on an implied slew (e.g. `` `3`` → FAT, `` `31H`` → FAT Chandler) decodes via the sender facility's `starsHandoffIds` (`ArtccConfigResolver.ResolveStarsHandoffCode`) and initiates a handoff — it must never fall through to the `SP1` primary-scratchpad write. `ResolveTcpToOwner` chains `ResolveTcpCode → ResolveEramCode → ResolveStarsHandoffCode` so the canonical `HO `{code}` resolves the same way the inbound implied path routes it. A delta entry that doesn't decode returns `ILL POS`.
- **Mid-session subscribers see only initial data + future deltas.** If your "update" only fires through change tracking, late joiners get whatever `BuildInitialData` returns — extend both.
- **`SysUid` is not the CID.** It's a signed 32-bit hash CRC computes client-side. Don't try to map it back to a VATSIM CID.
- **MessagePack `[Key]` is positional and load-bearing.** Renumbering breaks every connected CRC client. Always append.
- **Tower-vs-TRACON facility resolution is sticky.** Bays resolve through the facility hierarchy; getting this wrong sends a strip command to the wrong facility (see [flight-strips.md](flight-strips.md) for the gotcha).
- **`Ground.Layout` is `[JsonIgnore]`.** ASDEX visibility uses runtime layout, not snapshot data — restore re-resolves layouts by airport ID.
- **CRC FP amendment sends equipment in two fields.** `CreateOrAmendFlightPlan` Key 2 `Equipment` is an ICAO display string (e.g. `C182/L-DOV/C`); Key 16 `FaaEquipmentSuffix` is the canonical suffix. Use `FlightPlanNormalization.ResolveTypeAndSuffix`: split Key 2 on the first `/` for the type, prefer Key 16 for the suffix. Slash-splitting Key 2 for the suffix yields garbage like `L-DOV/C`.
- **CRC clients are born in the lobby with `RoomId=""`.** Position-registry entries are created at `HandleStartSession` (before any room bind), so `TryBindToRoom`/`UnbindFromRoom` must call `SyncRegistryRoomId` → `CrcSessionLifecycle.SetPositionRoom` for the primary and every secondary. Otherwise the auto-accept attendance gate (requires `IsActive` AND `RoomId==roomId` AND `Tcp.Id==tcp.Id`) misses the active student and steals the track.
- **Duplicate-beacon detection must filter to discrete codes.** `ComputeDuplicateBeaconCodes` skips non-discrete (XX00) codes to mirror CRC's `IsDiscrete`; without it, shared codes like 1200 (VFR) and the SPCs render "DB" on STARS / "DUP BCN" on ASDEX. CRC trusts the `IsDuplicateBeaconCode` flag wholesale, so the server is the only place to prevent false positives.
