# Say again

A command for students: "say again" asks a pilot to repeat its last transmission (or a part of it), as a controller does on frequency. Index line: [MAIN.md](./MAIN.md), under **Programmes next up**. It starts after the next release is cut (user 2026-10-01).

## Open

Exploration under way. It covers:

- how pilot transmissions are produced, queued, spoken and logged (`src/Yaat.Sim/Pilot/`, `PendingPilotTransmissions`, `PilotVoiceService`);
- what a student can send in solo mode, typed and by voice;
- whether a "last transmission" per aircraft is kept anywhere;
- the phraseology (AIM 4-2-x, 7110.65 §2-4-x) for "say again", "say again all after/before", and a pilot's own "say again" back to the controller.
