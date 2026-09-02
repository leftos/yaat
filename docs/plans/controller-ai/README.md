# Controller AI + Soak-Testing Harness

This folder coordinates the controller-AI plan. Each subsystem has its own subdesign file; this
README is the coordination doc — context, architecture, locked decisions, milestone tracking, reused
infrastructure, and the deferred list.

**Supersedes** the early-stage `controller-ai.md` plan (now in
[`archive/controller-ai-solo-training.md`](../archive/controller-ai-solo-training.md)) —
that document predates most of the current sim architecture and was not used as design input. The
solo-training AI co-play idea it described survives as milestone CA7's consumer of the tower brain.

## Context

YAAT has a fully automated pilot side (PilotProactive, PilotResponder, the phase system) but the
controller seat is always human. This feature adds **AI controllers** — deterministic rule-based
position brains for Ground, Tower/Local, Approach/Terminal, and Center/Enroute — whose primary
purpose is **internal soak-testing**: run scenarios for many sim-hours with the AI controlling
everything and surface sim bugs (tick exceptions, stuck aircraft, ground gridlock, silent despawns,
separation anomalies) with triage-ready artifacts. It is internal-only: dev/local builds, never
active on published builds or public servers.

The controller seat is a genuinely empty seat today — the only sim-issued ATC clearance in
production is the narrow hold-for-release auto-CTO. The brains fill that seat by issuing the same
canonical commands a human RPO types, through the same dispatch pipeline; phases execute everything,
exactly as they do for humans.

## Demo (the smallest compelling thing)

A single command line:

```
Yaat.SoakRunner run --scenario oak_ground_easy.json --seed 42 --sim-hours 4 --positions GC,LC
```

runs a seeded 4-sim-hour KOAK session headless at maximum speed — AI Ground taxiing departures and
answering arrivals, AI Local sequencing takeoffs and landings — and exits with a findings report. A
stuck-taxi bug shows up as one `stuck-aircraft` finding with a duration, a repro one-liner, and a
replayable recording whose timeline bookmark jumps straight to the moment it happened:

```
python tools/bug_bundle.py history episodes/ep00/recording.zip --callsign N152SP
```

Re-running the same seed reproduces the run byte-for-byte.

## Architecture

```
            ┌───────────────────────────────────────────────────────────┐
            │  Position brains (Yaat.Sim.ControllerAi)                  │
            │  GroundBrain → LocalBrain → ApproachBrain → CenterBrain   │
            │  guarded rules + per-aircraft intent memo, seeded, paced  │
            └──────────────────────────┬────────────────────────────────┘
                                       │ AiCommandRequest (canonical text, AS-prefixed)
                                       ▼
            ┌───────────────────────────────────────────────────────────┐
            │  Host dispatch — RoomEngine.SendCommandAsync("AI:{pos}")  │
            │  production routing + RecordedCommand + terminal + b'cast │
            └──────────────────────────┬────────────────────────────────┘
                                       │ same tick loop as production
                                       ▼
   ┌─────────────────────┐   ┌─────────────────────────────┐   ┌─────────────────────────┐
   │ HeadlessRoomHost    │   │ TickProcessor.ProcessPost-  │   │ Recording (v4 archive)  │
   │ (soak runner, 07)   │   │ Physics — auto-accept,      │   │ streamed snapshots +    │
   │ episodes × seeds    │   │ conflicts, pilot AI, …      │   │ finding bookmarks       │
   └──────────┬──────────┘   └──────────────┬──────────────┘   └─────────────────────────┘
              │                             │
              ▼                             ▼
   ┌───────────────────────────────────────────────────────────┐
   │  Detectors (Yaat.Sim.Soak, 08) — shared by runner + live  │
   │  Tier A hard failures · Tier B progress · Tier C safety   │
   │  → FindingAggregator → findings.jsonl + summary + triage  │
   └───────────────────────────────────────────────────────────┘
```

**Key choice #1 — brains issue canonical commands only; phases execute.** The mirror of the pilot
AI: no parallel execution model, no direct state mutation, one integration point.

**Key choice #2 — host dispatches; brains never run in replay.** AI commands are recorded exactly
like human commands (`RoomEngine.SendCommandAsync`, synthetic `"AI:{position}"` connection id);
replays re-drive the recorded commands with the AI off, so brain logic can evolve without breaking
historical recordings, and a soak finding's recording is its own reproduction.

**Key choice #3 — real position identities.** Each AI position is a `TrackOwner`/TCP resolved from
ArtccConfig, using the real TRACK/HO/ACCEPT/HFR/REL machinery — exercising track-sharing and release
code paths is soak coverage, and it is what lets a human take over any single position mid-session.

## Subdesigns

| Doc | Contents |
|---|---|
| [01-architecture.md](01-architecture.md) | Core abstractions, command sink + dispatch/replay contract, hosting, enablement |
| [02-positions-and-handoffs.md](02-positions-and-handoffs.md) | Identity/TCP resolution, jurisdiction query, gate-to-gate flows, auto-service precedence, partial staffing, GC↔LC coordination bus |
| [03-decision-framework.md](03-decision-framework.md) | Rules + memo FSM, determinism, pacing, anomaly log, rejection policy |
| [04-ground-brain.md](04-ground-brain.md) | Ground v1 rule set + non-goals |
| [05-tower-brain.md](05-tower-brain.md) | Tower v1 rule set (CTO/LUAW/CLAND/GA gates) + non-goals |
| [06-approach-center.md](06-approach-center.md) | Approach/Center later-milestone sketches |
| [07-soak-runner.md](07-soak-runner.md) | HeadlessRoomHost, CLI, episode model, artifacts, throughput plan |
| [08-detectors-and-findings.md](08-detectors-and-findings.md) | Detector framework, full detector table, findings schema, soak-triage |
| [09-live-attach.md](09-live-attach.md) | Dev-only gating, room enablement, finding surfacing |
| [10-facility-knowledge.md](10-facility-knowledge.md) | Codified facility SOP/LOA knowledge (schema, extraction tool, overlay contract) |
| [11-radio-model.md](11-radio-model.md) | Per-frequency radio: per-position `FrequencyState`, exchange lock, party line, collisions, `TunedPosition` persistence |
| [12-milestone-v1-scope.md](12-milestone-v1-scope.md) | The ordered v1 slice — 7 phases, 29 requirements, cross-cutting failure modes |

## Milestones

Core (CA) and harness (H) series interleave; each row is independently shippable and testable.
(There is no CA4 — the core design's original "soak harness" milestone became the H series.)

**The work in flight is the v1 slice** — the radio prerequisite, CA2 and H1, cut into seven ordered
phases with success criteria and requirement traceability in
[12-milestone-v1-scope.md](12-milestone-v1-scope.md). Read that for what is being built now; this table
stays the whole-feature map.

| | # | Milestone | Summary |
|---|---|---|---|
| [x] | H0 | Headless host + skeleton | **Shipped 2026-09-01.** `HeadlessRoomHost`, seeded load (`LoadScenarioSeededAsync` — the live load with an explicit seed + magnetic-model day; all three world RNG streams seeded), `RoomEngine.AdvanceLiveSecond` (the production per-second body, shared with the hosted loop), SoakRunner `run` with **no AI**: tick loop, streamed recording, SimLog tap, tick-exception detection, `TickTimings` dump. Measured **~420× realtime** on S1-OAK-2/3/4 (30 aircraft, 1 sim-hour in 8.6 s wall, Release) — well above the 50–200× estimate. Deviations from [07](07-soak-runner.md): the seeded core is a shared `PopulateRoom`, not a rewind-reload wrapper; the loop lives in `src/Yaat.Server/Soak/` (testable without an Exe reference); `findings.jsonl` waits for H1 (the tick exception lives in `report.json` + a bookmark). Two prerequisite fixes landed with it: `DEL` stamps `CompletionReason.Dropped`, and the magnetic-model evaluation day is recorded per session so replays never drift |
| [x] | CA0 | Core foundations + observer mode | **Shipped 2026-09-01** in two commits. CA0a: `DispatchOrigin` + `PilotContactRoster` (pilots call whichever answering position is responsible; AI commands never mark student contact or score). CA0b: `ControlRole`, `AiPositionResolver` (cab + TRACON + ARTCC catalog; `_DEL` skipped unless overridden), `ControllerAiConfig` (snapshotted), `IAiStaffing` (`HeadlessAiStaffing`, server `RoomAiStaffing`), `PositionJurisdiction` + `AiWorldView`, `AiAnomalyLog`, `AiControllerService` (owns `AiRng`), `EngineAiCommandSink` / server `HeadlessAiCommandSink`, the four observer rules + `ObserverBrain`, `SimulationEngine.TickControllerAi` (called from `RoomEngine.AdvanceLiveSecond`; never in replay/playback), SoakRunner `--positions GC,LC` + `anomalies.jsonl`, the routing-parity spike (`AiSinkRoutingParityTests`: TAXIAUTO/CTO/TRACK/DROP identical through the room and the engine; coordination verbs are the exemption — engine refuses), and the determinism tests (engine + headless). Deviations from 01/03: `AiRng` lives on the service (not snapshotted); `AiTickContext` carries no coordination bus yet; AI track commands carry no `AS` prefix — the `AI:{positionId}` connection id resolves to the position in both replay resolvers; pacing/`AiIntent`-driven command rules wait for CA1 |
| [x] | CA1 | Ground brain v1 | **Shipped 2026-09-01.** `GroundBrain` runs the three CA0 watchdogs unpaced, then four paced decision rules: answer-taxi-out (`TAXIAUTO <runway in use>`), runway-crossing (when nobody — human or AI — holds Local, Ground works the runway as a combined position and clears `CROSS <near end>` behind `RunwayCrossingGate`; when Local is staffed it asks once on the terminal and opens `CoordinationTimeout` after 120 s), answer-taxi-in (`TAXIAUTO @<the parking the pilot asked for>`), hand-to-local (`CT <tower>` 1,200 ft short of the departure bar with every crossing behind). `AiPacing` (5 ± 2 s gap from `AiRng`, 2–8 s FNV think time, one transmission per position per tick), `AiAircraftMemo` (intent, in-flight command, effect deadline, two retries then give up), `RunwayInUseResolver` (§3-5-1 generic; `ControllerAiConfig.RunwayInUse` / SoakRunner `--runway` override; calm falls back to the longest pavement by designator), the arrival's taxi-in call (`TaxiInRequest` from the post-exit idle phases; `ArrivalParkingPicker` — the pilot names an operator-appropriate free spot), origin-neutral `CT` (an AI transfer never flips the student-frequency flag or stamps a handoff), `IAiStaffing.IsHumanHeld(AiPositionConfig)`, and the derived `Parked` auto-delete default for scenarios that keep spawning traffic. **Findings:** with S1-OAK-2/3/4's arrival generators on, arrivals exiting 30 taxi in along W against departures taxiing out on W and the reactive give-way physics locks the two flows head-on within twenty minutes (v1.1 flow sequencing — the acceptance E2E runs the thirty departures alone); a gate-exiting 737 can yield five minutes to the terminal-lane stream (the stuck watchdog now keeps the yield threshold for the whole stall). Deviations from [04](04-ground-brain.md): no coordination bus yet (the combined-position gate and the terminal request line stand in), pre-clearance 500 ft short of a bar, the runway-in-use knob lives on `ControllerAiConfig`. Aviation-reviewed 2026-09-01 (must-fixes in: the gate closes for a go-around/low approach over the pavement; the taxi-in call waits for the tower's release when a separate Local answers; a combined cab transfers nobody; paced, repeated coordination requests). **Soak (Release, 2026-09-01):** S1-OAK-2/3/4 `--positions GC --runway 30`, 2 sim-h in 14 s (505×); S2-OAK-4 `--positions GC --runway 28R`, 1 sim-h in 5 s (693×); zero rejected commands, unanswered requests or coordination timeouts — the only anomalies are StuckAircraft queue waits (no Local to release the departure queue, plus the W finding with the generators on) |
| [x] | K1 | Facility-knowledge schema + OAK (runway-selection subset) | **Shipped 2026-09-01 as K1-lite.** `FacilityOps` records (strict JSON, every entry cites its SOP paragraph), `FacilityOpsDatabase` (loaded at startup from `Data/FacilityOps/`, cross-validated against navdata — a bad runway id stops the server), `KOAK.json` (OAK ATCT SOP 1-6 configurations SFOW/OAKE/SFOE with SFO's runway sets, 4-2 selection incl. the 10-kt threshold and the 4-2.c SFO coupling, 3-4 assignment policy), `SopAircraftClassifier` (NCT SOP 1-7 P/T/J), `FacilityRunwaySelector` + `FacilityRunwayAssigner`, `RunwayUsabilityGate` (10 kt dry / 5 kt wet tailwind — a violation files `KnowledgeConflict` and the generic rule decides), `ControllerAiConfig.RunwayConfigurations` + SoakRunner `--runway-config KSFO=SFOE`. Consumed by the Ground brain's rule 1 through `RunwayInUseState`. **Soak (Release, 2026-09-01):** S1-OAK-2/3/4 `--positions GC` with no `--runway`, 1 sim-h in 9.6 s (376×) — the calm OAK went SFOW, 22 jets/turboprops taxied to 30 and 8 pistons to 28R, the SOP split with no session knob. Deferred to K1b (with CA2): departure tables, release rules, missed-approach tables, crossing approvals, pattern preferences, jurisdiction overrides. See [`docs/facility-ops-knowledge.md`](../../facility-ops-knowledge.md) |
| [ ] | CA2 | Tower brain v1 + coordination + precedence | **v1 slice phases 4–5** ([12](12-milestone-v1-scope.md)); needs the per-frequency radio model ([11](11-radio-model.md)) first. [05](05-tower-brain.md), coordination bus, auto-accept/pointout skips, auto-CTO suppression, `IAiStaffing`. First full gate-to-gate loop |
| [ ] | H1 | Smallest useful soak | **v1 slice phases 6–7** ([12](12-milestone-v1-scope.md)). Detector framework (TDD) + Tier A + stuck-aircraft + ai-rejection, findings.jsonl + summary, per-finding snapshots + bookmarks. Target: GC+LC, one scenario, one seed |
| [ ] | H2 | Scale | Generator traffic source, episode loop, seed matrices, `--parallel`, `report` aggregation, disk budget |
| [ ] | CA3 | Live-room hosting | `RoomAiCommandSink`, `ProcessControllerAi`, `RoomAiStaffing`, human-takeover verified end-to-end (cross-repo) |
| [ ] | H4 | Live attach | Config gate, `AIPOS` wiring, `RoomSoakMonitor` in the live tick, terminal surfacing |
| [ ] | H3 | Full detector set | Tier B remainder + Tier C, threshold config + per-scenario overrides, false-positive burn-down |
| [ ] | K2 | Extraction tool + SFO knowledge | [10](10-facility-knowledge.md): `tools/facility_ops_extract.py` (LLM-assisted offline, human-reviewed); `KSFO.json` proves the schema on the harder tower |
| [ ] | H5 | Determinism + triage | `verify` subcommand (snapshot-restore → replay-to-finding → assert recurrence), `bug_bundle.py soak-triage`, `docs/soak-testing.md` |
| [ ] | K3 | TRACON/Center/LOA contracts | [10](10-facility-knowledge.md): `sectorContracts`/`loaContracts` halves + `NCT.json`/`ZOA.json`, scoped to what CA5/CA6 consume |
| [ ] | CA5 | Approach brain v1 | [06](06-approach-center.md); HO/ACCEPT both ways, HFR/REL to satellite towers, RD participation |
| [ ] | CA6 | Center brain v1 | [06](06-approach-center.md) |
| [ ] | CA7 | Refinements | Pattern ops (TG/EF/ERB), LUAW broadening, anticipated separation, crossing-request canonical-command decision, solo-training co-play consumer |

Future (noted, not planned): a nightly xUnit `[Trait("Category","Soak")]` wrapper invoking
`EpisodeRunner` on a small matrix.

## Decisions committed

- **Rule-based, deterministic, seeded — no LLM in the core.** A soak failure replays exactly; runs
  are cheap and fast.
- **Brains + detectors in Yaat.Sim; runner + host in yaat-server** (RoomEngine path). Verified:
  bare `SimulationEngine.SendCommand` does not record, and handoff/track machinery is server-side —
  a pure-sim runner cannot do recorded gate-to-gate soak. Cross-repo feature, both halves land
  together.
- **Host dispatches; brains never run during replay** (key choice #2 above).
- **Real handoff machinery, real identities, partial staffing** (key choice #3 above).
- **Ground + Tower first**, then Approach, then Center.
- **Traffic: existing scenarios + seeded generators**; endless soak = bounded episodes with
  incrementing seeds.
- **Findings tiers:** hard failures and progress invariants can fail a run; separation/safety events
  are advisory only (they may be the AI's own fault — attribution via the decision log).
- **Artifacts:** per-episode v4 recordings, streamed snapshots, finding bookmarks, findings.jsonl,
  `bug_bundle.py soak-triage`.
- **Internal-only gating is process-level:** `Yaat:ControllerAi:Enabled` (default false, DI-gated,
  refused in Production) — no remote-enable path exists when off.
- **Facility SOP knowledge is codified data, applied as an overlay** ([10](10-facility-knowledge.md)):
  schema-validated per-facility JSON in the repo (navdata-cross-checked), drafted by an
  LLM-assisted offline tool with human review, never a runtime LLM; brains stay generically correct
  everywhere and refine where knowledge exists. Tower tier (OAK, then SFO) first.
- **Clean-room design:** the old `controller-ai.md` was written extremely early and was not used as
  design input; it is archived, not extended.
- **AI positions are student stand-ins (2026-09-01).** Pilots initiate contact with whichever position is
  responsible for them when that position is staffed by a pilot-answering agent — the solo student or an AI
  position — through `PilotContactRoster` (CA0a), not through a solo-mode gate; AI-addressed calls use the same
  pilot-transmission channel the student's do (so they are voiced when watching live). `HasMadeInitialContact`
  stays student-scoped (`AiInitialContactPositionIds` latches AI contact per position — each new facility is a fresh
  initial contact, AIM 4-2-3.a.1.1), and an AI-issued command is `DispatchOrigin.ControllerAi` (never student contact,
  never evaluator-scored). Aviation-reviewed 2026-09-01: tower-cab AI positions are matched on the airport the call is
  physically made at (never the filed destination); every candidate obeys the ARTCC's initial-contact transfer SOP
  (an arrival owned by approach with no handoff does not call an AI tower, 7110.65 §2-1-17); a ground call falls back
  to an AI tower working the cab alone; an aircraft inside the tower's arrival side never makes an airborne check-in
  with a student radar position (`TowerCabPhases.IsArrivalSide`). With the AI on in an instructor room, human
  commands get pilot read-backs and pilot calls reach the terminal/TTS (AIM 4-4-7) — intended: an AI-staffed room is
  a solo-style room. v1 limitations (MAIN.md follow-ups): one shared frequency/airtime model (an AI Ground's answer
  clears the awaiting-response gate held for a pilot waiting on tower); first-by-id between two same-type AI positions
  at one airport; the AI Ground's own §2-1-17.a duty to transfer the pilot to tower (`CT`/"monitor tower") is CA2
  brain work and a soak anomaly; `AiPositionResolver` must never hand a taxi request a Clearance Delivery radio name
  (AIM TBL 4-2-1 — `_DEL` stays out of Ground by default) nor a departure check-in an "…Approach" name for a `DEP`
  position.
- **Watching the AI live gets a per-position TTS voice (CA3/H4).** Each AI position's instructions are voiced with a
  randomly assigned unique speaker id via `BroadcastPilotTransmission`'s speaker-id mechanism; the instruction text
  comes from the phraseology rules.
- **Per-frequency radio model before CA2 (user steer 2026-09-01).** Several AI positions run concurrently by design
  (brains tick Ground → Local → Approach → Center; plan 02's coordination bus is off-frequency), but the radio is one
  shared `SimulationWorld.ActiveFrequency`, so every pilot and controller transmission competes for the same airtime
  and the readback gate is global. Before two AI positions talk to pilots at once (CA2), land a milestone that gives
  each aircraft a `TunedPosition` (set at spawn by the responsible position, by `CT`, by the AIM 4-3-14.a self-switch
  to tower at the hold-short, by handoff + `CT`; snapshotted), keys `FrequencyState` per position (replacing the single
  `HasLeftStudentFrequency` bool), and tags pilot and AI-controller transmissions with their frequency so the client
  plays only what the human monitors (student: own position; instructor: a selectable monitor set) and AI
  instructions occupy airtime on their own frequency with the per-position voice. The same milestone adds **radio
  discipline** (user steer 2026-09-01): an exchange owns its frequency until it completes or times out — controller
  instruction → pilot A readback, and pilot A request → controller response → pilot A readback — so pilot B's
  proactive calls and follow-ups queue behind it and an AI controller never keys up while a readback is pending
  (today's `FrequencyState` protects only the 8 s "awaiting controller response" window after a request). Tracked in
  `docs/plans/MAIN.md`.

## Reused infrastructure (don't reinvent these)

- **`CommandDispatcher.DispatchCompound` + `RoomEngine.SendCommandAsync`** — the single command
  entry points; the AI adds zero new dispatch paths. Precedents for in-sim automation:
  `DispatchSinglePreset`, `AutoIssueTakeoffClearance`.
- **`TrackOwner`/TCP + `AS <tcp>`** — position identity and acting-position replay, unchanged.
- **`RunwayOccupancy.ClassifyBest`** — the "who is on/short of/final for runway X" oracle.
- **`RunwayDepartureQueue`** — who's-next ordinals at each hold-short node.
- **Phase system + `CommandAcceptance`/`CommandResult`** — execution and structured accept/reject
  feedback for free.
- **`PilotProactive` / `PilotRequestTracker`** — the pilot requests the AI answers, and the
  bookkeeping that closes them.
- **`ProcessDeferredAutoTrack` / `ProcessAutoAccept`** — kept; AI positions are added as a skip
  (staffed), not a replacement.
- **`RngSeed`/`SerializableRandom` + `RecordedCommand`/ActionLog + `RecordingArchiveWriter`
  (streaming `WriteSnapshot`, `WriteBookmarks`)** — determinism and artifacts end-to-end.
- **`RoomEngineTestHarness`** — the wiring blueprint for `HeadlessRoomHost` (stays test-only).
- **`ConflictAlertDetector`/`EramConflictDetector`/`GroundConflictDetector`/`RunwaySafetyAdvisor`/
  `SoloTrainingEvaluator`/`AircraftStatusDescriber`/PendingWarnings drains** — existing bug oracles
  the detectors tap rather than re-derive.
- **`TickTimings`** — throughput instrumentation; **`bug_bundle.py`** — the triage surface.
- **`TestVnasData.EnsureInitialized()` + real layouts** — every brain/detector test.
- **`AircraftProfileOverrides` correction-layer pattern + `NavigationDatabase` static-singleton
  loading** — the mold for `FacilityOpsDatabase` ([10](10-facility-knowledge.md)).
- **`zoa-reference-cli`** (`C:\Users\Leftos\source\repos\zoa-reference-cli`, `sop` command) — the
  SOP/LOA catalog and PDF source for knowledge extraction.

## Deferred indefinitely (and why)

| Feature | Reason |
|---|---|
| LLM-driven or hybrid brains | Nondeterministic, slow, costly — wrong shape for soak throughput; revisit only if rule coverage plateaus |
| Voice/speech for AI controllers | Cosmetic for soak; the pilot-speech stack stays student-facing |
| Intersecting/converging runway ops, LAHSO, opposite-direction | Geometry + rule complexity with little added sim coverage; v1 refuses such scenarios explicitly |
| Crossing-request as a first-class canonical command | Needs a UI affordance decision; the coordination bus + terminal lines suffice for AI↔AI and AI↔human v1 |
| Rewind-exact brain state (`ControllerAiStateDto`) | Memo reset + re-derivation is correct; add only if triage demands bit-identical resume |
| Rolling-window/continuous-recording soak | The bounded-episode model makes it unnecessary |
| Nightly xUnit soak category | Wrapper over `EpisodeRunner` whenever wanted; runner-first keeps iteration fast |
| Exposing any of this to end users | Internal tool; revisit only after it has proven itself (per original intent: "for now? maybe?") |

## Cross-plan risks

1. **Headless routing parity** for track/coordination verbs (CA0 spike; may need a shared-router
   extraction — cross-repo).
2. **Dispatch outside the tick body**: verify `RoomEngine.SendCommandAsync` call-site assumptions
   from the runner loop and the live hook (the auto-CTO precedent is engine-internal).
3. **GC/LC jurisdiction seam**: no per-aircraft tuned-frequency model; `CT` semantics must be
   verified before it anchors the transfer point.
4. **Solo-evaluator isolation**: AI commands must never score as student actions.
5. **800 ms shared tick budget** (all rooms, one loop): AI + detectors instrumented via
   `TickTimings` from day one.
6. **Throughput**: H0 validates the 50–200× real-time estimate before the matrix design is trusted.
7. **False positives**: stuck-phase allowlist completeness; H3 burn-down pass.
8. **Separation-finding attribution**: mitigated by tiering + the per-command decision log, never
   fully automatable.
9. **Implementation verifications flagged**: taxi-to-parking canonical; which dispatch paths need
   `PilotRequestTracker.ApplyControllerResponse`. (SRS Category I/II/III resolution already exists —
   `SameRunwaySeparation.ResolveSrsCategory` reads the FAA database's SRS column; note its
   (I-behind-II) cell returns 4,500 ft where §3-9-6.a.2 permits 3,000 ft — conservative,
   pre-existing, acceptable.)

## Verification approach

- **Determinism first**: CA0's regression test (same scenario + seed → byte-identical action logs)
  lands before any decision rule.
- **TDD per rule/detector** with real navdata and layouts; `aviation-sim-expert` review of every
  rule set before implementation and re-review after.
- **H5 `verify`** closes the loop: a captured finding's recording must reproduce the finding on
  replay.
- Cross-repo: `pwsh tools/test-all.ps1` after any Yaat.Sim signature change.
