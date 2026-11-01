# STT engines on clean synthetic audio (YAAT-1624)

Date: 2026-11-01.

**Question.** Does the shipped choice still hold on clean audio? That choice is rasr-parakeet v2 fp32 in two modes: fast (greedy) and accurate (beam 4 with `ContextDecoder` resolution). It was made on the 106 noisy `liveatc-us` clips ([bake-off](2026-10-30-stt-model-bakeoff.md), [context layer](2026-10-31-stt-context-layer.md)), but a YAAT user speaks into a clean local microphone. Owner: "feels like we've already picked the shipped engine without testing clean local audio".

**Scope.** The owner dropped the real-mic step ("I'll only do that if I use it in app and am not happy with it"), so this note rests on the LiveATC set and the synthetic set below.

## Set and method

- **Audio:** the `--atc-ouroboros` set at its committed seed 20260928: 200 controller transmissions across every `PhraseologyRules` family. Each one is rendered from a template, verified through the rule mapper, and spoken by Piper LibriTTS-R. Speakers are drawn from 8, and speeds from 0.9 to 1.2. Each clip carries 0.4 s of silence on both sides.
- **Speaking rate:** measured from each clip's transcript and length, padding included. It is a median of 137 to 161 words per minute across the four speeds. That is slower than controllers speak; [Faster speech](#faster-speech) covers it.
- **Pipeline:** the production one, from `--atc-ouroboros` through `EvalRunner`.
  - **fast** is the engine's top transcript into `MapTranscriptWithTraceAsync`: callsign extraction, then the rule mapper, then the LLM fallback.
  - **accurate** sends the whole n-best into `MapHypothesesWithTraceAsync`. That decodes it against the case's room, built by `RoomContextFactory`: KOAK from an approach position, the case's active callsigns with its programmed fixes on the addressed aircraft, the committed KOAK test layout, and the Oakland Tower, Oakland Ground and NorCal Approach facility names. A resolved callsign comes with the command text minus its span; `SayAgain` and `NoCallsign` fall back to `CallsignParser` on hypothesis 0.
- **Natives:** sherpa-onnx 2.0.1 with YAAT's own native at `804a1d0c29`, the one the client ships. The CPU and CUDA 13 copies are built by `New-SherpaNativeSandbox.ps1`.
- **Machine and timing:** Ryzen 7 7800X3D, 64 GB, RTX 4090. Every run held the whole machine (`gate.ps1 run --slot critical --alone --local`) after a `--slot heavy --local` Release build. There is 1 trial per case: sherpa decoding is deterministic on a fixed clip, while Whisper on GPU is not, so its row carries about a point of noise.
- **Scores:** per command clause (`CommandScoring`). Each gold clause is recognised, wrong (wrong arguments, wrong verb, or an inserted clause) or rejected, pooled over the 218 gold clauses. Callsign is the share of trials that extracted the expected callsign. WER is hypothesis 0's, digit-normalised.
- **Latency:** STT is the engine's own decode of the whole clip, which is what the user waits for after releasing push-to-talk. Decode+map is the accurate path's `ContextDecoder` plus mapping time, and the LLM fallback falls inside it when it runs.
- **Memory:** the peak figures in these runs are for the whole process, so they include the LLM fallback model. The engine's own memory is the bake-off's speech-only measurement, quoted under [Memory](#memory).

## Results

| Engine | Mode | Device | Recognised | Wrong | Callsign | WER | STT p50 / p90 ms | Resolved / say again / none | Decode+map p50 / p90 ms |
|---|---|---|---|---|---|---|---|---|---|
| rasr-parakeet v2 fp32 | accurate | GPU | **88.1%** | 12.8% | **100.0%** | **8.3%** | 469 / 724 | 190 / 5 / 5 | 65 / 230 |
| rasr-parakeet v2 fp32 | accurate | CPU | **88.1%** | 12.8% | **100.0%** | **8.3%** | 370 / 524 | 190 / 5 / 5 | 69 / 274 |
| rasr-parakeet v2 fp32 | fast | GPU | 86.2% | 14.7% | 99.0% | 8.4% | 117 / 142 | | |
| rasr-parakeet v2 fp32 | fast | CPU | 86.2% | 14.7% | 99.0% | 8.4% | 267 / 390 | | |
| Whisper medium ATC (shipped today) | fast | GPU | 85.8% | 12.4% | 96.5% | 15.3% | 289 / 788 | | |
| Whisper medium ATC (shipped today) | fast | CPU | 78.9% | 6.9% | 94.0% | 16.2% | 3,912 / 6,210 | | |
| rasr-parakeet v2 int8 | accurate | CPU | 84.4% | 17.0% | 99.5% | 9.3% | 302 / 422 | 181 / 11 / 8 | 68 / 234 |
| rasr-parakeet v2 int8 | accurate | GPU | 84.4% | 17.0% | 99.5% | 9.0% | 654 / 967 | 186 / 7 / 7 | 75 / 222 |
| rasr-parakeet v2 int8 | fast | GPU | 83.5% | 17.4% | 99.5% | 9.0% | 380 / 497 | | |
| rasr-parakeet v2 int8 | fast | CPU | 83.0% | 17.4% | 99.0% | 9.5% | 246 / 359 | | |
| Parakeet v3 ATC (qenneth) fp32 | accurate | both | 81.2% | 20.2% | 97.5% | 18.2% | 447 / 673 GPU, 356 / 516 CPU | 154 / 24 / 22 | 86 / 274 GPU |
| Parakeet v3 ATC (qenneth) fp32 | fast | both | 79.8% | 22.9% | 94.0% | 18.5% | 111 / 148 GPU, 271 / 391 CPU | | |
| Parakeet v3 ATC (qenneth) int8 | accurate | CPU | 79.4% | 21.6% | 96.5% | 19.8% | 295 / 415 | 143 / 39 / 18 | 99 / 1,123 |
| Parakeet v3 ATC (qenneth) int8 | fast | CPU | 77.1% | 24.8% | 95.5% | 20.6% | 226 / 319 | | |
| Nemotron Streaming 0.6B fp32 | fast | GPU | 76.1% | 26.1% | 99.5% | 20.0% | 71 / 79 | | |
| Nemotron Streaming 0.6B fp32 | fast | CPU | 76.1% | 26.1% | 99.5% | 20.0% | 247 / 295 | | |
| Parakeet-TDT 0.6B v2 fp32 | accurate | both | 69.7% | 31.2% | 98.5% | 29.2% | 469 / 680 GPU, 332 / 456 CPU | 127 / 52 / 21 | 142 / 380 |
| Parakeet-TDT 0.6B v2 fp32 | fast | both | 67.0% | 33.9% | 94.0% | 29.2% | 120 / 167 GPU, 256 / 373 CPU | | |
| Parakeet-TDT 0.6B v3 fp32 | accurate | both | 67.0% | 33.9% | 97.5% | 27.0% | 432 / 654 GPU, 352 / 492 CPU | 117 / 55 / 28 | 165 / 1,248 |
| Parakeet-TDT 0.6B v3 fp32 | fast | both | 63.8% | 37.2% | 95.0-95.5% | 29.5% | 117 / 161 GPU, 264 / 370 CPU | | |

The qenneth int8 GPU rows (78.4% / 77.1% recognised, STT p50 619 and 350 ms) follow the rasr int8 GPU pattern below. The full per-run table, with load time and the whole-process memory peaks, is `.tmp/clean-1624/table.md` in the feat/speech worktree.

### Raw against rewritten text in accurate mode

The context decoder rewrites the spans it accepts (fixes, landmarks, runways, taxiways) to their spoken forms. The A/B gave the mappers either the raw winning hypothesis or the decoder's rewritten transcript, each minus the callsign span. It ran on rasr v2 fp32 accurate.

| Text | Device | Recognised | Wrong | Callsign | STT p50 / p90 ms |
|---|---|---|---|---|---|
| raw hypothesis | GPU | 88.1% | 12.8% | 100.0% | 469 / 724 |
| rewritten | GPU | **88.5%** | **12.4%** | 100.0% | 341 / 509 |
| raw hypothesis | CPU | 88.1% | 12.8% | 100.0% | 370 / 524 |
| rewritten | CPU | **88.5%** | **12.4%** | 100.0% | 374 / 557 |

The counts are identical on both devices:
- The rewrite fixed one case (a `DEPART SUNOL`) and broke none.
- One other case changed but stayed wrong: "sep in" for CEPIN was rewritten to the fix SEAPO.
- Most fix-name misses ("balton", "altimat") are not accepted by the decoder, so the rewrite leaves them alone.

Accurate mode now maps the rewritten text only.

The two GPU runs decode identically, yet their STT p50 differs by 128 ms. GPU beam latency therefore varies by about that much from run to run; read the GPU beam figures as ranges.

### Faster speech

The default speed pool speaks at a median 166-193 words per minute of speech (padding excluded), and controllers on frequency speak faster. The owner asked for faster runs. `--speeds` reruns the same 200 cases at Piper speed 1.4 and at 1.7, keeping each case's template, callsign, slots and speaker; only the speed changes. Measured over each set:

| Pool | Median wpm (range) |
|---|---|
| default (0.9-1.2) | 166-193 (123-249) |
| 1.4 | 224 (180-307) |
| 1.7 | 260 (198-335) |
| 2.0 | 279 (209-348), not run |

Speed 2.0 was not run: it adds 19 wpm over 1.7 and is far outside the range Piper is used at. The runs are GPU only, since the sherpa models scored the same on CPU and GPU in every run above. Accurate mode uses the rewritten text.

| Engine | Mode | default: recognised / wrong | 224 wpm: recognised / wrong | 260 wpm: recognised / wrong | Callsign at 260 | WER at 260 | STT p50 / p90 at 260 ms |
|---|---|---|---|---|---|---|---|
| rasr v2 fp32 | accurate | **88.5% / 12.4%** | **85.8% / 16.1%** | **80.7% / 20.6%** | 98.0% | **10.8%** | 255 / 375 |
| rasr v2 fp32 | fast | 86.2% / 14.7% | 82.6% / 18.8% | 77.5% / 22.9% | 98.0% | 11.9% | 93 / 115 |
| rasr v2 int8 | accurate | 84.4% / 17.0% | 84.4% / 16.5% | 77.5% / 23.9% | 99.5% | 11.0% | 556 / 788 |
| rasr v2 int8 | fast | 83.5% / 17.4% | 83.0% / 17.9% | 73.9% / 26.6% | 99.0% | 11.9% | 307 / 421 |
| Whisper medium ATC | fast | 85.8% / 12.4% | 81.7% / 18.3% | 78.0% / 22.0% | 94.5% | 19.7% | 191 / 475 |
| qenneth v3 fp32 | accurate | 81.2% / 20.2% | 76.6% / 22.9% | 68.8% / 33.0% | 93.5% | 23.5% | 242 / 353 |
| qenneth v3 fp32 | fast | 79.8% / 22.9% | 75.2% / 25.2% | 66.5% / 34.4% | 93.5% | 23.9% | 81 / 104 |
| Nemotron 0.6B fp32 | fast | 76.1% / 26.1% | 70.6% / 31.2% | 55.0% / 44.5% | 94.5% | 30.5% | 54 / 62 |

The per-run tables are `.tmp/clean-1624/table-speed14.md` and `table-speed17.md` in the feat/speech worktree. Latency falls as speed rises, because the clips get shorter.

## Memory

The engine's own memory, from the bake-off's speech-only runs (`--corpus-wer`, no mapper, no LLM): rasr-parakeet v2 fp32 peaks at 2,835 MB on CPU, and at 1,140 MB of working set plus 3,696 MB of VRAM on GPU; int8 peaks at 1,067 MB on CPU. On clean audio, int8 lowers the whole-process peak by about 1.8 GB on CPU (4,021 against 5,798 MB) and by about 2.8 GB of VRAM on GPU, the same gap as speech-only.

## Findings

- **rasr-parakeet v2 fp32 leads on clean audio too, in both modes and on both devices.** It recognises the most commands and gets the most callsigns, with the lowest WER, ahead of every other local engine, as it did on LiveATC. The shipped default stands.
- **Accurate mode's gain grows with speaking rate.** With the rewritten text, rasr v2 fp32 accurate beats fast mode on recognised commands by 2.3 points at the default pace, by 3.2 at 224 wpm and by 3.2 at 260 wpm. At the default pace it also lifts callsign accuracy from 99.0% to 100%.
  - The cost at the default pace is about 100 ms at STT p50 on CPU, and 225-350 ms on GPU (the range of the two GPU runs).
  - The decode and mapping add 65-70 ms more at p50 and 225-275 ms at p90.
- **Faster speech costs every engine, and rasr v2 fp32 loses the least.** From the default pace to 260 wpm, rasr v2 fp32 accurate drops 7.8 points of recognised commands. Over the same range Whisper drops 7.8, rasr int8 accurate 6.9, qenneth accurate 12.4 and Nemotron 21.1. Callsign accuracy stays at 98% or above for rasr, while WER rises from 8.3% to 10.8%. At 260 wpm the shipped Whisper (78.0%) sits between rasr fp32's fast mode (77.5%) and its accurate mode (80.7%), with a worse callsign rate (94.5% against 98.0%) and WER (19.7% against 10.8%).
- **The beam runs slower on the GPU than on the CPU.** rasr fp32 accurate is 469 / 724 ms on the RTX 4090 against 370 / 524 ms on the CPU, while greedy is 117 ms on GPU against 267 ms on CPU. `modified_beam_search` runs its search loop on the CPU (context-layer note), and on GPU it also pays the transfers. The owner ruled that accurate mode follows the user's device pick and the in-app benchmark (YAAT-1604) shows the numbers.
- **int8 belongs on the CPU, if anywhere.**
  - On GPU it is slower than fp32 (380 against 117 ms greedy) and less accurate.
  - On CPU it is 21 ms faster than fp32 at greedy p50, 2.7-3.7 points lower on commands, and about 1.8 GB smaller.
  - It is the choice for a machine short on RAM, not a speed choice.
- **The shipped Whisper is close on commands but not on callsigns or speed.**
  - On GPU it recognises 85.8% of commands against rasr's 86.2% fast and 88.1% accurate. It makes the fewest wrong commands (12.4%), and it is at 96.5% callsign and 15.3% WER.
  - Its p90 is 788 ms on GPU, and on CPU it is unusable at 3.9 s p50.
- **The stock Parakeets and qenneth resolve badly in accurate mode.** They end 22-28% of trials in say-again or no callsign, against 5% for rasr fp32. The decoder cannot resolve what the recognizer garbles.
- **rasr fp32's remaining misses are mostly fix names and one number pattern:**
  - fix names, such as "balton" for ALTAM, "sep in" for CEPIN, "sunnell" for SUNOL and "altimat" for ALTAM;
  - a dropped trailing "zero" on five-digit altitudes ("one five zero zero" for 15,000);
  - "via SID except maintain" heard as "IAH SID accept";
  - the RNAV approach name inside a PTAC.

  The decoder's rewritten transcript corrects only the fix spans it accepts, and it accepts few of these. Fix-name recall is decoder work (YAAT-1575 spoken forms, the fix-recall item of the context-layer note), and the five-digit altitude belongs to the number parser.

## Recommendation

1. **Keep rasr-parakeet v2 fp32 as the default engine.** It leads on clean audio at every speaking rate tested, as it did on LiveATC radio.
2. **Offer rasr v2 int8 beside it, for machines short on RAM.** It is about 1.8 GB smaller on CPU and loses 0-4 points of recognised commands against fp32 in the same mode. It is slower than fp32 on GPU, so it is a CPU choice.
3. **No other local engine earns a place.**
   - qenneth v3 ATC and the stock Parakeets trail rasr v2 fp32 in the same mode by 6-22 points.
   - Nemotron is the fastest but collapses on fast speech.
   - Whisper, the shipped engine today, stays only until YAAT-1574 removes LM-Kit: it is close on commands at normal pace but has worse callsigns and WER, a 0.5-0.8 s GPU p90, and 3.9 s on CPU.
4. **Accurate mode maps the rewritten text**, the A/B winner.
5. **The default mode is the owner's call.** Accurate gains 2.3-3.2 points for 100-350 ms more latency.
6. **YAAT-1599's published set (rasr v2 fp32 and int8) needs no change.**

## Owner ruling

- **Default mode:** accurate. A fresh install starts in accurate mode, and the user can switch to fast.
- **Offered set:** rasr-parakeet v2 fp32, the default, and int8. Whisper leaves with YAAT-1574. The published set is unchanged.
- **Accurate mode's device** follows the user's pick, and the in-app benchmark (YAAT-1604) shows the numbers.
- **Real-mic captures** are not part of this measurement; they come back only if the owner is unhappy in app.
