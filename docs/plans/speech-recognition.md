# Speech Recognition for YAAT — Implementation Plan

## Context

YAAT users currently type canonical commands (e.g., `FH 270`, `AT CEPIN CAPP`). This side-project adds speech input: the user speaks natural ATC phraseology via push-to-talk, and the system converts it to a canonical command string placed in the existing command input box for review/edit before sending.

**Key challenge**: ATC phraseology → canonical commands requires number conversion, callsign parsing, fix name disambiguation (using per-aircraft programmed fixes), and handling phraseology variations. Fix names are hardest — Whisper will transcribe them phonetically as English words.

## Architecture

```
PTT key held → AudioCaptureService (PortAudioSharp2, 16kHz PCM mono)
  → WhisperSttEngine (Whisper.net, local transcription,
                      initial_prompt seeded with active callsigns + programmed fixes)
  → PhraseologyMapper (rule-based pattern matching)
    ↳ on no match / low confidence → LocalLlmCommandMapper (LLamaSharp, offline GGUF)
  → PhoneticFixMatcher post-pass on any {fix} capture
  → result placed in CommandText for user to review + Enter
```

All NLU logic lives in **Yaat.Sim** (testable, no UI deps). Audio capture and STT engine live in **Yaat.Client**.

## Implementation Phases

### Phase 1: Number & callsign parsing (Yaat.Sim)

Files to create:
- `src/Yaat.Sim/Speech/AtcNumberParser.cs` — spoken numbers → digits
  - "two seven zero" → "270", "five thousand" → "5000", "niner" → "9", "tree" → "3", "fife" → "5"
  - "flight level three five zero" → "35000"
  - Squawk codes: "seven five zero zero" → "7500"
- `src/Yaat.Sim/Speech/CallsignParser.cs` — bidirectional spoken ↔ ICAO callsign conversion
  - **Spoken → ICAO** (recognition path): "Southwest one two three" → "SWA123"
  - **ICAO → spoken** (prompt-seed path, used by `WhisperSttEngine` in Phase 6): "SWA123" → "Southwest one twenty three" (or "one two three"); feeds Whisper's `initial_prompt` so Whisper is primed for the telephony form Whisper actually hears
  - GA: "November one two three four five" ↔ "N12345"
  - Number pronunciation: "seventy two thirteen" → "7213"
  - Fuzzy match against active callsigns list to correct near-misses
- `src/Yaat.Sim/Speech/AirlineTelephony.cs` — static bidirectional map, ~1500–2000 entries
  - **Data source: OpenFlights `airlines.dat`** (ODbL 1.0, https://openflights.org/data.html, raw: https://raw.githubusercontent.com/jpatokal/openflights/master/data/airlines.dat).
  - CSV columns: `id, name, alias, iata, icao, callsign, country, active`. Filter to `active = "Y"`, non-empty ICAO, non-empty callsign; dedupe by ICAO (last-wins or first-wins — pick one and document). Trim trailing whitespace on callsign (some rows have leading/trailing spaces).
  - **Refresh model: checked-in snapshot**, manually refreshed via `tools/refresh-airlines.py` (see below). No build-time downloads.
  - **License obligations (ODbL 1.0)**:
    - `src/Yaat.Sim/Speech/Data/LICENSE-OPENFLIGHTS.txt` — full ODbL 1.0 text, sits next to the data file.
    - `NOTICE` (new file at repo root, or append to existing) — one-line credit: "Airline telephony data derived from OpenFlights (https://openflights.org/data.html), licensed under ODbL 1.0."
    - YAAT's source remains MIT; ODbL applies only to the `airlines.tsv` file itself (§ 4.5a exempts compiled binaries from share-alike).
    - Settings → Speech tab footer: small-text attribution link so end users can trace the data source.
- `tools/refresh-airlines.py` — one-shot fetcher that downloads, filters, normalizes, and writes `airlines.tsv` + a sidecar `airlines-source.meta` recording the upstream SHA and fetch date. Never runs during `dotnet build`; `python tools/refresh-airlines.py` is how we update the data.
- `tests/Yaat.Sim.Tests/Speech/AtcNumberParserTests.cs`
- `tests/Yaat.Sim.Tests/Speech/CallsignParserTests.cs`

Reuse:
- `src/Yaat.Sim/Commands/AltitudeResolver.cs` — `Resolve(string)` returns MSL int; feed `AtcNumberParser` output through it for altitude normalization.
- `src/Yaat.Sim/Commands/CommandRegistry.cs` — `All` / `AliasToCanonicType`; source of truth for the 87 `CanonicalCommandType` values that Phase 2 rules target.

#### Tasks
- [x] `tools/refresh-airlines.py` — fetches OpenFlights `airlines.dat`, dedupes by ICAO with active-preferred tiebreaker, keeps defunct airlines, reports callsign collisions, inline OVERRIDES for known upstream corruptions (ASA Alaska, AVA Avianca)
- [x] `src/Yaat.Sim/Speech/Data/airlines.tsv` — 5,171 rows checked in
- [x] `src/Yaat.Sim/Speech/Data/LICENSE-OPENFLIGHTS.txt` — ODbL 1.0 full text
- [x] `NOTICE` at repo root — OpenFlights + future attribution
- [ ] Settings → Speech tab footer attribution (deferred to Phase 5 as originally planned)
- [x] `src/Yaat.Sim/Speech/AirlineTelephony.cs` — `TryGetTelephony(icao)` + `TryGetIcaos(telephony)`; returns list for shared callsigns so runtime disambiguates via active aircraft
- [x] `src/Yaat.Sim/Speech/AtcNumberParser.cs` — `NormalizeDigits`, `FlightNumberToWords`, `FlightNumberToPairedWords`, `AltitudeToWords`
- [x] `src/Yaat.Sim/Speech/CallsignParser.cs` — `TryParseLeading`/`TryParseTrailing` (airline + US GA + foreign), `IcaoToSpoken` (paired form for airlines, NATO phonetic for unknown), `GetSpokenVariants(callsign, aircraftType, activeCallsigns)` returning all prompt-seedable forms
- [x] `tests/Yaat.Sim.Tests/Speech/AtcNumberParserTests.cs`
- [x] `tests/Yaat.Sim.Tests/Speech/CallsignParserTests.cs`
- [x] `tests/Yaat.Sim.Tests/Speech/AirlineTelephonyTests.cs`
- [x] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean
- [x] `timeout 30 dotnet test 2>&1 | tee .tmp/test.log` green (2640/2640)

Grew during implementation (not in original Phase 1 scope but landed together):
- [x] `src/Yaat.Sim/Speech/ScenarioCallsignExtractor.cs` — extracts custom telephonies from flight-plan remarks via `CALLSIGN "..."`, `CS "..."`, and bare-quoted patterns. Feeds Whisper prompt for ad-hoc per-scenario callsigns (JETLINX, PACK COAST, FLEX MALTA, CIRCADIAN — all real patterns observed in ZOA scenario examples).
- [x] `src/Yaat.Sim/Speech/AircraftTypeNames.cs` + `tools/refresh-aircraft-types.py` — data-driven ICAO type → spoken manufacturer/family names (e.g. `C172` → `cessna` + `skyhawk`, `BE20` → `beech` + `king air`). Source: vNAS `AircraftSpecs.json` (derived from ICAO Doc 8643). Family extraction via cross-designator bigram discovery + manual inclusions (`twin otter`, `global express`).
- [x] `tests/Yaat.Sim.Tests/Speech/ScenarioCallsignExtractorTests.cs`
- [x] `tests/Yaat.Sim.Tests/Speech/AircraftTypeNamesTests.cs`

### Phase 2: Phraseology rule engine (Yaat.Sim)

Files to create:
- `src/Yaat.Sim/Speech/PhraseologyRule.cs` — rule data model
  ```csharp
  record PhraseologyRule(
      string[] Pattern,        // ["climb", "and?", "maintain", "{alt}"]
      string OutputTemplate,   // "CM {alt}"
      CanonicalCommandType Type
  );
  ```
  - `?` suffix = optional token, `{name}` = capture group, literals = case-insensitive match
- `src/Yaat.Sim/Speech/PhraseologyMapper.cs` — the core engine
  - Pre-processes transcript (normalize numbers via AtcNumberParser, strip filler words)
  - Extracts callsign (beginning or end of utterance) via CallsignParser
  - Extracts condition prefixes ("at {fix}", "when level at {alt}")
  - Matches remaining tokens against rules, returns best match + confidence
  - Handles compound commands: "climb and maintain five thousand and fly heading two seven zero" → `CM 050, FH 270`
- `src/Yaat.Sim/Speech/PhraseologyRules.cs` — static rule definitions (~100 rules)
  - Organized by category matching CommandRegistry: heading, altitude, speed, nav, tower, approach, ground, etc.
  - Example rules:

    | Pattern | Output | Notes |
    |---------|--------|-------|
    | `fly heading {hdg}` | `FH {hdg}` | |
    | `heading {hdg}` | `FH {hdg}` | Short form |
    | `turn left heading {hdg}` | `TL {hdg}` | |
    | `turn right heading {hdg}` | `TR {hdg}` | |
    | `climb and? maintain {alt}` | `CM {alt}` | "and" optional |
    | `descend and? maintain {alt}` | `DM {alt}` | |
    | `maintain {alt}` | context-dependent | CM if above current alt, DM if below |
    | `reduce speed to? {spd}` | `SPD {spd}` | |
    | `direct to? {fix}` | `DCT {fix}` | |
    | `proceed direct {fix}` | `DCT {fix}` | |
    | `squawk {code}` | `SQ {code}` | |
    | `cleared approach` | `CAPP` | |
    | `cleared ILS {rwy} approach` | `CAPP ILS{rwy}` | |
    | `cleared for takeoff` | `CTO` | |
    | `line up and wait` | `LUAW` | |
    | `cleared to land` | `CLAND` | |
    | `go around` | `GA` | |
    | `resume normal speed` | `RNS` | |
    | `fly present heading` | `FPH` | |
    | `at {fix} ...` | `AT {fix} ...` | Condition prefix |

- `tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTests.cs` — extensive test cases
  - Pure string-in → string-out, no audio needed
  - Test each rule, plus compound commands, plus edge cases

Existing files to reuse:
- `src/Yaat.Sim/Commands/CommandRegistry.cs` — source of truth for command types/aliases
- `src/Yaat.Sim/Commands/AltitudeResolver.cs` — altitude format normalization

#### Tasks
- [x] `src/Yaat.Sim/Speech/PhraseologyRule.cs` — rule record (`Pattern`, `OutputTemplate`, `Type`) with pattern syntax: literal / literal? (optional) / {name} (capture)
- [x] `src/Yaat.Sim/Speech/PhraseologyMapper.cs` — normalize digits → strip filler → collapse runway designators ("28 right" → "28R") → extract callsign → extract condition prefix → greedy longest-match with compound connectors → "disregard" cancels prior outputs
- [x] `src/Yaat.Sim/Speech/PhraseologyRules.cs` — **163 rules** across 11 categories: heading, altitude/speed, navigation, tower, approach, pattern, hold, helicopter, transponder, ground, broadcast
- [x] `tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTests.cs` — 105 cases: per-category rule coverage, compound commands, callsign leading/trailing, condition prefixes, disregard, edge cases
- [x] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean
- [x] `timeout 120 dotnet test 2>&1 | tee .tmp/test.log` green (2744/2744)
- [x] aviation-sim-expert review vs 7110.65 — corrections applied:
  - Dropped inverted "turn left N degrees" form (7110.65 5-6-2 only specifies "TURN N DEGREES LEFT/RIGHT")
  - Added "MAINTAIN N KNOTS" (7110.65 5-7-2 canonical speed form)
  - Added "REPORT AIRPORT IN SIGHT" (7110.65 5-11-1; kept "field in sight" as colloquial tolerance)
  - Added "EXPECT (type) APPROACH RUNWAY N" form alongside "EXPECT (type) RUNWAY N APPROACH"
  - Fixed visual approach word order: "CLEARED VISUAL APPROACH RUNWAY N" (7110.65 7-4-3.b); removed incorrect inverted form
  - Kept "reduce to final (approach speed)" — user confirms commonly spoken on frequency even though not in the 7110.65 phrase box

### Phase 3: Fix disambiguation (Yaat.Sim)

Files to create:
- `src/Yaat.Sim/Speech/PhoneticFixMatcher.cs`
  - Double Metaphone encoding for phonetic comparison
  - Levenshtein distance on both raw strings and metaphone codes
  - Input: transcribed token + `HashSet<string>` of programmed fixes
  - Output: best match (or null if no match above threshold)
  - Fallback: if no match in programmed fixes, try full NavigationDatabase (slower, lower confidence)
- `tests/Yaat.Sim.Tests/Speech/PhoneticFixMatcherTests.cs`
  - "sepin" → "CEPIN", "sunol" → "SUNOL", "keeping" → "CEPIN" (phonetic), etc.

Existing files to reuse:
- `src/Yaat.Sim/AircraftState.cs:560` — `GetProgrammedFixes()` returns `HashSet<string>` (confirmed).
- `src/Yaat.Sim/ProgrammedFixResolver.cs` — underlying resolver used by `AircraftState`.
- `src/Yaat.Sim/Data/NavigationDatabase.cs` — `AllFixNames` and `GetFixPosition(name)` for the full-DB fallback after programmed-fix lookup misses.
- Tests: use `TestNavDbFactory` + `NavigationDatabase.ScopedOverride(...)`, annotate class with `[Collection("NavDbMutator")]` (pattern lifted from `AltitudeResolverTests.cs`).

#### Tasks
- [x] `src/Yaat.Sim/Speech/PhoneticFixMatcher.cs` — simplified phonetic encoder + Levenshtein; `max(rawDistance, phoneticDistance)` scoring prevents false positives on short phonetic codes
  - Programmed-fix scope: threshold 2 (accepts "sepin" → CEPIN, "seepin" → CEPIN)
  - Full NavigationDatabase fallback: threshold 1 (strict, handles references to non-programmed fixes)
- [x] `tests/Yaat.Sim.Tests/Speech/PhoneticFixMatcherTests.cs` — 25 cases: Phonetize sanity, Levenshtein sanity, exact/near-miss match, empty inputs, threshold rejection
- [x] Wire `PhoneticFixMatcher` into `PhraseologyMapper` as post-pass on `{fix}` and `{current}` captures via `MapContext.ProgrammedFixes`
- [x] Integration tests in `PhraseologyMapperTests.cs`: direct-to-mistranscribed-fix corrected against programmed fixes, raw passthrough when no context, compound commands still work
- [x] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean
- [x] `timeout 120 dotnet test 2>&1 | tee .tmp/test.log` green (2773/2773)

Note: we implemented a simplified phonetic encoder (soft/hard C, PH/F, silent GH, silent leading K/P/W before N, vowel collapse, double-letter collapse) instead of the full ~300-line Lawrence Philips Double Metaphone. It covers the realistic Whisper transcription errors without the code bloat. Phase 8 can upgrade to full Double Metaphone if coverage gaps appear.

### Phase 4: Local LLM fallback (Yaat.Client)

**Local-only.** No network calls, no API keys. User provides a GGUF model file via Settings. If no model is configured, Phase 4 is silently skipped and the pipeline ends at Phase 2 (rule engine) — `LocalLlmCommandMapper` returns `null` rather than throwing, so `SpeechRecognitionService` treats "no fallback configured" and "fallback didn't match" uniformly.

When rule-based matching fails or has low confidence, fall back to a local SLM via **LLamaSharp** (C# bindings for llama.cpp). User-configurable GGUF model — ship with a recommended default (e.g., Phi-4-mini Q4_K_M ~2.5GB) but allow any GGUF.

Files to create:
- `src/Yaat.Client/Services/LocalLlmService.cs`
  - Wraps LLamaSharp: load model on first use, keep in memory, run inference
  - System prompt includes: command reference summary, active callsigns, programmed fixes
  - User prompt: raw transcript → expected canonical command output
  - Returns: candidate command string + confidence heuristic (based on output format validity)
- `src/Yaat.Sim/Speech/ISpeechCommandMapper.cs` — interface for the NLU layer
  - `PhraseologyMapper` (rule-based, in Yaat.Sim) implements it
  - `LocalLlmCommandMapper` (in Yaat.Client, wraps LocalLlmService) implements it

NuGet packages to add (Yaat.Client only):
- `LLamaSharp`
- `LLamaSharp.Backend.Cpu` (or `.Cuda` / `.OpenCL` for GPU)

Settings:
- LLM model path (GGUF file) — user points to their downloaded model
- LLM enabled/disabled toggle (disabled by default until user configures a model)
- GPU layers offload count (0 = CPU only)

#### Tasks
- [x] `src/Yaat.Sim/Speech/ISpeechCommandMapper.cs` — async interface (`Task<MapResult?> MapAsync(...)`) with required `CancellationToken`. `MapContext` and `MapResult` hoisted from nested `PhraseologyMapper.MapContext`/`.MapResult` to top-level types in the `Yaat.Sim.Speech` namespace (PhraseologyMapperTests updated for the rename).
- [x] `src/Yaat.Sim/Speech/PhraseologyCommandMapper.cs` — non-static adapter wrapping the existing static `PhraseologyMapper.Map()`. Keeps the static API as the source of truth; this wrapper just makes it async-interface-compatible.
- [x] `LLamaSharp` **0.26.0** + `LLamaSharp.Backend.Cpu` **0.26.0** added to `src/Yaat.Client/Yaat.Client.csproj`. GPU backend shipping strategy is explicitly deferred to Phase 6 per the plan; CPU-only for Phase 4.
- [x] `src/Yaat.Client/Services/LocalLlmService.cs` — LLamaSharp wrapper. `StatelessExecutor` for single-shot inference, lazy `LLamaWeights.LoadFromFile()` + `ModelParams` (see tuning notes below), chat-ML-ish system/user prompt framing, AntiPrompts `["<|user|>", "<|system|>", "\n\n"]`, `MaxTokens=80`, 512-char output cap. `SemaphoreSlim(1,1)` for sequential inference, `CancellationToken` pass-through. Refactored to take a small `ILlmRuntimeConfig` interface (with `PreferencesLlmRuntimeConfig` adapter) instead of a direct `UserPreferences` dependency — lets opt-in integration tests avoid touching `%LOCALAPPDATA%`.
- [x] `src/Yaat.Client/Services/LocalLlmCommandMapper.cs` — `ISpeechCommandMapper` impl. System prompt is derived directly from `PhraseologyRules.All`, grouped by canonical output so every canonical command appears on one line listing all its natural-language variants. User prompt includes transcript + active callsigns + programmed fixes. `NormalizeOutput()` strips code fences / labels / quotes / trailing prose, uppercases, splits into comma-separated clauses, validates each clause against `CommandRegistry.AliasToCanonicType` with an `AT <fix>` / `LV <alt>` prefix allowance, a 5-token-per-clause cap, and an allowed-character set (`[A-Z0-9+\-./]`). Returns null when validation fails.
- [x] LLM settings fields already landed in Phase 5 — `LocalLlmService` reads `SpeechEnabled`, `LlmModelPath`, `LlmGpuLayers` via `PreferencesLlmRuntimeConfig(prefs)`.
- [x] `tests/Yaat.Client.Tests/LocalLlmCommandMapperNormalizeTests.cs` — 21 theory cases covering valid commands, markdown code fences, label prefixes, compound clauses with condition prefixes, and rejection of natural-language prose.
- [x] `tests/Yaat.Client.Tests/LlmCudaFixture.cs` — xUnit collection fixture: calls `NativeLibraryConfig.All.WithCuda(true).WithAutoFallback(true).WithLogCallback(...)` once before any weight load, auto-discovers CUDA 12.x under `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.*`, sets process-level `CUDA_PATH` + prepends `v12.*/bin` to `PATH`, then holds a shared `LocalLlmService`. All tests in `[Collection("LLM")]` reuse the single loaded model.
- [x] `tests/Yaat.Client.Tests/LlmCudaSanityTest.cs` — ground-truth `[Fact]` that times cold+warm inference and dumps native log lines via `ITestOutputHelper`. Proves CUDA is actually being used rather than silently falling back to CPU.
- [x] `tests/Yaat.Client.Tests/LocalLlmPipelineIntegrationTests.cs` — 10 cases (8 positive + 1 chit-chat rejection + 1 disabled-config guard) with strict exact-match assertions. Uses the fixture's shared service. Silently skips when `tests/Yaat.Client.Tests/TestData/llm/test-model.gguf` is absent.
- [x] `tests/Yaat.Client.Tests/TestData/llm/README.md` — download instructions for Qwen2.5-1.5B-Instruct Q4_K_M (recommended). Model file is gitignored.
- [x] `tests/Yaat.Client.Tests/Yaat.Client.Tests.csproj` — test-only `LLamaSharp.Backend.Cuda12` 0.26.0 reference. Production `Yaat.Client.csproj` stays CPU-only; end-user GPU story is Phase 6 Option B (download-on-configure).
- [x] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean (0 warnings).
- [x] `timeout 120 dotnet test 2>&1 | tee .tmp/test.log` green (Yaat.Sim 2,773 + Yaat.Client 368 = 3,141). Integration suite exact-match on all 10 cases against Qwen2.5-1.5B-Instruct Q4_K_M running on CUDA 12.9 in ~3 seconds.

#### Phase 4 lessons learned (verified on real hardware)

Four fixes turned the integration suite from 2/10 passing at ~97 seconds into 10/10 passing at ~3 seconds. Each one was a surprise worth capturing so Phase 8 and later sessions don't re-discover them:

1. **LLamaSharp 0.26.0 + CUDA 13 is a silent mismatch.** `SystemInfo.GetCudaMajorVersion()` in LLamaSharp (`LLama/Native/Load/SystemInfo.cs`) reads `$CUDA_PATH/version.json` and extracts the major version, then `DefaultNativeLibrarySelectingPolicy` passes that to `NativeLibraryWithCuda`, which computes the runtime path as `runtimes/{os}/native/cuda{majorVersion}/llama.dll`. On a host with CUDA 13 installed as the primary runtime, that path is `runtimes/.../cuda13/` — a folder that does not exist in any published NuGet backend (only `Cuda11` and `Cuda12` packages exist on nuget.org as of 2026-04). `WithAutoFallback(true)` silently falls through to CPU, so inference works but is ~5-7× slower than expected and nothing in the native logs makes the cause obvious unless you register a `WithLogCallback` and read the file-probing trace.
   - **Workaround (what the fixture does)**: on Windows, scan `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.*`, pick the highest installed minor, override `CUDA_PATH` at the process level, and prepend `v12.*/bin` to `PATH` so `LoadLibrary` can resolve `ggml-cuda.dll`'s CUDA runtime dependencies (`cudart64_12.dll`, `cublas64_12.dll`, `cublasLt64_12.dll`). Side-by-side CUDA toolkit installs are NVIDIA-supported, and the override is per-process so other tools on the machine still see the primary CUDA 13.
   - **Long-term**: Phase 6 Option B's `GpuRuntimeDownloader` will ship the matching CUDA runtime DLLs as part of the download-on-configure flow so end users don't need to install the full CUDA toolkit just to run speech recognition.

2. **`StatelessExecutor` + default `SeqMax = 64` is a KV-cache trap.** llama.cpp was failing every inference with `decode: failed to find a memory slot for batch of size 365`. Root cause: `ModelParams.ContextSize` is the **total** KV cache size across all sequences, and LLamaSharp defaults `SeqMax = 64`. With `ContextSize = 2048, SeqMax = 64`, each sequence gets `2048 / 64 = 32 KV slots` — nowhere near enough for our ~2000-token system prompt. Fix: `ContextSize = 4096, SeqMax = 1` since `StatelessExecutor` is single-shot and has no use for parallel sequences. Log output went from `llama_context: backend_ptrs.size() = 1` (CPU only) with failed decodes to `backend_ptrs.size() = 2` (CPU + CUDA) with `layer N: dev = CUDA0` on all 28 Qwen layers.

3. **Alphabetical `CommandRegistry` dump was the wrong prompt strategy for a small instruct model.** The initial Phase 4 prompt dumped every `CommandDefinition` as `{alias} — {label} (arg: {sampleArg})`, sorted by type name. Qwen 1.5B was picking plausible-but-wrong verbs (`CM` for "descend", `FPH` for "fly heading 270", `RD` for "reduce speed") because the label text didn't rhyme with the spoken phrasing the model was trained to match. The fix is to derive the prompt from `PhraseologyRules.All` grouped by `OutputTemplate` — one line per canonical command listing every natural-language variant that maps to it. That's the same `spoken phrasing → canonical` data the Phase 2 rule engine uses, so the two layers stay in sync automatically, and the discrimination signal is exactly what the model needs. Integration suite immediately went from 1/9 strict-match to 9/9 strict-match after this change alone.

4. **Small models are prompt-character-sensitive in surprising ways.** With `Temperature = 0` (greedy sampling), a one-character change in the system prompt — specifically `"Altitudes in feet (5000); flight levels..."` vs `"Altitudes in feet (5000), flight levels..."` — flipped "fly heading two seven zero" from `FH 270` to `FPH` (a completely different canonical command with no argument). Verified by diffing a working scratch run against a failing xunit run. `LocalLlmCommandMapper.BuildSystemPrompt` now carries a prominent warning comment telling future maintainers to re-run `LocalLlmPipelineIntegrationTests` after any cosmetic edits. Phase 8 should either pick a larger/better-instructed model or add automated prompt-regression tests.

Bonus tuning: `Temperature` dropped from `0.1` → `0.0`. Command mapping wants determinism — the same transcript must always yield the same canonical. Greedy sampling also makes the opt-in integration suite reproducible.

#### Phase 4 performance on the dev box (Qwen2.5-1.5B-Instruct Q4_K_M, CUDA 12.9, all 28 layers offloaded)

| Metric | Value |
|---|---|
| Cold model load (1.1 GB GGUF → VRAM) | ~1.2 s |
| Warm inference per command | 85–100 ms |
| Scratch harness full 9-case loop | ~1 s |
| xunit integration suite full 10-case loop | ~3 s |
| Original broken state (CPU, no fixture sharing) | ~97 s, 2/10 passing |

#### Yaat.Scratch repurposed as LLM prompt iteration harness

`tools/Yaat.Scratch/Program.cs` now references `Yaat.Client` + `Yaat.Sim` + `LLamaSharp.Backend.Cuda12`. It auto-discovers CUDA 12 the same way `LlmCudaFixture` does, loads `LocalLlmCommandMapper` + `LocalLlmService`, and runs the same 9 test cases directly through `mapper.MapAsync` for fast dev iteration (~1 s end-to-end vs ~3 s through xunit). Ideal loop for tuning prompts and sampling params without rebuilding the test project.

### Phase 5: Settings UI — "Speech" tab (Yaat.Client)

Speech recognition is entirely opt-in. No models are downloaded or loaded until the user enables the feature and downloads models from the Settings UI.

Files to modify:
- `src/Yaat.Client/Views/SettingsWindow.axaml` — add new `<TabItem Header="Speech">` after "Advanced"
- `src/Yaat.Client/ViewModels/SettingsViewModel.cs` — add observable properties + download commands
- `src/Yaat.Client/Services/UserPreferences.cs` — add Speech fields to `SavedPrefs` + public accessors

**Settings tab layout:**

```
┌─ Speech ─────────────────────────────────────────────────┐
│                                                          │
│  ☐ Enable speech recognition                             │
│                                                          │
│  ── Whisper Model (speech-to-text) ──────────────────    │
│  Status: Not downloaded / Downloaded (142 MB) / Loading  │
│  Model: [base.en ▾]  (dropdown: tiny.en, base.en,       │
│                        small.en, medium.en)              │
│  [Download]  [Delete]                                    │
│                                                          │
│  ── LLM Model (command interpretation) ──────────────    │
│  Status: Not configured                                  │
│  Model path: [________________________] [Browse...]      │
│  (Point to a GGUF file — e.g. phi-4-mini-q4_k_m.gguf)   │
│  GPU layers: [0 ▾]  (0 = CPU only)                      │
│                                                          │
│  ── Push-to-Talk ────────────────────────────────────    │
│  Key: [F12 ▾]  (or configurable keybind)                 │
│  Input device: [Default ▾]                               │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

**Whisper model download**: Models are well-known URLs from Hugging Face (ggerganov/whisper.cpp). The download is async with a progress bar. Models are stored in `%LOCALAPPDATA%/yaat/models/whisper/`.

**LLM model**: User provides their own GGUF file via file browser (too many options to auto-download). Settings just stores the path. Models like Phi-4-mini Q4_K_M can be downloaded from Hugging Face manually or via instructions linked in the UI.

Files to create:
- `src/Yaat.Client/Services/ModelManager.cs` — manages model downloads, paths, status
  - `WhisperModelStatus` (NotDownloaded, Downloading, Ready)
  - `DownloadWhisperModelAsync(modelSize, progress)` — downloads from HF, shows progress
  - `DeleteWhisperModel()`
  - `ValidateLlmModelPath(path)` — checks GGUF file exists and is loadable
  - Models stored in `%LOCALAPPDATA%/yaat/models/` (whisper/ and llm/ subdirs)

**SavedPrefs additions:**
- `bool SpeechEnabled` (default false)
- `string WhisperModelSize` (default "base.en")
- `string LlmModelPath` (default "")
- `int LlmGpuLayers` (default 0)
- `string PttKey` (default "F12")
- `string AudioInputDevice` (default "")

Cross-refs from exploration:
- `src/Yaat.Client/Services/UserPreferences.cs:807` — `SavedPrefs` nested class is where the six new fields land.
- `src/Yaat.Client/Views/SettingsWindow.axaml` — existing tabs (Identity / Servers / Macros / Commands / Advanced); Speech goes after Advanced.
- `WindowGeometryHelper` — use if adding any sub-dialog (e.g. download progress).

#### Tasks
- [x] Add seven `SavedPrefs` fields + public accessors in `UserPreferences.cs` (six speech + `PreferredGpuBackend`; single `SetSpeechSettings` bulk setter). PTT key defaults to `RightCtrl`.
- [x] `src/Yaat.Client/Services/ModelManager.cs` — Whisper download / delete / status + LLM GGUF path validation. HttpClient streaming to `.partial` → rename on completion, IProgress, CancellationToken.
- [x] `src/Yaat.Client/Services/GpuCapabilityDetector.cs` — `NativeLibrary.TryLoad` probes for CUDA (`nvcuda.dll`/`libcuda.so.1`) → Vulkan → Metal (macOS). nvidia-smi shell-out with 500 ms timeout for NVIDIA device name + VRAM. Never throws; degrades to `CpuOnly`.
- [x] `SettingsViewModel.cs` — 11 observable properties, download/cancel/delete commands, GPU detect on ctor, Save() persists via `SetSpeechSettings`, Ptt capture case (allows modifier-only keys only for PTT target), friendly display names for RightCtrl/LeftCtrl/etc.
- [x] `SettingsWindow.axaml` — new `<TabItem Header="Speech">` with Whisper download section (ComboBox + Download/Cancel/Delete buttons + ProgressBar), LLM path + Browse, Acceleration (detected summary + backend combo + GPU layers NumericUpDown), PTT key button, audio device TextBox, ODbL attribution footer.
- [x] `SettingsWindow.axaml.cs` — `OnBrowseLlmModelClick` via `StorageProvider.OpenFilePickerAsync` with `.gguf` filter; extended key-capture wiring to include `PttKeyButton` and the previously-missing `AlwaysOnTopKeyButton`.
- [x] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean (0 warnings).
- [x] `timeout 120 dotnet test 2>&1 | tee .tmp/test.log` — all tests green (Yaat.Sim 2,773 + Yaat.Client 336 = 3,109).
- [ ] Manual smoke test: toggle Enable, change Whisper size, click Download, verify file lands under `%LOCALAPPDATA%/yaat/models/whisper/`, verify PTT button opens capture and `RightCtrl` round-trips, verify GPU detection text matches host.

### Phase 6: Audio capture + STT (Yaat.Client)

Files to create:
- `src/Yaat.Client/Services/AudioCaptureService.cs`
  - **`PortAudioSharp2`** for microphone capture — cross-platform (Windows WASAPI/MME, Linux ALSA/Pulse, macOS CoreAudio)
  - PCM 16 kHz mono — Whisper's native input format
  - Push-to-talk: `StartCapture()` / `StopCapture()` returns `byte[]`
  - Configurable input device via settings (`SavedPrefs.AudioInputDevice`)
- `src/Yaat.Client/Services/WhisperSttEngine.cs`
  - Wraps `Whisper.net` — loads model on first use, transcribes audio buffer
  - `initial_prompt` seeded at each PTT capture with:
    - Active callsigns in **telephony form** ("American one twenty three", "Southwest four fifty six") via `AirlineTelephony.TryGetTelephony(icao)` + `AtcNumberParser` number-to-words — this is what Whisper actually hears, so priming raw ICAO is useless
    - Active callsigns in **ICAO form** too ("AAL123") so Whisper can emit either form
    - Per-aircraft programmed fix names (e.g. "CEPIN SUNOL MENLO") so Whisper biases toward the correct spelling
  - This is **layer A** of the two-layer fix/callsign strategy; `PhoneticFixMatcher` and `CallsignParser` fuzzy match are **layer B** (post-pass, applied in `PhraseologyMapper`)
  - Model file: `ggml-base.en.bin` (~142 MB), downloaded by `ModelManager` in Phase 5

NuGet packages to add (Yaat.Client only):
- `Whisper.net`
- `Whisper.net.Runtime` (CPU default; `.Cuda` / `.CoreML` / `.OpenVino` as stretch)
- `PortAudioSharp2` (replaces NAudio — cross-platform)
- `LLamaSharp` + `LLamaSharp.Backend.Cpu` (see Phase 4)

#### GPU runtime shipping strategy — "Option B" (download-on-configure)

The Phase 4 installer ships `LLamaSharp.Backend.Cpu` only. Phase 5's `GpuCapabilityDetector` can detect CUDA / Vulkan / Metal on the host, but the client has no GPU native libraries to actually call, so `PreferredGpuBackend = "Auto"` always ends up on CPU in practice. Phase 6 resolves this for both LLamaSharp (LLM) and Whisper.net (STT) with a single download-on-configure flow. Alternative was bundling all backends by default (~500 MB per backend × 2 libraries = ~1 GB installer bloat) — rejected as overkill for users who don't use speech recognition at all.

- **`src/Yaat.Client/Services/GpuRuntimeDownloader.cs`** — new service. Fetches a bundled archive of native DLLs per backend (LLamaSharp CUDA12, Whisper.net CUDA, Vulkan, etc.) into `%LOCALAPPDATA%/yaat/runtime/{llama,whisper}/{backend}/`. Archives hosted as GitHub Release assets on the yaat repo so we control the exact versions (matches whatever LLamaSharp + Whisper.net versions this client build was compiled against).
- **Archive contents** — only the native `.dll` files, not the managed assemblies. Source of truth: the `runtimes/` folder inside the corresponding NuGet package for the specific version + RID (e.g. `win-x64/native/cuda12/llama.dll`). A small GitHub Actions workflow can extract and publish these per release.
- **Settings UI** — extend the Speech tab's Acceleration section with a "Download GPU runtime" button that shows when `GpuCapabilityDetector.Detect().Kind != CpuOnly` and no runtime archive has been fetched yet. Reuse the `ModelManager`'s streaming download + `IProgress<double>` + `.partial` → rename pattern.
- **Native library resolution** — at app startup, after `AppLog.Initialize`, probe `%LOCALAPPDATA%/yaat/runtime/llama/{backend}/` and call `NativeLibraryConfig.All.WithSearchDirectory(path).WithCuda(true).WithAutoFallback(true)` (or equivalent for the chosen backend). Whisper.net has an analogous config — needs verifying against the current API during Phase 6.
- **Fallback** — if the user downloaded a CUDA runtime but later removes the CUDA driver, `WithAutoFallback(true)` lets LLamaSharp silently degrade to whatever CPU native is still in the installer. No crash, just slower.
- **Bundle size** — `win-x64/native/cuda12/llama.dll` alone is ~500 MB, Whisper.net's CUDA runtime is ~200 MB. Users who enable GPU pay that one-time ~700 MB download; default installer stays ~200 MB.
- **Test harness precedent** — the opt-in `LocalLlmPipelineIntegrationTests` in `tests/Yaat.Client.Tests/` already ship `LLamaSharp.Backend.Cuda12` as a test-only PackageReference, so the API shape (`NativeLibraryConfig.All.WithCuda(true).WithAutoFallback(true)`) is already validated on dev hardware. Phase 6 just replicates that init in the production `App.axaml.cs` once the download-on-configure path is in place.

Rejected: option A (ship all backends by default). Adds ~1 GB to every installer even for users who never open the Speech tab — the friction isn't justified.

#### Tasks
- [x] Add NuGet packages: `PortAudioSharp2` **1.0.6**, `Whisper.net` **1.9.0**, `Whisper.net.Runtime` **1.9.0** to `src/Yaat.Client/Yaat.Client.csproj`. CPU runtime only; GPU runtimes deferred to the Option B follow-up.
- [x] `src/Yaat.Client/Services/AudioCaptureService.cs` — PortAudioSharp2 wrapper. Lazy `PortAudio.Initialize()` on first `StartCapture`, Float32/16 kHz/mono stream, lock-guarded growing `List<float>` buffer filled from the PortAudio callback thread, `StopCapture()` returns the captured `float[]`. `ListInputDevices()` enumerates input devices for the Settings UI dropdown. Resolves the user's preferred device via exact name match → case-insensitive substring match → `PortAudio.DefaultInputDevice` fallback. Implements `IDisposable` for clean `PortAudio.Terminate()` at app shutdown.
- [x] `src/Yaat.Client/Services/WhisperSttEngine.cs` — Whisper.net wrapper. Lazy `WhisperFactory.FromPath(...)` using the model file resolved by `ModelManager.GetWhisperPath(WhisperModelSize)`. `TranscribeAsync(float[] samples, string initialPrompt, CancellationToken ct)` creates a per-call `WhisperProcessor` via `CreateBuilder().WithLanguage("en").WithPrompt(initialPrompt).Build()`, wraps the captured samples in an in-memory RIFF/WAV container (minimal 44-byte header + IEEE Float32 payload via the private `WavHeader.WriteFloatPcm` helper), and streams segments via `ProcessAsync`. Returns `null` when speech is disabled / model file missing / zero samples / inference throws. `SemaphoreSlim(1,1)` for sequential transcription.
- [x] `src/Yaat.Client/Program.cs` — `ConfigureLlamaSharpNative()` called in `Main()` after `AppLog.Initialize`. Installs `NativeLibraryConfig.All.WithAutoFallback(true).WithLogCallback(...)` so llama.cpp log messages flow into `AppLog`. No `WithCuda(true)` yet because production still ships CPU-only; the `WithSearchDirectory(...)` call lands with the Option B `GpuRuntimeDownloader` in the follow-up session.
- [x] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean (0 warnings).
- [x] `timeout 180 dotnet test 2>&1 | tee .tmp/test.log` green (Yaat.Sim 2,773 + Yaat.Client 368 = 3,141 passing; no regressions from the new services).
- [x] **Option B `GpuRuntimeDownloader.cs`** — fetches `LLamaSharp.Backend.Vulkan.Windows.{version}.nupkg` from nuget.org's flat-container API, streams it to `%LOCALAPPDATA%/yaat/runtime/llama/.{...}.nupkg.partial`, extracts the native DLLs via `System.IO.Compression.ZipFile`. **Remaps the 0.26.0+ `LLamaSharpRuntimes/` top-level folder onto the standard `runtimes/` layout** that LLamaSharp expects when `WithSearchDirectory` is in effect — older packages (≤0.25.0) already used `runtimes/` so they pass through unchanged. Vulkan native files (~57 MB, 5 DLLs) land at `%LOCALAPPDATA%/yaat/runtime/llama/runtimes/win-x64/native/vulkan/*.dll`. Exposes `DownloadLlamaVulkanRuntimeAsync` / `DeleteLlamaVulkanRuntime` / `GetLlamaVulkanStatus`. Version pinned to the `LlamaSharpVersion` constant which must track the `Yaat.Client.csproj` `PackageReference` version.
- [x] Settings UI "Download Vulkan runtime" button in the Speech tab's Acceleration section, with cancel support + progress bar + post-restart instruction. See `SettingsViewModel.DownloadLlamaVulkanRuntime` / `DeleteLlamaVulkanRuntime` / `CancelLlamaVulkanDownload` commands and the matching axaml block.
- [x] **LLM GGUF download flow mirrors the Whisper download flow** — no more Browse-only. `ModelManager` now exposes an `AvailableLlmModels` catalog (Qwen2.5-0.5B/1.5B/3B Instruct Q4_K_M) with stable Hugging Face URLs, plus `DownloadLlmModelAsync(catalogId, progress, ct)` / `DeleteLlmModel(catalogId)` / `GetLlmStatus(catalogId)` / `GetLlmFileSize(catalogId)` / `GetLlmPath(catalogId)` / `FindLlmEntryByPath(path)`. Settings UI Speech tab shows a `ComboBox` of catalog entries with Download/Cancel/Delete buttons + progress bar, same shape as the Whisper section. The Browse... button stays as a secondary "use custom GGUF" flow so users with their own fine-tuned models aren't locked out. Download saves the file to `%LOCALAPPDATA%/yaat/models/llm/{catalogFileName}.gguf` and writes the resolved path into `UserPreferences.LlmModelPath`, so `LocalLlmService` picks it up on next use without any further manual steps.
- [x] `Program.Main` startup: `ConfigureLlamaSharpNative()` now calls `NativeLibraryConfig.All.WithCuda(true).WithVulkan(true).WithAutoFallback(true)` and — if the runtime root directory exists — `WithSearchDirectory(GpuRuntimeDownloader.LlamaSearchRoot)`. LLamaSharp probes both the bin-dir CPU natives and the downloaded GPU natives, with auto-fallback picking the first one that loads.
- [x] **End-to-end Vulkan smoke test** via `tools/Yaat.Scratch`: `--download-vulkan` downloads the nupkg and extracts, `--probe-vulkan` loads the test GGUF through LLamaSharp with `WithSearchDirectory` set. Confirmed on a real system (RTX 4090 + AMD iGPU): LLamaSharp resolves `{LlamaSearchRoot}/runtimes/win-x64/native/vulkan/llama.dll`, discovers 2 Vulkan devices, loads the model onto `Vulkan0 (NVIDIA GeForce RTX 4090)` with all 28 Qwen layers offloaded. No CUDA toolkit required — Vulkan relies on the NVIDIA display driver's bundled `vulkan-1.dll`.
- [x] **CUDA runtime download** — `GpuRuntimeDownloader.DownloadLlamaCudaRuntimeAsync` fetches `LLamaSharp.Backend.Cuda12.Windows.{LlamaSharpVersion}.nupkg` and extracts to `%LOCALAPPDATA%/yaat/runtime/llama/runtimes/win-x64/native/cuda12/*.dll`. Downstream runtime dependency on `cudart64_12.dll` / `cublas64_12.dll` / `cublasLt64_12.dll` is solved by the new `GpuRuntimeDownloader.FindCuda12Toolkit` / `ApplyCudaToolkitToProcess` helpers in `Program.ConfigureCudaToolkit` — at startup, we scan `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.*`, pick the highest minor, override process-level `CUDA_PATH` + prepend its `bin/` to `PATH`. Settings UI Acceleration section shows a "CUDA Toolkit: v12.9 at ..." indicator and gates the "Download LLM CUDA 12" + "Download Whisper CUDA" buttons on that detection (greyed out when toolkit is absent). Verified end-to-end via `tools/Yaat.Scratch --probe-production`: with both Vulkan and CUDA runtimes installed, LLamaSharp's default preference (Cuda > Vulkan > Cpu) picks CUDA0 automatically and all 28 Qwen layers load onto the RTX 4090.
- [x] **Whisper.net GPU runtime download** — `DownloadWhisperVulkanRuntimeAsync` / `DownloadWhisperCudaRuntimeAsync` fetch `Whisper.net.Runtime.Vulkan.{WhisperNetVersion}.nupkg` / `Whisper.net.Runtime.Cuda.Windows.{WhisperNetVersion}.nupkg` and extract them to `%LOCALAPPDATA%/yaat/runtime/whisper/runtimes/{vulkan,cuda}/win-x64/*.dll` by remapping the package's `build/{os}-{arch}/` layout onto `runtimes/{runtime}/{os}-{arch}/` — exactly what Whisper.net's `.targets` file does at compile time, just at download time instead. The native loader hook is `Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath`: setting it to any path under our Whisper runtime root makes Whisper.net's `NativeLibraryLoader.GetRuntimePaths` probe `{GetDirectoryName(LibraryPath)}/runtimes/{runtime}/{os}-{arch}/whisper.dll`. `Program.ConfigureWhisperNetNative` sets it to `{WhisperSearchRoot}/whisper.placeholder` (the file doesn't need to exist — only the directory name matters). Default `RuntimeLibraryOrder` is `[Cuda, Cuda12, Vulkan, CoreML, OpenVino, Cpu, CpuNoAvx]`, so GPU backends beat CPU automatically. Verified end-to-end via `tools/Yaat.Scratch --probe-whisper-vulkan`: Whisper.net probes all search roots, finds `runtimes/vulkan/win-x64/whisper.dll` under the downloaded runtime, loads it successfully, and `RuntimeOptions.LoadedLibrary` reports `Vulkan`.
- [ ] **GitHub Actions workflow to publish per-backend native archives** — no longer needed because the downloader fetches directly from nuget.org's flat-container API. The nupkg files are already versioned, signed, and CDN-hosted by Microsoft; building a parallel delivery channel would just add maintenance burden.
- [ ] **Deferred to Phase 7**: Manual end-to-end — hold PTT, speak one command, confirm `WhisperSttEngine` returns non-empty transcript. Requires the PTT key handler and `SpeechRecognitionService` orchestration that Phase 7 delivers.
- [ ] **Deferred to Phase 8**: opt-in `WhisperSttEngine` integration test with a canned WAV fixture (similar to `LocalLlmPipelineIntegrationTests`).

#### Risks / unknowns
- PortAudio native lib loading on Linux (`libportaudio2` package) — verify before shipping; document in README. PortAudioSharp2 ships pre-compiled natives for Linux/macOS/Windows in the NuGet package, so the `libportaudio2` system package may not be required at all — to be confirmed during Phase 7 manual testing.
- Windows default input device selection — confirm `PortAudioSharp2` defaults to WASAPI shared-mode. Phase 7 manual test will surface any latency / exclusivity issues.
- First-use latency of Whisper model load — warm on Settings toggle rather than on first PTT press to avoid a 1-2s dead key. Phase 7 wire-up can call `WhisperSttEngine.TranscribeAsync` with a 0.1s silent buffer during Settings save to force the factory load.
- `NativeLibraryConfig` must be called before the first LLamaSharp weight load — handled in `Program.Main` via `ConfigureLlamaSharpNative()`. Phase 7 must NOT create a `LocalLlmService` before `Program.Main` completes (not an issue in practice because the service is owned by `MainViewModel`, which is constructed by `MainWindow` inside the Avalonia lifetime, well after `Main` returns).

### Phase 7: Client integration (Yaat.Client)

Files to create:
- `src/Yaat.Client/Services/SpeechRecognitionService.cs`
  - Orchestrates: AudioCapture → WhisperSTT → PhraseologyMapper (→ LLM fallback) → set CommandText
  - Holds reference to simulation state for callsigns + programmed fixes context

Files to modify:
- `src/Yaat.Client/ViewModels/MainViewModel.cs` — add PTT keybind handling, wire SpeechRecognitionService
- `src/Yaat.Client/Views/MainWindow.axaml` — microphone status indicator (small icon in status area)
- Reads PTT key, audio device, model paths from UserPreferences (configured in Phase 5)

#### Tasks
- [x] `src/Yaat.Client/Services/SpeechRecognitionService.cs` — orchestrator. Holds `AudioCaptureService`, `WhisperSttEngine`, `PhraseologyCommandMapper` (rule engine adapter from Phase 4), and the optional `LocalLlmCommandMapper` fallback. `StartPtt()` → `_audio.StartCapture`, `StopPtt()` → `_audio.StopCapture` → fire-and-forget `Task.Run(ProcessPipelineAsync)`. Pipeline fires `StatusChanged` (`Idle`/`Recording`/`Transcribing`/`Mapping`/`Error`) and `CommandReady(SpeechResult)` events on the thread pool. Simulation context queried via a caller-supplied `Func<SpeechContext>` delegate so the service stays free of any Avalonia / MVVM references.
- [x] `MainViewModel.cs` — instantiates the pipeline services in order (`AudioCaptureService`, `WhisperSttEngine`, `LocalLlmService` + adapter, `PhraseologyCommandMapper`, `LocalLlmCommandMapper`, then `SpeechRecognitionService`) and subscribes to `StatusChanged` + `CommandReady`. `BuildSpeechContext()` snapshots `Aircraft` callsigns, runs `ProgrammedFixResolver.Resolve(...)` for the `SelectedAircraft`, and composes a Whisper `initial_prompt` from ICAO callsigns + their first spoken variant via `CallsignParser.GetSpokenVariants` + programmed-fix names. `HandleSpeechServiceCommandReady` marshals to the UI thread via `Dispatcher.UIThread.Post` and writes to `CommandText` — canonical command when available, else raw transcript so the user can correct manually. `SpeechService` exposed as a public getter so `MainWindow` can reach it from the key handlers. New `[ObservableProperty] SpeechStatus _speechStatus` for the mic-indicator binding.
- [x] `MainWindow.axaml.cs` — added `_pttKey` / `_pttModifiers` fields with `Key.RightCtrl` / `KeyModifiers.None` defaults. `ApplyKeybinds` now parses `prefs.PttKey` via the existing `SettingsViewModel.ParseKeybind` helper. `OnKeyDown` start-of-PTT handler calls `vm.SpeechService.StartPtt()` — Windows key repeats are harmless because `StartPtt` is a no-op when `Status != Idle`. New `OnKeyUp` override stops PTT when the same key is released AND the service is currently `Recording`. `IsPttKeyEvent` helper special-cases bare-modifier keybinds (RightCtrl and friends): when the stored PTT key is itself a modifier, match by `e.Key` alone since `e.KeyModifiers` will also include the modifier's own flag, making a strict equality comparison impossible.
- [x] `MainWindow.axaml` — new mic status indicator in the status bar, bound to `SpeechStatus` as `"mic: {status}"`. Visible only when `Preferences.SpeechEnabled`. Plain text for MVP — Phase 8 can add a colored dot if the distinct states need more visual prominence.
- [x] **Structured per-stage logging** — `SpeechRecognitionService.ProcessPipelineAsync` logs every stage transition via `AppLog.LogInformation` / `LogError`, including the raw Whisper transcript, the rule-engine result, and the LLM fallback outcome. Users can open `yaat-client.log` and post-mortem any failed PTT session.
- [x] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean (0 warnings).
- [x] `timeout 180 dotnet test 2>&1 | tee .tmp/test.log` green (Yaat.Sim 2,773 + Yaat.Client 368 = 3,141 passing). No regressions.
- [ ] **Deferred to follow-up session**: Speech debug panel toggleable via `SavedPrefs.SpeechDebugPanelVisible`. The 9-step breakdown is already in the structured log, so the panel is purely a UX enhancement — it doesn't add recognition capability, just makes prompt/rule tuning faster without needing log-file inspection. Natural Phase 8 territory.
- [ ] **Manual end-to-end test** — user-side task: hold PTT, speak "climb and maintain five thousand", confirm "CM 5000" appears in the command input. Prereqs: enable speech recognition in Settings, download a Whisper model (e.g. `base.en`) via the Settings Speech tab, and — if running with CUDA 13 as primary CUDA — install CUDA 12 side-by-side for GPU acceleration (or accept CPU fallback). Phase 4's test harness covers the LLM half; this manual test covers the audio + Whisper + pipeline integration.

### Phase 8: Iteration & tuning

- Run against real ATC phraseology recordings, expand rule set
- Tune phonetic fix matching thresholds
- Add more airline telephony designators
- Handle edge cases: corrections ("correction, climb maintain six thousand"), readbacks, multi-aircraft

#### Tasks
- [ ] Collect ≥20 real ATC phraseology recordings (or transcripts) and log mapper hit rate
- [ ] Expand `PhraseologyRules.cs` from observed gaps
- [ ] Tune `PhoneticFixMatcher` Levenshtein / metaphone thresholds against observed mistranscriptions
- [ ] Extend telephony designator map (target: top-100 US airlines)
- [ ] Handle "correction, …" self-correction phrase — drop prior utterance tokens
- [ ] Handle readbacks (user speaks pilot readback, not own instruction) — detect and ignore or warn

## Key design decisions

1. **Whisper.net local** — free, offline, no account. Fix and callsign recognition handled by two complementary mechanisms: (a) Whisper's `initial_prompt` is seeded with active callsigns (in both ICAO and telephony forms) + per-aircraft programmed fixes at capture start to bias recognition; (b) `PhoneticFixMatcher` runs as a post-pass on any `{fix}` capture to correct residual mistranscriptions (Double Metaphone + Levenshtein against the programmed-fix set, then full `NavigationDatabase.AllFixNames` as fallback).
2. **Hybrid NLU** — rule-based first (~100 patterns for standard 7110.65 phraseology), local LLM fallback via LLamaSharp for unrecognized phrases (optional, user provides GGUF model). Entire pipeline runs offline.
3. **Place in input box** — user reviews, edits if needed, presses Enter. Zero risk of wrong commands.
4. **Push-to-talk** — matches ATC mental model, avoids false triggers, simpler than VAD.
5. **NLU in Yaat.Sim** — fully testable without audio, hundreds of string-in/string-out test cases.

## Verification

- **Unit tests**: `AtcNumberParser`, `CallsignParser`, `PhraseologyMapper`, `PhoneticFixMatcher` — all pure functions under `Yaat.Sim/Speech/**`, no audio needed.
- **Integration test**: `tests/Yaat.Sim.Tests/Speech/SpeechPipelineTests.cs` — feed transcript strings through `PhraseologyMapper` with real programmed-fix sets (via `TestNavDbFactory` + `NavigationDatabase.ScopedOverride`) and assert canonical output end-to-end.
- **Manual test**: Run client, hold PTT, speak commands, verify text appears in input box correctly. Primary target Windows; Linux/macOS stretch.
- **Build gate**: `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` — zero warnings per CLAUDE.md.
- **Test gate**: `timeout 30 dotnet test 2>&1 | tee .tmp/test.log` — all existing Yaat.Sim.Tests still green (no regressions).
