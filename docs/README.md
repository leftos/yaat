# YAAT Docs — Start Here

**Looking for code, or about to change a subsystem? Read the map before reading source.** YAAT's docs front-load each subsystem's overview, contracts, and footguns — starting here is faster and more accurate than grepping blind.

## 1. Locating files

→ **[`architecture.md`](./architecture.md)** — the full annotated file tree. Its top section, *"Task Index — I need to change X, which files?"*, maps common tasks straight to the relevant files in order of relevance. **Read this first.**

## 2. Understanding a subsystem

→ The **"Subsystem references"** table in [`../CLAUDE.md`](../CLAUDE.md) maps each area of the codebase to its design doc. Open the matching doc **before exploring, searching, or editing** that area. Quick pointers:

| Area | Doc |
|------|-----|
| Ground / taxi / exits | [`ground/README.md`](./ground/README.md) |
| Phases | [`phases.md`](./phases.md) |
| Command input → queue | [`command-pipeline.md`](./command-pipeline.md), [`command-handlers.md`](./command-handlers.md) |
| Flight physics | [`flight-physics.md`](./flight-physics.md) |
| Approach / pattern geometry | [`approach-and-pattern-geometry.md`](./approach-and-pattern-geometry.md) |
| Landing / runway exit | [`landing-and-runway-exit.md`](./landing-and-runway-exit.md) |
| Navigation database / routes | [`navigation-database.md`](./navigation-database.md) |
| Weather / wind | [`weather-and-wind.md`](./weather-and-wind.md) |
| Snapshots / replay / bundles | [`snapshots-and-replay.md`](./snapshots-and-replay.md) |
| Server rooms / hub | [`server-rooms-and-hub.md`](./server-rooms-and-hub.md), [`training-hub-contract.md`](./training-hub-contract.md) |
| CRC display state | [`crc-display-state.md`](./crc-display-state.md) |
| Client (`MainViewModel`) | [`client-mainviewmodel.md`](./client-mainviewmodel.md) |
| Radar / map rendering | [`radar-rendering.md`](./radar-rendering.md) |
| Speech (STT) / pilot speech (TTS) | [`speech-recognition-pipeline.md`](./speech-recognition-pipeline.md), [`solo-training-pilot-speech.md`](./solo-training-pilot-speech.md) |
| Pilot phraseology (wording / AIM) | [`pilot-phraseology.md`](./pilot-phraseology.md) |
| Tests | [`test-harness.md`](./test-harness.md), [`e2e-tdd-issue-debugging.md`](./e2e-tdd-issue-debugging.md) |

The table above is a quick index, not the full list — **[`../CLAUDE.md`](../CLAUDE.md) holds the complete, authoritative subsystem-references table.** When in doubt, consult it.

## 3. Plans & roadmap

→ [`plans/`](./plans/) — the main plan and open-issue plans. Milestone roadmap lives there.

---

*Agents: prefer the `yaat-explore` agent for codebase exploration — it follows this docs-first protocol automatically.*
