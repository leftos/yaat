# Post-physics ownership refactor — SimulationEngine owns sim logic, server carries comms

## Context

The `#N` runway-queue feature shipped broken because its per-tick pass was wired into
`SimulationEngine.TickPostPhysics`, which **the live server never calls**. The server drives the sim via
`RoomEngine.AdvanceOneSecond` → `TickProcessor` (`sim.TickPrePhysics()` + `sim.TickPhysics()` + its OWN
`ProcessPostPhysics`), so `SimulationEngine.TickPostPhysics` is only reached by the standalone
`TickOneSecond` / replay paths (Yaat.Sim tests + replay tooling). Two parallel "post-physics"
implementations exist, and they have already drifted:

- **Confirmed latent bug (dark since 2026-05-08):** `f4611f32` ("solo-training pending request
  reminders") added `PilotProactive.TickArrivalApproachRequest` + `TickPendingRequests` to
  `SimulationEngine.TickPostPhysics` one day after the server's `ProcessPilotProactive` was last written,
  and never ported them. Grep confirms **zero** yaat-server callers. So in live solo sessions, IFR
  arrivals never proactively request an approach and pilots never follow up on unanswered requests —
  yet both work in tests/replay.

Guiding principle (user): **the less specialized logic the server carries, and the more it trusts
SimulationEngine to run, the better.** `TickPrePhysics` is the model — it computes and *returns*
`TickPrePhysicsResult`; each host dispatches its own way (events vs SignalR). `TickPostPhysics` is the
anti-pattern: it couples compute to dispatch, so the server can't reuse it and re-implements the sim half.

## Phase 1 — unify the post-physics pilot-proactive orchestration (fixes the bug, removes the smell instance) — DONE

Implemented: `SimulationEngine.TickPilotProactive()` is the single owner; `TickPostPhysics` calls it, and
`TickProcessor.ProcessPostPhysics` calls `r.ActiveSim!.TickPilotProactive()` (its `ProcessPilotProactive` +
orphaned `LookupAirportPosition` deleted). Guarded by `PilotProactiveServerParityTests` (real `RoomEngine`
tick; mutation-verified). The two previously-dark solo behaviors now run live.

Original plan:

The duplicated sim orchestration is the pilot-proactive block at the top of
`SimulationEngine.TickPostPhysics` (`src/Yaat.Sim/Simulation/SimulationEngine.cs`) vs
`TickProcessor.ProcessPilotProactive` (`../yaat-server/src/Yaat.Server/Simulation/TickProcessor.cs`).
Only the compute (`PilotProactive.Tick*`) lives in Yaat.Sim; the *decision of which ticks to run each
second* is duplicated and has drifted.

1. Extract that block into one public method — `SimulationEngine.TickPilotProactive()` — running the
   full set: `TickAirborneCheckIn`, `TickArrivalApproachRequest`, `TickAirspaceBoundaryRespect`,
   `TickPendingRequests` (solo-gated) + `TickReportTriggers` (always).
2. `TickPostPhysics` calls `TickPilotProactive()` at the top (standalone behavior unchanged).
3. `TickProcessor.ProcessPostPhysics` replaces its `Post.PilotProactive` step with a call to
   `room.ActiveSim.TickPilotProactive()` and **deletes `ProcessPilotProactive`**.

Net: one place decides the post-physics pilot behaviors; both hosts run the identical set. The live
server gains the two missing behaviors (the bug fix) as a direct consequence.

**Regression guard:** the `RunwayDepartureQueueE2ETests` already drives the server sub-step sequence.
Add a focused Yaat.Server test (via `RoomEngineTestHarness`, `useRealNavData`) that loads a solo
scenario with an IFR arrival and asserts a proactive approach request fires after N `Tick()`s — proving
the behavior runs on the real `RoomEngine.AdvanceOneSecond` path.

## Phase 2 — migrate remaining server-carried sim orchestration (follow-up, larger)

Aligned with the principle but out of scope for the immediate fix; each is a separate change with its own
tests. Categorized from the current `ProcessPostPhysics` step list:

| Server step | Nature | Action |
|---|---|---|
| `ClearExpiredIdents` | pure sim timing (transponder ident expiry), no Yaat.Sim call | **DONE** — logic moved to `AircraftTransponder.TickIdent` (owns the `IdentDurationSeconds=18` const) + `SimulationEngine.TickTransponderIdents`; `TickPostPhysics` and the server's `ProcessPostPhysics` both call it. Server's private `ClearExpiredIdents` deleted. Guarded by `TransponderIdentServerParityTests` (mutation-verified). |
| `ProcessConflictAlerts` / `EramConflictAlerts` / `AsdexAlerts` | sim detect (`ConflictAlertDetector`) + server broadcast; needs STARS/ARTCC config | move the per-tick orchestration into the engine, server consumes results to broadcast |
| `ProcessVisualDetection` | sim logic looped in the server | **DONE** — moved to `SimulationEngine.TickVisualDetection` (reads `World.Weather`, the sim's active-weather field, in place of the server's synced `room.Weather` mirror — behavior-preserving); `TickPostPhysics` and the server's `ProcessPostPhysics` both call it. Server's private `ProcessVisualDetection` deleted. Guarded by `VisualDetectionServerParityTests` (mutation-verified); existing CVA E2E tests exercise the engine path via `TickOneSecond`. |
| `ProcessSoloTrainingEvaluation` | `ActiveSim.SoloTrainingEvaluator.Evaluate` + broadcast | move orchestration in, server broadcasts notices |
| `ProcessAutoDelete` | sweep is `SimulationEngine.SweepPendingAutoDeletes`; mode/broadcast is server | keep the delete broadcast server-side; engine owns the sweep decision |
| `Broadcast*`, `ApproachScores`, autotrack, coordination timers, tower lists, strips, TDLS, surface coast | genuinely comms / CRC / display | **stay server-side** |

The end state: `ProcessPostPhysics` shrinks to "call `sim` post-physics, then broadcast the results +
run CRC/strip/TDLS/ownership concerns."

## Verification

- `dotnet build -p:TreatWarningsAsErrors=true`; targeted tests; `pwsh tools/test-all.ps1` (cross-repo).
- Phase 1 must show the new Yaat.Server harness test failing before the server wiring and passing after.
- Manual: run a solo scenario with an IFR arrival on the live server; confirm the proactive approach
  request now fires (it does not today).
