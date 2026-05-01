# YAAT Flight Strips

Reference for how flight strips (full and half) work end-to-end in YAAT.
Read this before changing anything under
`src/Yaat.Server/Dtos/CrcDtos.Strips.cs`,
`src/Yaat.Server/Simulation/FlightStripState.cs`,
`src/Yaat.Server/Hubs/CrcClientState.Strips.cs`,
`src/Yaat.Server/Simulation/RoomEngine.cs` (strip handlers),
`src/Yaat.Server/Data/ArtccConfigService.cs` (bay lookup), or the
strip-related entries in `src/Yaat.Sim/Commands/` (parser, registry,
canonical type, describer).

For the user-facing command surface, see [`COMMANDS.md`](../COMMANDS.md)
(quick-reference + detailed Half-Strips subsection). For CRC's own
vStrips manual — the authoritative reference for strip semantics,
UI behavior, and terminology — see [`docs/crc/vstrips.md`](crc/vstrips.md).

## Overview

CRC's vStrips is a web app that simulates paper flight progress strips.
Real controllers run vStrips alongside CRC; it subscribes to the
`FlightStrips` topic on the CRC WebSocket and receives strip state from
whichever client owns a given position.

YAAT plays the role of that client. The **yaat-server** persists strip
state per training room and broadcasts it to any connected CRC / vStrips
instance over the CRC protocol. The **Yaat.Client** (instructor / RPO
desktop app) has no strip rendering UI of its own — it only produces
strip commands; the actual strip display lives in the trainee's CRC.
This means strip features in YAAT only become visible when a real CRC +
vStrips client is attached to the room.

```
Yaat.Client (instructor)              yaat-server                          CRC + vStrips (trainee)
─────────────────────────             ──────────────────                   ───────────────────────
user types "HSC Ground Hello\World"
   │
   │  SignalR: SendCommand(callsign, canonical, initials)
   ▼
CommandParser.Parse                   TrainingHub.SendCommand
   │                                   │
   │                                   ▼
   │                                  RoomEngine.SendCommandAsync
   │                                   │
   │                                   ▼
   │                                  IsStripCommand → HandleStripCmd
   │                                   │
   │                                   ▼
   │                                  HandleHalfStripCreate
   │                                   ├── ArtccConfigService.GetAccessibleStripBay
   │                                   ├── StripItemRecord(HSTRIP_{guid}, HalfStripLeft, …)
   │                                   ├── Room.StripState.Items[id] = record
   │                                   ├── PrependStripToBay(state, bayId, rack, id)
   │                                   └── BroadcastStripItemsAsync
   │                                        │
   │                                        │  CRC WebSocket: ReceiveStripItems [StripItemDto]
   │                                        ▼
   │                                                                       vStrips renders the half-strip
   ▼
(command result returned over SignalR; YAAT client shows success/error in StatusText)
```

## Strip types

The supported types are defined in `src/Yaat.Server/Dtos/CrcDtos.Strips.cs`
as the `StripItemType` enum, and match CRC vStrips one-for-one:

| Value | Name | What it is |
|-------|------|------------|
| 0 | `DepartureStrip` | Full strip printed from a filed IFR departure flight plan. 18 annotation-aware field slots. |
| 1 | `ArrivalStrip` | Full strip auto-printed when an airborne aircraft is within 20 minutes of destination. Similar field layout. |
| 2 | `HandwrittenSeparator` | Rack separator with freeform label. |
| 3 | `WhiteSeparator` | Colored rack separators. |
| 4 | `RedSeparator` | — |
| 5 | `GreenSeparator` | — |
| 6 | `HalfStripLeft` | Freeform half-height strip, occupying the left side of a rack slot. |
| 7 | `HalfStripRight` | Same, slid to the right side. |
| 8 | `BlankStrip` | Blank full-size strip printed on demand. |

YAAT produces types `0` (via `STRIP`), `1` (auto), `2`–`8` (via `SEP`,
`HSC`, `HSS`, `BLANK`, etc.). Arrival strip auto-printing is triggered
by `TickProcessor.ProcessAutoArrivalStrips` when the arrival is within
`StripMutations.ArrivalAutoPrintMinutes` (default 20.0) of destination.

### Departure strip auto-routing by student position

Where an auto-printed departure lands depends on the student's position
type (resolved from the callsign suffix in `DeterminePositionType`):

| Student type | Suffixes | Auto-print destination |
|--------------|----------|------------------------|
| Tower | `_TWR`, `_LOC` | First own bay whose name starts with "Ground" (case- and whitespace-insensitive). Falls back to printer queue if no Ground bay exists. |
| Ground | `_GND`, `_DEL` | Departure printer queue (student plays Clearance Delivery and physically picks the strip up). |
| Approach | `_APP`, `_DEP` | No spawn auto-print. `TickProcessor.ProcessAutoApproachDepartureStrips` creates the strip on takeoff roll (`TakeoffPhase` / `HelicopterTakeoffPhase`) and places it directly in the bay whose name matches the student's position display name (e.g. "Friant" bay for FAT_F_APP). Mimics the tower's "rolling call" handoff. |
| Center / unknown | `_CTR`, other | Departure printer queue (current default). |

Tower routing uses `ArtccConfigService.FindFirstOwnBayWithNamePrefix`;
approach routing uses `FindPositionByCallsign` + `FindFirstOwnBayWithNamePrefix`
together. Both filter to **own** (non-external) bays so auto-routed
strips always land in the student's own facility, never in a linked
external bay.

## Server state model

Strip state lives on the per-room `TrainingRoom.StripState` property,
typed as `FlightStripState` (`src/Yaat.Server/Simulation/FlightStripState.cs`):

```csharp
public sealed class FlightStripState
{
    // Every strip in the room, keyed by strip id.
    public ConcurrentDictionary<string, StripItemRecord> Items { get; } = new();

    // Bay -> rack -> rows of strip ids. rows[0] is the top of the rack.
    public ConcurrentDictionary<string, Dictionary<string, List<string>[]>> Bays { get; } = new();

    public ConcurrentQueue<string> PrinterQueue { get; } = new();
    public int NextBlankId { get; set; } = 1;
}

public record StripItemRecord(
    string Id,                 // "STRIP_{callsign}" or "HSTRIP_{guid}"
    string? AircraftId,        // null for half-strips without an owning aircraft
    int Type,                  // StripItemType value
    bool IsOffset,
    string[] FieldValues,      // free-text lines (half-strip) or annotation fields (full strip)
    string FacilityId,         // bay's owning facility (ATCT for tower, not parent TRACON)
    string BayId,              // vNAS bay ULID
    int Rack,
    int Index
);
```

`Items` is the flat lookup (`id → record`). `Bays` stores the rack
layout: `Bays[bayId][rackKey]` is an array of row lists, where each row
is a `List<string>` of strip ids. Today we only ever populate
`Bays[bayId][rackKey] = [new List<string>()]` — one row per rack — and
prepend new strips to row 0. If/when multi-row racks are supported by
CRC, this layout already permits it.

### `StripBayConfig`

Defined in the vNAS ARTCC config (`src/Yaat.Sim/Data/Vnas/ArtccConfig.cs`).
A facility's `flightStripsConfiguration` has:

- `stripBays: List<StripBayConfig>` — the bays this facility owns.
  Each has `id` (ULID), `name` (display, may contain spaces), and
  `numberOfRacks` (default 3).
- `externalBays: List<ExternalStripBayConfig>` — `(facilityId, bayId)`
  pairs linking bays from other facilities so they appear in this
  facility's strip bay menu. Typically used to bridge an ATCT with its
  parent TRACON.

## Bay resolution

### Gotcha: tower positions' `TrackOwner.FacilityId` is the parent TRACON

`ArtccConfigService.ResolvePosition` returns a `TrackOwner` whose
`FacilityId` is the **STARS facility** that owns the position's TCP.
For tower positions (OAK_TWR, SFO_TWR, etc.) STARS TCPs live on the
parent TRACON's `StarsConfiguration`, so the resolved `FacilityId` is
the TRACON — not the tower's own ATCT facility. That's correct for
handoff routing (which is how the field is mostly used elsewhere) but
**wrong for strip bay lookups**: controllers at a tower position see
their tower's flight strip bays, not the TRACON's.

The fix lives on the bay-resolver side, not the track-owner side.
Use:

```csharp
ArtccConfigService.GetAccessibleStripBay(string artccId, string positionCallsign, string bayName)
```

This walks the facility tree looking for a facility that contains a
position whose callsign matches (e.g. "OAK_TWR" lives inside the OAK
ATCT facility). That facility's `stripBays` plus its linked
`externalBays` are the bays visible from that position. Matching is
case- and whitespace-insensitive so commands can refer to `Ground 1`
as `Ground1`. See `GetAllAccessibleStripBays` for the bare list,
and `GetStripBay(artccId, facilityId, bayName)` for the legacy
facility-id lookup used by `HandleStripPush` for non-tower callers.

Whitespace-insensitive matching applies to both APIs; the in-facility
exact match is tried first, then a normalized fallback.

## Commands

Defined in `src/Yaat.Sim/Commands/`:

| Canonical type | Primary alias | Aliases | Record |
|----------------|---------------|---------|--------|
| `StripPush` | `STRIP` | — | `StripPushCommand(BayName)` |
| `StripScan` | `SCAN` | — | `StripScanCommand(Tokens)` |
| `Annotate` | `AN` | `ANNOTATE`, `BOX` | `StripAnnotateCommand(Box, Text)` |
| `HalfStripCreate` | `HSC` | `HALFSTRIPCREATE` | `HalfStripCreateCommand(BayName, Rack, Lines)` |
| `HalfStripAmend` | `HSA` | `HALFSTRIPAMEND` | `HalfStripAmendCommand(BayName, Rack, Tokens)` |
| `HalfStripDelete` | `HSD` | `HALFSTRIPDEL` | `HalfStripDeleteCommand(BayName, Rack, Tokens)` |
| `StripDelete` | `STRIPD` | — | `StripDeleteCommand(Callsign)` |
| `StripOffset` | `STRIPO` | — | `StripOffsetCommand(Callsign)` |
| `HalfStripMove` | `HSM` | — | `HalfStripMoveCommand(Tokens)` |
| `HalfStripOffset` | `HSO` | — | `HalfStripOffsetCommand(Tokens)` |
| `HalfStripSlide` | `HSS` | — | `HalfStripSlideCommand(Tokens)` |
| `SeparatorCreate` | `SEP` | — | `SeparatorCreateCommand(Type, BayName, Rack, Index, Label)` |
| `SeparatorDelete` | `SEPD` | — | `SeparatorDeleteCommand(BayName, LabelOrPosition)` |
| `BlankCreate` | `BLANK` | — | `BlankCreateCommand(BayName, Rack, Index)` |
| `BlankDelete` | `BLANKD` | — | `BlankDeleteCommand(BayName)` |

All strip commands are marked **phase-transparent** in `CommandDescriber.IsPhaseTransparent`
— they never interact with flight physics — and the parser routes them
through `TrackEngine.IsStripCommand`, which is checked in
`RoomEngine.SendCommandAsync` *before* the dispatcher so they bypass
aircraft phase gating and go straight to `HandleStripCmd`.

### Full strip: `STRIP`, `AN`, `STRIPD`, `STRIPO`

All four are aircraft-scoped (require a selected or prefix callsign):

- `STRIP {bay}[/{rack}[/{index}]]` pushes or reassigns a full flight
  strip keyed by `STRIP_{callsign}` to the named bay. Rack and index
  are **1-based** on the wire and converted to 0-based internally —
  `rack 1` in the spec means `Racks[0]` in the code. Both default to
  the first rack / first slot when omitted. The record is type
  `DepartureStrip (0)`. The slash-compound form removes the
  greedy-bay-match ambiguity that applied to the older space-separated
  syntax.
- `AN {box} [text]` writes (or clears) annotation text into one of
  boxes 1–9 on the aircraft's strip. Field index is `box + 9`. Accepts
  `AN 10`–`AN 18` as aliases for `AN 1`–`AN 9`.
- `STRIPD` deletes the full strip keyed by `STRIP_{callsign}`.
- `STRIPO` toggles the offset flag on the full strip.

`HandleStripPush` resolves the bay using `GetAccessibleStripBay(artccId, positionCallsign, bayName)`,
closing the tower-vs-TRACON facility gap. Tower students can now `STRIP`
against all bays visible to their tower position.

### `SCAN` — copy a strip into an external facility's bay

`SCAN {bay}[/{rack}[/{index}]]` copies the aircraft's full strip into an
external facility's bay while leaving the original strip in place — the
real-paper-strip "scan to receiving controller" workflow. The destination
**must** be marked `IsExternal=true` on the resolved `AccessibleBay`;
internal-bay SCAN errors out (use `STRIP` for in-facility moves).

Aircraft-scoped: requires a selected callsign and an existing
`STRIP_{callsign}` record. The copy is a brand-new strip with id
`STRIP_{callsign}_{shortGuid}` — keeping the `STRIP_` prefix means CRC
vStrips renders it as a regular departure/arrival, and the GUID suffix
prevents collision with the canonical `STRIP_{callsign}` record so
multiple scans to the same bay stack as separate copies. `FieldValues`
is deep-copied at scan time; subsequent `AN` / `STRIPO` on the originator
do **not** propagate to the copy (and vice-versa — each facility owns its
working copy after a scan).

The destination-facility check from `HandleStripPush` carries over:
if no connected CRC client staffs a position in the receiving facility,
the result message includes a "no controller connected" warning so the
sending controller knows the coordination preview has no live receiver.

**Scope limits and known gaps:**

- No cascade: deleting the originator (`STRIPD` or aircraft removed) does
  **not** delete its scanned copies. Each copy is independent after the
  scan; the receiving CRC vStrips can drop it via its own
  `DeleteStripItem` path. There is no canonical command that addresses a
  scanned copy directly (`STRIPD` is callsign-keyed and would target the
  original).
- Half-strip scan is out of scope; spec full strips first.
- The Yaat.Client embedded Strips tab hides "Offset" / "Push to" /
  "Push all in rack to" / "Delete" on scanned copies (id starts with
  `STRIP_` but doesn't equal `STRIP_{callsign}`) — those items dispatch
  callsign-keyed canonicals which would hit the **originator's** strip.
  See `VStripsView.BuildStripContextMenu`.

### Half-strips: `HSC` / `HSA` / `HSD` / `HSM` / `HSO` / `HSS`

#### Dual-mode operation

Every half-strip verb works in **two modes** based on whether an
aircraft is selected at send time:

- **Global** (empty `callsign` at `SendCommand`): user provides every
  line of the half-strip explicitly. Used for freeform notes, VFR
  coordination without a flight plan, etc.
- **Aircraft-scoped** (non-empty `callsign`): the callsign is
  automatically used as line 1 / the lookup key. The user only
  provides lines 2–N.

The client routes this in `Yaat.Client/ViewModels/MainViewModel.cs`:
the normal command path requires a target aircraft, but when the
target is null *and* the compound's first canonical verb is `HSC`,
`HSA`, or `HSD` (`IsHalfStripVerb`), the client falls through and
sends the canonical with an empty callsign. The server then sees
`callsign == ""` and takes the global branch.

#### Line encoding

Lines are separated by a literal backslash `\` in both the user input
and the canonical form. YAAT's compound parser doesn't treat `\` as
anything special, so it flows through `CommandSchemeParser` untouched.
The cap is **6 lines total** (enforced at both parse and handler).

> Because YAAT's compound command parser splits on `,` and `;`, a
> half-strip line cannot contain those characters. Space is fine.

#### Bay / rack argument

Bay is **required** for `HSC` — you have to tell the server where to
put the new strip. It's **optional** for `HSA` / `HSD`: without a bay
they auto-search across every accessible strip bay for the position.

Rack is always optional; it defaults to the first rack. Syntax is
`{bay}[/{rack}]` with rack as a **1-based** integer, so
`HSC Ground1/2 foo` means "bay Ground 1, rack 2 (internal index 1),
line 1 = foo". The handler validates the converted 0-based rack
against `bayConfig.NumberOfRacks`.

#### Bay vs. key disambiguation rule (HSA / HSD only)

The parser can't tell at parse time whether the first whitespace-
separated token is a bay name or part of the lookup key. The rule:

> **The first whitespace-separated token is treated as a bay specifier
> if and only if (a) it contains no `\` AND (b) there is at least one
> more whitespace-separated token after it.**

Examples:

| Input | Bay? | Tokens |
|-------|------|--------|
| `HSA` | — | `[]` |
| `HSA key` | — | `["key"]` (single token → not a bay) |
| `HSA key\new1\new2` | — | `["key","new1","new2"]` (first token has `\`) |
| `HSA Ground key\new1` | `Ground` | `["key","new1"]` |
| `HSA Ground/2 key\new1` | `Ground` rack 2 | `["key","new1"]` |
| `HSD` | — | `[]` |
| `HSD key` | — | `["key"]` |
| `HSD Ground key` | `Ground` | `["key"]` |

Consequence: a single-token global `HSD Ground` is interpreted as
"delete the half-strip whose first line is `Ground`", not as
"aircraft-scoped delete in bay `Ground`". Aircraft-scoped delete with
no bay is always just `HSD`.

Parsing for `HSA` is capped at 7 tokens total (1 lookup key + 6 new
lines). `HSD` is capped at 1 token.

**`HSTRIP_<id>` strip-id form (HSA / HSD / HSO / HSS / HSM):** when the
first token starts with `HSTRIP_`, the parser never treats it as a bay
spec — it's always the lookup key. The server's `FindHalfStripMatches`
correspondingly switches from first-line text matching to `Id` matching.
This is required because empty half-strips (created via `HSC bay/rack`
with no body) have no first-line text, so `StripItemViewModel.LookupKey`
falls back to the strip id and the embedded UI emits commands like
`HSD HSTRIP_<guid>`. Mirrors `SEP_` / `BLANK_` id-prefix handling on
`SEPD` / `SEPE` / `SEPM` / `BLANKD`.

#### Server dispatch

`HandleHalfStripCreate`:

1. Resolve the bay via `GetAccessibleStripBay(artccId, studentCallsign, cmd.BayName)`.
   Errors if the bay isn't in the position's accessible set.
2. Validate `rack < bayConfig.NumberOfRacks`.
3. Compose the `FieldValues` array:
   - Global mode: `userLines` verbatim.
   - Aircraft-scoped: `[callsign, …userLines]`.
4. Reject if the result would be empty (global, no lines) or longer
   than 6.
5. Generate id `HSTRIP_{Guid.NewGuid():N}` and build a `StripItemRecord`
   with `Type = HalfStripLeft (6)` and `FacilityId` set to the **bay's
   owning facility** (from the resolver), not the scenario's
   `StudentPosition.FacilityId`. This is the tower-vs-TRACON fix —
   the strip ends up scoped to the ATCT where the bay lives.
6. Insert into `Items`, prepend into `Bays[bayId]` via
   `PrependStripToBay`, and broadcast.

`HandleHalfStripAmend`:

1. `ResolveOptionalBay(cmd.BayName)` — if the user gave a bay, resolve
   it via `GetAccessibleStripBay` (scopes the search and controls the
   error message); otherwise the scope is room-wide.
2. Branch on callsign:
   - **Global**: require `Tokens.Count >= 2` (key + at least one new
     line). `lookupKey = Tokens[0]`, `newLines = Tokens[1..]`.
   - **Aircraft-scoped**: `lookupKey = callsign`, `newLines = [callsign, ...Tokens]`.
     Empty `Tokens` is allowed; it clears lines 2+.
3. `FindHalfStripMatches` enumerates `StripState.Items`, filters by
   type in `{HalfStripLeft, HalfStripRight}`, first-line case-insensitive
   equal to the lookup key, and optionally by bay and rack. The result:
   - 0 matches → `"No half-strip matching '{key}'{scopeSuffix}"`.
   - &gt;1 matches → `"Multiple half-strips match '{key}' — specify bay: bay1/rack, bay2/rack, …"`.
4. On a unique match, replace the record's `FieldValues` and broadcast
   the single updated item.

`HandleHalfStripDelete`:

1. Same bay resolution.
2. Decide the lookup key:
   - **Global**: require exactly 1 token; it becomes the key.
   - **Aircraft-scoped**: `cmd.Tokens.Count` must be 0 (user wrote
     `HSD`) or 1 with that token becoming the explicit key; otherwise
     default to the callsign.
3. Same find-exactly-one logic as amend.
4. On a unique match, `StripState.Items.TryRemove` and walk
   `StripState.Bays[existing.BayId]` to remove the id from its rack
   list. Broadcasts the full `FlightStripsStateDto` (not just the
   item) because the CRC topic is additive — deletions require the
   full-state rebroadcast, the same pattern used by
   `CrcClientState.Strips.cs::HandleDeleteStripItem`.

All three are also wired into `HandleStripReplay` so recording tapes
that contain half-strip commands replay correctly.

#### `HSM` (half-strip move)

Moves a half-strip to a new bay/rack/index. Works in global and
aircraft-scoped modes:

- **Global**: `HSM [src-bay[/src-rack]] key dest-bay[/rack[/index]]`
  where the source half-strip is identified by its first line (lookup
  key). The dest-spec is slash-compound 1-based like every other
  vStrips verb.
- **Aircraft-scoped**: `HSM dest-bay[/rack[/index]]` moves the
  half-strip keyed by the aircraft callsign.

Bay names can be multi-word (e.g. `Local 1`). The parser emits raw
tokens to `HandleHalfStripMoveAsync`, which walks them backward and
resolves the destination greedily via `StripMutations.ResolveStripDest`
against the bay registry. The same greedy match runs forward across
the leading tokens to peel off an optional source-bay scope. Mirrors
the STRIP token-list pattern.

#### `HSO` (half-strip offset)

Toggles the `IsOffset` flag on a half-strip. Dual-mode:

- **Global**: `HSO [bay[/rack]] key` identifies the half-strip by key.
- **Aircraft-scoped**: `HSO [bay[/rack]]` toggles the callsign's strip.

#### `HSS` (half-strip slide)

Toggles a half-strip between `HalfStripLeft` and `HalfStripRight`.
Dual-mode:

- **Global**: `HSS [bay[/rack]] key` identifies the half-strip by key.
- **Aircraft-scoped**: `HSS [bay[/rack]]` toggles the callsign's strip.

#### `SEP` / `SEPE` / `SEPD` (separators)

Create, edit, and delete rack separators. All use the same
slash-compound 1-based dest-spec as the rest of the vStrips verbs:

- `SEP H|W|R|G bay[/rack[/index]] [label]` creates a separator of type
  Handwritten (H), White (W), Red (R), or Green (G). Label is optional
  freeform text (may contain spaces). Defaults to the first rack and
  first slot.
- `SEPE bay/rack/index new-label…` rewrites the separator at the given
  1-based slot in place — atomic, preserves the strip id. `new-label`
  may span multiple space-separated tokens.
- `SEPD bay[/rack] label-or-position` deletes a separator by its label
  (case-insensitive) or by a 1-based numeric position.

#### `BLANK` / `BLANKD` (blanks)

Create and delete blank strips:

- `BLANK [bay[/rack[/index]]]` creates a blank full-size strip. Without
  arguments, adds the blank to the printer queue (not a specific bay).
- `BLANKD bay[/rack]` deletes one blank from the specified bay/rack
  (blanks are fungible).

## CRC protocol integration

The server ships strip state to CRC clients on the **FlightStrips**
topic. Two payload kinds:

- **Incremental item update** — `ReceiveStripItems` with
  `List<StripItemDto>`. Used for creates and mutations where a single
  item changes. Implemented in `BroadcastStripItemsAsync` on
  `RoomEngine.cs` (instructor-driven) and `CrcClientState.Strips.cs`
  (CRC-driven create/update).
- **Full-state snapshot** — `ReceiveFlightStripsState` with a
  `FlightStripsStateDto`. Used when the rack layout changes or items
  are deleted, because the topic is additive on the CRC side and
  deletes have to be expressed as "here is the complete set". Also
  served in response to `RequestFullFlightStripsState`.

Both broadcasts are routed through `_crcBroadcast.BroadcastToTopicSubscribersAsync`
with topic `"FlightStrips"` and no sub-id.

CRC can also drive strips itself — the hub partial
`CrcClientState.Strips.cs` handles `CreateStripItem`, `UpdateStripItem`,
`DeleteStripItem`, `MoveStripItem`, `RequestFullFlightStripsState`,
`RequestFlightStrip`, and `RequestBlankStrip`. These are used when a
controller moves a strip in vStrips; the server acknowledges, mutates
state, and rebroadcasts.

## CRC → canonical translation

When CRC sends a strip mutation (e.g., `UpdateStripItem`), the handler in
`CrcClientState.Strips.cs` parses the MessagePack payload, converts vNAS
bay ULIDs to bay names via `ArtccConfigService.GetAccessibleStripBayById`,
builds a canonical command string via `StripCommandTranslator`, and
dispatches it through `RoomEngine.RecordAndDispatchStripAsync` with
`initials="CRC"`. The wire-format wrapper DTOs match vNAS
`messaging/Commands/` enum definitions for compatibility across systems.

## Yaat.Client strip view (continued)

Tests for strips run on the server side (`tests/Yaat.Server.Tests/HalfStripCommandTests.cs`,
`tests/Yaat.Sim.Tests/HalfStripCommandParserTests.cs`, `StripCommandParserTests.cs`)
and in the client unit tests for `VStripsViewModel`. The architecture
delegates all state management to the server and broadcasts — both apps
(embedded and standalone) consume identical payloads.

## vStrips clone work (completed)

All major vStrips features implemented in YAAT:

- **`STRIP` bay resolution** now uses `GetAccessibleStripBay`, closing
  the tower-vs-TRACON facility gap. Tower students can use `STRIP`
  against all accessible bays.
- **`HalfStripRight`** produced by `HSS` (half-strip slide toggle).
- **Separators** + **blanks** have full command surface via `SEP`, `SEPD`,
  `BLANK`, `BLANKD`.
- **Half-strip move/offset/slide** via `HSM`, `HSO`, `HSS`.
- **Full strip delete/offset** via `STRIPD`, `STRIPO`.
- **Auto-departure/auto-arrival** strip printing configured by scenario.
  Arrivals auto-print when within 20 minutes of destination
  (`StripMutations.ArrivalAutoPrintMinutes = 20.0`).
- **CRC → canonical translation** in `CrcClientState.Strips.cs` parses
  MessagePack invocations, converts bay IDs to names via
  `ArtccConfigService.GetAccessibleStripBayById`, builds canonical
  command strings via `StripCommandTranslator`, and dispatches through
  `RoomEngine.RecordAndDispatchStripAsync`. Wire-format wrapper DTOs
  match vNAS messaging/Commands/ enum layout.

## Yaat.Client strip view

The **embedded Strips tab** in Yaat.Client subscribes to
`FlightStripsStateChanged` and `StripItemsChanged` broadcasts,
reconciles via `ReconcileFullState`/`ReconcileItems`, and emits every
user action as a canonical command through `_sendCommand`. Supports
drag/drop, keyboard shortcuts, and offset/offset-reverse annotation.

A **standalone Yaat.VStrips app** ships as a ~109 MB binary, cloned from
the shared `src/Yaat.Sim` core with a thin Avalonia UI. It uses
`Yaat.Client.Core` (no LM-Kit dependency), connects via `VStripsViewModel`
to the same `FlightStrips` topic as the main client, and renders the
identical `StripItemDto` payloads.
