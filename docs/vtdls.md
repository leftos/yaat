# YAAT vTDLS (PDC)

Reference for the Tower Data Link Services emulation in YAAT. Read this
before changing anything under:

- `src/Yaat.Sim/Simulation/Tdls/` (`TdlsState.cs`, `TdlsMutations.cs`, `TdlsCommandHandler.cs`,
  `TdlsChangeTracker.cs`) and `src/Yaat.Sim/Simulation/SimulationEngine.Tdls.cs` (the tick steps, the spawn hook)
- `src/Yaat.Server/Simulation/TdlsBroadcaster.cs` (the wire projection and the SignalR pushes)
- `src/Yaat.Server/Dtos/CrcDtos.Tdls.cs`
- `src/Yaat.Server/Hubs/TrainingHub.cs` (TDLS hub methods)
- `src/Yaat.Sim/Data/Vnas/ArtccConfig.cs` (TDLS config records)
- `src/Yaat.Sim/Commands/CanonicalCommandType.cs` (TDLSQ/TDLSS/TDLSW/TDLSDUMP)
- `src/Yaat.Client.Tdls/` (transport, viewmodels, view)
- `tools/Yaat.VTdls.Web/` (browser app served at `/vtdls/`)

For the upstream manual, see [`docs/vtdls/vtdls.md`](vtdls/vtdls.md)
(mirrored from [docs.virtualnas.net/vtdls](https://docs.virtualnas.net/vtdls/)
by `tools/refresh-crc-docs.py`).
For the user-facing command surface, see
[`COMMANDS.md`](../COMMANDS.md) (TDLSQ / TDLSS / TDLSW / TDLSDUMP entries).

## Overview

vTDLS is a separate vNAS web app that real controllers use to issue
Pre-Departure Clearances (PDCs). YAAT plays the role of that web app —
the simulation engine (`Yaat.Sim`) owns the vTDLS state and its whole
lifecycle on every run kind, the **yaat-server** broadcasts it per training
room, and the **Yaat.Client** desktop app + a browser-hosted WASM client
(served at `/vtdls/`) consume the state.

Unlike vStrips, vTDLS broadcasts are **SignalR-only**. CRC does not
subscribe to a TDLS topic on the WebSocket; the trainee's CRC client
has no vTDLS view. The vTDLS surface in YAAT is purely controller-side
state — what the issuer of the clearance sees.

The desktop and browser vTDLS views share the flight-strips in-view
**find** (Ctrl+F): `VTdlsView` hosts the same `FindBarView` / `FindController`
(`src/Yaat.Client.Strips/Find/`) as vStrips, searching the DCL + PDC lists
(callsign plus the filed flight-plan and clearance text) and scrolling matches
into view. `TdlsItemViewModel` implements `IFindableItem`. See
[`flight-strips.md`](flight-strips.md) for the shared find internals.

```
Yaat.Client (issuer)               yaat-server                       Pilot (sim)
──────────────────────             ──────────────────                ─────────────
user selects DCL item, edits the
flight plan, presses Send / F12
   │
   │  SignalR: SendCommand(callsign, "TDLSS Expect|SID|...", initials)
   ▼
CommandParser.ParseTdlsSend         TrainingHub.SendCommand
   │                                 │
   │                                 ▼
   │                                RoomEngine.SendCommandAsync
   │                                 │
   │                                 ▼
   │                                ActionRouter → the Tdls arm (Yaat.Sim)
   │                                 │
   │                                 ▼
   │                                TdlsCommandHandler.HandleSend (Yaat.Sim)
   │                                 ├── validate facility config + mandatory fields
   │                                 ├── snapshot TdlsClearance, Status → Sent
   │                                 ├── ScheduledWilcoAt[id] = SimTimeUtc + 3s
   │                                 ├── EmitTerminal (RPO-visible "[TDLS PDC sent at OAK] …")
   │                                 └── Tdls.Changes marks the item
   │                                      │  drained by ActionRouter.Finish →
   │                                      │  RoomHost.OnTdlsChanged → TdlsBroadcaster
   │                                      │  SignalR: TdlsItemChanged
   │                                      ▼
   │                                                                    (the pilot model is untouched —
   │                                                                    see "Pilot side" below)
   │                                 (3s later: TickTdlsAutoWilco → Status=Wilco, drained the same way)
   ▼
status flips on the issuer's vTDLS tab
```

## State model

`TdlsState` (engine-owned, `SimulationEngine.Tdls`) holds these things:

- **`Items`** — every TDLS list entry, keyed by item id (`TDLS_{n}`).
  - DCL list = items with `Status == Pending`.
  - PDC list = items with `Status == Sent` or `Wilco`.
- **`Configs`** — per-facility `TdlsConfig` (FE-defined SIDs, transitions,
  field defaults, mandatory-field flags). Derived from the loaded ARTCC by
  `SimulationEngine.InitializeFromArtcc` (the server's scenario
  load and the Sim replay driver both call it). NOT snapshotted — re-derived
  on session restore.
- **`Dumped`** — `(facility, callsign)` lockout (case-insensitive) so the
  auto-generator won't re-create entries the controller explicitly removed.
  Persists across snapshots.
- **`ScheduledWilcoAt`** — Sent items awaiting the 3 s auto-WILCO. Cleared
  on manual `TDLSW` or on Dump/expiry.
- **`Changes`** — the broadcast seam (`TdlsChangeTracker`): every mutation records
  the ids it touched, the items it removed and whether a full state is owed. Not
  snapshotted; the host drains it (below).
- **`Changes`** — the broadcast seam (`TdlsChangeTracker`): every mutation records
  the ids it touched, the items it removed and whether a full state is owed. Not
  snapshotted; the host drains it (below).
- **`Changes`** — the broadcast seam (`TdlsChangeTracker`): every mutation records
  the ids it touched, the items it removed and whether a full state is owed. Not
  snapshotted; the host drains it (below).

`TdlsItemRecord` (mirrors `TdlsItemDto` on the wire):

| Field           | Notes                                                                 |
|-----------------|-----------------------------------------------------------------------|
| `Id`            | `"TDLS_{n}"` — monotonic per session                                  |
| `AircraftId`    | Callsign (may not be active in sim yet — pre-files supported)         |
| `Cid`           | VATSIM CID; durable across despawn/respawn (currently `null` — no plumbed source yet) |
| `FacilityId`    | TDLS facility id (resolved from the filed departure airport)          |
| `Status`        | `Pending` / `Sent` / `Wilco`                                          |
| `Sequence`      | Monotonic per-state, for sort stability                               |
| `CreatedUtc` / `SentUtc` / `WilcoUtc` | Lifecycle timestamps on the **session clock** (`SimScenarioState.SimTimeUtc` = `SessionStartUtc` + elapsed), never `DateTime.UtcNow` — a rewind or bundle reconstruction reproduces them |
| `ExpiresUtc`    | `CreatedUtc + 2 hours` (upstream TTL) in sim time: a paused room expires nothing, a 4× room expires after 2 sim hours |
| `SentPayload`   | Snapshot of the `ClearanceDto` at TDLSS time (null while Pending)     |

## vNAS data-api config integration

TDLS configuration lives inside the ARTCC config response:
`https://data-api.vnas.vatsim.net/api/artccs/{id}`. Each facility node in
the tree may carry a `tdlsConfiguration` block (see
`src/Yaat.Sim/Data/Vnas/ArtccConfig.cs` for the C# DTOs). On scenario
load, `TdlsState.InitializeFromArtcc` walks the facility tree and
records a `TdlsConfig` entry for every facility that has one.

Within the ZOA ARTCC, NCT (the parent TRACON) consolidates five TDLS
facilities top-down: **OAK, RNO, SFO, SJC, SMF**. Other ARTCCs follow
similar parent-aggregation patterns — see upstream's documentation for
ZBW → ALB/BDL/BOS/PVD.

## Commands

| Verb       | Purpose                                                       |
|------------|---------------------------------------------------------------|
| `TDLSQ`    | Queue a Pending PDC for the aircraft's filed departure facility. Auto-fired on flight-plan creation; the controller rarely types this directly. |
| `TDLSS`    | Send the queued PDC. Takes nine `|`-separated fields: `Expect|Sid|Transition|Climbout|Climbvia|InitialAlt|ContactInfo|DepFreq|LocalInfo`. Empty between separators = `null`. |
| `TDLSW`    | Manually force the Sent PDC to WILCO (normally auto-fires ~3 s after Send). |
| `TDLSDUMP` | Remove the PDC from TDLS. Terminal — the (facility, callsign) pair is locked out for the rest of the session; clearance must be given by voice. |
| `TDLSOPS` | **Global** (no callsign): `TDLSOPS <facility> <config>` selects the facility's active operational configuration, by name or id. |

The full canonical syntax is enforced by `CommandRegistry` entries with
group `"vTDLS"` and asserted by completeness tests.

## Pilot side: not wired yet

A PDC does not reach the pilot model today. `TDLSS` records the clearance on
the TDLS item (`SentPayload`) and nothing else: no `AircraftState` field is
written, no pilot transmission is queued or suppressed, and a follow-up voice
`CL` behaves exactly as it would without the PDC. The remaining PDC flow —
applying the sent clearance to the aircraft silently — is unbuilt and unscheduled (design record:
[`docs/vtdls/emulation-design.md`](vtdls/emulation-design.md)).

`AircraftVoice.TdlsDumped` is CRC's own flight-plan flag ("the PDC was dumped,
clear this aircraft by voice"), set only by CRC's `TdlsDump` handler
(`CrcClientState.Session.cs`) and projected onto the `FlightPlans` topic; the
auto-WILCO does not set it and nothing in `Yaat.Sim` reads it.

## Auto-generation

`SimulationEngine.TickAutoTdlsQueue` (a post-physics Sim step) sweeps the
world every second, and `SimulationEngine.AfterAircraftSpawned` runs the same
check inline for every spawn (scenario load, a delayed or generated spawn,
`ADD`, a recorded spawn on replay), so a departure filed at a
TDLS-configured facility shows a Pending entry without the controller typing
anything. Both go through `TryQueueAutoTdlsForAircraft`. Conditions for the
auto-queue:

- The flight plan's departure airport (with leading `K` stripped) must
  match a facility id in `TdlsState.Configs`.
- `(facility, callsign)` must not be in the Dumped lockout.
- No existing Pending item for this (facility, callsign).

Pre-filed flight plans (a CID with a filed plan but no active
`AircraftState`) generate Pending entries too — matches upstream
behavior: "If a pilot pre-files prior to connecting to the network,
their flight plan is displayed in the DCL list."

## RPO terminal broadcasts

When `TDLSS` succeeds, `TdlsCommandHandler.HandleSend` emits a `Tdls`
terminal entry through the engine (`EmitTerminal`), which the room drains
with its other terminal lines:

```
[TDLS PDC sent at OAK] Expect=10 MIN, SID=OAKLAND4.ALTAM, Maintain=5000, DepFreq=120.9
```

Empty fields are omitted from the summary. RPOs in the room see this
in their terminal pane without subscribing to the SignalR TDLS events,
so they can follow the student's PDC stream while focused on radar.

## State ownership and snapshot persistence

`TdlsState` is the engine's (`SimulationEngine.Tdls`, `src/Yaat.Sim/Simulation/Tdls/TdlsState.cs`),
and `TdlsItemRecord` is the simulation's model: `Status` is the Sim enum `TdlsItemStatus` and
`SentPayload` the Sim record `TdlsClearance` (the nine canonical `TDLSS` fields). The CRC wire
types `TdlsStatus` and `ClearanceDto` stay in yaat-server, pinned by `CrcWireContractTests`;
`DtoConverter.ToTdlsItem` / `ToClearanceDto` project onto them. The mutation bodies are the
engine's too (since 2026-09-07): `TdlsMutations` and `TdlsCommandHandler` in
`src/Yaat.Sim/Simulation/Tdls/`, and the auto-queue / auto-WILCO / expiry / track-removal tick steps as
`SimulationEngine.TickAutoTdlsQueue` / `TickTdlsAutoWilco` / `TickTdlsExpiry` / `TickTdlsTrackRemoval`
(`SimulationEngine.Tdls.cs`, `SpineStep.Sim` entries in `SpineOrder.PostPhysics`). The router's `Tdls`
and `TdlsOps` arms are Sim arms, so a bare engine, a client-side replay, a server reconstruction and the
live room all run one body; nothing about TDLS is a host slot any more.

**The broadcast seam.** The mutations do not broadcast. Each records what it touched in
`TdlsState.Changes` (`TdlsChangeTracker`: changed item ids, `TdlsRemoval`s carrying the facility,
callsign and whether it was a dump, and a full-state flag for `TDLSOPS`). Two drains hand a
`TdlsChangeSet` to the host's `OnTdlsChanged` (declared once on `IStateChangeConsumer`, beside `OnStripsChanged`, which both
`IActionHost` and `IHostConsumers` extend): `ActionRouter.Finish` after every routed action, so a live
command's pushes still precede its result and tape playback pushes per record, and the
`StateChanges` spine step after the post-physics steps, before `AutoDelete` so an item's aircraft still
resolves for the DTO. `RoomHost.OnTdlsChanged` → `TdlsBroadcaster.BroadcastChanges`: items first, then
removals, then the full state when flagged, and nothing at all while `TrainingRoom.IsBroadcastSuppressed`
(a reconstruction; the room re-syncs when it lands). The scenario load drains once at the end of
`PopulateRoom` so the load-time PDCs reach the clients as before. `BareHost` discards the set.

`TdlsSnapshotMapper.Capture` (into `ServerSnapshotDto.Tdls`, every snapshot the engine takes —
recordings, bundles, session checkpoints) writes:

- `Items` (with full `SentPayload` for Sent/Wilco items)
- `Dumped` lockout
- `NextItemId`
- `ActiveOpConfigs`
- `ScheduledWilco` — the pending auto-WILCO instants. They run on the session clock
  (`SimScenarioState.SimTimeUtc`), so a restored or rewound run acknowledges at the same
  sim second the live one did; the old "restored Sent items sit at Sent" caveat is gone.

`Configs` is NOT persisted: it is re-derived from the ARTCC at scenario load via
`InitializeFromArtcc`, which runs before any restore; `TdlsState.ClearSession()` is what a restore
(and a fresh replay) clears, and it leaves the configs alone.

A fresh engine starts with no items, so a restart starts clean and a rewind holds the target's. A
live-session rewind is a from-scratch reconstruction (a live room carries no snapshots): the spawn
hook re-queues each departure's PDC on every run kind, and the recorded `TDLSQ` / `TDLSS` / `TDLSW` /
`TDLSD` / `TDLSOPS` re-apply through the same `TdlsCommandHandler` body live used, so the rewound
item carries the status, clearance and pending auto-WILCO the run had (`StripTdlsRewindTests`; the
bare-engine pins are `TdlsStepTests` in Yaat.Sim.Tests, and a client-side `ReplayDriver` replay now
builds the same lists — it refused every TDLS verb before the bodies crossed; because the Sim scenario load runs
no spawn hook, the replay runs it over the loaded aircraft at t=0 itself, right after its session reset, so a
load-time departure's PDC carries the session-start timestamp live gave it).
The broadcasts are what a reconstruction leaves out: it runs with broadcasts suppressed and the room
re-pushes the full state when it lands, while tape playback broadcasts as it goes.

## Lifecycle

| Event                                | Effect                                                       |
|--------------------------------------|--------------------------------------------------------------|
| Flight plan filed at TDLS facility   | `TDLSQ` auto-emitted → Pending entry                          |
| Controller selects Pending + Send    | Status → Sent; SentPayload snapshotted; auto-WILCO scheduled  |
| `TickTdlsAutoWilco` fires ~3 s later    | Status → Wilco                                                |
| Controller force-WILCOs              | Same as auto-WILCO; scheduler entry cleared                   |
| Controller dumps                     | Item removed from `Items`; (facility, callsign) → `Dumped`    |
| TTL > 2 hours since `CreatedUtc`     | `TickTdlsExpiry` removes the item; lockout NOT set            |
| Aircraft tracked on STARS by anyone  | `TickTdlsTrackRemoval` removes the item (any status); lockout NOT set |

## Multi-facility tabs and the consolidated parent view

The desktop client supports one vTDLS tab per facility. The student
entry (`TdlsEntries[0]`) auto-binds to the position's first accessible
TDLS facility after scenario load. Additional tabs open via **View →
vTDLS → New vTDLS Tab…** for any other facility in the position's
accessible set.

Per-facility geometry is keyed on `"VTdlsView:{facilityId}"`, so
multiple popped-out windows each remember their own size/position.

Per upstream, working a parent facility top-down (e.g. NCT in ZOA over
OAK/SFO/SJC/SMF/RNO) also lets you select **the parent itself** for a
consolidated page showing every child facility's DCL/PDC lists at once.
NCT carries no `tdlsConfiguration` of its own in the vNAS data — it is
listed purely because descendants have one.

`ArtccConfigResolver.GetAccessibleTdlsFacilities` builds that list: every
facility in {own ∪ descendants} that has a TDLS configuration **or** a
descendant with one, each carrying `MemberFacilityIds` (itself when it has
a config, plus every descendant that does). A leaf yields a single member,
so today's per-facility behaviour falls out of the same rule. The set is
keyed on TDLS configuration rather than strip bays, which also fixes a
latent gap: a TDLS facility with no strip bays used to be invisible here.

Deliberately **not** reached upward: working OAK does not offer NCT. The
consolidated page is a top-down-consolidation affordance, matching
upstream. (Strips *do* reach sideways/upward, via `externalBays` — see
[`flight-strips.md`](flight-strips.md) §Multi-facility strips tabs.)

`GetTdlsFacilityView(facilityId)` returns one `TdlsConfigDto` per member.
The client holds them in `VTdlsViewModel._memberConfigs`, and
`MatchesActiveFacility` becomes membership in that set — which is the whole
consolidation mechanism, since every item path already filters through it.
`Config` (and therefore the editor, the mandatory-field gating, the SID
list and the Ops Config footer) resolves from the **selected item's own**
facility, falling back to the sole member on a leaf page. `TDLSOPS`
likewise targets that member facility, never the parent page id — NCT owns
no configuration to set. On a consolidated page each row is prefixed with
its facility id so two airports' callsigns don't blur together; with
nothing selected there is no facility in force, so `Config` is null and the
Ops Config button hides.

Upstream additionally offers a "Filter staffed underlying facilities"
toggle that hides children someone else is working. **YAAT does not
implement it** — in a training room the student's own facility is the
staffed one, so filtering it out would hide exactly the lists the
instructor opened the page to watch. Every member facility is always shown.

## Web app at `/vtdls/`

`tools/Yaat.VTdls.Web/` publishes to
`../../yaat-server/src/Yaat.Server/wwwroot/vtdls/`. The server's
`ServerApp` maps `/vtdls/{**path:nonfile}` to `vtdls/index.html`.

The browser app gates the WASM boot on `?cid=…&initials=…&artcc=…` query
params (filled via the static landing form in `wwwroot/index.html`).
Subsequent connect follows the same shape as vStrips: `FindRoomForMyCid`
→ `JoinRoom` → `RefreshAccessibleTdlsFacilities` → `SwitchFacilityAsync`.

The browser app's `JoinRoom` uses `ClientKind.VTdls`, so the room
terminal log marks the participant as `(vTDLS)` for everyone else.

The desktop client opens this app via **Tools → Open TDLS in Browser**
(`MainViewModel.OpenTdlsInBrowserCommand`), which shells out to the
default browser with `{server}/vtdls/?cid=…&initials=…&artcc=…&room=…`
prefilled from the live connection — the vTDLS analog of **Open Strips
in Browser**.

## Phraseology compliance notes

PDC content phraseology follows AIM 5-2-2 (Pre-Departure Clearance
Procedures) and FAA Order 7210.3. The simulated FE-configured fields
exactly match real-world PDC structure:

- **Expect** — "EXPECT 10 MIN AFTER DEPARTURE" / "EXPECT VECTORS AFTER
  DEPARTURE" wording
- **SID + Transition** — published procedure id (e.g. `OAKLAND4.ALTAM`)
- **Climbout / Climb Via** — "ON COURSE" or "FLY RUNWAY HEADING" etc.;
  separate "CLIMB VIA SID" instruction when applicable
- **Initial Alt / Maintain** — assigned altitude in feet
- **Dep Freq** — departure-control frequency
- **Contact Info** — "CTC SAN FRANCISCO DEP" or equivalent
- **Local Info** — squawk, hold-short, etc., free-form per facility

The Send button gates on the facility's mandatory-field set
(`MandatorySid`, `MandatoryExpect`, …) so a PDC missing a required
field can't be issued — same enforcement upstream's vTDLS does. The
footer status "MANDATORY FIELD NOT SET — Expect, Initial Alt" mirrors
upstream's matching error.

That footer line is `VTdlsViewModel.FooterStatusText`, derived from the
open editor and bound directly by the view; the warning colour rides on
`IsFooterStatusWarning` via a `Classes.warning` binding. Two constraints
keep it honest. The colour has to come from a style rather than a local
`Foreground` — a local value outranks a style setter in Avalonia, so an
inline `Foreground` would make the warning class a no-op. And
`RecomputeCanSend` re-raises `MissingMandatoryFieldNames` on every
recompute, not only when `CanSend` flips: with two mandatory fields
blank, filling one leaves `CanSend` false, so an `[ObservableProperty]`
notification keyed on it would never fire and the footer would keep
naming the field the controller just filled.

### Operational configurations

A facility with `dclOpConfigsEnabled` keeps **no** SIDs at facility level — every SID moves into
an ops config, each carrying its own per-transition defaults so an east/west split issues
different departure frequencies, maintain altitudes or local info. SFO, OAK and BOS all ship an
empty facility-level `sids` array today.

Read the SID list through `TdlsConfig.ResolveSids(opConfigId)` (Sim) or
`TdlsConfigDto.ResolveSids` (client), never off `Sids` — the raw field is empty at exactly the
facilities controllers use most. `ResolveDefaultSidId` / `ResolveDefaultTransitionId` follow the
same rule; whether a config sets them is per-facility (SFO both, BOS SID only, OAK neither).

`TdlsFlightPlanEditorViewModel.MatchSidFromFiledRoute` pre-selects the SID and transition from the filed route's first
two tokens. The transition matches on its `FirstRoutePoint` first and falls back to its `Name` (2026-09-08): both are
FE-authored pass-throughs of `tdlsConfiguration.opConfigs[].sids[].transitions[]`, and ZOA's SFO config sets
`firstRoutePoint` on TRUKN2's ORRCA transition but not SNTNA2's, though both are named `ORRCA`. The no-transition
placeholder (`- - - -`) never matches. `TdlsFlightPlanEditorViewModelTests` pins the fallback.

The active config is **shared room state**, not a per-controller preference: it decides what a
PDC contains, so `TdlsState.ActiveOpConfigIds` holds it engine-side, `TdlsStateDto.ActiveOpConfigs`
broadcasts it, and it is snapshotted so a replay from a snapshot rebuilds the clearance that was
actually sent.

The only writer is the global `TDLSOPS` command, the router's `TdlsOps` arm
(`SimulationEngine.ApplyTdlsOpConfig`). Deliberately a **command, not a hub RPC**: the router
records every command into the action log, and a replay from t=0 re-applies it — an RPC
(as bookmarks use) is invisible to that log, so a mid-session configuration change would silently
fail to reproduce and the replay would rebuild clearances from the wrong config. The client never
applies a selection locally, which is what makes upstream's Save-not-select semantics fall out
naturally.

> ⚠️ **SID ids are not stable across configs.** The same SID name carries a different id in each
> config at OAK and BOS (SFO happens to reuse one), and the clearance sends the id. So changing
> the active config **closes any open editor** (`VTdlsViewModel.ApplyActiveOpConfig`) rather than
> leaving `SelectedSid` holding an id that no longer resolves.

Upstream renders the Ops Config menu in the footer only "when enabled" — `AreOpConfigsEnabled`
gates the button, which opens a Save/Cancel picker mirroring `docs/vtdls/img/opsconfig.png`.

`TdlsFlightPlanEditorViewModel.ResolveItem` maps an FE-supplied string
(a transition default, or a field on a sent clearance being reviewed)
onto the dropdown entry that offers it. It cannot match on exact string
equality alone: vNAS facility data routinely stores a transition
default in a different numeric form than the value list it points at —
KIAD defaults every transition's departure frequency to `125.05`
against a `depFreqs` list holding `125.050`, and KRNO does the same
with `119.2` / `119.200`. The match order is exact ordinal, then
trimmed/case-insensitive, then numeric equivalence; non-numeric values
(the `- - - -` placeholder, `3000FT`) never reach the numeric pass. A
value that resolves to nothing leaves the field unset — the editor
never guesses an entry the controller didn't pick.

`CanSend` carries `[NotifyCanExecuteChangedFor(nameof(SendCommand))]`,
which is load-bearing rather than decorative. Avalonia's `Button` ANDs
a cached `CanExecute` verdict into `IsEnabledCore` and refreshes it only
when the command raises `CanExecuteChanged`, so without it the Send
button latches disabled the moment it binds to an incomplete clearance
and never recovers — even though `IsSendEnabled` and the footer status
both report the clearance as valid.

### Reviewing a sent PDC (read-only)

Selecting an item in the **PDC** list (Sent / Wilco) re-opens the
flight-plan editor seeded from the issued clearance
(`TdlsItemViewModel.SentPayload`) but in **read-only** mode
(`TdlsFlightPlanEditorViewModel.IsReadOnly`): every dropdown is
disabled, the Send button is hidden, and `IsSendEnabled` is forced
false so F12 can't resend. Read-only construction skips
`ApplyTransitionDefaults`, so the panel shows exactly what was issued
and never back-fills FE defaults into fields that were sent blank. The
footer reads "CLEARANCE TYPE: PDC — SENT (READ ONLY)". Dump and Cancel
remain available. (Selecting a Pending DCL item opens the same editor
editable, `IsReadOnly == false`.)

Auto-WILCO at 3 sim seconds simulates the real FMS auto-acknowledgement (which
fires near-instantly). The 2-hour TTL matches upstream's policy. Both count
sim seconds (`TdlsCommandHandler.DefaultWilcoDelay`, `TdlsMutations.DefaultTtl`)
against the session clock.

`SimulationEngine.TickTdlsTrackRemoval` (the `TdlsTrackRemoval` spine step) removes any
TDLS item — Pending or Sent/Wilco — once its aircraft is tracked on
STARS by any controller (a non-null `AircraftTrack.Owner`). A tracked
departure has left the clearance-delivery workflow, so the strip clears
from every vTDLS client. It reads live `Track.Owner` each tick, so it
catches every ownership source (explicit `TRACK`, handoff accept,
auto-track), and sets no `Dumped` lockout — the removal is an automatic
lifecycle event, not a controller dump.
