# YAAT Command Reference

Complete reference for all YAAT commands. For a quick introduction to issuing commands, see [Getting Started](GETTING_STARTED.md#step-5-issue-your-first-command).

## Command Basics

Type commands in the command bar at the bottom of the YAAT window and press **Enter**.

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

### Syntax

YAAT uses a unified command scheme that accepts aliases from both ATCTrainer and VICE. Commands are space-separated by default (e.g., `FH 270`), but numeric arguments can be written without a space when unambiguous (e.g., `FH270`, `H270`, `CM240`). This concatenation works for any verb with a numeric argument.

The `H` alias is shared: bare `H` (no argument) maps to Fly Present Heading; `H 270` or `H270` maps to Fly Heading. Similarly, `T` is shared: `T30L` is relative left 30°, `T30R` is relative right 30°.

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

### Conditional Blocks

Use `LV` (level at altitude) and `AT` (at fix) to trigger blocks on specific conditions instead of waiting for the previous block:

- **LV** — triggers when the aircraft reaches an altitude:
  ```
  LV 050 FH 270
  ```
  When reaching 5,000 ft, turn to heading 270.

- **AT** — triggers when the aircraft reaches a fix, intercepts a radial, or reaches an [FRD](#fix-radial-distance-frd) point:
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
WAITD 2; TL 180
```
Fly 2 nm, then turn left to heading 180.

```
LV 050 WAIT 10; FH 090
```
At 5,000 ft, wait 10 seconds, then fly heading 090.

---

## Quick Reference

All commands grouped by category. Each table shows the primary command, aliases, and whether numeric concatenation is supported.

### Heading

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Fly heading | `FH 270` | `H` | `FH270`, `H270` |
| Turn left | `TL 180` | `L` | `TL180`, `L180` |
| Turn right | `TR 090` | `R` | `TR090`, `R090` |
| Relative left | `RELL 30` | `LT` | `T30L` |
| Relative right | `RELR 30` | `RT` | `T30R` |
| Fly present heading | `FPH` | `FCH`, `H` | — |

### Altitude / Speed

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Climb and maintain | `CM 240` | — | `CM240` |
| Descend and maintain | `DM 050` | — | `DM050` |
| Speed | `SPD 250` | `SPEED`, `DS`, `IS`, `SLOW`, `SL` | `SPD250` |
| Speed floor | `SPD 210+` | — | — |
| Speed ceiling | `SPD 210-` | — | — |
| Resume normal speed | `RNS` | `NS` | — |
| Expedite climb/descent | `EXP [alt]` | — | — |
| Resume normal rate | `NORM` | — | — |
| Reduce to final approach speed | `RFAS` | `FAS` | — |
| Mach number | `MACH .82` | `M` | — |
| Delete speed restrictions | `DSR` | — | — |

### Squawk / Transponder

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Squawk | `SQ 4521` | `SQUAWK` | `SQ4521` |
| Squawk (reset) | `SQ` | — | — |
| Squawk VFR | `SQVFR` | `SQV` | — |
| Squawk normal | `SQNORM` | `SN`, `SQA`, `SQON` | — |
| Squawk standby | `SQSBY` | `SS`, `SQS` | — |
| Ident | `IDENT` | `ID`, `SQI`, `SQID` | — |
| Random squawk | `RANDSQ` | — | — |
| Squawk all (reset) | `SQALL` | — | — |
| Squawk normal all | `SNALL` | — | — |
| Squawk standby all | `SSALL` | — | — |

### Navigation

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Direct to fix | `DCT SUNOL` | — | — |
| Turn left direct to | `TLDCT SUNOL` | — | — |
| Turn right direct to | `TRDCT OAK` | — | — |
| Force direct to | `DCTF SJC` | — | — |
| Append direct to | `ADCT SUNOL` | — | — |
| Append force DCT | `ADCTF SUNOL` | — | — |

### Ground

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
| Follow (ground) | `FOLLOWG SWA123` | `FOLG` | — |
| Give way | `GIVEWAY SWA123` | `BEHIND`, `GW` | — |
| Taxi all | `TAXIALL 30` | — | — |
| Break conflict | `BREAK` | — | — |

### Helicopter

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Takeoff present pos | `CTOPP` | — | — |
| Air taxi | `ATXI H1` | — | — |
| Land | `LAND H1` | — | — |

### Hold

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Hold (360 left) | `HPPL` | — | — |
| Hold (360 right) | `HPPR` | — | — |
| Hold present pos | `HPP` | — | — |
| Hold at fix (left) | `HFIXL SUNOL` | — | — |
| Hold at fix (right) | `HFIXR SUNOL` | — | — |
| Hold at fix | `HFIX SUNOL` | — | — |

### Approach / Procedures

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
| Cross fix | `CFIX SUNOL A034` | `CF` | — |
| Descend via | `DVIA` | — | — |
| Climb via | `CVIA` | — | — |
| Depart fix | `DEPART SUNOL 270` | `DEP`, `D` | — |
| Holding pattern | `HOLDP SUNOL R 180 1M` | `HOLD` (with args) | — |
| Expect approach | `EAPP I28R` | `EXPECT` | — |
| Follow traffic | `FOLLOW UAL456` | `FOL` | — |
| Visual approach | `CVA 28R` | `VISUAL` | — |
| Report field | `RFIS` | — | — |
| Report field (forced) | `RFISF` | — | — |
| Report traffic | `RTIS` | — | — |
| Report traffic (forced) | `RTISF` | — | — |
| List approaches | `APPS` | `APPS OAK` | — |

### Tower

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
| Begin takeoff (stop-and-go) | `GO` | — | — |
| Low approach | `LA` | — | — |
| Cleared for option | `COPT` | — | — |

### Pattern

> **VFR only:** Pattern commands, traffic direction, VFR holds, touch-and-go, stop-and-go, low approach, and cleared-for-option are restricted to VFR aircraft. IFR aircraft must first be given `CIFR` (Cancel IFR) to become VFR. Visual approaches (`CVA`) and report commands (`RFIS`/`RTIS`) are available to both IFR and VFR aircraft.

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

### Track Operations

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
| Reject pointout | `PORJ` | — | — |
| Retract pointout | `PORT` | — | — |
| Ack conflict alert | `CAACK` | — | — |
| Inhibit conflict alert | `CAINH` | `CAI` | — |
| Ghost track (runway) | `GHOST N12345 28R` | — | Global; callsign in args |
| Ghost track (airport+runway) | `GHOST N12345 KOAK 28R` | — | Global; callsign in args |
| Ghost track (lat/lon) | `GHOST N12345 37.7 -122.2` | — | Global; callsign in args |

### Display Operations

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Leader direction | `LDR 3` | — | — |
| J-Ring | `JRING` (clear) / `JRING ON` (set) | — | — |
| Cone | `CONE` (clear) / `CONE ON` (set) | — | — |

### Strip / Data Operations

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Annotate strip box | `AN 3 RV` | `ANNOTATE`, `BOX` | — |
| Push strip to bay | `STRIP Ground` | — | — |
| Scratchpad 1 | `SP1 OAK` / `SP1` (clear) | — | — |
| Scratchpad 2 | `SP2 I8R` / `SP2` (clear) | — | — |
| Temp altitude | `TEMPALT 120` | `TA`, `TEMP`, `QQ` | — |
| Cruise | `CRUISE 240` | `QZ` | — |
| Pilot reported altitude | `PRA 250` / `PRA 0` (clear) | — | — |
| On-handoff | `ONHO` | `ONH` | — |

### Flight Plan

| Command | Primary | Aliases | Notes |
|---------|---------|---------|-------|
| Change destination | `APT KSFO` | `DEST` | Changes aircraft destination airport |
| Create IFR flight plan | `FP B738 220 KBOS DCT KJFK` | — | Altitude in hundreds (220 = FL220) |
| Create VFR flight plan | `VP C172 5500 KOAK DCT KJFK` | — | Altitude absolute (5500 = 5,500 ft) |
| Flight Data (abbreviated FP) | `DA C172 065 4304` | — | CRC F6 key. Optional fields in any order: type, altitude (hundreds), beacon code, scratchpad (`` `VFF ``), flight rules (`.V`/`.E`). Creates VFR FP by default. Errors with DUP NEW ID if aircraft already has a flight plan. |
| Set remarks | `REMARKS /V/ STUDENT` | `REM` | Sets flight plan remarks field |
| Cancel IFR | `CIFR` | — | Changes aircraft from IFR to VFR. Sets flight rules to VFR and clears filed altitude. Required before issuing VFR-only commands (pattern entry, traffic pattern, VFR holds, touch-and-go, etc.) to IFR aircraft. |

### Consolidation

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Active position | `AS 2B` | — | — |
| Consolidate | `CON 1T 1F` | — | — |
| Consolidate full | `CON+ 1T 1F` | — | — |
| Deconsolidate | `DECON 1F` | — | — |

### Sim Control

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Pause | `PAUSE` | `P` | — |
| Unpause | `UNPAUSE` | `U`, `UN`, `UNP`, `UP` | — |
| Sim rate | `SIMRATE 2` | — | — |
| Delete aircraft | `DEL` | `X` | — |
| Delete queued commands | `DELAT` / `DELAT 2` | — | — |
| Show queued commands | `SHOWAT` | — | — |
| Say | `SAY text` | `SAYF` | — |
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
| Set turn rate | `TRATE 3` | — | — |
| Warp | `WARP FRD hdg alt spd` | — | — |
| Warp ground | `WARPG location` | — | — |

---

## Detailed Command Documentation

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
| `FOLLOWG SWA123` | Follow another aircraft on the ground |
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
| `CTO TLDCT SUNOL` | Cleared for takeoff, turn left direct to fix SUNOL |
| `CTO TRDCT OAK` | Cleared for takeoff, turn right direct to fix OAK |
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
| `GA MRT` | Go around, make right traffic (VFR/visual only) |
| `GA MLT` | Go around, make left traffic (VFR/visual only) |
| `GA 270` | Go around, fly heading 270, climb to 2,000 AGL (self-clear) |
| `GA 270 50` | Go around, fly heading 270, climb to 5,000 ft (overrides published missed approach) |
| `GA RH` | Go around, fly runway heading explicitly (same behavior as plain `GA`) |
| `GA RH 50` | Go around, fly runway heading, climb to 5,000 ft (overrides published missed approach) |
| `EL` / `EXITL` | Exit runway to the left |
| `ER` / `EXITR` | Exit runway to the right |
| `EXIT A3` | Exit runway at taxiway A3 |
| `EL NODEL` / `ER NODEL` / `EXIT A3 NODEL` | Exit with auto-delete exemption |

When an exit is assigned (via `EL`, `ER`, or `EXIT`), the aircraft maintains a higher rollout speed and only decelerates when kinematically necessary to reach the exit at the correct turn-off speed. High-speed exits (≤45° from runway heading) target ~30 kts; standard 90° exits target ~15 kts. Without an assigned exit, aircraft decelerate uniformly to 20 kts.

#### CTO Departure Modifiers

All CTO modifiers accept an optional altitude suffix using the same format as CM/DM (see [Altitude Arguments](#altitude-arguments)). A bare number (1-360) without a modifier prefix is interpreted as a heading: `CTO 270` = fly heading 270, `CTO 270 050` = fly heading 270, climb to 5,000 ft.

**IFR aircraft** can only use bare `CTO` (default SID/route departure) or `CTO` with a heading (`CTO 270`, `CTO RH`, `CTO H270`, etc.). Pattern exit modifiers (`MRC`, `MRD`, `OC`, `MLT`, `DCT`, etc.) are VFR-only.

| Modifier | Departure type | VFR/IFR |
|----------|----------------|---------|
| *(none)* | Default departure — VFR: runway heading; IFR: navigates filed route ([SID](#glossary) expansion) | Both |
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
| `TLDCT {fix}` | Turn left direct to named fix | VFR only |
| `TRDCT {fix}` | Turn right direct to named fix | VFR only |
| `MRT` / `MLT` | Make right/left closed traffic (enter pattern) | VFR only |
| `MRT {rwy}` / `MLT {rwy}` | Cross-runway closed traffic (pattern for a different runway) | VFR only |
| `MRT [rwy] [alt]` / `MLT [rwy] [alt]` | Closed traffic with optional altitude override (e.g., `CTO MLT 28R 15` = left traffic 28R at 1,500 ft) | VFR only |

**Cross-runway pattern** — `CTO MRT 28R` from runway 33 clears the aircraft for takeoff on runway 33 and enters right traffic for runway 28R. The pattern circuit (upwind, crosswind, downwind, base, final) is built for the pattern runway, not the takeoff runway.

**Altitude resolution** — when no altitude is specified, the target depends on flight rules and departure type:

1. Closed traffic → pattern altitude (1,000 ft AGL for props, 1,500 ft AGL for jets)
2. VFR with filed cruise altitude → cruise altitude
3. VFR without cruise → pattern altitude
4. IFR → self-clear at 1,500 ft AGL

**Navigation** — IFR `CTO` (default departure) automatically expands the filed route including [SID](#glossary) waypoints and navigates the aircraft along it. When CIFP data is available, SID legs include published altitude and speed constraints; SID via mode activates automatically so the aircraft follows the published climb profile. Use `CM` to override (disables via mode), or `CVIA` to re-enable it. `CTO DCT {fix}` turns the aircraft toward the fix after liftoff. `CTO OC` navigates toward the destination airport.

The `GA` altitude argument uses the same format as CM/DM (see [Altitude Arguments](#altitude-arguments)). `RH` in the heading position means "runway heading." `GA` accepts heading only (`GA 270`), heading + altitude (`GA 270 50`), or pattern direction (`GA MRT`/`GA MLT`). Without an altitude, the aircraft self-clears at 2,000 AGL. `GA MRT`/`GA MLT` sets the aircraft into pattern mode (make right/left traffic) and climbs to pattern altitude.

**Published missed approach** — When an aircraft on an instrument approach goes around — either automatically or via `GA` — it flies the published missed approach procedure from CIFP data. This includes climbing to the missed approach altitude, navigating through the MAP fix sequence, and entering a holding pattern at the final MAP fix if the procedure defines one. The aircraft holds indefinitely until given further instructions. If the procedure has no holding leg, the aircraft completes the MAP fix sequence and awaits vectors. Visual approaches and pattern traffic have no published missed approach and use the generic go-around behavior instead.

**ATC override** — If `GA` includes an explicit heading or altitude (e.g., `GA 270 50`), the published missed approach is cancelled and the aircraft flies the assigned heading/altitude instead. Any heading or altitude command issued while the aircraft is flying the missed approach procedure also cancels it.

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
| `FOLLOW UAL123` | Follow traffic (VFR): pursue lead and auto-join its pattern when close |

All pattern entry commands (ELB, ERB, ELD, ERD, ELC, ERC, EF) accept an optional runway argument. ELB/ERB also accept an optional distance argument that controls how far from the threshold the final turn occurs.

`P270` plans a 270° turn at the next pattern turn point without executing immediately. The turn direction is automatically determined from the traffic pattern direction (left 270 for left traffic, right 270 for right traffic). Use `NO270` to cancel.

`PS` sets the pattern downwind offset distance. The crosswind extension and base extension scale proportionally. The override persists across pattern circuits.

`FOLLOW` is a **VFR-only** command (per 7110.65 §7-6-7 "Sequencing"). It requires the pilot to have reported the traffic in sight first (`RTIS` or the forced `RTISF`) — a pilot cannot follow traffic they haven't visually acquired. Once `HasReportedTrafficInSight` is set, `FOLLOW` works from any airborne state — you do not need to put the follower in a pattern first. Behavior depends on where the follower and lead are:

- **Free pursuit** (lead not yet in a pattern, or follower far from the pattern): the follower turns toward the lead and matches the lead's speed with distance-based correction (±20 kts, wider free-flight spacing of 1.5/2.0/2.5 nm by category). Altitude is left at whatever the controller last assigned — real pilots do not dive/climb onto the lead; they maintain visual separation from their current level.
- **Pattern auto-join** (lead is in a pattern phase, follower within 3 nm of the lead's downwind abeam point, within 5 nm of the lead, and on the correct side of the runway): the follower's phase list is rebuilt with `PatternEntryPhase → DownwindPhase → BasePhase → FinalApproachPhase → LandingPhase` copying the lead's runway, pattern direction, and altitude. From then on, the existing pattern-tight spacing (1.0/1.5/2.0 nm) and extend-downwind logic take over.

Follow is cancelled automatically when the lead disappears, lands, the follower can't maintain separation at minimum speed, or the gap to the lead has been growing for more than 30 seconds (runaway-distance cancel). Any subsequent vector command (`FH`, `CM`, `SPD`, etc.) clears the follow phase and returns control to the controller's direct targets. To retarget, just issue another `FOLLOW` with a different callsign — the existing phase updates in place.

For **IFR** visual approaches, use `CVA 28R FOLLOW AAL123` instead — a distinct clearance that requires the pilot to have reported traffic in sight first (`RTIS`/`RTISF`).

### Approach Options

| Command | Effect |
|---------|--------|
| `TG` | Touch-and-go (establishes pattern mode if not already) |
| `TG MLT` / `TG MRT` | Touch-and-go, make left/right traffic on the go |
| `TG 28R` | Touch-and-go, runway 28R |
| `TG 28R MLT` | Touch-and-go, runway 28R, make left traffic |
| `SG` | Stop-and-go (full stop then re-takeoff, establishes pattern mode) |
| `SG MLT` / `SG MRT` | Stop-and-go, make left/right traffic |
| `GO` | Begin takeoff roll immediately during a stop-and-go (bypasses auto-pause) |
| `LA` | Low approach (fly-through without touchdown, establishes pattern mode) |
| `LA MLT` / `LA MRT` | Low approach, make left/right traffic |
| `COPT` | Cleared for the option |
| `COPT MLT` / `COPT MRT` | Cleared for the option, make left/right traffic |

TG, SG, LA, and COPT accept an optional `MLT`/`MRT` argument to set the traffic pattern direction on the go.

### Approach Control Commands

Approach clearances use FAA [CIFP](#glossary) procedure data. Approach IDs can be full CIFP identifiers (e.g., `I28R`) or common shorthand (e.g., `ILS28R`, `RNAV17L`, `LOC30`).

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

**Rich approach forms** — All approach commands (CAPP, JAPP, CAPPSI, JAPPSI, CAPPF, JAPPF) support combining navigation with the clearance:

| Command | Effect |
|---------|--------|
| `CAPP AT SUNOL ILS28R` | Navigate to fix SUNOL, then fly the ILS 28R |
| `CAPP DCT SUNOL ILS28R` | Direct to SUNOL, then fly the ILS 28R |
| `CAPP DCT SUNOL CFIX A034 ILS28R` | Direct to SUNOL, cross at or above 3,400, then ILS 28R |
| `JAPP AT SUNOL ILS28R` | Navigate to SUNOL, then join ILS 28R at nearest fix |
| `JAPP DCT SUNOL ILS28R` | Direct to SUNOL, then join ILS 28R |
| `CAPPSI DCT SUNOL ILS28R` | Direct to SUNOL, then cleared straight-in ILS 28R |

**CFIX altitude prefixes:** `A034` = at or above 3,400, `B034` = at or below 3,400, `034` = at 3,400.

**Heading intercept (implied PTAC):** When an aircraft is being vectored (has an assigned heading) and you issue a bare `CAPP` (no AT/DCT fix), the aircraft maintains its present heading and intercepts the final approach course — equivalent to an implied PTAC. If the aircraft has no assigned heading, CAPP navigates through approach fixes as usual. AT/DCT fixes always override heading intercept.

**Intercept angle validation:** Approach clearances validate the intercept angle per 7110.65 §5-9-2 — max 20° within 2nm of the approach gate, max 30° beyond. Use force variants (`CAPPF`/`JAPPF`) to override.

**Localizer bust-through detection:** When an aircraft on a heading intercept crosses the final approach course but its heading is too far off to capture (>30°), the approach is cancelled automatically. The aircraft continues on its current heading and a terminal broadcast notifies the RPO.

**Hold-in-lieu:** When a procedure includes a hold-in-lieu of procedure turn (e.g., at an IAF), `JAPP` automatically executes one holding circuit before proceeding. Use `CAPPSI`/`JAPPSI` to skip it.

**Expect approach:** `EAPP I28R` tells the pilot to expect the ILS 28R approach. This sets the expected approach on the aircraft state (visible in the data grid) and programs the approach fix names for display. Does not clear the aircraft for the approach.

**Force direct to:** `DCTF` works like `DCT` but bypasses the check that the target fix must be on or ahead of the current route.

**Visual approach:** `CVA 28R` clears the aircraft for a visual approach to runway 28R. No CIFP procedure is required — the aircraft navigates visually. Options:

| Command | Effect |
|---------|--------|
| `CVA 28R` | Cleared visual approach runway 28R |
| `CVA 28R LEFT` | Visual approach with left traffic pattern |
| `CVA 28R RIGHT` | Visual approach with right traffic pattern |
| `CVA 28R FOLLOW AAL123` | Visual approach following AAL123 (requires RTIS first) |

The aircraft execution path depends on its position relative to the runway:
- **Straight-in** (≤30° off final course): flies directly to final approach and landing
- **Angled join** (30°–90° off): navigates to an intercept point, then final
- **Pattern entry** (>90° off): enters downwind → base → final

The `FOLLOW` option requires the pilot to have reported traffic in sight first (via `RTIS` or `RTISF`). The follower adjusts speed and extends downwind to maintain visual separation from the leader. See [Pattern Commands](#pattern-commands) for follow behavior details.

**Field/traffic in sight:** Aircraft on an active visual approach automatically report "field in sight" or "traffic in sight" when detection conditions are met (forward hemisphere, within visibility range, below ceiling). `RFIS` and `RTIS` can also be issued on demand before clearance — they run the same visual check live against the arrival airport (`RFIS`) or a named target callsign (`RTIS`). On failure they return the specific reason — e.g. `"Unable, KOAK behind us"`, `"Unable, KOAK below the layer, ceiling 1500 AGL"`, `"Negative contact, LEAD, 9 miles"`, `"Unable, KOAK lost visual in the turn"` — so RPOs know whether to adjust the situation or fall back to the forced variants. The forced variants `RFISF` and `RTISF` set the flag directly without running detection.

**Bank angle affects initial acquisition:** A pilot in a turn cannot initially spot targets hidden by the high wing. Time your traffic calls for when the pilot can actually see the target — not during a turn that blocks the view. Once the pilot has the target in sight, they can track it through subsequent turns.

**Aircraft size affects detection range:** Larger aircraft are visible from farther away. A Super (A388) can be spotted at up to 15nm, while a Small (C172) is only visible at about 3nm. Detection range scales with the target aircraft's FAA wake turbulence group.

**Approach scoring:** When an aircraft completes an approach, the terminal shows an approach report evaluating the quality of the approach setup. Scored criteria include intercept angle, glideslope interception altitude, final approach speed, and stabilization. **Scenario > Approach Report** opens a summary window showing all approach reports from the current session.

### Direct To (DCT)

Navigate to one or more fixes:

```
DCT SUNOL
DCT SUNOL CEDES MYCOB
```

Fixes can also be specified as [FRD](#fix-radial-distance-frd) strings in the format `{fix}{radial:3}{distance:3}`:

```
DCT JFK090020
```

This means "the point on the 090 radial from JFK at 20 NM." The fix name must be 2+ characters, followed by exactly 3 digits for the radial (degrees) and 3 digits for the distance (NM). FRD works anywhere a fix name is accepted.

If the last fix in the list appears in the aircraft's filed route, the aircraft continues on its filed route from that point.

**Route validation** — When **Validate DCT fixes against route** is enabled in Settings > Scenarios, DCT commands to fixes not in the aircraft's filed route or expected approach are rejected. Use `DCTF` (force direct to) to override. Issue `EAPP` (Expect Approach) first to program approach fixes into the aircraft's route for validation purposes.

### Navigation Commands

| Command | Effect |
|---------|--------|
| `DCT SUNOL` | Direct to fix SUNOL |
| `DCT SUNOL CEDES MYCOB` | Direct to multiple fixes in sequence |
| `TLDCT SUNOL` | Turn left, direct to fix SUNOL |
| `TRDCT OAK` | Turn right, direct to fix OAK |
| `DCTF SJC` | Force direct to — bypasses route validation |
| `DCTF FIX1/A080 FIX2/050 FIX3` | Force direct to with inline altitude constraints |
| `ADCT SUNOL` | Append direct to — adds SUNOL to the end of the current route |
| `ADCTF SUNOL` | Append force direct to — appends without route validation |
| `JARR OAK.SALI2` | Join [STAR](#glossary): navigate to nearest fix on the SALI2 arrival into OAK |
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

`JARR` also accepts aliases `ARR`, `STAR`, `JSTAR`. `JRADO` also accepts `JRAD`. `JRADI` also accepts `JICRS`. `DEPART` also accepts `DEP` and `D`.

`JAWY` intercepts and joins a named airway (e.g., V25, J80). The aircraft flies its present heading until it intercepts the airway segment, then turns onto the airway course and follows the fix sequence in the direction of travel.

JARR supports CIFP altitude/speed constraints when available. The airport prefix (`OAK.`) is optional — when omitted, the aircraft's destination airport is used. The entry fix specifies where to join the STAR; when omitted, the nearest fix ahead of the aircraft is used.

CFIX supports two forms: `CFIX {altitude}` modifies the altitude restriction for the next fix in the route, while `CFIX {fix} {altitude}` targets a specific named fix. Altitude prefixes: `A` = at or above, `B` = at or below, no prefix = at exactly. CFIX uses step-based descent/climb planning — the aircraft computes the exact vertical rate needed to arrive at the constraint altitude precisely at the fix.

**Constrained DCTF** — `DCTF FIX1/A080 FIX2/050 FIX3` attaches altitude constraints inline. The `/` suffix uses the same CFIX altitude format. All constraints are visible to the planner simultaneously, so the aircraft plans descent across multiple waypoints at once.

**SID/STAR via mode** — When CIFP procedure data is available, [SID](#glossary) and [STAR](#glossary) fixes carry published altitude and speed restrictions. Via mode controls whether the aircraft follows those restrictions:

| Procedure | Default | Enable via mode | Disable via mode |
|-----------|---------|-----------------|------------------|
| SID (after CTO) | **ON** — aircraft follows published climb restrictions | `CVIA` (re-enable after CM override) | `CM` (any altitude command) |
| STAR (after JARR) | **OFF** — aircraft maintains altitude, follows lateral path only | `DVIA` (enable descent restrictions) | `DM` (any altitude command) |

`CVIA 190` and `DVIA 240` enable via mode with an altitude cap/floor — "climb/descend via, except maintain." `FH`, `DCT`, and heading commands clear the entire procedure (lateral path + via mode).

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

**SPD UNTIL shorthand** — `SPD 210 UNTIL 10` expands to `SPD 210; ATFN 10 RNS`. When chained, intermediate blocks are generated automatically: `SPD 210 UNTIL 10; SPD 180 UNTIL 5` becomes `SPD 210; ATFN 10 SPD 180; ATFN 5 RNS`. Fix-based UNTIL is also supported: `SPD 180 UNTIL AXMUL` expands to `SPD 180; AT AXMUL RNS`.

**Auto-cancel at 5nm final** — Per 7110.65 §5-7-1, ATC speed assignments (target, floor, ceiling) are automatically cancelled when the aircraft is within 5nm of the runway threshold. New speed commands are rejected inside this boundary.

**DSR interaction** — `DSR` suppresses SID/STAR via-mode speed constraints at waypoints. The aircraft still follows altitude restrictions but ignores published speed restrictions. A new `SPD` command, `CVIA`, or `DVIA` clears the DSR flag.

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

Track operations control aircraft ownership, handoffs, and coordination. These commands use STARS-style [TCP](#glossary) codes (e.g., "2B" = subset 2, sector B) or ERAM center codes (e.g., "C44" = center sector 44).

#### Active Position

By default, you operate as the scenario's student position. Use `AS` to act as a different position:

| Command | Effect |
|---------|--------|
| `AS 2B` | Set your active position to TCP 2B (persistent until changed) |
| `N135BS AS 2B HO 3Y` | Act as 2B for this command only (prefix mode) |

Resolution order: per-command `AS` prefix > persistent active position > student position default.

Changing your active position also updates the radar display:
- **DCB map shortcuts** switch to the position's configured 3x2 map group.
- **Weather airports** update to the position's STARS area's underlying airports.

#### Track Commands

| Command | Effect |
|---------|--------|
| `TRACK` | Initiate control — take ownership of the aircraft |
| `DROP` | Terminate control — release ownership |
| `HO 3Y` | Handoff to TCP 3Y (must own the aircraft) |
| `HO C44` | Handoff to ERAM center sector 44 (must own the aircraft) |
| `HOF 3Y` | Force handoff to TCP 3Y (transfers ownership regardless of current owner) |
| `ACCEPT` / `A` | Accept a pending inbound handoff |
| `CANCEL` | Retract a pending outbound handoff |
| `ACCEPTALL` | Accept all pending inbound handoffs (global — no callsign needed) |
| `HOALL 3Y` | Handoff all your aircraft to TCP 3Y (global — no callsign needed) |
| `PO 3Y` | Point out to TCP 3Y |
| `OK` | Acknowledge a pending pointout |
| `PORJ` | Reject a pending inbound pointout |
| `PORT` | Retract your outbound pending pointout |
| `CAACK` | Acknowledge conflict alerts for this aircraft |
| `CAINH` / `CAI` | Toggle conflict alert inhibit on/off |
| `GHOST N12345 28R` | Create ghost track off 28R (auto-stagger, scenario airport) |
| `GHOST N12345 KOAK 28R` | Create ghost track off 28R at KOAK |
| `GHOST N12345 37.7 -122.2` | Create ghost track at exact position |
| `AN 3 RV` / `BOX 3 RV` | Write "RV" in strip annotation box 3 (boxes 1-9) |
| `AN 3` | Clear strip annotation box 3 |
| `STRIP Ground` | Push flight strip to "Ground" bay in vStrips |
| `SP1 OAK` | Set scratchpad 1 |
| `SP1` | Clear scratchpad 1 |
| `SP2 I8R` | Set scratchpad 2 |
| `SP2` | Clear scratchpad 2 |

Scratchpads support **undo/toggle**: entering the same value again restores the previous value, and clearing an already-cleared scratchpad restores the previous value.

| `TA 120` / `QQ 120` | Set temporary altitude (in hundreds, e.g., 120 = FL120) |
| `CRUISE 240` / `QZ 240` | Set cruise altitude |
| `PRA 250` | Set pilot reported altitude (in hundreds; `PRA 0` clears) |
| `ONHO` / `ONH` | Toggle on-handoff status |
| `LDR 3` | Set leader line direction (1-9; `LDR 5` = default) |
| `JRING` / `JRING ON` | Clear / set J-Ring |
| `CONE` / `CONE ON` | Clear / set cone |

#### Coordination (Rundown List)

Coordination commands manage departure releases between tower and approach controllers. Coordination channels are loaded from the [ARTCC](#glossary) config when a scenario is loaded.

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

### Consolidation

Consolidation commands manage STARS position consolidation, allowing one controller position to absorb responsibility for another position's airspace.

| Command | Effect |
|---------|--------|
| `CON 1T 1F` | Consolidate positions 1T and 1F (1T absorbs 1F's airspace) |
| `CON+ 1T 1F` | Full consolidation — includes all display settings and map groups |
| `DECON 1F` | Deconsolidate position 1F (restore it as an independent position) |

These are global commands — no aircraft selection is needed. TCP codes follow the same format as track operations (e.g., "2B" = subset 2, sector B).

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
- **At fix**: `@{fix}` is a fix name or FRD, `{alt}` is altitude in feet. Aircraft spawns at the fix heading toward the primary airport.
- **Lined up**: `{runway}` is the runway designator (e.g., `28R`). Aircraft spawns on the runway threshold, ready for takeoff clearance.
- **On final**: `{runway}` plus `{dist}` in NM. Aircraft spawns on final approach at that distance from the runway.
- **At parking/helipad**: `@{spot}` is a parking or helipad name (e.g., `@H1`, `@B12`). Aircraft spawns at ground level. Useful for helicopters and ground operations.

**Optional trailing tokens:**
- Explicit aircraft type: `ADD IFR H J -090 20 15000 B77L`
- Explicit airline prefix: `ADD IFR L J -180 10 5000 *SWA`

**Valid weight/engine combinations:**

| | Piston (P) | Turboprop (T) | Jet (J) |
|---|---|---|---|
| Small (S) | C172, C182, PA28, SR22 | C208, PC12, BE20 | — |
| Large (L) | — | DH8D, AT76, AT72 | B738, A320, E170, E175, CRJ9 |
| Heavy (H) | — | — | B77L, B772, A332, B789, B744, A359 |

IFR aircraft get a random airline callsign (e.g., UAL1234). VFR aircraft get an N-number (e.g., N1234A).

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
| `TAXIALL 30` | Taxi all parked aircraft to runway 30 |

### Force Override Commands

When you need to immediately correct an aircraft's state (rather than waiting for flight physics to gradually adjust):

| Command | Effect |
|---------|--------|
| `FHN 270` | Force heading: immediately set heading to 270° |
| `CMN 50` | Force altitude: immediately set altitude to 5,000 ft |
| `SPDN 250` | Force speed: immediately set IAS to 250 knots |
| `TRATE 3` | Set turn rate: override default turn rate to 3°/sec (range 0.5–45; omit argument to clear) |

These commands set the aircraft's state immediately — no gradual transition. Useful for RPO corrections when an aircraft is in the wrong state. `TRATE` overrides the category-based turn rate for fine control over vectoring behavior.

### Warp Commands

Teleport an aircraft to a specific position:

| Command | Effect |
|---------|--------|
| `WARP OAK005002 020 050 120` | Warp to OAK 005°/2nm, heading 020, altitude 5,000 ft, speed 120 kts |
| `WARP SJC 180 100 250` | Warp to SJC fix, heading 180, altitude 10,000 ft, speed 250 kts |
| `WARPG C B` | Warp to the intersection of taxiways C and B (ground aircraft only) |
| `WARPG #42` | Warp to node ID 42 (ground aircraft only; use Ctrl+D debug overlay to find IDs) |
| `WARPG @B12` | Warp to parking spot B12 (ground aircraft only) |

**WARP** accepts a fix name or [FRD](#fix-radial-distance-frd) as the position, followed by heading (1-360), altitude (shorthand hundreds), and speed (knots).

**WARPG** accepts either two taxiway names (finds their intersection) or a node ID reference (`#nodeId`). Use the Ground View debug overlay (Ctrl+D) to find node IDs.

### Say Commands

Make an aircraft broadcast information:

| Command | Effect |
|---------|--------|
| `SAY text` | Aircraft broadcasts the text verbatim (alias: `SAYF`) |
| `SSPD` | Aircraft reports current speed (includes Mach at/above FL240) |
| `SMACH` | Aircraft reports current Mach number |
| `SEAPP` | Aircraft reports expected approach (or "No expected approach assigned") |
| `SALT` | Aircraft reports altitude and vertical trend |
| `SHDG` | Aircraft reports heading (includes direct-to fix if navigating) |
| `SPOS` | Aircraft reports position relative to nearest fix |

---

## Glossary

| Term | Definition |
|------|-----------|
| **ARTCC** | Air Route Traffic Control Center — a facility managing a region of airspace (e.g., ZOA = Oakland Center) |
| **CIFP** | Coded Instrument Flight Procedure — FAA database of instrument approaches, SIDs, and STARs |
| **CRC** | Consolidated Radar Client — the VATSIM radar client that students use |
| **ERAM** | En Route Automation Modernization — the FAA's center radar system |
| **FAF** | Final Approach Fix — the point where the final descent to the runway begins |
| **IAF** | Initial Approach Fix — the starting point of an instrument approach |
| **IFR** | Instrument Flight Rules — flight conducted under instrument procedures |
| **MAP** | Missed Approach Point / Missed Approach Procedure |
| **RPO** | Remote Pilot Operator — a YAAT user who controls simulated aircraft |
| **SID** | Standard Instrument Departure — a published departure procedure |
| **STAR** | Standard Terminal Arrival Route — a published arrival procedure |
| **STARS** | Standard Terminal Automation Replacement System — the FAA's terminal radar system |
| **TCP** | Terminal Control Position — a sector identifier in STARS (e.g., "2B") |
| **VFR** | Visual Flight Rules — flight conducted visually without instrument procedures |

### Fix-Radial-Distance (FRD)

A compact format for specifying a point in space relative to a navigation fix: `{fix}{radial:3}{distance:3}`.

- `OAK005002` = 005° radial from OAK at 2 NM
- `SJC090015` = 090° radial from SJC at 15 NM
- `JFK270050` = 270° radial from JFK at 50 NM

The fix name must be 2+ characters, followed by exactly 3 digits for the radial and 3 digits for the distance. FRD works anywhere a fix name is accepted (DCT, AT conditions, WARP, etc.).
