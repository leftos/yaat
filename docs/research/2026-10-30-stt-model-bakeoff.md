# STT model bake-off on US ATC radio (YAAT-1558)

Date: 2026-10-30. Question: which speech-to-text model transcribes real US ATC radio best, so that reference transcripts and development runs use it (owner rule: feature work uses the strongest model available; [speech-recognition-pipeline.md](../speech-recognition-pipeline.md), "Measuring recognition on real speech").

## Set and method

- **Clips:** 120 squelch-to-squelch clips (1.5-12 s, 16 kHz) from four LiveATC archives the owner saved by hand: KSFO ground, KOAK ground, KOAK tower and KOAK approach, all 2026-10-30 0000Z. They are kept in the owner's private config folder, never committed (LiveATC terms: personal, non-commercial use, no automated retrieval).
- **References:** 106 clips were settled with the owner, clip by clip, and 14 were dropped as noise or illegible. 43 references are the owner's own call by ear, 12 come from strong model agreement, and 51 are Claude's judgement from the candidates plus context and phraseology. The owner-only column is the unbiased one, since references settled from model agreement favour the models that agreed. Fixes are written as their navdata identifiers (`mitoe`, `wesch`).
- **Scoring:** pooled WER after `WerTextNormalizer` (strict), and again with spoken numbers collapsed to digits on both sides (digit-collapsed). The ranking is the same either way.
- **Models:**
  - Whisper medium-v3 ATC (the shipped default), Whisper large-v3 ATC (both jacktol fine-tunes, ggml by borisdiakur) and stock Whisper large-v3, run locally with YAAT's biasing prompt;
  - OpenAI `gpt-transcribe`, ElevenLabs `scribe_v2` and Gemini `gemini-4-argon`, each with a short ATC instruction, and again with a 364-term list drawn from `PhraseologyRules.cs` (ElevenLabs as key terms);
  - Gemini `gemini-3.5-transcribe`, which takes no prompt, digits only, and hit its daily quota on 15 of the 106 clips.
- **Cost:** under $1 for every cloud run together.

## Results (strict WER, sorted by owner-only)

| Model | All 106 | Owner-only 43 | Owner + agreed 55 |
|---|---|---|---|
| Gemini 4 Argon | 12.6% | **16.1%** | 13.3% |
| Gemini 4 Argon + terms | 11.2% | 17.8% | 14.9% |
| ElevenLabs Scribe v2 + key terms | 17.6% | 28.3% | 23.4% |
| ElevenLabs Scribe v2 + term list | 20.8% | 31.7% | 25.6% |
| ElevenLabs Scribe v2 | 20.7% | 32.0% | 25.9% |
| OpenAI gpt-transcribe | 27.9% | 39.1% | 33.9% |
| OpenAI gpt-transcribe + terms | 29.7% | 39.5% | 34.8% |
| Gemini 3.5 transcribe (91 clips) | 40.6% | 47.1% | 43.2% |
| Whisper large-v3 ATC | 37.8% | 50.4% | 44.6% |
| Whisper medium-v3 ATC (shipped) | 39.3% | 50.5% | 46.9% |
| Whisper large-v3 | 56.0% | 65.3% | 60.7% |

The callsign proxy counts clips where the transcript begins with the reference's first two words: Gemini + terms 80 of 106, Gemini 74, ElevenLabs + key terms 54, OpenAI 35, Whisper 22-23.

## Findings

- **Gemini 4 Argon is the strongest**, with about a third of the shipped model's errors on the owner-checked clips. It is the model that drafts reference transcripts.
- **The shipped Whisper models fail on US speech.** They hear European callsigns ("csa", "el al", "britair", "ruzyne"). They were trained on `jacktol/ATC-ASR-Dataset`, which is built from UWB-ATCC (Prague) and ATCO2. The large-v3 ATC fine-tune is no better than medium.
- **A static term list is not context.** It helped ElevenLabs as key terms, made little difference to OpenAI and to Gemini, and did harm:
  - Gemini + terms invented a whole takeoff clearance on a silent clip.
  - Key terms pulled unclear callsigns toward listed ones ("skywest", "kizer" for the KOAK landmark Kaiser).
  - Gemini + terms turned "skywest thirty seven zero seven" into "air serbia zero zero seven".

  The rebuild should feed per-transmission context and check the output against it (owner steer on YAAT-343).
- **Fix names need navdata, not spelling.** No model wrote `mitoe`; Gemini's "mehtoe" was the closest. YAAT-1560 adds fix-resolved scoring.
- **Digits:** Gemini 3.5 transcribe writes numbers as digits ("4413"). Its score suffers on formatting as well as recognition, which is why the digit-collapsed view exists.
