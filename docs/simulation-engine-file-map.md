# Simulation engine file map

Per-file map of `src/Yaat.Sim/Simulation/` outside the folders that have their own doc, plus the engine-owned verb folders (`Coast/`, `Coordination/`, `Bookmarks/`, `Transport/`) and `Soak/` and `Testing/`.

The `SimulationEngine` partials, the spine and the oracle are in [tick-loop.md](tick-loop.md); actions in [command-pipeline.md](command-pipeline.md); replay and snapshots in [snapshots-and-replay.md](snapshots-and-replay.md); strips, vTDLS and ERAM state in [flight-strips.md](flight-strips.md), [vtdls.md](vtdls.md) and [crc-display-state.md](crc-display-state.md). The Task Index and the layers are in [architecture.md](architecture.md).

## File map

One entry per file; paths are relative to `src/Yaat.Sim/`.

```
# Simulation/
RunwayOccupancy.cs             # Phase-independent runway-use classifier (RunwayUseKind: Departing/Landing/OnSurface/ShortFinal/Crossing/Fouling — Fouling = off the pavement with a body part past the hold line, not an occupant: OccupiesSurface is false, FoulsRunway opts in);; IsAlongRunway (on the pavement + aligned: a use of the runway other than crossing, 3-1-3.a.4)
                               # IsOnFinal (6 nm advisory ring), ClassifyBest (oriented pavements, LandedOnRunway-aware), AirportRunways (FAA/ICAO).
                               # Phase evidence first (ClassifyByPhase), geometry (pavement rectangle + axis alignment on the ground,
                               # TCH + final-approach course in the air; rotorcraft over the pavement below 100 ft AGL = surface movement — descending
                               # Landing, < 20 kt OnSurface, else axis — never ShortFinal/Departing) only for phase-less aircraft; AirTaxiPhase /
                               # HelicopterLandingPhase by phase; IsRolling = 35 kt, or 20 kt + 2.5 kt/s over 4 s of feed samples; landing-threshold
                               # distance/time helpers.
                               # IsVacating (the pilot rule: leaving a runway with the tail still over the strict pavement or inside the hold line; TailCanBeInside prefilters runways the tail cannot reach), IsOverStrictPavement / AnyPartOverStrictPavement (any body part over the strict paved half-width; shared with RejectedTakeoff).
                               # Consumed by RunwaySafetyAdvisor, GroundConflictDetector (IsOnRunwayByPhase, ComputeRank), SoloTrainingEvaluator.IsTakeoffRoll; Fouling by RunwaySafetyAdvisor, OccupiedRunwayGoAround, SoloTrainingEvaluator, RejectedTakeoff, PositionJurisdiction, RunwayCrossingGate.
Attendance.cs                  # CRC attendance as engine state (the first recorded input, ADR 0003): Attendance — Replace(positionIds, config) resolves each vNAS position id to
                               # its owner + TCP through the room's ARTCC config (an unresolvable id stays id-only); IsTcpAttended / IsPositionIdAttended / IsOwnerAttended,
                               # ConsolidationOwnerOf + IsTcpControlledByCrc (the tick gates' question over the facility hierarchy + ConsolidationState); PositionIds for the
                               # snapshot and the record. Written on the tick thread only (the live sync, the router, restore) — no lock. + AttendedPosition(PositionId, Owner?, Tcp?)
RunProfile.cs                  # RunKind (Live/Replay/Test/Soak) + RunProfile: the one enumeration of what a replay may do differently —
                               # RecordsActions / RunsGenerators / RunsControllerAi, all false only for Replay (ADR 0005). Host state, never snapshotted.
SimulatedTowerRunwayGuard.cs   # Whether the silently simulated tower may clear a departure for takeoff now (ReasonToWait: traffic on, crossing, landing on or fouling the runway, an arrival inside the final gate, the wake interval behind the preceding departure from its AircraftGroundOps.TakeoffRollStartedAtSeconds, traffic on a crossing runway); null = clear
RunwayDepartureSeparation.cs   # The runway relations (TryResolveRunwayRelation, IsSameRunway) and departure wake-turbulence intervals (CwtCategoryOf, DepartureWakeIntervalFor, 7110.65 3-9-6) that SoloTrainingEvaluator and SimulatedTowerRunwayGuard share
AddAircraftOutcome.cs          # What an ADD produced: the aircraft now in the world + its spawn snapshot (baked onto the RecordedCommand), or the refusal
ActiveRunways.cs               # The room's active runway ends per airport (ActiveRunway: designator + ActiveRunwayUse Both/Departures/Arrivals), keyed by
                               # NavigationDatabase.NormalizeAirport in an ordinal sorted map; immutable, carried on SimScenarioState.ActiveRunways and snapshotted.
ImpliedActiveRunways.cs        # The runway ends a loaded scenario implies (For: spawns win whole per airport, then arrival generators, then the primary airport's
                               # facility knowledge pruned by the usability gate, then the generic rule), RoomDefault (only airports whose ends name every runway) and OfferedToMentor (the prompt's pre-fill: an all-arrival airport offered both ways).
ScenarioRunwayUse.cs           # What a loaded scenario already decides about runways, per airport (CountAssigned): its immediate and delayed aircraft that already
                               # have a runway (spawn runway, else expected approach, else first runway-bearing preset; on the ground: departures; airborne: arrivals)
                               # and the primary's arrival-generator runways. The load prompt's lines.
ScenarioRunwaySignals.cs       # The runways a loaded aircraft is sent to (AircraftRunways: its expected approach, then its parsed preset commands, never dispatched, each with a
                               # departure/arrival/both use) and the primary airport's arrival-generator runways; shared by ImpliedActiveRunways.For and ScenarioRunwayUse.CountAssigned.
ActiveRunwayListParser.cs      # The one runway-list grammar (command, sidecar, snapshot, prompt): tokens split on commas, spaces or newlines; `30` both, `D28L`
                               # departures, `A28R` arrivals; ends 01-36 with optional L/R/C. Parse checks ends against navdata; FromTokenLists (sidecar, snapshot) warns and drops.
                               # ToTokenLists is its inverse (airport -> token list); ReadStored reads a recording's or checkpoint's stored starting list (null when it stored none).
                               # Separators is public: CommandParser.ParseActiveRunways splits ARWY's airport from its runway tokens on them.
                               # ParseWithNone is the reader ARWY and the client's prompt share: a lone NONE (any case) is the empty list, NONE beside a runway is refused, else Parse.
SimScenarioState.cs            # Per-scenario runtime state: queues, settings, ATC positions, coordination, ArtccConfig (loaded from bundle on replay), ToweredAirports (the room's towered set; rebuilt from the bundled config for an older recording), WindGridPins (the scenario's pins on the GFS grids its weather names; ReplaceWindGridPins, released by the next LoadScenario), LiveTrafficFilter (carried from room settings),
                               # SessionStartUtc (the pinned instant t=0 is anchored to: the room clock for a live load or restart, the recorded instant for a replay, ProcessDayUtc — the unclamped process day — where no clock exists; snapshotted + in the recording manifest) + InitialActiveRunways (the active runways the session started on; kept for the session, in the recording and checkpoint manifests, not in snapshots) + SimTimeUtc (start + elapsed) + MagneticModelDateUtc (derived: the start's UTC day),
                               # AiStaffedPositions (published by the AI host; never snapshotted) + PilotContacts (memoized PilotContactRoster, also keyed on StudentLowestWorkedLevel and the ATC roster) + IsAiStaffed + StudentLowestWorkedLevel (the lowest position type the solo student works; snapshotted) + IsAutoClearedToLandFor / IsSimulatedTowerArrival (the room toggle or a silent simulated tower at the assigned runway's airport),
                               # AsdexSafetyLogicConfigs (each ASDE-X facility's safety-logic configuration by facility id: the load's seed, replaced per facility by a CRC push and re-picked on an active-runway change; snapshotted, read by the surface-alert step),
                               # AsdexOppDirPairs (the opposite-direction pair memory by facility id: pairs that raised an OPP DIR alert and still share the runway; snapshotted, never broadcast),
                               # ActiveAsdexAlerts (the standing ASDE-X alerts by facility id, then alert id: an ordinal ImmutableSortedDictionary swapped by reference — written only on the tick thread, read off the tick gate by the CRC initial-data build; snapshotted)
ScenarioPacing.cs              # Shared solo-training pacing helpers for parking call-up intervals and arrival generator rates
ArrivalSpacingManager.cs       # Pure in-trail spacing math for the generator stream, plus the speed-restore policy (SpeedRestoreGateNm, SpeedRestoreDeadbandKts) RestoreManagedSpeed and RNS-on-final share — simulated approach-controller speed equalization: SpacingCeilingKts (proportional ceiling) and InTrailCeilingKts (raises it with a time-based closure allowance while the pair — InTrailPair: leader/follower IAS, GS, Vref, along-final distance, target gap — is farther apart than the target, bounded by the leader's Vref so the gap still holds when it crosses the threshold, 7110.65 §5-5-4.h); SimulationEngine.ApplyArrivalSpacing drives it
SameRunwayArrivalProtection.cs # Same-runway arrival protection: required threshold interval (category constant / live leader rollout, floored by 3 NM radar + wake), §5-7-3.c distance-keyed speed floor, the 20 NM pre-clearance range, the 10 NM tower speed authority + Vapp (§5-7-3.f), and the shared vacate arithmetic OccupiedRunwayGoAround projects against; SimulationEngine.ApplySameRunwayArrivalProtection drives it
ScratchpadRuleEngine.cs        # Applies the facility's vNAS scratchpad rules (airport/route/altitude match → Template) to SP1/SP2
                               # at track-acquisition events. Only fills an empty field that was not explicitly cleared.
                               # Also owns MaxScratchpadLength (3, or 4 with Allow4CharacterScratchpad).
AutoScratchpadResolver.cs      # Pure: the STARS destination fallback shown in the SP1 slot when no real scratchpad is set,
                               # gated by the area's ShowDestination* adaptation; classifies departure/arrival/primary-arrival
                               # (primary = area.TowerListConfigurations[0]). Mirrors CRC GetScratchpads + UpdateFlightType.
                               # Display-only — DtoConverter fills AircraftStateDto.AutoScratchpad1; never persisted to AircraftState.
                               # Not truncated to the scratchpad limit — STARS clips only controller-entered text.
SessionRecording.cs            # v1 (commands) + v2 (commands + snapshots) recording format; InitialActiveRunways (the starting active runways); ArtccConfigJson optional bundle; WindGridPins ([JsonIgnore]: the pins on the archive's GFS grids, owned by whoever holds the recording); StudentPositionState (from snapshot 0) for Sim-side replay restore; TerminalLog (broadcast terminal stream) for terminal-scrub repopulation
EramEntrySource.cs             # `EramEntrySource` (Crc, Scope; written by name, an old integer still reads): where an ERAM entry was typed, provenance only, carried by the ERAM records and set on RecordedCommand / AmendFlightPlan / RequestNewBeaconCode / Chat only when an ERAM entry wrote them.
RecordedAction.cs              # Polymorphic recorded actions: Command, Chat, AmendFlightPlan, RequestNewBeaconCode, WeatherChange, SettingChange, AircraftSpawn,
                               # LiveTrafficSample (pre-tick, like AircraftSpawn — SimulationEngine.IsPreTickAction), LiveTrafficRemoval (a Deleted reason re-adds the callsign to SimScenarioState.SuppressedLiveTraffic on replay),
                               # LiveTrafficStatus (feed health + wall clock per status broadcast; diagnostic only, replay ignores it),
                               # AttendanceChange (the full set of attended vNAS position ids — a recorded input the live host derives from its connections when it changes, not a controller action),
                               # the derived records a CRC handler writes for state it used to change without a trace (tick-path 3d-5b):
                               # StarsSharedStateChange (a position's per-TCP shared display state), ClearanceChange, HoldAnnotationChange (null = delete),
                               # EramEntry (an ERAM keyboard entry in Commands/EramEntryEngine's grammar + the acting position's AS code), EramCrrGroup (null Lat = delete),
                               # EramRoomEntry (RecordedEramRoomEntry: FacilityId + one absolute CA settings or SM / SMDE sector-message entry with explicit sector ids — the recorder expands ALL),
                               # StripRequest (a manual Request Strip: null FacilityId = the hub's own-bay form, else CRC's departure strip for that facility; StripId is the
                               # id the live print minted, baked on so a same-room rewind prints nothing and a from-scratch reconstruction prints under the same id),
                               # AsdexSafetyLogicChange (FacilityId + Config, the Sim-native Asdex/AsdexSafetyLogicConfig the CRC wire DTO projects to — scenario state,
                               # applied by SimulationEngine.ApplyRecordedAsdexSafetyLogic on every run kind),
                               # AutoTrackChange (a CRC .AUTOTRACK delta — positive ids, -X, none — the second recorded input; SimulationEngine.ApplyAutoTrackChange applies it on every run kind)
                               # AmendFlightPlan's StripId is the id the amendment's departure-strip reprint printed under, baked on so a replay reprints
                               # under it instead of minting a second copy beside what a snapshot restore carried in; null on a pre-feature record and when
                               # the reprint printed nothing, both of which mint as before
                               # RecordedCommand bakes the live run's draws (ReactionDelaySeconds, SpawnJitterSeconds, SpawnedAircraft, IssuedAtUtc, StripId — the id a
                               # creating strip verb (SEP/HSC/SCAN) minted, so replay creates the item under it instead of drawing again — all nullable)
                               # for replay determinism and carries Accepted (null = pre-feature = accepted) so a replay can compare verdicts
RecordedTerminalEntry.cs       # One broadcast terminal line (kind/callsign/message) with wall-clock Timestamp + scenario-elapsed ElapsedSeconds; persisted as terminal-log.json.br so a loaded recording repopulates the terminal and each line scrubs the replay
RecordedCommandClassifier.cs   # The exhaustive command classifier: RecordedCommandKind (one per ParsedCommand subtype, no default —
                               # UnroutedCommandException) + ScopeOf(kind) → ActionScope + IsAviationCommand (the dispatcher-owned list).
                               # Classify(text) is the key into Simulation/Actions/ArmTable for every fresh and recorded command in Yaat.Sim;
                               # Say (the SAY* queries) and ShowQueued (SHOWAT/SHOWCOND — never recorded) are separate kinds; the live room, a Sim replay and a
                               # server reconstruction all look the same kind up
CommandScopes.cs               # SendsWithoutSelection(CanonicalCommandType): the typed verbs the desktop client sends with an empty callsign whatever is selected —
                               # every verb whose kind (VerbKinds, checked against the parser by CommandScopesTests) has Global or Position scope in
                               # RecordedCommandClassifier.ScopeOf, plus GHOST and TIMER. A body whose leading verb is one of them and fails to parse
                               # classifies as RecordedCommandKind.MalformedGlobal (Global, never recorded; ActionArms.MalformedGlobal answers the parser's reason)
TimerCommandApplier.cs         # The one TIMER body (set / cancel ActiveTimers on SimScenarioState, with the controller-facing messages);
                               # the router's Timer arm runs it on every run kind; the live room's OnTimersChanged consumer adds the broadcast
RecordingCompression.cs        # Brotli compress/decompress; auto-detects Brotli, gzip, or plain JSON on read
GroundLayoutSerializer.cs      # The one JSON form of a parsed AirportGroundLayout (Options, Serialize, Deserialize, Repair relinks edges/arcs to nodes): recordings, checkpoints and the precompute cache share it.
RecordingArchive.cs            # v4 ZIP archive reader: on-demand snapshot loading (JSON deserialized straight from the Brotli stream; spawn synthesis decodes only first-seen aircraft), layout/source-GeoJSON reading, seek API
                               # static ReadLayoutEntry / ReadAirportGeoJsonEntry (shared with the room checkpoint; edges re-linked to the layout's own nodes),
                               # CurrentLayoutFormatVersion (the layout JSON form itself is GroundLayoutSerializer.Options, fields included for value-tuple coordinates)
                               # ToBaseSessionRecording (no snapshots), FindNearestSnapshotIndex, ReadSnapshotAt, ReadArtccConfigJson
                               # WindGridEntryName / RegisterWindGrids (wind-grids/{hash}.grib2.br: verify, register and pin each GFS grid before the weather text is parsed; returns WindGridPins)
                               # static ReadWindGridEntry (one entry as a WindGridEntryRead) / RegisterWindGridEntries (register and pin a list of entries), which the room checkpoint shares; the limit policy stays with each caller
                               # ReadToweredAirportIds (towered-airports.json.br, manifest HasToweredAirports; null in an older recording, whose replay rebuilds the set from the bundled own config)
                               # ReadBookmarks / static WriteBookmarks (client-injected bookmarks.json; optional, manifest-untracked)
TimelineBookmark.cs            # TimelineBookmark + RecordingBookmarks records (the bookmarks.json payload)
RecordingArchiveWriter.cs      # v4 ZIP archive writer (an ILayoutBundleWriter): streaming snapshots + layouts/source GeoJSON + missing-map airport list + bundled ArtccConfig; WriteToweredAirports (towered-airports.json.br);
                               # static WriteLayoutEntry / WriteAirportGeoJsonEntry, which the room checkpoint writer shares
                               # WriteWindGrid(hash, grib2): one Brotli wind-grids/ entry per distinct hash, listed in the manifest's WindGridHashes (over the static WriteWindGridEntry, which the room checkpoint shares); public static WriteReferencedWindGrids(writer, initialWeatherJson, actions, snapshotWeatherJsons) writes every grid the weather names (a server export and the soak sink call it)
WindGridEntryRead.cs           # WindGridEntryRead (GRIB2 bytes or why not, the entry name, the actual hash) + WindGridEntryProblem (None, Missing, TooLarge, WrongHash): what ReadWindGridEntry returns
ILayoutBundleWriter.cs         # The archive writer a room's pinned layouts are bundled into (recording archive or room checkpoint): WriteLayout, WriteAirportGeoJson, WriteMissingLayoutAirportIds
RecordingManifest.cs           # Archive manifest: snapshot index, LayoutAirportIds, AirportGeoJsonIds, LayoutFormatVersion, MissingLayoutAirportIds, WindGridHashes (the wind-grids/ entries; null = none), HasArtccConfig, metadata (client/build/server version, optional ServerName), SessionStartUtc (+ ResolveSessionStartUtc: recorded instant → RecordedAtUtc day → process day), InitialActiveRunways (airport → token list; null when not stored)
RecordingSchemaUpgrader.cs     # Surgical in-place snapshot schema upgrade (via SnapshotSchemaMigrator, never re-sim); handles .br/v4-zip/bug-bundle; drives yaat-server's Yaat.RecordingUpgrader CLI.
                               # Also rewrites recorded canonicals in place: retired HSE → HSA id form (HalfStripEditCanonicalRewriter) and the required FACILITY/BAY bay token (StripBayCanonicalQualifier, resolved against the recording's own ArtccConfig + student position).
StripBayCanonicalQualifier.cs  # Idempotent bay-token qualifier for recorded canonicals: adds the owning facility to STRIP/SCAN/HSC/HSM/SEP/SEPM/BLANK/... dest-specs; leaves id-form and bayless verbs alone.
HalfStripEditCanonicalRewriter.cs # Idempotent rewrite of the retired `HSE <id> …` half-strip verb into `HSA <id> …` for archived action logs.
DepartureGrammarCanonicalRewriter.cs # Rewrites recorded CTO/LUAW text from before schema 42 (`CTO NN` → `CTO 0NN`; the refused `CTO 28R` / `LUAW 28L` → `… RWY …`, still refused so still dropped); unsafe on current text, so gated on `PredatesRunwayGrammar` of the first snapshot's schema, by `RecordingArchive.ReadActions` at load and `RecordingSchemaUpgrader` on disk.
FiledAltitudeSpawnStateRewriter.cs # Seeds the filed altitude (`FiledCruiseAltitudeFt`) of each recorded live-traffic shadow spawn state and `ADD` spawn (`RecordedCommand.SpawnedAircraft`; defensive, no `ADD` path files a plan altitude) from its plan's altitude (null without one) for a tape from before schema 55; unsafe on current text, where null is what was recorded, so gated on `PredatesFiledCruiseAltitude` of the first snapshot's schema, by `RecordingArchive.ReadActions` at load and `RecordingSchemaUpgrader` on disk.
CompoundCanonical.cs           # RewriteUnits: applies a per-unit rewrite across `;`/`,` compound canonicals, preserving separators and padding; returns the input instance when nothing changed.
RecordingJsonOptions.cs        # Shared JsonSerializerOptions for recording serialization

# Simulation/Coast/ — the disconnect-coast types (SimulationEngine.DisconnectCoast.cs owns the lifecycle)
DisconnectCoastScope.cs        # Eram / Asdex / Said
DisconnectCoastFacet.cs        # DisconnectCoastFacet(Scope, FacilityId — null for ERAM, IsDrop, DeadlineSimSeconds, EramAnchor — the EramSweepSample of the coverage coast an ERAM facet carries on, else null)
EramCoverageCoast.cs           # Static: the ERAM coverage-loss coast state machine, run per aircraft by the EramCoverageCoast spine step (hysteresis 200 ft over the 1,500 ft AGL floor, 24 s coast);
                               # writes AircraftEramState.IsVisibleOnEramCoverage / IsCoverageCoasting / CoverageCoastStartSeconds / CoverageCoastAnchor and samples LastEramSweep (an EramSweepSample)
                               # on each EramSweepGrid sweep and on turning visible; a frozen or QT-coasted track is always visible; tests EramCoverageCoastStepTests, EramSweepGridTests, DisconnectCoastSnapshotTests
AircraftDisconnectCoast.cs     # AircraftDisconnectCoast(Anchor, AnchorTrackDeg, AnchorGroundSpeed, CoastStartSimSeconds, Facets): the last pose a coast dead-reckons from
ExpiredDisconnectCoastFacet.cs # (Callsign, Facet) — one entry of an OnDisconnectCoastExpired payload
EramSweepGrid.cs               # Static: the ERAM 12 s sweep grid (SweepSeconds, OffsetSeconds — FNV-1a of the callsign mod 12, Index, LastSweepSimSeconds); the ERAM
                               # facet's deadline is the last grid sweep (not before spawn) + 24 s; yaat-server's AircraftChangeTracker sweeps on the same grid
DisconnectCoastRules.cs        # IsVisibleOnEram (ERAM coverage: field elevation + EramCoverageFloorAglFt 1,500 ft; frozen / unsupported ghost visible, on-ground not) and
                               # IsDestinationFacility; yaat-server's CrcVisibilityTracker.IsVisibleOnEram wraps the first. Also EramCoverageHysteresisFt (200) and
                               # EramCoverageCoastSeconds (= SimScenarioState.EramDisconnectCoastSeconds, one figure by design), the figures EramCoverageCoast shares

# Simulation/Coordination/ — STARS coordination-verb command logic, engine-owned (SimulationEngine.Coordination.cs owns the timer/init/dirty-flag half)
CoordinationCommandHandler.cs  # Static dispatch for the aircraft-scoped verbs (RD/RDH/RDR/RDACK/RDDEL/RDPOS/RDTXT, via Handle) and the position-scoped
                               # RDAUTO (HandleGlobal) against SimScenarioState.CoordinationChannels. InferSenderListId resolves a list-less sender verb
                               # ahead of time so the recorded canonical carries the list explicitly. Item ids are {ListId}-{SequenceNumber} off the
                               # channel's snapshotted counter, not a fresh draw, so a rebuilt list holds the same items under the same ids; every
                               # successful mutation calls SimulationEngine.MarkCoordinationChanged

# Simulation/Bookmarks/ — timeline-bookmark verb logic, engine-owned (SimulationEngine.Bookmarks.cs owns the bodies + dirty flag)
BookmarkCommandHandler.cs      # Static dispatch of BM ADD / RENAME / DELETE / DEL ALL (HandleAdd/HandleRename/HandleDelete/HandleDeleteAll) onto the engine bodies;
                               # ADD stamps the scenario's ElapsedSeconds. Sim arm

# Simulation/Transport/ — session-clock verb logic, engine-owned (SimulationEngine.Transport.cs owns the bodies + dirty flag)
TransportCommandHandler.cs     # Static dispatch of PAUSE / UNPAUSE / SIMRATE onto SimulationEngine.Pause/Resume/SetSimRate. Sim arm

# Soak/ — soak-testing harness pieces shared by the yaat-server soak runner and live attach (docs/plans/controller-ai/08)
CapturedLogRecord.cs           # One captured log entry (level, category, formatted message, exception text, UTC stamp)
CapturingSimLogProvider.cs     # Bounded ring-buffer ILoggerProvider: Warning+ tap registered on the same factory as SimLog.Initialize; Drain() per tick, DroppedCount on overflow

# Testing/
TestVnasData.cs                # Shared test data loader: NavData and CIFP pinned to the committed TestData files (`PinnedNavDataOptions`, `PinnedCifpOptions`, `PinCommittedData`), AircraftSpecs, AircraftCwt, FaaAcd, AircraftProfiles, FacilityOpsDatabase (from Data/FacilityOps). The AircraftSpecs, AircraftCwt and FaaAcd loaders throw on a missing or empty file. EnsureAircraftDataInitialized loads just the aircraft tables once per process without touching the NavigationDatabase singleton, for a harness (e.g. yaat-server's RoomEngineTestHarness) that installs its own

Proto/nav_data.proto           # Compiled by Grpc.Tools → NavDataSet
```
