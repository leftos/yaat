# YAAT Speech-Recognition Pipeline

Reference for how push-to-talk (PTT) speech recognition works in YAAT today —
the controller-side STT input pipeline. Read this before changing anything under
`src/Yaat.Sim/Speech/`, `src/Yaat.Client/Services/Speech/` (the `ISttEngine` seam and its engines),
`src/Yaat.Client/Services/WhisperSttEngine.cs`, `src/Yaat.Client/Services/LocalLlm*`,
`src/Yaat.Client/Services/SpeechRecognitionService.cs`, or
`tools/Yaat.SpeechSandbox/`.

For the solo-training pilot TTS output pipeline, see
[`solo-training-pilot-speech.md`](solo-training-pilot-speech.md). The two pipelines
share nothing beyond the radio metaphor.

The historical implementation plan (LLamaSharp+Whisper.net era) was removed
with the other outdated plans; the LM-Kit swap rationale lives in the git
history of `1234ca45` ("swap speech engine to LM-Kit.NET"). This document
describes the **current** pipeline.

## Overview

User holds the PTT key. Audio is captured as 16 kHz mono float32, the
preferred STT engine (a rasr model on sherpa-onnx by default, or Whisper via
LM-Kit with a statically-built biasing prompt, the legacy engine; see STT Stage) transcribes it,
spoken numbers are normalized to digits, a rule-based phraseology mapper
tries first, and an LLM fallback (also via LM-Kit, grammar-constrained to
the canonical command surface) runs if no rule matches. The final
canonical command string is placed in the command input box — the user
reviews it and presses Enter to dispatch. Nothing auto-sends.

```
PTT key down
  └─ AudioCaptureService (16 kHz mono float32)
      └─ ISttEngine.TranscribeAsync (SwappableSttEngine → the engine SttEngineFactory built)
          ├─ rasr model: sherpa-onnx, greedy (Fast) or beam search (Accurate)
          └─ Whisper: LMKit.Speech.SpeechToText + LM.LoadFromModelID(...)
              └─ Prompt = WhisperBiasingPrompt.Default
                          + SpeechContext.WhisperInitialPrompt
                            (scenario callsigns + programmed fixes)
      └─ raw transcript
          └─ AtcNumberParser.NormalizeDigits
              (spoken → digit form + zero-pad single-digit runways
               + phonetic suffix collapse "10 tight" → "10R")
              └─ PhraseologyCommandMapper (rule engine)
                  ├─ {rwy} fuzzy snap via TryRecoverRunway
                  │  ("288" → "28R", "100" → "10L")
                  ├─ match → MapResult
                  └─ no-match (or runway snap failed)
                     → LocalLlmCommandMapper
                       (grammar-constrained LLM fallback,
                        sees AvailableRunways +
                        AircraftDestinations in user prompt)
          └─ SpeechRecognitionService.CommandReady event
              └─ MainViewModel pushes canonical text into CommandText
```

All NLU logic lives in **Yaat.Sim** (no UI deps, fully testable).
Audio capture, STT, and LLM engines live in **Yaat.Client** because they
depend on LM-Kit and PortAudio native libraries.

## Audio Capture & PTT

- `src/Yaat.Client/Services/AudioCaptureService.cs` — PortAudioSharp2
  capture stream. 16 kHz mono float32. Samples are buffered in memory
  while PTT is held, delivered as a single `float[]` when released.
- `src/Yaat.Client/Services/GlobalKeyHookService.cs` — system-wide key
  hook via SharpHook / libuiohook. Runs on a background thread, passes
  through native key events (non-blocking). Converts to Avalonia
  `Key` + `KeyModifiers`.
- `src/Yaat.Client/Views/MainWindow.axaml.cs` subscribes to the global
  key hook; it also has fallback window-local `OnKeyDown/OnKeyUp`
  handlers for platforms where libuiohook fails to attach.
- `UserPreferences.SpeechEnabled` gates the whole pipeline —
  `SpeechRecognitionService.StartPtt` early-returns false when speech is
  disabled.
- **Prewarm:** `SpeechRecognitionService.PrewarmAsync` is called at
  startup so first PTT press doesn't stall on the multi-second model
  load. Fires `StatusChanged(Warming)` on entry and returns to `Idle`
  unless a user PTT raced it to another state. It first runs the caller's
  `prepareEngine` (a failure is logged and the engine in place warms up),
  and skips the STT warm-up when the chosen model is not downloaded. The boot prewarm runs only when `SpeechEnabled` is on.
- **Warm after Settings Apply:** changing the LLM path, the LLM GPU layers, or the Whisper size while Whisper is the engine warms the models after Apply with no engine swap (`SettingsViewModel.LastApplyChangedSpeechModels`, `MainViewModel.WarmSpeechModelsAsync`). It goes through a second `SttEngineSwapScheduler` whose swap does nothing, so it waits for the mic to rest and runs under `Warming`; with speech off or a benchmark running it loads nothing.
- **Warm-up entry points:** the warm-up itself is `SpeechRecognitionService.WarmAsync`, entered with the status already `Warming`. `PrewarmAsync` sets `Warming` and calls it; `TryBeginWarming` is the other way in, moving the status to `Warming` only from `Idle`, `ModelMissing` or `Error`, atomically with the check, and returning false while the mic is busy. `SttEngineSwapScheduler.RequestAsync` always runs on the thread pool, since a warm can block for a whole native load. A swap queued while the mic is busy that finds speech off when it runs does an unloaded swap (`SttEngineSwapScheduler.ChooseBySpeechSetting`).

## STT Stage

Speech-to-text sits behind `ISttEngine` (`src/Yaat.Client/Services/Speech/ISttEngine.cs`): `WarmUpAsync` loads the model, `TranscribeAsync` turns 16 kHz mono samples into `SttHypotheses` (best first), and `Mode` says the mode the loaded engine runs in. `SpeechRecognitionService` transcribes through whichever engine `SwappableSttEngine` currently holds.

The default is rasr-parakeet through sherpa-onnx (`SherpaClientSttEngine` over `SherpaSttEngine`). Whisper on LM-Kit (`WhisperClientSttEngine` over `WhisperSttEngine`) is the legacy engine, described under "Legacy Whisper engine" below.

- **Which engine the live client builds:** `src/Yaat.Client/Services/Speech/SttEngineFactory.cs` makes the `ISttEngine` from `UserPreferences.SttEngine` (default the rasr v2 fp32 model on sherpa-onnx; `Whisper` keeps the LM-Kit engine, see "Legacy Whisper engine") and `SttMode` (default `Accurate`). `Create` makes no native call: a rasr model decodes greedily in `Fast` and with beam search (4 paths, hotword score 1.5) in `Accurate`. `CreateCheckedAsync` probes an accurate rasr model by decoding on the beam engine it built, and falls back to a greedy engine when this native cannot run the beam; a model that fails to load throws. `IsModelReady` says whether the chosen model's files are on disk (Whisper is always ready).
- **The swap:** `MainViewModel` wraps the engine in `SwappableSttEngine`, so a model or mode change takes effect without a restart (`MainViewModel.ApplySttEngineAsync`). One gate serializes warm-up, transcribe and swap; a PTT mid-swap waits and then runs on the new engine. The new engine is warmed before it replaces the old one, so a rasr model that fails to load fails the swap and the old engine stays. When requests overlap the latest wins, and the replaced engine is disposed off the caller's thread.
- **A swap never interrupts a push-to-talk:** `SttEngineSwapScheduler` (`MainViewModel.SwapSttEngineAsync`) runs a swap under `Warming` only while the mic rests (`Idle`, `ModelMissing` or `Error`) and enters `Warming` through `SpeechRecognitionService.TryBeginWarming`. A swap requested while the mic records, transcribes or maps waits for the next resting status, and requests made meanwhile share one pending swap that reads the latest preferences when it runs. A model that finishes downloading swaps in at once when it is the saved engine's.
- **Startup in Accurate mode:** with the model downloaded, the startup engine decodes greedily and the first prewarm runs the beam probe and swap under `Warming` (the `prepareEngine` argument of `PrewarmAsync`), so startup stays free of native calls.
- **With speech off a swap loads no model:** a swap from a benchmark's rebuild, Settings OK, a benchmark's Apply or a finished download goes through `SwappableSttEngine.SwapToLatestUnwarmedAsync` and installs an engine built without a native call (`MainViewModel.CreateUnloadedSttEngine`, the builder startup uses), so nothing loads while the mic is off. An Accurate rasr model that is downloaded decodes greedily until the first beam probe, and that probe and the warm-up happen at the next prewarm, when speech is turned on.
- **Model not downloaded:** a PTT whose chosen rasr model is not on disk is refused with `SpeechStatus.ModelMissing` (the mic indicator shows "mic: ModelMissing"); prewarm skips the STT warm-up then. `ModelMissing` is start-legal, so a press after the download records. While speech is on and the status is `ModelMissing`, `MainViewModel.IsSpeechModelMissing` shows the status bar's `SpeechModelMissingLink` ("Download the speech model in Settings > Speech"), which opens Settings at Speech; any other status or speech off hides it.
- **Sherpa model install:** the client reads `src/Yaat.Client/Speech/stt-downloads.json`'s `models[]` through `SttModelInstaller` (`ReadModels`/`ReadModel`), which downloads and SHA-256-verifies a model's files into `%LOCALAPPDATA%/yaat/models/stt/<id>/`. The files are built in a staging folder with the `.installed` sentinel written last, then swapped in, so a failed or cancelled install leaves an installed model as it was; one writer per root via `models/stt.lock`. Downloads go through `VerifiedFileDownloader`, shared with `SherpaCudaInstaller`, so download log lines log under that category. `SttDownloads` (`MainViewModel.SttDownloads`) owns one installer per model, the CUDA installer, the GPU and n-best checks and whether an engine swap is loading for the life of the app, so a download keeps running after Settings closes; a download refused because another writer holds the root (`SttModelInstaller.IsRefusal`) shows `Another model is downloading; try again when it finishes.` on its card in place of the size line; Settings' model cards (`SttModelCardViewModel`) read it through `UiThreadMirror`.
- **GPU runtime support, removal and Accurate availability (services behind Settings > Speech):** `CudaDriverProbe.ProbeAsync` decides whether GPU speech can run, through the CUDA driver API only (`nvcuda.dll` / `libcuda.so.1` loaded by name, so no toolkit download): `cuInit`, then `cuDriverGetVersion` must report 13000 or more (driver R580), then CUDA device 0 (the one ONNX Runtime's CUDA provider uses) must be compute capability 7.5 or newer. It returns a `GpuSupport` (device name, compute, driver CUDA version, `IsSupported`, and the `Reason` sentence the Device row shows); macOS and non-x64 never touch the driver, a missing library or export reads as no driver, and the probe never faults. `ICudaDriverApi` is the seam tests fake.
- **Scheduled CUDA-set removal:** the CUDA set cannot be deleted while this process has it loaded, so `SherpaCudaInstaller.ScheduleRemovalOnNextStart` writes a `remove-pending` marker (the UTC time) in the install root under the root's lock and removes nothing; the installer's `IsRemovalScheduled` and status text show it. `Program.ConfigureSherpaNatives` calls `SherpaCudaInstaller.ProcessPendingRemoval` first, before any sherpa native loads: it deletes the set and then the marker under the writer lock, and returns false when it could not run (the lock is held, on Windows a process has the set loaded, or a file would not delete), leaving the marker so the next start retries. While a removal is pending the process stays on the CPU set, and a GPU preference records a `CpuFallbackReason` saying so.
- **Orphaned CUDA-set recovery:** `native/` carries its own `.installed` copy (one `{Name} {Sha256}` line per download), written into the staged set before it is moved in (on Linux after the chmod), and a pre-existing set without one gets it backfilled before it is moved aside; the root sentinel stays the only judge of "installed". A crash mid-swap can leave the old set in `.old-*` with `native/` missing, and `SherpaCudaInstaller.RecoverOrphanedSet(installRoot)` (public, takes the root lock and skips when it is held) moves it back when exactly one `.old-*` carries its own sentinel, then rewrites the root sentinel from it, last. It runs at startup in `Program.ConfigureAppSherpaNatives` after a completed pending removal and before the set is chosen, in the installer's constructor, and at the start of every install before the leftover sweep. With none or more than one candidate nothing is restored and a warning says why. Uninstall (`DeleteRoot`) deletes every set sentinel before any folder, so a failed folder delete cannot bring a removed set back; a successful swap deletes the old set's sentinel before the old folder, clears a pending removal, then sweeps leftovers.
- **Silent GPU fallback at model load:** with sherpa 2.0.x a CUDA failure at recognizer creation (a missing provider DLL, say) does not fail the call: ONNX Runtime falls back to the CPU and `SherpaSttEngine.EnsureLoaded` still returns a recognizer, so `ConfigureSherpaNatives` and `CudaDriverProbe` cannot see it.

  `NativeStderrCapture.Install` runs first in `Program`, before any sherpa native loads (sherpa's statically linked C runtime binds stderr when its DLL loads). It redirects native stderr into the client log, one entry per line under the `NativeStderr` logger, and copies it back to the original stderr on Linux and on Windows when the process has a stderr handle (a WinExe started from Explorer has none). macOS is not captured. The log says at startup whether stderr is captured and copied, that it is not captured on this platform, or warns that the capture could not be set up (a fallback's native reason is then not logged).

  Sherpa's `Failed to enable CUDA: {reason}. ... Fallback to cpu` line prints as two stderr lines (ORT's message ends in a newline) and arrives while the provider is set up, seconds before the recognizer is returned. Detection is per load and for the `cuda` provider only: `EnsureLoaded` reads `INativeFallbackSource.Latest` before and after `CreateRecognizer`, and a newer line is the fallback, with the reason parsed from it; otherwise, on Windows, this process's GPU memory (`GpuMemoryProbe`) rising less than `GpuUseCheck.MinRiseBytes` is a fallback with the reason `SherpaSttEngine.NoGpuMemoryRiseReason`.

  A fallback sets `SherpaNativeLoader.CpuFallbackReason` and logs the Warning "Speech recognition runs on the CPU: {reason}"; a later GPU load clears it (`RecordGpuLoad`, logging "Speech recognition runs on the GPU again"). `CpuFallbackReasonChanged` lets the Settings > Speech Device row and the Speech Debug header show the reason live.
- **Accurate availability:** `SherpaNativeLoader.NbestAvailability()` says whether the loaded native can run Accurate (n-best) mode without loading a model: false, with the reason "Accurate needs the n-best native; running Fast", when the C API folder (`CApiDirectory`, the CUDA set's folder once loaded) is missing or holds the `sherpa-native-FALLBACK.txt` marker the build copies when it kept NuGet's stock native. The CUDA set is always our own build. `WhisperAccurateReason` is the matching text for the Whisper card. `SttModelCardViewModel.AccurateUnavailableReason(engine, nbest)` is the one rule for "Accurate cannot run" (Whisper, else the n-best check), and `EffectiveMode(saved, reason)` is the mode that runs: Settings disables the Accurate radio on the reason, and Speech Debug names the effective mode with it.
- **UI-thread mirror and size text:** the installers raise `PropertyChanged` on their worker thread, so Settings binds to a `UiThreadMirror` (`UiThreadMirror.Of(installer, post)` for `SttModelInstaller` or `SherpaCudaInstaller`) instead: each change only posts a callback that re-reads the installer into the mirror's `IsInstalled`, `IsBusy`, `Progress` and `StatusMessage`, so the view's values change only on the UI thread; a disposed mirror writes nothing. `SttSizeText` formats sizes for the same page in decimal gigabytes with the invariant culture: `Total` (`2.55 GB`), `Progress` (`1.1 of 2.55 GB`, never showing the total before the download is done) and `Ready` (`Ready (2.55 GB on disk)`).

### Legacy Whisper engine

Selecting `Whisper` in `UserPreferences.SttEngine` keeps the LM-Kit engine. It is the only engine that takes the biasing prompt (see "Biasing Prompt (Whisper, legacy)"), always reads as ready (`IsModelReady`) and never runs Accurate.

- `src/Yaat.Client/Services/WhisperSttEngine.cs` wraps
  `LMKit.Speech.SpeechToText`. The Whisper `LM` is lazy-loaded on the
  first `TranscribeAsync` call using the model ID from
  `UserPreferences.WhisperModelSize` (e.g. `whisper-base`,
  `whisper-medium`). Subsequent calls reuse the cached `LM` and
  `SpeechToText`; only `SpeechToText.Prompt` is rewritten per-call.
- Samples are wrapped in an in-memory WAV container via
  `WavHeader.WritePcm16` before being handed to
  `new LMKit.Media.Audio.WaveFile(byte[])`.
- `EnableVoiceActivityDetection = false` — PTT is explicitly
  single-shot; the engine transcribes exactly the captured buffer.
- A `SemaphoreSlim(1,1)` serializes transcribe calls so a rapid second
  PTT press can't race the cached engine state.
- Returns `null` on: empty input, missing model ID, LM-Kit load failure,
  empty transcript, or noise-marker output. The orchestrator treats
  `null` as `SpeechSessionOutcome.EmptyTranscript`.
- **Model selection:** `whisper-base` is adequate for quick dev loops.
  The Whisper default is **jacktol's ATC-domain Whisper-medium
  fine-tune** (ggml conversion by borisdiakur, MIT; loaded via the HF
  resolve URL in `LmKitModelCatalog.RecommendedWhisperId` — the engine's
  URI path downloads it into LM-Kit's cache on first use, ~1.5 GB).
  Chosen over `whisper-large-turbo3` by the `--eval` corpus A/B:
  mean WER 8.3% vs 18.1% on real mic captures (29/30 vs
  15/30 on the synthetic controller corpus), warm STT ~230 ms vs
  ~370 ms on an RTX 4090, and it natively resolves ATC-cadence mishears
  ("descendant maintain", "to 7-0") the generic model needed the
  deterministic recovery passes for. `whisper-large-turbo3` is
  **delisted from the picker** (the old default would invite
  regressions); a saved preference that still names it keeps loading
  through the curated-ID path, it just shows as a custom source.
- **Licensing:** every process funnels through
  `src/Yaat.Client/Services/LmKitLicense.Initialize()` exactly once
  before touching LM-Kit. Lookup order: `LMKIT_LICENSE_KEY` env var →
  `.env` line walked up from `AppContext.BaseDirectory` →
  `[assembly: AssemblyMetadata("LmKitLicenseKey", ...)]` baked into
  `Yaat.Client.dll` by MSBuild → empty (Community). Release builds
  inject the key via the `LmKitLicenseKey` MSBuild property in
  `release.yml`.

## Biasing Prompt (Whisper, legacy)

Only the legacy Whisper engine takes a prompt; the rasr engines ignore it.

- `src/Yaat.Sim/Speech/WhisperBiasingPrompt.cs` — **static** prompt
  built once and cached, in truncation-aware block order:
  1. Every distinct literal pattern token from `PhraseologyRules.All`
     (alphabetized) — so every word the rule engine knows how to map is
     primed into Whisper.
  2. Phonetic numbers + ATC variants (`tree`, `fife`, `niner`), magnitude
     words (`hundred`, `thousand`, `flight`, `level`, …), and digit-form
     bias examples (`090`, `270`, `FL350`, …).
  3. Full NATO phonetic alphabet (`alpha` … `zulu`) in scrambled order.
- **The prompt exceeds whisper.cpp's 224-token initial-prompt cap**
  (~284 GPT-2-BPE tokens as of 2026-07). whisper.cpp keeps the LAST
  ~224 tokens, so block order is load-bearing: the rule-literal head is
  sacrificial (common English Whisper knows unbiased); the number/digit
  and NATO blocks sit at the end where they survive. A test pins the
  ordering (`Default_NumberVocabularyComesAfterRuleLiterals`).
- **Re-validated against the ATC-fine-tune default** via
  `--eval --prompt none`: with the prompt, 30/30 synthetic + 2/2 real
  canonical-exact; without it, 29/30 — the miss hallucinated an extra
  `DCT LAS` clause, the dangerous failure mode. Keep the prompt. Noted
  for later: on the (tiny) real corpus the prompt-free transcripts had
  lower WER (4.2% vs 8.3%) with canonicals unaffected — re-run the A/B
  when the real corpus grows.
- The prompt is deliberately **static**. Per-PTT dynamic additions were
  probed and abandoned under the previous default, which recognized
  N-number tail numbers cleanly without per-callsign biasing. **That
  finding has not been re-validated against the ATC fine-tune**, which
  mangles spoken NATO suffix letters often enough to need
  `CallsignParser`'s unique-extension snap (see below). Per-callsign
  biasing is therefore worth re-probing — but measure it, don't assume
  it: the 224-token tail-keeping behavior means anything appended
  displaces the number/NATO blocks that currently survive the cap.
  `MainViewModel.BuildSpeechContext` sets
  `SpeechContext.WhisperInitialPrompt` to `WhisperBiasingPrompt.Default`
  verbatim — no scenario vocabulary is appended. If scenario-derived
  vocab ever becomes necessary, deliver it via `SpeechContext` (and mind
  the 224-token tail-keeping behavior), **not** by rebuilding
  `WhisperBiasingPrompt` per PTT.

## Digit Normalization & Filler Strip

- `src/Yaat.Sim/Speech/AtcNumberParser.cs` —
  - `NormalizeDigits(transcript)` collapses spoken number forms to
    digit strings: `"two eight right"` → `"28R"`,
    `"flight level three five zero"` → `"FL350"`,
    `"five thousand"` → `"5000"`. Handles `niner/tree/fife` — the parser
    accepts both `nine` and `niner` for digit 9, so pilot latitude on the
    air is normalized away. The runway
    suffix collapse runs as a post-pass via
    `PhraseologyMapper.CollapseRunwayDesignators` so both the rule engine
    and the LLM fallback see the canonical `"28R"` form.
  - `FlightNumberToWords` / `AltitudeToWords` are the reverse direction,
    used to seed the Whisper `initial_prompt` in natural-English forms
    pilots actually speak.
- `PhraseologyMapper.CollapseRunwayDesignators` does two extra things
  beyond the obvious `"28 right"` → `"28R"` collapse:
  - **Zero-pads single-digit runway numbers.** `"one right"` →
    `["1", "right"]` → `"01R"`. Real-world designators are always two
    digits and the airport's runway list uses the padded form, so the
    `{rwy}` capture has to match it.
  - **Phonetic suffix matching for Whisper mishears.**
    `MatchRunwaySuffixWord` does an `EndsWith` check on the post-digit
    token: anything ending in `"ight"` (right/tight/light/sight/might/
    bright) or `"ite"` (kite/bite/rite/mite/site/white) → `R`; anything
    ending in `"eft"` (left/deft/weft/cleft) → `L`. Only fires when the
    previous token is a digit string, so collision risk with non-runway
    use of these words is negligible.
- Filler handling happens inside
  `src/Yaat.Sim/Speech/PhraseologyMapper.cs`:
  - `FillerWords` — hard strip (`uh`, `um`, `please`, `sir`, `thanks`, …).
  - `SecondPassFillers` — words like `"for"` that some rules legitimately
    use as literals. The mapper tries a first pass keeping them, then
    a second pass stripping them if the first didn't match. This keeps
    `cleared for takeoff` working while also letting
    `enter right downwind FOR runway 28R` resolve.
  - `CompoundConnectors` — `and`/`then`/`also` skipped between clauses
    so compound commands chain cleanly.

## Phraseology Rule Mapper

- `src/Yaat.Sim/Speech/PhraseologyRules.cs` — single static catalog of
  rules grouped into category builders: `HeadingRules`,
  `AltitudeSpeedRules`, `NavigationRules`, `TowerRules`, `ApproachRules`,
  `PtacRules`, `PatternRules`, `HoldRules`, `HelicopterRules`,
  `TransponderRules`, `GroundRules`, `BroadcastRules`. `Build()`
  concatenates them into `PhraseologyRules.All`. Each category mirrors
  the grouping in `CommandRegistry.*Commands`.
- Rules are `PhraseologyRule` records:
  `(tokens, canonical-template, canonical-command-type)`. Tokens are
  literal strings (`"cleared"`), captures (`"{rwy}"`, `"{fix}"`,
  `"{callsign}"`, `"{taxiway}"`, …), optional literals (`"of?"`,
  `"the?"`), or **variadic captures** (`"{path...}"`). A variadic
  consumes one-or-more consecutive transcript tokens into a single
  space-joined capture. In the last pattern position it greedy-consumes
  the remainder; mid-pattern it walks to the next required literal and
  captures up to (but not including) that literal. Optional or capture
  tokens *cannot* follow a variadic in the same rule — `TryMatchPattern`
  throws at match time if you wire one by mistake. Minimum variadic
  consumption is 1 token.
- `src/Yaat.Sim/Speech/PhraseologyMapper.cs` — `Map(transcript, context)` tokenizes, strips fillers, extracts the callsign via `CallsignParser` (whose trailing parse never takes a traffic callsign after a follow / behind / give-way cue as the addressed aircraft), and collapses a spoken traffic callsign after such a cue into one ICAO token via `TrafficCallsignNormalizer` (kept only when that callsign is on frequency).

  It then extracts a condition prefix (`AT {fix}`, `when level at {alt}` → `LV {alt}`), collapses NATO phonetic runs via `NatoLetterNormalizer` (see below), then does a greedy left-to-right longest-match against `PhraseologyRules.All`.

  A `{callsign}` or `{rwy}` capture with no digit is no match for that rule. Multiple clause matches are concatenated with commas. Tie-break rule: **more literal tokens wins at equal consumed count** — so a more-specific rule always beats a shorter one. For two rules of equal pattern length this reduces to "fewer captures wins" (e.g. `squawk vfr → SQVFR` beats `squawk {code} → SQ vfr`).

  The mapper skips the three tokens "i say again" like a connector (the controller repeating itself, 7110.65 PCG), so they emit nothing and the instruction after them still matches. The say-again family in `BroadcastRules` is all `SttOnly` (pilots never speak it): "say again" → `AGAIN`, "say again your call sign" → `AGAIN CS`, "aircraft/station calling [facility…] say again [your call sign]" → `AGAINU` / `AGAINU CS` (the facility is a mid-pattern variadic), and "say again all after/before {rest…}" → `AGAIN` with the tail swallowed by a rest-of-line variadic so it is never issued as a command. A spoken call sign is extracted before matching, so it keeps the line addressed. "repeat" and bare "again" map to nothing.

  Returns `null` if no rule matched any part of the transcript. Because the digit and NATO passes run before matching, a spoken literal they rewrite ("three sixty", "bravo") needs an `SttOnly` twin in the rewritten form; the spoken rule stays for `PhraseologyVerbalizer`.
- `src/Yaat.Sim/Speech/NatoLetterNormalizer.cs` — after callsign +
  condition extraction, walks the token list and collapses runs of NATO
  phonetic words (`"tango uniform whiskey"`) into taxiway-name tokens
  (`"T"`, `"U"`, `"W"`). When the airport's taxiway set (from
  `MapContext.TaxiwayNames`) contains a multi-letter name that the run
  could form, the longest such name wins: `"tango echo"` → `"TE"` if
  `TE` is a real taxiway, otherwise `"T E"`. An airport won't name a
  taxiway "TE" if T connects to E precisely *because* of this ambiguity,
  so consulting the set resolves it correctly in every real airport.
  Alphanumeric names (`B6`, `A13`) are deferred — they collide with
  `AtcNumberParser`'s digit normalization pass.
- **Cardinal heading post-pass.** `PhraseologyMapper.TryMatchRule`
  rewrites any `{cardinal}` capture to its 3-digit magnetic heading
  via `AtcNumberParser.TryResolveCardinalHeading` (`north` → `360`,
  `south` → `180`, `east` → `090`, `west` → `270`). Intercardinals
  (`northeast` etc.) fail the post-pass, causing the rule to fall
  through so the LLM fallback can handle it.
- `src/Yaat.Sim/Speech/PhraseologyCommandMapper.cs` — thin
  `ISpeechCommandMapper` adapter (line 10) so the mapper can sit behind
  the same interface as the LLM fallback and the service can hold a
  list of them.
- `src/Yaat.Sim/Speech/PhoneticFixMatcher.cs` — post-processes any
  `{fix}` / `{current}` captures. Whisper tends to mangle fix names
  into English words (e.g. `CEPIN` → `"sea pin"`), and the matcher
  recovers them against the per-aircraft programmed-fix list in
  `MapContext.ProgrammedFixes`. `FixLikeCaptureNames` in
  `PhraseologyMapper` flags which captures pass through here.
- **Runway capture validation + fuzzy recovery.**
  `PhraseologyMapper.TryMatchRule` runs a parallel post-pass for
  `{rwy}` captures (`RunwayLikeCaptureNames`). When a captured runway
  isn't in `MapContext.AvailableRunways`, `TryRecoverRunway` attempts
  deterministic snap-back before failing the rule:
  - 3-digit `NNX` → `NN` + suffix from the trailing digit. Trailing
    `8` → `R` is the load-bearing case (Whisper transcribes `"two eight
    right"` as `"two eight eight"` → `"288"` because *right* and *eight*
    share the `/aɪt/` ~ `/eɪt/` rime). Trailing `0` → `L` is a weaker
    fallback used only when an L-suffixed runway exists at that base.
    Other trailing digits have no phonetic mapping.
  - Single digit + suffix letter (`"9R"`) → `"09R"` (defence-in-depth
    padding for cases `CollapseRunwayDesignators` missed).
  - Bare single digit (`"9"`) → `"09"`.
  - Bare base fallback (`"300"` → `"30"`) for suffix-less runways.

  When recovery succeeds, the capture is replaced with the recovered
  value before template-filling. When recovery fails, the rule mapper
  sets a `runwayInvalid` flag that escalates all the way to `Map`,
  causing it to return `null` even if a shorter runway-less rule would
  have matched — the LLM fallback then owns recovery for cases the
  deterministic rules can't handle. Without this escalation, a longer
  rule like `enter right downwind runway {rwy}` getting rejected by
  validation would silently fall back to the shorter `enter right
  downwind` rule, stripping the user's runway mention.

  The flag is scoped per matching pass: whenever the filler-stripping
  pass 2 runs, its flag is authoritative (win or lose), because pass 1
  routinely captures the filler itself as the runway (`"cross runway
  for two eight right"` → `{rwy}` = `"for"`) and that artifact must not
  null out a pass 2 that resolved the real runway.

  Skipped entirely when `MapContext.AvailableRunways` is empty — every
  existing test using `MapContext.Empty` continues to pass-through any
  `{rwy}` capture as-is.
- `src/Yaat.Sim/Speech/CallsignParser.cs` — bidirectional spoken ↔ ICAO
  callsign conversion. Uses `AirlineTelephony` (~2000-entry map built
  from OpenFlights airlines.dat) and `AircraftTypeNames`. Fuzzy-matches
  against `MapContext.ActiveCallsigns` to recover from near-miss
  transcriptions.
- **Unique-extension snap** (`FuzzyResolve`): an exact active match wins
  outright; otherwise, when exactly one active callsign extends the
  parsed candidate by 1-2 trailing **letters**, that callsign is
  returned. This recovers GA tails whose NATO suffix Whisper mangled
  past `NatoNearMissResolver`'s distance-1 envelope — "november 9225
  lima" heard as "november 9225 lien" parses to `N9225`, and the unique
  active `N9225L` is what the controller meant. Letter-only extensions
  keep airline flight numbers out of the snap: a digit extension would
  mean the number itself was misheard, which is too risky to guess. Two
  or more candidate extensions are ambiguous and keep the heard base.
  On a leading callsign the mangled token is deliberately **not** consumed: it stays in the command text, where the rule matcher's no-match advance skips it, rather than risking eating a real command word. On a trailing callsign (`TryParseTrailing`, and `TrafficCallsignNormalizer.TrafficTailStart`) the parse must reach the end of the transcript, so `CallsignParser.TryReachEnd` accepts up to `ParsedCallsign.SnapAddedLetters` leftover tokens, each alphabetic, not a NATO word, and starting with the letter the snap added at its position ("november 9225 lien" → N9225L, "november 514 rain mic" → N514RM); the trailing `TokensConsumed` covers them. "november 9225 immediately" is not a trailing callsign. Only the phonetic GA form sets `SnapAddedLetters`; the hybrid and bare-token forms never strip a following token.

## LLM Fallback Mapper

- `src/Yaat.Client/Services/LocalLlmService.cs` — owns the LLM `LM` and
  a `SingleTurnConversation` configured with
  `Sampling = new GreedyDecoding()` (deterministic — don't swap for
  `RandomSampling`) and a `Grammar` set from
  `CanonicalCommandGrammar.Default`. Lazy-loads the model on first
  `GenerateAsync` or `PrewarmAsync`.
- `src/Yaat.Client/Services/LocalLlmCommandMapper.cs` —
  `ISpeechCommandMapper` backed by `LocalLlmService`. Flow:
  1. `BuildUserPrompt(transcript, context)` — wraps the normalized
     transcript with scenario context. Currently emits up to three
     optional blocks: programmed fixes, available runways grouped by
     airport (with a directive line about snapping to closest valid
     runway), and active aircraft → destination map. The flat callsign
     listing is deliberately omitted because small instruct models
     echoed callsigns back as `AT N314GT AT N346G ...`
     condition-prefixed clauses.
  2. `_llm.GenerateAsync(_systemPrompt, userPrompt,
     CanonicalCommandGrammar.Default, ct)` — grammar-constrained inference.
  3. `NormalizeOutput(raw)` — defence-in-depth validation against
     condition prefixes (`AT`/`LV`), verb whitelist, and
     `[A-Z0-9.+/-]` arg charset. Returns `null` and logs the raw output
     if anything fails.
  4. Returns `MapResult` on success, `null` otherwise.
  - `BuildSystemPrompt()` derives few-shot examples from
    `PhraseologyRules` at startup, so new rules automatically enrich
    the LLM's priors. An override constructor lets the sandbox tool
    iterate on prompt phrasing without rebuilding the class.
  - `GetDefaultSystemPrompt()` exposes the default so the sandbox can
    fetch it as the starting point for probe-mode edits.
  - `BuildUserPromptForDebug()` and `MapWithPromptsAsync()` are
    `internal` escape hatches the speech sandbox uses to (a) preview
    the assembled user prompt for the current inputs and (b) run the
    LLM against a hand-edited override prompt without rebuilding from
    inputs. Both are bypassed by the production `MapAsync` path.
- **`MapContext` carries scenario context** beyond what the rule mapper
  needs: `AvailableRunways` (airport → runway list, used by both the
  fuzzy snap and the LLM user prompt) and `AircraftDestinations`
  (callsign → destination, used by the LLM user prompt to correlate a
  noisy callsign with the right airport's runway list). Populated by
  `MainViewModel.BuildSpeechContext` from `NavigationDatabase.GetRunways`
  and `AircraftState.Destination` / `AircraftState.Departure`.
- `src/Yaat.Client/Services/LocalLlmCallsignResolver.cs` — LLM-based
  disambiguation for noisy callsign transcripts where fuzzy-matching
  inside `CallsignParser` doesn't pick a winner.
- `src/Yaat.Sim/Speech/CanonicalCommandGrammar.cs` — GBNF built at
  runtime from `CommandRegistry.AliasToCanonicType`:
  - One or more comma-separated clauses.
  - Each clause is an optional condition prefix (`AT FIX `, `LV ALT `)
    + a verb + zero or more args.
  - Verbs are sorted longest-first so multi-character verbs match
    before single-letter prefixes (`RELL` beats `R`, `FPH` beats any
    future `F`).
  - Args restricted to `[A-Z0-9.+/-]` — exactly the set
    `LocalLlmCommandMapper.NormalizeOutput`'s charset check allows.
  - The grammar expands automatically as new aliases are added to
    `CommandRegistry` — there is no second list to keep in sync.
- **Model:** `gemma4:e4b` is the validated default (12/12
  `LocalLlmPipelineIntegrationTests` pass; well-calibrated enough to
  pick end-of-generation over inventing low-prior verbs when no valid
  command is appropriate). Override via the `LMKIT_TEST_MODEL` env var.
  Earlier Qwen3.5 4B models couldn't make the EOG-vs-invent decision
  correctly.

## Context-Layer Decoder

`src/Yaat.Sim/Speech/Context/` reads one transmission against its closed set: the room's aircraft and the airport's entities, each with its spoken forms. `ContextDecoder.Decode` returns the addressed callsign, the entities around it and a rewritten transcript, or a say-again with its best guess. `SpeechRecognitionService.MapHypothesesWithTraceAsync` is the accurate-mode mapping entry: it decodes the whole n-best, and a `Resolved` result gives the callsign and the command text (the decoder's rewritten transcript minus the callsign span, digit-normalized); `SayAgain` and `NoCallsign` fall back to `CallsignParser` (and the LLM callsign resolver) on the rewritten transcript, but `SpeechRecognitionService.AcceptUnresolvedCallsign` keeps a recovered callsign only when it equals the decoder's best guess (`SayAgain`) or is not one of the room's aircraft (`NoCallsign`; an aircraft with unknown telephony is not in the room and can be accepted). A press that accepts no callsign stages no canonical command: `SpeechResult.SayAgainText` carries the status `Say again: callsign unclear`, with `(best guess DAL34)` when the decoder has one, and the command box holds the raw transcript with its callsign words. The fast path is unchanged. The live PTT pipeline takes this path when the loaded engine is accurate (beam), the press carries a room snapshot and the n-best has entries; every other press, and one whose room build or decode throws, maps hypothesis 0 on the fast path. Rules and rulings: [`speech-context-decoder.md`](./speech-context-decoder.md).

Spoken forms come from the pilot-speech spelling plus the per-ARTCC pronunciation data in `NavigationDatabase` ([navigation-database.md](navigation-database.md)). `ContextEntityFactory.GroundEntities(airport, layout, nav)` adds each registered gate pronunciation as "gate p", "parking p" and a bare form, and each spot pronunciation as "spot p" and a bare form (a bare form only when the layout name has a letter and the form is not a taxiway's bare form). `AirportContextBuilder.Build` adds each named place (`GetGroundPlaces`: an FBO, ramp or bay) as a landmark entity valued `<airport>/<NAME>`, whether or not the layout is loaded; a caller-supplied landmark with the same value wins. SIDs and STARs are heard by every registered base-name pronunciation with the version ("nimitz five"), alone and with "departure" or "arrival", before the code-spelled form ("nimi five"), and include the ARTCC-fragment procedures the current cycle lacks.

## Orchestration — `SpeechRecognitionService`

- `src/Yaat.Client/Services/SpeechRecognitionService.cs` — pure service
  layer, **no MVVM/Avalonia dependencies**. Consumes a
  `Func<SpeechContext>` so the caller (typically `MainViewModel`)
  controls how scenario state is observed and the service stays
  decoupled from the view model.
- **State machine** (`SpeechStatus`): `Idle`, `Warming`, `Recording`,
  `Transcribing`, `Mapping`, `Error`, `ModelMissing`. `Warming` is treated as
  start-legal — `StartPtt` can fire mid-prewarm because the engine
  locks serialize the in-flight load against the user press. So is
  `ModelMissing`, set when a press is refused because the chosen model is
  not downloaded.
- **Session history**: last `MaxSessionHistory = 20` PTT presses are
  captured as `SpeechSession` records (transcript, canonical, elapsed
  timings, outcome) in a ring buffer `ObservableCollection`, exposed
  to the Speech debug window for post-mortem inspection.
- **`SpeechSessionOutcome`** enum: `CommandAccepted`,
  `NoMappingFound`, `EmptyTranscript`, `Error`, `Cancelled`.
- **Events** (all fire on thread-pool — UI consumers must marshal):
  - `StatusChanged(SpeechStatus)`
  - `CommandReady(SpeechResult)` — the final transcript + canonical
    command pair; consumed by `MainViewModel` which uses
    `Dispatcher.UIThread.Post()` to push the canonical string into
    `CommandText`.
  - `SessionRecorded(SpeechSession)` — fires after the session is
    appended to `SessionHistory`.
- **Cancellation:** a new PTT press mid-processing cancels the
  previous session via `_pendingCts`; the stale session finalises with
  `SpeechSessionOutcome.Cancelled`.

## Client Integration

- `src/Yaat.Client/Views/MainWindow.axaml.cs` subscribes to
  `GlobalKeyHookService.KeyDown`/`KeyUp` (and falls back to
  window-local `OnKeyDown`/`OnKeyUp`) for PTT press detection.
- `MainViewModel` subscribes to `SpeechRecognitionService` events and
  pushes `CommandReady.Canonical` into `CommandText` via
  `Dispatcher.UIThread.Post`. **The command is never auto-sent** — the
  user always reviews and presses Enter.
- `src/Yaat.Client/Views/SpeechDebugWindow.axaml.cs` — post-mortem UI listing recent PTT sessions with transcript, canonical command, elapsed times, and outcome. Updated via `SessionRecorded`. Its header carries `SpeechDebugViewModel.RunningEngineText` (built by `RunningEngineTextFor`: model title, effective mode, CPU or GPU from the loaded sherpa native set; Whisper always CPU), computed once when the window opens, and, when startup or a model load fell back from the GPU, `CpuFallbackText` ("GPU unavailable: {reason}", from `SherpaNativeLoader.CpuFallbackReason`), which updates live through `CpuFallbackReasonChanged`. Stage ② is titled "Transcription", engine-neutral.
- Each session row (`SpeechDebugSessionRow`) also projects its `SpeechSessionTrace`: `ModeText` (`Fast`, `Accurate` or `unknown`, from the trace, never the header), `DecodeTimeText` (`Decode 12 ms`, whole milliseconds, only when a context decode ran), `NBestLines` (one `#1 text  (score)` line per accurate-mode hypothesis, score omitted when the engine reported none), `DecodeStateText` (`Resolved`, `Say again`, `No callsign`, `not run`, or `Fell back to fast: <message>` when the accurate decode failed) and `BestGuessText` (`Best guess: <callsign>`, say-again only). The mode and decode time sit in the detail banner; the n-best list and the context-decode block sit on the Transcription card. A session with no trace shows none of them.

### Speech telemetry (opt-in upload)

Sample capture (`SpeechSampleStore`, `%LOCALAPPDATA%/yaat/speech-samples/<id>/audio.wav + session.json`) is local by default. Speech telemetry adds an automatic upload on top of it.

- **Opt-in.** `MainViewModel.OfferSpeechTelemetryIfDueAsync` shows `SpeechTelemetryOptInDialog` once. It runs the first time STT turns on, from any entry point (`OnIsSpeechEnabledChanged`), and at window open for users who had STT on before the prompt existed. It is gated by `UserPreferences.SpeechTelemetryPromptShown`. The Settings › Speech checkbox changes the choice later, and applying it also marks the prompt shown.
- **Telemetry implies capture.** `UserPreferences.SetSpeechTelemetryEnabled(true)` forces `SpeechSampleCaptureEnabled` on, and `SetSpeechSampleSettings` cannot turn capture off while telemetry is on.
- **Queue.** `RecordSession` calls `SpeechSampleStore.Add(..., queueForUpload: SpeechTelemetryEnabled)`, which drops an `upload-pending` marker in the sample folder.
  - Only samples captured while telemetry is on are ever sent.
  - Turning telemetry off calls `ClearPendingUploads()`.
  - FIFO eviction can drop a sample before it is sent.
- **Upload.** `SpeechTelemetryUploader.UploadPendingAsync` runs off the UI thread after every `SessionRecorded` and after connecting to a server.
  - It uploads only to the official server (`UserPreferences.OfficialServerUrl`). Samples captured while connected elsewhere stay pending until the next official connect.
  - Each iteration re-checks the preference and the sample's marker, so opting out stops a pass already running. It POSTs each pending sample, oldest first, as a single-sample export bundle (`SpeechSampleStore.WriteBundle`, the `.yaat-speech-sample.zip` format) to `{server}/telemetry/speech` with the VATSIM-session Bearer token.
  - 2xx clears the marker.
  - 400 or 413 also clears it, since that sample can never succeed.
  - Any other failure stops the run and leaves the rest pending for the next trigger. That includes 429, the server's per-CID daily cap.
- **Server.** yaat-server's `SpeechTelemetryEndpoints` requires the training-hub policy and caps the body at 5 MB.
  - It caps each CID at 300 samples / 150 MB per UTC day, answering 429 beyond that.
  - It rejects anything that isn't a zip whose `manifest.json` lists well-formed sample ids with their audio and session entries.
  - It writes atomically, storing the upload at `<TelemetryPath>/speech/<yyyy-MM-dd>/<cid>-<HHmmss>-<guid>.zip`, on the Docker volume `yaat-telemetry`. Developers pull the uploads with the admin routes `GET /admin/telemetry/speech[?since=]` and `GET /admin/telemetry/speech/{date}/{file}` (header `X-Yaat-Admin-Password`), through `tools/speech_telemetry.py`.
  - Uploads are kept for `Yaat:SpeechTelemetryRetentionMonths` (default 12): yaat-server's `DataRetentionHostedService` runs `SpeechTelemetryRetention.SweepExpired` at startup and daily, deleting each date folder older than that whole. `DELETE /admin/data/{cid}` deletes one CID's uploads on request ([data-handling.md](data-handling.md)). Pull what you want to keep before it ages out.

#### Reviewing telemetry

`tools/speech_telemetry.py` keeps everything under `.tmp/speech-telemetry/`. The admin password comes from the environment only (`YAAT_ADMIN_PASSWORD`), and `--server` defaults to `https://yaat1.leftos.dev`.

1. `python tools/speech_telemetry.py pull` downloads new bundles into `raw/<date>/` and unpacks each sample into `cases/<cid>-<sampleId>/` (`audio.wav`, `session.json`, `meta.json`).
2. `summary` prints the samples not yet reviewed, misses first: NoMappingFound, EmptyTranscript, Error, Cancelled, LLM fallback, then accepted. `--all` includes reviewed ones and `--json` gives machine output.
3. `eval [--trials N] [case…]` runs the sandbox `--eval` over those cases through the gate. The first run writes an unreviewed `expected.json` stub into each case. Fix the labels, remove `"unreviewed"`, and run `eval` again to score them.
4. `promote <case> [--name <slug>]` turns a labelled miss into a text-only regression entry in `tests/Yaat.Client.Tests/TestData/speech-transcripts/telemetry-regressions.json`: the raw transcript, the canonical and the scenario context.
   - It never copies audio, a CID or the session, because real users' recordings are not published.
   - `TelemetryTranscriptRegressionTests` then fails until the rule mapper handles the transcript, so the fix goes through `test-fix`.
5. `reviewed --all-shown` marks everything `summary` just showed, so the next session starts from the new samples.

Promotion exercises normalisation and the rule mapper, not Whisper. A miss caused by the audio (a wrong transcript) is fixed through the biasing prompt or the model choice, and checked with `eval` on the local case.

## Speech Sandbox Tool

`tools/Yaat.SpeechSandbox/Program.cs` is a CLI + Avalonia GUI for
iterating on prompts and probing models without launching the full
client. All flags run via
`dotnet run --project tools/Yaat.SpeechSandbox -- <flag>`.

| Flag | Purpose |
|------|---------|
| `--pipeline <wav> [<wav> ...]` | Full PTT pipeline for one or more WAVs; side-by-side STT / normalization / rule / LLM output so you can see where a transcript gets lost. |
| `--llm-probe <transcript>` | Skip STT; push a transcript straight through `LocalLlmCommandMapper` (after digit normalization). Useful for iterating on the LLM system prompt. Honors `LMKIT_TEST_MODEL`. |
| `--lmkit-stt <wav> [model-id ...]` | Probe multiple Whisper variants on the same clip; reports cold/warm/biased timing. |
| `--lmkit-models` | Dump LM-Kit's predefined model catalog (file sizes, licenses, local-availability). |
| `--lmkit-gpus` | Enumerate detected GPU devices for LM-Kit backend selection. |
| `--yaat-catalog` | Dump the filtered Whisper + LLM catalogs as they appear in the Settings picker. |
| `--ouroboros <corpus.json> [--out-dir <dir>] [--trials N]` | Synthetic round-trip harness: canonical → pilot readback → Piper TTS → full STT pipeline → compare. PASS/FLAKY/FAIL verdicts; markdown report + per-case WAVs. |
| `--eval <corpus-dir> [--out-dir <dir>] [--trials N] [--stt <kind>:<source>] [--device cpu\|gpu] [--decoding greedy\|beam] [--num-paths N] [--prompt default\|none]` | Real-audio eval harness: scores the full production pipeline (STT → callsign extraction → rule → LLM) against labeled captured recordings. Reports canonical exact-match verdicts, STT word-error-rate (hypothesis 0), per-trial STT latency, and per-case transcripts; real and synthetic cases are tallied separately in the summary; honors `LMKIT_TEST_MODEL`. `--stt`, `--device`, `--decoding` and `--num-paths` (`EngineFlags`) take the same values and refusals as `--corpus-wer` (default: the Settings Whisper model). The model is loaded and timed before the first trial, and the report and console add a device line (load, STT latency p50/p90/max, working set and GPU memory before load and at peak). **`--decoding beam` is accurate mode**: the whole n-best goes through `SpeechRecognitionService.MapHypothesesWithTraceAsync` against each case's room, built by `RoomContextFactory` from the case's `airport`, `approachPosition`, `facilityRadioNames`, active callsigns (programmed fixes on the addressed aircraft) and the airport's committed test layout; it must run in a yaat checkout, a case without `airport` stops the run, and the report adds the resolved / say-again / no-callsign counts and the decode+map p50/p90. `--prompt default` needs a Whisper engine, and `--trials` above 1 needs a local one, since cloud results are cached. See `EvalRunner.cs` for the corpus layout. |
| `--synth-corpus <out-dir> [--cases N] [--seed S] [--voice <dir>] [--no-synth-cache] [--speeds <s1>,<s2>,...]` | Generates labeled synthetic **controller-phraseology** eval cases from the templates in `SynthTemplates.cs` (every rule family). Each label is verified through the real text-mapping pipeline before any audio is made; templates that don't verify are left out and printed as `GAP` lines. Cases are Piper-synthesized with varied speakers and speeds (Piper multipliers drawn from `SynthTemplates.DefaultSpeeds`, 0.9-1.2, or from `--speeds`, each 0.5-3.0; a different pool keeps every case's template, callsign, slots and speaker and changes only its speed) and written as `--eval`-ready case dirs marked `"synthetic": true`. Deterministic per seed: Piper's audio differs between processes even for the same text, speaker and speed, so every synthesized WAV is cached under `%LOCALAPPDATA%/yaat/cache/synth-audio/` (`SynthAudioCache`, keyed by `SynthAudioCache.PipelineVersion`, the voice pack's model hash, Piper's length scale, text, speaker, speed, sample rate and padding; bump `PipelineVersion` whenever the synthesize → resample → pad path changes) and each case's `expected.json` records its `cacheKey`; `--no-synth-cache` bypasses the cache. A first write wins: a second process that synthesized the same key uses the stored audio. A corrupt entry is logged (stderr `WARN:` and `%LOCALAPPDATA%/yaat/yaat-speech-sandbox.log`), deleted and re-synthesized. Generate into `.tmp/`, don't commit the output. |
| `--atc-ouroboros [--cases N] [--seed S] [--trials N] [--out-dir D] [--baseline <json>] [--update-baseline] [--voice <dir>] [--no-synth-cache] [--speeds <s1>,<s2>,...] [--stt <kind>:<source>] [--device cpu\|gpu] [--decoding greedy\|beam] [--num-paths N]` | Controller-voice round trip (a non-default `--speeds` pool is recorded as `speeds` in `results.json` and skips the baseline comparison when it differs from the baseline's): `--synth-corpus` then `--eval` in one run, on the engine and mode the engine flags pick (as `--eval`; without them the Settings Whisper model). Synthetic cases are decoded as KOAK from an approach position with the Oakland Tower, Oakland Ground and NorCal Approach facility names (`SynthTemplates.CaseAirport` and its siblings). It writes `results.json` (per rule family, per template, the gaps, and the run's `timing` and accurate-mode `decode` stats, neither of which reaches the baseline) and `report.md`, then compares with `tools/Yaat.SpeechSandbox/Corpus/atc-ouroboros-baseline.json`. Defaults: 200 cases, seed 20260928, 3 trials, out dir `.tmp/atc-ouroboros-<stamp>/`. Scoring is per command clause (`CommandScoring`): each `,` clause of each trial's canonical is recognised, wrong (wrong arguments, wrong verb, or an inserted clause) or rejected (missing, no mapping, or the mapper's raw-text fallback), matched order-insensitively since `,` clauses are parallel. Exit 3 means a regression: a rule family's recognition rate fell by more than 1.0 points or its error rate rose by more than 0.5 points, and by at least one case (one clause wrong on every trial of one case); the totals, pooled over the families in both runs, gate the same way; or a template that verified in the baseline is now a gap. PASS / FLAKY / FAIL and per-template rows are information only. |
| `--corpus-wer uwb-atcc\|<dir> [--subset N \| --all] [--seed S] [--stt <kind>:<source>] [--device cpu\|gpu] [--prompt default\|none\|both] [--decoding greedy\|beam] [--num-paths N] [--hotwords-file <path>] [--hotword-score S] [--hotword-case asis\|title\|lower\|upper] [--slots [--resolve [--acoustic-weight W] [--confident-word-prob P]] [--room-hotwords unpadded\|padded\|oracle [--hotword-cap N] [--two-pass aircraft\|aircraft-airport]]] [--update-baseline] [--out <dir>]` | An STT engine's word error rate on real ATC speech, STT only (no mapper, no LLM). `--stt` picks the engine (`CorpusSttEngine.cs`): `whisper:<model id, URL or .bin>`, `sherpa:<dir>` (a sherpa-onnx offline NeMo transducer export: Parakeet-TDT, Orukeet, exported fine-tunes) , `sherpa-streaming:<dir>` (a streaming NeMo transducer such as Nemotron Speech Streaming, `SherpaStreamingSttEngine`), `gemini:<model>` or `openai:<model>` (`CloudSttEngine`). A cloud engine reads `GEMINI_API_KEY` or `OPENAI_API_KEY` from the process or user environment, sends a fixed ATC instruction, retries 429/5xx/transport errors up to 6 attempts, and caches each response under `%LOCALAPPDATA%/yaat/cache/cloud-stt/`, keyed by the audio plus everything else it sent, so a rerun costs nothing. A clip that fails is reported as "returned no transcript" in the console and `report.md`. Sherpa and cloud engines take no prompt, so they run the `none` arm only, and their results record `sherpa:<dir name>`, which skips the Whisper baseline comparison; `--update-baseline` refuses them, since the committed baseline tracks the shipped Whisper model. `uwb-atcc` downloads the UWB-ATCC test split at a pinned Hugging Face revision into `%LOCALAPPDATA%/yaat/cache/atc-corpora/` (SHA-256 checked, read with Parquet.Net); `<dir>` reads `<id>.wav` + `<id>.txt` pairs at any sample rate. Both sides go through `WerTextNormalizer` before `WerScorer` counts substitutions, deletions and insertions; WER is pooled per speaker role (an id ending `_PI` pilot, `_AT` controller, `PIAT` mixed) and per prompt arm (the biasing prompt and none). Defaults: 400 seeded segments for `uwb-atcc`, all for a folder, seed 1117, `LmKitModelCatalog.RecommendedWhisperId`, both arms, out dir `.tmp/corpus-wer/<stamp>/` (`report.md`, `results.json`, `results.jsonl`). The aggregates-only baseline is `Corpus/<corpus>-wer-baseline.json` (a folder's lowercased name); exit 3 when an arm's overall WER rises by more than 1.0 point, and the comparison is skipped when the corpus, its fingerprint, seed, subset or model differ. Each arm also records timing (`CorpusWerTiming`, never in the baseline): model load and warm-up, per-segment latency (`latencyMs` in `results.jsonl`; p50/p90/max nearest-rank over non-cached segments), pre-load and peak working set, and peak dedicated GPU memory from Windows' `GPU Process Memory` counters (`GpuMemoryProbe` in `src/Yaat.Client/Services/Speech/`, with `CorpusWerTiming` in `SttMeasurement.cs` beside it). The streaming engine is fed in 0.1 s chunks and times only the finalization after the last sample. `--device cpu|gpu` picks a local engine's device: Whisper on CPU turns LM-Kit's CUDA and Vulkan backends off (with either loaded, zero GPU layers run on one thread) and refuses a GPU backend; sherpa on GPU loads the `cuda` provider, which needs the CUDA native `New-SherpaNativeSandbox.ps1 -Variant cuda` overlays into `.tmp/sandbox-native-cuda/`; the script defaults to the CI native commit `stt-downloads.json` names, and `-NativeId` picks another staged native. A GPU run whose GPU memory rises under 100 MB through the load is refused as a CPU fallback. `--decoding beam` runs a sherpa engine with `modified_beam_search` and an n-best of `--num-paths` (1-4, default 4), which needs the model's `bpe.vocab` and YAAT's own native (`New-SherpaNativeSandbox.ps1`; the stock native is refused). `--hotwords-file` adds one phrase per line to every stream, at the run's `--hotword-score` (default 1.5) and `--hotword-case` (default `title`). A beam run records `sherpa:<dir name>+beam<N>`, and `results.jsonl` gains `nbest`, each entry's text, tokens, per-token log-probs and score. Time runs alone (`gate.ps1 run --slot critical --alone --local`), after a `--slot heavy --local` build. See "Measuring recognition on real speech" below. |
| `--tts-bench [--models <id>,<id>...] [--download-only] [--corpus <json>]` | Small-TTS benchmark for the pilot voice: renders the committed pilot readbacks (`Corpus/tts-bench-readbacks.json`, spoken text from `PilotResponder`) and times each model's download and disk size, cold load, first-audio latency and real-time factor (RTF). Model ids: `piper-libritts-r-fp32`, `-fp16`, `-int8`, `kitten-nano-0.8`, `kitten-micro-0.8`, `kitten-mini-0.8`, `matcha-ljspeech`, `pocket`, `pocket-int8`, `zipvoice-int8`, `supertonic-3-int8`, and `kitten-tts-2` (recorded only: its rows come from `recorded-screens.json`, never re-run). A **screen** (3 lines, 1 voice, at 8, 4 and 2 threads) comes first: a thread count passes at p90 first audio of 1.5 s or less and median RTF under 1.0, is marginal above 1.0 s, and a model is out on a load over 30 s or a first line over 10 s; survivors then get the full run. Every sherpa-onnx phase runs in a child process (`--tts-bench-one <job.json>`, internal) so a native abort costs only that model. `--download-only` fetches and extracts the models and stops. Exit 0: every model ran or was screened out; 3: a model did not run; 2: usage, corpus or download error. Models cache under `%LOCALAPPDATA%/yaat/tts-bench/models/` (`YaatPaths`), clips go to `.tmp/tts-bench/clips/`, and the report, `results.json` and listening page are rewritten after each model. The sandbox and the client both run sherpa-onnx 2.0.1. Results: `docs/research/2026-10-24-tts-benchmark/report.md`; rebuild recipe: `docs/plans/canvases/tts-benchmark/RECIPE.md`. |

**Tuning loop.** For STT work with no specific sample in hand, start from `--atc-ouroboros`:

1. Run it and read `report.md`, worst family first, plus its gap list (templates the rule mapper can't map; `AtcOuroborosTests.KnownGaps` names them, and `docs/plans/stt-rule-gaps.md` tracks them).
2. Fix a FAIL or FLAKY family, or a gap, through `test-fix`, with a failing `PhraseologyMapperTests` row first. A fixed gap fails `AtcOuroborosTests` until its `KnownGaps` entry is removed.
3. Rerun. If it exits 0 and a family went up, run `--update-baseline` and commit the baseline with the fix. An exit 3 names the family that regressed.

Keep the seed fixed when comparing against the baseline: a new seed draws different slot values, so its differences are sampling noise, not a change in the pipeline. Synthetic audio measures the phonetic surface only; real samples from speech telemetry (see "Reviewing telemetry" above) stay authoritative for model decisions.

### Measuring recognition on real speech

`--corpus-wer` measures the STT stage alone on real radio audio, so a model, prompt or decoder change can be judged by its word error rate.

- **Feature work runs on the strongest models available.** Reference transcripts, tuning and evaluation use the best STT model we can run or buy, so the pipeline is tuned to what was said, not to what a weaker model guessed. What a user's machine runs is a separate, per-machine choice. `--corpus-wer` keeps the shipped default (`RecommendedWhisperId`) so its committed baseline tracks what users get; pass `--stt` with the strongest local model for development runs.

  The strongest model found so far is Gemini 4 Argon (`gemini-4-argon`, cloud), with about a third of the shipped Whisper model's word errors on hand-checked US speech ([bake-off](research/2026-10-30-stt-model-bakeoff.md)). It drafts new reference transcripts and is the **golden baseline** for word error rate, the accuracy every local model and fine-tune aims at. Run it with `--stt gemini:gemini-4-argon`.

  It is too slow to ship (p50 3.0 s per clip against the push-to-talk budget of under 1 s). So local engines are ranked on latency and RAM as well as WER, and the in-app benchmark shows those figures so each user picks the model that suits their machine.

  The best local model measured is rasr-parakeet in fp32. Its owner-checked WER and callsign recall, and those of the int8 export, in both modes on the CI natives, are in `src/Yaat.Client/Speech/stt-reference.json` and in the context-layer note's section "Reference figures on the CI natives (YAAT-1605)", with the latencies and memory per device.

  The bake-off measured it at 31.3% on 1.13.8 against the shipped Whisper's 50.5%, at p50 100 ms on GPU and 324 ms on CPU. int8 exports of the Parakeet family lose 13-25 points of WER ([timed bake-off](research/2026-10-30-stt-model-bakeoff.md#local-models-timed-on-cpu-and-gpu-yaat-1564)). The shipped model's `liveatc-us` baseline is 39.1% with the biasing prompt and 39.5% without, identical across two runs.
- **The reference set is US speech kept outside git.** The committed baseline is measured on a folder of LiveATC clips the owner saved by hand and transcribed (`liveatc-us`, in the owner's private config folder; its README holds the transcription rules). LiveATC's terms allow personal, non-commercial use and forbid automated download, so only the aggregates are committed.
- **A folder corpus reports WER by provenance and by feed.** An optional `decisions.json` beside the clips maps each clip id to who settled its reference (`owner`, `agreed` or `claude`); with it, every arm adds an `owner` and an `owner+agreed` row, the owner-only row being the unbiased figure. Ids shaped `<feed>_seg<n>` (`koak-app_seg0004`) add one row per feed. A clip missing from `decisions.json`, or a value other than the three, stops the run.
- **`--slots` scores the context decoder on entity slots.** It needs `rooms.json` and `slots.json` beside the clips. `slots.json` gives each clip's gold slots: kind (callsign, fix, runway, taxiway, gate, landmark or procedure), canonical value and the spoken form. `rooms.json` gives per feed the airport, facility (`gnd`, `twr` or `app`) and a seed, plus telephony and aircraft-type overrides.

  Each clip is decoded under three rooms (`RoomContextBuilder`). The unpadded room holds the feed's real aircraft, whose own entities come from the feed's other clips only. The padded room adds seeded, arrival-weighted distractors up to 35 aircraft. The oracle holds only the clip's own gold. Ground and tower rooms take fixes within 10 nm, approach rooms within 40 nm plus the runways and approaches of nearby airports that have an instrument approach.

  Without `--resolve`, the decoder reads hypothesis 0 with exact matching (the raw transcript's slots). With it, the decoder runs fuzzy over the whole n-best, and each room also reports the WER of the rewritten transcript. `--acoustic-weight` and `--confident-word-prob` replace `DecoderSettings.Default`'s `AcousticWeight` (0.3) and `ConfidentWordProb` (0.97) for the run, and the report header records both. `--room-hotwords <room>` (beam only, `--hotword-cap`, default 300) biases each clip's stream with that room's phrases (`RoomHotwords`). The phrases are callsigns, then own entities, then airport entities, keeping only forms that hold a name word.

  `--two-pass aircraft|aircraft-airport` (needs `--room-hotwords` and `--resolve`) decodes each clip twice. The decoder finds the addressed aircraft in pass 1 under the hotword room: the resolved callsign, or the say-again best guess. Pass 2 re-decodes the audio with only that aircraft's callsign and own-entity phrases, plus the airport's with `aircraft-airport`. A clip with no callsign, or whose aircraft yields no phrase (an N-number is all letters and digits), keeps pass 1. Scoring reads the final pass; `latencyMs` sums both passes and the finder, and each `results.jsonl` line records the outcome in `twoPass`.

  On the full set, the context layer's measured verdict ([context-layer note](research/2026-10-31-stt-context-layer.md)) is as follows. Beam with resolution adds 3.2 points of callsign recall and 1.0 point of owner WER over greedy, for 125-290 ms. Room hotwords and the two-pass decode add latency and no accuracy. The decoder itself caps recall: even Argon's transcripts reach 45% callsign recall, with no wrong addressee in any run.

  On clean synthetic controller audio ([clean-audio note](research/2026-11-01-stt-clean-audio.md), `--atc-ouroboros` at 166-260 words per minute), rasr-parakeet v2 fp32 leads every local engine at every pace. Accurate mode, beam with the rewritten text, adds 2.3-3.2 points of recognised commands over greedy. The owner ruled accurate as the default mode and rasr v2 fp32 and int8 as the offered models.

  Every room reports per-kind recall and false insertions per provenance and feed (`CorpusSlotScorer`). It also reports wrong addressees, say-agains and no-callsigns on clips with a gold callsign, how much gold the generated forms reach and match, and how many clips the log-prob gate covered. Sherpa marks a word start with a leading space, which the sandbox maps to the decoder's `▁`. Slot results never enter a committed baseline.
- **The spoken-form lexicon is generated, then owner-reviewed.** `--lexicon-inputs <ICAO> <out.json>` dumps an airport's fixes (within 40 nm and on its procedures), SIDs/STARs (fragment ones included), lettered gates and spots, named places and landmarks, with their existing forms. `tools/generate-speech-lexicon.py generate` asks `claude-opus-5-5` which need a spoken form and proposes them. `review` serves a local page where the owner accepts, edits or rejects each one, and `apply` writes only the accepted ones into the ZOA `FixPronunciations`, `ProcedurePronunciations`, `GatePronunciations` and `CustomFixes` files ([ARTCCs README](../src/Yaat.Sim/Data/ARTCCs/README.md)). The key comes from the environment only: `uv run --env-file <main checkout>/.env tools/generate-speech-lexicon.py generate ...`.
- **UWB-ATCC is not a fair test of the default model.** The default (`RecommendedWhisperId`) is a ggml conversion of jacktol's ATC fine-tune, trained on `jacktol/ATC-ASR-Dataset`, which is built from UWB-ATCC and the ATCO2 1 h set, so its UWB-ATCC score flatters it. Use `uwb-atcc` to compare models that never trained on it. UWB-ATCC is European (Prague) speech under CC BY-NC-SA 4.0: it is downloaded at run time and never committed; cite "Air traffic control communication (ATCC) speech corpora and their use for ASR and TTS development" (https://link.springer.com/article/10.1007/s10579-019-09449-5) when publishing its numbers.
- **The normalizer, not the reference, decides what counts as the same word.** `WerTextNormalizer` lowercases, drops punctuation, spells every digit string digit by digit (`118.1` → `one one eight decimal one`, `FL350` → `flight level three five zero`), maps spelling variants (`alpha`/`alfa`, `juliet`/`juliett`, `x-ray`/`xray`, `niner`/`nine`), folds accents, joins split compounds (`stand by`, `push back`, `take off`, `taxi lane`, `station air`) and runs of single letters (`i l s` → `ils`), reads `oh` between digit words as `zero`, and drops fillers (`uh`, `um`, `ah`, `er`, `erm`, `hmm`). It is separate from `AtcNumberParser.NormalizeDigits`, which collapses words to digits for the mapper.
- **Clips are squelch-to-squelch spans**, so a clip can hold two transmissions or clip a word; references follow what is audible, which leaves a floor of errors no model can remove.

No flag → Avalonia GUI for interactive probing. The GUI exposes inputs
for active callsigns, programmed fixes, **available runways** (per-
airport, format `KOAK: 28R 28L 10R 10L 30 12 33 15`), **aircraft
destinations** (callsign → airport), the editable LLM system prompt,
and the **editable LLM user prompt** (auto-filled from inputs via
"Build from inputs" or hand-edited; on Run the exact textbox content
is sent to the LLM via `LocalLlmCommandMapper.MapWithPromptsAsync`,
bypassing `BuildUserPrompt`). The Whisper prompt textbox is seeded
at startup with `WhisperBiasingPrompt.Default` to match production.

## In-App Benchmark Core

`src/Yaat.Client/Services/Speech/Bench/` measures one **benchmark row** (one model in one mode on one device) in the calling process, or in the benchmark child process. The pieces are the job, the runner, the child, the clip set, the reference file and, on the app side, the service that runs the rows one child at a time and the Speech benchmark window that shows them (Settings > Speech > Benchmark this machine… opens that window).

- **A row is measured by `SttBenchRunner.RunAsync`.** It reads the bench clips, samples working set and GPU memory, creates the job's engine through `ISttBenchEngineSource`, and times the load (`WarmUpAsync`). It then checks the load reached what the job asked for: an Accurate job whose engine loaded in Fast mode fails, and so does a GPU job whose process GPU memory did not rise (`GpuUseCheck`, or an unreadable counter). The first clip is transcribed untimed, then every clip is timed, sampling memory after each; `CorpusWerLatency.Summarize` turns the figures into the same `CorpusWerTiming` `--corpus-wer` records.
- **`Yaat.Client --stt-bench-one <job.json>` measures one row in its own process** (`SttBenchChild`). It runs only when the arguments are exactly that flag and one path (`TryParseArgs`), checked in `Program.Main` right after `YaatPaths.Initialize` and before the app's log, so it never starts the UI. It logs to `%LOCALAPPDATA%/yaat/yaat-stt-bench.log`, not `yaat-client.log`, and installs `NativeStderrCapture` before any sherpa native loads.
- **The child picks the native set from the job's `Device`, not the saved preference.** `Program.ConfigureSherpaNatives(log, device)` is the one place a set is chosen (the app's startup path calls it with the saved device), and a process loads one set, so a CPU row and a GPU row are two children. A GPU job whose process ends on the CPU set is not run: it fails with `GPU not used: {CpuFallbackReason}`, or `GPU not used: the GPU runtime is not installed` when no reason was recorded. `FactorySttBenchEngineSource` builds the engine through `SttEngineFactory.CreateForBench`, which returns the greedy engine with the native problem when Accurate cannot run its beam, so the runner fails that row instead of timing Fast as Accurate. The model folder's parent is the install root and its name must be the engine's manifest id.
- **A parent reads the result file, then the exit code only for what is missing.** The child reads the `SttBenchJob` JSON, measures, and writes the `SttBenchResult` JSON to the job's `ResultPath` through a `<resultPath>.tmp` sibling that then replaces it, so a reader never sees half a file. Exit 0: a result was written, passed or failed. Exit 2: the job could not be read, no result. Exit 3: the result could not be written. Once the job is read, any throw becomes a `bench failed: {message}` result, so a readable job always gets a file. The app path of `Program.Main` returns 0.
- **A failed row carries one line instead of figures** in `SttBenchResult.Failure`: `no clips found in {dir}`, `load failed: …` (including `load failed: Whisper is not benchmarked` and `load failed: the model folder {dir} is not named {id}`), `accurate mode unavailable: the model loaded in Fast mode`, `GPU not used: …`, `bench failed: …` or `transcribe failed on {clipId}: …`. The GPU-fallback sentence points at the current log file (`AppLog.LogPath`), not the sandbox's.
- **The bench clips are synthetic and committed** in `src/Yaat.Client/Speech/BenchClips/`: `clips.json` lists each clip's `id` and `expected` words, beside one 16 kHz mono PCM16 WAV per id (`BenchClips.Load`, `WavReader`). They are controller phrases the sandbox synthesizes, so every row transcribes the same audio. Regenerate them with `dotnet run --project tools/Yaat.SpeechSandbox -c Release -- --synth-corpus .tmp/benchclips --cases 12 --seed 1604`, then copy the WAVs and a `clips.json` whose `expected` is each case's `transcript` into the folder.
- **`src/Yaat.Client/Speech/stt-reference.json` holds the figures a row is shown beside.** Its shape is `{ "schema": 1, "corpus", "sttNative", "entries": [ { "engine", "mode", "ownerWer", "callsignRecall" } ] }`; `SttReference.Load` refuses any other schema, skips an entry naming an engine this build does not know with a warning, and `Find(engine, mode)` returns a row's entry. The entries come from `--corpus-wer` runs on the CI natives (each CPU run's owner WER, the rewritten figure for Accurate, and the padded room's callsign recall), with `corpus` and `sttNative` naming the corpus and the native commit; the research note holds the full table.

  To refresh them, build the native set with `New-SherpaNativeSandbox.ps1`, run `--corpus-wer <liveatc-us> --all --slots --resolve --stt sherpa:<model dir>` per model and mode (`--decoding beam` for Accurate) alone in the gate, and copy the figures.

### The parent side

- **`MainViewModel.SttBench` (`SttBenchService`) is the app-lifetime owner of a run.** `State` (`Idle`, `Unloading`, `Running` or `Reloading`), `Progress`, `Rows` (each row with its outcome so far), `LastRun` and `Changed` (raised on whichever thread the run is on, so a view marshals it; each listener runs in turn and a throwing one is logged as `A speech benchmark listener failed`, never skipping the reload or a row); `RunAsync` and `Cancel` start and stop it. A run outlives any window that shows it, a second `RunAsync` throws, and closing the main window calls `Cancel`.
- **The rows come from `SttBenchPlan`.** `FromDownloadsAsync` reads the downloaded rasr models, the n-best check and the GPU check; each model gets its CPU rows then its GPU rows, Accurate then Fast on each, in manifest order. A row that cannot run stays in the list with its reason (`needs the GPU runtime (not installed)`, or the Accurate-unavailable reason) and is not run or counted in `Progress.Total`. Whisper gets no row.
- **Rows run one child at a time through `SttBenchProcess`.** It writes `job.json` into a per-row folder `%LOCALAPPDATA%/yaat/stt-bench/<id>/`, starts the app's own exe with `--stt-bench-one`, and reads `result.json`; the folder exists only while the row runs and is deleted after. A child past 120 s (`SttBenchService.RowDeadline`) or a cancel has its whole process tree killed: the kill is registered on both tokens, fires once, and the row never waits on it, so a kill that blocks cannot hold the row. A child still alive after the kill wait is logged with its stderr, on a cancel as on a deadline. `SttBenchChildLauncher` is the real launcher; `ISttBenchLauncher` is the test seam.
- **A row that left no result becomes a failed row**: `failed: exit {code}` plus `: {first stderr line}` when the child wrote any, or `timed out after 120 s`. The client log gets `Benchmark row {failure}; the child's stderr:` and then every stderr line.
- **A row running at Cancel shows `cancelled`** during the reload (`SpeechBenchmarkViewModel`), not `Measuring`; rows that never started stay `Waiting`.
- **Every speech model is unloaded for the run, and the wait is visible.** `SttBenchService.State` goes `Idle`, `Unloading`, `Running`, `Reloading`, `Idle`, with `StatusMessage` naming the two wait phases so the window can show progress and disable itself. Both waits are awaited off the UI thread: no row starts until the sherpa engine, Whisper and the LLM are unloaded (`WhisperSttEngine.UnloadAsync`, `LocalLlmService.UnloadAsync`), and `RunAsync` ends only once what the suspend took out is back (Whisper and the LLM reload only when the suspend asked to unload them). Closing the app skips the reload.

**The app's own engine is unloaded for the run.** `SttEngineSuspender` swaps a stand-in (`SttBenchStandInEngine`, no model, every transcription refused with a faulted task) into the `SwappableSttEngine`, then waits for every engine build in flight (`SwappableSttEngine.WhenSwapsSettledAsync`, a superseded build still loading its model included) before it unloads Whisper and the LLM. `SttEngineFactory.CreateCheckedAsync` in `ApplySttEngineAsync` goes through `StandInWhileSuspended`, so a settings change during the run keeps the stand-in. At the end, on every way out (done, failed or cancelled), `ResumeAsync` waits for the swaps in flight and rebuilds the engine the preferences name only when the suspend swapped it out or a stand-in is still in place. A push-to-talk press during a run shows `Push-to-talk is off while the speech benchmark runs` in the status bar (`MainViewModel.ShowSpeechErrorDuringBenchmark`), and the mic line reads `mic: Error` until the run ends.
- **The last finished run is `%LOCALAPPDATA%/yaat/stt-benchmark.json`** (`SttBenchLastRun`, `SttBenchLastRunStore`): the figures are this machine's, so they are not in the preferences or the settings bundle. A cancelled run leaves the file alone; an unreadable file loads as no run, with a warning in the log.

### The Speech benchmark window

- **`SpeechBenchmarkWindow` (`SpeechBenchmarkViewModel`) shows `MainViewModel.SttBench`** under the geometry key `SpeechBenchmark` (900x520): the intro, Run / Cancel, the status and progress lines, the last run's line and a table of `SpeechBenchmarkRowViewModel` rows. The view model only mirrors the service, marshalling `Changed` onto the UI thread; the run outlives the window.
- **A row shows this machine's load, p50/p90 latency, RAM and VRAM beside the reference WER and callsign recall** from `SttReference` (a dash when there is none), formatted by `SttBenchText`. A skipped, failed, waiting or running row shows one line instead of figures; a last-run row with no result shows `failed: no result`.
- **Apply saves a picked row's model, mode and device.** A row whose model was deleted since the run is refused with `{model} is no longer downloaded; download it again to apply this row.` The save is at once (`MainWindow.ApplyBenchmarkRow`): it writes the preferences, shows the choice in an open Settings window (`SettingsViewModel.ShowSavedSttChoice`, no save) and swaps the engine when the model or mode changed. A Device change needs a restart, which the window says (`RestartNote`). A Settings OK moves the `current` badge (`RefreshCurrent`).
- **`MainWindow.ShowSpeechBenchmark` opens it, one window at a time, unowned.** `SettingsWindow` raises `BenchmarkRequested` from Settings > Speech > Benchmark this machine…; a second request restores the open window. It has no owner and the Settings modal block skips it, so it stays usable under a reopened Settings.
- **Settings shows the benchmark in two places.** The row under the licence line (only with a benchmark service, so the standalone Settings window has none) holds the button and `SttBenchText.SettingsSummary` (`Never run on this machine`, `Running 3 of 8…`, `Last run 1 Nov 2026: 8 runs`, with `N failed` drawn as a warning and `· this setting 410 ms p50, 1.1 GB RAM` for the saved setting). Each model card's Measured row is `SttBenchText.CardMeasured` for the selected mode and device: `410 ms p50, 1.1 GB RAM (Fast, CPU)`, `Failed in the last benchmark run` or `Not measured: run the benchmark`. The int8 card's Note says it gets more words and callsigns wrong than fp32.
- **`SpeechBenchmarkViewModel.RequestClose` decides a close** (`CloseDecision`). Idle closes; during the unload and reload phases the close is refused; mid-run the window asks `Cancel the run?` inline and closes once the cancelled run has ended (`CloseRequested`). The owner window or app shutdown closes it without asking.

## Tests & Audio Fixtures

- `tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTests.cs` — unit tests
  per rule category. Ground rules are exercised as `[InlineData]` theory
  rows (`"pushback approved" → "PUSH"`,
  `"hold position" → "HOLD"`, etc.).
- `tests/Yaat.Client.Tests/SpeechPipelineTranscriptIntegrationTests.cs`
  — E2E rule-engine + LLM-fallback integration tests. Gated on the
  LM-Kit model cache being warm; tests silently skip when the model
  isn't available. **Do not interpret "no tests ran" as passing.**
- `tests/Yaat.Client.Tests/SpeechCallsignExtractionTests.cs` — callsign
  parser coverage.
- **Audio fixtures:** `tests/Yaat.Client.Tests/TestData/audio/probe-*.wav`
  — real captured PTT clips (16 kHz mono PCM). Add new clips under the
  same folder when expanding coverage.
- **Labeled eval corpus:** `tests/Yaat.Client.Tests/TestData/speech-corpus/`
  — one directory per case (`audio.wav` + `expected.json` with the
  ground-truth canonical, spoken transcript, callsign, and scenario
  context). Scored by the sandbox `--eval` mode. To grow it from real
  usage: enable speech-sample capture in Settings, copy a captured
  session folder (`audio.wav` + `session.json`) into the corpus, run
  `--eval` once (it writes an unreviewed `expected.json` stub from the
  session), review the labels, and delete the `"unreviewed"` flag.
  Two real-audio regressions ("descendant
  maintain" → wrong-direction CM, "heading to 7-0" → dropped FH clause)
  were found by exactly this loop.
- Tests that exercise navigation fixes rely on
  `TestVnasData.EnsureInitialized()` to load real `NavData.dat` and
  `FAACIFP18.gz`. Falls through silently if the test data files aren't
  present.

## Current Ground-Ops Coverage

`GroundRules()` in `src/Yaat.Sim/Speech/PhraseologyRules.cs` covers:

**Taxi (NATO-phonetic path via `{path...}` variadic, topology-aware collapse):**

- `"taxi via bravo charlie"` → `TAXI B C`
- `"taxi to? runway {rwy} via bravo charlie"` → `TAXI B C {rwy}`
- `"runway {rwy}, taxi via bravo charlie"` → `TAXI B C {rwy}`
- `"taxi via bravo charlie hold short of? runway? {rwy}"` → `TAXI B C HS {rwy}`
- `"taxi via bravo charlie cross runway {rwy}"` → `TAXI B C CROSS {rwy}`

**Pushback (onto-taxiway + optional facing/tail):**

- `"pushback approved"` / `"push back approved"` → `PUSH`
- `"pushback onto tango approved"` / `"push back onto tango approved"` → `PUSH T`
- `"pushback onto tango facing taxiway uniform approved?"` → `PUSH T U`
- `"pushback onto tango facing|face north/south/east/west approved?"` → `PUSH T FACE N/S/E/W`
- `"pushback onto tango tail north/south/east/west approved?"` → `PUSH T TAIL N/S/E/W`
- `"pushback approved? facing|face north"` → `PUSH FACE N`
- `"pushback approved? tail east"` → `PUSH TAIL E`
- Cardinals only resolve N/S/E/W today; intercardinals (NE/SE/SW/NW) fall through to the LLM
  fallback because their spoken forms collide with compass-point fix names.

**Other ground:**

- `"hold position"` → `HOLD`
- `"resume taxi"` / `"continue taxi"` → `RES`
- `"cross runway {rwy}"` → `CROSS {rwy}`
- `"hold short of? runway {rwy}"` → `HS {rwy}`
- `"hold short of? {taxiway}"` → `HS {taxiway}`
- `"follow the? {callsign} on ground"` → `FOLLOWG {callsign}`
- `"give way to {callsign}"` → `GIVEWAY {callsign}`
- `"exit left/right [at? {taxiway}]"` → `EL/ER [taxiway]`
- `"exit at? {taxiway}"` → `EXIT {taxiway}`

**Known gaps (LLM fallback or manual entry):**

- Alphanumeric taxiway names (`B6`, `A13`) — `"bravo six"` collides
  with `AtcNumberParser`'s digit pass before `NatoLetterNormalizer`
  sees it. Single-letter + bare-letter-combinations only.
- Destinations like "the ramp" — no canonical; a spoken gate, parking or
  spot name maps to `@<name>` only when the scenario's layout supplies it
  (NATO letters collapse first, Whisper's ICAO "alfa" included).
- `face left` / `face right` — no canonical without parking geometry.
- Intercardinals (`face northeast`).
- Compound clauses the rule engine can't express.

## Gotchas & Non-Obvious Behaviour

- **Whisper (legacy): the prompt is static and already over the 224-token cap.**
  whisper.cpp keeps the LAST ~224 tokens of the initial prompt, so
  `WhisperBiasingPrompt.Build` orders blocks tail-first by value
  (rule literals → number/digit vocab → NATO). Anything appended after
  NATO pushes NATO into the survival window at the expense of the
  number vocab — don't append; if you must add vocab, grow the
  sacrificial head or prune rule literals instead.
- **`GreedyDecoding` is intentional** for command mapping. Don't swap
  in `RandomSampling` — determinism is load-bearing for tests and user
  trust.
- **LM-Kit load failures return `null`** from the Whisper engine's `TranscribeAsync` and the LLM's
  `GenerateAsync` (legacy Whisper, LLM fallback). Never assume non-null. The pipeline is designed to
  fall through cleanly (`SpeechSessionOutcome.EmptyTranscript` /
  `NoMappingFound`) when the model isn't loadable.
- **Events fire on thread-pool.** `StatusChanged`, `CommandReady`, and
  `SessionRecorded` all need `Dispatcher.UIThread.Post()` marshalling
  before touching Avalonia state.
- **Rule tie-breaker.** `PhraseologyMapper` prefers rules with **more
  literal tokens** at equal consumed count. For two rules of equal
  pattern length this reduces to "fewer captures wins". Consequence:
  a rule with more explicit literal anchors always beats a shorter,
  vaguer one — so a bare `taxi via {path...}` greedy-rule cannot
  accidentally beat a more specific `taxi via {path...} cross runway
  {crossrwy}` rule when both consume the same input.
- **NATO normalization runs after callsign extraction.**
  `CallsignParser` consumes `"november 346 golf"` first, so the NATO
  normalizer never sees it — it only sees whatever survives. A NATO
  word that survives in isolation (e.g. just `"tango"` in "taxi via
  tango") is collapsed to its letter. The topology disambiguator reads
  `MapContext.TaxiwayNames`, which `MainViewModel.BuildSpeechContext`
  populates from the currently-loaded ground layout. When no layout
  is loaded the set is empty and every NATO word collapses to its
  single letter — which is the right fallback for the common bare-
  taxiway case.
- **A spot hold-short's `{spot}` capture is a destination name.** "hold
  short of spot one seven" → `HS $17` reuses the `{spot}` capture of the
  taxi-to-spot rules, so the same `ResolveDestinationName` post-pass
  joins and validates it against `MapContext.DestinationNames` (the
  layout's parking/spot/helipad names, populated by `BuildSpeechContext`)
  — a mis-transcribed number falls through to the LLM instead of arming
  a hold the route never reaches. Empty set ⇒ shape check only.
- **Runway validation escalates to the top of `Map`.** When a `{rwy}`
  capture fails `TryRecoverRunway`, the post-pass sets a `runwayInvalid`
  flag that propagates through `MatchTokens` / `FindLongestMatch` /
  `Map` and forces a `null` return — even if a shorter runway-less rule
  (e.g. `enter right downwind` → `ERD`) would have matched at the same
  position. The reason: the user explicitly said a runway, and silently
  downgrading to a runway-less rule would strip their intent. The LLM
  fallback (which has the full transcript and the runway list) owns
  recovery for these cases. If you add new runway-bearing rules, this
  behavior applies automatically.
- **Pipe order after STT is `normalize → rule → LLM`.** The LLM
  receives the normalized, filler-stripped transcript, not the raw
  Whisper output. If you add a new normalization pass, make sure both
  mappers see the same input.
- **LLM system prompt rebuilds from `PhraseologyRules`** at startup.
  Adding new rules automatically enriches the LLM's few-shot examples
  — the mapper doesn't need a separate update.
- **Integration tests silently skip** when the LM-Kit model cache is
  cold. `dotnet test` exit-zero does not mean the LLM path is covered.
  Check the test output for skip counts before claiming a change
  lands cleanly.
- **Commands are never auto-dispatched.** The pipeline always writes
  into `CommandText` for user review. Don't "shortcut" this — it's the
  designed safety valve.
- **Niner is controller-only.** FAA 7110.65 2-4-17 requires controllers
  to say "niner" for digit 9; pilots may say either. The STT parser
  accepts both forms. Pilot-speech generators emit "nine" (compliant);
  future controller-speech generators must route digit 9 through a
  separate table that emits "niner".
- **Pilot phraseology is FAA-grounded** (AIM 5-5-10 / 5-5-11). A pilot
  who can't see traffic says "negative contact", not "unable" (which
  7110.65 reserves for refusing a clearance); positive acquisition is
  "traffic in sight", not "tally". Sim-internal diagnostics never appear
  in pilot speech. Pilot transmissions intentionally keep GA
  colloquialisms like "short final" and "inbound" — they are not held to
  controller phraseology.

## Bumping LM-Kit.NET

The package is referenced in two csproj files (`src/Yaat.Client/Yaat.Client.csproj`,
`tools/Yaat.SpeechSandbox/Yaat.SpeechSandbox.csproj`). A version bump has three
non-obvious touchpoints beyond those two lines:

- **`CudaBackendInstaller.BackendVersion` is a hardcoded constant** baked into
  the nuget.org download URL for `LM-Kit.NET.Backend.Cuda13.Windows`. It must
  move in lockstep with the `LM-Kit.NET` version (and the SpeechSandbox's
  explicit `Backend.Cuda13.Windows` reference). `DepsVersion` is separate and
  independent — the `Cuda13.Deps.Windows.Part0/Part1` packages have their own
  release cadence, so leave it unless a newer deps release exists; both being
  CUDA 13 keeps them compatible. Bumping `BackendVersion` changes
  `ExpectedSentinelContent`, which invalidates any on-disk CUDA install and
  re-prompts the download — no extra code needed.
- **The `SQLitePCLRaw.lib.e_sqlite3` pin is independent of the LM-Kit version
  and almost always stays.** LM-Kit → `Microsoft.Data.Sqlite` (metapackage) →
  `SQLitePCLRaw.bundle_e_sqlite3` → `lib.e_sqlite3`; a `Microsoft.Data.Sqlite`
  bump does not move that transitive native binary, so the security pin only
  drops once `bundle_e_sqlite3` itself advances to a patched native. Pins that
  *are* tied to the LM-Kit version (a transitive dependency LM-Kit later
  updated on its own, as happened with `AngleSharp`) drop with the bump.
- **CPU and Vulkan backends are separate transitive packages**
  (`LM-Kit.NET.Backend.Cpu` / `.Vulkan`, pinned to the same version); they
  restore automatically, so the base package still delivers the Vulkan/CPU
  fallback.

Verify with `dotnet build -p:TreatWarningsAsErrors=true` (the NU1902 audit and
the closed-source API-compatibility gate) and the sandbox's `--lmkit-gpus` /
`--lmkit-models` / `--pipeline` runs (native load + STT/LLM API on cached
models).

## Extension Points

- **New command verb** → add to `CommandRegistry`; the GBNF grammar
  expands automatically. Add a rule in the appropriate
  `*Rules()` method (or rely on LLM fallback). Add tests.
- **New phraseology for an existing verb** → add a `PhraseologyRule`
  row; add a unit test in the category's test file.
- **New scenario-derived vocab** → extend `SpeechContext` + the
  `Func<SpeechContext>` provider. Do not touch `WhisperBiasingPrompt`.
- **LLM prompt iteration** → use
  `LocalLlmCommandMapper.GetDefaultSystemPrompt()` as a starting
  point, feed a customized prompt via the override constructor, and
  iterate in the sandbox with `--llm-probe`.
- **New Whisper model** → add it to the filter list surfaced by
  `--yaat-catalog`; verify cold/warm timing with `--lmkit-stt`.

## Related Docs

- [`docs/architecture.md`](architecture.md) — full annotated file tree.
- [`docs/command-aliases/`](command-aliases/) — ATCTrainer and VICE alias data (`atctrainer-commands.json`, `vice-commands.json`, rebuilt by `build.py`).
- [`COMMANDS.md`](../COMMANDS.md) — user-facing command reference.
