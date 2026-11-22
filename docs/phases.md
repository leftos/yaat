# Phase System Reference

> Read this before touching anything in `src/Yaat.Sim/Phases/`. Phases are the clearance-gated state machine that owns aircraft control between commands.

## Mental model

A **phase** is a self-contained behavior that owns an aircraft's `ControlTargets` for some span of time (taxi to runway, climb to 1500 AGL, fly downwind, etc.). At any moment an aircraft is either:

- **In a phase** (`aircraft.Phases?.CurrentPhase != null`) — the phase writes `TargetHeading`/`TargetAltitude`/`TargetSpeed`/`NavigationRoute` directly each tick. The CommandQueue is **bypassed**.
- **Free-flying** (`aircraft.Phases is null`) — `ControlTargets` reflects the last command issued; CommandQueue triggers fire when conditions are met.

`PhaseList` (the queue) is more than just a list — it also stores **persistent metadata** that crosses phase transitions: `AssignedRunway`, `TaxiRoute` (only the route a TAXI on the landing rollout stores), `LandingClearance`, `ActiveApproach`, `DepartureClearance`, `TrafficDirection` (pattern mode), `LahsoHoldShort`, `RequestedExit`.

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
| `IsIdleAwaitingCommands` | `bool` (virtual, default `false`) | `true` on terminal command-waiting phases whose `OnTick` never returns `true` on its own; while such a phase is current, `FlightPhysics` advances **untriggered** queued blocks (see the CommandQueue pitfall below). Never set it on a motion phase — queued blocks would fire mid-maneuver. Pinned by `GroundPhaseConvention.IdleAwaitingCommands_SetMatchesStationaryWhitelist`. |
| `ToSnapshot()` | `PhaseDto` (abstract) | Snapshot serialization |

### `CommandAcceptance` (`Phases/CommandAcceptance.cs`)

Three branches govern what happens when a command lands on an in-phase aircraft:

- **`Allowed`** — phase processes it; phase stays active. Use for commands that the phase intentionally handles (e.g. heading change while taxiing).
- **`Rejected(reason)`** — command denied; reason is user-visible. Use for "you can't do that right now" gates (e.g. takeoff clearance during runway crossing).
- **`ClearsPhase`** — phase ends and the command is then routed through normal dispatch. **This is the default**. CommandDispatcher does a dry-run validation on a clone *before* clearing; if validation fails, the phase is preserved.

### Additive speed/altitude family — single source of truth

A phase whose responsibility is **lateral** guidance (turns, S-turns, holds, procedure turns, approach intercepts, pattern legs, departure-procedure legs) must treat **speed** and **altitude** instructions as *additive*: they retarget the corresponding control axis without cancelling the maneuver. Speed and heading are independent axes — issuing `RFAS` during an `R360` should slow the aircraft, not stop the turn.

Don't hand-roll the per-command list in each `CanAcceptCommand` (that drift is exactly how `RFAS`/`RNS`/`DSR` ended up cancelling turns while `SPD`/`Mach` didn't). Use the shared predicates on `Phase`:

- `Phase.IsSpeedFamilyCommand(cmd)` — `Speed`, `Mach`, `ReduceToFinalApproachSpeed`, `ResumeNormalSpeed`, `ResumePublishedSpeed`, `DeleteSpeedRestrictions`, and the forced speeds `ForceSpeedFinal` (SPEEDF) and `ForceSpeed` (SPDN, `SPDN RFAS`): a forced speed retargets speed like any other and must not cancel a maneuver or an approach.
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
- `FinalApproachPhase` **rejects** the speed family inside `SpeedCommandFinalGateNm` (pilot "unable", 7110.65 §5-7-1.b.4) — the aircraft is committed to its stabilized final approach speed, so a speed instruction is declined, not honored.

  It must **reject**, never `ClearsPhase`: tearing the approach down for a speed command once wiped an established ILS final (SWA4587 on OAK ILS 30, killed by a stray `RFAS`). Outside the gate the speed family is additive. `SPEEDF` and `SPDN` (the explicit overrides) are always allowed, as is `FOLLOW`.
- `PatternEntryPhase` guards on `IsSpeedFamilyCommand` only; a `CM`/`DM` during the entry **clears** the phase and warns the RPO, because a climb/descend mid-entry usually means the aircraft is no longer being sequenced into this pattern. The in-pattern legs (`DownwindPhase`, etc.) take `CM`/`DM` additively.

An aircraft cleared for an instrument approach is the one place the altitude family is decided above the phase table: see the next section.

`PhaseAcceptanceAuditTests` pins this contract across the whole phase set.

### Commands on an aircraft cleared for an approach

The five instrument-approach legs are `InterceptCoursePhase`, `ApproachNavigationPhase` (not the missed approach), `ProcedureTurnPhase`, a hold-in-lieu `HoldingPatternPhase` and `FinalApproachPhase` (`ApproachAltitudeHold.IsApproachPhase`).

With `PhaseList.ActiveApproach` set, a command on one of them is kept, held, cancelling or refused as follows.

- **Kept (approach in place):** the whole speed family including `SPDN` (plain `SPD` is still refused inside the 5 nm gate on final); `CLAND`, `CLANDF`, `LAHSO`, `COPT`, `TG`, `SG`, `LA`, `CLC` and `EL`/`ER`/`EXIT` (`Phase.IsLandingClearanceOrExitCommand`, accepted by every leg including the procedure turn and the hold-in-lieu); `FOLLOW`; a bare `WAIT`; and `CIFR`, which is phase-transparent (`CommandDescriber.IsPhaseTransparent`) and only flips the flight-plan flag.
- **Cancelled:** a heading, direct-to or hold command, and a go-around with a heading, runway heading, altitude or traffic-pattern direction (`MLT`/`MRT`).
- **Altitude assignment (`CM`, `DM`, `ForceAltitude`, `EXP <alt>`):** decided by `CommandDispatcher.ApproachAltitudeVerdict`, ahead of each phase's own `CanAcceptCommand`, on the live path, for a fired queued block and for a parallel sibling alike.

  Outside the published final approach fix the approach is kept and the altitude is held "maintain until established" (AIM 5-4-7.f): `ApproachClearance.AltitudeHeldUntilEstablished` makes approach navigation, the procedure turn and the hold fly `AssignedAltitude` in place of the published altitudes, even below a published floor or the procedure turn's minimum. Inside the fix the command cancels the approach.

  A command that fails its dry run (an unable) leaves the approach, its phases and the queue untouched.
- **Release of the hold:** approach navigation releases it when it sequences its next fix, before any AT-fix trigger fires; `FinalApproachPhase` releases it at glideslope capture or on crossing the published final approach fix.

  While held, the glideslope is captured only when the aircraft is level at the held altitude (within 20 ft) and the glideslope is within 50 ft of it, so a `CM` given on the glideslope a little above the aircraft is flown and held, not recaptured, and an aircraft held above the glideslope keeps the altitude until the published FAF and then descends to the glideslope from above, and one held below captures only when the glideslope descends to it. A hold set after capture clears the capture.
- **Inside or outside the FAF** for the hold and the go-around refusal is `FinalApproachFix.IsInsidePublishedFaf`: established on the final and no further out than the runway's published FAF distance, uncapped (5 nm when the runway has none). `FinalApproachFix.IsInside` caps the distance at 5 nm and stays the speed gate's and the `FOLLOW` refusal's test.
- **Bare go-around refused:** a `GA` with no heading, runway heading, altitude or traffic-pattern direction, outside the published FAF on an instrument approach, is refused by the pilot ("unable go-around, we're in the procedure turn / in the hold / outside the final approach fix, request vectors", `PilotResponder.BuildUnableGoAroundRequestVectors`) via `CommandDispatcher.RefuseBareGoAroundOutsideFaf`, since the published missed approach does not start from there.

  A queued or conditional `GA` refuses the same way when it fires. A visual approach has no FAF and is never refused. Inside the FAF a bare `GA` flies the published missed approach.
- **Go-around climb-out altitude:** `GA <heading>` / `GA RH` with no altitude on an instrument approach climbs to the highest of the aircraft's altitude, the published missed-approach altitude and field elevation plus 2000 ft (never a descent).

  On a visual approach it climbs to the higher of its altitude and the base climb-out (`GoAroundHelper.ResolveClimbOutAltitude`, else field elevation plus 2000 ft; `PatternCommandHandler.VisualGoAroundFloorFt`), never descending, and the published missed-approach altitude does not apply.

  A `GA` with a heading, runway heading, altitude or `MLT`/`MRT` ends the approach clearance; a bare one keeps it for the missed-approach phases.
- **`CLAND, EXP 030`:** the expedite's altitude follows the altitude rule above, as `EXP 030` alone: inside the FAF it cancels the approach, outside it the altitude is held until established.

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
- **Pattern auto-cycle**: If the completed phase was a cycle terminator (`TouchAndGoPhase`, `StopAndGoPhase`, `LowApproachPhase`) or a `GoAroundPhase` with `ReenterPattern=true`, AND the queue is complete AND a direction resolves (persistent `AircraftPattern.TrafficDirection`, else the transient `TrafficDirection`) AND `AssignedRunway is not null`, `PatternBuilder` builds the next circuit and appends the leg phases.

  Landing clearance is cleared (defensively — `GoAroundHelper.InstallGoAroundPhases` already voided it at go-around installation).

Full-stop approaches **do not** auto-cycle. The pattern bit (`TrafficDirection`) is what distinguishes a touch-and-go aircraft from a full-stop arrival.

The terminator-type gate matters independently of the direction: an instrument go-around leaves `ReenterPattern=false`, and the persistent `AircraftPattern.TrafficDirection` survives `FH`/`TR`/`TL` phase clears, so without it a stale `MLT`/`MRT` would crank an IFR missed approach into a VFR circuit. `PatternCommandHandler.WillAppendNextCircuit` mirrors this gate — keep the two in sync.

While a terminator is still running — or on final before a planned touch-and-go, stop-and-go, low approach or option — and that gate says a circuit is coming, the pattern commands issued in the window queue for it: a bare `EXT` and `EXT UPWIND` set the one-shot `AircraftPattern.ExtendNextUpwind`, and `EXT CROSSWIND` / `EXT DOWNWIND` / `SA` / `MNA` set `AircraftPattern.PendingEntryModifier` (a queued `MNA` also cancels a queued extension). The slot holds one modifier: the last one given wins.

This auto-cycle consumes both: the upwind flag directly, and the modifier through `PatternCommandHandler.ApplyPendingEntryModifier` when its `ForNextCircuit` flag is true. `TryEnterPattern` applies whichever side of the partition is pending — a queued entry's own modifier (flag false), or one queued for the lap the cycle terminator would append (flag true) — since an entry builds the next circuit whichever way it is reached.

Re-issuing `MRT`/`MLT` during the climb-out splices the next circuit in at once and consumes both the next-circuit modifier (flag true) and the upwind flag onto it.

## `PhaseContext` — `Phases/PhaseContext.cs`

What every `OnTick` receives. Beyond `Aircraft` and `Targets`, notable fields:

- `DeltaSeconds` — usually `0.25s` (4 sub-ticks per sim-second).
- `Weather`, `Runway`, `FieldElevation`, `GroundLayout`, `Category`, `AircraftType`.
- `AircraftLookup` — delegate for finding other aircraft (used by `FollowingPhase`, ground conflict avoidance).
- `TowerPosition` — the local tower `TrackOwner`. `InitialClimbPhase` uses this to hold RV SID heading until handoff + 5s.
- `IsHoldShortNodeOccupied` / `OccupiedHoldShortNodes` / `MarkHoldShortNodeOccupied` — ground anti-collision plumbing.
- `ScenarioElapsedSeconds`, `AutoClearedToLand`, `AutoPullUpToParallel`, `AutoGoAroundOnOccupiedRunway`, `AutoRejectTakeoffOnOccupiedRunway`, `SoloTrainingMode`, `RpoShowPilotSpeech` — scenario flags. `AutoClearedToLand` and `AutoGoAroundOnOccupiedRunway` are derived per aircraft in `BuildPhaseContext`: the room toggle, or a silently simulated tower at the aircraft's assigned runway's airport (`SimScenarioState.IsAutoClearedToLandFor` / `IsSimulatedTowerArrival`).
- `PilotContacts` (+ `ToEligibilityContext()`) — who answers pilot calls (`PilotContactRoster`: AI-staffed positions and the solo student). The initial-contact sites resolve their addressee through it instead of gating on `SoloTrainingMode`; a hand-built context that leaves it unset derives the student-only roster from the solo fields. See [solo-training-pilot-speech.md](solo-training-pilot-speech.md#who-the-pilot-calls--pilotcontactroster-pilotpilotcontactrostercs).
- `ActiveRunways` (required) — the room's active runways, which decide the end a pilot names a runway by (`RunwayCrossingEnd.Spoken`); `ActiveRunways.Empty` when none, as the airborne and pattern handlers pass.
- `ListAircraft` — delegate returning every aircraft in the world (used by `OccupiedRunwayGoAround` from `FinalApproachPhase`; null in minimal command-time contexts).

Phases write **directly** to `ctx.Targets` — they do not enqueue commands.

## Persistent state — `Phases/PhaseList.cs`

`PhaseList` carries metadata that outlives any single phase:

| Field | Set by | Consumed by |
|---|---|---|
| `AssignedRunway` | TAXI, RWY, departure clearance | `LineUpPhase`, `TakeoffPhase`, `FinalApproachPhase` |
| `TaxiRoute` | A TAXI or TAXIAUTO accepted on the landing rollout (`GroundCommandHandler.RolloutTaxi.cs`); cleared when the exit completes and the route starts, or dropped (exit given up, another exit instructed, go-around, stopped with no exit ahead). The active route is `Ground.AssignedTaxiRoute`, not this | `RunwayExitPhase` (rolls into it; `GroundCommandHandler.TryStartStoredTaxiRoute`), the CROSS handlers, `SituationFlagCalculator`, `AircraftStatusDescriber` |
| `StoredTaxiDestinationRunway` | The same TAXI, when its clearance names a destination runway; set and cleared with `TaxiRoute` | `TryStartStoredTaxiRoute` (assigned as the departure runway in preference to the one the route's last bar detects) |
| `DepartureClearance` | LUAW / CTO; a CTO, or a LUAW with a runway assigned, while holding short of a taxiway stores it on the route of the implicit taxi onto that taxiway (`DepartureClearanceHandler.TakeoffFromTaxiwayHold`); `PhaseList.LineUpIntersectionTaxiway` remembers a LUAW's intersection until the takeoff roll (`TakeoffPhase` / `HelicopterTakeoffPhase` `OnStart`) or a non-takeoff end of `LinedUpAndWaitingPhase` clears it | `LinedUpAndWaitingPhase`, threshold logic |
| `LandingClearance` (`ClearanceType?`) | CLAND, CTOC, LUAW-cancel; voided by any go-around (`GoAroundHelper.InstallGoAroundPhases`, along with `Pattern.PendingLandingClearance`) | `FinalApproachPhase`, `LandingPhase` |
| `ClearedRunwayId` | landing clearance; voided with it at go-around | `FinalApproachPhase` |
| `TrafficDirection` (`PatternDirection?`) | pattern commands | auto-cycle in `PhaseRunner` |
| `PatternRunway` | cross-runway closed traffic | pattern phases |
| `ActiveApproach` (`ApproachClearance?`) | JFAC / CAPP / JAPP / PTAC | `ApproachNavigationPhase`, `FinalApproachPhase` |
| `LahsoHoldShort` | LAHSO clearance | `LandingPhase` (for braking target) |
| `RequestedExit` (`ExitPreference?`) | EL / ER / EXIT during approach | `RunwayExitPhase` |
| `ForceLanding` | CLANDF; cleared by a go-around, `CLC`, or `LandingPhase.OnEnd` on the ground | `FinalApproachPhase`, `LandingPhase` (forced descent and forced rollout) |
| `ForcedRollout` | `LandingPhase.OnEnd` when a forced landing completes on the ground (not under LAHSO); cleared by any successful tower/ground command on the ground and by `RunwayExitPhase.OnEnd` | `RunwayExitPhase` backstop (backtrack to the exit behind) |

### A boundary hold in front of a running phase

`PhaseList.InsertHoldInFront` puts an Orbit-mode `AirspaceBoundaryHoldPhase` at the current index, Pending, and leaves the running phase directly behind it still Active: the **held phase**. It is not ticked and gets no `OnEnd` while held. `PhaseList.HeldPhase` reads it from the list's order and statuses alone, so a snapshot round-trip keeps the pair with no extra field, and `PhaseList.EffectivePhase` is the held phase when there is one, else the current phase. `PhaseList.EffectiveIndex` is its index (one past `CurrentIndex` while a hold stands), for a reader that scans the phases queued after the one flown.

**Which reader sees which.** The rule is by kind of reader. A reader that judges the aircraft's progress, sequence or role reads `EffectivePhase` (or `EffectiveIndex`): what it is flying or established on (`PhaseList.IsEstablishedOnApproach`, `ApproachCommandHandler.IsOnFinal` and `IsInboundToLand`, the after-cycle terminator test `FlightPhysics.IsAfterCycleTerminatorMet`, the arrival-side check-in test, the EXT readback leg, `BravoCapScope`), where it stands in a sequence behind or ahead of other traffic (every follow and traffic reader in `AirborneFollowHelper`: FOLLOW refusals, pattern join versus pursuit, base spacing, leg order, whether a lead is going around), and what role it holds on a runway (`AsdexRunwayUsers` counts a held final as an Arrival, the solo evaluator's `SameRunwaySeparationTracker` treats it as an arrival approach). A follower sequences on a held lead as if it were not held.

A reader that drives the list itself (the `PhaseRunner` tick, `PhaseList.AdvanceToNext`), writes speed or phase state, exports, or must see the orbit reads `CurrentPhase`, the hold: the generator spacing authority and same-runway protection (an orbiting aircraft gets no speed adjustment, 7110.65 5-7-1.b.3), the scenario exporter (a held final exports at its real position) and the runway crossing gate's go-around clause (a held go-around away from the runway does not shut crossings). The engine's visual-reference loss pass (`SimulationEngine.RunWithHoldLifted`) runs its consequence inside a hold lift (`CommandDispatcher.BeginHoldLift`/`EndHoldLift`, the same lift a controller command gets, below), so the held base or final is ended as the current phase.

`PhaseList.AdvanceToNext`, `InsertAfterCurrent` and `ReplaceUpcoming` throw `InvalidOperationException` while a hold stands: lift it first (`LiftHold`), or end the pair through `PhaseRunner.EndHold`. Until a paired hold's end is routed to `EndHold`, the tick's `AdvanceToNext` below throws for a pair.

`PhaseRunner.EndHold` ends the hold with a `HoldResume`:

- `InPlace` moves the current index onto the held phase without calling its `OnStart` again, so it ticks on from its own state.
- `RestartWith(copy)` ends the held phase `Completed` once and starts the Pending copy in its place.

A hold that never started is removed with neither `OnStart` nor `OnEnd`.

Each phase declares whether a hold may stand in front of it, `Phase.CanSuspendForBoundaryHold` (false by default), and how it resumes, `Phase.ResumeAfterBoundaryHold`. The pattern entry, midfield crossing, pattern exit, approach navigation, go-around, initial climb and departure procedure resume in place. The teardrop resumes in place too, and completes on the next tick when only its abeam fix is left past abeam, so the downwind rejoins its line. Upwind, crosswind, downwind, base, final, intercept course, procedure turn and holding pattern resume from a new copy that re-anchors on its leg from the present position, keeps a controller altitude and lateral offset given on the leg (the offset re-acquired), and voices no "turning {leg}" call; the holding copy picks its entry from the course to the fix. Every other phase cannot be held. `PhaseAcceptanceAuditTests` lists every concrete phase's answer. At most one hold and one held phase stand at a time. Design: [plans/class-b-in-phase-hold.md](plans/class-b-in-phase-hold.md).

**The dispatcher lifts the hold for every controller command.** `CommandDispatcher.DispatchCompoundCore`, and a queued block's body (`BuildApplyAction` → `ApplyCapturedBlock`), call `PhaseList.LiftHold` first, so the gate, the dry run and the handlers all judge the command against the held phase, never the hold. The lifted hold is parked on the transient `AircraftState.LiftedBoundaryHold` (never serialized), which `ImplicitBravoClearance.CaptureWait` reads when the list is no longer there to read. A nested dispatch or a block applied inside a lift lifts nothing more. Afterwards `PhaseList.PutBackHold` puts the hold back only when the held phase survived (same list, same index, same instance, still Active); otherwise the hold is discarded as Skipped, with `OnEnd` only if it had started, and a debug line `[HoldLift]` says so.

## Command interaction — `CommandDispatcher.cs`

When a command lands on an in-phase aircraft:

1. `DispatchWithPhase` is called. It calls `CurrentPhase.CanAcceptCommand(cmdType)`.
2. **`Rejected`** → return error to user. State unchanged.
3. **`ClearsPhase`** → run a **dry-run validation** on a clone (`DryRunValidate`). If valid, set `shouldClearPhases = true`. After the rest of the compound validates, `aircraft.Phases?.Clear()` runs and the command is dispatched normally. If dry-run fails, the phase is preserved and the user sees the validation error.
4. **`Allowed`** → tower-command handler processes the command; remaining blocks queue normally.

Phase-transparent commands (squawk, ident, say, strip ops), the RFIS/RTIS/REPORT family and the WARP/WARPG sim-control bypass never reach the gate; `CommandDispatcher.ReachesPhaseGate` is the one test for that, shared by `DispatchWithPhase` and the deferred-dispatch pushback hold.

A scenario-scripted deferred command that would clear an active `PushbackPhase` is held until the pushback ends, along with any scripted command the pushback would reject; an instructor-typed command still clears it. See [ground/pushback.md](ground/pushback.md) and [command-pipeline.md](command-pipeline.md#deferred-dispatch--wait-behind-and-the-command-run-delay).

**A re-TAXI drops stored clearances and says so** is a decided rule the code follows. `TryTaxi`/`TryAirTaxi` install a fresh `PhaseList`, so a `TAXI` re-issued to an aircraft holding a mid-taxi `CTO`/`LUAW` drops it, for the same runway as for a different one, and a `TAXI` also drops `LandingClearance`/`ClearedRunwayId`. The rule is to keep dropping them and name each dropped clearance in the response, rather than carry them across the new route.

The response of a `TAXI`, `TAXIAUTO`, `RTAXI`, `ATXI` or a `CROSS` at a taxiway hold short ends with one note naming each, the takeoff or line-up clearance first: `(dropped: takeoff clearance for 28R; landing clearance for 28R)`, with `line up and wait clearance for 28R` for a `LUAW`.

A change of runway names no takeoff or line-up clearance when no departure clearance had been read, because its runway-change warning then already says the clearance is void; after a read departure clearance the warning speaks only of that, so the note names the takeoff clearance. An `RTAXI` leaves a stored takeoff or line-up clearance to its own keep-or-void rule, and the taxi a `CTO` or `LUAW` at a taxiway hold short issues itself adds no note. A landing clearance the aircraft has landed on is consumed, not dropped: it is not named once the aircraft's phase list has run its landing, through the rollout, the runway exit, holding after the exit and a pull-up to the parallel's hold short.

**A takeoff clearance cancelled inside the stopping distance** is a decided rule the code follows. A cleared aircraft crosses the hold bar rolling at up to ~12 kt, and a piston needs ~42 ft to stop from 10 kt at 2 kt/s, so a `CTOC` issued closer than that cannot make the hold-short. The rule: the aircraft brakes at the max-effort rate, and when it still overruns the bar it stops just past it and the instructor is warned that it entered the runway; it never crosses quietly. Governing paragraphs: 7110.65 §3-9-11 (cancellation) and §3-9-4.k (the hold instruction on any amendment to an aircraft holding short).

`DepartureClearanceHandler.TryCancelTakeoff` re-arms the hold-shorts the stored `CTO` pre-cleared and judges the first one ahead with `TaxiingPhase.IsHoldShortUnmakeable`, as the dispatcher judges an `HS` armed under a moving aircraft. A bar it can stop for keeps the plain "Takeoff clearance cancelled, hold short" readback. An unmakeable bar is flagged `Unable`, the pilot answers "Takeoff clearance cancelled, unable to hold short of runway 28R, stopping", and the taxi brakes at the firm `ExpediteExitDecelRate` with no speed cap at the bar. The aircraft rests just past the hold line, the usual "holding position on E, not clear of runway 28R" report follows, and `TaxiingPhase.MarkCancelledTakeoffUnable` gives the instructor one amber warning: "{callsign} could not stop short of runway {rwy} after its takeoff clearance was cancelled: nose {d} ft past the hold line[, on the runway] (cancelled {c} ft out, needed {s} ft)". Nothing is scored. A `CTO` or `LUAW` re-clear while it brakes drops the pending warning.

`Phases.Clear()` marks the active phase `Skipped`, all pending phases `Skipped`, calls `OnEnd(ctx, Skipped)` on each phase that was Active (the current one, and a held phase behind a boundary hold, below), sets `aircraft.Phases = null`, clears `TurnRateOverride` and `PreferredTurnDirection` (so a torn-down turn phase can't bias the next lateral target — e.g. `L360` then `DCT`), and emits a phase-cancellation summary via `PhaseClearSummary`.

Turn phases (`MakeTurnPhase`, `InitialClimbPhase`) also null `PreferredTurnDirection` in their own `OnEnd`, covering natural completion as well as force-clear.

## Phase catalog

Files are under `Phases/Tower/`, `Phases/Ground/`, `Phases/Pattern/`, `Phases/Approach/`. Quick map:

**Tower** — `LineUpPhase`, `LinedUpAndWaitingPhase`, `TakeoffPhase`, `RejectedTakeoffPhase` (reaction window → max-effort braking on the centerline → `HoldingInPositionPhase`; installed by `RejectedTakeoff.Install`, shared by the auto trigger in `TakeoffPhase.TickGroundRoll` and the CTOC mid-roll abort), `InitialClimbPhase`, `DepartureProcedurePhase` (charted heading/course SID legs — see below).

The Tower group also holds `FinalApproachPhase`, `LandingPhase`, `RunwayHoldingPhase` (LAHSO), `GoAroundPhase`, `LowApproachPhase`, `TouchAndGoPhase`, `StopAndGoPhase` and `HelicopterTakeoffPhase`.

The rest are `HelicopterApproachPhase` (LAND @spot from off the airport: hold altitude → 400 ft/nm descent to 500 ft AGL → 6° final onto the spot, hands off to `HelicopterLandingPhase` hovering at the air-taxi height; on-field LAND/ATXI use `AirTaxiPhase` instead), `HelicopterLandingPhase`, `VfrHoldPhase`, `MakeTurnPhase`, `STurnPhase`.

**Ground** — `TaxiingPhase`, `HoldingShortPhase`, `CrossingRunwayPhase`, `RunwayExitPhase`, `ClearRunwayPhase`, `PushbackPhase`, `TugReleasePhase`, `AirTaxiPhase`, `AtParkingPhase`, `FollowingPhase`, `HoldingInPositionPhase`, `HoldingAfterPushbackPhase`, `HoldingAfterExitPhase`. `RunwayExitPhase.CompleteExit` sets `AircraftGroundOps.AwaitingTaxiInCall`; `HoldingAfterExitPhase` and `HoldingInPositionPhase` make the arrival's taxi-in call from it (`Pilot/TaxiInRequest.cs`).

`HoldingShortPhase` accepts `FOLLOWG` at every bar. At a bar that protects a runway (`ProtectsARunway`) the follow is **armed**: `GroundCommandHandler.ArmFollowBehindRunwayHold` keeps the hold and leaves [hold, `FollowingPhase`], because following is not a crossing clearance (7110.65 §3-7-2d requires the crossing clearance in addition).

`HoldingShortPhase` takes a `HoldShortArrival` from whoever inserts it (`TaxiingPhase.ArrivalAt`, `FollowingPhase`) that picks its report: held short reads "holding short of X at T"; a hold beyond the point reads "unable to hold short of X, holding position on T" (no unable call made) or "holding position on T" (after it; a runway bar adds ", not clear of runway X", a `RouteIncomplete` bar ", request further taxi") ([ground/hold-short-placement.md](ground/hold-short-placement.md) § Unable).

`CROSS` then puts a `CrossingRunwayPhase` in front of the follow — built from the route when it resumes from this bar, else over its own synthetic path (`CrossingRunwayPhase.OverOwnPath`, snapshotted as `OwnPath`), never replacing `AssignedTaxiRoute`, so the follower's departure bar still files as `DestinationRunway`; the runways that `CROSS` cleared ride on the `FollowingPhase` (`CrossingClearedRunways`), which does not stop at their bars.

`RES` at an armed runway bar is refused in `CommandDispatcher`, and `FOLLOWG` at a departure bar whose route is incomplete is refused (`LUAW`/`CTO` only).

`FollowingPhase` drives its own follow route over the taxi graph through a `GroundNavigator` (the follower's route to the merge node, then the lead's path), gives way short of a merge its lead has not passed, and keeps the nose-to-tail gap along the path; [the navigator doc](ground/navigator.md#followg-driving-the-follow-route) has the detail. Its navigator treats every hold-short as cleared: the phase owns every runway-bar stop.

`FollowingPhase.CheckRunwayHoldShort` skips a bar of a runway the follower is already on (leaving is not crossing), a bar of a runway it is exiting met moving away from that runway, and the bar a clearing route leaves by. It files the bar of the follower's own route destination as `DestinationRunway` (so `RES` there does not release it onto the runway, and the departure queue ranks it tier 0); every other bar stays `RunwayCrossing`.

With no plan to join (no layout, the lead not yet on a taxiway, no taxi path to the lead's path, or the follower ahead of the lead) the follow holds in position inside the phase rather than steering at the lead. It never holds on a runway or inside its hold line: with no follow route there, it first drives a clearing route past the nearest hold-short bar of that runway ahead.

When the lead is deleted or leaves the ground, the follow drops its follow route and, inside a runway's hold line, drives its clearing route until its tail and both wingtips are past the bar's hold line, then brakes to rest along that route (the navigator still steering it). On a taxiway with an assigned route not yet driven to its end, a `TaxiingPhase` takes over at once on the rest of that route, re-anchored where the follower stands, and its own hold-shorts and crossings stop it. It does not take that route when the follow is unjoinable, or when the way back onto the route crosses a runway hold line; the latter makes it unjoinable and the pilot says once that it has lost the traffic and wants taxi instructions. Otherwise it brakes to a stop where it is. At rest it completes into a `HoldingInPositionPhase`, so the aircraft stays in a phase that accepts commands.

A follower braking along its clearing route past the bar, with its lead still on the ground, plans again each tick while it rolls clear of every runway's hold line, and takes up following without stopping first once a plan joins the lead's path.

**A stand is `AtParkingPhase`; anything else a ground movement stops on is `HoldingInPositionPhase`** — the same rule in four writers.

They are `GroundCommandHandler.AddAirTaxiTerminus` (`ATXI`/`LAND` to a helipad or parking vs a taxi spot), `TaxiingPhase.CompleteRoute` (`route.DestinationParking` vs a spot; `TaxiingPhase.EndWithLostRoute` writes the hold for a restored taxi whose route could not be rebuilt), `GroundCommandHandler.TryTaxi`'s zero-segment shortcut (the same discriminator, for a `TAXI` issued to an aircraft already standing on its destination), and `FlightCommandHandler.ApplyWarpGround` (`GroundNodeType.Parking or Helipad` vs everything else).

`GroundCommandHandler.InstallTugMove` applies it too for every `PUSH` form and `PUSHM`, with `HoldingAfterPushbackPhase` in place of `HoldingInPositionPhase` for a spot or node terminus — `TryPushback` and `TryPushbackMulti` take either of those two (or a running `PushbackPhase`, for a redirect) as a precondition, so an aircraft pushed onto a spot can be pushed again.

The stand writers also own `Ground.ParkingSpot`; a writer that lands the aircraft somewhere else clears it, or the Aircraft List keeps reporting a stand the aircraft left.

**Pattern** (all set `ManagesSpeed = true`) — `PatternEntryPhase`, `UpwindPhase`, `CrosswindPhase`, `DownwindPhase`, `BasePhase`, `MidfieldCrossingPhase`, `TeardropReentryPhase`, `PatternExitPhase`, `VfrFollowPhase`.

**Approach** — `ApproachNavigationPhase`, `InterceptCoursePhase`, `ProcedureTurnPhase`, `HoldingPatternPhase`.

**Airspace hold** (`Phases/`, no subfolder) — `AirspaceBoundaryHoldPhase` is the solo-training VFR self-restriction outside Class B/C airspace: it orbits outside the boundary until the entry gate is satisfied, or levels off beneath the volume floor when the volume is overhead (see [airspace-database.md](airspace-database.md)). It runs in free flight only; a pilot in a running phase is capped beneath the shelf by `AircraftState.BravoCap` instead.

**Military routes** (`Phases/`, no subfolder) — `MilitaryRoutePhase` flies an AP/1B training route or
a refueling track: lateral guidance along the published points plus the altitude block, re-armed from
durable state at every segment boundary because `FlightCommandHandler`'s climb/descend/force-altitude
paths all null both altitude bounds. `AerialRefuelingAnchorPhase` flies a refueling anchor and is
separate because an anchor does **not** terminate — an empty navigation route means "fly another lap
of the published orbit", not "done". See [military-training-routes.md](military-training-routes.md).

### Procedure turns and transition selection

- **`ProcedureTurnPhase`** executes a published procedure turn from a CIFP PI leg (AIM 5-4-9 course reversal). CAPP/JAPP auto-engage it when the procedure has a PI leg, the transition is not NoPT, and either the DCT fix matches the PT anchor or the intercept angle exceeds 90°. The PI leg's `OutboundCourse` is the published 45°-offset PT heading (magnetic, **not** the radial out of the fix); `TurnDirection` is the direction of the 180° turn back to inbound.

  The inbound course is the leg into the PT fix, never the final approach course, and every published course converts with the station declination of its navaid, never the aircraft's (details in [approach-and-pattern-geometry.md](approach-and-pattern-geometry.md#procedure-turns--procedureturnphase)); after the turn the aircraft flies the common-route step-downs to the FAF. Implied PTAC (`CAPP` on a vector) additionally requires an empty `NavigationRoute`.
- **`ApproachCommandHandler.SelectBestTransition`** competes each transition's first fix against every common-leg `IAF`/`IF` in its position-based fallback and returns `null` when a common-leg fix is nearest (the aircraft flies the published feeder from the start). When a route fix (a nav-route target or the first known filed-route fix found in transition order) is one transition's first fix and another's through-fix, `FindTransitionBeginningAt` selects the transition beginning there, not the first in CIFP order that contains it: `CFIX KLIDE 40` then `CAPP` at KSJC I30L flies KLIDE, ZULUP, JENES, HIVAK, not BORED, SWIGS, KLIDE (an approach begins at its IAF, 7110.65 §4-8-1.a.3). PT engagement gates `interceptTooSteep` on `transition is not null` to avoid a false-positive PT when no transition is selected and the published feeder already delivers FAC alignment.

### Charted procedure legs — `DepartureProcedurePhase`

Aircraft fly charted SID legs coded as **headings/courses**, not just fix-to-fix great circles (driving case: LINDZ ONE out of KASE — climb 343° to 9100, climbing left turn to 273° to intercept, track the 303° back-course to LINDZ).

`DepartureProcedurePhase` (`Phases/Tower/`) flies the ARINC-424 terminators VA (heading→alt), CA (course→alt, WCA-corrected), VI/CI (heading/course→intercept the next leg's course), VM (heading→manual), a course-tracked CF, and CD/VD/FD/FC (course/heading→DME or along-track distance) + CR/VR (→radial).

`InitialClimbPhase` holds runway heading to the 400 ft AGL TERPS gate, then `InsertAfterCurrent`s the procedure phase (gated on `_proceduralDeparture`).

A departure with no assigned altitude and no SID altitude climbs to the altitude filed when the aircraft was created: `InitialClimbPhase.CruiseAltitude` is `AircraftState.FiledCruiseAltitudeFt`, the pilot's copy, set by every departure-clearance path (`DepartureClearanceHandler`, `TaxiingPhase`). Once that copy is set, a later STARS `++ddd`, `CRUISE`/`QZ`, ERAM or Flight Plan Editor altitude change updates the plan altitude and not the climb. A filing with no route that sets the pilot's altitude (an ERAM `AM` type and altitude) on a departure already cleared for takeoff moves only the pending initial climb's cruise altitude (`DepartureClearanceHandler.RefreshPendingInitialClimbCruiseAltitude`).

Legs come from `ProcedureLegResolver.Resolve` (which keeps the fix-less legs `DepartureClearanceHandler.ResolveLegsToTargets` drops); `ExtractActiveDepartureLegs` returns only the **leading run** of coded legs, stopping at the first plain fix — interior fix legs keep FlightPhysics' turn anticipation via `NavigationRoute`. It reuses approach-intercept geometry (`GeoMath.SignedCrossTrackDistanceNm`, turn-radius lead for VI, proportional cross-track steering for CF).

- **SID climb windows** — a leg's `AtOrBelow`/`Between` crossing altitude (`Altitude1Ft`) is enforced as a **level-off cap** (`ApplyLegAltitudeCap`, min against `_climbCeiling`) only while the SID's published altitudes are in force (`AltitudeRestrictionInForce.ProcedureAltitudesInForce`: a climb via, or the no-data fallback), so a CTO altitude or TDLS maintain is never held below by a leg window; released when the leg sequences (back to the SID hold altitude below) — it never *lowers* the overall ceiling, so an assigned / top-altitude cap survives (AIM §5-2-9.e.5: level off only if reached before the fix).

  A `CFIX` in the window does not end it: while a handover will load the departure route (`IPendingDepartureRoute.PendingRoute`: `DepartureProcedurePhase` flying its coded legs, `InitialClimbPhase` before the deferred-turn gate or holding a radar-vectors SID heading), `CanAcceptCommand` allows it and the crossing is stamped on that pending route's `NavigationTarget`s, which the handover re-adds as they are. `DepartureProcedurePhase.Finish` and `InitialClimbPhase.SetupDepartureNavigation` apply the speed reverts of the pending route's fixes the handover re-adds (`Finish` also takes a flown coded-leg fix's revert, only as a guard for a phase restored from an older snapshot); a lateral instruction or a phase clear that abandons the route applies the pending reverts at once (`OnCommandAccepted`, `OnEnd`). With no route pending (a heading departure) the `CFIX` clears the phase. A `CFIX` on a coded-leg fix is flown on that leg. An "at" or at-or-below crossing caps the climb (`ApplyLegAltitudeCap`) from the command until that fix sequences, earlier legs' published caps still binding; when the leg sequences (`AdvanceLeg`, logged "crossing at {Fix} ended as its leg sequenced") the target returns once to the climb via, else the assigned altitude, else the SID hold. A controller-set crossing speed on the active coded leg's fix is planned so the aircraft crosses the fix at it while the live route is still empty (`FlightPhysics.UpdateSpeedPlanning` → `PlanPendingCrossingSpeed`, fed by `DepartureProcedurePhase.ActiveLegCrossingSpeed`: the distance straight to the fix less the phase's fix-arrival distance), and the speed it replaced returns once when that leg sequences at the fix (`AdvanceLeg` → `ApplyCrossingSpeedRevertAtFix`, logged "crossing speed at {Fix} reverted as its leg sequenced"), not at the handover. Under a climb via the window `CFIX` assigns nothing: a crossing above the via top stops at the top, and one the via already meets changes nothing. Off a via, an "at" or at-or-above crossing above the cleared altitude or SID hold sets `AssignedAltitude`, which the aircraft keeps past the fix; a crossing the clearance already meets (`CrossingBoundMetByClearance`) stores no altitude revert. After a `CM` given in the window, the crossing caps until its fix and then the `CM` altitude applies. A `CM` or `DM` accepted while a SID handover is pending cancels the altitude of crossings stamped in an earlier transmission on the pending route, so the fix gets the SID's published restriction back (speeds stay); a crossing of the same or a later transmission keeps its altitude. A `CFIX` on a coded-leg fix the departure already sequenced is refused "unable, we're already past {FIX}" (not when the fix appears again later on the route). A constrained `DCTF` in the window assigns as before.

  An IFR departure with no altitude assignment (and no climb via) holds `ResolveSidHoldAltitude` = `max(current altitude, SidInitialAltitudeFt)` (the SID transition's TDLS default initial altitude, stored by a bare CTO/CTOPP/LUAW when the default carries no climb via; a climb-via default puts the climb via in force instead) through every coded leg and after `Finish`, instead of climbing toward `_climbCeiling` (7110.65 §4-3-2.a); it is never commanded down, and VFR, no-ARTCC-config and CTO-assigned-altitude departures are unchanged.

  The handover, where `NavigationCommandHandler.PendingDepartureRoute` starts returning null and the departure route goes live, happens at: `DepartureProcedurePhase.Finish` for a SID with coded legs; `InitialClimbPhase.UpdateRvSidHeadingHold`, 5 s after the comms hand-off, for a radar-vectors SID or a CIFP-miss SID held on runway heading; the 400 ft deferred-turn gate (`ApplyDepartureTurn` → `SetupDepartureNavigation`); and `InitialClimbPhase.OnStart` at once for a non-deferred SID such as SJC3, which never has a pending route. `SidInitialAltitudeFt` is not cleared at any of them; it is cleared only when a climb via or descend via is put in force (a newer altitude clearance), so a reader that wants the cleared altitude decides itself whether the maintain still governs: a CFIX or constrained DCTF bound uses `NavigationCommandHandler.ClearedAltitude` (the assigned altitude, else the maintain for an IFR flight with no via clearance and no active or pending approach clearance), on either side of the handover, so a bound the maintain meets assigns nothing and the aircraft holds the maintain before and after it.

  A controller `CM`/`DM`/`CMN`/`FA` — during the phase, or before it started (a `Targets.AssignedAltitude` that differs from the CTO altitude at `OnStart`) — sets the snapshotted `_controllerAltitude` flag and the caps stop writing `TargetAltitude` (AIM §5-2-9.h.3: a plain "maintain" interrupts the SID's vertical navigation). `ForceAltitude` is accepted like `ClimbVia` — the SID keeps flying laterally and the forced altitude is held through the crossing windows — though `Phase.IsAltitudeFamilyCommand` still names only `ClimbMaintain`/`DescendMaintain`.

  A `CVIA` (`FlightPhysics.ClimbViaAltitude`: `ClimbViaTargetFt` lowered by an at / at-or-below / between-upper restriction still ahead) puts the SID's restrictions back in force under its top altitude: `ApplyLegAltitudeCap` caps against it instead of `_climbCeiling` even when `_controllerAltitude` is set, and `OnStart` takes it over the CTO altitude. `FlightPhysics.ApplyClimbViaAltitude` never ends the climb via at its top while this phase flies the coded legs; the phase holds the top until it hands over.

  A lateral vector (`_overridden`) releases the target to the climb via target, else the assigned altitude, else for an IFR aircraft `max(current altitude, SidInitialAltitudeFt)` (a vector never commands a descent), else `_climbCeiling` (7110.65 §4-3-2.c.1); `Finish` releases to the climb via target, else the assigned altitude, else `ResolveSidHoldAltitude`, else `_climbCeiling`. DME / along-track distance is the ARINC `dist_time` field → `LegDistanceNm` (not `Rho`); radial is `Theta`.
- **STARs** handle only the **FM and VM terminators**: `NavigationTarget.TerminalCourseMagnetic` carries the FM or VM outbound course (set by `ResolveLegsToTargets`, including the dedup-collapse case where a TF-to-fix is followed by an FM-from-the-same-fix; a VM leg never becomes a target of its own, its course lands on the fix before it), which `FlightPhysics.ApplyRouteEndLateral` (the route-exhausted branch of `UpdateNavigation`) flies in preference to the direct to the cleared destination and instead of holding. A spawned arrival keeps it too (`ArrivalRouteResolver` resolves the whole runway transition for its terminal courses).

  There is **no `ArrivalProcedurePhase`** — the typed-leg path is departure-only (sole caller `DepartureClearanceHandler`); approaches/STARs otherwise use the flat `ResolveLegsToTargets`.
- **Scope decisions (don't re-litigate):** CF course-tracking is deliberately kept out of core `UpdateNavigation` (only `DepartureProcedurePhase` + the STAR FM terminal use it) to avoid core-nav blast radius.

  An "Approach Phase 3" for fix-less approach legs was intentionally skipped — an instrument `GoAroundPhase` already climbs runway heading to the missed-approach altitude before missed-approach nav, so the "climb heading to altitude" legs `BuildMissedApproachFixes` drops are redundant. Aviation-reviewed: VA = heading (no WCA), CF/CA = track (WCA); a climb-window altitude is a turn *trigger*, not a level-off; do **not** apply the ≤30° approach capture gate to a departure VI→CF intercept.

### Rejected takeoff — `RejectedTakeoff` + `RejectedTakeoffPhase`

`TakeoffPhase` writes IAS directly with `TargetSpeed = null` (the spool ramp of `GroundRollProfile`, clocked by the phase's own
`RollElapsedSeconds` so a hand-ticked phase rolls exactly like an engine-ticked one) and deliberately never reads `Ground.SpeedLimit` — the taxi-calibrated
ground-conflict cap cannot stop a rolling departure and is not asked to. Stopping one is the job of the per-tick `RejectedTakeoff.TryTrigger` scan in
`TakeoffPhase.TickGroundRoll`, gated by the `AutoRejectTakeoffOnOccupiedRunway` session setting (helicopters and live-traffic shadows never
auto-reject); `FlightPhysics.UpdatePosition`'s clamp stays a last-resort backstop only.

- **Blockers** come from `RunwayOccupancy.Classify`: anything `OnSurface` or `Landing` blocks; a `Fouling` aircraft (past the hold line, off the pavement) blocks only with a body part over the strict paved half-width (`RunwayOccupancy.AnyPartOverStrictPavement`); a `Crossing` blocks unless it is projected past the
  `ClearOfRunwayStandoffFt` (250 ft) hold-line standoff — not the pavement edge — by the time the departure arrives; a preceding `Departing` aircraft
  blocks per `PrecedingDepartureBlock`: at a standstill, 7110.65 §3-9-6.a is applied at roll start — the leader projected `ReactionSeconds`
  ahead must have crossed the runway end or be airborne (`SameRunwaySeparation.WillBeFlying`; a decelerating leader never is) with the
  SRS-category spacing (`RequiredDepartureBehindDepartureFt`: 3,000 / 4,500 / 6,000 ft) — else the clearance is declined; once rolling the
  question is collision, so the leader blocks only when the trailer's projected roll reaches it while it is still on the ground, and the usual
  `ShouldReject`/`CanOverfly` chain decides; an opposite-direction roller on the same pavement blocks unconditionally. Every distance is measured to the occupant minus `StopMarginFt` (500 ft, a stand-in for the unmodelled aircraft length).
- **Decision** (`ShouldReject`): below `CategoryPerformance.LowSpeedRejectThresholdKts` (jet 80 / turboprop 60 / piston 35) reject for *any* blocker
  ahead; at or above it reject only when `!CanOverfly` — the liftoff point (`IasToTas(Vr)` minus headwind, in the ground frame) plus
  `RejectedTakeoffOverflyMarginFt` (3000 / 2000 / 1500 ft, all-engines sized) does not fit inside the effective distance. Past V1 the same
  `!CanOverfly` test is the emergency reject.
- **A standstill declines, it does not abort.** Below `RollUnderwayMinKts` (5 kt groundspeed) the pilot answers "unable, traffic on the runway" and
  goes straight to `HoldingInPositionPhase` — there is no maneuver to terminate yet.
- **`RejectedTakeoff.Install` is the single construction site** for both the auto trigger and the controller's CTOC mid-roll abort (the
  go-around's two-site divergence, #283, is the precedent). It replaces the upcoming chain with `[RejectedTakeoffPhase, HoldingInPositionPhase]`:
  `ReactionSeconds` (2 s) of continued acceleration, then `CategoryPerformance.RejectedTakeoffDecelRate` (8.0 / 6.5 / 5.5 kt/s — the invariant is
  rollout < expedite-exit < RTO) with `TakeoffPhase`-style direct IAS integration and centerline cross-track steering.
- **Findings live on the phase, never on `Ground` flags.** `AutoTriggered`, `CannotStopShortOf` and `OverrunReported` are snapshotted on
  `RejectedTakeoffPhase`; `SoloTrainingEvaluator` samples them while the phase is current (Warning for any pilot-initiated reject, plus Safety for
  cannot-stop-short and pavement overrun; a CTOC-commanded abort scores nothing). A per-aircraft `Ground` flag that outlived the phase false-positived
  the next clean reject.
- **CTOC's V1 gate converts through `GroundFrame.IasForGroundSpeed`** — the on-ground IAS field carries groundspeed, so a headwind would otherwise
  move the gate. Past V1 the "unable, continuing" refusal is conditional on `TryFindBlockedBeyondOverfly`. Helicopters have no V1 gate
  (`CancelHelicopterTakeoff`: below 50 ft AGL set down → `HoldingInPositionPhase`, otherwise hover in place through the `CTOPP` `CompletionAgl` +
  `VfrHoldPhase` shape, knowingly inside the height-velocity avoid region).
- **After the stop the aircraft is in `HoldingInPositionPhase`** with the takeoff clearance spent. A fresh CTO/LUAW works through the tower-command
  path (`LineUpFromPosition`), which bypasses HIP's `CanAcceptCommand` gate, and HIP-on-pavement trips `WarnIfRunwayOccupied` for the next clearance.
- `AutoRejectTakeoffOnOccupiedRunway` mirrors auto-go-around: a bare `bool` (default `false`) on `SimScenarioState` for replay fidelity; the client
  preference defaults `true` and pushes it on scenario load.

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

  The two used to disagree about whether a VFR aircraft re-enters the pattern, which stranded a `GA`-then-`MRT` aircraft in a 2000 ft AGL climb (issue #283); both now call `GoAroundHelper.ResolvePatternIntent`.
- **Three ways a go-around picks its climb-out altitude.** `GoAroundHelper.ResolveClimbOutAltitude`: published missed-approach altitude when MAP phases were built; else pattern altitude − 300 ft when re-entering the pattern (AIM 4-3-2 — the same gate `UpwindPhase` uses to release the crosswind turn, so the turn is available the instant the phase hands off); else `null`, meaning `GoAroundPhase` self-clears at 2000 ft AGL.

  Pattern altitude comes from `PatternGeometry.ResolveAuthoredOverrides`, not the bare `CategoryPerformance.PatternAltitudeAgl` constant — the airport's authored per-runway TPA and a commanded `MRT 15` override both have to win, or the go-around levels off above the circuit it is joining.
- **A route-driven ground phase never completes without a successor.** A `TaxiingPhase` that ends with no phase after it leaves the aircraft with no phase, where no ground command reaches it. Every end of the taxi (route complete on entry, route complete mid-taxi, route lost on restore) installs the stand-or-hold phase before it returns true.
- **`ManagesSpeed` is contagious.** If you set it on a non-pattern phase and don't drive `TargetSpeed`, the aircraft will not auto-decelerate or hold a speed restriction. Only set `ManagesSpeed = true` if you take responsibility for speed.
- **Zero forward speed needs `ManagesSpeed` too.** Setting `ctx.Targets.TargetSpeed = 0` once in `OnStart` is not enough. On the first sub-tick
  `FlightPhysics.UpdateSpeed` snaps a reached target to `null` (`|goal − IAS| < SpeedSnapKts`, 2 kt), after which the auto-speed schedule — guarded
  only by `CurrentPhase?.ManagesSpeed != true` — drives `TargetSpeed` to the category default and re-accelerates the aircraft; airborne,
  `UpdatePosition` then drifts it laterally. A hover or hold phase must override `ManagesSpeed => true` **and** re-assert `TargetSpeed = 0` every
  `OnTick` (`VfrHoldPhase`, `HelicopterTakeoffPhase`). The symptom was a `CTOPP` departure-form helicopter spinning up to ~100 kt during a 400 ft
  vertical liftoff; the bare-hover form looked fine only because it completes in about a second and hands off to `VfrHoldPhase`.
- **A speed target read back in a phase can be null.** Physics clears `TargetSpeed` on arrival at it (`FlightPhysics.ArriveAtGoal`), and a ground navigator publishes no speed on a node-arrival sub-tick, so a phase that caps the published target after its navigator ticks may find none. Cap `TargetSpeed ?? IndicatedAirspeed` (`TaxiingPhase.CapAtUnclearedBar`): skipping a null leaves the aircraft uncapped, and taking the cap itself raises its speed.
- **The 5nm-final gate leaves a `SpeedCeiling` on non-phase-managed inbounds.** `FlightPhysics.AutoCancelSpeedAtFinal` releases an explicit ATC speed at 5 nm but, for a hand-vectored inbound with no speed-owning phase, retains the last-assigned speed as a `SpeedCeiling` so the auto schedule can't re-accelerate it (7110.65 §5-7-1.b.4).

  Any phase that transitions an inbound aircraft into a **climb-out** (`GoAroundPhase`, `TouchAndGoPhase`, `StopAndGoPhase`, `LowApproachPhase`) must clear `SpeedFloor`/`SpeedCeiling` in `OnStart`, or the missed-approach / option climb is capped at the approach speed.

  A phase that clears a controller bound equal to the assigned speed also ends that assignment (`FlightCommandHandler.EndBoundWithTheAssignment`), so a `SPD n-` ceiling is not left behind as an exact speed; `ProcedureTurnPhase` clears only the 200 kt ceiling it imposed (`CeilingImposed`). `LandingPhase`, `TouchAndGoPhase`, `StopAndGoPhase` and `HelicopterLandingPhase` end the whole speed assignment at touchdown (`FlightCommandHandler.CancelAssignedSpeedAtTouchdown`); the touchdown tick keeps the flare's own target, and a go-around before touchdown keeps its assignment.

  Add a new climb-out phase → clear the ceiling there too. `GoAroundPhase` also clears `AltitudeFloor`/`AltitudeCeiling` (set by `AOA`/`AOB`): its completion check reads the aircraft's own altitude, so a stale restriction would level the climb somewhere the phase never notices. The one ceiling it puts back is a solo pilot's Class B cap (`AircraftState.ReassertBravoCap`), and the go-around and the low approach's climb-out then complete level at a standing ceiling below their target altitude (`GoAroundPhase.IsLevelAtCeilingBelowTarget`) instead of holding runway heading for an altitude the pilot may not reach ([airspace-database.md](airspace-database.md) "In-phase Class B cap").
- **Tight maneuvers slow to holding speed via `ManeuverSpeedController`.** `MakeTurnPhase`, `VfrHoldPhase`, and `STurnPhase` set `ManagesSpeed = true` and delegate speed to a `ManeuverSpeedController` (`Capture` in `OnStart`, `Reduce` to decelerate to `AircraftPerformance.HoldingSpeed`, `Resume` in `OnEnd`, `CancelAutoResume` from `OnCommandAccepted` on a mid-maneuver speed command).

  `CancelAutoResume` also latches `SpeedTakenOver`: once the controller assigns a speed during the maneuver, neither a later `Reduce` (a VFR hold reaching its fix) nor `Resume` at the end touches speed, since no IFR holding speed limit applies to a VFR orbit and 7110.65 §5-7-4 keeps an assignment until deleted. A resume with an explicit prior assignment but no prior target speed restores the captured assigned speed (`PriorAssignedSpeed`) instead of null.

  `Reduce` never speeds an aircraft up (only acts when current > holding speed). `STurnPhase` additionally gates `Reduce` behind a 5 nm / FAF check (7110.65 §5-7-1.b.4 — no speed adjustment inside the final approach fix), so S-turns slow only when issued outside that boundary and never on short final. Every controller field must be round-tripped in each phase's snapshot DTO.
- **`Rejected` vs `ClearsPhase` semantics.** `Rejected` is for "valid command, but not now" (temporary gate). `ClearsPhase` is for "this command supersedes the phase's intent" — the default. If you find yourself returning `Rejected` for a command that *should* cancel the phase, you've got it backwards.
- **LAHSO insertion is post-hoc.** Hold/exit phases are appended *after* `LandingPhase` completes, not before. `LandingPhase` sets `StoppedForLahso = true`; `PhaseRunner` detects it next tick and appends.
- **`FlightPhysics.NotifyPhaseAdvanced` runs once per completed phase, after the list is settled.** `PhaseRunner` calls it after the LAHSO rebuild, the full-stop exit append or the pattern auto-cycle, never straight after `AdvanceToNext`: a phase those branches skip must not be offered (and consume) a queued block. So a `;` block queued behind a LAHSO landing fires on the tick the aircraft stops, against `RunwayHoldingPhase`.
- **`CurrentPhase != null` bypasses ordinary CommandQueue advancement — except in idle phases.**

  Triggered blocks still fire, and while the current phase overrides `IsIdleAwaitingCommands` (AtParking, HoldingAfterPushback, HoldingAfterExit, HoldingInPosition, HoldingShort, LinedUpAndWaiting) `FlightPhysics.AdvanceQueueWhileIdle` also applies untriggered blocks in strict `;` order — these phases never complete on their own, so the queue is the aircraft's only source of progress (`PUSH; SQ; SQNORM; TAXI …`, issue #407).

  In every other phase an untriggered block waits; the phase either accepts a fresh command (`Allowed`), rejects it (`Rejected`), or yields (`ClearsPhase`).
- **`InitialClimbPhase` must hold while `_rvSidActive`.** The phase must NOT complete and `ApplyDepartureTurn` must NOT load the nav route while `_rvSidActive` is true — only `UpdateRvSidHeadingHold` releases the phase after comms handoff. A bare `CTO` (no assigned altitude) reproduces the early-completion bug; tests asserting before the deferred-turn gate fires do not catch it.
- **Physics never reads `AssignedAltitude` — a cleared climb phase must re-arm `TargetAltitude`.** `FlightPhysics.UpdateAltitude` moves only toward `ControlTargets.TargetAltitude`; `AssignedAltitude` is persistent clearance/UI state that physics ignores. So a phase-clearing lateral command (`FH` or any `ClearsPhase` verb) mid-departure can strand the aircraft at `TakeoffPhase`'s ~400 ft AGL handoff target while `AssignedAltitude` is ignored (bug N85439).

  `CommandDispatcher.ResumeAssignedAltitudeAfterPhaseClear(aircraft, clearedGoAround)` — called from `ReleaseChainControlState`, which every phase-chain teardown (`ClearPhaseChain`, `ReplacePhaseChain`) runs — re-arms `TargetAltitude = AssignedAltitude` only when the phase was actively climbing (`phaseTarget > current + PhaseClearClimbMarginFt`) **and** `phaseTarget <= assigned` (never lowers a climb target) **and** `assigned > current`.

  It skips on-ground aircraft, and skips a `clearedGoAround` clear outright (a go-around owns its published missed-approach target; `AssignedAltitude` is the stale approach clearance). A lateral vector is horizontal-only and never cancels an altitude clearance (7110.65 §4-5-7.h.1), but the climb-only guard is mandatory — an aircraft vectored off a *descent* below its last assigned altitude must hold, not climb back.

  **Any state a phase writes into `ControlTargets` that physics depends on must be reconciled on phase-clear** — physics won't fall back to the `Assigned*` fields. Tests: `N85439ClimbStallAfterFhTests`, `ResumeAssignedAltitudeAfterPhaseClearTests`.
- **A runway crossing can swallow the *next* runway's hold-short bar.** When a taxi route's crossing slice ends the route, `TaxiingPhase.BuildTerminalPhasesAtCrossingExit` — shared by `BuildResumePhases` and `BuildPreClearedCrossingPhases` — decides the terminal phase.

  If the crossing's exit node still carries an **uncleared** `HoldShortPoint`, the terminal is `HoldingShortPhase`, not `HoldingInPositionPhase`: on parallel-runway geometry the far side of the crossed runway *is* the next runway's hold line (SFO taxiway C — cross 10R/28L, hold short 28R, ~190 ft apart with one painted bar between), so `FindRunwayCrossingExitNode`'s `requireSameRunwayExit: false` fallback legitimately returns the *destination-runway* bar.

  Getting this wrong is silent and nasty (issue #315): the aircraft parks on the right line, skips its "holding short runway 28R" report, and LUAW routes to `DepartureClearanceHandler.LineUpFromPosition` instead of `LineUpFromHoldShort`.

  The same helper is where a departure clearance stored during taxi gets consumed on the crossing paths — `CompleteRoute` is not reached when the crossing slice consumes the remaining segments — so it mirrors `CompleteRoute`'s order exactly: clearance first, terminal phase second, or a route that also names a parking destination silently drops the clearance.
- **The crossing's tail-clearance overshoot must yield to a hold-short at its exit.** `CrossingRunwayPhase` appends a virtual node ½ a fuselage *past* the exit so the tail clears the runway behind it, along the route's own onward segments.

  When the route ends on the crossed runway's far bar or runs out first, `AppendLayoutTailClearance` walks the layout straight ahead for what is left plus `RunwayTailClearance.MarginFt`, judging each edge by its whole turn including the end bearing, and never ends with the nose past another runway's bar (cut short of it, an arc that would carry the nose past it dropped; every crossing).

  The walk stops at a runway centreline node, or where no edge is within 45°, logging a Warning.

  Two conditions suppress that: a binding taxiway hold-short within a fuselage downstream (`BindingHoldShortWithin`, issue #172) and an uncleared hold-short on the exit node itself (`ExitIsBindingHoldShort`, issue #315).

  The second is the parallel-runway case above — without it the aircraft ends up ~54 ft *inside* the next runway's holding position markings while announcing that it is holding short of them (AIM 4-3-18.a.6).

  `ApplyExitHoldShortOffset` then snaps the navigator to the hold-short's nose-at-line coordinates, mirroring `TaxiingPhase.SetupCurrentSegment` — the node itself sits on the painted bar, not a fuselage short of it. Hold-short compliance outranks tail clearance in both cases; tail-over-runway exposure is what `HoldShortPoint.TailOverRunwayNodeId` reports.
- **`LineUpFromPosition` starts the phase list itself, so it must be handed a ground layout.** Every other LUAW/CTO path inserts phases and lets the tick loop start them with a full `PhaseContext`; `LineUpFromPosition` calls `Phases.Start(...)` directly.

  `LineUpPhase.OnStart` **faults outright** on a null `ctx.GroundLayout`, and the command still returns success — an "OK — line up and wait runway 28R" with an aircraft that never moves. `TryDepartureClearance` therefore takes a required (never-defaulted) `groundLayout` parameter. `CommandDispatcher.BuildMinimalContext`'s `groundLayout` still defaults to `null`; any new caller that starts a *ground* phase has to pass one.
- **A ground phase publishes `TargetSpeed` every `OnTick`, never only in `OnStart`.** Physics is the only integrator of ground speed and it nulls `TargetSpeed` on the snap, and `GroundConflictDetector.IsAtRest` reads a positive `TargetSpeed` as "this aircraft is a mover" (a pinned lining-up aircraft at 0 kt must stay a mover or it creeps through the LUAW occupant ahead, #409).

  A phase that publishes conditionally is invisible as a mover the first tick after its speed settles, and a hold that pins only in `OnStart` lets a stale positive target push the aircraft forward (`RunwayHoldingPhase` re-publishes 0 per tick for exactly this reason). Roll phases (`TakeoffPhase`, `StopAndGoPhase`, `TouchAndGoPhase`, `RejectedTakeoffPhase`) are the exception: they write IAS directly with `TargetSpeed = null`.
- **Re-inserting a phase re-runs its `OnStart`.** `PhaseList.AdvanceToNext`/`Start`/`SkipTo` always call `OnStart` when a phase becomes current — the only no-`OnStart` path is snapshot restore (`FromSnapshot` sets `CurrentIndex` directly).

  **A phase whose maneuver lives in live objects the DTO does not carry must therefore rebuild them on its first tick after a restore**: `CrossingRunwayPhase` (`_initialized = false`) and `LineUpPhase` (`_needsRestoreRebuild`).

  `LineUpPhase`'s snapshot carries the maneuver (state, the pose it was planned from, the arc playback or the graph-taxi navigator and route segment), and the first tick plans it again from that pose and resumes it where it stood, in every state (Rollout and Stop included) and also while held: a held restore runs its steering tick from the first tick, as the original did.

  A saved maneuver whose re-planned kind no longer fits is dropped with a warning. Only then, or for a snapshot without the maneuver, is the rebuild pose-based, and it never reaches `Fault`: within a main-gear turn radius of the centreline and within `LineUpGeometry.AlignedMaxTurnDeg` of runway heading → complete; within the radius but still turning → the already-aligned rollout, so the nose swings on while rolling; otherwise re-plan from the restored pose with the same `PlanFromCurrentPose` `OnStart` runs. A held restore without a saved maneuver waits for the release.

  `ClearRunwayPhase` restores its navigator the same way (`ClearRunwayPhaseDto.Navigator`) and keeps it when the layout is absent.

  The restored `RollingMode` decides roll vs hold, and `TryUpgradeToRolling` accepts a `CTO` that lands in the one-tick window before the rebuild.

  Without it, a restore landing on an active `LineUpPhase` parks the aircraft at 0 kt forever — `OnTick`'s `PathPlan is null` guard goes to `TickFaulted` every second (S2-OAK-4 bundle, N152SP); `LineUpPhaseRestoreTests` restores at every second of a real line-up and asserts each reaches `TakeoffPhase` / `LinedUpAndWaitingPhase`.

  `LandingPhase` closes the same hole the other way round: its DTO carries the whole `LandingPlan` (geometry and category constants), so a restore rebuilds nothing and a snapshot taken before the first tick is lossless; a snapshot without the plan still rebuilds the constants from the category table on the first tick.

  Any other phase with live-object state and a dead-end `OnTick` has the same exposure; audit before adding one. So `InsertAfterCurrent([maneuver, resume])` re-fires the resume phase's `OnStart` side effects; guard one-shot effects (e.g. `FinalApproachPhase.CloneForResume` sets `_goAroundRolled` so an in-final 360 / S-turn doesn't re-roll the solo go-around RNG — see [approach-and-pattern-geometry.md](approach-and-pattern-geometry.md) footguns).

## File map

One entry per file; paths are relative to `src/Yaat.Sim/`.

```
# Phases/ — clearance-gated behavior
Phases/Phase.cs                # Abstract: OnStart/OnTick/OnEnd, CanAcceptCommand→CommandAcceptance, OnCommandAccepted (release internal state machines on accept), ManagesSpeed (suppresses auto schedule), IsIdleAwaitingCommands (terminal phases awaiting controller input; lets queue advance untriggered blocks while idle)
Phases/PhaseList.cs            # Mutable list: AssignedRunway, TaxiRoute, LandingClearance, ActiveApproach, DepartureClearance, LineUpIntersectionTaxiway (the taxiway a LUAW at a taxiway hold short lined up at, named by the later CTO readback), RequestedExit/ResolvedExit, GivenUpExitTaxiways (exits the crew said "unable" to, shared by LandingPhase and RunwayExitPhase), ReportedNoExitAhead / NoExitReachTestActive (the one no-exit call, and the latch that makes every exit search judge reach once the no-exit stop engaged), mutations
Phases/PhaseRunner.cs          # Static lifecycle: start→tick→advance; auto-appends exit/pattern phases; auto-cycle builds the runway transition when PatternRunway ≠ AssignedRunway; VoicePendingRunwayCrossing voices the owed pattern-crossing warning
Phases/PhaseContext.cs         # Readonly tick context; includes Weather, TowerPosition for RV SID heading hold, PilotContacts (who answers pilot calls) + ToEligibilityContext()
Phases/TowerCabPhases.cs       # Phase families inside local control's jurisdiction: IsArrivalSide (final, landing, pattern, go-around, tower maneuvers on final) — the airborne check-in guard; departures deliberately excluded
Phases/PhaseStatus.cs          # Enum: phase lifecycle status
Phases/CommandAcceptance.cs    # Enum: Allowed, Rejected, ClearsPhase
Phases/CourseLineSteering.cs   # Heading onto a course line through a fix: the course corrected by cross-track error, capped at a 45° cut; used by the procedure turn's inbound intercept and DepartureProcedurePhase's course legs
Phases/ClearanceRequirement.cs # Clearance requirement definitions
Phases/IGroundRollClock.cs      # A phase flying a takeoff roll along the GroundRollProfile spool ramp reports how far into it the roll is (RollClockSeconds); predictors (same-runway separation, rejected takeoff, preceding departure) place the aircraft where the roll has it instead of inferring from speed
Phases/ExitPreference.cs       # ExitSide enum, ExitPreference class, ResolvedExitInfo (branch point + path + turn-off speed), ExitInstructionVerdict (accept/refuse an EL/ER/EXIT on the rollout or a late re-target, with the pilot's own "unable" line in PilotUnable); ResolvedExitInfo.Side is the side the search found the exit on
Phases/ArrivalExitSide.cs      # An arrival's default exit side when the controller gave none: the side of the runway its parking remark lies on (stand from the layout, area from the sidecar parkingAreas via AreaStands, which `@GATE` also uses to pick a stand in the area; 250 ft dead band), else the sidecar exitDirections entry for its ArrivalClass (ga/cargo), else AirportGroundLayout.InferPreferredExitSide. LandingPhase, RunwayExitPhase and FinalApproachExitForecast resolve through it. Logger category ArrivalExitSide.
Phases/RolloutBraking.cs       # Braking kinematics (required decel, DecelOverDistanceKtsPerSec, braking distance, max entry speed for a stop) + NamedExitBrakingLimit (per-category firm rate, max effort under EXP) and the turn-off tolerance, plus the turn-entry point (TurnEntryLeadNm, IsStandardExit, CaptureTurnOffRate), shared by LandingPhase and RunwayExitPhase
Phases/ClearanceType.cs        # Enum: LineUpAndWait, ClearedForTakeoff/Land/Option/TouchAndGo/StopAndGo, RunwayCrossing
Phases/RunwayInfo.cs           # Runway geometry. Both ends' thresholds/headings/elevations; Designator picks the active one. PavementLengthFt is DERIVED from the two ends' coordinates and is the physical length; the nav data's landing_distance_available is declared per end, differs between them, and is never stored on RunwayInfo (NavigationDatabase.DeclaredLandingDistanceFt answers it; an arrival's touchdown geometry comes from LandingThreshold). TWO elevation datums: ElevationFt = the active end's landing threshold (CIFP-sourced, carries the glidepath); AirportElevationFt = the field (carries traffic pattern altitude, AIM 4-3-3), falling back to the mean of the ends when unset so pre-existing fixtures/snapshots keep their value.
Phases/GlideSlopeGeometry.cs   # Glidepath altitude/descent rate (3°, 6° helicopter). AltitudeAtDistance carries a threshold-CROSSING HEIGHT, so the aiming point (AimPointFt = height/tan) emerges from the geometry instead of being tuned per category. Production calls the (dist, elev, category) overload, which pairs AngleForCategory with CategoryPerformance.WheelCrossingHeightFt (wheel band per AIM 1-1-9.d.7, not the published antenna TCH).
Phases/InterceptAngleLimits.cs # 7110.65 §5-9-2 TBL 5-9-1 final-approach-course interception angles: 20° inside 2 nm of the approach gate (every category), otherwise 30° / 45° helicopter. Single home for the rule — InterceptCoursePhase's bust-through gate, FinalApproachPhase's ApproachScore legality verdict, and its forced-intercept glideslope bypass all read it. PatternCommandHandler.MaxCloseInFinalAngleOffDeg deliberately diverges (45° helicopter at any distance; VFR-pattern airmanship analogy, not the vectoring limit).
Phases/RunwayLandability.cs    # Static: whether a type can land on a runway end (LandingDistanceAvailableFt: the declared distance, else pavement length; IsLandable: LDA at least 1.15 × the type's landing distance, no figure / helicopter / VEH* always landable); the pattern-entry flyout's "short" rule
Phases/PatternCompression.cs   # Pattern compression: shrinks a circuit that would cross another active runway (slower Vref-capped speeds, 25° bank, all or nothing, protected corridor = pavement + 1 NM extended centerline); PatternCommanded (PSIZE / controller speed / NeverCompresses), CompressedLegs, PatternRunwayCrossing (the warning payload), CommandedFloorNm for PSIZE, HoldClimbCap
Phases/PatternGeometry.cs      # Compute takes one PatternGeometryRequest (carries the room's ActiveRunways and the PatternCommanded). 7 pattern waypoints from RunwayInfo + category + direction + the authored GroundRunway (required param); also InferDefaultPatternDirection (an L/R parallel's outboard side, else left traffic) and the designator split it uses. The arrival legs (Threshold, downwind abeam, base turn) hang off LandingThreshold.Resolve; DepartureEnd/CrosswindTurn stay on the pavement end per AIM 4-3-2. MinFlyablePatternSizeNm computes the minimum flyable pattern width for an aircraft type given wind; Compute(runway, category, aircraftType, windSpeedKt, ...) enforces this floor to prevent overshooting the final onto adjacent runways (issue #412). ComputeTransition builds runway-transition waypoints; MidfieldAlongTrackNm computes midfield crossing point distance. ResolveAuthoredOverrides(runway, authoredRunway, category, ...) treats the authored TPA as the established pattern altitude and applies the AIM 4-3-3.a category rule (turbine +500, helicopter <=500, piston verbatim); a command TPA wins verbatim
Phases/PatternBuilder.cs       # BuildCircuit, BuildNextCircuit, BuildRunwayTransitionCircuit (one runway's upwind into another's pattern: close parallels turn crosswind beyond both departure ends, crossing runways join through a MidfieldCrossingPhase), BuildPatternExitCircuit (CTO MRC/MRD pattern-exit departures: upwind[→crosswind]→PatternExitPhase, no landing tail), UpdateWaypoints, SetPendingPatternAltitude (queued phases only, for a pattern altitude change: each keeps its own geometry). All Build* methods take aircraftType and windSpeedKt for pattern flyability floor enforcement. BuildFieldCrossingPrefix: the shared crossing→downwind join (crossing, then a TeardropReentryPhase for a jet/turboprop entry-height crossing, else RejoinTrack on the downwind) used by PatternCommandHandler's arrival entry and wrong-side MLT/MRT rebuild and by BuildRunwayTransitionCircuit's crossing arm
Phases/RunwayGeometry.cs       # AreParallelRunways: same airport (ids normalised), headings within 5° (MaxParallelHeadingDeltaDeg), any spacing — classifies the runway-transition circuit, closed-traffic/FOLLOW transitions and the wrong-side downwind switch; AreCloseParallels adds the 0.25 nm centerline limit (AIM §5-4-19 side-step envelope) for the EF sidestep only
Phases/PhaseClearSummary.cs    # Builds short label ("pattern to RWY 28R", "approach to RWY 28R", "missed approach" for the published missed approach's fixes or hold, or phase Name) for the cancellation warning surfaced when a command clears the active phase chain; labels from AssignedRunway only

# Phases/Tower/
LineUpPhase.cs                 # State-machine lineup. A restore plans the maneuver again on its first tick from the snapshot's start pose and resumes the saved state, arc playback or graph-taxi navigator (RollingMode round-trips; the rebuild from the aircraft's pose is only the fallback without the saved maneuver). Preferred: graph-taxi (State.GraphTaxi) — follow the taxiway onto the runway via LineUpGraphRoute + a GroundNavigator; LUAW stops on the centerline rollout, a rolling clearance completes at the fillet exit and lets TakeoffPhase have the rollout as the first 80 ft of the roll. Fallback (no clean junction arc): synthetic LineUpGeometry — Aligned (straight → fillet arc → rollout) or Pivot (SlowTurn → perpendicular straight → SlowTurn → rollout); its straights run at taxi speed braking onto ArcSpeedKts, which governs the arc alone. A hold (CTOC / HOLD) pins the speed but never skips the steering tick, a held restore included. Faulted stays stopped (user recovers via TAXI / CANCEL CLEARANCE)
LineUpGraphRoute.cs            # Graph-aware lineup route (issue #239): walks taxiway edges from the node nearest the aircraft (forward, toward the runway) to the junction bearing a departure-aligned runway fillet arc, then the arc onto the centerline + a rollout straight projected down the runway heading. The walk skips nodes ON the centerline, not just centerline edges — a junction fillet carries no such flag, so at a runway-CROSSING taxiway it used to step through the reciprocal-end prong onto the runway and dead-end (SFO 28R off E). Two speeds: MaxSpeedKts caps the straights at taxi speed, FlowSpeedKts is the rolling floor; the fillet is left to the arc's own SpeedProfile. Follows the painted lead-on lines instead of cutting a diagonal across the taxiway/runway junction. Returns null (→ synthetic pivot) when no departure-aligned onto-runway arc route resolves
LineUpGeometry.cs              # Pure geometry fallback: classifies aircraft pose as Aligned, Pivot, or Fault; builds LineUpPathPlan with closed-form primitives (nose-out, arc, pivot turns, straight, rollout). The single-arc Aligned path is gated by a 90° turn cap + convergence check (above 90° the straight nose-out backs toward the runway-start corner, issue #203); large-turn / non-converging / parallel-taxiway poses fall through to the Pivot (issue #193), as do shallow poses where the straight path wastes >20% of remaining runway (issue #142). Fault only when there is no room to pivot (cross-track < main-gear turn radius), past runway end, or degenerate pivot; ResumedRolloutSpeedKts / ResumedSwingRolloutLengthFt size the restore-only fallback rollout so a mid-turn restore finishes its swing at the ground yaw rate (LineUpPhase publishes TurnRateOverride and clears it in OnEnd)
LineUpArcPlayback.cs           # Closed-form circular-arc playback (invariant I2: position and heading are functions of a single scalar)
LinedUpAndWaitingPhase.cs      # Hold at threshold; await ClearedForTakeoff
TakeoffPhase.cs                # Ground roll→Vr→400ft AGL; each roll tick offers RejectedTakeoff.TryTrigger a shot at ending the roll
RejectedTakeoff.cs             # Rejected-takeoff trigger/decision/install (issue #410): blocker scan via RunwayOccupancy (OnSurface/Landing always, Crossing unless projected clear, a preceding Departing per PrecedingDepartureBlock), Takeoff Safety Training Aid decision table (low-speed reject for any blocker; high-speed/past-V1 reject only when liftoff+overfly margin doesn't fit), single construction site shared with the CTOC mid-roll abort; gated by AutoRejectTakeoffOnOccupiedRunway; IsAtOrPastV1 is the one V1 test the CTOC handler and the PastV1 situation flag share
PrecedingDepartureBlock.cs     # Whether a preceding rolling/airborne departure blocks the trailer: §3-9-6.a landmark spacing (SameRunwaySeparation) projected to roll start at a standstill, a projected-rendezvous collision test once rolling, opposite-direction rollers always
RejectedTakeoffPhase.cs        # 2s reaction (25.109(a)(2)) then max-effort braking on the centerline (RejectedTakeoffDecelRate) → "stopped on the runway, standing by" → HoldingInPositionPhase; honest overrun past the pavement end raises a solo-eval finding
InitialClimbPhase.cs           # Climb to 1500ft AGL, assigned, or the TDLS default initial altitude, or the sent PDC's, a bare CTO stored (AircraftProcedure.SidInitialAltitudeFt); activates SID via mode; RV SID heading hold until handoff+5s; hands off to DepartureProcedurePhase at the TERPS gate for charted heading/course SID legs
DepartureProcedurePhase.cs     # Flies charted ARINC-424 SID legs: VA (heading→alt), VI/CI (heading/course→intercept), VM (heading→manual), CA (course→alt), course-tracked CF, CD/VD/FD/FC (course/heading→DME or along-track distance), CR/VR (→radial); levels off at a leg's at-or-below/between crossing altitude until it sequences (AIM 5-2-9.e), then loads NavigationRoute for the fix-to-fix remainder
IPendingDepartureRoute.cs      # Interface for a departure phase holding the route its SID handover will load (PendingRoute, null when none); implemented by InitialClimbPhase and DepartureProcedurePhase so a CFIX in the climb window stamps that route and the stamp survives the handover
FinalApproachSpeedSchedule.cs  # Uncontrolled long-final speed schedule, additive on Vref like airline flap schedules: clean (Vref+70 capped 240 heavy/220 large/210 RJ; turboprop Vref+55/200; piston Vref+35/120, ±5kt per-callsign jitter), approach-flap max(1.3·Vref, min(clean−25, Vref+45)) settled by a per-callsign 9±1.5nm (jet) / 8±1nm (turboprop) gate; SpeedAtDistanceKts feeds OnFinal spawns and the generator spacing ceiling
FinalApproachSpeedVariety.cs   # Deterministic per-callsign FAS-reduction distance (right-skewed 2.0-5.0nm); lazily assigned in FinalApproachPhase gated on SimScenarioState.FinalApproachSpeedVarietyEnabled (off by default, server-on for live, recording-captured for replay), stored on AircraftApproachState, so arrivals slow to final approach speed at varied distances
HelicopterApproachPhase.cs     # LAND @spot from off the airport (7110.65 §3-11-6): holds the present altitude direct to the spot (a standing SPD is the en-route speed, else type cruise; ≤ 90 kt inside 3 nm), descends at 400 ft/nm to the 500 ft AGL rotorcraft pattern altitude, then a 6° final at 60 kt onto the spot aiming at the air-taxi height, decelerating to a hover over the last 0.25 nm; hands off to HelicopterLandingPhase over the spot. Installed by GroundCommandHandler.TryLand when IsOnFieldForAirTaxi is false (on the field the AirTaxi chain is kept)
RunwayHoldingPhase.cs          # LAHSO: hold at 0kts on runway after landing; clearance-gated (RunwayCrossing); a CROSS of the held runway releases it, an exit instruction is stored, and so is a TAXI whose route enters the crossing runway (started from where the aircraft stands by that CROSS)
GoAroundPhase.cs               # TOGA, runway heading. Climbs to pattern altitude−300 (VFR/pattern traffic), the published missed-approach altitude (instrument), or 2000ft AGL (self-clear); re-asserts a solo pilot's Class B cap and completes level at a ceiling below its target
OccupiedRunwayGoAround.cs      # Pilot-initiated go-around ≤30 s from the threshold when RunwayOccupancy finds a blocking occupant (stopped, crossing, or inside the §3-10-3 landmark); gated by AutoGoAroundOnOccupiedRunway
GoAroundHelper.cs              # Shared go-around wiring for the auto trigger + the GA command: pattern-intent resolution (ResolvePatternIntent), climb-out altitude (ResolveClimbOutAltitude), phase-list install (voids the standing + pending landing clearance and a LAHSO hold-short target), pre-GA landing-intent capture
                               # InstallGoAroundPhases voids a VIS* clearance (any go-around off a visual kills the approach authorization — 7110.65 §7-4-1)
VisualApproachHelper.cs        # AIM §5-5-11.a.3 lost-visual-reference consequence engine: HandleTrafficContactLost (handback vs end-of-visual),
                               # EndVisualLostReference (committed → go-around w/ spoken reason; else level-off + "unable the visual, request vectors"),
                               # VoidVisualApproach (clearance/flags/follow/landing-clearance teardown). Fed by CheckLeadLifecycle + TickVisualDetection.
TouchAndGoPhase.cs / StopAndGoPhase.cs / LowApproachPhase.cs
MakeTurnPhase.cs               # 360/270 turn tracking (cumulative degrees, exit heading); clones pattern phase for 360s; slows to holding speed then resumes
STurnPhase.cs                  # S-turn phase: alternating 30° deviations from final heading for spacing
VfrHoldPhase.cs                # VFR hold: orbit at current position (HPP) or navigate-then-orbit at fix (HFIX); slows to holding speed then resumes
ManeuverSpeedController.cs     # Shared holding-speed slow-down + resume for tight maneuvers (MakeTurn/VfrHold/STurn)
AirspaceBoundaryHoldPhase.cs   # Solo-training VFR boundary hold outside Class B/C until the Bravo clearance or two-way-comms gate is satisfied.
```
