# YAAT vTDLS (PDC)

Reference for the Tower Data Link Services emulation in YAAT. Read this
before changing anything under:

- `src/Yaat.Server/Simulation/TdlsState.cs`,
  `TdlsMutations.cs`, `TdlsCommandHandler.cs`, `TdlsBroadcaster.cs`
- `src/Yaat.Server/Dtos/CrcDtos.Tdls.cs`
- `src/Yaat.Server/Hubs/TrainingHub.cs` (TDLS hub methods)
- `src/Yaat.Sim/Data/Vnas/ArtccConfig.cs` (TDLS config records)
- `src/Yaat.Sim/Commands/CanonicalCommandType.cs` (TDLSQ/TDLSS/TDLSW/TDLSDUMP)
- `src/Yaat.Client.Tdls/` (transport, viewmodels, view)
- `tools/Yaat.VTdls.Web/` (browser app served at `/vtdls/`)

For the upstream manual, see [`docs/vtdls/vtdls.md`](vtdls/vtdls.md)
(verbatim copy of [tdls.virtualnas.net/docs](https://tdls.virtualnas.net/docs/)).
For the user-facing command surface, see
[`COMMANDS.md`](../COMMANDS.md) (TDLSQ / TDLSS / TDLSW / TDLSDUMP entries).

## Overview

vTDLS is a separate vNAS web app that real controllers use to issue
Pre-Departure Clearances (PDCs). YAAT plays the role of that web app —
the **yaat-server** persists vTDLS state per training room and the
**Yaat.Client** desktop app + a browser-hosted WASM client (served at
`/vtdls/`) consume the state.

Unlike vStrips, vTDLS broadcasts are **SignalR-only**. CRC does not
subscribe to a TDLS topic on the WebSocket; the trainee's CRC client
has no vTDLS view. The vTDLS surface in YAAT is purely controller-side
state — what the issuer of the clearance sees.

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
   │                                IsTdlsCommand → HandleTdlsCmd
   │                                 │
   │                                 ▼
   │                                TdlsCommandHandler.HandleSend
   │                                 ├── validate facility config + mandatory fields
   │                                 ├── snapshot ClearanceDto, Status → Sent
   │                                 ├── ScheduledWilcoAt[id] = now + 3s
   │                                 ├── ApplyClearance(ac, dto, viaTdls:true)  → no voice readback
   │                                 ├── TerminalBroadcast (RPO-visible "[TDLS PDC sent at OAK] …")
   │                                 └── TdlsBroadcaster.BroadcastItemAsync
   │                                      │
   │                                      │  SignalR: TdlsItemChanged
   │                                      ▼
   │                                                                    pilot's clearance is set
   │                                                                    silently — no PendingPilotTransmissions
   │                                 (3s later: ProcessTdlsAutoWilco → Status=Wilco, broadcast)
   ▼
status flips on the issuer's vTDLS tab
```

## State model

`TdlsState` (per `TrainingRoom`) holds three things:

- **`Items`** — every TDLS list entry, keyed by item id (`TDLS_{n}`).
  - DCL list = items with `Status == Pending`.
  - PDC list = items with `Status == Sent` or `Wilco`.
- **`Configs`** — per-facility `TdlsConfig` (FE-defined SIDs, transitions,
  field defaults, mandatory-field flags). Loaded from the vNAS data-api
  on scenario load via `InitializeFromArtcc`. NOT snapshotted — re-fetched
  on session restore.
- **`Dumped`** — `(facility, callsign)` lockout (case-insensitive) so the
  auto-generator won't re-create entries the controller explicitly removed.
  Persists across snapshots.
- **`ScheduledWilcoAt`** — Sent items awaiting the 3 s auto-WILCO. Cleared
  on manual `TDLSW` or on Dump/expiry.

`TdlsItemRecord` (mirrors `TdlsItemDto` on the wire):

| Field           | Notes                                                                 |
|-----------------|-----------------------------------------------------------------------|
| `Id`            | `"TDLS_{n}"` — monotonic per session                                  |
| `AircraftId`    | Callsign (may not be active in sim yet — pre-files supported)         |
| `Cid`           | VATSIM CID; durable across despawn/respawn (currently `null` — no plumbed source yet) |
| `FacilityId`    | TDLS facility id (resolved from the filed departure airport)          |
| `Status`        | `Pending` / `Sent` / `Wilco`                                          |
| `Sequence`      | Monotonic per-state, for sort stability                               |
| `CreatedUtc` / `SentUtc` / `WilcoUtc` | Lifecycle timestamps                            |
| `ExpiresUtc`    | `CreatedUtc + 2 hours` (upstream TTL)                                 |
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

The full canonical syntax is enforced by `CommandRegistry` entries with
group `"vTDLS"` and asserted by completeness tests.

## Pilot side: TDLS-silent

A PDC issued via `TDLSS` calls `ApplyClearance(ac, dto, viaTdls: true)`.
`viaTdls=true` suppresses every voice-readback path:

- `PilotResponder` skips the standard clearance readback.
- `PilotProactive` and `PilotRequestTracker` honor `ac.Voice.TdlsDumped`
  (set on auto-WILCO) so no pilot transmission is queued for the
  clearance event.
- Subsequent voice events (CTO/takeoff readback, frequency changes,
  etc.) are unaffected — the pilot still talks normally after the
  silent PDC.

After a `TDLSDUMP`, the pilot is in the standard voice-clearance flow
(no flag set), so a follow-up `CL` voice clearance behaves normally.

## Auto-generation

`TickProcessor.ProcessAutoTdlsQueue` watches flight-plan creations.
When a flight plan filed at a TDLS-configured facility lands in the
room, the tick processor emits an internal `TDLSQ` so the controller
sees a Pending entry without typing anything. Conditions for the
auto-queue:

- The flight plan's departure airport (with leading `K` stripped) must
  match a facility id in `room.TdlsState.Configs`.
- `(facility, callsign)` must not be in the Dumped lockout.
- No existing Pending item for this (facility, callsign).

Pre-filed flight plans (a CID with a filed plan but no active
`AircraftState`) generate Pending entries too — matches upstream
behavior: "If a pilot pre-files prior to connecting to the network,
their flight plan is displayed in the DCL list."

## RPO terminal broadcasts

When `TDLSS` succeeds, `TdlsCommandHandler.HandleSend` emits a
`TerminalBroadcast` to the room's group with the issued clearance:

```
[TDLS PDC sent at OAK] Expect=10 MIN, SID=OAKLAND4.ALTAM, Maintain=5000, DepFreq=120.9
```

Empty fields are omitted from the summary. RPOs in the room see this
in their terminal pane without subscribing to the SignalR TDLS events,
so they can follow the student's PDC stream while focused on radar.

## Snapshot persistence

`RoomStateSnapshotMapper.CaptureTdls` writes:

- `Items` (with full `SentPayload` for Sent/Wilco items)
- `Dumped` lockout
- `NextItemId`

`Configs` and `ScheduledWilcoAt` are NOT persisted:

- `Configs` are re-fetched from the data-api on restore via
  `InitializeFromArtcc`.
- `ScheduledWilcoAt` is reset; restored Sent items stay at Sent until a
  manual `TDLSW` or TTL expiry. This is a deliberate choice — a 2-hour
  restart shouldn't auto-wilco every Pending PDC immediately on come-up.

## Lifecycle

| Event                                | Effect                                                       |
|--------------------------------------|--------------------------------------------------------------|
| Flight plan filed at TDLS facility   | `TDLSQ` auto-emitted → Pending entry                          |
| Controller selects Pending + Send    | Status → Sent; SentPayload snapshotted; auto-WILCO scheduled  |
| `ProcessTdlsAutoWilco` fires ~3 s later | Status → Wilco; `Voice.TdlsDumped = true`                  |
| Controller force-WILCOs              | Same as auto-WILCO; scheduler entry cleared                   |
| Controller dumps                     | Item removed from `Items`; (facility, callsign) → `Dumped`    |
| TTL > 2 hours since `CreatedUtc`     | `ProcessTdlsExpiry` removes the item; lockout NOT set         |
| Aircraft activates on departure      | (Future) — item removed; currently relies on TTL              |

## Multi-facility tabs and the consolidated parent view

The desktop client supports one vTDLS tab per facility. The student
entry (`TdlsEntries[0]`) auto-binds to the position's first accessible
TDLS facility after scenario load. Additional tabs open via **View →
vTDLS → New vTDLS Tab…** for any other facility in the position's
accessible set.

Per-facility geometry is keyed on `"VTdlsView:{facilityId}"`, so
multiple popped-out windows each remember their own size/position.

Per upstream: working a parent facility (e.g. NCT in ZOA) where the
child TDLS facilities (OAK/SFO/SJC/SMF/RNO) are not staffed top-down
gives you a consolidated view aggregating all of them. The current
YAAT implementation surfaces each child facility as its own tab —
selecting the parent in the facility menu is not yet wired up. Track
this in the plan.

## Web app at `/vtdls/`

`tools/Yaat.VTdls.Web/` publishes to
`../../yaat-server/src/Yaat.Server/wwwroot/vtdls/`. The server's
`YaatHost` maps `/vtdls/{**path:nonfile}` to `vtdls/index.html`.

The browser app gates the WASM boot on `?cid=…&initials=…&artcc=…` query
params (filled via the static landing form in `wwwroot/index.html`).
Subsequent connect follows the same shape as vStrips: `FindRoomForMyCid`
→ `JoinRoom` → `RefreshAccessibleTdlsFacilities` → `SwitchFacilityAsync`.

The browser app's `JoinRoom` uses `ClientKind.VTdls`, so the room
terminal log marks the participant as `(vTDLS)` for everyone else.

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

Auto-WILCO at ~3 s simulates the real FMS auto-acknowledgement (which
fires near-instantly). The 2-hour TTL matches upstream's policy. Both
are tunable via `SimScenarioState.TdlsWilcoDelaySeconds` and a
session-snapshot constant if needed.
