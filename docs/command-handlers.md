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
4. **Phase gate** — if `aircraft.Phases?.CurrentPhase` exists, route through `DispatchWithPhase` (`:1172`). See [the phase gate](#the-phase-gate).
5. **Dry-run validation** — `DryRunValidate` (`:812`) runs the first block on a clone. If it fails, return the error; **real state is unchanged**.
6. **Post-validation phase clear** — only now (after dry-run passes) does the deferred `ClearsPhase` actually clear the `PhaseList`
   (`CommandDispatcher.cs:176`).
7. **Dimension-aware queue clearing** — `ClearConflictingBlocks` (`:1798`) removes queued blocks whose dimensions overlap the incoming command;
   non-conflicting blocks survive and are re-appended.
8. **Enqueue + apply first block** — `EnqueueBlocks` (`:1985`) appends the new blocks; the first new block with no trigger is applied immediately via
   `ApplyBlock` (`:1757`). Triggered blocks wait for the physics tick.

## The phase gate

`DispatchWithPhase` (`CommandDispatcher.cs:1172`) decides what an active phase does with the first command:

1. **Conditional leading block** (`AT FIX` / `LV alt` / distance-final / on-handoff / ground-entity) → return `null` so the compound takes the normal
   `DryRunValidate` + `EnqueueBlocks` path and the block waits for its `BlockTrigger`. The active phase must not be torn down by a block that hasn't
   fired yet (`:1182`).
2. **`UnsupportedCommand`** → hard reject (`:1193`). Never let an unsupported verb interact with phases — it used to map to `FlyHeading` and destroy
   pattern state.
3. **Phase-transparent command** → return `null` (`:1203`). `IsPhaseTransparentCommand` (`CommandDispatcher.cs:1321`) is a **narrow** dispatcher-local
   list — RFIS/RTIS and their forced variants, `SafetyAlert`, `WakeAdvisory`, and `CancelAutoDelete` (NODEL). These are pure status setters that must
   never clear a phase; routing them through normal dispatch lets `NavigationCommandHandler` apply them without disturbing the phase.
4. **Sim-control bypass** → return `null` (`:1212`). `IsSimControlBypass` (`:1337`) is just `Warp` / `WarpGround` — destructive teleports that wipe
   phase/queue/route *inside the handler*, so the gate has nothing to protect.
5. **Tower command** → `TryApplyTowerCommand` first (`:1218`). If it returns non-null, the result is used (and any parallel sibling commands in the
   same block are dispatched too). This is how `EF 28L, CLAND` applies both clauses.
6. Otherwise consult the phase's acceptance verdict (`:1264`):
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
| **Pattern** | `PatternCommandHandler` | `TryEnterPattern`, pattern direction/turn/extend/size/offset/S-turn mods, option ops (T&G/S&G/low-approach/option), hold-orbit/hover, CLAND/LAHSO/CLC/GA. Builds/mutates pattern `PhaseList`. **VFR-gated** via `RequiresVfr` (`CommandDispatcher.cs:359`). |
| **Departure** | `DepartureClearanceHandler` | CTO/LUAW/CTOC state machine. `TryDepartureClearance` (`:90`) branches on current phase: `HoldingShort` / `Taxiing` (stores clearance for later) / `LineUp` / `HoldingInPosition`. Installs `LineUp → [LinedUpAndWaiting] → Takeoff → InitialClimb` tower phases; stores `Phases.DepartureClearance`. **IFR-gated** via `CheckIfrDepartureCompatibility`. |
| **Ground** | `GroundCommandHandler` | Taxi/pushback/hold-short/cross/exit/follow/give-way/break/go. The routing methods (`TryTaxi`, `TryTaxiAuto`, `TryPushback`, `TryHoldShort`, `TryFollow`, `TryAirTaxi`, `TryLand`, `TryAddExplicitHoldShorts`) take an `AirportGroundLayout? groundLayout`; the rest (`TryAssignRunway`, `TryHoldPosition`, `TryResumeTaxi`, `TryCrossRunway`, `TryGiveWay`, `TryBreakConflict`, `TryGo`, `TryExitCommand`) don't. On the phase path the layout is `ctx.GroundLayout` (see `TryApplyTowerCommand:1347`); on the `ApplyCommand` path (AirTaxi/Land/CTOPP) it is `aircraft.Ground.Layout`. Installs/mutates ground `PhaseList`. |
| **Contact** | `ContactCommandHandler` | CT / FCA — pure pilot-speech, **no flight-control mutation**. Resolves a target position to a frequency via `ctx.ArtccConfig`, queues a pilot readback, sets `aircraft.HasLeftStudentFrequency = true`, and stamps `CompletedAtSeconds`/`CompletionReason = HandedOff` on the first CT/FCA. |
| **Flight plan** | `FlightPlanCommandHandler` | `TryChangeDestination` (APT/DEST) only — canonicalizes the airport via `NavigationDatabase.TryResolveAirport`, rejects unknowns, and clears arrival-procedure state when the destination actually changes. **Dispatched from `RoomEngine` (`RoomEngine.cs:563`), not from `ApplyCommand`.** |

## VFR / IFR gating lives in the dispatcher

Both switch surfaces check two gates at the top before dispatching (`ApplyCommand:435`, `TryApplyTowerCommand:1349`):

- `RequiresVfr(command)` (`CommandDispatcher.cs:359`) — the long list of pattern/option/hold verbs. If the command is on the list and the aircraft is
  IFR, return `VfrRequiredResult` ("Command requires VFR aircraft. Use CIFR to cancel IFR flight plan").
- `CheckIfrDepartureCompatibility(command, aircraft)` (`:411`) — an IFR aircraft may receive only a bare `CTO` (follow SID), `CTO` with an assigned
  numeric heading, or present-position hover. Pattern-relative CTO modifiers (MRC, ML*, RH, OC, DCT, MLT/MRT) are VFR-only and rejected so an IFR
  departure doesn't peel off runway heading at liftoff.

> A new pattern-ish verb must be added to `RequiresVfr` or it will silently be accepted for IFR traffic.

## Dimension classification

`ClearConflictingBlocks` (`CommandDispatcher.cs:1798`) is dimension-aware. Each command declares which axes it touches via
`CommandDescriber.GetCommandDimension` (`CommandDescriber.cs:274`): `Lateral`, `Vertical`, `Speed`, `All`, or `None`. Tower and ground commands are
`All`; ClimbVia/DescendVia and CrossFix/DepartFix are `Lateral | Vertical`; holds are `Lateral`. A new heading command clears queued lateral blocks
but leaves a queued altitude block alone; mixed-dimension blocks are split via `SplitBlockNonConflicting` (`:1923`).

> Never call `aircraft.Queue.Clear()` / `aircraft.CommandQueue.Clear()` inside a handler to "reset" state — that defeats parallel-block survival.

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
have no arm in `ApplyCommand`, so a triggered `AT FIX HO 2B` would hit the no-dispatcher-arm fallback. `EnqueueBlocks`
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
| `FindAircraft` / `ListAircraft` | RTIS/FOLLOW relative-traffic lookups; GiveWay target validation (nullable). |
| `ValidateDctFixes` | `ApplyDirectTo` strict off-route fix check; forced off in transparent + dry-run paths. |
| `AutoCrossRunway` | `TryTaxi` / `TryTaxiAuto` auto-crossing-clearance behavior; forced off in dry-run. |
| `SoloTrainingMode` / `RpoShowPilotSpeech` | CT/FCA pilot-speech routing. |
| `TerminalEmitter` | SAY-class verbs broadcast through it; **nulled in dry-run / parser tests** so SAYs don't fire twice. |
| `ArtccConfig` | `ContactCommandHandler` target → frequency resolution (nullable). |
| `ScenarioElapsedSeconds` | CT/FCA handoff-completion stamp. |

All fields are positional and required so a future addition breaks at the compiler, not silently at runtime. **Add a new contextual flag here and set
it at the `SimulationEngine` / `RoomEngine` call sites — never pass it as a handler parameter.** The bundle exists to avoid signature creep.

## Adding a new command's effect

Enum + registry + scheme + parser are covered in `architecture.md`. Inside the dispatcher:

1. **Pick the switch arm.** Phase-interactive tower/ground verb → `TryApplyTowerCommand` (and `ApplyCommand` too if it can arrive without a phase or
   be queued/triggered). Plain airborne/nav/flight verb → `ApplyCommand`.
2. **Write the handler.** Read `AircraftState`, write `Targets.*` / `Procedure.*` or install a `PhaseList` (via `BuildMinimalContext` for the
   `PhaseContext`), return `CommandResult`. Keep it clone-safe.
3. **Classify the dimension** in `CommandDescriber.GetCommandDimension` (and `ClassifyCommand` for the `TrackedCommandType`) so dimension-aware queue
   clearing works.
4. **Gate VFR/IFR** if applicable: add pattern/option verbs to `RequiresVfr`; add departure-clearance modifiers to `CheckIfrDepartureCompatibility`.
5. **Wire phase acceptance** — give the relevant phase a `CanAcceptCommand` arm (`Allowed` / `Rejected` / `ClearsPhase`, see [phases.md](phases.md)).
   A pure status verb that must never clear a phase goes in `CommandDescriber.IsPhaseTransparent` (broad list, fast path) and/or the dispatcher-local
   `IsPhaseTransparentCommand` (narrow list, phase gate).
6. **Verify the triggered path** — if the verb can be queued behind a trigger (`AT FIX` / `LV alt`), confirm `BuildApplyAction` re-dispatches it
   correctly (tower verbs need a `TryApplyTowerCommand` arm to avoid the no-dispatcher-arm fallback).
7. **Give it display names** — add an arm for the new `ParsedCommand` in **both** `CommandDescriber.DescribeCommand` (canonical short form) and
   `CommandDescriber.DescribeNatural` (user-friendly text). Without them the command falls through to the record's `ToString()` and leaks raw text
   like `"DeleteCommand { }"` into every queued-block description, the RPO ack, `SHOWAT`/`DELAT`, and the client "Pending Cmds" column (issue #226).
   `CommandDescriberCompletenessTests` enforces both switches cover every subtype, so a missing arm fails the build.

## Footguns / Pitfalls

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
- **Handlers write `ControlTargets`, never position — except Force\*.** `ApplyForceHeading`/`ApplyForceAltitude`/`ApplyForceSpeed`/WARP teleport by
  writing `aircraft.TrueHeading`/`Altitude`/`Position` directly. They are sim-control bypasses that skip the phase gate because they wipe
  phase/queue/route inside the handler.
- **VFR/IFR gating lives in the dispatcher, not the handlers.** `RequiresVfr` rejects pattern/option verbs for IFR aircraft;
  `CheckIfrDepartureCompatibility` rejects pattern-relative CTO modifiers for IFR departures. A new pattern-ish verb omitted from `RequiresVfr` is
  silently accepted for IFR traffic.
- **Two different "transparent" lists.** `CommandDescriber.IsPhaseTransparent` (`CommandDescriber.cs:948`) is the **broad** list used by the
  `IsAllTransparent` fast path (squawk, ident, say, RFIS/RTIS, NODEL, CT/FCA, expedite, …). A verb on this list is applied directly by
  `ApplyTransparentCompound` (`CommandDispatcher.cs:297`), which **skips `ClearConflictingBlocks` entirely** — so it neither consults phases nor
  wipes the queue. The dispatcher-local `IsPhaseTransparentCommand` (`CommandDispatcher.cs:1321`) is a **narrow** subset used by the phase gate to
  fall through to normal dispatch (apply via the handler without clearing the active phase). They are not interchangeable. The real hazard runs the
  other way: a "harmless" status verb that is **omitted** from the broad list but whose `GetCommandDimension` resolves to `None` falls through to
  normal dispatch, where `ClearConflictingBlocks`'s `All`/`None` fast path (`CommandDispatcher.cs:1809`) clears the **entire** pending queue — wiping a
  queued pattern entry whether or not a phase is active (the in-code comment at `CommandDispatcher.cs:73` documents this, citing N435C in S2-OAK-5).
- **Installing a phase has a lifecycle.** Build a fresh `PhaseList`, `Clear()` the old one with a `PhaseContext`, `Add` phases, then `Start()` with
  another `PhaseContext` (see `DispatchJfac`, `TryAirborneFollow` at `CommandDispatcher.cs:2387` — the install sequence is at `:2452`, `DispatchHoldingPattern`). Use
  `BuildMinimalContext` (`:1666`) to construct the `PhaseContext`. Skip the `Clear()`/`Start()` and you leave stale phase indices or unstarted phases.
- **`NotifyPhaseCommandAccepted` releases internal phase state only after a successful apply.** Its Unsupported/transparent/sim-control guards are
  load-bearing for the queued-block path even though they look redundant for immediate dispatch — the in-code comment warns against removing them.
- **APT/DEST is not in `ApplyCommand`.** `ChangeDestinationCommand` is dispatched server-side in `RoomEngine.cs:563`, calling
  `FlightPlanCommandHandler.TryChangeDestination` directly. Don't expect to find it in either dispatcher switch.
