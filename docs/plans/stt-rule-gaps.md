# STT tuning list

Work found by the controller-voice ouroboros (`--atc-ouroboros`; see "Tuning loop" in `docs/speech-recognition-pipeline.md`). Method, full results and limits: [`../research/2026-09-28-atc-ouroboros-baseline.md`](../research/2026-09-28-atc-ouroboros-baseline.md).

**Baseline** (`tools/Yaat.SpeechSandbox/Corpus/atc-ouroboros-baseline.json`):
- Run: seed 20260928, 200 cases × 3 trials. STT was the Whisper-medium ATC fine-tune (`borisdiakur/whisper-finetuned-for-ATC-ggml`); the LLM was `gemma4:e4b`.
- **Totals: 153 PASS, 0 FLAKY, 47 FAIL (76.5 %), mean WER 19.9 %, 7 gaps.** Zero flaky cases across three trials, so a fixed seed gives a stable diff.

| Family | Cases | Pass rate |
|---|---|---|
| TrafficAdvisoryRules | 7 | 29 % |
| HoldRules | 6 | 33 % |
| PtacRules | 6 | 50 % |
| HelicopterRules | 5 | 60 % |
| BroadcastRules | 8 | 62 % |
| PatternRules | 15 | 67 % |
| GroundRules | 33 | 73 % |
| NavigationRules | 16 | 75 % |
| TransponderRules | 9 | 78 % |
| AltitudeSpeedRules | 27 | 81 % |
| ApproachRules | 17 | 82 % |
| Compound | 15 | 93 % |
| TowerRules | 25 | 96 % |
| HeadingRules | 11 | 100 % |

How to work an item:
- Use `test-fix`, starting with a failing `PhraseologyMapperTests` row (text in, canonical out).
- Any phraseology change gets an `aviation-sim-expert` review.
- Rerun `--atc-ouroboros` afterwards, and `--update-baseline` when a family goes up.
- Shared files: `src/Yaat.Sim/Speech/PhraseologyRules.cs`, `PhraseologyMapper.cs`, the digit normaliser, and the LLM prompt in `LocalLlmCommandMapper`.

## Gaps: templates the rule mapper can't map at all

`tests/Yaat.Client.Tests/AtcOuroborosTests.cs` `KnownGaps` names these seven. A fix fails that test until its entry is removed.

- [ ] **`clbrv` (wrong command)**: "cleared into bravo airspace" maps to `CMTR B`. The NATO-letter collapse turns "bravo" into `B` before the Class B clearance literal can match.
- [ ] **`followg` (wrong command)**: "follow {callsign} on ground" maps to `FOLLOW <first word>` (e.g. `FOLLOW american`). The `{callsign}` slot captures one token.
- [ ] **`follow`, `giveway`**: a multi-word telephony after "follow" / "give way to" isn't captured. This has the same cause as `followg`, so one fix covers all three.
- [ ] **`l360`, `r270`**: "make left three sixty" / "make right two seventy" can never match, because digit normalisation rewrites them to `360` / `270` before the literals run.
- [ ] **`cva`**: "cleared visual approach runway {rwy}" maps to nothing under the ouroboros's full scenario context, although `PhraseologyMapperTests` maps it to `CVA` with an empty context. Start by bisecting the context fields.

## Baseline failures, clustered by cause

Case names are from the baseline run (`synth-20260928-NNN-<template>`). "STT" means Whisper produced the wrong words. "Rule" means the words were right, or close enough, and the mapper got them wrong.

- [ ] **Spoken aircraft type dropped from traffic advisories (Rule).** In `rtis-nr`, `rtis-left`, `rtis-dw` and `rtis-final`, "…Boeing/Cirrus/Cessna" comes out as a trailing `of` / `to`. That is the whole TrafficAdvisory shortfall.
- [ ] **Taxi to a gate or parking spot (Rule).** `taxi-gate`, `taxi-parking`, `taxi-parking-hs` and `taxi-parking-cross` leave "to gate G alfa five" / "to parking K alfa India three" verbatim instead of `@GA5` / `@KAI3`. NATO letters inside a spot name aren't collapsed, and the destination isn't resolved against the scenario's parking names. `taxi-parking-cross` drops the taxi clause entirely.
- [ ] **Hold direction lost (Rule).** `hfixl` and `hfixr` map to `HFIX`: "left turns" / "right turns" is dropped.
- [ ] **Programmed fixes misheard (STT).** ALTAM became "ultima" / "ultramarve" and CEPIN became "SE" / "7C" (`cfix`, `tldct`, `hfix`, `hfixr`), although the case's programmed fixes feed the Whisper biasing prompt. Check that the fixes reach the prompt, then consider a fuzzy match of an unknown fix token against the programmed fixes.
- [ ] **Second clause dropped (Rule/LLM).** `spd-until` loses `UNTIL CEPIN`; `dm-spd` loses `SPD 250`; `cfix-spd` becomes `CM 14000`; `cvia-alt` becomes `CM 3000`.
- [ ] **PTAC mapped as separate commands (Rule/LLM).** Both `ptac-fh-rnav` cases give `FH …, DM …` instead of `PTAC hdg alt approach`.
- [ ] **IDENT (Rule).** Both `ident` cases give `SQNORM` / `SQ then`.
- [ ] **Callsign lost on short transmissions (STT).** `circle`, `la` and `capp` produce the right canonical with no callsign or the wrong one (e.g. `AAL20`, `DLH331`).
- [ ] **Approach names (Rule).** `japp` "ILS 33" became `LOC33`; `eapp-ils` gives `EAPP ILS KOAK` (runway lost).
- [ ] **Broadcast and helicopter forms (Rule/STT).** `salt` → `SL TGT`; `sspd` → nothing, or `csa`; `atxi` → `TAXI KOAK`; `atxi-rwy` loses `@J`.
- [ ] **Singles.** `eld` → `RTIS DW L 19L`; `push-onto` → `PUSH 33` (expected `PUSH P`); `hs-twy-at` loses `@L`; `spd-maintain` 210 → 220 (STT).
