# Snapshots, Recording, and Replay

> Read this before touching anything in `src/Yaat.Sim/Simulation/Snapshots/` or `src/Yaat.Sim/Simulation/Replay*` and before debugging a bug bundle. The bug-bundle skill (`tools/bug_bundle.py`) rides on this machinery.

## What gets serialized — `Simulation/Snapshots/`

`StateSnapshotDto` is the top of the tree:

```
StateSnapshotDto
├─ SchemaVersion (int)
├─ ElapsedSeconds, Rng (RngState), WeatherJson, FrequencyTransmissions?, Server?
└─ Aircraft[] : AircraftSnapshotDto
    ├─ identity + kinematics (callsign, lat/lon, true heading, alt, IAS, …)
    ├─ FlightPlan       — AircraftFlightPlanDto
    ├─ Transponder      — AircraftTransponderDto
    ├─ Ground           — AircraftGroundOpsDto    (Layout is JsonIgnore!)
    ├─ Track            — AircraftTrackDto
    ├─ Stars            — AircraftStarsStateDto
    ├─ Eram             — AircraftEramStateDto
    ├─ Approach         — AircraftApproachStateDto
    ├─ Procedure        — AircraftProcedureDto
    ├─ Pattern          — AircraftPatternDto
    ├─ Clearance        — AircraftClearanceDto
    ├─ HoldAnnotation   — AircraftHoldAnnotationDto
    ├─ Ghost            — AircraftGhostTrackDto
    ├─ Voice            — AircraftVoiceDto
    ├─ Targets          — ControlTargetsDto
    ├─ Queue            — CommandQueueDto
    └─ Phases (Phase chain) — polymorphic PhaseDto[]
```

`AircraftState.ToSnapshot()` and `FromSnapshot()` are hand-written constructors. Each sub-object owns its own `ToSnapshot/FromSnapshot` pair so changes localize.

## Schema versions — `SnapshotSchemaMigrator.cs`

| Version | Change |
|---|---|
| 1 → 2 | Added `ServerSnapshotDto` (null-safe, no transform) |
| 2 → 3 | Added `AircraftFlightPlanDto.CreatedByOwner` (null-safe) |
| 3 → 4 | Split actual vs filed aircraft type — seed `FlightPlan.AircraftType` from top-level `AircraftType` |
| 32 → 33 | `AircraftGroundOpsDto.IsScriptedDeparture` replaced by `InitialCallup` (the `InitialCallupPlan`, written as its number): a scripted departure → `None`; an open call-up decision on an aircraft whose current phase is `AtParkingPhaseDto` → `StandCall`; anything else (an airborne arrival never decided) → `None`. The legacy field is read as `LegacyIsScriptedDeparture` and nulled, so a rewritten snapshot drops it. Added with clean defaults, no transform: `AircraftGroundOpsDto.SpawnTaxiway`, `PushedBackFrom`, `PushEndSpot`, `PresetTaxiStop` (`PresetTaxiStopDto`: `Kind`, `Name`, `OnTaxiway`), `VfrDepartureDirection` (all null) and `ReleasedAtSpawnGate` (false); `PilotPendingRequestDto.Kind` gains `Clearance` (5) and `Release` (6) |
| 33 → 34 | `BlockTriggerDto.Radial` renamed `TrueRadial` (the true bearing from the fix) and `IssuedRadial` (the magnetic radial as issued) added: the legacy key is read as `LegacyRadial`, copied into `TrueRadial` (the old engine compared it as a true bearing, so old replays fire where they did), copied into `IssuedRadial` for a `ReachFrdPoint` trigger only (an `InterceptRadial`'s may be JAWY's true course), then nulled |
| 36 → 37 | `NavigationTargetDto.SpeedTransmissionId` added beside `CrossingTransmissionId`, which now answers for the crossing altitude only. Through 36 one stamp marked both, so every fix carrying a `CrossingTransmissionId` and a `SpeedRestriction` gets that id as its `SpeedTransmissionId`, in the live route, `DepartureClearance.DepartureRoute` and the routes `AirspaceBoundaryHold`, `InitialClimb` and `DepartureProcedure` keep (`MigrateLegacySpeedStamps`); an old CFIX speed is read as a controller's, never as a published speed |
| 37 → 38 | `ControlTargetsDto.SpeedMaxHeld` (SMAX in force) and `NavigationTargetDto.RevertSpeedMax` (a crossing fix past which SMAX is armed again) added, each written only when true; no aircraft before 38 could hold SMAX, so absence restores false and the step transforms nothing |
| 38 → 39 | `StateSnapshotDto.FrequencyTransmissionTimes` (start seconds) replaced by `FrequencyTransmissions` (`{Start, Duration}`); each old time becomes a 3 s transmission (`SnapshotSchemaMigrator.LegacyTransmissionSeconds`) |
| 39 → 40 | `FollowingPhaseDto.HasBeenOnClearedRunway` (a bool) replaced by `BeenOnClearedRunways` (a list, one clearance spent per runway); a true flag marks every cleared runway been-on, false or absent none (`SnapshotSchemaMigrator.MigrateLegacyBeenOnClearedRunway`); `BlockTriggerDto.FollowCrossingRunways` (a list or null: the runways a block chained behind a follower's `CROSS` waits for) added, null before V40; `FollowingPhaseDto.BrokenOff` (bool: a `HOLD` broke the follow off; false before V40) and `CrossingRunwayPhaseDto.EndsAtRest` (bool?, written only when true: the crossing brakes to rest past its far bar) added |
| 40 → 41 | `AsdexAlertKind` lost `TaxiOntoActiveRunway` (stored as 2; ClosedRunway=0, OccupiedRunway=1 and TaxiwayLanding=3 keep their numbers): a standing `AsdexSafetyAlertDto` of kind 2 restores as `OccupiedRunway` under its stored id, and the next tick's detector clears it by id and raises the pair's current alert. Every standing closed-runway alert (kind 0) is dropped, since the stored alert does not say whether its aircraft was an arrival (`RWY CLOSED, GO AROUND` and the `GoAround` aural) and a standing alert is never rewritten; the next tick raises it again under the same id with the right role, at the cost of one extra aural. Every other stored alert's third line moves to the vNAS text (`OCCUPIED RWY` and `RWY INCURSION` → `RWY OCCUPIED`), so an alert whose id the detector still raises shows it. `AsdexSafetyAlertDto.GoAround` is new; its absence reads false |

**Actual vs filed aircraft type (v4).** `AircraftState.AircraftType` is the physical type (fixed at spawn; drives Tower Cab, physics, and the operator Aircraft List). `AircraftFlightPlan.AircraftType` is the filed type (mutable via amendment; drives STARS, ASDE-X, the Flight Plan Editor, strips, and ERAM). Top-level wins for actual; the FP field is opt-in (no cross-fill). The migrator seeds the filed field from the top-level type for legacy v3 bundles.

**Optional fields added without a bump.** `ScenarioSnapshotDto.StudentLowestWorkedLevel` (the `AtcPositionLevel` number, left out of the JSON while null) and `AircraftGroundOpsDto.LeftStudentFrequencyAtSeconds`, `ClearanceObtainedAtSeconds` and `TakeoffRollStartedAtSeconds` (scenario-elapsed seconds, null while unset) restore null from an earlier snapshot: the student then answers every call type, and the simulated tower reads no transfer.

The level also rides the tape as a `StudentLowestWorkedLevel` setting change (the level code, `""` to clear), which the recorded-setting applier replays.

The table lists only the steps with a data *transform*; the authoritative full chain (up to `SnapshotSchemaMigrator.CurrentSchemaVersion`; v22 added `ServerSnapshotDto.Strips` / `.Tdls`, null-safe) is the `Migrate()` source. `Migrate` must tolerate leniently-deserialized nulls on the fields it touches — e.g. a legacy aircraft snapshot can carry a null `FlightPlan`, so the v3→v4 seed guards with `ac.FlightPlan is { AircraftType: null or "" }` rather than dereferencing.

**Renaming a field is a bump even when the step transforms nothing** — schema 24 renamed `SharedStateDto.IsQueriedUntil` (a wall-clock `DateTime?`) to `QueriedUntilElapsedSeconds`; the legacy property is skipped on read and the new one restores null (an old recording shows no query flash rather than one that expired with the recorded session), and the step exists so the version names the change.

**Rule for adding a field**: if it defaults cleanly (`null` / `false` / `0`) and old data is correct under that default, **no migration step needed**. Just add it to the DTO. If old data needs transformation (rename, split, reinterpret), bump `SchemaVersion` and add a `Migrate()` step.

**A field left out while empty keeps old snapshots byte-identical.**

`PhaseListDto.GivenUpExitTaxiways` (the taxiways the crew has said "unable" to on this landing — [landing-and-runway-exit.md](landing-and-runway-exit.md) § The given-up set) is `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`: written only when the set is non-empty, and then sorted (`StringComparer.OrdinalIgnoreCase`) so two runs holding the same set in a different hash order serialize identically; absent on read means none given up.

No schema bump: nothing is given up in snapshot 0, whose hash yaat-server pins. The same additive, no-bump pattern covers `LandingPhaseDto.UnableBroadcast` and `OriginalPreferenceIsRequestedExit` (false when absent) and `RunwayExitPhaseDto.TurnOffDecelRate` (null when absent: the taxi rate older builds turned off at) and `RunwayExitPhaseDto.InferredSideValue` (the default exit side `ArrivalExitSide` resolved; null when absent, which restores no side).

`ControlTargetsDto.SpeedLimitsLifted` (`bool`, `WhenWritingDefault`) carries `ControlTargets.SpeedLimitsLifted`, the SPDN lift of 91.117 and Vmo/Mmo ([flight-physics.md](flight-physics.md)). It is written only when true, so every older snapshot and its hash are unchanged and restore false; no schema bump.

`StateSnapshotDto.FrequencyTransmissions` (`List<FrequencyTransmissionDto>?` of `{Start, Duration}`, `WhenWritingNull`) carries the active frequency's `FrequencyActivityMeter` intervals in recorded order, so a rewind or restore resumes in the load band the run was in ([solo-training-pilot-speech.md](solo-training-pilot-speech.md) § the activity meter).

`CaptureSnapshot` writes it only when the meter holds a transmission, so a snapshot of a silent frequency omits the field and the pinned snapshot-zero hash stays put.

`RestoreFromSnapshot` replaces the meter's intervals with the field (absent restores an empty meter), without trimming them: the next `Trim` trims, as the original run would have. Only the active frequency's meter is captured.

`PhaseListDto.TaxiRoute` (a `TaxiRouteDto`) is the route a TAXI on the landing rollout stores behind the exit ([landing-and-runway-exit.md](landing-and-runway-exit.md) § TAXI on the rollout), null otherwise; `PhaseListDto.StoredTaxiDestinationRunway` is the destination runway that TAXI named, `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` and restored only with a route.

`PhaseListDto.RequestedExitIsTaxiImplied` (true, not written while null) marks `RequestedExit` as the exit that TAXI implied (`PhaseList.TaxiImpliedExit`), and `LandingPhaseDto.ActivePreferenceIsRequestedExit` (the same, for the landing's active possibly-relaxed preference), so a restore shares the instance again and a go-around still tells the implied exit from the controller's own.

`LandingPhaseDto.SideTowardTaxiRoute` is true while `InferredSideValue` points toward the stored route and `SideBeforeTaxiRouteValue` is the side it replaced, given back when the route is dropped. All are additive and omitted while null, so snapshot 0 is unchanged.

`HoldShortPointDto.RecrossesLandingRunway` (true, omitted otherwise) is the re-crossing of the landing runway on that route, which auto-cross never clears. A restore with no ground layout, or whose route nodes are not in the layout, drops the stored route and logs.

`TaxiingPhaseDto.PassedStartBarNodeId` is another such field: the node of the uncleared runway bar the route starts on whose marking the nose is past, which the phase is stopping for at the firm rate ([ground/navigator.md](ground/navigator.md) § Stopping at an uncleared bar). It is null whenever no such stop is under way, and null on older snapshots, which never carried one; no schema bump.

Four fields carry the transmission id a DVIA uses to tell a crossing restated in its own transmission from an earlier one (see **Transmission id** and **Speed stamp** in [`../CONTEXT.md`](../CONTEXT.md)), each written only at a non-default value and absent on older snapshots: `NavigationTargetDto.CrossingTransmissionId` (int?, the transmission whose CFIX or constrained DCTF stamped the fix's crossing altitude), `NavigationTargetDto.SpeedTransmissionId` (int?, the speed stamp: the transmission that set the fix's speed; added in schema 37 with a migration step because through 36 the crossing stamp also marked the speed; the other three need no bump), `CommandQueueDto.LastTransmissionId` (the aircraft's counter, so a restore followed by the same commands hands out the same ids), and `CommandBlockDto.TransmissionId` (so a restored block's rebuilt `ApplyAction` stamps the id the live dispatch would have).

Four `AircraftProcedureDto` fields carry the via clearances and the speed memory, omitted at the default and absent on older snapshots, with no schema bump: `ClimbViaTargetFt` (int?, the top altitude the climb via flies to; null restores no climb via), `StarViaTransmissionId` / `ClimbViaTransmissionId` (int?, the transmission whose DVIA or CVIA is in force, so a CFIX in that transmission restored mid-chain still reads the via clearance as its own), and `LastCrossedPublishedSpeedKts` (double?, the last published speed crossed on the procedure; an older snapshot restores no memory, so a resume holds no ceiling until the next published speed is crossed).

`HoldShortPointDto.StopMoved` (bool?, written only when true, absent on older snapshots; no schema bump) latches that the taxi phase has moved an unable bar's stop forward, so a restored taxi does not move it again. The latch lives on the bar rather than the phase, so `TaxiingPhaseDto` carries no unable-stop node.

`HoldingInPositionPhaseDto.NotClearReportPositions` (list of strings, written whenever `TailInsideRunway` is set, otherwise null; absent on older snapshots, which restore with none; no schema bump) is the student positions the hold's not-clear report goes to in solo training: Tower for a `CLRWY` hold, Ground and Tower for a hold after a break-off.

`HoldingInPositionPhaseDto.EndsBrokenOffFollow` (bool?, written only when true, absent on older snapshots; no schema bump, still 40) marks the hold a `HOLD` that broke off a `FOLLOWG` follow ends in, so a restored aircraft still refuses `RES` there (`GroundCommandHandler.RefusesResumeTaxi`); a lead-gone hold in position is not marked.

`HoldShortPointDto.EndsNoDestinationRoute` (bool, written only when true, absent on older snapshots; no schema bump) carries `HoldShortPoint.EndsNoDestinationRoute`, the flag on the hold that ends a clearance with no destination short of its last taxiway, so a restored aircraft still refuses `RES`/`CROSS` there.

`ScenarioSnapshotDto.ActiveRunways` (the room's active runway ends per airport, as `ActiveRunwayListParser` tokens: `28L` both, `D28L` departures, `A28R` arrivals) is the one nullable member of that DTO left out while empty; its other nullable members are written as null.

A snapshot with no active runways stays byte-identical, and an older one restores none; no schema bump. Restore goes through `ActiveRunwayListParser.FromTokenLists`, which logs and drops a malformed token instead of failing the load.

The snapshot holds the current list, not the starting one.

The starting list (`SimScenarioState.InitialActiveRunways`) rides the recording manifest (`RecordingManifest.InitialActiveRunways`, airport → token list, `SessionRecording.InitialActiveRunways` for a legacy recording) and the room checkpoint manifest, and is read back with `ActiveRunwayListParser.ReadStored`; a rewind, export reconstruction, recording load or checkpoint restore reloads on it, so a rewind to before an `ARWY` shows the list the session began with.

An archive that stored none (null) has the start value worked out again from the sidecar or the implied guess.

`GroundNavigatorPlaybackDto.PendingTurnAboutArc` and `TurnAboutReversalPlaying` follow the same pattern for a turn about on a taxiway ([ground/navigator.md](ground/navigator.md) § Entry-alignment threshold). `PendingTurnAboutArc` is the reversal arc parked while the jog that centres it plays, and `TurnAboutReversalPlaying` is true while that reversal plays, so its end-of-arc nudge keeps the arc's own radius.

Four more carry what the straight after the turn about is laid as ([ground/navigator.md](ground/navigator.md#after-a-turn-about-on-a-taxiway) § After a turn about on a taxiway). `TurnAboutReversalOnEdgeBearing` is true while the jog and reversal of a turn about rolled out on the edge's own bearing play, so a straight after it that ends in a stop is laid on the centreline through the stop.

`TurnAboutRollsOutAlongEdge` is true over the same span when that straight holds the roll-out bearing to abeam the node instead.

`TurnAboutRollOutOffsetFt` is how far that held straight runs off the centreline inside the coming turn, kept through the node turn it ends in: it sets the straight's arrival point and keeps the node turn's end-of-arc nudge off. `TurnAboutSquareStopLine` is true while the straight is laid on the centreline through a stop and steered past the stop in its last look-ahead window.

All six are written only when set and absent (null) otherwise; no schema bump.

`GroundNavigatorPlaybackDto.TurnAboutHold` (`TurnAboutHoldDto`) carries the stop an aircraft holds at when it has no room to turn about, rolling or at rest short of a runway hold line: the stop point, bearing and brake rate, the taxiway named in its unable (null when it stands on none), whether it has said unable (`UnableSaid`), and `Runway`, the runway whose hold line governs the stop, written only when set (`WhenWritingNull`, additive, no schema bump) and absent when the stop is not at a runway bar.

A restore neither re-plans the stop nor says unable again, and a snapshot written without `Runway` restores a hold that asks for a route ahead rather than naming a runway ([ground/navigator.md](ground/navigator.md#a-rolling-turn-about-with-no-room)).

A restore that kept no latch would let the stop go once the aircraft had slowed too far from the node to find it again.

`AircraftGroundOpsDto.TaxiEdgeTrail` follows the same pattern: the taxi edges a ground aircraft drove, oldest first (`TaxiTrailEdgeDto`: `NodeA`, `NodeB`, `LengthFt`, `EntryNodeId`, the end node the edge was entered from; additive, no schema bump, and a restore throws `InvalidDataException` on an entry node that is neither end of its edge, as a snapshot written without it loads 0), written only when the trail is not empty, and restored as an empty trail when absent.

The aircraft's own `AircraftGroundOps.TaxiEdgeTrail` is `[JsonIgnore]`; this DTO field is its carrier.

`AircraftGroundOpsDto` carries the ground conflict detector's cross-pass state the same additive way (no schema bump; each is written only when non-default, so a snapshot with none set is byte-identical to an older one, and an older snapshot restores them false or 0): `CrossingStopCurveCarry` (the crossing stop curve limited the aircraft last pass, so the next pass gives it the curve again), `CrossingStopCurveBoundClosureTravelFt` (the travel of traffic the curve counted since the carry began, which decides when a crosser held at its ground speed is released) and `BrakedByDetectorLastPass` (the detector's limit was below the aircraft's ground speed). A replay that dropped them would brake a carried crosser differently, so they round-trip bit-exact ([conflict-and-visual-detection.md](conflict-and-visual-detection.md)).

`FollowingPhaseDto` carries the `FOLLOWG` follow's graph state the same additive way, with no schema bump ([ground/navigator.md](ground/navigator.md) § FOLLOWG: driving the follow route):

- `FollowRoute` (a `TaxiRouteDto`: the follower's route to the merge node, then the lead's path) and `MergeSegmentIndex` (the first segment on the lead's path), null and 0 before the follow planned one.
- `LeadEdgeIntoMerge` (a `TaxiSegmentDto`, the lead's path edge into the merge, which the give-way stop keeps clear of with the edge out), written as the route's own segments are; null where the lead's path starts at the merge.
- `GivingWay`, `Unjoinable` (the hold-for-good latch: a restored follow holds where the live one held rather than planning again) and `ExitingRunways` (the runways whose bars it passes on the way out).
- `BrakingLostRoute` (`bool?`, `FollowingPhase.IsBrakingLostRoute`: braking to rest along a follow route a lead re-route lost, which the follower drops once at rest): true while braking, else null and not written (`JsonIgnore` `WhenWritingNull`), so a snapshot of a follow not braking along a lost route serializes byte-identical to one written before the field existed; absent restores as false.
- `LeadWaitVerdicts` (`Dictionary<int, bool>?`, keyed by hold-short node id: whether the follower waits for its lead at each cleared runway bar judged in the phase's current whole second) and `LeadWaitSecond` (`int?`, that second): written only while a verdict is cached, so a restore mid-second stops on the same tick as the original run; absent restores as none cached.
- `LeadDepartureBarNodeId` and `LeadDepartureRunwayEnd` (the lead's latched departure bar and the runway end it departs on, e.g. "30"; both null until the follow has seen one, and written only then) and `LeadSeenDeparting` (`bool`, written only when true: a lead deleted after it was seen departing still counts as departed); absent restores as no latch and not seen departing.
- `ClearingRoute` (a `TaxiRouteDto`) and `ClearingBarNodeId`, null when not clearing, and `ClearingAttemptedRunways`, written only when a clearing route has been tried.
- `Navigator` (a `GroundNavigatorDto`), null with neither a follow route nor a clearing route.

Absent fields restore as no route and no latch, and a follow restored with no follow route, or one the layout no longer resolves, plans afresh on its next tick. Whether a follower past its clearing bar is braking to rest along the clearing route is not carried: the first tick after a restore reads it again from the follower's position.

**The run profile is not a field.** `SimulationEngine.RunProfile` (live / replay / test / soak — [tick-loop.md](tick-loop.md) § the engine's partial files) is host state: the host that drives the engine sets it, and it is never captured into or restored from a snapshot. Restoring a live snapshot into a replaying room must not make the room live.

**An "on by default" scenario setting still defaults to `false` on the Sim side.** `SimScenarioState.AutoCrossRunwayMode` (enum, `Off`) and `AutoPullUpToParallel` (bare `bool`, false) default off, the `PhaseContext` fallback is `Scenario?.X ?? false`, and the `ScenarioSnapshotDto` field is the same.

The on-by-default lives only in the client's `UserPreferences` (`AutoPullUpToParallel` defaults true there), which pushes the value to the server at scenario bootstrap (`MainViewModel.SendAutoCrossRunwayMode` / `SendAutoPullUpToParallel`).

**The auto-cross switch is a three-state mode in the snapshot.** `ScenarioSnapshotDto.AutoCrossRunwayMode` (Off / All / InactiveOnly) replaced the on/off `AutoCrossRunway`; `SnapshotSchemaMigrator` (V35) maps a snapshot's old `true` to `All` and `false` or absent to `Off`, then drops the legacy field. A recorded `RecordedSettingChange` named `AutoCrossRunway` replays the same way (`true` = All), and `AutoCrossRunwayMode` carries the mode name.

The reason is replay fidelity: `engine.Replay(recording, t)` builds a fresh `SimScenarioState`, so a Sim-side default of `true` would switch the feature on for every recording made before it existed and diverge those replays (aircraft auto-pulling up before the recorded `TAXI`/`RES`). A new opt-out toggle mirrors this pair end to end and flips only the client default.

### Upgrading recordings on disk — surgical, never re-simulated

`SnapshotSchemaMigrator.Migrate` runs at replay (`SimulationEngine.RestoreFromSnapshot`) so old recordings load without any disk change.

To rewrite committed fixtures to the current schema, `RecordingSchemaUpgrader.Upgrade(bytes)` (Yaat.Sim) transforms each snapshot's JSON **in place** via the same migrator — decompress → `StateSnapshotDto` → `Migrate` → re-serialize — across all three containers (non-zip `.br` `SessionRecording`, v4 archive zip, and bug-report bundle wrapping a v4 archive), copying every other entry verbatim.

The same pass rewrites recorded canonicals that the current grammar no longer accepts — the retired `HSE` half-strip verb becomes the `HSA` id form (`HalfStripEditCanonicalRewriter`) and bay tokens gain their `FACILITY/` qualifier (`StripBayCanonicalQualifier`); both are idempotent so they need no schema gate.

It **never re-simulates**; re-simulation with current code would rewrite a frozen pre-fix snapshot into the fixed state and silently invalidate hybrid-replay tests (see [e2e-tdd-issue-debugging.md §5b](e2e-tdd-issue-debugging.md)). The `Yaat.RecordingUpgrader` CLI (yaat-server) drives it and reserves re-simulation for the one v1→v2 bootstrap (v1 carries no snapshots to preserve).

Snapshot entries are Brotli. `RecordingCompression.Decompress` autodetects in a fixed order: gzip by its magic number, then Brotli, then plain UTF-8 JSON only when Brotli genuinely cannot read the bytes. It never sniffs the first byte for `{`/`[`, because a Brotli stream can legitimately start with those bytes and a first-byte heuristic misfires on it.

## What is NOT serialized

Some state is intentionally runtime-only:

- **`AircraftState.DeclinationCachePosition`** is `[JsonIgnore]` on the state but snapshotted: `AircraftSnapshotDto.DeclinationCachePosition` carries it (null, left out of the JSON, for a cold cache and for snapshots written before it existed), so a restored aircraft recomputes its declination where the original did.
- **`WeatherProfile.WindGrid`** is not serialized; the weather JSON carries `WindGridHash` and `WindGridRegistry.Attach` resolves the grid on parse and on snapshot restore. The grid bytes travel in the recording archive (`wind-grids/` below).
- **`Ground.Layout`** is `[JsonIgnore]`. Only `Ground.LayoutAirportId` round-trips. On restore, `SimulationEngine.RestoreFromSnapshot` re-resolves the layout from the airport ID against the engine's ground data — for each live aircraft (`ResolveAirportLayout(LayoutAirportId)`, falling back to the primary airport's layout when the snapshot carries no id or names a field with no map) and for each aircraft in the delayed-spawn queue alike.

  An aircraft bound to a secondary airport therefore comes back on that airport's layout, so its on-final and distance-to-threshold verdicts are the same before and after a rewind (`SnapshotRoundTripTests`). This avoids embedding an entire taxiway graph per aircraft.
- **`PendingObservations`** (pilot "watch for condition" state) — ephemeral, never restored.
- **`CommandBlock.ApplyAction` / `CommandBlock.ParsedCommands`** — the queued-block closure and its parsed
  commands are runtime-only; `SourceCommandText` is the durable carrier, with `IsScenarioScripted` (the creating dispatch was a preset or AI controller). `SimulationEngine.RehydrateRestoredQueueBlocks`
  (top of `TickPhysics`, shared by both hosts) rebuilds them by re-parsing that text, under the block's scripted flag, before the queue can fire, so a
  queued instruction survives rewind/replay/restore instead of firing as a silent no-op. An unrecoverable block is
  dropped with an RPO warning. No snapshot field carries `ParsedCommands` (`CommandQueueDto` has none; `CommandBlock.ToSnapshot` writes only `SourceCommandText`); a restored block gets it set again on rehydration. The JRADO, JRADI, DEPART and airway-intercept follow-up blocks restore the same way, through their own factories. See [command-handlers.md](command-handlers.md).

- **Room-owned state outside `Yaat.Sim`** — the ASDE-X / SAID wire projection the room keeps between ticks (`AsdexRoomState` / `SaidRoomState`: the coasting surface tracks' wire cache, ended by the Sim's disconnect-coast expiry and clear, and the per-tick termination drain), which no snapshot carries.

  The ASDE-X safety-logic configuration and the standing alert set are not on this list: both are scenario state, `SimScenarioState.AsdexSafetyLogicConfigs` / `.ActiveAsdexAlerts`, carried by `ScenarioSnapshotDto` (`AsdexSafetyLogicConfigs`, keyed by facility id; `ActiveAsdexAlerts`).

  **The safety-logic configuration is per facility in the snapshot.** `ScenarioSnapshotDto.AsdexSafetyLogicConfigs` (a facility-keyed map, null when empty) and `AsdexSafetyAlertDto.FacilityId` replaced the single scenario-wide `AsdexSafetyLogicConfig`; `SnapshotSchemaMigrator` (V36) files a legacy single config and its alerts under the snapshot's `PrimaryAirportId` with a leading `K` stripped from a four-letter id, or drops them with a warning when there is no primary airport.

  The next tick re-raises what a standing configuration still warrants.

  **The hold-bar statuses are transient engine state and are not snapshotted.** They are derived from the aircraft each tick (`SimulationEngine.TickAsdexHoldBars`), so a restore needs nothing stored: the next tick recomputes them and diffs against the statuses last reported, which the engine keeps across the restore.

  A rewind, restart or recording load rebuilds the engine with its changes suppressed, so the room re-sends the whole set and turns off every bar the abandoned run had lit (`RecordingManager.ResyncEngineStateAsync`).

  `ActiveAsdexAlerts` is a nullable list of `AsdexSafetyAlertDto`, ordinal-sorted by facility then id, absent ⇒ empty, and a restore replaces the set rather than merging into it, so the first tick after a restore diffs against what the displays were already told and does not re-announce it. The live-traffic `DEL` set is scenario state too, `SimScenarioState.SuppressedLiveTraffic` (`ScenarioSnapshotDto.SuppressedLiveTraffic`, ordinal-sorted callsigns, absent ⇒ empty, replaced on restore).

  So are the disconnect coasts, `SimScenarioState.DisconnectCoasts` (`ScenarioSnapshotDto.DisconnectCoasts`: a nullable list of `DisconnectCoastDto` in ordinal callsign order, each with its facets in registration order, absent ⇒ empty); a restore replaces the set and empties the pending cleared-callsign list, so a rewind drops a coast registered in the undone future. A facet carries `EramAnchor` (`EramSweepSampleDto`, the coverage coast an ERAM facet carries on) only when set.

  The ERAM coverage-loss coast is per-aircraft Sim state on `AircraftEramStateDto`: `IsVisibleOnEramCoverage`, `IsCoverageCoasting`, `CoverageCoastStartSeconds`, `CoverageCoastAnchor` and `LastEramSweep` (both `EramSweepSampleDto`: lat, lon, ground track, ground speed, sim second). Each is left out while null, so older data restores unevaluated and the schema version is unchanged.

  Facet selection is the Sim's, so every run kind (live, bare test, replay, reconstruction) computes the same facets. ASDE-X / SAID membership is per-aircraft snapshotted state on `AircraftStarsState` with its own spine step (`SurfaceMembership`), not a stateless range recheck, so the coast of an aircraft that disconnects inside the hysteresis band is kept.

  A spawn under a coasting callsign ends the whole entry in the Sim (`SimulationEngine.AfterAircraftSpawned`), even when it spawns at another airport than the facet's, and the server then ends every store entry it held for that callsign.

  What stays server-side in `RoomStateSnapshotDto` is identity and display state no tick body reads; state a tick body reads belongs in the Sim snapshot.

  Flight strips, the vTDLS session and the tower lists are *not* on this list. The tower lists are captured in `ServerSnapshotDto.TowerLists` via `TowerListSnapshotMapper`, schema 23 — the per-list dwell entries, so a restore keeps the P-list's entry order; the list airports are re-derived from the ARTCC at load and never snapshotted.

  `SimulationEngine.Strips` / `.Tdls` are engine-owned and captured in `ServerSnapshotDto.Strips` / `.Tdls` (schema 22; `FlightStripSnapshotMapper`, `TdlsSnapshotMapper` — items, bays (each rack with its `AutoSort` flag), printer queues, the dumped lockout, active ops configs and the pending auto-WILCO instants; `TdlsState.Configs` is re-derived from the ARTCC at load and never snapshotted).

  Nor is CRC attendance: the live room derives it from `PositionRegistry` and records it (`RecordedAttendanceChange`), the engine's `Attendance` is snapshot-covered, and every other run kind reads the recorded set.

  A fresh engine starts with empty bays, so a restart starts from the scenario's own strips and a rewind holds the target second's. Because **a live room never carries snapshots** (`LoadedSnapshots` / `SnapshotProvider` are set only when a recording archive is imported), every live-session rewind is a from-scratch reconstruction from t=0 that rebuilds only what the action log and the host's tick bodies reach.

  Recorded strip requests print under their baked id, and auto-printed strips are re-derived by the tick — with the same PDT/ETA text, because strip and TDLS times read `SimScenarioState.SimTimeUtc`, never the wall clock.

  Every spawn — scenario, generator, `ADD` — runs the strip/PDC hooks on every run kind, and a recorded TDLS verb re-applies through the same body live used.

  The room re-pushes the rebuilt strips and PDCs to its clients when the rewind lands (`RecordingManager.ResyncEngineStateAsync` — the CRC coordination lists too; a recording or archive load runs the same resync, so the abandoned run's strips and PDCs leave every client's screen); tape playback broadcasts each print as it happens, because the rewind reset the clients' view.

If you see `[JsonIgnore]` on a field, also check that there's a separate carrier (like `LayoutAirportId`) that lets restore reattach.

**A rebuilt route needs its cursor, not just its nodes.** Most navigator-owning ground phases don't serialize their `TaxiRoute` — it is rebuilt from stored node ids against the engine's layout (on a server archive load or session restore, the layout the archive bundles) on the first tick after restore — so the rebuild has to be told *where along it* the aircraft was.

`FollowingPhase` is the exception: its follow route and clearing route round-trip whole as `TaxiRouteDto`s, cursor included, and `FollowingPhase.FromSnapshot` rebuilds them over the layout `PhaseList` passes it.

`RunwayExitPhase` is the sharp case: its segment 0 is a virtual approach leg [aircraft position → branch node] down the runway centerline, so rebuilding from segment 0 for an aircraft that has already turned off hands `GroundNavigator` a leg pointing *backward*, and the ~180° entry-alignment slow-turn taxis the reconstruction back onto the runway it just vacated (issue #309).

`ExitWaypointIndex` round-trips for exactly this reason, `ResumeSegmentIndexAfterRestore` floors it at the first real segment whenever the aircraft is already past the branch (a snapshot can land on the tick before the navigator advances), and past the branch segment 0 is re-anchored on the centerline *behind* it so its arrival bearing is still the runway heading. See [landing-and-runway-exit.md](landing-and-runway-exit.md).

**A general-purpose route may start with a virtual leg.** `AircraftGroundOps.AssignedTaxiRoute` *is* serialized (`TaxiRouteDto`), and `TaxiRoute.FromSnapshot` resolves every segment endpoint by node id against the layout. A ramp-lane reposition (`RampLaneReposition`, issue #396) puts a `VirtualNode` leg — aircraft position → lane node — at segment 0, and a virtual id is never in `layout.Nodes`, which would have nulled the whole route on rewind/replay.

`TaxiSegmentDto` therefore carries optional `From/ToLatitude`/`Longitude`, filled only for virtual (negative-id) endpoints; restore rebuilds the virtual node from them and the synthetic edge via `VirtualNode.CreateSegment`.

One pair writes and resolves a single segment: `TaxiRoute.ToSegmentSnapshot` and `TaxiRoute.ResolveSegment`. Every snapshot segment goes through them, a route's and `FollowingPhaseDto.LeadEdgeIntoMerge` alike; `ResolveSegment` takes the first of the from-node's edges to the to-node, pointed that way, so two nodes joined by more than one edge restore to the same edge everywhere.

Old snapshots leave the fields null and resolve by id exactly as before — no schema bump, the same pattern as `HoldShortPointDto.Latitude/Longitude`. A destination-end cut (issue #400) is the other shape: a free-space leg between two *layout* nodes with no edge between them.

`ToSnapshot` flags it (`TaxiSegmentDto.IsFreeSpace`, from `VirtualNode.IsVirtualEdge`) and `FromSnapshot` rebuilds it from the nodes' own positions; an *unflagged* pair with no edge still voids the route — a bundle whose node ids predate the current parser must drop the route, not sprout stray legs (`VfrFollowSequencesToFinalTests` caught exactly that).

A restored taxi whose route the current layout cannot rebuild (an archive without `LayoutFormatVersion` restores against today's layout, whose node ids may differ) is not left phaseless: `TaxiingPhase.EndWithLostRoute` brakes it and holds it in position, with no pilot transmission ([ground/navigator.md](ground/navigator.md)).

`TaxiRouteDto` also carries `DestinationParking` / `DestinationSpot` (nullable, additive) so a restored taxi-to-parking still installs `AtParkingPhase` at the end of the route.

`TaxiRouteDto.ClearanceTaxiways` (additive, no schema bump, not written while null) keeps `TaxiRoute.ClearanceTaxiways`, the taxiways the TAXI clearance named, so a `CROSS`/`RES` after a restore still reads back only those and the route's implied lanes; a route restored without it reads every leg it drives past the bar and no issued taxiways past its end.

`TaxiRouteDto.TurnAboutShape` and `TurnAboutTargetNodeId` (additive, no schema bump) keep `TaxiRoute.TurnAboutShape` and `TurnAboutTargetNodeId` across a restore, so a route that turns the aircraft about on its taxiway still reports the pending shape and its target node (`AircraftGroundOps.TaxiTurnAboutShape` / `TaxiTurnAboutTargetNodeId`) while segment 0 is unflown, and a far-end route with no leg back that cleared both at its target restores cleared.

Older snapshots read them as `TaxiTurnAboutShape.None` and null.

`GroundNavigator` round-trips the primitive it is playing: `GroundNavigatorDto.Playback` carries the primitive (straight, Bézier or slow turn), its playback progress, the arc-entry blend, the entry-alignment bookkeeping and the from-node of the segment it was captured on.

`FromSnapshot` holds the playback back, and the owning phase's first `SetupSegment` after the restore resumes it (`TryResumeRestoredPlayback`) when that segment's from- and to-nodes match the saved ones, so a restore mid-curve goes on along the same curve, position for position (`GroundNavigatorArcRestoreTests`, `CrossingRunwayRestoreTests`).

A mismatch drops the playback with a warning and builds the segment afresh from the aircraft's pose; a virtual (negative-id) from-node is not compared: its id hashes its position (`VirtualNode.IdFor`), but it sits at the aircraft's pose when the route was built (`RunwayExitPhase`'s approach leg), so a rebuild puts it somewhere else.

More fields are the same kind (nullable, written only when set, null on older snapshots; no schema bump), each latching a once-only pilot call so a restore does not repeat it: `TaxiingPhaseDto.SaidFarSideBlockedNodeId` (int?, the cleared crossing bar it said "holding short of runway …, traffic ahead" at).

`HoldingShortPhaseDto.SaidTrafficAhead` (bool?, the same call from the hold at the bar), `HoldingShortPhaseDto.WaitingForLead` (bool?, the hold, cleared across, is waiting at its bar for the lead of the follow armed behind it) with `SaidWaitForLead` (bool?, the "waiting for the traffic to pass" call made for the current wait; a restore does not repeat it), both omitted when null.

`HoldingShortPhaseDto.DisplayEnd` (string, `1R`) is the end the phase's `Name` gives its runway, chosen once when the hold starts from the room's active runways and kept to the hold's end; omitted when no end was active (the name carries the combined id) and on older snapshots (no schema bump).

`HoldShortPointDto.IsExpedited` (bool?, written only when true, absent on older snapshots; no schema bump) is a `CROSS … EXP` crossing's expedite, kept on the bar; `CrossingRunwayPhaseDto.IsExpedited` (bool?, same rule) is the expedite a crossing phase flies under.
`CrossingRunwayPhaseDto.SaidNotClear` (bool?) with `NotClearRestSeconds` (double?, the rest time counted toward the not-clear call).
`CrossingRunwayPhaseDto.TailClearanceSuppressed` (bool?), `TailFullSegmentCount` (int?), `TailCutFt` (double?) and `HeldShortBarNodeId` (int?, the node id of the other runway's hold-short bar that set where a crossing ending at rest stops) are the crossing's tail-clearance decision; all nullable and additive, no schema bump.

A restore applies the stored decision instead of deciding again, a snapshot without them decides afresh, and a stored decision that no longer fits the route is decided again with a warning.

`ApproachClearanceDto.AltitudeHeldUntilEstablished` (bool?, in `PhaseSnapshotDto.cs`) is `ApproachClearance.AltitudeHeldUntilEstablished`, a controller's altitude held outside the final approach fix ([approach-and-pattern-geometry.md](approach-and-pattern-geometry.md) § A controller's altitude held until established). It is written only when true and absent on older snapshots, which restore unheld; no schema bump.

`ApproachClearanceDto.TransitionName` (string?) is `ApproachClearance.TransitionName`, the procedure transition the clearance selected when it was issued; `ClearedApproachSpeeds` reads that transition's legs and then the common legs. It is written only when set (a clearance with no transition, a lateral intercept or a visual approach has none) and absent on older snapshots, within schema 37 and with no migration step.

A crossing also carries its slice segment and the pose that segment was set up at (`CrossingRunwayPhaseDto.CrossingRouteSegmentIndex`, `SegmentSetup*`), and a runway exit whose route cannot be rebuilt drops the navigator it was restored with.

`LineUpPhaseDto` carries the maneuver under way, all nullable and written only when non-null (additive, no schema bump): `ManeuverState`, the plan's start pose (`PlanStartLat`, `PlanStartLon`, `PlanStartHeadingDeg`), and the arc playback (`Arc`, `ArcTargetSpeedKts`) or the graph-taxi navigator with its `RouteSegmentIndex`.

The restore plans again from the start pose and resumes the playback (`LineUpRestoreRoundTripTests`); `ClearRunwayPhaseDto.Navigator` does the same for the clearance segment (`ClearRunwayRestoreTests`). A snapshot without them rebuilds from the aircraft's pose.

A follow restores its navigator (`FollowingPhaseDto.Navigator`) only when its follow route or clearing route resolved, and the playback resumes at that route's first `SetupSegment` after the restore (`FollowGroundOnGraphTests`). Snapshots written before the playback existed rebuild the primitive from the aircraft's pose, and such a reconstruction can trail the original on the same path.

## Phase polymorphism

`PhaseDto` is the abstract base; every concrete phase DTO has a `[JsonDerivedType(typeof(XyzPhaseDto), "Xyz")]` registration. See [phases.md](phases.md) for the four steps required to add a new phase to the snapshot system.

**Never remove a phase's `[JsonDerivedType]` if a committed recording may have captured that phase in a snapshot.** Polymorphic deserialization throws `JsonException: Read unrecognized type discriminator id 'Xyz'` **before** `SnapshotSchemaMigrator` runs, so a version bump / migrator can't rescue it. The failure misleads — affected tests pass in isolation and fail only once a test that replays the offending recording runs (it looks like a static-singleton race but is the discriminator).

When a phase is superseded, **retain its DTO + `JsonDerivedType`**: the class that flew it may go, but only if the restore path converts the retained DTO into the phase that replaced it (e.g. `PushbackToSpotPhaseDto`, kept as data after the #233 pushback rewrite deleted `PushbackToSpotPhase` — `PhaseList.SpotPushbackAsMove` maps it to a `PushbackPhaseDto`, and the DTO is never written).

"Unreleased software: delete freely" does **not** extend to types serialized into committed recording fixtures. Verify with `pwsh tools/test-all.ps1` (full suite), not just targeted tests.

## Recording — `RecordingArchive.cs`

A recording is a ZIP with this layout:

```
manifest.json                # Version, RngSeed, SessionStartUtc, InitialActiveRunways, ActionCount, HasWeather,
                             # HasArtccConfig, HasToweredAirports, HasTerminalLog, ArtccId, ScenarioId/Name,
                             # ClientVersion, ClientBuildKind, ServerVersion, ServerName (optional),
                             # Snapshots[], LayoutAirportIds[], AirportGeoJsonIds[],
                             # LayoutFormatVersion, MissingLayoutAirportIds[],
                             # WindGridHashes[] (omitted when the recording flies no GFS grid)
scenario.json.br             # Brotli-compressed scenario JSON
actions.json.br              # Brotli-compressed RecordedAction[]
terminal-log.json.br         # Brotli-compressed RecordedTerminalEntry[] (optional; HasTerminalLog)
snapshots/NNN.json.br        # one per snapshot index
layouts/{AirportId}.json.br  # ground layouts (optional; a server export or soak run writes every airport the room pinned)
airport-geojson/{AirportId}.geojson.br
                             # the source GeoJSON each bundled layout was parsed from (optional)
wind-grids/{hash}.grib2.br   # a GFS wind grid's raw GRIB2 bytes, Brotli, named by their SHA-256 (optional; one per grid the weather names, listed in WindGridHashes)
weather.json                 # plain JSON (optional; gated by HasWeather)
artcc-config.json.br         # ARTCC config JSON (optional; HasArtccConfig; the room's pinned config of the scenario's ARTCC)
towered-airports.json.br     # Brotli JSON list of the towered airport ids (optional; HasToweredAirports)
bookmarks.json               # plain JSON (optional; user-authored timeline bookmarks)
```

**`wind-grids/{hash}.grib2.br`** entries are written by `RecordingArchiveWriter.WriteWindGrid` (a hash already written is skipped; bytes that do not hash to the name throw) from `WindGridRegistry`, for every hash `WindGridHashes.Referenced` finds in the initial weather, the recorded weather changes and the snapshots' `WeatherJson`; a grid the registry no longer holds is left out with a warning.

`RecordingArchive.RegisterWindGrids` reads them back (through `ReadWindGridEntry` / `RegisterWindGridEntries` and `RecordingArchiveWriter.WriteWindGridEntry`, which the room checkpoint shares), verifying each against its name, and both loaders (`ToBaseSessionRecording`, `ToSessionRecording`) call it before the weather text is parsed. The pins it takes ride on `SessionRecording.WindGridPins`, owned by whoever holds the recording; a load that fails after registering grids disposes them.

A manifest listing more than 16 distinct grids, or an entry past 512 KB decompressed, refuses the whole recording with a `WindGridLimitException` (`RecordingArchive.WindGridLimitRefusal` is the non-registering pre-check).

`ReplayDriver` takes its own pins before `LoadScenario` and hands them to `SimScenarioState.WindGridPins`. The server's archive load registers the grids inside its gated commit and its `TrainingRoom` pins them (`RebuildWindGridPins`); a live export and the soak sink write the grids with `RecordingArchiveWriter.WriteReferencedWindGrids`, the public form of the call the writer's own recording write makes.

Archives written before grids were recorded have no `WindGridHashes` and replay on the FD layers. See [weather-and-wind.md](weather-and-wind.md).

**`towered-airports.json.br`** is the room's towered airport set (`SimScenarioState.ToweredAirports`, `SessionRecording.ToweredAirportIds`), written by `RecordingArchiveWriter.WriteToweredAirports` and read by `RecordingArchive.ReadToweredAirportIds`. It is built from every ARTCC config the room loaded (its own, its roster's and its geographic neighbours'), of which the archive bundles only the own one, so replay judges an airport-level `CVA` against the same set.

An older recording has no entry (`HasToweredAirports` false) and replay rebuilds the set from its one bundled config. `ReplayDriver` restores the bundled ARTCC config, the towered set and the recording's initial active runways (none recorded: empty) onto the scenario state as part of `LoadScenario`, before the scenario's presets dispatch, so a preset reads them as the live room did.

**`terminal-log.json.br`** is the room's broadcast terminal stream — the command echoes, responses, SAY lines, warnings, and chat the user saw — each captured with a wall-clock `Timestamp` and the scenario-elapsed `ElapsedSeconds` it occurred at (`RecordedTerminalEntry`).

The server appends to `SimScenarioState.TerminalLog` inside `TrainingBroadcastService.BroadcastTerminalEntry` only while live (never during playback/reconstruction), and preserves it across rewind reloads alongside `ActionLog`.

On load, the client repopulates its terminal from it (via the `GetTerminalLog` hub method) so every terminal line is a replay-scrub target — right-click a line → `RewindToSeconds(entry.ElapsedSeconds)`. Absent in recordings written before this feature (`HasTerminalLog` false → the reader returns an empty log).

For those legacy recordings, the live host's playback pump (`LiveRoomHost.ApplyPlaybackActionsThrough`) still echoes each replayed command/chat into the otherwise-empty terminal during forward playback; when a terminal log is present that echo is suppressed (guarded on `TerminalLog.Count == 0`) so it does not duplicate the repopulated lines.

**`layouts/` and `airport-geojson/`** hold every airport the room had pinned (`TrainingRoom.ResourcePin`), whether or not an aircraft referenced it, each under the layout's own `AirportId` (`LayoutAirportIds`, `AirportGeoJsonIds`; null when there is none). A server export and a soak run write them through `RecordingLayoutBundler.WritePinnedLayouts` over `ILayoutBundleWriter`, the path a room checkpoint uses too; a room with no pin bundles none.

`MissingLayoutAirportIds` lists the FAA codes the room had pinned with no map (empty in archives written before it), so a load pins them with none instead of falling through to the live map.

`LayoutFormatVersion` is `RecordingArchive.CurrentLayoutFormatVersion` when written (null in older archives), and a server load reads bundled layouts only in that format. A layout in format 1 carries its runway coordinates, edge shapes (value tuples, so `GroundLayoutSerializer.Options` includes fields) and the runway-end turnoff, no-turnoff and threshold-displacement maps.

Fields added to a layout node after format 1 are optional and do not bump the version: a layout without one reads it as null or its default (`GroundNode.StandDeparture` reads as `PushBack`, `GroundNode.IsShortBar` as null, written only when non-null, where readers measure the bar's cross-track instead).

The server reads a bundled layout only when the manifest carries `LayoutFormatVersion` 1 (yaat-server `RoomResourcePin.cs`, `BundledAirportLayouts.Read`). Every test archive is pre-format, so a version-pinned recording test (`RecordingGroundData.VersionPinned`) runs on a bundled layout the product would not load for it, and reads runway geometry from `NavigationDatabase` because a pre-format layout lacks it.

The divergence is deliberate. Never remap a version-pinned fixture's node ids; only evergreen fixtures are remapped when a parser change renumbers nodes (see [test-harness.md](test-harness.md)).

Each arc's construction angles (`GroundArc.EdgeBearingAtNode0Deg` / `EdgeBearingAtNode1Deg` / `TurnAngleDeg`) are serialized, not rebuilt, so a read-back layout gives the parsed one's corner speeds and route costs bit for bit. On read (`RecordingArchive.ReadLayoutEntry`), every edge is re-linked to the layout's own node objects and the adjacency lists are rebuilt (`RecordingArchiveTests.ReadLayout_OfARealLayout_RoutesAndTurnsAsTheParsedOne`).

**Version fields** (`ClientVersion`, `ClientBuildKind`, `ServerVersion`) are stamped at export time for bug-report triage. `ClientVersion`/`ClientBuildKind` are sent by the exporting client (`BuildInfo.Version` / `BuildInfo.BuildKind`) and describe the user's build; `ServerVersion` is `SimBuildInfo.Version` — the Yaat.Sim assembly that actually ran the session on the server (Yaat.Server carries no independent version).

Since the hosted server and a user's client can be on different builds, the two answer different questions: was the *user's* client behind a fix, vs. was the *sim code that ran* behind a fix. All three are null for recordings exported before this was added or migrated from legacy formats.

`ServerName` is the deployment's name (`Yaat:ServerName`, e.g. `yaat1.leftos.dev`; in the docker-compose deployment `YAAT_SERVER_NAME`, defaulting to `YAAT_DOMAIN`), stamped by the server's `RecordingManager` at export, trimmed and left out when the server has none. It names which host's full server log to read. Adding it did not change the manifest `Version`, which stays 4.

**`bookmarks.json`** persists the room's shared timeline bookmarks (GitHub issue #288). Bookmarks are server-authoritative per-room state (`SimScenarioState.Bookmarks`), synced to every RPO via the `BookmarksChanged` broadcast and the `RoomStateDto.Bookmarks` join seed.

The client still injects `bookmarks.json` at save time via `RecordingArchive.WriteBookmarks(bytes, …)` (a copy-into-fresh-`Create` rebuild, not Update mode) from `SaveRecording`/`SaveBugReportBundle` — its `Bookmarks` mirror equals the server's list.

On **load**, the *server* reads the entry via `RecordingArchive.ReadBookmarks()` in `RoomEngine.LoadRecordingArchiveAsync`, seeds `SimScenarioState.Bookmarks` (re-minting ids), and broadcasts — so every RPO in the room sees the loaded recording's bookmarks, not just whoever loaded it.

The entry is not tracked in the manifest, so it is absent in older recordings — `ReadBookmarks()` returns `[]` then. Bookmarks are deliberately excluded from `ScenarioSnapshotDto` (they are timeline-global metadata, not per-tick state); `RewindAsync` and snapshot-replay carry them across the scenario reload explicitly, so scrubbing never drops a bookmark.

**`RecordedAction`** is a discriminated union via `[JsonDerivedType]`. The common members are `(ElapsedSeconds, $type)`; concrete types add their fields:

- `RecordedCommand(Callsign, Command, Initials, ConnectionId)` — every user command (including ones rejected at validation; replay is faithful to history). Its baked draws ride as init-only fields: `ReactionDelaySeconds`, `SpawnJitterSeconds`, `SpawnedAircraft`, `IssuedAtUtc`, and `StripId` — the id a `SEP` / `HSC` / `SCAN` / `BLANK` minted live, reused on replay so a later command addressing it resolves and a record re-applied over a snapshot that already holds the item creates no second one.
- `RecordedChat(Initials, Message)` — a controller/RPO chat message. Has no simulation-state effect, so replay/reconstruction ignores it; recorded so bug-bundle tooling carries the chat log and forward tape-playback can re-surface it in the terminal.
- `RecordedSettingChange` — sim-control toggles (e.g. `SetValidateDctFixes`), plus one t=0 record per session setting the room seeded into the scenario at load, or at a restart's fresh log, when it differs from the default (`ScenarioLifecycleService.RecordSeededSessionSettings` — without it, a setting already on at load would be invisible to every reconstruction, including the export's own snapshot regeneration, which runs in a fresh room).

  Replay handlers in both repos apply these. **Pattern: any new sim-control toggle should produce one of these so replays stay faithful, and `RecordSeededSessionSettings` must list it too.**

  A development flag that only a headless tool sets is a recorded setting change too, never an environment variable. `SoloRpoCommandsAllowed` is the example: yaat-server's SoakRunner `--solo-rpo-commands` sets it so a solo recording can use the RPO-only commands.

  A replay or rewind restores it from the archive, so it holds before the recorded commands that need it run. It lives on the scenario only, never in `RoomSessionSettings`: room state would leak a rewound recording's flag into the next scenario.
- `RecordedAircraftSpawn` — full `AircraftSnapshotDto` for aircraft created by runtime generators. Replay injects this aircraft directly and skips the RNG-driven generator path when spawn actions are present, so generator implementation changes do not rename or re-type historical arrivals.
- `RecordedLiveTrafficSample(Callsign, Sample, SpawnState?)` — one live-traffic sample for a shadow aircraft; `SpawnState` (the shadow's snapshot) rides only on the sample that created it. **Pre-tick**, like `RecordedAircraftSpawn` (`SimulationEngine.IsPreTickAction`). `RecordedLiveTrafficRemoval(Callsign, Reason)` applies after the second. See [live-traffic.md](live-traffic.md).
- `RecordedStarsSharedStateChange(Callsign, TcpId, State)`, `RecordedClearanceChange(Callsign, Clearance)`, `RecordedHoldAnnotationChange(Callsign, HoldAnnotation?)` — the CRC handlers' writes to a track's per-position shared display state, its departure clearance and its hold annotation (null = delete), recorded whole and replaced whole on apply. Only the flight-plan editor records a hold annotation this way; the ERAM HM / QH entries record a `RecordedEramEntry` that `EramEntryEngine` applies.
- `RecordedEramEntry(Callsign, Entry, IdentityCode?, SweptLat?, SweptLon?, SweptTrackDeg?, SweptSimSeconds?)` — the four swept fields are set together on a QT coast entry only (the pose CRC showed: the coverage-coast point, else the last sweep, with its sweep time), so the no-location coast starts where CRC drew the target; null everywhere else and in older recordings, which then anchor at the live position.

  An ERAM keyboard entry that wrote per-track ERAM state (`TRACK [/OK]`, `FREEZE {lat} {lon}`, `QQ …`, `QR {alt}`, `QS …`, `LF [{label}]`, `VCI {sector}`, `LEADER [D{n}] [L{n}]`, `HANDOFF {code} [/OK]`, `PO …`, `POACK …`, `POCLEAR …`, `DRI [J|T]`, `MIN {facility} {sector}`, `FDB {facility} {sector}`), applied by `Commands/EramEntryEngine` on every run kind; the identity code is the acting position's `AS` code, resolved on apply.
- `RecordedEramCrrGroup(Label, Color?, Lat?, Lon?)` — a CRR group created, replaced, recolored or (null `Lat`) deleted. Engine state (`SimulationEngine.CrrGroups`, snapshotted as `ServerSnapshotDto.CrrGroups`), applied by `SimulationEngine.ApplyCrrGroup` on every run kind — a client-side replay and a bug bundle hold the groups; membership rides each aircraft's `LF` entries.
- `RecordedStripRequest(Callsign, FacilityId?, StripId)` — a manual *Request Strip*: a null facility is the training-hub form (the student's own bay, arrival or departure format by the aircraft), a facility is CRC vStrips' form (a departure strip for it). `StripId` is the id the live print minted, baked on like a reaction delay: the print always stacks a fresh copy, so re-deriving the id would duplicate it.

  `StripRequests.PrintRequestedStrip` (Yaat.Sim, applied by the router on every run kind) prints under the recorded id — an engine that already holds it (a snapshot restore carried it in) prints nothing, a from-scratch reconstruction prints it — and answers the verdict, so a request whose aircraft is gone is a fidelity warning.

  `RecordedAmendFlightPlan` carries a `StripId` the same way: the id the amendment reprint printed under, null on records written before it existed (which then mint, as before).
- `RecordedAttendanceChange(AttendedPositionIds)` — the first **recorded input** (ADR 0003, `CONTEXT.md`): the full set of vNAS position ids a CRC client was working in the room. The live host writes it whenever the set it derives from `PositionRegistry` differs from the engine's (`RoomEngine.SyncAttendance`, in the live host's end-of-second recorded-actions slot — the slot a replay applies the record in — after a load and on return to live).

  On apply it is replaced whole through `SimulationEngine.Attendance` (each id resolved to its owner and TCP through the *room's* ARTCC config; an id the config cannot resolve stays id-only).

  Auto-accept, point-out auto-acknowledge, the delayed-handoff consolidation check, `CON+`'s descendant transfer and the handoff / point-out consolidation redirect all read that set, so a rewind or a bundle decides them as live did. Snapshotted in `ServerSnapshotDto.AttendedPositionIds`; a fresh replay starts with nobody attended.
- `RecordedAutoTrackChange(PositionId, Entries)` — a CRC `.AUTOTRACK` as received (the delta: a positive airport id the caller takes from whoever held it, `-X` removing X everywhere, `none` clearing the caller's list), the second recorded input.

  `SimulationEngine.ApplyAutoTrackChange` is the one body on every run kind: it resolves a position the scenario never listed through `Scenario.ArtccConfig` (or refuses, so nothing is logged), applies the delta to `SimScenarioState.AtcPositions`, and claims every airborne untracked departure out of the caller's airports. The live `RoomEngine.UpdateAutoTrackAirports` is `ApplyAndRecord` plus a terminal drain, so a rewind or a bundle reconstruction reaches the roster the live room had.
- `RecordedAsdexSafetyLogicChange(FacilityId, Config)` — CRC's `UpdateAsdexSafetyLogicConfiguration` push (runway areas, closures, the active configuration, inhibited arrival-alert positions) as the Sim-native `Yaat.Sim.Asdex.AsdexSafetyLogicConfig` the server projects the wire DTO to; scenario state, so `SimulationEngine.ApplyRecordedAsdexSafetyLogic` replaces that facility's entry in `SimScenarioState.AsdexSafetyLogicConfigs` on every run kind, which the alert tick reads for closed runways.

  The configurations are snapshotted (`ScenarioSnapshotDto.AsdexSafetyLogicConfigs`, null-absent when empty) and a restore replaces rather than merges: it keeps what the snapshot holds (the load's seed, `AsdexSafetyLogicSeed.Build`, which is derived at load and not recorded, plus any earlier push) and the log's later pushes move it on, so a rewind to before a push returns to the seed.

  Each `AsdexSafetyLogicConfigDto` also carries `InhibitStartSeconds` (position id → the sim second its arrival-alert inhibit was set; it lapses 180 sim seconds later) and `SupersededConfigurationIds` (configuration id → the sim second an `ARWY` re-pick moved away from it; a push naming it within 10 sim seconds is stale). Both are written only when non-empty and absent on older snapshots, within the current schema and with no migration step: an inhibited position with no start restores as set at the snapshot's own second (`AsdexSafetyLogicConfig.FromSnapshot(dto, snapshotSeconds)`). All three derived fields (`RunwayConfigurationChosen` included) are `JsonIgnore` on the Sim record, so they are off the recorded push and a replay derives them again; the lapse is worked out from stored state and never recorded. A restore drops the engine's pending configuration-change marks (they belong to the undone future).
- Spawn, preset, and other event-shaped actions.

A derived record is appended only when its write applied (`ActionRouter.IssueDerived`), so a refusal on replay — the aircraft is gone, an ERAM guard answers differently — is by construction a fidelity break and logs a `replay-fidelity` warning like a command whose verdict changed.

**Snapshot cadence**: snapshots are written on demand by the recording manager (rewind checkpoints, periodic captures). Live replay does not need a snapshot per tick — it ticks forward from the most recent prior snapshot, applying actions at their `ElapsedSeconds`.

## Replay — `Simulation/Replay/`

The replay surface on `SimulationEngine`:

| Method | Purpose |
|---|---|
| `ReplayFromStartTo(target, actions)` | Reset to t=0 and replay forward to `target`. **From-scratch every call** — only use for one-shot rewinds, never in a loop. |
| `FastForwardTo(target, actions)` | Advance from the current `ElapsedSeconds` to `target`, applying actions in between. Throws if `target ≤ current` (use `ReplayFromStartTo` or restore from a snapshot to rewind). Updates the replay cursor so subsequent `ReplayOneSecond` calls continue from `target`. |
| `ReplayRange(start, target, actions)` | Replay between two timestamps. Engine must already be at `start` (e.g. via snapshot restore); does not reset. |
| `ReplayOneSecond()` | Advance exactly one sim-second (4 sub-ticks) from current state, then advance the action cursor. **This is the right tool for stepping through a recording.** |
| `ReplayOneSubTick()` | 0.25s granularity — physics tests. |

Every recorded command is applied by the `ActionRouter` (`Simulation/Actions/`, `SimulationEngine.Actions`): it strips the `AS` prefix, classifies the verb, resolves its `ActionScope` (a global command resolves nothing, so `SQALL`/`TAXIALL`/`ADD` with an empty callsign apply; an aircraft-scoped one is refused identically on every run kind when the aircraft is missing; a position-scoped one resolves the issuing identity) and runs the `ArmTable` row.

A bare `AS` goes through `SimulationEngine.SelectPosition` into the engine's `PositionSelections`, and every identity resolves through `SimulationEngine.ResolveIdentity` — the same map and resolver the live server and its reconstruction use (see [command-pipeline.md](command-pipeline.md) § one identity).

The selections are part of the snapshot (`ServerSnapshotDto.PositionSelections`, connection id → owner), so a replay that starts from a mid-recording snapshot resolves identity exactly as live did; `RestoreFromSnapshot` replaces the map (a pre-feature snapshot restores it empty), and a server rewind therefore restores the selections *as of the rewind target* — the snapshot's map plus the recorded `AS` commands up to the target.

Runtime aircraft spawns are action-driven during replay. `RecordedAircraftSpawn` actions apply before the tick's generator phase, and old archives that predate those actions synthesize them from snapshot deltas for aircraft that were not declared in the scenario JSON.

`RecordedLiveTrafficSample` shares that pre-tick slot (`SimulationEngine.IsPreTickAction`), which is the spine's `OpenSecond` step — `host.ApplyPreTickRecordedActions` — after the clock increment and before pre-physics on every run kind.

Those are the `ReplayHost` (`Replay`, `ReplayOneSecond`, `ReplayOneSubTick`), the `ReconstructionHost` (`RecordingManager.ReconstructViaServerTick`) and the `LiveRoomHost` in tape playback — all three through one `RecordedActionPump` (`Simulation/Replay/`), whose pre-tick pass never moves the cursor and whose main pass skips what the pre-tick pass applied.

`RecordedSpawnReplayServerTests` guards the spawn half, see [live-traffic.md](live-traffic.md) for the sample half.

### Finding the first divergence

`SnapshotTreeDiff.Compare(left, right)` (`Simulation/Oracle/`) compares two `StateSnapshotDto`s field by field over the whole tree, with no tolerances, and returns one `SnapshotDivergence` (path, left, right) per differing leaf; `SnapshotStateHash.Compute` hashes the same comparable tree (`SnapshotTreeDiff.ToComparableNode`), so a per-second loop can compare hashes and run `SnapshotTreeDiff.CompareNodes` only on a second whose hashes differ (the tick oracle does exactly this, ADR 0004).

To find where a replay leaves a recording, step it with `ReplayOneSecond()` and, at each second the archive has a snapshot for (`RecordingArchive.SnapshotTimestamps`, `FindNearestSnapshotIndex`), compare `CaptureSnapshot()` against `RecordingArchive.ReadSnapshot(index)`. `ReadSnapshot` does not migrate: on a recording made by an older build, run `SnapshotSchemaMigrator.Migrate` on the read snapshot first and expect `(absent)` leaves for fields added since.

## Bug bundles — `tools/bug_bundle.py`

A `*.yaat-bug-report-bundle.zip` (v4) wraps a `RecordingArchive` plus client/server logs. The client logs are the live `yaat-client.log` and each rotated previous session as `yaat-client.N.log` (`.1` newest, from `FileLoggerProvider.PreviousLogs`), each zip entry stamped with its file's last-write time, so a user who relaunched YAAT before saving still sends the session they are reporting.

Every client log is scrubbed as it is written into the bundle (`ClientLogScrubber`, Yaat.Client.Core); the file on disk is untouched.

Participants get the server log's pseudonyms (the list comes back with `GetSessionServerLog`, which the client fetches whenever it is in a room, recording or not; outside a room, or when the fetch fails or returns no list, the client builds a local list from its own identity and the room and CRC members it holds, uses it for every file in the bundle and adds a top-level `ANONYMIZATION-NOTE.txt` saying the pseudonyms may not match across files).

Structural passes catch people the list misses (they left before the export, or appear only in a rotated log): the terminal initials slot, `(from XX)` (mapped in `SessionLogAnonymizer` itself, so the server log gets it too), the person who acted at the start of a `[Terminal] [System]` room line (two-character initials followed by a server room-action verb: "XY joined the room", "XY set command run delay"; echoed alias text such as "TWR frequency is 118.3" is left alone), the target of "… assigned … to XY", the person an RPO pull names, CRC lobby entries (`id(CALLSIGN (Name))`) and any `CALLSIGN (Name)` (a 1-3 character sector code such as `OAK_TWR (TW)` is kept) give the pseudonym when the person is listed, else `??`; so does a CRC client's bare real name (a client with no position) in its connect, activate, end, flight plan, consolidation, kick, kill, pull and message lines, and a whole multi-word name in the initials slot of a private message to an aircraft.

A pseudonym-shaped token is kept only when the list issued it (a departed person whose initials look like `B2` becomes `??`); `AI`, the system senders and position callsigns are kept, and an override command's initials keep their ` (override)` suffix behind the pseudonym. Command verbs and aliases (`SessionLogAnonymizer.CommandVerbs`) are never replaced in free text, so a controller with initials `CM` does not wipe every `CM 050`; the server log uses the same keep set.

The user profile folder becomes `%USERPROFILE%` and other users' home folders `{user}`; the hosts of the user's own servers (the connected one and every saved one, except the official server and loopback) become `{server}` with or without a scheme, while public endpoints (GitHub, vNAS) stay readable; non-loopback IP addresses become `{ip}`; emails, tokens and auth codes become placeholders. Speech transcripts are kept. Subcommands:

```
info        snapshot   track       actions     history
phases      commands   terminal-log scenario    weather
layouts     artcc-config logs      install     validate
```

`terminal-log` reads the archive's `terminal-log.json.br` — the room's broadcast terminal stream (commands, responses, SAY, warnings, chat), one line per entry with its sim-elapsed `t=`.

It filters by `--callsign` / `--kind` / `--from` / `--to`, dumps the filtered entries with `--json`, or emits SubRip captions with `--srt` (`--hold`, `--offset`) for the FOLLOW montage; `--exclude-kind` drops kinds, and `--captions` is the video-caption preset (no command, response or strip echoes, no solo-training notices, a 30 s repeat skip, cues timed by reading speed).

An archive with no terminal log (`HasTerminalLog` false, or the entry absent) prints `no terminal log in <path> (recorded before the feature)` and exits 1.

For single-aircraft triage, `history --callsign X` is one chronological view that replaces 5+ targeted `snapshot --at` calls. See `.claude/skills/bug-bundle/SKILL.md` for the full reference and CLAUDE.md for examples.

A bug bundle's outer root also carries `server.json`, `{"url": ...}`: the scheme, host and port the client dialled (`MainViewModel.Timeline.cs`, `WriteBugReportBundleAsync`), with a host other than the official server or loopback written as `{server}`. It is absent when the client was not connected. `bug_bundle.py info` prints `Server:` from the manifest's `ServerName`, else the host of `server.json`'s URL, else `(unknown)` (`(unreadable server.json)` when malformed).

The bundle's `yaat-server.log` is only the room-scoped slice (below), so a tick-loop or connection problem needs the full server log from the host the manifest's `ServerName` names (targets in `deploy-targets.ps1`). Actions stamped with the same `t` across unpaused wall-clock seconds mean the tick loop stopped.

Bundles embed a room-scoped, anonymized `yaat-server.log`, including for **remote** servers (not just a local disk read). Server-side: `RoomLogStore` (Simulation) is a per-room bounded ring buffer (50k-line cap, marks earlier lines dropped) that `FileLogger` mirrors lines into only while inside `BeginRoomScope` (tick loop, `SendCommand`, CRC dispatch) — unscoped lines are file-only and never exported.

`SessionLogAnonymizer` (Yaat.Sim `Logging/`, pure/static, shared by server and client) replaces each participant's CID + real names + initials with a stable pseudonym (`A0`..`B9`, whole-word matched so CIDs embedded in beacon codes/callsigns survive) before `TrainingHub.GetSessionServerLog()` returns the text with its participant list.

A person's pseudonym is their position in the list, which is in first-seen order, so a later join never renumbers anyone. A person seen under several sets of initials or names (a rejoin with other initials) is one `LogParticipant`: the first in `Initials`/`RealName`, the rest in `OtherInitials`/`OtherRealNames`, all mapping to the one pseudonym.

A `(from XX)` sender the list does not hold becomes `??` (system senders, issued pseudonyms and position callsigns are kept), and a Tower Cab 3D viewer's CID, which is listed nowhere, also becomes `??`. The session server log no longer records the hub caller's VATSIM Connect name. Client: `ServerConnection.GetSessionServerLogAsync()` → `MainViewModel.Timeline` runs the client's leftover-people pass over the text, embeds it into the archive and scrubs the client logs with the same list.

The list is not who is connected at export time. `TrainingRoom.Identities` (`RoomIdentityLedger`, yaat-server) is an append-only ledger of everyone ever bound to the room (creator, hub members, CRC controllers with their real names, pulled RPOs' lobby names, Tower Cab viewers), fed at every bind site and persisted in the room checkpoint, so a person who left is still hidden.

The recording in a bundle is pseudonymised too. `ExportRecording` takes a required `anonymize` flag, true for the bundle and false for Save Recording, which keeps the real initials.

With it, `RecordingPseudonymizer` (Yaat.Sim `Logging/`, pure) rewrites the action log and the terminal log from the same participant list as the server log, so `A0` is one person in the recording and in every log: the initials slot of each command, chat and terminal entry (an override command keeps its ` (override)` suffix) and the initials named by the assign/unassign lines and the `Assigned to XY.` error become the pseudonym, or `??` for a person the list does not hold, and the terminal and chat text goes through `SessionLogAnonymizer.Anonymize`.

`""`, `AI`, `CRC`, `YAAT`, a CRC flight plan's `[CRC] {TCP}`, the ERAM `SECTOR {id}` and `SIGWX` senders and position callsigns are kept. The free text inside an action (SAY-family commands, ERAM SM/WX entries, bookmark names) stays as recorded.

Nothing the simulation replays reads the initials, so the pseudonymised recording replays to the same states. The client rewrites `bookmarks.json` so each bookmark's creator carries the pseudonym. LADD-listed callsigns are pseudonymised on every export by the separate `LaddRecordingScrubber`.

Tests: `RoomLogStoreTests`, `FileLoggerRoomScopeTests`, `SessionLogAnonymizerTests`, `RecordingPseudonymizerTests`, `ClientLogScrubberTests`, `MainViewModelBugBundleTests`, and yaat-server's `RecordingExportPseudonymTests` and `CrcJoinSessionTests.SessionLog`.

## Pitfalls

- **`ReplayFromStartTo` is not a step function.** It resets to t=0 and replays forward every call — looping it is `O(N²)` and trips assertions like `MagneticDeclination` cache mismatches. To step, use `ReplayOneSecond()`. To advance from the current time to a later one, use `FastForwardTo(target, actions)` (it throws on rewind, which is the whole point — silent rewinds were the original footgun).
- **A `WAIT n <cmd>` preset fires on the clock, not on the aircraft's state, so a kinematics change moves every later preset's pose.** Scenario presets like `WAIT 30 TAXI T7B $7B` fire n seconds in whatever the aircraft is doing then; any change to tug or taxi kinematics (a faster tow, a different turn speed) changes where the aircraft stands when every later preset catches it, in every replay fixture that carries one.

  The canary is `5d33df162626.zip`'s SKW5707 at SFO gate E2 (`PUSH T7B`, then `WAIT 30 TAXI T7B $7B` firing mid-push): the 15 replay tests over that recording (`AtFixTriggerDuringPhasesTests`, `CfixSpeedAccelerationTests`, `ExitOverlapTests`, `SfoGroundSpeedUntilTests`) fail as a block when that TAXI lands the aircraft on a navigator edge case — the node-aimed fillet hand-over in [ground/navigator.md](ground/navigator.md) was found this way.

  When the block fails, look first for a new pose reaching a latent navigator or phase defect before treating the recording as stale.
- **Bundles need ARTCC config.** v4 bundles include ARTCC config (`HasArtccConfig` flag); replay reads it into `Scenario.ArtccConfig`. Older test fixtures without it must call `TestArtccConfig.LoadZoa()`.
- **Rewind and snapshot generation reconstruct through the *server-side* tick, not the Sim-only replay.** Room-owned tick state (the `IHostSteps` bodies `TickProcessor` holds — `LiveTrafficSync`; the track automation, the strip auto-print, the TDLS lifecycle, the coordination timers, the tower lists and the ASDE-X alert step are Sim steps since tick-path step 4) is filled only by a server host.

  So `RecordingManager.RewindAsync` and snapshot generation (`RoomEngine.CreateTempReplayEngine` → `RecordingManager.GenerateSnapshotsViaServerTick`) drive reconstruction via `RecordingManager.ReconstructViaServerTick` — the spine under a `ReconstructionHost`, which fills every room-owned step the live host fills and re-applies the recorded log around each second.

  The temp room reloads via `ScenarioLifecycleService.ReloadForRewind` (the sync sibling of `ReloadTapeForRewindAsync`, handed the tape) on the exporting room's resource pin, from `ReloadStart.Of(scenario)` (seed, session start, starting active runways) and the room's scenario sidecar, so `StudentPosition`/`AtcPositions`/auto-track conditions resolve against the configs the live run used.

  Using the old bare Sim-only replay here was issue #188: rewind reverted ownership to the start-of-file auto-track owner and re-queued every aircraft's delayed handoff to the student, and generated snapshots captured `Track.Owner = null`. Reconstruction runs with `IsBroadcastSuppressed = true`; the strip/TDLS broadcasters honor that flag so reconstruction doesn't spam phantom strips/PDCs.
- **A command the router never records can never diverge a tape being played back.** A rewind leaves the room paused *and* in playback with the whole action log intact; the first command that `RoomEngine.IssueLive` sees while `IsPlaybackMode` is true cuts the tape at the current second (`RecordingManager.TakeControl` → `ActionLog.RemoveAll(a => a.ElapsedSeconds > current)`) and returns the room to its own run kind.

  The gate is the routed arm's `RecordingPolicy`, asked ahead of the arm through `ActionRouter.WouldRecord(command)` — the mirror of `Route`'s decomposition, and the two must change together.

  `RecordingPolicy.Never` (`Transport` — PAUSE/UNPAUSE/SIMRATE, `Bookmark`, `ShowQueued`) records nothing and so leaves the tape alone, which is what makes the timeline's play button replay the tape instead of discarding it: the button sends the text `UNPAUSE` through the ordinary command path, and before the gate existed pressing play deleted the rest of the session.

  The same reason keeps the automatic CRC position sync off the truncating entry — `SyncCrcPositionToRpo` goes through `IssueAutomatic`, because a CRC client binding to the room or changing position is not the instructor taking control. This is the mirror of the rule at `ActionRouter.Route` that a never-recorded kind is never applied *from* a record.

  During playback the replay profile swallows that `AS` append (the selection still changes in the room's map), so `RoomEngine` remembers each swallowed sync per RPO connection (latest code wins; a sync recorded live drops the entry) and `RecordingManager.LeavePlayback` re-issues exactly those, after `SyncAttendance`, for connections still in the room.

  A position picked during playback survives a later rewind, and leaving playback with nothing swallowed (end of tape, take control) adds nothing to the tape.

  Loads, restart and unload forget the map.
- **A Session Settings toggle during playback takes control too.** `SimControlService` records it as a `RecordedSettingChange`, and `RecordingManager.Record` cuts the tape there like a command.

  That is why a recording load applies the tape's t≤0 actions before the first second plays: both load paths call `RecordingManager.ApplyTapeStartActions`, which runs a `ReconstructionHost` over `ActionLog` through t=0, as reconstruction does, and moves `PlaybackCursor` past them.

  The tape's t≤0 settings already hold by then: `ScenarioLifecycleService.ReloadRecording` starts the scenario on the defaults plus those records (`StartOnTapeSettings`) before any preset dispatches, so a preset TAXI resolves under the tape's `AutoCrossRunwayMode` and not the room's.

  A rewind or seek from t=0 (`ReloadTapeForRewindAsync`) and the export and migration snapshot generation (`ReloadForRewind`, `RoomEngine.CreateTempReplayEngine`) start the same way, and the room's own value never leaks in.

  The hub then broadcasts `SessionSettingsChanged`. Without that, a solo tape loaded into a room with solo off showed solo off. Ticking it on then cut the tape at t=0, so the replay lost every later instructor command and pilot line. Applied this way, t≤0 commands and chat echo to the terminal only through the tape's recorded terminal log, as in reconstruction.

  A mid-tape setting change reaches the room's copy as well: `RoomHost.ApplyRecordedAction` refreshes `room.SessionSettings` after every `RecordedSettingChange` that playback or a reconstruction applies. Clients follow through `SessionSettingsChanged`: once per playback second in which a change altered a setting the DTO carries (`LiveRoomHost`), and once at the end of every rewind, seek and hybrid replay.
- **A bundle's snapshots are reconstructions, not live captures.** `RecordingManager.ExportRecording` regenerates every snapshot by re-applying the action log from t=0 through a temp room (`GenerateSnapshotsViaReplay` → `ReconstructViaServerTick`); the embedded `yaat-server.log` is the live session.

  When `history`/`track` disagree with the log for the same aircraft and `t=`, the snapshot series is showing a replay-fidelity defect — a recorded action the reconstruction applies differently from live — not a second live bug.

  Triage it as its own defect, and pick replay-test restore points before the divergence or after the two re-converge (`FlightPlanCommandReconstructionKeepsPhaseTests` in yaat-server is the pattern: `RewindFromSnapshotAsync(before, after)` over the fixture). The tick oracle cannot see this class by construction — both oracle legs replay the same log through the same applier — so it is pinned by replay-vs-live-log tests on real recordings.
- **A recorded command takes the same arm live did — the router, not a replay-specific switch.** `SimulationEngine.Actions.Apply(RecordedCommand)` runs the same `ArmTable` row a fresh `Issue` does; the only differences are the baked draws (`ReactionDelaySeconds`, `SpawnJitterSeconds`, `SpawnedAircraft`, `IssuedAtUtc`, `StripId` — used instead of drawing) and that nothing is re-recorded.

  Consequences worth knowing: the aviation arm calls `ApplyPostDispatch` on replay too, so read-backs, the "unable" response and the frequency gates become sim state a replay reproduces (`RegisterControllerContact` establishing the two-way comms that clears a solo Class B/C boundary hold is one of them — a replay that skipped it left the aircraft orbiting).

  A `DA`/`FP`/`RMK` record applies nothing but the filing position's creator tag (the flight-plan arm from a record — the `RecordedAmendFlightPlan` recorded beside it carries the state).

  `CommandDispatcher` refuses the verbs at its public entries via `CompoundPolicy.IsFlightPlanCommand` so no path can push a flight-plan edit through the phase gate: a `VfrHoldPhase` cancels on any non-additive command, which is how a reconstructed hover hold was lost while the live one carried on, S2-OAK-5 bundle t=1771.

  A record whose `Accepted` disagrees with the replay's verdict logs a `replay-fidelity` warning (`Accepted` is null on recordings written before rejections were recorded). The legacy `PAUSE`/`UNPAUSE`/`SIMRATE` and `BM` records in older fixtures are inert — a kind that is never recorded is never applied from a record, decided in the router before any host is asked, so a rewind can never pause itself.

  An `ADD` is *derived* on every run kind: `SimulationEngine.AddAircraft` runs the generator and the CID draw against the shared `World.Rng` and the beacon pool exactly as live did, so both advance in lockstep.

  The aircraft the live run baked onto the record (`SpawnedAircraft`) is the authority when the derivation disagrees (a divergent world, a generator that changed since): the derived aircraft leaves the world and the pool, the recorded one takes its place, and a `replay-fidelity` warning names the disagreement.

  A record without a baked spawn (written before the field existed) simply derives. `ACCEPTALL`/`HOALL` (under the resolved identity), `GHOST` (the staggered phantom or the overlay), `RPOSLOC`/`RPOSMOVE` and `CAACK` replay through the same `TrackEngine` bodies the live room's router runs, so a replay has the ghost tracks, parked datablocks and acknowledged alerts the live session had.

  The server's reconstruction (rewind, bug-bundle export) and tape playback apply every recorded action through the same router with the room's host answering the slots (`RecordingManager.ApplyRecordedAction(action, host)` is a thin wrapper that only marks the room as re-applying): a reconstructed command honours its baked reaction delay, runs `ApplyPostDispatch`, derives an `ADD`, applies the global verbs — `actions.live-vs-reconstruct.baseline.json` is empty.

  What a recorded action does to *room-owned* state is the host's decision: while replaying, the `RoomHost` consumers rebuild the state every run kind must carry (a spawn runs its strip/PDC hooks, a `DEL` drops the room assignment) and leave out the broadcasts (a reconstruction runs suppressed and the room re-syncs when it lands; tape playback pushes the strip, PDC and spawn broadcasts as they are re-applied because the rewind reset the clients).

  Strips, TDLS and coordination are Sim arms (`StripCommandHandler`, `TdlsCommandHandler`, `CoordinationCommandHandler`) and so are the recorded ASDE-X / SAID mutations (`SimulationEngine.ApplyAsdexMutation` / `ApplySaidMutation`), and the ASDE-X safety-logic configuration (`SimulationEngine.ApplyRecordedAsdexSafetyLogic`), so the bare engine and a client-side replay apply them all; no host slot is left.

  The CRC entry points that write state issue canonical text through the same router: the F13 coordination entries (`AS 1M RDH DR EXPECT 28R`), the STARS console consolidation forms (`AS 2B CON+ 1N 1R`, `AS 2B DECON 2B`) and the CRC position sync (the RPO connection's own `AS {code}` — its TCP, else its callsign, else `callsign@tcp`, whichever first resolves back to exactly that position), so a rewind or a bundle reproduces them.
- **Sim-side replay restores the student position from snapshot 0.** The scenario JSON does not carry the resolved runtime student position (the server sets it at load via `InitializeTrackPositions`; the server reconstruction path re-derives it through `ReloadForRewind`).

  The Sim-only `ReplayWithScenarioOverride` loads only the scenario JSON, so it restores `StudentPosition`/`StudentTcp`/`StudentPositionType`/`IsStudentTowerPosition` from `SessionRecording.StudentPositionState` (populated by `RecordingArchive` from snapshot 0). Without it, `CanInitiateWithStudent` and the proactive check-in misbehave and many solo behaviors desync on client playback. Legacy recordings without snapshots carry no student position (null) and replay as before.

  **The resolved ATC roster rides the same path**: `ScenarioSnapshotDto.AtcPositions` (`AtcPositionDto`: the scenario's `atc` record plus the owner and TCP the room's ARTCC config resolved) is captured with every snapshot, restored by `RestoreFromSnapshot` when present, read from snapshot 0 into `SessionRecording.InitialAtcPositions` and restored by `ReplayDriver.To`.

  These are fresh objects on every replay, since a scrub replays one `SessionRecording` many times and a later `.AUTOTRACK` mutates the entries.

  Without it `TickDeferredAutoTrack` runs over an empty roster and a departure crossing the display floor replays unowned. A snapshot **without** the field (written before it existed) restores nothing and leaves the loader's roster in place — unlike the other nullable server-side fields, which restore empty — because on the server a rewind reloads through `PopulateRoom` and clearing it would strip the live room's positions; a pre-feature recording therefore still replays unowned on the client.
- **The session clock is one pinned instant, not "now".** `SimScenarioState.SessionStartUtc` is set once at load (the room's `TimeProvider` clock for a live load or restart, `RecordingManifest.ResolveSessionStartUtc()` for a loaded archive — the recorded value, else the `RecordedAtUtc` day for pre-feature archives, the process day for headless/soak/oracle rooms), carried in every `ScenarioSnapshotDto`, the manifest, `SessionRecording`, and the room checkpoint.

  `SimTimeUtc` (start + `ElapsedSeconds`) is the session's time of day on every run kind, and `MagneticModelDateUtc` (the start's UTC day) is fed to every sim-state use of `MagneticDeclination` (aircraft declination via `PhysicsTickOptions`, scenario/spawn magnetic→true headings).

  Without the pinned day a replay a year later drifts by the WMM's secular variation (~0.1°/yr) and byte-identical snapshot comparisons fail. Display-only callers (client, ASDE-X variation, navdata approach courses) still use the process day — they are not replayed state.

  The one sim-side residual is FRD resolution (`FrdResolver`) anchored on a non-navaid fix, which runs inside the command parser with no scenario in scope; an FRD anchored on a navaid follows the CIFP cycle's station declination instead.
- **The live host and the reconstruction host differ only in tape handling and METARs.**

  Both run the same spine and the same `TickProcessor` bodies; `LiveRoomHost` plays a tape back when the room is in playback (a `RecordedActionPump` over `scenario.ActionLog` seated at `scenario.PlaybackCursor` before every pass and written back after — rewind, seek and take-control move that cursor) and issues METARs, while `ReconstructionHost` re-applies a recorded log through its own pump and never issues METARs.

  Weather advances in the sim on both; a recorded weather change also tears the live METAR re-issuer down (`ApplyRecordedWeatherChange`), and the live host rebuilds it on return to live (`EnsureLiveMetarIssuer`).
- **A server rewind reads the room's resource pin, not the live vNAS caches.** Every reload of a room's scenario — restart, rewind, rewind-from-snapshot, session restore, the export reconstruction — rebuilds the loader and engine on the ARTCC configs and airport layouts the room's load used (`TrainingRoom.ResourcePin`), so an edit on vNAS between the load and the rewind cannot make the rewind diverge from the live run; a correction reaches the room at its next scenario load.

  A recording load replaces the pin with the resources its own scenario names. A server archive load pins the layouts and missing-map airports the archive bundles (`BundledAirportLayouts.FromRecording`) and fetches only the airports it lacks or holds in another layout format, logging each once, so a vNAS map change since the recording cannot renumber the graph under its snapshots. A session restore does the same from its checkpoint; a `SessionRecording` load fetches every layout.

  The v1 recording migration (`RecordingManager.MigrateToV2Async`, run by `tools/Yaat.RecordingUpgrader`) replays a v1 recording on its own scenario's resources, never the calling room's. See [server-rooms-and-hub.md](server-rooms-and-hub.md#the-resource-pin).
- **`Ground.Layout` doesn't round-trip.** Only the `LayoutAirportId` does. If a restore is missing a layout, that airport's GeoJSON wasn't loaded — fix the loader, don't add the layout to the DTO.
- **Don't add `[JsonIgnore]` and call it done.** If state matters across a session, it should serialize. CRC display state in particular must be wired through `ToSnapshot`/`FromSnapshot` — don't defer with "runtime-only" (see [crc-display-state.md](crc-display-state.md) and the `feedback_serialize_display_state` memory).
- **Build a diagnostic, don't grep.** When investigating "X diverges from Y over time," a `ReplayOneSecond()` loop comparing each second the archive has a snapshot for with `SnapshotTreeDiff` (§ Finding the first divergence) finds the first divergent snapshot in one pass. Five targeted `snapshot --at` calls is a sign you should be writing a diff iterator instead.
- **Schema bumps can be free.** A new optional field with a clean default doesn't need a migration step. Only bump `SchemaVersion` when old data needs transformation.
- **A phase that tells a new instruction by reference identity needs a flag in its snapshot.** `LandingPhase` tells a new exit instruction from the one it already acted on by comparing the preference it remembers (`_originalPreference`) with `PhaseList.RequestedExit` by reference: re-issuing the same `EXIT W3` must count as new, so value equality is wrong. A restore builds each side from its own DTO, and two instances read as a new instruction — re-enabling an exit the crew already gave up.

  `LandingPhaseDto.OriginalPreferenceIsRequestedExit` records whether they were one instance (`PhaseList.ToSnapshot` hands `RequestedExit` to `LandingPhase.ToSnapshot(ExitPreference?)`), and `PhaseList.FromSnapshot` shares the restored instance again (`LandingPhase.ShareRequestedExit`) only when it says so, so a re-issued `EXIT` not yet seen by a tick still restores as new. Any other phase that keys on reference identity needs the same.
- **Fillet-minted node ids are geometry-coupled.** Tangent-cut nodes are numbered by the fillet generator at parse time, and snapshot DTOs persist them (`RunwayExitPhaseDto.ExitNodeId`, `LandingPhaseDto.CandidateExitPathNodeIds`, taxi-route segment endpoints).

  Any fillet-geometry change renumbers or moves those nodes, so a restored phase can be handed a stale path (`[Exit] <callsign>: no edge between nodes A and B`); `RunwayExitPhase` degrades by re-searching from the aircraft's actual position, but every replay-derived expectation (node whitelists, taxiway at time T, event by time T) is coupled to the geometry version. Triage recipes for replay tests broken by a geometry change:
  - *Correct but late* (an exit or arc got longer, a speed profile changed, the event slid past the recorded window): keep the invariant and add a bounded physics-only tail — `ReplayOneSecond` to the window end, then `TickOneSecond` for up to ~60 s, breaking on the awaited state.
  - *Premise change* (an aircraft now legitimately picks a different exit): if the test's subject needs the recorded premise, pin it by injecting a command mid-replay — `Replay(rec, T_before_choice)` → `SendCommand("ER G")` → `ReplayOneSecond` loop. Find T with `python tools/bug_bundle.py history --callsign X`.
  - *Node-id whitelists*: hold-short ids come from the raw graph and are stable; tangent-cut ids are not. Never assert tangent-cut ids — regenerate whitelists from `LayoutInspector --exits`.
