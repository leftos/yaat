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

### Connecting

1. Enter the server URL (default `http://localhost:5000`)
2. Click **Connect**
3. If the server has active scenarios from a previous session, a **rejoin** dialog appears

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
| AHdg / AAlt / ASpd | Assigned heading, altitude, speed targets |

Click an aircraft row to select it. Press **Esc** to deselect.

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
| Direct to fix | `DCT SUNOL` | `DCT SUNOL` |
| Delete aircraft | `DEL` | `X` |

### Altitude Arguments

Altitude arguments (used by CM, DM, and LV) accept three formats:

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

This means "the point on the 090 radial from JFK at 20 NM." The fix name must be 2-5 characters, followed by exactly 3 digits for the radial (degrees) and 3 digits for the distance (NM). FRD works anywhere a fix name is accepted: DCT arguments and AT conditions.

If the last fix in the list appears in the aircraft's filed route, the aircraft continues on its filed route from that point.

### Conditional Blocks

Use `LV` (level at altitude) and `AT` (at fix) to trigger blocks on specific conditions instead of waiting for the previous block:

- **LV** — triggers when the aircraft reaches an altitude:
  ```
  LV 050 FH 270
  ```
  When reaching 5,000 ft, turn to heading 270.

- **AT** — triggers when the aircraft reaches a fix:
  ```
  AT SUNOL FH 180
  ```
  When reaching SUNOL, turn to heading 180.

These work within compound chains:

```
CM 100; LV 050 FH 270; LV 100 DCT SUNOL
```

Climb to 10,000 ft. At 5,000 ft, turn to heading 270. At 10,000 ft, proceed direct SUNOL.

### Global Commands

These commands don't require an aircraft selection:

| Command | Effect |
|---------|--------|
| `PAUSE` | Pause the simulation |
| `UNPAUSE` | Resume the simulation |
| `SIMRATE <n>` | Set simulation speed (1, 2, 4, 8, 16) |

The pause/unpause button and sim rate dropdown in the bottom bar also control these.

## Simulation Controls

At the bottom-right of the window:

- **Pause/Resume** button — toggle simulation pause
- **Sim rate dropdown** — speed up the simulation (1x, 2x, 4x, 8x, 16x)

Pause and sim rate are scoped to your scenario — they don't affect other clients' scenarios.

## Settings

Click **Settings** in the top-right to configure:

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
