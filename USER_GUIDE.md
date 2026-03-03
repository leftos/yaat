# YAAT User Guide

YAAT (Yet Another ATC Trainer) is an instructor/RPO desktop client for air traffic control training. It works alongside CRC (the VATSIM radar client) — you control simulated aircraft in YAAT while viewing them on CRC's radar display.

## Getting Started

### Prerequisites

- .NET 8 SDK
- A running [yaat-server](https://github.com/leftos/yaat-server) instance (default: `http://localhost:5000`)
- CRC (optional, for radar display)

### Launch

```bash
dotnet run --project src/Yaat.Client
```

### Configuration

Before connecting, open **File > Settings** and configure:

- **Connection** tab: Server URL (default `http://localhost:5000`)
- **Identity** tab: VATSIM CID, 2-letter initials (required), and ARTCC ID

Initials and ARTCC ID are required — you cannot connect without them.

### Connecting

1. Configure your identity in **File > Settings** (required)
2. **File > Connect** (or **Disconnect** to close the connection)
3. After connecting, the room list appears — create or join a room

### Loading a Scenario

1. **Scenario > Load Scenario...** — opens a file picker for ATCTrainer-format JSON scenarios
2. Aircraft spawn at their configured starting positions
3. The window title shows the room name and scenario name
4. To switch scenarios, load a new one — a confirmation dialog appears if one is already active

### Unloading a Scenario

**Scenario > Unload Scenario** removes all aircraft from the current scenario. A confirmation dialog appears if multiple clients are connected.

### Loading a Weather Profile

1. **Weather > Load Weather...** — opens a file picker for ATCTrainer-format weather JSON files
2. Select a profile from the list; each entry shows the name and wind layer count
3. Weather is room-level and persists across scenario loads/unloads

The active weather name is shown in the terminal when weather is loaded or cleared.

**Speed note:** Speed commands (`SPD`, `CM`, `DM`) assign IAS (indicated airspeed). Aircraft ground speed — what appears on the radar scope and in the aircraft grid — is derived from IAS + wind at altitude. Expect aircraft flying into a headwind to show a lower ground speed than their assigned IAS, and a higher ground speed when the wind is from behind. Aircraft on the ground are unaffected by wind.

**Clear weather:** **Weather > Clear Weather** removes the active weather profile. All aircraft return to IAS = GS behavior.

Weather profiles use the same JSON format as ATCTrainer standalone weather files.

## Aircraft List

The main grid shows all aircraft in your scenario, grouped into **Active** and **Delayed** sections. Click the group header row to collapse or expand each section. Use **View > Reset Aircraft List Layout** to restore default column order, widths, and sorting.

| Column | Description |
|--------|-------------|
| Callsign | Aircraft callsign (e.g., UAL123) |
| Status | Spawn status (Active, Delayed, etc.) |
| Type | Aircraft type code (e.g., B738/L) |
| Rules | Flight rules (IFR / VFR) |
| Dep / Dest | Departure and destination airports |
| Squawk | Assigned transponder code |
| Hdg | Current heading |
| Alt | Current altitude (ft) |
| Spd | Current ground speed (kts) |
| VS | Vertical speed (fpm) |
| Owner | Track owner (sector code, e.g., "2B", or callsign) |
| HO | Pending handoff target (sector code or callsign) |
| SP | Scratchpad text |
| TA | Temporary altitude assignment |
| Phase | Current phase name (e.g., Downwind, FinalApproach, TakeoffRoll) |
| Rwy | Assigned runway |
| AHdg / AAlt / ASpd | Assigned targets — AHdg shows the next fix name when navigating, or heading when under vectors |
| Dist | Distance in NM from the reference fix (see below) |
| Pending Cmds | Queued command blocks not yet executed (from compound commands) |

Click an aircraft row to select it. Press **Esc** to deselect. Click a column header to sort by that column; click again to reverse the sort direction.

Drag column headers to rearrange the column order. Column order and sort state are remembered across sessions.

### Aircraft Detail Panel

Selecting an aircraft row expands a detail panel below it showing additional state not visible in the grid columns:

| Section | Shown when | Content |
|---------|------------|---------|
| Phases | Aircraft is tower-managed | Phase sequence with active phase in brackets, e.g. `[Base] > FinalApproach > Landing` |
| Pattern | Aircraft is in the pattern | Traffic direction (Left/Right traffic) |
| Clearance | A clearance has been issued | Clearance type and runway, e.g. "Cleared to land Rwy 28L" |
| Route | Aircraft has a navigation route | Remaining waypoints in the route |
| Cruise | Filed cruise altitude exists | Filed cruise altitude and speed |
| Pending | Queued command blocks exist | Full pending commands text (same as grid column, with more space) |

Sections with no data are hidden. Selecting a different row collapses the previous panel and expands the new one.

### Distance Column

The **Dist** column shows each aircraft's distance in nautical miles from a reference fix. When a scenario is loaded, the reference fix defaults to the scenario's primary airport.

To change the reference fix, **right-click** the "Dist" column header. A flyout appears where you can type a fix name or FRD (fix-radial-distance) string. Autocomplete suggestions appear as you type. Press **Enter** or click a suggestion to apply, **Escape** to cancel.

Delayed aircraft (not yet spawned) show a blank distance.

## Commands

Type commands in the command bar at the bottom and press **Enter**.

### Selecting an Aircraft

Type the callsign (or any partial match that uniquely identifies one aircraft) before the command:

```
UAL123 FH 270
```

If an aircraft is already selected (clicked in the grid), you can omit the callsign:

```
FH 270
```

### Command Reference

YAAT uses a unified command scheme that accepts aliases from both ATCTrainer and VICE. Commands are space-separated by default (e.g., `FH 270`), but numeric arguments can be written without a space when unambiguous (e.g., `FH270`, `H270`, `CM240`).

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Fly heading | `FH 270` | `H` | `FH270`, `H270` |
| Turn left | `TL 180` | `L` | `TL180`, `L180` |
| Turn right | `TR 090` | `R` | `TR090`, `R090` |
| Relative left | `LT 30` | `T30L` | — |
| Relative right | `RT 30` | `T30R` | — |
| Fly present heading | `FPH` | `FCH`, `H` | — |
| Climb and maintain | `CM 240` | `C` | `CM240`, `C240` |
| Descend and maintain | `DM 050` | `D` | `DM050`, `D050` |
| Speed | `SPD 250` | `S`, `SLOW`, `SL`, `SPEED` | `SPD250`, `S250` |
| Squawk | `SQ 4521` | `SQUAWK` | `SQ4521` |
| Squawk (reset) | `SQ` | — | — |
| Squawk VFR | `SQVFR` | `SQV` | — |
| Squawk normal | `SQNORM` | `SN`, `SQA`, `SQON` | — |
| Squawk standby | `SQSBY` | `SS`, `SQS` | — |
| Ident | `IDENT` | `ID`, `SQI`, `SQID` | — |
| Random squawk | `RANDSQ` | — | — |
| Direct to fix | `DCT SUNOL` | — | — |
| Pushback | `PUSH` | — | — |
| Taxi | `TAXI S T U` | — | — |
| Hold position | `HOLD` | `HP` | — |
| Resume taxi | `RES` | `RESUME` | — |
| Cross runway | `CROSS 28L` | — | — |
| Hold short | `HS B` | — | — |
| Follow | `FOLLOW SWA123` | `FOL` | — |
| Cleared approach | `CAPP ILS28R` | — | — |
| Join approach | `JAPP ILS28R` | — | — |
| Straight-in apch | `CAPPSI ILS28R` | `JAPPSI` | — |
| Forced approach | `CAPPF ILS28R` | `JAPPF` | — |
| Pos/Turn/Alt/Clr | `PTAC 280 025 ILS30` | — | — |
| Join final | `JFAC ILS28R` | — | — |
| Join arrival | `JARR OAK.SALI2` | — | — |
| Join radial out | `JRADO SJC 150` | — | — |
| Join radial in | `JRADI SJC 150` | — | — |
| Cross fix alt | `CFIX A034` | — | — |
| Descend via | `DVIA` | — | — |
| Climb via | `CVIA` | — | — |
| Depart heading | `DEPART 270` | — | — |
| Holding pattern | `HOLD SUNOL R 180 1M` | — | — |
| List approaches | `APPS` | `APPS OAK` | — |
| Track | `TRACK` | — | — |
| Drop | `DROP` | — | — |
| Handoff | `HO 3Y` | — | `HO3Y` |
| Accept | `ACCEPT` | `A` | — |
| Cancel | `CANCEL` | — | — |
| Accept all | `ACCEPTALL` | — | — |
| Handoff all | `HOALL 3Y` | — | — |
| Pointout | `PO 3Y` | — | — |
| Acknowledge | `OK` | — | — |
| Annotate | `ANNOTATE` | `AN`, `BOX` | — |
| Scratchpad | `SCRATCHPAD TEST` | `SP` | — |
| Temp altitude | `TEMPALT 120` | `TA`, `TEMP`, `QQ` | — |
| Cruise | `CRUISE 240` | `QZ` | — |
| On-handoff | `ONHO` | `ONH` | — |
| Freq change | `FC` | — | — |
| Contact TCP | `CT 3Y` | — | — |
| Contact tower | `TO` | — | — |
| Active position | `AS 2B` | — | — |
| Delete aircraft | `DEL` | `X` | — |

Aliases are fully editable in **Settings > Commands**.

### Altitude Arguments

Altitude arguments (used by CM, DM, LV, and GA) accept three formats:

| Format | Example | Result |
|--------|---------|--------|
| Hundreds (1-3 digits) | `050` | 5,000 ft |
| Absolute (4+ digits) | `5000` | 5,000 ft |
| AGL (airport`+`altitude) | `KOAK+010` | 1,000 ft above KOAK field elevation |

The hundreds-vs-absolute rule: values under 1,000 are multiplied by 100; values 1,000+ are used as-is. This applies to both numeric and AGL formats — `KOAK+010` means 1,000 ft AGL, `KOAK+1500` means 1,500 ft AGL. The airport code can be FAA (e.g., `OAK`) or ICAO (e.g., `KOAK`), followed by `+` and the AGL value.

### Command Chaining

Commands can be combined using `,` (parallel) and `;` (sequential):

- **Parallel (`,`)** — execute simultaneously:
  ```
  CM 050, FH 090
  ```
  Climb to 5,000 ft **and** turn to heading 090 at the same time.

- **Sequential (`;`)** — execute in order, waiting for the previous block to complete:
  ```
  CM 050, FH 090; FH 180
  ```
  Climb and turn simultaneously. Once **both** complete, turn to heading 180.

- **Combined:**
  ```
  CM 014, FH 090; FH 180; DCT MYCOB
  ```
  Climb to 1,400 ft and turn to 090. Then turn to 180. Then proceed direct MYCOB.

### Direct To (DCT)

Navigate to one or more fixes:

```
DCT SUNOL
DCT SUNOL CEDES MYCOB
```

Fixes can also be specified as FRD (Fix-Radial-Distance) strings in the format `{fix}{radial:3}{distance:3}`:

```
DCT JFK090020
```

This means "the point on the 090 radial from JFK at 20 NM." The fix name must be 2+ characters, followed by exactly 3 digits for the radial (degrees) and 3 digits for the distance (NM). FRD works anywhere a fix name is accepted: DCT arguments and AT conditions. AT conditions also support fix-radial (FR) format without distance — see Conditional Blocks below.

If the last fix in the list appears in the aircraft's filed route, the aircraft continues on its filed route from that point.

### Tower Commands

These commands control aircraft during takeoff, landing, and pattern operations. They require the aircraft to be in the phase system (e.g., spawned on a runway or on final approach from a scenario).

| Command | Effect |
|---------|--------|
| `LUAW` | Line up and wait — aircraft holds on runway |
| `CTO` | Cleared for takeoff (default departure) |
| `CTO 050` | Cleared for takeoff, climb and maintain 5,000 ft |
| `CTO MRC` | Cleared for takeoff, right crosswind departure (90° right turn) |
| `CTO MRD` | Cleared for takeoff, right downwind departure (180° right turn) |
| `CTO MR45` | Cleared for takeoff, turn right 45° from runway heading |
| `CTO MLC` / `MLD` / `ML45` | Left-turn equivalents of MRC/MRD/MR{N} |
| `CTO MRH` / `RH` / `MSO` | Cleared for takeoff, fly runway heading |
| `CTO H270` | Cleared for takeoff, fly heading 270 (shortest turn) |
| `CTO RH270` / `RT270` | Cleared for takeoff, turn right heading 270 |
| `CTO LH270` / `LT270` | Cleared for takeoff, turn left heading 270 |
| `CTO OC` | Cleared for takeoff, on course (direct to destination) |
| `CTO DCT SUNOL` | Cleared for takeoff, direct to fix SUNOL |
| `CTO MRT` / `CTOMRT` | Cleared for takeoff, make right traffic (closed pattern) |
| `CTO MLT` / `CTOMLT` | Cleared for takeoff, make left traffic (closed pattern) |
| `CTOC` | Cancel takeoff clearance |
| `CTL` / `FS` | Cleared to land (full stop) |
| `CTL NODEL` | Cleared to land (exempt from auto-delete after landing) |
| `CLC` / `CTLC` | Cancel landing clearance |
| `GA` | Go around (fly runway heading, climb to 2,000 AGL) |
| `GA MRT` | Go around, make right traffic |
| `GA MLT` | Go around, make left traffic |
| `GA 270 50` | Go around, fly heading 270, climb to 5,000 ft |
| `GA RH 50` | Go around, fly runway heading, climb to 5,000 ft |
| `EL` | Exit runway to the left |
| `ER` | Exit runway to the right |
| `EXIT A3` | Exit runway at taxiway A3 |
| `EL NODEL` / `ER NODEL` / `EXIT A3 NODEL` | Exit with auto-delete exemption |

#### CTO Departure Modifiers

All CTO modifiers accept an optional altitude suffix using the same format as CM/DM (see Altitude Arguments above). Examples: `CTO MRC 014` (right crosswind, climb to 1,400 ft), `CTO RH 050` (runway heading, climb to 5,000 ft), `CTO DCT SUNOL 050` (direct SUNOL, climb to 5,000 ft).

| Modifier | Departure type |
|----------|----------------|
| *(none)* | Default departure — VFR: runway heading; IFR: navigates filed route (SID expansion) |
| `MRC` / `MLC` | Right/left crosswind (90° turn from runway heading) |
| `MRD` / `MLD` | Right/left downwind (180° turn) |
| `MR{N}` / `ML{N}` | Right/left turn of N degrees (1-359) from runway heading |
| `MRH` / `MSO` / `RH` | Fly runway heading (straight out) |
| `H{N}` | Fly heading N (shortest turn) |
| `RH{N}` / `RT{N}` | Turn right heading N |
| `LH{N}` / `LT{N}` | Turn left heading N |
| `OC` | On course — navigate direct to destination airport |
| `DCT {fix}` | Direct to named fix |
| `MRT` / `MLT` | Make right/left closed traffic (enter pattern) |

**Altitude resolution** — when no altitude is specified, the target depends on flight rules and departure type:

1. Closed traffic → pattern altitude (1,000 ft AGL for props, 1,500 ft AGL for jets)
2. VFR with filed cruise altitude → cruise altitude
3. VFR without cruise → pattern altitude
4. IFR → self-clear at 1,500 ft AGL

**Navigation** — IFR `CTO` (default departure) automatically expands the filed route including SID waypoints and navigates the aircraft along it. When CIFP data is available, SID legs include published altitude and speed constraints; SID via mode activates automatically so the aircraft follows the published climb profile. Use `CM` to override (disables via mode), or `CVIA` to re-enable it. `CTO DCT {fix}` turns the aircraft toward the fix after liftoff. `CTO OC` navigates toward the destination airport.

The `GA` altitude argument uses the same format as CM/DM (see Altitude Arguments above). `RH` in the heading position means "runway heading." `GA MRT`/`GA MLT` sets the aircraft into pattern mode (make right/left traffic) and climbs to pattern altitude. Auto go-around (no landing clearance by 0.5nm) broadcasts a warning; VFR and pattern traffic re-enter the pattern automatically, while IFR non-pattern traffic flies runway heading at 2,000 AGL.

### Pattern Commands

| Command | Effect |
|---------|--------|
| `ELD` / `ERD` | Enter left/right downwind |
| `ELD 28R` / `ERD 28R` | Enter left/right downwind, assign runway |
| `ELB` / `ERB` | Enter left/right base |
| `ELB 3` / `ERB 5` | Enter left/right base with specified final distance (NM) |
| `ELB 28R` / `ERB 28R` | Enter left/right base, assign runway |
| `ELB 28R 3` | Enter left base, assign runway 28R, 3nm final |
| `EF` | Enter final (straight-in) |
| `EF 28R` | Enter final, assign runway |
| `MLT` / `MRT` | Make left/right traffic (sets pattern direction) |
| `TC` / `TD` / `TB` | Turn crosswind / downwind / base (advance to next leg) |
| `EXT` | Extend current pattern leg (upwind, crosswind, downwind, or base) |

All pattern entry commands (ELB, ERB, ELD, ERD, EF) accept an optional runway argument that assigns or overrides the runway. ELB/ERB also accept an optional distance argument that controls how far from the threshold the final turn occurs (default is the standard pattern geometry).

### Approach Options

| Command | Effect |
|---------|--------|
| `TG` | Touch-and-go (establishes pattern mode if not already) |
| `SG` | Stop-and-go (full stop then re-takeoff, establishes pattern mode) |
| `LA` | Low approach (fly-through without touchdown, establishes pattern mode) |
| `COPT` | Cleared for the option |

TG, SG, and LA set pattern mode — the aircraft will continue doing touch-and-goes after the next approach. Use `MLT`/`MRT` to specify traffic direction, or combine: `TG, MLT`.

### Hold Commands

| Command | Effect |
|---------|--------|
| `HPPL` / `HPPR` | Hold present position, left/right 360° orbits |
| `HPP` | Hold present position (hover, for helicopters) |
| `HFIXL {fix}` / `HFIXR {fix}` | Fly to fix, then left/right orbits |
| `HFIX {fix}` | Fly to fix, then hover |

Any heading, altitude, or speed command clears the hold.

### Approach Control Commands

Approach clearances use FAA CIFP procedure data. Approach IDs can be full CIFP identifiers (e.g., `I28R`) or common shorthand (e.g., `ILS28R`, `RNAV17L`, `LOC30`).

| Command | Effect |
|---------|--------|
| `CAPP ILS28R` | Cleared ILS Runway 28R approach — navigates through the full procedure (IAF → IF → FAF → final) |
| `JAPP ILS28R` | Join ILS 28R approach at nearest IAF/IF ahead of the aircraft |
| `CAPPSI ILS28R` | Cleared straight-in ILS 28R (skips hold-in-lieu of procedure turn) |
| `JAPPSI ILS28R` | Join straight-in ILS 28R (skips hold-in-lieu) |
| `CAPPF ILS28R` | Forced approach clearance (bypasses intercept angle validation) |
| `JAPPF ILS28R` | Forced join (bypasses intercept angle check) |
| `PTAC 280 025 ILS30` | Position/Turn/Altitude/Clearance — turn heading 280, maintain 2,500, cleared ILS 30 |
| `JFAC ILS28R` | Join final approach course (intercept and fly the localizer) |
| `APPS` | List available approaches for the aircraft's destination airport |
| `APPS OAK` | List available approaches at a specific airport |

**Rich CAPP forms** combine approach clearance with navigation:

| Command | Effect |
|---------|--------|
| `CAPP AT SUNOL ILS28R` | Navigate to fix SUNOL, then fly the ILS 28R |
| `CAPP DCT SUNOL ILS28R` | Direct to SUNOL, then fly the ILS 28R |
| `CAPP DCT SUNOL CFIX A034 ILS28R` | Direct to SUNOL, cross at or above 3,400, then ILS 28R |

**CFIX altitude prefixes:** `A034` = at or above 3,400, `B034` = at or below 3,400, `034` = at 3,400.

**Intercept angle validation:** Approach clearances validate the intercept angle per 7110.65 §5-9-2 — max 20° within 2nm of the approach gate, max 30° beyond. Use force variants (`CAPPF`/`JAPPF`) to override.

**Hold-in-lieu:** When a procedure includes a hold-in-lieu of procedure turn (e.g., at an IAF), `JAPP` automatically executes one holding circuit before proceeding. Use `CAPPSI`/`JAPPSI` to skip it.

### Navigation Commands

| Command | Effect |
|---------|--------|
| `DCT SUNOL` | Direct to fix SUNOL |
| `ADCT SUNOL` | Append direct to — adds SUNOL to the end of the current route |
| `JARR OAK.SALI2` | Join STAR: navigate to nearest fix on the SALI2 arrival into OAK (with CIFP altitude/speed constraints when available) |
| `JRADO SJC 150` | Join radial outbound: fly to SJC VOR, then outbound on the 150° radial |
| `JRADI SJC 150` | Join radial inbound: intercept and fly inbound on the 150° radial to SJC |
| `CFIX A034` | Cross next fix at or above 3,400 ft (modifies current target altitude) |
| `DVIA` | Descend via STAR — enables altitude/speed constraint following on active STAR |
| `DVIA 240` | Descend via STAR, except maintain FL240 (altitude floor) |
| `CVIA` | Climb via SID — re-enables altitude/speed constraint following on active SID |
| `CVIA 190` | Climb via SID, except maintain FL190 (altitude ceiling) |
| `DEPART 270` | Depart heading 270 (sets heading after departure) |

**SID/STAR via mode** — When CIFP procedure data is available, SID and STAR fixes carry published altitude and speed restrictions. Via mode controls whether the aircraft follows those restrictions:

| Procedure | Default | Enable via mode | Disable via mode |
|-----------|---------|-----------------|------------------|
| SID (after CTO) | **ON** — aircraft follows published climb restrictions | `CVIA` (re-enable after CM override) | `CM` (any altitude command) |
| STAR (after JARR) | **OFF** — aircraft maintains altitude, follows lateral path only | `DVIA` (enable descent restrictions) | `DM` (any altitude command) |

`CVIA 190` and `DVIA 240` enable via mode with an altitude cap/floor — "climb/descend via, except maintain." `FH`, `DCT`, and heading commands clear the entire procedure (lateral path + via mode). When CIFP data is unavailable, JARR and CTO fall back to NavData fix lists (lateral path only, no constraints).

### Holding Patterns

| Command | Effect |
|---------|--------|
| `HOLD SUNOL R 180 1M` | Hold at SUNOL, right turns, 180° inbound, 1-minute legs |
| `HOLD SUNOL L 090 5` | Hold at SUNOL, left turns, 090° inbound, 5nm legs |
| `HOLD SUNOL R 180` | Hold at SUNOL, right turns, 180° inbound (default 1-minute legs) |

Hold format: `HOLD {fix} {L/R} {inbound_course} {leg_length}`. Leg length ending in `M` is minutes; plain number is nautical miles. Any RPO command (heading, altitude, approach, etc.) exits the hold.

### Ground Commands

| Command | Effect |
|---------|--------|
| `PUSH` | Push back from parking (reverse at ~5 kts) |
| `PUSH 270` | Push back facing heading 270 |
| `TAXI S T U W W1` | Taxi via taxiways S, T, U, W, W1 |
| `TAXI T U W 30` | Taxi via T, U, W to runway 30 |
| `TAXI T U W RWY 30` | Same as above (explicit RWY keyword) |
| `RWY 30 TAXI T U W` | Same as above (RWY-first syntax) |
| `TAXI S T U HS 28L` | Taxi via S, T, U with explicit hold-short at runway 28L |
| `TAXI S T U @B12 NODEL` | Taxi via S, T, U to parking B12 (exempt from auto-delete) |
| `HOLD` / `HP` | Hold position (stop wherever on the ground) |
| `RES` / `RESUME` | Resume taxi after hold |
| `CROSS 28L` | Cross runway 28L (clears hold-short) |
| `CROSS B` | Cross taxiway B (clears hold-short) |
| `HS B` | Hold short at the next intersection with taxiway B |
| `HS 28L` | Hold short at the next runway 28L crossing |
| `FOLLOW SWA123` | Follow another aircraft on the ground |

Aircraft automatically hold short at all runway crossings along the taxi route. Use `CROSS` to clear a hold-short — either while already holding short, or in advance to pre-clear it before the aircraft arrives. `CROSS` works for both runway and taxiway hold-shorts.

`HS` can be issued to a taxiing aircraft to add a hold-short point at the first upcoming intersection with the given taxiway or runway along the remaining route.

Ground aircraft automatically detect and avoid collisions — trailing aircraft slow down or stop to maintain safe separation. Head-on conflicts cause both aircraft to stop.

### Helicopter Commands

Helicopters are detected automatically from the ICAO type designator. They use tighter traffic patterns (500ft AGL), steeper glideslopes (6°), and can take off/land vertically from non-runway positions.

| Command | Effect |
|---------|--------|
| `CTOPP` | Cleared for takeoff, present position — vertical liftoff from ramp, helipad, or parking |
| `ATXI H1` | Air taxi to spot H1 — airborne below 100ft AGL, ~40 KIAS |
| `LAND H1` | Land at named spot H1 (helipad, parking, or ramp position) |
| `LAND H1 NODEL` | Land at H1, exempt from auto-delete |

Helicopters can also use all standard tower commands (`CTO`, `CTL`, `LUAW`, `TG`, `SG`, `GA`) with runway assignments — they hover-taxi onto the runway, hold position, and take off/land like fixed-wing aircraft. This is typical for IFR operations. `CTO` requires a runway; `CTOPP` does not.

**Spawning helicopters:** Use the ADD command with a helicopter type (e.g., `H60`, `EC35`, `R44`). Use `%` prefix for helipad/parking spawn: `ADD V S P %H1 H60`.

### Track Operations

Track operations control aircraft ownership, handoffs, and coordination. These commands use STARS-style TCP codes (e.g., "2B" = subset 2, sector B).

#### Active Position

By default, you operate as the scenario's student position. Use `AS` to act as a different position:

| Command | Effect |
|---------|--------|
| `AS 2B` | Set your active position to TCP 2B (persistent until changed) |
| `N135BS AS 2B HO 3Y` | Act as 2B for this command only (prefix mode) |

Resolution order: per-command `AS` prefix > persistent active position > student position default.

#### Track Commands

| Command | Effect |
|---------|--------|
| `TRACK` | Initiate control — take ownership of the aircraft |
| `DROP` | Terminate control — release ownership |
| `HO 3Y` | Handoff to TCP 3Y |
| `ACCEPT` / `A` | Accept a pending inbound handoff |
| `CANCEL` | Retract a pending outbound handoff |
| `ACCEPTALL` | Accept all pending inbound handoffs (global — no callsign needed) |
| `HOALL 3Y` | Handoff all your aircraft to TCP 3Y (global — no callsign needed) |
| `PO 3Y` | Point out to TCP 3Y |
| `OK` | Acknowledge a pending pointout |
| `ANNOTATE` / `AN` / `BOX` | Toggle annotation flag |
| `SP TEST` / `SCRATCHPAD TEST` | Set scratchpad text |
| `TA 120` / `QQ 120` | Set temporary altitude (in hundreds, e.g., 120 = FL120) |
| `CRUISE 240` / `QZ 240` | Set cruise altitude |
| `ONHO` / `ONH` | Toggle on-handoff status |

| Command | Effect |
|---------|--------|
| `FC` | Approve frequency change |
| `CT 3Y` | Tell pilot to contact TCP 3Y |
| `TO` | Tell pilot to contact tower |

#### Coordination (Rundown List)

Coordination commands manage departure releases between tower and approach controllers. Coordination channels are loaded from the ARTCC config when a scenario is loaded.

| Command | Effect |
|---------|--------|
| `RD [listId]` | Send a departure release (auto-detect list if omitted) |
| `RDH [listId] [text]` | Hold a release without sending; or send a held release |
| `RDR [listId]` | Recall a sent release, or delete an unsent one |
| `RDACK [listId]` | Acknowledge a received release |
| `RDAUTO <listId>` | Toggle auto-acknowledge for a list (global — no callsign needed) |

When `listId` is omitted, the server auto-detects the correct coordination list from the sender/receiver TCP. `RDACK` works without a list ID even when the TCP belongs to multiple lists, as long as there is only one unacknowledged release across all lists.

Example flow using `AS` to role-play both sides:
```
AAL123 AS 1T RD PFAT        — Tower (1T) sends release on list PFAT
AAL123 AS 1F RDACK PFAT     — Approach (1F) acknowledges the release
```

Release lifecycle:
- **Unsent** — created via `RDH`, not yet sent to the receiver
- **Unacknowledged** — sent to receiver, awaiting acknowledgment
- **Acknowledged** — receiver has accepted; expires after 5 minutes (warning at 3 minutes)
- **Recalled** — sender recalled the release; removed after 10 seconds
- Coordination items are automatically removed when an aircraft is tracked (radar acquisition)

#### Auto-Accept

Handoffs to unattended positions (no CRC client logged in) can be automatically accepted after a configurable delay. Enable and configure the delay in **Settings > General > Auto-accept handoffs**. When disabled, handoffs to unattended positions remain pending until manually accepted.

#### Auto-Delete

Scenarios can define an `autoDeleteMode` that automatically removes aircraft after they land or reach parking:
- **On Landing** — aircraft deleted after clearing the runway (at hold-short point)
- **On Parking** — aircraft deleted when they reach a parking spot

Override the scenario setting in **Settings > General > Auto-Delete Aircraft**. Options: "Use Scenario Setting" (default), "Never", "On Landing", "On Parking".

To exempt a specific aircraft from auto-delete, append `NODEL` to `CTL`, `TAXI`, `EL`, `ER`, or `EXIT` commands (e.g., `CTL NODEL`, `TAXI S T U @B12 NODEL`, `EL NODEL`, `EXIT A3 NODEL`). This is useful when repositioning aircraft after landing or parking for reuse.

### Conditional Blocks

Use `LV` (level at altitude) and `AT` (at fix) to trigger blocks on specific conditions instead of waiting for the previous block:

- **LV** — triggers when the aircraft reaches an altitude:
  ```
  LV 050 FH 270
  ```
  When reaching 5,000 ft, turn to heading 270.

- **AT** — triggers when the aircraft reaches a fix, intercepts a radial, or reaches an FRD point:
  ```
  AT SUNOL FH 180
  ```
  When reaching SUNOL (within 0.5 NM), turn to heading 180.

  ```
  AT SUNOL090 FH 270
  ```
  When crossing radial 090 from SUNOL, turn to heading 270. (Fix-Radial format: `{fix}{radial:3}`)

  ```
  AT SUNOL090020 FH 270
  ```
  When reaching 20 NM on the 090 radial from SUNOL (within 1.5 NM), turn to heading 270. (Fix-Radial-Distance format: `{fix}{radial:3}{distance:3}`)

  If an aircraft passes through an FRD point without triggering (misses by more than 1.5 NM but came within 5 NM), a warning message appears in the terminal: `Missed condition at SUNOL R090 D020 (closest: 2.3 NM)`.

- **GIVEWAY** / **BEHIND** — triggers when the named aircraft no longer conflicts (ground only):
  ```
  GIVEWAY SWA5456 TAXI S T U
  ```
  Wait for SWA5456 to pass, then taxi via S, T, U. Works with compound chains:
  ```
  BEHIND DAL423; TAXI S T U W
  ```

These work within compound chains:

```
CM 100; LV 050 FH 270; LV 100 DCT SUNOL
```

Climb to 10,000 ft. At 5,000 ft, turn to heading 270. At 10,000 ft, proceed direct SUNOL.

### Wait Commands

Use `WAIT` and `WAITD` to delay the next command in a `;` sequence by time or distance:

| Command | Effect |
|---------|--------|
| `WAIT 30` | Wait 30 seconds before executing the next block |
| `WAITD 4` | Fly 4 nautical miles before executing the next block |

These commands occupy their own block in a compound sequence and do not change the aircraft's heading, altitude, or speed. They simply delay progression to the next block.

**Examples:**

```
FH 270; WAIT 10; FH 090
```
Turn to heading 270, wait 10 seconds, then turn to heading 090.

```
CM 100; WAIT 5; DM 030
```
Climb to 10,000 ft, wait 5 seconds, then descend to 3,000 ft.

```
WAITD 2; TL 180
```
Fly 2 nm, then turn left to heading 180.

```
LV 050 WAIT 10; FH 090
```
At 5,000 ft, wait 10 seconds, then fly heading 090.

### Delayed Aircraft Commands

These commands target aircraft in the delayed spawn queue (shown with "Delayed" status):

| Command | Effect |
|---------|--------|
| `SPAWN` | Spawn the selected aircraft immediately |
| `DELAY <n>` | Set spawn delay to N seconds from now (accepts M:SS, e.g., `DELAY 2:00`) |

### Add Aircraft (ADD)

Spawn an aircraft on demand without a scenario file. Requires an active scenario.

**Syntax variants:**

| Variant | Syntax | Example |
|---------|--------|---------|
| Airborne | `ADD {rules} {weight} {engine} -{bearing} {dist} {alt}` | `ADD IFR H J -270 15 10000` |
| At fix | `ADD {rules} {weight} {engine} @{fix} {alt}` | `ADD IFR L J @SUNOL 8000` |
| Lined up on runway | `ADD {rules} {weight} {engine} {runway}` | `ADD VFR S P 28R` |
| On final | `ADD {rules} {weight} {engine} {runway} {dist}` | `ADD IFR L J 28R 8` |

**Parameters:**

| Parameter | Values |
|-----------|--------|
| Rules | `I`/`IFR` (instrument) or `V`/`VFR` (visual) |
| Weight | `S` (small/GA), `L` (large), `H` (heavy) |
| Engine | `P` (piston), `T` (turboprop), `J` (jet) |

**Position arguments:**
- **Airborne**: `-{bearing}` is degrees from the primary airport, `{dist}` is distance in NM, `{alt}` is altitude in feet. Aircraft spawns heading toward the airport.
- **At fix**: `@{fix}` is a fix name or FRD (fix-radial-distance, e.g., `SJC090015`), `{alt}` is altitude in feet. Aircraft spawns at the fix heading toward the primary airport.
- **Lined up**: `{runway}` is the runway designator (e.g., `28R`). Aircraft spawns on the runway threshold, ready for takeoff clearance.
- **On final**: `{runway}` plus `{dist}` in NM. Aircraft spawns on final approach at that distance from the runway.

**Optional trailing tokens:**
- Explicit aircraft type: `ADD IFR H J -090 20 15000 B77L`
- Explicit airline prefix: `ADD IFR L J -180 10 5000 *SWA`

**Valid weight/engine combinations:**

| | Piston (P) | Turboprop (T) | Jet (J) |
|---|---|---|---|
| Small (S) | C172, C182, PA28, SR22 | C208, PC12, BE20 | — |
| Large (L) | — | DH8D, AT76, AT72 | B738, A320, E170, E175, CRJ9 |
| Heavy (H) | — | — | B77L, B772, A332, B789, B744, A359 |

IFR aircraft get a random airline callsign (e.g., UAL1234). VFR aircraft get an N-number (e.g., N1234A). Aircraft type is randomly selected from the table unless explicitly specified.

### Global Commands

These commands don't require an aircraft selection:

| Command | Effect |
|---------|--------|
| `PAUSE` | Pause the simulation |
| `UNPAUSE` | Resume the simulation |
| `SIMRATE <n>` | Set simulation speed (1, 2, 4, 8, 16) |
| `ADD ...` | Spawn an aircraft (see above) |
| `SQALL` | Reset all aircraft to their assigned squawk codes |
| `SNALL` | Set all aircraft transponders to mode C (normal) |
| `SSALL` | Set all aircraft transponders to standby |
| `RDAUTO <listId>` | Toggle auto-acknowledge for a coordination list |

The pause/unpause button and sim rate dropdown in the bottom bar also control these.

## Simulation Controls

At the bottom-right of the window:

- **Pause/Resume** button — toggle simulation pause
- **Sim rate dropdown** — speed up the simulation (1x, 2x, 4x, 8x, 16x)

Pause and sim rate are scoped to your scenario — they don't affect other clients' scenarios.

## Views

The main window uses a tabbed layout with three views: **Aircraft List**, **Ground View**, and **Radar View**. The terminal panel sits below the tab area.

### Tabs and Pop-Out

Each view can be popped out into its own window via **View > Pop Out Aircraft List / Ground View / Radar View**. When a view is popped out, its tab disappears from the main window and a separate window opens. Close the pop-out window (or uncheck the menu item) to dock it back as a tab.

All three views can be popped out simultaneously. Pop-out state and window positions are remembered across sessions.

### Aircraft List

The default tab. Shows the aircraft data grid described above.

### Ground View

An interactive airport surface map showing taxiways, runways, and aircraft positions. Useful for tower operations (taxi, hold short, cross runway).

- **Pan**: left-click and drag
- **Zoom**: mouse wheel
- **Select aircraft**: click an aircraft triangle on the map (syncs with the grid selection)

**Right-click context menus:**
- **On a node** (with aircraft selected): up to 4 route options ("Taxi via T U W") computed via K-shortest paths. When only one route exists it appears as a flat item; when multiple exist they nest under a "Taxi here" submenu. Routes that cross runways automatically append crossing commands. Also: "Hold short {rwy}" (hold-short nodes), "Park at {name}" (parking nodes).
- **On an aircraft** — items vary by phase:
  - *At Parking*: "Push back" (default), "Push back, face {taxiway}" (one per connected edge, with heading)
  - *Pushback / Taxiing / Following*: "Hold position"
  - *Taxiing*: "Hold short of..." submenu listing all intersecting runways and taxiways along the current taxi route
  - *Holding Short {rwy}*: "Resume taxi", "Cross {rwy}", "Line up and wait {rwy}", "Cleared for takeoff {rwy}", plus crossing options for other nearby runways
  - *Holding After Exit*: "Resume taxi"
  - *Lined Up And Waiting*: "Cleared for takeoff {rwy}", "Cancel takeoff clearance"
  - *Final Approach*: "Cleared to land", "Touch and go", "Stop and go", "Low approach", "Cleared for the option", "Go around", "Cancel landing clearance"
  - *Landing*: "Exit left", "Exit right"
  - *Takeoff*: "Cancel takeoff clearance"
  - All phases include "Delete" at the bottom
- **On empty space** (with aircraft selected): multi-route taxi options (same as node right-click), or pushback directions if aircraft is at parking

The ground layout loads automatically when a scenario is loaded for an airport with ground data.

### Radar View

A simplified STARS-style radar display showing aircraft targets, video maps, and navigation fixes. Useful for approach/departure operations.

- **Pan**: left-click and drag
- **Zoom**: mouse wheel
- **Select aircraft**: click a target on the display (syncs with the grid selection)

**DCB bar** (top of the radar view):
- **RNG +/-**: increase/decrease display range
- **RR**: toggle range rings
- **FIX**: toggle fix overlay
- Range display shows current range in NM

**Right-click context menus:**
- **On an aircraft**: Heading (fly/present/turn left/turn right), Altitude (common values), Speed, Approach (ILS/RNAV/VIS per runway), Track operations, Delete
- **On the map** (with aircraft selected): "Fly heading" (computed bearing to click point), "Direct to" (nearest fix within 5nm)

Video maps load automatically from the vNAS data API based on your ARTCC ID. Map lines render in green with brightness categories A/B.

## Terminal Panel

The terminal panel sits below the tab area. It shows a scrolling history of all commands and server feedback for your scenario, visible to all connected RPOs.

### Entry Format

Each line shows:
```
HH:MM:SS  AB  UAL123  FH 270
```
- **Timestamp** (gray) — when the entry was received
- **Initials** (blue) — who issued the command (2-letter user initials)
- **Callsign** (gold) — which aircraft was targeted
- **Message** (colored by type):
  - **White** — command echo
  - **Green** — server response/feedback
  - **Gray** — system message (errors, scenario events, chat)
  - **Orange** — SAY (future instructor messages)

### Multi-User Visibility

All RPOs connected to the same scenario see each other's commands in their terminal. This lets you monitor what other controllers are doing.

### Chat Messages

Type a message prefixed with `'`, `/`, or `>` to send a text chat to all RPOs in your scenario:

```
'Switching to RNAV approach
>Ready for next aircraft
```

Chat messages appear as gray system entries with your initials.

### Resizing

Drag the splitter bar between the aircraft grid and terminal panel to resize them.

### Pop Out / Dock

Click the **Pop Out** button in the terminal header to undock the terminal into a separate floating window. The command input bar moves to the terminal window. Click **Dock** (or close the window) to return it to the main window.

### Warnings

Warning messages appear as gray system entries when the simulator detects potential issues:

- **Missed FRD condition**: `Missed condition at SUNOL R090 D020 (closest: 2.3 NM)` — an aircraft passed through an FRD trigger point without getting close enough (within 1.5 NM) but came within 5 NM.
- **Illegal approach intercept**: `Illegal intercept: turned on final 5.2nm from threshold (min 9.0nm) [7110.65 §5-9-1]` — an aircraft was vectored onto the final approach course closer to the runway than the minimum intercept distance derived from the approach gate (FAA 7110.65 §5-9-1). The minimum distance is computed from FAA CIFP data as: approach gate + 2nm, where approach gate = max(FAF distance + 1nm, 5nm). Pattern traffic (base-to-final turns) is exempt.

## Students Panel

CRC clients (students) connect to the server independently using their own VATSIM CID. If a student's CID doesn't match any YAAT client in a room, their CRC session sits in the "lobby" — connected but not receiving any room data.

Open **Room > Students...** to manage CRC clients:

### In Room
Lists CRC clients currently bound to your room. Each entry shows the client's display name (callsign + real name), position ID, and active status. Click **Kick** to remove a client from your room — they return to the lobby and stop receiving room data.

### Lobby
Lists CRC clients not currently in any room. Each entry shows display name, ARTCC ID, and position. Click **Pull** to bring a client into your room — they immediately start receiving your room's aircraft and position data.

### Notes
- CRC clients that connect with a CID matching a YAAT client in a room are automatically bound to that room
- If all YAAT clients leave a room, any CRC clients bound to it are automatically unbound back to the lobby
- The panel updates in real-time as CRC clients connect, disconnect, or change state
- Use **Refresh** to manually re-fetch both lists

## Settings

Open **File > Settings** to configure:

- **Connection** — Server URL for the yaat-server instance
- **Identity** — VATSIM CID, user initials (required before connecting), and ARTCC ID
- **Scenarios** — Auto-accept handoff settings (enable/disable + delay in seconds), and auto-delete aircraft override (Use Scenario Setting / Never / On Landing / On Parking)
- **Commands** — Alias editor for customizing command verbs
- **Macros** — Define reusable command shortcuts (see [Macros](#macros) below)
- **Advanced** — Server admin mode

## Autocomplete

As you type in the command bar, a popup appears with matching suggestions:

- **Command verbs** — matching verbs from your active command scheme with syntax hints (e.g., `FH  Fly Heading {270}`)
- **Callsigns** — aircraft whose callsign matches what you've typed, showing type and route
- **Fix names** (for DCT and AT arguments) — all VNAS navdata fixes (~40k airports, navaids, waypoints) plus custom scenario fixes
- **Macros** (yellow) — when typing `#`, matching macro names with parameter hints (e.g., `#HC $1 $2` or `#FC $hdg $alt`)
- After accepting a callsign, the popup immediately shows all available command verbs

Suggestions are context-aware: after a `;` or `,` separator in compound commands, suggestions reset for the new command. Conditions (`LV`, `AT`) are also suggested.

### Fix Suggestion Priority

When typing a fix argument (after `DCT` or `AT`), suggestions use two tiers:

1. **Route fixes** (teal) — fixes from the selected aircraft's flight plan: departure, destination, filed route fixes, and all fixes from expanded SIDs/STARs. These are fixes "in the FMS" that the pilot has programmed.
2. **Navdata fixes** (white) — all other navdata fixes matching your prefix.

If no aircraft is selected, only navdata fixes are shown. Route fixes always appear first.

## Macros

Macros let you define reusable command shortcuts. A macro maps a `#NAME` to a command expansion, optionally with positional parameters.

### Defining Macros

Open **Settings > Macros** to create, edit, and manage macros. Each macro has:

- **Name** — alphanumeric identifier (e.g., `BAYTOUR`, `HC`). The `#` prefix is added automatically when you type it.
- **Expansion** — the command(s) to expand to. Can include parameter placeholders.

### Parameters

Macros support two parameter styles:

| Style | Expansion | Invocation | Result |
|-------|-----------|------------|--------|
| Positional | `FH $1, CM $2` | `#HC 270 5000` | `FH 270, CM 5000` |
| Named | `FH $hdg, CM $alt` | `#FC 270 5000` | `FH 270, CM 5000` |

Named parameters serve as documentation — the autocomplete popup shows `#FC $hdg $alt` instead of `#FC $1 $2`, making it clear what each argument means. Arguments are always supplied positionally (in the order they first appear in the expansion).

### Usage

Type `#` followed by the macro name in the command bar:

- `#BAYTOUR` → expands to `DCT VPCOL VPCHA VPMID`
- `#HC 270 5000` → expands to `FH 270, CM 5000`
- `#HC 270 5000; DCT SUNOL` → macro + compound: `FH 270, CM 5000; DCT SUNOL`

Macros work anywhere in a compound command (after `;` or `,` separators). The command history records the original macro text, not the expansion.

### Import / Export

- **Export All** / **Export Selected** — save macros to a `.yaat-macros.json` file
- **Import** — load macros from a file, with a selection dialog showing which macros will overwrite existing ones

## Command History

The command bar remembers your last 50 commands. Navigate with Up/Down arrows:

- **Up** — recall the previous command
- **Down** — move forward through history (or restore what you were typing)
- If you type something first, only history entries starting with that text are shown

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| Enter | Send command (dismisses suggestions if open) |
| Tab | Accept the highlighted suggestion (or first if none highlighted) |
| Up | Navigate suggestions or recall older history |
| Down | Navigate suggestions or recall newer history |
| Escape | Dismiss suggestions / deselect aircraft / close dialog |

## Window State

YAAT remembers window size and position for the main window and all pop-out windows (Aircraft List, Ground View, Radar View, Terminal) across sessions. Pop-out state (which views are in separate windows vs. tabbed) is also persisted.
