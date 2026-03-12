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

## Round 2 — Completed (was 438 failures, 67 unique patterns)

### TRACK with optional TCP arg (~300 failures)
- [x] `TRACK` accepts optional TCP code: `TrackAircraftCommand(TcpCode: "OAK_41_CTR")`
- [x] CommandRegistry changed from Bare to Cmd with optional position overload

### SP alias for SP1 (~40 failures)
- [x] Added `SP` alias to SP1 in CommandParser and CommandRegistry

### AT + track/scratchpad commands (~30 failures)
- [x] Fixed by TRACK/ACCEPT/SP changes — AT condition extraction + normal parse

### ACCEPT with optional callsign
- [x] `ACCEPT` accepts optional callsign: `AcceptHandoffCommand(Callsign: "JBU33")`
- [x] CommandRegistry changed from Bare to Cmd with optional callsign overload

### TG with optional runway (~30 failures)
- [x] `TG` accepts optional runway: `TouchAndGoCommand(RunwayId: "31")`
- [x] CommandRegistry changed from Bare to Cmd with optional runway overload

### SPD in ExpandMultiCommand (1 failure)
- [x] Added `SPD` to `HeadingAltVerbs` set

### HOLD without direction / without leg (3 failures)
- [x] Rewrote `ParseHold` for flexible 3-5 token forms
- [x] 3 tokens: fix course direction (default leg 1M) OR fix course leg (default Right)
- [x] 4 tokens: fix course leg direction (standard) OR fix course leg (default Right)
- [x] Default direction is Right per 7110.65

### WAIT with fix name (1 failure)
- [x] `WAIT FIXNAME ...` → `AT FIXNAME ...` rewrite in ExpandWaitBlock

### AT bare / no command (1 failure)
- [x] `AT BRIXX` → condition with empty command list
- [x] Fixed in both CommandParser.ParseAtCondition and CommandSchemeParser.ParseBlockToCanonical

### WAIT 150 HOLD (1 failure)
- [x] Fixed by HOLD 3-token flexibility + WAIT expansion

### Skip (not implementing)
- `WAI T6 DVIA` — typo
- `WAIT10 TRACK Q2B` — typo (missing space)
- `CFIXX` / `CFIX SCTRR AT 360` — typos in scenario **YW-DN |14_35 [16Z]**
- `SPAWNED AT OAR...` — not a command (text description)

### PO bare (1 failure from scenario test)
- [x] `PO` without TCP arg returns `PointOutCommand(TcpCode: null)` — resolves to student position at dispatch
- [x] CommandRegistry changed from single required-arg overload to bare + position overload

## Test plan
- [x] Unit tests for all completed patterns (`ZoaParseFixTests.cs`, 59 tests)
- [x] `VnasScenarioParseTests` runs against local snapshots — 0 new failures (6 known typos excluded)
- [x] Target: <10 remaining failures (typos and true unknowns only) — achieved: 6 known typos
- [x] Local scenario snapshots gitignored, test skips gracefully when absent
- [x] `tools/refresh-scenarios.py` for downloading ARTCC scenarios
- [x] `docs/scenario-validation.md` documenting the workflow
