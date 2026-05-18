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

After validation, every command is recorded for replay: `Record(new RecordedCommand(scenario.ElapsedSeconds, callsign, command, initials, connectionId))`. **Including rejected ones** — replay needs a faithful history, not a clean one.

### 5. CommandDispatcher.DispatchCompound

(`src/Yaat.Sim/Commands/CommandDispatcher.cs`)

`DispatchCompound(aircraft, compound, ctx)` is the entry point for non-track, non-coordination, non-strip commands. The big moves:

1. **Phase gate.** If `aircraft.Phases?.CurrentPhase` exists, route through `DispatchWithPhase`. The phase's `CanAcceptCommand` is consulted (see [phases.md](phases.md)). `Rejected` returns immediately. `ClearsPhase` defers clearing until validation passes.
2. **Dry-run validation.** The full compound is run on a clone of the aircraft (`DryRunValidate`). If any block fails (e.g. unknown fix, illegal intercept), the user gets the error and **state is unchanged**.
3. **Dimension-aware queue clearing.** New blocks declare which dimensions they touch (`Lateral | Vertical | Speed`). `ClearConflictingBlocks` removes queued blocks whose dimensions overlap; non-conflicting blocks survive. Mixed-dimension blocks may be split via `SplitBlockNonConflicting`.
4. **Apply or enqueue.** Blocks with no trigger apply immediately via `ApplyCommand`. Blocks with a trigger (LV, AT, …) are wrapped in `CommandBlock` and pushed onto `aircraft.CommandQueue`.

`ApplyCommand` is a thin routing switch over command type → `FlightCommandHandler`, `NavigationCommandHandler`, `ApproachCommandHandler`, `DepartureClearanceHandler`, `GroundCommandHandler`, `PatternCommandHandler`, `FlightPlanCommandHandler`, etc. See `Commands/CommandRegistry.cs` for the complete enum.

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

Adding a new contextual flag to handlers? Add it to `DispatchContext`, set it at the call sites in `SimulationEngine` / `RoomEngine`, and read from `ctx`. Don't pass it as a parameter — the bundle exists to avoid signature creep.

## Track command bypass — `TrackCommandHandler` / `TrackEngine`

`TRACK`, `DROP`, `HO`, `ACCEPT`, `CANCEL HO`, `POINTOUT`, scratchpad, temp alt, cruise, `AS <tcp> …` — these change ownership and STARS-track metadata, not flight controls. They go through `TrackEngine.Dispatch` directly (pure-domain logic in `Yaat.Sim`), not `CommandDispatcher`. `ReplayTrackApplier` uses the same `TrackEngine` so live and replay agree.

## Coordination command bypass — `CoordinationCommandHandler`

`RD`, `RDH`, `RDR`, `RDACK`, `RDAUTO` — STARS coordination items between TCPs. Channels are resolved from ARTCC config; items auto-expire 5 min after ack.

## Pitfalls

- **Heading/altitude/speed are NOT track commands.** They take `HandleStandardCmd` → `CommandDispatcher`. Track commands are STARS ownership ops. Easy to confuse because both involve callsigns.
- **Two parsers, one truth.** Client parses for autocomplete; server parses for execution. The server is authoritative — don't trust client-side parse results for behavior.
- **Records include rejects.** `RecordedCommand` is logged before validation. If you "fix" replay drift by skipping rejects, replay diverges from the original session.
- **Dry-run uses a clone — make handlers idempotent on a clone.** Anything `ApplyCommand` does must work on a snapshot copy without affecting the live aircraft. If a handler writes to non-cloned state (a singleton, a sibling aircraft), dry-run will leak.
- **`TerminalEmitter` must be nulled in dry-run.** SAY-class verbs broadcast via `ctx.TerminalEmitter`; if dry-run forgets to null it, SAYs fire twice. See the `project_dispatch_context_terminal_emitter` memory.
- **Phase clearing is post-validation.** `ClearsPhase` does not immediately clear — validation runs first on a clone, then the phase is cleared, then commands apply. This protects against half-applied compound commands.
- **Dimension-aware clearing isn't all-or-nothing.** A new heading command clears queued lateral blocks but leaves a queued altitude block alone. If you find yourself adding `aircraft.CommandQueue.Clear()` to a handler, you're probably bypassing this design.
