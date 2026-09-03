# Tick Loop Reference

> Read this before changing anything that runs every sim-second: `SimulationEngine`, `RoomEngine` (yaat-server), `FlightPhysics`, `PhaseRunner`, `TickProcessor`, conflict detectors, or broadcast services. The order of operations matters.

## The engine's partial files

`SimulationEngine` is split across partial files by cluster, following the `MainViewModel` precedent in [client-mainviewmodel.md](client-mainviewmodel.md). The split is purely organizational — they compile into one class, so a private field declared in `SimulationEngine.cs` is visible from `SimulationEngine.Tick.cs`. Use this to find the right file; nothing about the split changes behaviour or the public surface.

| File | Owns |
|---|---|
| `SimulationEngine.cs` | Engine state (`World`, `Scenario`, the evaluators and pools), the lifecycle events (`TickCompleted`, `WarningEmitted`, `PilotSpeechEmitted`, `StripDispatchRequested`, `TerminalEntryEmitted`) and their `Fire*` helpers, the constructor, `FindAircraft`, `DeleteAircraft`, and the terminal-entry sink every other partial writes to. |
| `SimulationEngine.Snapshots.cs` | `CaptureSnapshot` / `RestoreFromSnapshot` and the server's slice (`CaptureServerSnapshot` / `RestoreServerSnapshot`). |
| `SimulationEngine.Scenario.cs` | `LoadScenario` and `ResolveGroundLayout`. |
| `SimulationEngine.Tick.cs` | **The per-tick path** — `TickPrePhysics`, `TickPhysics`, `TickPostPhysics`, the detectors (`TickVisualDetection`, `TickConflictAlerts`, `TickEramConflictAlerts`, `TickTransponders`, `TickAutoDelete`, `TickSoloTrainingEvaluation`), `TickControllerAi`, and the whole-second entry points `TickOneSecond` / `TickOnce`. This is the file the ordering in this doc describes. |
| `SimulationEngine.Replay.cs` | Replay drivers — `ReplayFromStartTo`, `FastForwardTo`, `ReplayRange`, `Replay`, `ReplayOneSecond`, `ReplayOneSubTick`. These are thin delegators: the stepping logic and the recorded-action cursors live in `Simulation/Replay/ReplayDriver.cs`. The two replay *mode* flags (`IsReplayingRecordedActions`, `ReplayHasRecordedAircraftSpawns`) stay on the engine, because the recorders, `TickControllerAi` and the generators read them to decide what they may do — they describe the engine's mode, not the driver's bookkeeping. |
| `SimulationEngine.Commands.cs` | `SendCommand`, `DispatchAiCommand`, `DispatchLiveCommand`, `ApplyPostDispatch`, and the aircraft mutations commands drive (`WarpAircraft`, `AmendFlightPlan`, `RequestNewBeaconCode`). |
| `SimulationEngine.DeferredCommands.cs` | Commands held until a trigger fires: `ProcessDeferredDispatches` and the triggered track blocks. |
| `SimulationEngine.Generators.cs` | Arrival, VFR and overflight generators — spawning, in-trail spacing, weight and engine selection. |
| `SimulationEngine.Presets.cs` | Scenario-authored timing: the release queue, timers, timed presets, triggers, global commands. |
| `SimulationEngine.LiveTraffic.cs` | Shadow aircraft from the live feed — samples, beacon tracking, runway-use latching. |
| `SimulationEngine.Recording.cs` | `RecordAction` and applying recorded actions back onto the world. |
| `SimulationEngine.ReplayCommands.cs` | `ReplayCommand` and the setting / generator / weather appliers it routes to. |

## Where the tick comes from

The **server** drives the simulation. `SimulationHostedService` runs a `PeriodicTimer` at 1 Hz wall-clock; for each non-paused room it fires one tick per second. The client does **not** run physics — it receives broadcast snapshots and animates between them ([tick-animator.md](tick-animator.md)).

When a room is paused (`scenario.IsPaused == true`), the host service skips it entirely. `ElapsedSeconds` does not advance, no physics runs, no broadcasts.

## Per-second structure

One sim-second is split into **PrePhysics → Physics ×4 → PostPhysics**:

```
Tick(1s)
├─ PrePhysics                          (SimulationEngine.TickPrePhysics)
│   ├─ delayed spawns / generators
│   ├─ scenario triggers / presets
│   ├─ broadcast aircraft-spawned events
│   └─ live-traffic shadow sync (server only: ShadowTrafficSync, last so samples record at this second)
├─ Physics ×4 (0.25 s sub-ticks)       (SimulationEngine.TickPhysics)
│   └─ SimulationWorld.Tick(0.25, PreTick)
│       ├─ GroundConflictDetector.ApplySpeedLimits
│       ├─ [IsShadow] LiveTrafficKinematics.Advance + airborne latch, then continue (no PreTick, no physics — live-traffic.md)
│       ├─ PreTick → PhaseRunner.Tick   (per aircraft)
│       └─ FlightPhysics.Update         (per aircraft, 8 steps)
└─ PostPhysics                         (SimulationEngine.TickPostPhysics)
    ├─ TickLiveTrafficRunwayUse        (shadow landing observer — also in the server's ProcessPostPhysics)
    ├─ ConflictAlertDetector.Detect    (airborne)
    ├─ PilotObservationUpdater         (already ran inside FlightPhysics)
    ├─ drain warnings / notifications / pilot readbacks → terminal
    └─ DetectChanges + BroadcastTrainingUpdates
```

The 4 sub-ticks give physics 0.25-second resolution while keeping all broadcasts on a 1-second cadence. `PhysicsSubTickRate = 4` is the constant.

### The server runs its own PostPhysics

`SimulationEngine.TickPostPhysics` is reached only by the standalone `TickOneSecond` and the replay drivers (`ReplayOneSecond`,
`ReplayRangeCore`, `ReplayOneSubTick`) — Yaat.Sim tests and replay tooling. The live server never calls it: `RoomEngine.AdvanceOneSecond` →
`TickProcessor` drives `TickPrePhysics()` + `TickPhysics()` and then runs its own ordered `ProcessPostPhysics` list (transponders, visual
detection, pilot-proactive, conflict / solo-evaluation / auto-delete broadcasts, strips, TDLS, …). A per-tick step added only to
`TickPostPhysics` runs in tests and replay and does nothing live; the pilot-proactive request reminders once ran dark on the server for months
this way.

- **Per-tick sim logic lives in the engine as a public `SimulationEngine.Tick*` method the server calls** — never as private orchestration in
  `TickProcessor`, and never only inside `TickPostPhysics`. `TickPrePhysics` is the model: it returns a `TickPrePhysicsResult` and each host
  dispatches it its own way. Two seam shapes exist:
  - *void, both hosts call it* — `TickTransponders`, `TickVisualDetection`, `TickPilotProactive`: invoked by `TickPostPhysics` **and**
    `ProcessPostPhysics`.
  - *return-value, the broadcasting host consumes it* — `TickSoloTrainingEvaluation`, `TickAutoDelete`, `TickConflictAlerts`,
    `TickEramConflictAlerts`: the engine computes, updates engine-owned state, and returns the diff; the server fans it out. `TickPostPhysics`
    still calls the two conflict methods (with no internal airports) and discards the diff so a restored conflict set is re-examined in replay
    rather than pinned; it never calls solo evaluation or auto-delete (no controller to notify).
- **A Yaat.Sim test that drives `TickOneSecond` cannot catch a server-path gap.** Guard live behavior with a yaat-server harness test
  (`RoomEngineTestHarness`, whose `Tick()` is the real `RoomEngine.AdvanceOneSecond`) — `PilotProactiveServerParityTests` is the pattern, and
  no-op'ing the engine method must turn the parity test red.
- **ASDE-X alert processing stays server-side on purpose.** `AsdexSafetyLogicDetector.Detect` already lives in Yaat.Sim and is called once;
  the rest is CRC glue over server-only DTOs and the room-held alert set, so it is not a second brain.

## `FlightPhysics.Update` — 8 steps in order

> This section gives the step ORDER. For the integration math inside each step, the airspeed-frame model (IAS/TAS/GS/Mach), and the validated per-category performance constants, see [flight-physics.md](flight-physics.md).

For each aircraft, `FlightPhysics.Update(ac, deltaSeconds, …)` runs:

1. **`UpdateNavigation`** — sequence to next waypoint; on arrival, fire `NotifyFixSequenced` (which feeds AT-fix triggers); compute course-to-target.
2. **`UpdateDescentPlanning`** — look ahead at altitude restrictions on the route; precompute descent rate to make them.
3. **`UpdateClimbPlanning`** — same for upcoming climb-to constraints.
4. **`UpdateSpeedPlanning`** — proactive speed look-ahead for procedure speed restrictions. Mirrors descent/climb planning.
5. **`UpdateHeading`** — turn toward target; bank angle from `atan(TAS × turnRate × coeff)`; snap at ±0.5°.
6. **`UpdateAltitude`** — climb/descend; expedite multiplies rate by 1.5×; snap at ±10 ft.
7. **`UpdateSpeed`** — accelerate/decelerate. Auto schedule **skipped** when `ActiveApproach` is set or current phase has `ManagesSpeed=true`. 14 CFR 91.117 caps 250 KIAS below 10,000 ft. Mach hold recomputes equivalent IAS each tick.
8. **`UpdatePosition`** — TAS = `IasToTas(IAS, alt)`; ground track and groundspeed from TAS plus wind vector; lat/lon advances by groundspeed × delta.
9. **`UpdateCommandQueue`** — evaluate `CommandBlock` triggers (LV altitude, AT fix, intercept, give way, on handoff…). When met, fire the closure; advance the queue if `ReadyToAdvance`.
10. **`PilotObservationUpdater.Update`** — re-check pending visual acquisitions (RTIS/RFIS soft-fail watch state). On success, emit pilot readback. **Runs after `UpdateCommandQueue`** so observations see post-queue state.

The numbering is 1–10 even though the documented summary says "8-step" — the post-position queue and observation steps are sometimes counted with the queue step.

## Phases run **before** physics

Inside `SimulationWorld.Tick(delta, preTick, …)`, the `preTick` callback constructs a `PhaseContext` and calls `PhaseRunner.Tick(phases, ctx)`. So per sub-tick:

1. `PhaseRunner.Tick` — phases write to `ControlTargets`.
2. `GroundConflictDetector.ApplySpeedLimits` — caps target speed for proximity.
3. `FlightPhysics.Update` — physics consumes the freshly-written targets.

This is why phases write directly to `ctx.Targets` and never enqueue commands: they own targets up until physics reads them.

## Conflict detection

Two detectors. Both are server-only (clients see results in broadcast snapshots).

- **Ground** — `GroundConflictDetector.ApplySpeedLimits` runs each sub-tick *before* physics. Pairwise check, classifies movement state (taxiing / pushing / stationary / following), applies speed caps when proximity drops below thresholds.
- **Airborne** — `ConflictAlertDetector.Detect` runs in `TickProcessor.ProcessPostPhysics`. Predicts position 5 s ahead; reports pairs where current or predicted separation crosses thresholds (3 nm / 1000 ft IFR; 0.25 nm / 500 ft VFR). Hysteresis on existing conflicts (must reach 3.3 nm / 1100 ft to clear). Mode-C-only; ignores aircraft on ground or with CA inhibited; suppressed during paired approaches; `CASUP` pairs and shadow↔shadow pairs never alert.

## Broadcast cadence

After PostPhysics, `BroadcastTrainingUpdates` runs **once per sim-second**:

1. `DetectChanges` walks each aircraft and computes a delta.
2. Changed aircraft → `AircraftSnapshot` DTOs go out via SignalR (JSON to YAAT clients).
3. `CrcBroadcastService.BroadcastUpdates` evaluates each subscribed CRC topic and emits MessagePack updates/deletes. See [crc-display-state.md](crc-display-state.md).
4. Drained warnings / notifications / pilot readbacks become terminal entries.

CRC visibility transitions (entering STARS coverage, ASDEX airport entry/exit, coast phase) are evaluated by `CrcVisibilityTracker` inside the broadcast pass — not in physics.

## Recording capture

Recording is **event-driven, not snapshot-per-tick**. `RecordingManager.Record(action)` appends `RecordedAction` entries (commands, setting changes, spawns) at their `ElapsedSeconds`. Snapshots (`StateSnapshotDto`) are captured on demand — for rewind checkpoints and periodic insurance against drift — not every second. See [snapshots-and-replay.md](snapshots-and-replay.md).

## The tick oracle — what guards the two paths against each other

The per-feature parity tests above each guard one step. The **oracle** guards the whole captured state: it loads one scenario and seed into two headless rooms, drives one with `RoomEngine.AdvanceLiveSecond()` and the other with `SimulationEngine.TickOneSecond()`, and diffs `CaptureSnapshot` every sim-second. A step added to one path and not the other shows up whether or not anyone wrote a test for it. See ADR [0004](adr/0004-the-oracle-and-the-corpus.md).

- **Comparator**: `src/Yaat.Sim/Simulation/Oracle/` — `SnapshotTreeDiff` walks two `JsonNode` trees serialized with `RecordingJsonOptions.Default` and emits one divergence per differing leaf, at a path that is literally the JSON pointer (`Aircraft[SWA123].Track.Owner.SectorId`). `DivergenceAccumulator` folds the per-second stream by *normalized* path (collection keys collapsed to `[*]`), which is what keeps the summary bounded when one divergence cascades.
- **Driver**: `tests/Yaat.Server.Tests/Oracle/TickOracleTests.cs` in yaat-server — only that repo can drive the live path.
- **Accepted divergences**: `yaat-server/docs/tick-oracle/live-vs-test.baseline.json`, one entry per normalized path with the step it is attributed to. The path set is asserted exactly, so both a new divergence and a silently-fixed one fail. Regenerate deliberately with `YAAT_ORACLE_REBASELINE=1` and review the diff before committing.
- **Permanent exemptions**: `OracleExemptions` — for what no amount of unification would fix (a value that is not simulation state). Empty by design; if it grows, something is being accepted there that belongs in the baseline.

**Two things it cannot see, both worth knowing before trusting a green run.** It sees exactly what `CaptureSnapshot` covers, so strips, TDLS, ASDE-X alerts, tower lists and the approach evaluator — room-held, no snapshot DTO — are invisible until that state moves into `Yaat.Sim`. And it sees only what its scenario exercises: the current fixture defines no weather and parks nothing within the window, so the weather-advance cascade and the missing `TickAutoDelete` do not appear in the baseline even though both are real. The test's own doc comment carries the current blind-spot list.

## Client-side: animation only

The client receives `AircraftSnapshot` DTOs each sim-second. It does not run physics. `TickAnimator` (UI layer) interpolates position/heading between consecutive snapshots so movement looks smooth at the display refresh rate. Animation is non-authoritative; if a snapshot disagrees, the snapshot wins.

## Pitfalls

- **Order matters for new tick work.** Want to inject something? PrePhysics if it must affect this tick's physics; PostPhysics if it consumes physics output (alerts, broadcasts). Inside the physics sub-tick: pre-physics callback for things that must run before the 8 steps; otherwise add a step in `FlightPhysics.Update` and document where in the order.
- **`ManagesSpeed` and `ActiveApproach` skip auto speed schedule.** Pattern phases own speed; approach owns speed via the procedure. Don't add a feature that "fights" them — gate it on those flags.
- **Sub-tick rate is 4.** If you write a feature that fires "once per tick," be explicit: per **sub-tick** (4× per second) or per **sim-second** (after PostPhysics)? Conflict detection is post-physics → per second. Phase tick is pre-physics → 4× per second. Get this wrong and rates drift by 4×.
- **No client tick.** Don't try to run physics on the client to "smooth out" anything. Smoothing is `TickAnimator`'s job and the server is authoritative.
- **Broadcasts are deltas.** Adding a field to a DTO doesn't make it broadcast — `DetectChanges` / `ChangeTracker` must consider it, otherwise the field round-trips on initial subscribe but never updates.
- **Removing an aircraft must clear its tracker entry.** `DetectChanges` returns `DtoChangeFlags.All` only for a callsign it has never seen. Any path that removes an aircraft has to call `room.ChangeTracker.Remove(callsign)` (and `room.AircraftAssignments.Remove(callsign)`) alongside the delete broadcast — otherwise a same-callsign recurrence is diffed against the *dead* aircraft and every category that still matches is silently dropped from its initial CRC push. CRC has no out-of-band spawn message to paper over this; the training hub does (`AircraftSpawned`), which is why the client looks fine while CRC shows no track.
- **Pause halts everything for that room.** Don't put background work in the tick path expecting it to keep ticking when paused.
