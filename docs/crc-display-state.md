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
- **ERAM**: `EramTargets`, `EramDataBlocks`, `EramTracks`, `EramSectorConfiguration`, `EramRouteLines`, `EramShortTermConflicts`, `EramCrrGroups`.
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

The reconstruction runs under `room.IsBroadcastSuppressed = true` for the whole teardown→rebuild window, and `BroadcastUpdatesAsync` **skips suppressed rooms** (mirroring `DetectChanges`). This is load-bearing: the reload briefly repopulates the world with the *full initial scenario* before restoring the target snapshot, and the periodic CRC broadcast runs outside the tick gate — without the skip it would snapshot that transient world and push `ReceiveStarsTracks` adds for aircraft absent at the target time. Those additive tracks are never deleted (the aircraft is gone from the world, so `EvaluateSnapshot` never processes it again), so they survive as STARS ghost tracks until the CRC client reconnects and `BuildInitialData` rebuilds from the live snapshot.

### Initial data on subscribe

`CrcClientState.HandleSubscribe` calls `BuildInitialData(topic, _roomEngine)` so newly-subscribed clients see the current snapshot of that topic, not just future deltas. **If you forget to extend the initial-data builder when adding a field, mid-session subscribers won't see it until the next change.** This is the biggest footgun — see "Adding fields" below.

## DTO mapping — `DtoConverter.cs`

Pure transformations from internal state to wire DTOs:

- `ToStarsTrack(ac)` — `AircraftState` → `StarsTrackDto` (coast phase, ATPA targets, owner, line, scratchpads, temp alt, cruise, …).
  - `StarsTrackDto.TpaType` (Key 30, CRC's `RemoteTpaType`) is **reserved for the automatic ATPA cone** — it is set only from the server-computed `AtpaResult` cone state via `MapAtpaTpaType`. The instructor's manual `JRING`/`CONE` (`ac.Stars.TpaType` + `TpaSize`) is **not** mapped here: it is an instructor-only overlay drawn on YAAT's own radar (`AircraftStateDto.TpaType`/`TpaSize` → `TargetRenderer`), never projected to the student's CRC. CRC controllers draw their own manual TPA graphics locally and sync them via shared track state.
- `ToParkedDataBlock(ac)` — STARS Track Reposition (`TRK RPOS`) Form 2: when `ac.DataBlock.Binding == Parked`, the broadcast loop emits a **second** `StarsTrackDto` (id `RPOS{callsign}`, `IsUnsupported`, no surveillance) at the parked location carrying the flight plan, while `ToStarsTrack` renders the aircraft's own track as a bare unassociated LDB (blank `AircraftId`, `Owner = null`). `CrcVisibilityTracker` keeps a parked aircraft visible regardless of the floor; `AircraftChangeTracker` fingerprints `DataBlock`, and the broadcast layer deletes the stale `RPOS{callsign}` id on un-park or removal (CRC is additive).
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

- `IsVisibleOnStars` + `IsCoasting` — an aircraft on the airport surface (`AircraftState.IsOnGround`) is **never** on STARS/ERAM and is **always** a ground target, regardless of altitude — a hard veto checked before the floor, mirroring `RadarCanvas.FilterAircraft` (which hides on-ground aircraft outright). This keeps a taxiing aircraft off terminal radar even when field-elevation resolution drifts below its stored altitude or stale hysteresis survives a timeline scrub. For airborne aircraft the STARS floor is AGL ≥ 100 ft (`FieldElevationResolver.AcquisitionFloorAglFt`); on exit, hysteresis to field elevation; coast for 12 s before delete (`CoastDurationSeconds`, ~2 missed ASR sweeps); re-ascent during coast cancels deletion. Airborne ghosts auto-resolve. YAAT's own radar mirrors this floor: the training-hub DTO carries `BelowDisplayFloor` (same `FieldElevationResolver` field-elevation logic) and `RadarCanvas.FilterAircraft` withholds an airborne target while its displayed altitude rounds to `000`.
- `VisibleAsdexAirports` — per-airport, range + altitude ceiling per ASDEX config; ±600 ft hysteresis at the ceiling; tracks newly-visible / removed transitions.
- **Pure phantoms are excluded from every surface display; ghost *overlays* are not** — ASDE-X (`GetVisibleAsdexAirports` / `EvaluateAsdex`), SAAB SAID (`GetVisibleSaidAirports` / `EvaluateSaid`), Tower Cab (`GetTowerCabVisibility` / `EvaluateTowerCab`), and `GroundTargets` all early-out on `CrcVisibilityTracker.IsPurePhantom` — `ac.Ghost.IsUnsupported && !ac.Ghost.IsOverlay`. A manually typed STARS data block (DA/VP) is an uncorrelated radar-scope annotation with no surface return / physical body, so it belongs on the STARS scope only (it sits at field level over the airport, which would otherwise pass every surface range+ceiling gate). The per-tick `Evaluate*` methods emit deletes for any airport a track was on when it becomes a phantom (#293). A **ghost overlay** (`GHOST` placed on a real scenario aircraft — e.g. a departure ghost off the runway end) rides an aeroplane that exists: only its *STARS* position is displaced, and it must stay on every surface display. Gating the surface displays on `IsUnsupported` alone made ghosted departures vanish from Tower Cab until the 100 ft AGL auto-resolve in `EvaluateStars` cleared the flag (#300). This matches the client-side rule in `MainViewModel.ShouldShowAircraft` / `GroundCanvas`.
- `IsVisibleOnTowerCab` — within 20 nm and at or below the facility's configured `aircraftVisibilityCeiling` (`TowerCabAirportInfo.VisibilityCeiling`), which is **MSL**, not AGL — same datum as the ASDE-X `targetVisibilityCeiling`. Real ZOA data settles it: every facility is 6000 except Reno at 10000, and Reno is the only high-elevation field (4415 ft). Don't reintroduce a `fieldElevation + N` form here; it silently under-covers high-elevation towers.
- `IsVisibleAsGroundTarget` — inverse of STARS for non-ghost aircraft: on-ground aircraft (or those below the AGL threshold) feed the surface display / `GroundTargets`.
- **On-ground → immediate delete, not coast.** The `IsOnGround` veto returns `CrcAction.Delete` right away (clearing any coast state) rather than routing a landed/taxiing aircraft through the STARS coast — a surface target is never a coasting terminal return (coast bridges *airborne* missed returns, 7110.65 §5-3-4). The coast timer that *does* apply is measured in **sim-elapsed seconds**, not wall-clock: `Evaluate`/`EvaluateStars` take a required `double simElapsedSeconds` (fed `room.ActiveScenario.ElapsedSeconds`), so a 12 s coast stays 12 s regardless of `SimRate`. A wall-clock timer lingered up to 5×`SimRate` seconds because the host advances `SimRate` sim-seconds per tick while broadcasting once per tick.

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

## SAAB SAID surface display

CRC 2.17 (2026-06) added **SAAB SAID**, a surface-awareness display in the ASDE-X family. YAAT emulates it server-side only — real CRC clients render it; there is no YAAT instructor view (same posture as ASDE-X). SAID ≈ **ASDE-X minus alerts / safety-logic / hold-bars, plus a `HasFlightPlanData` track flag**, behind a `SaidVendor {UAvionix, Saab, Indra}` abstraction (only Saab implemented).

Every site mirrors the ASDE-X stack: `Said*` state on `AircraftStarsState` (separate fields, not reused `Asdex*`); `SaidConfig`/`SaabSaidConfig` in `ArtccConfig.cs`; `SaidRoomState` + `SaidMutationApplier`; `DtoConverter.ToSaid*`; `SaidTarget`/`SaidTrack` fingerprints; `CrcVisibilityTracker.EvaluateSaid`; `CrcClientState.Said.cs` (reuses the ASDE-X wire helpers). `saidConfiguration` JSON is vendor-nested with **no range/ceiling**, so it reuses the ASDE-X defaults (15 nm / 1500 ft + 600 ft hysteresis, tower-centered). Topic categories serialize as **string names**, so the mid-enum vNAS `SaabSaid*` `TopicCategory` insertions don't shift yaat-server parsing — just add the four string cases.

- **Suspend mirrors ASDE-X; don't hide/clamp.** `DtoConverter.ToSaidTrack` emits `Suspended` when `stars.SaidSuspended` (parallels `ToAsdexTrack`); the aircraft stays on the surface display, only the track status changes, and only `SaidTerminated` removes it. CRC once crashed rendering a `Suspended` SAID track (its "mega font" only supported `FontType.Asdex`); YAAT had a temporary hide+clamp workaround, but **CRC fixed it and the workaround was reverted — do not re-introduce `IsHiddenFromSaid` or status-clamping.**
- **Disconnect must delete the surface target for *every* coast status, not just `Dropped`.** A coasting surface track has no live return — CRC draws the coast icon from the *track*, and a target left alive orphans an uncorrelated blip that NRE-crashes CRC on click (`GetByCorrelatedAircraftId` returns null). `CrcBroadcastService.BroadcastDisconnectAsync` sends `DeleteSaabSaidTargets`/`DeleteAsdexTargets` for all coast statuses. Regression: `CrcDisconnectSurfaceTargetTests`.

## STARS keyboard & slew commands — `CrcClientState.Stars.cs`

CRC STARS per-track keyboard/slew amends are handled in `CrcClientState.Stars.cs`. Most route through the single-owner canonical pipeline via `DispatchCrc(callsign, "<verb>", identity)` → `RecordAndDispatch` (replay-durable, inheriting ownership/undo/broadcasts for free): `SP1`/`SP2`, `PRA n`, `TA n`, `CRUISE n`, `CAINH`, `DROP`. Only two paths mutate state directly and warrant scrutiny: Mode-C inhibit (a display bool) and beacon.

- **Beacon amend sets `AssignedCode` only, never `Code`.** A STARS beacon amend (`M<FLID> ####`, the FP Editor beacon field, or `AmendFlightPlan(BeaconCode:)`) writes `Transponder.AssignedCode`; the pilot keeps squawking `Transponder.Code` until told `SQ` (which snaps `Code = AssignedCode`). The datablock mismatch until then is intentional, and `FpCreatorAutoTrack` correctly won't auto-acquire while `Code != AssignedCode`. Beacon input is **octal** — reject digits 8/9 with `FORMAT`.
- **Keyboard `<HND OFF>` arrives with `TrackId = null`.** A bare keyboard handoff-accept carries `Type=Handoff, Param=<callsign>, TrackId=null` (CRC auto-fills the nearest inbound handoff's callsign); clicking the datablock instead sends `Implied` with the trackId. When `type==Handoff && trackId==null && param non-empty`, resolve the aircraft by the trailing FLID token (`ResolveKeyboardFlid`), then `CrcHandoffCommand` routes it (single token = accept inbound / recall outbound via `CrcHandoffAcceptOrRecall`; two tokens = initiate). Resolving only by the clicked `trackId` regressed the keyboard path to `TRACK NOT FOUND`.
- **TRK RPOS Forms 1 & 3 un-park in place.** (Form 2's parked-datablock split is under **DTO mapping**, above.) Forms 1/3 emit `RPOSMOVE {from} {to}`; because YAAT has one surveillance source per callsign, a parked datablock can only re-bind to its *own* track (AID must match — enforced `from==to` in the hub→handler); cross-flight rebind is rejected `ILL TRK`. `RPOSLOC`/`RPOSMOVE` are server-internal canonicals (CRC-handler only, never user-typed), routed through `DispatchCrc` + a `RecordingManager` Reposition kind. Snapshot schema went V13→V14 (`DataBlock` nullable, defaults `Bound`).

## Scenario unload — resetting per-room CRC state

`TrainingRoom` is reused across scenarios, so its per-room CRC session state leaks into the next scenario unless `ScenarioLifecycleService.ExecuteUnloadScenario` resets **and re-broadcasts** it (both the hub-unload path and load-over-existing go through here). CRC topics are additive, so a server-side `Reset()` alone leaves stale items on screen — the broadcast is the load-bearing half.

- `StripState.Reset()` + `StripBroadcaster.BroadcastFullStateAsync`; `TdlsState.Reset()` + `TdlsBroadcaster.BroadcastFullStateAsync`.
- `LineNumbers.Reset()` (server-only; the per-callsign delete loop already emits `DeleteStarsLineNumbers`).
- `AsdexState.Reset()` + `BroadcastAsdexTempDataClearAsync`, and `EramState.ClearScenarioArtifacts()` (route lines) + `BroadcastEramRouteLinesClearAsync` — the per-callsign delete loop only clears *per-aircraft* artifacts, so non-per-aircraft ones (ASDE-X temp markers, ERAM route lines) need these room-wide topic clears.
- **Preserve** ERAM `SectorConfigurations` (velocity-vector length, CRR color) and `QuickLook` — controller-workstation display *preferences*, not scenario data; wiping them on unload is a regression.

This is a different "four" than the *simulation* spawn queues that [scenario-loading-and-generation.md](scenario-loading-and-generation.md) says unload clears. Tests: `ScenarioUnloadWipesStateTests`.

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
- **An FPE amend on a planless target files the plan.** YAAT emits a blank `FlightPlanDto` for radar-only cold-call targets (`DtoConverter.ToFlightPlan`, `HasFlightPlan==false` branch), so CRC's Flight Plan Editor submits an `AmendFlightPlan` (not Create) for them. `SimulationEngine.AmendFlightPlan` therefore promotes a planless target to a filed plan — sets `HasFlightPlan=true` and draws a discrete beacon from `BeaconCodePool` (VFR/IFR bank) when `AssignedCode==0` — so the editor and the "recycle beacon" button surface a code. It is the single owner of "filing establishes the plan + assigns a beacon"; the typed `DA`/`VP`/`NEW` create path reaches it through its own amend. `RequestNewBeaconCode` (recycle) is recorded as `RecordedRequestNewBeaconCode` so it replays on rewind, and `BeaconCodePool` cursors round-trip in `BeaconCodePoolDto`.
- **CRC clients are born in the lobby with `RoomId=""`.** Position-registry entries are created at `HandleStartSession` (before any room bind), so `TryBindToRoom`/`UnbindFromRoom` must call `SyncRegistryRoomId` → `CrcSessionLifecycle.SetPositionRoom` for the primary and every secondary. Otherwise the auto-accept attendance gate (requires `IsActive` AND `RoomId==roomId` AND `Tcp.Id==tcp.Id`) misses the active student and steals the track.
- **Duplicate-beacon detection must filter to discrete codes.** `ComputeDuplicateBeaconCodes` skips non-discrete (XX00) codes to mirror CRC's `IsDiscrete`; without it, shared codes like 1200 (VFR) and the SPCs render "DB" on STARS / "DUP BCN" on ASDEX. CRC trusts the `IsDuplicateBeaconCode` flag wholesale, so the server is the only place to prevent false positives.
- **There is no "blue" STARS datablock.** Datablock colors are white = `sColorOwnedDataBlock`, LimeGreen = `sColorUnownedDataBlock`, yellow = `sColorPointOut`, cyan = `sColorHighlightedDataBlock` (from the decompiled CRC — see CLAUDE.md → Reference Docs for the repo path). The blue `FromArgb(30,120,255)` is the target *symbol* color, not a datablock — don't reach for a "blue datablock" state that doesn't exist.
- **Unsupported (ghost) STARS tracks carry no history trail.** A ghost overlay (`GHOST` command → `ac.Ghost.IsUnsupported` + `Ghost.Latitude/Longitude`) overrides `StarsTrackDto.Location` to the placed lat/lon, but CRC draws the blue history trail (`sColorHistoryTrails`) at each `History[i].Location` *independently* of `Location`. If `DtoConverter.ToStarsTrack` populated `History` from the aircraft's real `PositionHistory`, the trail rendered as a stray blue dot offset from the ghost (the aircraft is typically parked/below-floor, so all samples cluster at one real spot). `ToStarsTrack` therefore emits `History = []` for an unsupported ghost, matching `ToParkedDataBlock` and real STARS (an unsupported data block "is not currently supported by radar data, and never was" — `docs/crc/stars.md`).
- **The STARS readout (preview) area is the single-client push channel.** `CrcClientState.SendStarsReadoutAreaAsync(lines)` sends an unsolicited `ReceiveStarsReadoutArea` (`StarsReadoutAreaDto{Tcp, Lines}`) over *this* client's own WebSocket; CRC renders it in the STARS preview area, pixel-identical to a native command rejection like `ILL TRK`, auto-clearing after 15 s. It is scoped by the client's own TCP, so it reaches only the requesting controller. Use it to give feedback on tool-window actions whose ack CRC discards — e.g. an FP-editor `AmendFlightPlan` or `RequestNewBeaconCode` rejected for a track another sector owns, surfaced as `ILL TRK` (the STARS-native `RequireOwnership` term); the amend hub method is a void `Task` the client never reads, so a bare nil-ack is indistinguishable from a bug. Do **not** reach for `BroadcastCrcTerminal` for this — it sends a `TerminalBroadcast` to the *instructor's* YAAT client over the training hub, not the student's CRC scope.
