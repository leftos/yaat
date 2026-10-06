# STT tuning list

Work found by the controller-voice ouroboros (`--atc-ouroboros`; see "Tuning loop" in `docs/speech-recognition-pipeline.md`). Method, full results and limits: [`../research/2026-09-28-atc-ouroboros-baseline.md`](../research/2026-09-28-atc-ouroboros-baseline.md).

**Baseline** (`tools/Yaat.SpeechSandbox/Corpus/atc-ouroboros-baseline.json`):
- Run: seed 20260928, 200 cases × 3 trials. STT was the Whisper-medium ATC fine-tune (`borisdiakur/whisper-finetuned-for-ATC-ggml`); the LLM was `gemma4:e4b`.
- **Totals at batch 2 (the committed baseline): 157 PASS of 200, 0 gaps, 43 failing cases with their transcripts.** The first baseline (153 PASS, 76.5 %, mean WER 19.9 %, 7 gaps) is the table below.

  Two batch-2 runs of one seed differed in 104 of 200 transcripts because Piper synthesized different audio in each process; the synthesized audio is now cached (YAAT-225), so runs of one seed on one machine transcribe the same audio. Re-take the baseline once before reading a family's movement against it.

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

All seven are closed (batch 1): `KnownGaps` in `tests/Yaat.Client.Tests/AtcOuroborosTests.cs` is empty, and a new gap fails that test until it is listed. The fixes, for the next gap of the same shape:

- Spoken literals that a normaliser rewrites before matching (digits for "three sixty", NATO letters for "bravo") get `SttOnly` twins in the rewritten form; the spoken rules stay, because `PhraseologyVerbalizer` builds pilot speech from them.
- A `{rwy}` capture with no digit is no match (no runway veto, so no LLM escalation); a `{callsign}` capture with no digit is no match.
- `TrafficCallsignNormalizer` collapses a spoken traffic callsign after a cue (`follow`, `follow the`, `follow traffic`, `behind`, `give way to`: the literals in front of a `{callsign}` slot) into one ICAO token, kept only when it is on frequency; `CallsignParser.TryParseTrailing` never takes that tail as the addressed aircraft.
- Spoken "behind {callsign}" is `GIVEWAY` (7110.65 §3-7-2.a; the typed `BEHIND` alias agrees).
- Follow-ups from the aviation review: YAAT-218 (ground "follow X" → `FOLLOWG` by aircraft state), YAAT-219 (follow by description; follow with a visual approach).

## Baseline failures, clustered by cause

Batch 2 rulings (user, nextup decision round): the traffic-advisory rules absorb a stray "of" / "to" before the aircraft type, so "traffic off your left 4 miles of boeing" maps to `RTIS L 4 BOEING`; the ouroboros results record each failing case's Whisper transcript and mapper output, kept in the committed baseline, so a tuning wave starts from evidence.

Batch 2 takes the Rule clusters the transcripts confirm (gate/parking "alfa", traffic-advisory type, approach names, hold direction, PTAC's "rnav"); IDENT, broadcast/helicopter, second-clause and the singles stay STT-led.

Batch 2 landed the two clusters the transcripts confirmed: "alfa" is an input alias for A (a programmed fix spelled with an alias stays a fix), and every RTIS rule absorbs "of"/"to" before the type; approach names, hold direction and PTAC turned out to be mishears. Still open from its review: a stray "of"/"to" with no type after it becomes the type ("…four miles of" → `RTIS L 4 of`), as "…four miles" gives `RTIS L 4 miles`.

Batch 3 (owner rulings in YAAT-28) took the rule-side clusters the committed baseline still fails; the baseline's per-template rows already pass `japp`, `eapp-ils`, `atxi`, `atxi-rwy`, `eld`, `hs-twy-at`, `dm-spd` and `spd-maintain`, so they are off this list.

It added `SttOnly` "climb via <one word> [except maintain <alt>]" → `CVIA [alt]` (a guard in `PhraseologyMapper` rejects keywords, numbers, and a word followed by "departure", which keeps the named-SID validation), an "on to" twin of the plain pushback-onto rule, a crossing rule without the second "at", and stopped `AtcNumberParser` from adding bare digit words after "thousand" (teen and tens words still join).

A speed after "climb via … except maintain" is still read as an altitude (YAAT-345). The whole pipeline is being rethought (YAAT-343, [`../research/2026-10-05-stt-greenfield.md`](../research/2026-10-05-stt-greenfield.md)); that proposal decides whether the clusters below are still worked as rule patches.

Case names are from the baseline run (`synth-20260928-NNN-<template>`). "STT" means Whisper produced the wrong words. "Rule" means the words were right, or close enough, and the mapper got them wrong.

- [ ] **Hold direction lost (STT).** Batch 2's transcripts show misheard fixes, not a missing wording: `hfixl` "hold at all to left turns" → `HFIX ABL`, `hfixr` "hold at seven right turns" → `HFIX 07R` ("seven right" read as a runway). Earlier note: `hfixl` and `hfixr` map to `HFIX`: "left turns" / "right turns" is dropped.
- [ ] **Programmed fixes misheard (STT).** ALTAM became "ultima" / "ultramarve" and CEPIN became "SE" / "7C" (`cfix`, `tldct`, `hfix`, `hfixr`), although the case's programmed fixes feed the Whisper biasing prompt. Check that the fixes reach the prompt, then consider a fuzzy match of an unknown fix token against the programmed fixes.
- [ ] **Second clause dropped (STT).** `spd-until` loses `UNTIL CEPIN`: CEPIN was heard as "seppen" or "seven", beyond `PhoneticFixMatcher`'s edit distance of 2, so the mapper keeps `SPD 180` and drops the clause. (`cfix-spd` and `cvia-alt` were batch 3.)
- [ ] **PTAC mapped as separate commands (STT).** Batch 2's transcripts lose "rnav" ("clear of runway two eight right approach", "cleared on runway one five approach"); it is never spelled another way. Earlier note: Both `ptac-fh-rnav` cases give `FH …, DM …` instead of `PTAC hdg alt approach`.
- [ ] **IDENT (STT).** The rule exists (`PhraseologyRules.cs` ~:692-693) and the TransponderRules family's mean WER is 86.9 %. The mapper already rejects a non-numeric squawk code (`CommandParser.ParseSquawkOrReset`); the baseline's `SQ EW` comes from the LLM fallback.
- [ ] **Callsign lost on short transmissions (STT).** `circle`, `la` and `capp` produce the right canonical with no callsign or the wrong one (e.g. `AAL20`, `DLH331`).
- [ ] **Broadcast and helicopter forms (STT).** `salt` ("destination altitude") and `sspd` ("safe speed") are mishears.
- [ ] **"taxi via" heard as "taxiway" (STT).** `rwy-taxi-via` "runway one left taxiway foxtrot hotel echo" loses its path in the LLM; left to the LLM by owner ruling, since "taxiway" is a real word in hold-short phrases.
