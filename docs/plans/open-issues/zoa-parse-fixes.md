# ZOA Scenario Parse Fixes

Follow-up from #57. The ZOA scenario parse completeness test (`VnasScenarioParseTests`) revealed 869 unique unparseable preset commands. These are ATCTrainer patterns not yet supported in `CommandParser`.

Down to 438 failures (67 unique patterns) after the first round of fixes.

## Completed

### ExpandWait preprocessing (was 692 failures)
- [x] `WAIT N <command>` expands to `WAIT N; <command>` via `ExpandWait`
- [x] Handle nested `WAIT N WAIT N <command>` (double wait)
- [x] Add `DELAY` as alias for `WAIT` (registry + both parsers)
- [x] `SPAWNDELAY` is now a separate verb (was `DELAY`)

### SAY with commas (was 30 failures)
- [x] When SAY/APREQ is the verb, consume entire remainder as literal text (don't split on `,`)
- [x] Applied in both `CommandParser` and `CommandSchemeParser`

### CF as CFIX alias (was 52 failures)
- [x] Added `CF` alias in CommandRegistry and CommandParser

### HOLD direction aliases (was 11 failures)
- [x] `LEFT`/`RIGHT` accepted alongside `L`/`R` in `ApproachCommandParser.ParseHold`

### Bare CAPP/JFAC auto-resolution (was 116+ failures)
- [x] `CAPP` and `JFAC` with no approach ID return null ApproachId
- [x] Auto-resolve at dispatch from `DestinationRunway`/`DepartureRunway`
- [x] Bare overloads added to CommandRegistry

### Heading+altitude multi-command expansion
- [x] `ExpandMultiCommand`: splits `FH 270 CM 5000` → `FH 270, CM 5000`
- [x] Limited to heading/altitude verbs: FH, TL, TR, CM, DM
- [x] Applied in `ParseCompound` and `ParseBlock` (both parsers)

### Minor aliases
- [x] `SN`/`SQA`/`SQON` → SQNORM
- [x] `POS`/`LU`/`PH` → LUAW
- [x] `GW` → GIVEWAY (also as compound condition prefix)
- [x] `SQUAWK` → SQ
- [x] `SCRATCHPAD` → SP1
- [x] `SQV` → SQVFR
- [x] `SQS` → SQSBY
- [x] `SLN` → SPDN (force speed)
- [x] `ID` → IDENT
- [x] `APREQ` → SAY (prefixes "APREQ" to text)
- [x] `CTOMLT`/`CTOMRT` → CTO with MLT/MRT prefix

### TAXI $ gate references
- [x] `$` aliased to `@` in TAXI/PUSHBACK/TAXIALL parsing

### Warning on unparseable presets
- [x] Logging in Yaat.Sim SimulationEngine
- [x] Broadcast to clients in yaat-server TickProcessor (both immediate and timed presets)
- [x] Upgraded TickProcessor from `Parse`/`Dispatch` to `ParseCompound`/`DispatchCompound`

## Remaining (438 failures, 67 unique patterns)

### WAIT N TRACK tcp (~300 failures — largest category)
- [ ] `WAIT 5 TRACK OAK_41_CTR` → ExpandWait splits to `WAIT 5; TRACK OAK_41_CTR`
- [ ] But `TRACK OAK_41_CTR` fails: `Parse` treats TRACK as zero-arg (`"TRACK" when arg is null`)
- [ ] TRACK with a TCP arg means "initiate track and assign to position" — server-side concept
- [ ] Need `TRACK` to accept optional TCP arg in CommandParser

### WAIT N SP val (~40 failures)
- [ ] `WAIT 15 SP OA1` — SP is shorthand for SP1 (scratchpad)
- [ ] `SP` is not in the parser switch; only `SP1` and `SP2` are
- [ ] Add `SP` as alias for `SP1`

### AT fix TRACK/ACCEPT/PO/SP (~30 failures)
- [ ] `AT BESSA PO`, `AT OAK TRACK OAK_41_CTR`, `AT INYOE ACCEPT JBU33`
- [ ] Track/coordination commands inside AT conditional blocks
- [ ] These bypass CommandDispatcher at runtime — need special handling in preset dispatch
- [ ] `AT ARCHI SP +RGT`, `AT EDDYY SP +LFT` — scratchpad in AT condition

### WAIT N TG rwy (~30 failures)
- [ ] `WAIT 10 TG 31` — Touch-and-go with runway number
- [ ] `TG` is currently zero-arg (`"TG" when arg is null`)
- [ ] Need TG to accept optional runway arg

### AT fix (bare, no command) (1 failure)
- [ ] `AT BRIXX` — condition with no following command
- [ ] Unclear what this means in ATCTrainer; may be a scenario error

### SPD + altitude multi-command (1 failure)
- [ ] `AT PIECH SPD 210 DM 040` — needs SPD in ExpandMultiCommand
- [ ] Currently only FH/TL/TR/CM/DM are in the expansion set

### HOLD without direction (2 failures)
- [ ] `HOLD VPBCK 080 10` — 3-token hold (fix, course, leg) with no direction
- [ ] Parser requires 4 tokens (fix, course, leg, direction)
- [ ] Could default to right turns per standard holding

### WAIT with fix name (1 failure)
- [ ] `WAIT OAK WAIT 100 DM 5000` — fix-based WAIT (not numeric)
- [ ] Currently WAIT only accepts numeric seconds

### WAIT 150 HOLD (1 failure)
- [ ] `WAIT 150 HOLD RBL 341 RIGHT` — HOLD after WAIT in compound
- [ ] ExpandWait should handle this but HOLD with fix+course+leg+dir might need `HOLDP` verb

### Skip (not implementing)
- `WAI T6 DVIA` — typo
- `WAIT10 TRACK Q2B` — typo (missing space)
- `CFIXX` / `CFIX SCTRR AT 360` — typos in scenario **YW-DN |14_35 [16Z]**
- `SPAWNED AT OAR...` — not a command (text description)

## Test plan
- [x] Unit tests for all completed patterns (`ZoaParseFixTests.cs`, 41 tests)
- [ ] Re-run `VnasScenarioParseTests` after remaining fixes
- [ ] Target: <10 remaining failures (typos and true unknowns only)
