# Command Aliases Reference

Sources:
- **ATCTrainer**: https://atctrainer.collinkoldoff.dev/docs/commands
- **VICE**: https://pharr.org/vice/#atc-commands
- Structured extracts: `atctrainer-commands.json`, `vice-commands.json` (regenerate with `build.py`)

**Bold** = implemented in YAAT. Normal = not yet implemented.
YAAT aliases = what's actually in the code presets (primary alias listed first).

🟢 = **YAAT-only** — functionality that neither ATCTrainer nor VICE offers.

---

## Implementation Summary

88 `CanonicalCommandType` enum values, each with aliases in both presets. 92 bold rows below (4 extra rows are GA argument variants and the Give Way condition prefix, which map to existing types).

| Category | Implemented | Not yet | Notes |
|---|---|---|---|
| Heading | 6/8 | 2 | Missing: Turn (dir as arg), Say Heading |
| Altitude | 2/9 | 7 | Missing: Assign Alt, Expedite, Normal Rate, Cross Fix, CVIA/DVIA, Say Alt |
| Speed | 1/11 | 10 | Missing: Min/Max/Present/Cancel/Floor/Ceiling, Mach, RNS, RFAS, Say Speed |
| Transponder | **9/9** | 0 | Complete |
| Navigation | 1/6 | 5 | Missing: Depart Fix, Join STAR/Airway/FAC, Airport/Dest |
| Tower | 16/24 | 8 | Missing: CTO+traffic (×2), CTO present pos, CTO rwy hdg, FS†, LAND, LAHSO, GO |
| Approach | 0/6 | 6 | Not started |
| Pattern | 11/22 | 11 | Missing: Crosswind entry (×2), 270, 360s (×2), S-turns (×2), MNA, MSA, NO270, Pattern Size |
| Hold | 6/7 | 1 | Missing: Hold (general — custom course/distance/direction) |
| Sim Control | **8/8** | 0 | Complete (includes 3 YAAT-only) |
| Track Ops | 18/19 | 1 | Missing: Strip |
| Coordination | **5/5** | 0 | Complete (all YAAT-only) |
| VFR | 0/3 | 3 | Not started |
| Ground | 7/10 | 3 | Missing: Taxi All, HS†, Break |
| Misc | 2/12 | 10 | Missing: FP, VP, Remarks, SAYF, Cleared, Delete At, Open Chat, OPS, Show At, Global Msg |
| Debug | 0/6 | 6 | Not planned |

**Total: 92/165** bold rows. †FS works as a CTL alias; HS works via `TAXI ... HS` syntax — both functional but not standalone commands.

---

## YAAT-Only Commands

Commands and behaviors that exist only in YAAT — not available in ATCTrainer or VICE.

| Command | Aliases | Description |
|---|---|---|
| 🟢 **Cleared to Land** | CTL, FS | Explicit landing clearance. ATCTrainer has no equivalent (pattern aircraft land implicitly; `CTL` there means Clear Approach). VICE has no tower commands. |
| 🟢 **Cancel Landing Clearance** | CLC, CTLC | Revokes a landing clearance. Neither app has this. |
| 🟢 **Cleared for the Option** | COPT | Cleared for touch-and-go, stop-and-go, low approach, or full-stop landing at pilot's discretion. Neither app has this. |
| 🟢 **Go Around (heading + altitude)** | GA {hdg} {alt} | Go around with assigned heading and altitude (e.g., `GA 270 5000`). ATCTrainer only has `GAMLT`/`GAMRT`; VICE's `GA` means "go ahead" (VFR acknowledgment). YAAT also supports `GA RH {alt}` (runway heading). |
| 🟢 **Spawn Now** | SPAWN | Immediately spawns a delayed aircraft. Neither app has delayed spawn control. |
| 🟢 **Set Spawn Delay** | DELAY | Sets a new spawn countdown in seconds (accepts `M:SS` format, e.g., `DELAY 2:00`). |
| 🟢 **Wait (distance)** | WAITD | Executes a queued command after the aircraft travels a specified distance. ATCTrainer's `WAIT` is time-only. |
| 🟢 **Release (Rundown)** | RD | STARS departure release coordination. |
| 🟢 **Hold Release** | RDH | Hold a departure release. |
| 🟢 **Recall Release** | RDR | Recall a departure release. |
| 🟢 **Acknowledge Release** | RDACK | Acknowledge a departure release. |
| 🟢 **Toggle Auto-Ack** | RDAUTO | Toggle automatic release acknowledgment for a coordination list. |

### YAAT-only alias additions

These commands exist in ATCTrainer/VICE, but YAAT adds aliases that neither app uses:

| YAAT alias | Canonical command | Why |
|---|---|---|
| `SQVFR` | Squawk VFR | Clearer than ATCTrainer's `SQV` |
| `SQNORM` | Squawk Normal | Clearer than ATCTrainer's `SN` |
| `SQSBY` | Squawk Standby | Clearer than ATCTrainer's `SS` |
| `IDENT` | Ident | Clearer than ATCTrainer's `ID` |
| `BEHIND` | Give Way | Alternative to ATCTrainer's `GIVEWAY` |
| `RESUME` | Resume Taxi | Alternative to ATCTrainer's `RES` |
| `FOL` | Follow | Shorthand for ATCTrainer's `FOLLOW` |
| `HP` | Hold Position (ground) | Shorthand for ATCTrainer's `HOLD` |

### YAAT-only behavioral differences

| Behavior | ATCTrainer | VICE | YAAT |
|---|---|---|---|
| **Pause/Unpause** | Separate commands | Single toggle (`P`) | Separate commands (PAUSE/UNPAUSE) |
| **Hold-short** | Standalone `HS` command | — | Integrated into `TAXI` syntax: `TAXI S T U HS 28L` |
| **Auto go-around** | — | — | Aircraft without landing clearance at 0.5nm auto-executes go-around with broadcast warning |
| **Give Way** | Standalone command | — | Condition prefix (like `AT`/`LV`): `GIVEWAY SWA5456; TAXI S T U` |
| **Squawk no-arg** | Resets to assigned code | — | Resets to flight-plan-assigned code (same as ATCTrainer) |
| **Ident timeout** | — | — | Auto-clears after 18 seconds |

---

## Heading

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Fly Heading** | FH | H{hdg} | FH | H |
| **Turn Left** | TL | L{hdg} | TL | L |
| **Turn Right** | TR | R{hdg} | TR | R |
| **Relative Left** | LT | T{deg}L | LT | T |
| **Relative Right** | RT | T{deg}R | RT | T |
| **Fly Present Heading** | FPH, FCH | H (no arg) | FPH, FCH | H |
| Turn (direction as arg) | T {deg} {dir} | — | — | — |
| Say Heading | — | SH | — | — |

## Altitude

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Climb/Maintain** | CM | C{alt}, TC{alt} | CM | C |
| **Descend/Maintain** | DM | D{alt}, TD{alt} | DM | D |
| Assign Altitude | — | A{alt} | — | — |
| Expedite | EXP | EC, ED | — | — |
| Normal Rate | NORM | — | — | — |
| Cross Fix at Altitude | CFIX, CF | C{fix}/A{alt} | — | — |
| Climb Via SID | CVIA | CVS | — | — |
| Descend Via STAR | DVIA | DVS | — | — |
| Say Altitude | — | SA | — | — |

## Speed

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Speed** | SPD, DS, IS, SLOW, SL, SPEED | S{kts}, TS{kts} | SPD, SLOW, SL, SPEED | S |
| Speed Min | — | SMIN | — | — |
| Speed Max | — | SMAX | — | — |
| Speed Present | — | SPRES | — | — |
| Speed Cancel | — | S (no arg) | — | — |
| Speed Floor | — | S{kts}+ | — | — |
| Speed Ceiling | — | S{kts}- | — | — |
| Mach | MACH, M | — | — | — |
| Resume Normal Speed | RNS, NS | — | — | — |
| Reduce Final Approach Speed | RFAS, FAS | — | — | — |
| Say Speed | — | SS | — | — |

> **Note:** ATCTrainer's `DS` (decrease speed) and `IS` (increase speed) are excluded from YAAT aliases — they imply directional speed changes, while our Speed command sets an absolute value.

## Transponder

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Squawk** | SQ, SQUAWK | SQ{code} | SQ, SQUAWK | SQ |
| **Squawk VFR** | SQV | — | 🟢 SQVFR, SQV | 🟢 SQVFR |
| **Squawk Normal** | SN | SQA, SQON | 🟢 SQNORM, SN | 🟢 SQNORM, SQA, SQON |
| **Squawk Standby** | SS | SQS | 🟢 SQSBY, SS | 🟢 SQSBY, SQS |
| **Ident** | ID, SQI, SQID | ID | 🟢 IDENT, ID, SQI, SQID | 🟢 IDENT, ID, SQI |
| **Random Squawk** | RANDSQ | — | RANDSQ | RANDSQ |
| **Squawk All** | SQALL | — | SQALL | SQALL |
| **Squawk Normal All** | SNALL | — | SNALL | SNALL |
| **Squawk Standby All** | SSALL | — | SSALL | SSALL |

> **Note:** `SQ` with no argument resets the aircraft to its flight-plan-assigned squawk code. `SQ 1234` sets only the actual transponder code (the assigned code in the flight plan remains unchanged). `RANDSQ` generates a random actual code without changing the assigned code. `SQALL` resets all aircraft to their assigned codes. `SNALL` and `SSALL` set all aircraft to mode C or standby. Ident auto-clears after 18 seconds.

## Navigation

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Direct To** | DCT | D{fix} | DCT | DCT |
| Depart Fix | DEPART, DEP, D | D{fix}/H{hdg} | — | — |
| Join STAR | JARR, ARR, STAR, JSTAR | — | — | — |
| Join Airway | JAWY | — | — | — |
| Join Final Approach | JFAC, JLOC, JF | — | — | — |
| Airport/Destination | APT, DEST | — | — | — |

> **Note:** VICE uses `D` for both Direct-to and Descend (resolved by fix name vs number). YAAT keeps `DCT` in the VICE preset to avoid ambiguity.

## Tower

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Line Up and Wait** | LUAW, POS, LU, PH | — | LUAW, POS, LU, PH | LUAW |
| **Cleared for Takeoff** | CTO | — | CTO | CTO |
| **Cancel Takeoff Clearance** | CTOC | — | CTOC | CTOC |
| Takeoff Left Traffic | CTOMLT | — | — | — |
| Takeoff Right Traffic | CTOMRT | — | — | — |
| Takeoff Present Position | CTOPP | — | — | — |
| Takeoff Runway Heading | CTORH | — | — | — |
| **Go Around** | GA | GA (VFR "go ahead") | GA | GA |
| **Go Around Make Left Traffic** | GAMLT | — | (via GA MLT) | (via GA MLT) |
| **Go Around Make Right Traffic** | GAMRT | — | (via GA MRT) | (via GA MRT) |
| 🟢 **Go Around (heading + alt)** | — | — | (via GA {hdg} {alt}) | (via GA {hdg} {alt}) |
| 🟢 **Cleared to Land** | — | — | CTL, FS | CTL, FS |
| 🟢 **Cancel Landing Clearance** | — | — | CLC, CTLC | CLC, CTLC |
| **Touch and Go** | TG | — | TG | TG |
| **Stop and Go** | SG | — | SG | SG |
| **Low Approach** | LA | — | LA | LA |
| 🟢 **Cleared for the Option** | — | — | COPT | COPT |
| Full Stop | FS | — | (alias of CTL) | (alias of CTL) |
| Land (heli at parking spot) | LAND | — | — | — |
| Land and Hold Short | LAHSO | — | — | — |
| Takeoff Roll | GO | — | — | — |
| **Exit Left** | EL | — | EL | EL |
| **Exit Right** | ER | — | ER | ER |
| **Exit (taxiway)** | EXIT | — | EXIT | EXIT |

> **Note:** ATCTrainer's `CTL` means "Clear Approach" (IFR approach clearance), NOT "Cleared to Land".
>
> ATCTrainer has no explicit "cleared to land" command — aircraft in the pattern are implicitly cleared to land. `LAND` in ATCTrainer is only for helicopters landing at a named parking spot on the destination ramp (ground ops, not tower clearance). `FS` (Full Stop) tells a pattern aircraft to make a full-stop landing on the current/next approach. YAAT uses `CTL` as its own "Cleared to Land" command (no ATCTrainer equivalent); `FS` is also supported as an alias.
>
> `LAND` will be added to YAAT in the ground ops milestone for helicopter parking spot landings.
>
> VICE has no tower commands; the VICE preset uses ATCTrainer verbs for tower operations.
>
> `GA` accepts optional arguments: `GA MRT` / `GA MLT` (go around and make right/left traffic), `GA 270 5000` (heading + altitude), `GA RH 2000` (runway heading + altitude). `GAMRT` and `GAMLT` are verb aliases parsed by the server. Auto go-around (no landing clearance by 0.5nm) broadcasts a warning and re-enters the pattern for VFR and pattern traffic; IFR non-pattern aircraft fly runway heading at 2000 AGL.

## Approach

Neither ATCTrainer nor VICE approach commands are implemented in YAAT yet.

| Command | ATCTrainer doc | VICE doc |
|---|---|---|
| Clear Approach | CAPP, CTL | C{appr}, C (no arg) |
| Clear Straight In | — | CSI{appr} |
| Clear at Fix | — | A{fix}/C{appr} |
| Expect Approach | — | E{appr} |
| Intercept Localizer | — | I, A{fix}/I |
| Cancel Approach | — | CAC |

## Pattern

VICE has no pattern commands. The VICE preset uses ATCTrainer verbs.

| Command | ATCTrainer doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|
| **Enter Left Downwind** | ELD | ELD [runway] | ELD [runway] |
| **Enter Right Downwind** | ERD | ERD [runway] | ERD [runway] |
| **Enter Left Base** | ELB | ELB [runway] [distance] | ELB [runway] [distance] |
| **Enter Right Base** | ERB | ERB [runway] [distance] | ERB [runway] [distance] |
| **Enter Final** | EF | EF [runway] | EF [runway] |
| Enter Left Crosswind | ELC | — | — |
| Enter Right Crosswind | ERC | — | — |
| **Make Left Traffic** | MLT | MLT | MLT |
| **Make Right Traffic** | MRT | MRT | MRT |
| **Turn Crosswind** | TC | TC | TC |
| **Turn Downwind** | TD | TD | TD |
| **Turn Base** | TB | TB | TB |
| **Extend Downwind** | EXT | EXT | EXT |
| Make 270 | M2, M270 | — | — |
| Make Left 360s | ML3, ML360 | — | — |
| Make Right 360s | MR3, MR360 | — | — |
| Make Left S-Turns | MLS, STURN, STURNS | — | — |
| Make Right S-Turns | MRS | — | — |
| Make Normal Approach | MNA | — | — |
| Make Short Approach | MSA | — | — |
| No 270 | NO270 | — | — |
| Pattern Size | PS, PATTSIZE, PSIZE, PATTS | — | — |

## Hold

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Hold Present Position (360 Left)** | HPPL | — | HPPL | HPPL |
| **Hold Present Position (360 Right)** | HPPR | — | HPPR | HPPR |
| **Hold Present Position (Hover)** | HPP | — | HPP | HPP |
| **Hold at Fix (Left)** | HFIXL | H{fix}/L | HFIXL | HFIXL |
| **Hold at Fix (Right)** | HFIXR | H{fix} (default right) | HFIXR | HFIXR |
| **Hold at Fix (Hover)** | HFIX | — | HFIX | HFIX |
| Hold (general) | HOLD | H{fix} (with /NM, /M, /R options) | — | — |

## Sim Control

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Delete** | DEL | X | DEL | X |
| **Pause** | PAUSE, P | P (toggle) | PAUSE, P | PAUSE |
| **Unpause** | UNPAUSE, U, UN, UNP, UP | P (toggle) | UNPAUSE, U, UN, UNP, UP | UNPAUSE |
| **Sim Rate** | SIMRATE | — | SIMRATE | SIMRATE |
| 🟢 **Spawn Now** | — | — | SPAWN | SPAWN |
| 🟢 **Set Spawn Delay** | — | — | DELAY | DELAY |
| **Wait (seconds)** | WAIT | — | WAIT | WAIT |
| 🟢 **Wait (distance)** | — | — | WAITD | WAITD |

> **Note:** VICE pause is a toggle (`P`); YAAT splits into separate Pause/Unpause. The VICE preset uses PAUSE/UNPAUSE (ATCTrainer verbs) since YAAT needs distinct commands.
>
> SPAWN and DELAY are YAAT-specific commands for controlling delayed aircraft spawns. SPAWN immediately spawns a delayed aircraft; DELAY sets a new spawn countdown in seconds (accepts M:SS format, e.g., `DELAY 2:00`).

## Track Operations

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Act As Position** | AS | — | AS | AS |
| **Track** | TRACK | — | TRACK | TRACK |
| **Drop Track** | DROP | — | DROP | DROP |
| **Handoff** | HO | — | HO | HO |
| **Accept Handoff** | ACCEPT, A | — | ACCEPT, A | ACCEPT |
| **Cancel Handoff** | CANCEL | — | CANCEL | CANCEL |
| **Accept All Handoffs** | ACCEPTALL | — | ACCEPTALL | ACCEPTALL |
| **Handoff All** | HOALL | — | HOALL | HOALL |
| **Point Out** | PO | — | PO | PO |
| **Acknowledge** | OK | — | OK | OK |
| **Annotate** | ANNOTATE, AN, BOX | — | ANNOTATE, AN, BOX | ANNOTATE |
| **Scratchpad** | SCRATCHPAD, SP | — | SCRATCHPAD, SP | SP |
| **Temporary Altitude** | TEMPALT, TA, TEMP, QQ | — | TEMPALT, TA, TEMP, QQ | TA |
| **Cruise** | CRUISE, QZ | — | CRUISE, QZ | CRUISE |
| **On Handoff** | ONHO, ONH | — | ONHO, ONH | ONHO |
| **Frequency Change** | — | FC | FC | FC |
| **Contact Position** | — | CT{tcp} | CT | CT |
| **Contact Tower** | — | TO | TO | TO |
| Strip | STRIP | — | — | — |

## Coordination

🟢 YAAT-specific commands for STARS departure release coordination (rundown lists). Neither ATCTrainer nor VICE has native coordination commands.

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| 🟢 **Release (Rundown)** | — | — | RD | RD |
| 🟢 **Hold Release** | — | — | RDH | RDH |
| 🟢 **Recall Release** | — | — | RDR | RDR |
| 🟢 **Acknowledge Release** | — | — | RDACK | RDACK |
| 🟢 **Toggle Auto-Ack** | — | — | RDAUTO | RDAUTO |

> **Note:** `RD`, `RDH`, `RDR`, and `RDACK` take an optional list ID argument (e.g., `RD PFAT`). When omitted, the server auto-detects the list from the sender/receiver TCP. `RDACK` works without a list ID even when the TCP belongs to multiple lists, as long as there is only one unacknowledged release across all lists. `RDAUTO` requires a list ID and is a global command (no callsign needed).

## VFR (not implemented)

| Command | ATCTrainer doc | VICE doc |
|---|---|---|
| Resume Own Navigation | — | RON |
| Radar Services Terminated | — | RST |
| Altitude Your Discretion | — | A (no arg, VFR context) |

## Ground

VICE has no ground commands. The VICE preset uses ATCTrainer verbs.

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Pushback** | PUSH | — | PUSH | PUSH |
| **Taxi** | TAXI, RWY | — | TAXI, RWY | TAXI |
| **Hold Position** | HOLD | — | HOLD, 🟢 HP | HOLD |
| **Resume Taxi** | RES | — | RES, 🟢 RESUME | RES |
| **Cross Runway** | CROSS | — | CROSS | CROSS |
| **Follow** | FOLLOW | — | FOLLOW, 🟢 FOL | FOLLOW |
| Taxi All | TAXIALL | — | — | — |
| Hold Short | HS | — | (via TAXI HS) | (via TAXI HS) |
| **Give Way** | GIVEWAY, GW, PB | — | GIVEWAY, 🟢 BEHIND | GIVEWAY, 🟢 BEHIND |
| Break | BREAK | — | — | — |

> **Note:** ATCTrainer's `HS` is a standalone command for hold-short. In YAAT, hold-short is specified as part of the `TAXI` command: `TAXI S T U HS 28L` (tokens after `HS` are explicit hold-short runways). Implicit hold-shorts are added at all runway crossings automatically.
>
> `CROSS` can be issued before reaching the hold-short point to pre-clear it, or while holding short to satisfy the clearance immediately.
>
> `HOLD` and `HP` stop the aircraft wherever it is on the ground. `RES` / `RESUME` resumes taxi movement.
>
> `GIVEWAY` / `BEHIND` is a condition prefix (like `AT`/`LV`), not a standalone command. It delays the next command until the named aircraft no longer conflicts. Example: `GIVEWAY SWA5456; TAXI S T U` waits for SWA5456 to pass before taxiing.

## Misc

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Add Aircraft** | ADD | — | ADD | ADD |
| **Say** | SAY | — | SAY | SAY |
| Flight Plan | FP | — | — | — |
| VFR Flight Plan | VP | — | — | — |
| Remarks | REMARKS | — | — | — |
| Say Frequency | SAYF | — | — | — |
| Cleared | CLRD | — | — | — |
| Delete At | DELAT | — | — | — |
| Open Chat | OPENCHAT | — | — | — |
| Operations/Stats | OPS, STATS, STAT | — | — | — |
| Show At | SHOWAT | — | — | — |
| Global Message | — | /{msg} | — | — |

## ATCTrainer Debug Commands (not implemented)

| Command | ATCTrainer doc | Description |
|---|---|---|
| APPS | APPS | Display available approaches |
| CMN, DMN | CMN | Instant altitude change |
| FHN | FHN | Instant heading change |
| SLN, ISN | SLN | Instant speed change |
| SHOWPATH | SHOWPATH | Display waypoints as aircraft |
| HIDEPATH | HIDEPATH | Remove displayed path |
