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
- [ ] `src/Yaat.Sim/Speech/PhraseologyRule.cs` — rule record (`Pattern`, `OutputTemplate`, `Type`)
- [ ] `src/Yaat.Sim/Speech/PhraseologyMapper.cs` — preprocess, callsign extract, condition prefix, rule match, compound handling
- [ ] `src/Yaat.Sim/Speech/PhraseologyRules.cs` — ≥50 rules spanning every `CommandRegistry` category (heading, alt, speed, nav, tower, approach, ground, hold, pattern)
- [ ] `tests/Yaat.Sim.Tests/Speech/PhraseologyMapperTests.cs` — rule coverage + compound commands + edge cases
- [ ] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean
- [ ] `timeout 30 dotnet test 2>&1 | tee .tmp/test.log` green

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
- [ ] `src/Yaat.Sim/Speech/PhoneticFixMatcher.cs` — Double Metaphone + Levenshtein, programmed-fix scope, full-DB fallback
- [ ] `tests/Yaat.Sim.Tests/Speech/PhoneticFixMatcherTests.cs` — `[Collection("NavDbMutator")]`, mistranscription cases
- [ ] Wire `PhoneticFixMatcher` into `PhraseologyMapper` as post-pass on any `{fix}` capture
- [ ] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean
- [ ] `timeout 30 dotnet test 2>&1 | tee .tmp/test.log` green

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
- [ ] `src/Yaat.Sim/Speech/ISpeechCommandMapper.cs` — interface returning nullable canonical command string
- [ ] `PhraseologyMapper` implements `ISpeechCommandMapper` (retrofit)
- [ ] `src/Yaat.Client/Services/LocalLlmService.cs` — LLamaSharp wrapper, lazy model load, system prompt builder
- [ ] `src/Yaat.Client/Services/LocalLlmCommandMapper.cs` — `ISpeechCommandMapper` impl wrapping `LocalLlmService`, returns `null` when disabled or no match
- [ ] Add LLM settings fields (see Phase 5 `SavedPrefs`)
- [ ] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean

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
- [ ] Add six `SavedPrefs` fields + public accessors in `UserPreferences.cs`
- [ ] `src/Yaat.Client/Services/ModelManager.cs` — Whisper download / delete / status + LLM GGUF path validation
- [ ] `SettingsViewModel.cs` — observable properties, download command, browse command
- [ ] `SettingsWindow.axaml` — new `<TabItem Header="Speech">` with the layout above, including small-text footer "Airline data: OpenFlights (ODbL 1.0)" with hyperlink to https://openflights.org/data.html (ODbL attribution requirement from Phase 1)
- [ ] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean
- [ ] Manual: toggle Enable, download base.en model, confirm status updates and model file appears under `%LOCALAPPDATA%/yaat/models/whisper/`

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

#### Tasks
- [ ] Add NuGet packages listed above
- [ ] `src/Yaat.Client/Services/AudioCaptureService.cs` — PortAudio init, device enumeration, start/stop, 16 kHz mono buffer
- [ ] `src/Yaat.Client/Services/WhisperSttEngine.cs` — Whisper.net wrapper, initial_prompt seeding, async transcribe
- [ ] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean
- [ ] Manual: hold PTT, speak one command, confirm `WhisperSttEngine` returns non-empty transcript

#### Risks / unknowns
- PortAudio native lib loading on Linux (`libportaudio2` package) — verify before shipping; document in README.
- Windows default input device selection — confirm `PortAudioSharp2` defaults to WASAPI shared-mode.
- First-use latency of Whisper model load — warm on Settings toggle rather than on first PTT press to avoid a 1-2s dead key.

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
- [ ] `src/Yaat.Client/Services/SpeechRecognitionService.cs` — orchestrate audio → STT → mapper → fix post-pass → `CommandText`
- [ ] `MainViewModel.cs` — PTT down/up handlers, hold `SpeechRecognitionService`, push transcript into command input
- [ ] `MainWindow.axaml` — mic status indicator (idle / recording / transcribing / error)
- [ ] Build + manual end-to-end test: hold PTT, speak "climb and maintain five thousand", confirm `CM 050` appears in the command box
- [ ] `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` clean
- [ ] `timeout 30 dotnet test 2>&1 | tee .tmp/test.log` green

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
