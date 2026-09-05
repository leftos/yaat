# Tick Loop Reference

> Read this before changing anything that runs every sim-second: `SimulationEngine`, `RoomEngine` (yaat-server), `FlightPhysics`, `PhaseRunner`, `TickProcessor`, conflict detectors, or broadcast services. The order of operations matters.

## The engine's partial files

`SimulationEngine` is split across partial files by cluster, following the `MainViewModel` precedent in [client-mainviewmodel.md](client-mainviewmodel.md). The split is purely organizational — they compile into one class, so a private field declared in `SimulationEngine.cs` is visible from `SimulationEngine.Tick.cs`. Use this to find the right file; nothing about the split changes behaviour or the public surface.

| File | Owns |
|---|---|
| `SimulationEngine.cs` | Engine state (`World`, `Scenario`, the evaluators and pools), the lifecycle events (`TickCompleted`, `WarningEmitted`, `PilotSpeechEmitted`, `StripDispatchRequested`, `TerminalEntryEmitted`) and their `Fire*` helpers, the constructor, `FindAircraft`, `DeleteAircraft`, and the terminal-entry sink every other partial writes to. |
| `SimulationEngine.Snapshots.cs` | `CaptureSnapshot` / `RestoreFromSnapshot` and the server's slice (`CaptureServerSnapshot` / `RestoreServerSnapshot`). |
| `SimulationEngine.Scenario.cs` | `LoadScenario` and `ResolveGroundLayout`. |
| `SimulationEngine.Spine.cs` | **The segment entry points and the runner** — `RunSecond`, `BeginSecond`, `OpenSecond`, `RunPrePhysics`, `RunPhysicsSubTick`, `RunPostPhysics`, `RunEndOfSecond`; iterates the lists in `Simulation/Spine/SpineOrder.cs`, records every step into `StepTrace`, and times each into `TickTimings` when a sink is attached. |
| `SimulationEngine.Tick.cs` | **The engine's own step bodies** — `TickPrePhysics`, `TickPhysics`, the detectors (`TickVisualDetection`, `TickConflictAlerts`, `TickEramConflictAlerts`, `TickTransponders`, `TickAutoDelete`, `TickSoloTrainingEvaluation`), `TickPilotProactive`, `TickControllerAi`, and the bare-host wrappers `TickOneSecond` / `TickPostPhysics`. The order they run in is `SpineOrder`'s, not this file's. |
| `SimulationEngine.Replay.cs` | Replay entry points — `ReplayFromStartTo`, `FastForwardTo`, `ReplayRange`, `Replay`, `ReplayOneSecond`, `ReplayOneSubTick`. These are thin delegators: the stepping logic and the recorded-action cursors live in `Simulation/Replay/ReplayDriver.cs`, which runs the spine under a `ReplayHost`. Also `RunProfile` and `EnterReplay()`: the engine's run profile (`Simulation/RunProfile.cs` — kind + the three allowances `RecordsActions`, `RunsGenerators`, `RunsControllerAi`) stays on the engine because the recorders, the generators and `TickControllerAi` read it to decide what they may do; each driver stepping call runs under an `EnterReplay()` scope and hands the previous profile back. Steps read the allowances, never the kind (ADR 0005). `TickTimings` (opt-in, null in production) lives here too. |
| `SimulationEngine.Commands.cs` | `SendCommand`, `DispatchAiCommand`, `DispatchLiveCommand`, `ApplyPostDispatch`, and the aircraft mutations commands drive (`WarpAircraft`, `AmendFlightPlan`, `RequestNewBeaconCode`). |
| `SimulationEngine.DeferredCommands.cs` | Commands held until a trigger fires: `ProcessDeferredDispatches` and the triggered track blocks. |
| `SimulationEngine.Generators.cs` | Arrival, VFR and overflight generators — spawning, in-trail spacing, weight and engine selection. |
| `SimulationEngine.Presets.cs` | Scenario-authored timing: the release queue, timers, timed presets, triggers, global commands. |
| `SimulationEngine.LiveTraffic.cs` | Shadow aircraft from the live feed — samples, beacon tracking, runway-use latching. |
| `SimulationEngine.Recording.cs` | `RecordAction` and applying recorded actions back onto the world. |
| `SimulationEngine.ReplayCommands.cs` | `ReplayCommand` and the setting / generator / weather appliers it routes to. |

## Where the tick comes from

The **server** drives the simulation. `RoomTickLoopService` runs a `PeriodicTimer` at 1 Hz wall-clock; for each non-paused room it fires one tick per second. The client does **not** run physics — it receives broadcast snapshots and animates between them ([tick-animator.md](tick-animator.md)).

When a room is paused (`scenario.IsPaused == true`), the host service skips it entirely. `ElapsedSeconds` does not advance, no physics runs, no broadcasts.

## The spine — one sim-second, one definition

Every run kind advances a sim-second by iterating **the spine**: the ordered lists in `src/Yaat.Sim/Simulation/Spine/SpineOrder.cs` (ADR 0001). There is no other list. The live room, the bare test engine, the replay driver and recording reconstruction all call `SimulationEngine.RunSecond(host)` (or, for the sub-tick replay step, its segment entry points), and what differs between them is the **host** each supplies — see below.

A sim-second has five **segments** (CONTEXT.md; *phase* is reserved for an aircraft's flight phase):

```
BeginSecond    ElapsedSeconds += 1                                   (fixed code; the sub-tick replay step keeps its own clock)
OpenSecond     trace reset; host.ApplyPreTickRecordedActions(t)     (recorded spawns / live-traffic samples land before physics)
PrePhysics     SpineOrder.PrePhysics
               ├─ sim TickPrePhysics → host.OnPrePhysics             delayed spawns, generators, triggers, presets, release queue, timers
               ├─ sim DrainTerminalEntries → host.OnTerminalEntries
               ├─ host.DelayedHandoffs
               └─ host.LiveTrafficSync                                last, so a sample placed at this second records at this second
Physics ×4     sim TickPhysics(0.25)                                 (fixed code, traced with its sub-tick index)
               └─ SimulationWorld.Tick(0.25, PreTick)
                   ├─ GroundConflictDetector.ApplySpeedLimits
                   ├─ [IsShadow] LiveTrafficKinematics.Advance + airborne latch, then continue (no PreTick, no physics — live-traffic.md)
                   ├─ PreTick → PhaseRunner.Tick   (per aircraft)
                   └─ FlightPhysics.Update         (per aircraft, 8 steps)
PostPhysics    SpineOrder.PostPhysics — the live server's 32-step order
               ├─ sim TickLiveTrafficRunwayUse, TickTransponders
               ├─ host AutoAccept, PointoutAutoAck, FlightPlanCreatorAutoTrack, DeferredAutoTrack, CoordinationTimers, TowerLists
               ├─ sim TickVisualDetection, TickConflictAlerts → host, TickEramConflictAlerts → host
               ├─ host AsdexAlerts
               ├─ sim TickSoloTrainingEvaluation → host                empty outside solo mode
               ├─ sim TickPilotProactive                              after the detectors, before the drains
               ├─ sim drains → host: warnings, notifications, pilot speech, readbacks, transmissions, approach scores
               ├─ host AutoArrivalStrips, AutoApproachDepartureStrips, AutoTdlsQueue, TdlsAutoWilco, TdlsExpiry, TdlsTrackRemoval
               ├─ sim drain strip dispatches → host                   immediately before the only step that removes aircraft
               ├─ sim TickAutoDelete → host                          removes on every run kind; the host tears down room state and broadcasts
               ├─ host SurfaceCoastExpiry
               └─ host RundownBroadcast, LiveTrafficStatusBroadcast, TimersBroadcast
EndOfSecond    SpineOrder.EndOfSecond
               ├─ sim SamplePositionHistory                          every 5 s, 10 deep — the history trails
               ├─ sim AdvanceWeatherTimeline → host                  ungated; the live host mirrors Room.Weather
               ├─ host IssueMetars, ApplyRecordedActions
               ├─ sim TickControllerAi                                gated by RunProfile.RunsControllerAi
               └─ TickCompleted event
```

`sim` is an engine body (`SimulationEngine.Tick*` and the world drains); `host.X` is a **host step**, a body the host supplies; `→ host.OnX` is a **consumer**, where a sim step hands its result over. The 4 sub-ticks give physics 0.25-second resolution while keeping all broadcasts on a 1-second cadence; `SimulationEngine.PhysicsSubTickRate = 4` is the one constant.

### Hosts

`ISimulationHost` (`Simulation/Spine/`) is two views: `IHostSteps` — the bodies — and `IHostConsumers` — the results. A sim step is handed only the consumer view, so it can deliver but never invoke a slot, and no view says which run kind this is; that is `RunProfile`'s (ADR 0005). Neither interface has default implementations: **adding a member fails the build in every host** until each has answered, which is how "a step added to one path and not the other" became unrepresentable.

| Host | Where | Slots | Consumers |
|---|---|---|---|
| `BareHost` | `Yaat.Sim` — `TickOneSecond`, the `RunKind.Test` run | all empty | the engine's events (`WarningEmitted`, `TerminalEntryEmitted`, `PilotSpeechEmitted`, `StripDispatchRequested`); auto-deleted aircraft and solo findings are discarded (the removal and the evaluator's record already happened in the sim) |
| `ReplayHost` | `Yaat.Sim` — the replay driver | pre-tick recorded actions, recorded actions after the second; the rest as bare | as bare |
| `LiveRoomHost` | yaat-server — `RoomEngine.AdvanceLiveSecond` (the hosted loop, the harness, the headless soak room) | every `TickProcessor` body; playback actions when in tape playback; METAR issuance | the broadcasts; `Room.Weather` follows the world's profile |
| `ReconstructionHost` | yaat-server — `RecordingManager.ReconstructViaServerTick` | the same `TickProcessor` bodies; the recorded log around the second; no METAR | the broadcasts (suppressed) |

**While a host step mutates snapshot state, the host still decides whether a simulation-affecting step runs** — the residue ADR 0001 forbids. `IHostSteps`'s header lists those members; ADR 0003 (tick step 4) moves each body into the engine, deleting the member and turning its `SpineStep.Host` entry into a `SpineStep.Sim` entry without touching the order. Until then the tick oracle records what the empty slots cost as accepted divergences.

### The step trace

`SimulationEngine.StepTrace` records every step of the current second — `(StepId, subTick)` in order, an FNV-1a 64 digest over the second and that sequence, per-second and total counts. It is on by default and allocation-free once warm. It exists because the snapshot oracle cannot see ordering: every host iterates the same lists, so what the trace catches is a host that skips a segment, a second opened against the wrong time, a wrong sub-tick count, or the sub-tick replay split drifting from the whole-second path. What it cannot see is a slot body that does nothing — that stays the oracle's job.

- `SpineTraceTests` (Yaat.Sim.Tests) pins the literal step sequence of one bare second — the only pin on `TickPilotProactive` sitting after the detectors — and proves four `ReplayOneSubTick`s compose the same second as one `ReplayOneSecond` (equal digest *and* equal snapshot).
- The oracle sweep (below) asserts every leg's digest, counts and second equal live's **every second, hard fail, never baselined**.
- `PostPhysicsDrainOrderTests` still pins the drain order behaviourally, through the buffers themselves.

### What a new per-second step looks like

Add a `StepId`, add the entry to the right `SpineOrder` list at its position, and — if the body is the engine's — write it as a `SimulationEngine.Tick*` method (`TickPrePhysics` is the model: compute, update engine-owned state, hand the host the result). If the body is still the server's, add the member to `IHostSteps` and let the compiler walk you through the hosts. Never add a second list anywhere: the pilot-proactive request reminders once ran dark on the live server for months because the engine and the server each kept their own.

- **ASDE-X alert processing stays server-side on purpose.** `AsdexSafetyLogicDetector.Detect` already lives in Yaat.Sim and is called once; the rest is CRC glue over server-only DTOs and the room-held alert set, so it is not a second brain — it is a host step until ADR 0003 moves the alert set.

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
- **Airborne** — `ConflictAlertDetector.Detect` runs in the `ConflictAlerts` spine step (`SimulationEngine.TickConflictAlerts`). Predicts position 5 s ahead; reports pairs where current or predicted separation crosses thresholds (3 nm / 1000 ft IFR; 0.25 nm / 500 ft VFR). Hysteresis on existing conflicts (must reach 3.3 nm / 1100 ft to clear). Mode-C-only; ignores aircraft on ground or with CA inhibited; suppressed during paired approaches; `CASUP` pairs and shadow↔shadow pairs never alert.

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

The per-feature parity tests above each guard one step. The **oracle** guards the whole captured state: it loads one scenario and seed into a headless room per run kind, advances them in lockstep, and diffs `CaptureSnapshot` every sim-second against the live room's. A step added to one path and not the other shows up whether or not anyone wrote a test for it. See ADR [0004](adr/0004-the-oracle-and-the-corpus.md).

- **The legs.** `live` is `RoomEngine.AdvanceLiveSecond()` — the spine under the `LiveRoomHost`. `test` is `SimulationEngine.TickOneSecond()` — the spine under the `BareHost`. `replay` is `SimulationEngine.ReplayOneSecond()` — the spine under the `ReplayHost`, which differs from bare only in the three recorded-action / weather steps and runs under the `Replay` profile; it earns its own leg because a regression in the run-profile allowances can surface on no other pair. `reconstruct` is `RecordingManager.ReconstructViaServerTick()` — the spine under the `ReconstructionHost`, which fills every room-owned slot the live host fills and re-applies the recorded log around each second. Every room is loaded through the server's own `LoadScenarioSeededAsync`, so the scenario load is not a variable and only the host differs; the replay room is armed afterwards with `SimulationEngine.ArmReplay(actions)`, using an action log a first live pass collected (it must be complete before arming — the replay leg applies it second by second). Alongside the snapshot diff, every leg's `StepTrace` digest, counts and second are asserted equal to live's each second — a hard failure, never baselined.
- **Findings so far.** *Without weather, physics is identical on all four run kinds* — zero position/altitude/speed/heading divergence over 600 s. The entire live-vs-test and live-vs-replay gap there (13 paths each, the same 13) is ATC/track state: the live-only auto-accept, autotrack and delayed-handoff passes, plus position-history sampling. It was 14 until the `InternalAirports` argument asymmetry was retired by resolving the list inside the sim (tick step 3c-0). *Reconstruction reproduces live exactly except position history* — one path, because it runs the server's own post-physics list; everything the other two legs disagree about, it gets right, which is the shape ADR 0003 predicts. *With weather, physics diverges on every leg* — wind, true track, heading, position, then altitude and speed downstream. That is the cascade ADR 0001 predicts from three advance semantics over one timeline, and it is the only thing in the oracle that moves an aircraft.

- **Comparator**: `src/Yaat.Sim/Simulation/Oracle/` — `SnapshotTreeDiff` walks two `JsonNode` trees serialized with `RecordingJsonOptions.Default` and emits one divergence per differing leaf, at a path that is literally the JSON pointer (`Aircraft[SWA123].Track.Owner.SectorId`). `DivergenceAccumulator` folds the per-second stream by *normalized* path (collection keys collapsed to `[*]`), which is what keeps the summary bounded when one divergence cascades.
- **Driver**: `tests/Yaat.Server.Tests/Oracle/TickOracleTests.cs` in yaat-server — only that repo can drive the live path.
- **The fixtures.** The sweep runs every pair over each `OracleFixture`, because a divergence set is a statement about the code *and* about what the input reaches. `s2oak4` is the long real-traffic run that reaches the ATC/track divergences. `weather` is the same scenario under a deliberately gentle wind ramp — 40° and 2 kt over two minutes, so each second's change stays under `HasMeaningfulChange`'s 1°/0.5 kt threshold, which is what separates all three weather semantics at once instead of only exposing the frozen one. `autodelete` is two parked aircraft, one carrying a preset `DEL`. It has to use the queued-delete branch: `ScenarioLoader` sets `AutoDeleteExempt` on every scenario-declared aircraft, so the `Parked`/`OnLanding`/departed-overflight gates are unreachable without generated traffic. The fixture asserts an aircraft actually disappeared, because one that silently stops reaching its gate produces a spotless baseline that reads as agreement.
- **Accepted divergences**: one baseline file per fixture and pair under `yaat-server/docs/tick-oracle/`, named `<fixture>.<pair>.baseline.json`, one entry per normalized path with the step it is attributed to. The pair itself is an `OraclePair` in the driver — key, the two side labels, and the long comparison string — so adding a leg is that record plus the loop driving its right-hand side; the csproj globs `*.baseline.json`, so adding a file does not mean editing it. The path set is asserted exactly, so both a new divergence and a silently-fixed one fail, and every leg is evaluated before the test fails so one red pair never hides another. Regenerate deliberately with `YAAT_ORACLE_REBASELINE=1` and review the diff before committing; a regeneration that finds nothing new rewrites every file byte-identically, so anything `git status` shows is a real change.
- **The two failure directions are not symmetric, and the message says so.** A *new* path is self-evidently something to investigate. A *vanished* one reads like good news and usually is not: during behaviour-preserving work the likeliest cause is that a path lost the step that produced the divergence, so `Describe` renders that branch as a regression to be attributed and deliberately **withholds** the re-baseline command there — offering it would hand you a one-liner that banks the lost step as the new expected state. Retiring an entry on purpose (tick step 5) still works; it just has to be something you can name first. Pinned by `TickOracleBaselineTests`.
- **Permanent exemptions**: `OracleExemptions` — for what no amount of unification would fix (a value that is not simulation state). Empty by design; if it grows, something is being accepted there that belongs in the baseline.

**What it still cannot see, worth knowing before trusting a green run.** It sees exactly what `CaptureSnapshot` covers, so strips, TDLS, ASDE-X alerts, tower lists and the approach evaluator — room-held, no snapshot DTO — are invisible until that state moves into `Yaat.Sim`. Coordination timers and solo-training evaluation are unreached by every fixture. And **the replay leg is weaker than it looks**: no fixture declares a runtime generator, so no action log carries a `RecordedAircraftSpawn` (asserted in the sweep, not assumed) and the generator stand-down in replay never has anything to stand down (it is pinned by `ReplayGeneratorStandDownTests` in Yaat.Sim.Tests instead), while recording suppression is invisible in principle because the action log is not part of `ScenarioSnapshotDto` — it is not simulation state. The test's own doc comment carries the current blind-spot list.

## Client-side: animation only

The client receives `AircraftSnapshot` DTOs each sim-second. It does not run physics. `TickAnimator` (UI layer) interpolates position/heading between consecutive snapshots so movement looks smooth at the display refresh rate. Animation is non-authoritative; if a snapshot disagrees, the snapshot wins.

## Pitfalls

- **Order matters for new tick work.** Want to inject something? PrePhysics if it must affect this second's physics; PostPhysics if it consumes physics output (alerts, broadcasts); EndOfSecond for bookkeeping over the completed second. Put it in `SpineOrder` at its position (§ What a new per-second step looks like) — never in a host or a caller. Inside the physics sub-tick: pre-physics callback for things that must run before the 8 steps; otherwise add a step in `FlightPhysics.Update` and document where in the order.
- **`ManagesSpeed` and `ActiveApproach` skip auto speed schedule.** Pattern phases own speed; approach owns speed via the procedure. Don't add a feature that "fights" them — gate it on those flags.
- **Sub-tick rate is 4.** If you write a feature that fires "once per tick," be explicit: per **sub-tick** (4× per second) or per **sim-second** (after PostPhysics)? Conflict detection is post-physics → per second. Phase tick is pre-physics → 4× per second. Get this wrong and rates drift by 4×.
- **No client tick.** Don't try to run physics on the client to "smooth out" anything. Smoothing is `TickAnimator`'s job and the server is authoritative.
- **Broadcasts are deltas.** Adding a field to a DTO doesn't make it broadcast — `DetectChanges` / `ChangeTracker` must consider it, otherwise the field round-trips on initial subscribe but never updates.
- **Removing an aircraft must clear its tracker entry.** `DetectChanges` returns `DtoChangeFlags.All` only for a callsign it has never seen. Any path that removes an aircraft has to call `room.ChangeTracker.Remove(callsign)` (and `room.AircraftAssignments.Remove(callsign)`) alongside the delete broadcast — otherwise a same-callsign recurrence is diffed against the *dead* aircraft and every category that still matches is silently dropped from its initial CRC push. CRC has no out-of-band spawn message to paper over this; the training hub does (`AircraftSpawned`), which is why the client looks fine while CRC shows no track.
- **Pause halts everything for that room.** Don't put background work in the tick path expecting it to keep ticking when paused.
