# YAAT Command Reference

Complete reference for all YAAT commands. For a quick introduction to issuing commands, see [Getting Started](GETTING_STARTED.md#step-5-issue-your-first-command). For student-oriented solo workflow, see the [Solo Training Guide](SOLO_TRAINING.md).

## Table of Contents

- [Solo Training Command Differences](#solo-training-command-differences)
- [Command Basics](#command-basics)
  - [Selecting an Aircraft](#selecting-an-aircraft)
  - [Syntax](#syntax)
  - [Altitude Arguments](#altitude-arguments)
  - [Runway Designators](#runway-designators)
  - [Command Chaining](#command-chaining)
  - [Conditional Blocks](#conditional-blocks)
  - [Wait Commands](#wait-commands)
  - [The Conditional List](#the-conditional-list)
- [Quick Reference](#quick-reference)
  - [Heading](#heading)
  - [Altitude / Speed](#altitude--speed)
  - [Squawk / Transponder](#squawk--transponder)
  - [Navigation](#navigation)
  - [Ground](#ground)
  - [Helicopter](#helicopter)
  - [Hold](#hold)
  - [Approach / Procedures](#approach--procedures)
  - [Tower](#tower)
  - [Pattern](#pattern)
  - [Track Operations](#track-operations)
  - [Display Operations](#display-operations)
  - [Strip / Data Operations](#strip--data-operations)
  - [vTDLS (Pre-Departure Clearance)](#vtdls-pre-departure-clearance)
  - [ASDE-X Display State](#asde-x-display-state)
  - [Flight Plan](#flight-plan)
  - [Consolidation](#consolidation)
  - [Sim Control](#sim-control)
- [Detailed Command Documentation](#detailed-command-documentation)
  - [Ground Commands](#ground-commands)
  - [Helicopter Commands](#helicopter-commands)
  - [Tower Commands](#tower-commands)
    - [CTO Departure Modifiers](#cto-departure-modifiers)
  - [Pattern Commands](#pattern-commands)
  - [Approach Options](#approach-options)
  - [Approach Control Commands](#approach-control-commands)
  - [Direct To (DCT)](#direct-to-dct)
  - [Navigation Commands](#navigation-commands)
  - [Speed Management](#speed-management)
  - [Holding Patterns](#holding-patterns)
  - [Hold Commands](#hold-commands)
  - [Track Operations](#track-operations-1)
    - [Active Position](#active-position)
    - [Track Commands](#track-commands)
    - [Note](#note)
    - [Half-Strips](#half-strips)
    - [vTDLS (Pre-Departure Clearance)](#vtdls-pre-departure-clearance-1)
    - [Coordination (Rundown List)](#coordination-rundown-list)
  - [Consolidation](#consolidation-1)
  - [Delayed Aircraft Commands](#delayed-aircraft-commands)
  - [Hold for Release (HFR / REL)](#hold-for-release-hfr--rel)
  - [Timer (TIMER / TMR)](#timer-timer--tmr)
  - [Add Aircraft (ADD)](#add-aircraft-add)
  - [Global Commands](#global-commands)
  - [Auto-Delete on Hold-Short](#auto-delete-on-hold-short)
  - [Force Override Commands](#force-override-commands)
  - [Warp Commands](#warp-commands)
  - [Say Commands](#say-commands)
- [Glossary](#glossary)
  - [Fix-Radial-Distance (FRD)](#fix-radial-distance-frd)

## Solo Training Command Differences

Most commands work the same in Solo Training and RPO mode. The differences below matter because Solo Training uses simulated pilot speech and Session Report evidence, while RPO mode keeps convenience shortcuts for a human operator.

| Situation | Solo Training form | RPO mode shortcut or behavior | Notes |
|-----------|--------------------|-------------------------------|-------|
| Traffic advisory | `RTIS <clock> <miles> <direction> <type> [altitude]` | `RTIS <callsign>`, bare `RTIS`, and `RTISF` are RPO conveniences | The structured form records the advisory content for Session Report scoring. Matching is tolerant — it resolves the best-matching traffic within realistic bands, altitude is optional, and a within-tolerance but imprecise call still counts (adds a low-severity coaching note). Accepted soft-fails still count as advisory proof because the instruction was issued. |
| Field advisory | `RFIS <clock> <miles>` | Bare `RFIS` and `RFISF` are RPO conveniences | The structured form gives the pilot field-position information, records visual-approach field proof, and runs the normal field-acquisition flow. |
| Safety alert | `SAFAL <clock> <miles> [L\|R] [C\|D]` | Same syntax | Resolves a target by clock position and whole-mile distance. It proves a safety alert for that recipient-target pair, but does not set traffic in sight or satisfy `FOLLOW`. |
| Wake advisory | `CWT`, `CTO ... CWT`, or `CLAND [NODEL] CWT` | Same syntax | Records caution-wake-turbulence proof. Bare `CWT` proves only when the Session Report sees exactly one current wake-advisory context for that aircraft. |
| VFR Class B entry | `CLBRV`, `CBRV`, or `BRAVO` | Same syntax | In Solo Training, this satisfies the VFR Class B entry gate. |
| Class C contact without a maneuver | `STBY`, `STANDBY`, `ROGER`, or `RGR` | Same syntax | In Solo Training, this can establish two-way communications for Class C when targeted at that aircraft. It does not satisfy a separate pending pilot request. |
| Visual follow | Structured `RTIS` first, then `FOLLOW` or `CVA ... FOLLOW` | RPO mode can use forced traffic-in-sight shortcuts | The aircraft must have reported the traffic in sight before visual follow behavior can start. |

Solo Training rejects the RPO-only forms with guidance such as `Use RTIS <clock> <miles> <direction> <type> [altitude] in solo training` or `RFISF is RPO-only; use RFIS <clock> <miles> in solo training`.

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

### Runway Designators

Runways are written in FAA form, with the leading zero dropped for single-digit runways — type `8R` and `9`, not `08R` and `09`. Both are accepted everywhere a runway is taken (taxi, runway crossings, takeoff and landing clearances, and approaches, where `I8R` resolves the same as `I08R`), and YAAT displays runways the same way — on the radar datablock, in the aircraft list and context menus, on the ground map and hold-short labels, in pilot read-backs, and in the Session Report.

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
  Climb and turn simultaneously. Once the lateral part of the first block is complete, turn to heading 180; the climb continues if it has not reached 5,000 yet. For altitude-specific sequencing, use `LV` or numeric `AT`.

- **Combined:**
  ```
  CM 014, FH 090; FH 180; DCT MYCOB
  ```
  Climb to 1,400 ft and turn to 090. Then turn to 180. Then proceed direct MYCOB.

- **Word aliases (`AND` / `THEN`)** — `AND` is a case-insensitive alias for `,` and `THEN` is a case-insensitive alias for `;`. Both are substituted at parse time, so `CM 014 AND FH 090 THEN FH 180` is treated exactly like `CM 014, FH 090; FH 180`. The aliases are skipped inside `SAY` / `SAYF` literal text, so `SAYF READING YOU LOUD AND CLEAR` is transmitted verbatim.

### Conditional Blocks

Use `LV` (level at altitude) and `AT` (at fix) to trigger blocks on specific conditions instead of waiting for the previous block. Conditional blocks in a chain are watched while the earlier block continues, until YAAT reaches an ordinary untriggered block:

- **LV** — triggers when the aircraft reaches an altitude:
  ```
  LV 050 FH 270
  ```
  When reaching 5,000 ft, turn to heading 270.

- **AT** — triggers when the aircraft reaches a fix, intercepts a radial, reaches an [FRD](#fix-radial-distance-frd) point, or arrives at a ground entity (taxiway, named spot, parking, or two-taxiway intersection):
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

  **Ground entities** (only fire while taxiing — the same `$` and `@` sigils used by `TAXI`/`PUSH`):

  | Form | Meaning | Example |
  |------|---------|---------|
  | `AT <taxiway>` | Fires the first time the aircraft turns onto that taxiway | `AT B SPD 10` |
  | `AT $<spot>` | Fires when the aircraft reaches the named spot/intersection node | `AT $5 RNS` |
  | `AT @<parking>` | Fires when the aircraft reaches the named parking spot | `AT @TERM2 FCA` |
  | `AT <t1>/<t2>` | Fires at the node where two named taxiways meet (closest to the aircraft when issued) | `AT B/C SPD 5` |

  **Disambiguation order:** sigils win first (`$` spot, `@` parking, `/` intersection), then bare digits stay altitudes (so `AT 30` is still 3,000 ft — to target SFO spot `30` use `AT $30`), then bare names try airborne fixes (`AT SUNOL`) before falling back to a taxiway. If the resolved entity is not present in the airport layout, the block is rejected with a `AT ground entity not found` warning.

- **GIVEWAY** / **BEHIND** — as a *condition prefix* (callsign followed by a command), gates that command until the named aircraft no longer conflicts (ground only):
  ```
  GIVEWAY SWA5456 TAXI S T U
  ```
  Wait for SWA5456 to pass, then taxi via S, T, U. Works with compound chains:
  ```
  BEHIND DAL423; TAXI S T U W
  ```
  On its own — `GIVEWAY SWA123` — it is instead an immediate instruction to give way to (yield to) that traffic on the current taxi route until it passes (see the Ground command table). You can also append it to a taxi clearance: `TAXI A A1 1R GIVEWAY KLM605` (comma optional) taxis via A, A1 to runway 1R while giving way to KLM605.

These work within compound chains:

```
CM 100; LV 050 FH 270; LV 100 DCT SUNOL
```

Climb to 10,000 ft. At 5,000 ft, turn to heading 270. At 10,000 ft, proceed direct SUNOL.

If you put a condition after a comma, YAAT promotes it to a new sequential block, so `CM 020, AT OAK30NUM CM 014` is treated like `CM 020; AT OAK30NUM CM 014`.

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

```
AT TTE WAIT 170 DM 110
```
On crossing TTE, wait 170 seconds, then descend and maintain 11,000 ft. A `WAIT` placed after a fix or altitude condition starts counting once that condition is met, so the delay runs from the moment the aircraft reaches TTE — not from when the command was issued.

### The Conditional List

Commands gated by a precondition — `AT`/`LV` (altitude or fix), `ONHO` (on handoff), `ATFN` (intercept), `BEHIND` (give-way), and `WAIT`/`WAITD` (time/distance) — accumulate into a single **conditional list**: an unordered set of pending instructions that each fire when their own trigger is met. Conditionals are **additive** — issuing one never cancels the others. A scenario or controller can pre-load `WAIT 120 RWY 18L TAXI N B`, `ONHO CM 120`, and `AT 6000 DCT MUNCH` on a departure and all three stand: it taxis at the 120s mark, climbs on handoff, and turns direct at 6,000 ft.

A freshly-issued **immediate** command (one with no precondition) still supersedes: it clears the conditional list and replaces the conflicting active control surface. When you re-issue an immediate command that conflicts with one already in flight, YAAT supersedes only the **same control surface** (altitude, lateral, or speed) — orthogonal active targets survive. For example, after `DM 020, DCT VPCOL`, re-issuing `DM 025` updates the altitude target but leaves `DCT VPCOL` flying.

`SHOWAT` (alias `SHOWCOND`) lists the conditional list with each entry's live status; the same entries appear in the **Pending Cmds** column. To wipe the whole list, issue **`CXL`** (or `CLR`, `DELAT`, `DELCOND`, `DC`) as a follow-up command:

```
DM 025
CXL
```

`CXL` removes every pending conditional but does not touch the aircraft's currently active commands. To also drop an active `DCT` or heading hold, add `FPH` (fly present heading) before clearing:

```
DM 025, FPH
CXL
```

Use `DELAT 3` (or `DELCOND 3` / `DC 3` / `CXL 3` / `CLR 3`) to remove a specific conditional by its 1-based index from `SHOWAT` — including a pending `WAIT`/`BEHIND` deferral, not just precondition-gated queue blocks.

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
| Climb/maintain VFR at or above | `CM A025` | — | — |
| Climb/maintain VFR at or below | `CM B055` | — | — |
| Descend and maintain | `DM 050` | — | `DM050` |
| Speed | `SPD 250` | `SPEED`, `DS`, `IS`, `SLOW`, `SL` | `SPD250` |
| Speed (force, override 5nm final) | `SPEEDF 180` | `SPDF`, `SLF` | — |
| Speed floor | `SPD 210+` | — | — |
| Speed ceiling | `SPD 210-` | — | — |
| Resume normal speed | `RNS` | `NS` | — |
| Expedite climb/descent (or taxi/runway exit) | `EXP [alt]` | — | — |
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

**Automatic beacon-code assignment.** YAAT picks a discrete code for an aircraft in two places: when the aircraft **spawns with a flight plan** (`ADD` IFR, the arrival generator, scenario-file aircraft) and when a **flight plan is filed or amended** without an explicit code (`FP`, `VP`, `DA`). Both draw from the facility's beacon-code banks in the ARTCC config — IFR traffic from the `Ifr` banks, VFR traffic from the `Vfr` banks, each falling through to the `Any` banks — and drop to sequential octal codes from `0001` when the facility defines no matching bank or the bank is exhausted. Every assigned code is tracked, so no two live aircraft hold the same one. An aircraft that spawns **without** a flight plan (a VFR cold call) gets no assigned code and squawks `1200`.

Codes that would raise a false indication on a controller's scope are never assigned automatically, and `RANDSQ` never picks one either:

| Withheld | Why | Reference |
|----------|-----|-----------|
| Any code ending in `00` — `1200`, `4000`, `7500`, `7600`, `7700`, and every block code | Non-discrete | 7110.65 §5-2-3 … §5-2-7 |
| The entire `7500`–`7777` block | Every discrete code in the hijack, radio-failure, and emergency series raises the SPC / RF / EMRG indicator in STARS and ERAM — not only the three ending in `00` — and `7777` is the military interceptor code | AIM §4-1-20 |
| `1202`, `1203`, `1255`, `1276`, `1277` | VFR conspicuity codes ATC monitors: gliders, formation lead, firefighting, and SAR | 7110.65 §5-2-11 |
| `5000`–`5062` | DoD-allocated block | FAA Order JO 7110.66 (NBCAP) |

The restriction covers only codes YAAT chooses on its own. `SQ {code}` still makes an aircraft squawk any code you name — including a reserved one, which is how you stage a 7600 or 7700 scenario — and an explicit code typed into `FP` / `VP` / `DA` is honored as-is. Note that `RANDSQ` changes only the code the aircraft is *squawking*, simulating a pilot dialing the wrong code; the aircraft's assigned code is untouched, so `SQ` with no argument returns it to the assigned code.

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
| Resume taxi | `RES` | `RESUME` | `RES CROSS 28R 28L HS 20` |
| Cross runway/HS | `CROSS 28R 28L` | `CROSS` (bare) | `CROSS 28R HS 20` |
| Clear the runway | `CLRWY` | `CLEARRWY` | — |
| Hold short | `HS B` | — | — |
| Assign runway | `RWY 30` | — | — |
| Exit left | `EL` | `EXITL` | `EL W2 EXP` |
| Exit right | `ER` | `EXITR` | `ER W5 EXP` |
| Exit taxiway | `EXIT A3` | — | `EXIT A3 EXP` |
| Follow (ground) | `FOLLOWG SWA123` | `FOLG` | — |
| Give way | `GIVEWAY SWA123` | `BEHIND`, `GW` | `TAXI A A1 1R GIVEWAY KLM605` |
| Taxi all | `TAXIALL 30` | — | — |
| Break conflict | `BREAK` | — | — |

### Helicopter

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Takeoff present pos (hover & hold) | `CTOPP` | `CTOPP +002` | — |
| Takeoff present pos heading | `CTOPP 270` | `CTOPP H270`, `CTOPP LT270`, `CTOPP RT270` | — |
| Takeoff present pos on course | `CTOPP OC` | — | — |
| Takeoff present pos direct | `CTOPP DCT FIX` | `CTOPP TLDCT FIX`, `CTOPP TRDCT FIX` | — |
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
| Forced approach | `CAPPF ILS28R` | `JAPPF`, `PTACF` | — |
| Pos/Turn/Alt/Clr | `PTAC 280 025 ILS30` | `PTAC PH PA` | — |
| Forced PTAC | `PTACF 280 025 ILS30` | `PTACF PH PA` | — |
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
| Follow traffic | `FOLLOW UAL456` / `FOLLOW` | `FOL` | Callsign optional — bare `FOLLOW` defaults to last in-sight traffic |
| Follow traffic (forced) | `FOLLOWF UAL456` / `FOLLOWF` | `FOLF` | RPO-only; folds `RTISF` in, no prior `RTIS` needed. Bare form follows a pending/last-called `RTIS` traffic |
| Visual approach | `CVA 28R` | `VISUAL` | Requires field-in-sight first (`RFIS`) |
| Visual approach (forced) | `CVAF 28R` | `VISUALF` | RPO-only; folds `RFISF` (and `RTISF` when following) in |
| Report field | `RFIS 11 18` | `RFIS` (RPO shorthand) | Descriptive form required in solo training |
| Report field (forced) | `RFISF` | — | RPO-only |
| Report traffic | `RTIS 3 5 W B737 024` | `RTIS` (RPO shorthand) | Descriptive form required in solo training; altitude optional (`RTIS 3 5 W B737`) |
| Report traffic (relative) | `RTIS NR 2 C172` | — | VFR form: position off the nose (`NOSE`/`NL`/`NR`/`L`/`R`/`LR`/`RR`/`TAIL`) `<miles> <type>` |
| Report traffic (pattern) | `RTIS BASE R 2 28R M20P` | — | VFR form: `<leg> <L\|R> <miles> <rwy> <type>` (leg `UW`/`XW`/`DW`/`BASE`/`FINAL`; omit side for `FINAL`) |
| Report traffic (landmark) | `RTIS OVER VPCOL C172` | — | VFR form: `OVER <fix/VFR point> <type>` |
| Report traffic (forced) | `RTISF UAL456` / `RTISF` | — | RPO-only; bare form folds in a pending `RTIS` call |
| Report position | `REPORT BASE` / `REPORT 5 FINAL` / `REPORT SUNOL` | — | Pilot reports the event when it happens; pattern-leg reports repeat each circuit. Cancel with `REPORT OFF [leg]` |
| Safety alert | `SAFAL 12 1 L D` | — | Optional `L`/`R` and `C`/`D` action tokens |
| Wake advisory | `CWT` | — | Caution wake turbulence; phase-transparent proof command |
| List approaches | `APPS` | `APPS OAK` | — |

### Tower

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Line up and wait | `LUAW` | `POS`, `LU`, `PH` | Optional `WD`/`ND`/`IMM` suffix (without delay) |
| Cleared for takeoff | `CTO` | — | Optional `CWT` and/or `IMM`/`WD`/`ND` (immediate) suffix |
| Cancel takeoff | `CTOC` | — | — |
| Cleared to land | `CLAND [runway]` | `CL`, `FS` | Optional runway; optional `CWT` suffix. A following aircraft can be cleared before it has a runway. During a low approach (`LA`), `CLAND <divergingRunway>` turns onto and lands the other runway ("change to runway N"). |
| Force landing | `CLANDF` | — | RPO-only override: implies clearance + forces touchdown, suppressing automatic go-arounds — or cancelling one already underway |
| Land and hold short | `LAHSO` | — | — |
| Cancel landing | `CLC` | `CTLC` | — |
| Go around | `GA` | — | — |
| Touch and go | `TG` | — | — |
| Stop and go | `SG` | — | — |
| Begin takeoff (stop-and-go) | `GO` | — | — |
| Low approach | `LA` | — | — |
| Cleared for option | `COPT` | — | — |

### Pattern

> **VFR only:** Pattern commands, traffic direction, VFR holds, touch-and-go, stop-and-go, low approach, and cleared-for-option are restricted to VFR aircraft. IFR aircraft must first be given `CIFR` (Cancel IFR) to become VFR. The report commands `RFIS`/`RTIS` are available to both IFR and VFR aircraft. **Visual approaches (`CVA`) are IFR only** — VFR pattern aircraft join the pattern via the pattern-entry commands (`ELD`/`ERD`/`SI` etc.) above.

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
| Extend leg | `EXT` | `EXTEND` | `EXT [leg]` |
| Short approach | `SA` | `MSA` | — |
| Normal approach | `MNA` | — | — |
| Left 360 | `L360` | `ML3`, `ML360` | — |
| Right 360 | `R360` | `MR3`, `MR360` | — |
| Left 270 | `L270` | — | — |
| Right 270 | `R270` | — | — |
| Cancel 270 | `NO270` | — | — |
| Plan 270 | `P270` | `PLAN270` | — |
| Pattern size | `PS 1.5` | `PATTSIZE` | — |
| S-turns (init L) | `MLS` | — | — |
| S-turns (init R) | `MRS` | — | — |
| Offset pattern L | `OFL` | `OFFSETL` | `OFL [nm]` |
| Offset pattern R | `OFR` | `OFFSETR` | `OFR [nm]` |
| Circle airport | `CA` | `CIRCLE` | — |

### Track Operations

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Track | `TRACK` / `TRACK 3Y` | — | — |
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
| Contact next controller | `CT OAK_TWR` / `CT 121.9` / `CT 3O` / `CT` | `CONT` | — |
| Frequency change approved | `FCA` | — | — |
| Cleared Bravo airspace | `CLBRV` | `CBRV`, `BRAVO` | — |
| Acknowledge pilot contact | `STBY` | `STANDBY`, `ROGER`, `RGR` | — |
| Ghost track (runway) | `GHOST N12345 28R` | — | Global; callsign in args |
| Ghost track (airport+runway) | `GHOST N12345 KOAK 28R` | — | Global; callsign in args |
| Ghost track (lat/lon) | `GHOST N12345 37.7 -122.2` | — | Global; callsign in args |

### Display Operations

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Leader direction | `LDR 3` | — | — |
| J-Ring | `JRING 3` (radius nm) / `JRING` (clear) | — | — |
| Cone | `CONE 5` (length nm) / `CONE` (clear) | — | — |

### Strip / Data Operations

| Command | Primary | Aliases | Concatenated |
|---------|---------|---------|-------------|
| Annotate strip box | `AN 3 RV` | `ANNOTATE`, `BOX` | Optional id-form: `AN STRIP_<id> 3 RV` targets a specific strip (e.g. a scanned copy) |
| Push strip to bay | `STRIP Ground/1/1` | — | Slash-compound `bay[/rack[/index]]`, 1-based. Optional id-form: `STRIP STRIP_<id> Ground/1/1` |
| Scan strip to external bay | `SCAN NCT/1` | — | Copies strip to external bay; original stays put |
| Delete strip | `STRIPD` | — | Optional id-form: `STRIPD STRIP_<id>` (required to remove a scanned copy) |
| Toggle strip offset | `STRIPO` | — | Optional id-form: `STRIPO STRIP_<id>` |
| Create half-strip | `HSC Ground/1 Hello\World` | `HALFSTRIPCREATE` | — |
| Amend half-strip | `HSA Hello\Updated\Body` | `HALFSTRIPAMEND` | Also accepts `HSA HSTRIP_<id> ...` (UI default — disambiguates duplicate first-line text) |
| Delete half-strip | `HSD Hello` | `HALFSTRIPDEL` | Also accepts `HSD HSTRIP_<id>` (UI default) |
| Scratchpad 1 | `SP1 OAK` / `SP1` (clear) | — | Max 3 chars (4 if the facility enables 4-char scratchpads); longer entries are rejected with `FORMAT`. |
| Scratchpad 2 | `SP2 I8R` / `SP2` (clear) | — | Max 3 chars (4 if the facility enables 4-char scratchpads); longer entries are rejected with `FORMAT`. |
| Note | `NOTE Watch wake` / `NOTE` (clear) | — | Instructor freetext datablock note (max 40 chars, preserves case/spaces). Shown as an extra amber line on the radar and ground datablocks; not sent to CRC. |
| Temp altitude | `TEMPALT 120` | `TA`, `TEMP`, `QQ` | — |
| Cruise | `CRUISE 240` | `QZ` | — |
| Pilot reported altitude | `PRA 250` / `PRA 0` (clear) | — | — |
| On-handoff | `ONHO` | `ONH` | — |
| On hold-short | `ONHS` | — | Condition prefix only — use as `ONHS DEL` for auto-delete on reaching the hold-short after landing. |

### vTDLS (Pre-Departure Clearance)

See [docs/vtdls.md](docs/vtdls.md) for the full lifecycle and pilot-side
semantics. Queue is auto-emitted on flight-plan creation at a TDLS-
configured facility, so controllers normally only see Send / Wilco / Dump.

| Command | Primary | Aliases | Notes |
|---------|---------|---------|-------|
| Queue PDC | `TDLSQ` | — | Auto-fired internally; controllers rarely type this. |
| Send PDC | `TDLSS Expect\|Sid\|Transition\|Climbout\|Climbvia\|InitialAlt\|ContactInfo\|DepFreq\|LocalInfo` | — | Nine `\|`-separated fields, empty between separators = null. Mandatory fields gated by facility config. |
| Force PDC Wilco | `TDLSW` | — | Manually marks Sent → Wilco. Auto-fires ~3 s after Send. |
| Dump PDC | `TDLSDUMP` | `TDLSD` | Removes the PDC; (facility, callsign) locks out further auto-queueing this session. Clearance must now be issued by voice. |

### ASDE-X Display State

These mutate ASDE-X display state only; they never change the underlying scenario callsign, transponder code, aircraft type, or filed destination. CRC's ASDE-X DB editor (`Y`/`H` keys, F3/F4/F5/F12) sends the same mutations from the controller side.

| Command | Primary | Aliases | Notes |
|---------|---------|---------|-------|
| ASDE-X Scratchpad 1 | `ASDXSP1 OUT` / `ASDXSP1` (clear) | — | Independent of STARS `SP1`. |
| ASDE-X Scratchpad 2 | `ASDXSP2 R` / `ASDXSP2` (clear) | — | Independent of STARS `SP2`. |
| ASDE-X Callsign override | `ASDXCS UAL238` / `ASDXCS` (clear) | — | Display only; sim callsign unchanged. |
| ASDE-X Beacon override | `ASDXBCN 4321` / `ASDXBCN` (clear) | — | Empty clears beacon on display; unparseable falls back to sim transponder. |
| ASDE-X Category override | `ASDXCAT B` / `ASDXCAT` (clear) | — | Wake category. Falls back to derived CWT when cleared. |
| ASDE-X Aircraft type override | `ASDXTYPE B738` / `ASDXTYPE` (clear) | — | Display only. |
| ASDE-X Fix override | `ASDXFIX SEGUL` / `ASDXFIX` (clear) | — | Display only; falls back to AsdexConfig fix rules when cleared. |
| Tag (untermination) | `ASDXTAG` | — | Clears terminated bit so the per-tick reconciliation re-emits. |
| Terminate | `ASDXTERM` | — | Hides track on ASDE-X; emits one-shot delete to clients. |
| Suspend | `ASDXSUSP` | — | Sets `Status=Suspended` on ASDE-X track DTO. |
| Unsuspend | `ASDXUSUS` | — | Restores `Status=Associated`. |
| Inhibit alerts | `ASDXINHIB` | — | Suppresses safety-logic alerts for this aircraft on ASDE-X. |
| Enable all alerts (global) | `ASDXALERTS` | — | Clears alert inhibits across all aircraft in the room. |

### Flight Plan

| Command | Primary | Aliases | Notes |
|---------|---------|---------|-------|
| Change destination | `APT KSFO` | `DEST` | Changes aircraft destination airport. Accepts FAA (`OAK`) or ICAO (`KOAK`); resolves to canonical ICAO. Rejects unknown airports. |
| Create IFR flight plan | `FP B738 220 KBOS DCT KJFK` | — | Altitude in hundreds (220 = FL220). Type accepts equipment suffix (`B738/L`) — split into AircraftType and FaaEquipmentSuffix. Departure/destination accept FAA (`OAK`) or ICAO (`KOAK`) — resolved to canonical ICAO when recognized; an unrecognized identifier (e.g. a non-US airport like `WSSS`) is kept as typed rather than rejected. Single-token route is treated as destination only (`FP C172 050 MOD`). Auto-assigns a discrete beacon code if none was typed in the command; pilot keeps squawking their previous code until told `SQ`. |
| Create VFR flight plan | `VP C172 5500 KOAK DCT KJFK` | — | Altitude absolute (5500 = 5,500 ft). Type accepts equipment suffix (`C172/G`) — split into AircraftType and FaaEquipmentSuffix. Departure/destination accept FAA (`MOD`) or ICAO (`KMOD`) — resolved to canonical ICAO when recognized; an unrecognized identifier (e.g. a non-US airport like `WSSS`) is kept as typed rather than rejected. Single-token route is treated as destination only (`VP C172 5500 MOD`). Auto-assigns a discrete beacon code; pilot keeps squawking their previous code until told `SQ`. |
| Flight Data (abbreviated FP) | `DA C172 065 4304` | — | CRC F6 key. Optional fields in any order: type (with optional equipment suffix `C172/G`), altitude (hundreds), beacon code, scratchpad (`` `VFF ``), flight rules (`.V`/`.E`). Creates VFR FP by default. Errors with DUP NEW ID if aircraft already has a flight plan. Auto-assigns a discrete beacon code if none was typed; pilot keeps squawking their previous code (e.g. 1200) until told `SQ`. |
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
| Auto-delete on hold-short | `ONHS DEL` | — | Queues a delete that fires when the aircraft reaches HoldingAfterExit after landing. Datablock shows a trailing `*` while armed. |
| Cancel auto-delete | `NODEL` | — | Strips any queued `ONHS DEL` and re-arms `AutoDeleteExempt` so scenario-level auto-delete also won't touch the aircraft. Distinct from the `NODEL` *modifier* on `CLAND`/`TAXI`/`EL`/`ER`/`EXIT`/`LAND`, which sets the exempt flag at the time those commands are issued. |
| Delete conditional(s) | `DELAT` / `DELAT 2` | `DELCOND`, `DC`, `CXL`, `CLR` | — |
| Show conditional list | `SHOWAT` | `SHOWCOND` | — |
| Say | `SAY text` | `SAYF` | — |
| Say speed | `SSPD` | — | Aircraft reports current speed (includes Mach at/above FL240) |
| Say mach | `SMACH` | — | Aircraft reports current Mach number |
| Say expected approach | `SEAPP` | — | Aircraft reports expected approach |
| Say altitude | `SALT` | — | Aircraft reports current and target altitude |
| Say heading | `SHDG` | — | Aircraft reports heading (includes direct-to fix if navigating) |
| Say position | `SPOS` | — | Aircraft reports position relative to a route/DCT fix or sizeable airport |
| Spawn now | `SPAWN` | — | — |
| Spawn delay | `DELAY 120` | — | — |
| Hold for release | `HFR SJC` | — | Arms hold-for-release for an airport's IFR departures (global) |
| Disarm hold for release | `HFROFF SJC` | — | Auto-releases anything still held (global) |
| Release departure | `REL SJC` | `CTOA` | `REL N123` releases a specific aircraft; `REL SJC 2` releases the field's queue 2 min apart (global) |
| Call for release | `CFR 1830` | — | Marks the selected departure released with a −2/+1 min CFR window; alerts the instructor if it departs outside it. `CFR` = immediate release; `CFR OFF` clears; `CFR CHECK` prints the window status |
| Wait (seconds) | `WAIT 30` | — | — |
| Wait (distance) | `WAITD 4` | — | — |
| Timer | `TIMER 5:00 text` | `TMR` | Countdown reminder; on expiry posts a green SAY (`text`, or `timer expired`). Global, or prefix a callsign. `TIMER CANCEL <id\|ALL>` cancels |
| Add aircraft | `ADD IFR H J ...` | — | — |
| Force heading | `FHN 270` | — | — |
| Force altitude | `CMN 240` | — | — |
| Force speed | `SPDN 250` | `SLN`, `SPEEDN` | — |
| Set turn rate | `TRATE 3` | — | — |
| Warp | `WARP FRD [hdg] [alt] [spd]` | — | — |
| Warp ground | `WARPG location` | — | — |

---

## Detailed Command Documentation

### Ground Commands

| Command | Effect |
|---------|--------|
| `PUSH` | Push back from parking (reverse at ~5 kts) |
| `PUSH FACE E` / `PUSH >E` | Push back facing east (cardinal direction) |
| `PUSH TAIL W` / `PUSH <W` | Push back with the tail pointing west (equivalent to `FACE E`) |
| `PUSH A` | Push back onto taxiway A |
| `PUSH TE FACE E` | Push back onto TE, aligning with whichever direction of TE is closest to east |
| `PUSH TE TAIL W` | Same — `TAIL W` and `FACE E` resolve to the same alignment |
| `PUSH TE T` | Push back onto taxiway TE, facing toward taxiway T |
| `PUSH @4A` | Push back directly to gate 4A (straight/curved reverse, use parking heading) |
| `PUSH @4A A` | Push back to gate 4A, face toward taxiway A |
| `PUSH @4A FACE NE` | Push back to gate 4A, facing northeast |
| `PUSH $7A` | Push onto spot 7A — reverse past it then pull forward, ending lined up **nose-out** toward the parent taxiway with the nosewheel on the mark |
| `PUSH $7A TAIL W` | Push onto spot 7A with tail pointing west (= face east), overriding the default nose-out facing |
| `TAXI S T U W W1` | Taxi via taxiways S, T, U, W, W1 |
| `TAXI T U W 30` | Taxi via T, U, W to runway 30 |
| `TAXI T U W RWY 30` | Same as above (explicit RWY keyword) |
| `RWY 30 TAXI T U W` | Same as above (RWY-first syntax) |
| `TAXI S T U HS 28L` | Taxi via S, T, U with explicit hold-short at runway 28L |
| `TAXI D C HS E RWY 28R` | Taxi via D, C, hold short of **taxiway** E, then continue to runway 28R. A taxiway named as a hold-short target also steers the route — it is taxied through (D→C→E), not detoured around — so this is equivalent to `TAXI D C E HS E RWY 28R`. (Runway hold-short targets like `HS 28L` do not add a routing waypoint.) |
| `TAXI S T U @B12 NODEL` | Taxi via S, T, U to parking B12 (exempt from auto-delete) |
| `TAXI #42 #18 #95` | Taxi via exact node IDs (used by draw route; see Ground View debug overlay) |
| `TAXI A #42 B` | Mixed: walk taxiway A, A* to node 42, walk taxiway B |
| `TAXI >A B <C D` | Taxi via A, B, C, D with turn-direction hints: right onto A, left onto C (the `>`/`<` glyph biases which way the aircraft turns onto that taxiway) |
| `TAXI 28R G D` | Taxi **along** runway 28R, then taxiways G, D — a runway named as a path segment is taxied along its centerline (e.g. back-taxi). The aircraft taxis straight onto the cleared runway; a *different* runway the route crosses still holds short. |
| `HOLD` / `HP` | Hold position — stop wherever on the ground. While the aircraft is taxiing into position after `LUAW`, it stops where it is instead of continuing onto the centerline; resume the line-up with `LUAW` or `CTO` (not `RES`). Rejected once the takeoff roll has begun — use `CTOC` to cancel the takeoff clearance. |
| `RES` / `RESUME` | Resume taxi after HOLD or release a runway hold-short (explicit or crossing). Does not apply at the destination runway hold, or to an aircraft held while lining up — use CTO or LUAW. |
| `RES CROSS 28R 28L` | Resume taxi AND pre-clear listed crossings on the rest of the route (unordered set). Hold-shorts for any runway NOT in the list still stop the aircraft until a fresh CROSS. Fails the whole command if a listed runway has no matching upcoming crossing, or if it appears only as the destination runway. |
| `RES HS 20` / `RES HS B` | Resume taxi AND add a hold-short further on the route. Runway targets re-arm the route's entry-side hold-short for that runway, revoking whatever cleared it (AutoCross, an earlier `CROSS`); taxiway targets add a new hold-short at the first matching intersection. Fails the whole command — applying none of the targets — if any target doesn't appear on the upcoming route, or if the aircraft has already entered a named runway. |
| `RES CROSS 28R 28L HS 20` | Combine both modifiers in one command. CROSS and HS are independent and can appear in either order. |
| `CROSS` | Bare — clear the next uncleared hold-short on the route, runway or taxiway, whether the aircraft is already holding short or still taxiing toward it. Clears exactly one hold-short; the aircraft still stops at any subsequent ones. |
| `CROSS 28L` | Cross runway 28L (clears hold-short). Also crosses a runway that was the **destination** of the previous taxi (e.g. `TAXI B 28L` then `CROSS 28L`), whether the aircraft is holding short there or still taxiing toward it — it taxis across to the far side and holds in position. (Use `CTO`/`LUAW` instead to depart from it.) |
| `CROSS 28R 28L` | Clear/pre-clear **multiple** runway crossings in one command — for taxi routes that cross closely-spaced parallels (e.g. B across 28R/10L then 28L/10R at OAK). Satisfies the crossing the aircraft is holding short of and pre-clears the rest so it flows through without stopping. Strict and atomic like `RES CROSS`: every listed runway must be an upcoming crossing on the route and none may be the destination runway (use `CTO`/`LUAW` for that) — otherwise the whole command is rejected and nothing is applied. The destination-runway far-side crossing only works with the single-runway form. |
| `CROSS 28R HS 20` | Cross runway 28R and add a hold-short further on the route in one command. Runways before `HS` are crossings; targets after `HS` are hold-shorts (taxiway or runway) — the same grammar as `RES … CROSS … HS …`, applied through the shared crossing/hold-short engine. |
| `CROSS B` | Cross taxiway B (clears hold-short) |
| `TAXI G CROSS 28R` | Taxi via G, cross 28R, and hold just past it. With no taxiway named past the crossing, the crossed runway sets the direction — the aircraft heads toward and across it (even when G also crosses another runway behind), then holds clear on the far side. |
| `CROSS; HOLD` | Bare CROSS plus chained HOLD: cross the runway and halt right after clearing the far-side hold bars (HOLD fires only when CrossingRunwayPhase completes). |
| `CLRWY` / `CLEARRWY` | Pull an aircraft holding short of a taxiway with its tail over a runway forward until it is just clear of the runway (½ a length past the bars), then hold. Only valid in that tail-over-runway hold — it arises when a hold-short of a taxiway sits closer than the aircraft's own length past a runway it just crossed (e.g. `TAXI G B HS B` across 01L/19R at SFO). Resolves the "runway not clear" warning. |
| `HS B` | Hold short at the next intersection with taxiway B |
| `HS 28L` | Hold short at the entry side of the next runway 28L crossing. Revokes any clearance that crossing already had — AutoCross, an earlier `CROSS 28L`, or the implicit first-crossing clearance — because the hold-short is the most recent instruction. Rejected once the aircraft has entered 28L (use `HOLD`, or issue a new `TAXI`). A no-op when 28L is the assigned departure runway, since the aircraft already holds short of it. |
| `RWY 30` | Assign runway 30 (override runway assignment without taxi) |
| `FOLLOWG SWA123` | Follow another aircraft on the ground |
| `GIVEWAY SWA123` | Give way to (yield to) SWA123 on the current taxi route until it passes. As a condition prefix (`GIVEWAY SWA123 TAXI …`) it instead waits for SWA123 before running the command (see [Conditional Blocks](#conditional-blocks)). Can be appended to a taxi clearance: `TAXI A A1 1R GIVEWAY KLM605`. |
| `TAXIALL 30` | Taxi all parked aircraft to runway 30 via A* pathfinding (global command, no callsign needed) |
| `BREAK` | Ignore ground conflicts for 15 seconds |

Pushback orientation accepts the eight compass points: `N`, `NE`, `E`, `SE`, `S`, `SW`, `W`, `NW`. Use `FACE C` (or shorthand `>C`) to specify the nose direction, or `TAIL C` (`<C`) to specify the tail direction. When pushed onto a taxiway, the cardinal acts as a hint — the aircraft aligns with whichever of the taxiway's two directions is closest. For parking/spot destinations, the cardinal is the absolute facing.

A `PUSH @parking` reverses **directly** to the gate node (a straight or gently curved tug reverse), like the tug pushing along the ramp lead line — it does not taxi there along the taxiway graph and never routes through a movement-area taxiway to reach the gate.

A `PUSH $spot` lines the aircraft up straight on the marking the way a tug positions it onto a stand: it reverses *past* the spot to a staging point behind it, then pulls **forward** onto the mark so the **nosewheel** sits on it (the fuselage doesn't jut a half-length past the mark toward the adjacent taxiway). With no facing given it ends **nose-out**, facing the parent taxiway ready to taxi — add `FACE <dir>` / `TAIL <dir>` to override. Like `@parking` it never routes onto a movement-area taxiway to get there.

While a pushback is already in progress, a **heading-only** `PUSH FACE C` / `PUSH TAIL C` / `PUSH >C` / `PUSH <C` amends the target facing in place — no new phase, no restart. Accepted until the aircraft has begun rotating the nose to the prior target (simple-mode: until alignment completes; taxiway-, spot-, and parking-target: until 60% of the push distance is covered). After the turn begins, the amendment is rejected with `Unable, pushback turn in progress`. Non-heading-only PUSH commands (`PUSH A`, `PUSH @SPOT`, etc.) issued during active pushback are rejected — only the facing can be amended.

Aircraft automatically hold short at all runway crossings along the taxi route. Use `CROSS` to clear a hold-short — either while already holding short, or in advance to pre-clear it before the aircraft arrives. `CROSS` works for both runway and taxiway hold-shorts. It also crosses a runway the aircraft taxied **to** (its taxi destination): the runway is undesignated as a departure hold and the aircraft taxis across to the far side and holds in position. To depart from that runway instead, use `CTO` or `LUAW`.

When an aircraft lands and vacates **between two parallel runways** and the parallel runway's hold-short is the next thing along the same exit taxiway with no intersection in between (e.g. SFO 19L exit G → 19R, OAK 28L exit G/H → 28R), it automatically pulls up to hold short of the parallel runway. A bare `CROSS` (or `CROSS 19R`) then takes it across without a prior `TAXI`. This auto-pull-up is on by default and can be disabled in **Settings**; it always waits for an explicit `CROSS` before entering the parallel.

`HS` can be issued to a taxiing aircraft to add a hold-short point at the first upcoming intersection with the given taxiway or runway along the remaining route.

When a `TAXI` clearance ends on a taxiway with no downstream destination (no runway, parking `@`, spot `$`, or hold-short) — e.g. `TAXI G B` — and that final taxiway runs both ways from where the route reaches it, the aircraft stops at the intersection rather than guessing a direction along it. Issue a follow-up taxi to send it either way (e.g. `TAXI B Q A @F11`). A final taxiway that only leads one way from the junction is taxied normally.

When a `TAXI` clearance names a `CROSS <rwy>` but no taxiway, parking, or spot past it (e.g. `TAXI G CROSS 28R`), the crossed runway becomes the direction anchor: the route heads toward and across that runway and stops just past the far side, where the aircraft holds clear awaiting further instructions. This resolves the direction even when the taxiway crosses more than one runway — `TAXI G CROSS 28R` from an aircraft that just exited the parallel 28L heads toward 28R, not back across 28L. (When the clearance also names a real destination, e.g. `RWY 30 TAXI G CROSS 28R`, the destination anchors direction and `CROSS 28R` is purely a crossing pre-clearance, as before.)

A runway may be named as a segment of the taxi path, not just as the destination — `TAXI 28R G D` taxis the aircraft **along** runway 28R's centerline, then turns off onto G and D (e.g. a back-taxi). The runway can appear first or mid-path. Because the clearance authorizes that runway, the route flows straight onto and along it with no hold-short at its entry; any *different* runway the route crosses still holds short as usual. The named runway must physically intersect the adjacent taxiways — if it doesn't (e.g. `TAXI 28R W` where W never meets 28R), the command fails cleanly with "Taxiway W does not intersect runway 28R" rather than detouring. A turn glyph on a runway token has no effect (travel direction along the runway is fixed by the adjacent waypoints).

A taxiway token may carry an optional turn-direction hint: prefix `>` for a right turn or `<` for a left turn onto that taxiway (e.g. `TAXI >A B <C D` = right onto A, then B, left onto C, then D — no space between the glyph and the taxiway). The hint applies at the junction where the route turns onto the named taxiway: for the first taxiway it picks the start direction relative to the aircraft's current heading; for later taxiways it prefers the junction whose turn matches the hint. Hints are a best-effort preference — when the geometry only admits the other direction the pathfinder still routes (it never strands the clearance) and the TAXI echo notes the unhonored turn (e.g. `[Unable left turn onto B — taxiing right instead]`), and an unprefixed taxiway keeps the pathfinder's own choice. (In a `PUSH` command the same `>`/`<` glyphs instead mean face/tail; they are turn hints only in `TAXI`.)

When you taxi to a hold-short point (via context menu or command), the runway is automatically assigned based on the closest threshold. Override with `RWY {id}` if needed.

A `TAXI` clearance does not need to name the taxiway the aircraft is already on. When the cleared taxiways begin with one the aircraft can turn onto directly from its current taxiway, that current taxiway is added to the route automatically — `TAXI W` from W5 routes as `TAXI W5 W`, and `TAXI E RWY 28R` from C routes as `TAXI C E RWY 28R`. The current taxiway appears in the readback only when the route actually drives along it. When the current taxiway and the first cleared taxiway meet only across a runway, the inference does not apply and the runway crossing must still be cleared explicitly. An aircraft that has just exited a runway is implicitly cleared to finish crossing that same runway's hold-short bars when it receives a `TAXI`; any subsequent crossing of a different runway still requires `CROSS`.

Ground aircraft automatically detect and avoid collisions — trailing aircraft slow down or stop to maintain safe separation. Head-on conflicts cause both aircraft to stop. Use `BREAK` to temporarily override conflict avoidance for 15 seconds.

### Helicopter Commands

Helicopters are detected automatically from the ICAO type designator. They use tighter traffic patterns (500ft AGL), steeper glideslopes (6°), and can take off/land vertically from non-runway positions.

| Command | Effect |
|---------|--------|
| `CTOPP` | Cleared for takeoff, present position — vertical liftoff to a hover, **holds position** at 25 ft AGL awaiting further instructions (does not depart) |
| `CTOPP +002` | Hover and hold at a specified height — `+0XX` ft AGL relative to present position (`+001` = 100 ft, `+002` = 200 ft) |
| `CTOPP 270 050` | CTOPP, **depart**: lift vertically, then fly heading 270, climb to 5000 ft. Optional climb altitude on every directional form. |
| `CTOPP LT270 050` / `CTOPP RT090` | CTOPP depart with explicit turn direction to heading after the vertical climb |
| `CTOPP OC [alt]` | CTOPP, depart direct to flight-plan destination after the vertical climb |
| `CTOPP DCT FIX [alt]` / `CTOPP TLDCT FIX` / `CTOPP TRDCT FIX` | CTOPP, depart direct to fix after the vertical climb (optionally turning left/right) |
| `ATXI H1` / `ATXI @H1` | Air-taxi to helipad/parking spot H1 — airborne at 100 ft AGL, ~40 KIAS, descends and lands at the spot. `@` prefix optional. |
| `ATXI $M1` / `ATXI M1` | Air-taxi to taxiway spot M1. `$` prefix optional. |
| `ATXI 28L` | Air-taxi to the threshold of runway 28L. |
| `LAND H1` | Land at named spot H1 (helipad, parking, or ramp position) |
| `LAND H1 NODEL` | Land at H1, exempt from auto-delete |

While air-taxiing or on final to a spot (`ATXI`/`LAND`), `HPP` hovers the helicopter in place (re-issue `ATXI`/`LAND` to continue); any normal airborne command (`FH`, a turn, `CM`/`DM`, `SPD`, `DCT`) pulls it out of the relocation and flies the new clearance (a bare heading holds the current altitude). The ground `HOLD`/`RES` verbs don't apply to an airborne helicopter.

Helicopters can also use all standard tower commands (`CTO`, `CLAND`, `LUAW`, `TG`, `SG`, `GA`) with runway assignments — they hover-taxi onto the runway, hold position, and take off/land like fixed-wing aircraft. This is typical for IFR operations. `CTO` requires a runway; `CTOPP` does not.

For hovering in place or at a fix, use `HPP` and `HFIX <fix>` (see [Hold Commands](#hold-commands) below — both are no-orbit variants intended for helicopters).

**Spawning helicopters:** Use the ADD command's `H` engine token: `ADD V S H @H1` spawns a light civil helicopter (R22/R44/B06) at helipad/parking spot H1. Name any other helicopter explicitly to override the default: `ADD V S H @H1 H60`. The `@` prefix marks a helipad/parking spawn. (Helicopters are also detected automatically from the ICAO type regardless of the engine token, so an explicit heli type still works with any engine.)

### Tower Commands

These commands control aircraft during takeoff, landing, and pattern operations. They require the aircraft to be in the phase system (e.g., spawned on a runway or on final approach from a scenario).

| Command | Effect |
|---------|--------|
| `LUAW` / `POS` / `LU` / `PH` | Line up and wait — aircraft holds on runway |
| `LUAW WD` / `LUAW ND` / `LUAW IMM` | Line up and wait, without delay — taxis briskly onto the runway, still stops and holds at the centerline. `WD`/`ND`/`IMM` are interchangeable. |
| `CTO` | Cleared for takeoff (default departure) |
| `CTO 060` | Cleared for takeoff, fly heading 060 |
| `CTO 060 250` | Cleared for takeoff, fly heading 060, climb and maintain 25,000 ft |
| `CTO MRC` | Cleared for takeoff, right crosswind departure — flies upwind, turns crosswind, then departs on the crosswind heading (VFR only) |
| `CTO MRD` | Cleared for takeoff, right downwind departure — flies upwind, crosswind, downwind, then departs on the downwind heading (VFR only) |
| `CTO MR270` | Cleared for takeoff, right 270° departure (turn right 270° from runway heading) — VFR only |
| `CTO MR45` | Cleared for takeoff, turn right 45° from runway heading — VFR only |
| `CTO ML270` / `MLC` / `MLD` / `ML45` | Left-turn equivalents of MR270/MRC/MRD/MR{N} — VFR only |
| `CTO MRH` / `RH` / `MSO` | Cleared for takeoff, fly runway heading (holds runway heading, awaits vectors) — IFR and VFR |
| `CTO H270` | Cleared for takeoff, fly heading 270 (shortest turn) |
| `CTO RH270` / `RT270` | Cleared for takeoff, turn right heading 270 |
| `CTO LH270` / `LT270` | Cleared for takeoff, turn left heading 270 |
| `CTO OC` | Cleared for takeoff, on course (direct to destination) — VFR only |
| `CTO DCT SUNOL` | Cleared for takeoff, direct to fix SUNOL — VFR only |
| `CTO TLDCT SUNOL` | Cleared for takeoff, turn left direct to fix SUNOL — VFR only |
| `CTO TRDCT OAK` | Cleared for takeoff, turn right direct to fix OAK — VFR only |
| `CTO MRT` / `CTOMRT` | Cleared for takeoff, make right traffic (closed pattern) — VFR only |
| `CTO MRT 28R` | Cleared for takeoff, make right traffic runway 28R (cross-runway pattern) — VFR only |
| `CTO MLT` / `CTOMLT` | Cleared for takeoff, make left traffic (closed pattern) — VFR only |
| `CTO MLT 28L` | Cleared for takeoff, make left traffic runway 28L (cross-runway pattern) — VFR only |
| `CTO CWT` / `CTO 270 CWT` / `CTO DCT SUNOL CWT` | Cleared for takeoff and caution wake turbulence. `CWT` can follow any `CTO` form. |
| `CTO IMM` / `CTO WD` / `CTO ND` / `CTO RT270 IMM` | Cleared for **immediate** takeoff — taxis briskly onto the runway and rolls without stopping at the centerline. `IMM`/`WD`/`ND` are interchangeable and can follow any `CTO` form (and combine with `CWT`). Super/Heavy aircraft still make a standing-start takeoff (no rolling start, per 7110.65 §3-9-5.3). |
| `CTOC` | Cancel takeoff clearance, hold in position. While the aircraft is lining up onto the runway it stops immediately where it is (does not continue onto the centerline); a fresh `CTO` resumes the line-up and departs. If already lined up and waiting, it holds. Mid-roll abort works below V1 (≈ Vr − 5 kts); above V1 the aircraft is committed and CTOC is rejected. |
| `CLAND` / `CL` / `FS` | Cleared to land (full stop) for an airborne aircraft established on an approach or pattern with an assigned runway. |
| `CLAND 28R` | Cleared to land on a named runway. For an aircraft that is **following** traffic but has no runway of its own (`RTIS`/`FOLLOW` issued, not yet sequenced onto final), the clearance is **armed** and applied automatically when the follower joins the traffic's runway final — it lands behind the traffic without a second `CLAND`. A bare `CLAND` while following inherits the lead's runway. If the named runway differs from the runway the follower actually joins, the clearance is not applied and the follower awaits an explicit `CLAND` on the actual runway. (Not for an enroute aircraft with no approach and no follow — assign a pattern entry like `EF 28R` first.) |
| `CLAND 33` (during a low approach) | **Low approach, then land a diverging runway** (7110.65 §3-10-5 "change to runway"). If the aircraft is doing a low approach (`LA`) on one runway and `CLAND` names a *different*, diverging/non-intersecting runway, it flies the low pass, makes a sharp turn onto the new runway, and lands there — e.g. `LA` then `CLAND 33` at KOAK for a low approach 28R, land 33. The controller and pilot phraseology becomes "change to runway 33, runway 33, cleared to land". Because runways like 28R/33 share a corner, the aircraft on 28R's final is always well off 33's final, so the turn is a tight, short-final intercept (not a straight-in) flown late and low — a light-aircraft (piston/helicopter) maneuver. Rejected with a pilot "unable" when the geometry doesn't allow a clean transition (runways near-parallel — use a sidestep; near-reciprocal — needs a re-entry; the turn can't be made; the aircraft is already past the new runway's final; or the runways physically intersect) or the aircraft is a jet/turboprop. Outside a low approach, `CLAND <otherRunway>` still rejects with "established for runway …". |
| `CLAND NODEL` | Cleared to land (exempt from auto-delete after landing) |
| `CLAND CWT` / `CLAND NODEL CWT` | Cleared to land and caution wake turbulence. |
| `CLANDF` | **Force landing — instructor/RPO override.** Grants landing clearance and forces the aircraft to land, suppressing every automatic go-around (unstable/balked approach, too high at the missed-approach point, no landing clearance) and disregarding the normal descent/speed limits so it reaches a touchdown from any energy state (too high, too fast, off centerline). If the aircraft is already going around (climbing out in the go-around phase), CLANDF cancels the go-around and re-establishes it on final for its assigned runway. RPO-only — rejected in solo training. Cancelled by `GA`, by `CLC`, or once it touches down. |
| `CWT` | Caution wake turbulence. Phase-transparent; in solo training it records wake-advisory proof when there is exactly one current wake context for the selected aircraft. |
| `LAHSO 33` | Cleared to land, hold short of runway 33 (LAHSO). Includes landing clearance. Aircraft stops before the intersecting runway and waits for a taxi/cross command. |
| `CLC` / `CTLC` | Cancel landing clearance |
| `GA` | Go around (VFR: re-enter the traffic pattern; instrument: fly published missed approach; otherwise: runway heading, 2,000 AGL) |
| `GA MRT` | Go around, make right traffic (VFR/visual only) |
| `GA MLT` | Go around, make left traffic (VFR/visual only) |
| `GA 270` | Go around, fly heading 270, climb to 2,000 AGL (self-clear) |
| `GA 270 50` | Go around, fly heading 270, climb to 5,000 ft (overrides published missed approach) |
| `GA RH` | Go around, fly runway heading explicitly (same behavior as plain `GA`) |
| `GA RH 50` | Go around, fly runway heading, climb to 5,000 ft (overrides published missed approach) |
| `EL` / `EXITL` | Exit runway to the left. Requires a pending landing or active runway exit; rejected with feedback otherwise. |
| `ER` / `EXITR` | Exit runway to the right. Same precondition as `EL`. |
| `EXIT A3` | Exit runway at taxiway A3. Same precondition as `EL`. |
| `EL NODEL` / `ER NODEL` / `EXIT A3 NODEL` | Exit with auto-delete exemption |
| `ER W5 EXP` / `EL EXP` / `EXIT A3 EXP` | Exit **without delay** — clear the runway as fast as possible. Takes the earliest reachable exit (instead of the first comfortable one), braking harder (max-effort) to make it, then brakes firmly to the hold-short stop after the turn-off. High-speed exits keep their higher turn-off speed. `EXP` combines with `NODEL` in any order. |
| `EXP` (standalone) | On a just-landed aircraft (rolling out or exiting), expedites the runway exit — same behavior as the `EXP` modifier above, without changing the assigned side/taxiway. |

A side and a taxiway can be combined: `ER D` exits right at taxiway D, and giving the two as a sequence (`ER ; EXIT D`) does the same — a bare `EXIT <taxiway>` after `EL`/`ER` keeps the standing side. If that taxiway only exists on the other side of the runway, the aircraft still takes it (the taxiway is a hard constraint, the side a soft preference).

When an exit is assigned (via `EL`, `ER`, or `EXIT`), the aircraft maintains a higher rollout speed and only decelerates when kinematically necessary to reach the exit at the correct turn-off speed. High-speed exits (≤45° from runway heading) target ~30 kts; standard 90° exits target ~15 kts. Without an assigned exit, aircraft decelerate uniformly to 20 kts. Adding `EXP` ("without delay") raises the braking limit to a max-effort rate (jet ~7.5 kts/s vs the normal firm 5 kts/s) so the pilot takes the earliest reachable exit and brakes firmly to the hold-short stop — reducing runway occupancy at the cost of a firmer rollout.

#### CTO Departure Modifiers

All CTO modifiers accept an optional altitude suffix using the same format as CM/DM (see [Altitude Arguments](#altitude-arguments)). A bare number (1-360) without a modifier prefix is interpreted as a heading: `CTO 270` = fly heading 270, `CTO 270 050` = fly heading 270, climb to 5,000 ft.

When a CTO carries no explicit climb altitude, an IFR departure on a SID climbs to and maintains the SID's published initial ("maintain") altitude from the facility's vTDLS configuration (e.g. KIAH 4,000 ft, KHOU 5,000 ft) until you issue a climb (`CM`). A later climb command supersedes the cap; VFR departures and airports without a published initial altitude are unaffected.

Append `CWT` after any CTO form to include "caution wake turbulence" in the takeoff clearance and record wake-advisory proof: `CTO CWT`, `CTO 270 CWT`, `CTO DCT SUNOL CWT`.

Append `IMM` (or its interchangeable aliases `WD` / `ND`) after any CTO form for a **cleared for immediate takeoff** (AIM 4-4-13): the pilot taxis briskly onto the runway and begins the takeoff roll without stopping at the centerline — useful to fit a departure in ahead of an arrival. `CTO IMM`, `CTO RT270 IMM`, `CTO IMM CWT`. The same modifier on `LUAW` (`LUAW WD`) gives a "line up and wait, without delay": the aircraft taxis briskly onto the runway but still stops and holds at the centerline. Super/Heavy aircraft still make a standing-start (non-rolling) takeoff per 7110.65 §3-9-5.3, so `IMM` only speeds their taxi onto the runway, not the takeoff start.

**IFR aircraft** can use bare `CTO` (default SID/route departure), `CTO` with a numeric heading (`CTO 270`, `CTO H270`, `CTO RH270`, etc.), or `CTO RH` / `MRH` / `MSO` (fly runway heading — holds runway heading and awaits vectors, suppressing the SID; routinely issued to IFR departures). Pattern-exit and other runway-relative modifiers (`MRC`, `MRD`, `MR{N}`, `OC`, `MLT`, `DCT`, etc.) are VFR-only — dispatch rejects them with a message naming the IFR restriction so the controller can reissue with a vector or let the SID run. An IFR aircraft given `CTO RH` (or vectored off its SID) **rejoins** its filed SID when issued `DCT <SID fix>` followed by `CVIA` (climb via), which self-activates the filed SID and overlays its published crossing restrictions (see [Navigation Commands](#navigation-commands)).

After liftoff most assigned departure turns (`MR{N}`/`ML{N}`, `H{N}`, `RH{N}`, `DCT`, `OC`) are **deferred** to InitialClimbPhase. The aircraft holds runway heading until it reaches the minimum safe altitude — 400 ft above field elevation for IFR (TERPS criterion: AIM 5-2-9.e.1 / 7110.65 5-8-3, no turns below 400 ft AGL and no lateral past-DER requirement), or pattern altitude − 300 ft **and** past the departure end of runway for VFR (AIM 4-3-2) — then makes the single relative turn.

The named **pattern-exit departures** (`MRC`/`MRD`/`MLC`/`MLD`) instead fly the actual traffic pattern: a crosswind departure flies the upwind and turns crosswind; a downwind departure flies upwind, crosswind, and downwind. The aircraft then rolls out on the exit-leg heading and departs the area, climbing continuously toward its assigned/filed altitude (it does **not** level at pattern altitude). Because these build real pattern legs, `EXT` / `EXT UPWIND` / `EXT CROSSWIND` work on them — e.g. "extend upwind" delays the crosswind turn for spacing. (`MR90`/`MR180` remain single relative turns; use the named `MRC`/`MRD` tokens for pattern departures.)

For a **radar-vectors SID** (e.g. NIMI6 off KOAK), the published departure heading is read from CIFP and held after liftoff while you still have the aircraft, then the filed route is picked up after you hand it off. If that published heading can't be resolved from the current FAA CIFP cycle — for example the procedure was renamed and is briefly absent from the cycle's data — the aircraft holds **runway heading** and awaits vectors instead of turning direct to the first enroute fix (FAA 7110.65 5-8-2). When a recently-superseded CIFP cycle is still cached, the published heading is recovered from it.

| Modifier | Departure type | VFR/IFR |
|----------|----------------|---------|
| *(none)* | Default departure — VFR: runway heading; IFR: navigates filed route ([SID](#glossary) expansion) | Both |
| `{N}` | Bare heading (1-360) — fly heading N (shortest turn) | Both |
| `H{N}` | Fly heading N (shortest turn) | Both |
| `RH{N}` / `RT{N}` | Turn right heading N | Both |
| `LH{N}` / `LT{N}` | Turn left heading N | Both |
| `MRH` / `MSO` / `RH` | Fly runway heading (straight out — holds runway heading, awaits vectors; suppresses the SID for IFR) | Both |
| `MRC` / `MLC` | Right/left **crosswind departure** — fly upwind, turn crosswind, then depart on the crosswind heading | VFR only |
| `MRD` / `MLD` | Right/left **downwind departure** — fly upwind, crosswind, downwind, then depart on the downwind heading | VFR only |
| `MR{N}` / `ML{N}` | Right/left turn of N degrees (1-359) from runway heading (single relative turn, not a pattern) | VFR only |
| `OC` | On course — navigate direct to destination airport | VFR only |
| `DCT {fix}` | Direct to named fix | VFR only |
| `TLDCT {fix}` | Turn left direct to named fix | VFR only |
| `TRDCT {fix}` | Turn right direct to named fix | VFR only |
| `MRT` / `MLT` | Make right/left closed traffic (enter pattern) | VFR only |
| `MRT {rwy}` / `MLT {rwy}` | Cross-runway closed traffic (pattern for a different runway). Cancels any landing clearance held for the old runway | VFR only |
| `MRT [rwy] [alt]` / `MLT [rwy] [alt]` | Closed traffic with optional altitude override (e.g., `CTO MLT 28R 15` = left traffic 28R at 1,500 ft) | VFR only |

**Cross-runway pattern** — `CTO MRT 28R` from runway 33 clears the aircraft for takeoff on runway 33 and enters right traffic for runway 28R. The aircraft lines up and departs on the **departure runway** (33), climbs the upwind on runway 33's extended centerline, then turns toward the assigned pattern side and joins the **pattern runway** (28R) downwind. Downwind, base, and final are flown for the pattern runway (28R); subsequent circuits stay entirely on the pattern runway. (Per AIM 4-3-2: the departure/upwind leg belongs to the departure runway; downwind/base/final belong to the landing runway.)

**Altitude resolution** — when no altitude is specified, the target depends on flight rules and departure type:

1. Closed traffic → pattern altitude (1,000 ft AGL for props, 1,500 ft AGL for jets)
2. VFR with filed cruise altitude → cruise altitude
3. VFR without cruise → pattern altitude
4. IFR → self-clear at 1,500 ft AGL

**Navigation** — IFR `CTO` (default departure) automatically expands the filed route including [SID](#glossary) waypoints and navigates the aircraft along it. When CIFP data is available, SID legs include published altitude and speed constraints; SID via mode activates automatically so the aircraft follows the published climb profile. Use `CM` to override (disables via mode), or `CVIA` to re-enable it. `CTO DCT {fix}` turns the aircraft toward the fix after liftoff. `CTO OC` navigates toward the destination airport.

The `GA` altitude argument uses the same format as CM/DM (see [Altitude Arguments](#altitude-arguments)). `RH` in the heading position means "runway heading." `GA` accepts heading only (`GA 270`), heading + altitude (`GA 270 50`), or pattern direction (`GA MRT`/`GA MLT`). `GA MRT`/`GA MLT` sets the aircraft into pattern mode (make right/left traffic) and climbs to pattern altitude.

**Pattern re-entry** — a bare `GA` given to a VFR aircraft re-enters the traffic pattern: it climbs to 300 ft below pattern altitude, turns crosswind past the departure end, and flies a full circuit (AIM 4-3-2). The side is the one the controller last assigned — a persistent `MRT`/`MLT`, else the pattern the aircraft was already flying, else the runway's L/R suffix (28R implies right traffic when 28L exists), else left traffic. The aircraft keeps its pre-go-around intent: one that was cleared to land tries to land again, one cycling touch-and-goes keeps cycling. Attach a heading or altitude (`GA 270`, `GA 50`) and the controller owns the climb-out instead — the aircraft flies the assignment and self-clears at 2,000 AGL without re-entering the pattern. IFR aircraft never auto-enter the pattern.

**Pattern direction during a go-around** — `MRT`/`MLT` issued while an aircraft is climbing out on a go-around converts the climb-out into a pattern go-around: it levels 300 ft below pattern altitude instead of running out to 2,000 AGL, drops any heading assigned by `GA 270`, and flies the requested side.

**Published missed approach** — When an aircraft on an instrument approach goes around — either automatically or via `GA` — it flies the published missed approach procedure from CIFP data. This includes climbing to the missed approach altitude, navigating through the MAP fix sequence, and entering a holding pattern at the final MAP fix if the procedure defines one. The aircraft holds indefinitely until given further instructions. If the procedure has no holding leg, the aircraft completes the MAP fix sequence and awaits vectors. Visual approaches and pattern traffic have no published missed approach and use the generic go-around behavior instead.

**ATC override** — If `GA` includes an explicit heading or altitude (e.g., `GA 270 50`), the published missed approach is cancelled and the aircraft flies the assigned heading/altitude instead. Any heading or altitude command issued while the aircraft is flying the missed approach procedure also cancels it.

**Auto go-around** — When no landing clearance is issued by 0.5nm from the threshold, the aircraft goes around automatically and broadcasts a warning. Instrument approaches fly the published missed approach; VFR and pattern traffic re-enter the pattern; IFR non-pattern traffic without MAP data flies runway heading at 2,000 AGL.

### Pattern Commands

Pattern-entry verbs (`ELD`, `ERD`, `ELB`, `ERB`, `EF`, `ELC`, `ERC`) require the aircraft to be airborne and rejected with feedback otherwise. For closed-traffic departures from the ground, use `CTO MLT 28R` / `CTO MRT 28R` instead, which sets up the pattern as part of takeoff clearance.

A pattern-entry verb re-shapes the approach geometry only — it preserves the aircraft's current landing clearance. An aircraft already cleared to land (`CLAND`) stays cleared for a full stop when re-sequenced; to clear a touch-and-go or the option instead, use `TG` / `COPT` (or `SG` / `LA`).

| Command | Effect |
|---------|--------|
| `ELD` / `ERD` | Enter left/right downwind |
| `ELD 28R` / `ERD 28R` | Enter left/right downwind, assign runway |
| `ELB` / `ERB` | Enter left/right base perpendicular to centerline from present position (base leg length = current cross-track) |
| `ELB 3` / `ERB 5` | Enter left/right base projected to a fixed final distance (NM) |
| `ELB 28R` / `ERB 28R` | Enter left/right base, assign runway (perpendicular from present position) |
| `ELB 28R 3` | Enter left base, assign runway 28R, 3nm final |
| `EF` | Enter final (straight-in) |
| `EF 28R` | Enter final, assign runway |
| `MLT` / `MRT` | Make left/right traffic (sets pattern direction) |
| `MLT 28R` / `MRT 28R` | Make left/right traffic for a specific runway (cross-runway pattern) |
| `TC` / `TD` / `TB` | Turn crosswind / downwind / base (advance to next leg). `TC` is also accepted during the takeoff roll / initial climb on a closed-traffic or pattern-exit departure (`CTO MR…` / `ML…`): it arms the crosswind turn, which fires the moment the aircraft reaches the upwind leg (~400 ft AGL, the safe-turn floor), turning crosswind earlier than the normal turn point. |
| `EXT` / `EXTEND` | Extend the current pattern leg (upwind, crosswind, or downwind — not base). Before the numbered leg becomes active — while navigating a pattern entry (after `ERD`/`ERC`/…) or during a touch-and-go ground roll — it extends the leg the aircraft is heading onto (the next queued upwind/crosswind/downwind). The pre-arm also reaches a pattern entry that is itself still queued behind another command (e.g. `EXT DOWNWIND` while `ERD 28R` sits queued behind `DCT VPCOL`), so the leg comes out extended when the entry later builds it. |
| `EXT UPWIND` / `EXT UW` | Extend upwind. If aircraft has just started turning crosswind, cancels the turn and re-establishes the upwind leg. Also accepted before the upwind has begun — during a touch-and-go ground roll, on short final for a planned touch-and-go, or while holding short pre-takeoff — and arms the upcoming upwind so it extends without a second command after liftoff. |
| `EXT CROSSWIND` / `EXT CW` | Extend crosswind. Rolls the aircraft back from downwind to crosswind if issued one leg late. Also accepted before the crosswind is reached — while on upwind, while navigating a pattern entry (e.g. after `ERC`), or while a crosswind-or-earlier entry (`ERC`/`ELC`) is still queued behind another command — it pre-arms the upcoming crosswind so it extends automatically when the aircraft turns onto it. |
| `EXT DOWNWIND` / `EXT DW` | Extend downwind. Rolls the aircraft back from base to downwind if issued one leg late. Also accepted before the downwind is reached — while on upwind or crosswind, while navigating a pattern entry (e.g. right after `ERD`), or while the pattern entry itself is still queued behind another command (e.g. `DCT VPCOL; ERD 28R`, then a separate `EXT DOWNWIND` before the aircraft reaches VPCOL) — it pre-arms the downwind so it extends automatically when the entry builds it. `>1`-leg rollbacks are still rejected, and a matching pattern entry must be queued or active — `EXT DOWNWIND` with nothing to extend is rejected. `MNA` cancels a pending pre-arm. While the downwind is extended, the pilot no longer voices the midfield-downwind "uncleared" reminder (and RPO mode no longer shows the matching warning) — extending the leg is itself a sequencing instruction. |
| `ELC` / `ERC` | Enter left/right crosswind |
| `ELC 28R` / `ERC 28R` | Enter left/right crosswind, assign runway |
| `SA` / `MSA` | Make short approach — compress the unflown pattern. Issue while on or before downwind/base; can be chained with `ERD`/`ERB` (e.g. `ERD 28R; SA`) to arm the upcoming leg, or issued separately while a pattern entry is still queued behind another command (e.g. `DCT VPCOL; ERD 28R`, then a separate `SA`) to pre-arm the downwind the entry builds. |
| `MNA` | Make normal approach — clear an armed/active short approach or a pre-armed leg extension. Same chaining/queued-entry semantics as `SA`; issued while a pattern entry is still queued, it cancels any pending `EXT`/`SA` pre-arm for that entry. |
| `L360` / `R360` (`ML3`/`ML360`, `MR3`/`MR360`) | Left/right 360° orbit in the pattern (resumes same leg after). Flies the orbit at holding speed, then resumes normal speed. |
| `L270` / `R270` | Left/right 270° turn (immediate). Flies at holding speed, then resumes normal speed. |
| `P270` / `PLAN270` | Plan a 270° turn at the next pattern turn point |
| `NO270` | Cancel a 270 in progress or a planned 270 |
| `PS 1.5` / `PATTSIZE 1.5` | Set pattern size (0.25–10.0 NM downwind offset) |
| `MLS` / `MRS` | S-turns on final, initial left/right (default 2 turns) |
| `MLS 3` / `MRS 4` | S-turns with specified count |
| `OFL` / `OFR` | Offset pattern left/right (default 0.5 NM perpendicular to current pattern heading) |
| `OFL 0.3` / `OFR 1.0` | Offset N NM (range 0.1–1.5) — one-shot lateral dogleg + parallel hold for in-pattern spacing |
| `CA` / `CIRCLE` | Circle the airport |
| `FOLLOW UAL123` | Follow traffic (VFR): pursue lead and auto-join its pattern when close |
| `FOLLOW` | Follow the most recently reported in-sight traffic (bare form — no callsign needed) |

All pattern entry commands (ELB, ERB, ELD, ERD, ELC, ERC, EF) accept an optional runway argument. ELB/ERB behave in two modes:

- **Without a distance** — the aircraft turns perpendicular to the extended centerline from its current position (matching 7110.65 §3-1-9 *"TURN BASE LEG NOW"*). The base-leg length equals the aircraft's current cross-track; the final turn fires at the aircraft's projection onto centerline. Rejected with `"Unable, too close for base"` if the along-track from threshold is below the category floor (jets/turboprops 2.0 nm, pistons 1.0 nm, helicopters 0.5 nm) or the aircraft is past the threshold. Rejected with `"Unable, too high for base"` if the aircraft cannot descend to field elevation within the remaining base + final path at the category's pattern descent rate — issue `DM` (descend maintain) first.
- **With a distance** — the aircraft flies to a base entry point on the extended centerline at that NM from the threshold (offset by the standard pattern width), then flies the standard base leg.

`EF` (enter final) joins the extended centerline where the aircraft can fly a stabilized straight-in:

- For an aircraft roughly **aligned** with the runway, it flies a straight-in from its current position (a shallow cut-in onto final).
- For a **diagonal** aircraft (heading outside the ~30° intercept envelope — 20° inside 2 nm, 45° helicopters; an airmanship analogy to 7110.65 §5-9-2 / TBL 5-9-1, not a VFR mandate) that has room for at least the category minimum final (jets/turboprops 2.0 nm, pistons 1.0 nm, helicopters 0.5 nm), the join is an **altitude-aware "make straight-in"**: the aircraft descends immediately on the diagonal cut-in toward the runway and joins final **as close to the threshold as it can** while still reaching the glideslope by the join — a shortcut. A lower aircraft (or one with a longer diagonal to descend on) shortcuts to the minimum final; a higher one — needing more descent room — joins a longer final. The join is **capped at the aircraft's along-track distance** so `EF` never routes the aircraft outbound / farther from the field. If the aircraft is too high to lose its altitude on the diagonal even at the capped join, the clearance still stands but a controller warning ("unable to descend for straight-in … — too high") is raised so the RPO can re-vector, descend it first, or pick another approach.
- For an aircraft **crossing** the final approach course (a base leg, or a diagonal cut-in) that is *inside* the category minimum final, a stabilized straight-in is no longer possible — so `EF` degrades to a **base entry**: the aircraft continues its base from its present position and turns final onto the target runway's centerline, no repositioning waypoint. This is the pattern-work runway change (7110.65 §3-10-5.c *"CHANGE TO RUNWAY (n), RUNWAY (n) CLEARED TO LAND"*), and it is what makes `EF 28L` work for an aircraft on a right base to the parallel 28R. Only the ¼-mile turn-to-final minimum applies (AIM FIG 4-3-2 note 3), not the straight-in floor — jets and turboprops cannot fly a pattern final that short, so they are refused. The base leg's side is taken from which side of the new centerline the aircraft is actually on, not from the runway's default pattern direction.
- **Already on final for the same runway:** `EF` for the runway the aircraft is already established on final for is redundant — it stays on its current approach ("continuing final") rather than re-sequencing, including mid base-to-final roll-out. `EF` never sends an aircraft on short final back outbound to re-enter.
- **Infeasible:** an aircraft inside the category minimum final that can fly neither a stabilized straight-in nor a base entry is rejected with "unable, short final" and continues its current approach. That covers an aligned aircraft too close in, a jet or turboprop on a short base, an aircraft that has already overshot the target centerline, and one too high to descend over the remaining base + final. A runway change that can't be flown (a parallel too low to sidestep, or a non-parallel runway) likewise rejects rather than looping outbound — issue `GA` to go around and re-sequence if the switch is required.
- **Never outbound:** an aircraft *already tracking outbound* — on the downwind, which parallels the runway in the departure direction (AIM §4-3-2.c) — may legitimately be sent ahead to an entry point farther from the threshold and then turned onto final; that is just the rest of the pattern. Any aircraft with an inbound component is never routed to an entry point behind it.

**Runway changes void the landing clearance.** A landing clearance names a runway (7110.65 §3-10-5), so any pattern entry (`EF` / `ERB` / `ELB` / `ELD` / `ERD` / `ELC` / `ERC`) whose runway argument names a *different* runway than the standing clearance cancels it — reissue `CLAND`/`TG`/`SG`/`LA`/`COPT` for the new runway. Re-entering the pattern for the same runway keeps the clearance. The instrument sidestep (`EF` to a closely-spaced parallel while established on `FinalApproach`) is exempt: there the approach clearance itself authorizes the landing on the parallel (7110.65 §4-8-7, AIM §5-4-19). The **low-approach runway change** (`CLAND <divergingRunway>` during an `LA`) is the one case where `CLAND` *itself* performs the change-to-runway: it reassigns the runway, voids the low-approach clearance, and re-issues the landing clearance for the new runway in one step (7110.65 §3-10-5.a.3).

`P270` plans a 270° turn at the next pattern turn point without executing immediately — the "long way round" spacing turn. The turn is flown **opposite** the traffic pattern direction (right 270 for left traffic, left 270 for right traffic) so the aircraft turns away from the runway, sweeps ~270°, and rolls out on the same course a normal 90° pattern turn would have reached — adding spacing without cutting across final. Use `NO270` to cancel.

`PS` sets the pattern downwind offset distance. The crosswind extension and base extension scale proportionally. The override persists across pattern circuits.

`OFL` / `OFR` apply a one-shot lateral offset to the current pattern leg (upwind, crosswind, downwind, or base — not final; use `MLS`/`MRS` for final-leg spacing). The aircraft doglegs ~30° from the current pattern heading in the requested direction, acquires a parallel track offset by the specified distance, then resumes parallel flight. Direction is relative to the aircraft's current heading (`OFR` on left downwind for 28L widens the pattern, away from the runway). Offset state lives on the active phase only — it discards on the next leg transition, so the next downstream leg resumes from the aircraft's actual offset position (a wider downwind naturally produces a longer base; an offset on base pushes the final-intercept point further out). Default offset is 0.5 NM (range 0.1–1.5 NM). Useful for in-pattern spacing when a faster aircraft is closing on slower traffic ahead and `EXT` isn't enough.

**Pattern direction persistence** — `MLT` / `MRT` / `CTO MLT` / `CTO MRT` / `GA MLT` / `GA MRT` set a persistent pattern-direction intent on the aircraft. The intent survives heading vectors (`FH` / `TR` / `TL`) that clear queued phases, and is **not** overridden by a single-approach clearance like `ERB 28L` / `ELB 28R` / `EF 28L`. After a touch-and-go on the single-approach side, the auto-cycled next circuit reverts to the persistent MLT/MRT direction. `CLAND` (full-stop) and `LAHSO` clear the persistent intent; the aircraft auto-exits the runway after touchdown.

**Mid-pattern runway / direction switches** — `MLT 28L` (or `MRT 28R`) issued while the aircraft is on an active Upwind / Crosswind / Downwind / Base rebuilds the phase chain from the current leg with the new direction and runway. If the aircraft is on the wrong physical side for the new pattern (e.g., on the upwind from a 28R touch-and-go when switched to 28L left traffic), a midfield-crossing leg is inserted automatically so the aircraft crosses to the correct side before joining downwind — equivalent to `ELD 28L` from that position.

`FOLLOW` is a **VFR-only** command (per 7110.65 §7-6-7 "Sequencing"). It requires the pilot to have reported the traffic in sight first (`RTIS` or the forced `RTISF` in RPO mode; structured `RTIS` in solo training) — a pilot cannot follow traffic they haven't visually acquired. The one-shot **`FOLLOWF`** (`FOLF`, RPO-only, rejected in solo training) folds the `RTISF` into the follow, so you can issue it without a separate `RTIS`/`RTISF` first. A **bare `FOLLOWF`** (no callsign) also folds in a still-*pending* `RTIS` — the traffic the controller just called but the pilot hasn't yet visually acquired — so you don't have to re-type the callsign while the pilot is still looking. Once `HasReportedTrafficInSight` is set, `FOLLOW` works from any airborne state — you do not need to put the follower in a pattern first. Behavior depends on where the follower and lead are:

- **Free pursuit** (lead not yet in a pattern, or follower far from the pattern): the follower flies a trail behind the lead rather than aiming its nose at the lead's instantaneous position (AIM §5-5-12 — the pilot maneuvers as necessary to keep in-trail separation). Steering is relative to the lead's ground track: far behind, it lag-pursues a point at the desired distance behind the lead and curves into trail; once established, it flies parallel to the lead's track with only a gentle cross-track correction; when too close, it slows first and — if it's already at approach speed and slowing alone can't open the gap — makes a shallow widen (a few degrees off the lead's track, AIM §4-3-5) to bleed distance. Speed still tracks the lead with distance-based correction (±20 kts, wider free-flight spacing of 1.5/2.0/3.5 nm by category). Altitude is left at whatever the controller last assigned — real pilots do not dive/climb onto the lead; they maintain visual separation from their current level. (When the lead is nearly stopped, its ground track is unreliable, so the follower simply points at it.)
- **Pattern auto-join** (lead is in a pattern phase, follower within 3 nm of the lead's downwind abeam point, within 5 nm of the lead, and on the correct side of the runway): the follower's phase list is rebuilt with `PatternEntryPhase → DownwindPhase → BasePhase → FinalApproachPhase → LandingPhase` copying the lead's runway, pattern direction, and altitude. From then on, the existing pattern-tight spacing (1.0/1.5/2.0 nm) and extend-downwind logic take over. While the lead is ahead of the follower in the pattern (on base or final to the same runway), the follower holds its base turn — extending the downwind past its normal base-turn point — until turning base would roll it out at least the category spacing behind the lead, so it sequences in trail instead of cutting inside and overtaking it. The same in-trail leg-hold applies on the **upwind and crosswind** legs: rather than turning ahead of the traffic it is following, the follower extends its current leg to stay behind it. A follower **never turns on its own to break the hold** — past a 4 nm extension it reports "extending … unable to turn" and keeps flying the leg until you turn it or re-sequence it.
- **Straight-in sequencing** (lead is on a straight-in final/landing to a known runway — e.g. an IFR aircraft on the ILS, with no VFR pattern to join): once the follower is at least the in-trail minimum behind the lead and within a sane intercept of the extended centerline (≤ 1 nm cross-track, ≤ 30° intercept), it is sequenced onto that runway's final via `PatternEntryPhase → FinalApproachPhase → LandingPhase`. The follower descends on the glideslope behind the traffic, but **no landing clearance is set** — it holds for a separate `CLAND` (FAA 7110.65 §3-10-6) and goes around at minimums if never cleared. The clearance can also be issued **up front** while the follower is still being sequenced: `CLAND 28R` (or a bare `CLAND` to inherit the lead's runway) arms it, and it is applied automatically once the follower joins the runway final, so the follower lands behind the traffic without a second clearance. The in-trail minimum keeps it genuinely behind the traffic (AIM §4-3-4.4 — never cut in front) at the 1.5 nm same-runway floor for a light single behind same/lighter traffic, raised to the CWT wake minimum (7110.65 Table 5-5-2) when the lead is heavier. If the lead lands before the follower has rolled onto final, the follower is still sequenced onto that runway's final rather than levelling off over the field. `FOLLOW` is rejected outright when the lead is a **super** (visual separation not authorized behind a super, 7110.65 §7-2-1).

- **Cross-runway re-sequence** (follower is flying a pattern to one runway, the lead is landing another — e.g. a 28L pattern told to follow traffic landing 28R): in-trail sequencing only means something on a shared runway, so `FOLLOW` **moves the follower onto the lead's runway** rather than leaving it on a pattern where nothing sequences. Its pattern is dropped and it re-joins via the pattern auto-join / straight-in sequencing above, on the lead's runway. A runway-specific landing clearance is **not** carried across (7110.65 §3-10-5.c — a runway change needs a new clearance), so re-issue `CLAND` for the new runway. This is refused once the follower is already on **base or final** — *"Unable, established for runway 28L — vector or go around"* — because swinging a low, close-in aircraft onto a closely-spaced parallel would cross the original runway's final approach course (AIM §4-3-3 FIG 4-3-3 note 7; §4-3-5). Re-sequence it yourself with `ELB`/`ERB`, vector it, or send it around.

Once a follower is **on final** behind the traffic and catches up to less than the desired in-trail spacing while still more than 5 nm from the threshold, it self-initiates one shallow S-turn for spacing (AIM §4-3-5) and reports it, then resumes the approach — slowing alone isn't always enough that far out. Inside 5 nm it's committed to the approach: no lateral maneuvering, only the stabilized-approach speed and go-around logic. On downwind the follower instead extends the downwind (above), the usual pattern spacing tool. In all cases the follower only ever *slows* to maintain spacing — it never speeds up to chase a lead that is too far ahead (that's what extending the downwind is for).

When the follower simply **cannot** sequence behind much-slower traffic — its own approach speed exceeds the lead's by more than 10 kt, so no amount of slowing will open the gap (e.g. a Cessna 210 told to follow a 56-kt Cessna 152) — and it is on base or final closing inside 0.8 nm of the still-airborne lead, it **breaks off the follow and goes around** ("unable to maintain separation, going around") rather than overtaking or cutting in front of the traffic it was told to follow (AIM §4-3-3 — never overtake/cut in front). It climbs out and re-enters the pattern; re-sequence it (or break it out) and re-issue `FOLLOW` as needed.

The callsign argument is **optional**. A bare `FOLLOW` defaults to the most recently reported in-sight traffic (the last callsign acquired via a successful traffic-in-sight command). An explicit callsign always overrides the stored value. If no RTIS has succeeded yet, bare `FOLLOW` returns *"Unable, say traffic callsign"* without side effects. The forced bare **`FOLLOWF`** is the exception — it also resolves a still-*pending* `RTIS` target (traffic called but not yet acquired), not only a successfully acquired one.

Follow is cancelled automatically when the lead disappears, when the lead lands, or when the follower **loses sight of the traffic** (a cloud layer moves between them). A lead that merely outpaces the follower never breaks off the follow — a growing gap *increases* separation and removes the overtake risk, so it is yours to re-sequence, not the pilot's to abandon (AIM §5-5-12.a.2 / §4-4-14 NOTE: the pilot reports only when it cannot maintain visual contact). A follower that is already at minimum speed and still too close will also break off *only* when it has no lateral option left — if the traffic is ahead of it in the pattern on the same runway, it holds minimum speed and **extends its leg** instead of cutting in front (7110.65 §3-10-3.a.1: landing behind a jet, the traffic must be on the ground and clear before the follower crosses the threshold). When the lead *lands*, the follower is sequenced onto its runway's final (straight-in case above) if a runway was captured; otherwise the follow is cancelled. Any subsequent vector command (`FH`, `CM`, `SPD`, etc.) clears the follow phase and returns control to the controller's direct targets. To retarget, just issue another `FOLLOW` with a different callsign — the existing phase updates in place.

For **IFR** visual approaches, use `CVA 28R FOLLOW AAL123` instead — a distinct clearance that requires the pilot to have reported the traffic in sight first (or the one-shot `CVAF`).

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

TG, SG, LA, and COPT accept an optional `MLT`/`MRT` argument to set the traffic pattern direction on the go. Without one, the side is inferred the way a go-around infers it: the side you last assigned, then the pattern the aircraft is already flying, then the runway's L/R suffix, then left traffic.

### Approach Control Commands

Approach clearances use FAA [CIFP](#glossary) procedure data. Approach IDs can be full CIFP identifiers (e.g., `I28R`) or common shorthand (e.g., `ILS28R`, `RNAV17L`, `LOC30`). The runway number may be typed FAA-style without the leading zero — `I8R` resolves the same as `I08R`, and `ILS8R` the same as `ILS08R`.

| Command | Effect |
|---------|--------|
| `CAPP ILS28R` | Cleared ILS Runway 28R approach — navigates through the full procedure (IAF → IF → FAF → final) |
| `JAPP ILS28R` | Join ILS 28R approach at nearest IAF/IF ahead of the aircraft |
| `CAPPSI ILS28R` | Cleared straight-in ILS 28R (skips hold-in-lieu of procedure turn) |
| `JAPPSI ILS28R` | Join straight-in ILS 28R (skips hold-in-lieu) |
| `CAPPF ILS28R` | Forced approach clearance — marks the clearance as forced and, on the implied-PTAC branch, bypasses the 30° intercept-angle gate in `InterceptCoursePhase`. Aircraft captures the FAC regardless of angle (overshoots and S-turns back). Approach scoring records `WasForced`. |
| `JAPPF ILS28R` | Forced join — marks the clearance as forced; approach scoring records `WasForced`. (Fix-based join path doesn't create an InterceptCoursePhase, so only the scoring tag applies.) |
| `PTAC 280 025 ILS30` | Position/Turn/Altitude/Clearance — turn heading 280, maintain 2,500, cleared ILS 30 |
| `PTAC PH PA ILS30` | PTAC with present heading and present altitude, explicit approach |
| `PTAC PH PA` | PTAC with present heading/altitude, auto-resolve approach from expected approach or runway |
| `PTAC 280 PA` | PTAC with explicit heading 280, present altitude, auto-resolve approach |
| `PTACF 280 025 ILS30` | Forced PTAC — same as PTAC but bypasses the 30° capture-angle gate in `InterceptCoursePhase`. Aircraft captures the FAC regardless of intercept cut, overshoots laterally, and S-turns back onto the localizer under `FinalApproachPhase` control. Use when vectoring onto final at a steep angle is intentional. All `PTACF` forms mirror `PTAC` (`PTACF PH PA ILS30`, `PTACF PH PA`, `PTACF 280 PA`, bare `PTACF`). |
| `JFAC ILS28R` | Join final approach course / localizer — **lateral only**: turn to intercept and track the final approach course while holding the last assigned altitude and speed. Does **not** authorize a glideslope descent; issue `CAPP` to clear the aircraft for the approach (begin the descent). |
| `APPS` | List available approaches for the aircraft's destination airport |
| `APPS OAK` | List available approaches at a specific airport |

`JFAC` also accepts aliases `JLOC` and `JF`. Unlike `CAPP`, `JFAC`/`JLOC` is a vector to join the localizer, not an approach clearance: the aircraft maintains its assigned altitude and speed and descends on the glideslope only after a subsequent `CAPP`.

**Rich approach forms** — All approach commands (CAPP, JAPP, CAPPSI, JAPPSI, CAPPF, JAPPF) support combining navigation with the clearance (note: `PTAC`/`PTACF` do not; they're always heading+altitude+clearance in one shot):

| Command | Effect |
|---------|--------|
| `CAPP AT SUNOL ILS28R` | Navigate to fix SUNOL, then fly the ILS 28R |
| `CAPP DCT SUNOL ILS28R` | Direct to SUNOL, then fly the ILS 28R |
| `CAPP DCT SUNOL CFIX A034 ILS28R` | Direct to SUNOL, cross at or above 3,400, then ILS 28R |
| `JAPP AT SUNOL ILS28R` | Navigate to SUNOL, then join ILS 28R at nearest fix |
| `JAPP DCT SUNOL ILS28R` | Direct to SUNOL, then join ILS 28R |
| `CAPPSI DCT SUNOL ILS28R` | Direct to SUNOL, then cleared straight-in ILS 28R |

**CFIX altitude prefixes:** `A034` = at or above 3,400, `B034` = at or below 3,400, `034` = at 3,400.

**Heading intercept (implied PTAC):** When an aircraft is being vectored (has an assigned heading **and no nav route**) and you issue a bare `CAPP` (no AT/DCT fix), the aircraft maintains its present heading and intercepts the final approach course — equivalent to an implied PTAC. If the aircraft has a nav route (e.g. you previously issued `DCT FIX`), CAPP builds the published procedure instead. AT/DCT fixes always override heading intercept.

**Intercept angle validation:** Approach clearances validate the intercept angle per 7110.65 §5-9-2 — max 20° within 2nm of the approach gate, max 30° beyond. Use force variants (`CAPPF`/`JAPPF`) to override.

**Localizer bust-through detection:** When an aircraft on a heading intercept crosses the final approach course but its heading is too far off to capture (>30°), the approach is cancelled automatically. The aircraft continues on its current heading and a terminal broadcast notifies the RPO.

**Hold-in-lieu:** When a procedure includes a hold-in-lieu of procedure turn (e.g., at an IAF), `JAPP` automatically executes one holding circuit before proceeding. Use `CAPPSI`/`JAPPSI` to skip it.

**Procedure turn (PI):** When the published approach has a charted procedure turn (e.g. KCCR S19R via the COLLI transition), `CAPP` automatically engages it per AIM 5-4-9.1 when **any** of the following is true: (a) the aircraft is entering via the PI-bearing transition (e.g. CCR is in the nav route as the PT anchor), (b) you issued `DCT <fix>` to the PT anchor as part of CAPP, or (c) the aircraft's heading vs the inbound FAC exceeds 90° (no straight-in possible). The aircraft crosses the fix, flies the published outbound radial, executes the 45°/180° course reversal at/above the published PT altitude (capped at 200 KIAS, AIM 5-4-9.a.3), and intercepts inbound on the FAC. The PT is **not** engaged when the aircraft is on a NoPT transition (a published feeder marked NoPT — modeled in YAAT as any transition without a PI/HM/HF/HA leg) — the chart guarantees the geometry, so CAPP just navigates the inbound. `CAPPSI`/`JAPPSI` skip the PT but reject with an error when the intercept angle exceeds 90° and a course reversal is published — vector to final or use `CAPP` to fly the published procedure.

**Expect approach:** `EAPP I28R` tells the pilot to expect the ILS 28R approach. This sets the expected approach on the aircraft state (visible in the data grid), programs the approach fix names for display, and assigns the approach's runway as the aircraft's `DestinationRunway` — matching real-world phraseology where "expect ILS Runway 28R" implies arrival runway 28R. If a STAR is already loaded, its runway-transition fixes for the new runway are appended to the live navigation route so the published radar-vector tail enters the route immediately, without waiting for `CAPP`. A `JARR` issued after `EAPP` (no prior `RWY`) picks up the runway transition too. Does not clear the aircraft for the approach.

**Force direct to:** `DCTF` works like `DCT` but bypasses the check that the target fix must be on or ahead of the current route.

**Visual approach:** `CVA 28R` clears the aircraft for a visual approach to runway 28R. No CIFP procedure is required — the aircraft navigates visually. **Requires an IFR flight plan**; VFR pattern entry uses `ELD`/`ERD`/`SI`. Per 7110.65 §7-4-3 the pilot must have reported **either** the field in sight (`RFIS`/`RFISF`) when number-one, **or** the preceding traffic in sight (`RTIS`/`RTISF`) when given a `FOLLOW` clause — a following aircraft need not report the field (§7-4-3.a.2 NOTE). Without the required report `CVA` is rejected (*"Field not in sight — issue RFIS first"* / *"Traffic not in sight — issue RTIS first"*). Visual separation is not authorized behind a **super**, so `CVA … FOLLOW` a super is refused. The one-shot **`CVAF`** (`VISUALF`, RPO-only, rejected in solo training) folds the required `RFISF`/`RTISF` into the clearance so no separate report is needed. Pattern-entry geometry is sized for an IFR aircraft being vectored from cruise: downwind altitude is held at 2000 ft AGL above the field (clear of standard 1000 ft VFR pattern traffic), and parallel-runway pattern deconfliction does not apply. Options:

| Command | Effect |
|---------|--------|
| `CVA 28R` | Cleared visual approach runway 28R (requires RFIS first) |
| `CVA 28R LEFT` | Visual approach with left traffic pattern |
| `CVA 28R RIGHT` | Visual approach with right traffic pattern |
| `CVA 28R FOLLOW AAL123` | Visual approach following AAL123 (requires RTIS first; not authorized behind a super) |
| `CVAF 28R` | Forced visual approach — bypasses the RFIS/RTIS prerequisites (RPO-only) |

The aircraft execution path depends on its position relative to the runway:
- **Straight-in** (≤30° off final course): flies directly to final approach and landing
- **Angled join** (30°–90° off): navigates to an intercept point, then final
- **Pattern entry** (>90° off): enters downwind → base → final

The `FOLLOW` option requires the pilot to have reported traffic in sight first (via `RTIS`/`RTISF` in RPO mode, or structured `RTIS` in solo training). The follower adjusts speed and extends downwind to maintain visual separation from the leader. See [Pattern Commands](#pattern-commands) for follow behavior details. In solo training, a plain `CVA` clearance can produce a Session Report Advisory / Visual warning until the aircraft has reported the field in sight; `RFIS <clock> <miles>` records that proof and uses the same field-acquisition path as the visual-approach workflow.

**Field/traffic in sight:** Aircraft on an active visual approach automatically report "field in sight" or "traffic in sight" when detection conditions are met (forward hemisphere, within visibility range, below ceiling). RPO mode can still use shorthand `RFIS`, `RTIS <callsign>`, `RFISF`, and `RTISF`; solo-training students must use descriptive forms so the Session Report can score the actual advisory content. `RTIS <clock> <miles> <direction> <type> [altitude]` issues FAA-style traffic information, resolves the best-matching target, records proof for that recipient-target pair, and then runs the same visual-acquisition behavior as callsign `RTIS`. Matching is tolerant rather than exact (FAA 7110.65 §2-1-21; AIM §4-1-15): candidates must fall within realistic per-field bands — clock ±2 hours (±4, and de-weighted, when the recipient is itself maneuvering, since its clock reference is swinging), distance ±2 NM, direction within one octant, altitude ±500 ft — and the lowest-weighted-error candidate wins. The altitude is **optional** (`RTIS 3 5 W B737` for "altitude unknown" VFR traffic). A call where every field is spot-on (clock within an hour, distance within a mile, exact direction, altitude within a hundred feet) scores clean; a within-tolerance but noticeably-off call still resolves and proves the advisory but adds a low-severity "traffic advisory imprecise" coaching note. Example: `RTIS 3 5 W B737 024` renders *"Traffic, 3 o'clock, 5 miles, westbound, Boeing 737, 2,400, report it in sight."* `RFIS <clock> <miles>` renders *"Field's at your 11 o'clock, 18 miles, report it in sight."* and records field-in-sight proof for solo visual-approach scoring.

**VFR-style traffic advisories:** alongside the radar-style clock form, three simpler descriptive forms cover plain VFR-tower traffic calls. All three resolve the best-matching aircraft (same tolerant matching and graded "imprecise" coaching note as the clock form), then run the same visual-acquisition behavior, so they work as drop-in `RTIS` alternatives in both RPO and solo-training modes.

- **Relative position** — `RTIS <pos> <miles> <type>`, where `<pos>` is one of `NOSE`, `NL`, `NR`, `L`, `R`, `LR`, `RR`, `TAIL` (octants off the nose). Example: `RTIS NR 2 C172` renders *"Traffic, off your nose and to the right, 2 miles, a Cessna, report it in sight."* The relative-octant method is an intentional informal VFR-tower convention (not codified in 7110.65, which defines only clock and cardinal-compass azimuth); a target within one octant (±45°) and ±2 NM of the call resolves.
- **Pattern leg** — `RTIS <leg> <L|R> <miles> <rwy> <type>` (leg `UW`/`XW`/`DW`/`BASE`/`FINAL`; omit the side for `FINAL`). Example: `RTIS BASE R 2 28R M20P` renders *"Traffic, 2-mile right base for runway 28R, a Mooney, report it in sight."* (7110.65 §3-10-4). Each candidate is classified from its active pattern/final phase; the leg, side, and distance (±2 NM) must match.
- **Landmark / VFR reporting point** — `RTIS OVER <fix> <type>`, where `<fix>` is a navigation fix or VFR reporting point identifier (e.g. `VPCOL`). Example: `RTIS OVER VPCOL C172` renders *"Traffic, over Oakland Coliseum, a Cessna, report it in sight."* (7110.65 §2-1-21.b.1). A target within 2 NM of the reporting point resolves; the spoken friendly name comes from the fix's pronunciation data.

All three forms can be spoken via the speech recognizer ("traffic off your nose and to the right, two miles, a Cessna"; "traffic on a two-mile right base for runway two-eight right, a Mooney"; "traffic over the Oakland Coliseum, a Cessna") — landmark names resolve to their fix identifier through the pronunciation pre-pass.

`RTIS` and `RFIS` **fail soft**: if the pilot can't acquire the target on the first check, the command still succeeds, the pilot reports a diagnostic-free readback (e.g. *"Negative contact, LEAD, looking"*, *"Negative contact, KOAK, on top, looking"*, *"in the turn, looking"*, *"field's behind us, looking"*), and the pilot keeps re-checking acquisition each tick. When the target becomes acquirable — traffic enters the forward hemisphere, pilot rolls out of the turn, ownship descends through a deck, distance closes — the pilot reports *"traffic in sight"* / *"field in sight"* (announced via orange WRN notifications so the controller sees the resolution) and the look-state clears. The specific failure reason (cloud layer code, distance, hemisphere, bank state) is surfaced to the RPO through the command response only — never to the pilot readback. A second visual-acquisition command replaces the prior looking state; target leaving the simulation, destination cleared, or an approach-procedure reset silently clear it. Hard failures are still hard: a structured `RTIS` with no traffic within tolerance, bare `RTIS` with no callsign, a callsign not on frequency, or `RFIS` with no destination / destination not in the nav database returns an immediate error. (A structured `RTIS` no longer fails on ambiguity — it picks the best-matching traffic — but a `SAFAL` still requires an unambiguous target and fails rather than guess.) The forced variants `RTISF` / `RFISF` set the flag directly with no acquisition check and no looking state, and are rejected in solo training mode.

**Position reports (`REPORT`):** `REPORT <what>` asks the pilot to report an event back when it happens, rather than now (unlike `RFIS`/`RTIS`, which the pilot answers immediately). The pilot acknowledges on receipt (*"will report turning base"*) and voices the actual report when the event occurs, as a normal pilot radio transmission (terminal SAY line plus TTS).

- **Pattern leg** — `REPORT BASE`, `REPORT FINAL`, `REPORT CROSSWIND` (`XW`), `REPORT DOWNWIND` (`DW`). The pilot reports *"turning base runway 28R"* when the leg begins. **Repeats every circuit** — a closed-traffic aircraft reports the leg on each lap until cancelled or it lands full-stop (a touch-and-go keeps reporting). Rejected if the aircraft is not in the pattern.
- **N-mile final** — `REPORT 5 FINAL` (or `REPORT FINAL 5`). The pilot reports *"5-mile final runway 28R"* once, when the aircraft reaches that distance from the threshold. One-shot. Rejected if no runway is assigned; only fires for an aircraft inbound to land.
- **At a fix** — `REPORT SUNOL`. The pilot reports *"passing SUNOL"* once, when the aircraft reaches the fix. One-shot. Rejected if the fix is not in the nav database.
- **Cancel** — bare `REPORT OFF` cancels **all** standing reports for the aircraft (every armed pattern leg plus any pending n-mile-final or at-fix report). To cancel a **single** pattern leg, name it: `REPORT OFF BASE`, `REPORT OFF FINAL`, `REPORT OFF CROSSWIND` (`XW`), `REPORT OFF DOWNWIND` (`DW`). The leg may come before or after the keyword — `REPORT BASE OFF` is equivalent to `REPORT OFF BASE`. The cancel keyword is interchangeable: `OFF`, `CANCEL`, `STOP`, and `NONE` all work (e.g. `REPORT CANCEL`, `REPORT STOP BASE`). A standing pattern-leg report keeps re-arming every circuit until you cancel it this way (or the aircraft lands full-stop), so cancel is the normal way to stop a lap-by-lap report you no longer need.

The leg keywords are the same whether arming or cancelling — `BASE`, `FINAL`, `CROSSWIND` (short form `XW`), `DOWNWIND` (short form `DW`) — so `REPORT DW` arms and `REPORT OFF DW` cancels the downwind report.

`REPORT` is phase-transparent: arming or cancelling a report never disturbs the aircraft's pattern or approach phase. The reports route through the same pilot-speech pipeline as the automatic midfield-downwind and short-final calls, so they are audible to tower and approach in solo training and shown to the RPO in instructor mode.

**Safety alerts:** `SAFAL <clock> <miles> [L|R] [C|D]` issues an aircraft-conflict safety alert. Examples: `SAFAL 12 1`, `SAFAL 12 1 L`, `SAFAL 12 1 D`, and `SAFAL 12 1 R C`. The command resolves a target by clock position and distance, but it does not set traffic-in-sight and does not satisfy `FOLLOW` or `CVA FOLLOW`; it exists for 7110.65 §2-1-6 safety-alert proof in solo training.

**Wake advisories:** `CWT` issues "caution wake turbulence" without changing the aircraft's flight path. `CTO ... CWT` and `CLAND [NODEL] CWT` include the same advisory inside the takeoff or landing clearance readback. In solo training, these forms record wake-advisory proof. Bare `CWT` is intentionally conservative: it suppresses a missing-advisory finding only when the Session Report has exactly one current wake-advisory context for that aircraft.

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
| `JARR SALI2` | Join STAR by name (airport inferred from destination; version digit optional, e.g. `SALI`) |
| `JARR SALI2 KENNO` | Join STAR via specific entry fix KENNO |
| `JARR SALI2 28R` | Join STAR for landing runway 28R (selects the runway transition, sets the destination runway) |
| `JARR SALI2 KENNO 28R` | Join STAR via entry fix KENNO for runway 28R |
| `JARR OAK.SALI2 KENNO` | Join STAR with both airport qualifier and entry fix |
| `JAWY V25` | Join airway: intercept and join airway V25, following fixes in the direction of travel |
| `JRADO SJC 150` | Join radial outbound: fly to SJC VOR, then outbound on the 150° radial |
| `JRADI SJC 150` | Join radial inbound: intercept and fly inbound on the 150° radial to SJC |
| `CFIX A034` | Cross next fix at or above 3,400 ft |
| `CFIX SUNOL A034` | Cross specific fix SUNOL at or above 3,400 ft |
| `DVIA` | Descend via STAR — enables altitude/speed constraint following on active STAR |
| `DVIA 240` | Descend via STAR, except maintain FL240 (altitude floor) |
| `DVIA SPD 180 SUNOL` | Descend via STAR with speed restriction (maintain 180 knots at SUNOL) |
| `CVIA` | Climb via SID — enables altitude/speed constraint following; self-activates the filed SID if none is active (e.g. after `CTO RH` or vectors) |
| `CVIA 190` | Climb via SID, except maintain FL190 (altitude ceiling) |
| `DEPART SUNOL 270` | Depart fix SUNOL on heading 270 |

`JARR` also accepts aliases `ARR`, `STAR`, `JSTAR`. `JRADO` also accepts `JRAD`. `JRADI` also accepts `JICRS`. `DEPART` also accepts `DEP` and `D`.

`JAWY` intercepts and joins a named airway (e.g., V25, J80). The aircraft flies its present heading until it intercepts the airway segment, then turns onto the airway course and follows the fix sequence in the direction of travel.

JARR supports CIFP altitude/speed constraints when available. The airport prefix (`OAK.`) is optional — when omitted, the aircraft's destination airport is used. The STAR name's version digit is optional (`SALI` resolves to the current `SALI2`). The second argument is an entry fix (e.g. `KENNO`) unless it looks like a runway (e.g. `28R`, `27`), in which case it selects the runway transition and sets the landing runway; a third argument combines both (`JARR SALI2 KENNO 28R`). When the entry fix is omitted, the nearest fix ahead of the aircraft is used.

CFIX supports two forms: `CFIX {altitude}` modifies the altitude restriction for the next fix in the route, while `CFIX {fix} {altitude}` targets a specific named fix. Altitude prefixes: `A` = at or above, `B` = at or below, no prefix = at exactly. CFIX uses step-based descent/climb planning — the aircraft computes the exact vertical rate needed to arrive at the constraint altitude precisely at the fix. CFIX is additive and applies in place: it stamps the restriction on the named fix without rerouting (the fixes ahead of it are kept), so multiple CFIX commands stack — each fix on the route retains its own restriction. If the named fix is not yet on the route (for example an aircraft being vectored with no STAR loaded), it is appended to the route, so a chain of CFIX builds a crossing profile in the order issued.

**Constrained DCTF** — `DCTF FIX1/A080 FIX2/050 FIX3` attaches altitude constraints inline. The `/` suffix uses the same CFIX altitude format. All constraints are visible to the planner simultaneously, so the aircraft plans descent across multiple waypoints at once.

**SID/STAR via mode** — When CIFP procedure data is available, [SID](#glossary) and [STAR](#glossary) fixes carry published altitude and speed restrictions. Via mode controls whether the aircraft follows those restrictions:

| Procedure | Default | Enable via mode | Disable via mode |
|-----------|---------|-----------------|------------------|
| SID (after CTO) | **ON** — aircraft follows published climb restrictions | `CVIA` (re-enable after `CM`, `CTO RH`, or vectors) | `CM` (any altitude command) |
| STAR (after JARR) | **OFF** — aircraft maintains altitude, follows lateral path only | `DVIA` (enable descent restrictions) | `DM` (any altitude command) |

`DVIA` no longer requires a prior `JARR`: when no STAR is active, it activates the STAR filed in the flight-plan route and applies that STAR's published crossing restrictions before descending. Symmetrically, `CVIA` no longer requires an active SID: when none is active — for example after `CTO RH` or being vectored off the SID — it activates the SID filed in the route and overlays its published crossing restrictions onto the current route. Issue `DCT <SID fix>` first to reload the lateral path, then `CVIA` to climb via.

`CVIA 190` and `DVIA 240` enable via mode with an altitude cap/floor — "climb/descend via, except maintain." `FH`, `DCT`, and heading commands clear the entire procedure (lateral path + via mode).

### Speed Management

| Command | Effect |
|---------|--------|
| `SPD 210` | Exact speed: maintain 210 knots |
| `SPD 210+` | Speed floor: maintain 210 knots or greater |
| `SPD 210-` | Speed ceiling: do not exceed 210 knots |
| `SPEEDF 180` / `SPDF` / `SLF` | Force speed: assign a speed that overrides the 5nm-final restriction (`+`/`-` floor/ceiling supported). Unlike `SPDN` it converges via physics rather than teleporting IAS |
| `RNS` / `NS` | Resume normal speed: clears speed/floor/ceiling, preserves SID/STAR via mode |
| `EXP` | Expedite climb/descent: increases vertical rate (approx 1.5x category rate). On the ground with an assigned taxi route, raises the taxi speed cap by ~30% (jet 30→39 kts; mutually exclusive with a numeric `SPD` taxi speed); cleared by next HOLD/RES/HS. On a just-landed aircraft (rolling out or exiting), expedites the runway exit instead — earliest reachable exit + max-effort braking (see the exit commands above) |
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

**Helicopter minimum** — a `SPD` value below 60 KIAS issued to an airborne helicopter is floored to 60 (the 7110.65 §5-7-3.5 minimum for radar-vectored helicopters), with a warning. Use a force-speed command (`SPEEDF` or the teleporting `SPDN`/`SPEEDN`) to command a lower speed.

**Taxi speed (ground)** — issued to a **taxiing** aircraft, `SPD {n}` sets its taxi speed to `n` knots — slower or faster than the category default (jet 30 / turboprop 25 / piston 20 / helo 15 kts). The value is clamped to 5 kts at the low end and to the expedite ceiling at the high end (jet 39 / turboprop 33 / piston 26 / helo 20 kts); corner slowdowns, hold-short braking, and conflict/give-way slowdowns still apply on top. It is mutually exclusive with `EXP` (issuing either clears the other) and persists across `HOLD`/`RES`. `SPD 0` (resume normal speed) restores the category default, as does a new `TAXI` clearance. Also fires from a conditional block, e.g. `AT B SPD 10` slows to 10 kts on reaching taxiway B.

**VFR altitude floor/ceiling (`CM A` / `CM B`)** — alongside the hard `CM 240` assignment, `CM A{altitude}` clears the aircraft to maintain VFR at or above the given altitude (floor) and `CM B{altitude}` at or below (ceiling). The altitude accepts shorthand or full notation via `AltitudeResolver` (`CM A025` = `CM A2500` = at or above 2,500 ft). The aircraft is free to drift inside the band; the boundary is what the controller assigns. **VFR aircraft only** — the command is rejected for IFR. A plain `CM {altitude}` (or any other hard altitude assignment) clears any active floor or ceiling.

**ATFN (at final)** — `ATFN {distance}` is a compound-block condition that fires when the aircraft is within the specified distance (in NM) of the assigned runway threshold. Use it to set up staged speed reductions on approach: `SPD 210; ATFN 10 SPD 180; ATFN 5 RNS`.

**SPD UNTIL shorthand** — `SPD 210 UNTIL 10` expands to `SPD 210; ATFN 10 RNS`. When chained, intermediate blocks are generated automatically: `SPD 210 UNTIL 10; SPD 180 UNTIL 5` becomes `SPD 210; ATFN 10 SPD 180; ATFN 5 RNS`. Fix-based UNTIL is also supported: `SPD 180 UNTIL AXMUL` expands to `SPD 180; AT AXMUL RNS`.

**Auto-cancel at 5nm final** — Per 7110.65 §5-7-1.b.4, an ATC speed assignment is released — and a new speed instruction (`SPD`, `RFAS`, `RNS`, `DSR`, `MACH`) declined with a pilot **"unable"** — when an aircraft is **inbound to land** within 5nm of the runway threshold (the controller can no longer adjust the speed; the pilot owns the approach speed). The instruction is refused, not obeyed; the aircraft stays on its stabilized approach (it is never knocked off the final). The aircraft never speeds back up: an arrival flying an approach or pattern slows to its own approach speed, and a hand-vectored arrival holds its last-assigned speed (or eases further down) but is never re-accelerated toward the descent default. This applies only on final — departures, go-arounds, and missed approaches climbing within 5nm of the field are **not** affected and accept normal speed assignments. To deliberately assign a speed to an arrival inside the 5nm gate (e.g. military or compression scenarios), use `SPEEDF`, which overrides the restriction and persists past the gate.

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
| `HPPL` / `HPPR` | Hold present position, left/right 360s |
| `HPP` | Hold present position (hover) — **helicopters only**; fixed-wing aircraft are rejected and should use `HPPL`/`HPPR` |
| `HFIXL {fix}` / `HFIXR {fix}` | Fly to fix, then left/right 360s |
| `HFIX {fix}` | Fly to fix, then hover — **helicopters only**; fixed-wing aircraft are rejected and should use `HFIXL`/`HFIXR` |

Winged-aircraft holds (`HPPL`/`HPPR`, `HFIXL`/`HFIXR`) decelerate to holding speed while orbiting and resume normal speed when the hold is cancelled. Any heading, altitude, or speed command clears the hold.

### Track Operations

Track operations control aircraft ownership, handoffs, and coordination. These commands use STARS-style [TCP](#glossary) codes (e.g., "2B" = subset 2, sector B), ERAM center codes (e.g., "C44" = center sector 44), ERAM→TRACON codes that name a neighboring terminal position by its single-character facility prefix plus TCP (e.g., "Q2B" = NorCal's Boulder sector — the prefix comes from the facility's vNAS `singleCharacterStarsId`), or interfacility codes that hand off to an adjacent terminal facility (e.g., "Δ3" = Fresno, "Δ31H" = Fresno's Chandler sector — entered with the tilde/delta key in CRC and resolved from the facility's STARS handoff IDs).

#### Active Position

By default, you operate as the scenario's student position. Use `AS` to act as a different position:

| Command | Effect |
|---------|--------|
| `AS 2B` | Set your active position to TCP 2B (persistent until changed) |
| `N135BS AS 2B TRACK` | Act as 2B for this command only (prefix mode) |

Resolution order: per-command `AS` prefix > persistent active position > student position default.

Ownership and pointout commands (`HO`, `ACCEPT`, `CANCEL`, `DROP`, `PO`, `OK`, `PORJ`, `PORT`) infer the acting position from the track itself — the current owner for `HO`/`CANCEL`/`DROP`/`PO`, the handoff target for `ACCEPT`, the pointout recipient for `OK`/`PORJ`, the pointout sender for `PORT` — so they never need an `AS` prefix. `TRACK` claims an unowned track and so must say who is claiming it: either implicitly (your active position, the student position by default), via an `AS` prefix, or directly with a `TRACK [position]` argument (e.g. `TRACK 3Y` — a one-shot equivalent of `AS 3Y TRACK`). `AS` still matters for the no-argument `PO` (which needs your position to tell an acknowledge from a retract) and for setting your persistent active position.

Changing your active position also updates the radar display:
- **DCB map shortcuts** switch to the position's configured 3x2 map group.
- **Weather airports** update to the position's STARS area's underlying airports.

#### Track Commands

| Command | Effect |
|---------|--------|
| `TRACK` | Initiate control — take ownership as your active position (the student position by default) |
| `TRACK 3Y` | Initiate control as a specific position — claims the track for TCP 3Y. Equivalent to `AS 3Y TRACK`, but one-shot: it does **not** change your persistent active position |
| `DROP` | Terminate control — release ownership (acts as the current owner) |
| `HO 3Y` | Handoff to TCP 3Y (initiated from the current owner) |
| `HO C44` | Handoff to ERAM center sector 44 (initiated from the current owner) |
| `HO Q2B` | Handoff from a Center sector to a neighboring TRACON position named by its facility prefix + TCP (e.g. `Q2B` = NorCal Boulder); the bare TCP (`HO 2B`) also works |
| `HO Δ3` | Handoff to an adjacent terminal facility by its interfacility code (the Δ/tilde entry — e.g. `Δ3` to Fresno, `Δ31H` to Fresno's Chandler sector), decoded from the facility's STARS handoff IDs |
| `HOF 3Y` | Force handoff to TCP 3Y (transfers ownership regardless of current owner) |
| `ACCEPT` / `A` | Accept a pending inbound handoff (acts as the handoff target) |
| `CANCEL` | Retract a pending outbound handoff (acts as the current owner) |
| `ACCEPTALL` | Accept all pending inbound handoffs (global — no callsign needed) |
| `HOALL 3Y` | Handoff all your aircraft to TCP 3Y (global — no callsign needed) |
| `PO 3Y` | Point out to TCP 3Y |
| `OK` | Acknowledge a pending pointout |
| `PORJ` | Reject a pending inbound pointout |
| `PORT` | Retract your outbound pending pointout |
| `CAACK` | Acknowledge conflict alerts for this aircraft |
| `CAINH` / `CAI` | Toggle conflict alert inhibit on/off |
| `CT` | Tell pilot to contact the next controller — auto-resolves to the just-accepted handoff target |
| `CT OAK_TWR` / `CT CONT OAK_TWR` | Tell pilot to contact a specific position by callsign (use to disambiguate when two positions share a STARS scope, e.g. OAK_TWR vs OAK_GND on 3O) |
| `CT 121.9` | Tell pilot to contact a position by frequency in MHz (±5 kHz tolerance covers 25 kHz and 8.33 kHz spacing) |
| `CT 3O` | Tell pilot to contact a position by TCP code; first match wins on ambiguity, prefer callsign or frequency forms when multiple positions share a TCP |
| `FCA` | Frequency change approved — VFR dismissal when there is no next controller (FAA 7110.65 §7-6-11) |
| `CLBRV` / `CBRV` / `BRAVO` | Cleared through/to enter/out of Bravo airspace. In solo training, satisfies the VFR Class B entry gate (FAA 7110.65 §7-9-2). |
| `STBY` / `STANDBY` / `ROGER` / `RGR` | Acknowledge pilot contact without issuing a maneuver. In solo training, satisfies the VFR Class C two-way-comms gate when targeted at that aircraft (AIM 3-2-4; FAA 7110.65 §7-8-4). |
| `GHOST N12345 28R` | Create ghost track off 28R (auto-stagger, scenario airport) |
| `GHOST N12345 KOAK 28R` | Create ghost track off 28R at KOAK |
| `GHOST N12345 37.7 -122.2` | Create ghost track at exact position |
| `AN 3 RV` / `BOX 3 RV` | Write "RV" in strip annotation box 3 (boxes 1-9) |
| `AN 3` | Clear strip annotation box 3 |
| `AN 3 ?` | Writes a checkmark (✓) in strip annotation box 3. Typing `?` in the inline annotation editor substitutes live; the server also normalizes any `?` on incoming AN commands (per CRC docs/crc/vstrips.md:130). |
| `STRIP Ground` | Push flight strip to "Ground" bay (first-available slot in rack 1) |
| `STRIP Ground/2/3` | Push flight strip to Ground bay rack 2, slot 3 (1-based) |
| `SCAN NCT` | Copy flight strip to external "NCT" bay; originator keeps the strip in place. External bays only — internal-bay SCAN errors. |
| `SCAN NCT/2/1` | Copy to external NCT bay, rack 2 slot 1. Repeat scans to the same bay stack as separate copies. |
| `HSC Ground Hello\World` | Create half-strip in Ground bay (rack defaults to 1) with two lines (`\` separates lines, max 6) |
| `HSC Ground/2 line2` | (with aircraft selected) Create half-strip in Ground rack 2 with callsign as line 1, "line2" as line 2 |
| `HSA Hello\Updated\Body` | Amend half-strip whose first line is "Hello" — replaces all lines with `Updated`, `Body` |
| `HSA Ground Hello\New` | Same, scoped to "Ground" bay (use to disambiguate when key matches in multiple bays) |
| `HSD Hello` | Delete half-strip whose first line is "Hello" (auto-search across bays) |
| `HSD Ground Hello` | Delete with explicit bay scope |
| `SP1 OAK` | Set scratchpad 1 |
| `SP1` | Clear scratchpad 1 |
| `SP2 I8R` | Set scratchpad 2 |
| `SP2` | Clear scratchpad 2 |

STARS scratchpads hold at most **3 characters** (4 when the facility enables 4-character scratchpads). A longer entry is rejected with `FORMAT` and the existing value is left unchanged. The ASDE-X (`ASDXSP1`/`ASDXSP2`) and ERAM scratchpads are governed by their own rules and are not bound by this limit.

Scratchpads support **undo/toggle**: entering the same value again restores the previous value, and clearing an already-cleared scratchpad restores the previous value.

#### Note

| Command | Effect |
|---------|--------|
| `NOTE Watch wake, exam prep` | Set the instructor note shown on the aircraft's datablock |
| `NOTE` | Clear the note |

`NOTE` is an **instructor freetext annotation** distinct from STARS scratchpads. It preserves case and spaces, is capped at **40 characters** (longer text is truncated), and renders as an extra **amber line at the bottom of the datablock** on both the radar (STARS and EuroScope tag styles) and ground views. It follows the aircraft across views, reconnects, and recordings, but is **instructor-only — it is never projected onto the student CRC STARS/Tower scopes**. Besides the command, a note can be set by right-clicking the aircraft (Data Block → Note… on the radar; Note… on the ground and aircraft-list menus; or clicking the note line on a EuroScope tag).

#### Half-Strips

`HSC`, `HSA`, and `HSD` create / amend / delete free-form vStrips half-strips. They run in two modes:

- **Global** (no aircraft selected) — the user types every line of the half-strip.
- **Aircraft-scoped** (an aircraft is selected) — the callsign is automatically used as line 1 and as the lookup key for amend/delete.

Lines are separated by a literal backslash `\` and capped at 6 lines total. The bay name is matched case- and whitespace-insensitively, so `Ground 1` can be referenced as `Ground1`. An optional rack is appended with `/` as a 1-based integer, e.g. `Ground1/2` targets the second rack of `Ground 1`. Without a rack, the half-strip lands on the first rack. Every vStrips wire format uses this same `bay[/rack[/index]]` slash-compound form — STRIP, HSC, HSM, SEP, SEPE, SEPD, BLANK, and BLANKD are all 1-based on the wire.

`HSA` and `HSD` do **not** require a bay name. They search every accessible strip bay for a half-strip whose first line matches the lookup key (case-insensitive). If exactly one half-strip matches, it is amended or deleted; if more than one matches across bays, the command fails and lists the bay/rack pairs so the user can disambiguate by adding the bay explicitly.

**Bay vs. key disambiguation rule (HSA / HSD only):** the parser treats the first whitespace-separated token as a bay specifier *if and only if* it contains no `\` AND there is at least one more token after it. Otherwise the entire argument is the body. Examples:

| Input | Bay? | Body |
|-------|------|------|
| `HSA key\new1\new2` | — | `key\new1\new2` (auto-search) |
| `HSA Ground key\new1` | `Ground` | `key\new1` |
| `HSA Ground/2 key\new1` | `Ground` rack `2` | `key\new1` |
| `HSA key` | — | `key` (single token) |
| `HSA HSTRIP_<id> new1\new2` | — | `HSTRIP_<id>\new1\new2` (id-form lookup) |

Because of this rule, a single-token global delete like `HSD Ground` is interpreted as "delete the half-strip with first line `Ground`" (auto-search), not as "delete the aircraft-scoped half-strip in bay `Ground`". Aircraft-scoped delete with no bay is just `HSD`.

**Strip-id form (UI default):** if the first token starts with `HSTRIP_` it is always treated as a strip id (lookup matches by `Id`, not by first-line text), never as a bay name. The strips UI and the CRC → canonical translator always emit this shape so two half-strips with the same first-line text remain individually addressable. Empty half-strips with no first-line text also work: `HSD HSTRIP_<id>`, `HSA HSTRIP_<id> line1\line2`, `HSO HSTRIP_<id>`, `HSS HSTRIP_<id>`. Mirrors the `SEP_<id>` and `BLANK_<id>` id-prefix handling on `SEPD` / `SEPE` / `SEPM` / `BLANKD`. Half-strip and separator ids are 8-char hex (e.g. `HSTRIP_aece26a3`); legacy 32-char GUID ids in older recordings keep working.

**Full-strip id form (UI default for STRIPD / STRIPO / AN / STRIP):** the four full-strip verbs accept an optional leading full-strip id token to address a specific strip. A full-strip id is `STRIP_<id>` (a departure or a scanned copy `STRIP_{callsign}_{shortGuid}` sharing its callsign with the original) or `ARRIVAL_{callsign}` (an arrival strip — the only way to move or delete one, since its id is not a bare callsign). `STRIPD STRIP_<id>` / `STRIPD ARRIVAL_<id>` deletes a specific strip; `STRIPO …` toggles offset; `AN STRIP_<id> 3 RV` annotates; `STRIP STRIP_<id> Local/2/3` moves. Terminal users keep the bare callsign-keyed shorthand. Bare `STRIP {bay}` moves an existing strip only — if no `STRIP_{callsign}` strip exists it errors rather than creating a blank one.

**`AN` and `STRIPO` require the strip to be in a bay.** Both are rejected while the strip is still in the printer ("…is still in the printer — move it to a bay first"): annotation boxes are edited in a bay and offset slides a strip within a rack, neither of which applies to an unfiled printer strip (matches vStrips, whose printer view only offers *Move to Bay*). Use `STRIP <bay>` to file it first, or `STRIPD` to discard it — both still operate on printer strips.

| `TA 120` / `QQ 120` | Set temporary altitude (in hundreds, e.g., 120 = FL120) |
| `CRUISE 240` / `QZ 240` | Set cruise altitude |
| `PRA 250` | Set pilot reported altitude (in hundreds; `PRA 0` clears) |
| `ONHO` / `ONH` | Toggle on-handoff status |
| `LDR 3` | Set leader line direction (1-9; `LDR 5` = default) |
| `JRING 3` / `JRING` | Draw a TPA J-Ring of radius 3 NM on **your** radar (1-30 NM); bare `JRING` clears it |
| `CONE 5` / `CONE` | Draw a TPA Cone of length 5 NM along the target's track on **your** radar (1-30 NM); bare `CONE` clears it |

`JRING` and `CONE` are instructor-only proximity tools that emulate the STARS TPA J-Ring / Cone (`*J` / `*P`) on YAAT's own radar view. They are **never** drawn on the student's CRC scope — the student's automatic ATPA "P-cones" and their own manual TPA graphics are unaffected. The Cone's wedge angle defaults to the CRC-exact 2° and is adjustable under Settings → Display → Overlays.

#### vTDLS (Pre-Departure Clearance)

Real vNAS exposes a separate web app, [vTDLS](https://tdls.virtualnas.net/),
that controllers use to issue PDCs. YAAT emulates this as both an in-client
tab and a browser app served from `/vtdls/` on the deployed server.

**Auto-queue:** When a flight plan is filed at a TDLS-configured facility
(e.g. KOAK, KSFO, KSJC, KSMF, KRNO under NCT), the server auto-emits a
`TDLSQ` internally. The Pending callsign appears in the DCL list without
controller action. Pre-files (CID with a filed plan but no active sim
aircraft) also generate entries.

**Send:** `TDLSS` ships the prepared clearance with nine `|`-separated
fields. The vTDLS UI's flight-plan editor builds the canonical string from
the dropdown selections. Mandatory-field validation (per facility) is
enforced before the Send button enables — typing the canonical bypasses
the UI but the server-side mandatory-field check still applies.

| Form                                       | Effect                                                              |
|--------------------------------------------|---------------------------------------------------------------------|
| `TDLSS Expect\|Sid\|...`                   | Issue PDC with the nine pipe-separated fields.                      |
| (empty between pipes)                      | Field is null on the server side.                                   |

After Send: an RPO-visible terminal entry fires (`[TDLS PDC sent at OAK]
Expect=10 MIN, SID=OAKLAND4.ALTAM, …`), and the pilot's clearance is
applied silently — no voice readback. Roughly 3 seconds later, the server
auto-fires `TDLSW` (Wilco). The controller can force this earlier with a
manual `TDLSW`.

**Dump:** `TDLSDUMP` (alias `TDLSD`) removes the PDC and locks out the
(facility, callsign) pair for the rest of the session. The pilot loses the
TDLS-silent flag, so a subsequent `CL` voice clearance behaves normally.

Items time out at 2 hours after queue; the TTL sweep happens in
`TickProcessor.ProcessTdlsExpiry`. Snapshots preserve Pending/Sent items
and the Dumped lockout; per-facility configs are re-fetched from the vNAS
data-api on session restore.

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

### Hold for Release (HFR / REL)

Models the real-world departure-release coordination a TRACON provides to satellite towered
airports: an IFR departure may not take off until released. These are global commands — the airport
or callsign rides in the argument, no aircraft selection is needed.

| Command | Effect |
|---------|--------|
| `HFR <airport>` | Arm hold-for-release for an airport. Its **IFR** departures are then held until released — those that spawn airborne/on-the-runway don't appear until released; those at parking/taxiway taxi out and **hold short** of the runway (they never take the runway while held). VFR departures are unaffected. |
| `HFROFF <airport>` | Disarm the airport. Anything still held there is auto-released. |
| `REL <airport>` (alias `CTOA <airport>`) | Release the **next pending** departure at that field (earliest-scheduled first). |
| `REL <callsign>` | Release a specific held departure. |
| `REL <airport> <minutes>` | Release the field's **whole** held queue, auto-spaced by the given interval in minutes (e.g. `REL SJC 2` = one every two minutes). |

Released departures don't pop airborne instantly — a held runway/airborne departure appears after a
20–60 s delay; a held ground departure is auto-cleared for takeoff once it's holding short (after a
short readback delay) and departs normally. The **Releases** flyout on the command bar shows the live
rundown of what's held at each armed field with click-to-release buttons; a held departure also gets a
one-click "Release (HFR)" item in its radar right-click menu.

### Call for Release (CFR)

Models the Call-for-Release coordination a tower performs with the overlying TRACON/Center for a
departure that needs an approved release time. The instructor grants the release and YAAT tracks the
FAA compliance window, alerting if the departure gets airborne outside it. **Alert-only** — it never
blocks a takeoff and never changes aircraft behavior. Aircraft-scoped (select the departure first),
unlike the airport-scoped `HFR`/`REL`.

| Command | Effect |
|---------|--------|
| `CFR <HHMM>` | Release the selected on-ground departure with a window of **2 min before to 1 min after** the assigned Zulu time (FAA 7110.65 §4-3-4.e.5). E.g. `CFR 1830` → window 1828–1831Z. |
| `CFR` (no time) | **Immediate release** — the same −2/+1 window, assigned 2 min out so it **opens right now** (e.g. window 1800–1803Z). |
| `CFR OFF` (or `CANCEL`) | Clear the release window on the selected departure. |
| `CFR CHECK` (or `STATUS`) | Print the current window status to the terminal (opens-in / closes-in / expired), without changing it. |

The window is tracked against **real wall-clock UTC** and is purely an instructor aid, so it is
deliberately unaffected by pause / rewind / fast-forward (its alerts may be inconsistent while
scrubbing a replay). YAAT raises an instructor warning — an amber terminal line, plus a speech bubble
on the aircraft when warning bubbles are enabled — when the departure **departs after** the window
expires (late), **departs before** the window opens (early), or is **still holding for release** when
the window expires. Rejected if the aircraft is already airborne (nothing to release).

While a window is active, the **Aircraft List** "Info" column shows a live amber `CFR M:SS` countdown
badge for that departure (turning red `CFR EXP` past the window); the aircraft's right-click menu
(radar / ground / list) also gains a **Check release window** item that runs `CFR CHECK`.

### Timer (TIMER / TMR)

A countdown reminder. When it expires it posts a green SAY-style line to the terminal — the free-text
message, or **`timer expired`** when none is given. Timers count in **sim time** (they pause when the
sim is paused and scale with sim rate).

| Command | Effect |
|---------|--------|
| `TIMER <mm:ss\|seconds> [text]` | Set a **global** reminder (no aircraft selected). On expiry the terminal shows `TIMER → text` (or `TIMER → timer expired`). |
| `<callsign> TIMER <mm:ss\|seconds> [text]` | Set a timer **attributed to an aircraft** — on expiry the SAY is shown as `<callsign> → text`, like a normal SAY from that aircraft. |
| `TIMER CANCEL <id>` | Cancel a specific timer by its id (shown in the timers panel). |
| `TIMER CANCEL ALL` | Cancel every running timer. |

Duration accepts `mm:ss` (`5:00`, `1:30`) or bare seconds (`90`). The message is free text and may
contain commas. The **timers panel** on the command bar shows each running timer's live countdown
(`mm:ss`), its label, and a one-click cancel (`✕`); the panel button's caption is the soonest timer's
remaining time. `TMR` is an alias for `TIMER`.

### Add Aircraft (ADD)

Spawn an aircraft on demand without a scenario file. Requires an active scenario.

**Syntax variants:**

| Variant | Syntax | Example |
|---------|--------|---------|
| Airborne | `ADD {rules} {weight} {engine} -{bearing} {dist} {alt}` | `ADD IFR H J -270 15 10000` |
| At fix | `ADD {rules} {weight} {engine} @{fix} {alt}` | `ADD IFR L J @SUNOL 8000` |
| Lined up on runway | `ADD {rules} {weight} {engine} {runway}` | `ADD VFR S P 28R` |
| Departure on runway | `ADD {rules} {weight} {engine} {runway} {route}` | `ADD IFR S P 28R NIMI6.OAK.SAU` |
| On final | `ADD {rules} {weight} {engine} {runway} {dist}` | `ADD IFR L J 28R 8` |
| At parking/helipad | `ADD {rules} {weight} {engine} @{spot}` | `ADD VFR S H @H1` |
| Arrival on STAR | `ADD {rules} {weight} {engine} {wpt}.{star}[.{rwy}] [alt] [SP{spd}] [LVL] [airport]` | `ADD IFR H J TBARR.TBARR4.34R 230` |

**Parameters:**

| Parameter | Values |
|-----------|--------|
| Rules | `I`/`IFR` (instrument) or `V`/`VFR` (visual) |
| Weight | `S` (small/GA), `S+` (smallplus — regional/commuter), `L` (large/narrow-body), `H` (heavy) |
| Engine | `P` (piston), `T` (turboprop), `J` (jet), `H` (helicopter) |

**Position arguments:**
- **Airborne**: `-{bearing}` is degrees from the primary airport, `{dist}` is distance in NM, `{alt}` is the altitude (see below). Aircraft spawns heading toward the airport.
- **At fix**: `@{fix}` is a fix name or FRD, `{alt}` is the altitude (see below). Aircraft spawns at the fix heading toward the primary airport.
- **Lined up**: `{runway}` is the runway designator (e.g., `28R`). Aircraft spawns on the runway threshold, ready for takeoff clearance.
- **Departure on runway**: `{runway}` plus a dot-joined `{route}` (e.g., `NIMI6.OAK.SAU`, converted to the filed route `NIMI6 OAK SAU`). Spawns lined up on the runway with the route filed and the departure airport set, so a subsequent `CTO` flies the filed SID. IFR only — the route is ignored for VFR. A numeric token after the runway is the on-final distance, not a route.
- **On final**: `{runway}` plus `{dist}` in NM. Aircraft spawns on final approach at that distance from the runway.
- **At parking/helipad**: `@{spot}` is a parking or helipad name (e.g., `@H1`, `@B12`). Aircraft spawns at ground level. Useful for helicopters and ground operations — use the `H` engine token (`ADD VFR S H @H1`) to spawn a helicopter.
**Altitude argument (`{alt}`):** same shorthand as every other altitude in YAAT. A number below 1000 is hundreds of feet (`035` = 3500 ft, `005` = 500 ft, `230` = FL230); a number at or above 1000 is literal feet (`3500` = 3500 ft). `{airport}+{hundreds}` is AGL above field elevation (`KOAK+010` = 1000 ft AGL). Zero and negative values are rejected.

- **Arrival on STAR**: a dotted `{waypoint}.{star}[.{runway}]` token (e.g. `TBARR.TBARR4.34R`) spawns an IFR aircraft already established on the arrival at the named waypoint, heading down the route. By default it **descends via** the STAR's published crossing restrictions from its current altitude; add `LVL` to hold the altitude until you issue `DVIA`. Optional trailing tokens, any order: a bare number = current altitude in hundreds (e.g. `230` = FL230; omit to auto-compute a realistic establishment altitude from the STAR profile); `SP{kts}` = speed override (e.g. `SP250`); `{airport}` = ICAO/FAA destination for multi-airport STARs (defaults to the primary scenario airport). Runway transition is optional — omit it (`TBARR.TBARR4`) to fly the common legs and resolve the runway portion later. IFR only. *Note:* descending-via at spawn is a trainer convenience — by the book (7110.65 §4-5-7 / AIM §5-4-1) an aircraft on a STAR holds its altitude until ATC issues "descend via", so use `LVL` when you want to issue the descent yourself.

**Optional trailing tokens:**
- Explicit aircraft type: `ADD IFR H J -090 20 15000 B77L`
- Explicit airline prefix: `ADD IFR L J -180 10 5000 *SWA`

**Valid weight/engine combinations:**

| | Piston (P) | Turboprop (T) | Jet (J) |
|---|---|---|---|
| Small (S) | C172, C182, P28A, SR22 | C208, PC12, BE20 | — |
| SmallPlus (S+) | — | AT72, DH8C, SF34, B190, B350 | C560, C56X, C680, LJ60, LJ45 |
| Large (L) | — | — | B737, B738, B739, A319, A320, A321, CRJ7, E170, E75L, E145, E135 |
| Heavy (H) | — | — | B763, B764, B772, B788, B744, A332, A333 |

Tiers track RECAT CWT category: Small = CWT I (light GA), SmallPlus = CWT H (upper-small business jets and commuter turboprops), Large = mainline narrow-body plus regional jets (CWT F/G), Heavy = wide-body. The SmallPlus turboprop pool keeps the CWT G commuter turboprops because their approach speeds stay short-field appropriate.

**Helicopters (H):** the `H` engine token spawns a helicopter. With no explicit type it auto-selects a light civil helicopter (R22, R44, B06). The weight token is cosmetic for helicopters — any weight resolves to the same light pool. Name any other helicopter explicitly to override it (e.g. `ADD V S H @H1 H60`, `ADD V S H @H1 EC35`); the type drives the Helicopter category automatically. Helicopters spawned at a helipad/parking sit on the spot until you clear them (`CTOPP` for a present-position vertical departure, `ATXI` to air-taxi). See [Helicopter Commands](#helicopter-commands).

IFR aircraft get a random airline callsign (e.g., UAL1234). VFR aircraft — and helicopters, which no scheduled airline operates — get an N-number (e.g., N1234A).

**Beacon codes.** An IFR spawn squawks a discrete code drawn from the facility's IFR code banks — the same allocator that assigns a code when you file a flight plan — so it never duplicates a live aircraft's code and never lands on a reserved one. A VFR spawn is a cold call: no assigned code, squawking `1200` until you file a flight plan for it. See [Automatic beacon-code assignment](#squawk--transponder).

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

### Auto-Delete on Hold-Short

For busy tower / local scenarios with a steady arrival flow, landing aircraft pile up at the post-runway hold-short and have to be manually deleted by the controller before they clog the scope. `ONHS DEL` queues a per-aircraft auto-delete that fires the moment the aircraft transitions into the `HoldingAfterExit` phase (i.e., it has rolled out, taken the runway exit, and stopped at the next intersecting taxiway).

| Command | Effect |
|---------|--------|
| `ONHS DEL` | Queue auto-delete for this aircraft. Fires when it reaches the hold-short after landing. Bypasses `AutoDeleteExempt` (controller explicitly asked). |
| `NODEL` | Cancel the queued auto-delete and re-arm `AutoDeleteExempt` so scenario-level auto-delete also leaves the aircraft alone. |

`ONHS DEL` can be issued any time during the approach or rollout (typically during the landing rollout once an exit has been chosen). The pilot still calls "clear of runway" on phase entry before the delete fires.

The radar / Tower Cab datablock shows a trailing `*` on the callsign while the auto-delete is armed, so you can see at a glance which aircraft are pre-marked. The `*` clears either when `NODEL` cancels the request or when the auto-delete itself removes the aircraft a moment later.

`NODEL` as a bare verb is distinct from the `NODEL` *modifier* on `CLAND` / `LAND` / `TAXI` / `EL` / `ER` / `EXIT`. The modifier sets `AutoDeleteExempt` at the time those commands are issued; the bare verb does the same plus strips any queued `ONHS DEL` block.

### Force Override Commands

When you need to immediately correct an aircraft's state (rather than waiting for flight physics to gradually adjust):

| Command | Effect |
|---------|--------|
| `FHN 270` | Force heading: immediately set heading to 270° |
| `CMN 50` | Force altitude: immediately set altitude to 5,000 ft |
| `SPDN 250` | Force speed: immediately set IAS to 250 knots (aliases: `SLN`, `SPEEDN`) |
| `TRATE 3` | Set turn rate: override default turn rate to 3°/sec (range 0.5–45; omit argument to clear) |

These commands set the aircraft's state immediately — no gradual transition. Useful for RPO corrections when an aircraft is in the wrong state. `TRATE` overrides the category-based turn rate for fine control over vectoring behavior.

Note: `SPDN` teleports IAS instantly. To assign a speed that overrides the 5nm-final restriction but still converges *gradually* via physics (the realistic controller instruction), use `SPEEDF` instead (see the Speed commands above).

### Warp Commands

Teleport an aircraft to a specific position:

| Command | Effect |
|---------|--------|
| `WARP OAK005002 020 050 120` | Warp to OAK 005°/2nm, heading 020, altitude 5,000 ft, speed 120 kts |
| `WARP SJC 180 100 250` | Warp to SJC fix, heading 180, altitude 10,000 ft, speed 250 kts |
| `WARP SJC` | Teleport to SJC keeping current heading, altitude, and speed |
| `WARP SJC 270` | Teleport to SJC and turn to 270; altitude/speed unchanged |
| `WARP SJC 5000` | Teleport to SJC and set altitude 5,000 ft; heading/speed unchanged |
| `WARP SJC 5000 220` | Teleport to SJC, altitude 5,000 ft, speed 220 kts; heading unchanged |
| `WARPG C B` | Warp to the intersection of taxiways C and B (ground aircraft only) |
| `WARPG #42` | Warp to node ID 42 (ground aircraft only; use Ctrl+D debug overlay to find IDs) |
| `WARPG @B12` | Warp to parking B12 (ground aircraft only) |
| `WARPG $9` | Warp to taxi spot 9 (ground aircraft only) |

**WARP** accepts a fix name or [FRD](#fix-radial-distance-frd) as the position. The trailing heading, altitude, and speed are optional — when omitted, the aircraft keeps its current value for that parameter (the same way the radar context-menu Warp pre-fills current values).

Trailing tokens are matched left-to-right against `heading → altitude → speed`. A token fills `heading` only if it is an integer in 1–360; otherwise it skips heading and is tried against altitude (`AltitudeResolver`: shorthand hundreds, full feet, or AGL form), then speed. So `WARP SJC 5000` sets altitude 5,000 ft (the value can't be a heading), and `WARP SJC 270` sets heading 270 (any value 1–360 is taken as a heading first). To set altitude alone in heading-overlap range, use full feet (e.g., `WARP SJC 5000`) rather than shorthand (`50`).

**WARPG** accepts two taxiway names (finds their intersection), a node reference (`#nodeId`), a parking (`@parking`), or a taxi spot (`$spot`) — matching the `@`/`$` prefixes used by `TAXI`/`PUSH`. Use the Ground View debug overlay (Ctrl+D) to find node IDs.

### Say Commands

Make an aircraft broadcast information. Output uses spoken pilot phraseology per AIM 4-2-8/9/10/11 (digit-by-digit numbers, "thousand"/"hundred"/"flight level" altitudes, three-digit headings, "Mach point X").

| Command | Effect |
|---------|--------|
| `SAY text` | Aircraft broadcasts the text verbatim (alias: `SAYF`) |
| `SSPD` | Aircraft reports current speed (e.g. `two five zero knots`; includes Mach at/above FL240) |
| `SMACH` | Aircraft reports current Mach number (e.g. `Mach point seven eight`) |
| `SEAPP` | Aircraft reports expected approach (e.g. `Expecting the ILS one niner left approach`) or `Negative, no approach assigned` |
| `SALT` | Aircraft reports altitude and vertical trend (e.g. `Leaving five thousand three hundred for eight thousand`) |
| `SHDG` | Aircraft reports heading (e.g. `Heading two seven zero, direct MENLO`) |
| `SPOS` | Aircraft reports position relative to a fix on its filed route, DCT queue, or departure/destination airport, with a parenthetical airport reference when the anchor is a fix (e.g. `one two miles east of WAITZ (three zero miles east of KOAK)`). Falls back to the nearest sizeable airport when off-route. |

**Combine with `AT` for deferred reports.** Wrap any SAY-class verb in an `AT <fix>` condition to make the aircraft transmit when overflying that fix:

```
AT WAITZ SALT
AT MENLO SHDG
AT RHV SAY position report
```

Triggered SAY transmissions fire when the aircraft passes within 0.5 NM of the fix (same threshold as any other `AT FIX` condition). They appear in the terminal as a `Say`-typed entry tagged to the aircraft's callsign.

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

The fix name must be 2+ characters, followed by exactly 3 digits for the radial and 3 digits for the distance. FRD works anywhere a fix name is accepted (DCT, AT conditions, WARP, etc.). The radial is magnetic (aligned with the fix's magnetic variation), matching real-world VOR-radial convention (7110.65 §4-4-3).
