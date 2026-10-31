# STT context layer measured on US ATC radio (YAAT-1571)

Date: 2026-10-31. Question: does the per-transmission context layer (YAAT-343 step 3), meaning room hotwords at the recognizer and the context decoder's resolution over the n-best, make rasr-parakeet's transcripts and the slots read from them better, and at what latency and memory? Design: [`plans/yaat-343-context-layer.md`](../plans/yaat-343-context-layer.md). The model choice is in the [bake-off](2026-10-30-stt-model-bakeoff.md).

## Set and method

- **Clips:** the 106 `liveatc-us` clips of the bake-off (KSFO ground, KOAK ground, tower and approach), with gold slots in `slots.json` (127 callsigns, 44 runways, 76 taxiways, 8 gates, 6 procedures, 3 landmarks, 2 fixes) and per-feed rooms in `rooms.json`. Both live in the owner's private config folder; only aggregates are committed.
- **Rooms:** each clip is decoded under three rooms (`RoomContextBuilder`). The unpadded room holds the feed's real aircraft. The padded room adds seeded distractors up to 35 aircraft. The oracle room holds only the clip's own gold.
- **Lexicon:** the generated KOAK/KSFO spoken forms (YAAT-1568), including gate and spot forms, named places, procedure names and the generated fix forms.
- **Model:** rasr-parakeet v2 fp32 on sherpa-onnx 2.0.1 with YAAT's own native (`edad9a7fe1`), on CPU and on GPU (CUDA 13).
- **Arms:**
  - greedy (the off/off cell);
  - `modified_beam_search` with 4 paths;
  - room hotwords (`--room-hotwords`, cap 300, score 1.0);
  - resolution over the n-best (`--resolve`);
  - the two-pass per-aircraft decode (`--two-pass`).
- **Golden runs:** Gemini 4 Argon (cloud) and the shipped Whisper medium ATC, both with resolution on.
- **Timing:** every run held the whole machine (`gate.ps1 run --slot critical --alone --local`), after a `--slot heavy --local` build.
- **Scoring:** WER by `--corpus-wer` (`WerTextNormalizer`), all clips and the 43 owner-checked ones. Slots by `CorpusSlotScorer`: per-kind recall, false insertions per 100 clips and wrong-addressee resolutions. Unless a row says otherwise, slot figures are the padded room's.

## Results

| Arm | WER all | WER owner | Owner WER, rewritten | Callsign recall | All-slot recall | Insertions /100 | p50 / p90 GPU | p50 / p90 CPU |
|---|---|---|---|---|---|---|---|---|
| greedy (off/off) | 22.1% | 29.6% | - | 29.1% | 41.0% | 16.0 | 92 / 128 ms | 285 / 480 ms |
| greedy + resolve | 22.1% | 29.6% | 29.6% | 29.1% | 41.0% | 17.9 | 123 / 154 ms | 270 / 455 ms |
| beam + resolve | 21.3% | 28.6% | 28.3% | 32.3% | 42.9% | 18.9 | 382 / 675 ms | 410 / 672 ms |
| beam + hotwords | 21.3% | 28.3% | - | 28.3% | 41.0% | 16.0 | 394 / 742 ms | |
| beam + hotwords + resolve | 21.3% | 28.3% | 28.0% | 32.3% | 42.9% | 18.9 | 420 / 671 ms | 410 / 691 ms |
| same, hotwords lowercase | 21.4% | 28.8% | 28.7% | 32.3% | 42.9% | 20.8 | 372 / 688 ms | |
| same, unpadded-room hotwords | 21.2% | 28.2% | 27.9% | 32.3% | 42.9% | 18.9 | 380 / 634 ms | |
| two-pass, aircraft list | 21.2% | 28.3% | 28.0% | 31.5% | 42.5% | 18.9 | 658 / 1,196 ms | 696 / 1,408 ms |
| two-pass, aircraft + airport list | 21.2% | 28.2% | 27.9% | 32.3% | 42.9% | 18.9 | 620 / 1,338 ms | |
| Gemini 4 Argon + resolve | 11.3% | 15.9% | 16.2% | 44.9% | 52.6% | 28.3 | 2.7 / 3.8 s (cloud) | |
| shipped Whisper + resolve | 39.1% | 50.5% | 50.7% | 7.9% | 28.2% | 13.2 | 233 / 474 ms | |

"Rewritten" is the WER of the transcript after the decoder rewrites its accepted spans. Memory is set by the model, not the arm: on GPU, 1.4 GB working set plus 3.7 GB of VRAM; on CPU, 3.1 GB. Hotwords and the two-pass decode add under 30 MB.

Argon scores 15.9% owner-only and 11.3% on all clips by `--corpus-wer`. That is 3.2 points better than the bake-off's scorer gave it (19.1%), which is within the ±3 points expected between the two scorers.

Before any full run, the hotword score was swept on a 30-clip smoke subset (seed 1117) at 1.0, 1.5 and 2.5, along with casing (Title, lower, UPPER) and cap (50, 100, 300). Every variant landed within one or two clips of the others, except score 2.5, which lost three callsigns. The smoke folders are `.tmp/1571/sweep-*`.

## Findings

- **Room hotwords buy nothing measurable.** Against beam with resolution, they move owner WER by 0.3 points and slot recall by nothing, and they cost up to 40 ms at p50 on GPU. They help as often as they hurt: on the smoke set they fixed a misheard airline in some clips and turned a correct airline into a near-homophone in others.
- **Resolution helps only over beam's alternatives.** Resolution over greedy's single hypothesis changes nothing, because the log-prob gate keeps a confident greedy word exact. Over beam's four paths it adds 3.2 points of callsign recall (37 to 41 of 127) and cuts owner WER by 1.0 point. The price is the beam search: p50 rises by 290 ms on GPU (the search runs on the CPU, so the GPU's speed-up is lost) and by 125 ms on CPU.
- **The two-pass decode loses.** It re-decoded 51-55 of 106 clips and gained no slot, while p50 rose by about 240 ms and p90 nearly doubled. No say-again guess became a resolved call. Room-wide context with per-aircraft scoring in the decoder (design (a) of the owner's ruling) is the better design.
- **Room size does not matter yet, and precision is perfect.** The unpadded, padded and oracle rooms give the same recall in every run, and no run resolved a single wrong addressee. Padding to 35 aircraft adds no false insertions over the unpadded room. The oracle room inserts 0.9 per 100 clips against 18.9, so most insertions are real airport entities the gold does not list.
- **The decoder caps recall, not the recognizer.** With Argon's transcripts (15.9% owner WER) callsign recall is only 44.9%, and 45.7% in the oracle room.
  - **Forms:** of 124 reachable gold callsigns, 89 are spoken in a form the lexicon generates.
  - **Rules:** Argon's oracle room misses the callsign in 45 clips. In about 13 of them (counted by hand), the transcript holds the callsign correctly and the decoder refuses it on purpose. The patterns:
    - a greeting or facility name before the callsign pushes it past `MaxStartOffset`;
    - an N-number spoken without "november" fails the prefixed-form rule;
    - GA digits spoken grouped ("twenty eight sixty six") match no form;
    - a GA type telephony form is missing because the room fixture lacks the aircraft's type;
    - a callsign at the end of a transmission after a traffic word is refused as traffic.
  - The other misses are recognizer errors on the airline name or the digits. The two gold fixes are never recalled by any arm.
- **The lexicon reached the slots it could.** On the 12-clip smoke run at b003b21a2 (greedy, CPU, resolution on), landmark form coverage went from 0 of 1 to 1 of 1, and recall stayed the same (callsign 35.3%, all slots 23.7%).

## Recommendation for the next plan

1. **Ship greedy rasr-parakeet fp32 as the fast mode, and beam with resolution as the accurate mode.** Greedy is 92 ms on GPU and 285 ms on CPU. Beam with resolution adds 3.2 points of callsign recall and 1.0 point of owner WER for about 125-290 ms. Both fit the push-to-talk budget, and the in-app benchmark shows each mode's latency and RAM so the user picks one. Room hotwords and the two-pass decode stay in the sandbox and do not go into the client.
2. **Raise decoder recall before client wiring, measured on Argon's transcripts and on rasr's.** Precision has headroom: no wrong addressee in any run, and room size changes nothing. The work is YAAT-1578 (openers, closing tails, prefixes), YAAT-1575 (more spoken forms), and the new patterns above (N-numbers without "november", grouped GA digits, a trailing addressee after a traffic word, GA types in the room fixture, fix recall).
3. **Judge a fine-tune afterwards, on slot recall.** rasr trails Argon by 12.7 points of owner WER, but with today's decoder even Argon's transcripts give only 45% callsign recall. Measure the decoder gain first; then the gap between rasr and Argon on slot recall says whether a fine-tune is worth its cost.
