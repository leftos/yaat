# Track Sharing, Pointouts & the Consolidation Hierarchy

> Read this before touching `ConsolidationEngine`, `ConsolidationState`, `AircraftStarsState.SharedState`, `AircraftEramState`,
> `StarsPointout`, `EramPointoutState`, the server's consolidation handlers (`RoomEngine.HandleConsolidate` /
> `TransferTracksForConsolidation` / `TryConsolidationRedirect`), or the `StarsConsolidation` CRC topic. This is the
> multiplayer scope-sharing layer that sits **on top of** the TRACK/DROP/HO/ACCEPT ownership state machine.

## Scope: two layers, not one

There are two distinct layers and this doc owns only the upper one.

- **Ownership state machine** — *who is tracking a target.* `AircraftTrack.Owner` / `HandoffPeer` / `Pointout`, mutated by
  `TrackEngine` (TRACK, DROP, HO, ACCEPT, CANCEL HO, POINTOUT, scratchpad, temp alt, …). These commands bypass `CommandDispatcher`
  entirely — see [command-pipeline.md](command-pipeline.md) (the track-command bypass section) and
  [command-handlers.md](command-handlers.md).
- **Scope-sharing layer (this doc)** — *how attended positions absorb unattended ones, and how display state is shared between
  positions.* The TCP consolidation hierarchy, manual consolidation overrides, full-consolidation track transfer, handoff
  redirection, per-track shared display state, and STARS/ERAM pointouts.

Wire/broadcast plumbing (per-connection `CrcClientState`, the topic catalog, the WebSocket lifecycle, `CrcVisibilityTracker`) lives
in [crc-display-state.md](crc-display-state.md). The aircraft satellite objects (`AircraftTrack`, `AircraftStarsState`,
`AircraftEramState`) and the snapshot DTO tree are owned by [aircraft-data-model.md](aircraft-data-model.md) and
[snapshots-and-replay.md](snapshots-and-replay.md); this doc cross-links to them rather than redrawing them. Server room/hub
structure is in [server-rooms-and-hub.md](server-rooms-and-hub.md).

## Where the code lives (cross-repo map)

The consolidation **algorithm** is pure and lives in `Yaat.Sim`; the **server** files are thin wrappers that supply the
attended-position predicate, the manual-override store, and the CRC broadcast.

| Concern | File | Repo |
|---|---|---|
| Consolidation algorithm (walk-up / walk-down + manual-override pass) | `src/Yaat.Sim/Simulation/ConsolidationEngine.cs` | yaat |
| Manual-override store (room-scoped, thread-safe) | `src/Yaat.Sim/Simulation/ConsolidationState.cs` | yaat |
| TCP value type (hierarchy via `ParentTcpId`) | `src/Yaat.Sim/Tcp.cs` | yaat |
| Track ownership + STARS pointout state | `src/Yaat.Sim/AircraftTrack.cs`, `src/Yaat.Sim/StarsPointout.cs`, `src/Yaat.Sim/StarsPointoutStatus.cs` | yaat |
| Per-TCP shared STARS display state | `src/Yaat.Sim/AircraftStarsState.cs`, `src/Yaat.Sim/StarsTrackSharedState.cs` | yaat |
| ERAM display state + ERAM pointouts | `src/Yaat.Sim/AircraftEramState.cs`, `src/Yaat.Sim/EramPointoutState.cs` | yaat |
| Pure track-command logic (pointout accept/reject/retract) | `src/Yaat.Sim/Commands/TrackEngine.cs` | yaat |
| World-level override snapshot capture/restore | `src/Yaat.Sim/Simulation/SimulationEngine.cs` (`CaptureServerSnapshot` / `RestoreServerSnapshot`) | yaat |
| Override snapshot DTO | `src/Yaat.Sim/Simulation/Snapshots/ServerSnapshotDto.cs` (`ConsolidationOverrideDto`) | yaat |
| Facility-scoped algorithm wrappers (`GetConsolidationItems`, `GetConsolidationOwner`, `IsAutoConsolidation`) | `src/Yaat.Sim/Data/Vnas/ArtccConfigResolver.cs` | yaat |
| Server config-service facade | `../yaat-server/src/Yaat.Server/Data/ArtccConfigService.Consolidation.cs` | yaat-server |
| Attended-position registry + CRC-control test | `../yaat-server/src/Yaat.Server/Data/PositionRegistry.cs` | yaat-server |
| `CONS` / `DECON` command handlers + track transfer | `../yaat-server/src/Yaat.Server/Simulation/RoomEngine.cs` | yaat-server |
| Handoff consolidation redirect | `../yaat-server/src/Yaat.Server/Simulation/TrackCommandHandler.cs` (`TryConsolidationRedirect`) | yaat-server |
| Auto-accept suppression for CRC-controlled targets | `../yaat-server/src/Yaat.Server/Simulation/TickProcessor.cs` (`ProcessAutoAccept`, delayed-handoff guard) | yaat-server |
| `StarsConsolidation` topic broadcast | `../yaat-server/src/Yaat.Server/Simulation/CrcBroadcastService.cs` (`BuildConsolidationData`, `BroadcastStarsConsolidationAsync`) | yaat-server |
| DTO mappers (shared state, STARS/ERAM pointouts, consolidation item) | `../yaat-server/src/Yaat.Server/Simulation/DtoConverter.cs` | yaat-server |
| CRC-side consolidate/deconsolidate + cleanup | `../yaat-server/src/Yaat.Server/Hubs/CrcClientState.Stars.cs`, `CrcClientState.Session.cs` | yaat-server |

## The TCP hierarchy and the consolidation algorithm

A `Tcp` (`Tcp.cs:5`) is `record Tcp(int Subset, string SectorId, string Id, string? ParentTcpId)`. The facility's TCP tree is a
forest expressed by `ParentTcpId` back-references — each TCP names its parent (or `null` if it is a root). The facility-level flag
`StarsConfiguration.AutomaticConsolidation` (read via `ArtccConfigRoot.IsAutoConsolidation`, `ArtccConfigResolver.cs:1139`) decides
whether unattended TCPs fold into their attended ancestors automatically.

`ConsolidationEngine.GetConsolidationItems(allTcps, autoConsolidate, isAttended, manualOverrides?)`
(`ConsolidationEngine.cs:19`) returns one `ConsolidationItem(Tcp, Owner, Children, BasicConsolidation)` per TCP. `isAttended` is
injected by the server as `tcp => PositionRegistry.IsPositionAttended(tcp, roomId)` so the algorithm stays free of server state.

Two graph walks do the work:

- **`FindAttendedAncestor`** (`ConsolidationEngine.cs:198`) — walk **UP** the `ParentTcpId` chain from a TCP until an attended TCP
  is found; that attended TCP is the owner. Cycle-guarded with a visited set.
- **`CollectConsolidatedDescendants`** (`ConsolidationEngine.cs:225`) — walk **DOWN** the children index (`childrenOf`, keyed by
  parent id) from an attended TCP, stopping at any child that is itself attended (it owns its own subtree). Optionally skips ids in
  an `excludeIds` set (used for manual overrides — see below).

### CRC ownership conventions (load-bearing)

These conventions are baked into the item shape and must be preserved:

- **`Owner == null` does NOT mean "unowned."** It means *this TCP is the root / owns itself (attended).* `Owner != null` means the
  TCP is consolidated under that owner. When `FindAttendedAncestor` returns the TCP itself, the engine normalizes `Owner` to `null`
  (`ConsolidationEngine.cs:77`).
- **Self is excluded from the `Children` list.** CRC injects the controller's own TCP (`OurTcp`) into the consolidated set
  automatically, so the engine filters `c.Id != tcp.Id` (`ConsolidationEngine.cs:85`) to avoid a duplicate.
- **When `autoConsolidate` is off**, every TCP gets `Owner = null` and `Children = []` — each position owns only itself
  (`ConsolidationEngine.cs:64`). Manual overrides still apply on top.

`GetConsolidationOwner` (`ConsolidationEngine.cs:146`) is the single-TCP version used by handoff redirection and auto-accept
suppression: manual override first, then (auto on) `FindAttendedAncestor`, else (auto off) the TCP itself iff attended.

## Manual consolidation overrides

`ConsolidationState` (`ConsolidationState.cs:7`) is a **room-scoped, thread-safe** store of manual overrides — one instance per
`SimulationEngine` (`SimulationEngine.cs:55`). It keys `_overrides` by the **sending** TCP id; each entry is
`ManualOverride(string ReceivingTcpId, bool IsBasic)`. A given sending TCP can be consolidated into exactly one receiver.

- `Consolidate(receiving, sending, basic)` writes `_overrides[sending.Id] = new(receiving.Id, basic)`.
- `Deconsolidate(tcp)` removes the override keyed by that TCP (the sender).
- `RemoveOverridesInvolving(tcpId)` removes the override *keyed by* the TCP **and** every override whose `ReceivingTcpId` equals it
  (sender OR receiver) — called on position deactivation/disconnect (`ConsolidationState.cs:49`).
- `Clear()` wipes all overrides — called on scenario load/unload (`ScenarioLifecycleService.cs:378`, also via
  `SimulationEngine.cs:276`).
- `Restore(overrides)` replaces the whole set during snapshot restore.

**Basic vs Full** is the `IsBasic` flag. *Basic* consolidation only changes how the hierarchy is displayed/owned for new traffic;
*Full* additionally transfers existing tracks and redirects in-flight handoffs (see next section). The boolean inverts at the
command boundary — `con.Full` becomes `!con.Full` passed as `basic` (`RoomEngine.cs:1298`).

### The two-pass override build in `GetConsolidationItems`

Manual overrides require a second pass because moving a TCP under a *different* receiver affects both ends of the relationship:

1. **First pass** (`ConsolidationEngine.cs:49`): for each TCP, if it has a manual override pointing at a receiving TCP, its owner
   becomes that receiving TCP — or, if the receiving TCP is itself not attended, the receiving TCP's *attended ancestor*
   (`ConsolidationEngine.cs:56`). Overridden senders are collected into `manuallyOverriddenIds` and **excluded** from every
   auto-computed `Children` walk (passed as `excludeIds` to `CollectConsolidatedDescendants`, `ConsolidationEngine.cs:84`) so a
   sender isn't double-counted under both its natural ancestor and its override receiver.
2. **Second pass** (`ConsolidationEngine.cs:94`): for each override, find the receiving TCP's result, resolve its *actual owner*
   (`receivingResult.Owner ?? receivingResult.Tcp` — the override re-attaches to the attended owner of the receiver, **not
   necessarily the receiver itself**, `ConsolidationEngine.cs:114`), and append the sending TCP plus its non-attended descendants
   to that owner's `Children`.

## Full consolidation: track transfer & handoff redirection

When a `CONS …+` (Full) command fires, `RoomEngine.HandleConsolidate` (`RoomEngine.cs:1278`) records the override and then calls
`TransferTracksForConsolidation(sendingTcp, receivingTcpCode)` (`RoomEngine.cs:1344`). That method walks the world snapshot and:

- **Transfers tracks**: any aircraft whose `Track.Owner` matches the sending TCP **by `(Subset, SectorId)` value** has its `Owner`
  reassigned to the receiving owner (`RoomEngine.cs:1360`).
- **Redirects in-flight handoffs**: any aircraft whose `Track.HandoffPeer` matches the sending TCP gets `HandoffRedirectedBy` set to
  the original peer and `HandoffPeer` reassigned to the receiving owner (`RoomEngine.cs:1367`).

Basic consolidation does neither — it only records the override and rebroadcasts the topic.

### Handoff redirection at issue time

Independently of full-consolidation transfer, when a controller issues a handoff to a TCP that is currently consolidated under
someone else, `TrackCommandHandler.HandleHandoff` redirects it. `TryConsolidationRedirect` (`TrackCommandHandler.cs:520`):

1. If the target TCP is already attended → no redirect.
2. Otherwise resolve `GetConsolidationOwner(... ConsolidationState)`; if the owner differs from the target, the handoff peer
   becomes the owner and `Track.HandoffRedirectedBy` records the originally-targeted TCP (`TrackCommandHandler.cs:262`).

The user sees `Handoff … to <code> (redirected to <ownerSubset><ownerSector>)`.

### Auto-accept suppression

The server normally auto-accepts handoffs after `AutoAcceptDelay`. But if the handoff peer is **CRC-controlled** — either directly
attended or attended via consolidation — the server must NOT auto-accept; the real CRC controller owns that action.
`TickProcessor.ProcessAutoAccept` (`TickProcessor.cs:984`) calls
`PositionRegistry.IsTcpControlledByCrc(tcp, t => GetConsolidationOwner(...), roomId)` (`PositionRegistry.cs:120`) and skips the
target when it returns true. The same consolidation check guards delayed scenario handoffs (`TickProcessor.cs:872`): a handoff to a
TCP consolidated under another attended TCP is held until that TCP activates (otherwise it would be a handoff to oneself).

## Per-track shared display state

Display state that one position sets and other positions can observe lives **per (aircraft, viewing-TCP)**, not globally.

### STARS — `AircraftStarsState.SharedState`

`AircraftStarsState.SharedState` (`AircraftStarsState.cs:37`) is `Dictionary<string tcpId, StarsTrackSharedState>`. Each
`StarsTrackSharedState` (`StarsTrackSharedState.cs:5`) carries `ForceFdb`, `IsHighlighted`, `LeaderDirection` (default `5`),
`IsQueriedUntil` (a `DateTime?` expiry), `WasPreviouslyOwned`, `TpaType`, and `TpaSize`. This is distinct from
`AircraftStarsState.GlobalLeaderDirection` (`AircraftStarsState.cs:36`), the facility-wide default leader direction set via the
`LeaderDirection` track command (`TrackEngine.HandleLeaderDirection`, where `5` resets to `null`).

`WasPreviouslyOwned` is the one field the sim writes on its own: when a handoff is auto-accepted, `ProcessAutoAccept` sets
`WasPreviouslyOwned = true` in the *previous* owner's `SharedState` entry (`TickProcessor.cs:1038`) so CRC keeps the datablock as a
full datablock (white) until the controller acknowledges.

`SharedState` maps to the wire on the `StarsTracks` topic: `DtoConverter.ToStarsTrack` writes `SharedState = MapSharedState(...)`
(`DtoConverter.cs:59`), where `MapSharedState` (`DtoConverter.cs:568`) converts each entry's `LeaderDirection`/`TpaType` ints into
their CRC enum types.

### ERAM — `AircraftEramState`

`AircraftEramState` (`AircraftEramState.cs:9`) holds the ERAM-tier display overrides: `IsDwellLocked`, `IsVci`, `LeaderDirection`,
`LeaderLength`, the interim/procedure/local-interim/controller-entered altitude pile, the active `Pointouts`, and
`ForcedPointoutsTo` (a `List<Tcp>`). It maps onto the `EramDataBlocks` topic via `DtoConverter.ToEramDataBlock`
(`DtoConverter.cs:332`); `ForcedPointoutsTo` also rides the `StarsTrackDto` (`DtoConverter.cs:58`).

## Pointouts

### STARS pointouts — `AircraftTrack.Pointout`

A STARS pointout is a single `StarsPointout?` on `AircraftTrack.Pointout` (`AircraftTrack.cs:18`). `StarsPointout`
(`StarsPointout.cs:5`) carries `Recipient` (`Tcp`), `Sender` (`Tcp`), and a `StarsPointoutStatus` (`Pending` / `Accepted` /
`Rejected`, `StarsPointoutStatus.cs:3`). The lifecycle is implemented purely in `TrackEngine`:

- **Issue** — `HandlePointOut` (`TrackEngine.cs:174`) sets `Track.Pointout = new StarsPointout(target, sender)`; rejects if the
  issuer doesn't own the track or a pointout is already pending.
- **Accept** — `HandleAcknowledge` (`TrackEngine.cs:158`) sets `Status = Accepted`.
- **Reject** — `HandleRejectPointout` (`TrackEngine.cs:358`) sets `Status = Rejected`.
- **Retract** — `HandleRetractPointout` (`TrackEngine.cs:374`) clears `Track.Pointout` (sender only).
- **No-arg `PO`** — `HandlePointOutNoArgs` (`TrackEngine.cs:190`) is dual-purpose: if the issuer is the recipient it accepts, if the
  issuer is the sender it retracts.

Recipient/sender matching is by the **stringified TCP** `$"{Subset}{SectorId}"` (`Tcp.ToString()`, `Tcp.cs:11`), e.g.
`TrackEngine.cs:165`. STARS pointouts ride the `StarsTracks` topic via `DtoConverter.ToStarsTrack` →
`MapStarsPointout` (`DtoConverter.cs:45`, `DtoConverter.cs:887`).

### ERAM pointouts — `AircraftEramState.Pointouts`

ERAM uses a **list** of `EramPointoutState` (`EramPointoutState.cs:9`) on `AircraftEramState.Pointouts`, each with originating /
receiving facility+sector strings and four lifecycle bits: `IsAcknowledged`, `IsRecipientSuppressed`, `IsRSideCleared`,
`IsDSideCleared` (R-side = radar controller, D-side = data controller). `EramPointoutState` mirrors vatsim-server-rs
`radar_state::PointoutState`. They map to the `EramDataBlocks` topic as `EramPointout` items in `DtoConverter.ToEramDataBlock`
(`DtoConverter.cs:365`); the wire id is synthesized as `PO_{callsign}_{origFac}_{origSector}_{recvSector}`.

## Snapshot policy

Three different rules apply across this subsystem. Get the carrier wrong and state silently fails to round-trip:

| State | Carrier | Granularity |
|---|---|---|
| STARS `SharedState` (whole dictionary) | `AircraftStarsStateDto.SharedState` | **Per-aircraft** (`AircraftStarsState.ToSnapshot`, `AircraftStarsState.cs:67`) |
| STARS `Track.Pointout` | `AircraftTrackDto.Pointout` | **Per-aircraft** (`AircraftTrack.ToSnapshot`, `AircraftTrack.cs:29`) |
| ERAM `Pointouts` + `ForcedPointoutsTo` | `AircraftEramStateDto.Pointouts` / `.ForcedPointoutsTo` | **Per-aircraft** (`AircraftEramState.ToSnapshot`, `AircraftEramState.cs:48`/`64`) |
| Manual consolidation overrides | `ServerSnapshotDto.ConsolidationOverrides` (`ConsolidationOverrideDto`) | **World level** (`SimulationEngine.CaptureServerSnapshot`/`RestoreServerSnapshot`, `SimulationEngine.cs:311`/`344`) |

Consolidation overrides are **not** per-aircraft — they are a single dictionary captured from `ConsolidationState.GetSnapshot()` and
restored via `ConsolidationState.Restore(...)` at the world level. Everything else here hangs off `AircraftState` and round-trips per
aircraft.

ERAM pointouts **are** round-tripped — `EramPointoutState`'s own doc-comment (`EramPointoutState.cs:3`) states this, and
`AircraftEramState.ToSnapshot`/`FromSnapshot` serialize both `Pointouts` and `ForcedPointoutsTo`. (Earlier drafts of this subsystem
treated ERAM pointouts as runtime-only; that is no longer the case — trust the code, which serializes them.) For the snapshot DTO
tree, schema migrations, and the runtime-only `[JsonIgnore]` set, see [snapshots-and-replay.md](snapshots-and-replay.md).

## Broadcast cadence

The `StarsConsolidation` topic is a **full replacement**, not a delta. `BroadcastStarsConsolidationAsync`
(`CrcBroadcastService.cs:459`) rebuilds the entire item list for every subscribed client via `BuildConsolidationData`
(`CrcBroadcastService.cs:285`), which calls `GetConsolidationItems(...)` and maps each result through
`DtoConverter.ToStarsConsolidationItem` (`DtoConverter.cs:410`, also attaching `GetDefaultConsolidation` for the reset baseline). It
fires on topic subscribe, on `CONS`/`DECON` (`RoomEngine.cs:1319`/`1340`), on CRC-side consolidate/deconsolidate
(`CrcClientState.Stars.cs:830`/`854`), on position open/close (`CrcSessionLifecycle.cs:28`, `CrcWebSocketHandler.cs:103`), and on
scenario load/unload (`ScenarioLifecycleService.cs:175`/`383`). Consolidation is never broadcast to unbound (lobby) clients —
`BuildConsolidationData` returns `null` for a `null` room (`CrcBroadcastService.cs:290`).

Per-track shared state and pointouts do **not** have their own topic — they ride the per-aircraft `StarsTracks` and
`EramDataBlocks` datablock topics on the normal per-sim-second broadcast pass (see [tick-loop.md](tick-loop.md) and
[crc-display-state.md](crc-display-state.md)).

## Footguns & pitfalls

- **`Owner == null` is "owns itself," not "unowned."** Every consumer of `ConsolidationItem.Owner` must treat `null` as
  *attended root.* The second override pass relies on this when it does `receivingResult.Owner ?? receivingResult.Tcp`
  (`ConsolidationEngine.cs:114`). Treating `null` as "no owner" inverts the hierarchy.
- **Self-exclusion from `Children` is deliberate.** The engine filters out the TCP itself because CRC injects `OurTcp`
  automatically. Re-adding self produces a duplicate in the consolidated set on the scope.
- **`Tcp.Equals`/`GetHashCode` are by `Id` only** (`Tcp.cs:7`). Two `Tcp` records with the same `Id` but different `ParentTcpId`
  compare equal, and the `byId`/`childrenOf` dictionaries key on `Id`. Don't assume structural (whole-record) equality anywhere in
  the algorithm.
- **Two different matching styles, both by value.** Track transfer / handoff redirect compare ownership by `(Subset, SectorId)`
  (`RoomEngine.cs:1360`); pointout recipient/sender comparison uses the stringified `"{Subset}{SectorId}"` form
  (`TrackEngine.cs:165`). `Tcp` and `TrackOwner` are *different types* — a mismatch in either comparison silently fails to
  transfer/redirect rather than erroring.
- **Overrides leak unless cleaned up on deactivation/disconnect.** `CleanUpConsolidationOverrides`
  (`CrcClientState.Session.cs:305`) must call `RemoveOverridesInvolving(tcp.Id)` (sender OR receiver) when a position deactivates.
  Skip it and a stale override survives for the rest of the room session, silently reshaping the hierarchy.
- **The override store is keyed by the *sending* TCP.** `Deconsolidate(tcp)` removes the entry where that TCP is the sender. To
  remove an override where the TCP is the *receiver*, you need `RemoveOverridesInvolving` — `Deconsolidate` alone won't do it.
- **`SharedState` is per-(aircraft, viewing-TCP), not global.** Writing to the wrong TCP id (or to `GlobalLeaderDirection` when you
  meant a per-TCP entry, or vice versa) targets the wrong scope. `WasPreviouslyOwned` in particular is written into the *previous*
  owner's entry, not the new owner's (`TickProcessor.cs:1038`).
- **Three snapshot policies in one subsystem.** Per-aircraft for STARS shared state / STARS pointout / ERAM pointouts; world-level
  for consolidation overrides. A new consolidation-override field belongs in `ConsolidationOverrideDto` +
  `Capture`/`RestoreServerSnapshot`; a new per-track field belongs in the relevant `Aircraft*StateDto`. Wiring it into the wrong one
  means it round-trips on the wrong axis.
- **Full vs Basic inverts at the command boundary.** `con.Full` is passed to `ConsolidationState.Consolidate(... !con.Full)` as the
  `basic` flag. Only Full performs `TransferTracksForConsolidation`; Basic just records the override and rebroadcasts.
- **`StarsConsolidation` is full-replacement.** Unlike most CRC topics it does not accumulate deltas — each broadcast supplies the
  complete item list, so a missing item *removes* it from the scope. Don't try to send a partial update.
