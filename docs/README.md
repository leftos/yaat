# YAAT Docs — Start Here

**Looking for code, or about to change a subsystem? Read the map before reading source.** YAAT's docs front-load each subsystem's overview, contracts, and footguns — starting here is faster and more accurate than grepping blind.

**Where docs live.** The repo root holds the user-facing docs only (`README`, `INSTALL`, `GETTING_STARTED`, `USER_GUIDE`, `COMMANDS`, `SOLO_TRAINING`, `ARTCC_CUSTOMIZATION`, `CHANGELOG`); developer docs live here in `docs/`, and the root README points users at the root set. A developer doc still at the root is moved here.

## 1. Locating files

→ **[`architecture.md`](./architecture.md)** — the full annotated file tree. Its top section, *"Task Index — I need to change X, which files?"*, maps common tasks straight to the relevant files in order of relevance. **Read this first.**

## 2. Understanding a subsystem

→ The **"Subsystem references"** table in [`../CLAUDE.md`](../CLAUDE.md) maps each area of the codebase to its design doc. Open the matching doc **before exploring, searching, or editing** that area. Quick pointers:

| Area | Doc |
|------|-----|
| Vocabulary and decisions | [`../CONTEXT.md`](../CONTEXT.md) (the glossary — sim-second, spine, host, run kind, action, arm, baked draw), [`adr/`](./adr/) (0001–0007: the tick-path and action-router decisions) |
| Ground / taxi / exits | [`ground/README.md`](./ground/README.md) |
| ERAM command syntax, validation and feedback text (what yaat-server's ERAM handling is held to) | [`eram/README.md`](./eram/README.md) |
| Phases | [`phases.md`](./phases.md) |
| Command input → queue | [`command-pipeline.md`](./command-pipeline.md), [`command-handlers.md`](./command-handlers.md) |
| Chaining (`;`/`,`) contract | [`command-chaining.md`](./command-chaining.md) |
| Flight physics | [`flight-physics.md`](./flight-physics.md) |
| Approach / pattern geometry | [`approach-and-pattern-geometry.md`](./approach-and-pattern-geometry.md) |
| Landing / runway exit | [`landing-and-runway-exit.md`](./landing-and-runway-exit.md) |
| Navigation database / routes | [`navigation-database.md`](./navigation-database.md) |
| Weather / wind | [`weather-and-wind.md`](./weather-and-wind.md) |
| Live-traffic shadows | [`live-traffic.md`](./live-traffic.md) |
| Snapshots / replay / bundles | [`snapshots-and-replay.md`](./snapshots-and-replay.md) |
| Server rooms / hub, scenario load (prepare/commit, load flag, resource pin, progress) | [`server-rooms-and-hub.md`](./server-rooms-and-hub.md), [`training-hub-contract.md`](./training-hub-contract.md) |
| CRC display state | [`crc-display-state.md`](./crc-display-state.md), [`crc-protocol-support.md`](./crc-protocol-support.md) (hub-method status table) |
| Client (`MainViewModel`) | [`client-mainviewmodel.md`](./client-mainviewmodel.md) |
| Radar / map rendering | [`radar-rendering.md`](./radar-rendering.md) |
| Ground view rendering | [`ground-rendering.md`](./ground-rendering.md) |
| Speech (STT) / pilot speech (TTS) | [`speech-recognition-pipeline.md`](./speech-recognition-pipeline.md), [`solo-training-pilot-speech.md`](./solo-training-pilot-speech.md); measurements in [`research/`](./research/) (e.g. the controller-voice ouroboros baseline) |
| Pilot phraseology (wording / AIM) | [`pilot-phraseology.md`](./pilot-phraseology.md) |
| Driving the real client / CRC from an agent | [`client-driver-mcp.md`](./client-driver-mcp.md) |
| Setting up CRC against a local server (profile, connect, FPE) | [`crc-first-session.md`](./crc-first-session.md) |
| vEDST sign-in: enabling it on a server, connecting a vEDST checkout | [`vedst-sign-in.md`](./vedst-sign-in.md) |
| Tests | [`test-map.md`](./test-map.md) (which class of test pins what, and where a new one goes), [`test-harness.md`](./test-harness.md), [`e2e-tdd-issue-debugging.md`](./e2e-tdd-issue-debugging.md), [`test-suite-speed.md`](./test-suite-speed.md) |

The table above is a quick index, not the full list — **[`../CLAUDE.md`](../CLAUDE.md) holds the complete, authoritative subsystem-references table.** When in doubt, consult it.

## 3. Plans & roadmap

→ The plan lives in Linear, team YAAT; [`plans/MAIN.md`](./plans/MAIN.md) is a generated snapshot of it. [`plans/README.md`](./plans/README.md) says what each design file and programme folder under `plans/` holds; finished plans are deleted, not archived.

---

*Agents: the user-level `Explore` agent explores the codebase — it follows this docs-first protocol automatically.*
