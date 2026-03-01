# Command Aliases Reference

Sources:
- **ATCTrainer**: https://atctrainer.collinkoldoff.dev/docs/commands
- **VICE**: https://pharr.org/vice/

**Bold** = implemented in YAAT. Normal = not yet implemented.
YAAT aliases = what's actually in the code presets (primary alias listed first).

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
| Mach | MACH, M | — | — | — |
| Resume Normal Speed | RNS, NS | — | — | — |
| Reduce Final Approach Speed | RFAS, FAS | — | — | — |
| Say Speed | — | SS | — | — |

> **Note:** ATCTrainer's `DS` (decrease speed) and `IS` (increase speed) are excluded from YAAT aliases — they imply directional speed changes, while our Speed command sets an absolute value.

## Transponder

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Squawk** | SQ, SQUAWK | SQ{code} | SQ, SQUAWK | SQ |
| **Squawk Ident** | SQI, SQID | — | SQI, SQID | SQI |
| **Squawk VFR** | SQV | — | SQVFR, SQV | SQVFR |
| **Squawk Normal** | SN | SQA, SQON | SQNORM, SN | SQNORM, SQA, SQON |
| **Squawk Standby** | SS | SQS | SQSBY, SS | SQSBY, SQS |
| **Ident** | ID | ID | IDENT, ID | IDENT, ID |
| Random Squawk | RANDSQ | — | — | — |
| Squawk All | SQALL | — | — | — |
| Squawk Normal All | SNALL | — | — | — |
| Squawk Standby All | SSALL | — | — | — |

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
| **Cleared to Land** | — | — | CTL, FS | CTL, FS |
| **Cancel Landing Clearance** | — | — | CLC, CTLC | CLC, CTLC |
| **Touch and Go** | TG | — | TG | TG |
| **Stop and Go** | SG | — | SG | SG |
| **Low Approach** | LA | — | LA | LA |
| **Cleared for the Option** | — | — | COPT | COPT |
| Full Stop | FS | — | (alias of CTL) | (alias of CTL) |
| Land (heli at parking spot) | LAND | — | — | — |
| Land and Hold Short | LAHSO | — | — | — |
| Takeoff Roll | GO | — | — | — |
| Exit Left | EL | — | — | — |
| Exit Right | ER | — | — | — |
| Exit (taxiway) | EXIT | — | — | — |

> **Note:** ATCTrainer's `CTL` means "Clear Approach" (IFR approach clearance), NOT "Cleared to Land".
>
> ATCTrainer has no explicit "cleared to land" command — aircraft in the pattern are implicitly cleared to land. `LAND` in ATCTrainer is only for helicopters landing at a named parking spot on the destination ramp (ground ops, not tower clearance). `FS` (Full Stop) tells a pattern aircraft to make a full-stop landing on the current/next approach. YAAT uses `CTL` as its own "Cleared to Land" command (no ATCTrainer equivalent); `FS` should also be supported as an alternative (not yet implemented).
>
> `LAND` will be added to YAAT in the ground ops milestone for helicopter parking spot landings.
>
> ClearedForOption (`COPT`) is YAAT-specific; neither ATCTrainer nor VICE has this command.
>
> VICE has no tower commands; the VICE preset uses ATCTrainer verbs for tower operations.

## Approach

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
| **Spawn Now** | — | — | SPAWN | SPAWN |
| **Set Spawn Delay** | — | — | DELAY | DELAY |
| **Wait (seconds)** | WAIT | — | WAIT | WAIT |
| **Wait (distance)** | — | — | WAITD | WAITD |

> **Note:** VICE pause is a toggle (`P`); YAAT splits into separate Pause/Unpause. The VICE preset uses PAUSE/UNPAUSE (ATCTrainer verbs) since YAAT needs distinct commands.
>
> SPAWN and DELAY are YAAT-specific commands for controlling delayed aircraft spawns. SPAWN immediately spawns a delayed aircraft; DELAY sets a new spawn countdown in seconds (accepts M:SS format, e.g., `DELAY 2:00`).

## ATC / Handoff (not implemented)

| Command | ATCTrainer doc | VICE doc |
|---|---|---|
| Accept | ACCEPT, A | — |
| Accept All | ACCEPTALL | — |
| Handoff | HO | — |
| Handoff All | HOALL | — |
| Cancel Handoff | CANCEL | — |
| Point Out | PO | — |
| Drop | DROP | — |
| Track | TRACK | — |
| OK | OK | — |
| Annotate | ANNOTATE, AN, BOX | — |
| Scratchpad | SCRATCHPAD, SP | — |
| Temporary Altitude | TEMPALT, TA, TEMP, QQ | — |
| Cruise | CRUISE, QZ | — |
| Strip | STRIP | — |
| On Handoff | ONHO, ONH | — |
| Frequency Change | — | FC |
| Contact TCP | — | CT{tcp} |
| Contact Tower | — | TO |

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
| **Hold Position** | HOLD | — | HOLD, HP | HOLD |
| **Resume Taxi** | RES | — | RES, RESUME | RES |
| **Cross Runway** | CROSS | — | CROSS | CROSS |
| **Follow** | FOLLOW | — | FOLLOW, FOL | FOLLOW |
| Taxi All | TAXIALL | — | — | — |
| Hold Short | HS | — | (via TAXI HS) | (via TAXI HS) |
| **Give Way** | GIVEWAY, GW, PB | — | GIVEWAY, BEHIND | GIVEWAY, BEHIND |
| Break | BREAK | — | — | — |

> **Note:** ATCTrainer's `HS` is a standalone command for hold-short. In YAAT, hold-short is specified as part of the `TAXI` command: `TAXI S T U HS 28L` (tokens after `HS` are explicit hold-short runways). Implicit hold-shorts are added at all runway crossings automatically.
>
> `CROSS` can be issued before reaching the hold-short point to pre-clear it, or while holding short to satisfy the clearance immediately.
>
> `HOLD` and `HP` stop the aircraft wherever it is on the ground. `RES` / `RESUME` resumes taxi movement.
>
> `GIVEWAY` / `BEHIND` is a condition prefix (like `AT` / `LV`), not a standalone command. It delays the next command until the named aircraft no longer conflicts. Example: `GIVEWAY SWA5456; TAXI S T U` waits for SWA5456 to pass before taxiing.

## Misc

| Command | ATCTrainer doc | VICE doc | YAAT ATCTrainer preset | YAAT VICE preset |
|---|---|---|---|---|
| **Add Aircraft** | ADD | — | ADD | ADD |
| Flight Plan | FP | — |
| VFR Flight Plan | VP | — |
| Remarks | REMARKS | — |
| Say | SAY | — |
| Say Frequency | SAYF | — |
| Cleared | CLRD | — |
| Delete At | DELAT | — |
| Wait | WAIT | — |
| Open Chat | OPENCHAT | — |
| Operations/Stats | OPS, STATS, STAT | — |
| Show At | SHOWAT | — |
| Global Message | — | /{msg} |
