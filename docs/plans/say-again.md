# Say again

Say again is a command for students. It asks a pilot to repeat its last transmission (or part of it), as a controller does on frequency. Index line: [MAIN.md](./MAIN.md), under **Programmes next up**. Work starts after the next release is cut (user 2026-10-01).

## Map (exploration 2026-10-01, main @ 6e6e5caf)

- **Transmissions.**
  - `PilotResponder.Build*` produces a `PilotSpeechText` with separate terminal and TTS forms.
  - Readbacks come from `SimulationEngine.ApplyPostDispatch` (`SimulationEngine.Commands.cs` ~:166-240); proactive calls from `PilotProactive` and phases; reminders from `PilotRequestTracker`.
  - Every one is queued by `PilotResponder.QueueSoloPilotTransmission` into the transient `AircraftState.PendingPilotTransmissions`.
  - `SimulationWorld.DrainReadyPilotTransmissions` moves them into the single `ActiveFrequency`. `FrequencyState` applies the airtime, awaited-readback and controller-response gates.
  - The spine's `OnPilotTransmissions` then runs on the server (`TickProcessor.BroadcastPilotTransmissions`: terminal entry plus `PilotTransmissionBroadcast`). The client speaks it through `PilotVoiceService` when solo mode and voice are on.
- **No per-aircraft "last transmission" exists.**
  - The pending list is cleared on drain.
  - `FrequencyState` keeps no history and is not snapshotted.
  - The only retained line is `PilotPendingRequest.LastPilotLine`/`LastPilotLineTts`, which is snapshotted but covers proactive requests only.
- **No say-again exists today.** No command, no STT rule, and no pilot "say again" on an unmapped transcript.
- **Models to copy.**
  - `AcknowledgePilotContact` (`STBY`/`ROGER`, `CommandRegistry.cs` ~:1211; STT rules `PhraseologyRules.cs` ~:285) is the argument-less student verb that talks to the pilot side.
  - `ContactCommandHandler.Route` (~:112-132) splits solo from RPO, and its `HasLeftStudentFrequency` check applies here too.
  - The `SAY*` report family (`CommandDispatcher.cs` ~:1035) writes terminal entries outside the frequency model, which is the wrong model for a repeat.
- **Determinism.** A say-again is a recorded command, and `ApplyPostDispatch` runs on replay. A re-queue therefore replays identically, but only if the last transmission is snapshotted Sim state (a rewind restores from a snapshot). That means `AircraftSnapshotDto` plus a `SnapshotSchemaMigrator` step.
- **Grounding.** 7110.65 Pilot/Controller Glossary "SAY AGAIN": "Used to request a repeat of the last transmission. Usually specifies transmission or portion thereof not understood or received; e.g., 'Say again all after ABRAM VOR.'" The local AIM has no "say again" phraseology beyond the glossary example.
- **Gate.** Solo only (`ctx.SoloTrainingMode`): an RPO is the pilot. Solo scoring (`SoloTrainingEvaluator.RecordControllerCommand`) must treat it as neutral.
- **Overlap.**
  - Pilot-AI M11.3 (readback errors, "I say again") and M11.4 (stepped-on transmissions, "station calling, say again") need the same last-transmission store.
  - The STT tuning wave shares `PhraseologyRules.cs`.

## Decisions (user 2026-10-01)

- **Re-queued in the Sim.** The last transmission is snapshotted Sim state. A say-again puts it back on the frequency, subject to airtime and the gates, so it replays identically.
- **"Say again callsign" is supported** besides the whole-transmission repeat: the pilot repeats only its callsign. Partial repeats ("all after/before X") are not supported; a plain "say again" repeats the whole transmission.
- **Nothing to repeat:** the pilot answers briefly (e.g. "{callsign}, I didn't say anything"). An aircraft that has left the frequency gets the refusal `CONTACT` already gives.
- **Feature branch** `feat/say-again`, opened when work starts.
- **Verb** (user): `AGAIN` repeats the whole transmission, `AGAIN CS` only the callsign. Neither ATCTrainer's nor VICE's command list has a say-again verb; `SA` is already taken in `CommandRegistry`.
- **Last transmission** (user): any pilot line on the frequency counts — readbacks, proactive calls, reports and "unable" lines. The repeat itself does not replace it (its text is the same).
- **Mid-transmission** (user): at most one pending repeat per aircraft. A say-again while the pilot is transmitting or has a line queued queues one repeat behind it; a second say-again while one is pending is a no-op. The pending flag is snapshotted with the last transmission.

## Open decisions

1. STT phrases ("say again", "say again callsign", "repeat"), and how an unaddressed "station calling ground, say again callsign" picks its aircraft (the last one that transmitted?).
6. Pilots asking the student to say again (M11.3/M11.4 territory).

## Task Index rows for the landing commit

- **Change what a pilot says on the radio / add a student radio command:** `docs/solo-training-pilot-speech.md` → `PilotResponder` → `FrequencyState` → `SimulationWorld.DrainReadyPilotTransmissions` → `ApplyPostDispatch` → `BareHost` / yaat-server `TickProcessor.BroadcastPilotTransmissions` → client `MainViewModel.Aircraft.cs` / `PilotVoiceService`.
- **Add an STT phrase for a command:** `PhraseologyRules.cs` → `PhraseologyMapper` → `SpeechRecognitionService` → `docs/speech-recognition-pipeline.md`.
