# Pilot AI for Self-Training — Plan

## Context

YAAT is currently built around an **instructor + student** topology. The student plays the controller (typing canonical RPO like `DM 5000, R 270`); an instructor plays every pilot via PTT — Whisper STT + `PhraseologyMapper` (~163 ATC phraseology rules) + `LocalLlmCommandMapper` fallback turn pilot phraseology into canonical commands. Aircraft "responses" are text in the terminal; there is no TTS, no autonomous pilot behavior, and no notion of an aircraft holding an instruction-aware mental model.

The user wants a **solo-student** topology: the student plays the controller, the simulator plays every pilot. The old plan (`docs/plans/pilot-ai-architecture.md`, written when the project was just starting) over-engineered this by introducing `Intent`, `Contingency`, `Expectation`, `FrequencyState`, `VerbalAction`, and `SpeedRestrictionStack` types that largely duplicate what the now-mature phase system already does. This plan supersedes it.

**The right size for YAAT today**: the phase system *is* the pilot brain. 30+ phases already encode autonomous behaviors (runway exit selection, holding entry, ILS capture, pattern entry classification). `CommandDispatcher` already validates and accepts every maneuver, clearance, and ground op (231 canonical commands). `PhraseologyMapper` already converts ATC English → canonical. The work isn't to invent a new execution layer; it's to (a) **make the simulator talk back**, (b) **let the student speak real ATC instead of RPO shorthand**, and (c) **add a few autonomous pilot behaviors** that genuinely improve solo training (DA/MDA missed approach, "unable" rejections, ready-to-taxi check-in).

## Demo (the smallest compelling thing)

**One IFR jet at KOAK, parking → handoff, solo student plays Ground+Tower.**

A B738 spawns at FBO ramp with a flight plan and assigned SID. After 5 s settle:

```
[N123AB] Oakland Ground, N123AB at the FBO ramp, information Charlie, ready to taxi.
```

Student types `N123AB PUSH FACE EAST`:

```
> N123AB PUSH FACE EAST
[N123AB] Push approved facing east, N123AB.
```

Continues `TAXI 28R`, `LUAW`, `CTOR CM 5000 FH 280`, `HOO` — every command produces a readback line. After `HOO`:

```
[N123AB] Departure on one two five point three five, N123AB, good day.
```

Goal: **the student can fly an aircraft from gate to handoff without a human in the loop**, and the simulator feels alive. M10.1 ships exactly this. M10.2 lets the student *speak* ATC English instead of typing canonical. M10.3 adds the audible pilot voice. M10.4–M10.5 add genuine pilot autonomy.

## Architecture (thin sidecar, not a parallel system)

```
        ┌──────────────────────────────────────────────────┐
        │  STUDENT INPUT (typed canonical OR spoken ATC)   │
        └─────────────────┬────────────────────────────────┘
                          │
                          ▼
       ┌──────────────────────────────────────────────────┐
       │  PhraseologyMapper / LocalLlmCommandMapper       │   (already exists)
       │   • English ATC → canonical command              │
       └─────────────────┬────────────────────────────────┘
                         │  canonical CompoundCommand
                         ▼
       ┌──────────────────────────────────────────────────┐
       │  CommandDispatcher.DispatchCompound  (unchanged) │
       │   • Validates against current Phase              │
       │   • Mutates Targets / PhaseList                  │
       └─────────────────┬────────────────────────────────┘
              success    │
                         ▼
       ┌──────────────────────────────────────────────────┐ ◄── NEW
       │  PilotResponder                                  │
       │   • Build readback template from accepted cmd    │
       │   • Push to AircraftState.PendingNotifications   │
       │   • (M10.3) speak via IPilotVoice                │
       │   • (M10.4) update PilotExpectation              │
       └──────────────────────────────────────────────────┘

       ┌──────────────────────────────────────────────────┐ ◄── NEW (M10.4)
       │  PilotProactive (ticked post-physics)            │
       │   • Spawn check-in (M10.1)                       │
       │   • Pending-clearance reminders (M10.4)          │
       │   • DA/MDA missed-approach trigger (M10.5)       │
       └──────────────────────────────────────────────────┘
```

**Key choice — pilot AI does not generate phases or build plans.** It dispatches canonical commands like the controller does, and the existing phase system handles the rest. This is the single biggest divergence from the old plan, which proposed a parallel `Intent → Plan → Phase` execution model.

## Milestone breakdown

> **Decisions committed:** Before any milestone work, **M10.0 runs a TTS spike in `tools/Yaat.Scratch`** to validate the sherpa-onnx + Piper LibriTTS-R + radio-DSP stack on this dev box. M10.1+ are provisional pending spike findings. The first production ship is **M10.1 + M10.2 together** (readbacks + natural-language ATC) — the smallest slice that actually feels like self-training. All new pilot behavior is **gated by `UserPreferences.SoloTrainingMode`** so instructor-mode users see zero behavior change. Missed approach uses a **warn-early-then-miss-at-DA** pattern. **TTS is in scope** — M10.3, layered on top of M10.1's readback strings, locked to **sherpa-onnx + Piper LibriTTS-R 904-speaker model + NAudio.Core DSP** (subject to M10.0 confirmation). **Voice pack is downloaded on-demand**, not bundled — keeps the installer slim for users who never enable TTS. The old `docs/plans/pilot-ai-architecture.md` is **deleted** as part of M10.6.

### Progress

- [x] **M10.0** — TTS pipeline spike (validated 2026-04-25, branched into `tools/Yaat.SpeechSandbox` TTS tab)
- [ ] **M10.1** — Pilot voice (text-only readbacks)
- [ ] **M10.2** — Student speaks/types real ATC
- [ ] **M10.3** — TTS layer (audible pilot voice + radio realism)
- [ ] **M10.4** — Pilot expectations + proactive requests
- [ ] **M10.5** — DA/MDA contingency + "unable" rejection
- [ ] **M10.6** — Solo Training scenario pack + USER_GUIDE + cleanup

### M10.0 — TTS pipeline spike in Yaat.Scratch — Small, ~1–2 days (GATING)

**Purpose:** validate the M10.3 engine stack on the actual dev box before committing to the bigger plan. If sherpa-onnx + LibriTTS-R doesn't deliver acceptable latency/quality cross-platform, we revisit *before* shipping any production code that depends on it.

**What ships:** a runnable program in `tools/Yaat.Scratch/` (currently an empty placeholder per CLAUDE.md). Not committed to the full plan; throwaway code that produces a clear go/no-go answer.

**Concrete tasks:**
1. Add NuGet refs to `tools/Yaat.Scratch/Yaat.Scratch.csproj`:
   - `org.k2fsa.sherpa.onnx` (latest, Apache-2.0)
   - `NAudio.Core` (for `BiQuadFilter`)
   - PortAudio binding (whatever `tools/Yaat.SpeechSandbox` already uses — match the project for consistency)
2. Download Piper LibriTTS-R medium voice pack (`vits-piper-en_US-libritts_r-medium.tar.bz2`, ~75 MB) into `.tmp/voices/` (gitignored).
3. Write `Program.cs` that:
   - Loads the voice via sherpa-onnx's `OfflineTtsConfig` + `OfflineTts.Generate(text, sid, speed)`.
   - Synthesizes 5 fixed ATC utterances at 5 different speaker IDs (e.g., 50, 100, 200, 300, 450). Save each as a WAV to `.tmp/spike/`.
   - Measures and prints synthesis latency per call.
   - Applies the radio DSP chain (band-pass 300–3000 Hz, soft-clip, ~150 ms squelch tail) to one synthesized clip and saves as `*-radio.wav` for A/B comparison.
   - Plays one clip through PortAudio to confirm output works end-to-end.
4. Manually listen to the WAVs and compare:
   - Are the 5 speaker IDs audibly distinct enough for ATC variety?
   - Is the radio-FX clip recognizable as "radio voice" without sounding broken?
   - Does the latency feel sub-second from invocation to first audio?
5. Cross-platform check (best-effort): if a Mac or Linux machine is available, repeat steps 3–5 there. If not, note as a known risk for M10.3.

**Go/no-go criteria:**
- ✅ Synthesis latency <500 ms for ~10-word utterances on this dev box.
- ✅ Speaker ID variation produces clearly distinct voices.
- ✅ Radio DSP chain produces a recognizable VHF-aviation effect.
- ✅ End-to-end audio playback works from C# on this machine.
- ✅ Memory footprint with one model loaded is reasonable (~100–200 MB resident is fine).
- If any of the above fail or feel unworkable, report findings and revise M10.3 in the plan (candidates: Kokoro via KokoroSharp instead of Piper; Coqui or other engines are off the table per research).

**What this spike does NOT validate (deferred to M10.3 proper):**
- LRU caching across multiple voice models (single model is now the design — moot).
- Replay determinism (mechanical, low-risk).
- Settings UI plumbing.
- Aircraft callsign pronunciation quality (defer until we have `SpokenCallsignFormatter` in M10.1).

**Verify:**
- Listen to the WAVs.
- Print latency numbers; eyeball them.
- One short paragraph summary committed to a `.tmp/spike-notes.md` (gitignored) capturing what worked, what didn't, and any plan revisions needed.

After M10.0 finishes, we revisit this plan, lock M10.3 specifics (or pivot if findings demand it), and proceed with M10.1.

### M10.1 — Pilot voice (text-only readbacks) — Small, ~3–5 days

**What ships:** when `SoloTrainingMode` is on, every successful dispatch produces a deterministic readback string written to `aircraft.PendingNotifications`. Spawn check-in for IFR aircraft at parking after 5 s of stillness. No new input mode — student still types canonical (M10.2 adds NL ATC).

**Files to touch:**
- `src/Yaat.Sim/Pilot/PilotResponder.cs` *(new)* — `BuildReadback(CompoundCommand accepted, AircraftState ac, ResolvedCommandContext ctx) → string?`. Hard-coded template table keyed by `CanonicalCommandType` for the ~30 commands a solo student plausibly issues at MVP (Pushback, Taxi, HoldShort, CrossRunway, LineUpAndWait, ClearedForTakeoff, ClimbMaintain, DescendMaintain, FlyHeading, TurnLeft, TurnRight, Speed, DirectTo, ClearedApproach, JoinFinalApproachCourse, ClearedToLand, GoAround, Squawk, Ident, ContactDeparture/Approach, Pattern entries). Returns `null` for commands with no template (no fake fallback).
- `src/Yaat.Sim/Speech/SpokenCallsignFormatter.cs` *(new)* — inverse of `CallsignParser`. ICAO/N-number → spoken form ("AAL123" → "American one twenty three"). Reuse `AirlineTelephony` + `AtcNumberParser`.
- `src/Yaat.Sim/Commands/CommandDispatcher.cs` — single hook: after a `DispatchCompound` returns success, if `SoloTrainingMode` (carried via dispatch context or read from a sim-level config flag), call `PilotResponder.BuildReadback` and append to `aircraft.PendingNotifications`. ~10 lines including the gate.
- `src/Yaat.Sim/Phases/Ground/AtParkingPhase.cs:29` — `OnTick` adds: if `aircraft.HasFlightPlan && !aircraft.HasAnnouncedReady && ElapsedSeconds > 5`, push the spawn check-in line and set `HasAnnouncedReady = true`. ~10 lines.
- `src/Yaat.Sim/AircraftState.cs:10` — add `bool HasAnnouncedReady` (mutable, snapshot-serialized).
- `src/Yaat.Sim/Simulation/Snapshots/AircraftSnapshotDto.cs` — add the new field + migration.

**Riskiest part:** which event fires the readback. Pick **dispatch acceptance** for MVP — if the controller said "at SUNOL turn left 270" (`AT SUNOL TL 270`), the pilot reads back the *whole* compound once at acceptance, not again when the trigger fires. Keeps the brain from having to know about block-trigger semantics.

**Verify:**
- `timeout 30 dotnet test --filter PilotResponderTests 2>&1 | tee .tmp/test.log` — golden-file test asserts readback strings for ~30 commands.
- Manual: load `KOAK Solo Departure 1` (M10.6 deliverable below — for M10.1 just use any KOAK scenario), type a sequence, eyeball terminal pane.

### M10.2 — Student speaks/types real ATC — Small, ~3–5 days

**What ships:** the student-controller's input box accepts natural-language ATC, not just canonical. Reuses the existing speech pipeline (PTT → Whisper → PhraseologyMapper → LocalLlmCommandMapper) — but rewires it from "instructor's pilot voice" to "controller's voice." Both work; a setting toggles which side the local user is playing.

**Files to touch:**
- `src/Yaat.Client/Services/CommandInputController.cs` — when student types something that is not a canonical command, route through `PhraseologyMapper` first; if that returns a canonical, replace the text and dispatch. Today's behavior is canonical-only.
- `src/Yaat.Client/Services/UserPreferences.cs` + settings UI — `SoloTrainingMode` boolean (off = current instructor topology; on = student plays controller and PTT goes through controller-side parsing).
- `src/Yaat.Client/ViewModels/MainViewModel.cs` — when `SoloTrainingMode` is on, route PTT through the same path. (Today PTT already routes through `PhraseologyMapper` — most of this is just re-labeling and ensuring the input ends up in the controller's input box, not the pilot-readback channel.)

**Open question for user before implementation:** should `SoloTrainingMode` also turn on M10.1's auto-readback? My default: **yes** — the two features are useless apart for a solo student. Toggle still off by default for instructor-mode users so their workflow is unchanged.

**Riskiest part:** none of `PhraseologyMapper`'s 163 rules are scoped to "controller-side" vs "pilot-side" — they're all ATC phraseology, which is fundamentally bidirectional. Verify with `aviation-sim-expert` that nothing in `PhraseologyRules.cs` is colloquial-pilot-speak that wouldn't make sense as a controller utterance ("with you", "negative contact", etc. should not match a controller message).

**Verify:**
- Existing `SpeechPipelineTests` continue to pass (no behavior change for instructor mode).
- New test: enable `SoloTrainingMode`, simulate "United 123, descend and maintain 5000" → assert canonical `AAL123 DM 5000` appears, then dispatched, then aircraft descends, then readback line appears.

### M10.3 — TTS layer (audible pilot voice with radio realism) — Medium, ~1.5–2 weeks

**What ships:** M10.1's readback strings get spoken aloud through **sherpa-onnx** (Apache-2.0 NuGet `org.k2fsa.sherpa.onnx`) loading the **Piper `en_US-libritts_r-medium`** voice pack — a single ~75 MB ONNX model that exposes **904 speakers via integer speaker IDs**. Each aircraft is assigned a unique speaker ID (deterministic from scenario seed + callsign hash, capped at IDs 0–500 to avoid undertrained speakers). Output flows through a **band-pass filter + squelch tail** so it sounds like a real radio. Audio plays through a **dedicated radio output device** when configured. Audio queueing ensures one aircraft speaks at a time on the active frequency. Still gated by `SoloTrainingMode`.

**Why this isn't deferred:** audible voice is core to the self-training experience — listening to multiple readbacks while typing, parsing cadence, knowing when the frequency is busy is *the* cognitive load of real ATC. Text in a terminal validates the templates; voice closes the loop.

**Engine choice — sherpa-onnx loading Piper LibriTTS-R, locked.** Validated against external research:
- **Cross-platform** via `org.k2fsa.sherpa.onnx` plus runtime packages for `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`.
- **Apache-2.0** (engine code) — avoids the GPLv3 trap in the new `piper1-gpl` engine.
- **Single 75 MB voice file** with **904 speakers selected by `sid` integer** — no per-voice file management, no LRU caching needed, voice variety is free.
- **Latency** ~150–300 ms synth on commodity x86 desktop CPUs — well within sub-second budget.
- **License compatibility** — LibriTTS-R is **CC-BY 4.0**, requires attribution in About / credits.
- **Note on espeak-ng data**: sherpa-onnx bundles espeak-ng phoneme data (GPLv3) loaded as data, not statically linked. Major commercial Piper-via-sherpa shippers (NVDA, Home Assistant) treat this as compatible with closed-source apps. If we want a maximally clean Apache-2.0 distribution path later, **KokoroSharp** (Apache-2.0 model, MIT wrapper, 54 voices, Misaki phonemizer with no espeak dep) is a swap-in via the same `IPilotVoice` seam.

The `IPilotVoice` interface seam still matters — leaves room for cloud/Kokoro/alternate-engine swaps without touching dispatch.

**Files to touch (Yaat.Sim — no platform deps):**
- `src/Yaat.Sim/Pilot/IPilotVoice.cs` *(stub from M10.1)* — `Task SpeakAsync(VoiceUtterance utt, CancellationToken ct)`. `VoiceUtterance` carries text + speaker id + aircraft callsign.
- `src/Yaat.Sim/Pilot/PilotVoiceProfile.cs` *(new)* — `record (int SpeakerId, float RateMultiplier)`. Assigned at scenario load.
- `src/Yaat.Sim/Pilot/PilotVoiceAssigner.cs` *(new)* — deterministic seeded RNG (seed = scenario seed XOR callsign hash) picks an `int sid ∈ [0, 500]`. Replays produce identical voice assignments. Pure function — easy to unit-test.
- `src/Yaat.Sim/Pilot/PilotResponder.cs` — when emitting a readback, also fire `IPilotVoice.SpeakAsync`. Text + voice from the same code path — they stay in sync.

**Files to touch (Yaat.Client — audio platform):**
- `src/Yaat.Client/Audio/SherpaOnnxPilotVoice.cs` *(new)* — concrete `IPilotVoice` impl using `org.k2fsa.sherpa.onnx`. On startup, checks `PiperVoiceInstaller.IsInstalled`; if false, surfaces "Pilot voice not installed — download in Settings" and remains silent (no fallback synthesis). If installed, loads the .onnx from `%LOCALAPPDATA%/yaat/voices/vits-piper-en_US-libritts_r-medium/` once, kept resident (~75 MB). Synthesizes raw 22.05 kHz PCM via `tts.Generate(text, sid, speed)`. **Pre-warm at app boot:** spike measured first-call latency at 1287 ms (cold) vs 133–207 ms (warm). The constructor must spawn a background task that synthesizes a short throwaway utterance (e.g., "warmup") and discards the result, so the first real readback hits the warm path. Block subsequent calls on the warmup task only if it hasn't completed yet.
- `src/Yaat.Client/Services/PiperVoiceInstaller.cs` *(new)* — on-demand download of the LibriTTS-R voice pack from the public sherpa-onnx releases URL. Mirrors the `CudaBackendInstaller` pattern (already used to keep the ~700 MB CUDA backend out of the base installer). Surfaces progress events; downloads to `%LOCALAPPDATA%/yaat/voices/vits-piper-en_US-libritts_r-medium/`; verifies SHA-256 against a hash committed in source. Idempotent — second-time install is a no-op.
- `src/Yaat.Client/Audio/RadioAudioFx.cs` *(new)* — band-pass filter (300–3000 Hz) using NAudio's `BiQuadFilter.BandPassFilterConstantPeakGain` (or NWaves equivalent if NAudio's Windows-only behavior is a problem on Mac/Linux), soft-clip distortion, and squelch tail (~150 ms low-amplitude noise + click on utterance end). Pure DSP, ~50 lines.
- `src/Yaat.Client/Audio/PilotVoiceMixer.cs` *(new)* — single-channel mixer. One aircraft speaks at a time on the active frequency. Drops queued items past 30 s staleness. Audio output via PortAudio (already used by `tools/Yaat.SpeechSandbox`).
- `src/Yaat.Client/Services/UserPreferences.cs` — add `PilotVoiceVolume` (0–100), `RadioOutputDeviceId` (separate from default audio device), `RadioFxEnabled` (default on — students can opt out for cleaner voice; confirmed user requirement from M10.0).
- `src/Yaat.Client/Views/SettingsWindow.axaml` + `SettingsViewModel.cs` — volume slider, "Radio Output Device" dropdown enumerated via PortAudio, Radio FX toggle. **"Download pilot voice (~75 MB)" button** that calls `PiperVoiceInstaller.DownloadAsync` with progress display — same UX pattern as the existing CUDA backend download in Settings → Speech → Acceleration. Disabled once installed; shows "Installed at <path>" with a "Re-install" affordance.
- `src/Yaat.Client/Yaat.Client.csproj` — add `org.k2fsa.sherpa.onnx` NuGet (Apache-2.0). NAudio.Core for DSP (BiQuadFilter is in `NAudio.Core` which is cross-platform; only audio I/O classes are Windows-only).

**Voice pack packaging — on-demand download:**
- **NOT bundled.** Per CudaBackendInstaller precedent (which kept ~700 MB of CUDA libs out of the base installer), the ~75 MB Piper voice pack is downloaded on demand to `%LOCALAPPDATA%/yaat/voices/vits-piper-en_US-libritts_r-medium/`. Users who never enable solo training / TTS pay zero installer or disk cost for this feature.
- Source URL: `https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-libritts_r-medium.tar.bz2` (public, stable).
- Trigger points: Settings page button ("Download pilot voice"), and an automatic prompt the first time `SoloTrainingMode` is toggled on if the pack isn't present yet.
- Integrity: hard-code expected SHA-256 in source; verify after extract; fail-loud on mismatch.
- License attribution: add a line to `AboutWindow.axaml`: *"Speech synthesized using Piper TTS LibriTTS-R voices, CC BY 4.0. Built on sherpa-onnx (Apache-2.0)."* — shown unconditionally regardless of whether the user has installed the pack.

**Phraseology and pronunciation considerations:**
- **Callsigns** — feed `SpokenCallsignFormatter` output to TTS, not the canonical callsign. ("AAL123" → "American one twenty three").
- **Numbers** — readback templates already format numbers as ATC speech (M10.1).
- **Fix names** — VITS handles unfamiliar names phonetically; some will be wrong (DUMBA, SUNOL). A pronunciation lexicon is a follow-up if needed.
- **Radio FX** — applied uniformly to every utterance.

**Riskiest parts:**
1. **Audio threading and lifetime.** Avalonia is single-UI-thread; PortAudio runs its own callback thread; sherpa-onnx synthesis is CPU-bound. Cancellation must propagate cleanly when aircraft are deleted mid-utterance, `SoloTrainingMode` toggles off, or the user quits. Plan cancellation tokens through every layer.
2. **Cross-platform PortAudio device enumeration.** Different OSes return different device names/orderings. Persist device ID, not name; fall back gracefully if a saved device disappears.
3. **Replay determinism.** `(scenario seed, callsign) → sid` must be a pure function so recorded sessions replay with identical voices. Test explicitly.
4. **NAudio.Core cross-platform.** NAudio's audio *I/O* is Windows-only, but `NAudio.Core` (which contains `BiQuadFilter`) is pure managed and cross-platform. Verify on a Mac/Linux build before committing. If problematic, swap in NWaves (also pure managed).

**Verify:**
- Manual: load demo, issue commands to two aircraft within 1 s, confirm: (a) both voices sound distinct, (b) one finishes before the other starts, (c) audio routes to the configured radio device.
- Automated: `IPilotVoice` mock asserts utterance content for command/readback pairs (no audio).
- Replay test: record a scenario, replay, confirm voice assignments match (same `sid` per callsign).
- Cross-platform: build and smoke-test on Windows + Linux + macOS (CI matrix).
- License surface: confirm About dialog shows the CC-BY 4.0 + sherpa-onnx attribution.

### M10.4 — Pilot expectations + proactive requests — Medium, ~1–1.5 weeks

**What ships:** a tiny `PilotExpectation` value object on `AircraftState` (NOT a full intent system) and a `PilotProactive` ticker that handles three real-world pilot behaviors: contextual spawn check-in, request-after-silence, and pre-handoff sign-off elaboration.

**Files to touch:**
- `src/Yaat.Sim/Pilot/PilotExpectation.cs` *(new)* — small record:
  ```csharp
  public sealed record PilotExpectation
  {
      public ExpectationKind Kind { get; init; }   // OnRamp, ReadyForTaxi, HoldingShort,
                                                   // EnRouteFiledRoute, ApproachExpected,
                                                   // EstablishedOnApproach, LandedTaxiingIn
      public long? UnsatisfiedRequestSinceMs { get; init; }
      public string? PendingRequestKind { get; init; }  // "taxi", "takeoff", "landing"
  }
  ```
  Snapshot-serialized. Computed each tick by `ExpectationUpdater` from current phase + `HasFlightPlan` + `ActiveSidId`/`ActiveStarId`/`DestinationRunway` + clearance state (`DepartureClearance`, `LandingClearance`, `ActiveApproach`).
- `src/Yaat.Sim/Pilot/PilotProactive.cs` *(new)* — ticked from `SimulationEngine` post-physics:
  - Holding short waiting for takeoff for >60 s with no `DepartureClearance`: push a "ready for departure 28R, callsign" reminder.
  - Inside 10 nm of destination on a STAR with no `ActiveApproach`: push a "with you, ten miles to land 28R" check-in (only fires once per arrival).
  - At parking with flight plan, no `DepartureClearance`, >120 s since last reminder: push "ready to taxi" again.
- `src/Yaat.Sim/Pilot/PilotResponder.cs` — extend templates to use `PilotExpectation` for richer phraseology (e.g., handoff acknowledgment includes the next frequency from `ExpectationUpdater`).
- `src/Yaat.Sim/Simulation/SimulationEngine.cs` — call `ExpectationUpdater.Update(ac)` and `PilotProactive.Tick(ac)` after physics, before broadcast.

**What this buys:** a solo student who forgets to clear an arriving aircraft for the approach gets a nudge. Closer to real training, where a flight-following pilot calls you when you've gone quiet.

**Riskiest part:** spamming the terminal. Each proactive request must have a per-request timestamp gate so it fires once per situation, not once per tick. Aggressive cap: a single aircraft cannot generate more than 1 proactive line per 30 s.

**Verify:**
- New scenario test: aircraft at parking with flight plan, no commands issued → after 5 s a check-in line appears, after 125 s a second reminder, no third reminder for another 120 s.
- Aviation review by `aviation-sim-expert` that the prompts are AIM-realistic (initial-contact format per AIM 4-2-3, etc.).

### M10.5 — DA/MDA contingency + "unable" rejection — Medium, ~1 week

**What ships:** two genuine pilot autonomy behaviors that improve training fidelity, using a **warn-early-then-miss-at-DA** pattern:

1. **Missed approach with early warning.** When `FinalApproachPhase` descends through `DA + 1000 ft` (or `MDA + 1000 ft` for non-precision) and `aircraft.LandingClearance == null`, push a warning: `"[N123AB] Approaching minimums, no landing clearance, callsign."` to `PendingWarnings` (prominent channel, not notifications). When the aircraft reaches the published DA/MDA without a clearance, autonomously dispatch `GoAround` and push `"[N123AB] Going around, callsign."` to warnings. If a missed-approach procedure is published in the CIFP data, build the missed-approach phase sequence (see existing `ApproachClearance.MissedApproachFixes` — `Phases/ApproachClearance.cs`).
2. **"Unable" responses for impossible commands.** Today, if a command is rejected at dispatch (e.g., `DM 5000` on a parked aircraft), the dispatcher silently fails. Hook the rejection path in `CommandDispatcher` to call `PilotResponder.BuildUnable(cmd, reason) → string` and push to notifications: `"[N123AB] Unable, callsign."` with optional reason if known (`"Unable, we're at the gate, callsign."`).

**Files to touch:**
- `src/Yaat.Sim/Phases/Approach/FinalApproachPhase.cs` — already tracks altitude vs glideslope. Add: when `Altitude <= ActiveApproach.MinimumAltitude` (DA/MDA, sourced from CIFP) AND `aircraft.LandingClearance == null`: enqueue `GoAround` via `CommandDispatcher` (NOT directly skip phases — go through the dispatcher so phase transitions remain consistent).
- `src/Yaat.Sim/Phases/PhaseList.cs` — confirm `LandingClearance` is reset on go-around and on missed-approach phase transition.
- `src/Yaat.Sim/Commands/CommandDispatcher.cs` — when dispatch returns `CommandResult.Failure` with a phase-rejection cause, call `PilotResponder.BuildUnable` and push to notifications. Skip for diagnostic/admin commands (`PAUSE`, `WARP`, `DELETE`, `TRACK`, etc. — gated by a `producesUnable` bit in `CommandRegistry`).
- `src/Yaat.Sim/Pilot/PilotResponder.cs` — add `BuildUnable(cmd, RejectReason) → string`.

**Riskiest part:** missed-approach autonomy can hide a student's mistake (they forgot to clear to land — sim quietly goes around). Make the missed-approach line **prominent** in the terminal (warning channel, not notification channel) so the student notices what happened.

**Verify:**
- Scenario: aircraft on RNAV28R approach, no landing clearance issued. Asserts: at MDA crossing, `GoAround` is dispatched, `PendingWarnings` contains "going around" line, aircraft transitions through `GoAroundPhase`.
- Scenario: parked aircraft, dispatch `DM 5000`. Asserts: dispatch fails AND notifications contain "Unable, ..." line.

### M10.6 — Solo Training scenario pack + USER_GUIDE + cleanup — Small, ~2–3 days

**What ships:**
- 3–5 hand-curated solo scenarios in `docs/atctrainer-scenario-examples/solo/` (or new folder): KOAK departure, KOAK arrival, KSFO pattern (VFR), and one mixed.
- USER_GUIDE.md section "Solo Training" walking through one scenario step-by-step.
- A 90-second screen recording of the demo (using existing `RecordingArchiveWriter`) embedded or linked, with audio.
- **Delete `docs/plans/pilot-ai-architecture.md`** (the old superseded plan). Per CLAUDE.md ("No phantom features. Replace, don't deprecate.") it actively misleads future readers.

**Riskiest part:** none. Documentation + content + cleanup.

## Deferred indefinitely (and why)

| Feature | Reason |
|---|---|
| **Default plan / autonomous taxi-without-clearance** | Anti-feature for ATC training. The student must own every clearance. The "do nothing without a command" baseline is a feature, not a bug. |
| **NLP-rich ATC parsing for solo students** | M10.2's PhraseologyMapper covers ~163 patterns. LLM fallback covers most else. Resist building a richer parser until production playtesting reveals what's actually missing. |
| **Lost-comms 14 CFR 91.185 procedures** | Solo sessions never run that long. M10.4's pending-clearance reminders + M10.5's missed-approach autonomy cover the failure modes that matter. |
| **Frequency state machine, ATIS dynamic injection, monitor-vs-active frequencies** | Adds modeling burden with near-zero training value when there's no actual radio. `HOO` already removes the aircraft from the controller's view; that's enough. |
| **Pilot personalities / variable phraseology / response timing jitter** | Pure flavor. Deterministic readbacks are easier to test, easier for students to learn, and avoid the "uncanny valley" of bad randomization. Keep enum-gated stub (`PilotRealism.Frictionless` is the only value) so a future PR can extend without touching M10.1 code. |
| **AI-against-AI conflict resolution from the pilot side** | Phase system doesn't model TCAS RAs. Out of scope. |
| **`SpeedRestrictionStack` from the old plan** | Existing `SpeedFloor`/`SpeedCeiling` + hardcoded 250kt-below-10k regulatory cap is sufficient for current scenarios. Build the stack only when a real scenario forces it. |
| **`Contingency` system from the old plan** | M10.5's two specific contingencies (DA missed approach, dispatch-rejected unable) cover the cases that matter. Don't build a generic framework for two instances. |

## Reused infrastructure (don't reinvent these)

- **`CommandDispatcher.DispatchCompound`** — the single integration point. M10.1's hook is one function call after success.
- **`PhraseologyMapper`** + **`LocalLlmCommandMapper`** — the M10.2 student-input-parser. Already proven, already grammar-constrained, already covering ATC English.
- **`PendingNotifications` / `PendingWarnings`** (`AircraftState:149-150`) — the talkback channel. Terminal already drains them. No new wire format.
- **Phase system** — the autonomous pilot brain. Auto-exit selection, auto-glideslope capture, auto-pattern entry, auto-holding entry — all already work.
- **`AirlineTelephony`** + **`AtcNumberParser`** + **`CallsignParser`** — invert these for `SpokenCallsignFormatter`. Mostly symmetric.
- **`CommandDescriber`** — extend it to be the home of readback templates (single source of truth, parallel to existing `BuildSummary`-style methods).
- **`AircraftState.HasFlightPlan` / `Destination` / `ActiveSidId` / `ExpectedApproach`** — already populated. They are the seed inputs for `PilotExpectation` (M10.4). No new flight-plan fields needed.
- **`ApproachClearance.MissedApproachFixes`** + **`MinimumAltitude`** — already populated from CIFP. M10.5's missed-approach autonomy reads them.
- **`CudaBackendInstaller`** (`src/Yaat.Client/Services/`) — pattern reference for `PiperVoiceInstaller` (M10.3). The CUDA installer already keeps ~700 MB of native libs out of the base installer with a Settings-driven download to `%LOCALAPPDATA%/yaat/backends/cuda13/`; voice-pack download mirrors the same UX and lifecycle.
- **`tools/Yaat.SpeechSandbox` TTS tab** (`TtsSandboxView.axaml{,.cs}`) — built during M10.0; provides a live-tunable testbed for the radio-FX parameters (band-pass center/Q, drive, squelch ms) that M10.3 will productionize. Use to iterate on FX before settling on `RadioAudioFx.cs` defaults.
- **PortAudio** + **`tools/Yaat.SpeechSandbox`** — already used for STT audio capture. M10.3's audio output reuses the same dependency.

## Verification (end-to-end)

After M10.1+M10.2 (text-only):
```bash
dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log
timeout 30 dotnet test --filter "PilotResponder|SoloTrainingMode" 2>&1 | tee .tmp/test.log
```
Manual:
1. Launch Yaat.Client with `SoloTrainingMode = true`.
2. Load `KOAK Solo Departure 1`.
3. Wait 5 s — confirm "ready to taxi" check-in appears.
4. Speak (PTT) "November one two three alpha bravo, push approved facing east" — confirm canonical `N123AB PUSH FACE EAST` in input box.
5. Press Enter — confirm aircraft pushes back AND readback line appears.
6. Repeat for full sequence through `HOO` — confirm clean handoff sign-off.

After M10.3 (TTS):
- Plug headphones in, run the same scenario. Confirm pilot voice speaks each readback. Issue commands to two aircraft within 1 s; confirm one finishes speaking before the other starts.

After M10.5:
- Run scenario where landing clearance is intentionally never issued. Confirm "approaching minimums" warning at DA+1000, then autonomous go-around at DA with prominent warning.

## Open questions

All major scope/architecture decisions are committed. **The voice-pool sizing question is resolved**: sherpa-onnx + Piper LibriTTS-R is a single 75 MB ONNX file with 904 speakers selectable by integer — no per-voice file management.

Remaining provisional items are **pending M10.0 spike findings**:
- M10.3 engine choice locks only after the spike confirms acceptable latency, voice quality, and cross-platform behavior.
- If the spike reveals issues, candidate fallbacks (in priority order): Kokoro via `KokoroSharp` (Apache-2.0, 54 voices, no espeak dep) → KittenTTS via sherpa-onnx (Apache-2.0, 8 voices, ~25 MB, fastest) → revisit cloud TTS only if both local options fail.
