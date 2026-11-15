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
   │                                                                    (an open clearance request is satisfied —
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
- **`Expired`** — `(facility, callsign)` memory of items that ran out their TTL, so the
  auto-queue does not list that aircraft again for the rest of the session. No
  controller lockout: a manual `TDLSQ` still queues it, and clients are never told.
  Persists across snapshots (additive; an older snapshot restores it empty).
- **`ScheduledWilcoAt`** — Sent items awaiting the 3 s auto-WILCO. Cleared
  on manual `TDLSW` or on Dump/expiry.
- **`Changes`** — the broadcast seam (`TdlsChangeTracker`): every mutation records
  the ids it touched, the items it removed and whether a full state is owed. Not
  snapshotted; the host drains it (below). `SimulationEngine.AmendFlightPlan` marks the aircraft's items too:
  the item DTO carries no flight-plan copy — `DtoConverter.BuildTdlsFlightPlanInfo` reads the live
  `AircraftState` at broadcast time — so an amendment needs only the dirty mark the strip reprint already has.
  An amendment that moves the departure to another airport also removes the aircraft's items at the old
  facility, whatever their status (`ExpireItemsAtOtherFacilities`): the removal carries the old facility id,
  so a client on that facility drops the row; it sets neither `Dumped` nor `Expired`, and the next sweep queues
  a fresh Pending item at the new facility, or none when no TDLS facility serves it.

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
| `SentPayload`   | Snapshot of the clearance at TDLSS time (null while Pending). On the wire it is the YAAT-only `TdlsClearanceDto`: the nine `ClearanceDto` fields plus `SidName` / `TransitionName`, resolved at send time (null on a payload stored before names were kept). CRC's `ClearanceDto` is unchanged |

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

## Pilot side: the clearance request and the sent altitude

`TDLSS` records the clearance on the TDLS item (`SentPayload`) and answers the pilot's open clearance request: `TdlsCommandHandler.AnswerClearanceRequest` satisfies it through `PilotRequestTracker.SatisfyOpenRequest`, so the pilot stops following up and the awaiting-controller-response gate clears.

The send also captures the PDC's altitude onto the aircraft (`AircraftProcedure.SentPdcAltitudeKind` / `SentPdcAltitudeFt`, from `ArtccConfigResolver.GetSentPdcDepartureAltitude`). The Climbvia field governs (7110.65 §14-1-3.a.4): any climb-via text is a climb via, its "EXC MAINT X" the except-maintain even beside a different Maintain. "Except" is read in any of the spellings facility texts use (EXC, EXCPT, EXCEPT, EXPT, EXP) and "maintain" as MAINT, MAINTAIN or MTN, in a sent PDC and in a facility default alike. A Maintain beside a bare climb via is flown as "climb via SID except maintain" that altitude. A bare "CLIMB VIA SID" climbs to the SID's published top (FL190 when the SID publishes none). A Maintain with no climb via is a maintain, and a PDC with neither captures nothing.

The line-up and takeoff clearances (`DepartureClearanceHandler.StoreSidInitialAltitude`) read the captured altitude before the facility's TDLS default, so the pilot flies what the PDC said. A fixed altitude (an except-maintain) is kept across a runway change; only a bare sent climb via has its top worked out again for the new runway. A PDC with no altitude, or a sent climb via no filed SID activates for, falls through to the facility default.

The capture lives on the aircraft, not on the TDLS item: a dump, a STARS track removal or a TTL expiry after the send leaves it, and it rides snapshots. A PDC dumped before it is sent captures nothing, so the pilot flies the facility default. A new send replaces the capture. An amendment that moves the departure to an airport served by a different TDLS facility, or by none, clears it (`SimulationEngine.AmendFlightPlan`), whether or not an item still stands, so the pilot flies the new airport's default until a PDC is sent from there.

A later voice altitude outranks the capture without clearing it: a CM or DM, a CTO with an altitude and a controller `CVIA` all govern as they do over the facility default. A voice bare `CVIA` after a sent except-maintain climbs to the facility-default top, not to the sent altitude. A PDC sent after a line-up clearance has already put the facility default's climb via in force does not replace it: the TDLS-default via already governs, and the takeoff clearance only works out its top again for the runway. A facility-default maintain stored at the line-up does give way to the sent altitude at the takeoff clearance.

Nothing else reaches the pilot model: no pilot transmission is queued or suppressed, and a follow-up voice `CL` behaves exactly as it would without the PDC. The rest of the PDC flow (applying the whole sent clearance to the aircraft silently) is unbuilt and unscheduled (design record: [`docs/vtdls/emulation-design.md`](vtdls/emulation-design.md)).

`AircraftVoice.TdlsDumped` is CRC's own flight-plan flag ("the PDC was dumped,
clear this aircraft by voice"), set only by CRC's `TdlsDump` handler
(`CrcClientState.Session.cs`) and projected onto the `FlightPlans` topic; the
auto-WILCO does not set it and nothing in `Yaat.Sim` reads it.

## Auto-generation

`SimulationEngine.TickAutoTdlsQueue` (a post-physics Sim step) sweeps the
world's aircraft every second, and `SimulationEngine.AfterAircraftSpawned` runs the same
check inline for every spawn (scenario load, a delayed or generated spawn,
`ADD`, a recorded spawn on replay), so a departure filed at a
TDLS-configured facility shows a Pending entry without the controller typing
anything. Both go through `TryQueueAutoTdlsForAircraft`, so the tick and the spawn hook
apply one gate. Conditions for the auto-queue:

- The flight plan's departure airport (with leading `K` stripped) must
  match a facility id in `TdlsState.Configs`.
- The aircraft must still be awaiting its clearance (`IsAwaitingClearance`): on the ground,
  never airborne, tracked by no controller and with no departure message (the flight plan is
  still Proposed, the rule `DtoConverter.ToFlightPlanStatus` applies in yaat-server). An airborne
  arrival, a departed flight or a tracked aircraft is never listed; an aircraft whose owner is
  dropped while still grounded is listed again.
- `(facility, callsign)` must not be in the Dumped lockout or in `Expired`.
- No existing item for this (facility, callsign) in any status.

Only aircraft in the world are swept: a pre-filed flight plan with no `AircraftState` gets no
entry until its aircraft spawns, unlike upstream, where "If a pilot pre-files prior to connecting to
the network, their flight plan is displayed in the DCL list."

## Names and the frequency token

A SID id means something only against the ops config active when it was chosen. `TdlsCommandHandler` therefore resolves the SID and transition names at send time against the facility's SID list then in force and stores them on `SentPayload` (`TdlsClearance.SidName` / `TransitionName`; the ids stay). The pilot PDC text and the terminal line read those names, so a PDC keeps naming the SID sent after `TDLSOPS` changes the active ops config. A placeholder transition is left out of the text. A payload stored before names were kept reads them as null and falls back to resolving the ids against the facility-level SID list.

`$FREQ` in a contact-info or local-info value, or a transition default, is filled with the student position's primary frequency in `0.000` MHz form (`CTC 121.100 FOR PUSH`). yaat-server `ArtccConfigService.BuildTdlsConfigDto` fills it when it builds the page's config, and `TdlsCommandHandler.HandleSend` fills it again on a typed `TDLSS` (`TdlsFreqToken`). With no student position, or one whose frequency is 0, the token stays literal. The substitution is on the projection and the payload only; the shared cached ARTCC config is never changed.

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
`SentPayload` the Sim record `TdlsClearance` (the nine canonical `TDLSS` fields plus the SID and
transition names resolved at send time). The CRC wire types `TdlsStatus` and `ClearanceDto` (the
`TDLSS` body) stay in yaat-server, pinned by `CrcWireContractTests`; `DtoConverter.ToTdlsItem` /
`ToTdlsClearanceDto` project the item onto them and the payload onto the YAAT-only
`TdlsClearanceDto`. The mutation bodies are the
engine's too: `TdlsMutations` and `TdlsCommandHandler` in
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
(a reconstruction; the room re-syncs when it lands). An item whose DTO equals the one last sent in a delta (`TrainingRoom.LastSentTdlsItems`) is not re-sent, so an amendment touching only fields the item does not show (e.g. `Scratchpad1`) reaches no client; a full-state send clears that cache. The scenario load drains once at the end of
`PopulateRoom` so the load-time PDCs reach the clients as before. `BareHost` discards the set.

`TdlsSnapshotMapper.Capture` (into `ServerSnapshotDto.Tdls`, every snapshot the engine takes —
recordings, bundles, session checkpoints) writes:

- `Items` (with full `SentPayload` for Sent/Wilco items)
- `Dumped` lockout
- `Expired` — the TTL-expiry memory
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
| TTL > 2 hours since `CreatedUtc`     | `TickTdlsExpiry` removes the item; no `Dumped` lockout, but (facility, callsign) → `Expired`, so the auto-queue does not re-create it (manual `TDLSQ` still can) |
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

`tools/Yaat.VTdls.Web/` publishes (its `CopyToServerWwwroot` target, run by yaat-server's `Dockerfile` with `-p:YaatServer=/src`) into `src/Yaat.Server/wwwroot/vtdls/` of the yaat-server checkout; that folder is a build output, not committed. The server's
`ServerApp` maps `/vtdls/{**path:nonfile}` to `vtdls/index.html`.

The browser app gates the WASM boot on `?cid=…&initials=…&artcc=…` query
params (filled via the static landing form in `wwwroot/index.html`).
Subsequent connect follows the same shape as vStrips: `FindRoomForMyCid`
→ `JoinRoom` → `RefreshAccessibleTdlsFacilities` → `SwitchFacilityAsync`.

`MainView.BootstrapFacilityAsync` is the one place that puts the page on a facility, after a join, a rejoin or a scenario change: it refetches the accessible list, reloads the page already shown when still accessible (`VTdlsViewModel.ReloadFacilityAsync`), else switches to the first leaf facility, and with none accessible calls `VTdlsViewModel.Clear`. The app remembers its last successful-or-in-flight join and, on `Reconnected` (a reconnect lands the connection in the lobby only), joins it again; `BrowserTdlsTransport.ScenarioLoaded` / `ScenarioUnloaded` re-run the bootstrap. Both server events carry a payload the client drops, bound through the empty `BrowserTdlsScenarioChangedDto` because `YaatTdlsHubJsonContext` has no reflection fallback (`TdlsHubJsonContextTests`).

The browser app's `JoinRoom` uses `ClientKind.VTdls`, so the room
terminal log marks the participant as `(vTDLS)` for everyone else.

The desktop client opens this app via **Tools → Open TDLS in Browser**
(`MainViewModel.OpenTdlsInBrowserCommand`), which shells out to the
default browser with `{server}/vtdls/?cid=…&initials=…&artcc=…&room=…`
prefilled from the live connection — the vTDLS analog of **Open Strips
in Browser**.

## Light and dark mode

The Facility Menu's dark-mode toggle sets `VTdlsViewModel.IsDarkMode`; `VTdlsView.ApplyTheme` swaps the `VTdls*` brushes and pins the view's own `RequestedThemeVariant` to the same mode, so the Fluent parts it does not repaint (scroll bars, focus rings, the Ops Config flyout) follow the vTDLS mode rather than the host app. The view starts light.

The mode is chosen per host. The desktop tab seeds it from `UserPreferences.IsVTdlsDarkMode` and writes every change back (`MainViewModel.Tdls.cs`, `OnTdlsDarkModeChanged`, which also syncs the other open vTDLS tabs). The browser app defaults to light (`App.axaml`) and keeps its toggle in `localStorage` under `yaat-vtdls-dark-mode` (`MainView.axaml.cs`), which also flips the Application variant for the page chrome outside the view.

The editor's dropdowns and its Dump / Edit / Cancel / Send buttons are styled by the view from the `VTdlsControl*` brushes, set for both modes in `ApplyTheme`: grey with a mid-grey border and black text in light mode, a step lighter than the panel with pale text in dark mode, a paler fill and dimmer text when disabled, darker on hover and press. The `- - - -` placeholder is drawn at full strength. `VTdlsViewInteractionTests` (`Theme_*`) checks the pinned variant and the control contrast in both modes.

## Key commands

The keyboard model follows upstream's vTDLS. Two pieces of `VTdlsViewModel` state drive it: `ActiveList` (`TdlsAircraftList`: Dcl, Cpdlc, Pdc) and `HighlightedItem`, the row Up/Down point at and Enter opens. The highlight is not the selection (`SelectedItem`): only Enter, a row click and a send move one to the other. Existing tests that say "highlight" mean the selection; here the word means this pointer (see the glossary).

`VTdlsView` routes the keys and the view model holds the logic (`MoveHighlight`, `CycleActiveList`, `OpenHighlighted`), pinned by `VTdlsKeyboardModelTests` and `VTdlsViewInteractionTests`:

- **No editor open.** Up/Down move the highlight within the active list and wrap, opening nothing; Enter opens the highlighted row as a click does. Tab, or backtick / Shift+Tab backwards, cycles the active list DCL, CPDLC, PDC; CPDLC is an empty stop and entering a list highlights its first row. The active list's first row is also highlighted when the page opens and when items arrive.
- **Editor open.** The SID box takes focus when an editable editor opens. Tab steps Sid, Transition, Climb out, Climb via, Maintain, Dep Freq, Expect, Contact info, Local info; Shift+Tab and backtick go backwards; Left/Right do nothing.
- **After a Send** the highlight moves to the next DCL row.
- **Esc** toggles the Facility Menu; with Find open it closes Find only. **Ctrl+Alt+Right / Left** switch to the next / previous accessible facility, wrapping, and do nothing while disconnected.
- A click on a row still opens it and moves the highlight and the active list there (`HighlightSelected`).
- The keys reach the view only while it has keyboard focus, and it does not take focus when it opens: a click or Tab into it comes first.

The active list's header is bright green and bold, the others light green and normal weight. The highlighted row is drawn inverted by a `highlighted` class on the row container (`VTdlsHighlightBg` / `VTdlsHighlightFg`, set per mode in `ApplyTheme`: white on black in light mode, black on white in dark); the open row keeps the selection colours.

## Temporary and override departure frequencies

The Facility Menu holds three view-local settings under a separator after Dark Mode: **Temp. Dep Freqs** (a text box), **Override Dep Freq** (a drop-down of "None" plus the temp frequencies) and **Add current frequency to Temp. Dep Freqs** (a checkable item). They live on `VTdlsViewModel` (`TempDepFreqs`, `OverrideDepFreq`, `AddCurrentFrequencyToTempDepFreqs`), are not saved, are lost when the view closes and survive a facility switch.

`VTdlsView` commits the typed text through `CommitTempDepFreqs` on Enter, on losing focus and when the menu closes. One entry that fails `TempDepFreqPattern` rejects the whole line: the list empties and `IsTempDepFreqsInvalid` shows the red "Invalid Frequency" line. A commit that drops the current override clears it. The check box adds the student position's primary frequency, which arrives as `TdlsFacilityViewDto.StudentFrequencyMhz` from `GetTdlsFacilityView` (null when the position has none).

The editor takes the settings as `TdlsDepFreqChoices` when it opens: `OfferedDepFreqs` lists the facility's frequencies, then the temp ones, and `ApplyOverrideDepFreq` selects the override when the editor opens and after every SID or transition change. A change reaches only editors opened afterwards, and the Sent and Wilco review editors are unchanged. `VTdlsViewInteractionTests` and `TdlsFlightPlanEditorViewModelTests` cover it.

## Phraseology compliance notes

PDC content phraseology follows AIM 5-2-2 (Pre-Departure Clearance
Procedures) and FAA Order 7210.3. The simulated FE-configured fields
exactly match real-world PDC structure:

- **Expect** — "EXPECT 10 MIN AFTER DEPARTURE" / "EXPECT VECTORS AFTER
  DEPARTURE" wording
- **Aircraft type** — `TdlsFlightPlanInfoDto.AircraftType` is the bare type (`AircraftFlightPlan.BaseAircraftType`);
  the client appends `EquipmentSuffix` once (`TypeAndEquipment`), the same split vStrips uses — a scenario that files
  `B77W/L` inside the type field shows `B77W/L`, never `B77W/L/L`
- **SID + Transition** — published procedure id (e.g. `OAKLAND4.ALTAM`)
- **Climbout / Climb Via** — "ON COURSE" or "FLY RUNWAY HEADING" etc.;
  separate "CLIMB VIA SID" instruction when applicable
- **Initial Alt / Maintain** — assigned altitude in feet
- **Dep Freq** — departure-control frequency
- **Contact Info** — "CTC SAN FRANCISCO DEP" or equivalent
- **Local Info** — squawk, hold-short, etc., free-form per facility

Every read-only value in the flight-plan header block and the footer status is a `SelectableTextBlock` (the control the vStrips METAR bar uses), so a controller can copy a route or squawk the way the real vTDLS web page allows; labels and the field ComboBoxes stay plain.

The DCL/PDC **list rows stay `TextBlock`**: `SelectableTextBlock` marks `PointerPressed` handled, so the press never reaches the `ListBox` and clicking a row would stop selecting it — the same trap applies to any `SelectableTextBlock` inside a `ListBoxItem` anywhere in the app.

The two lists must not share one two-way `SelectedItem` binding: a selection pushed into a list that does not hold the item makes that list clear its selection and write null back over the click, which stuck the view after reviewing a sent PDC. `DclListBox` binds `SelectedPendingItem` and `PdcListBox` binds `SelectedSentItem`, both mirrored from `VTdlsViewModel.SelectedItem` (the one selection every action reads), and a send clears `SelectedItem` explicitly. `VTdlsViewInteractionTests` clicks through the `ListBox` rows to pin it. The keyboard highlight (`VTdlsViewModel.HighlightedItem`, see "Key commands") is a third, separate pointer: it is not bound to either `ListBox` selection and selecting never reads it, so moving it with the arrow keys opens nothing.

The footer's style selectors are `:is(TextBlock).footer-status` because an Avalonia type selector matches the exact type. `VTdlsViewInteractionTests` pins the control type and the selection API; the headless drawing backend cannot hit-test text, so drag-selection itself needs an eyeball in the running client.

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

`VTdlsViewModel.CommandError` takes the footer in place of the editor status text (and sets the warning class) for the last failure the controller must see: no `TdlsConfigDto` for the selected item's facility, an editor that threw or a sent item with no clearance (`RejectSelection` also releases the row on the dispatcher so a second click re-fires), and a send or dump whose `CommandResultDto` failed or never came (`ReportCommandResult`; the `sendCommand` delegate now returns the result, null when the send threw or never went out). The next selection, a successful send or dump, `Clear` and a facility page apply reset it. `MatchesActiveFacility` is false until a page is applied, so early items wait for the full-state request that follows every apply. While `IsConnected` is false Send and Dump are disabled and F4/F12 are ignored; the Dump button is hidden on a read-only (sent or Wilco) editor, F4 still dumps.

### Climb via and Maintain are mutually exclusive

7110.65 §4-3-2 gives a departure clearance one altitude instruction, not
two: "climb via SID" (optionally "except maintain (altitude)") when the
SID publishes crossing restrictions, or "maintain (altitude)" when no SID
is assigned, the SID publishes none, or it is a radar-vectors SID. So a
real climb-via pick **clears** Maintain and a real Maintain pick clears
climb-via; neither dropdown is ever disabled, so the controller always has
a way back. A mandatory `InitialAlt` counts as satisfied by a climb-via on
both gates — the editor's missing-field list and the Sim's
`ValidateMandatoryFields` — so a facility setting `mandatoryInitialAlt`
over a climb-via SID is not a dead end.

**The suppression has to read live state.** Some FE transitions define
both defaults: ZOA's OAK `NUEVO8`/`SLNT3` pair `CLB VIA SID EXC MAINT
10000FT` with `defaultInitialAlt` `10000FT`, duplicating the altitude,
and SMF's `SCTWN4`/`RVRCT4` pair a climb-via with the `- - - -`
placeholder. `TransitionDefaultFields` lists Climbvia ahead of InitialAlt
and the InitialAlt entry carries a suppression predicate rather than a
captured bool, so the climb-via is assigned first and the Maintain
default is then skipped. A bool captured when the array is built would
read the pre-assignment value and fill both. Read-only editors suppress
nothing: a sent PDC under review shows exactly what was issued, both
fields included.

**The `- - - -` placeholder is no value.** It is a real selectable list
entry at SMF, not just a display string for an empty dropdown.
`TdlsPlaceholder` (Yaat.Sim) is the one predicate both sides use: a
placeholder climb-via does not clear Maintain, a placeholder selection
never satisfies a mandatory field, and `ClearancePayloadFromFields` nulls
it on the way in, so a PDC no longer reaches the pilot reading
`MAINT - - - -`.

### Operational configurations

A facility with `dclOpConfigsEnabled` keeps **no** SIDs at facility level — every SID moves into
an ops config, each carrying its own per-transition defaults so an east/west split issues
different departure frequencies, maintain altitudes or local info. SFO, OAK and BOS all ship an
empty facility-level `sids` array today.

Read the SID list through `TdlsConfig.ResolveSids(opConfigId)` (Sim) or
`TdlsConfigDto.ResolveSids` (client), never off `Sids` — the raw field is empty at exactly the
facilities controllers use most. `ResolveDefaultSidId` / `ResolveDefaultTransitionId` follow the
same rule; whether a config sets them is per-facility (SFO both, BOS SID only, OAK neither).

`TdlsFlightPlanEditorViewModel.MatchSidFromFiledRoute` pre-selects the SID and transition from the filed route. A route
that names a SID takes the transition from the token after it, or, when that matches none, from a later route fix that
starts one of the SID's transitions; a route that names no SID selects the SID and transition whose first route point the
route passes through (the first in route order, then config order). The transition matches on its `FirstRoutePoint` first and falls back to its `Name`: both are
FE-authored pass-throughs of `tdlsConfiguration.opConfigs[].sids[].transitions[]`, and ZOA's SFO config sets
`firstRoutePoint` on TRUKN2's ORRCA transition but not SNTNA2's, though both are named `ORRCA`. The no-transition
placeholder (`- - - -`) never matches. `TdlsFlightPlanEditorViewModelTests` pins the fallback. An amendment pushed into an
open editor (`VTdlsViewModel.ApplyItem` → `TdlsFlightPlanEditorViewModel.ApplyAmendedFlightPlan`) re-runs the same match when
the route changed and re-seeds SID + transition; a push that leaves the route alone touches no selection.

**A SID or transition change applies the transition's defaults** (upstream: "Selecting a SID and transition pair also
populates the remaining fields with default values defined by the Facility Engineer", `docs/vtdls/vtdls.md`): every field
the new transition defines a default for is overwritten (`ApplyTransitionDefaults`, run from the change hooks — a manual pick
or the amended-route re-seed), and a field it defines none for is emptied. Only a real transition sets anything: clearing the
SID or the transition with `- - - -` empties just those selections and leaves the seven fields as they are. Construction is the one back-fill site
(`BackFillTransitionDefaults`): the seed there is a saved Pending clearance, which outranks the defaults, so only blanks are
filled. Read-only editors apply nothing either way. Were the change hooks to back-fill only blanks, a SID
change would keep the previous SID's departure frequency and altitude.

**Every editable dropdown offers a leading `- - - -` clear entry** (`TdlsFlightPlanEditorViewModel.ClearValue`, `ClearSid`, `ClearTransition`, id `ClearEntryId`, distinct from the FE's own placeholder rows). Picking it is turned into null at once, so it never reaches the clearance or the mandatory check. The Transition list offers it only when the SID has no transitions, and a read-only (sent) editor offers none. Re-picking the shown entry changes nothing.

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
> leaving `SelectedSid` holding an id that no longer resolves. A sent PDC under review is immune: it
> carries the SID and transition names it was sent with (see "Reviewing a sent PDC").

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
remain available. The SID dropdown offers only the SID the PDC was sent
with, built from the payload's `SidName` and `TransitionName` rather than
looked up by id, so a later `TDLSOPS` change cannot alter what the review
shows; the transition dropdown holds that one transition, or is empty when
none was sent. A payload stored before names were kept (no `SidName`) still
resolves its ids against the active config. Find (Ctrl+F) matches the SID
and transition names as well as their ids. (Selecting a Pending DCL item opens the same editor
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

## File map

One entry per file, grouped by project.

### Yaat.Client.Tdls — WASM-clean vTDLS layer (`src/Yaat.Client.Tdls/`)

Sibling of Yaat.Client.Strips for the vTDLS (Pre-Departure Clearance) view. Same constraints: pure Avalonia + SignalR + CommunityToolkit.Mvvm — no Avalonia.Desktop, no Velopack, no file IO. Both the embedded vTDLS tab in Yaat.Client and the browser app at `tools/Yaat.VTdls.Web` consume this assembly. ProjectReferences `Yaat.Sim` + `Yaat.Client.Strips` (the latter just for the shared JetBrains Mono font registration via `WithJetBrainsMonoFont()`).

```
Services/
  TdlsDtos.cs                   # Client-side JSON mirrors of the server vTDLS DTOs: TdlsStatus enum, TdlsItemDto, TdlsItemRemovedDto, TdlsStateDto, ClearanceDto, TdlsConfigDto + nested SID/transition/value records, TdlsFacilityViewDto (one config per member facility — a consolidated parent carries its children's — plus the student position's `StudentFrequencyMhz`). Property names match the server one-for-one so System.Text.Json round-trips without converters.
  ITdlsTransport.cs             # Narrow contract VTdlsViewModel depends on. IsConnected + transport-state events + TdlsItemChanged/Removed/StateChanged broadcasts + GetAccessibleTdlsFacilities/GetTdlsFacilityView/RequestFullTdlsState RPC trio. ServerConnection (Core) and BrowserTdlsTransport (here) both implement it.
  BrowserTdlsTransport.cs       # WASM-side ITdlsTransport. Owns its own HubConnection, wires JsonHubProtocol against YaatTdlsHubJsonContext only, exposes auto-join helpers (FindRoomForMyCidAsync, JoinRoomAsync, SendCommandAsync, ConnectAsync, RoomAvailableForCid, ScenarioLoaded and ScenarioUnloaded events) needed by tools/Yaat.VTdls.Web/MainView. Browser-only DTO subsets (BrowserTdlsRoomInfoDto, BrowserTdlsJoinRoomResultDto, and the empty BrowserTdlsScenarioChangedDto that binds the dropped scenario-event payloads) downscope the wire format.
  YaatTdlsHubJsonContext.cs     # Source-generated JsonSerializerContext for the TDLS DTO subset. Inserted into the JsonHubProtocol resolver chain by both ServerConnection (alongside YaatHubJsonContext + YaatStripsHubJsonContext) and BrowserTdlsTransport (alone).

ViewModels/
  VTdlsViewModel.cs             # Root vTDLS VM; reconciles DCL (Pending) and PDC (Sent+Wilco) lists from broadcast events; surfaces accessible-facility list + Switch/Refresh. `SelectedItem` is the one selection; `SelectedPendingItem` / `SelectedSentItem` are the DCL and PDC lists' own two-way bound mirrors of it (the lists must not share one binding). Ctor takes ITdlsTransport + send-command delegate + Func<string>? getUserInitials. Also holds the keyboard model's state, separate from the selection: `ActiveList` (DCL / CPDLC / PDC) and `HighlightedItem`, moved by `MoveHighlight` / `CycleActiveList` / `OpenHighlighted`. Clear() empties every item, the selection and any open editor (called on connection loss and by the host when the room's session goes away).
  TdlsItemViewModel.cs          # Per-item observable: AircraftId, Status, Sequence, timestamps, SentPayload. Instance identity preserved across reconciles so Avalonia bindings stay stable.
  TdlsFlightPlanEditorViewModel.cs # Nine-field editor; wraps a working ClearanceDto, exposes per-field dropdowns from the facility's TdlsConfigDto, applies SID+transition defaults on selection, gates Send button on mandatory-field completion. Every editable dropdown lists a leading "- - - -" that empties its field. A real climb-via value clears Maintain and a real Maintain clears climb-via (Maintain is never disabled), and a climb-via satisfies a mandatory InitialAlt — one altitude instruction per clearance. Takes the view's `TdlsDepFreqChoices` (temp frequencies, override) when opened: Dep Freq lists the facility's frequencies then the temp ones, and the override is selected on open and after each SID or transition change.
  VTdlsCanonicalBuilder.cs      # Build canonical TDLS commands (TDLSQ / TDLSS Expect|Sid|... | LocalInfo / TDLSW / TDLSDUMP) from UI gestures.

Views/VTdls/
  VTdlsView.axaml(.cs)          # Embedded vTDLS UserControl. Layout mirrors upstream tdls.virtualnas.net: black header chrome, DCL list on top (full width, WrapPanel Vertical column wrap), PDC + decorative empty CPDLC split 50/50 below, flight-plan editor docks at bottom when a DCL item is selected, footer with CLEARANCE TYPE status + Zulu clock. Hosts the shared FindBar overlay (Ctrl+F, searches the DCL + PDC lists). Facility Menu after Dark Mode: the Temp. Dep Freqs box (committed on Enter, focus loss or menu close), Override Dep Freq and Add current frequency. Key bindings: F4 Dump, F10 close editor, F12 Send, Ctrl+F / F3 / Shift+F3 find, Up/Down/Enter/Tab/backtick for the keyboard highlight and editor field order, Esc for the Facility Menu, Ctrl+Alt+Left/Right for the facility; the active list's header and the highlighted row are styled from the view model. `ApplyTheme` swaps the light/dark brushes (incl. the `VTdlsControl*` ones for the editor's dropdowns and buttons) and pins the view's own theme variant, so it renders alike on the dark desktop host and the light web host.
```

### Yaat.VTdls.Web — Browser vTDLS client (`tools/Yaat.VTdls.Web/`)

WebAssembly Avalonia client for the vTDLS view. Hosted by yaat-server at `/vtdls/` (mapped in `ServerApp`). Mirrors the VStrips.Web shape: references Yaat.Client.Tdls + Yaat.Client.Strips (for the shared JetBrains Mono font registration), no Avalonia.Desktop / Velopack / file IO. Identity (CID, initials, ARTCC) flows in via URL query — first-time visitors fill a landing form that redirects with the params filled in.

```
Program.cs                       # Entry point. Wires SimLog to ConsoleLineLoggerProvider. Stores window.location.search + origin on App.
App.axaml(.cs)                   # XAML app root with the Light theme variant (upstream vTDLS is light-themed) + SubtleTextBrush/MonoFont resources.
MainView.axaml(.cs)              # Root view. Hosts VTdlsView, handles auto-join via BrowserTdlsTransport, calls RefreshAccessibleFacilities + SwitchFacility on the first facility after JoinRoom.
wwwroot/index.html               # WASM host page; landing form (DOM-only, no innerHTML); gates the WASM boot on identity params; localStorage key `yaat-vtdls-identity`.
wwwroot/main.js                  # Boot script.
wwwroot/app.css                  # Page chrome (status footer + landing form).
runtimeconfig.template.json      # net10.0-browser runtime config.
```

### Yaat.Sim (`src/Yaat.Sim/`)

Paths are relative to `src/Yaat.Sim/`.

```
# Simulation/Tdls/ — vTDLS session state + command/tick logic, engine-owned (SimulationEngine.Tdls, SimulationEngine.Tdls.cs)
TdlsState.cs                   # The DCL (Pending) and PDC (Sent/Wilco) lists plus a Dumped lockout (keeps a controller-removed entry from being
                               # re-created) for one run. Engine-owned; a fresh engine starts empty and the snapshot carries the session. Configs
                               # (per-facility TdlsConfig) is the exception — reloaded from the ARTCC on every scenario load and never snapshotted,
                               # so ClearSession (not Reset) is what a restore uses and leaves Configs alone; Reset also clears Configs, for
                               # scenario unload. ResolveActiveOpConfigId / ResolveSids honor the facility's active operational configuration.
                               # Changes (TdlsChangeTracker) records what TdlsMutations touched since the host's last drain — transient, not
                               # snapshotted, cleared by ClearSession alongside the session state.
                               # TdlsItemRecord: one list entry (Pending → Sent → Wilco, removed on Dump/TTL-expiry/activation); TdlsItemStatus
TdlsChangeTracker.cs           # The broadcast seam for TdlsState: TdlsRemoval (one item that left Items — dumped, or removed by TTL/track-removal),
                               # TdlsChangeSet (ChangedItemIds, Removed, FullState — what the host broadcasts from) and TdlsChangeTracker itself
                               # (MarkChanged/MarkRemoved/MarkFullState; Drain takes everything accumulated and resets). IStateChangeConsumer.OnTdlsChanged
                               # (Spine/IStateChangeConsumer.cs) is the interface both IActionHost and IHostConsumers share, so the router (after every
                               # routed action) and the post-physics StateChanges spine step (for what the tick steps produced) each reach it once.
                               # Transient — never snapshotted, cleared by TdlsState.ClearSession — mutated under TdlsState.Gate like the state it describes.
TdlsMutations.cs               # Stateless mutation helpers for TdlsState (queue/send/mark-Wilco/dump/expire, ResolveFacilityForAirport, FindActiveItem) —
                               # callers hold Gate externally. Helpers that allocate new ids advance NextItemId; helpers that change status return the
                               # updated record (or null if the item didn't exist) and record it in TdlsState.Changes for the host to broadcast.
                               # ClearancePayloadFromFields nulls an FE placeholder as well as an empty field, so a "- - - -" pick never reaches
                               # SentPayload, the PDC text or the snapshot.
TdlsCommandHandler.cs          # Static dispatch for TDLSQ / TDLSS / TDLSW / TDLSDUMP against SimulationEngine.Tdls: resolves the aircraft's TDLS facility,
                               # mutates through TdlsMutations, and builds the ACARS PDC text for a TDLSS send; owns the auto/manual WILCO delay
                               # (DefaultWilcoDelay). ArmTable's Tdls/TdlsOps rows call it (and SimulationEngine.ApplyTdlsOpConfig) as Sim bodies now,
                               # not host slots. ValidateMandatoryFields treats a climb-via as satisfying a mandatory InitialAlt — one altitude
                               # instruction per clearance (7110.65 4-3-2), so requiring both would be a dead end.
TdlsClearance.cs               # The clearance a PDC carries: the nine canonical TDLSS payload fields, in the order the command describes them.
                               # The simulation's clearance model — held by TdlsItemRecord and round-tripped by the snapshot; the server projects
                               # it onto the CRC wire ClearanceDto on its way out. SidName / TransitionName keep the names the ids picked at send
                               # time (null on a payload stored before names were kept, which falls back to the id lookup)
TdlsFreqToken.cs               # The $FREQ token in a configured contact-info or local-info value: StudentPrimaryFrequencyMhz (the student position's
                               # frequency as 0.000 MHz, null with no student position or a 0 frequency) and Apply (replaces the token, else returns the
                               # value unchanged). A projection only — the shared cached ARTCC config is never written back
TdlsPlaceholder.cs             # The one predicate for the FE's "no value" entry (blank, or dashes and spaces only — "- - - -" is a real selectable
                               # list entry at SMF, not just an empty dropdown's display text). Shared by the Sim's PDC text and mandatory-field
                               # gates and by the client editor's transition matching, climb-via rule and missing-field list.
```
