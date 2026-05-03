# YAAT User Guide

YAAT (Yet Another ATC Trainer) is an instructor/[RPO](#glossary) desktop client for air traffic control training. It works alongside [CRC](#glossary) (the [VATSIM](#glossary) radar client) — you control simulated aircraft in YAAT while students view them on CRC's radar display.

**New to YAAT?** Start with the [Getting Started guide](GETTING_STARTED.md) for installation, first connection, and your first scenario.

## Table of Contents

- [Interface Overview](#interface-overview)
  - [Main Window](#main-window)
  - [Tabs and Pop-Out](#tabs-and-pop-out)
  - [Terminal Panel](#terminal-panel)
  - [Command Bar](#command-bar)
  - [Keyboard Shortcuts](#keyboard-shortcuts)
- [Views](#views)
  - [Aircraft List](#aircraft-list)
  - [Ground View](#ground-view)
  - [Radar View](#radar-view)
  - [Flight Strips](#flight-strips)
  - [Flight Plan Editor](#flight-plan-editor)
  - [Copying View Settings](#copying-view-settings)
- [Scenarios and Weather](#scenarios-and-weather)
  - [Loading a Scenario](#loading-a-scenario)
  - [Unloading a Scenario](#unloading-a-scenario)
  - [Loading a Weather Profile](#loading-a-weather-profile)
  - [Loading Live Weather](#loading-live-weather)
  - [Weather Editor](#weather-editor)
  - [Weather Timelines (V2 Format)](#weather-timelines-v2-format)
- [Commands](#commands)
- [Simulation Controls](#simulation-controls)
  - [Timeline / Rewind](#timeline--rewind)
  - [Save / Load Recordings](#save--load-recordings)
- [Multi-User Features](#multi-user-features)
  - [Aircraft Assignments](#aircraft-assignments)
  - [Room Members](#room-members)
  - [Students Panel](#students-panel)
- [Connecting CRC for Students](#connecting-crc-for-students)
- [Customization](#customization)
  - [Autocomplete](#autocomplete)
  - [Macros](#macros)
  - [Favorite Commands](#favorite-commands)
  - [Command History](#command-history)
  - [Settings](#settings)
  - [Window State](#window-state)
- [Glossary](#glossary)

---

## Interface Overview

### Main Window

The YAAT window has three areas:

1. **Tab area** (top) — three views: Aircraft List, Ground View, Radar View
2. **Terminal panel** (bottom) — scrolling log of commands, responses, warnings, and errors
3. **Command bar** (bottom edge) — where you type commands

The menu bar provides access to File (connect/disconnect), Scenario (load/unload/weather), Room (members/students), View (pop-out windows), and Settings.

### Tabs and Pop-Out

Each view can be popped out into its own window via **View > Pop Out Aircraft List / Ground View / Radar View**. When a view is popped out, its tab disappears from the main window and a separate window opens. Close the pop-out window (or uncheck the menu item) to dock it back as a tab.

All three views can be popped out simultaneously. Pop-out state and window positions are remembered across sessions.

**Always on Top:** Press **Ctrl+Shift+T** (configurable in Settings > Advanced) while a pop-out window is focused to pin it above all other windows. You can also toggle this per window in Settings > Display > Windows.

### Terminal Panel

The terminal panel shows a scrolling history of all commands and server feedback, visible to all connected [RPOs](#glossary).

#### Entry Format

Each line shows:
```
HH:MM:SS  CMD  AB  UAL123  FH 270
```
- **Timestamp** — when the entry was received
- **Kind tag** — entry category (CMD, RSP, SYS, SAY, WRN, ERR, CHAT)
- **Initials** — who issued the command
- **Callsign** — which aircraft was targeted
- **Message** (colored by type):
  - **White** — command echo (CMD)
  - **Light gray** — server response/feedback (RSP)
  - **Gray** — system message (SYS)
  - **Green** — SAY (instructor messages)
  - **Orange** — warnings (WRN)
  - **Red** — errors (ERR)
  - **Cyan** — chat messages (CHAT)

#### Filters

The terminal header includes toggle buttons to filter entries by kind: **CMD**, **RSP**, **SYS**, **SAY**, **WRN**, **ERR**, **CHAT**. Click a toggle to hide/show that kind. Hidden entries remain in the backing store — toggling a filter back on restores all entries. All entries are always written to the client log file regardless of filter state. Filter state persists across sessions.

#### Multi-User Visibility

All RPOs connected to the same scenario see each other's commands in their terminal.

#### Chat Messages

Type a message prefixed with `'`, `/`, or `>` to send a text chat to all RPOs in your scenario:

```
'Switching to RNAV approach
>Ready for next aircraft
```

#### Resizing and Pop Out

Drag the splitter bar between the aircraft grid and terminal panel to resize them. Click the **Pop Out** button in the terminal header to undock the terminal into a separate floating window. The command input bar moves to the terminal window. Click **Dock** (or close the window) to return it to the main window.

#### Warnings

Warning messages appear when the simulator detects potential issues:

- **Missed FRD condition**: `Missed condition at SUNOL R090 D020 (closest: 2.3 NM)` — an aircraft passed through an FRD trigger point without getting close enough.
- **Illegal approach intercept**: `Illegal intercept: turned on final 5.2nm from threshold (min 9.0nm) [7110.65 §5-9-1]` — an aircraft was vectored onto final closer than the minimum intercept distance.

### Command Bar

The command bar at the bottom is where you type and send commands. See [Commands](#commands) for details.

### Keyboard Shortcuts

> **macOS:** Substitute **⌘ (Cmd)** for **Ctrl** in all shortcuts below.

| Key | Action |
|-----|--------|
| Enter | Send command. If a suggestion is highlighted, expand it first (toggle in **Settings > Advanced > Command Input**) |
| Tab | Accept the highlighted suggestion (or first if none highlighted) |
| Up | Navigate suggestions or recall older history |
| Down | Navigate suggestions or recall newer history |
| Escape | Dismiss suggestions / deselect aircraft / close dialog |
| Numpad + | Select aircraft matching typed callsign (configurable in Settings > Advanced) |
| ~ (tilde) | Focus the command input from anywhere in the app (configurable in Settings > Advanced) |
| Ctrl+T | Take control of the selected aircraft (configurable in Settings > Advanced) |

---

## Views

### Aircraft List

The default view. Shows all aircraft in your scenario, grouped into **Active** and **Delayed** sections. Click the group header row to collapse or expand each section. Use **View > Reset Aircraft List Layout** to restore defaults.

| Column | Description |
|--------|-------------|
| Callsign | Aircraft callsign (e.g., UAL123) |
| Info | Smart status — contextual summary (see [Info Column](#info-column)) |
| Status | Spawn status (Active, Delayed, etc.) |
| Type | Aircraft type code (e.g., B738/L) |
| Rules | Flight rules ([IFR](#glossary) / [VFR](#glossary)) |
| Dep / Dest | Departure and destination airports |
| Route | Filed route |
| P.Alt | Planned cruise altitude (e.g., 035, VFR, VFR/035, OTP/035) |
| Remarks | Flight plan remarks |
| Clnc | Clearance shorthand (e.g., CL = cleared to land, TG = touch and go) |
| Apch | Active approach ID (e.g., ILS28R) |
| EApch | Expected approach |
| Squawk | Assigned transponder code |
| Hdg | Current heading |
| Alt | Current altitude (ft) |
| IAS | Indicated airspeed (kts) |
| Mach | Mach number (shown when applicable) |
| GS | Ground speed (kts) |
| Wind | Wind at aircraft altitude (direction/speed) |
| VS | Vertical speed (fpm) |
| RPO | Assigned controller initials (see [Aircraft Assignments](#aircraft-assignments)) |
| Owner | Track owner (sector code, e.g., "2B", or callsign) |
| HO | Pending handoff target |
| SP1 | Scratchpad 1 text |
| SP2 | Scratchpad 2 text |
| TA | Temporary altitude assignment |
| Phase | Current phase name (e.g., Downwind, FinalApproach, TakeoffRoll) |
| Rwy | Assigned runway |
| AHdg / AAlt / ASpd | Assigned targets — AHdg shows the next fix name when navigating, or heading when under vectors |
| Dist | Distance in NM from the reference fix (see [Distance Column](#distance-column)) |
| Pending Cmds | Numbered pending command blocks. Shows `[Active]` for the current block, then `[1]`, `[2]`, etc. for queued blocks |

Click an aircraft row to select it, or type a callsign in the command input and press the aircraft select key. Press **Esc** to deselect. Click a column header to sort; click again to reverse.

Drag column headers to rearrange. **Right-click any column header** to open the Column Chooser, where you can show/hide columns and reorder them. Click **Reset to Defaults** to restore the original layout.

**Show only active aircraft:** Use the checkbox in the Column Chooser to hide delayed aircraft. Column order, widths, visibility, sort state, and the active-only filter are remembered across sessions.

**Import/Export layouts:** Use the **Export...** and **Import...** buttons in the Column Chooser to share grid layouts. Different training levels may benefit from different layouts — export one for each level and swap as needed.

**Zoom:** Use **Ctrl+Plus** / **Ctrl+Minus** to adjust font size. **Ctrl+0** resets to default (12pt). Also configurable in **Settings > Display**. Range: 8-24pt.

#### Info Column

The **Info** column shows a contextual summary of each aircraft's state.

**Color coding:**
- **White** — normal status (phase description, taxi route, navigation info)
- **Gold/amber** — warning that needs attention (pending handoff, no altitude assignment)
- **Red** — critical alert requiring immediate action (on final or landing without clearance)

**Alert conditions** (highest priority first):

| Condition | Text | Color |
|-----------|------|-------|
| On final approach without landing clearance | "No landing clnc" | Red |
| Landing without clearance | "Landing — no clnc!" | Red |
| Handoff in progress | "HO → {sector}" | Gold |
| Airborne, no phase/SID/STAR, no altitude assignment, no nav route | "No altitude asgn" | Gold |

**Phase-based status** (white, when no alerts apply): Describes the current phase — e.g., "Taxi to RWY 28R via A B C", "LUAW 28R", "Departing 28R, OAK5", "ILS28R → CEPIN DUMBA AXMUL", "Left downwind 28R", "Landing 28R".

**No-phase fallback** (white, when no phase is active): Shows climb/descent arrows with altitude ("↑ FL350", "↓ 5,000"), navigation route ("→ OAK SFO LAX"), or "On ground" / "FL350, on course".

If the aircraft has an assigned heading, ", hdg {heading}" is appended to the Info text.

The Info column is searchable — type status text in the search box to filter aircraft.

#### Aircraft Detail Panel

Selecting an aircraft row expands a detail panel below it:

| Section | Shown when | Content |
|---------|------------|---------|
| Phases | Aircraft is tower-managed | Phase sequence with active phase in brackets |
| Pattern | Aircraft is in the pattern | Traffic direction (Left/Right traffic) |
| Clearance | A clearance has been issued | Clearance type and runway |
| FP | Aircraft has a flight plan | Filed route and cruise altitude |
| Remarks | Flight plan has remarks | Flight plan remarks text |
| Nav | Aircraft has a navigation route | Remaining waypoints in the route |
| Pending | Queued command blocks exist | Numbered pending commands |

Sections with no data are hidden.

#### Distance Column

The **Dist** column shows distance in nautical miles from a reference fix. Defaults to the scenario's primary airport.

To change the reference fix, **middle-click** the "Dist" column header. A flyout appears where you can type a fix name or [FRD](COMMANDS.md#fix-radial-distance-frd). Press **Enter** or click a suggestion to apply, **Escape** to cancel.

### Ground View

An interactive airport surface map showing taxiways, runways, and aircraft positions. Useful for tower operations.

- **Pan**: right-click and drag
- **Zoom**: mouse wheel (hold **Ctrl** for fine zoom)
- **Rotate**: Shift + mouse wheel (1° per notch)
- **Select aircraft**: click an aircraft triangle on the map

**Right-click context menus:**
- **On a node** (with aircraft selected): up to 4 route options ("Taxi via T U W") computed via K-shortest paths. Routes that cross runways automatically append crossing commands. Also: "Push to {spot}" (parking nodes), "Draw taxi route...", "Custom taxi...", and "Warp here".
- **On an aircraft** — items vary by phase:
  - *At Parking*: "Push back" (default), "Push back, face {taxiway}" per connected edge
  - *Pushback / Taxiing / Following*: "Hold position"
  - *Taxiing*: "Hold short of..." submenu listing intersecting runways and taxiways
  - *Holding Short*: "Resume taxi", "Cross {rwy}", "Line up and wait", "Cleared for takeoff", plus nearby runway crossings
  - *Holding After Exit*: "Resume taxi"
  - *Lined Up*: "Cleared for takeoff", "Cancel takeoff clearance"
  - *Takeoff*: "Cancel takeoff clearance"
  - *Final Approach*: "Cleared to land", "Touch and go", "Stop and go", "Low approach", "Cleared for the option", "Go around", "Cancel landing clearance"
  - *Landing*: "Exit left", "Exit right"
  - All phases include "Delete"
- **On empty space**: left-click to deselect the current aircraft

**Draw taxi route mode:** Right-click a node or aircraft and select "Draw taxi route..." to enter draw mode. Click nodes to add waypoints — the route is computed via A* between consecutive waypoints. Hover shows a dashed preview. Right-click to finish. Backspace undoes the last waypoint, Escape cancels.

**Debug overlay:** Press **Ctrl+D** to toggle node IDs, names, types, and edge labels on the ground map. Useful for finding node IDs for manual `#nodeId` taxi commands.

**Controls bar** (top-right corner):
- **Layer toggles** — **SAT** (satellite background image), **MAP** (video map overlay), **GND** (YAAT ground layout)
- **Label filters** — **RWY** and **TWY** toggle labels on/off. **HS**, **PARK**, and **SPOT** are tri-state: labels+icons → icons only → hidden. Hovering over a hidden element temporarily shows it.
- **RESET** — reset view to fit the airport
- **LOCK / UNLK** — lock or unlock pan, zoom, and rotation.

**Per-scenario persistence** — Ground view settings (pan, zoom, rotation, label filters, lock) are saved independently for each scenario.

When weather is loaded, wind direction/speed and altimeter setting are displayed in the top-left corner.

### Radar View

A simplified [STARS](#glossary)-style radar display showing aircraft targets, video maps, and navigation fixes. Useful for approach/departure operations.

- **Pan**: right-click and drag
- **Zoom**: mouse wheel (hold **Ctrl** for fine zoom)
- **Select aircraft**: click a target on the display

**DCB bar** (top of the radar view):
- **RNG +/-**: increase/decrease display range
- **MAP**: toggle individual video maps
- **Map shortcuts**: up to 6 quick-toggle buttons for frequently used map groups
- **RR**: range ring size spinner; **PLACE RR** positions the center, **RR CNTR** resets to center
- **FIX**: toggle fix name overlay
- **LOCK**: lock/unlock pan and zoom
- **TOP-DN**: toggle top-down display mode
- **BRITE**: adjust brightness per video map category
- **SHIFT**: switch to the AUX menu (second DCB page)
- Range display shows current range in NM

**AUX menu** (press SHIFT in the DCB bar):
- **HISTORY**: history trail dots — set count (0-10) showing past radar returns at ~5-second intervals; brightness via **HST** in BRITE menu
- **PTL**: predicted track lines — adjust length (in minutes); **PTL OWN** shows lines for your tracked aircraft, **PTL ALL** shows lines for all aircraft

**Right-click context menus:**
- **On an aircraft**: Heading, Altitude, Speed, Approach, Track operations, Delete
- **On the map**: always shows [FRD](COMMANDS.md#fix-radial-distance-frd) header (nearest fix + radial + distance) and "Copy FRD". With aircraft selected, also: "Fly heading {hdg}", "Direct to {FRD}", "Append direct to {FRD}", "Hold at {FRD} (left/right)", "Warp here ({FRD})"

**Datablocks** show three lines: (1) callsign (with `*` suffix for VFR), (2) altitude in hundreds + ground speed in tens + aircraft type/weight category, (3) RPO assignment (in brackets), track owner TCP, handoff indicator, and scratchpads when set.

#### EuroScope-Style Interactive Tags

Enable **Settings > Display > Radar Display > "EuroScope-style interactive tags"** to switch the radar tag layout to a EuroScope pseudopilot-style block where individual fields are clickable. The setting is global and off by default. With it on, every aircraft data block has four lines:

```
{*} CALLSIGN                 ← owner marker + callsign
TYPE/CWT  DEST               ← aircraft type / weight category, destination
080 (120) ASP(180) AHDG(270) ← current alt, assigned alt, assigned spd, assigned hdg
RWY28L .SCRA +SCRB           ← assigned runway + scratchpad 1/2 + handoff target
```

Empty assigned fields show their identifier (`ASP`, `AHDG`, `(---)`) so the click target is always present.

| Click on… | Opens |
|---|---|
| Current or assigned **altitude** field | Altitude flyout (FL010..FL300 in 1000-ft steps). Selection dispatches `CM` or `DM` based on whether the picked FL is above or below the aircraft. |
| Current or assigned **speed** / `ASP` | Speed flyout (80..350 kt + Resume Normal Speed). Dispatches `SPD` or `RNS`. |
| **AHDG** field | Enters **heading mode** — see below. |
| Assigned **runway** field | Runway flyout listing every runway end at the aircraft's departure (if on ground) or destination (if airborne) airport, sorted numerically. Dispatches `RWY <designator>`. Includes a Clear option. |
| **Scratchpad 1/2** (`.XXX` / `+XXX`) | Text-entry popup with the current value pre-filled, plus EuroScope-convention preset chips (CLEA / NOTC / ST-UP / PUSH / TAXI / DEPA). Enter submits, Esc cancels. |
| **Squawk** | VFR / Standby / Normal / Ident / Random Squawk quick actions. For specific 4-digit codes use the typed command bar (`SQ 1234`). |
| **Handoff** indicator | Text-entry popup for `HO <position>`. When an inbound handoff is pending, an "Accept handoff" button appears. |

##### Heading Mode

Two flows are supported, mirroring EuroScope's pseudopilot UX:

- **Drag**: press-and-hold on `AHDG`, drag to a point on the map (the cursor turns to a crosshair, an elastic vector and turn-radius arc draw live), release on the target point. The bearing aircraft → release-point is computed (converted to magnetic) and dispatched as `FH <heading>`.
- **Click-to-confirm**: click `AHDG` and release without dragging — the mode stays active. Move the cursor anywhere; the live preview follows. Left-click on the map to confirm.

The preview shows a turn-anticipation arc (standard rate, radius derived from current ground speed) curving from the current heading into the new heading, plus a straight line to the cursor and a label `"275M  3.2nm  0:48"` (heading magnetic / distance / ETA at current GS).

**Cancel** at any time with **Esc** or right-click. The mode does not commit any command if cancelled.

For a primary-source reference on the EuroScope conventions this mode mirrors, see [`docs/euroscope/pseudopilot.md`](docs/euroscope/pseudopilot.md).

Video maps load automatically from the vNAS data API based on your [ARTCC](#glossary) ID.

**Per-scenario persistence** — Radar view settings (maps, center, zoom, range rings, PTL, brightness, lock) are saved independently for each scenario.

When weather is loaded, wind and altimeter are displayed in the top-left corner in STARS green.

### Flight Strips

The **Strips** tab is a YAAT-side reimplementation of CRC's vStrips. It renders the same flight-strip bays, racks, drag/drop, and keyboard shortcuts a real vStrips client would, so an instructor can push, annotate, and manage strips without students needing CRC's vStrips open.

Strip state is owned by the server and broadcast to every client in the room — including any real CRC + vStrips clients connected to the same room. Mutations from CRC, the embedded tab, and the standalone [Yaat.VStrips](tools/Yaat.VStrips/README.md) app all converge on the same authoritative state.

> A standalone build of this same view ships separately as **YAAT Flight Strips** (`YaatVStrips-*`). It connects to a YAAT server in the same way and is intended for trainees who want a vStrips-equivalent without installing the full trainer. See [tools/Yaat.VStrips/USER_GUIDE.md](tools/Yaat.VStrips/USER_GUIDE.md).

#### Opening the tab

The Strips tab appears next to Aircraft List / Ground View / Radar View as soon as the server tells the client which strip bays the student position can access. There is one **student entry** for the position you connected as, plus optional extra entries for any other facility your position can see (commonly an ATCT and its parent TRACON).

- **View → Strips → New Strips Tab…** opens a picker of accessible facilities and adds a new tab. Useful when you control a tower position and want both the local and TRACON bays visible at once.
- **View → Strips → Pop Out Strips (X)** detaches the tab into its own window. The student entry can be popped out and re-docked but not closed. Non-student entries also get a **Close Strips (X)** action.
- Each tab is titled `Strips (FacilityName)` so multiple strip tabs/windows can be told apart at a glance.

#### Header bar

Across the top of every strip view:

- **Facility button** (leftmost) — shows the current facility name. Click to switch this view to another accessible facility in place. Equivalent to CRC's leftmost facility indicator.
- **Bay buttons** — one per bay accessible from the position. **Own** bays render filled in neutral grey; **external** bays (linked from a sibling facility, e.g. a tower's parent TRACON) render with a thin outlined style and an **↗** suffix in context menus.
- **Zoom controls** (− / % / +) — scales the racks area without affecting the header. Range 50%–150% in 10% steps; default 80% fits two racks comfortably on a 1080p screen.
- **Trash zone** — drop a strip on the red bin to delete it.
- **Printer** toggle — opens the printer modal (see below). Bound to **Tab** as well.

#### Bays and racks

Selecting a bay button shows that bay's racks side by side. Each rack is a fixed-width column and renders strips **bottom-up FIFO** — the newest strip lands at the visual bottom and older strips stack upward. Bays scroll horizontally if they overflow the window.

Bay layout (number of racks per bay, which bays are own vs external, whether separators are locked, whether arrivals get a separate printer) comes from the ARTCC config. There is no client-side override.

#### Strip types

| Type | What it is |
|------|------------|
| **Departure strip** | Full-width strip printed from a filed IFR departure flight plan. 18 field slots including a 3×3 annotation grid (boxes 10–18). Auto-printed on aircraft spawn — see [Auto-printing](#auto-printing). |
| **Arrival strip** | Full-width strip auto-printed when an airborne aircraft is within 20 minutes of destination. Same layout as departures. Only rendered if the position's ARTCC config enables arrival strips. |
| **Half-strip** | Compact freeform note up to 6 lines, occupying either the left or right side of a rack slot. Created via the rack right-click menu, the `HSC` command, or **Ctrl+Shift+H**. |
| **Separator** | Thin colored divider (handwritten / white / red / green) with optional freeform label. Locked facilities allow handwritten only. |
| **Blank strip** | Empty placeholder for manual annotation. Created from the printer modal or the rack right-click menu. |

#### Working with strips

**Selecting:** click a strip to select it; **Esc** deselects. Plain arrow keys move selection between adjacent strips; **Ctrl+arrows** move the selected strip itself.

**Drag-drop:**
- Drag any strip onto another rack (same bay or another bay's button in the header) to move it.
- Drag onto the **trash zone** in the header to delete.
- A drop preview shows where the strip will land. Drops on rack padding or empty space below the last strip resolve to the rack's tail.

**Right-click on a strip** — opens a context menu:
- **Offset / Un-offset** — shifts the strip horizontally so the callsign column stays visible above the next rack
- **Slide** (half-strip only) — toggles between left/right half-strip
- **Edit lines** (half-strip only) — opens the inline editor with lines joined by ` / `
- **Edit label** (separator only) — opens the inline editor for the separator's label
- **Push to {bay}** — append to rack 1 of the chosen bay (external bays show **↗**)
- **Push all in rack to {bay}** — bulk move every strip in the source rack
- **Delete**

**Right-click on empty rack space** — opens a creation menu:
- **Add half-strip**
- **Add separator** (with handwritten / white / red / green submenu, or only handwritten if separators are locked for the position)
- **Add blank strip**
- **Push all to {bay}** — when the rack already has strips

**Editing annotations on a full strip:**
- Click any of the nine annotation cells (boxes 10–18) to open an inline editor
- **Tab / Shift+Tab** moves to the next / previous annotation cell
- Typing **`?`** substitutes a checkmark **✓** live; the server normalizes any `?` on `AN` commands the same way
- **Esc** cancels without committing

#### Printer modal

The printer modal is a centered overlay reachable via the **Printer** toggle, **Tab**, or **Esc** (when nothing is selected). The racks stay visible behind the modal, so dropping a strip from the printer onto a rack updates immediately without dismissing the modal.

- **Request Strip** — type a callsign and click to ask the server to print that aircraft's strip
- **Print Blank Strip** — adds a blank to the printer queue
- **Departure printer carousel** — ❮ ❯ arrows step through queued strips, **N/M** counter shows position
  - **Move to Bay** — opens a bay picker for the visible strip
  - **Move All to Bay** — bulk-moves the entire queue
  - **Delete** — discards the visible strip
- **Arrival printer carousel** — only present when the ARTCC config enables separate arrival/departure printers (`EnableSeparateArrDepPrinters`); otherwise arrivals share the departure queue.

#### Auto-printing

Strip printing is driven by the server based on student position type:

| Position type | Suffixes | Departure spawn | Arrival within 20 min |
|---------------|----------|------------------|------------------------|
| Tower | `_TWR`, `_LOC` | First own bay whose name starts with "Ground", else printer queue | Auto-prints to first matching bay if arrival strips enabled |
| Ground / Clearance | `_GND`, `_DEL` | Departure printer queue | — |
| Approach / Departure | `_APP`, `_DEP` | No spawn print — strip appears in the position's matching bay on takeoff roll | Bay matching position display name |
| Center / unknown | `_CTR`, other | Departure printer queue | — |

#### Keyboard shortcuts

> Strips need keyboard focus. Click anywhere in the strip view first.

| Key | Action |
|-----|--------|
| Click strip | Select |
| Esc | Deselect → if nothing selected, toggle printer panel |
| Arrow keys | Move selection between adjacent strips |
| Ctrl+arrows | Move the selected strip |
| Shift+← / → | Toggle offset on the selected strip |
| Ctrl+Shift+← / → | Slide a half-strip; cycle separator style (handwritten → white → red → green) |
| Enter | Edit half-strip lines / separator label |
| Ctrl+1..9 (with full strip selected) | Edit annotation box 10..18 |
| Tab | Toggle printer panel |
| PageDown / PageUp | Next / previous bay |
| Ctrl+Alt+1..9 | Push selected strip to bay N — or, if nothing selected, switch to bay N |
| Ctrl+Alt+← / → | Cycle this view to the previous / next accessible facility |
| Ctrl+Shift+H | Add a half-strip in the selected strip's rack (or rack 1) |
| Ctrl+Shift+S | Add a handwritten separator (cycle styles afterwards with Ctrl+Shift+→) |
| Delete / Backspace | Delete selected strip |

#### Command surface

Every strip mutation is also available as a [command](COMMANDS.md#strip--data-operations) — the UI just builds the canonical form for you. Useful when you want to script a flow, drive strips from a macro, or work without leaving the command bar.

| Verb | Effect |
|------|--------|
| `STRIP {bay}[/{rack}[/{index}]]` | Push the selected aircraft's full strip to a bay |
| `STRIPD` / `STRIPO` | Delete / toggle offset on the selected aircraft's strip |
| `AN {box} [text]` | Write or clear annotation box (1–9 = boxes 10–18) |
| `HSC {bay}[/{rack}] line\line\…` | Create a half-strip (max 6 lines) |
| `HSA [bay[/rack]] key\new1\…` | Amend by lookup key (auto-search across bays without bay arg) |
| `HSD [bay] key` | Delete by lookup key |
| `HSM` / `HSO` / `HSS` | Move / toggle offset / slide |
| `SEP H\|W\|R\|G bay[/rack[/index]] [label]` | Create separator |
| `SEPE bay/rack/index new-label` / `SEPD bay[/rack] label-or-position` | Edit / delete separator |
| `BLANK [bay[/rack[/index]]]` / `BLANKD bay[/rack]` | Create / delete blank |

Half-strip verbs run in two modes: with no aircraft selected, every line you type goes on the strip; with an aircraft selected, the callsign becomes line 1 and the lookup key automatically. See the [Half-Strips section in COMMANDS.md](COMMANDS.md#half-strips) for the full disambiguation rules.

#### Persistence

Pop-out state for the student strips entry is saved in `preferences.json` under the `VStrips` key. The popped-out window's geometry is saved separately. Bay layouts, zoom level, and selection are not persisted — they're driven by the server config and reset on each scenario load.

### Flight Plan Editor

Double-click an aircraft row in the Aircraft List to open its Flight Plan Editor (FPE). You can also **Ctrl+Left-Click** an aircraft symbol or datablock in the Ground View or Radar View.

The FPE shows editable flight plan fields: beacon code, aircraft type, equipment suffix, departure, destination, cruise speed, altitude, route, and remarks. The ALT field accepts 3-digit altitude codes (e.g., `035` for 3,500ft) or prefixed formats: `VFR`, `VFR/035`, `OTP/035`.

Edit any field, then click **Amend** to send the changes to the server. The Amend button is only enabled when at least one field differs from the current flight plan.

### Copying View Settings

Use **View > Copy View Settings From...** to apply another scenario's Ground and Radar view settings to the current scenario. The submenu lists all scenarios with saved settings (excluding the current one).

---

## Scenarios and Weather

### Loading a Scenario

**Scenario > Load Scenario...** opens a dialog with two tabs:

- **ARTCC Scenarios** (default) — lists training scenarios from the [vNAS](#glossary) data API for your configured ARTCC. Use the Airport filter to narrow by primary airport. Requires an ARTCC ID set in Settings.
- **Local Files** — browse a local folder for ATCTrainer-format JSON scenario files. Supports Facility and Rating filters.

Select a scenario and click **Load** (or double-click). Aircraft spawn at their configured starting positions. The window title shows the room name and scenario name. To switch scenarios, load a new one — a confirmation dialog appears if one is already active.

Both API and local scenarios appear in the **Scenario > Load Recent Scenario** menu for quick reloading.

### Unloading a Scenario

**Scenario > Unload Scenario** removes all aircraft from the current scenario. A confirmation dialog appears if multiple clients are connected.

### Loading a Weather Profile

**Scenario > Load Weather...** opens a dialog with two tabs:

- **ARTCC Weather** (default) — lists weather profiles from the vNAS data API. Each entry shows the name and wind layer count.
- **Local Files** — browse a local folder for ATCTrainer-format weather JSON files.

Select a profile and click **Load** (or double-click). Weather is room-level and persists across scenario loads/unloads. Both API and local weather profiles appear in **Scenario > Load Recent Weather**.

The active weather name is shown in the terminal when weather is loaded or cleared.

**Speed note:** Speed commands (`SPD`, `CM`, `DM`) assign [IAS](#glossary) (indicated airspeed). Aircraft ground speed — visible on the radar scope and aircraft grid — is derived from IAS + wind at altitude. Aircraft flying into a headwind show a lower ground speed than their assigned IAS. Aircraft on the ground are unaffected by wind.

### Loading Live Weather

**Scenario > Load Live Weather** fetches real-world METARs and winds aloft from aviationweather.gov. Requires a room, an ARTCC ID, and navdata.

Live weather builds wind layers from FAA Winds and Temperatures Aloft (FD) data at standard levels (3000–39000 ft) and a surface layer averaged from METARs. Wind directions are converted from true to magnetic heading automatically.

**Clear weather:** **Scenario > Clear Weather** removes the active weather profile. All aircraft return to IAS = GS behavior.

**Save weather:** **Scenario > Save Weather As...** exports the active weather profile to a JSON file.

### Weather Editor

**Scenario > New Weather...** opens the weather editor with a single empty period.

**Scenario > Edit Weather...** opens the editor pre-populated with the active weather. If a timeline is active, all periods are restored.

The editor has two panels:

- **Left panel** — period list. Use **+ Add** to create new periods and **- Remove** to delete the selected one (minimum one period). Each period shows its start time.
- **Right panel** — selected period details: start time (minutes), transition duration (minutes), precipitation type, wind layers grid (altitude, direction, speed, gusts), and METARs list.

**Saving format:** If the editor has a single period, it saves as a v1 weather profile (compatible with ATCTrainer). With two or more periods, it saves as a v2 weather timeline.

- **Apply to Sim** sends the weather to the server immediately. The editor stays open.
- **Save As...** exports to a JSON file without sending.
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
- `transitionMinutes` — duration (in minutes) over which wind layers blend from the previous period.
- At `startMinutes`, METARs and precipitation snap to this period's values immediately.
- Wind layers interpolate linearly over the transition window, handling the 360°/0° boundary correctly.
- If `transitionMinutes` is 0, all weather changes instantly.

**Loading:** Load v2 weather files using **Scenario > Load Weather... > Local Files** — files with a `periods` array are detected automatically. The v1 format continues to work.

**Rewind:** Weather timelines are fully compatible with the rewind system. Rewinding past a transition boundary restores the correct interpolated weather.

---

## Commands

YAAT has a comprehensive command system for controlling aircraft. Commands are typed in the command bar and sent with **Enter**.

**Quick examples:**

| Command | What it does |
|---------|-------------|
| `FH 270` | Fly heading 270 |
| `CM 100` | Climb and maintain 10,000 ft |
| `SPD 250` | Maintain 250 knots |
| `CM 050, FH 090` | Climb to 5,000 **and** turn to 090 simultaneously |
| `CM 100; LV 050 FH 270` | Climb to 10,000; at 5,000 ft, turn heading 270 |
| `TAXI S T U` | Taxi via taxiways S, T, U |
| `CLAND` | Cleared to land |
| `CAPP ILS28R` | Cleared ILS Runway 28R approach |

Commands support chaining (`;` sequential, `,` parallel), conditional triggers (`LV` at altitude, `AT` at fix), and aliases from both ATCTrainer and VICE.

**See the [Command Reference](COMMANDS.md) for the complete list of every command, alias, syntax detail, and example.**

---

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
- Press **Take Control** or issue any command to exit playback and resume live operation

### Save / Load Recordings

Under the **Scenario** menu:

- **Save Recording...** — exports the current session (scenario + all recorded actions) to a `.yaat-recording.json` file
- **Load Recording...** — loads a previously saved recording; enters playback mode at t=0

Recordings are self-contained JSON files that include the scenario definition, RNG seed, weather state, and all user actions with timestamps. They can be shared between users for review or training.

---

## Multi-User Features

### Aircraft Assignments

When multiple instructors/RPOs are in the same room, aircraft can be assigned to specific members for sole control.

**Context menu:** Right-click an aircraft in the Aircraft List, Ground View, or Radar View:
- **Take control** — assign the aircraft to yourself
- **Give up control** — unassign the aircraft
- **Give control > [initials]** — assign to another room member
- **Unassign** — remove any assignment

Multi-select works: select multiple aircraft, right-click, and the actions apply to all selected.

**Keybind:** Press **Ctrl+T** (configurable) with an aircraft selected to take control instantly.

**Terminal commands:**
- `TAKE` — assign the selected aircraft to yourself
- `GIVE XX` — assign to the member with initials XX
- `GIVEUP` — unassign the selected aircraft

All three support callsign prefix: `AAL123 TAKE`, `AAL123 GIVE AB`, `AAL123 GIVEUP`.

**Enforcement:** Once any assignment exists in the room, commands to aircraft assigned to someone else are rejected. The error shows who the aircraft is assigned to.

**Override:** Prefix your command with `** ` (double asterisk + space) to bypass the assignment check.

**Visual indicators:**
- The **RPO** column shows assigned controller initials
- The radar datablock shows `[INITIALS]` on line 3
- Optional color tint: in **Settings > Advanced > Radar Display**, enable **Tint my assigned aircraft** (default green `#00FF00`)

Assignments are cleared when a member leaves, an aircraft is deleted, or the scenario is unloaded.

### Room Members

Open **Room > Members...** to see everyone in the current training room:

- **Instructors** — YAAT clients in the room (initials, CID, ARTCC). Click **Kick** to remove.
- **CRC Students** — CRC clients bound to the room (display name, position, active status). Click **Kick** to remove.

The panel updates in real-time as members join or leave.

### Students Panel

Open **Room > Students...** to manage CRC clients:

**In Room** — CRC clients currently bound to your room. Click **Kick** to remove (they return to the lobby).

**Lobby** — CRC clients not in any room. Click **Pull** to bring a client into your room.

**Notes:**
- CRC clients with a CID matching a YAAT client in a room are automatically bound to that room
- If all YAAT clients leave a room, CRC clients are automatically unbound to the lobby
- Use **Refresh** to manually re-fetch both lists

---

## Connecting CRC for Students

[CRC](#glossary) (Consolidated Radar Client) is the VATSIM radar client that students use to work scopes. Connecting CRC to your YAAT server lets students see and interact with the simulated traffic you're controlling. This section is for **mentors/instructors** setting up CRC for their students.

### Option A: Setup Script (Recommended)

The YAAT repo includes a PowerShell script that configures CRC automatically:

```powershell
cd path/to/yaat
.\Setup-CrcEnvironment.ps1
```

This finds the student's CRC installation via the registry and creates or updates its `DevEnvironments.json` with two entries:

- **YAAT1** → `https://yaat1.leftos.dev` (hosted server)
- **YAAT Local** → `http://localhost:5000` (local development)

To add only specific servers, use the `-Servers` parameter:

```powershell
.\Setup-CrcEnvironment.ps1 -Servers @(@{Name="YAAT1";Url="https://yaat1.leftos.dev"})
```

### Option B: Manual Configuration

If the student doesn't have the YAAT repo, or is on macOS/Linux:

1. Find the CRC installation folder (check `HKCU:\Software\CRC\Install_Dir` in the registry, or look in `%LOCALAPPDATA%\Programs\crc`)
2. Create or edit `DevEnvironments.json` in that folder:

```json
[
  {
    "name": "YAAT1",
    "clientHubUrl": "https://yaat1.leftos.dev/hubs/client",
    "apiBaseUrl": "https://yaat1.leftos.dev",
    "isDisabled": false,
    "isSweatbox": false
  },
  {
    "name": "YAAT Local",
    "clientHubUrl": "http://localhost:5000/hubs/client",
    "apiBaseUrl": "http://localhost:5000",
    "isDisabled": false,
    "isSweatbox": false
  }
]
```

### How Students Connect

Once CRC is configured:

1. Make sure the YAAT server is running (or use the hosted YAAT1 server)
2. Have the student restart CRC (it reads `DevEnvironments.json` on startup)
3. In CRC's environment selector, the student chooses **YAAT1** (or **YAAT Local**)
4. The student connects with their VATSIM credentials
5. In YAAT, open **Room > Students...** and click **Pull** to bring the student into your room — they immediately start seeing your room's traffic

If the student's VATSIM CID matches a YAAT client in the room, they're pulled in automatically.

---

## Customization

### Autocomplete

As you type in the command bar, a popup appears with matching suggestions:

- **Command verbs** — matching verbs with syntax hints (e.g., `FH  Fly Heading {270}`)
- **Callsigns** — aircraft matching what you've typed, showing type and route
- **Command arguments** — context-specific options after typing a verb:
  - **CTO modifiers** — direction and traffic pattern modifiers for cleared-for-takeoff (IFR: heading only; VFR: all modifiers including pattern, on-course, direct-to)
  - **Runway designators** — for ELD, ERD, EF, CROSS, CLAND, LAHSO, CVA
  - **Fix names** — for DCT, DCTF, HFIX, CFIX, DEPART, AT conditions
- **Macros** (yellow) — when typing `!`, matching macro names with parameter hints

Suggestions are context-aware: after `;` or `,` separators, suggestions reset. When the input starts with a callsign, suggestions use that aircraft's data (route fixes, flight rules).

#### Fix Suggestion Priority

Fix suggestions use two tiers:

1. **Route fixes** (teal) — fixes from the target aircraft's flight plan (departure, destination, route, expanded SIDs/STARs)
2. **Navdata fixes** (white) — all other matching fixes

Route fixes always appear first.

### Macros

Macros let you define reusable command shortcuts. A macro maps a `!NAME` to a command expansion.

#### Defining Macros

Open **Settings > Macros** to create, edit, and manage macros. Each macro has a **Name** (e.g., `BAYTOUR`, `HC`) and an **Expansion** (the commands to expand to).

#### Parameters

| Style | Expansion | Invocation | Result |
|-------|-----------|------------|--------|
| Positional | `FH &1, CM &2` | `!HC 270 5000` | `FH 270, CM 5000` |
| Named | `FH &hdg, CM &alt` | `!FC 270 5000` | `FH 270, CM 5000` |

Named parameters serve as documentation — the autocomplete popup shows `!FC &hdg &alt`. Arguments are always supplied positionally.

#### Usage

Type `!` followed by the macro name:

- `!BAYTOUR` → `DCT VPCOL VPCHA VPMID`
- `!HC 270 5000` → `FH 270, CM 5000`
- `!HC 270 5000; DCT SUNOL` → macro + compound: `FH 270, CM 5000; DCT SUNOL`

Macros work anywhere in a compound command. Command history records the original macro text, not the expansion.

#### Import / Export

- **Export All** / **Export Selected** — save macros to a `.yaat-macros.json` file
- **Import** — load macros from a file, with a selection dialog for conflicts

### Favorite Commands

The favorites bar sits below the command input and provides quick-access buttons for frequently used commands.

- **Click** a favorite to execute immediately. If text is in the command input (e.g., a callsign), the favorite command is appended.
- **Ctrl+Click** a favorite to append its command text without sending (joined with `,`).
- **Right-click** a favorite to edit its label, command text, or delete it.
- Click **+** to add a new favorite.

Each favorite has a **label** (displayed on the button) and **command text** (the command to execute). Favorites can be **scenario-specific** — check "Scenario-specific" when adding/editing to make it visible only when that scenario is loaded.

### Command History

The command bar remembers your last 50 commands. Navigate with Up/Down arrows:

- **Up** — recall the previous command
- **Down** — move forward through history (or restore what you were typing)
- If you type something first, only history entries starting with that text are shown

### Settings

Open **Settings** to configure:

- **Identity** — VATSIM CID, user initials (required), and [ARTCC](#glossary) ID
- **Scenarios** — Auto-accept handoff settings, auto-delete aircraft override, simulation shortcuts (auto-clear to land, auto-cross runways), validate DCT fixes against route
- **Commands** — Alias editor for customizing command verbs. Use **Reset to Defaults** to restore built-in aliases.
- **Macros** — Define reusable command shortcuts (see [Macros](#macros))
- **Display** — Aircraft list font size, command signature help placement, **EuroScope-style interactive tags** toggle (see [Radar View > EuroScope-Style Interactive Tags](#euroscope-style-interactive-tags)), ground display options (start with datablocks hidden), and per-window always-on-top toggles
- **Colors** — Radar display colors (assignment tint, unassigned tint, selected aircraft color) and ground view colors
- **Advanced** — Aircraft select keybind, focus command input keybind, take control keybind, always-on-top keybind, and server admin mode

#### Simulation Shortcuts

Two optional shortcuts in **Settings > Scenarios > Simulation Shortcuts** simplify tower operations for trainees:

- **Auto-clear aircraft to land** — Aircraft on final are automatically cleared to land without requiring a CLAND command. Configured per position type (GND, TWR, APP, CTR). Defaults: GND on, TWR off, APP on, CTR on — so only tower controllers must issue explicit landing clearances.
- **Aircraft cross runways automatically** — Taxiing aircraft cross runways without stopping for a CROSS command. Explicit hold-short commands and destination runway hold-shorts still apply.

#### Auto-Accept

Handoffs to unattended positions can be automatically accepted after a configurable delay. Enable in **Settings > General > Auto-accept handoffs**.

#### Auto-Delete

Scenarios can define an `autoDeleteMode` that removes aircraft after landing or parking. Override in **Settings > General > Auto-Delete Aircraft** (options: "Use Scenario Setting", "Never", "On Landing", "On Parking").

To exempt a specific aircraft, append `NODEL` to `CLAND`, `TAXI`, `EL`, `ER`, or `EXIT` commands.

### Window State

YAAT remembers window size and position for the main window and all pop-out windows across sessions. Pop-out state (which views are in separate windows vs. tabbed) is also persisted.

---

## Glossary

| Term | Definition |
|------|-----------|
| **ARTCC** | Air Route Traffic Control Center — a facility managing a region of airspace (e.g., ZOA = Oakland Center, ZLA = Los Angeles Center) |
| **ATC** | Air Traffic Control |
| **CIFP** | Coded Instrument Flight Procedure — FAA database of instrument approaches, [SIDs](#glossary), and [STARs](#glossary) |
| **CRC** | Consolidated Radar Client — the [VATSIM](#glossary) radar client that students use to work scopes |
| **ERAM** | En Route Automation Modernization — the FAA's center radar system |
| **FRD** | Fix-Radial-Distance — a compact format for specifying a point in space relative to a navigation fix (see [COMMANDS.md](COMMANDS.md#fix-radial-distance-frd)) |
| **IAS** | Indicated Airspeed — the speed shown on the aircraft's airspeed indicator, before wind correction |
| **IFR** | Instrument Flight Rules — flight conducted under instrument procedures and ATC separation |
| **METAR** | Meteorological Aerodrome Report — a standardized weather observation format |
| **RPO** | Remote Pilot Operator — a YAAT user who controls simulated aircraft |
| **SID** | Standard Instrument Departure — a published departure procedure with waypoints and altitude/speed constraints |
| **STAR** | Standard Terminal Arrival Route — a published arrival procedure |
| **STARS** | Standard Terminal Automation Replacement System — the FAA's terminal radar system |
| **TCP** | Terminal Control Position — a sector identifier in STARS (e.g., "2B" = subset 2, sector B) |
| **VATSIM** | Virtual Air Traffic Simulation Network — a community for online ATC and pilot simulation |
| **VFR** | Visual Flight Rules — flight conducted visually without instrument procedures |
| **vNAS** | Virtual National Airspace System — VATSIM's infrastructure for facility data, nav data, and scenarios |
