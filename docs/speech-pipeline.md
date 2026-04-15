# YAAT Speech-to-Text Pipeline

Reference for how push-to-talk (PTT) speech recognition works in YAAT today.
Read this before changing anything under `src/Yaat.Sim/Speech/`,
`src/Yaat.Client/Services/Whisper*`, `src/Yaat.Client/Services/LocalLlm*`,
`src/Yaat.Client/Services/SpeechRecognitionService.cs`, or
`tools/Yaat.SpeechSandbox/`.

For the historical implementation design (decisions, phase breakdown, why
LLamaSharp+Whisper.net was chosen and later replaced), see
[`docs/plans/speech-recognition.md`](plans/speech-recognition.md). This
document describes the **current** pipeline, not the one in that plan.

## Overview

User holds the PTT key. Audio is captured as 16 kHz mono float32, Whisper
(via LM-Kit) transcribes it with a statically-built biasing prompt,
spoken numbers are normalized to digits, a rule-based phraseology mapper
tries first, and an LLM fallback (also via LM-Kit, grammar-constrained to
the canonical command surface) runs if no rule matches. The final
canonical command string is placed in the command input box — the user
reviews it and presses Enter to dispatch. Nothing auto-sends.

```
PTT key down
  └─ AudioCaptureService (16 kHz mono float32)
      └─ WhisperSttEngine.TranscribeAsync(samples, initialPrompt)
          ├─ LMKit.Speech.SpeechToText + LM.LoadFromModelID(...)
          └─ Prompt = WhisperBiasingPrompt.Default
                      + SpeechContext.WhisperInitialPrompt
                        (scenario callsigns + programmed fixes)
      └─ raw transcript
          └─ AtcNumberParser.NormalizeDigits
              (spoken → digit form, strip fillers)
              └─ PhraseologyCommandMapper (rule engine)
                  ├─ match → MapResult
                  └─ no-match → LocalLlmCommandMapper
                                (grammar-constrained LLM fallback)
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
  unless a user PTT raced it to another state.

## STT Stage — LM-Kit Whisper

- `src/Yaat.Client/Services/WhisperSttEngine.cs` wraps
  `LMKit.Speech.SpeechToText`. The Whisper `LM` is lazy-loaded on the
  first `TranscribeAsync` call using the model ID from
  `UserPreferences.WhisperModelSize` (e.g. `whisper-base`,
  `whisper-large-turbo3`). Subsequent calls reuse the cached `LM` and
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
- **Model selection:** `whisper-base` is adequate for quick dev loops;
  `whisper-large-turbo3` is the production default (~700 ms for 7 s of
  audio on an RTX 4090 + CUDA 13). See the LM-Kit engine decision notes
  for probe results on N-number recognition.
- **Licensing:** every process funnels through
  `src/Yaat.Client/Services/LmKitLicense.Initialize()` exactly once
  before touching LM-Kit. Lookup order: `LMKIT_LICENSE_KEY` env var →
  `.env` line walked up from `AppContext.BaseDirectory` →
  `[assembly: AssemblyMetadata("LmKitLicenseKey", ...)]` baked into
  `Yaat.Client.dll` by MSBuild → empty (Community). Release builds
  inject the key via the `LmKitLicenseKey` MSBuild property in
  `release.yml`.

## Biasing Prompt

- `src/Yaat.Sim/Speech/WhisperBiasingPrompt.cs` — **static** prompt
  built once and cached. Three vocabulary sets unioned:
  1. Full NATO phonetic alphabet (`alpha` … `zulu`).
  2. Phonetic numbers + ATC variants (`tree`, `fife`, `niner`) plus
     magnitude words (`hundred`, `thousand`, `flight`, `level`, …).
  3. Every distinct literal pattern token from
     `PhraseologyRules.All` — so every word the rule engine knows how
     to map is primed into Whisper.
- The prompt is deliberately **static**. Per-PTT dynamic additions were
  probed and abandoned: `whisper-large-turbo3` recognized N-number tail
  numbers cleanly without per-callsign biasing, and avoiding per-PTT
  rebuilds eliminates the 224-token decoder-context truncation risk.
- Dynamic scenario vocabulary (active callsigns, programmed fixes) is
  delivered separately via `SpeechContext.WhisperInitialPrompt` and
  concatenated onto the static prompt by
  `SpeechRecognitionService` before each PTT transcription. If you need
  to add new scenario-derived vocab, extend `SpeechContext`, **not**
  `WhisperBiasingPrompt`.

## Digit Normalization & Filler Strip

- `src/Yaat.Sim/Speech/AtcNumberParser.cs` —
  - `NormalizeDigits(transcript)` collapses spoken number forms to
    digit strings: `"two eight right"` → `"28R"`,
    `"flight level three five zero"` → `"FL350"`,
    `"five thousand"` → `"5000"`. Handles `niner/tree/fife`.
  - `FlightNumberToWords` / `AltitudeToWords` are the reverse direction,
    used to seed the Whisper `initial_prompt` in natural-English forms
    pilots actually speak.
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
  ~160 rules grouped into category builders: `HeadingRules`,
  `AltitudeSpeedRules`, `NavigationRules`, `TowerRules`, `ApproachRules`,
  `PtacRules`, `PatternRules`, `HoldRules`, `HelicopterRules`,
  `TransponderRules`, `GroundRules`, `BroadcastRules`. `Build()` (line 48)
  concatenates them into `PhraseologyRules.All`. Each category mirrors
  the grouping in `CommandRegistry.*Commands`.
- Rules are `PhraseologyRule` records:
  `(tokens, canonical-template, canonical-command-type)`. Tokens are
  literal strings (`"cleared"`), captures (`"{rwy}"`, `"{fix}"`,
  `"{callsign}"`, `"{taxiway}"`, …), or optional literals (`"of?"`,
  `"the?"`).
- `src/Yaat.Sim/Speech/PhraseologyMapper.cs` — `Map(transcript, context)`
  tokenizes, strips fillers, extracts the callsign via
  `CallsignParser`, extracts a condition prefix (`AT {fix}`,
  `when level at {alt}` → `LV {alt}`), then does a greedy
  left-to-right longest-match against `PhraseologyRules.All`. Multiple
  clause matches are concatenated with commas. Tie-break rule:
  **literal-only rules beat rules with captures** at the same length —
  so a new rule that adds a capture in place of a literal will *lose*
  the tie to the literal it's trying to replace. Returns `null` if no
  rule matched any part of the transcript.
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
- `src/Yaat.Sim/Speech/CallsignParser.cs` — bidirectional spoken ↔ ICAO
  callsign conversion. Uses `AirlineTelephony` (~2000-entry map built
  from OpenFlights airlines.dat) and `AircraftTypeNames`. Fuzzy-matches
  against `MapContext.ActiveCallsigns` to recover from near-miss
  transcriptions.

## LLM Fallback Mapper

- `src/Yaat.Client/Services/LocalLlmService.cs` — owns the LLM `LM` and
  a `SingleTurnConversation` configured with
  `Sampling = new GreedyDecoding()` (deterministic — don't swap for
  `RandomSampling`) and a `Grammar` set from
  `CanonicalCommandGrammar.Default`. Lazy-loads the model on first
  `GenerateAsync` or `PrewarmAsync`.
- `src/Yaat.Client/Services/LocalLlmCommandMapper.cs` —
  `ISpeechCommandMapper` backed by `LocalLlmService`. Flow:
  1. `BuildUserPrompt(transcript, context)` (line 86) — wraps the
     normalized transcript with scenario context.
  2. `_llm.GenerateAsync(_systemPrompt, userPrompt,
     CanonicalCommandGrammar.Default, ct)` (line 66) — grammar-constrained
     inference.
  3. `NormalizeOutput(raw)` (line 166) — defence-in-depth validation
     against condition prefixes (`AT`/`LV`), verb whitelist, and
     `[A-Z0-9.+/-]` arg charset. Returns `null` and logs the raw output
     if anything fails.
  4. Returns `MapResult` on success, `null` otherwise.
  - `BuildSystemPrompt()` (line 106) derives few-shot examples from
    `PhraseologyRules` at startup, so new rules automatically enrich
    the LLM's priors. An override constructor lets the sandbox tool
    iterate on prompt phrasing without rebuilding the class.
  - `GetDefaultSystemPrompt()` (line 51) exposes the default so the
    sandbox can fetch it as the starting point for probe-mode edits.
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

## Orchestration — `SpeechRecognitionService`

- `src/Yaat.Client/Services/SpeechRecognitionService.cs` — pure service
  layer, **no MVVM/Avalonia dependencies**. Consumes a
  `Func<SpeechContext>` so the caller (typically `MainViewModel`)
  controls how scenario state is observed and the service stays
  decoupled from the view model.
- **State machine** (`SpeechStatus`): `Idle`, `Warming`, `Recording`,
  `Transcribing`, `Mapping`, `Error`. `Warming` is treated as
  start-legal — `StartPtt` can fire mid-prewarm because the engine
  locks serialize the in-flight load against the user press.
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
- `src/Yaat.Client/Views/SpeechDebugWindow.axaml.cs` — post-mortem UI
  listing recent PTT sessions with transcript, canonical command,
  elapsed times, and outcome. Updated via `SessionRecorded`.

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

No flag → Avalonia GUI for interactive probing.

## Tests & Audio Fixtures

- `tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTests.cs` — unit tests
  per rule category. Ground rules are exercised as `[InlineData]` theory
  rows around line 457 (`"pushback approved" → "PUSH"`,
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
- Tests that exercise navigation fixes rely on
  `TestVnasData.EnsureInitialized()` to load real `NavData.dat` and
  `FAACIFP18.gz`. Falls through silently if the test data files aren't
  present.

## Current Ground-Ops Coverage

`GroundRules()` in `src/Yaat.Sim/Speech/PhraseologyRules.cs:354` covers:

- `"pushback approved"` / `"push back approved"` → `PUSH`
- `"hold position"` → `HOLD`
- `"resume taxi"` / `"continue taxi"` → `RES`
- `"cross runway {rwy}"` → `CROSS {rwy}`
- `"hold short of? runway {rwy}"` → `HS {rwy}`
- `"hold short of? {taxiway}"` → `HS {taxiway}`
- `"follow the? {callsign} on ground"` → `FOLLOWG {callsign}`
- `"give way to {callsign}"` → `GIVEWAY {callsign}`
- `"exit left/right [at? {taxiway}]"` → `EL/ER [taxiway]`
- `"exit at? {taxiway}"` → `EXIT {taxiway}`

**`TAXI` is deliberately absent** — see the block comment above the
rule body. Valid ATC phraseology (`"runway 28R, taxi via bravo charlie"`,
`"taxi via delta hotel"`) needs multi-token path captures *and* phonetic
letter-to-ID conversion (`"bravo"` → `B`), neither of which the current
rule engine supports. Transcripts fall through to the LLM fallback or
manual entry until multi-token captures land.

**The canonical form side is already ready.** The ground-command parser
(`src/Yaat.Sim/Commands/GroundCommandParser.cs`) and
`ParsedCommand.cs:305-322` already accept everything the example
phrases need:

- `TAXI {taxiway-list} [RWY {rwy}] [HS ...] [CROSS ...] [@{parking}|${spot}] [NODEL]`
  — trailing runway is auto-detected, so `TAXI T U W 28R` works without
  an explicit `RWY` keyword.
- `PUSH [{taxiway}] [{heading|facing-taxiway}]`
- `PUSH @{parking} [{heading|facing-taxiway}]`
- `PUSH ${spot} [{heading|facing-taxiway}]`

The gap for full STT ground-ops coverage is purely in mapping
transcripts into those canonical strings, not in the command
infrastructure.

## Gotchas & Non-Obvious Behaviour

- **Whisper prompt is static.** Adding scenario-derived vocab must
  happen via `SpeechContext.WhisperInitialPrompt`, **not** by rebuilding
  `WhisperBiasingPrompt.Default`. Rebuilding the static prompt per PTT
  reintroduces the 224-token truncation risk that's currently avoided.
- **`GreedyDecoding` is intentional** for command mapping. Don't swap
  in `RandomSampling` — determinism is load-bearing for tests and user
  trust.
- **LM-Kit load failures return `null`** from `TranscribeAsync` /
  `GenerateAsync`. Never assume non-null. The pipeline is designed to
  fall through cleanly (`SpeechSessionOutcome.EmptyTranscript` /
  `NoMappingFound`) when the model isn't loadable.
- **Events fire on thread-pool.** `StatusChanged`, `CommandReady`, and
  `SessionRecorded` all need `Dispatcher.UIThread.Post()` marshalling
  before touching Avalonia state.
- **Rule tie-breaker.** `PhraseologyMapper` prefers literal-only rules
  over rules with captures at the same token count. New rules that add
  a capture in place of a literal token will lose the tie to the
  literal-only rule and not match.
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

- [`docs/plans/speech-recognition.md`](plans/speech-recognition.md) —
  historical implementation plan and design rationale.
- [`docs/architecture.md`](architecture.md) — full annotated file tree.
- [`docs/command-aliases/reference.md`](command-aliases/reference.md) —
  canonical verb + alias reference.
- [`COMMANDS.md`](../COMMANDS.md) — user-facing command reference.
