# Speech Recognition for YAAT — Implementation Plan

## Context

YAAT users currently type canonical commands (e.g., `FH 270`, `AT CEPIN CAPP`). This side-project adds speech input: the user speaks natural ATC phraseology via push-to-talk, and the system converts it to a canonical command string placed in the existing command input box for review/edit before sending.

**Key challenge**: ATC phraseology → canonical commands requires number conversion, callsign parsing, fix name disambiguation (using per-aircraft programmed fixes), and handling phraseology variations. Fix names are hardest — Whisper will transcribe them phonetically as English words.

## Architecture

```
PTT key held → AudioCaptureService (NAudio, PCM buffer)
  → WhisperSttEngine (Whisper.net, local transcription)
  → PhraseologyMapper (rule-based pattern matching)
    ↳ on no match → LLM fallback (Claude API, optional)
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
- `src/Yaat.Sim/Speech/CallsignParser.cs` — spoken callsigns → ICAO format
  - Airline telephony → ICAO: "Southwest" → "SWA", "United" → "UAL", "Delta" → "DAL" (~50 US airlines)
  - GA: "November one two three four five" → "N12345"
  - Number pronunciation: "seventy two thirteen" → "7213"
  - Fuzzy match against active callsigns list to correct near-misses
- `tests/Yaat.Sim.Tests/Speech/AtcNumberParserTests.cs`
- `tests/Yaat.Sim.Tests/Speech/CallsignParserTests.cs`

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
- `src/Yaat.Sim/Helpers/AltitudeResolver.cs` — altitude format normalization

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
- `src/Yaat.Sim/AircraftState.cs` — `GetProgrammedFixes()` returns `HashSet<string>`
- `src/Yaat.Sim/ProgrammedFixResolver.cs` — the underlying resolver

### Phase 4: Local LLM fallback (Yaat.Client)

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

### Phase 6: Audio capture + STT (Yaat.Client)

Files to create:
- `src/Yaat.Client/Services/AudioCaptureService.cs`
  - NAudio for microphone capture (PCM 16kHz mono — Whisper's expected format)
  - Push-to-talk: `StartCapture()` / `StopCapture()` returns `byte[]`
  - Configurable input device in settings
- `src/Yaat.Client/Services/WhisperSttEngine.cs`
  - Wraps `Whisper.net` — loads model on first use, transcribes audio buffer
  - `initial_prompt` seeded with active callsigns + programmed fixes to bias recognition
  - Model file: `ggml-base.en.bin` (~142MB) bundled or downloaded on first use

NuGet packages to add (Yaat.Client only):
- `Whisper.net`
- `Whisper.net.Runtime.Cpu` (or `.Cuda` for GPU)
- `NAudio` (Windows) — evaluate cross-platform options for Linux/macOS later
- `LLamaSharp` + `LLamaSharp.Backend.Cpu` (or `.Cuda`) — see Phase 4

### Phase 7: Client integration (Yaat.Client)

Files to create:
- `src/Yaat.Client/Services/SpeechRecognitionService.cs`
  - Orchestrates: AudioCapture → WhisperSTT → PhraseologyMapper (→ LLM fallback) → set CommandText
  - Holds reference to simulation state for callsigns + programmed fixes context

Files to modify:
- `src/Yaat.Client/ViewModels/MainViewModel.cs` — add PTT keybind handling, wire SpeechRecognitionService
- `src/Yaat.Client/Views/MainWindow.axaml` — microphone status indicator (small icon in status area)
- Reads PTT key, audio device, model paths from UserPreferences (configured in Phase 5)

### Phase 8: Iteration & tuning

- Run against real ATC phraseology recordings, expand rule set
- Tune phonetic fix matching thresholds
- Add more airline telephony designators
- Handle edge cases: corrections ("correction, climb maintain six thousand"), readbacks, multi-aircraft

## Key design decisions

1. **Whisper.net local** — free, offline, no account. Fix names handled by post-processing phonetic match against programmed fixes.
2. **Hybrid NLU** — rule-based first (~100 patterns for standard 7110.65 phraseology), local LLM fallback via LLamaSharp for unrecognized phrases (optional, user provides GGUF model). Entire pipeline runs offline.
3. **Place in input box** — user reviews, edits if needed, presses Enter. Zero risk of wrong commands.
4. **Push-to-talk** — matches ATC mental model, avoids false triggers, simpler than VAD.
5. **NLU in Yaat.Sim** — fully testable without audio, hundreds of string-in/string-out test cases.

## Verification

- **Unit tests**: AtcNumberParser, CallsignParser, PhraseologyMapper, PhoneticFixMatcher — all pure functions, no audio needed
- **Integration test**: Feed known transcription strings through full pipeline, verify canonical output
- **Manual test**: Run client, hold PTT, speak commands, verify text appears in input box correctly
- **Build**: `dotnet build -p:TreatWarningsAsErrors=true` must pass
- **Existing tests**: `dotnet test` on Yaat.Sim.Tests must still pass (no regressions)
