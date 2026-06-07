# Command Pipeline

> Read this before touching `MainViewModel.SendCommandAsync`, `CommandSchemeParser`, `CommandDispatcher`, `RoomEngine.SendCommandAsync`, or any `*CommandHandler.cs`. Walks an example command end-to-end.

## Worked example: `UA H180 AT FIX1`

The instructor types `UA H180 AT FIX1`. By the end of the tick, `ControlTargets.TargetTrueHeading = 180` is queued behind a "reach FIX1" trigger. Here's every step.

### 1. Client input — `MainViewModel.SendCommandAsync`

(`src/Yaat.Client/ViewModels/MainViewModel.cs`)

- Macro expansion runs first (`MacroExpander.TryExpand`) so `#climb 5` could become `CM 5000`.
- The first whitespace-delimited token is the **partial callsign**. `CallsignPrefixResolver.Resolve` matches `"UA"` against `Aircraft.Callsign` (exact, then substring via `CallsignMatcher`); when multiple aircraft match, the status bar shows an ambiguity message listing the candidates and the command is not sent.
- Optional `**` override prefix bypasses assignment-ownership checks.
- `CallsignArgumentResolver.TryRewrite` rewrites partial callsigns inside arguments (e.g. `FOLLOW UA` → `FOLLOW UAL123`).
- Sends via SignalR: `SendCommand(callsign, command, initials)` — three strings, JSON-serialized.

The client does **not** canonicalize before sending. Parsing happens twice (once on the client for autocomplete and signature help, once on the server for execution); the server is authoritative.

### 2. Parsing — `CommandSchemeParser.ParseCompound`

(`src/Yaat.Sim/Commands/CommandSchemeParser.cs`)

Compound syntax:

| Operator | Meaning |
|---|---|
| `;` | sequential — next block runs after current block completes |
| `,` | parallel — blocks run concurrently (subject to dimension conflicts) |
| `LV <alt>` | block fires when aircraft passes that altitude |
| `AT <fix>` | block fires when aircraft sequences that fix |
| `ATFN`, `ONHO`, `GIVEWAY` … | other deferred triggers (see `BlockTrigger`) |

`H180 AT FIX1` parses to a single `ParsedBlock`:
- `Trigger = ReachFix("FIX1")`
- `Commands = [ FlightHeading(180) ]`

`ToCanonical()` normalizes aliases (`H` → `FH`, etc.) and produces a stable string used for recording and replay.

### 3. Hub — `TrainingHub.SendCommand`

(`yaat-server: src/Yaat.Server/Hubs/TrainingHub.cs`)

Resolves the connection's `RoomEngine`, opens a room scope, and delegates to `RoomEngine.SendCommandAsync(connectionId, callsign, command, initials)`.

### 4. RoomEngine routing — `RoomEngine.SendCommandAsync`

(`yaat-server: src/Yaat.Server/Simulation/RoomEngine.cs`)

This is where commands fan out to one of four paths. **Heading/altitude/speed/nav commands take the `HandleStandardCmd` path**; track and coordination commands bypass `CommandDispatcher` entirely.

```
SendCommandAsync(callsign, command, initials)
  ↓ ExtractAsPrefix → strips "AS <tcp>" position override, returns asOverrideTcp
  ↓ partial callsign resolution
  ↓ ParsedCommand sniff
  │
  ├─ TrackCommandHandler.IsTrackCommand   → HandleTrackCmd        (TRACK, DROP, HO, ACCEPT,
  │                                                                CANCEL HO, POINTOUT, AS,
  │                                                                scratchpad, temp alt, …)
  ├─ CoordinationCommandHandler.IsCoordinationCommand
  │                                       → HandleCoordinationCmd  (RD, RDH, RDR, RDACK, RDAUTO)
  ├─ Strip-mutation commands              → HandleStripCmd         (STRIP, AN, HSC/HSA/HSD, …)
  └─ otherwise                            → HandleStandardCmd
                                              ↓
                                          CommandDispatcher.DispatchCompound
```

Track commands go to `TrackEngine.Dispatch` (pure logic in `Yaat.Sim`) — `TrackCommandHandler` is a thin adapter. The replay applier (`ReplayTrackApplier`) shares this engine, so live and replay paths always agree.

After validation, every command is recorded for replay: `Record(new RecordedCommand(scenario.ElapsedSeconds, callsign, command, initials, connectionId) { ReactionDelaySeconds = … })` — the pilot-reaction delay, if any, is baked in so replays reproduce it exactly (see [Deferred dispatch](#deferred-dispatch--wait-behind-and-the-command-run-delay)). **Including rejected ones** — replay needs a faithful history, not a clean one.

### 5. CommandDispatcher.DispatchCompound

(`src/Yaat.Sim/Commands/CommandDispatcher.cs`)

`DispatchCompound(aircraft, compound, ctx)` is the entry point for non-track, non-coordination, non-strip commands. It first checks for a leading `WAIT`/`WAITD`/`BEHIND` and short-circuits to a **deferred dispatch** (see [Deferred dispatch](#deferred-dispatch--wait-behind-and-the-command-run-delay)); otherwise the big moves:

1. **Phase gate.** If `aircraft.Phases?.CurrentPhase` exists, route through `DispatchWithPhase`. The phase's `CanAcceptCommand` is consulted (see [phases.md](phases.md)). `Rejected` returns immediately. `ClearsPhase` defers clearing until validation passes.
2. **Dry-run validation.** The full compound is run on a clone of the aircraft (`DryRunValidate`). If any block fails (e.g. unknown fix, illegal intercept), the user gets the error and **state is unchanged**.
3. **Additive vs. supersede.** `IsConditionalIncoming` checks whether the incoming compound's first block carries a precondition (`AT`/`LV`/`ATFN`/`ONHO`/`ONHS`/…; leading `WAIT`/`BEHIND` were already siphoned to deferred dispatch). A **conditional** incoming command is purely additive — it skips both queue clearing and `DeferredDispatches.Clear()`, appending its triggered block so sibling conditionals and pending WAIT/BEHIND deferrals survive. Only a **fresh immediate** command supersedes.
4. **Dimension-aware queue clearing** (immediate commands only). New blocks declare which dimensions they touch (`Lateral | Vertical | Speed`). `ClearConflictingBlocks` removes queued blocks whose dimensions overlap; non-conflicting blocks survive. Mixed-dimension blocks may be split via `SplitBlockNonConflicting`. When a **deferred dispatch fires** its payload (`ctx.PreserveConditionals`, set only by `SimulationEngine.ProcessDeferredDispatches`), clearing runs with `preserveTriggeredBlocks: true`: the firing payload still supersedes conflicting *untriggered* work but keeps every triggered conditional — so a WAIT-deferred taxi clearance executing does not wipe the departure's queued `ONHO`/`AT` airborne instructions.
5. **Apply or enqueue.** Blocks with no trigger apply immediately via `ApplyCommand`. Blocks with a trigger (LV, AT, …) are wrapped in `CommandBlock` and pushed onto `aircraft.CommandQueue`.

> **The conditional list.** `ConditionalList` (`src/Yaat.Sim/Commands/ConditionalList.cs`) enumerates an aircraft's pending precondition-gated work as one unified list — pending `CommandQueue` trigger blocks **plus** `DeferredDispatch`es (WAIT/WAITD/BEHIND, excluding internal reaction-delay timers) — with shared numbering. It backs `SHOWAT`/`SHOWCOND`, the "Pending Cmds" column (`DtoConverter.BuildPendingCommands`, stable text so the change-tracker fingerprint doesn't churn), and `DELAT`/`DELCOND`/`DC` deletion (`ConditionalList.Delete`, mirrored by `SimulationEngine.ReplayDeleteQueued`). Two intentional simplifications (per aviation review, 7110.65 §4-2-5): additive coexistence assumes conditionals are on independent control axes — same-axis stacking won't auto-resolve to last-wins; and a fresh immediate command clears *all* pending conditionals, where strict amended-clearance rules would only amend the matching axis.

`ApplyCommand` is a thin routing switch over command type → `FlightCommandHandler`, `NavigationCommandHandler`, `ApproachCommandHandler`, `DepartureClearanceHandler`, `GroundCommandHandler`, `PatternCommandHandler`, `FlightPlanCommandHandler`, etc. See `Commands/CommandRegistry.cs` for the complete enum. For what happens *inside* the dispatcher and each handler — the two switch surfaces (`ApplyCommand` vs `TryApplyTowerCommand`), the handler read/write contract, and the per-domain effect cheat-sheet — see [command-handlers.md](command-handlers.md).

**Flight-plan commands (VP / FP / DA) canonicalize their inputs.** `FlightPlanCommandHandler` splits `C172/G` into `AircraftType` + `EquipmentSuffix`, canonicalizes departure/destination via `NavigationDatabase.TryResolveAirport` (rejecting unknown airports), and treats a single-token route as destination-only (`VP C172 5500 MOD` → `Destination=KMOD`, `Departure=null`). On the server, `RoomEngine.RecordAndDispatchFlightPlanAsync` spawns an unsupported track before dispatching the handler and rolls that spawn back on handler failure, gated on a `spawnedUnsupported` flag so a DUP-NEW-ID collision with a pre-existing aircraft doesn't delete it.

### 6. CommandQueue & triggers — `CommandQueue.cs`

A queued `CommandBlock` carries:

- `BlockTrigger` — `ReachAltitude`, `ReachFix`, `InterceptRadial`, `OnHandoff`, `GiveWay`, `AtGroundEntity`, …
- `Commands` — `TrackedCommand[]` (the actual heading/altitude/speed payloads).
- `Dimensions` — which axes this block touches, for selective clearing.
- `ApplyAction` — closure that runs when the trigger fires.
- `SourceCommandText` — the canonical compound string (snapshot/replay support).

Each tick, step 9 of `FlightPhysics.Update` (`UpdateCommandQueue`) checks the current block's trigger. When met, the closure runs. `ReadyToAdvance` gates lateral changes until they're complete; altitude/speed continue in parallel when paired with lateral work. While the current block is still running, `ApplyReadyConditionalBlocks` scans the contiguous conditional blocks behind it so `AT`, `LV`, `ATFN`, radial/FRD, and handoff triggers can fire without waiting for the current target to complete. Active phases still skip ordinary queue advancement, but run the same triggered-block scan; fix and ground triggers can also fire through `NotifyFixSequenced` and `NotifyGroundEntityReached`.

### 7. Effect on the aircraft

Handlers don't move aircraft directly — they write to `ControlTargets` (the autopilot panel):

- `FlightCommandHandler.HandleHeading(180)` → `ac.Targets.TargetTrueHeading = 180`.
- `FlightCommandHandler.HandleAltitude(...)` → `ac.Targets.TargetAltitude = …`.
- `NavigationCommandHandler.HandleDirectTo(fix)` → updates `ac.Targets.NavigationRoute`.

**Navigation route supersession:** When a controller instruction replaces routing context, stale procedure fixes are removed from `NavigationRoute` rather than left appended:

- **EAPP / RWY (arrival)** — `ExtendActiveStarWithRunwayTransition` drops fixes exclusive to other STAR runway transitions before appending the new transition.
- **Deferred CAPP** — a second clearance replaces the approach tail after the STAR connecting fix (not `InsertRange` on top of the old tail).
- **Immediate CAPP / JAPP / JFAC / PTAC** — `ClearExistingPhases` clears `PendingClearance` so an old deferred clearance cannot activate when the route empties; **JFAC** also clears the queued route.
- **DCT on active STAR** — `TryPreserveProcedure` truncates before the fix, then scrubs other-runway-transition fixes when `DestinationRunway` is set.
- **APT (destination change)** — `ClearArrivalProcedureState` clears STAR, pending approach, expected approach, and the live route when the airport changes.

`FlightPhysics.Update` reads `ControlTargets` next tick and turns/climbs/accelerates accordingly. See [tick-loop.md](tick-loop.md).

## `DispatchContext` — bundled call-site state

(`src/Yaat.Sim/Commands/DispatchContext.cs`)

Threaded through `DispatchCompound` and every handler. Holds:

| Field | Purpose |
|---|---|
| `GroundLayout` | taxiway graph for ground commands |
| `Rng` | deterministic RNG (snapshotable) |
| `Weather` | wind/visibility for visual detection (RTIS/RFIS) |
| `FindAircraft` | callsign → `AircraftState?` lookup for relative commands |
| `ValidateDctFixes` | strict mode for direct-to off-route fixes |
| `AutoCrossRunway` | whether to auto-issue runway crossing clearances |
| `SoloTrainingMode`, `RpoShowPilotSpeech` | scenario flags |
| `TerminalEmitter` | broadcasts SAY-class verbs to the terminal log; **null in dry-run / tests**, otherwise SAYs would fire twice |
| `PreserveConditionals` | true **only** when a deferred dispatch fires its payload (`SimulationEngine.ProcessDeferredDispatches`); makes that payload preserve pending triggered conditionals + sibling deferrals instead of superseding them. Every other call site passes false |

Adding a new contextual flag to handlers? Add it to `DispatchContext`, set it at the call sites in `SimulationEngine` / `RoomEngine`, and read from `ctx`. Don't pass it as a parameter — the bundle exists to avoid signature creep.

## Track command bypass — `TrackCommandHandler` / `TrackEngine`

`TRACK`, `DROP`, `HO`, `ACCEPT`, `CANCEL HO`, `POINTOUT`, scratchpad, temp alt, cruise, `AS <tcp> …` — these change ownership and STARS-track metadata, not flight controls. They go through `TrackEngine.Dispatch` directly (pure-domain logic in `Yaat.Sim`), not `CommandDispatcher`. `ReplayTrackApplier` uses the same `TrackEngine` so live and replay agree.

## Coordination command bypass — `CoordinationCommandHandler`

`RD`, `RDH`, `RDR`, `RDACK`, `RDAUTO` — STARS coordination items between TCPs. Channels are resolved from ARTCC config; items auto-expire 5 min after ack.

## Deferred dispatch — WAIT, BEHIND, and the command-run delay

(`DeferredDispatch` in `src/Yaat.Sim/CommandQueue.cs`; ticked by `SimulationEngine.ProcessDeferredDispatches`)

Distinct from the CommandQueue (§6): a queued `CommandBlock` holds *part* of an already-dispatched compound behind a trigger and writes `ControlTargets` when the trigger fires. A `DeferredDispatch` instead holds the **entire un-dispatched compound** and re-runs it through `DispatchCompound` from scratch when its timer/condition expires — phases, queue clearing, and validation all happen fresh at fire time, not at issue time. Each aircraft owns a `DeferredDispatches` list (snapshot-serialized).

Three things create a deferred dispatch:

1. **`TryDeferLeadingWait`** (inside `DispatchCompound`, before the phase gate) — a leading `WAIT n` (seconds) or `WAITD nm` (flying miles). The WAIT is stripped; the remaining blocks become the payload.
2. **`TryDeferGiveWay`** (inside `DispatchCompound`) — a leading `BEHIND <callsign>` give-way condition. The payload dispatches once the named aircraft has passed.
3. **Command-run delay** — `SimulationEngine.TryDeferCommandForReaction`, called from `SendCommand` / `RoomEngine.HandleStandardCmd` *around* `DispatchCompound` (not inside it). The configurable pilot-reaction delay (issue #180): when active, the whole compound is deferred a sampled `[min,max]` seconds; the controller gets an immediate "Pilot complying in Ns" acknowledgement and the aircraft acts when the timer expires. In **solo training mode** that acknowledgement is suppressed (empty `CommandResult` message → no terminal `Response`) so the student can't read off the exact sampled delay — the pilot's read-back is the acknowledgement instead.

`ProcessDeferredDispatches` (a per-tick step) ticks every aircraft's `DeferredDispatches` each 0.25 s sub-tick — decrementing seconds, accumulating distance, or evaluating the give-way condition — and re-dispatches the payload through `DispatchCompound` on expiry. WAIT/BEHIND/distance expiries emit a `[Deferred] … →` terminal line; reaction delays fire silently (the controller already saw the acknowledgement at issue time).

### Command-run delay specifics

- **Scope.** Applies to anything reaching the standard dispatch path: flight/nav/approach/hold/ground plus squawk/ident/say. Track/coordination/strip commands are routed away earlier (see above) and never reach it. *Pure* frequency-change/contact compounds (`ContactCommand` / `FrequencyChangeApprovedCommand` / `AcknowledgePilotContactCommand`) are exempt — AIM 4-2-3 expects a pilot to switch frequency ASAP — while a mixed compound (`FH 270; CON TWR`) is delayed as a whole. Commands carrying explicit timing (leading `WAIT`/`WAITD`/`BEHIND`) are not additionally reaction-delayed.
- **Replay determinism.** Live sampling draws from a *dedicated* `SimulationWorld.ReactionDelayRng`, never the shared `World.Rng`, so it can't perturb the RNG sequence driving emergent events (go-arounds, generator spawns). The sampled value is baked into `RecordedCommand.ReactionDelaySeconds`; `SimulationEngine.ReplayCommand` recreates the deferral from that recorded value and never re-samples — re-sampling would draw from a divergent RNG state and break determinism.
- **Issue order.** `TryDeferCommandForReaction` clamps each new reaction delay so it fires no sooner than any already-pending one, and `ProcessDeferredDispatches` applies same-tick expiries FIFO — so two rapid commands always take effect in the order issued.

### The clears-on-supersede invariant

`DispatchCompound` calls `aircraft.DeferredDispatches.Clear()` so a **new** controller command cancels pending WAITs (the new instruction supersedes). A deferred **re-dispatch** must *not* cancel its siblings, so `ProcessDeferredDispatches` detaches the surviving (not-yet-ready) deferrals across the dispatch and restores them afterward. Without this, two stacked reaction-delayed (or WAIT) commands would wipe each other when the first fires.

## Pitfalls

- **Heading/altitude/speed are NOT track commands.** They take `HandleStandardCmd` → `CommandDispatcher`. Track commands are STARS ownership ops. Easy to confuse because both involve callsigns.
- **Two parsers, one truth.** Client parses for autocomplete; server parses for execution. The server is authoritative — don't trust client-side parse results for behavior.
- **Records include rejects.** `RecordedCommand` is logged before validation. If you "fix" replay drift by skipping rejects, replay diverges from the original session.
- **Dry-run uses a clone — make handlers idempotent on a clone.** Anything `ApplyCommand` does must work on a snapshot copy without affecting the live aircraft. If a handler writes to non-cloned state (a singleton, a sibling aircraft), dry-run will leak.
- **`TerminalEmitter` must be nulled in dry-run.** SAY-class verbs broadcast via `ctx.TerminalEmitter`; if dry-run forgets to null it, SAYs fire twice. See the `project_dispatch_context_terminal_emitter` memory.
- **Phase clearing is post-validation.** `ClearsPhase` does not immediately clear — validation runs first on a clone, then the phase is cleared, then commands apply. This protects against half-applied compound commands.
- **Dimension-aware clearing isn't all-or-nothing.** A new heading command clears queued lateral blocks but leaves a queued altitude block alone. If you find yourself adding `aircraft.CommandQueue.Clear()` to a handler, you're probably bypassing this design.
- **`SimulationWorld.AddAircraft` is replacement-safe.** It drops any same-callsign entry (case-insensitive) before appending and logs a warning. Spawn wins over a pre-existing user-typed VP/DA ghost — don't add per-call-site dedup. A logged replacement is expected when a scenario spawn collides with a ghost; two scenario spawn paths firing for one callsign is a bug.
- **A deferred re-dispatch must not cancel sibling deferrals.** `DispatchCompound` clears `DeferredDispatches` to supersede pending WAITs on a *new* command, so `ProcessDeferredDispatches` detaches and restores the survivors around a re-dispatch. If you rework deferred dispatch, keep that — otherwise a firing WAIT or command-run delay silently wipes the others. See [Deferred dispatch](#deferred-dispatch--wait-behind-and-the-command-run-delay).
