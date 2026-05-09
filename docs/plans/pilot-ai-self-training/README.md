# Pilot AI for Self-Training

This folder coordinates YAAT's pilot-AI self-training plan. Each milestone has its own subplan file; this README is the coordination doc — context, architecture, progress tracking, locked decisions, and the deferred-indefinitely list.

## Context

YAAT was originally built around an **instructor + student** topology. The student plays the controller (typing canonical RPO like `DM 5000, R 270`); an instructor plays every pilot via PTT — Whisper STT + `PhraseologyMapper` (~163 ATC phraseology rules) + `LocalLlmCommandMapper` fallback turn pilot phraseology into canonical commands. Aircraft "responses" are text in the terminal; there is no TTS, no autonomous pilot behavior, and no notion of an aircraft holding an instruction-aware mental model.

The user wants a **solo-student** topology: the student plays the controller, the simulator plays every pilot. An earlier plan (`docs/plans/pilot-ai-architecture.md`, written when the project was just starting) over-engineered this by introducing `Intent`, `Contingency`, `Expectation`, `FrequencyState`, `VerbalAction`, and `SpeedRestrictionStack` types that largely duplicate what the now-mature phase system already does. This plan supersedes it.

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

Continues `TAXI 28R`, `LUAW`, `CTOR CM 5000 FH 280`, `HOO`, then `CT` (contact next controller — distinct from the radar handoff `HOO`, per FAA 7110.65 §7-6-11). Every command produces a readback line. After `CT`:

```
[N123AB] Departure on one two five point three five, november one two three alpha bravo, so long.
```

Goal: **the student can fly an aircraft from gate to handoff without a human in the loop**, and the simulator feels alive. M10.1 ships the readback half. M10.1.x extend spawn check-ins for IFR + VFR. M10.2 lets the student *speak* ATC English instead of typing canonical. M10.3 adds the audible pilot voice. M10.4–M10.5 add genuine pilot autonomy.

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
       ┌──────────────────────────────────────────────────┐ ◄── M10.1+
       │  PilotResponder                                  │
       │   • PhraseologyVerbalizer inverts each clause    │
       │     using the same PhraseologyRules that         │
       │     parse controller speech (one source of truth)│
       │   • Concat clauses, append spoken callsign tail  │
       │   • Queue delayed PendingPilotTransmissions      │
      │   • (M10.3.5) serialize and pace SAY/audio       │
       │   • (M10.4) record pending pilot requests        │
       └──────────────────────────────────────────────────┘

       ┌──────────────────────────────────────────────────┐ ◄── M10.4
       │  PilotProactive (ticked post-physics)            │
       │   • Spawn check-in (M10.1+)                      │
       │   • Pending-clearance reminders (M10.4)          │
       │   • DA/MDA missed-approach trigger (M10.5)       │
       └──────────────────────────────────────────────────┘
```

**Key choice — pilot AI does not generate phases or build plans.** It dispatches canonical commands like the controller does, and the existing phase system handles the rest. This is the single biggest divergence from the old plan, which proposed a parallel `Intent → Plan → Phase` execution model.

**Second key choice — pilot speech is inverted from the same `PhraseologyRules` that parse controller speech.** Real ATC convention is verbatim readback; the codebase already has the vocabulary as 163 input rules. Adding new readbacks happens by adding to `PhraseologyRules`, not by maintaining a parallel template table. Pilot-only utterances (spawn check-in, "going around" volunteered) live in `PilotResponder` directly — small set, no controller equivalent. Solo-training readbacks use `PilotPersonality.Varied` to shorten safe command families only when the frequency is busy.

## Milestone progress

| | Milestone | Subplan | Summary |
|---|---|---|---|
| [x] | **M10.0** | [m10.0-tts-spike.md](m10.0-tts-spike.md) | TTS pipeline spike — sherpa-onnx + Piper LibriTTS-R + radio DSP validated |
| [x] | **M10.1** | [m10.1-pilot-readbacks.md](m10.1-pilot-readbacks.md) | Pilot voice (text-only readbacks) + AtParking IFR spawn check-in |
| [x] | **M10.1.1** | [m10.1.1-ground-spawn-checkins.md](m10.1.1-ground-spawn-checkins.md) | Ground spawn check-ins (IFR + VFR): HoldingShort, LinedUp, OnFinal + drop M10.1's IFR-only gate |
| [x] | **M10.1.2** | [m10.1.2-airborne-spawn-checkins.md](m10.1.2-airborne-spawn-checkins.md) | Airborne-spawn check-ins: VFR inbound, IFR airborne arrival, VFR overflight transition |
| [x] | **M10.1.3** | [m10.1.3-vfr-pattern-work.md](m10.1.3-vfr-pattern-work.md) | VFR closed-traffic: initial-call request + uncleared-only reminders at midfield-downwind and short-final |
| [x] | **M10.1.4** | [m10.1.4-hoo-signoff.md](m10.1.4-hoo-signoff.md) | New `CT` and `FCA` commands for the frequency-change instruction (separate from HOO/ACCEPT per 7110.65 §7-6-11) |
| [x] | **M10.1.5** | [m10.1.5-vfr-airspace-respect.md](m10.1.5-vfr-airspace-respect.md) | VFR self-restrict outside FAA AIS Class B (no clearance) / Class C (no two-way comms) until gate satisfied |
| [x] | **M10.2** | [m10.2-student-natural-atc.md](m10.2-student-natural-atc.md) | Student speaks/types real ATC; rewires PTT pipeline to the controller side |
| [x] | **M10.3** | [m10.3-tts-layer.md](m10.3-tts-layer.md) | TTS v1: typed pilot-transmission SignalR event + off-by-default local client voice |
| [x] | **M10.3.5** | [m10.3.5-frequency-contention.md](m10.3.5-frequency-contention.md) | Frequency queue + activity-aware solo readback verbosity |
| [x] | **M10.3.6** | [m10.3.6-scenario-pace-controls.md](m10.3.6-scenario-pace-controls.md) | Scenario-load training pace controls: parking initial call-up and arrival generator rate sliders |
| [x] | **M10.4** | [m10.4-proactive-after-silence.md](m10.4-proactive-after-silence.md) | Pending-request reminders, including slower follow-up after standby |
| [x] | **M10.5** | [m10.5-da-mda-unable.md](m10.5-da-mda-unable.md) | DA/MDA contingency (warn-then-miss) + "unable" rejection on dispatch failure |
| [x] | **M10.6** | [m10.6-scenario-pack.md](m10.6-scenario-pack.md) | Solo training USER_GUIDE + scenario-author notes + cleanup |
| [x] | **M10.7** | [m10.7-session-report-separation-scoring.md](m10.7-session-report-separation-scoring.md) | Live Session Report + FAA-grounded airborne, runway, wake, and advisory scoring |

## Decisions committed

- **`SoloTrainingMode` gates everything.** All pilot behavior is preference-gated so instructor-mode users see zero behavior change.
- **TTS v1 is event-driven and client-side.** The sim emits typed `PilotTransmission` metadata alongside existing terminal queues; the server broadcasts `PilotTransmissionBroadcast`; the client speaks it only when the user enables Pilot Voice and the active session is solo training.
- **`RSP` and `SAY` are separate solo-training channels.** `RSP` is immediate command parse/dispatch feedback. `SAY` is the delayed radio transcript of what the pilot says on frequency; one command can produce both.
- **TTS engine: sherpa-onnx + Piper LibriTTS-R 904-speaker model + NAudio radio DSP + PortAudio playback.** This keeps M10.3 cross-platform and aligned with the M10.0 spike.
- **Pilot speech inverts `PhraseologyRules`** — single source of truth; one rule covers both controller-input parsing (M10.2) and pilot-readback generation (M10.1+).
- **Missed approach uses warn-early-then-miss-at-DA pattern** (M10.5).
- **First production ship was M10.1; M10.1.1 + M10.1.2 + M10.1.3 form the VFR-emphasis pull-forward** (decided 2026-05-02 — moved airborne-spawn out of M10.4 to land alongside ground-spawn).
- **Towered-field pilot speech is no-clearance-driven, not phase-entry-driven** (decided 2026-05-02 during M10.1.3 planning). At Class D / Class C tower fields, pilots in closed traffic do **not** self-announce every leg at phase entry. They speak twice: (1) initial contact with intent (`"request closed traffic"`) on first phase activation, and (2) reminder transmissions at midfield-downwind or short-final **only if** tower hasn't cleared them yet. The existing `_midfieldBroadcastIssued` block in `DownwindPhase` already encodes the right trigger; M10.1.3 branches its output by mode. Position reports go in `OnTick` with a clearance gate; intent declarations go in `OnStart`. Output channel branches on `SoloTrainingMode`: delayed `PendingPilotTransmissions` (pilot SAY/audio) in solo mode, `PendingWarnings` (controller nag, existing behavior) in RPO mode.
- **Training pace controls live at scenario load** (M10.3.6). The existing difficulty overlay becomes a shared scenario setup modal. The parking initial call-up and arrival generator rate sliders appear only for scenarios with the matching workload source, apply per loaded session/scenario, and remember previous values only as the next defaults.
- **`STBY` acknowledges but does not satisfy a pending pilot request** (M10.4). It can still satisfy two-way radio communication requirements such as the M10.1.5 Class C gate, but the pilot should follow up later if the underlying request has not been handled.
- **M10.6 does not create ARTCC scenario content.** Training staff own official scenario design; YAAT documents how solo training works with existing ARTCC/local scenarios and what scenario authors can optionally include.
- **The old `docs/plans/pilot-ai-architecture.md` stays deleted as part of M10.6 cleanup.**

## Reused infrastructure (don't reinvent these)

- **`CommandDispatcher.DispatchCompound`** — single integration point. M10.1's hook is one function call after success.
- **`PhraseologyRules`** + **`PhraseologyMapper`** — single source of ATC vocabulary, used both directions.
- **`LocalLlmCommandMapper`** — LLM fallback for the M10.2 student-input parser. Already grammar-constrained.
- **`PendingNotifications` / `PendingWarnings`** (`AircraftState`) — immediate non-radio talkback and warning channels. Terminal already drains them.
- **Phase system** — the autonomous pilot brain. Auto-exit selection, auto-glideslope capture, auto-pattern entry, auto-holding entry — all already work.
- **`AirlineTelephony`** + **`AtcNumberParser`** + **`CallsignParser`** — reused for spoken callsign formatting.
- **`CommandDescriber`** — extension point for readback templates.
- **`AircraftState.HasFlightPlan` / `Destination` / `ActiveSidId` / `ExpectedApproach`** — already populated. Seed inputs for pending-request triggers such as M10.4's arrival/approach reminder.
- **`ApproachClearance.MissedApproachFixes`** + **`MapAltitudeFt`** — already populated from CIFP. M10.5's missed-approach autonomy reads them.
- **`PiperVoiceInstaller`** (`src/Yaat.Client/Services/`) — Settings download/remove flow for the Piper LibriTTS-R voice pack.
- **`tools/Yaat.SpeechSandbox` TTS tab** — built during M10.0; live-tunable testbed for radio-FX parameters.
- **PilotTransmission queue + FrequencyState** — transient side queue on `AircraftState` plus sim-level active-frequency serializer. Solo pilot speech drains later as `SAY` and typed audio metadata; command `RSP` remains immediate.
- **Pattern phases** (`src/Yaat.Sim/Phases/Pattern/`) — `DownwindPhase`, `BasePhase`, `UpwindPhase`, `CrosswindPhase`, `PatternEntryPhase`, `VfrFollowPhase`. Pattern plumbing already exists; M10.1.3 only adds pilot speech.
- **Scenario difficulty overlay** (`MainViewModel.Scenario` + `MainWindow.axaml`) — reused by M10.3.6 as the shared scenario setup modal rather than adding another load-time dialog.

## Deferred indefinitely (and why)

| Feature | Reason |
|---|---|
| **Default plan / autonomous taxi-without-clearance** | Anti-feature for ATC training. The student must own every clearance. The "do nothing without a command" baseline is a feature, not a bug. |
| **NLP-rich ATC parsing for solo students** | M10.2's PhraseologyMapper covers ~163 patterns. LLM fallback covers most else. Resist building a richer parser until production playtesting reveals what's actually missing. |
| **Lost-comms 14 CFR 91.185 procedures** | Solo sessions never run that long. M10.4's pending-clearance reminders + M10.5's missed-approach autonomy cover the failure modes that matter. |
| **Multi-frequency monitor-vs-active modeling, ATIS dynamic injection** | Adds modeling burden with near-zero training value beyond the single active-frequency queue. `HOO` already removes the aircraft from the controller's view; that's enough. |
| **Richer pilot personality / callsign abbreviation / frequency compression** | M10.3.5 ships activity-aware shortcuts for safe command families plus light quiet-frequency flavor. Broader style changes need more aircraft/frequency context to avoid ambiguous or nonstandard readbacks. |
| **Response timing jitter** | Pure flavor; deterministic readbacks are easier to test and learn against. Defer indefinitely or add as a `PilotPersonality` knob if it ever matters. |
| **AI-against-AI conflict resolution from the pilot side** | Phase system doesn't model TCAS RAs. Out of scope. |
| **`SpeedRestrictionStack` from the old plan** | Existing `SpeedFloor` / `SpeedCeiling` + hardcoded 250-kt-below-10k regulatory cap is sufficient. Build the stack only when a real scenario forces it. |
| **`Contingency` system from the old plan** | M10.5's two specific contingencies (DA missed approach, dispatch-rejected unable) cover the cases that matter. Don't build a generic framework for two instances. |

## Cross-cutting verification (end-to-end)

After M10.1 + M10.1.1 + M10.2 (text-only with NL ATC):

```bash
dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log
timeout 30 dotnet test --filter "PilotResponder|SoloTraining" 2>&1 | tee .tmp/test.log
```

Manual:

1. Launch `Yaat.Client` with `SoloTrainingMode = true`.
2. Load `KOAK Solo Departure 1`.
3. Wait 5 s — confirm "ready to taxi" check-in appears.
4. Speak (PTT) "November one two three alpha bravo, push approved facing east" — confirm canonical `N123AB PUSH FACE EAST` in input box.
5. Press Enter — confirm aircraft pushes back AND readback line appears.
6. Repeat for full sequence through `HOO` — confirm clean handoff sign-off.

After M10.3 (TTS):

- Plug headphones in, run the same scenario. Confirm pilot voice speaks each readback. Issue commands to two aircraft within 1 s; confirm one finishes speaking before the other starts.

M10.5:

- Run scenario where landing clearance is intentionally never issued. Confirm "approaching minimums" radio warning at DA/MDA + 1000, then autonomous go-around at DA/MDA with the normal go-around pilot transmission.
- Issue an impossible live solo pilot command. Confirm the command fails and the pilot queues an "unable" readback; parse/admin failures remain silent.

## Open questions

All major scope/architecture decisions are committed. **The voice-pool sizing question is resolved**: sherpa-onnx + Piper LibriTTS-R is a single 75 MB ONNX file with 904 speakers selectable by integer — no per-voice file management.

Remaining provisional item is pending a later milestone:

- M10.3 follow-up: output-device picker.
