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

### Additive speed/altitude family — single source of truth

A phase whose responsibility is **lateral** guidance (turns, S-turns, holds, procedure turns, approach intercepts, pattern legs, departure-procedure legs) must treat **speed** and **altitude** instructions as *additive*: they retarget the corresponding control axis without cancelling the maneuver. Speed and heading are independent axes — issuing `RFAS` during an `R360` should slow the aircraft, not stop the turn.

Don't hand-roll the per-command list in each `CanAcceptCommand` (that drift is exactly how `RFAS`/`RNS`/`DSR` ended up cancelling turns while `SPD`/`Mach` didn't). Use the shared predicates on `Phase`:

- `Phase.IsSpeedFamilyCommand(cmd)` — `Speed`, `Mach`, `ReduceToFinalApproachSpeed`, `ResumeNormalSpeed`, `DeleteSpeedRestrictions`.
- `Phase.IsAltitudeFamilyCommand(cmd)` — `ClimbMaintain`, `DescendMaintain`.
- `Phase.IsAdditiveAirborneAdjustment(cmd)` — either of the above.

The canonical shape for a lateral phase is a leading guard, then a switch for the phase's own special cases:

```csharp
public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
{
    if (IsAdditiveAirborneAdjustment(cmd))
    {
        return CommandAcceptance.Allowed;
    }
    return cmd switch { /* phase-specific Allowed cases */ _ => CommandAcceptance.ClearsPhase };
}
```

Phases that auto-resume a captured speed (e.g. `MakeTurnPhase`, `STurnPhase`, `VfrHoldPhase` via `ManeuverSpeedController`) must also cancel that auto-resume in `OnCommandAccepted` for the **whole** speed family (`IsSpeedFamilyCommand`), or a controller-issued `RFAS`/`RNS`/`DSR` would be clobbered when the phase ends.

Two intentional exceptions accept the speed family but **not** the full additive family:
- `FinalApproachPhase` **rejects** the speed family inside `SpeedCommandFinalGateNm` (pilot "unable", 7110.65 §5-7-1.b.4) — the aircraft is committed to its stabilized final approach speed, so a speed instruction is declined, not honored. It must **reject**, never `ClearsPhase`: tearing the approach down for a speed command once wiped an established ILS final (SWA4587 on OAK ILS 30, killed by a stray `RFAS`). Outside the gate the speed family is additive. `SPEEDF` (the explicit override) is always allowed.
- `PatternEntryPhase` guards on `IsSpeedFamilyCommand` only; a `CM`/`DM` during the entry **clears** the phase and warns the RPO, because a climb/descend mid-entry usually means the aircraft is no longer being sequenced into this pattern. The in-pattern legs (`DownwindPhase`, etc.) take `CM`/`DM` additively.

`PhaseAcceptanceAuditTests` pins this contract across the whole phase set.

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
- **Pattern re-entry from go-around**: After `GoAroundPhase` with `ReenterPattern=true`, set `TrafficDirection` from the persistent `AircraftPattern.TrafficDirection` (falling back to the transient field, then Left) when a runway is assigned.
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

`Phases.Clear()` marks the active phase `Skipped`, all pending phases `Skipped`, calls each `OnEnd(ctx, Skipped)`, sets `aircraft.Phases = null`, clears `TurnRateOverride` and `PreferredTurnDirection` (so a torn-down turn phase can't bias the next lateral target — e.g. `L360` then `DCT`), and emits a phase-cancellation summary via `PhaseClearSummary`. Turn phases (`MakeTurnPhase`, `InitialClimbPhase`) also null `PreferredTurnDirection` in their own `OnEnd`, covering natural completion as well as force-clear.

## Phase catalog

Files are under `Phases/Tower/`, `Phases/Ground/`, `Phases/Pattern/`, `Phases/Approach/`. Quick map:

**Tower** — `LineUpPhase`, `LinedUpAndWaitingPhase`, `TakeoffPhase`, `InitialClimbPhase`, `DepartureProcedurePhase` (charted heading/course SID legs — see below), `FinalApproachPhase`, `LandingPhase`, `RunwayHoldingPhase` (LAHSO), `GoAroundPhase`, `LowApproachPhase`, `TouchAndGoPhase`, `StopAndGoPhase`, `HelicopterTakeoffPhase`, `HelicopterLandingPhase`, `VfrHoldPhase`, `MakeTurnPhase`, `STurnPhase`.

**Ground** — `TaxiingPhase`, `HoldingShortPhase`, `CrossingRunwayPhase`, `RunwayExitPhase`, `PushbackPhase`, `PushbackToSpotPhase`, `AirTaxiPhase`, `AtParkingPhase`, `FollowingPhase`, `HoldingInPositionPhase`, `HoldingAfterPushbackPhase`, `HoldingAfterExitPhase`.

**Pattern** (all set `ManagesSpeed = true`) — `PatternEntryPhase`, `UpwindPhase`, `CrosswindPhase`, `DownwindPhase`, `BasePhase`, `MidfieldCrossingPhase`, `TeardropReentryPhase`, `VfrFollowPhase`.

**Approach** — `ApproachNavigationPhase`, `InterceptCoursePhase`, `ProcedureTurnPhase`, `HoldingPatternPhase`.

### Procedure turns and transition selection

- **`ProcedureTurnPhase`** executes a published procedure turn from a CIFP PI leg (AIM 5-4-9 course reversal). CAPP/JAPP auto-engage it when the procedure has a PI leg, the transition is not NoPT, and either the DCT fix matches the PT anchor or the intercept angle exceeds 90°. The PI leg's `OutboundCourse` is the published 45°-offset PT heading (magnetic, **not** the radial out of the fix); `TurnDirection` is the direction of the 180° turn back to inbound. Implied PTAC (`CAPP` on a vector) additionally requires an empty `NavigationRoute`.
- **`ApproachCommandHandler.SelectBestTransition`** competes each transition's first fix against every common-leg `IAF`/`IF` in its position-based fallback and returns `null` when a common-leg fix is nearest (the aircraft flies the published feeder from the start). PT engagement gates `interceptTooSteep` on `transition is not null` to avoid a false-positive PT when no transition is selected and the published feeder already delivers FAC alignment.

### Charted procedure legs — `DepartureProcedurePhase`

Aircraft fly charted SID legs coded as **headings/courses**, not just fix-to-fix great circles (driving case: LINDZ ONE out of KASE — climb 343° to 9100, climbing left turn to 273° to intercept, track the 303° back-course to LINDZ). `DepartureProcedurePhase` (`Phases/Tower/`) flies the ARINC-424 terminators VA (heading→alt), CA (course→alt, WCA-corrected), VI/CI (heading/course→intercept the next leg's course), VM (heading→manual), a course-tracked CF, and CD/VD/FD/FC (course/heading→DME or along-track distance) + CR/VR (→radial). `InitialClimbPhase` holds runway heading to the 400 ft AGL TERPS gate, then `InsertAfterCurrent`s the procedure phase (gated on `_proceduralDeparture`). Legs come from `ProcedureLegResolver.Resolve` (which keeps the fix-less legs `DepartureClearanceHandler.ResolveLegsToTargets` drops); `ExtractActiveDepartureLegs` returns only the **leading run** of coded legs, stopping at the first plain fix — interior fix legs keep FlightPhysics' turn anticipation via `NavigationRoute`. It reuses approach-intercept geometry (`GeoMath.SignedCrossTrackDistanceNm`, turn-radius lead for VI, proportional cross-track steering for CF).

- **SID climb windows** — a leg's `AtOrBelow`/`Between` crossing altitude (`Altitude1Ft`) is enforced as a **level-off cap** (`ApplyLegAltitudeCap`, min against `_climbCeiling`), released when the leg sequences — it never *lowers* the overall ceiling, so an assigned / top-altitude cap survives (AIM §5-2-9.e.5: level off only if reached before the fix). DME / along-track distance is the ARINC `dist_time` field → `LegDistanceNm` (not `Rho`); radial is `Theta`.
- **STARs** handle only the **FM terminator**: `NavigationTarget.TerminalCourseMagnetic` carries the FM outbound course (set by `ResolveLegsToTargets`, including the dedup-collapse case where a TF-to-fix is followed by an FM-from-the-same-fix), which `FlightPhysics.UpdateNavigation`'s empty-route handler flies instead of holding. There is **no `ArrivalProcedurePhase`** — the typed-leg path is departure-only (sole caller `DepartureClearanceHandler`); approaches/STARs otherwise use the flat `ResolveLegsToTargets`.
- **Scope decisions (don't re-litigate):** CF course-tracking is deliberately kept out of core `UpdateNavigation` (only `DepartureProcedurePhase` + the STAR FM terminal use it) to avoid core-nav blast radius. An "Approach Phase 3" for fix-less approach legs was intentionally skipped — an instrument `GoAroundPhase` already climbs runway heading to the missed-approach altitude before missed-approach nav, so the "climb heading to altitude" legs `BuildMissedApproachFixes` drops are redundant. Aviation-reviewed: VA = heading (no WCA), CF/CA = track (WCA); a climb-window altitude is a turn *trigger*, not a level-off; do **not** apply the ≤30° approach capture gate to a departure VI→CF intercept.

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

- **Two GoAround construction sites.** `GoAroundPhase` is built in both `GoAroundHelper.Trigger` (auto go-around at minimums) and `PatternCommandHandler.TryGoAround` (manual `GA`). New fields on `GoAroundPhase` must be set in **both** — extract a shared builder if you find yourself touching this. The two used to disagree about whether a VFR aircraft re-enters the pattern, which stranded a `GA`-then-`MRT` aircraft in a 2000 ft AGL climb (issue #283); both now call `GoAroundHelper.ResolvePatternIntent`.
- **Three ways a go-around picks its climb-out altitude.** `GoAroundHelper.ResolveClimbOutAltitude`: published missed-approach altitude when MAP phases were built; else pattern altitude − 300 ft when re-entering the pattern (AIM 4-3-2 — the same gate `UpwindPhase` uses to release the crosswind turn, so the turn is available the instant the phase hands off); else `null`, meaning `GoAroundPhase` self-clears at 2000 ft AGL. Pattern altitude comes from `PatternGeometry.ResolveAuthoredOverrides`, not the bare `CategoryPerformance.PatternAltitudeAgl` constant — the airport's authored per-runway TPA and a commanded `MRT 15` override both have to win, or the go-around levels off above the circuit it is joining.
- **`ManagesSpeed` is contagious.** If you set it on a non-pattern phase and don't drive `TargetSpeed`, the aircraft will not auto-decelerate or hold a speed restriction. Only set `ManagesSpeed = true` if you take responsibility for speed.
- **The 5nm-final gate leaves a `SpeedCeiling` on non-phase-managed inbounds.** `FlightPhysics.AutoCancelSpeedAtFinal` releases an explicit ATC speed at 5 nm but, for a hand-vectored inbound with no speed-owning phase, retains the last-assigned speed as a `SpeedCeiling` so the auto schedule can't re-accelerate it (7110.65 §5-7-1.b.4). Any phase that transitions an inbound aircraft into a **climb-out** (`GoAroundPhase`, `TouchAndGoPhase`, `StopAndGoPhase`, `LowApproachPhase`) must clear `SpeedFloor`/`SpeedCeiling` in `OnStart`, or the missed-approach / option climb is capped at the approach speed. Add a new climb-out phase → clear the ceiling there too. `GoAroundPhase` also clears `AltitudeFloor`/`AltitudeCeiling` (set by `AOA`/`AOB`): its completion check reads the aircraft's own altitude, so a stale restriction would level the climb somewhere the phase never notices.
- **Tight maneuvers slow to holding speed via `ManeuverSpeedController`.** `MakeTurnPhase`, `VfrHoldPhase`, and `STurnPhase` set `ManagesSpeed = true` and delegate speed to a `ManeuverSpeedController` (`Capture` in `OnStart`, `Reduce` to decelerate to `AircraftPerformance.HoldingSpeed`, `Resume` in `OnEnd`, `CancelAutoResume` from `OnCommandAccepted` on a mid-maneuver speed command). `Reduce` never speeds an aircraft up (only acts when current > holding speed). `STurnPhase` additionally gates `Reduce` behind a 5 nm / FAF check (7110.65 §5-7-1.b.4 — no speed adjustment inside the final approach fix), so S-turns slow only when issued outside that boundary and never on short final. The controller's three fields must be round-tripped in each phase's snapshot DTO.
- **`Rejected` vs `ClearsPhase` semantics.** `Rejected` is for "valid command, but not now" (temporary gate). `ClearsPhase` is for "this command supersedes the phase's intent" — the default. If you find yourself returning `Rejected` for a command that *should* cancel the phase, you've got it backwards.
- **LAHSO insertion is post-hoc.** Hold/exit phases are appended *after* `LandingPhase` completes, not before. `LandingPhase` sets `StoppedForLahso = true`; `PhaseRunner` detects it next tick and appends.
- **`CurrentPhase != null` bypasses CommandQueue entirely.** Don't try to "queue a command behind a phase" — there is no such mechanism. Either the phase accepts it (`Allowed`), rejects it (`Rejected`), or yields (`ClearsPhase`).
- **`InitialClimbPhase` must hold while `_rvSidActive`.** The phase must NOT complete and `ApplyDepartureTurn` must NOT load the nav route while `_rvSidActive` is true — only `UpdateRvSidHeadingHold` releases the phase after comms handoff. A bare `CTO` (no assigned altitude) reproduces the early-completion bug; tests asserting before the deferred-turn gate fires do not catch it.
- **Physics never reads `AssignedAltitude` — a cleared climb phase must re-arm `TargetAltitude`.** `FlightPhysics.UpdateAltitude` moves only toward `ControlTargets.TargetAltitude`; `AssignedAltitude` is persistent clearance/UI state that physics ignores. So a phase-clearing lateral command (`FH` or any `ClearsPhase` verb) mid-departure can strand the aircraft at `TakeoffPhase`'s ~400 ft AGL handoff target while `AssignedAltitude` is ignored (bug N85439). `CommandDispatcher.ResumeAssignedAltitudeAfterPhaseClear(aircraft, clearedGoAround)` — called from **both** phase-clear sites (immediate `~:211`, triggered `~:2487` — keep in sync) — re-arms `TargetAltitude = AssignedAltitude` only when the phase was actively climbing (`phaseTarget > current + PhaseClearClimbMarginFt`) **and** `phaseTarget <= assigned` (never lowers a climb target) **and** `assigned > current`. It skips on-ground aircraft, and skips a `clearedGoAround` clear outright (a go-around owns its published missed-approach target; `AssignedAltitude` is the stale approach clearance). A lateral vector is horizontal-only and never cancels an altitude clearance (7110.65 §4-5-7.h.1), but the climb-only guard is mandatory — an aircraft vectored off a *descent* below its last assigned altitude must hold, not climb back. **Any state a phase writes into `ControlTargets` that physics depends on must be reconciled on phase-clear** — physics won't fall back to the `Assigned*` fields. Tests: `N85439ClimbStallAfterFhTests`, `ResumeAssignedAltitudeAfterPhaseClearTests`.
- **Re-inserting a phase re-runs its `OnStart`.** `PhaseList.AdvanceToNext`/`Start`/`SkipTo` always call `OnStart` when a phase becomes current — the only no-`OnStart` path is snapshot restore (`FromSnapshot` sets `CurrentIndex` directly). So `InsertAfterCurrent([maneuver, resume])` re-fires the resume phase's `OnStart` side effects; guard one-shot effects (e.g. `FinalApproachPhase.CloneForResume` sets `_goAroundRolled` so an in-final 360 / S-turn doesn't re-roll the solo go-around RNG — see [approach-and-pattern-geometry.md](approach-and-pattern-geometry.md) footguns).
