# Solo-Training Pilot Speech Pipeline

Reference for how solo-training mode produces pilot transmissions — both the terminal-visible radio transcripts and the optional Piper TTS audio. Read this before touching `src/Yaat.Sim/Pilot/`, `AircraftState.PendingPilotTransmissions`, `SimulationWorld.ActiveFrequency`, or `src/Yaat.Client/Services/PilotVoiceService.cs`.

For the controller-side STT (PTT → canonical command), see [`speech-recognition-pipeline.md`](speech-recognition-pipeline.md). The two pipelines share nothing beyond the radio metaphor.

## Why a queue at all

Solo training puts one student on every frequency, playing every controller role. A compound command issued to one aircraft used to push the entire readback into `PendingNotifications` synchronously, so a busy session would draw orange-bracket notification text faster than any real pilot could speak. Worse, every aircraft on the frequency could "talk over" each other in the same tick.

The pipeline now serializes pilot speech the way a real radio works:

- A pilot can be **awaited** for a readback — the controller just spoke to them, so the next mic key on the frequency is theirs.
- Other pilots' proactive calls (ready-to-taxi, holding short, on final, going around, position reports) wait their turn.
- Each transmission consumes airtime proportional to its word count. The next dequeue can't happen until the current transmission's airtime has elapsed.

The end result: solo training feels like one frequency with many aircraft, not a controller looking at a notification firehose.

## Per-aircraft typed queue

`AircraftState.PendingPilotTransmissions` is a `List<PilotTransmission>` (transient — not snapshot-serialized).

```csharp
public sealed record PilotTransmission(
    string Callsign,
    string Text,         // terminal-readable form ("N123AB, cleared to land runway 28R")
    string SpeechText,   // TTS form ("november one two three alpha bravo, cleared to land runway two eight right")
    string SourceKind,   // "Response" or "SayReadback" — drives terminal channel
    PilotTransmissionKind Kind  // Readback / Proactive / Report / SayReadback
);
```

Builders push entries via `PilotResponder.QueueSoloPilotTransmission` (Readback/Proactive/Report) or `QueueSoloPilotReadback` (SayReadback). **Don't push directly to `PendingNotifications` from solo-mode pilot code paths.** The queue is what makes airtime serialization work; bypassing it is the bug.

## Frequency airtime serialization

`SimulationWorld.ActiveFrequency` is a single `FrequencyState` instance. Each tick, `SimulationEngine.TickPostPhysics` calls `World.DrainReadyPilotTransmissions(elapsedSeconds)`:

1. **Drain.** Move every aircraft's `PendingPilotTransmissions` into the frequency's pending queue.
2. **Awaited-readback gate.** If `_awaitingReadbackFrom` is set (the controller just spoke to a callsign), promote the matching readback to the head; nothing else dequeues until that readback emerges.
3. **Airtime gate.** A transmission can only dequeue when `elapsedSeconds >= _nextAvailableAtSeconds`. After dequeue, set the next slot to `now + (~0.25 s/word, min 1 s)`.
4. **Activity meter.** `FrequencyActivityMeter` records each transmitted message in a rolling 60-second window. Counts classify into Quiet (<5) / Moderate (≤12) / Busy (≤20) / Saturated.

Drained transmissions become terminal entries with kind `SayReadback` (for readbacks) or `SayPilot` (for proactive/reports). The client renders both as green SAY lines and routes them to `PilotVoiceService` if Piper is installed.

When solo mode is OFF, `World.DiscardAllPilotTransmissions()` runs each tick to keep RPO/test state clean.

## Awaited readback after dispatch

Successful command dispatch in `SimulationEngine.SendCommand`:

```csharp
World.ExpectPilotReadback(aircraft.Callsign);
PilotResponder.QueueSoloPilotTransmission(
    aircraft, readback, PilotTransmissionKind.Readback, PilotResponder.SourceResponse);
```

The frequency holds back every other queued transmission until the readback dequeues. As soon as it does, `_awaitingReadbackFrom` clears and normal FIFO order resumes. Two consecutive controller commands to two different aircraft will queue both readbacks; the second waits for the first to clear.

## Activity-aware readback shortening

`PilotPersonality.Varied` + Busy/Saturated activity unlocks `PhraseologyVerbalizer.PilotShortcuts` — the phraseology rule's secondary patterns produce shorter readbacks ("two eighty" instead of "fly heading two eight zero"). The base verbatim form fires at Quiet/Moderate.

`SimulationEngine.SendCommand` always passes `PilotPersonality.Varied` plus the live activity level into `PilotResponder.BuildReadback`, so shortening is automatic on the live path. Tests that need stable output use the parameterless `BuildReadback(compound, aircraft)` overload, which defaults to `Verbatim` + `Moderate`.

## Dual-output builders (terminal vs TTS)

A readback rendered for TTS reads digit-by-digit ("two eight right") and spelled-out ("november one two three alpha bravo"). The terminal log displays the same readback in the form a human reads at a glance ("28R", "N123AB"). The two forms are produced **independently** by builders, never derived by regex-stripping the TTS output.

The shared record:

```csharp
public sealed record PilotSpeechText(string Terminal, string Tts);
```

`PilotResponder.RouteSoloOrRpoTransmission(... PilotSpeechText, soloRelevantPositions, ...)` is the four-way router:

| Mode | Student position relevant? | Where it lands |
|---|---|---|
| Solo | yes (TWR for clear-of-runway, TWR/APP for pattern/approach) | `PendingPilotTransmissions` (audio + terminal SAY) |
| Solo | no | terminal warning only (orange) — student isn't on this frequency |
| RPO + `RpoShowPilotSpeech` | n/a | `PendingPilotSpeech` (green SAY) |
| RPO default | n/a | `PendingWarnings` (orange) |

Two well-known position lists:

- `PilotResponder.SoloPositionsTower` — `["TWR"]`. Used for clear-of-runway, holding-short, lined-up-ready, short-final reminder.
- `PilotResponder.SoloPositionsTowerApproach` — `["TWR", "APP"]`. Used for pattern reports, follow events, going-around, airspace-boundary holds, approaching-minimums-no-landing-clearance.

A legacy single-string overload of `RouteSoloOrRpoTransmission` exists for callers that haven't migrated to `PilotSpeechText` yet. Prefer the dual-output overload for new code.

## "Unable" routing on rejected commands

`CommandResult` now carries the rejected `CanonicalCommandType`:

```csharp
public record CommandResult(bool Success, string? Message = null, CanonicalCommandType? RejectedCommandType = null);
```

`CommandDispatcher.WithRejectedCommand` stamps the type onto every failure path before it returns. `SimulationEngine.QueueSoloUnableIfNeeded` then checks `CommandRegistry.Get(rejectedType)?.ProducesPilotUnable` and queues `PilotResponder.BuildUnable(aircraft, result.Message)` if true.

`ProducesPilotUnable` is set on each `CommandDefinition` and defaults from category:

- **True**: Heading, Altitude / Speed, Navigation, Tower, Pattern, Hold, Helicopter, Ground, Approach.
- **False**: global commands and other categories. Tracking, coordination, flight-plan, scenario, etc. don't get a pilot "unable" — those failures are controller-side problems.

`PatternEntry`-built definitions are explicitly `true` regardless of category (every pattern-entry verb is pilot-aviated). When adding a new command, accept the category default unless the command demonstrably is or isn't pilot-aviated.

## Pending-request reminders

`AircraftState.PendingPilotRequest` (snapshot-serialized — schema v5) tracks one open pilot request per aircraft. `PilotRequestTracker`:

- `RecordRequest(kind, nowSeconds, lastPilotLine, context)` — fired by the originating site (e.g. `AtParkingPhase`'s ready-to-taxi, `FinalApproachPhase`'s arrival check-in, `AirspaceBoundaryHoldPhase`'s self-hold).
- `ApplyControllerResponse(compound, nowSeconds)` — runs after every successful dispatch (live + replay). Maps command type to `Satisfied` / `Denied` / `Superseded` / `Standby` per request kind.
- `TryQueueFollowUp(nowSeconds)` — called from `PilotProactive.TickPendingRequests` each tick. Re-queues `LastPilotLine` after 120 s normally, 300 s after `STBY`/`ROGER` (`AcknowledgePilotContactCommand`).

Five request kinds: `Taxi`, `Takeoff`, `Landing`, `Approach`, `AirspaceEntry`. Each maps controller commands to terminal states (e.g. `ClearedForTakeoffCommand` → Satisfied for Takeoff; `ExpectApproachCommand` → Standby for Approach; `LineUpAndWaitCommand` → Superseded for Takeoff).

## Solo pacing rates

Two scenario knobs control pilot-AI cadence (persisted via `RecordedSettingChange` so replay round-trips):

- `SoloParkingInitialCallupRatePercent` (0–200) — interval = 20 s × 100 / rate. Setting to 0 suppresses the spawn check-in entirely; aircraft sit on `InitialCallupDecisionProcessed = false` until rate goes positive.
- `SoloArrivalGeneratorRatePercent` (0–100) — multiplier on each generator's `IntervalTime`. Setting to 0 suspends generators; generators reschedule from now when rate goes positive.

`SimulationEngine.ApplySoloPacingRates(parking, arrival, rescheduleFromNow)` is the only setter. Live UI changes pass `rescheduleFromNow=true`; scenario-load and replay pass `false`. `ScenarioPacing.TryReserveParkingInitialCallupSlot` is the slot-allocator threaded into `PhaseContext.TryReserveSoloParkingInitialCallupSlot`; `AtParkingPhase` consults it before queueing the ready-to-taxi call.

## Client-side TTS (optional)

`PilotVoiceService` (in `Yaat.Client`) consumes `PilotTransmissionBroadcastDto` from the server. The service is unbounded-channel-backed and runs a single worker.

- **Synthesizer.** `SherpaOnnxPilotVoiceSynthesizer` loads the Piper LibriTTS-R medium voice pack (904 speakers). The voice pack is downloaded by `PiperVoiceInstaller` from the sherpa-onnx GitHub release into `YaatPaths.Combine("voices", ...)` so Velopack upgrades don't wipe it.
- **Speaker assignment.** `PilotVoiceAssigner` (yaat-server) maps `(scenarioRngSeed, callsign)` → speaker id 0–903 deterministically. Same callsign in the same scenario keeps the same voice.
- **Radio FX.** `RadioAudioFx` applies a band-pass (1450 Hz, Q=0.9), highpass at 200 Hz, soft tanh saturation, and a 120 ms squelch tail. Toggle via `UserPreferences.PilotVoiceRadioFxEnabled`.
- **Output.** `PortAudioFloatPlayer` writes mono float32 to the default output device.

The pipeline is silent and harmless if Piper isn't installed — `IsAvailable` returns false and `Enqueue` no-ops. The terminal SAY lines still render either way.

## Pitfalls

- **Don't push solo-mode pilot speech to `PendingNotifications`.** Use `QueueSoloPilotTransmission` / `QueueSoloPilotReadback`. Pre-queue paths bypass airtime serialization and can step on awaited readbacks.
- **Don't regex-strip the TTS form to recover the terminal form.** Builders that want different terminal/TTS output should return `PilotSpeechText` directly.
- **Wire new sim-control toggles through `RecordedSettingChange`.** `SimulationEngine.ApplySettingChange` must handle the new key, and the UI emitter must record one. Otherwise the bundle replays with stale values. The pacing rates and `RpoShowPilotSpeech` are the live examples.
- **`PilotPendingRequest.LastPilotLine` is replayed verbatim.** When the follow-up fires, the original line is re-queued — including any transient airport/runway IDs. Don't bake user-mutable identifiers into the line if they could change.
- **Two `ResolveAltitudeGoal` implementations.** `FlightPhysics.UpdateAltitude` uses `AltitudeSnapFt` hysteresis; `AirspaceDatabase.ProjectAltitude` doesn't (projection wants raw trajectory). Both are correct for their use cases — keep them in sync if you change the floor/ceiling resolution rules.
