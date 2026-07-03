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
| CRC client management | Via VATSIM network | Pull/kick from lobby; Room Members window |

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
| Weather timelines | — | Yes — v2 JSON format with time-based periods; wind interpolates during transitions |

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
| Layout | Single window (aircraft grid) | Tabbed: Aircraft List, Ground View, Radar View + terminal below; pop-outs support always-on-top (keybind or per-window setting) |
| Ground view | No (only via CRC) | Interactive SkiaSharp airport surface map with extensive context menus |
| Radar view | No (only via CRC) | Built-in simplified STARS-style display with video maps and extensive context menus |

## Ground View (YAAT-only)

- Interactive airport surface map with taxiways, runways, aircraft positions
- Right-click context menus: taxi route options (K-shortest paths), pushback, hold short, cross, clearances
- Draw taxi route mode: click nodes to draw exact paths
- Debug overlay (Ctrl+D): node IDs, names, types for manual routing
- Filters: RWY/TWY label toggles; HS/PARK/SPOT tri-state (labels+icons → icons only → off)
- Datablock deconfliction (DCNF) — opt-in auto-repositioning of overlapping datablocks; off → snap (8 leader directions) → free-form; manual drags pinned; per-view global setting
- Lock/unlock pan/zoom
- Per-scenario settings persistence
- Wind/altimeter display when weather loaded
- Node ID taxi references (`TAXI !42 !18 !95`) for precise control

## Radar View (YAAT-only)

- STARS-style display with video maps from vNAS data API
- DCB bar: RNG, MAP, map shortcuts, RR, FIX, MVA, DCNF, LOCK, TOP-DN, PTL, HISTORY, BRITE
- Minimum Vectoring Altitude awareness — FAA-charted MVA for all 148 published facilities; datablock altitude tinted red below / amber within 100 ft of the floor (IFR only), Ctrl+hover tooltip, and right-click MVA-at-point. Defaults per student position type (Approach/Center on, Ground/Tower off)
- Datablock deconfliction (DCNF) — opt-in auto-repositioning of overlapping datablocks; off → snap (8 leader directions) → free-form; manual drags pinned; per-view global setting
- Right-click context menus on aircraft and map
- Draw route; click on map to set waypoints, with optional altitude crossings and commands to be executed at waypoint
- Datablocks: callsign, altitude, ground speed, owner, scratchpad
- Per-scenario settings persistence
- Predicted track lines (PTL) — own or all aircraft
- History trails — configurable 0-10 dots showing past positions at ~5-second intervals
- Brightness controls per video map category (including HST for history trails)
- Copy View Settings From... to reuse settings across scenarios

## Command Input

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Input method | Only one person uses built-in CLI, every RPO is limited to CRC Messages window | Mentor & RPOs both can input commands via the terminal or the context menus |
| Aircraft selection | Only via CLI | CLI, Aircraft Grid, Ground View, Radar View left-click |
| Autocomplete | — | Yes — verbs, callsigns, arguments (runways, fixes, CTO modifiers), macros |
| Fix suggestions | — | Route fixes (teal) prioritized over navdata fixes (white) |
| Macros | Via aliases | Yes, via built-in macro editor; import/export |
| Favorites bar | — | Quick-access buttons; click/ctrl+click/right-click; scenario-specific option; import/export |
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
| Cross fix at alt | `CFIX {fix} {alt}`/`CF` | `CFIX {fix} {alt}`, `CFIX {alt}` | YAAT adds fix-less form (next fix in route) + A/B prefixes; YAAT uses step-based rate planning |
| Climb/Descend Via | `CVIA`/`DVIA` | `CVIA`/`DVIA` | Both; YAAT adds altitude cap/floor: `CVIA 190`, `DVIA 240` |
| DVIA CFIX | `DVIA CFIX {fix} {alt}` | via compound: `DVIA; CFIX {fix} {alt}` | ATCTrainer combines in one command |

### Speed

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Speed | `SPD {kts}` (aliases: DS, IS, SLOW, SL, SPEED) | `SPD {kts}`, `S`, `SLOW`, `SL`, `SPEED`, `S250` | YAAT drops DS/IS aliases (directional implication); adds `S` + concatenation |
| Speed floor/ceiling | — | `SPD 210+` / `SPD 210-` | YAAT-only |
| Delete speed restrictions | — | `DSR` | YAAT-only — suppresses SID/STAR via-mode speed |
| SPD UNTIL shorthand | `SPD 250 FIX` (requires waypoint) | `SPD 210 UNTIL 10`, `SPD 180 UNTIL AXMUL`, `SPD 180 AXMUL` | YAAT supports both distance-based and fix-based UNTIL; ATCTrainer alias `SPD X FIX` also supported |
| Say speed | — | `SSPD` | YAAT-only (includes Mach at/above FL240) |
| Say mach | — | `SMACH` | YAAT-only |
| Say expected approach | — | `SEAPP` | YAAT-only — broadcasts expected approach |
| Say altitude | — | `SALT` | YAAT-only — reports altitude and vertical trend |
| Say heading | — | `SHDG` | YAAT-only — reports heading and direct-to fix |
| Say position | — | `SPOS` | YAAT-only — anchors on a fix from the filed route, DCT queue, or dep/dest airport (with a parenthetical airport reference for unfamiliar fixes); falls back to the nearest sizeable airport when off-route |
| AT FIX SAY | — | `AT WAITZ SALT`, `AT MENLO SHDG`, `AT RHV SAY position report` | YAAT-only — defer any SAY-class report to fix overflight |

### Navigation

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Turn left/right DCT | — | `TLDCT {fix}`, `TRDCT {fix}` | YAAT-only: direct to fix with turn direction preference |
| Append direct to | — | `ADCT {fix}`, `ADCTF {fix}` | YAAT-only |
| Constrained route | — | `DCTF FIX1/A080 FIX2/050` | YAAT-only: inline altitude constraints with step-based planning |
| Join airway | `JAWY` | `JAWY` | Parity (YAAT intercepts airway segment, then follows fix sequence) |
| Join radial out/in | — | `JRADO`/`JRADI` (`JRAD`/`JICRS`) | YAAT-only |
| Airport/Destination | `APT`/`DEST` | `APT`/`DEST` | Parity |
| FRD navigation | — | `DCT JFK090020` | YAAT supports FRD in DCT args and AT conditions |
| DCT route validation | — | Optional setting: rejects off-route DCT; `DCTF` overrides | YAAT-only |

### Tower

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Cleared for takeoff | `CTO [heading]` | `CTO` + rich modifiers | YAAT has many more CTO forms (see below) |
| CTO left/right traffic | `CTOMLT`/`CTOMRT` | `CTO MLT`/`CTO MRT` (+ `CTOMLT`/`CTOMRT` aliases) | YAAT supports as modifiers and standalone verbs |
| CTO runway heading | `CTORH` | `CTO RH`/`CTO MRH`/`CTO MSO` | YAAT supports as modifier (IFR and VFR) |
| GA heading (± alt) | — | `GA 270`, `GA 270 5000`, `GA RH 2000` | **YAAT-only** |
| GA + traffic pattern | — | `GA MRT`, `GA MLT` (VFR/visual only) | **YAAT-only** |
| Cleared to land | — (implicit for pattern; `FS` for full-stop) | `CLAND [runway]`/`CL` | **YAAT-only** optional explicit landing clearance; `CLAND 28R` can pre-clear a following aircraft that has no runway yet (armed, applied when it joins the traffic's final) |
| Wake advisory | — | `CWT`, `CTO ... CWT`, `CLAND ... CWT` | **YAAT-only** standalone caution-wake-turbulence advisory and clearance suffix |
| Immediate / without delay | — | `CTO IMM` (immediate takeoff), `LUAW WD` (line up and wait, without delay) | **YAAT-only** — `IMM`/`WD`/`ND` suffix; brisk lineup taxi (+ rolling start on CTO). Super/Heavy keep a standing-start takeoff (7110.65 §3-9-5.3) |
| Cancel landing | — | `CLC`/`CTLC` | **YAAT-only** |
| Force landing | — | `CLANDF` | **YAAT-only** instructor override — implies clearance and forces a touchdown regardless of energy state, suppressing the automatic go-around; RPO-only |
| Cleared for option | — | `COPT` | **YAAT-only** |
| Option + traffic pattern | — | `TG MLT`, `SG MRT`, `LA MLT`, `COPT MRT` | **YAAT-only** — set traffic direction on the go |
| LAHSO | `LAHSO {rwy}` | `LAHSO {rwy}` | Parity |
| GO (begin takeoff roll during stop-and-go) | `GO` | `GO` | Parity |
| NODEL flag | — | `CLAND NODEL`, `EL NODEL`, etc. | YAAT-only — exempts from auto-delete |

#### CTO Modifiers (YAAT-only richness)

YAAT's CTO command supports a comprehensive set of departure modifiers that ATCTrainer lacks. Pattern and most runway-relative modifiers are VFR-only — IFR aircraft accept bare `CTO` (follow SID), a numeric heading vector, or `CTO RH` (fly runway heading):

| Modifier | Example | VFR/IFR | Notes |
|----------|---------|---------|-------|
| Right/left heading | `CTO RH270`, `CTO LH270` | Both | Explicit turn direction |
| Fly runway heading | `CTO RH`, `CTO MRH`, `CTO MSO` | Both | Holds runway heading and awaits vectors; suppresses the SID for IFR (rejoin via `DCT <fix>` + `CVIA`) |
| Crosswind departure | `CTO MRC`, `CTO MLC` | VFR only | Flies upwind, turns crosswind, then departs on the crosswind heading |
| Downwind departure | `CTO MRD`, `CTO MLD` | VFR only | Flies upwind, crosswind, downwind, then departs on the downwind heading |
| 45-degree & 270-degree turn departures | `CTO MR45`, `CTO ML270` | VFR only | Single relative turn of any degree from runway heading |
| On course | `CTO OC` | VFR only | Direct to destination |
| Direct to fix | `CTO DCT SUNOL` | VFR only | Direct to named fix after liftoff |
| Turn left/right DCT | `CTO TLDCT SUNOL`, `CTO TRDCT OAK` | VFR only | YAAT-only: direct to fix with turn direction preference |
| Cross-runway pattern | `CTO MRT 28R` | VFR only | Pattern for different runway than takeoff |
| Altitude suffix | `CTO 270 050` | Both | Any modifier + altitude |
| Wake advisory suffix | `CTO 270 CWT` | Both | Adds "caution wake turbulence" to the takeoff clearance |
| Immediate suffix | `CTO IMM`, `CTO RT270 IMM` | Both | `IMM`/`WD`/`ND` (interchangeable) = cleared for immediate takeoff: brisk lineup taxi + rolling start (Super/Heavy keep a standing start) |

After liftoff, a relative/heading/direct departure turn is deferred until the aircraft reaches the minimum safe altitude: 400 ft above field elevation for IFR (TERPS criterion — AIM 5-2-9.e.1 / 7110.65 5-8-3, with no lateral past-DER requirement), or pattern altitude − 300 ft AND past the departure end of runway for VFR (AIM 4-3-2). ATCTrainer applies the turn immediately at Vr. The named pattern-exit departures (`MRC`/`MRD`/`MLC`/`MLD`) go further: YAAT flies the actual upwind/crosswind/downwind legs to the exit point and then departs on the exit-leg heading (climbing continuously, no level-off at pattern altitude), so `EXT`/`EXT UPWIND` can extend a leg for spacing — ATCTrainer models these as a single immediate turn.

YAAT also flies charted SID legs coded as headings or courses rather than fixes. A departure such as the **LINDZ ONE** out of Aspen ("climb heading 343° to 9100, then climbing left turn to 273° to intercept the I-PKN back course to LINDZ") climbs to the charted altitude before turning, flies the charted turn, then intercepts and tracks the published course to the fix — handing off to the normal route once the coded legs are flown. ATCTrainer flies straight out on runway heading and cannot make the turn or join the back course.

This extends to legs terminated by a DME distance or radial rather than an altitude or fix. The Oakland **COAST9** ("climb heading 296° to cross the OAK 4 DME between 1400 and 2000") levels off at the 2000 ft window ceiling if it reaches it before the DME point, then resumes the climb once past it — matching the published crossing restriction (AIM 5-2-9.e). ATCTrainer ignores the DME-defined window and climbs straight through.

### Approach

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Cleared approach | `CAPP`/`CTL` | `CAPP`/`CTL` | Parity — `CTL` is ATCTrainer alias for CAPP |
| Straight-in | — | `CAPPSI`/`JAPPSI` | YAAT-only — skips hold-in-lieu |
| Force approach | — | `CAPPF`/`JAPPF`/`PTACF` | YAAT-only — marks the clearance as forced; for CAPPF implied-PTAC and PTACF the aircraft bypasses the 30° intercept-angle gate and captures even on steep cuts (overshoots laterally and S-turns back onto the localizer). Approach scoring records `WasForced`. |
| PTAC | — | `PTAC 280 025 ILS30` | YAAT-only — position/turn/altitude/cleared; supports PH/PA for present heading/altitude, optional approach ID. `PTACF` is the forced variant. |
| CAPP heading intercept | — | `CAPP ILS28R` (on vectored aircraft) | YAAT-only — bare CAPP on vectored aircraft intercepts on present heading (implied PTAC) |
| Rich approach forms | — | `CAPP AT SUNOL ILS28R`, `CAPP DCT SUNOL ILS28R` | YAAT-only — combines navigation + clearance |
| Expect approach | — | `EAPP I28R` | YAAT-only — sets expected approach, assigns `DestinationRunway`, programs approach fixes for DCT, and extends an active STAR with the runway transition |
| Visual approach | — | `CVA 28R` (+ LEFT/RIGHT/FOLLOW) | YAAT-only — IFR-only; per 7110.65 §7-4-3 requires field-in-sight (`RFIS`/`RFISF`) when number-one, or traffic-in-sight (`RTIS`/`RTISF`) when following (a following aircraft need not report the field, §7-4-3.a.2 NOTE). Rejected otherwise; `CVA … FOLLOW` a super is refused (§7-4-3.a.4 NOTE). |
| Visual approach (forced) | — | `CVAF 28R` / `VISUALF` | YAAT-only — RPO-only one-shot that folds `RFISF` (and `RTISF` when following) into the clearance, bypassing the report prerequisites; rejected in solo training |
| Follow (airborne VFR) | — | `FOLLOW [callsign]`/`FOL` | YAAT-only — VFR-only, requires traffic-in-sight proof (`RTIS`/`RTISF` in RPO mode, structured `RTIS` in solo training). Callsign argument is **optional**: bare `FOLLOW` defaults to the most recently reported in-sight traffic. From any airborne state: flies a trail behind the lead (steers relative to the lead's ground track — parallel/lag-pursuit/shallow widen, not aimed at the lead — plus speed with spacing correction) and auto-joins the lead's pattern when within 3 nm of the downwind abeam point; if the lead is on a straight-in final (no VFR pattern — e.g. an IFR ILS arrival), sequences the follower onto that runway's final to descend behind the traffic and hold for a separate `CLAND` (which can be pre-armed with `CLAND [runway]` while still following). Altitude held per controller assignment until sequenced onto final. Slows to maintain spacing but never speeds up to chase a far lead. When it structurally cannot sequence behind much-slower traffic (own approach speed > lead's by >10 kt) and is closing inside 0.8 nm on base/final, it breaks off and goes around ("unable to maintain separation") instead of overtaking or cutting in front. Rejected behind a super. The one-shot `FOLLOWF`/`FOLF` (RPO-only, rejected in solo training) folds `RTISF` in so no separate `RTIS` is needed. |
| Report field in sight | — | `RFIS 11 18` / `RFIS` | YAAT-only — structured form required in solo training; bare shorthand is RPO-only |
| Report field (forced) | — | `RFISF` | YAAT-only — RPO-only, bypasses visual detection |
| Report traffic in sight | — | `RTIS 3 5 W B737 024` / `RTIS <callsign>` | YAAT-only — structured form required in solo training; callsign shorthand is RPO-only. Tolerant matching (best traffic within realistic bands per 7110.65 §2-1-21 / AIM §4-1-15); altitude optional (`RTIS 3 5 W B737`) |
| Report traffic (VFR-style) | — | `RTIS NR 2 C172` / `RTIS BASE R 2 28R M20P` / `RTIS OVER VPCOL C172` | YAAT-only — relative-position (octant off the nose), pattern-leg (7110.65 §3-10-4), and landmark/VFR-reporting-point (§2-1-21.b.1) forms; typeable and speakable; same tolerant matching + graded scoring as the clock form |
| Report traffic (forced) | — | `RTISF` | YAAT-only — RPO-only, bypasses visual detection |
| Report position | — | `REPORT BASE` / `REPORT 5 FINAL` / `REPORT SUNOL` (+ `REPORT OFF [leg]`) | YAAT-only — deferred pilot position report; pattern-leg reports repeat each circuit until cancelled or full-stop |
| Safety alert | — | `SAFAL 12 1 [L/R] [C/D]` | YAAT-only — structured solo-training proof for unsafe aircraft proximity |
| Intercept validation | — | Yes — per 7110.65 §5-9-2 | YAAT-only |
| Illegal intercept warning | — | Yes — per 7110.65 §5-9-1 | YAAT-only |
| Approach scoring | — | Yes — terminal report + summary window | YAAT-only |

### Pattern

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Enter base | `ELB`/`ERB` | `ELB`/`ERB` + optional runway + distance | YAAT adds final distance; no-distance turns base perpendicular from present position (7110.65 §3-1-9 "turn base leg now"), not a fixed standard base turn point |
| Make traffic | `MLT`/`MRT` | `MLT`/`MRT` + optional runway | YAAT adds cross-runway pattern |
| Make 270 | — | `L270`/`R270` | YAAT adds direction (left/right 270) |
| Plan 270 | `M2`/`M270` | `P270`/`PLAN270` | plans 270 at next pattern turn |
| 360s | `ML3`/`MR3` (with count) | `L360`/`R360` (aliases `ML3`/`ML360`, `MR3`/`MR360`) | ATCTrainer supports count; YAAT single orbit, flown at holding speed then resumes normal speed |
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
| Pushback | `PUSH [twy/spot]` | `PUSH`, `PUSH FACE E`, `PUSH <E`, `PUSH A`, `PUSH TE FACE W`, `PUSH TE TAIL E`, `PUSH TE T`, `PUSH @4A`, `PUSH @4A A`, `PUSH @4A FACE NE`, `PUSH $7A`, `PUSH $7A TAIL W` | YAAT adds cardinal facing/tail (`FACE C`/`TAIL C` or `<C`/`>C`, C∈N/NE/E/SE/S/SW/W/NW), taxiway+cardinal-hint (snaps to nearest edge direction), a direct `@parking` reverse, and a `$spot` reverse-past-then-pull-forward that lines up nose-out on the mark |
| Taxi | `TAXI {path} [hs-list]` / `RWY {rwy} TAXI {path}` | Same + `TAXI !42 !18` (node IDs), `TAXI A !42 B` (mixed) | YAAT adds node ID references for precise routing |
| Follow (ground) | — | `FOLLOWG`/`FOLG` | YAAT-only |
| Give way | `GIVEWAY`/`GW`/`PB` (standalone) | `GIVEWAY`/`BEHIND` (condition prefix) | ATCTrainer: standalone. YAAT: condition prefix in compound chains |
| Taxi all | `TAXIALL` | `TAXIALL` | Parity |
| Break | `BREAK` | `BREAK` | Parity (15s collision ignore) |
| NODEL for taxi | — | `TAXI S T U @B12 NODEL` | YAAT-only, overrides "at parking" deletion |
| Taxi variant resolution | — | Automatic | YAAT auto-extends taxiway variants (e.g., `TAXI W` → `W1`) when a numbered variant reaches the destination runway hold-short; picks closest variant to threshold when ambiguous |
| Taxi current-taxiway inference | — | Automatic | YAAT prepends the taxiway the aircraft is already on when it joins the first cleared taxiway directly (`TAXI W` from W5 → `W5 W`; `TAXI E RWY 28R` from C → `C E RWY 28R`); the occupied taxiway is never flagged "not in authorized path". Skipped when the two meet only across a runway |
| Cross-runway direction anchor | — | Automatic | `TAXI <twy> CROSS <rwy>` with no destination heads toward and across the named runway and holds just past it; the crossed runway disambiguates direction even when the taxiway crosses two runways (`TAXI G CROSS 28R` from a plane just off 28L heads at 28R, not back across 28L) |
| Per-taxiway turn hints | — | `TAXI >A B <C D` | YAAT-only. A `>`/`<` glyph on a taxiway token biases the turn onto it (right/left); for the first taxiway it sets the start direction from the aircraft's heading, for later taxiways it prefers the junction whose turn matches. Best-effort — never strands the route; unprefixed taxiways keep the router's pick |
| Runway as a taxi segment | — | `TAXI 28R G D` | YAAT-only. A runway named mid-path (not just as the destination) is taxied **along** its centerline (back-taxi), then the aircraft turns off onto the named taxiways. It taxis straight onto the cleared runway; a *different* runway the route crosses still holds short. A non-intersecting taxiway/runway pair fails cleanly instead of detouring |
| Clear the runway | — | `CLRWY` / `CLEARRWY` | YAAT-only. Pulls an aircraft holding short of a taxiway with its tail over a runway forward until just clear of the runway, then holds. Resolves the tail-over-runway encroachment YAAT models when a taxiway hold-short sits closer than the aircraft's length past a crossed runway |
| RWY (standalone) | Part of `RWY {rwy} TAXI` | `RWY 30` (assign runway without taxi) | YAAT adds standalone runway assignment |
| Air taxi (helicopter) | — | `ATXI H1` | YAAT-only |

### Track Operations

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Scratchpad | `SCRATCHPAD`/`SP` | `SP1`, `SP2` | YAAT has separate SP1/SP2 fields; bare `SP1`/`SP2` clears; undo/toggle on repeat |
| Note | — | `NOTE {text}` | YAAT-only; instructor freetext datablock note (max 40), amber line on radar + ground, instructor-only (not on CRC); bare `NOTE` clears |
| Strip move | `STRIP {bay}` | `STRIP {bay}[/{rack}[/{index}]]` | YAAT extends to rack/slot positioning; slash-compound 1-based |
| Strip scan to external bay | — | `SCAN {bay}[/{rack}[/{index}]]` | YAAT-only; copies a strip into an external facility's bay (originator keeps its strip) |
| Strip delete | — | `STRIPD` | YAAT-only |
| Strip offset toggle | — | `STRIPO` | YAAT-only |
| Annotate box | — | `AN {box} [text]` | YAAT-only; annotate strip box 1-9 |
| Half-strip create | — | `HSC {bay}[/{rack}] {l1\l2\...}` | YAAT-only; freeform half-strip with up to 6 lines (callsign auto-prepended if aircraft selected) |
| Half-strip amend | — | `HSA [{bay}[/{rack}]] {key\new1\...}` | YAAT-only; amend half-strip by first-line key, auto-search across bays |
| Half-strip delete | — | `HSD [{bay}[/{rack}]] {key}` | YAAT-only; delete half-strip by first-line key |
| Half-strip move | — | `HSM [{src-bay}[/{src-rack}]] {key} {dest-bay}[/{rack}[/{index}]]` | YAAT-only; slash-compound dest-spec |
| Half-strip offset | — | `HSO [{bay}[/{rack}]] {key}` | YAAT-only; toggle half-strip offset |
| Half-strip slide | — | `HSS [{bay}[/{rack}]] {key}` | YAAT-only; toggle half-strip left ↔ right |
| Separator create | — | `SEP {style} {bay}[/{rack}[/{index}]] [label]` | YAAT-only; slash-compound position, optional label |
| Separator edit | — | `SEPE {bay}/{rack}/{index} {new-label}` | YAAT-only; atomic label rewrite at explicit slot |
| Separator delete | — | `SEPD {bay}[/{rack}] {label-or-position}` | YAAT-only; label or 1-based position locator |
| Blank create | — | `BLANK [{bay}[/{rack}[/{index}]]]` | YAAT-only; bare verb adds to printer queue |
| Blank delete | — | `BLANKD {bay}[/{rack}]` | YAAT-only; blanks are fungible |
| Act As | — | `AS` + per-command prefix | YAAT-only, allows user to act as any TCP for one command, or switch to that TCP as their primary for commands, independent of CRC. Ownership and pointout commands (`HO`/`ACCEPT`/`CANCEL`/`DROP`/`PO`/`OK`/`PORJ`/`PORT`) infer the acting position from the track (owner; handoff target for `ACCEPT`; pointout recipient for `OK`/`PORJ`; sender for `PORT`), so `AS` is **not** required for them — unlike real STARS, where you must own a track to hand it off. `TRACK` claims an unowned track and so must name who is claiming it — via your active position, an `AS` prefix, or a `TRACK [position]` argument (e.g. `TRACK 3Y`, a one-shot `AS 3Y TRACK`); the no-arg `PO` still needs `AS`. |
| Reject pointout | — | `PORJ` | YAAT-only; rejects pending inbound pointout |
| Retract pointout | — | `PORT` | YAAT-only; retracts your outbound pending pointout |
| Ack conflict alert | — | `CAACK` | YAAT-only; acknowledges CA for a track |
| Inhibit conflict alert | — | `CAINH` / `CAI` | YAAT-only; toggles CA inhibit |
| Pilot reported altitude | — | `PRA {hundreds}` | YAAT-only; `PRA 0` clears |
| Leader direction | — | `LDR {1-9}` | YAAT-only; `LDR 5` = default |
| J-Ring | — | `JRING {nm}` / `JRING` | YAAT-only; instructor TPA J-Ring (radius 1-30 NM) on YAAT's radar; bare clears |
| Cone | — | `CONE {nm}` / `CONE` | YAAT-only; instructor TPA Cone (length 1-30 NM) on YAAT's radar; bare clears |
| Ghost track | — | `GHOST {cs} [apt] {rwy}` | YAAT-only; creates ghost track staggered off runway end |

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
| On hold-short (post-landing) | — | `ONHS {cmd}` (currently `ONHS DEL` only) | YAAT-only — fires when the aircraft enters `HoldingAfterExit` |
| Cancel auto-delete | `NODEL` *modifier* only | `NODEL` bare verb + `NODEL` modifier | YAAT also exposes `NODEL` as a standalone verb to strip a queued `ONHS DEL` and re-arm `AutoDeleteExempt` |

### Wait Commands

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Wait (time) | `WAIT {seconds}` / `DELAY` | `WAIT {seconds}` | Same (YAAT's `DELAY` is for spawn delay, not wait) |
| Wait (distance) | — | `WAITD {nm}` | **YAAT-only** |
| Timer | — | `TIMER {mm:ss\|sec} [text]` (alias `TMR`), `TIMER CANCEL {id\|ALL}` | **YAAT-only** — countdown reminder; on expiry posts a green SAY (`text`, or `timer expired`). Global, or callsign-prefixed to attribute it. Visible/cancelable timers panel. |

### Spawn / Delay Control

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Spawn now | — | `SPAWN` | **YAAT-only** |
| Set spawn delay | — | `DELAY {n}` (M:SS supported) | **YAAT-only** |
| Hold for release | — | `HFR {airport}` / `HFROFF {airport}` | **YAAT-only** — hold an airport's IFR departures for release |
| Release departure | — | `REL {airport\|callsign}` (alias `CTOA`), `REL {airport} {min}` | **YAAT-only** — release next / specific / whole queue auto-spaced |
| Call for release | — | `CFR {HHMM}`, `CFR`, `CFR OFF`, `CFR CHECK` | **YAAT-only** — release a departure with a −2/+1 min CFR window (FAA §4-3-4.e.5); instructor-alerts if it departs outside it |

### Add Aircraft

| Variant | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| At parking | `ADD {rules} {wt} {eng} @{space} [type] *[airline]` | `ADD {rules} {wt} {eng} @{spot} [type] *[airline]` | Same `@` prefix |
| On runway | Not documented as separate | `ADD {rules} {wt} {eng} {rwy}` | YAAT adds lined-up-on-runway |
| At fix | — | `ADD {rules} {wt} {eng} @{fix} {alt}` | YAAT-only |
| Arrival on STAR | — | `ADD {rules} {wt} {eng} {wpt}.{star}[.{rwy}] [alt] [SP{spd}] [LVL] [airport]` | YAAT-only — IFR aircraft established on an arrival, descending via (or `LVL` to hold) |

### Debug / Force Commands

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Force speed (teleport) | `SLN`/`ISN {spd}` | `SPDN`/`SLN`/`SPEEDN {spd}` | YAAT accepts the ATCTrainer `SLN` alias plus `SPDN`/`SPEEDN` |
| Force speed (gradual, override 5nm final) | — | `SPEEDF`/`SPDF`/`SLF {spd}` | YAAT-only; overrides the 5nm-final restriction but converges via physics instead of teleporting IAS |
| Show/Hide path | `SHOWPATH`/`HIDEPATH` | — | ATCTrainer-only |
| Warp | — | `WARP {frd} [hdg] [alt] [spd]` | YAAT-only; trailing args optional, keep current values when omitted |
| Warp on ground | — | `WARPG {twy1} {twy2}` | YAAT-only |

### Misc

| Command | ATCTrainer | YAAT | Difference |
|---------|-----------|------|------------|
| Flight plan | `FP {type} {alt} {route}` | `FP {type} {alt} {route}` | Parity; also available via FPE. Auto-assigns squawk + echoes details. |
| VFR flight plan | `VP {type} {alt} {route}` | `VP {type} {alt} {route}` | Parity; also available via FPE. Auto-assigns squawk + echoes details. |
| Flight Data (abbreviated FP) | — | `DA [type] [alt] [beacon] [scratchpad] [.V/.E]` | YAAT-only (CRC F6 key). Optional fields, any order. DUP NEW ID if FP exists. |
| Cancel IFR | — | `CIFR` | YAAT-only. Changes aircraft from IFR to VFR. Required before issuing VFR-only commands (pattern entry, VFR holds, TG/SG/LA/COPT) to IFR aircraft. |
| Remarks | `REMARKS {text}` | `REMARKS {text}` (`REM`) | Parity; also available via FPE |
| Cleared | `CLRD` | — | ATCTrainer-only (YAAT uses clearance column instead) |
| Delete at | `DELAT` | `DELAT` / `DELAT {n}` (`DELCOND`, `DC`) | Parity; YAAT adds per-conditional delete by number and spans WAIT/BEHIND deferrals |
| Open chat | `OPENCHAT {controller}` | — | ATCTrainer-only |
| Operations/Stats | `OPS`/`STATS` | — | ATCTrainer-only |
| Show at | `SHOWAT` | `SHOWAT` (`SHOWCOND`) | Parity; lists the full conditional list (queue blocks + WAIT/BEHIND); output is ephemeral (only sender sees it) |
| Chat messages | No specifix prefixes, chat messages can be misinterpreted as commands | `'msg`, `/msg`, `>msg` | YAAT has dedicated chat prefixes |

## Behavioral Differences

### STARS Flight-Plan Creation vs AID + Slew (YAAT-only)

Typed STARS `DA`/`VP` commands are create-only — they never amend an existing flight plan. Typing against a callsign that already has a flight plan returns `DUP NEW ID`. The legitimate "aircraft exists but no FP" case (e.g. a ground aircraft before its FP is filed) is still a create — the FP is attached for the first time. No amend variants belong on `FlightPlanCommandHandler`.

The AID + slew gesture (type the aircraft ID, left-click the video map without pressing ENTER) is a separate path: it drops a ghost-overlay on the existing flight-planned aircraft at the click point and claims `Track.Owner` for the slewing controller, leaving the flight plan untouched. `CrcVisibilityTracker` auto-merges the ghost into the real surveillance track once the aircraft crosses the STARS floor; the owner persists. The two implied paths are discriminated by `CrcClientState.IsAidSlewGhostOverlay`.

### IFR/VFR Command Gating (YAAT-only)

VFR-oriented commands (pattern entry, traffic pattern turns/modifiers, VFR holds, touch-and-go, stop-and-go, low approach, cleared-for-option) are restricted to VFR aircraft. IFR aircraft receive an error with a hint to use `CIFR`. The report commands `RFIS`/`RTIS` are available to both IFR and VFR aircraft. Visual approaches (`CVA`) are IFR only and use IFR pattern geometry — wide pattern at 2000 ft AGL with no parallel-runway deconfliction; VFR pattern entry uses `ELD`/`ERD`/`SI` instead.

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
| Final approach speed reduction | Every aircraft settles at final speed at the same fixed distance | Per-aircraft distance, fixed at spawn and reproducible in replays — right-skewed 2.0–5.0nm (median ~3nm), so some slow early and compress the stream (live-network realism) |
| Speed floor/ceiling | — | `SPD 210+` / `SPD 210-` |
| Auto-cancel at 5nm | Not documented | Yes, for aircraft inbound to land only (7110.65 §5-7-1.b.4); speed released but never re-accelerated (holds or eases down); departures/go-arounds exempt; `SPEEDF` overrides |

### Runway Exit Deceleration

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Exit-aware rollout | Not documented | Yes — aircraft maintains speed until kinematic braking point for assigned exit |
| High-speed exit speed | Not documented | ~30 kts (≤45° angle from runway heading) |
| Standard exit speed | Not documented | ~15 kts (>45° angle, e.g. 90° turn) |
| No exit assigned | Uniform deceleration | Uniform deceleration to 20 kts (same) |
| Expedited exit (`EXP` / "without delay") | Not documented | YAAT-only — earliest reachable exit + max-effort braking (jet ~7.5 kts/s) + firm hold-short stop |

### Ground Operations

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Collision avoidance | Yes — taxiing aircraft avoid collisions | Yes — automatic detection/avoidance |
| In-trail sequencing | Yes — within 30° and speed-independent | Yes |
| Merging paths | Yes — priority-based with instructor intervention | Not documented as extensively |
| BREAK command | 15-second collision ignore | Not available |
| Implicit hold-short | Not documented | Yes — automatic at all runway crossings |
| Auto-cross runways | Default behavior | Optional setting — aircraft cross runways automatically |
| Parallel-runway pull-up after landing | Not documented | Yes — vacating between parallels auto-advances to hold short of the parallel runway (opt-out setting); a bare `CROSS` then crosses it without a prior `TAXI` |

### Command Delay

| Aspect | ATCTrainer | YAAT |
|--------|-----------|------|
| Automatic command-run delay | Yes — static delay on "Delayable" commands; configurable in seconds | Yes — configurable min–max range (random per command; equal = fixed), simulating pilot reaction / FMC setup. Off by default. Frequency changes and `WAIT`/`BEHIND`-timed commands are exempt |
| Per-command explicit delay | — | `WAIT` for wait N seconds, `WAITD` for wait N flying miles |
| Command-received feedback | Errors shown after delay completes | Immediate "Pilot complying in Ns" acknowledgement; the maneuver (and solo read-back) follow after the delay. In solo training the "Ns" hint is suppressed (the read-back is the acknowledgement) so the student can't read off the exact delay. Parse errors are still immediate |

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

### Aircraft Assignments / RPO Control
- Assign aircraft to specific RPOs in multi-instructor rooms
- **Take control** keybind (Ctrl+T, customizable) for instant self-assignment
- **Terminal commands**: `TAKE`, `GIVE XX`, `GIVEUP` (support callsign prefix)
- Context menu RPO submenu in all three views (Aircraft List, Ground View, Radar View)
- Command rejection for others' assigned aircraft
- `**` prefix for override
- Visual indicators (Ctrl column, radar datablock, color tint)

### Visual Approach & Follow System
- `CVA` command with LEFT/RIGHT/FOLLOW options
- Three execution paths (straight-in, angled join, pattern entry)
- Airborne `FOLLOW` command (VFR-only, requires traffic-in-sight proof like CVA FOLLOW) works from any airborne state: keeps a trail behind the lead — steering relative to the lead's ground track (parallel once established, lag-pursuit when far behind, a shallow widen to open the gap when too close at approach speed, per AIM §5-5-12 / §4-3-5) with speed-based spacing — and auto-joins the lead's pattern when within 3 nm of the downwind abeam point. Inside pattern phases, spacing is handled by the existing downwind/base/final speed-adjust and extend-downwind logic per 7110.65 §7-6-7 "Sequencing". A downwind follower also holds its base turn — extending up to 4 nm past its normal base-turn point — while the lead is pattern-flow-ahead (on base/final to the same runway), so it sequences in trail behind the lead instead of turning base early and overtaking it; at the cap it turns base and reports tight spacing for re-sequencing. On final more than 5 nm out, a follower that has caught up too close makes one shallow S-turn for spacing (AIM §4-3-5) and resumes the approach
- `RFIS`/`RTIS` run live visual detection on demand per 7110.65 §7-4-3 / AIM §5-4-23 and return a specific failure reason (behind us, above ceiling, too far, occluded by bank, out of range, wrong side of runway) so RPOs understand why
- Structured `RTIS <clock> <miles> <direction> <type> [altitude]` and `RFIS <clock> <miles>` provide FAA-style advisory phraseology; solo training rejects RPO shorthand `RTIS <callsign>`, bare `RFIS`, and forced variants. `RTIS` matching is tolerant rather than exact (best-matching traffic within realistic bands; altitude optional) and grades a within-tolerance-but-imprecise call with a low-severity coaching note
- Three VFR-style `RTIS` descriptive forms simplify plain traffic calls: relative-position `RTIS <pos> <miles> <type>` (octant off the nose, an informal VFR-tower convention), pattern-leg `RTIS <leg> [L/R] <miles> <rwy> <type>` (7110.65 §3-10-4), and landmark `RTIS OVER <fix> <type>` (§2-1-21.b.1). All three are both typeable and speakable, and feed the same tolerant matching and graded scoring as the clock form
- `SAFAL <clock> <miles> [L/R] [C/D]` proves 7110.65 §2-1-6 safety-alert work without setting traffic-in-sight or satisfying follow gates
- `RFISF`/`RTISF` forced variants bypass visual detection for RPO convenience and are rejected in solo training mode
- CVA FOLLOW requires traffic-in-sight proof
- Auto-cancels follow with warning when separation can't be maintained at minimum speed
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
- Favorites bar and pop-out panel with global/scenario/airport scopes, categories, colors, sizing, blank panel slots, and ground-command overrides; import/export favorites as `.yaat-favorites.json` files to share them

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

### Browser vStrips App
- `tools/Yaat.VStrips.Web` — WASM strip view served by yaat-server at `/vstrips/`
- No install required; opens in any browser
- Runs alongside CRC while YAAT awaits vNAS vStrips approval
- Auto-departure/arrival strip printing

### Commands Not in YAAT
- `OPENCHAT` (Open PM window)
- `OPS`/`STATS` (Operations statistics)
- `SAYF` (Frequency message)
- `SHOWPATH`/`HIDEPATH` (Debug path display)

### Commands Added by YAAT
- `CT [target]` — Contact next controller. Tells the simulated pilot to switch frequency. Distinct from the radar handoff (`HOO`/`ACCEPT`), matching the FAA 7110.65 §7-6-11 separation between radar coordination and the pilot frequency-change instruction. Optional argument is a position callsign (`OAK_TWR`), MHz frequency (`121.9`), or STARS TCP (`3O`); auto-resolves to the just-accepted handoff target when omitted. Required for the solo-training workflow because there's no second human controller to play the receiving sector.
- `FCA` — Frequency change approved. VFR dismissal phraseology (FAA 7110.65 §7-6-11) for aircraft leaving without a next controller.
- `CLBRV` / `CBRV` / `BRAVO` — Cleared through/to enter/out of Bravo airspace. YAAT-only solo-training command that satisfies the VFR Class B entry gate (FAA 7110.65 §7-9-2).
- `STBY` / `STANDBY` / `ROGER` / `RGR` — Acknowledge pilot contact without issuing a maneuver. YAAT-only solo-training command that satisfies the VFR Class C two-way-comms gate when targeted at that aircraft (AIM 3-2-4; FAA 7110.65 §7-8-4).
