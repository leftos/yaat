# YAAT User Guide

YAAT (Yet Another ATC Trainer) is an instructor/RPO desktop client for air traffic control training. It works alongside CRC (the VATSIM radar client) — you control simulated aircraft in YAAT while viewing them on CRC's radar display.

## Table of Contents

- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Launch](#launch)
  - [Configuration](#configuration)
  - [Connecting](#connecting)
  - [Connecting CRC (Optional)](#connecting-crc-optional)
  - [Loading a Scenario](#loading-a-scenario)
  - [Unloading a Scenario](#unloading-a-scenario)
  - [Loading a Weather Profile](#loading-a-weather-profile)
  - [Loading Live Weather](#loading-live-weather)
  - [Weather Editor](#weather-editor)
- [Aircraft List](#aircraft-list)
  - [Info Column](#info-column)
  - [Aircraft Detail Panel](#aircraft-detail-panel)
  - [Distance Column](#distance-column)
- [Commands](#commands)
  - [Selecting an Aircraft](#selecting-an-aircraft)
  - [Command Reference](#command-reference)
  - [Altitude Arguments](#altitude-arguments)
  - [Command Chaining](#command-chaining)
  - [Ground Commands](#ground-commands)
  - [Helicopter Commands](#helicopter-commands)
  - [Tower Commands](#tower-commands)
  - [Pattern Commands](#pattern-commands)
  - [Approach Options](#approach-options)
  - [Approach Control Commands](#approach-control-commands)
  - [Direct To (DCT)](#direct-to-dct)
  - [Navigation Commands](#navigation-commands)
  - [Speed Management](#speed-management)
  - [Holding Patterns](#holding-patterns)
  - [Hold Commands](#hold-commands)
  - [Track Operations](#track-operations)
  - [Consolidation](#consolidation)
  - [Conditional Blocks](#conditional-blocks)
  - [Wait Commands](#wait-commands)
  - [Delayed Aircraft Commands](#delayed-aircraft-commands)
  - [Add Aircraft (ADD)](#add-aircraft-add)
  - [Global Commands](#global-commands)
  - [Force Override Commands](#force-override-commands)
  - [Warp Commands](#warp-commands)
  - [Say Command](#say-command)
- [Simulation Controls](#simulation-controls)
- [Views](#views)
  - [Tabs and Pop-Out](#tabs-and-pop-out)
  - [Aircraft List](#aircraft-list-1)
  - [Flight Plan Editor](#flight-plan-editor)
  - [Ground View](#ground-view)
  - [Radar View](#radar-view)
- [Terminal Panel](#terminal-panel)
  - [Entry Format](#entry-format)
  - [Multi-User Visibility](#multi-user-visibility)
  - [Chat Messages](#chat-messages)
  - [Resizing](#resizing)
  - [Pop Out / Dock](#pop-out--dock)
  - [Warnings](#warnings)
- [Students Panel](#students-panel)
  - [In Room](#in-room)
  - [Lobby](#lobby)
  - [Notes](#notes)
- [Settings](#settings)
- [Autocomplete](#autocomplete)
  - [Fix Suggestion Priority](#fix-suggestion-priority)
- [Macros](#macros)
  - [Defining Macros](#defining-macros)
  - [Parameters](#parameters)
  - [Usage](#usage)
  - [Import / Export](#import--export)
- [Favorite Commands](#favorite-commands)
- [Command History](#command-history)
- [Keyboard Shortcuts](#keyboard-shortcuts)
- [Window State](#window-state)

## Getting Started

### Prerequisites

- .NET 10 SDK
- A running [yaat-server](https://github.com/leftos/yaat-server) instance (default: `http://localhost:5000`)
- CRC (optional, for radar display)

### Launch

```bash
dotnet run --project src/Yaat.Client
```

**Auto-connect:** Pass `--autoconnect <target>` to connect automatically on startup. The target can be a full URL or just a hostname:

```bash
dotnet run --project src/Yaat.Client -- --autoconnect Local
dotnet run --project src/Yaat.Client -- --autoconnect http://192.168.1.50:5000
```

### Configuration

Before connecting, open **Settings** and configure:

- **Identity** tab: VATSIM CID, 2-letter initials (required), and ARTCC ID

Initials and ARTCC ID are required — you cannot connect without them.

### Connecting

1. Configure your identity in **Settings** (required)
2. **File > Connect** (or **Disconnect** to close the connection)
3. After connecting, the room list appears — create or join a room

### Connecting CRC (Optional)

CRC (Consolidated Radar Client) is the VATSIM radar client that students use to work scopes. Connecting CRC to YAAT is optional — you can use YAAT standalone for command practice.

**Option A: Setup script (recommended)**

The yaat-server repo includes a script that configures CRC automatically:

```powershell
cd path/to/yaat-server
.\Setup-CrcEnvironment.ps1
```

This finds your CRC installation via the registry and creates or updates its `DevEnvironments.json` with a "YAAT Local" entry pointing to `http://localhost:5000`.

**Option B: Manual configuration**

1. Find your CRC installation folder (check `HKCU:\Software\CRC\Install_Dir` in the registry, or look in `%LOCALAPPDATA%\Programs\crc`)
2. Create or edit `DevEnvironments.json` in that folder:

```json
[
  {
    "name": "YAAT Local",
    "clientHubUrl": "http://localhost:5000/hubs/client",
    "apiBaseUrl": "http://localhost:5000",
    "isDisabled": false,
    "isSweatbox": false
  }
]
```

**Connecting from CRC:**

1. Make sure the YAAT server is running
2. Restart CRC (it reads `DevEnvironments.json` on startup)
3. In CRC's environment selector, choose **YAAT Local**
4. Connect with your VATSIM credentials — the instructor can then pull the student into a room from the [Students Panel](#students-panel)

### Loading a Scenario

**Scenario > Load Scenario...** opens a dialog with two tabs:

- **ARTCC Scenarios** (default) — lists training scenarios from the vNAS data API for your configured ARTCC. Use the Airport filter to narrow by primary airport. Requires an ARTCC ID set in Settings.
- **Local Files** — browse a local folder for ATCTrainer-format JSON scenario files. Supports Facility and Rating filters parsed from scenario names.

Select a scenario and click **Load** (or double-click). Aircraft spawn at their configured starting positions. The window title shows the room name and scenario name. To switch scenarios, load a new one — a confirmation dialog appears if one is already active.

Both API and local scenarios appear in the **Scenario > Load Recent Scenario** menu for quick reloading.

### Unloading a Scenario

**Scenario > Unload Scenario** removes all aircraft from the current scenario. A confirmation dialog appears if multiple clients are connected.

### Loading a Weather Profile

**Scenario > Load Weather...** opens a dialog with two tabs:

- **ARTCC Weather** (default) — lists weather profiles from the vNAS data API for your configured ARTCC. Each entry shows the name and wind layer count. Requires an ARTCC ID set in Settings.
- **Local Files** — browse a local folder for ATCTrainer-format weather JSON files.

Select a profile and click **Load** (or double-click). Weather is room-level and persists across scenario loads/unloads. Both API and local weather profiles appear in the **Scenario > Load Recent Weather** menu for quick reloading.

The active weather name is shown in the terminal when weather is loaded or cleared.

**Speed note:** Speed commands (`SPD`, `CM`, `DM`) assign IAS (indicated airspeed). Aircraft ground speed — what appears on the radar scope and in the aircraft grid — is derived from IAS + wind at altitude. Expect aircraft flying into a headwind to show a lower ground speed than their assigned IAS, and a higher ground speed when the wind is from behind. Aircraft on the ground are unaffected by wind.

### Loading Live Weather

**Scenario > Load Live Weather** fetches real-world METARs and winds aloft from aviationweather.gov for your ARTCC and loads them as a weather profile. Requires a room, an ARTCC ID configured in Settings, and navdata to be initialized.

Live weather builds wind layers from FAA Winds and Temperatures Aloft (FD) data at standard levels (3000–39000 ft) and a surface layer averaged from METARs. FD wind directions are converted from true to magnetic heading automatically.

**Clear weather:** **Scenario > Clear Weather** removes the active weather profile. All aircraft return to IAS = GS behavior.

**Save weather:** **Scenario > Save Weather As...** exports the active weather profile to a JSON file. Only available when weather is loaded.

### Weather Editor

**Scenario > New Weather...** opens the weather editor with a single empty period.

**Scenario > Edit Weather...** opens the editor pre-populated with the active weather. If a timeline is active, all periods are restored.

The editor has two panels:

- **Left panel** — period list. Use **+ Add** to create new periods and **- Remove** to delete the selected one (minimum one period). Each period shows its start time.
- **Right panel** — selected period details: start time (minutes), transition duration (minutes), precipitation type, wind layers grid (altitude, direction, speed, gusts), and METARs list.

**Saving format:** If the editor has a single period, it saves as a v1 weather profile (compatible with ATCTrainer). With two or more periods, it saves as a v2 weather timeline with time-based transitions.

- **Apply to Sim** sends the weather to the server immediately. The editor stays open for further edits.
- **Save As...** exports to a JSON file without sending it to the server.
- **Close** closes the editor.

Only one editor window can be open at a time.

### Weather Timelines (V2 Format)

YAAT supports a **v2 weather JSON format** that defines time-based weather evolution with multiple periods. Wind layers interpolate smoothly during transitions while METARs and precipitation change instantly at the period boundary.

**V2 JSON structure:**

```json
{
  "name": "SFOW → SFOE transition",
  "artccId": "ZOA",
  "periods": [
    {
      "startMinutes": 0,
      "transitionMinutes": 0,
      "precipitation": "None",
      "windLayers": [
        { "altitude": 3000, "direction": 280, "speed": 12 }
      ],
      "metars": ["KSFO 031753Z 28012KT 10SM FEW200"]
    },
    {
      "startMinutes": 20,
      "transitionMinutes": 10,
      "precipitation": "Rain",
      "windLayers": [
        { "altitude": 3000, "direction": 250, "speed": 15 }
      ],
      "metars": ["KSFO 031853Z 25015G22KT 6SM -RA"]
    }
  ]
}
```

**How transitions work:**

- `startMinutes` — simulation elapsed time (in minutes) when this period activates.
- `transitionMinutes` — duration (in minutes) over which wind layers blend from the previous period to this one.
- At `startMinutes`, METARs and precipitation snap to this period's values immediately.
- Wind layers interpolate linearly from the previous period over the transition window, handling the 360°/0° direction boundary correctly.
- If `transitionMinutes` is 0, all weather changes instantly at `startMinutes`.

**Loading:** Load v2 weather files using **Scenario > Load Weather... > Local Files** — files with a `periods` array are automatically detected. The v1 (single weather profile) format continues to work unchanged.

**Rewind:** Weather timelines are fully compatible with the rewind system. Rewinding past a transition boundary restores the correct interpolated weather for that point in time.

## Aircraft List

The main grid shows all aircraft in your scenario, grouped into **Active** and **Delayed** sections. Click the group header row to collapse or expand each section. Use **View > Reset Aircraft List Layout** to restore default column order, widths, and sorting.

| Column | Description |
|--------|-------------|
| Callsign | Aircraft callsign (e.g., UAL123) |
| Info | Smart status — contextual summary of what the aircraft is doing right now (see [Info Column](#info-column) below) |
| Status | Spawn status (Active, Delayed, etc.) |
| Type | Aircraft type code (e.g., B738/L) |
| Rules | Flight rules (IFR / VFR) |
| Dep / Dest | Departure and destination airports |
| Route | Filed route |
| P.Alt | Planned cruise altitude (e.g., 035, VFR, VFR/035, OTP/035) |
| Remarks | Flight plan remarks |
| Squawk | Assigned transponder code |
| Hdg | Current heading |
| Alt | Current altitude (ft) |
| Spd | Current ground speed (kts) |
| VS | Vertical speed (fpm) |
| Ctrl | Assigned controller initials (see [Aircraft Assignments](#aircraft-assignments)) |
| Owner | Track owner (sector code, e.g., "2B", or callsign) |
| HO | Pending handoff target (sector code or callsign) |
| SP1 | Scratchpad 1 text |
| SP2 | Scratchpad 2 text |
| TA | Temporary altitude assignment |
| Phase | Current phase name (e.g., Downwind, FinalApproach, TakeoffRoll) |
| Rwy | Assigned runway |
| AHdg / AAlt / ASpd | Assigned targets — AHdg shows the next fix name when navigating, or heading when under vectors |
| Dist | Distance in NM from the reference fix (see below) |
| Pending Cmds | Numbered pending command blocks (from compound commands). Shows `[Active]` for the current block, then `[1]`, `[2]`, etc. for queued blocks |

Click an aircraft row to select it, or type a callsign in the command input and press the aircraft select key (**Numpad +** by default, configurable in Settings > Advanced). Press **Esc** to deselect. Click a column header to sort by that column; click again to reverse the sort direction. Sorting always keeps the Active group on top and Delayed on the bottom, sorting within each group independently.

Drag column headers to rearrange the column order. **Right-click any column header** to open the Column Chooser, where you can show/hide columns and reorder them using the Top/Up/Down/Last buttons. Click **Reset to Defaults** to restore the original column order with all columns visible. The Column Chooser also has a **"Show only active aircraft"** checkbox that hides delayed (not yet spawned) aircraft from the grid. Column order, widths, visibility, sort state, and the active-only filter are remembered across sessions.

To share your layout with others, use the **Export...** and **Import...** buttons in the Column Chooser. Export saves the current column order, visibility, widths, and sort state to a `.yaat-grid-layout.json` file. Import loads a layout file and updates the dialog preview — click OK to apply it to the grid. Different training levels may benefit from different layouts — for example, an S1 ground layout might hide approach-related columns, while an S2 or S3 layout shows them. Export a layout for each level and swap between them as needed.

### Info Column

The **Info** column shows a single contextual summary of what each aircraft is doing. It adapts to the aircraft's flight stage so you can scan one column instead of cross-referencing Phase, Clearance, Runway, Approach, etc.

**Color coding:**
- **White** — normal status (phase description, taxi route, navigation info)
- **Gold/amber** — warning that needs attention (pending handoff, no altitude assignment)
- **Red** — critical alert requiring immediate action (on final or landing without clearance)

**Alert conditions** (override normal text, highest priority first):
| Condition | Text | Color |
|-----------|------|-------|
| On final approach without landing clearance | "No landing clnc" | Red |
| Landing without clearance | "Landing — no clnc!" | Red |
| Handoff in progress | "HO → {sector}" | Gold |
| Airborne, no phase/SID/STAR, no altitude assignment, no nav route | "No altitude asgn" | Gold |

**Phase-based status** (white text, shown when no alerts apply): The text describes the current phase in plain language — e.g., "Taxi to RWY 28R via A B C", "LUAW 28R", "Departing 28R, OAK5", "ILS28R → CEPIN DUMBA AXMUL", "Left downwind 28R", "Landing 28R", "Go-around 28R".

**No-phase fallback** (white text, when no phase is active): Shows climb/descent arrows with assigned altitude if set (e.g., "↑ FL350", "↓ 5,000"), navigation route if set (e.g., "→ OAK SFO LAX"), or "On ground" / "FL350, on course" as a last resort.

The Info column is searchable — type status text in the search box to filter aircraft (e.g., "landing", "taxi", "holding").

### Aircraft Detail Panel

Selecting an aircraft row expands a detail panel below it showing additional state not visible in the grid columns:

| Section | Shown when | Content |
|---------|------------|---------|
| Phases | Aircraft is tower-managed | Phase sequence with active phase in brackets, e.g. `[Base] > FinalApproach > Landing` |
| Pattern | Aircraft is in the pattern | Traffic direction (Left/Right traffic) |
| Clearance | A clearance has been issued | Clearance type and runway, e.g. "Cleared to land Rwy 28L" |
| Route | Aircraft has a navigation route | Remaining waypoints in the route |
| Cruise | Filed cruise altitude exists | Filed cruise altitude and speed |
| Pending | Queued command blocks exist | Numbered pending commands: `[Active]` current block, `[1]`/`[2]`/... queued blocks. Use `DELAT {n}` to remove by number |

Sections with no data are hidden. Selecting a different row collapses the previous panel and expands the new one.

### Distance Column

The **Dist** column shows each aircraft's distance in nautical miles from a reference fix. When a scenario is loaded, the reference fix defaults to the scenario's primary airport.

To change the reference fix, **middle-click** the "Dist" column header. A flyout appears where you can type a fix name or FRD (fix-radial-distance) string. Autocomplete suggestions appear as you type. Press **Enter** or click a suggestion to apply, **Escape** to cancel.

Delayed aircraft (not yet spawned) show a blank distance.

## Commands

Type commands in the command bar at the bottom and press **Enter**.

### Selecting an Aircraft

Type the callsign (or any partial match that uniquely identifies one aircraft) before the command:

```
UAL123 FH 270
```

You can also select an aircraft without sending a command: type the callsign and press the aircraft select key (**Numpad +** by default). This selects the aircraft and clears the input.

If an aircraft is already selected (clicked in the grid or via the select key), you can omit the callsign:

```
FH 270
```

### Command Reference

YAAT uses a unified command scheme that accepts aliases from both ATCTrainer and VICE. Commands are space-separated by default (e.g., `FH 270`), but numeric arguments can be written without a space when unambiguous (e.g., `FH270`, `H270`, `CM240`). This concatenation works for any verb with a numeric argument — not just the examples listed below.

The `H` alias is shared: bare `H` (no argument) maps to Fly Present Heading; `H 270` or `H270` maps to Fly Heading. Similarly, `T` is shared: `T30L` is relative left 30°, `T30R` is relative right 30°.

#### Heading

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Fly heading | `FH 270` | `H` | `FH270`, `H270` |
| Turn left | `TL 180` | `L`, `LT` | `TL180`, `L180` |
| Turn right | `TR 090` | `R`, `RT` | `TR090`, `R090` |
| Relative left | `RELL 30` | — | `T30L` |
| Relative right | `RELR 30` | — | `T30R` |
| Fly present heading | `FPH` | `FCH`, `H` | — |

#### Altitude / Speed

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Climb and maintain | `CM 240` | `C` | `CM240`, `C240` |
| Descend and maintain | `DM 050` | `D` | `DM050`, `D050` |
| Speed | `SPD 250` | `S`, `SLOW`, `SL`, `SPEED` | `SPD250`, `S250` |
| Speed floor | `SPD 210+` | — | — |
| Speed ceiling | `SPD 210-` | — | — |
| Resume normal speed | `RNS` | `NS` | — |
| Expedite climb/descent | `EXP [alt]` | — | — |
| Resume normal rate | `NORM` | — | — |
| Reduce to final approach speed | `RFAS` | `FAS` | — |
| Mach number | `MACH .82` | `M` | — |
| Delete speed restrictions | `DSR` | — | — |

#### Squawk / Transponder

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Squawk | `SQ 4521` | `SQUAWK` | `SQ4521` |
| Squawk (reset) | `SQ` | — | — |
| Squawk VFR | `SQVFR` | `SQV` | — |
| Squawk normal | `SQNORM` | `SN`, `SQA`, `SQON` | — |
| Squawk standby | `SQSBY` | `SS`, `SQS` | — |
| Ident | `IDENT` | `ID`, `SQI`, `SQID` | — |
| Random squawk | `RANDSQ` | — | — |

#### Navigation

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Direct to fix | `DCT SUNOL` | — | — |
| Force direct to | `DCTF SJC` | — | — |
| Append direct to | `ADCT SUNOL` | — | — |
| Append force DCT | `ADCTF SUNOL` | — | — |

#### Ground

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Pushback | `PUSH` | — | — |
| Taxi | `TAXI S T U` | — | — |
| Hold position | `HOLD` | `HP` | — |
| Resume taxi | `RES` | `RESUME` | — |
| Cross runway | `CROSS 28L` | — | — |
| Hold short | `HS B` | — | — |
| Assign runway | `RWY 30` | — | — |
| Exit left | `EL` | `EXITL` | — |
| Exit right | `ER` | `EXITR` | — |
| Exit taxiway | `EXIT A3` | — | — |
| Follow | `FOLLOW SWA123` | `FOL` | — |
| Give way | `GIVEWAY SWA123` | `BEHIND` | — |

#### Helicopter

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Takeoff present pos | `CTOPP` | — | — |
| Air taxi | `ATXI H1` | — | — |
| Land | `LAND H1` | — | — |

#### Hold

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Hold (360 left) | `HPPL` | — | — |
| Hold (360 right) | `HPPR` | — | — |
| Hold present pos | `HPP` | — | — |
| Hold at fix (left) | `HFIXL SUNOL` | — | — |
| Hold at fix (right) | `HFIXR SUNOL` | — | — |
| Hold at fix | `HFIX SUNOL` | — | — |

#### Approach / Procedures

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Cleared approach | `CAPP ILS28R` | — | — |
| Join approach | `JAPP ILS28R` | — | — |
| Straight-in apch | `CAPPSI ILS28R` | `JAPPSI` | — |
| Forced approach | `CAPPF ILS28R` | `JAPPF` | — |
| Pos/Turn/Alt/Clr | `PTAC 280 025 ILS30` | `PTAC PH PA` | — |
| Join final | `JFAC ILS28R` | `JLOC`, `JF` | — |
| Join arrival | `JARR OAK.SALI2` | `ARR`, `STAR`, `JSTAR` | — |
| Join airway | `JAWY V25` | — | — |
| Join radial out | `JRADO SJC 150` | `JRAD` | — |
| Join radial in | `JRADI SJC 150` | `JICRS` | — |
| Cross fix | `CFIX SUNOL A034` | — | — |
| Descend via | `DVIA` | — | — |
| Climb via | `CVIA` | — | — |
| Depart fix | `DEPART SUNOL 270` | `DEP` | — |
| Holding pattern | `HOLDP SUNOL R 180 1M` | `HOLD` (with args) | — |
| Expect approach | `EAPP I28R` | `EXPECT` | — |
| Visual approach | `CVA 28R` | `VISUAL` | — |
| Report field | `RFIS` | — | — |
| Report traffic | `RTIS` | — | — |
| List approaches | `APPS` | `APPS OAK` | — |

#### Tower

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Line up and wait | `LUAW` | `POS`, `LU`, `PH` | — |
| Cleared for takeoff | `CTO` | — | — |
| Cancel takeoff | `CTOC` | — | — |
| Cleared to land | `CLAND` | `CL`, `FS` | — |
| Land and hold short | `LAHSO` | — | — |
| Cancel landing | `CLC` | `CTLC` | — |
| Go around | `GA` | — | — |
| Touch and go | `TG` | — | — |
| Stop and go | `SG` | — | — |
| Begin takeoff roll during stop-and-go | `GO` | — | — |
| Low approach | `LA` | — | — |
| Cleared for option | `COPT` | — | — |
| Landing sequence | `SEQ 2 UAL123` | — | — |

#### Pattern

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Enter L downwind | `ELD` | — | — |
| Enter R downwind | `ERD` | — | — |
| Enter L crosswind | `ELC` | — | — |
| Enter R crosswind | `ERC` | — | — |
| Enter L base | `ELB` | — | — |
| Enter R base | `ERB` | — | — |
| Enter final | `EF` | — | — |
| Make L traffic | `MLT` | — | — |
| Make R traffic | `MRT` | — | — |
| Turn crosswind | `TC` | — | — |
| Turn downwind | `TD` | — | — |
| Turn base | `TB` | — | — |
| Extend leg | `EXT` | — | — |
| Short approach | `SA` | `MSA` | — |
| Normal approach | `MNA` | — | — |
| Left 360 | `L360` | — | — |
| Right 360 | `R360` | — | — |
| Left 270 | `L270` | — | — |
| Right 270 | `R270` | — | — |
| Cancel 270 | `NO270` | — | — |
| Plan 270 | `P270` | `PLAN270` | — |
| Pattern size | `PS 1.5` | `PATTSIZE` | — |
| S-turns (init L) | `MLS` | — | — |
| S-turns (init R) | `MRS` | — | — |
| Circle airport | `CA` | `CIRCLE` | — |

#### Track Operations

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Track | `TRACK` | — | — |
| Drop | `DROP` | — | — |
| Handoff | `HO 3Y` | — | `HO3Y` |
| Force Handoff | `HOF 3Y` | — | `HOF3Y` |
| Accept | `ACCEPT` | `A` | — |
| Cancel | `CANCEL` | — | — |
| Accept all | `ACCEPTALL` | — | — |
| Handoff all | `HOALL 3Y` | — | — |
| Pointout | `PO 3Y` | — | — |
| Acknowledge | `OK` | — | — |

#### Strip Operations

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Annotate strip box | `AN 3 RV` | `ANNOTATE`, `BOX` | — |
| Push strip to bay | `STRIP Ground` | — | — |

#### Data Operations

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Scratchpad 1 | `SP1 OAK` | — | — |
| Scratchpad 2 | `SP2 I8R` | — | — |
| Temp altitude | `TEMPALT 120` | `TA`, `TEMP`, `QQ` | — |
| Cruise | `CRUISE 240` | `QZ` | — |
| On-handoff | `ONHO` | `ONH` | — |

#### Flight Plan

| Command | Primary | Aliases | Notes |
|---------|---------|---------|-------|
| Change destination | `APT KSFO` | `DEST` | Changes aircraft destination airport |
| Create IFR flight plan | `FP B738 220 KBOS DCT KJFK` | — | Altitude in hundreds (220 = FL220) |
| Create VFR flight plan | `VP C172 5500 KOAK DCT KJFK` | — | Altitude absolute (5500 = 5,500 ft) |
| Set remarks | `REMARKS /V/ STUDENT` | `REM` | Sets flight plan remarks field |

#### Consolidation

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Active position | `AS 2B` | — | — |
| Consolidate | `CON 1T 1F` | — | — |
| Consolidate full | `CON+ 1T 1F` | — | — |
| Deconsolidate | `DECON 1F` | — | — |

#### Sim Control

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Pause | `PAUSE` | `P` | — |
| Unpause | `UNPAUSE` | `U`, `UN`, `UNP`, `UP` | — |
| Sim rate | `SIMRATE 2` | — | — |
| Delete aircraft | `DEL` | `X` | — |
| Delete queued commands | `DELAT` / `DELAT 2` | — | — |
| Show queued commands | `SHOWAT` | — | — |
| Say | `SAY text` | — | — |
| Say speed | `SSPD` | — | Aircraft reports current speed (includes Mach at/above FL240) |
| Say mach | `SMACH` | — | Aircraft reports current Mach number |
| Say expected approach | `SEAPP` | — | Aircraft reports expected approach |
| Say altitude | `SALT` | — | Aircraft reports current and target altitude |
| Say heading | `SHDG` | — | Aircraft reports heading (includes direct-to fix if navigating) |
| Say position | `SPOS` | — | Aircraft reports position as fix-radial-distance |
| Spawn now | `SPAWN` | — | — |
| Spawn delay | `DELAY 120` | — | — |
| Wait (seconds) | `WAIT 30` | — | — |
| Wait (distance) | `WAITD 4` | — | — |
| Add aircraft | `ADD IFR H J ...` | — | — |
| Force heading | `FHN 270` | — | — |
| Force altitude | `CMN 240` | — | — |
| Force speed | `SPDN 250` | — | — |
| Warp | `WARP FRD hdg alt spd` | — | — |
| Warp ground | `WARPG location` | — | — |

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

### Ground Commands

| Command | Effect |
|---------|--------|
| `PUSH` | Push back from parking (reverse at ~5 kts) |
| `PUSH 270` | Push back facing heading 270 |
| `PUSH A` | Push back onto taxiway A |
| `PUSH TE 180` | Push back onto taxiway TE, facing heading 180 |
| `PUSH TE T` | Push back onto taxiway TE, facing toward taxiway T |
| `PUSH @4A` | Push back to spot 4A (A* pathfinding, use parking heading) |
| `PUSH @4A A` | Push back to spot 4A, face toward taxiway A |
| `PUSH @4A 180` | Push back to spot 4A, face heading 180 |
| `TAXI S T U W W1` | Taxi via taxiways S, T, U, W, W1 |
| `TAXI T U W 30` | Taxi via T, U, W to runway 30 |
| `TAXI T U W RWY 30` | Same as above (explicit RWY keyword) |
| `RWY 30 TAXI T U W` | Same as above (RWY-first syntax) |
| `TAXI S T U HS 28L` | Taxi via S, T, U with explicit hold-short at runway 28L |
| `TAXI S T U @B12 NODEL` | Taxi via S, T, U to parking B12 (exempt from auto-delete) |
| `TAXI #42 #18 #95` | Taxi via exact node IDs (used by draw route; see Ground View debug overlay) |
| `TAXI A #42 B` | Mixed: walk taxiway A, A* to node 42, walk taxiway B |
| `HOLD` / `HP` | Hold position (stop wherever on the ground) |
| `RES` / `RESUME` | Resume taxi after hold |
| `CROSS 28L` | Cross runway 28L (clears hold-short) |
| `CROSS B` | Cross taxiway B (clears hold-short) |
| `HS B` | Hold short at the next intersection with taxiway B |
| `HS 28L` | Hold short at the next runway 28L crossing |
| `RWY 30` | Assign runway 30 (override runway assignment without taxi) |
| `FOLLOW SWA123` | Follow another aircraft on the ground |
| `GIVEWAY SWA123` | Wait for SWA123 to pass before executing the next command (see [Conditional Blocks](#conditional-blocks)) |
| `TAXIALL 30` | Taxi all parked aircraft to runway 30 via A* pathfinding (global command, no callsign needed) |
| `BREAK` | Ignore ground conflicts for 15 seconds |

Aircraft automatically hold short at all runway crossings along the taxi route. Use `CROSS` to clear a hold-short — either while already holding short, or in advance to pre-clear it before the aircraft arrives. `CROSS` works for both runway and taxiway hold-shorts.

`HS` can be issued to a taxiing aircraft to add a hold-short point at the first upcoming intersection with the given taxiway or runway along the remaining route.

When you taxi to a hold-short point (via context menu or command), the runway is automatically assigned based on the closest threshold. Override with `RWY {id}` if needed.

Ground aircraft automatically detect and avoid collisions — trailing aircraft slow down or stop to maintain safe separation. Head-on conflicts cause both aircraft to stop. Use `BREAK` to temporarily override conflict avoidance for 15 seconds.

### Helicopter Commands

Helicopters are detected automatically from the ICAO type designator. They use tighter traffic patterns (500ft AGL), steeper glideslopes (6°), and can take off/land vertically from non-runway positions.

| Command | Effect |
|---------|--------|
| `CTOPP` | Cleared for takeoff, present position — vertical liftoff from ramp, helipad, or parking |
| `ATXI H1` | Air taxi to spot H1 — airborne below 100ft AGL, ~40 KIAS |
| `LAND H1` | Land at named spot H1 (helipad, parking, or ramp position) |
| `LAND H1 NODEL` | Land at H1, exempt from auto-delete |

Helicopters can also use all standard tower commands (`CTO`, `CLAND`, `LUAW`, `TG`, `SG`, `GA`) with runway assignments — they hover-taxi onto the runway, hold position, and take off/land like fixed-wing aircraft. This is typical for IFR operations. `CTO` requires a runway; `CTOPP` does not.

**Spawning helicopters:** Use the ADD command with a helicopter type (e.g., `H60`, `EC35`, `R44`). Use `@` prefix for helipad/parking spawn: `ADD V S P @H1 H60`.

### Tower Commands

These commands control aircraft during takeoff, landing, and pattern operations. They require the aircraft to be in the phase system (e.g., spawned on a runway or on final approach from a scenario).

| Command | Effect |
|---------|--------|
| `LUAW` / `POS` / `LU` / `PH` | Line up and wait — aircraft holds on runway |
| `CTO` | Cleared for takeoff (default departure) |
| `CTO 060` | Cleared for takeoff, fly heading 060 |
| `CTO 060 250` | Cleared for takeoff, fly heading 060, climb and maintain 25,000 ft |
| `CTO MRC` | Cleared for takeoff, right crosswind departure (90° right turn) |
| `CTO MRD` | Cleared for takeoff, right downwind departure (180° right turn) |
| `CTO MR270` | Cleared for takeoff, right 270° departure (turn right 270° from runway heading) |
| `CTO MR45` | Cleared for takeoff, turn right 45° from runway heading |
| `CTO ML270` / `MLC` / `MLD` / `ML45` | Left-turn equivalents of MR270/MRC/MRD/MR{N} |
| `CTO MRH` / `RH` / `MSO` | Cleared for takeoff, fly runway heading |
| `CTO H270` | Cleared for takeoff, fly heading 270 (shortest turn) |
| `CTO RH270` / `RT270` | Cleared for takeoff, turn right heading 270 |
| `CTO LH270` / `LT270` | Cleared for takeoff, turn left heading 270 |
| `CTO OC` | Cleared for takeoff, on course (direct to destination) |
| `CTO DCT SUNOL` | Cleared for takeoff, direct to fix SUNOL |
| `CTO MRT` / `CTOMRT` | Cleared for takeoff, make right traffic (closed pattern) |
| `CTO MRT 28R` | Cleared for takeoff, make right traffic runway 28R (cross-runway pattern) |
| `CTO MLT` / `CTOMLT` | Cleared for takeoff, make left traffic (closed pattern) |
| `CTO MLT 28L` | Cleared for takeoff, make left traffic runway 28L (cross-runway pattern) |
| `CTOC` | Cancel takeoff clearance |
| `CLAND` / `CL` / `FS` | Cleared to land (full stop) |
| `CLAND NODEL` | Cleared to land (exempt from auto-delete after landing) |
| `LAHSO 33` | Cleared to land, hold short of runway 33 (LAHSO). Includes landing clearance. Aircraft stops before the intersecting runway and waits for a taxi/cross command. |
| `CLC` / `CTLC` | Cancel landing clearance |
| `GA` | Go around (instrument: fly published missed approach; otherwise: runway heading, 2,000 AGL) |
| `GA MRT` | Go around, make right traffic |
| `GA MLT` | Go around, make left traffic |
| `GA 270 50` | Go around, fly heading 270, climb to 5,000 ft (overrides published missed approach) |
| `GA RH 50` | Go around, fly runway heading, climb to 5,000 ft (overrides published missed approach) |
| `EL` / `EXITL` | Exit runway to the left |
| `ER` / `EXITR` | Exit runway to the right |
| `EXIT A3` | Exit runway at taxiway A3 |
| `EL NODEL` / `ER NODEL` / `EXIT A3 NODEL` | Exit with auto-delete exemption |

When an exit is assigned (via `EL`, `ER`, or `EXIT`), the aircraft maintains a higher rollout speed and only decelerates when kinematically necessary to reach the exit at the correct turn-off speed. High-speed exits (≤45° from runway heading) target ~30 kts; standard 90° exits target ~15 kts. Without an assigned exit, aircraft decelerate uniformly to 20 kts. This allows realistic runway throughput in high-traffic scenarios.

#### CTO Departure Modifiers

All CTO modifiers accept an optional altitude suffix using the same format as CM/DM (see Altitude Arguments above). A bare number (1-360) without a modifier prefix is interpreted as a heading: `CTO 270` = fly heading 270, `CTO 270 050` = fly heading 270, climb to 5,000 ft.

**IFR aircraft** can only use bare `CTO` (default SID/route departure) or `CTO` with a heading (`CTO 270`, `CTO RH`, `CTO H270`, etc.). Pattern exit modifiers (`MRC`, `MRD`, `OC`, `MLT`, `DCT`, etc.) are VFR-only.

| Modifier | Departure type | VFR/IFR |
|----------|----------------|---------|
| *(none)* | Default departure — VFR: runway heading; IFR: navigates filed route (SID expansion) | Both |
| `{N}` | Bare heading (1-360) — fly heading N (shortest turn) | Both |
| `MRH` / `MSO` / `RH` | Fly runway heading (straight out) | Both |
| `H{N}` | Fly heading N (shortest turn) | Both |
| `RH{N}` / `RT{N}` | Turn right heading N | Both |
| `LH{N}` / `LT{N}` | Turn left heading N | Both |
| `MRC` / `MLC` | Right/left crosswind (90° turn from runway heading) | VFR only |
| `MRD` / `MLD` | Right/left downwind (180° turn) | VFR only |
| `MR{N}` / `ML{N}` | Right/left turn of N degrees (1-359) from runway heading | VFR only |
| `OC` | On course — navigate direct to destination airport | VFR only |
| `DCT {fix}` | Direct to named fix | VFR only |
| `MRT` / `MLT` | Make right/left closed traffic (enter pattern) | VFR only |
| `MRT {rwy}` / `MLT {rwy}` | Cross-runway closed traffic (pattern for a different runway) | VFR only |

**Cross-runway pattern** — `CTO MRT 28R` from runway 33 clears the aircraft for takeoff on runway 33 and enters right traffic for runway 28R. The pattern circuit (upwind, crosswind, downwind, base, final) is built for the pattern runway, not the takeoff runway. Auto-cycle after touch-and-go also uses the pattern runway.

**Altitude resolution** — when no altitude is specified, the target depends on flight rules and departure type:

1. Closed traffic → pattern altitude (1,000 ft AGL for props, 1,500 ft AGL for jets)
2. VFR with filed cruise altitude → cruise altitude
3. VFR without cruise → pattern altitude
4. IFR → self-clear at 1,500 ft AGL

**Navigation** — IFR `CTO` (default departure) automatically expands the filed route including SID waypoints and navigates the aircraft along it. When CIFP data is available, SID legs include published altitude and speed constraints; SID via mode activates automatically so the aircraft follows the published climb profile. Use `CM` to override (disables via mode), or `CVIA` to re-enable it. `CTO DCT {fix}` turns the aircraft toward the fix after liftoff. `CTO OC` navigates toward the destination airport.

The `GA` altitude argument uses the same format as CM/DM (see Altitude Arguments above). `RH` in the heading position means "runway heading." `GA MRT`/`GA MLT` sets the aircraft into pattern mode (make right/left traffic) and climbs to pattern altitude.

**Published missed approach** — When an aircraft on an instrument approach (ILS, RNAV, VOR, etc.) goes around — either automatically or via `GA` — it flies the published missed approach procedure from CIFP data. This includes climbing to the missed approach altitude, navigating through the MAP fix sequence, and entering a holding pattern at the final MAP fix if the procedure defines one (HA/HF/HM leg). The aircraft holds indefinitely until given further instructions. If the procedure has no holding leg, the aircraft completes the MAP fix sequence and awaits vectors. Visual approaches and pattern traffic have no published missed approach and use the generic go-around behavior instead.

**ATC override** — If `GA` includes an explicit heading or altitude (e.g., `GA 270 50`), the published missed approach is cancelled and the aircraft flies the assigned heading/altitude instead. Any heading or altitude command issued while the aircraft is flying the missed approach procedure also cancels it and re-vectors the aircraft.

**Auto go-around** — When no landing clearance is issued by 0.5nm from the threshold, the aircraft goes around automatically and broadcasts a warning. Instrument approaches fly the published missed approach; VFR and pattern traffic re-enter the pattern; IFR non-pattern traffic without MAP data flies runway heading at 2,000 AGL.

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
| `MLT 28R` / `MRT 28R` | Make left/right traffic for a specific runway (cross-runway pattern) |
| `TC` / `TD` / `TB` | Turn crosswind / downwind / base (advance to next leg) |
| `EXT` | Extend current pattern leg (upwind, crosswind, downwind, or base) |
| `ELC` / `ERC` | Enter left/right crosswind |
| `ELC 28R` / `ERC 28R` | Enter left/right crosswind, assign runway |
| `SA` / `MSA` | Make short approach (cut base turn short) |
| `MNA` | Make normal approach (cancel short approach) |
| `L360` / `R360` | Left/right 360° orbit in the pattern (resumes same leg after) |
| `L270` / `R270` | Left/right 270° turn (immediate) |
| `P270` / `PLAN270` | Plan a 270° turn at the next pattern turn point |
| `NO270` | Cancel a 270 in progress or a planned 270 |
| `PS 1.5` / `PATTSIZE 1.5` | Set pattern size (0.25–10.0 NM downwind offset) |
| `MLS` / `MRS` | S-turns on final, initial left/right (default 2 turns) |
| `MLS 3` / `MRS 4` | S-turns with specified count |
| `CA` / `CIRCLE` | Circle the airport |
| `SEQ 2 UAL123` | Landing sequence: you are number 2, follow UAL123 |

All pattern entry commands (ELB, ERB, ELD, ERD, ELC, ERC, EF) accept an optional runway argument that assigns or overrides the runway. ELB/ERB also accept an optional distance argument that controls how far from the threshold the final turn occurs (default is the standard pattern geometry).

`P270` plans a 270° turn at the next pattern turn point without executing immediately. The turn direction is automatically determined from the traffic pattern direction (left 270 for left traffic, right 270 for right traffic). Use `NO270` to cancel a planned 270 before it executes. `NO270` also cancels an in-progress 270.

`PS` sets the pattern downwind offset distance. The crosswind extension and base extension scale proportionally. The override persists across pattern circuits until cleared. Use the category default by setting `PS` to the standard value for the aircraft type.

`SEQ` assigns a sequence number and optionally a traffic to follow. Use `SEQ 2 UAL123` to tell the aircraft "number 2, follow UAL123." The number-only form `SEQ 2` sets the sequence position without specifying traffic.

### Approach Options

| Command | Effect |
|---------|--------|
| `TG` | Touch-and-go (establishes pattern mode if not already) |
| `SG` | Stop-and-go (full stop then re-takeoff, establishes pattern mode) |
| `GO` | Begin takeoff roll immediately during a stop-and-go (bypasses auto-pause) |
| `LA` | Low approach (fly-through without touchdown, establishes pattern mode) |
| `COPT` | Cleared for the option |

TG, SG, and LA set pattern mode — the aircraft will continue doing touch-and-goes after the next approach. Use `MLT`/`MRT` to specify traffic direction, or combine: `TG, MLT`.

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
| `PTAC PH PA ILS30` | PTAC with present heading and present altitude, explicit approach |
| `PTAC PH PA` | PTAC with present heading/altitude, auto-resolve approach from expected approach or runway |
| `PTAC 280 PA` | PTAC with explicit heading 280, present altitude, auto-resolve approach |
| `JFAC ILS28R` | Join final approach course (intercept and fly the localizer) |
| `APPS` | List available approaches for the aircraft's destination airport |
| `APPS OAK` | List available approaches at a specific airport |

`JFAC` also accepts aliases `JLOC` and `JF`.

**Rich approach forms** — All approach commands (CAPP, JAPP, CAPPSI, JAPPSI, CAPPF, JAPPF) support rich forms that combine navigation with the clearance:

| Command | Effect |
|---------|--------|
| `CAPP AT SUNOL ILS28R` | Navigate to fix SUNOL, then fly the ILS 28R |
| `CAPP DCT SUNOL ILS28R` | Direct to SUNOL, then fly the ILS 28R |
| `CAPP DCT SUNOL CFIX A034 ILS28R` | Direct to SUNOL, cross at or above 3,400, then ILS 28R |
| `JAPP AT SUNOL ILS28R` | Navigate to SUNOL, then join ILS 28R at nearest fix |
| `JAPP DCT SUNOL ILS28R` | Direct to SUNOL, then join ILS 28R |
| `CAPPSI DCT SUNOL ILS28R` | Direct to SUNOL, then cleared straight-in ILS 28R |

**CFIX altitude prefixes:** `A034` = at or above 3,400, `B034` = at or below 3,400, `034` = at 3,400.

**Heading intercept (implied PTAC):** When an aircraft is being vectored (has an assigned heading) and you issue a bare `CAPP` (no AT/DCT fix), the aircraft maintains its present heading and intercepts the final approach course — equivalent to an implied PTAC. If the aircraft has no assigned heading, CAPP navigates through approach fixes as usual. AT/DCT fixes always override heading intercept regardless of assigned heading.

**Intercept angle validation:** Approach clearances validate the intercept angle per 7110.65 §5-9-2 — max 20° within 2nm of the approach gate, max 30° beyond. Use force variants (`CAPPF`/`JAPPF`) to override.

**Hold-in-lieu:** When a procedure includes a hold-in-lieu of procedure turn (e.g., at an IAF), `JAPP` automatically executes one holding circuit before proceeding. Use `CAPPSI`/`JAPPSI` to skip it.

**Expect approach:** `EAPP I28R` tells the pilot to expect the ILS 28R approach. This sets the expected approach on the aircraft state (visible in the data grid) and programs the approach fix names for display. Does not clear the aircraft for the approach.

**Force direct to:** `DCTF` works like `DCT` but bypasses the check that the target fix must be on or ahead of the current route.

**Visual approach:** `CVA 28R` clears the aircraft for a visual approach to runway 28R. No CIFP procedure is required — the aircraft navigates visually. Options:

| Command | Effect |
|---------|--------|
| `CVA 28R` | Cleared visual approach runway 28R |
| `CVA 28R LEFT` | Visual approach with left traffic pattern |
| `CVA 28R RIGHT` | Visual approach with right traffic pattern |
| `CVA 28R FOLLOW AAL123` | Visual approach following AAL123 |

The aircraft execution path depends on its position relative to the runway:
- **Straight-in** (≤30° off final course): flies directly to final approach and landing
- **Angled join** (30°–90° off): navigates to an intercept point, then final
- **Pattern entry** (>90° off): enters downwind → base → final

**Field/traffic in sight:** Aircraft on a visual approach automatically report "field in sight" or "traffic in sight" when detection conditions are met (forward hemisphere, within visibility range, below ceiling). Use `RFIS` (report field in sight) or `RTIS` (report traffic in sight) to get the pilot's current visual status on demand.

**Bank angle affects initial acquisition:** A pilot in a turn cannot initially spot targets hidden by the high wing. For example, during a right turn the raised left wing blocks the view of traffic to the left at or below the aircraft's altitude. Time your traffic calls for when the pilot can actually see the target — not during a turn that blocks the view. Once the pilot has the target in sight, they can track it through subsequent turns.

**Aircraft size affects detection range:** Larger aircraft are visible from farther away. A Super (A388) can be spotted at up to 15nm, while a Small (C172) is only visible at about 3nm. Detection range scales with the target aircraft's FAA wake turbulence group (WTG).

**Approach scoring:** When an aircraft completes an approach (lands or goes around), the terminal shows an approach report evaluating the quality of the approach setup. Scored criteria include intercept angle, glideslope interception altitude, final approach speed, and stabilization. This provides feedback to the controller-in-training. **Scenario > Approach Report** opens a summary window showing all approach reports from the current session.

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

**Route validation** — When **Validate DCT fixes against route** is enabled in Settings > Scenarios, DCT commands to fixes not in the aircraft's filed route or expected approach are rejected. Use `DCTF` (force direct to) to override. Issue `EAPP` (Expect Approach) first to program approach fixes into the aircraft's route for validation purposes.

### Navigation Commands

| Command | Effect |
|---------|--------|
| `DCT SUNOL` | Direct to fix SUNOL |
| `DCT SUNOL CEDES MYCOB` | Direct to multiple fixes in sequence |
| `DCTF SJC` | Force direct to — bypasses route validation |
| `DCTF FIX1/A080 FIX2/050 FIX3` | Force direct to with inline altitude constraints |
| `ADCT SUNOL` | Append direct to — adds SUNOL to the end of the current route |
| `ADCTF SUNOL` | Append force direct to — appends without route validation |
| `JARR OAK.SALI2` | Join STAR: navigate to nearest fix on the SALI2 arrival into OAK |
| `JARR SALI2` | Join STAR by name (airport inferred from destination) |
| `JARR SALI2 KENNO` | Join STAR via specific entry fix KENNO |
| `JARR OAK.SALI2 KENNO` | Join STAR with both airport qualifier and entry fix |
| `JAWY V25` | Join airway: intercept and join airway V25, following fixes in the direction of travel |
| `JRADO SJC 150` | Join radial outbound: fly to SJC VOR, then outbound on the 150° radial |
| `JRADI SJC 150` | Join radial inbound: intercept and fly inbound on the 150° radial to SJC |
| `CFIX A034` | Cross next fix at or above 3,400 ft |
| `CFIX SUNOL A034` | Cross specific fix SUNOL at or above 3,400 ft |
| `DVIA` | Descend via STAR — enables altitude/speed constraint following on active STAR |
| `DVIA 240` | Descend via STAR, except maintain FL240 (altitude floor) |
| `DVIA SPD 180 SUNOL` | Descend via STAR with speed restriction (maintain 180 knots at SUNOL) |
| `CVIA` | Climb via SID — re-enables altitude/speed constraint following on active SID |
| `CVIA 190` | Climb via SID, except maintain FL190 (altitude ceiling) |
| `DEPART SUNOL 270` | Depart fix SUNOL on heading 270 |

`JARR` also accepts aliases `ARR`, `STAR`, and `JSTAR`. `JRADO` also accepts `JRAD`. `JRADI` also accepts `JICRS`. `DEPART` also accepts `DEP`.

`JAWY` intercepts and joins a named airway (e.g., V25, J80). The aircraft flies its present heading until it intercepts the airway segment between the nearest fix behind and ahead, then turns onto the airway course and follows the fix sequence in the direction of travel. This ensures the aircraft is on the airway centerline (for MEA coverage and radio navigation) rather than cutting corners direct to a fix.

JARR supports CIFP altitude/speed constraints when available. The airport prefix (`OAK.`) is optional — when omitted, the aircraft's destination airport is used. The entry fix specifies where to join the STAR; when omitted, the nearest fix ahead of the aircraft is used.

CFIX supports two forms: `CFIX {altitude}` modifies the altitude restriction for the next fix in the route, while `CFIX {fix} {altitude}` targets a specific named fix. Altitude prefixes: `A` = at or above, `B` = at or below, no prefix = at exactly. CFIX now uses step-based descent/climb planning — the aircraft computes the exact vertical rate needed to arrive at the constraint altitude precisely at the fix, rather than descending at a fixed rate.

**Constrained DCTF** — `DCTF FIX1/A080 FIX2/050 FIX3` attaches altitude constraints inline. The `/` suffix uses the same CFIX altitude format (`A` = at or above, `B` = at or below, bare = at). All constraints are visible to the planner simultaneously, so the aircraft plans descent across multiple waypoints at once instead of reacting to each fix individually. The altitude reverts to the previous assignment after the last constrained fix is sequenced.

**SID/STAR via mode** — When CIFP procedure data is available, SID and STAR fixes carry published altitude and speed restrictions. Via mode controls whether the aircraft follows those restrictions:

| Procedure | Default | Enable via mode | Disable via mode |
|-----------|---------|-----------------|------------------|
| SID (after CTO) | **ON** — aircraft follows published climb restrictions | `CVIA` (re-enable after CM override) | `CM` (any altitude command) |
| STAR (after JARR) | **OFF** — aircraft maintains altitude, follows lateral path only | `DVIA` (enable descent restrictions) | `DM` (any altitude command) |

`CVIA 190` and `DVIA 240` enable via mode with an altitude cap/floor — "climb/descend via, except maintain." `FH`, `DCT`, and heading commands clear the entire procedure (lateral path + via mode). When CIFP data is unavailable, JARR and CTO fall back to NavData fix lists (lateral path only, no constraints).

### Speed Management

| Command | Effect |
|---------|--------|
| `SPD 210` | Exact speed: maintain 210 knots |
| `SPD 210+` | Speed floor: maintain 210 knots or greater |
| `SPD 210-` | Speed ceiling: do not exceed 210 knots |
| `RNS` / `NS` | Resume normal speed: clears speed/floor/ceiling, preserves SID/STAR via mode |
| `EXP` | Expedite climb/descent: increases vertical rate (approx 1.5x category rate) |
| `EXP 50` | Expedite through 5,000 ft, then resume normal rate (requires active altitude assignment) |
| `NORM` | Resume normal vertical rate: clears expedite and any custom vertical rate |
| `RFAS` / `FAS` | Reduce to final approach speed: sets speed to per-type approach speed (e.g., B738→144 kts) |
| `MACH .82` / `M .82` | Maintain Mach number (also accepts `0.82` or `82`); IAS adjusts with altitude |
| `DSR` | Delete speed restrictions: clears all speed + suppresses via-mode speed at future waypoints |
| `SPD 210; ATFN 10 SPD 180` | Maintain 210, then at 10nm final slow to 180 |
| `SPD 210 UNTIL 10` | Shorthand: maintain 210 until 10nm final, then cancel |
| `SPD 210 UNTIL 10; SPD 180 UNTIL 5` | Chained: maintain 210, at 10nm final slow to 180, at 5nm final cancel |
| `SPD 180 UNTIL AXMUL` | Fix-based: maintain 180 until reaching AXMUL, then resume normal speed |
| `SPD 180 AXMUL` | ATCTrainer alias for `SPD 180 UNTIL AXMUL` |

**Floor and ceiling** — `SPD 210+` sets a minimum speed; the aircraft accelerates only if below 210 but maintains its current speed if already faster. `SPD 210-` sets a maximum; the aircraft decelerates only if above 210. Both are enforced continuously and respect the 250-knot limit below 10,000 ft. An exact speed command (`SPD 210`) clears any active floor or ceiling.

**ATFN (at final)** — `ATFN {distance}` is a compound-block condition that fires when the aircraft is within the specified distance (in NM) of the assigned runway threshold. Use it to set up staged speed reductions on approach: `SPD 210; ATFN 10 SPD 180; ATFN 5 RNS`.

**SPD UNTIL shorthand** — `SPD 210 UNTIL 10` expands to `SPD 210; ATFN 10 RNS`. When chained, intermediate blocks are generated automatically: `SPD 210 UNTIL 10; SPD 180 UNTIL 5` becomes `SPD 210; ATFN 10 SPD 180; ATFN 5 RNS`. Fix-based UNTIL is also supported: `SPD 180 UNTIL AXMUL` expands to `SPD 180; AT AXMUL RNS`, cancelling the speed restriction when the aircraft reaches the named fix. The ATCTrainer shorthand `SPD 180 AXMUL` (without UNTIL) is equivalent.

**Auto-cancel at 5nm final** — Per 7110.65 §5-7-1, ATC speed assignments (target, floor, ceiling) are automatically cancelled when the aircraft is within 5nm of the runway threshold. New speed commands are rejected inside this boundary.

**DSR interaction** — `DSR` suppresses SID/STAR via-mode speed constraints at waypoints. The aircraft still follows altitude restrictions but ignores published speed restrictions. A new `SPD` command, `CVIA`, or `DVIA` clears the DSR flag and re-engages speed constraint compliance.

### Holding Patterns

| Command | Effect |
|---------|--------|
| `HOLDP SUNOL R 180 1M` | Hold at SUNOL, right turns, 180° inbound, 1-minute legs |
| `HOLDP SUNOL L 090 5` | Hold at SUNOL, left turns, 090° inbound, 5nm legs |
| `HOLDP SUNOL R 180` | Hold at SUNOL, right turns, 180° inbound (default 1-minute legs) |
| `HOLD SUNOL R 180 1M` | Same as HOLDP (parser detects holding pattern from arguments) |

Hold format: `HOLDP {fix} {L/R} {inbound_course} {leg_length}`. Leg length ending in `M` is minutes; plain number is nautical miles. Any RPO command (heading, altitude, approach, etc.) exits the hold.

The explicit verb is `HOLDP`. However, `HOLD` followed by holding pattern arguments (fix name + L/R + course) is automatically recognized as a holding pattern rather than a ground hold-position command.

### Hold Commands

| Command | Effect |
|---------|--------|
| `HPPL` / `HPPR` | Hold present position, left/right 360° orbits |
| `HPP` | Hold present position (hover, for helicopters) |
| `HFIXL {fix}` / `HFIXR {fix}` | Fly to fix, then left/right orbits |
| `HFIX {fix}` | Fly to fix, then hover |

Any heading, altitude, or speed command clears the hold.

### Track Operations

Track operations control aircraft ownership, handoffs, and coordination. These commands use STARS-style TCP codes (e.g., "2B" = subset 2, sector B).

#### Active Position

By default, you operate as the scenario's student position. Use `AS` to act as a different position:

| Command | Effect |
|---------|--------|
| `AS 2B` | Set your active position to TCP 2B (persistent until changed) |
| `N135BS AS 2B HO 3Y` | Act as 2B for this command only (prefix mode) |

Resolution order: per-command `AS` prefix > persistent active position > student position default.

Changing your active position also updates the radar display:
- **DCB map shortcuts** switch to the position's configured 3x2 map group (matching real STARS map group assignments by TCP code).
- **Weather airports** update to the position's STARS area's underlying airports (determines which METARs are relevant for the radar readout area).

#### Track Commands

| Command | Effect |
|---------|--------|
| `TRACK` | Initiate control — take ownership of the aircraft |
| `DROP` | Terminate control — release ownership |
| `HO 3Y` | Handoff to TCP 3Y (must own the aircraft) |
| `HOF 3Y` | Force handoff to TCP 3Y (transfers ownership regardless of current owner) |
| `ACCEPT` / `A` | Accept a pending inbound handoff |
| `CANCEL` | Retract a pending outbound handoff |
| `ACCEPTALL` | Accept all pending inbound handoffs (global — no callsign needed) |
| `HOALL 3Y` | Handoff all your aircraft to TCP 3Y (global — no callsign needed) |
| `PO 3Y` | Point out to TCP 3Y |
| `OK` | Acknowledge a pending pointout |
| `AN 3 RV` / `BOX 3 RV` | Write "RV" in strip annotation box 3 (boxes 1-9) |
| `AN 3` | Clear strip annotation box 3 |
| `STRIP Ground` | Push flight strip to "Ground" bay in vStrips |
| `SP1 OAK` | Set scratchpad 1 |
| `SP2 I8R` | Set scratchpad 2 |
| `TA 120` / `QQ 120` | Set temporary altitude (in hundreds, e.g., 120 = FL120) |
| `CRUISE 240` / `QZ 240` | Set cruise altitude |
| `ONHO` / `ONH` | Toggle on-handoff status |

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

To exempt a specific aircraft from auto-delete, append `NODEL` to `CLAND`, `TAXI`, `EL`, `ER`, or `EXIT` commands (e.g., `CLAND NODEL`, `TAXI S T U @B12 NODEL`, `EL NODEL`, `EXIT A3 NODEL`). This is useful when repositioning aircraft after landing or parking for reuse.

#### Simulation Shortcuts

Two optional shortcuts in **Settings > Scenarios > Simulation Shortcuts** simplify tower operations for trainees:

- **Auto-clear aircraft to land** — Aircraft on final approach are automatically cleared to land without requiring a CLAND command. Go-arounds due to missing landing clearance will not occur. Manual CLAND commands still work when issued. Configured per position type (GND, TWR, APP, CTR). Defaults: GND on, TWR off, APP on, CTR on — so only tower controllers must issue explicit landing clearances by default.
- **Aircraft cross runways automatically** — Taxiing aircraft cross inactive runways without stopping for a CROSS command. Explicit hold-short commands and destination runway hold-shorts still apply.

The auto-clear setting is applied based on the student position type in the loaded scenario. Both settings are synced to the server on scenario load and when settings are saved.

### Consolidation

Consolidation commands manage STARS position consolidation, allowing one controller position to absorb responsibility for another position's airspace.

| Command | Effect |
|---------|--------|
| `CON 1T 1F` | Consolidate positions 1T and 1F (1T absorbs 1F's airspace) |
| `CON+ 1T 1F` | Full consolidation — includes all display settings and map groups |
| `DECON 1F` | Deconsolidate position 1F (restore it as an independent position) |

These are global commands — no aircraft selection is needed. TCP codes follow the same format as track operations (e.g., "2B" = subset 2, sector B).

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
| At parking/helipad | `ADD {rules} {weight} {engine} @{spot}` | `ADD VFR S P @H1 H60` |

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
- **At parking/helipad**: `@{spot}` is a parking or helipad name (e.g., `@H1`, `@B12`). Aircraft spawns at ground level at that spot. Disambiguated from at-fix by the absence of an altitude argument. Useful for helicopters and ground operations.

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
| `PAUSE` / `P` | Pause the simulation |
| `UNPAUSE` / `U` / `UN` / `UNP` / `UP` | Resume the simulation |
| `SIMRATE <n>` | Set simulation speed (1, 2, 4, 8, 16) |
| `ADD ...` | Spawn an aircraft (see above) |
| `SQALL` | Reset all aircraft to their assigned squawk codes |
| `SNALL` | Set all aircraft transponders to mode C (normal) |
| `SSALL` | Set all aircraft transponders to standby |
| `ACCEPTALL` | Accept all pending inbound handoffs |
| `HOALL 3Y` | Handoff all your aircraft to TCP 3Y |
| `RDAUTO <listId>` | Toggle auto-acknowledge for a coordination list |
| `CON` / `CON+` / `DECON` | Consolidation commands (see [Consolidation](#consolidation)) |
| `RFIS` / `RTIS` | Report field/traffic in sight (see [Approach Control Commands](#approach-control-commands)) |

The pause/unpause button and sim rate dropdown in the bottom bar also control these.

### Force Override Commands

When things go wrong and you need to immediately correct an aircraft's state (rather than waiting for flight physics to gradually adjust), use force commands:

| Command | Effect |
|---------|--------|
| `FHN 270` | Force heading: immediately set heading to 270° |
| `CMN 50` | Force altitude: immediately set altitude to 5,000 ft |
| `SPDN 250` | Force speed: immediately set IAS to 250 knots |

These commands set both the aircraft's current state and its targets simultaneously, so there is no gradual transition. Useful for RPO corrections when an aircraft is in the wrong state.

### Warp Commands

Teleport an aircraft to a specific position:

| Command | Effect |
|---------|--------|
| `WARP OAK005002 020 050 120` | Warp to OAK 005°/2nm, heading 020, altitude 5,000 ft, speed 120 kts |
| `WARP SJC 180 100 250` | Warp to SJC fix, heading 180, altitude 10,000 ft, speed 250 kts |
| `WARPG C B` | Warp to the intersection of taxiways C and B (ground aircraft only) |
| `WARPG #42` | Warp to node ID 42 (ground aircraft only; use Ctrl+D debug overlay to find IDs) |
| `WARPG @B12` | Warp to parking spot B12 (ground aircraft only) |

**WARP** accepts a fix name or FRD (Fix-Radial-Distance) as the position, followed by heading (1-360), altitude (shorthand hundreds), and speed (knots). The aircraft is placed airborne at the specified position.

**WARPG** accepts either two taxiway names (finds their intersection) or a node ID reference (`!{id}`). The aircraft must have a loaded ground layout (i.e., be at an airport). Use the Ground View debug overlay (Ctrl+D) to find node IDs.

### Say Command

Make an aircraft broadcast a message (simulating pilot readback or request):

| Command | Effect |
|---------|--------|
| `SAY REQUEST VFR TRANSITION` | Aircraft broadcasts "REQUEST VFR TRANSITION" |
| `SAY UNABLE` | Aircraft broadcasts "UNABLE" |

The message text is broadcast verbatim as a radio transmission from the aircraft. Aliases: `SAY`.

### Say Speed

Make an aircraft report its current indicated airspeed:

| Command | Effect |
|---------|--------|
| `SSPD` | Aircraft broadcasts its current speed (includes Mach at/above FL240) |

Aliases: `SSPD`.

### Say Mach

Make an aircraft report its current Mach number:

| Command | Effect |
|---------|--------|
| `SMACH` | Aircraft broadcasts its current Mach number |

Aliases: `SMACH`.

### Say Expected Approach

Make an aircraft report its expected approach:

| Command | Effect |
|---------|--------|
| `SEAPP` | Aircraft broadcasts its expected approach (or "No expected approach assigned") |

Aliases: `SEAPP`.

### Say Altitude

Make an aircraft report its current altitude:

| Command | Effect |
|---------|--------|
| `SALT` | Aircraft broadcasts its altitude and vertical trend (e.g., "12500 ft, climbing to 17000 ft") |

Reports present altitude. If a target altitude is assigned, also reports climbing/descending to target, or "level" if at target. Aliases: `SALT`.

### Say Heading

Make an aircraft report its current heading:

| Command | Effect |
|---------|--------|
| `SHDG` | Aircraft broadcasts its heading (includes direct-to fix if navigating a route) |

Reports current heading as three digits. If the aircraft is navigating to a fix, also reports which fix (e.g., "Heading 270, direct SUNOL"). Aliases: `SHDG`.

### Say Position

Make an aircraft report its position:

| Command | Effect |
|---------|--------|
| `SPOS` | Aircraft broadcasts its position relative to the nearest fix |

Reports position as natural language relative to the nearest navigation fix (e.g., "15 nm northeast of OAK" or "Over SUNOL"). Falls back to lat/lon if no fix is nearby. Aliases: `SPOS`.

## Simulation Controls

At the bottom-right of the window:

- **Pause/Resume** button — toggle simulation pause
- **Sim rate dropdown** — speed up the simulation (1x, 2x, 4x, 8x, 16x)

Pause and sim rate are scoped to your room — they don't affect other rooms.

### Timeline / Rewind

When a scenario is loaded, a timeline bar appears below the menu. It shows elapsed time and provides rewind controls:

- **|◀** — rewind to the start of the scenario
- **-30s / -15s** — rewind 30 or 15 seconds back from current time
- **Elapsed time** — displayed in mm:ss format

After rewinding, the simulation enters **Playback Mode**. The timeline bar shows "PLAYBACK" and a "Take Control" button. In playback mode:

- The simulation replays all previously recorded commands at their original timestamps
- Terminal entries broadcast normally — you can watch commands execute as they happened
- The simulation auto-pauses when it reaches the end of the recorded tape
- Press **Take Control** or issue any command to exit playback and resume live operation (the future recording is discarded)

### Save / Load Recordings

Under the **Scenario** menu:

- **Save Recording...** — exports the current session (scenario + all recorded actions) to a `.yaat-recording.json` file
- **Load Recording...** — loads a previously saved recording; enters playback mode at t=0

Recordings are self-contained JSON files that include the scenario definition, RNG seed, weather state, and all user actions with timestamps. They can be shared between users for review or training.

## Aircraft Assignments

When multiple instructors/RPOs are in the same room, aircraft can be assigned to specific members for sole control.

**Context menu:** Right-click an aircraft in the Aircraft List, Ground View, or Radar View to see the RPO control submenu:
- **Take control** — assign the aircraft to yourself (hidden if already yours)
- **Give up control** — unassign the aircraft (shown only if assigned to you)
- **Give control > [initials]** — assign the aircraft to another room member
- **Unassign** — remove any assignment (shown if assigned to someone else)

In the Aircraft List, multi-select works: select multiple aircraft, right-click, and the RPO actions apply to all selected.

**Keybind:** Press **Ctrl+T** (configurable in Settings > Advanced) with an aircraft selected to take control of it instantly.

**Terminal commands:**
- `TAKE` — assign the selected (or callsign-prefixed) aircraft to yourself
- `GIVE XX` — assign the selected aircraft to the member with initials XX
- `GIVEUP` — unassign the selected aircraft

All three support callsign prefix: `AAL123 TAKE`, `AAL123 GIVE AB`, `AAL123 GIVEUP`.

**Enforcement:** Once any assignment exists in the room, commands to aircraft assigned to someone else are rejected. The error message shows who the aircraft is assigned to.

**Override:** Prefix your command with `** ` (double asterisk + space) to bypass the assignment check. The terminal shows the override: `JE (override): AAL100 FH 270`.

**Visual indicators:**
- The **Ctrl** column in the Aircraft List shows the assigned controller's initials
- The radar datablock shows `[INITIALS]` on line 3 when an aircraft is assigned
- Optional color tint: in **Settings > Advanced > Radar Display**, enable **Tint my assigned aircraft** to render your assigned targets and datablocks in a custom color (default green `#00FF00`). Enter any hex color code.

Assignments are cleared when a member leaves the room, when an aircraft is deleted, or when the scenario is unloaded.

## Views

The main window uses a tabbed layout with three views: **Aircraft List**, **Ground View**, and **Radar View**. The terminal panel sits below the tab area.

### Tabs and Pop-Out

Each view can be popped out into its own window via **View > Pop Out Aircraft List / Ground View / Radar View**. When a view is popped out, its tab disappears from the main window and a separate window opens. Close the pop-out window (or uncheck the menu item) to dock it back as a tab.

All three views can be popped out simultaneously. Pop-out state and window positions are remembered across sessions.

### Aircraft List

The default tab. Shows the aircraft data grid described above.

**Zoom:** Use **Ctrl+Plus** / **Ctrl+Minus** to increase or decrease the font size while the aircraft list is focused. **Ctrl+0** resets to the default size (12pt). The font size can also be set in **Settings > Display**. Range: 8-24pt. The setting persists across sessions.

### Flight Plan Editor

Double-click an aircraft row in the Aircraft List to open its Flight Plan Editor (FPE). You can also **Ctrl+Left-Click** an aircraft symbol or datablock in the Ground View or Radar View.

The FPE shows editable flight plan fields: beacon code, aircraft type, equipment suffix, departure, destination, cruise speed, altitude, route, and remarks. The callsign is displayed but not editable. The ALT field accepts 3-digit altitude codes (e.g., `035` for 3,500ft) or prefixed formats: `VFR`, `VFR/035`, `OTP/035`.

Edit any field, then click **Amend** to send the changes to the server. The Amend button is only enabled when at least one field differs from the current flight plan. The FPE window stays on top of the main window but does not block interaction with it.

### Ground View

An interactive airport surface map showing taxiways, runways, and aircraft positions. Useful for tower operations (taxi, hold short, cross runway).

- **Pan**: right-click and drag
- **Zoom**: mouse wheel (hold **Ctrl** for fine zoom)
- **Rotate**: Shift + mouse wheel (1° per notch)
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

**Draw taxi route mode:** Right-click a node or aircraft and select "Draw taxi route..." to enter draw mode. Click nodes to add waypoints — the route is computed via A* between consecutive waypoints. As you hover over nodes, a dashed preview shows what the next segment would look like. Right-click a node to finish. Backspace undoes the last waypoint, Escape cancels. The resulting command uses node ID references (`!nodeId` tokens) instead of taxiway names, guaranteeing the aircraft follows the exact drawn path. The terminal displays the human-readable taxiway names.

**Debug overlay:** Press **Ctrl+D** to toggle a debug overlay that shows node IDs, names, types, and edge labels on the ground map. Use this to find specific node IDs for manual `#nodeId` taxi commands (e.g., `TAXI #42 #18 #95`). Node references can be mixed freely with taxiway names (e.g., `TAXI A #42 B`). This is useful when automatic taxiway-name resolution picks the wrong path and you need precise control over a specific junction or segment.

The ground layout loads automatically when a scenario is loaded for an airport with ground data.

**Controls bar:** A bar in the top-right corner provides label filters and a lock toggle:
- **Filters** — **RWY** and **TWY** toggle labels on/off. **HS**, **PARK**, and **SPOT** are tri-state: click to cycle through labels+icons (bright) → icons only (medium) → hidden (dim). When a category is hidden, hovering over the relevant element temporarily shows its icon and label.
- **LOCK / UNLK** — lock or unlock pan, zoom, and rotation. When locked, the map cannot be accidentally moved. Defaults to unlocked.

**Per-scenario persistence** — Ground view settings (pan position, zoom, rotation, label filters, lock state) are saved independently for each scenario. When you load a scenario you've used before, the ground view restores your previous settings. Label filter and lock defaults for new scenarios come from your last-used values.

When weather is loaded, wind direction/speed and altimeter setting are displayed in the top-left corner of the ground view.

### Radar View

A simplified STARS-style radar display showing aircraft targets, video maps, and navigation fixes. Useful for approach/departure operations.

- **Pan**: right-click and drag
- **Zoom**: mouse wheel (hold **Ctrl** for fine zoom)
- **Select aircraft**: click a target on the display (syncs with the grid selection)

**DCB bar** (top of the radar view):
- **RNG +/-**: increase/decrease display range
- **MAP**: open map selection popup to toggle individual video maps
- **Map shortcuts**: up to 6 quick-toggle buttons for frequently used map groups
- **RR**: toggle range rings; **RR SIZE** / **RR POS** adjust ring radius and center position
- **FIX**: toggle fix name overlay
- **LOCK**: lock/unlock pan and zoom (prevents accidental map movement). Defaults to unlocked for new scenarios
- **TOP-DN**: toggle top-down display mode
- **PTL**: predicted track lines — adjust length (in minutes) with the spinner; **PTL OWN** shows lines for your tracked aircraft, **PTL ALL** shows lines for all aircraft
- **BRITE**: open brightness controls to adjust intensity per video map category
- Range display shows current range in NM

**Right-click context menus:**
- **On an aircraft**: Heading (fly/present/turn left/turn right), Altitude (common values), Speed, Approach (ILS/RNAV/VIS per runway), Track operations, Delete
- **On the map** (with aircraft selected): "Fly heading" (computed bearing to click point), "Direct to" (nearest fix within 5nm)

When weather is loaded, wind and altimeter are displayed in the top-left corner of the radar view in STARS green.

**Datablocks** show callsign, altitude, ground speed, and owner. When SP1 or SP2 is set, an additional line displays the scratchpad values.

Video maps load automatically from the vNAS data API based on your ARTCC ID. Map lines render in green with brightness categories A/B.

**Per-scenario persistence** — Radar view settings (enabled maps, center position, zoom range, range rings, PTL, brightness, lock state) are saved independently for each scenario. When you load a scenario you've used before, the radar view restores your previous settings for that scenario.

### Copying View Settings

Use **View > Copy View Settings From...** to apply another scenario's Ground and Radar view settings to the current scenario. The submenu lists all scenarios with saved settings (excluding the current one). Selecting an entry copies pan, zoom, rotation, label filters, lock state (Ground) and center, range, maps, range rings, brightness, PTL, lock state (Radar) to the active scenario.

## Terminal Panel

The terminal panel sits below the tab area. It shows a scrolling history of all commands and server feedback for your scenario, visible to all connected RPOs.

### Entry Format

Each line shows:
```
HH:MM:SS  CMD  AB  UAL123  FH 270
```
- **Timestamp** — when the entry was received
- **Kind tag** — entry category matching the filter buttons (CMD, RSP, SYS, SAY, WRN, ERR, CHAT)
- **Initials** — who issued the command (2-letter user initials)
- **Callsign** — which aircraft was targeted
- **Message** (colored by type):
  - **White** — command echo (CMD)
  - **Light gray** — server response/feedback (RSP)
  - **Gray** — system message (SYS)
  - **Green** — SAY (instructor messages)
  - **Orange** — warnings (WRN)
  - **Red** — errors (ERR)
  - **Cyan** — chat messages (CHAT)

### Filters

The terminal header includes toggle buttons to filter entries by kind: **CMD** (commands), **RSP** (responses), **SYS** (system), **SAY** (chat), **WRN** (warnings), **ERR** (errors). Click a toggle to hide/show that kind. Hidden entries remain in the backing store — toggling a filter back on restores all entries of that kind. All entries are always written to the client log file regardless of filter state. Filter state persists across sessions.

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

Before students can connect, CRC must be configured to point at your YAAT server — see [Connecting CRC](#connecting-crc-optional) above.

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

Open **Settings** to configure:

- **Identity** — VATSIM CID, user initials (required before connecting), and ARTCC ID
- **Scenarios** — Auto-accept handoff settings (enable/disable + delay in seconds), auto-delete aircraft override (Use Scenario Setting / Never / On Landing / On Parking), simulation shortcuts (auto-clear to land, auto-cross runways), and validate DCT fixes against route (rejects DCT to off-route fixes; use DCTF to override)
- **Commands** — Alias editor for customizing command verbs. Each command shows its primary name, editable aliases, and an example. Use **Reset to Defaults** to restore the built-in aliases.
- **Macros** — Define reusable command shortcuts (see [Macros](#macros) below)
- **Advanced** — Aircraft select keybind (default: Numpad +), focus command input keybind (default: `~`), take control keybind (default: Ctrl+T), and server admin mode

## Autocomplete

As you type in the command bar, a popup appears with matching suggestions:

- **Command verbs** — matching verbs from your active command scheme with syntax hints (e.g., `FH  Fly Heading {270}`)
- **Callsigns** — aircraft whose callsign matches what you've typed, showing type and route
- **Command arguments** — after typing a verb + space, context-specific options appear:
  - **CTO modifiers** — departure instructions vary by flight rules. IFR: `RH` and heading prefixes (`H`, `RH`, `LH`). VFR: all modifiers including `OC`, `MRC`, `MRD`, `MR270`, `MLC`, `MLD`, `ML270`, `MLT`, `MRT`, `DCT`.
  - **Runway designators** — for `ELD`, `ERD`, `EF`, `ELB`, `ERB`, `CROSS`, `CLAND`, `LAHSO`, `CVA`: shows runways from the primary airport
  - **Fix names** — for `DCT`, `DCTF`, `ADCTF`, `HFIXL`, `HFIXR`, `HFIX`, `CFIX`, `DEPART`, `JFAC`, and `AT` conditions: route fixes + navdata fixes
- **Macros** (yellow) — when typing `!`, matching macro names with parameter hints (e.g., `!HC &1 &2` or `!FC &hdg &alt`)
- After accepting a callsign, the popup immediately shows all available command verbs

Suggestions are context-aware: after a `;` or `,` separator in compound commands, suggestions reset for the new command. Conditions (`LV`, `AT`) are also suggested. When the input starts with a callsign, suggestions use that aircraft's data (route fixes, flight rules) rather than the grid-selected aircraft.

### Fix Suggestion Priority

When typing a fix argument (after `DCT`, `HFIX`, `CFIX`, `DEPART`, etc. or `AT`), suggestions use two tiers:

1. **Route fixes** (teal) — fixes from the target aircraft's flight plan: departure, destination, filed route fixes, and all fixes from expanded SIDs/STARs. These are fixes "in the FMS" that the pilot has programmed.
2. **Navdata fixes** (white) — all other navdata fixes matching your prefix.

If no aircraft is targeted, only navdata fixes are shown. Route fixes always appear first.

## Macros

Macros let you define reusable command shortcuts. A macro maps a `!NAME` to a command expansion, optionally with positional parameters.

### Defining Macros

Open **Settings > Macros** to create, edit, and manage macros. Each macro has:

- **Name** — alphanumeric identifier (e.g., `BAYTOUR`, `HC`). The `!` prefix is added automatically when you type it.
- **Expansion** — the command(s) to expand to. Can include parameter placeholders.

### Parameters

Macros support two parameter styles:

| Style | Expansion | Invocation | Result |
|-------|-----------|------------|--------|
| Positional | `FH &1, CM &2` | `!HC 270 5000` | `FH 270, CM 5000` |
| Named | `FH &hdg, CM &alt` | `!FC 270 5000` | `FH 270, CM 5000` |

Named parameters serve as documentation — the autocomplete popup shows `!FC &hdg &alt` instead of `!FC &1 &2`, making it clear what each argument means. Arguments are always supplied positionally (in the order they first appear in the expansion).

### Usage

Type `!` followed by the macro name in the command bar:

- `!BAYTOUR` → expands to `DCT VPCOL VPCHA VPMID`
- `!HC 270 5000` → expands to `FH 270, CM 5000`
- `!HC 270 5000; DCT SUNOL` → macro + compound: `FH 270, CM 5000; DCT SUNOL`

Macros work anywhere in a compound command (after `;` or `,` separators). The command history records the original macro text, not the expansion.

### Import / Export

- **Export All** / **Export Selected** — save macros to a `.yaat-macros.json` file
- **Import** — load macros from a file, with a selection dialog showing which macros will overwrite existing ones

## Favorite Commands

The favorites bar sits below the command input and provides quick-access buttons for frequently used commands.

- **Click** a favorite button to execute the command immediately. If text is already in the command input (e.g., a callsign), the favorite command is appended to it.
- **Ctrl+Click** a favorite button to append its command text to the current input without sending (joined with `,`).
- **Right-click** a favorite button to edit its label, command text, or delete it.
- Click the **+** button at the end of the bar to add a new favorite.

Each favorite has a **label** (displayed on the button) and **command text** (the command to execute). Favorites can optionally be **scenario-specific** — check "Scenario-specific" when adding/editing to make the favorite visible only when that scenario is loaded. Global favorites (not scenario-specific) are always visible.

Favorites are saved in your preferences and persist across sessions.

## Command History

The command bar remembers your last 50 commands. Navigate with Up/Down arrows:

- **Up** — recall the previous command
- **Down** — move forward through history (or restore what you were typing)
- If you type something first, only history entries starting with that text are shown

## Keyboard Shortcuts

> **macOS:** Substitute **⌘ (Cmd)** for **Ctrl** in all shortcuts below.

| Key | Action |
|-----|--------|
| Enter | Send command (dismisses suggestions if open) |
| Tab | Accept the highlighted suggestion (or first if none highlighted) |
| Up | Navigate suggestions or recall older history |
| Down | Navigate suggestions or recall newer history |
| Escape | Dismiss suggestions / deselect aircraft / close dialog |
| Numpad + | Select aircraft matching typed callsign (configurable in Settings > Advanced) |
| ~ (tilde) | Focus the command input from anywhere in the app (configurable in Settings > Advanced) |
| Ctrl+T | Take control of the selected aircraft (RPO assign to self, configurable in Settings > Advanced) |

## Window State

YAAT remembers window size and position for the main window and all pop-out windows (Aircraft List, Ground View, Radar View, Terminal) across sessions. Pop-out state (which views are in separate windows vs. tabbed) is also persisted.
