# STT model bake-off on US ATC radio (YAAT-1558, YAAT-1564)

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

## Local models timed on CPU and GPU (YAAT-1564)

The same 106 clips through `--corpus-wer`. Each run is ranked on accuracy, speed and memory together, and each local model is run on the CPU and on the GPU, since those are the figures the in-app benchmark will show users choosing a model for their machine.

### Method

- **Machine:** Ryzen 7 7800X3D (8 cores, 16 logical), 64 GB RAM (63 GB usable), RTX 4090. Every run held the whole machine through the gate's `--alone` critical slot, after a local Release build.
- **Latency:** per clip, the time from handing over the whole clip to the transcript: what the user waits for after releasing push-to-talk. The streaming Nemotron model is fed in 0.1 s chunks as it would be while the user speaks, and only the finalization after the last sample is timed. p50, p90 and max are nearest-rank over all 106 clips. Model load and warm-up are timed separately. Whisper's warm-up decodes a full 30 s window, so its figure is above the load alone.
- **Memory:** peak working set of the process, and peak dedicated GPU memory from Windows' `GPU Process Memory` counters. A GPU run whose GPU memory does not rise 100 MB through the load is refused as a silent CPU fallback.
- **Devices:**
  - Whisper runs on LM-Kit. On CPU, its CUDA and Vulkan backends are turned off: with either one loaded, zero GPU layers ran Whisper on a single thread (32 s per clip).
  - The sherpa-onnx models run on sherpa-onnx 1.13.8, the CPU build from NuGet. GPU runs use the release's CUDA 13 build with cuDNN 9.27, through `tools/Yaat.SpeechSandbox/New-SherpaGpuSandbox.ps1`.
  - The sherpa models are timed as int8 (the published or `quantize_dynamic` exports) and as fp32 (re-exported from each `.nemo` with sherpa-onnx's NeMo scripts at 935e5ded).
- **WER:** strict, scored as in the first table. The owner-only column is the unbiased one.
- **Gemini 4 Argon** was rerun uncached as the golden baseline. It had the same request as the first pass but scored 19.1% owner-only against 16.1% then: the model is not deterministic between runs, so read its row as a range of about ±3 points.

### CPU

| Model | Precision | WER all 106 | WER owner-only 43 | p50 ms | p90 ms | load + warm-up s | peak RAM MB | peak VRAM MB | Licence |
|---|---|---|---|---|---|---|---|---|---|
| Whisper medium ATC (shipped) | ggml f16 | 38.6% | **50.5%** | 3,873 | 5,869 | 4.7 | 2,012 | 0 | MIT |
| Whisper large-v3 ATC | ggml f16 | 37.7% | **50.5%** | 7,995 | 16,583 | 26.8 | 4,140 | 0 | MIT |
| Whisper large-v3 | ggml f16 | 56.6% | **67.2%** | 11,877 | 40,709 | 17.8 | 4,148 | 0 | Apache-2.0 |
| Parakeet-TDT 0.6B v2 | int8 | 54.6% | **61.1%** | 253 | 396 | 2.8 | 984 | 0 | CC-BY-4.0 |
| Parakeet-TDT 0.6B v2 | fp32 | 46.8% | **53.8%** | 302 | 484 | 2.8 | 2,800 | 0 | CC-BY-4.0 |
| Parakeet-TDT 0.6B v3 | int8 | 63.3% | **69.5%** | 246 | 374 | 2.8 | 992 | 0 | CC-BY-4.0 |
| Parakeet-TDT 0.6B v3 | fp32 | 42.6% | **50.1%** | 318 | 515 | 5.6 | 2,837 | 0 | CC-BY-4.0 |
| Orukeet v0.1 | int8 | 57.7% | **65.0%** | 187 | 317 | 3.0 | 1,069 | 0 | CC-BY-SA-4.0 |
| Orukeet v0.1 | fp32 | 39.5% | **49.9%** | 303 | 504 | 7.7 | 2,835 | 0 | CC-BY-SA-4.0 |
| rasr-parakeet v1 (US ATC) | int8 | 38.2% | **47.5%** | 260 | 411 | 3.0 | 1,067 | 0 | Llama 3.2 Community |
| rasr-parakeet v1 (US ATC) | fp32 | 22.6% | **31.7%** | 303 | 513 | 8.5 | 2,836 | 0 | Llama 3.2 Community |
| rasr-parakeet v2 (US ATC) | int8 | 35.0% | **44.5%** | 253 | 425 | 2.9 | 1,067 | 0 | Llama 3.2 Community |
| rasr-parakeet v2 (US ATC) | fp32 | 22.9% | **31.3%** | 324 | 494 | 8.3 | 2,835 | 0 | Llama 3.2 Community |
| Parakeet v3 ATC (qenneth) | int8 | 60.8% | **71.3%** | 250 | 392 | 3.0 | 1,067 | 0 | MIT |
| Parakeet v3 ATC (qenneth) | fp32 | 35.7% | **45.9%** | 299 | 497 | 8.3 | 2,833 | 0 | MIT |
| Nemotron Streaming 0.6B (560 ms) | int8 | 42.5% | **50.9%** | 191 | 233 | 3.3 | 893 | 0 | NVIDIA Open Model |
| Nemotron Streaming 0.6B (560 ms) | fp32 | 42.1% | **49.2%** | 254 | 318 | 8.9 | 2,652 | 0 | NVIDIA Open Model |

### GPU

| Model | Precision | WER all 106 | WER owner-only 43 | p50 ms | p90 ms | load + warm-up s | peak RAM MB | peak VRAM MB | Licence |
|---|---|---|---|---|---|---|---|---|---|
| Whisper medium ATC (shipped) | ggml f16 | 39.3% | **50.5%** | 221 | 441 | 2.0 | 658 | 1,844 | MIT |
| Whisper large-v3 ATC | ggml f16 | 37.8% | **50.4%** | 488 | 1,516 | 6.8 | 695 | 3,332 | MIT |
| Whisper large-v3 | ggml f16 | 56.0% | **65.3%** | 950 | 4,327 | 3.0 | 693 | 3,332 | Apache-2.0 |
| Parakeet-TDT 0.6B v2 | fp32 | 46.8% | **53.8%** | 97 | 125 | 8.5 | 1,138 | 3,642 | CC-BY-4.0 |
| Parakeet-TDT 0.6B v3 | fp32 | 42.6% | **50.1%** | 95 | 126 | 8.3 | 1,140 | 3,694 | CC-BY-4.0 |
| Orukeet v0.1 | fp32 | 39.6% | **49.9%** | 99 | 140 | 8.1 | 1,142 | 3,724 | CC-BY-SA-4.0 |
| rasr-parakeet v1 (US ATC) | fp32 | 22.5% | **31.3%** | 93 | 132 | 8.5 | 1,140 | 3,698 | Llama 3.2 Community |
| rasr-parakeet v2 (US ATC) | fp32 | 22.9% | **31.3%** | 100 | 126 | 8.4 | 1,140 | 3,696 | Llama 3.2 Community |
| Parakeet v3 ATC (qenneth) | fp32 | 35.7% | **45.9%** | 97 | 135 | 8.4 | 1,140 | 3,698 | MIT |
| Nemotron Streaming 0.6B (560 ms) | fp32 | 42.1% | **49.2%** | 51 | 65 | 8.7 | 1,117 | 3,676 | NVIDIA Open Model |

### Cloud (golden baseline)

| Model | Precision | WER all 106 | WER owner-only 43 | p50 ms | p90 ms | load + warm-up s | peak RAM MB | peak VRAM MB | Licence |
|---|---|---|---|---|---|---|---|---|---|
| Gemini 4 Argon | n/a | 13.8% | **19.1%** | 2,956 | 5,719 | 0.0 | 193 | 0 | API |

### Findings

- **int8 costs the Parakeet family most of its accuracy.** On the owner-checked clips:

  | Model | int8 | fp32 |
  |---|---|---|
  | rasr-parakeet v2 | 44.5% | 31.3% |
  | qenneth ATC | 71.3% | 45.9% |
  | Parakeet v3 | 69.5% | 50.1% |
  | Orukeet | 65.0% | 49.9% |

  The precision changed these scores, not the device. fp32 on CPU and on GPU score within half a point of each other. Nemotron, whose int8 is the sherpa-onnx release export, loses little (50.9% to 49.2%). An accuracy-first user should get fp32, or a better-calibrated quantization than `quantize_dynamic`'s.
- **rasr-parakeet in fp32 is the best local model in both modes.** It scores 31.3% owner-only against the shipped Whisper's 50.5%.
  - On GPU: p50 100 ms, 3.7 GB VRAM.
  - On CPU: p50 324 ms with 2.8 GB RAM.

  Its gap to Gemini 4 Argon is about 12 points owner-only (31.3% against 19.1%) and 9 points on all clips (22.9% against 13.8%). v1 and v2 tie. Its weights are under the Llama 3.2 Community License, which carries more conditions than MIT or Apache.

  On sherpa-onnx 2.0.1 the same model file scores 30.0% owner-only and 22.5% on all clips by this note's scorer, and 29.6% and 22.1% by `--corpus-wer`, on CPU at p50 296 ms. 33 clips changed (14 better, 8 worse, 11 the same error count). YAAT's own native gives identical text to the stock 2.0.1 native on all 106 clips, so the change came with the upgrade; upstream #3999 (a log-zero fix for NeMo mel features) is the likely cause, not isolated by a build. The other sherpa rows here are on 1.13.8 and were not re-run.
- **The best MIT-licensed local model is the qenneth Parakeet v3 ATC fine-tune in fp32:** 45.9% owner-only, at the same speed as rasr.
- **Whisper is the slow family on CPU.** The shipped medium ATC takes p50 3.9 s per clip on CPU and 221 ms on GPU. large-v3 ATC is no more accurate (50.4–50.5%) at twice the latency, and stock large-v3 is worse on both.
- **Nemotron streaming has the shortest wait:** 51 ms after the last sample on GPU, 191 ms on CPU (int8). That is because it decodes while the user speaks, but its accuracy (49–51%) matches Whisper's, not rasr's.
- **The cloud is too slow for push-to-talk.** Gemini 4 Argon's p50 is 3.0 s, p90 5.7 s.
- **Memory:** a Parakeet-family model needs about 1 GB of RAM as int8, 2.8 GB as fp32 on CPU, and 1.1 GB of RAM plus 3.7 GB of VRAM as fp32 on GPU. Whisper medium needs 2.0 GB on CPU, or 0.7 GB plus 1.8 GB of VRAM on GPU.

The tables above are the record. The raw runs were scratch output in the feat/speech worktree's `.tmp/`, and are not kept.
