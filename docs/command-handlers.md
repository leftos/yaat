# Command Handlers

> Read this before editing anything in `src/Yaat.Sim/Commands/CommandDispatcher.cs` or any `*CommandHandler.cs`. This is the inside-the-dispatcher
> companion to [command-pipeline.md](command-pipeline.md), which walks one command end-to-end through client → parser → `RoomEngine` → dispatcher →
> queue and stops at "`ApplyCommand` is a thin routing switch → handlers." This doc opens that box: the two switch surfaces, the handler read/write
> contract, and the per-domain effect cheat-sheet.

## Scope

- [command-pipeline.md](command-pipeline.md) owns the **flow** (how a command travels), the four `RoomEngine` paths, and the `CommandQueue` trigger
  machinery. Don't re-read those here.
- [phases.md](phases.md) owns the `CanAcceptCommand` / `CommandAcceptance` contract (`Allowed` / `Rejected` / `ClearsPhase`) and the `PhaseList`
  install/clear lifecycle. This doc references that contract; it does not restate it.
- [tick-loop.md](tick-loop.md) owns when triggered blocks fire (step 9, `UpdateCommandQueue`).
- `architecture.md` owns the "which files to add a new command" index (enum → registry → scheme → parser). This doc covers the rest of the chain:
  which switch arm, which handler, dimension classification, VFR/IFR gating, dry-run safety, and phase-acceptance wiring.

This doc covers **`src/Yaat.Sim/Commands/`** only. The entry point is `CommandDispatcher.DispatchCompound`; everything below is what happens after a
compound has been parsed and routed to the standard (non-track, non-coordination, non-strip) path.

## The two switch surfaces

A command's effect is dispatched through one of **two** giant `switch` statements in `CommandDispatcher.cs`. Knowing which one a verb belongs in is
the single most important thing to get right.

| Switch | Signature | Handles |
|---|---|---|
| `ApplyCommand` | `CommandDispatcher.cs:433` | Airborne / nav / flight / squawk / say / approach-clearance / pattern-entry verbs. The general arm. |
| `TryApplyTowerCommand` | `CommandDispatcher.cs:1345` | Phase-interactive tower & ground verbs (CTO, LUAW, CLAND, pattern turns, TAXI, CROSS, hold-short, exits). |

`ApplyCommand` is the fallback: it returns a real `CommandResult` for everything it knows, and a `NoDispatcherArm` result
(its `default:` arm) for anything it doesn't. `TryApplyTowerCommand` is *nullable*: it returns a `CommandResult` when it recognizes the verb
and `null` (its `default:` arm, `CommandDispatcher.cs:1617`) when it doesn't — `null` means "not a tower verb, let the caller try `ApplyCommand`."

### Why some verbs live in both

Several verbs appear in **both** switches: `ClearedToLandCommand`, `LandAndHoldShortCommand`, `CancelLandingClearanceCommand`, `GoAroundCommand`,
all pattern-entry verbs (`EnterLeftDownwind`, …), pattern turns (`MakeLeft360`, …), `PatternSize`, the hold-orbit verbs, `AirTaxiCommand`,
`LandCommand`, `ClearedTakeoffPresentCommand`. They land in the same `PatternCommandHandler` / `GroundCommandHandler` / `DepartureClearanceHandler`
method either way — the duplication exists because the command can arrive **with or without an active phase**:

- **With a phase** (e.g. `CLAND` arriving while in `FinalApproachPhase`): `DispatchWithPhase` calls `TryApplyTowerCommand` first.
- **Without a phase** (e.g. `EF 28L` issued to a free-flying aircraft, or a *triggered* block re-firing after the phase has been cleared):
  `ApplyCommand` handles it.

`BreakConflictCommand` (`BREAK`) and `GoCommand` (`GO`) are the inverse case: they have an arm **only** in `TryApplyTowerCommand`, never in
`ApplyCommand`. `BREAK` is classified as a ground command (`CommandDescriber.IsGroundCommand`, `CommandDescriber.cs:933`); `GO` is in neither
`IsGroundCommand` nor `IsTowerCommand` (`CommandDescriber.cs:868`). Both reach `TryApplyTowerCommand` only when a phase is active: a directly-typed
`BREAK`/`GO` parses into a `CompoundCommand` and flows through `DispatchCompound` → the phase gate (`DispatchWithPhase`) → `TryApplyTowerCommand`.
(The single-command `Dispatch` entry point at `CommandDispatcher.cs:326` — used by the engine-level `TaxiAll` fan-out — also re-wraps any ground
command into a compound for `DispatchCompound`, but that path is not how a user-typed `BREAK`/`GO` arrives.)

> If you add a phase-interactive verb to **only** `ApplyCommand`, an immediate dispatch may work, but a *queued/triggered* instance of that verb that
> re-fires after a phase transition will hit the no-dispatcher-arm fallback in `BuildApplyAction` (see [Triggered re-dispatch](#triggered-re-dispatch-buildapplyaction)). Add it to both, or to `TryApplyTowerCommand` only if it always requires a phase.

## `DispatchCompoundCore` control flow

`DispatchCompound` (`CommandDispatcher.cs:37`) is a thin wrapper that records initial-contact state, then calls `DispatchCompoundCore`
(`CommandDispatcher.cs:54`). Core runs these steps **in order**; the first one that produces a non-null result short-circuits:

1. **Leading-WAIT defer** — `TryDeferLeadingWait` (`:1038`). A bare leading `WAIT <n>` / `WAITD <nm>` extracts the timer and stores the remaining
   blocks as a `DeferredDispatch` that re-dispatches fresh when the timer expires. Phases and the queue are untouched.
2. **GiveWay defer** — `TryDeferGiveWay` (`:1136`). A leading `GIVEWAY <callsign>` condition defers the whole compound; the aircraft stays in its
   current phase. With `ctx.FindAircraft` wired, an unresolved target callsign is hard-rejected so a typo can't silently fire via the "target gone"
   shortcut.
3. **All-transparent fast path** — `IsAllTransparent` (`:271`) → `ApplyTransparentCompound` (`:297`). If every command in the compound is
   phase-transparent (per `CommandDescriber.IsPhaseTransparent`) and has no condition, apply each directly and return. **This fires whether or not a
   phase is active** — see the footgun about queue-wiping below.
3b. **Standalone pattern-modifier reroute** — a single-command, no-condition `EXT`/`SA`/`MNA` (`IsImmediatePhaseModifierBlock`) on an aircraft with
   **no active phase** is applied directly via `ApplyTransparentCompound` too. These classify as `Immediate` → dimension `None`, so without this the
   queue-wipe fast path (step 7) would destroy a queued pattern entry the moment their dispatcher arm makes dry-run succeed. Applying directly pre-arms
   the queued entry (`AircraftPattern.PendingEntryModifier`, consumed by `TryEnterPattern` when it builds the circuit) without touching the queue. With
   an active phase the phase gate (step 4) already returns before the wipe, so this reroute is scoped to the no-phase case only.
3c. **Pre-issued landing/option clearance reroute** — a compound of only single-command, no-condition `CLAND`/`TG`/`SG`/`LA`/`COPT` blocks
   (`IsPendingLandingClearanceBlock`) on an aircraft with **no `PhaseList` at all** *and* an unfired pattern entry in the queue
   (`PatternCommandHandler.HasQueuedPatternEntry`) is likewise applied via `ApplyTransparentCompound`. Same footgun as 3b, opposite dimension: these are
   tower commands → `CommandDimension.All`, which hits the *other* half of the step-7 fast path. Today they survive only because `DryRunValidate` rejects
   them first; the moment the handler starts pre-issuing, dispatch would reach the wipe and destroy the entry the clearance is meant to attach to.
   Applying directly stores `AircraftPattern.PendingLandingClearance`, which `TryEnterPattern` folds into `standingClearance` when it builds the circuit.
   The `HasQueuedPatternEntry` guard keeps a clearance with nothing queued on the ordinary dry-run-guarded path and its ordinary rejection.
4. **Phase gate** — if `aircraft.Phases?.CurrentPhase` exists, route through `DispatchWithPhase` (`:1172`). See [the phase gate](#the-phase-gate).
5. **Dry-run validation** — `DryRunValidate` (`:812`) runs the first block on a clone. If it fails, return the error; **real state is unchanged**.
6. **Post-validation phase clear** — only now (after dry-run passes) does the deferred `ClearsPhase` actually clear the `PhaseList`
   (`CommandDispatcher.cs:176`).
7. **Dimension-aware queue clearing** — `ClearConflictingBlocks` (`:1798`) removes queued blocks whose dimensions overlap the incoming command;
   non-conflicting blocks survive and are re-appended.
8. **Enqueue + apply first block** — `EnqueueBlocks` (`:1985`) appends the new blocks; the first new block with no trigger is applied immediately via
   `ApplyBlock` (`:1757`). Triggered blocks wait for the physics tick.

## The phase gate

`DispatchWithPhase` (`CommandDispatcher.cs:1369`) decides what an active phase does with the block's **driver** command:

0. **Pick the driver.** `FindPhaseGateDriverIndex` (`:1511`) selects the first *phase-interactive* (non-broad-transparent) command in the parallel
   block — **not** blindly `Commands[0]`. A block only reaches the gate because it holds at least one non-transparent command (`IsAllTransparent`
   claims the rest), so gating on a leading transparent sibling would let any phase that doesn't whitelist it reject the whole block. `SQ, SQNORM,
   PUSH` at parking is the canonical case: `AtParkingPhase` rejects `Squawk`, so the batch failed even though each verb succeeded on its own.
1. **Conditional leading block** (`AT FIX` / `LV alt` / distance-final / on-handoff / ground-entity) → return `null` so the compound takes the normal
   `DryRunValidate` + `EnqueueBlocks` path and the block waits for its `BlockTrigger`. The active phase must not be torn down by a block that hasn't
   fired yet.
2. **`UnsupportedCommand`** → hard reject. Never let an unsupported verb interact with phases — it used to map to `FlyHeading` and destroy
   pattern state. (`CommandDescriber.ToCanonicalType` *throws* on `UnsupportedCommand`, so the transparency probe guards it.)
3. **Phase-transparent command** → return `null`. `IsPhaseTransparentCommand` (`CommandDispatcher.cs:1578`) is a **narrow** dispatcher-local
   list — RFIS/RTIS and their forced variants, `SafetyAlert`, `WakeAdvisory`, and `CancelAutoDelete` (NODEL). These are pure status setters that must
   never clear a phase; routing them through normal dispatch lets `NavigationCommandHandler` apply them without disturbing the phase.
4. **Sim-control bypass** → return `null`. `IsSimControlBypass` (`:1595`) is just `Warp` / `WarpGround` — destructive teleports that wipe
   phase/queue/route *inside the handler*, so the gate has nothing to protect.
5. **Tower command** → `TryApplyTowerCommand` on the driver. If it returns non-null, the result is used and every **sibling** command in the same
   block is then applied via `ApplyParallelSibling` (`:1531`) — transparent siblings through `ApplyCommand`, the rest through `TryApplyTowerCommand`.
   This is how `EF 28L, CLAND` applies both clauses, and why `PUSH, SQ 0233` no longer silently drops the squawk (a squawk has no tower arm, so the
   old tower-only sibling loop skipped it).
6. Otherwise consult the phase's acceptance verdict:
   - **`Rejected`** → return the reason; state unchanged.
   - **`ClearsPhase`** → return the `PhaseShouldBeCleared` sentinel (`:1276`) so `DispatchCompoundCore` can validate *before* clearing.
   - **`Allowed`** → return `null` (fall through to normal dispatch). Phase notification is deferred to `BuildApplyAction` after a successful apply.

### The two sentinels

Both are `CommandResult` values detected by identity/substring, **not** exceptions:

- `PhaseShouldBeCleared` (`CommandDispatcher.cs:25`) — a private static `CommandResult` instance. `DispatchCompoundCore` and `BuildApplyAction` test
  it with `ReferenceEquals`. Clearing is deferred until after `DryRunValidate` succeeds so an invalid command never destroys pattern/approach state.
  The clear sequence (build a `PhaseContext` via `BuildMinimalContext`, capture a `PhaseClearSummary`, `Phases.Clear(ctx)`, null out `Phases`, reset
  turn-rate overrides, `AirborneFollowHelper.ClearFollowState`) is **re-implemented identically** at `CommandDispatcher.cs:176` (immediate dispatch)
  and `CommandDispatcher.cs:2110` (triggered re-dispatch). Both sites must stay in sync.
- `CommandResult.NoDispatcherArm` — set true by `ApplyCommand`'s `default:` arm, which also logs the command type (for bug triage) and
  returns a plain user-facing message: a ground command to an airborne aircraft → "… requires the aircraft to be on the ground", otherwise
  "Unable to …". `DryRunApplyCommand` and `WithRejectedCommand` branch on the typed flag (no message-string parsing) to know a verb fell
  through to no arm, so a no-arm failure isn't mislabeled with a rejected command type.

> Returning a generic `CommandResult(false, …)` where one of these sentinels is expected silently breaks the tower-fallback routing.

## The handler contract

Every per-domain handler follows the same read/write rules:

- **Read** the live `AircraftState` (position, heading, flight plan, current phase, procedure state).
- **Write** one of:
  - `aircraft.Targets.*` (`ControlTargets`, `src/Yaat.Sim/ControlTargets.cs`) — the autopilot panel. Lateral: `TargetTrueHeading`,
    `AssignedMagneticHeading`, `PreferredTurnDirection`, `NavigationRoute` (get-only list). Vertical: `TargetAltitude`, `AssignedAltitude`,
    `AltitudeFloor`, `AltitudeCeiling`. Speed: `TargetSpeed`, `AssignedSpeed`, `SpeedFloor`, `SpeedCeiling`, `TargetMach`, `HasExplicitSpeedCommand`.
  - `aircraft.Procedure.*` (SID/STAR via-mode, active procedure IDs, `DestinationRunway`).
  - A fresh `PhaseList` installed on `aircraft.Phases` (approach/pattern/ground handlers do this).
- **Return** `CommandResult(true, message)` on success or `CommandResult(false, reason)` on failure. Success messages are joined for the RPO.
- **Never move the aircraft directly.** Physics reads `Targets` next tick and turns/climbs/accelerates toward them ([tick-loop.md](tick-loop.md)).

The convention is: **command handlers set `Assigned*`; physics reads them.** `ApplyHeading` (`FlightCommandHandler.cs:11`) is the canonical example —
it clears the active procedure, clears `NavigationRoute`, sets `TargetTrueHeading` (true) + `AssignedMagneticHeading` (magnetic) + clears
`PreferredTurnDirection`, then returns `Ok(...)`. It does **not** touch `aircraft.TrueHeading`.

### The Force* exception

`ApplyForceHeading` (`FlightCommandHandler.cs:80`), `ApplyForceAltitude`, `ApplyForceSpeed`, and the WARP verbs deliberately **teleport** by writing
`aircraft.TrueHeading` / `aircraft.TrueTrack` / `Altitude` / `Position` directly, in addition to the targets. These are the sim-control bypasses
(`IsSimControlBypass`) that skip the phase gate because the handler wipes phase/queue/route itself.

## Per-domain effect cheat-sheet

| Domain | Handler | What it mutates / installs |
|---|---|---|
| **Flight** | `FlightCommandHandler` | Heading/alt/speed/squawk/turn-rate. Heading verbs call `ClearActiveProcedure` + clear `NavigationRoute` + set `Assigned*` + `PreferredTurnDirection`. CM/DM clear via-mode and set `TargetAltitude`/`AssignedAltitude`. Force* teleport. |
| **Navigation** | `NavigationCommandHandler` | JRADO/JRADI/DEPART/CROSS multi-fix routing, STAR (`DispatchJarr`) and airway (`DispatchJawy`) resolution into `Targets.NavigationRoute`, climb/descend-via mode. RFIS/RTIS visual-acquisition (need `ctx.Weather`/`ctx.FindAircraft`). `DispatchJfac`/`DispatchHoldingPattern` install a fresh `PhaseList`. |
| **Approach** | `ApproachCommandHandler` | CAPP/JAPP/PTAC/CVA (JFAC/JLOC are `NavigationCommandHandler.DispatchJfac` — see Navigation row). Deferred clearance → `aircraft.Approach.PendingClearance` (`:112`); immediate → `aircraft.Phases = new PhaseList { AssignedRunway, ActiveApproach }` (`:130`, `:303`, `:391`, `:481`). Procedure-turn engagement (see [phases.md](phases.md)). `ClearArrivalProcedureState` (`:1757`) tears down STAR/pending/expected/route on airport change. |
| **Pattern** | `PatternCommandHandler` | `TryEnterPattern`, pattern direction/turn/extend/size/offset/S-turn mods, option ops (T&G/S&G/low-approach/option), hold-orbit/hover, CLAND/LAHSO/CLC/GA. Builds/mutates pattern `PhaseList`. Classified **VFR-only** by `VfrCommandPolicy.RequiresVfr`; the client is what gates it. |
| **Departure** | `DepartureClearanceHandler` | CTO/LUAW/CTOC state machine. `TryDepartureClearance` (`:90`) branches on current phase: `HoldingShort` / `Taxiing` (stores clearance for later) / `LineUp` / `HoldingInPosition`. Installs `LineUp → [LinedUpAndWaiting] → Takeoff → InitialClimb` tower phases; stores `Phases.DepartureClearance`. Pattern-relative modifiers classified **VFR-only** by `VfrCommandPolicy.IsVfrOnlyDeparture`; the client is what gates them. |
| **Ground** | `GroundCommandHandler` | Taxi/pushback/hold-short/cross/exit/follow/give-way/break/go. The routing methods (`TryTaxi`, `TryTaxiAuto`, `TryPushback`, `TryHoldShort`, `TryFollow`, `TryAirTaxi`, `TryLand`, `TryAddExplicitHoldShorts`, `TryCrossRunway`, `TryApplyRouteCrossingsAndHoldShorts`) take an `AirportGroundLayout? groundLayout`; the rest (`TryAssignRunway`, `TryHoldPosition`, `TryResumeTaxi`, `TryGiveWay`, `TryBreakConflict`, `TryGo`, `TryExitCommand`) don't. `TryApplyRouteCrossingsAndHoldShorts` (pre-clear listed crossings then add/re-arm hold-shorts, atomic) is the shared engine behind both `RES … CROSS … HS …` and multi-runway `CROSS … HS …`; single-runway `CROSS` keeps its own path (immediate-satisfy + destination-runway far-side crossing). On the phase path the layout is `ctx.GroundLayout` (see `TryApplyTowerCommand:1347`); on the `ApplyCommand` path (AirTaxi/Land/CTOPP) it is `aircraft.Ground.Layout`. Installs/mutates ground `PhaseList`. `TryTaxi` resolves through `TaxiPathfinder.ResolveExplicitPathDetailed` so it can react to the structured `PathfindingFailure`. Recovery ladder, in order: (1) a **first** cleared taxiway that is a parallel sibling ramp lane the map does not connect (SFO M3 → M4 / M5, from a gate or mid-lane) is honoured by a free-space cut across the apron onto it — `RampLaneReposition.TryPlan` returns a route whose first segment is a `VirtualNode` leg, see [`ground/pathfinder.md`](ground/pathfinder.md) (issue #396); (1b) a `@parking` / `$spot` clearance whose **last** cleared lane is a ramp taxilane the map does not join to the stand's sibling lane (OAK `TAXI V T TE @22`) — `DestinationUnreachable` blaming no taxiway — is honoured by taxiing the lane to the point nearest the stand and cutting across the apron onto the stand's lane (`RampLaneReposition.TryPlanDestinationCut`, issue #400), readback unchanged; (2) a parking start whose first cleared taxiway is `TaxiwayNotConnected`, is **not** such a lane, but lies within `GateAdjacentTaxiwayMaxFt` (450 ft) with no runway centerline between (`AirportGroundLayout.RunwayCenterlineBetween`) is retried without it and warned `unable via K — no ramp connection from the gate; taxiing via M3` (`TryDropGateLeadOut`); (3) the older contradictory-via drop for `@parking`/`$spot` destinations (`TryDropContradictoryVia`) runs right after it. Both return a `DroppedTaxiwayRoute` whose `DroppedName` becomes the `CommandResult.EffectiveCommand` the solo readback verbalizes and whose `Command` is the clearance as applied (used for the route summary's runway/turn-hint context). |
| **Military routes** | `MilitaryRouteCommandHandler` | CMTR/MTRA/XMTR/SAYEXIT (7110.65 §9-2-6) and CAR (§9-2-13). Kept out of `NavigationCommandHandler`, which is already large; military routes are a self-contained domain with their own database dependency. Installs a fresh `PhaseList` holding `MilitaryRoutePhase` (training route or refueling track) or `AerialRefuelingAnchorPhase` (anchor orbit). Picks which published direction of a refueling track is meant from the aircraft's position — the clearance does not say. Takes `DispatchContext` for `ListAircraft`, used to warn when a second aircraft is cleared into an occupied route. `CMTR` on a refueling track and `CAR` on a training route each point at the other. See [military-training-routes.md](military-training-routes.md). |
| **Contact** | `ContactCommandHandler` | CT / FCA — pure pilot-speech, **no flight-control mutation**. Resolves a target position to a frequency via `ctx.ArtccConfig`, queues a pilot readback, sets `aircraft.HasLeftStudentFrequency = true`, and stamps `CompletedAtSeconds`/`CompletionReason = HandedOff` on the first CT/FCA. |
| **Flight plan** | `FlightPlanCommandHandler` | `TryChangeDestination` (APT/DEST) only — canonicalizes the airport via `NavigationDatabase.TryResolveAirport`, rejects unknowns, and clears arrival-procedure state when the destination actually changes. Dispatched from `RoomEngine.SendCommandAsync`'s intercept for the bare interactive form, and from `ApplyCommand`'s arm for preset/conditional forms. |

## VFR / IFR gating is classification here, enforcement in the client

**The dispatcher does not gate on flight rules.** It applies whatever it is given to whatever aircraft
it is given. Since issue #317 the VFR-only restriction is a controller preference
(`VfrCommandsForIfr`: `None` / `EnterFinalOnly` / `All`, default `EnterFinalOnly`) that the desktop
client enforces before a command reaches the wire. What lives in Yaat.Sim is the *classification*:

`src/Yaat.Sim/Commands/VfrCommandPolicy.cs`

- `RequiresVfr(command)` — the long list of pattern/option/hold verbs (ELD…EF, MLT/MRT, TC/TD/TB, EXT,
  SA/MNA, the 360/270 orbits, PS, MLS/MRS, OFL/OFR, CA, TG/SG/LA/COPT, HPP\*/HFIX\*).
- `IsVfrOnlyDeparture(departure)` — the three pattern-relative departure modifiers: a relative turn
  off runway heading (`MR{N}`/`ML{N}`), a pattern-exit departure (`MRC`/`MLC`, `MRD`/`MLD`), and
  closed traffic (`MLT`/`MRT`). Given to an IFR departure they abandon its SID or filed route with no
  amended clearance. Everything else — bare `CTO`, an assigned heading, `CTO RH`, present-position
  hover, `OC`, and `DCT`/`TLDCT`/`TRDCT` — is a routine IFR clearance and is never gated.
- `IsVfrOnly(command)` — the union: the above plus `FOLLOW` and `CM A`/`CM B`.
- `AllowsForIfr(command, mode)` — whether an IFR aircraft may receive it under a given mode.

Client side, two independent surfaces consume that:

- `Yaat.Client/Services/VfrCommandGate.cs` — typed, speech-mapped, and favorite/macro commands, wired
  into `MainViewModel.SendCommandAsync`. It re-parses the canonical string through `CommandParser` to
  get typed commands, so no verb list is duplicated.
- `Yaat.Client/Services/AircraftCommandApplicability.cs` — the right-click menus, which enforce by not
  offering the item. Menu send paths call `Connection.SendCommandAsync` directly and never reach
  `SendCommandAsync`, so both surfaces are needed.

> A new pattern-ish verb must be added to `VfrCommandPolicy.RequiresVfr`, or the client will offer and
> forward it for IFR traffic regardless of the controller's setting. Scenario presets, solo training,
> and any non-desktop front-end are ungated by design — they behave as `All`.

## Dimension classification

`ClearConflictingBlocks` (`CommandDispatcher.cs:1798`) is dimension-aware. Each command declares which axes it touches via
`CommandDescriber.GetCommandDimension` (`CommandDescriber.cs:274`): `Lateral`, `Vertical`, `Speed`, `All`, or `None`. Tower and ground commands are
`All`; ClimbVia/DescendVia and DepartFix are `Lateral | Vertical`; holds are `Lateral`. A new heading command clears queued lateral blocks
but leaves a queued altitude block alone; mixed-dimension blocks are split via `SplitBlockNonConflicting` (`:1923`).

**CrossFix is not lateral; DepartFix is.** `DispatchCrossFix` stamps the restriction on the fix when it is already on the route and appends it when it
is not, leaving every other fix and restriction untouched — so `CFIX` is `Vertical` (plus `Speed` only when a speed was given) and a hand-built
descend-via chain never cancels the lateral work the aircraft is flying toward. `DispatchDepartFix` does the opposite: it clears `NavigationRoute`,
installs the single fix, and sets a departure heading, so it stays lateral.

**Incoming vs. queued is a different question.** `GetCommandDimension` answers "what does this command seize when it fires" — which is what the
*incoming* compound is measured by. What a block that is still *waiting in the queue* occupies is `CommandDescriber.GetQueuedCommandDimension`, and
`SplitBlockNonConflicting`'s per-command keep test must use that one. The two diverge for pattern entries, approach clearances, and holds
(`CommandDescriber.IsHoldCommand` — `HPP*`/`HFIX*`/`HP`): an incoming `ERD` or `CAPP` takes all three axes (so issuing one clears the whole queue),
but one sitting behind a `DCT` or `AT` condition is only a lateral plan — a fresh vector or DCT replaces it and must cancel it, while an altitude or
speed assignment issued on the way to the fix must not. Everything else falls through to the `TrackedCommandType` classification.

> Never call `aircraft.Queue.Clear()` / `aircraft.CommandQueue.Clear()` inside a handler to "reset" state — that defeats parallel-block survival.

> A queued block's per-command dimensions must never all read `None` when the block reports a conflict in aggregate: the block then survives every
> supersede, and the moment the block ahead of it is marked complete-because-superseded the queue advances straight into it. That is exactly how
> `RELR 20` used to turn an aircraft 20° right and then hand it back to the downwind it had just been vectored off.

## Dry-run safety

`DryRunValidate` (`CommandDispatcher.cs:812`) clones the aircraft (`AircraftState.FromSnapshot(aircraft.ToSnapshot(), ctx.GroundLayout)`) and runs
**only the first block** on the clone via `DryRunApplyCommand` (`:852`), which tries `TryApplyTowerCommand` first (if phases are active) then
`ApplyCommand`. The dry-run context (`:826`) overrides `Rng = new Random(0)`, `ValidateDctFixes = false`, `AutoCrossRunway = false`, and crucially
`TerminalEmitter = null`.

> Handlers must be **clone-safe**: any write to a singleton, a sibling aircraft, or anything not on the cloned `AircraftState` leaks out of the
> dry-run. `TerminalEmitter` is nulled specifically so SAY-class verbs (`ApplyCommand:555-577`) don't broadcast phantom pilot transmissions on the
> throwaway clone.

## Triggered re-dispatch (`BuildApplyAction`)

`BuildApplyAction` (`CommandDispatcher.cs:2086`) builds the closure stored on a queued `CommandBlock` and run when its trigger fires
([tick-loop.md](tick-loop.md) step 9). It captures the parsed commands and the `DispatchContext`, then for each command:

1. If a phase is active, try `TryApplyTowerCommand` first (mirroring the user-typed path). This is why queued tower verbs (`TAXI … ; CTO MRT`) re-fire
   correctly after a phase transition instead of hitting the no-dispatcher-arm fallback.
2. If `TryApplyTowerCommand` returns the `PhaseShouldBeCleared` sentinel, run the same phase-clear sequence as `DispatchCompoundCore`, then apply via
   `ApplyCommand` against the cleared state (`:2105`). We're already past validation here (the block was enqueued through the same dispatcher).
3. Otherwise fall back to `ApplyCommand`.
4. After a *successful* apply, call `NotifyPhaseCommandAccepted` (`:2142`).

**Track commands are excluded from the closure.** `TrackEngine.IsTrackCommand` verbs (`HO`/`TRACK`/`DROP`/`ACCEPT`/…)
have no arm in `ApplyCommand`, so a triggered `AT FIX HO 2B` would hit the no-dispatcher-arm fallback. `CreateBlock`
therefore omits track commands from the `ApplyAction` and flags the block `HasTrackCommand`; when the trigger fires,
`SimulationEngine.ProcessTriggeredTrackBlocks` (run inside `TickPhysics`, shared by the standalone sim and the
server tick) dispatches them through `TrackEngine.Dispatch` — the one path with the live `SimScenarioState` and
ARTCC config needed to resolve the target. `TrackApplied` guards against the per-sub-tick scan re-firing and
survives snapshot restore (the post step re-parses `SourceCommandText` when `ParsedCommands` is dropped by
`FromSnapshot`). Immediate (unconditional) track-command presets are routed straight to `TrackEngine.Dispatch` by
`SimulationEngine.TryDispatchImmediateTrackPreset` before they ever reach `DispatchCompound`.

## Phase-acceptance notification

`NotifyPhaseCommandAccepted` (`CommandDispatcher.cs:1299`) releases phase-internal holds (e.g. the RV-SID runway-heading hold in `InitialClimbPhase`)
by calling `currentPhase.OnCommandAccepted(...)` — but **only after a command actually applied**, so a later validation/apply failure doesn't release
internal state prematurely. It is called both on immediate dispatch (after `DispatchWithPhase` returns `Allowed`) and at trigger-fire time in
`BuildApplyAction`.

> The Unsupported / phase-transparent / sim-control-bypass guards inside `NotifyPhaseCommandAccepted` look redundant for immediate dispatch but are
> **load-bearing for the triggered-block path** — queued blocks reach this helper without the pre-filtering `DispatchWithPhase` applies. The in-code
> comment (`:1290`) warns against removing them.

## `DispatchContext` — bundled call-site state

`DispatchContext` (`src/Yaat.Sim/Commands/DispatchContext.cs`) is a positional `record` threaded through every dispatch path and handler. Its fields
and which domains read them:

| Field | Read by |
|---|---|
| `GroundLayout` | The graph-routing `GroundCommandHandler` methods (via the phase path's `TryApplyTowerCommand:1347`); the dry-run clone constructor; `ConvertGroundEntityCondition`. |
| `Rng` | `ApplyRandomSquawk` and anything needing deterministic randomness; overridden to `Random(0)` in dry-run. |
| `Weather` | RFIS/RTIS visual acquisition (nullable; commands fail gracefully when absent). |
| `FindAircraft` / `ListAircraft` | RTIS/FOLLOW relative-traffic lookups; GiveWay target validation; `RunwaySafetyAdvisor` occupied-runway scans (nullable). |
| `ValidateDctFixes` | `ApplyDirectTo` strict off-route fix check; forced off in transparent + dry-run paths. |
| `AutoCrossRunway` | `TryTaxi` / `TryTaxiAuto` auto-crossing-clearance behavior; forced off in dry-run. |
| `SoloTrainingMode` / `RpoShowPilotSpeech` | CT/FCA pilot-speech routing. |
| `TerminalEmitter` | SAY-class verbs broadcast through it; **nulled in dry-run / parser tests** so SAYs don't fire twice. |
| `ArtccConfig` | `ContactCommandHandler` target → frequency resolution; `RunwaySafetyAdvisor` safety-logic gate (nullable). |
| `ScenarioElapsedSeconds` | CT/FCA handoff-completion stamp. |

All fields are positional and required so a future addition breaks at the compiler, not silently at runtime. **Add a new contextual flag here and set
it at the `SimulationEngine` / `RoomEngine` call sites — never pass it as a handler parameter.** The bundle exists to avoid signature creep.

## Non-blocking controller advisories (`PendingWarnings`)

Handlers that want to warn the RPO **without failing the command** append a string to `AircraftState.PendingWarnings`. The strings are drained every
tick (`SimulationWorld.DrainAllWarnings` → yaat-server `TickProcessor.BroadcastWarnings`) and broadcast as `"Warning"` terminal entries, rendered
amber in the client — so the advisory surfaces on the tick *after* the command's own response, decoupled from `CommandResult.Message`. Two users:

- `MilitaryRouteCommandHandler.WarnIfRouteOccupied` — a second aircraft cleared onto an occupied MTR (7110.65 9-2-6.a).
- `RunwaySafetyAdvisor` (`src/Yaat.Sim/Commands/RunwaySafetyAdvisor.cs`) — 7110.65 3-9-4 occupied-runway advisories: a landing-family clearance
  (CLAND/COPT/TG/SG/LA/LAHSO/CLANDF) onto a runway with traffic holding in position or taxiing to line up, and the reverse (LUAW while an aircraft
  holds a landing-family clearance for the runway), plus `WarnIfTrafficOnFinal` on LUAW for live-traffic shadows within 6 nm of the
  final (3-9-4.d) and shadow occupants by geometry. Gated on the airport's vNAS ARTCC config: suppressed when
  `ArtccConfigResolver.AirportHasFullSafetyLogic` finds an ASDE-X config with runway configurations (CRC's Safety Logic covers the incursion there).
  `WarnIfAnotherHoldingInPosition` (also on LUAW) is the 3-9-4.h reminder — a second aircraft lined up on the same pavement while the first
  still awaits its takeoff clearance — and is not safety-logic gated (the sim has no daylight or LA/LM staffing state, so it always fires).
  `WarnIfRunwayOccupiedForTakeoff` (#409) fires on CTO when another aircraft is holding in position on the runway without its own takeoff
  clearance (or a shadow occupies the surface): 3-9-6 same-runway separation. Also not safety-logic gated — the separation rule applies
  regardless of ASDE-X equipage. Called from `TryDepartureClearance` only when the aircraft is entering the runway now (hold-short,
  holding-in-position, or mid-line-up — a CTO stored during taxi doesn't warn) and from `TryClearedForTakeoff` (CTO to an already-LUAW
  aircraft, the dual-LUAW case). An occupant that already holds its takeoff clearance, a rolling departure, and arrivals cleared to land all
  stay silent (anticipated separation, 3-9-5).
  This is why `PatternCommandHandler`'s clearance methods, `DepartureClearanceHandler.TryDepartureClearance`, and
  `DepartureClearanceHandler.TryClearedForTakeoff` take the full `DispatchContext`.

## Adding a new command's effect

Enum + registry + scheme + parser are covered in `architecture.md`. Inside the dispatcher:

1. **Pick the switch arm.** Phase-interactive tower/ground verb → `TryApplyTowerCommand` (and `ApplyCommand` too if it can arrive without a phase or
   be queued/triggered). Plain airborne/nav/flight verb → `ApplyCommand`.
2. **Write the handler.** Read `AircraftState`, write `Targets.*` / `Procedure.*` or install a `PhaseList` (via `BuildMinimalContext` for the
   `PhaseContext`), return `CommandResult`. Keep it clone-safe.
3. **Classify the dimension** in `CommandDescriber.GetCommandDimension` (and `ClassifyCommand` for the `TrackedCommandType`) so dimension-aware queue
   clearing works. If the verb seizes more axes when it fires than it occupies while it waits — anything that installs its own phase chain — also give
   it an arm in `GetQueuedCommandDimension`, or a queued instance of it will survive every supersede.
4. **Classify VFR/IFR** if applicable: add pattern/option verbs to `VfrCommandPolicy.RequiresVfr`; add departure-clearance modifiers to
   `VfrCommandPolicy.IsVfrOnlyDeparture`. The client gate and the context menus both read from there.
5. **Wire phase acceptance** — give the relevant phase a `CanAcceptCommand` arm (`Allowed` / `Rejected` / `ClearsPhase`, see [phases.md](phases.md)).
   A pure status verb that must never clear a phase goes in `CommandDescriber.IsPhaseTransparent` (broad list, fast path) and/or the dispatcher-local
   `IsPhaseTransparentCommand` (narrow list, phase gate). Put it on the **broad** list if it may ever ride alongside an interactive verb in a parallel
   block — that list is also what excludes it from driving the phase gate (see the two-lists footgun below).
6. **Verify the triggered path** — if the verb can be queued behind a trigger (`AT FIX` / `LV alt`), confirm `BuildApplyAction` re-dispatches it
   correctly (tower verbs need a `TryApplyTowerCommand` arm to avoid the no-dispatcher-arm fallback).
7. **Give it display names** — add an arm for the new `ParsedCommand` in **both** `CommandDescriber.DescribeCommand` (canonical short form) and
   `CommandDescriber.DescribeNatural` (user-friendly text). Without them the command falls through to the record's `ToString()` and leaks raw text
   like `"DeleteCommand { }"` into every queued-block description, the RPO ack, `SHOWAT`/`DELAT`, and the client "Pending Cmds" column (issue #226).
   `CommandDescriberCompletenessTests` enforces both switches cover every subtype, so a missing arm fails the build.

## Footguns / Pitfalls

- **The chaining contract lives in [command-chaining.md](command-chaining.md)** — per-category completion, the three advancement regimes, the fire-time abort-remainder rule, and the historical regression classes. A handler failure returned from a queued block's fire-time apply now discards that compound's remaining blocks.
- **Two switch surfaces, not one.** Add a phase-interactive verb to only `ApplyCommand` and a queued/triggered instance hits the
  no-dispatcher-arm fallback when it re-fires after a phase transition. Tower verbs that can be queued need an arm in **both** `ApplyCommand` and `TryApplyTowerCommand`.
- **`PhaseShouldBeCleared` is a sentinel value, not an exception** — detected by `ReferenceEquals`. The no-dispatcher-arm case is the typed
  `CommandResult.NoDispatcherArm` flag. Returning a generic failure where one is expected silently breaks tower-fallback routing.
- **Phase clearing is deferred until after dry-run.** `DispatchWithPhase` returns the sentinel rather than clearing in place; clearing before
  validation would destroy pattern/approach state on a command that then fails. The same clear sequence is duplicated in `BuildApplyAction`
  (`:2110`) for triggered blocks — both sites must stay in sync.
- **Dry-run runs the first block on a clone.** Handlers must be clone-safe: any write to a singleton, a sibling aircraft, or off-clone state leaks
  out. `TerminalEmitter` is nulled in the dry-run context specifically so SAY-class verbs don't broadcast phantom pilot transmissions.
- **Never call `Queue.Clear()` in a handler.** Queue clearing is dimension-aware (`ClearConflictingBlocks` + `SplitBlockNonConflicting`); a handler
  that wipes the whole queue defeats parallel-block survival (a heading command should preserve a queued altitude block).
- **Build queued blocks only through `CreateBlock`.** Both the enqueue path (`EnqueueBlocks`) and the supersede-split path
  (`SplitBlockNonConflicting`) construct a `CommandBlock` from a list of `ParsedCommand`s; every field derivable from those commands
  (`Commands`, `Dimensions`, `HasTrackCommand`, `IsWaitBlock`, and the `ApplyAction`'s filtered command list) is derived inside `CreateBlock`.
  Hand-rolling a second construction site is how the split path twice lost `HasTrackCommand` and silently dropped a queued handoff. A caller
  rebuilding an existing block must still copy that block's live `WaitRemaining*` countdown and `TrackApplied` guard across — those are runtime
  state, not derivable from the commands. The condition label (`"at OAK: "`) is likewise not derivable: `BuildConditionLabels` produces it once
  from the `BlockCondition`, and `CreateBlock` stores it on the block as `DescriptionPrefix`/`NaturalDescriptionPrefix` so a split can re-apply it
  verbatim instead of re-deriving it from the lossy `BlockTrigger`.
- **Handlers write `ControlTargets`, never position — except Force\*.** `ApplyForceHeading`/`ApplyForceAltitude`/`ApplyForceSpeed`/WARP teleport by
  writing `aircraft.TrueHeading`/`Altitude`/`Position` directly. They are sim-control bypasses that skip the phase gate because they wipe
  phase/queue/route inside the handler.
- **VFR/IFR gating is classification in Yaat.Sim, enforcement in the client.** The dispatcher applies whatever it is given.
  `VfrCommandPolicy.RequiresVfr` lists the pattern/option verbs and `IsVfrOnlyDeparture` the pattern-relative CTO modifiers; the desktop client
  checks them against the controller's `VfrCommandsForIfr` setting. A new pattern-ish verb omitted from `RequiresVfr` reaches IFR traffic
  regardless of that setting.
- **Two different "transparent" lists.** `CommandDescriber.IsPhaseTransparent` (`CommandDescriber.cs:1262`) is the **broad** list used by the
  `IsAllTransparent` fast path (squawk, ident, say, RFIS/RTIS, NODEL, CT/FCA, expedite, the entire strip / half-strip / separator / blank
  family, …). Keep the strip family complete against `TrackEngine.IsStripCommand`: a strip type missing from the broad list sends a preset or
  deferred `STRIP <bay>` into the phase gate, where `AtParkingPhase` / `TaxiingPhase` reject it (issue #396). A verb on this list is applied directly by
  `ApplyTransparentCompound`, which **skips `ClearConflictingBlocks`** — so it neither consults phases nor wipes the queue. The single exception is
  `EXP <alt>`, which assigns an altitude: `NeedsVerticalSupersede` routes it through `ClearConflictingBlocks` with `CommandDimension.Vertical` so a
  pending `CM`/`DM` cannot later override the clearance it just issued. That exception is deliberately keyed on the command type rather than on
  "has a dimension" — pattern modifiers (`EXT`/`SA`/`MNA`) are transparent *and* classify as `All`, so a dimension-based rule would clear the very
  queued pattern entry they exist to modify. The dispatcher-local `IsPhaseTransparentCommand` (`CommandDispatcher.cs:1578`) is a **narrow** subset used by the phase gate to
  fall through to normal dispatch (apply via the handler without clearing the active phase). They are not interchangeable. The real hazard runs the
  other way: a "harmless" status verb that is **omitted** from the broad list but whose `GetCommandDimension` resolves to `None` falls through to
  normal dispatch, where `ClearConflictingBlocks`'s `All`/`None` fast path clears the **entire** pending queue — wiping a
  queued pattern entry whether or not a phase is active (the in-code comment at `CommandDispatcher.cs:84` documents this, citing N435C in S2-OAK-5).
  The **broad** list has a second job inside the gate: it is what `FindPhaseGateDriverIndex` uses to skip transparent siblings when picking the
  command the phase's `CanAcceptCommand` is asked about. Adding a status verb to only the narrow list therefore still lets it wrongly drive the gate
  when it leads a mixed parallel block.
- **A mixed parallel block gates on its interactive command, not its first.** `SQ, SQNORM, PUSH` is one block of three parallel commands. Because
  `PUSH` is not transparent, the block loses the `IsAllTransparent` fast path and reaches the gate — where the *driver* (`PUSH`), not the leading
  `SQ`, is checked against `CanAcceptCommand`, and the transparent siblings are applied via `ApplyParallelSibling`. Order within the block does not
  matter. Regression coverage: `PhaseTransparentCommandTests.ParallelBlock_*_AtParking_AppliesAll`. The `;`-sequenced form is handled one step
  earlier: `DispatchCompoundCore` **peels** a leading all-transparent block (`SQ; SQNORM; PUSH; …`), applies it immediately, and re-dispatches the
  remainder, so the gate is always driven by a block that contains a phase-interactive command (issue #407 — before the peel, a lone leading `SQ`
  fell through `FindPhaseGateDriverIndex`'s fallback, drove the gate, and `AtParkingPhase` rejected the whole compound). Unlike the atomic `,`
  block, a sequential head commits before a later block can fail. Regression coverage: `SequentialTransparentCompoundTests`.
- **Installing a phase has a lifecycle.** Build a fresh `PhaseList`, `Clear()` the old one with a `PhaseContext`, `Add` phases, then `Start()` with
  another `PhaseContext` (see `DispatchJfac`, `TryAirborneFollow` at `CommandDispatcher.cs:2387` — the install sequence is at `:2452`, `DispatchHoldingPattern`). Use
  `BuildMinimalContext` (`:1666`) to construct the `PhaseContext`. Skip the `Clear()`/`Start()` and you leave stale phase indices or unstarted phases.
- **`NotifyPhaseCommandAccepted` releases internal phase state only after a successful apply.** Its Unsupported/transparent/sim-control guards are
  load-bearing for the queued-block path even though they look redundant for immediate dispatch — the in-code comment warns against removing them.
- **APT/DEST has two dispatch routes.** A bare, unconditioned `DEST KOAK` is intercepted server-side in `RoomEngine.SendCommandAsync`,
  which calls `FlightPlanCommandHandler.TryChangeDestination` and then `AmendFlightPlan` for the immediate CRC/recording push. Scenario
  presets and conditional forms (`AT 5000 DEST KOAK`) bypass that intercept and queue a normal block, so `ApplyCommand` carries its own
  `ChangeDestinationCommand` arm; the sim-side mutation reaches CRC via the flight-plan change tracker on the next tick.
- **A queued `CommandBlock.ApplyAction` closure is NOT serialized — it is rehydrated on the next physics tick.**
  `CommandBlock.FromSnapshot` (`CommandQueue.cs`) persists only `SourceCommandText`.
  `SimulationEngine.RehydrateRestoredQueueBlocks` (top of `TickPhysics`, shared by the standalone sim/replay and the live server, before
  `World.Tick` can fire the queue) re-parses that text, matches the block's sub-block by its serialized `Description` (longest suffix match),
  and rebuilds `ParsedCommands` + `ApplyAction` via `CommandDispatcher.RehydrateRestoredBlock` with a fresh engine `DispatchContext`. A block
  that cannot be recovered (text no longer parses / no description match) is **dropped with an RPO warning** rather than left to fire as a
  silent no-op. Rehydration does not recover `DescriptionPrefix`/`NaturalDescriptionPrefix` (cosmetic: a post-restore supersede-split loses
  the "At FIXIE: " label, not the trigger). Long-lived deferred behavior still belongs on the aircraft (as the `REPORT` armed flags do on
  `AircraftApproachState`) — rehydration protects queued instructions, it doesn't make closures a durable store. Track commands are
  additionally re-dispatched by `ProcessTriggeredTrackBlocks` (see "Triggered re-dispatch" above).
- **`CTO`/`LUAW` take no runway argument — only `CROSS` does.** The server resolves the departure runway from `aircraft.Phases.AssignedRunway`
  (`DepartureClearanceHandler.TryClearedForTakeoff`); `ParseCtoArg`/`ParseLuawArg` *reject* a runway token (`"CTO does not understand '28R'"`). A
  runway is valid for `CTO` only as the optional 2nd token after `MLT`/`MRT` (`CTO MRT 28R`). Context-menu convention: show the runway in the
  **label** (`Cleared for takeoff {ToDisplayDesignator(rwy)}`) but send the **bare verb** — appending the runway the way `CROSS` menus do is the
  #229 bug that hit the Ground and aircraft-list menus. New Ground/DataGrid takeoff or line-up items pass `Cmd("CTO")`/`Cmd("LUAW")`, never a runway.
- **`CTO RH` is allowed for IFR; `CVIA` self-activates the filed SID.** `RunwayHeadingDeparture` is in `VfrCommandPolicy.IsVfrOnlyDeparture`'s allowed
  set — after `CTO RH` the aircraft holds runway heading with no SID loaded (`ActiveSidId` stays null). `CVIA` (`NavigationCommandHandler.DispatchClimbVia`)
  then self-activates the filed SID when `ActiveSidId is null` via `TryActivateFiledSid` + `OverlaySidRestrictions` — a mirror of the arrival-side
  `DVIA`/`TryActivateFiledStar`. Two-step rejoin: `DCT <SID fix>` reloads the lateral remainder, then `CVIA` overlays the published crossing
  restrictions. **Footgun:** `OverlaySidRestrictions` must read the persistent `Procedure.DepartureRunway`, not `Phases.AssignedRunway` — a `CVIA`
  issued mid-`InitialClimbPhase` ClearsPhase, so the dispatcher nulls `aircraft.Phases` before `DispatchClimbVia` runs (`AssignedRunway` gone), but
  the DCT-loaded `NavigationRoute` survives.
- **Runway lookups key off the physical/operational airport, not filed flight-plan fields.** `CommandDispatcher.ResolveRunway` (used by `RWY`,
  `TAXI … <rwy>`, and the CTO/LUAW hold-short resolution) derives the airport in physical-first order — `Phases.AssignedRunway.AirportId` →
  `AircraftState.AirportId` → `Ground.Layout.AirportId` → `FlightPlan.Departure` → `FlightPlan.Destination` (last resort). An aircraft on the
  ground departs on the airport its wheels are on, never on a filed destination; this mirrors `SimulationEngine.ResolveGroundLayout`. **Footgun:** do
  not reorder the flight-plan fields ahead of the physical airport — a VFR plan filed with only a destination (e.g. KAPC while parked at OAK) would
  then send every runway lookup to the wrong airport and reject `CTO`/`RWY`/`TAXI`-to-runway (`VfrDestinationOnlyRunwayResolutionTests`).
- **Runway *identity* stays zero-padded everywhere; de-pad only at display.** FAA drops the leading zero ("8R", "9") but the sim keys identity on
  the padded canonical ("08R", "09"). `RunwayIdentifier.NormalizeDesignator` pads (identity); `ToDisplayDesignator` strips (display; token-aware —
  handles "26L/08R", "RWY 08R", comma-joined queue text). Keep padded in `RunwayInfo.Designator`, `ClearedRunwayId`, command args, wire DTOs, and
  **all** comparisons/lookups. De-padding the stored `RunwayInfo.Designator` silently breaks `RunwayInfo.IsEnd1` (wrong threshold geometry),
  `NavigationCommandHandler.LookupRunwayTransition` (drops STAR transitions), `AirportGroundLayout.FindRunway`, `ApproachGateDatabase`, CIFP
  key-building (`"RW"+Designator`), and old-recording replay. `CommandDescriber.DescribeCommand` (canonical, shared with the CRC FP/STARS amendment
  code) is machine-facing — never de-pad it; de-pad at display sites (`PhraseologyVerbalizer.SpellRunway`, `AircraftStatusDescriber`, menu labels,
  `RunwayDisplayConverter`). Use `RunwayIdentifier.Contains` (end-exact) for hold-short matching, never `RunwayId.ToString().Contains(x)` (accepts
  the opposite end).
