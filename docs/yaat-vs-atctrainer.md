# YAAT vs ATCTrainer

A living comparison of features, commands, and behaviors between YAAT and ATCTrainer.

**ATCTrainer reference:** https://atctrainer.collinkoldoff.dev/docs/#/

---

## Architecture & Platform

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Closed vs Open Source | Closed Source | Open Source (MIT License) |
| vNAS Support | Official support from vNAS, uses real vNAS servers | Uses vNAS server emulation (for now?) |
| RPO Support | RPOs have to use the CRC messages interface | All instructors and RPOs can use the full app with all its features and conveniences |

## Session & Room Model

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Session model | All training sessions on the same server occupy the same world instance | Room-based: training scenarios can be run without conflicting with each other |
| CRC client management | Via VATSIM network | Pull/kick from lobby; Students Panel UI |

## Scenario Management

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Scenario source | vNAS API | vNAS API + local JSON files |
| Recent scenarios | — | Scenario > Load Recent Scenario menu |

## Weather

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Live weather | — | Yes — fetches METARs + winds aloft from aviationweather.gov |
| Weather editor | Only via Data Admin | Yes — create/edit profiles with wind layers + METARs; apply live |
| Save/export weather | — | Scenario > Save Weather As... |
| Weather display | Only in CRC | Wind/altimeter shown on Ground View and Radar View |

## Aircraft Grid / Main Window

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Columns | Callsign, Type, Rules, Heading (w/ assigned), Altitude (w/ assigned + VNAV), Speed (w/ assigned + mach), Status, Instruction | Callsign, Status, Type, Rules, Dep/Dest, Route, P.Alt, Remarks, Squawk, Hdg, Alt, Spd, VS, Ctrl, Owner, HO, SP1, SP2, TA, Phase, Rwy, AHdg/AAlt/ASpd, Dist, Pending Cmds |
| Column customization | — | Drag-reorder, show/hide via Column Chooser, resize, show only active filter; persisted |
| Sorting | Airborne > Ground > Parked > Delayed (alpha within each) | Click column headers; Active group always on top |
| Detail panel | — | Expandable panel per row: Phases, Pattern, Clearance, Route, Cruise, Pending |
| Distance column | — | Yes — distance from reference fix; middle-click to change reference |
| Flight plan editor | Only via CRC | Double-click row to open FPE |

## Views

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Layout | Single window (aircraft grid) | Tabbed: Aircraft List, Ground View, Radar View + terminal below |
| Ground view | No (only via CRC) | Interactive SkiaSharp airport surface map with extensive context menus |
| Radar view | No (only via CRC) | Built-in simplified STARS-style display with video maps and extensive context menus |

## Ground View (YAAT-only)

- Interactive airport surface map with taxiways, runways, aircraft positions
- Right-click context menus: taxi route options (K-shortest paths), pushback, hold short, cross, clearances
- Draw taxi route mode: click nodes to draw exact paths
- Debug overlay (Ctrl+D): node IDs, names, types for manual routing
- Label filters: RWY, TWY, HS, PARK toggles
- Lock/unlock pan/zoom
- Per-scenario settings persistence
- Wind/altimeter display when weather loaded
- Node ID taxi references (`TAXI !42 !18 !95`) for precise control

## Radar View (YAAT-only)

- STARS-style display with video maps from vNAS data API
- DCB bar: RNG, MAP, map shortcuts, RR, FIX, LOCK, TOP-DN, PTL, BRITE
- Right-click context menus on aircraft and map
- Draw route; click on map to set waypoints, with optional altitude crossings and commands to be executed at waypoint
- Datablocks: callsign, altitude, ground speed, owner, scratchpad
- Per-scenario settings persistence
- Predicted track lines (PTL) — own or all aircraft
- Brightness controls per video map category
- Copy View Settings From... to reuse settings across scenarios

## Command Input

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Input method | Only one person uses built-in CLI, every RPO is limited to CRC Messages window | Mentor & RPOs both can input commands via the terminal or the context menus |
| Aircraft selection | Only via CLI | CLI, Aircraft Grid, Ground View, Radar View left-click |
| Autocomplete | — | Yes — verbs, callsigns, arguments (runways, fixes, CTO modifiers), macros |
| Fix suggestions | — | Route fixes (teal) prioritized over navdata fixes (white) |
| Macros | Via aliases | Yes, via built-in macro editor; import/export |
| Favorites bar | — | Quick-access buttons; click/ctrl+click/right-click; scenario-specific option |
| Compound commands | — | `,` (parallel) and `;` (sequential) chaining |
| Concatenated syntax (VICE-style) | — | `FH270`, `H270`, `CM240`, etc. — verb + number without space |

## Command Differences

### Heading

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Fly heading | `FH {hdg}` | `FH {hdg}`, `H {hdg}`, `H270` | YAAT adds `H` alias + concatenation |
| Turn left/right | `TL`/`TR` | `TL`/`TR`, `L`/`R`, `LT`/`RT`, `L180` | YAAT adds short aliases + concatenation |
| Relative turn | `LT`/`RT` | `RELL`/`RELR`, `T30L`/`T30R` | YAAT uses `RELL`/`RELR` + combined `T` format |
| Present heading | `FPH`/`FCH` | `FPH`/`FCH`, `H` (no arg) | Same + bare `H` alias |

### Altitude

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Climb/descend | `CM`/`DM` | `CM`/`DM`, `C`/`D`, `CM240` | YAAT adds short aliases + concatenation |
| AGL altitudes | Not documented | `KOAK+010` format | YAAT-only |
| Cross fix at alt | `CFIX {fix} {alt}`/`CF` | `CFIX {fix} {alt}`, `CFIX {alt}` | YAAT adds fix-less form (next fix in route) + A/B prefixes |
| Climb/Descend Via | `CVIA`/`DVIA` | `CVIA`/`DVIA` | Both; YAAT adds altitude cap/floor: `CVIA 190`, `DVIA 240` |
| DVIA CFIX | `DVIA CFIX {fix} {alt}` | via compound: `DVIA; CFIX {fix} {alt}` | ATCTrainer combines in one command |

### Speed

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Speed | `SPD {kts}` (aliases: DS, IS, SLOW, SL, SPEED) | `SPD {kts}`, `S`, `SLOW`, `SL`, `SPEED`, `S250` | YAAT drops DS/IS aliases (directional implication); adds `S` + concatenation |
| Speed floor/ceiling | — | `SPD 210+` / `SPD 210-` | YAAT-only |
| Delete speed restrictions | — | `DSR` | YAAT-only — suppresses SID/STAR via-mode speed |
| SPD UNTIL shorthand | `SPD 250 FIX` (requires waypoint) | `SPD 210 UNTIL 10` → staged reductions | YAAT-only |
| Say speed | — | `SSPD` | YAAT-only |

### Navigation

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Append direct to | — | `ADCT {fix}`, `ADCTF {fix}` | YAAT-only |
| Join airway | `JAWY` | — | ATCTrainer-only |
| Join radial out/in | — | `JRADO`/`JRADI` (`JRAD`/`JICRS`) | YAAT-only |
| Airport/Destination | `APT`/`DEST` | `APT`/`DEST` | Parity |
| FRD navigation | — | `DCT JFK090020` | YAAT supports FRD in DCT args and AT conditions |
| DCT route validation | — | Optional setting: rejects off-route DCT; `DCTF` overrides | YAAT-only |

### Tower

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Cleared for takeoff | `CTO [heading]` | `CTO` + rich modifiers | YAAT has many more CTO forms (see below) |
| CTO left/right traffic | `CTOMLT`/`CTOMRT` | `CTO MLT`/`CTO MRT` (+ `CTOMLT`/`CTOMRT` aliases) | YAAT supports as modifiers and standalone verbs |
| CTO runway heading | `CTORH` | `CTO RH`/`CTO MRH`/`CTO MSO` | YAAT supports as modifier |
| GA heading + alt | — | `GA 270 5000`, `GA RH 2000` | **YAAT-only** |
| Cleared to land | — (implicit for pattern; `FS` for full-stop) | `CTL`/`FS` | **YAAT-only** optional explicit landing clearance |
| Cancel landing | — | `CLC`/`CTLC` | **YAAT-only** |
| Cleared for option | — | `COPT` | **YAAT-only** |
| LAHSO | `LAHSO {rwy}` | — | ATCTrainer-only |
| GO (begin takeoff roll during stop-and-go) | `GO` | `GO` | Parity |
| NODEL flag | — | `CTL NODEL`, `EL NODEL`, etc. | YAAT-only — exempts from auto-delete |

#### CTO Modifiers (YAAT-only richness)

YAAT's CTO command supports a comprehensive set of departure modifiers that ATCTrainer lacks:

| Modifier | Example | Notes |
|----------|---------|-------|
| Right/left heading | `CTO RH270`, `CTO LH270` | Explicit turn direction |
| Crosswind | `CTO MRC`, `CTO MLC` | 90-degree turn |
| Downwind | `CTO MRD`, `CTO MLD` | 180-degree turn |
| 45-degree & 270-degree turn departures | `CTO MR45`, `CTO ML270` | Any degree turn from runway heading |
| On course | `CTO OC` | Direct to destination |
| Direct to fix | `CTO DCT SUNOL` | Direct to named fix after liftoff |
| Cross-runway pattern | `CTO MRT 28R` | Pattern for different runway than takeoff |
| Altitude suffix | `CTO 270 050` | Any modifier + altitude |

### Approach

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Cleared approach | `CAPP`/`CTL` | `CAPP` | ATCTrainer uses `CTL` as alias; YAAT uses `CTL` for landing clearance |
| Straight-in | — | `CAPPSI`/`JAPPSI` | YAAT-only — skips hold-in-lieu |
| Force approach | — | `CAPPF`/`JAPPF` | YAAT-only — bypasses intercept angle check |
| PTAC | — | `PTAC 280 025 ILS30` | YAAT-only — position/turn/altitude/cleared |
| Rich approach forms | — | `CAPP AT SUNOL ILS28R`, `CAPP DCT SUNOL ILS28R` | YAAT-only — combines navigation + clearance |
| Expect approach | — | `EAPP I28R` | YAAT-only — sets expected approach for DCT fix programming feature |
| Visual approach | — | `CVA 28R` (+ LEFT/RIGHT/FOLLOW) | YAAT-only |
| Report field in sight | — | `RFIS` | YAAT-only |
| Report traffic in sight | — | `RTIS` | YAAT-only |
| Intercept validation | — | Yes — per 7110.65 §5-9-2 | YAAT-only |
| Illegal intercept warning | — | Yes — per 7110.65 §5-9-1 | YAAT-only |
| Approach scoring | — | Yes — terminal report + summary window | YAAT-only |

### Pattern

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Enter base | `ELB`/`ERB` | `ELB`/`ERB` + optional runway + distance | YAAT adds final distance |
| Make traffic | `MLT`/`MRT` | `MLT`/`MRT` + optional runway | YAAT adds cross-runway pattern |
| Make 270 | — | `L270`/`R270` | YAAT adds direction (left/right 270) |
| Plan 270 | `M2`/`M270` | `P270`/`PLAN270` | plans 270 at next pattern turn |
| 360s | `ML3`/`MR3` (with count) | `L360`/`R360` | ATCTrainer supports count; YAAT single orbit |
| Short approach | `MSA` | `SA`/`MSA` | Same |
| Pattern size | `PS`/`PATTSIZE`/`PSIZE` | `PS`/`PATTSIZE` | Same (YAAT uses NM, not multiplier) |
| Circle airport | — | `CA`/`CIRCLE` | YAAT-only |

### Holding

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| VFR hold present pos | — | `HPPL`/`HPPR`/`HPP` | YAAT-only, with turn direction for winged aircraft, `HPP` for helicopters |
| VFR hold at fix | — | `HFIXL`/`HFIXR`/`HFIX` | YAAT-only |
| Holding pattern | `HOLD {fix} {course} {dist} {dir}` | `HOLDP {fix} {L/R} {course} {leg}` | YAAT supports minute-legs, e.g. `1M` for 1-minute legs |
| Hold-in-lieu | — | Automatic during approach procedures | YAAT-only |

### Ground

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Pushback | `PUSH [twy/spot]` | `PUSH`, `PUSH 270`, `PUSH A`, `PUSH TE 180`, `PUSH TE T`, `PUSH @4A`, `PUSH @4A A`, `PUSH @4A 180` | YAAT adds heading, taxiway+heading, taxiway+toward-taxiway, and `@spot` (A* pathfinding to named parking) forms |
| Taxi | `TAXI {path} [hs-list]` / `RWY {rwy} TAXI {path}` | Same + `TAXI !42 !18` (node IDs), `TAXI A !42 B` (mixed) | YAAT adds node ID references for precise routing |
| Follow | — | `FOLLOW`/`FOL` | YAAT-only |
| Give way | `GIVEWAY`/`GW`/`PB` (standalone) | `GIVEWAY`/`BEHIND` (condition prefix) | ATCTrainer: standalone. YAAT: condition prefix in compound chains |
| Taxi all | `TAXIALL` | `TAXIALL` | Parity |
| Break | `BREAK` | `BREAK` | Parity (15s collision ignore) |
| NODEL for taxi | — | `TAXI S T U @B12 NODEL` | YAAT-only, overrides "at parking" deletion |
| Taxi variant resolution | — | Automatic | YAAT auto-extends taxiway variants (e.g., `TAXI W` → `W1`) when a numbered variant reaches the destination runway hold-short; picks closest variant to threshold when ambiguous |
| RWY (standalone) | Part of `RWY {rwy} TAXI` | `RWY 30` (assign runway without taxi) | YAAT adds standalone runway assignment |
| Air taxi (helicopter) | — | `ATXI H1` | YAAT-only |

### Track Operations

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Scratchpad | `SCRATCHPAD`/`SP` | `SP1`, `SP2` | YAAT has separate SP1/SP2 fields |
| Strip | `STRIP {bay}` | — | ATCTrainer-only |
| Act As | — | `AS` + per-command prefix | YAAT-only, allows user to act as any TCP for one command, or switch to that TCP as their primary for commands, independent of CRC |

### Coordination (YAAT-only)

ATCTrainer has no native coordination commands. YAAT implements STARS departure release coordination:

| Command | Description |
|---------|-------------|
| `RD [listId]` | Send departure release |
| `RDH [listId]` | Hold/send release |
| `RDR [listId]` | Recall/delete release |
| `RDACK [listId]` | Acknowledge release |
| `RDAUTO <listId>` | Toggle auto-acknowledge for list |

### Consolidation (YAAT-only)

| Command | Description |
|---------|-------------|
| `CON {tcp1} {tcp2}` | Consolidate positions |
| `CON+ {tcp1} {tcp2}` | Full consolidation (includes display settings) |
| `DECON {tcp}` | Deconsolidate position |

### Conditional Blocks

| Condition | ATCTrainer | YAAT | Difference |
|-----------|-----------|------|------------|
| At fix | `AT {fix} {cmd}` | `AT {fix} {cmd}` | Same; YAAT adds FRD and Fix-Radial format |
| At altitude | — | `LV {alt} {cmd}` | YAAT-only |
| At final distance | — | `ATFN {dist} {cmd}` | YAAT-only |
| Give way | `GIVEWAY` (standalone) | `GIVEWAY`/`BEHIND` (condition prefix) | Different paradigm |
| FRD conditions | — | `AT SUNOL090020 FH 270` | YAAT-only |
| Fix-Radial conditions | — | `AT SUNOL090 FH 270` (radial intercept) | YAAT-only |
| Missed FRD warning | — | Yes — warns if aircraft passes within 5nm but misses 1.5nm threshold | YAAT-only |

### Wait Commands

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Wait (time) | `WAIT {seconds}` / `DELAY` | `WAIT {seconds}` | Same (YAAT's `DELAY` is for spawn delay, not wait) |
| Wait (distance) | — | `WAITD {nm}` | **YAAT-only** |

### Spawn / Delay Control

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Spawn now | — | `SPAWN` | **YAAT-only** |
| Set spawn delay | — | `DELAY {n}` (M:SS supported) | **YAAT-only** |

### Add Aircraft

| Variant | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| At parking | `ADD {rules} {wt} {eng} @{space} [type] *[airline]` | `ADD {rules} {wt} {eng} @{spot} [type] *[airline]` | Same `@` prefix |
| On runway | Not documented as separate | `ADD {rules} {wt} {eng} {rwy}` | YAAT adds lined-up-on-runway |
| At fix | — | `ADD {rules} {wt} {eng} @{fix} {alt}` | YAAT-only |

### Debug / Force Commands

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Force speed | `SLN`/`ISN {spd}` | `SPDN {spd}` | Different alias |
| Show/Hide path | `SHOWPATH`/`HIDEPATH` | — | ATCTrainer-only |
| Warp | — | `WARP {frd} {hdg} {alt} {spd}` | YAAT-only |
| Warp on ground | — | `WARPG {twy1} {twy2}` | YAAT-only |

### Misc

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Flight plan | `FP {type} {alt} {route}` | `FP {type} {alt} {route}` | Parity; also available via FPE |
| VFR flight plan | `VP {type} {alt} {route}` | `VP {type} {alt} {route}` | Parity; also available via FPE |
| Remarks | `REMARKS {text}` | `REMARKS {text}` (`REM`) | Parity; also available via FPE |
| Cleared | `CLRD` | — | ATCTrainer-only (YAAT uses clearance column instead) |
| Delete at | `DELAT` | `DELAT` / `DELAT {n}` | Parity; YAAT adds per-block delete by number |
| Open chat | `OPENCHAT {controller}` | — | ATCTrainer-only |
| Operations/Stats | `OPS`/`STATS` | — | ATCTrainer-only |
| Show at | `SHOWAT` | `SHOWAT` | Parity; output is ephemeral (only sender sees it) |
| Chat messages | No specifix prefixes, chat messages can be misinterpreted as commands | `'msg`, `/msg`, `>msg` | YAAT has dedicated chat prefixes |

## Behavioral Differences

### Auto Go-Around (YAAT-only)

| Aspect | YAAT |
|--------|-----------|------|
| Behavior | Aircraft without landing clearance at 0.5nm auto-executes go-around with terminal warning |
| VFR/pattern traffic | Re-enters pattern automatically |
| IFR non-pattern | Flies runway heading at 2,000 AGL |
| Auto cleared-to-land setting | Configurable per position type (GND/TWR/APP/CTR) |

### Speed Management

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Auto speed reductions | 250kt < 10,000ft; base speed < 4,000ft AGL; staged reductions at 17/12/7nm; auto final speed < 4nm | 250kt < 10,000ft (14 CFR 91.117) |
| Speed floor/ceiling | — | `SPD 210+` / `SPD 210-` |
| Auto-cancel at 5nm | Not documented | Yes (per 7110.65 §5-7-1) |

### Ground Operations

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Collision avoidance | Yes — taxiing aircraft avoid collisions | Yes — automatic detection/avoidance |
| In-trail sequencing | Yes — within 30° and speed-independent | Yes |
| Merging paths | Yes — priority-based with instructor intervention | Not documented as extensively |
| BREAK command | 15-second collision ignore | Not available |
| Implicit hold-short | Not documented | Yes — automatic at all runway crossings |
| Auto-cross runways | Default behavior | Optional setting — aircraft cross inactive runways automatically |

### Command Delay

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Delayable commands | Yes — static delay on "Delayable" commands; configurable in seconds | `WAIT` for wait N seconds, `WAITD` for wait N flying miles |
| Syntax error timing | Errors shown after delay completes | Immediate |

### Private Messages

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| PM forwarding | Yes — PM to aircraft displayed in separate window for CPDLC training | Not documented |

## Features Unique to YAAT

### Timeline / Rewind
- Rewind to any point during a scenario
- Playback mode replays recorded commands
- Save/load recordings as JSON files
- Take Control button to exit playback and resume live

### Aircraft Assignments
- Assign aircraft to specific RPOs in multi-instructor rooms
- Command rejection for others' assigned aircraft
- `**` prefix for override
- Visual indicators (Ctrl column, radar datablock, color tint)

### Visual Approach System
- `CVA` command with LEFT/RIGHT/FOLLOW options
- Three execution paths (straight-in, angled join, pattern entry)
- Automatic "field in sight" / "traffic in sight" detection
- Bank angle affects initial visual acquisition
- WTG-based detection range (Super=15nm, Small=3nm)
- METAR-based ceiling/visibility gating

### Approach Scoring
- Quality evaluation on every approach completion
- Scores intercept angle, glideslope interception, final speed, stabilization
- Summary window via Scenario > Approach Report

### Flight Plan Editor
- Double-click or Ctrl+Click to open
- Edit all flight plan fields and amend

### Macros & Favorites
- `!NAME` macros with parameters (positional/named)
- Import/export macro files
- Favorites bar with scenario-specific option

### Autocomplete
- Context-aware suggestions for verbs, callsigns, fixes, runways, CTO modifiers, macros
- Route-fix prioritization (teal vs white)

### Weather Editor
- Create/edit weather profiles in-app
- Apply live to simulation
- Live weather from aviationweather.gov (METARs + winds aloft)

### Per-Scenario View Persistence
- Ground and Radar view settings saved independently per scenario
- Copy View Settings From... to reuse across scenarios

### Commands Not in YAAT
- `JAWY` (Join Airway)
- `LAHSO` (Land and Hold Short)
- `STRIP` (Push flight strips)
- `OPENCHAT` (Open PM window)
- `OPS`/`STATS` (Operations statistics)
- `SAYF` (Frequency message)
- `SHOWPATH`/`HIDEPATH` (Debug path display)
