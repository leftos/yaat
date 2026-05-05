# Phase System Reference

> Read this before touching anything in `src/Yaat.Sim/Phases/`. Phases are the clearance-gated state machine that owns aircraft control between commands.

## Mental model

A **phase** is a self-contained behavior that owns an aircraft's `ControlTargets` for some span of time (taxi to runway, climb to 1500 AGL, fly downwind, etc.). At any moment an aircraft is either:

- **In a phase** (`aircraft.Phases?.CurrentPhase != null`) — the phase writes `TargetHeading`/`TargetAltitude`/`TargetSpeed`/`NavigationRoute` directly each tick. The CommandQueue is **bypassed**.
- **Free-flying** (`aircraft.Phases is null`) — `ControlTargets` reflects the last command issued; CommandQueue triggers fire when conditions are met.

`PhaseList` (the queue) is more than just a list — it also stores **persistent metadata** that crosses phase transitions: `AssignedRunway`, `TaxiRoute`, `LandingClearance`, `ActiveApproach`, `DepartureClearance`, `TrafficDirection` (pattern mode), `LahsoHoldShort`, `RequestedExit`.

## Base contract — `Phases/Phase.cs`

Every phase implements:

| Member | Type | Purpose |
|---|---|---|
| `Name` | `string` (abstract) | Human label (e.g. `"Upwind"`) |
| `OnStart(ctx)` | `void` | Runs once when phase becomes current; set initial targets |
| `OnTick(ctx)` | `bool` | Per-tick update; **return `true` when complete** |
| `OnEnd(ctx, status)` | `void` (virtual) | Cleanup on completion or skip |
| `CanAcceptCommand(cmd)` | `CommandAcceptance` (virtual) | What happens when a command arrives mid-phase |
| `ManagesSpeed` | `bool` (virtual, default `false`) | If `true`, FlightPhysics auto speed schedule is **suppressed** — the phase must drive `TargetSpeed` itself. Pattern phases override to `true`. |
| `ToSnapshot()` | `PhaseDto` (abstract) | Snapshot serialization |

### `CommandAcceptance` (`Phases/CommandAcceptance.cs`)

Three branches govern what happens when a command lands on an in-phase aircraft:

- **`Allowed`** — phase processes it; phase stays active. Use for commands that the phase intentionally handles (e.g. heading change while taxiing).
- **`Rejected(reason)`** — command denied; reason is user-visible. Use for "you can't do that right now" gates (e.g. takeoff clearance during runway crossing).
- **`ClearsPhase`** — phase ends and the command is then routed through normal dispatch. **This is the default**. CommandDispatcher does a dry-run validation on a clone *before* clearing; if validation fails, the phase is preserved.

## Lifecycle — `Phases/PhaseRunner.cs`

`PhaseRunner.Tick(phases, ctx)` runs **before** `FlightPhysics.Update` each tick (see [tick-loop.md](tick-loop.md)). On each call:

1. If current phase is `Pending`, `phases.Start()` flips it to `Active` and calls `OnStart`.
2. `current.ElapsedSeconds += DeltaSeconds`.
3. `OnTick(ctx)` runs. If it returns `true`, `phases.AdvanceToNext()` marks current `Completed`, calls `OnEnd`, and starts the next phase.
4. After advancement, auto-append rules may extend the queue (see below).

### Auto-append rules

These run inside `PhaseRunner` after the current phase advances. They turn one-shot phases into the chains controllers expect:

- **Post-LAHSO**: When `LandingPhase.StoppedForLahso` is set, append `RunwayHoldingPhase → RunwayExitPhase → HoldingAfterExitPhase`.
- **Post-landing (no pattern)**: If queue is complete and `TrafficDirection is null`, auto-append `RunwayExitPhase → HoldingAfterExitPhase`.
- **Pattern re-entry from go-around**: After `GoAroundPhase` with `ReenterPattern=true`, set `TrafficDirection = Left` (default) when a runway is assigned.
- **Pattern auto-cycle**: If queue is complete AND `TrafficDirection is not null` AND `AssignedRunway is not null`, `PatternBuilder` builds the next circuit and appends the leg phases. Landing clearance is cleared.

Full-stop approaches **do not** auto-cycle. The pattern bit (`TrafficDirection`) is what distinguishes a touch-and-go aircraft from a full-stop arrival.

## `PhaseContext` — `Phases/PhaseContext.cs`

What every `OnTick` receives. Beyond `Aircraft` and `Targets`, notable fields:

- `DeltaSeconds` — usually `0.25s` (4 sub-ticks per sim-second).
- `Weather`, `Runway`, `FieldElevation`, `GroundLayout`, `Category`, `AircraftType`.
- `AircraftLookup` — delegate for finding other aircraft (used by `FollowingPhase`, ground conflict avoidance).
- `TowerPosition` — the local tower `TrackOwner`. `InitialClimbPhase` uses this to hold RV SID heading until handoff + 5s.
- `IsHoldShortNodeOccupied` / `OccupiedHoldShortNodes` / `MarkHoldShortNodeOccupied` — ground anti-collision plumbing.
- `ScenarioElapsedSeconds`, `AutoClearedToLand`, `SoloTrainingMode`, `RpoShowPilotSpeech` — scenario flags.

Phases write **directly** to `ctx.Targets` — they do not enqueue commands.

## Persistent state — `Phases/PhaseList.cs`

`PhaseList` carries metadata that outlives any single phase:

| Field | Set by | Consumed by |
|---|---|---|
| `AssignedRunway` | TAXI, RWY, departure clearance | `LineUpPhase`, `TakeoffPhase`, `FinalApproachPhase` |
| `TaxiRoute` | TAXI handler | `TaxiingPhase` |
| `DepartureClearance` | LUAW / CTO | `LinedUpAndWaitingPhase`, threshold logic |
| `LandingClearance` (`ClearanceType?`) | CLAND, CTOC, LUAW-cancel | `FinalApproachPhase`, `LandingPhase` |
| `ClearedRunwayId` | landing clearance | `FinalApproachPhase` |
| `TrafficDirection` (`PatternDirection?`) | pattern commands | auto-cycle in `PhaseRunner` |
| `PatternRunway` | cross-runway closed traffic | pattern phases |
| `ActiveApproach` (`ApproachClearance?`) | JFAC / CAPP / JAPP / PTAC | `ApproachNavigationPhase`, `FinalApproachPhase` |
| `LahsoHoldShort` | LAHSO clearance | `LandingPhase` (for braking target) |
| `RequestedExit` (`ExitPreference?`) | EL / ER / EXIT during approach | `RunwayExitPhase` |

## Command interaction — `CommandDispatcher.cs`

When a command lands on an in-phase aircraft:

1. `DispatchWithPhase` is called. It calls `CurrentPhase.CanAcceptCommand(cmdType)`.
2. **`Rejected`** → return error to user. State unchanged.
3. **`ClearsPhase`** → run a **dry-run validation** on a clone (`DryRunValidate`). If valid, set `shouldClearPhases = true`. After the rest of the compound validates, `aircraft.Phases?.Clear()` runs and the command is dispatched normally. If dry-run fails, the phase is preserved and the user sees the validation error.
4. **`Allowed`** → tower-command handler processes the command; remaining blocks queue normally.

`Phases.Clear()` marks the active phase `Skipped`, all pending phases `Skipped`, calls each `OnEnd(ctx, Skipped)`, sets `aircraft.Phases = null`, clears `TurnRateOverride`, and emits a phase-cancellation summary via `PhaseClearSummary`.

## Phase catalog

Files are under `Phases/Tower/`, `Phases/Ground/`, `Phases/Pattern/`, `Phases/Approach/`. Quick map:

**Tower** — `LineUpPhase`, `LinedUpAndWaitingPhase`, `TakeoffPhase`, `InitialClimbPhase`, `FinalApproachPhase`, `LandingPhase`, `RunwayHoldingPhase` (LAHSO), `GoAroundPhase`, `LowApproachPhase`, `TouchAndGoPhase`, `StopAndGoPhase`, `HelicopterTakeoffPhase`, `HelicopterLandingPhase`, `VfrHoldPhase`, `MakeTurnPhase`, `STurnPhase`.

**Ground** — `TaxiingPhase`, `HoldingShortPhase`, `CrossingRunwayPhase`, `RunwayExitPhase`, `PushbackPhase`, `PushbackToSpotPhase`, `AirTaxiPhase`, `AtParkingPhase`, `FollowingPhase`, `HoldingInPositionPhase`, `HoldingAfterPushbackPhase`, `HoldingAfterExitPhase`.

**Pattern** (all set `ManagesSpeed = true`) — `PatternEntryPhase`, `UpwindPhase`, `CrosswindPhase`, `DownwindPhase`, `BasePhase`, `MidfieldCrossingPhase`, `TeardropReentryPhase`, `VfrFollowPhase`.

**Approach** — `ApproachNavigationPhase`, `InterceptCoursePhase`, `ProcedureTurnPhase`, `HoldingPatternPhase`.

## Snapshotting — `Simulation/Snapshots/PhaseSnapshotDto.cs`

`PhaseDto` is an abstract polymorphic base. Every concrete phase has a sibling DTO and a `[JsonDerivedType(typeof(XyzPhaseDto), "Xyz")]` registration on `PhaseDto`. Restore lives in `PhaseList.RestorePhase()` — a switch on DTO type that calls each phase's static `FromSnapshot(dto)` factory.

Adding a new phase that needs to round-trip through snapshots requires **all four** of:

1. `NewPhase : Phase` with `OnStart`/`OnTick`/`Name`/`ToSnapshot`.
2. `NewPhaseDto : PhaseDto` next to the other DTOs.
3. `[JsonDerivedType(typeof(NewPhaseDto), "NewPhase")]` on `PhaseDto`.
4. A case in `PhaseList.RestorePhase()` calling `NewPhase.FromSnapshot(dto)`.

Skip any of these and old recordings will throw `InvalidOperationException` on restore (or new ones will silently lose state).

`Phase.SnapshotRequirements()` / `RestoreRequirements()` handle the `Requirements` (clearance state machine) list — only serialized when non-empty.

## Pitfalls

- **Two GoAround construction sites.** `GoAroundPhase` is built in both `GoAroundHelper.Trigger` (auto go-around at minimums) and `PatternCommandHandler.TryGoAround` (manual `GA`). New fields on `GoAroundPhase` must be set in **both** — extract a shared builder if you find yourself touching this.
- **`ManagesSpeed` is contagious.** If you set it on a non-pattern phase and don't drive `TargetSpeed`, the aircraft will not auto-decelerate or hold a speed restriction. Only set `ManagesSpeed = true` if you take responsibility for speed.
- **`Rejected` vs `ClearsPhase` semantics.** `Rejected` is for "valid command, but not now" (temporary gate). `ClearsPhase` is for "this command supersedes the phase's intent" — the default. If you find yourself returning `Rejected` for a command that *should* cancel the phase, you've got it backwards.
- **LAHSO insertion is post-hoc.** Hold/exit phases are appended *after* `LandingPhase` completes, not before. `LandingPhase` sets `StoppedForLahso = true`; `PhaseRunner` detects it next tick and appends.
- **`CurrentPhase != null` bypasses CommandQueue entirely.** Don't try to "queue a command behind a phase" — there is no such mechanism. Either the phase accepts it (`Allowed`), rejects it (`Rejected`), or yields (`ClearsPhase`).
