# Tick-path step 4 (second slice): strips + TDLS state into Yaat.Sim and the snapshot

## Context

`docs/plans/MAIN.md` → current focus → step 4 (relocation) → next unchecked item in `docs/plans/tick-path/04-relocation.md:14`:
**`FlightStripState` / `TdlsState` into the snapshot, retiring `RoomStateSnapshotMapper`'s duplicate** (ADR 0003: "Strip and
TDLS state gain Yaat.Sim snapshot coverage; yaat-server's session-persistence DTOs for the same state collapse into it").

**The problem today (verified this session).** Both classes are room-owned in yaat-server (`TrainingRoom.cs:151-152`) and in no
Sim snapshot. Consequences:
- A same-room **rewind never touches strips or TDLS** — `ReloadForRewindAsync`/`PopulateRoom` only *add* rack slots
  (`FlightStripState.InitializeFromArtcc` is documented "existing bay contents are preserved"), so a rewind to before a print keeps
  the strip and never un-broadcasts it; a from-scratch reconstruction (bundle export, oracle) starts empty and rebuilds strips
  from the recorded requests and the host auto-print steps, but rebuilds **no TDLS at all** (`RoomHost.ApplyTdls` refuses while
  `Replaying`, comment: "TDLS state is in no snapshot").
- **Restart never resets them either** (`RecordingManager.RestartScenarioAsync` has no strip/TDLS line; only
  `ExecuteUnloadScenario` calls `Reset()` + the empty full-state broadcast) — the server half of #424 ("after a reload the strip
  bays should return to the scenario's starting strips").
- The `ADD`/`SPAWN`/`GHOST` arm's `OnAircraftSpawned` skips the spawn hooks (`AfterAircraftSpawned`: PDC auto-queue + departure
  strip print) while `Replaying` (`RoomHost.cs:183-190`), so a reconstruction lacks the strip and PDC of every `ADD`ed aircraft —
  invisible today because the oracle diffs `CaptureSnapshot` output only. The generator/delayed-spawn path
  (`HandlePrePhysicsResult` → `BroadcastSpawnedAircraft` → `AfterAircraftSpawned`) already runs the hooks on every RoomHost.
- Session persistence carries a hand-synced duplicate: `RoomStateSnapshotMapper.CaptureStrips/RestoreStrips/CaptureTdls/RestoreTdls`
  + `FlightStripStateSnapshotDto`/`StripItemSnapshotDto`/`StripBayRackSnapshotDto`/`TdlsStateSnapshotDto`/`TdlsItemSnapshotDto`/
  `TdlsDumpedSnapshotDto`/`TdlsActiveOpConfigSnapshotDto` in `RoomStateSnapshotDto.cs`, pinned by two mapper-level tests in
  `SessionPersistenceTests` (`TdlsState_RoundTrip_…`, `FlightStripState_RoundTrip_…`).

**Type inventory.** `FlightStripState` (`Gate`, `Items: ConcurrentDictionary<string, StripItemRecord>`,
`Bays: ConcurrentDictionary<string, Dictionary<string, List<string>[]>>`, `DeparturePrinterQueue`, `ArrivalPrinterQueue`,
`NextBlankId`) and `StripItemRecord` (9 primitives) reference nothing server-side — they cross verbatim. `TdlsState` (`Gate`,
`Items`, `Configs: TdlsConfig` (already Yaat.Sim), `ActiveOpConfigIds`, `Dumped: HashSet<DumpedKey>`, `ScheduledWilcoAt`,
`NextItemId`) crosses verbatim except `TdlsItemRecord`, whose `Status: TdlsStatus` and `SentPayload: ClearanceDto?` are
yaat-server wire types (`CrcDtos.Tdls.cs:10`, `CrcDtos.FlightPlan.cs:38`; `ClearanceDto` is in `messaging-contract.json` as
`FlightPlanDto.Clearance`). Per ADR 0003 those stay put and the Sim gets a core model with a server-side wire projection.

**Decisions taken with the user 2026-09-07** (AskUserQuestion): (1) #424's remaining halves ride along as sub-commit B —
restart carries user-placed separators over, Leave Room clears the client strip/TDLS views; (2) `ScheduledWilcoAt` **is**
snapshotted (it runs on `SimTimeUtc` since the session-clock work, so it is deterministic; the "restored items sit at Sent"
caveat goes away); (3) reconstruction fidelity — the TDLS replay refusal lifted, the spawn hooks on every run kind — lands as
sub-commit A2, red-first against the oracle entries A1 banks.

**Decisions taken here (stated, not asked):** the state is **engine-owned** (`SimulationEngine.Strips` / `.Tdls`, like
`Attendance`): a fresh engine starts empty, so restart/rewind reset without hand-clearing, and `CaptureSnapshot` covers it with no
host hook. It is captured in the **`Server` section** (`ServerSnapshotDto`, "engine-level state outside the aircraft list and the
scenario"), not `ScenarioSnapshotDto` as the plan line says — strips are session state, not `SimScenarioState`. Schema 21→22 is a
documentation-only migrator entry (both fields default to null → restore empty, the established rule). `TrainingRoom.StripState` /
`.TdlsState` are **deleted**, not forwarded (replace, don't shim): the ~200 server call sites read `room.ActiveSim!.Strips` /
`.Tdls` where a scenario is already asserted (every handler and tick body dereferences `ActiveScenario!`), and the handful of
no-scenario readers (`TrainingHub.JoinRoom` → `SendInitialStateToClientAsync` ×2, `RequestFullTdlsState`, `GetTdlsFacilityView`,
the CRC `FlightStrips` subscribe in `CrcClientState.Strips.cs:401`, `ExecuteUnloadScenario`'s empty full-state push) send an
explicit empty DTO when `ActiveSim is null`.

**Discipline (tick-path README).** Predict the baseline changes before `YAAT_ORACLE_REBASELINE=1`; an unpredicted `Added`/`Removed`
stops the work; corpus triage per ADR 0004; TDD red-first; `pwsh tools/test-all.ps1` green cross-repo before every commit; each
sub-commit records predicted-vs-got in `04-relocation.md`. Orchestrator/implementer split: each sub-commit is one implementer
brief (worktree, files, change, proving commands). This plan file is copied to `docs/plans/tick-path/04b-strips-and-tdls-snapshot.md`
(the 04a precedent) as step 0, linked from `04-relocation.md` and `README.md`.

---

## A1 — the state crosses, engine-owned, snapshotted; the persistence duplicate collapses (one commit, both repos)

### Yaat.Sim

1. **`src/Yaat.Sim/Simulation/Strips/FlightStripState.cs`** — `FlightStripState` + `StripItemRecord` moved verbatim from
   yaat-server (`Yaat.Sim.Simulation.Strips`). `InitializeFromArtcc(IEnumerable<StripBayConfig>)` and `Reset()` unchanged.
2. **`src/Yaat.Sim/Simulation/Tdls/TdlsState.cs`** — `TdlsState`, `TdlsItemRecord`, `DumpedKey` moved verbatim
   (`Yaat.Sim.Simulation.Tdls`) with two re-modelled members: `TdlsItemRecord.Status: TdlsItemStatus` (new Sim enum
   `Pending = 0, Sent = 1, Wilco = 2`, same file) and `SentPayload: TdlsClearance?` (new `sealed record TdlsClearance(string? Expect,
   string? Sid, string? Transition, string? Climbout, string? Climbvia, string? InitialAlt, string? ContactInfo, string? LocalInfo,
   string? DepFreq)` — the nine fields of the canonical `TDLSS` payload, in `Simulation/Tdls/TdlsClearance.cs`).
   `TdlsState.Reset()` keeps clearing everything (unload); `ResetSession()` is **not** added — a fresh engine is the reset.
3. **`SimulationEngine`**: `public FlightStripState Strips { get; } = new();` and `public TdlsState Tdls { get; } = new();` beside
   `Attendance` (`SimulationEngine.cs:82`), with the ownership doc ("engine-owned: a fresh engine starts empty; the snapshot
   carries it; the mutation bodies are still the room's — step-4 debt").
4. **Snapshot** (`Simulation/Snapshots/`): `ServerSnapshotDto.Strips: FlightStripSnapshotDto?` and `.Tdls: TdlsSnapshotDto?`.
   DTOs ported from `RoomStateSnapshotDto.cs:40-104` into `Snapshots/FlightStripSnapshotDto.cs` / `TdlsSnapshotDto.cs`:
   `FlightStripSnapshotDto { Items, BayRacks, DeparturePrinterQueue, ArrivalPrinterQueue, NextBlankId }`,
   `StripItemSnapshotDto`, `StripBayRackSnapshotDto { BayId, RackKey, Columns }`; `TdlsSnapshotDto { Items, Dumped, NextItemId,
   ActiveOpConfigs, ScheduledWilco: List<TdlsScheduledWilcoDto> }` (**new**: `ItemId`, `DueUtc`), `TdlsItemSnapshotDto`
   (`Status: int`, `SentPayload: TdlsClearance?`), `TdlsDumpedSnapshotDto`, `TdlsActiveOpConfigSnapshotDto`. `Configs` stays out
   (re-derived from the ARTCC at load, as today); `Gate` out.
   Mappers `Snapshots/FlightStripSnapshotMapper.cs` (`Capture(FlightStripState)` / `Restore(FlightStripState, dto)`) and
   `Snapshots/TdlsSnapshotMapper.cs` — the four bodies from `RoomStateSnapshotMapper.cs:161-327` verbatim, plus
   `ScheduledWilcoAt` in both directions; restore is replace (clear + rebuild) under the gate. `CaptureServerSnapshot` /
   `RestoreServerSnapshot` (`SimulationEngine.Snapshots.cs:325-429`) call them; a null section restores empty (pre-feature).
   `SnapshotSchemaMigrator.CurrentSchemaVersion` 21→22 with the documentation entry (no transform).
   `ReplayDriver.RangeCore` (`ReplayDriver.cs:103-107`): `_engine.Strips.Reset(); _engine.Tdls.Reset();` at `startSeconds == 0`
   beside the selections/attendance clears (a reused engine must not carry strips into a fresh replay).
5. **Docs comment** on `IActionHost` (`IActionHost.cs:13-19`): strips and TDLS *state* has crossed and every run kind carries it;
   the slots remain because the *bodies* (`StripMutations`, `TdlsMutations`, the handlers, the auto-print/TDLS host steps) are
   still the room's.

### yaat-server

6. Delete `Simulation/FlightStripState.cs` and `Simulation/TdlsState.cs`; delete `TrainingRoom.StripState` / `.TdlsState`;
   every reader/writer moves to `room.ActiveSim!.Strips` / `.Tdls` (files: `StripMutations.cs` (26), `StripCommandHandler.cs`
   (61), `TdlsCommandHandler.cs` (20), `TickProcessor.cs` (15), `RoomEngine.cs` (10), `TdlsBroadcaster.cs`, `StripBroadcaster.cs`,
   `ScenarioLifecycleService.cs`, `CrcClientState.Strips.cs`, `TrainingHub.cs`, `CrcBroadcastService.cs`, `ArtccConfigService.cs`,
   `CrcDtos.Tdls.cs` comment). No-scenario readers (listed under Decisions) branch on `ActiveSim is null` → empty
   `FlightStripsStateDto` / `TdlsStateDto`. `StripMutations`/`TdlsMutations` signatures keep taking the state instance.
7. **Wire projection**: `DtoConverter.ToTdlsItem` maps `TdlsItemStatus` → `TdlsStatus` (`(TdlsStatus)(int)`) and
   `TdlsClearance` → `ClearanceDto` (`DtoConverter.ToClearanceDto`); `TdlsMutations.ClearancePayloadFromFields` returns
   `TdlsClearance`; `TdlsMutations.MarkSent`, `TdlsCommandHandler.ValidateMandatoryFields` / `FormatPilotPdcMessage` /
   `FormatPdcRemarks` take `TdlsClearance`; status compares use `TdlsItemStatus`. `TdlsStatus` and `ClearanceDto` stay in
   `Yaat.Server.Dtos` untouched (wire-pinned).
8. **Persistence collapse**: `RoomStateSnapshotDto` loses `Strips`/`Tdls` and the seven strip/TDLS DTO classes;
   `RoomStateSnapshotMapper` loses the four bodies. `RestoreRoomFromArchiveAsync` needs no change — `RestoreFromSnapshot`
   (`SessionPersistenceService.cs:315`) now carries them, and `PopulateRoom` re-derived `Tdls.Configs` and the rack slots just
   before. `ExecuteUnloadScenario` (`:427-434`): the `Reset()` calls go (the engine is dropped at `:464`); the empty full-state
   pushes stay and must build from an empty state (see 6).
9. **Rewind / restart resync** (`RecordingManager.RewindAsync` after the `finally`, `RestartScenarioAsync` likewise): the
   clients still hold the abandoned run's strips and PDCs, so push `StripBroadcaster.BroadcastItemsAsync(all items)` then
   `BroadcastFullStateAsync`, and `TdlsBroadcaster.BroadcastFullStateAsync` (its DTO carries the items) — the same
   items-then-state order `AfterAircraftSpawned` uses. `RoomHost` header paragraph: a rewind *does* rebuild the strips now, and the
   room resyncs the clients afterwards.
10. `RoomHost.ApplyRecordedStripRequest` (`RoomHost.cs:167-176`) keeps its `!Replaying` broadcast gate in A1 (A2 revisits).
11. **Oracle** (`TickOracleTests` doc comment "Blind spots": strips/TDLS are in the snapshot now; the remaining blind spot is that
    no fixture scripts a strip or TDLS verb) — see the prediction below.

### Tests (red first)

- **Sim** `Simulation/Snapshots/FlightStripSnapshotMapperTests` and `TdlsSnapshotMapperTests` (the two server mapper tests
  moved, over the Sim types; `ScheduledWilcoAt` round-trips; a null section restores empty); `SimulationEngineStripSnapshotTests`:
  `CaptureSnapshot` → fresh engine `RestoreFromSnapshot` carries a strip in a bay, a printer-queue entry, a Sent PDC with its
  payload and its pending WILCO instant; `Replay(recording, 0)` on a reused engine starts empty; schema-22 snapshot JSON with no
  `Server.Strips` loads (the `sample-recording.json` pin pattern).
- **Server** `StripTdlsRewindTests` (over `autotrack-departure.json` / ZOA + student, the resource the session-clock pins
  used — `RequestFlightStripForAircraft` refuses without a resolved student position): (1) **rewind restores the target's
  strips** (red today): request a strip at t=5, tick to 10, `RewindAsync(3)` → no strip and the clients received an empty
  full-state (`CollectingTrainingBroadcast` or the hub mock — see what `RewindAutoTrackHandoffTests` asserts broadcasts with);
  `RewindAsync(7)` → the strip is back under the same id; (2) **rewind restores TDLS** (red today): `TDLSQ` + `TDLSS` at t=5
  → `RewindAsync(6)` → item `Sent` with its payload, and the auto-WILCO fires at the same sim second live did (t=8);
  (3) **restart empties the bays and pushes the empty state** (red today) — sub-commit B then refines this for separators;
  (4) `SessionPersistenceTests.SaveAndRestoreCheckpoint_…` gains a strip + a Sent PDC seeded before the save and asserted after
  the restore (the gap the survey found: no full-pipeline test carries strip/TDLS state).
- Existing server tests that seed `room.StripState`/`TdlsState` by hand move to `room.ActiveSim!.Strips`/`.Tdls` after a load
  (the TDLS suites already load `empty-oak.json` via `CreateWithOakData`).

### Oracle prediction for A1

All four rooms load through `PopulateRoom`, so the load-time strips and PDCs (every OAK departure gets a Pending PDC — ZOA's
OAK carries a `tdlsConfiguration`; a TWR student's departures print into the Ground bay) are on every leg at t=0. What differs
per second is the host-only bodies, so:
- **`actions` live-vs-test and live-vs-replay — `Added`**: `Server.Tdls.Items[*]` paths for every departure `TdlsTrackRemoval`
  removes on live once `Track.Owner` is set (the test/replay legs run no host steps), and from t=20 the `ADD V S P @NEW1`
  spawn hooks — `Server.Strips.Items[…NEW1…]`, the Ground-bay rack entry, `Server.Tdls.Items[TDLS_n]` + `NextItemId`
  (the bare engine and the Sim replay host have no spawn hooks). Notes name the body: `TdlsTrackRemoval` (host step) /
  `AfterAircraftSpawned` (spawn hook) — retired when the strip/TDLS bodies cross (the `IActionHost` shrink).
- **`actions` live-vs-reconstruct — `Added`** (the defect A2 fixes, and A2's red): the NEW1 departure strip paths (the
  reconstruction skips the spawn hooks while `Replaying`; no per-tick catch-up prints a tower departure strip) and the NEW1
  PDC's `CreatedUtc`/`ExpiresUtc` (the `ProcessAutoTdlsQueue` sweep queues it one second late on reconstruct).
- **`s2oak4` / `weather`**: `Added` on live-vs-test and live-vs-replay for `TdlsTrackRemoval` only if the scenario's departures
  acquire an owner inside the window (`Track.Owner` is set at load by `ApplyAutoTrackConditions` where the scenario has
  autotrack conditions, else when the FP-creator/deferred autotrack fires) — enumerate from the load-time census in the brief's
  first oracle run and attribute before rebaselining; live-vs-reconstruct byte-identical. **`autodelete`**: byte-identical
  (15 s, no departures reach a track owner — verify).
- Nothing `Removed` anywhere. Any `Added` outside this family stops the work.

### Docs for A1

`docs/snapshots-and-replay.md` (the `Server` section fields; the "room-owned state" paragraph: strips/TDLS are engine state now;
`ReplayDriver` reset list), `docs/flight-strips.md` (state model — fix the stale single `PrinterQueue` sample; ownership;
rewind resync), `docs/vtdls.md` (`§restore semantics`: `ScheduledWilcoAt` persisted, the Sent-caveat removed;
`TdlsItemStatus`/`TdlsClearance` core vs `TdlsStatus`/`ClearanceDto` wire), `docs/session-persistence.md` (`room-state.json.br`
shrinks; add the missing `terminal-log.json.br` row), `docs/tick-loop.md:171` (the "invisible until that state moves" claim),
`docs/architecture.md` both repos, `04-relocation.md` (predicted-vs-got), CHANGELOG **Fixed**: "Rewinding restores the flight
strips and PDCs as they were at the target time; restarting a scenario starts with the scenario's own strips."

---

## A2 — reconstruction fidelity: TDLS verbs and the spawn hooks on every run kind (one commit, yaat-server + Sim docs)

1. `RoomHost.ApplyTdls` / `ApplyTdlsOpsConfig`: drop the `Replaying` refusal and `TdlsNotReconstructed` — a recorded
   `TDLSQ`/`TDLSS`/`TDLSW`/`TDLSD`/`TDLSOPS` re-applies through the same body live used; the `TdlsBroadcaster` gates on
   `IsBroadcastSuppressed` already, and during tape playback the broadcasts are wanted (the rewind reset the clients).
2. `RoomHost.OnAircraftSpawned`: the spawn hooks (`TickProcessor.AfterAircraftSpawned`) run on every run kind; only the hub
   broadcast stays live-only — split `RoomEngine.AnnounceAircraftSpawned` into hooks + broadcast. Same for
   `ApplyRecordedStripRequest`: gate the broadcast on `IsBroadcastSuppressed` (playback broadcasts).
3. ADR 0007's "Deliberately not recorded" bullet on TDLS is superseded: TDLS verbs were always recorded (`RecordedCommand`),
   only refused on replay — note the supersession in `04-relocation.md`, not in the ADR.

Tests (red first, server): reconstruct-from-scratch (`CreateTempReplayEngine` + `ReconstructViaServerTick`) after `ADD V S P @X`
holds X's departure strip and Pending PDC with the live `CreatedUtc`; a recorded `TDLSQ`+`TDLSS` re-applies on a same-room
rewind past a snapshot (item `Sent`, payload equal) and on the temp room; during tape playback a replayed strip request reaches
the strips clients (`FlightStripsStateChanged` observed after `RewindAsync` + ticking through the request).

**Oracle for A2 (rewritten after A1 — see `04-relocation.md`).** A1 banked nothing on the reconstruct legs: no fixture
departure acquires a track owner in its window, and `ADD V S P @NEW1` is a VFR spawn with no flight plan, so neither the
TDLS refusal nor the `ADD`-arm spawn-hook skip is reached by today's scripts. A2 therefore **extends the `actions` script**
so the oracle proves it: an `ADD` that spawns an IFR KOAK departure with a filed flight plan (find the `ADD` grammar's IFR
form; `ExpectSuccess: true`), then `TDLSQ` and a valid nine-field `TDLSS` on it a few seconds later (the payload
`StripTdlsRewindTests.SendCanonical` uses). Predicted **before** the A2 code change (run once, read, do not rebaseline):
`actions` live-vs-reconstruct `Added` on the new aircraft's `Server.Strips.*` / `Server.Tdls.Items[*]` paths (spawn hooks
skipped while `Replaying`) and on its PDC's `Status` / `SentUtc` / `SentPayload` / `ScheduledWilco` (the recorded `TDLSS`
refused) — that run is A2's red. Predicted **after**: live-vs-reconstruct byte-identical again; `actions` live-vs-test and
live-vs-replay gain the same family as `Added` (the bare engine and the Sim replay host refuse `ApplyTdls` and run no spawn
hooks — banked with Notes naming `ApplyTdls` / `AfterAircraftSpawned`, retired when the bodies cross). Nothing `Removed`;
the other three fixtures byte-identical.

Docs: `docs/vtdls.md` (replay refusal gone), `docs/snapshots-and-replay.md` ("TDLS is refused while replaying" → applied),
`RoomHost` header, `IActionHost` slot doc for `ApplyTdls`, CHANGELOG **Fixed**: "Bundle reconstructions and rewinds now carry
the PDCs and strips of aircraft added during the session, and re-apply TDLS actions."

---

## B — #424: restart keeps user-placed separators; Leave Room clears the strip and TDLS views (one commit, both repos)

1. **Server** `RecordingManager.RestartScenarioAsync`: before `ActiveSim = null`, collect the separator records
   (`StripItemRecord.Type ∈ {2 HandwrittenSeparator, 3 White, 4 Red, 5 Green}`, `CrcDtos.Strips.cs:7-18`) with their bay/rack/
   index; after `ReloadForRewindAsync`, re-insert them into the fresh `Strips` through the existing rack-insert path
   `StripCommandHandler`'s `SEP`/`SEPE`/`SEPD` use (preserve ids; bump `NextBlankId` past them), then the A1 resync
   broadcast shows the scenario's initial strips plus the separators. Rewind is untouched (it restores the target faithfully).
   Test: place a separator + print a manual strip → restart → the separator is at the same bay/rack, the manual strip is gone,
   the scenario's initial departure strips are back.
2. **Client** `MainViewModel.ClearScenarioState` (`MainViewModel.Scenario.cs:652`): `ApplyBayConfig(null)` on every
   `StripsEntries[*].Vm` and the TDLS equivalent on every `TdlsEntries[*].Vm` (read `VTdlsViewModel` for its clear path), so
   Leave Room (`LeaveRoomAsync` → `ClearRoomState` → `ClearScenarioState`) empties the docked tab and the popped-out window
   (same VM instance, `MainWindow.axaml.cs:1360`). Test in `tests/Yaat.Client.UI.Tests/ViewModels/` beside
   `MainViewModelArtccAdoptionTests.ClearRoomState_…`: seed a strips VM with items via its transport events, `ClearRoomState()`,
   assert empty printer/bays/items and that a later `MoveStripAsync` is a no-op.
3. Docs: `USER_GUIDE.md` (restart keeps separators), `docs/flight-strips.md`, CHANGELOG **Fixed** (#424): "Leaving a room clears
   the Strips and TDLS views; restarting a scenario keeps only the separators you placed." Commit closes #424.

---

## Files (representative)

Yaat.Sim: `Simulation/Strips/FlightStripState.cs` (new), `Simulation/Tdls/{TdlsState,TdlsClearance}.cs` (new),
`Simulation/SimulationEngine.cs`, `SimulationEngine.Snapshots.cs`, `Simulation/Snapshots/{ServerSnapshotDto,
FlightStripSnapshotDto,TdlsSnapshotDto,FlightStripSnapshotMapper,TdlsSnapshotMapper,SnapshotSchemaMigrator}.cs`,
`Simulation/Replay/ReplayDriver.cs`, `Simulation/Actions/IActionHost.cs`, `tests/Yaat.Sim.Tests/Simulation/Snapshots/*`.
yaat-server: `Simulation/{TrainingRoom,RoomEngine,RoomHost,TickProcessor,StripMutations,StripCommandHandler,TdlsMutations,
TdlsCommandHandler,StripBroadcaster,TdlsBroadcaster,ScenarioLifecycleService,RecordingManager,DtoConverter}.cs`,
`Simulation/Persistence/{RoomStateSnapshotDto,RoomStateSnapshotMapper}.cs`, `Hubs/{TrainingHub,CrcClientState.Strips}.cs`,
`tests/…/SessionPersistenceTests.cs`, new `StripTdlsRewindTests.cs`, `Oracle/TickOracleTests.cs`, `docs/tick-oracle/*.baseline.json`.
Client (B): `ViewModels/MainViewModel.Scenario.cs`, `tests/Yaat.Client.UI.Tests/ViewModels/`.

## Verification

- Per sub-commit: `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log`; targeted
  `timeout 30 dotnet test -- --filter-class "*StripTdlsRewindTests"` red then green; `timeout 120 dotnet test -- --filter-class
  "*TickOracleTests"` — read the first run's `Added` against the prediction, attribute every entry, then
  `YAAT_ORACLE_REBASELINE=1` and diff `docs/tick-oracle/`; `pwsh tools/test-all.ps1` green cross-repo before each commit.
- End state: `RoomStateSnapshotDto` has no strip/TDLS members; `TrainingRoom` has no `StripState`/`TdlsState`; a bundle's
  `snapshot-*.json` carries `Server.Strips`/`Server.Tdls`; `RoomHost` has no `TdlsNotReconstructed`; a rewind in the running
  client shows the target's strips and PDCs; Leave Room empties the Strips tab.
- Aviation review: none owed (state relocation and reconstruction plumbing; no pilot/ATC behaviour changes) — `csharp-reviewer`
  after each sub-commit per the 04a precedent.
