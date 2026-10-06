# Tick-path unification

**Status:** top priority (steer 2026-09-02; controller AI waits). Steps 1–3 and 3d shipped (ADRs 0001–0007); **step 4, relocation, is done**; **step 5 is done** ([05-retirements.md](./05-retirements.md)), so every step has shipped.

Step 4's record is [04-relocation.md](./04-relocation.md); its first slice shipped 2026-09-06: attendance is a recorded input, and delayed handoffs, auto-accept, point-out auto-ack and the autotrack passes are Sim steps — the `ProcessAutoAccept` family the step-5 residual was made of is retired and every oracle baseline is empty.

The session clock shipped 2026-09-07 (one pinned `SessionStartUtc`; TDLS timestamps, strip PDT/ETA text and the METAR observation clock read it). Strips and TDLS crossed into the snapshot 2026-09-07 (engine-owned; the recorded TDLS verbs and the spawn hooks re-apply on every run kind; the `actions`/`s2oak4` test and replay baselines now bank the strip/TDLS host bodies).

Shrinking `IActionHost` is underway (plan 04c, deleted — its record is the `IActionHost` item in [04-relocation.md](./04-relocation.md)). The TDLS bodies (commit A), the strip bodies (commit B) and the coordination bodies (commit C) crossed 2026-09-07.

With that crossing, the `Strip`/`Tdls`/`TdlsOps`/`Coordination`/`GlobalCoordination` arms are Sim arms, the strip auto-print, deferred strip dispatch, TDLS and coordination-timer steps are Sim steps, a coordination item's id is deterministic (`{ListId}-{SequenceNumber}`), `IHostSteps`'s step-4 debt is `LiveTrafficSync`, `TowerLists`, and **all twelve oracle baselines are empty** with the `actions` fixture now scripting `RD`/`RDH`/`RDACK`/`RDR`.

The bookmark, clock, CRR-group and ASDE-X/SAID-mutation slots crossed 2026-09-08 and the ASDE-X safety-logic configuration, the last, 2026-09-19 (`IActionHost` has no `Apply*` slot left).

The ASDE-X alert set crossed 2026-09-20 (entry I: `SimulationEngine.TickAsdexAlerts` is a Sim step over the snapshotted `SimScenarioState.ActiveAsdexAlerts`, `IHostSteps.AsdexAlerts` and `TickProcessor.ProcessAsdexAlerts` are deleted, the host broadcasts the diff `OnAsdexAlertsChanged(new, cleared)` hands it).

`LiveTrafficSync` crossed 2026-09-24 as a Sim step over a feed port the server implements, so `IHostSteps` has no simulation-affecting member left, and the live-traffic `DEL` set crossed into the snapshot the same day (plan 04e, deleted 2026-09-24 once its step 4 put the live-traffic `DEL` set into the snapshot; its record is `docs/live-traffic.md`).

04d's commit D shipped 2026-09-07 (the tower-list dwell entries are snapshotted, `TowerLists` is a Sim step, `IHostSteps`'s debt is `LiveTrafficSync` alone; 04d deleted, its record is in [04-relocation.md](./04-relocation.md)). The reconstruct-vs-live-log pin on the 2026-09-06 S2-OAK-4 bundle shipped 2026-09-08 as a recording-completeness fix (seeded session settings are recorded at t=0).

The disconnect coast crossed as coasts A–D (sim core, snapshot, server wiring), and step 4 closed 2026-10-03: what is left in the server's `RoomStateSnapshotDto` is identity and display state no tick body reads, so it stays server-side (owner ruling, YAAT-17).

The design was re-derived clean-room this session; the decisions are ADRs [0001](../../adr/0001-state-equivalence-is-the-tick-contract.md)–[0006](../../adr/0006-decompose-simulationengine-before-adding-to-it.md) and the vocabulary is [`CONTEXT.md`](../../../CONTEXT.md). It replaces the deleted `tick-loop-unification.md`, which scoped to post-physics only and carried three factual claims that did not hold. Ordered steps, each green on its own, incremental to main:

## Steps

| Step | File | Status |
|---|---|---|
| 1. Oracle first (+ 1b: the missing legs and the two blind-spot fixtures) | ADR [0004](../../adr/0004-the-oracle-and-the-corpus.md), [docs/tick-loop.md](../../tick-loop.md#the-tick-oracle--what-guards-the-two-paths-against-each-other) | shipped 2026-09-02 |
| 2. `SimulationEngine` decomposition | ADR [0006](../../adr/0006-decompose-simulationengine-before-adding-to-it.md) | shipped 2026-09-02 |
| 3. Spine over the whole sim-second, run profile, `host` rename (3a, 3b, 3c-0, 3c) | ADRs [0001](../../adr/0001-state-equivalence-is-the-tick-contract.md), [0005](../../adr/0005-host-and-run-profile.md) | shipped 2026-09-04 |
| 3d. The action router (3d-0 … 3d-6) | ADR [0007](../../adr/0007-one-action-router.md) | shipped 2026-09-06 |
| 4. Relocate tick-reachable ATC logic into `Yaat.Sim` | [04-relocation.md](./04-relocation.md) | **done** 2026-10-03 — first slice (attendance + track automation) shipped 2026-09-06; `IActionHost` emptied of `Apply*` slots 2026-09-19; the ASDE-X alert step crossed 2026-09-20, `LiveTrafficSync` crossed 2026-09-24 (plan 04e, deleted), leaving no step-4 host step; the disconnect coast (A–D) closed it |
| 5. Retire the accepted divergences; hash + step trace | [05-retirements.md](./05-retirements.md) | **done** 2026-10-04 — three retirements 2026-09-04; the step trace and the per-second state hash ship; the cleanups are done (the uncalled drift verification deleted, the pre-tick loops on one pump, the trace always on and `TickTimings` opt-in by owner ruling) |

## Rules every step follows

The rules (the server is a thin layer, predict then re-baseline, corpus triage, green across both repos) and the live behaviour changes that land as named decisions are in [docs/tick-loop.md](../../tick-loop.md#rules-for-a-change-to-the-tick-path).

## History

- [x] Latent bug found during the review, fixed independently 2026-09-02 (`5ee2f4a0`): `_replayTrackApplier` was documented replay-only but `DispatchAiCommand` uses it on the live path, so a recorded `AS` carrying an AI connection id displaced the live AI's identity. `ResolveEffectiveIdentity` now resolves the AI branch before the shared selected-position map; pinned by `ReplayTrackApplierIdentityIsolationTests`
