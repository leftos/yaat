# Scenario Validation — Known Failures

Commands that fail parsing but are **not parser bugs** — genuine scenario typos, unsupported ATCTrainer features we won't implement, fixes or procedures missing from the current navdata, or free-text notes in command fields. These should remain as failures in validation reports.

Last full run: 2026-09-26 — 1964 scenarios, 79890 presets, 284 failures (99.6% parse rate). 210 of the 284 are listed here; the other 74 are suspected parser gaps and are not catalogued.

"Not in navdata" means the name resolves neither as a fix in the current vNAS NavData nor as an FRD; the fix was renamed, decommissioned or never published. "Route element in DCT" means a STAR, a STAR.runway or an airway inside a `DCT` fix list; `DCT` takes fixes only (use `JARR` / `JAWY`).

## ZAB (10 failures)

- `AT SCOLE: HOLDING AT FL250, ...` (×8) — instructor notes in command field (colon after fix)
- `AT HOGGZ: CROSSING 'HOGGZ' AT 290, ...` (×1) — instructor note
- `WAIT 30 45 210` — ambiguous/malformed preset

## ZAN — PASS

## ZAU (13 failures)

- `AT GOMPE 10 DROP` — bare number after AT fix (missing verb)
- `AT Z SAY ` — SAY with no text
- `DELAY 20 DCT CMSKY CARYN CYBIL PXV GUMMA` — `GUMMA` not in navdata
- `DELAY 60 DCT MOBLE ADIME GERBS J146 MIP` / `DELAY 60 DCT SQUIB HAUCK HRRSH HOCKE Q935 PONCT` / `WAIT 30 DCT OLINN OREOS OBENE OGALE LNK J60 HCT` — route element in DCT (airway)
- `WAIT 60 DCT <fixes> FYTTE7` (×5) — route element in DCT (STAR)
- `WAIT 75 DCT PEKUE OBENE MONNY MNOSO BLUEM4` / `WAIT 60 DCT DOKTR RST ALO IRK LORLE3` — route element in DCT (STAR)

## ZBW (3 failures)

- `AT 2000CM 080` — typo (missing space, should be `AT 2000 CM 080`)
- `AT 1000 CM 2600 DCT RAKET ORW` — `RAKET` not in navdata (the reported error blames the CM altitude)
- `AT 1000 DCT SEGOE HYLND WAYGO` — `SEGOE` not in navdata

## ZDC (4 failures)

- `ONHO WAIT 20 CM` — CM missing altitude argument
- `ONHO \`AT FEBEL DEL` — backtick in command (typo)
- `AT 3500 DCT W17` — `W17` not in navdata
- `ONHO AT GIBBZ DCT KUKSE MATTC CUTZZ` — `MATTC` not in navdata (removed since the previous cycle)

## ZDV (3 failures)

- `CFIX VCTRE 23000` / `CFIX ONNNN 23000` / `CFIX WRIPS 10000` — fix not in navdata

## ZFW — PASS

## ZHN — PASS

## ZHU (2 failures)

- `WAIT 60 REQ LOWER UPON CHEK-ON THE FREQ` — free-text instructor note
- `WAIT 45 REQ DEPARTURE FROM @HPD TO MED CTR` — free-text instructor note

## ZID (62 failures)

- `WAIT 120 TAXI` (×40) — bare TAXI with no route arguments
- `AT DM 024` (×4) — typo (AT consumes `DM` as fix name; missing fix)
- `AT BYRAN DCT ZEVVE` (×4) — `ZEVVE` not in navdata
- `CFIX APCOT 210` (×3) / `CFIX WARSA 140` (×2) — fix not in navdata
- `WAIT 100 DCT OOM WEGEE PXV J131 LIT` — route element in DCT (airway)
- `WAIT 120 DCT DAWNN IIU SKYWA FILPZ4` / `WAIT 120 DCT OOM WEGEE PXV BLUZZ4` — route element in DCT (STAR)
- `AT IFIGO DM 2100 FH 1000` — `FH 1000` invalid heading (typo)
- `AT WAIT 120 DM 3000` — typo (AT consumes `WAIT` as fix name)
- `WAIT 10 ANNOTATE P` — ANNOTATE missing box number
- `WAIT 120 DHL2 N S HS S` — unknown command
- `WAIT 15 STIRP GCW` / `WAIT 60 STIRP LC` — typo (`STIRP` instead of `STRIP`)

## ZJX (19 failures)

- `CIFX HOTAR 110` / `XFIX PLZZZ 11000` — typo (`CFIX`)
- `CFIX WIGET 80` — `WIGET` not in navdata
- `AT DUCEN 200 ERB 6` — unknown command pattern
- `WAIT <n> QS M81+` / `QS M80` / `QS M79-` (×3) — unknown command `QS` (ERAM assigned-speed entry, not an ATCTrainer verb)
- `WAIT 1 DELETE` (×2) — unknown verb (`DEL` is the delete command)
- `REQUEST RELEASE RUNWAY 14 CRG FF SAWGY` — free-text instructor note
- `REQUEST VFR DEPARTURE TO THE <dir> ...` / `REQUEST VFR TO REMAIN IN THE PATTERN ...` (×9) — free-text pilot requests (should be `SAY`)

## ZKC (7 failures)

- `CFIX PEGGI 12000` (×7) — `PEGGI` not in navdata

## ZLA (2 failures)

- `CFIX SLA 7000 210` — `SLA` is not a fix (Seal Beach VOR is `SLI`)
- `AT NNAVY DCT MISEN RNDRZ3.26L` — route element in DCT (STAR.runway)

## ZLC (3 failures)

- `UDUZU SPD 210` — missing verb prefix (probably `DCT UDUZU, SPD 210`)
- `CFIX BEKKHO` — CFIX missing altitude argument
- `WAIT 90 360` — bare number, not a command

## ZMA (6 failures)

- `SQ 6894` / `SQ 3871` — invalid squawk codes (digits 8/9 not valid)
- `TAXU T7 Q B 28R` (×2) — typo (`TAXU` instead of `TAXI`)
- `CFIX GUUR 12000` — `GUUR` not in navdata
- `DCT HEVVN MAATY5` — route element in DCT (STAR)

## ZME — PASS

## ZMP (7 failures)

- `CALL FOR RELEASE OFF TVC` — free-text instructor note
- `CALL AS <position> FOR RELEASE, DEPARTING RWY <id>` (×3) — free-text instructor notes
- `CFIX TVCDS 24000` — `TVCDS` not in navdata
- `AT 2500 DCT FOMRE FRRAN KLVN` — `FOMRE` not in navdata
- `WAIT 30 DCT TTAIL BAINY3.30R` — route element in DCT (STAR.runway)

## ZNY (14 failures)

- `AT CMK HDG 140` (×8) — unknown verb `HDG` (should be `FH 140`)
- `CFIX LOLLY 20000` (×5) — `LOLLY` not in navdata
- `WAIT 240 CLEARANCE AND RELEASE @ FOK` — free-text instructor note

## ZOA (18 failures)

- `PUSH 421` (×5) — SFO taxiway is now `T421`; YAAT deliberately rejects numeric PUSH arguments
- `DCT GRAIS` (×2) / `HOLD GRAIS 200 1 RIGHT` (×2) — `GRAIS` not in navdata
- `WAIT 2 DCT HEFSE` (×2) / `WAIT 3 HOLD HEFSE 360 2 RIGHT` — `HEFSE` not in navdata
- `WAIT 80 DCT WUSVU` / `WAIT 85 HOLD WUSVU 140 1 LEFT` — `WUSVU` not in navdata
- `DCT FOLSI` / `AT FOLSI DCT RETBE` — `FOLSI` / `RETBE` not in navdata
- `AT UZIZY DCT UWFIR` — `UWFIR` not in navdata
- `AT ZINNN HOLD DYKEE 360 2 RIGHT` — `DYKEE` not in navdata

## ZOB (15 failures)

- `CFIX ZEPDO 3000` (×10) — `ZEPDO` not in navdata
- `HANBL219015` — bare FRD with no verb (garbled)
- `AT KLYNK RNS\`` — typo (backtick, and `RNS` standalone isn't a valid command after AT condition)
- `HAYLL VCTRZ2.22R` — garbled concatenation
- `AT OFKTD DCR PATRC` — typo (`DCR` instead of `DCT`)
- `AT CUUGR DCT ROOD` — `ROOD` not in navdata

## ZSE (7 failures)

- `WAIT <n> CONSIDER CALLING FOR IFR OUT OF VUO` (×3) — free-text instructor notes
- `WAIT 1 60 SAY ON THE GROUND VUO, REQ IFR TO SPB` — malformed WAIT + free-text
- `DCT LMT HAWKZ8` / `DCT LKV HAWKZ8` / `DCT PDT CHINS5` — route element in DCT (STAR)

## ZSU (1 failure)

- `CFIX ILALO 5500` — `ILALO` not in navdata

## ZTL (14 failures)

- `CFIX WHINZ 12000 [250]` / `CFIX WHINZ 14000` (×6) — `WHINZ` not in navdata (removed since the previous cycle)
- `CFIX CODKA 14000` / `CFIX SABIN 5500` / `CFIX FOBES 11000` — fix not in navdata
- `AT 2000 DCT TUFTY APELE BUYAB ...` / `AT 2000 DCT MOATE DOBCE ...` — `BUYAB` / `MOATE` not in navdata
- `AT 3000 DCT POORE LILIC KILNS` / `AT 3000 DCT JRUTT` / `WAIT 140 DCT HARAY` — `LILIC` / `JRUTT` / `HARAY` not in navdata
