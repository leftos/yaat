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

### User Initials

Before connecting, open **Settings** and enter your 2-letter initials (e.g., "AB") in the **General** tab. Initials are required — you cannot connect without them. They appear in the terminal panel so all RPOs can see who issued each command.

### Connecting

1. Set your initials in Settings (required)
2. Enter the server URL (default `http://localhost:5000`)
3. Click **Connect**
4. If the server has active scenarios from a previous session, a **rejoin** dialog appears

### Loading a Scenario

1. Click **Browse** and select an ATCTrainer-format JSON scenario file
2. Click **Load Scenario**
3. Aircraft spawn at their configured starting positions
4. The active scenario name and client count appear in the scenario bar
5. To switch scenarios, load a new one — a confirmation dialog appears if one is already active

### Deleting Aircraft

Click **Delete All** to remove all aircraft from the current scenario. A confirmation dialog appears if multiple clients are connected.

## Aircraft List

The main grid shows all aircraft in your scenario:

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

Delayed and deferred aircraft (not yet spawned) show a blank distance.

## Commands

Type commands in the command bar at the bottom and press **Enter**.

### Selecting an Aircraft

Type the callsign (or a unique prefix) before the command:

```
UAL123 FH 270
```

If an aircraft is already selected (clicked in the grid), you can omit the callsign:

```
FH 270
```

### Command Schemes

YAAT supports two command schemes, switchable in Settings:

| Command | ATCTrainer | VICE |
|---------|-----------|------|
| Fly heading | `FH 270` | `H270` |
| Turn left | `TL 180` | `L180` |
| Turn right | `TR 090` | `R090` |
| Relative left | `LT 30` | `T30L` |
| Relative right | `RT 30` | `T30R` |
| Fly present heading | `FPH` | `H` |
| Climb and maintain | `CM 240` | `C240` |
| Descend and maintain | `DM 050` | `D050` |
| Speed | `SPD 250` | `S250` |
| Squawk | `SQ 4521` | `SQ4521` |
| Squawk (reset) | `SQ` | `SQ` |
| Squawk VFR | `SQVFR` | `SQVFR` |
| Squawk normal | `SQNORM` | `SQNORM` |
| Squawk standby | `SQSBY` | `SQSBY` |
| Ident | `IDENT` / `SQI` | `IDENT` / `SQI` |
| Random squawk | `RANDSQ` | `RANDSQ` |
| Direct to fix | `DCT SUNOL` | `DCT SUNOL` |
| Pushback | `PUSH` | `PUSH` |
| Taxi | `TAXI S T U` | `TAXI S T U` |
| Hold position | `HOLD` | `HOLD` |
| Resume taxi | `RES` | `RES` |
| Cross runway | `CROSS 28L` | `CROSS 28L` |
| Follow | `FOLLOW SWA123` | `FOLLOW SWA123` |
| Track | `TRACK` | `TRACK` |
| Drop | `DROP` | `DROP` |
| Handoff | `HO 3Y` | `HO 3Y` |
| Accept | `ACCEPT` / `A` | `ACCEPT` / `A` |
| Cancel | `CANCEL` | `CANCEL` |
| Accept all | `ACCEPTALL` | `ACCEPTALL` |
| Handoff all | `HOALL 3Y` | `HOALL 3Y` |
| Pointout | `PO 3Y` | `PO 3Y` |
| Acknowledge | `OK` | `OK` |
| Annotate | `ANNOTATE` / `AN` | `ANNOTATE` / `AN` |
| Scratchpad | `SP TEST` | `SP TEST` |
| Temp altitude | `TA 120` / `QQ 120` | `TA 120` / `QQ 120` |
| Cruise | `CRUISE 240` / `QZ 240` | `CRUISE 240` / `QZ 240` |
| On-handoff | `ONHO` / `ONH` | `ONHO` / `ONH` |
| Freq change | — | `FC` |
| Contact TCP | — | `CT3Y` |
| Contact tower | — | `TO` |
| Active position | `AS 2B` | `AS 2B` |
| Delete aircraft | `DEL` | `X` |

### Altitude Arguments

Altitude arguments (used by CM, DM, LV, and GA) accept three formats:

| Format | Example | Result |
|--------|---------|--------|
| Hundreds (1-3 digits) | `050` | 5,000 ft |
| Absolute (4+ digits) | `5000` | 5,000 ft |
| AGL (airport + altitude) | `KOAK010` | 1,000 ft above KOAK field elevation |

The hundreds-vs-absolute rule: values under 1,000 are multiplied by 100; values 1,000+ are used as-is. This applies to both numeric and AGL formats — `KOAK010` means 1,000 ft AGL, `KOAK1500` means 1,500 ft AGL. The airport code can be FAA (e.g., `OAK`) or ICAO (e.g., `KOAK`).

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
| `CTO` | Cleared for takeoff |
| `CTO 270` | Cleared for takeoff, fly heading 270 |
| `CTOR 270` / `CTOL 270` | Cleared for takeoff, turn right/left to heading 270 |
| `CTOR45` / `CTOL45` | Cleared for takeoff, turn right/left 45° from runway heading (no space) |
| `CTOMLT` / `CTOMRT` | Cleared for takeoff, make left/right traffic |
| `CTOC` | Cancel takeoff clearance |
| `CTL` / `FS` | Cleared to land (full stop) |
| `CLC` / `CTLC` | Cancel landing clearance |
| `GA` | Go around (fly runway heading, climb to 1500 AGL) |
| `GA 270 50` | Go around, fly heading 270, climb to 5,000 ft |
| `GA RH 50` | Go around, fly runway heading, climb to 5,000 ft |

The `GA` altitude argument uses the same format as CM/DM (see Altitude Arguments above). `RH` in the heading position means "runway heading."

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
| `HOLD` / `HP` | Hold position (stop wherever on the ground) |
| `RES` / `RESUME` | Resume taxi after hold |
| `CROSS 28L` | Cross runway 28L (clears hold-short) |
| `FOLLOW SWA123` | Follow another aircraft on the ground |

Aircraft automatically hold short at all runway crossings along the taxi route. Use `CROSS` to clear a hold-short — either while already holding short, or in advance to pre-clear it before the aircraft arrives.

Ground aircraft automatically detect and avoid collisions — trailing aircraft slow down or stop to maintain safe separation. Head-on conflicts cause both aircraft to stop.

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

VICE-only commands:

| Command | Effect |
|---------|--------|
| `FC` | Approve frequency change |
| `CT3Y` | Tell pilot to contact TCP 3Y |
| `TO` | Tell pilot to contact tower |

#### Auto-Accept

Handoffs to unattended positions (no CRC client logged in) can be automatically accepted after a configurable delay. Enable and configure the delay in **Settings > General > Auto-accept handoffs**. When disabled, handoffs to unattended positions remain pending until manually accepted.

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

The pause/unpause button and sim rate dropdown in the bottom bar also control these.

## Simulation Controls

At the bottom-right of the window:

- **Pause/Resume** button — toggle simulation pause
- **Sim rate dropdown** — speed up the simulation (1x, 2x, 4x, 8x, 16x)

Pause and sim rate are scoped to your scenario — they don't affect other clients' scenarios.

## Terminal Panel

The terminal panel sits below the aircraft grid. It shows a scrolling history of all commands and server feedback for your scenario, visible to all connected RPOs.

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

## Settings

Click **Settings** in the top-right to configure:

- **General** — User initials (required before connecting) and auto-accept handoff settings (enable/disable + delay in seconds)
- **Command scheme** — ATCTrainer (space-separated) or VICE (concatenated)

## Autocomplete

As you type in the command bar, a popup appears with matching suggestions:

- **Command verbs** — matching verbs from your active command scheme with syntax hints (e.g., `FH  Fly Heading {270}`)
- **Callsigns** — aircraft whose callsign matches what you've typed, showing type and route
- **Fix names** (for DCT and AT arguments) — all VNAS navdata fixes (~40k airports, navaids, waypoints) plus custom scenario fixes
- After accepting a callsign, the popup immediately shows all available command verbs

Suggestions are context-aware: after a `;` or `,` separator in compound commands, suggestions reset for the new command. Conditions (`LV`, `AT`) are also suggested.

### Fix Suggestion Priority

When typing a fix argument (after `DCT` or `AT`), suggestions use two tiers:

1. **Route fixes** (teal) — fixes from the selected aircraft's flight plan: departure, destination, filed route fixes, and all fixes from expanded SIDs/STARs. These are fixes "in the FMS" that the pilot has programmed.
2. **Navdata fixes** (white) — all other navdata fixes matching your prefix.

If no aircraft is selected, only navdata fixes are shown. Route fixes always appear first.

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

YAAT remembers your window size and position across sessions.
