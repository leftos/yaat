# Controller-voice ouroboros: first baseline

Date: 2026-09-28. Tool: `tools/Yaat.SpeechSandbox --atc-ouroboros` (see "Tuning loop" in [`../speech-recognition-pipeline.md`](../speech-recognition-pipeline.md)). Committed baseline: `tools/Yaat.SpeechSandbox/Corpus/atc-ouroboros-baseline.json`. Actionable findings: [`../plans/stt-rule-gaps.md`](../plans/stt-rule-gaps.md).

## Question

How well does the production speech pipeline map **controller** transmissions, across every phraseology rule family, when the words are spoken rather than typed? Where does it fail, and is each failure in STT (wrong words) or in mapping (right words, wrong command)?

## Method

1. **Templates.** `SynthTemplates` holds controller-phraseology templates covering all 13 rule families in `PhraseologyRules.cs`, plus compound (multi-clause) forms. Canonicals were derived from the rules, not guessed.
2. **Label verification.** Each rendered case must map to its expected canonical and callsign through the rule mapper alone before any audio is made. A template that can't verify is excluded and reported as a gap, so no mislabelled case enters the run.
3. **Synthesis.** Piper `vits-piper-en_US-libritts_r-medium` renders each case with 8 speaker ids × 4 speeds (0.9–1.2) and 400 ms of silence on each side.
4. **Scoring.** The full production path scores every case: Whisper → digit normalisation → callsign extraction → rule mapper → LLM fallback. Each case runs 3 trials. PASS means every trial produced the expected canonical and callsign, FAIL means none did, and FLAKY means some did.
5. **Context.** Every case is scored under the same scenario context its label was verified with: KOAK/KSFO runways, KOAK taxiways, parking/spot names and programmed fixes.

Run parameters:
- seed 20260928, 200 cases, 3 trials;
- STT: Whisper-medium ATC fine-tune (`borisdiakur/whisper-finetuned-for-ATC-ggml`);
- LLM: `gemma4:e4b`;
- this development machine, GPU.

The run took about 5 minutes. STT averaged about 294 ms per trial; clean transmissions took ~150 ms, and long or garbled ones up to ~650 ms.

## Results

**153 PASS, 0 FLAKY, 47 FAIL: a pass rate of 76.5 % and a mean WER of 19.9 %. 7 templates are gaps.**

| Family | Cases | Pass rate | Mean WER |
|---|---|---|---|
| TrafficAdvisoryRules | 7 | 29 % | 14.6 % |
| HoldRules | 6 | 33 % | 23.2 % |
| PtacRules | 6 | 50 % | 18.4 % |
| HelicopterRules | 5 | 60 % | 24.6 % |
| BroadcastRules | 8 | 62 % | 53.5 % |
| PatternRules | 15 | 67 % | 26.5 % |
| GroundRules | 33 | 73 % | 8.4 % |
| NavigationRules | 16 | 75 % | 22.5 % |
| TransponderRules | 9 | 78 % | 86.9 % |
| AltitudeSpeedRules | 27 | 81 % | 27.4 % |
| ApproachRules | 17 | 82 % | 8.4 % |
| Compound | 15 | 93 % | 8.4 % |
| TowerRules | 25 | 96 % | 8.0 % |
| HeadingRules | 11 | 100 % | 4.8 % |

119 templates were sampled, and 38 of them failed at least once. The 200 cases spread to about 1–2 per template, which is why the regression gate compares families rather than templates.

## Findings

- **Deterministic on this setup.** No case was FLAKY across 3 trials. With a fixed seed, the baseline diff therefore reflects pipeline changes rather than GPU noise, at least on this machine and model pair.
- **About a third of the failures are mapping failures.** 15 of the 47 FAILs had a transcript WER of 15 % or less: Whisper heard the transmission nearly right, and the rules or the LLM produced the wrong canonical. Clear examples:
  - "taxi … to gate G alfa five" is left verbatim instead of `@GA5`.
  - Traffic advisories lose the aircraft type (`RTIS NR 4 of`).
  - Hold direction is dropped (`HFIXL` → `HFIX`).
  - PTAC is split into `FH, DM`.
  - `IDENT` becomes `SQNORM`.

  These are the cheapest wins, because a text-level `PhraseologyMapperTests` row reproduces each without audio.
- **The rest are STT-led.** The largest group is programmed fixes misheard despite the biasing prompt: ALTAM became "ultima" or "ultramarve", and CEPIN became "SE" or "7C". Callsigns were also lost on very short transmissions (`circle`, `la`, `capp`). High family WER (Transponder 87 %, Broadcast 54 %) comes from a few garbled short clips, not from broad degradation.
- **Seven templates can't map even as text** (gaps). Two of them yield a wrong command rather than a miss:
  - "cleared into bravo airspace" → `CMTR B`
  - "follow X on ground" → `FOLLOW <first word>`

  The other five:
  - `follow` and `giveway`, where a multi-word telephony isn't captured;
  - `l360` and `r270`, where digit normalisation runs before the rule literals;
  - `cva` under a populated scenario context.

## Limits

- The audio is clean TTS from one voice pack, with no radio band-limiting, noise or real accents. These numbers bound the phonetic surface and the mapping logic, not field accuracy. Real samples from speech telemetry (`tools/speech_telemetry.py`) stay authoritative for model decisions.
- A family of 5–8 cases has coarse resolution: one case moves its pass rate by 12–20 points. Raise `--cases` for a sharper read of a family under active tuning.
- Results are specific to the Whisper and LLM models named above. Switching either model means taking a new baseline, not reading the diff.
- Template changes. The seed fixes which cases are drawn, so adding or removing templates changes the drawn cases. Take a new baseline (`--update-baseline`) in the same commit as a template change.

## Reproduce

```
pwsh tools/gate.ps1 -Log .tmp/atc-ouroboros.log -TimeoutSeconds 1800 -Slot heavy -- dotnet run --project tools/Yaat.SpeechSandbox -c Release -- --atc-ouroboros --out-dir .tmp/atc-baseline
```

Run it from the repo root, through `tools/gate.ps1`, with the Piper voice pack in `%LOCALAPPDATA%\yaat\voices\` and the Whisper and LLM models configured in the client's Settings → Speech. It exits 0 against the committed baseline when nothing regressed.
