# ZOA Scenario Parse Fixes

Follow-up from #57. The ZOA scenario parse completeness test (`VnasScenarioParseTests`) revealed 869 unique unparseable preset commands. These are ATCTrainer patterns not yet supported in `CommandParser`.

## Agreed fixes

### WAIT as compound condition (692 failures — 80%)
- [ ] Add `WAIT N` as a condition type in `CommandParser.ParseCompound`
- [ ] `WAIT N <command>` expands to `WAIT N; <command>` (existing WAIT + inner command in next block)
- [ ] Handle nested `WAIT N AT <fix> <command>` and `AT <fix> WAIT N <command>`
- [ ] Handle nested `WAIT N WAIT N <command>` (double wait)
- [ ] Add `DELAY` as alias for `WAIT`

### SAY with commas (30 failures)
- [ ] When SAY is the verb, consume the entire remainder as literal text (don't split on `,`)
- [ ] Apply same logic in both `CommandParser` and `CommandSchemeParser`

### CF as CFIX alias (52 failures)
- [ ] Add `CF` as verb alias for `CFIX` in CommandParser
- [ ] Altitude parsing goes through the existing altitude resolver (supports both <=3 and >3 digit formats)

### HOLD direction aliases (11 failures)
- [ ] Add `LEFT`/`RIGHT` as aliases for `L`/`R` in `ApproachCommandParser.ParseHold` direction parsing

### Bare CAPP auto-resolution (116+ failures)
- [ ] Allow `CAPP` with no approach ID argument
- [ ] Auto-resolve approach from: AT-condition fix context (which approaches contain that fix), destination/runway assignment
- [ ] Requires access to approach database during command parsing or dispatch

### Bare JFAC auto-resolution
- [ ] Allow `JFAC` with no approach ID argument
- [ ] Same auto-resolution logic as bare CAPP

### Auto-split at verb boundaries
- [ ] When a block has extra tokens after the initial command, scan for known verb boundaries
- [ ] Split into parallel commands joined by `,` (not `;`)
- [ ] Example: `AT PIECH SPD 210 DM 040` → `AT PIECH SPD 210, DM 040`
- [ ] Example: `FH 170 CM 2000` → `FH 170, CM 2000`

### Track commands in AT/WAIT conditions (8+ failures)
- [ ] Support TRACK, ACCEPT, HO, PO inside AT/WAIT conditional blocks
- [ ] These bypass CommandDispatcher → route through TrackCommandHandler
- [ ] Need special handling in preset command dispatch path

### Minor aliases
- [ ] `SN` → squawk mode C (SQNORM)
- [ ] `POS` → LUAW (line up and wait)
- [ ] `GW` → GIVEWAY alias
- [ ] `SQUAWK` → `SQ` alias
- [ ] `SCRATCHPAD` → `SP1` alias
- [ ] `SQV` → squawk VFR (1200)
- [ ] `SLN` → instantaneous speed (alias for SPDN)
- [ ] `APREQ` → parse as SAY equivalent
- [ ] `CTOMLT`/`CTOMRT` → add to CommandParser (already in CommandSchemeParser)

### TAXI $ gate references
- [ ] Alias `$` to `@` in TAXI route parsing (e.g., `$10` → `@10`)

### Warning on unparseable presets
- [ ] Broadcast a warning message when a preset command fails to parse, so instructors are alerted

### Skip (not implementing)
- `WAI T6 DVIA` — typo
- `WAIT10 TRACK Q2B` — typo (missing space)
- `CFIXX` / `CFIX SCTRR AT 360` — typos in scenario **YW-DN |14_35 [16Z]**
- `SPAWNED AT OAR...` — not a command (text description)

## Test plan
- [ ] Re-run `VnasScenarioParseTests` after all fixes
- [ ] Target: <10 remaining failures (typos and true unknowns only)
- [ ] Unit tests for each new pattern
