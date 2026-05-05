# Tick Loop Reference

> Read this before changing anything that runs every sim-second: `SimulationEngine`, `RoomEngine` (yaat-server), `FlightPhysics`, `PhaseRunner`, `TickProcessor`, conflict detectors, or broadcast services. The order of operations matters.

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
│   └─ broadcast aircraft-spawned events
├─ Physics ×4 (0.25 s sub-ticks)       (SimulationEngine.TickPhysics)
│   └─ SimulationWorld.Tick(0.25, PreTick)
│       ├─ PreTick → PhaseRunner.Tick   (per aircraft)
│       ├─ GroundConflictDetector.ApplySpeedLimits
│       └─ FlightPhysics.Update         (per aircraft, 8 steps)
└─ PostPhysics                         (SimulationEngine.TickPostPhysics)
    ├─ ConflictAlertDetector.Detect    (airborne)
    ├─ PilotObservationUpdater         (already ran inside FlightPhysics)
    ├─ drain warnings / notifications / pilot readbacks → terminal
    └─ DetectChanges + BroadcastTrainingUpdates
```

The 4 sub-ticks give physics 0.25-second resolution while keeping all broadcasts on a 1-second cadence. `PhysicsSubTickRate = 4` is the constant.

## `FlightPhysics.Update` — 8 steps in order

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
- **Airborne** — `ConflictAlertDetector.Detect` runs in `TickProcessor.ProcessPostPhysics`. Predicts position 5 s ahead; reports pairs where current or predicted separation crosses thresholds (3 nm / 1000 ft IFR; 0.25 nm / 500 ft VFR). Hysteresis on existing conflicts (must reach 3.3 nm / 1100 ft to clear). Mode-C-only; ignores aircraft on ground or with CA inhibited; suppressed during paired approaches.

## Broadcast cadence

After PostPhysics, `BroadcastTrainingUpdates` runs **once per sim-second**:

1. `DetectChanges` walks each aircraft and computes a delta.
2. Changed aircraft → `AircraftSnapshot` DTOs go out via SignalR (JSON to YAAT clients).
3. `CrcBroadcastService.BroadcastUpdates` evaluates each subscribed CRC topic and emits MessagePack updates/deletes. See [crc-display-state.md](crc-display-state.md).
4. Drained warnings / notifications / pilot readbacks become terminal entries.

CRC visibility transitions (entering STARS coverage, ASDEX airport entry/exit, coast phase) are evaluated by `CrcVisibilityTracker` inside the broadcast pass — not in physics.

## Recording capture

Recording is **event-driven, not snapshot-per-tick**. `RecordingManager.Record(action)` appends `RecordedAction` entries (commands, setting changes, spawns) at their `ElapsedSeconds`. Snapshots (`StateSnapshotDto`) are captured on demand — for rewind checkpoints and periodic insurance against drift — not every second. See [snapshots-and-replay.md](snapshots-and-replay.md).

## Client-side: animation only

The client receives `AircraftSnapshot` DTOs each sim-second. It does not run physics. `TickAnimator` (UI layer) interpolates position/heading between consecutive snapshots so movement looks smooth at the display refresh rate. Animation is non-authoritative; if a snapshot disagrees, the snapshot wins.

## Pitfalls

- **Order matters for new tick work.** Want to inject something? PrePhysics if it must affect this tick's physics; PostPhysics if it consumes physics output (alerts, broadcasts). Inside the physics sub-tick: pre-physics callback for things that must run before the 8 steps; otherwise add a step in `FlightPhysics.Update` and document where in the order.
- **`ManagesSpeed` and `ActiveApproach` skip auto speed schedule.** Pattern phases own speed; approach owns speed via the procedure. Don't add a feature that "fights" them — gate it on those flags.
- **Sub-tick rate is 4.** If you write a feature that fires "once per tick," be explicit: per **sub-tick** (4× per second) or per **sim-second** (after PostPhysics)? Conflict detection is post-physics → per second. Phase tick is pre-physics → 4× per second. Get this wrong and rates drift by 4×.
- **No client tick.** Don't try to run physics on the client to "smooth out" anything. Smoothing is `TickAnimator`'s job and the server is authoritative.
- **Broadcasts are deltas.** Adding a field to a DTO doesn't make it broadcast — `DetectChanges` / `ChangeTracker` must consider it, otherwise the field round-trips on initial subscribe but never updates.
- **Pause halts everything for that room.** Don't put background work in the tick path expecting it to keep ticking when paused.
