# Scenario Validation — Known Failures

Commands that fail parsing but are **not parser bugs** — genuine scenario typos, unsupported ATCTrainer features we won't implement, or free-text notes in command fields. These should remain as failures in validation reports.

Last full run: 2026-03-12 — 1760 scenarios, 68722 presets, 231 failures (99.7% parse rate).

## ZAB (10 failures)

- `AT SCOLE: HOLDING AT FL250, ...` (×8) — instructor notes in command field (colon after fix)
- `AT HOGGZ: CROSSING 'HOGGZ' AT 290, ...` (×1) — instructor note
- `WAIT 30 45 210` — ambiguous/malformed preset

## ZAU (61 failures)

- `AT BHAWK QQ P110` (×58) — TEMPALT with invalid P prefix (should be `QQ 110`)
- `AT BOWNN DELAY 2 QQ P110` — same P-prefix typo
- `AT GOMPE 10 DROP` — bare number after AT fix (missing verb)
- `AT Z SAY ` — SAY with no text

## ZBW (1 failure)

- `AT 2000CM 080` — typo (missing space, should be `AT 2000 CM 080`)

## ZDC (5 failures)

- ` AIRCRAFT CURRENTLY SPAWNED...` — free-text instructor note in command field
- `ONHO WAIT 20 CM` — CM missing altitude argument
- `ONHO \`AT FEBEL DEL` — backtick in command (typo)
- `ONHO 1AT SCOOB DEL` — typo (`1AT` instead of `AT`)
- `WIAT 10 HO PUB_APP` — typo (`WIAT` instead of `WAIT`)

## ZDV (67 failures)

- `CFIX  <altitude>` (×56) — double space, missing fix name
- `AT <fix> QQ P<alt>` (×10) — TEMPALT with invalid P prefix
- `DVIA TBARR3` — STAR name not in navdata

## ZFW — PASS

## ZHU (2 failures)

- `WAIT 60 REQ LOWER UPON CHEK-ON THE FREQ` — free-text instructor note
- `WAIT 45 REQ DEPARTURE FROM @HPD TO MED CTR` — free-text instructor note

## ZID (54 failures)

- `WAIT 120 TAXI` (×40) — bare TAXI with no route arguments
- `AT DM 024` (×4) — typo (AT consumes `DM` as fix name; missing fix)
- `AT IFIGO DM 2100 FH 1000` — `FH 1000` invalid heading (typo)
- `AT WAIT 120 DM 3000` — typo (AT consumes `WAIT` as fix name)
- `WAIT 10 ANNOTATE P` (×2) — ANNOTATE missing box number
- `WAIT 120 DHL2 N S HS S` (×2) — unknown command
- `WAIT 15 STIRP GCW` (×3) / `WAIT 60 STIRP LC` — unknown command (STIRP)

## ZJX (5 failures)

- `SQ1200` — typo (missing space, should be `SQ 1200` or use `SQV`)
- `AT DUCEN 200 ERB 6` — unknown command pattern
- `WAIT N QS /M8x` (×3) — unknown command `QS` (not an ATCTrainer verb)

## ZKC — PASS

## ZLA (1 failure)

- `AT SHADIHO 31` — incomplete command (bare number, probably missing verb like CM)

## ZLC (3 failures)

- `UDUZU SPD 210` — missing verb prefix (probably `DCT UDUZU, SPD 210`)
- `CFIX BEKKHO` — CFIX missing altitude argument
- `WAIT 90 360` — bare number, not a command

## ZMA (4 failures)

- `SQ 6894` / `SQ 3871` — invalid squawk codes (digits 8/9 not valid)
- `TAXU T7 Q B 28R` (×2) — typo (`TAXU` instead of `TAXI`)

## ZME (4 failures)

- `AT <fix> DELAY <n> QQ P<alt>` (×4) — TEMPALT with invalid P prefix (should be `QQ <alt>`)

## ZMP (4 failures)

- `CALL FOR RELEASE OFF TVC` — free-text instructor note
- `CALL AS <position> FOR RELEASE, DEPARTING RWY <id>` (×3) — free-text instructor notes

## ZNY (1 failure)

- `WAIT 240 CLEARANCE AND RELEASE @ FOK` — free-text instructor note

## ZOA (1 failure)

- `APREQ RWY 33 DEPARTURE` — free-text (approval request note)

## ZOB (3 failures)

- `HANBL219015` — garbled/concatenated text
- `AT KLYNK RNS\`` — typo (backtick, and `RNS` standalone isn't a valid command after AT condition)
- `HAYLL VCTRZ2.22R` — garbled concatenation

## ZSE (5 failures)

- `CFIX  7000` — double space, missing fix name
- `WAIT <n> CONSIDER CALLING FOR IFR OUT OF VUO` (×3) — free-text instructor notes
- `WAIT 1 60 SAY ON THE GROUND VUO, REQ IFR TO SPB` — malformed WAIT + free-text

## ZTL — PASS
