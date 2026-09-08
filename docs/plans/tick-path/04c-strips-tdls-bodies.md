# Tick-path step 4: the strip and TDLS bodies cross into Yaat.Sim (shrink `IActionHost`)

## Context

`docs/plans/MAIN.md` → Current focus → "next is shrinking `IActionHost` — the strip/TDLS bodies are the first to cross and
retire the banked `actions`/`s2oak4` entries" (`docs/plans/tick-path/04-relocation.md:19`). The *state* crossed on
2026-09-07 (A1: `SimulationEngine.Strips` / `.Tdls`, snapshotted, engine-owned) but every *mutation body* is still
yaat-server's: `StripMutations` (1,123 lines), `StripCommandHandler` (1,582), `TdlsMutations` (220),
`TdlsCommandHandler` (472), six `TickProcessor` tick steps, the spawn hooks (`AfterAircraftSpawned`), the amendment
reprint and the strip-request print (`RoomEngine`), and the deferred strip dispatch. So:

- `IActionHost` still carries four slots (`ApplyStrip`, `ApplyTdls`, `ApplyTdlsOpsConfig`, `ApplyRecordedStripRequest`)
  that `BareHost` / `ReplayHost` refuse — a Sim replay (client playback) and the bare test engine never build strips or
  PDCs, and the `actions` / `s2oak4` live-vs-test and live-vs-replay oracle baselines bank exactly that (Notes name
  `ApplyTdls`, `AfterAircraftSpawned`, `ReprintDepartureStripAfterAmendment`).
- `IHostSteps` still carries `AutoArrivalStrips`, `AutoApproachDepartureStrips`, `AutoTdlsQueue`, `TdlsAutoWilco`,
  `TdlsExpiry`, `TdlsTrackRemoval`, and `IHostConsumers.OnStripDispatches` — host-decided simulation steps, the residue
  ADR 0001 forbids.
- No TDLS or strip handler logic can be unit-tested against a bare engine; every test boots `RoomEngineTestHarness`.

ADR 0003 fixes the shape: the bodies move, the server keeps broadcast and wire projection only (`TdlsStatus`,
`ClearanceDto`, `StripItemDto`, `FlightStripsStateDto` stay in `Yaat.Server.Dtos`, pinned by `CrcWireContractTests`).
ADR 0003 also already states `ArtccConfigService` is not a blocker: every bay/position lookup the bodies make is an
`ArtccConfigResolver` extension over `ArtccConfigRoot` (verified: `GetAllAccessibleStripBays`,
`GetAllCommandTargetableStripBays`, `GetAccessibleStripBay`, `GetAccessibleStripBayById`, `FindFirstOwnBayWithNamePrefix`,
`FindPositionByCallsign`, `FindFacility`, `FindFacilityForPositionCallsign` all exist in
`src/Yaat.Sim/Data/Vnas/ArtccConfigResolver.cs`), and `SimScenarioState.ArtccConfig` holds the resolved config.

**Decisions taken with the user (2026-09-07, AskUserQuestion):**
1. **Broadcast seam = engine-owned change trackers.** `FlightStripState.Changes` / `TdlsState.Changes` accumulate what
   the mutations touched; the router drains them in `Finish` and one post-physics spine step drains what the tick
   steps produced; two new consumers `OnStripsChanged` / `OnTdlsChanged`; `RoomHost` broadcasts unless
   `Room.IsBroadcastSuppressed` (tape playback broadcasts, a reconstruction does not — the A2 rule).
2. **Two commits: TDLS first (A), then strips (B).** Each predicts and retires its own baseline family.
3. **`AnyControllerStaffsFacility` reads `engine.Attendance`** (the recorded CRC-client derivation from 04a) instead of
   `CrcClientManager`; secondary positions are not in `Attendance` — a known, documented narrowing.
4. **The amendment-reprint id is baked in B** (MAIN.md backlog item "`ReprintDepartureStripAfterAmendment` mints a fresh
   strip id on replay"): the `FP` verb bakes through the existing `ctx.StripId` channel, `RecordedAmendFlightPlan`
   gains a `StripId` the CRC amendment handler bakes at issue time.

Out of scope (stay on MAIN.md): the S2-OAK-4 reconstruct-vs-live pin; the deferred/preset `SCAN`/`HSC` unbaked-id item
(the queue still carries no record to bake onto — the body moves verbatim, `bakedStripId: null`); the `StripPrintTarget`
parameter-count cleanup (moves verbatim; note it in the plan's follow-ups, do not widen).

Discipline (tick-path README): predict the baseline changes before `YAAT_ORACLE_REBASELINE=1`; an unpredicted
`Added`/`Removed` stops the work; corpus triage per ADR 0004; TDD red-first; `pwsh tools/test-all.ps1` green before
each commit; predicted-vs-got recorded in `04-relocation.md`. Aviation review is not owed (no aviation behaviour changes;
verbatim moves of strip/PDC bookkeeping) — `csharp-reviewer` is, per commit.

---

## Shared shape (both commits)

**Sim host contract after A+B**

- `IActionHost` loses `ApplyStrip`, `ApplyTdls`, `ApplyTdlsOpsConfig`, `ApplyRecordedStripRequest`; its consumer half gains
  `void OnStripsChanged(StripChangeSet changes)` and `void OnTdlsChanged(TdlsChangeSet changes)` (on `IActionHost` because
  the router only holds that view; the spine step's `ISimulationHost` reaches them too).
- `IHostSteps` loses the six strip/TDLS members (step-4 debt left: `LiveTrafficSync`, `CoordinationTimers`, `TowerLists`);
  `IHostConsumers` loses `OnStripDispatches`; `SimulationEngine.StripDispatchRequested` / `FireStripDispatchRequested` are
  deleted (the engine applies deferred strip commands itself).
- `ArmTable` (`Simulation/Actions/ActionArm.cs:107-109`): the `Strip`, `Tdls`, `TdlsOps` rows become `Sim` arms;
  `ActionRouter.ApplyStateRecord` applies `RecordedStripRequest` through `engine.PrintRequestedStrip`.
- `ActionRouter.Finish`: after the arm (fresh or recorded), `if (engine.Tdls.Changes.HasAny) host.OnTdlsChanged(engine.Tdls.Changes.Drain())`,
  same for strips — so a live command's broadcast still precedes its result, and the end-of-second pump broadcasts per
  record on playback.
- `SpineOrder.PostPhysics`: the six `Host` entries become `Sim` entries **at the same positions**; `StripDispatches`
  becomes `engine.TickStripDispatches()`; one new `SpineStep.Sim(StepId.StripTdlsChanges, (e, h) => e.DrainStripTdlsChangesInto(h))`
  immediately after `StripDispatches` and before `AutoDelete` (an item's aircraft still resolves for the TDLS DTO).
  `SpineTraceTests` pins the literal sequence — update the pin once per commit.
- `SimulationEngine.InitializeStripsAndTdlsFromArtcc()` (new): the two private `ScenarioLifecycleService` bodies
  (`InitializeStripBays` :865, `InitializeTdlsConfigs` :889) over `Scenario.ArtccConfig` + `Scenario.StudentPosition`.
  Callers: `PopulateRoom` (replacing both), `ReplayDriver` after it restores `StudentPosition` (:171) and `ArtccConfig`
  (:216) — today a Sim replay never initialises either, which is why the Sim replay could not have applied a `TDLSQ`
  even if the slot had allowed it — and the bare-engine tests after they set the positions by hand (the Sim
  `LoadScenario` never resolves `ArtccConfig` / `StudentPosition`; see memory `sim_loadscenario_has_no_artcc_positions`).
- `RoomHost` gains `OnStripsChanged` / `OnTdlsChanged`: `if (Room.IsBroadcastSuppressed) return;` then items first
  (`Items[id]` → `DtoConverter.ToStripItem` / `ToTdlsItem(record, World.FindAircraft(...))`; an id no longer present is
  skipped), removals (`TdlsBroadcaster.BroadcastRemovalAsync(itemId, facilityId, callsign, dumped)`), then the full state
  when flagged — the same items-then-full-state order every body uses today. `BareHost` discards; `ReplayHost` delegates.
- Change-set types (Sim, `Simulation/Strips/StripChangeTracker.cs`, `Simulation/Tdls/TdlsChangeTracker.cs`):
  `StripChangeSet(IReadOnlyList<string> ChangedItemIds, bool FullState)`;
  `TdlsChangeSet(IReadOnlyList<string> ChangedItemIds, IReadOnlyList<TdlsRemoval> Removed, bool FullState)` with
  `TdlsRemoval(string ItemId, string FacilityId, string Callsign, bool Dumped)`. Trackers are transient (not snapshotted),
  cleared by `ClearSession()` / `Reset()`, mutated only under the state's `Gate` on the tick thread.
- Logging: `private static readonly ILogger Log = SimLog.CreateLogger("StripCommandHandler")` etc. Terminal lines
  (`[TDLS PDC sent at OAK] …`, the deferred-strip failure warning) go through `EmitTerminal`, drained by the existing
  pre/post-physics terminal drains. Results are `CommandResult` (Sim); `RoomEngine.Result(CommandResultDto)` is deleted.
- Spawn hooks: `SimulationEngine.AfterAircraftSpawned(AircraftState ac)` (A: the TDLS auto-queue; B: + the strip
  auto-print). Called by the engine at every spawn site *before* the host consumer: the pre-physics spawns inside
  `TickPrePhysics` (delayed, generator, recorded), the router's spawn paths (`ActionArms.cs:241/380/420`,
  `ActionRouter.cs:76/83`) via one helper, and `PopulateRoom`'s immediate aircraft (replacing
  `_tickProcessor.AfterAircraftSpawned`, `ScenarioLifecycleService.cs:825`). The hook only mutates and marks changes;
  the broadcast comes from the later drain, so the client still learns the callsign (spawn broadcast) before its strip/PDC.
  `PopulateRoom` drains once at the end of load (`RoomEngine` calls the host consumers directly) so the load-time
  strips/PDCs reach clients as they do today.

**Layout.** Sim: `src/Yaat.Sim/Simulation/Tdls/{TdlsMutations,TdlsCommandHandler,TdlsChangeTracker}.cs`,
`src/Yaat.Sim/Simulation/Strips/{StripMutations,StripCommandHandler,StripRequests,StripChangeTracker,StripItemType}.cs`,
engine partials `SimulationEngine.Tdls.cs` / `SimulationEngine.Strips.cs` (tick steps, hooks, init). Server files
deleted: the four handler/mutation files, their DI registrations (`ServerApp`), `TickProcessor`'s strip/TDLS bodies
(~600 lines), `RoomEngine`'s strip/TDLS bodies (~200 lines). Verbatim moves: no restructuring beyond the seams above
(the ≤5-param / optional-param debt in `StripMutations` stays a listed follow-up).

---

## Commit A — TDLS crosses (Yaat.Sim + yaat-server)

**Shipped 2026-09-07.** Predicted-vs-got, corrections and findings are recorded under the `IActionHost` item in [04-relocation.md](./04-relocation.md).

### Yaat.Sim

1. **`TdlsMutations`** moves verbatim minus `BuildFullState` (→ `TdlsBroadcaster.BuildState`, server). `DefaultTtl` stays
   on it. Each mutation marks `state.Changes` (`QueuePending`/`MarkSent`/`MarkWilco` → changed id; `Dump` → removal
   `dumped: true`; `Expire` → removal `dumped: false`).
2. **`TdlsCommandHandler`** → `internal static class`, `Handle(SimulationEngine engine, ParsedCommand parsed, string callsign) : CommandResult`;
   `room.ActiveSim!.Tdls` → `engine.Tdls`, `room.ActiveScenario!.SimTimeUtc` → `engine.Scenario!.SimTimeUtc`,
   `room.World` → `engine.World`; the two broadcasters go (tracker + `EmitTerminal("Tdls", callsign, message)` for
   `BroadcastSendTerminalAsync`); `DefaultWilcoDelay` stays on it. `HandleWilco` stays public (the auto-WILCO step calls it).
3. **`SimulationEngine.Tdls.cs`**: `TickAutoTdlsQueue()`, `TickTdlsAutoWilco()`, `TickTdlsExpiry()`, `TickTdlsTrackRemoval()`
   (the `TickProcessor` bodies :188-405 verbatim over `World`/`Scenario`/`Tdls`), `TryQueueAutoTdlsForAircraft` (already
   Sim-typed, :228-263), `SetTdlsOpConfig` + `ApplyTdlsOpConfig` (`RoomEngine.cs:1883-1919`; the full-state broadcast
   becomes `Tdls.Changes.MarkFullState()`), `AfterAircraftSpawned` (TDLS half: `TryAutoQueueTdlsPdcForAircraft` :796),
   `InitializeStripsAndTdlsFromArtcc()` (both halves now — strips' bay init is a one-liner over the resolver),
   `DrainStripTdlsChangesInto(IActionHost host)` (TDLS only until B).
4. Contract edits listed under *Shared shape* for the TDLS members: `IActionHost` −`ApplyTdls` −`ApplyTdlsOpsConfig`
   +`OnTdlsChanged`; `IHostSteps` −4; `ArmTable` `Tdls`/`TdlsOps` → `Sim`; `SpineOrder` four entries → `Sim`, new
   `StripTdlsChanges` step + `StepId`; `ActionRouter.Finish` drain; `BareHost`/`ReplayHost` shed the members;
   `ReplayDriver` calls the init after :216.

### yaat-server

5. `RoomHost`: delete `ApplyTdls`, `ApplyTdlsOpsConfig`, the four step forwarders; add `OnTdlsChanged`. `RoomEngine`: delete
   `ApplyTdlsCommand`, `SetTdlsOpConfig`, `ApplyTdlsOpConfig`, `_tdlsHandler`; `TickProcessor`: delete the four bodies,
   `TryQueueAutoTdlsForAircraft`, `TryAutoQueueTdlsPdcForAircraft`, `_tdlsBroadcaster` where unused;
   `ScenarioLifecycleService`: `InitializeTdlsConfigs` → the engine call (strips' init moves too, see B — in A the
   engine method initialises both and `InitializeStripBays` is deleted as well); `AfterAircraftSpawned` keeps only the
   strip half until B. `TdlsBroadcaster.BuildState` absorbs `TdlsMutations.BuildFullState`. DI: drop
   `TdlsCommandHandler`. `SessionPersistenceService` restore path: after it sets `ArtccConfig` (:299) it must call the
   engine init if it does not already reload through `PopulateRoom` — verify, and pin if it does not.

### Tests (red first)

- **Sim `Simulation/Tdls/TdlsStepTests`** on the bare engine (`TestVnasData.EnsureInitialized()`, `TestArtccConfig.LoadZoa()`,
  positions set by hand, `InitializeStripsAndTdlsFromArtcc()`; a capturing test host records `OnTdlsChanged`):
  1. a KOAK departure passed to `AfterAircraftSpawned` gets a Pending item and the consumer sees its id;
  2. `TDLSS <nine fields>` via `Actions.IssueLive` on the bare host → `Sent`, `SentUtc == SimTimeUtc`, `ScheduledWilcoAt == +3 s`,
     a `Tdls` terminal entry drained (today `BareHost` refuses — compile/behaviour red);
  3. three `TickOneSecond`s → `Wilco` and `Voice.TdlsDumped` (auto-WILCO body crossed);
  4. `TDLSW` before the timer → `Wilco`, scheduler entry cleared, a second timer fire is benign;
  5. `TDLSDUMP` → removal with `Dumped: true` and the lockout blocks a re-queue;
  6. TTL: `ElapsedSeconds` walked past 2 h → `Expire` removal with `Dumped: false`, no lockout;
  7. an item whose aircraft gains `Track.Owner` is removed on the next tick;
  8. `TDLSOPS OAK <name>` → `ActiveOpConfigIds`, full-state flag set; unknown name refused with the known list.
  Mutation checks: remove the `Status != Pending` guard in `HandleSend` → test 2's re-send variant red; remove the
  `Dumped` check in `TryQueueAutoTdlsForAircraft` → test 5 red.
- **Sim `Replay`**: `ReplayDriver` over a recording carrying `TDLSQ`/`TDLSS` (build one in-test with `RecordingArchiveWriter`
  from a bare-engine run, or reuse `StripTdlsRewindTests`' shape on the Sim side) → the replayed engine's item is `Sent`
  with the recorded payload (today: refused, item absent).
- **Server**: the eight TDLS suites (`TdlsCommandHandlerTests`, `TdlsTickProcessorTests`, `TdlsAutoGenerationTests`,
  `TdlsAutoQueueScenarioLoadTests`, `TdlsOpConfigTests`, `TdlsPdcMessageFormatTests`, `TdlsStateInitTests`,
  `StripTdlsRewindTests`) keep passing on the moved bodies — update call sites, never weaken. `SpineTraceTests` pin updated.

### Oracle prediction for A

`actions` live-vs-test and live-vs-replay **lose** `Server.Tdls.Items[*].SentPayload` / `.SentUtc` / `.Status` (t=58),
`Server.Tdls.Items[*].WilcoUtc` (t=59), `Server.Tdls.ScheduledWilco[*]` (t=58), and `Server.Tdls.Items[*]` +
`Server.Tdls.NextItemId` (t=110, the `ADD I L J 28R` spawn hook); `s2oak4` live-vs-test / live-vs-replay lose their
`Server.Tdls.*` paths (t=271, N342T's delayed spawn). The `Server.Strips.*` entries on all four **stay** (B). All four
reconstruct legs, `weather` and `autodelete` byte-identical. **Nothing `Added`.** Anything else stops the work.

### Docs for A

`docs/vtdls.md` (§State model / §State ownership: bodies are Sim's; the broadcast seam; the `ScheduledWilcoAt` sentence),
`docs/tick-loop.md` (hosts table: `IHostSteps` step-4 list, `IActionHost` slot list, the new drain step),
`docs/adr/0007-one-action-router.md` (append a dated note under the "TDLS … slots refuse while replaying" bullet — it is
superseded), `docs/architecture.md` in both repos, `04-relocation.md` (predicted-vs-got under a new sub-item),
`05-retirements.md` residual, CHANGELOG **Changed**: "Replaying a recording in the client now rebuilds the PDC list as the
live session had it."

---

## Commit B — strips cross (Yaat.Sim + yaat-server)

### Yaat.Sim

1. **`StripItemType`** enum in `Simulation/Strips` (same values as the wire enum); `StripMutations.TypeFor` / `StyleOf` /
   `IsSeparator` use it; `StripItemRecord.Type` stays `int` (snapshot schema unchanged); `DtoConverter.ToStripItem` casts.
2. **`StripMutations`** moves verbatim minus `BuildFullState` (→ `StripBroadcaster.BuildState`, also the
   `CrcBroadcastService.BuildFlightStripsData` caller) and minus the stale `using Yaat.Server.Data`. Every mutation marks
   `state.Changes` (item writes → changed id; moves, deletes, rack/queue changes → `FullState`).
3. **`StripCommandHandler`** → `internal static class`, `Handle(SimulationEngine engine, ParsedCommand parsed, string callsign, string? bakedStripId) : StripApplyResult`;
   `_artccConfig.*` → `engine.Scenario!.ArtccConfig.*` (`GetAllCommandTargetableStripBays` :1479, `GetAccessibleStripBay` :1497);
   `AnyControllerStaffsFacility` (:352) → `engine.Attendance.PositionIds.Any(id => config.FindFacilityForPositionCallsign(id)?.Id == facilityId)`
   (doc the narrowing: no secondary positions); the 27 broadcast sites → tracker marks (most already fall out of the
   `StripMutations` marks; keep an explicit `MarkFullState()` where a handler broadcast full state without a mutation).
4. **`StripRequests`** (new file): `StripRequestPlan`, `ResolveStripRequest`, `PrintRequestedStrip`, `IsArrivalCandidate`,
   `ResolveAirportPosition` (from `RoomEngine.cs:1038-1168` and `TickProcessor`), over `engine`. `RoomEngine.RequestFlightStripForAircraft`
   keeps the hub-facing mint + `ApplyAndRecord`.
5. **`SimulationEngine.Strips.cs`**: `TickAutoArrivalStrips()`, `TickAutoApproachDepartureStrips()` (`TickProcessor.cs:426-608`
   verbatim), `TickStripDispatches()` (drain `World.DrainAllStripDispatches()`, `StripCommandHandler.Handle(this, cmd, cs, null)`,
   failure → `EmitTerminal("Warning", cs, message)`), `AfterAircraftSpawned` gains the strip half (:648-734 incl.
   `TryPlaceConfiguredStrip`), `ReprintDepartureStripAfterAmendment(string callsign, string? bakedStripId) : string?`
   (`RoomEngine.cs:1413-1440`; returns the id it printed under, reuses a baked id and prints nothing when `Items` already
   holds it — the `PrintRequestedStrip` rule), `DrainStripTdlsChangesInto` now drains both.
6. **Amendment id baking**: the `FP` arm (`ActionArms.cs:164`) sets `ctx.StripId` from the reprint so `Finish` bakes it onto
   `RecordedCommand.StripId` and a replay hands it back through `ctx.Input.Baked?.StripId`; `RecordedAmendFlightPlan` gains
   `string? StripId` (pre-feature records deserialize null → mint, as today); the CRC amendment handler mints via
   `StripMutations.MintStripId` before `IssueDerived` the way `RequestFlightStripForAircraft` does; `ActionRouter.cs:94`
   passes it through. `tools/bug_bundle.py` needs no tag change.
7. Contract edits for the strip members: `IActionHost` −`ApplyStrip` −`ApplyRecordedStripRequest` +`OnStripsChanged`;
   `IHostSteps` −2; `IHostConsumers` −`OnStripDispatches`; `StripDispatchRequested` event deleted; `ArmTable` `Strip` →
   `Sim` (sets `ctx.StripId`); `ApplyStateRecord` `RecordedStripRequest` → engine; `SpineOrder` two entries → `Sim`.

### yaat-server

8. `RoomHost`: delete `ApplyStrip`, `ApplyRecordedStripRequest`, `OnStripDispatches`, the two step forwarders, the
   `TickProcessor.AfterAircraftSpawned` call in `OnAircraftSpawned` and `Engine.ReprintDepartureStripAfterAmendment` in
   `OnFlightPlanAmended` (the consumer keeps only what is left — if nothing, delete the consumer from `IActionHost` too and
   note it); add `OnStripsChanged`. `RoomEngine`: delete `ApplyStripCommand`, `ResolveStripRequest`, `PrintRequestedStrip`,
   `BroadcastPrintedStrip`, `ReprintDepartureStripAfterAmendment`, `StripPrintOutcome`, `_stripHandler`; `TickProcessor`:
   delete the two step bodies, `AfterAircraftSpawned`, `TryPlaceConfiguredStrip`, `ProcessDeferredStripDispatches`,
   `DispatchDeferredStripAsync`, `IsArrivalCandidate`, `ResolveAirportPosition` (move), `IsDepartureAircraft` (move);
   `CrcClientState.Strips.cs` keeps translating and calls `StripMutations` from Sim; `RecordingManager`
   (`CollectSeparators`, `ResyncStripsAndTdlsAsync`) unchanged except namespaces. DI: drop `StripCommandHandler`.

### Tests (red first)

- **Sim `Simulation/Strips/StripStepTests`** on the bare engine (ZOA config, OAK_TWR / OAK_GND / an APP position set by hand):
  1. TWR student: a KOAK departure through `AfterAircraftSpawned` lands in the first "Ground" bay; GND: printer queue;
     `InitialStripBayByCallsign` placement wins and clamps the rack; APP: nothing at spawn, a strip into the position-name
     bay on the tick the aircraft enters `TakeoffPhase`;
  2. arrival auto-print: ETA under 20 min prints `ARRIVAL_{cs}` only when the own facility's `EnableArrivalStrips` is true
     (use a facility that has it on; OAK ATCT has it off — assert both);
  3. `SEP W OAK/Ground 1/1/1 Foo` and `HSC OAK/Ground 1 a\b` via `Actions.IssueLive` on the bare host create the items and
     bake `RecordedCommand.StripId`; re-applying the record over the same engine creates nothing (today: refused);
  4. `WAIT 1 AN 1 ✓` preset on a strip-bearing aircraft applies inside the engine after one tick (no host involved);
  5. `SCAN` to an external bay: message carries the "no controller connected" warning with an empty `Attendance` and not
     once a position in the receiving facility is attended (`AttendanceTestSupport.Attend`);
  6. amendment: `FP` on a departure whose `STRIP_{cs}` is in a bay reprints under a fresh id; the record carries it; a
     replay of that record over an engine already holding the id prints nothing (the rewind-across-amendment pin, plus
     the server `StripIdBakingTests` shape: rewind past an `FP` → exactly one printer copy);
  7. the capturing host sees changed ids then a full-state flag in that order for a print.
  Mutation: remove the `Items.ContainsKey` guard in the amendment reprint → test 6 red; remove the `IsExternal` check in
  `SCAN` → the existing server `StripScanCommandTests` red.
- **Server**: the strip suites (`StripIdBakingTests`, `StripTdlsRewindTests`, `StripReconstructionClockTests`,
  `StripRequestRecordingTests`, `HalfStripCommandTests`, `CrcStripDispatchTests`, `DeferredStripDispatchTests`,
  `StripScanCommandTests`, `IssueStripsAddressByIdTests`, `Issue391…`, `Issue277…`, `LinkedFacilityStripTabTests`,
  `StripPrinterAnnotateGuardTests`, `ArrivalStripConfigGateTests`, `StripMutationsTests`) keep passing on the moved
  bodies; `StripMutationsTests` may move to Yaat.Sim.Tests as-is (it already has no harness).

### Oracle prediction for B

`actions` live-vs-test / live-vs-replay lose the rest: `Server.Strips.Items[*]` (+ `.AircraftId/.BayId/.FieldValues[*]/.Id/.Index`),
`Server.Strips.DeparturePrinterQueue[*]` (t=50, the scripted `FP`), `Server.Strips.BayRacks[*].Columns[*][*]` (t=110);
`s2oak4` live-vs-test / live-vs-replay lose their `Server.Strips.*` paths (t=271). **After B every one of the twelve
baselines is `Entries: []` or byte-identical; nothing `Added`.** The amendment-id fix is invisible to the fixture by
construction (a Guid is not a seeded draw; the two live passes never agree) — `StripStepTests` 6 is its pin, and the
fixture doc already names the blind spot.

### Corpus risk to triage (ADR 0004)

Sim replay E2E tests and client playback now build strip and TDLS state. A recorded snapshot series that carries strips
now matches the replay instead of being overwritten at each restore — not a desync. A Sim test that asserted "the bare
engine never prints a strip / never queues a PDC" (grep `PendingStripDispatches`, `StripDispatchRequested`,
`DeferredPresetStripAndTrackDispatchTests`) is over-broad → fix the test. A bare test that now hits "No own strip bay for
current position" because it never set `StudentPosition` is behaving honestly — leave it.

### Docs for B

`docs/flight-strips.md` (§Overview's "yaat-server persists strip state", §State model's "still yaat-server's", the
preset/deferred paragraph — the Sim applies them itself, §Manual strip requests, the `RoomHost.ApplyStrip` sentence),
`docs/tick-loop.md`, `docs/command-pipeline.md` / `docs/command-handlers.md` if they name the strip slot,
`docs/snapshots-and-replay.md` (record list: `RecordedAmendFlightPlan.StripId`), `docs/architecture.md` both repos,
`04-relocation.md` (predicted-vs-got; tick the `IActionHost` item's strip/TDLS half; list the remaining slots:
coordination, ASDE-X/SAID, CRR groups, bookmarks, transport), `05-retirements.md`, `tick-path/README.md` status line,
`MAIN.md` (Current-focus line → next slot family; delete the amendment-reprint backlog item; keep the deferred-`SCAN`
item), CHANGELOG **Fixed**: "Rewinding across a flight-plan amendment no longer stacks a duplicate departure strip."
**Changed**: "Replaying a recording in the client now rebuilds the flight strips as the live session printed them."

---

## Execution

Orchestrator/implementer split per the global workflow: A is one implementer brief (worktree, files, change, proving
commands), then gate, `csharp-reviewer`, docs, commit; then B the same (B is large — the brief may bundle steps 1–5 and
6–7 as two consecutive briefs in the same tree if the first report is clean, but one commit). Aviation review: skipped
(no aviation behaviour; verbatim bookkeeping moves) — say so in the commit's plan note. Never run a reviewer while the
implementer is still writing the tree.

## Verification

- Per commit: `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log`; red-first on the new Sim suite
  (`timeout 30 dotnet test -- --filter-class "*TdlsStepTests"` / `"*StripStepTests"`), then green; `timeout 120 dotnet test -- --filter-class "*TickOracleTests"`
  with the prediction above — rebaseline only after the diff matches it (`YAAT_ORACLE_REBASELINE=1`), review the diff,
  check the per-entry `FirstSecond`s by hand (the assertion keys on the path set); `SpineTraceTests` pin updated;
  `pwsh tools/test-all.ps1` green cross-repo.
- End state: `IActionHost` slots = coordination (2), ASDE-X (3), SAID, CRR, bookmarks, transport — no strip/TDLS member;
  `IHostSteps` step-4 list = `LiveTrafficSync`, `CoordinationTimers`, `TowerLists`; `docs/tick-oracle/*.baseline.json`
  carry no `ApplyTdls` / `AfterAircraftSpawned` / `ReprintDepartureStripAfterAmendment` note; a bare-engine test prints a
  strip and sends a PDC with no server in the process; a client-side replay of a recording with `TDLSS` shows the PDC
  `Sent` in the engine's `Tdls`.
