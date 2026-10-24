# Small TTS model benchmark (YAAT-1136)

Run 2026-10-24 09:00 UTC by `Yaat.SpeechSandbox --tts-bench`; every number below comes from `results.json` beside this file.

## Results

Full run: 20 pilot readback lines × each passing model's voices, speed 1.1 (the production `PilotVoiceSpeechRate` default), at the lowest thread count that passed the screen (the client runs 2). Load time is the model constructor in a cold process (the model files are in the OS file cache by then); one warm-up synthesis on the first voice is discarded. First audio is the time from the call to the first callback carrying samples ("not measured" when no callback fired); RTF is synthesis time over audio duration (below 1 is faster than real time). A cloning model's warm-up uses only its first voice's reference, so the first line of each later voice also carries the cost of encoding that voice's reference.

| Model | Download MB | Disk MB | Threads | Load s | First audio median ms | First audio p90 ms | Median RTF | Result |
|---|---|---|---|---|---|---|---|---|
| piper-libritts-r-fp32 | 78.2 | 92.1 | 2 | 0.73 | 143 | 269 | 0.042 | pass, 60 clips |
| piper-libritts-r-fp16 | 41.1 | 54.9 | n/a | n/a | n/a | n/a | n/a | not run: exit code -532462766, Unhandled exception. System.Runtime.InteropServices.SEHException (0x80004005): External component has thrown an exception. |
| piper-libritts-r-int8 | 22.3 | 38.6 | 2 | 1.51 | 634 | 1207 | 0.183 | pass, 60 clips |
| kitten-nano-0.8 | 60.9 | 74.4 | 2 | 0.80 | 455 | 687 | 0.097 | pass, 60 clips |
| kitten-micro-0.8 | 42.4 | 59.8 | 2 | 1.07 | 1158 | 2043 | 0.267 | marginal, 60 clips |
| kitten-mini-0.8 | 64.4 | 94.9 | 4 | 0.56 | 1614 | 2619 | 0.432 | marginal, 60 clips |
| matcha-ljspeech | 124.5 | 139.3 | 2 | 1.38 | 162 | 287 | 0.037 | pass, 20 clips |
| pocket | 160.4 | 455.4 | n/a | n/a | n/a | n/a | n/a | not run: exit code -532462766, D:\a\sherpa-onnx\sherpa-onnx\sherpa-onnx\csrc\offline-tts-model-config.cc:Validate:66 Please provide exactly one tts model. |
| pocket-int8 | 93.8 | 193.8 | n/a | n/a | n/a | n/a | n/a | not run: exit code -532462766, D:\a\sherpa-onnx\sherpa-onnx\sherpa-onnx\csrc\offline-tts-model-config.cc:Validate:66 Please provide exactly one tts model. |
| zipvoice-int8 | 155.8 | 195.6 | 4 | 1.23 | 1384 | 1842 | 0.467 | marginal, 60 clips |
| supertonic-3-int8 | 122.8 | 138.6 | n/a | n/a | n/a | n/a | n/a | not run: exit code -532462766, D:\a\sherpa-onnx\sherpa-onnx\sherpa-onnx\csrc\offline-tts-model-config.cc:Validate:66 Please provide exactly one tts model. |
| kitten-tts-2 | n/a | n/a | n/a | n/a | n/a | n/a | n/a | screened out at every thread count tried (8: screened out: p90 first audio 61730 ms, RTF 12.16; 2: screened out: first audio not measured, RTF 3.02) |

Kokoro (en v0.19, multi-lang 1.0 and 1.1-zh) left out by the owner: too few voices for a pilot pool.

The sherpa-onnx models ran on sherpa-onnx 1.13.8+dc5583f49917e4c95f6e7d862bb378e4ed5e9076 (the sandbox's package), each phase in its own child process. The client ships 1.12.40, so Piper timings here may differ from the client's, and adopting any model means a client bump.

## Screen

Each model speaks the screen lines `speed-210`, `cleared-for-takeoff`, `squawk-4521` with its first voice, after one warm-up, at 8, 4, 2 threads (each count a cold load in its own process). A count passes when its p90 first audio is 1500 ms or less and its median RTF is under 1.0 (so streamed playback never underruns); a pass with p90 first audio over 1000 ms is marginal. A model passes when any count does, and its full run uses the lowest passing count. Load over 30 s or first audio over 10 s on the first line screens a model out at once. A count that does not run, or RTF 3.0 or worse at 8 threads, skips the model's other counts.

Load time is a cold process each time, but only the first count tried reads the model from disk cold: the rows after 8 threads load with the model files already in the OS file cache.

First audio is what a streaming player would wait for. The shipped client calls `GenerateWithConfig` with no callback (`PilotVoiceService.cs`) and plays only the finished clip, so in today's app the pilot's delay is the **total synthesis** time.

| Model | Threads | Load s | First audio p50 ms | First audio p90 ms | Total synthesis p50 ms | Total synthesis p90 ms | Median RTF | Verdict |
|---|---|---|---|---|---|---|---|---|
| piper-libritts-r-fp32 | 8 | 0.73 | 75 | 83 | 75 | 83 | 0.027 | pass |
| piper-libritts-r-fp32 | 4 | 0.72 | 96 | 117 | 96 | 117 | 0.027 | pass |
| piper-libritts-r-fp32 | 2 | 0.71 | 119 | 145 | 119 | 145 | 0.041 | pass |
| piper-libritts-r-fp16 | 8 | n/a | n/a | n/a | n/a | n/a | n/a | not run: exit code -532462766, Unhandled exception. System.Runtime.InteropServices.SEHException (0x80004005): External component has thrown an exception. |
| piper-libritts-r-int8 | 8 | 1.76 | 616 | 879 | 616 | 879 | 0.211 | pass |
| piper-libritts-r-int8 | 4 | 1.59 | 613 | 694 | 613 | 694 | 0.170 | pass |
| piper-libritts-r-int8 | 2 | 1.49 | 538 | 669 | 538 | 669 | 0.185 | pass |
| kitten-nano-0.8 | 8 | 0.82 | 229 | 247 | 229 | 247 | 0.069 | pass |
| kitten-nano-0.8 | 4 | 0.79 | 237 | 249 | 237 | 249 | 0.072 | pass |
| kitten-nano-0.8 | 2 | 0.67 | 312 | 312 | 312 | 312 | 0.092 | pass |
| kitten-micro-0.8 | 8 | 0.80 | 1325 | 1470 | 1325 | 1470 | 0.395 | marginal |
| kitten-micro-0.8 | 4 | 1.36 | 971 | 1456 | 971 | 1456 | 0.300 | marginal |
| kitten-micro-0.8 | 2 | 1.00 | 961 | 1155 | 961 | 1155 | 0.287 | marginal |
| kitten-mini-0.8 | 8 | 0.74 | 1458 | 1555 | 1458 | 1555 | 0.503 | screened out: p90 first audio 1555 ms |
| kitten-mini-0.8 | 4 | 0.56 | 1278 | 1472 | 1278 | 1472 | 0.444 | marginal |
| kitten-mini-0.8 | 2 | 0.56 | 1423 | 1624 | 1423 | 1624 | 0.490 | screened out: p90 first audio 1624 ms |
| matcha-ljspeech | 8 | 1.71 | 119 | 126 | 119 | 126 | 0.030 | pass |
| matcha-ljspeech | 4 | 1.37 | 99 | 105 | 99 | 105 | 0.028 | pass |
| matcha-ljspeech | 2 | 1.36 | 129 | 150 | 129 | 150 | 0.036 | pass |
| pocket | 8 | n/a | n/a | n/a | n/a | n/a | n/a | not run: exit code -532462766, D:\a\sherpa-onnx\sherpa-onnx\sherpa-onnx\csrc\offline-tts-model-config.cc:Validate:66 Please provide exactly one tts model. |
| pocket-int8 | 8 | n/a | n/a | n/a | n/a | n/a | n/a | not run: exit code -532462766, D:\a\sherpa-onnx\sherpa-onnx\sherpa-onnx\csrc\offline-tts-model-config.cc:Validate:66 Please provide exactly one tts model. |
| zipvoice-int8 | 8 | 1.51 | 1094 | 1149 | 1094 | 1150 | 0.525 | marginal |
| zipvoice-int8 | 4 | 1.20 | 1286 | 1361 | 1286 | 1361 | 0.612 | marginal |
| zipvoice-int8 | 2 | 1.20 | 1702 | 1852 | 1702 | 1852 | 0.841 | screened out: p90 first audio 1852 ms |
| supertonic-3-int8 | 8 | n/a | n/a | n/a | n/a | n/a | n/a | not run: exit code -532462766, D:\a\sherpa-onnx\sherpa-onnx\sherpa-onnx\csrc\offline-tts-model-config.cc:Validate:66 Please provide exactly one tts model. |
| kitten-tts-2 | 8 | 4.99 | 55760 | 61730 | 55760 | 61750 | 12.161 | screened out: p90 first audio 61730 ms, RTF 12.16 (recorded, not re-run: the screen run's timings.json, voice Bruno) |
| kitten-tts-2 | 2 | n/a | not measured | not measured | 13840 | 16030 | 3.025 | screened out: first audio not measured, RTF 3.02 (recorded, not re-run: the stopped full smoke run, voice Bruno, first audio not logged; its llama.cpp log on the first load showed load 200.5 s (including the first-run weight download), prompt eval 9.4 tok/s, eval 5.7 tok/s, 223 s total for one line) |

## Models, voices and licences

- **piper-libritts-r-fp32** (Piper LibriTTS-R medium fp32, 22050 Hz): voices speaker-10, speaker-300, speaker-600. Licence: CC BY 4.0 (LibriTTS-R voices; Piper code MIT). Full run at 2 threads (pass).
- **piper-libritts-r-fp16** (Piper LibriTTS-R medium fp16): voices speaker-10, speaker-300, speaker-600. Licence: CC BY 4.0 (LibriTTS-R voices; Piper code MIT).
  not run: exit code -532462766, Unhandled exception. System.Runtime.InteropServices.SEHException (0x80004005): External component has thrown an exception.
- **piper-libritts-r-int8** (Piper LibriTTS-R medium int8, 22050 Hz): voices speaker-10, speaker-300, speaker-600. Licence: CC BY 4.0 (LibriTTS-R voices; Piper code MIT). Full run at 2 threads (pass).
- **kitten-nano-0.8** (Kitten nano 0.8 fp32, 24000 Hz): voices expr-voice-2-m, expr-voice-3-m, expr-voice-4-m. Licence: Apache-2.0 (LICENSE in the archive). Full run at 2 threads (pass).
- **kitten-micro-0.8** (Kitten micro 0.8, 24000 Hz): voices expr-voice-2-m, expr-voice-3-m, expr-voice-4-m. Licence: Apache-2.0 (LICENSE in the archive). Full run at 2 threads (marginal).
- **kitten-mini-0.8** (Kitten mini 0.8, 24000 Hz): voices expr-voice-2-m, expr-voice-3-m, expr-voice-4-m. Licence: Apache-2.0 (LICENSE in the archive). Full run at 4 threads (marginal).
- **matcha-ljspeech** (Matcha (icefall LJSpeech) + Vocos 22 kHz, 22050 Hz): voices ljspeech. Licence: No licence file in the archive (LJSpeech is public domain). Full run at 2 threads (pass).
- **pocket** (Pocket TTS fp32): voices clone-speaker-10, clone-speaker-300, clone-speaker-600. Licence: CC BY 4.0 (LICENSE in the archive).
  not run: exit code -532462766, D:\a\sherpa-onnx\sherpa-onnx\sherpa-onnx\csrc\offline-tts-model-config.cc:Validate:66 Please provide exactly one tts model.
- **pocket-int8** (Pocket TTS int8): voices clone-speaker-10, clone-speaker-300, clone-speaker-600. Licence: CC BY 4.0 (LICENSE in the archive).
  not run: exit code -532462766, D:\a\sherpa-onnx\sherpa-onnx\sherpa-onnx\csrc\offline-tts-model-config.cc:Validate:66 Please provide exactly one tts model.
- **zipvoice-int8** (ZipVoice distill int8 (zh-en Emilia) + Vocos 24 kHz, 24000 Hz): voices clone-speaker-10, clone-speaker-300, clone-speaker-600. Licence: No licence file in the archive (trained on Emilia, CC BY-NC 4.0). Full run at 4 threads (marginal).
  Voices cloned from Piper LibriTTS-R fp32 references (speakers 10, 300, 600) reading one fixed sentence.
- **supertonic-3-int8** (Supertonic 3 int8): voices sid-0, sid-1, sid-2. Licence: MIT (LICENSE in the archive).
  not run: exit code -532462766, D:\a\sherpa-onnx\sherpa-onnx\sherpa-onnx\csrc\offline-tts-model-config.cc:Validate:66 Please provide exactly one tts model.
- **kitten-tts-2** (Kitten-TTS 2 (kitten_tts_cpp, student_w4 decoder)): voices Bruno, Jasper, Hugo. Licence: Stellon Labs Community License.
  screened out at every thread count tried (8: screened out: p90 first audio 61730 ms, RTF 12.16; 2: screened out: first audio not measured, RTF 3.02)

## Machine

- CPU: AMD Ryzen 7 7800X3D 8-Core Processor, 16 logical cores
- OS: Microsoft Windows 10.0.26200
- Gate slot: not recorded (the run cannot see it; docs/plans/canvases/tts-benchmark/RECIPE.md gives the alone invocation)
- A shared development PC: timings carry its background load and are comparable within this run, not across machines.

## Readback lines

1. `CM 11000` (AAL1234): "climb and maintain one one thousand, american twelve thirty four."
2. `FH 270` (N512RK): "fly heading two seven zero, november five one two romeo kilo."
3. `SPD 210` (UAL889): "maintain two ten knots, united eight eighty nine."
4. `TL 240, DM 4000, SPD 190` (SWA2117): "turn left heading two four zero, descend and maintain four thousand, maintain one ninety knots, southwest twenty one seventeen."
5. `TAXI B A1 HS 28R` (N738PJ): "taxi via bravo, alpha one, hold short of runway two eight right, november seven three eight papa juliett."
6. `TAXI F C HS 19L` (DAL455): "taxi via foxtrot, charlie, hold short of runway one nine left, delta four fifty five."
7. `TAXI A CROSS 28L HS 28R` (JBU612): "taxi via alpha, cross runway two eight left, hold short of runway two eight right, jetblue six twelve."
8. `CTO` (SKW5432): "cleared for takeoff, skywest fifty four thirty two."
9. `CLAND` (N4417Q): "cleared to land, november four four one seven quebec."
10. `CVA 28R` (ASA319): "cleared visual approach runway two eight right, alaska three nineteen."
11. `CAPP ILS28L` (UAL1572): "cleared approach, united fifteen seventy two."
12. `CT` (FDX1408): "NorCal Approach on one two five point three five, fedex fourteen zero eight, so long."
13. `FCA` (N81TW): "november eight one tango whiskey, good day."
14. `SQ 4521` (N2093M): "squawk four five two one, november two zero nine three mike."
15. `IDENT` (EJA622): "squawk ident, execjet six twenty two."
16. `DCT SUNOL` (AAL2290): "proceed direct to sunol, american twenty two ninety."
17. `HFIX OAK` (N9246C): "hold at Oakland VOR, november nine two four six charlie."
18. `FH 300, CM 7000, SPD 230` (DAL1938): "fly heading three zero zero, climb and maintain seven thousand, maintain two thirty knots, delta nineteen thirty eight."
19. `DCT MOD, DM 9000` (JBU1127): "proceed direct to Modesto VOR, descend and maintain nine thousand, jetblue eleven twenty seven."
20. `SQ 2671, CM 5000` (N716SF): "squawk two six seven one, climb and maintain five thousand, november seven one six sierra foxtrot."

The listening page is `docs/plans/canvases/tts-benchmark/` (its RECIPE.md rebuilds the clips).
