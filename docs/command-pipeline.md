# Command Pipeline

> Read this before touching `MainViewModel.SendCommandAsync`, `CommandSchemeParser`, `CommandDispatcher`, `RoomEngine.SendCommandAsync`, or any `*CommandHandler.cs`. Walks an example command end-to-end.

## Worked example: `UA H180 AT FIX1`

The instructor types `UA H180 AT FIX1`. By the end of the tick, `ControlTargets.TargetTrueHeading = 180` is queued behind a "reach FIX1" trigger. Here's every step.

### 1. Client input — `MainViewModel.SendCommandAsync`

(`src/Yaat.Client/ViewModels/MainViewModel.cs`)

- Input starting with `.` (or the `CRC ` force-alias prefix) never enters this pipeline — it is a client-local dot command: YAAT's scope markers first, then CRC aliases. See [client-mainviewmodel.md](client-mainviewmodel.md) § `SendCommandAsync`.
- Macro expansion runs first (`MacroExpander.TryExpand`) so `#climb 5` could become `CM 5000`.
- The first whitespace-delimited token is the **partial callsign**. `CallsignPrefixResolver.Resolve` matches `"UA"` against `Aircraft.Callsign` (exact, then substring via `CallsignMatcher`); when multiple aircraft match, the status bar shows an ambiguity message listing the candidates and the command is not sent. A leading token that is a known command verb (e.g. `CM`) is never treated as a partial callsign — only an exact callsign match overrides it — so `CM 020` is a climb/maintain for the selected aircraft even when live callsigns like `CMD2` contain the substring.
- Optional `**` override prefix bypasses assignment-ownership checks.
- `CallsignArgumentResolver.TryRewrite` rewrites partial callsigns inside arguments (e.g. `FOLLOW UA` → `FOLLOW UAL123`).
- Sends via SignalR: `SendCommand(callsign, command, initials)` — three strings, JSON-serialized.

The client **does** canonicalize before sending: `SendCommandAsync` runs `CommandSchemeParser.ParseCompound` and transmits `compound.CanonicalString`, not the raw input. The server re-parses that canonical string with `CommandParser.ParseCompound` and is authoritative for execution — but any shape the client canonicalizer produces is what the server sees, so canonicalizer bugs (e.g. issue #335, where a condition-led `WAIT` compound was split into a top-level unconditioned payload block) change runtime behavior on the interactive path even when the direct server parse is correct.

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

This is a long `else if (simpleParsed is XCommand)` chain ending in a `HandleStandardCmd` fallback. **Heading/altitude/speed/nav commands take the `HandleStandardCmd` path**; every branch above it bypasses `CommandDispatcher` entirely and mutates room or aircraft state directly.

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
  ├─ Room/session-state commands          → per-command inline handlers
  │                                          PAUSE, UNPAUSE, SIMRATE, DELETE, SPAWN, SPAWNDELAY,
  │                                          HFR, HFROFF, REL, CFR, TIMER, BM, CON/DECON, TAXIALL,
  │                                          SQAWKALL, ACCEPTALL, DA/VP, RMK, NOTE, DEST, …
  └─ otherwise                            → HandleStandardCmd
                                              ↓
                                          CommandDispatcher.DispatchCompound
```

Adding a new room-state verb means adding a branch to this chain — check the exclusion list on the
`Record(...)` call below before you do, since a few of these verbs deliberately stay out of the action log.

Track commands take a separate path (see **Track command bypass** below): the **live** server switch (`TrackCommandHandler.HandleTrackCommand`) and the **replay** switch (`TrackEngine.Dispatch`) are two parallel dispatch tables that share only the `TrackEngine.Handle*` leaf logic — not a single adapter.

After validation, every command is recorded for replay: `Record(new RecordedCommand(scenario.ElapsedSeconds, callsign, command, initials, connectionId) { ReactionDelaySeconds = … })` — the pilot-reaction delay, if any, is baked in so replays reproduce it exactly (see [Deferred dispatch](#deferred-dispatch--wait-behind-and-the-command-run-delay)), as are the `REL` spawn jitter (`SpawnJitterSeconds`) and the aircraft an `ADD` generated (`SpawnedAircraft` — the record a replay holds its own derivation against; see [snapshots-and-replay.md](snapshots-and-replay.md)). Only **successful** commands are recorded (`if (result.Success && …) Record(...)`); pause/unpause/sim-rate/CFR/bookmark verbs are excluded.

The connection id also says **who** issued the command: an AI-controller position dispatches under
`AiConnectionId.Format(positionId)` (`"AI:{positionId}"`, `Commands/DispatchOrigin.cs`), and `HandleStandardCmd` derives
`DispatchOrigin.ControllerAi` from it — the dispatch runs with `IsScenarioScripted: true` (not the student establishing
contact) and `ApplyPostDispatch` skips two-way-comms registration and evaluator scoring. Because the origin is derived
from the recorded connection id, a reconstruction or tape playback (the router's `Apply` under the server's `RoomHost`) replays an AI command exactly as it ran live.

`RecordingPolicy.Never` keeps two kinds out of the action log — `PAUSE`/`UNPAUSE`/`SIMRATE` (transport state, not simulation state) and `BM` (bookmarks are timeline-global metadata that the rewind paths carry over verbatim, so replaying an add would duplicate every bookmark on each rewind) — and the router never applies either kind from a record, so the legacy records older recordings carry stay inert.

### 5. CommandDispatcher.DispatchCompound

(`src/Yaat.Sim/Commands/CommandDispatcher.cs`)

`DispatchCompound(aircraft, compound, ctx)` is the entry point for non-track, non-coordination, non-strip commands. A live-traffic shadow (`aircraft.IsShadow`) is rejected right here — and in `Dispatch` — before anything else, so even phase-transparent commands cannot reach it (see [live-traffic.md](live-traffic.md)). It then checks for a leading `WAIT`/`WAITD`/`BEHIND` and short-circuits to a **deferred dispatch** (see [Deferred dispatch](#deferred-dispatch--wait-behind-and-the-command-run-delay)); otherwise the big moves:

1. **Transparent-block peel.** A `;`-sequenced compound led by an all-transparent block (`SQ; SQNORM; PUSH; …`) peels the head block off, applies it immediately via `ApplyTransparentCompound`, and re-dispatches the remainder fresh — so the phase gate is driven by the first block that actually contains a phase-interactive command, not by a lone squawk that a restrictive phase (AtParkingPhase) would reject (issue #407). Unlike a `,` block (atomic, gated before anything applies), a sequential head may commit before a later block fails.
2. **Phase gate.** If `aircraft.Phases?.CurrentPhase` exists, route through `DispatchWithPhase`. The phase's `CanAcceptCommand` is consulted (see [phases.md](phases.md)). `Rejected` returns immediately. `ClearsPhase` defers clearing until validation passes.
3. **Dry-run validation.** Only the **first block** — and only when it is unconditioned — is run on a clone of the aircraft (`DryRunValidate`). If it fails (e.g. unknown fix, illegal intercept), the user gets the error and **state is unchanged**. Later blocks (and anything behind a condition) are validated only when they fire; a fire-time failure discards the rest of that compound's chain and warns — see [command-chaining.md](command-chaining.md#abort-on-fire-time-failure).
4. **Additive vs. supersede.** `IsConditionalIncoming` checks whether the incoming compound's first block carries a precondition (`AT`/`LV`/`ATFN`/`ONHO`/`ONHS`/…; leading `WAIT`/`BEHIND` were already siphoned to deferred dispatch). A **conditional** incoming command is purely additive — it skips both queue clearing and `DeferredDispatches.Clear()`, appending its triggered block so sibling conditionals and pending WAIT/BEHIND deferrals survive. Only a **fresh immediate** command supersedes.
5. **Dimension-aware queue clearing** (immediate commands only). New blocks declare which dimensions they touch (`Lateral | Vertical | Speed`). `ClearConflictingBlocks` removes queued blocks whose dimensions overlap; non-conflicting blocks survive. Mixed-dimension blocks may be split via `SplitBlockNonConflicting`. When a **deferred dispatch fires** its payload (`ctx.PreserveConditionals`, set only by `SimulationEngine.ProcessDeferredDispatches`), clearing runs with `preserveTriggeredBlocks: true`: the firing payload still supersedes conflicting *untriggered* work but keeps every triggered conditional — so a WAIT-deferred taxi clearance executing does not wipe the departure's queued `ONHO`/`AT` airborne instructions.
6. **Apply or enqueue.** Blocks with no trigger apply immediately via `ApplyCommand`. Blocks with a trigger (LV, AT, …) are wrapped in `CommandBlock` and pushed onto `aircraft.CommandQueue`.

> **The conditional list.** `ConditionalList` (`src/Yaat.Sim/Commands/ConditionalList.cs`) enumerates an aircraft's pending precondition-gated work as one unified list — pending `CommandQueue` trigger blocks **plus** `DeferredDispatch`es (WAIT/WAITD/BEHIND, excluding internal reaction-delay timers) — with shared numbering. It backs `SHOWAT`/`SHOWCOND`, the "Pending Cmds" column (`DtoConverter.BuildPendingCommands`, stable text so the change-tracker fingerprint doesn't churn), and `DELAT`/`DELCOND`/`DC` deletion (`ConditionalList.Delete`, mirrored by `SimulationEngine.ReplayDeleteQueued`). Two intentional simplifications (per aviation review, 7110.65 §4-2-5): additive coexistence assumes conditionals are on independent control axes — same-axis stacking won't auto-resolve to last-wins; and a fresh immediate command clears *all* pending conditionals, where strict amended-clearance rules would only amend the matching axis.

`ApplyCommand` is a thin routing switch over command type → `FlightCommandHandler`, `NavigationCommandHandler`, `ApproachCommandHandler`, `DepartureClearanceHandler`, `GroundCommandHandler`, `PatternCommandHandler`, `FlightPlanCommandHandler`, etc. See `Commands/CommandRegistry.cs` for the complete enum. For what happens *inside* the dispatcher and each handler — the two switch surfaces (`ApplyCommand` vs `TryApplyTowerCommand`), the handler read/write contract, and the per-domain effect cheat-sheet — see [command-handlers.md](command-handlers.md).

**Flight-plan commands (VP / FP / DA) canonicalize their inputs.** `FlightPlanCommandHandler` splits `C172/G` into `AircraftType` + `EquipmentSuffix`, canonicalizes departure/destination via `NavigationDatabase.TryResolveAirport` (rejecting unknown airports), and treats a single-token route as destination-only (`VP C172 5500 MOD` → `Destination=KMOD`, `Departure=null`). On the server, `RoomEngine.RecordAndDispatchFlightPlanAsync` spawns an unsupported track before dispatching the handler and rolls that spawn back on handler failure, gated on a `spawnedUnsupported` flag so a DUP-NEW-ID collision with a pre-existing aircraft doesn't delete it.

### 6. CommandQueue & triggers — `CommandQueue.cs`

> **The chaining contract — when a `;` chain advances, per command category, and what happens when a block fails — is written up in [command-chaining.md](command-chaining.md).** Read it before changing anything about block completion, advancement, or fire-time failure handling.

A queued `CommandBlock` carries:

- `BlockTrigger` — `ReachAltitude`, `ReachFix`, `InterceptRadial`, `OnHandoff`, `GiveWay`, `AtGroundEntity`, …
- `Commands` — `TrackedCommand[]` (the actual heading/altitude/speed payloads).
- `Dimensions` — which axes this block touches, for selective clearing.
- `ApplyAction` — closure that runs when the trigger fires.
- `SourceCommandText` — the canonical compound string (snapshot/replay support).

Each tick, step 9 of `FlightPhysics.Update` (`UpdateCommandQueue`) checks the current block's trigger. When met, the closure runs. `ReadyToAdvance` gates lateral changes until they're complete; altitude/speed continue in parallel when paired with lateral work. While the current block is still running, `ApplyReadyConditionalBlocks` scans the contiguous conditional blocks behind it so `AT`, `LV`, `ATFN`, radial/FRD, and handoff triggers can fire without waiting for the current target to complete. Active phases still skip ordinary queue advancement, but run the same triggered-block scan; fix and ground triggers can also fire through `NotifyFixSequenced` and `NotifyGroundEntityReached`. Exception: while the current phase is a terminal command-waiting phase (`Phase.IsIdleAwaitingCommands` — AtParking, HoldingAfterPushback, HoldingAfterExit, HoldingInPosition, HoldingShort, LinedUpAndWaiting), `AdvanceQueueWhileIdle` also applies untriggered blocks in strict `;` order: the phase never completes on its own, so the queue is the aircraft's only source of progress (issue #407 — `PUSH; SQ; SQNORM; TAXI …` used to strand forever in HoldingAfterPushback). A block the idle phase rejects stays queued (transparent commands and WAITs are exempt from that acceptance pre-check; untriggered WAITs count down before firing).

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

`TRACK`, `DROP`, `HO`, `ACCEPT`, `CANCEL HO`, `POINTOUT`, scratchpad, temp alt, cruise, `AS <tcp> …` — these change ownership and STARS-track metadata, not flight controls, and bypass `CommandDispatcher`.

**Two parallel switch tables dispatch them — keep both in sync:**
- **Live** (server): `TrackCommandHandler.HandleTrackCommand` (yaat-server) has its own `cmd switch` and its own inline identity-guard exemption list. Its `HandleHandoff`/`HandlePointOut` wrap `TrackEngine.ApplyHandoff`/`ApplyPointOut` with the room's CRC attendance as the `ConsolidationRedirect` (the branch that used to be server-only). Its `HandleGlobalTrackCommand`, `HandleGhostTrack`, `HandleRepositionToLocation`/`HandleRepositionMove` and `HandleAcknowledgeConflictAlert` are wrappers over the `TrackEngine` bodies (`DispatchGlobal`, `CreateGhostTrack`, `RepositionToLocation`/`RepositionMove`, `AcknowledgeConflictAlert`) that the router's `GlobalTrack`, `GhostTrack`, `Reposition` and `Track` arms run on every Sim run kind — one body per verb, two entry points until 3d-5. `RoomEngine.HandleConsolidationCmd` is likewise a wrapper over `SimulationEngine.Consolidate`/`Deconsolidate` (the router's `Consolidate`/`Deconsolidate` arms), handing the room's CRC attendance in as the `IActionHost.IsPositionAttended` answer a full consolidation reads.
- **Every Sim run kind** (replay, the bare test engine, the solo client, the AI's `DispatchAiCommand`): `TrackEngine.Dispatch` (`Yaat.Sim`) is a *second* switch, run by the `ActionRouter`'s `TrackOwnership` arm (`Simulation/Actions/ActionArms.Track`); its guard is `TrackEngine.RequiresIdentity`.

Both ultimately call the shared `TrackEngine.Handle*` leaf methods, so the per-command *behavior* is shared — but the routing, arg handling, and identity guards are **duplicated**. A track-command change applied to only one table passes that path's tests and silently misbehaves on the other (live works, replay doesn't, or vice-versa). Edit both switches **and** both guards, and add tests in both `Yaat.Sim.Tests` (Dispatch) and `Yaat.Server.Tests` (HandleTrackCommand). Example: issue #199 `TRACK [position]`.

**One identity, one map (tick-path step 3d-2).** Who a command acts as is resolved once, in Yaat.Sim, on every run kind: `TrackResolver.ResolveIdentity(scenario, selections, connectionId, asOverrideTcp)` — the `AS` override, else an AI connection's own position (`AiConnectionId`, resolved from `scenario.ArtccConfig`), else the position the connection selected with a bare `AS`, else the student. The selections live in one `PositionSelections` (`Simulation/Actions/`): `SimulationEngine.PositionSelections`, which the server room owns for its lifetime and hands to every engine it creates (`TrainingRoom.PositionSelections`, set in `PopulateRoom`), written by `SimulationEngine.SelectPosition` (live `AS`, Sim replay, server reconstruction, the CRC position sync) and snapshotted in `ServerSnapshotDto.PositionSelections`. TCP arguments resolve through one chain too — `TrackResolver.ResolveTcpToOwner(scenario, code)`: student TCP → scenario ATC positions → facility TCP → ERAM code (`C44`) → STARS interfacility handoff code (`` `31H ``) → ERAM-to-STARS prefixed code (`Q2B`) — reading `scenario.ArtccConfig`, so replay resolves every code live does.

**Compound concatenation.** Because these commands are single-command-parsed (not run through `ParseCompound`), a compound that *includes* one — `HO 3G; ACCEPT` — would otherwise swallow the `;`/`,` tail into the first command's argument. `CompoundPolicy.TrySplitSpecialCompound` (Yaat.Sim) detects this (parse succeeds, ≥2 commands, ≥1 is track/coordination/strip/TDLS, none is in the splitter's bail set — the rejection set plus `DEL`/`APT`, which have chain semantics) and produces the ordered `CompoundUnit`s. The `ActionRouter` routes each unit in turn (so each records its own `RecordedCommand` and replay stays per-unit); `RoomEngine.SendCommandAsync` does the same by **recursing** into `SendCommandAsync(..., announce: false)` per unit via `DispatchSpecialCompoundAsync`, suppressing the per-unit terminal echo. One combined `Command` echo + one combined `Response` is emitted, joined with `" ; then "` (across `;` blocks) / `", "` (parallel), matching `CommandDispatcher`. Aviation-only blocks in the compound are kept whole and still flow through the aviation arm → `DispatchCompound`, preserving their triggers.

**Non-compoundable rejection.** A genuinely multi-command compound containing a rejection-set command (PAUSE, spawn, flight-plan ops, room-wide commands — `CompoundPolicy.IsNonCompoundable` in Yaat.Sim, the single predicate shared with the client's pre-send check in `MainViewModel`) is rejected outright with *"{verb} cannot be part of a chained command"* instead of falling through to the single-command router, where the queued unit would no-op at fire time. A line the single-command parser accepts whole (free-text `NOTE …; …`) is not a chain and passes through; `DEL` and `DEST`/`APT` are deliberately **not** in the rejection set — they have real chain semantics (`CROSS 28R; DEL`, `AT 5000 APT OAK`).

## Coordination command bypass — `CoordinationCommandHandler`

`RD`, `RDH`, `RDR`, `RDACK`, `RDAUTO` — STARS coordination items between TCPs. Channels are resolved from ARTCC config; items auto-expire 5 min after ack.

## Deferred dispatch — WAIT, BEHIND, and the command-run delay

(`DeferredDispatch` in `src/Yaat.Sim/CommandQueue.cs`; ticked by `SimulationEngine.ProcessDeferredDispatches`)

Distinct from the CommandQueue (§6): a queued `CommandBlock` holds *part* of an already-dispatched compound behind a trigger and writes `ControlTargets` when the trigger fires. A `DeferredDispatch` instead holds the **entire un-dispatched compound** and re-runs it through `DispatchCompound` from scratch when its timer/condition expires — phases, queue clearing, and validation all happen fresh at fire time, not at issue time. Each aircraft owns a `DeferredDispatches` list (snapshot-serialized).

Three things create a deferred dispatch:

1. **`TryDeferLeadingWait`** (inside `DispatchCompound`, before the phase gate) — a leading `WAIT n` (seconds) or `WAITD nm` (flying miles). The WAIT is stripped; the remaining blocks become the payload.
2. **`TryDeferGiveWay`** (inside `DispatchCompound`) — a leading `BEHIND <callsign>` give-way condition. The payload dispatches once the named aircraft has passed.
3. **Command-run delay** — `ReactionDelayPolicy.Decide` + `SimulationEngine.DeferForReaction` (`Simulation/Actions/`), called by the `ActionRouter`'s aviation arm and by `RoomEngine.HandleStandardCmd` *around* `DispatchCompound` (not inside it). The configurable pilot-reaction delay (issue #180): when active, the whole compound is deferred a sampled `[min,max]` seconds; the controller gets an immediate "Pilot complying in Ns" acknowledgement and the aircraft acts when the timer expires. In **solo training mode** that acknowledgement is suppressed (empty `CommandResult` message → no terminal `Response`) so the student can't read off the exact sampled delay — the pilot's read-back is the acknowledgement instead.

**A WAIT *after* a condition is NOT a deferred dispatch.** `TryDeferLeadingWait` only fires when the first block has no precondition. `<condition> WAIT n <cmd>` (e.g. `AT TTE WAIT 170 DM 110`, or the scenario-preset shape `CFIX TTE 140; AT TTE WAIT 170 DM 110`) instead becomes a single queued `CommandBlock` with the trigger *and* `IsWaitBlock`/`WaitRemainingSeconds`: `CommandParser.ParseBlock` merges the leading WAIT and its payload into one conditioned block, and `FlightPhysics.ApplyOrCountdownWait` holds the payload until the wait counts down *after* the trigger fires — so `DM 110` runs `n` seconds after the fix, not on it (issue #286). Blocks sequenced after it with `;` (a trailing `RNS`) are held behind the counting-down wait by `ApplyReadyConditionalBlocks`/`NotifyFixSequenced` so they run once it completes, honoring `;` sequencing even when a perpetual CFIX `Navigation` block keeps the queue pinned at index 0.

`ProcessDeferredDispatches` (a per-tick step) ticks every aircraft's `DeferredDispatches` each 0.25 s sub-tick — decrementing seconds, accumulating distance, or evaluating the give-way condition — and re-dispatches the payload through `DispatchCompound` on expiry. WAIT/BEHIND/distance expiries emit a `[Deferred] … →` terminal line; reaction delays fire silently (the controller already saw the acknowledgement at issue time).

**Non-standard payloads at fire time.** A ready payload that is a *pure track* compound (e.g. `WAIT 5 SP1 …`) is routed through `TryDispatchImmediateTrackPreset` → `TrackEngine.Dispatch` before `DispatchCompound`, mirroring the immediate-preset path (`DispatchSinglePreset`) — otherwise the transparent-command fast-path would send a track command to `ApplyCommand`'s no-dispatcher-arm default and fail. *Strip* payloads (e.g. `WAIT 2 ANNOTATE 10 ✓`) do reach `DispatchCompound`, but `ApplyCommand` queues them onto `AircraftState.PendingStripDispatches` for the host to apply (strip state is host-owned — see [flight-strips.md](flight-strips.md)) rather than failing. Before these two routes existed, every deferred/preset strip command and every deferred *transparent*-track command failed with `[Deferred] could not apply: …`.

### Command-run delay specifics

- **Scope.** Applies to anything reaching the standard dispatch path: flight/nav/approach/hold/ground plus squawk/ident/say. Track/coordination/strip commands are routed away earlier (see above) and never reach it. *Pure* frequency-change/contact compounds (`ContactCommand` / `FrequencyChangeApprovedCommand` / `AcknowledgePilotContactCommand`) are exempt — AIM 4-2-3 expects a pilot to switch frequency ASAP — while a mixed compound (`FH 270; CON TWR`) is delayed as a whole. Commands carrying explicit timing (leading `WAIT`/`WAITD`/`BEHIND`) are not additionally reaction-delayed.
- **Replay determinism.** Live sampling draws from a *dedicated* `SimulationWorld.ReactionDelayRng`, never the shared `World.Rng`, so it can't perturb the RNG sequence driving emergent events (go-arounds, generator spawns). The sampled value is baked into `RecordedCommand.ReactionDelaySeconds`; `ReactionDelayPolicy.Decide` returns a baked value unconditionally (the router passes it from the record), so a replay recreates the deferral and never re-samples — re-sampling would draw from a divergent RNG state and break determinism.
- **Issue order.** `ReactionDelayPolicy.Decide` clamps each new reaction delay so it fires no sooner than any already-pending one, and `ProcessDeferredDispatches` applies same-tick expiries FIFO — so two rapid commands always take effect in the order issued.

### The clears-on-supersede invariant

`DispatchCompound` calls `aircraft.DeferredDispatches.Clear()` so a **new** controller command cancels pending WAITs (the new instruction supersedes). A deferred **re-dispatch** must *not* cancel its siblings, so `ProcessDeferredDispatches` detaches the surviving (not-yet-ready) deferrals across the dispatch and restores them afterward. Without this, two stacked reaction-delayed (or WAIT) commands would wipe each other when the first fires.

## Pitfalls

- **Heading/altitude/speed are NOT track commands.** They take `HandleStandardCmd` → `CommandDispatcher`. Track commands are STARS ownership ops. Easy to confuse because both involve callsigns.
- **Two parsers, one truth.** Client parses for autocomplete; server parses for execution. The server is authoritative — don't trust client-side parse results for behavior.
- **Records include rejects — on the Sim entry points.** The `ActionRouter` records every fresh command it routes, accepted or not, with `RecordedCommand.Accepted`; a replay that reaches the other verdict logs a `replay-fidelity` warning. The live server's `SendCommandAsync` still records only successful commands until it moves onto the router (tick step 3d-5), so a recording from a live room has no rejected entries yet. Either way, never "fix" replay drift by skipping records.
- **Bare typed `APT` is amendment-only on replay until 3d-5.** Live, `RoomEngine`'s `ChangeDestinationCommand` arm runs `FlightPlanCommandHandler.TryChangeDestination` (which clears the STAR / pending approach / route through `ClearArrivalProcedureState`) and then `AmendFlightPlan`, recording only the `RecordedAmendFlightPlan` — never the command text. `SimulationEngine.AmendFlightPlan` sets `Destination` without the procedure clear, so a replay of a bare `APT` keeps arrival state the live session dropped. `APT` consumes no non-replayable input, so the fix is text-only recording through the aviation arm (the dispatcher's `ChangeDestination` arm, which the chained form `AT 5000 APT OAK` already takes) — a 3d-5 change to the live arm. Do not move `APT` to the `FlightPlan` kind: replaying it as an amendment is the bug.
- **Dry-run uses a clone — make handlers idempotent on a clone.** Anything `ApplyCommand` does must work on a snapshot copy without affecting the live aircraft. If a handler writes to non-cloned state (a singleton, a sibling aircraft), dry-run will leak.
- **`TerminalEmitter` must be nulled in dry-run.** SAY-class verbs broadcast via `ctx.TerminalEmitter`; if dry-run forgets to null it, SAYs fire twice. See the `project_dispatch_context_terminal_emitter` memory.
- **Phase clearing is post-validation.** `ClearsPhase` does not immediately clear — validation runs first on a clone, then the phase is cleared, then commands apply. This protects against half-applied compound commands.
- **Dimension-aware clearing isn't all-or-nothing.** A new heading command clears queued lateral blocks but leaves a queued altitude block alone. If you find yourself adding `aircraft.CommandQueue.Clear()` to a handler, you're probably bypassing this design.
- **`SimulationWorld.AddAircraft` is replacement-safe.** It drops any same-callsign entry (case-insensitive) before appending and logs a warning. Spawn wins over a pre-existing user-typed VP/DA ghost — don't add per-call-site dedup. A logged replacement is expected when a scenario spawn collides with a ghost; two scenario spawn paths firing for one callsign is a bug.
- **A deferred re-dispatch must not cancel sibling deferrals.** `DispatchCompound` clears `DeferredDispatches` to supersede pending WAITs on a *new* command, so `ProcessDeferredDispatches` detaches and restores the survivors around a re-dispatch. If you rework deferred dispatch, keep that — otherwise a firing WAIT or command-run delay silently wipes the others. See [Deferred dispatch](#deferred-dispatch--wait-behind-and-the-command-run-delay).
