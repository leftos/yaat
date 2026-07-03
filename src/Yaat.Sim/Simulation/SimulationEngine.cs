using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Result from <see cref="SimulationEngine.TickPrePhysics"/>. <see cref="SpawnedAircraft"/> lists the
/// delayed-queue aircraft spawned this tick; <see cref="GeneratorSpawns"/> lists the arrival-generator
/// spawns paired with their autotrack configuration. The server broadcasts both and, for generator
/// spawns that carry autotrack, applies the owner/scratchpad/handoff before broadcasting.
/// </summary>
public record struct TickPrePhysicsResult(List<AircraftState> SpawnedAircraft, List<GeneratorSpawn> GeneratorSpawns);

/// <summary>
/// One arrival-generator spawn this tick paired with its generator's <see cref="AutoTrackConditions"/>
/// (null when the generator has none). Threaded out of the sim so the server can apply the autotrack and
/// record the spawn AFTER, so the owner/scratchpad land in the initial broadcast and the recorded
/// snapshot replays with them intact (the eager in-sim recording would capture an untracked state).
/// </summary>
public readonly record struct GeneratorSpawn(AircraftState State, AutoTrackConditions? AutoTrack);

/// <summary>
/// Diagnostic record of one arrival-generator spawn. Lets the time-first spawn cadence and placement
/// be inspected and asserted in tests. <see cref="RearmostAtSpawnNm"/> is null when the corridor was
/// empty (the arrival spawned at <c>InitialDistance</c>); <see cref="RequiredGapNm"/> is then 0 — otherwise
/// it is the binding in-trail gap (max of <c>IntervalDistance</c> and the wake minimum) the arrival was
/// placed behind the rearmost.
/// </summary>
public readonly record struct GeneratorSpawnRecord(
    string GeneratorId,
    string Callsign,
    double ElapsedSeconds,
    double SpawnDistanceNm,
    double? RearmostAtSpawnNm,
    double RequiredGapNm
);

public sealed class SimulationEngine
{
    private const int PhysicsSubTickRate = 4;

    private readonly IAirportGroundData _groundData;

    private readonly ILogger _logger;
    private readonly List<TerminalEntry> _terminalEntries = [];

    // Replay cursor state — set by Replay(), consumed by ReplayOneSecond()
    private List<RecordedAction>? _replayActions;
    private int _replayActionCursor;
    private int _replayPreTickActionCursor;
    private readonly HashSet<int> _replayPreTickAppliedActionIndexes = [];
    private bool _isReplayingRecordedActions;
    private bool _replayHasRecordedAircraftSpawns;

    // Replay-time track applier: routes track commands and AS-prefixed compounds through
    // the shared Sim helpers (TrackEngine.Dispatch + TrackResolver) during Replay/ReplayRange
    // so in-engine replay reaches the same state captured in recorded snapshots. Reset at the
    // start of each fresh Replay/ReplayRange call (startSeconds == 0).
    private readonly ReplayTrackApplier _replayTrackApplier = new();

    // Holds the set of hold-short node IDs currently occupied by aircraft.
    // Built at the start of each TickPhysics, used by PreTick to prevent stacking.
    private HashSet<int>? _occupiedHoldShortNodes;

    public SimulationWorld World { get; } = new();
    public SimScenarioState? Scenario { get; set; }
    public ConsolidationState ConsolidationState { get; } = new();
    public ApproachEvaluator ApproachEvaluator { get; } = new();
    public SoloTrainingEvaluator SoloTrainingEvaluator { get; } = new();
    public BeaconCodePool BeaconCodePool { get; } = new();
    public TowerListTracker TowerListTracker { get; } = new();
    public ConflictAlertState ConflictAlerts { get; } = new();

    private readonly List<GeneratorSpawnRecord> _generatorSpawnLog = [];

    /// <summary>Diagnostic log of arrival-generator spawns (distance, spacing, timing) for the session.</summary>
    public IReadOnlyList<GeneratorSpawnRecord> GeneratorSpawnLog => _generatorSpawnLog;

    /// <summary>
    /// Diagnostic per-tick timing buckets. Keyed by bucket name (e.g. "PrePhysics",
    /// "Physics.Ground", "Physics.World", "PostPhysics"). Populated by
    /// <see cref="ReplayRange"/>. Reset at the start of each <see cref="Replay"/> /
    /// <see cref="ReplayRange"/> call. Intended for test instrumentation only —
    /// call <see cref="DumpTickTimings"/> to format.
    /// </summary>
    public Dictionary<string, (int Count, double Ms)> TickTimings { get; } = new();

    /// <summary>
    /// Fires at the end of each integer-second tick, after physics and post-physics
    /// complete. The int argument is <c>Scenario.ElapsedSeconds</c> at tick end.
    /// Fires from <see cref="TickOneSecond"/>, <see cref="ReplayOneSecond"/>,
    /// <see cref="ReplayRange"/>, and <see cref="ReplayOneSubTick"/> (at second-end only).
    /// Intended for test instrumentation (see <c>TickRecorder.Attach</c>).
    /// </summary>
    public event Action<int>? TickCompleted;

    private void FireTickCompleted(int elapsedSeconds)
    {
        TickCompleted?.Invoke(elapsedSeconds);
    }

    /// <summary>
    /// Fires during the post-physics drain for each <see cref="AircraftState.PendingWarnings"/>
    /// entry produced this tick — queue-clear notices, missed AT/AT-fix conditions, deferred
    /// commands rejected when their trigger fires, etc. Mirrors the server's
    /// <c>TickProcessor.BroadcastWarnings</c> fan-out so non-server consumers (solo client,
    /// tests) can react to the same per-aircraft warnings the RPO would see in the terminal
    /// log. Default null = warnings are still drained from the aircraft (so they don't
    /// accumulate) but otherwise discarded by this engine instance.
    /// </summary>
    public event Action<string, string>? WarningEmitted;

    private void FireWarningEmitted(string callsign, string warning)
    {
        WarningEmitted?.Invoke(callsign, warning);
    }

    public SimulationEngine(IAirportGroundData groundData, ILogger? logger = null)
    {
        _groundData = groundData;
        _logger = logger ?? SimLog.CreateLogger<SimulationEngine>();
    }

    // --- Drain collections ---

    public List<TerminalEntry> DrainTerminalEntries()
    {
        var entries = new List<TerminalEntry>(_terminalEntries);
        _terminalEntries.Clear();
        return entries;
    }

    /// <summary>
    /// Append a terminal entry produced by an external dispatcher (e.g., the server's
    /// RoomEngine) so it surfaces through the same drain path as engine-internal entries.
    /// Callers wire this into <see cref="DispatchContext.TerminalEmitter"/> when they
    /// dispatch commands outside the engine's own SendCommand/preset/replay paths.
    /// </summary>
    public void EmitTerminalEntry(TerminalEntry entry) => _terminalEntries.Add(entry);

    // --- Snapshots ---

    public StateSnapshotDto CaptureSnapshot(int actionIndex)
    {
        var scenario = Scenario ?? throw new InvalidOperationException("No scenario loaded.");
        var aircraft = World.GetSnapshot();

        return new StateSnapshotDto
        {
            ElapsedSeconds = scenario.ElapsedSeconds,
            Rng = World.Rng.GetState(),
            WeatherJson = World.Weather is not null ? JsonSerializer.Serialize(World.Weather) : null,
            Aircraft = aircraft.Select(ac => ac.ToSnapshot()).ToList(),
            Scenario = scenario.ToSnapshot(),
            Server = CaptureServerSnapshot(aircraft),
        };
    }

    public void RestoreFromSnapshot(StateSnapshotDto snapshot)
    {
        SnapshotSchemaMigrator.Migrate(snapshot);

        World.Clear();
        World.Rng = new SerializableRandom(snapshot.Rng.S0, snapshot.Rng.S1, snapshot.Rng.S2, snapshot.Rng.S3);
        World.Weather = snapshot.WeatherJson is not null ? JsonSerializer.Deserialize<WeatherProfile>(snapshot.WeatherJson) : null;

        var scenarioDto = snapshot.Scenario;

        // Resolve ground layout for the primary airport
        AirportGroundLayout? groundLayout = null;
        if (scenarioDto.PrimaryAirportId is not null)
        {
            groundLayout = _groundData.GetLayout(scenarioDto.PrimaryAirportId);
            World.GroundLayout = groundLayout;
        }

        foreach (var acDto in snapshot.Aircraft)
        {
            var ac = AircraftState.FromSnapshot(acDto, groundLayout);
            World.AddAircraft(ac);
        }

        // Restore scenario state — we need the original scenario JSON from the existing Scenario
        // (it's not in the snapshot DTO since it's immutable). The caller must ensure Scenario
        // is pre-populated with the original scenario metadata before calling RestoreFromSnapshot.
        if (Scenario is not null)
        {
            Scenario.ElapsedSeconds = scenarioDto.ElapsedSeconds;
            Scenario.AutoClearedToLand = scenarioDto.AutoClearedToLand;
            Scenario.AutoCrossRunway = scenarioDto.AutoCrossRunway;
            Scenario.AutoPullUpToParallel = scenarioDto.AutoPullUpToParallel;
            Scenario.ValidateDctFixes = scenarioDto.ValidateDctFixes;
            Scenario.SoloTrainingMode = scenarioDto.SoloTrainingMode;
            Scenario.SoloParkingInitialCallupRatePercent = scenarioDto.SoloParkingInitialCallupRatePercent;
            Scenario.SoloArrivalGeneratorRatePercent = scenarioDto.SoloArrivalGeneratorRatePercent;
            Scenario.SoloGoAroundProbabilityPercent = ScenarioPacing.ClampGoAroundProbabilityPercent(scenarioDto.SoloGoAroundProbabilityPercent);
            Scenario.HasSoloParkingInitialCallupSource = scenarioDto.HasSoloParkingInitialCallupSource;
            Scenario.HasSoloArrivalGeneratorSource = scenarioDto.HasSoloArrivalGeneratorSource;
            Scenario.NextSoloParkingInitialCallupSlotSeconds = scenarioDto.NextSoloParkingInitialCallupSlotSeconds;
            Scenario.RpoShowPilotSpeech = scenarioDto.RpoShowPilotSpeech;
            Scenario.MetarReissuanceEnabled = scenarioDto.MetarReissuanceEnabled;
            Scenario.WeatherSourceJson = scenarioDto.WeatherSourceJson;

            // Rebuild the forward-evolving weather timeline from the persisted source — otherwise it
            // is lost on a snapshot-based rewind and the weather freezes. World.Weather keeps the
            // snapshot's collapsed profile (restored above) as the authoritative current state.
            Scenario.WeatherTimeline = null;
            if (scenarioDto.WeatherSourceJson is { } weatherSourceJson)
            {
                var weatherParse = WeatherTimelineParser.Parse(weatherSourceJson);
                if (weatherParse.IsTimeline)
                {
                    Scenario.WeatherTimeline = weatherParse.Timeline;
                }
            }

            Scenario.IsPaused = scenarioDto.IsPaused;
            Scenario.SimRate = scenarioDto.SimRate;
            Scenario.CommandRunDelayMinSeconds = scenarioDto.CommandRunDelayMinSeconds;
            Scenario.CommandRunDelayMaxSeconds = scenarioDto.CommandRunDelayMaxSeconds;
            Scenario.AutoAcceptDelay = TimeSpan.FromSeconds(scenarioDto.AutoAcceptDelaySeconds);
            Scenario.IsStudentTowerPosition = scenarioDto.IsStudentTowerPosition;
            Scenario.ScenarioAutoDeleteMode = scenarioDto.ScenarioAutoDeleteMode;
            Scenario.ClientAutoDeleteOverride = scenarioDto.ClientAutoDeleteOverride;
            Scenario.StudentPosition = scenarioDto.StudentPosition is not null ? TrackOwner.FromSnapshot(scenarioDto.StudentPosition) : null;
            Scenario.StudentTcp = scenarioDto.StudentTcp is not null ? Tcp.FromSnapshot(scenarioDto.StudentTcp) : null;
            World.StudentTcp = Scenario.StudentTcp;
            Scenario.StudentPositionType = scenarioDto.StudentPositionType;

            // Clear and restore queues
            Scenario.DelayedQueue.Clear();
            Scenario.TriggerQueue.Clear();
            Scenario.PresetQueue.Clear();
            Scenario.DelayedHandoffQueue.Clear();
            Scenario.Generators.Clear();
            Scenario.HeldDepartureAirports.Clear();
            Scenario.ReleaseQueue.Clear();
            Scenario.ActiveTimers.Clear();

            if (scenarioDto.DelayedQueue is not null)
            {
                foreach (var d in scenarioDto.DelayedQueue)
                {
                    var aircraft = JsonSerializer.Deserialize<LoadedAircraft>(d.AircraftJson)!;
                    // Reattach ground layout — excluded from JSON by [JsonIgnore], resolve by airport ID
                    if (aircraft.State.Ground.LayoutAirportId is { } layoutAirportId)
                    {
                        aircraft.State.Ground.Layout = _groundData.GetLayout(layoutAirportId);
                    }

                    Scenario.DelayedQueue.Add(
                        new DelayedSpawn
                        {
                            Aircraft = aircraft,
                            SpawnAtSeconds = d.SpawnAtSeconds,
                            HeldForRelease = d.HeldForRelease,
                        }
                    );
                }
            }

            if (scenarioDto.HeldDepartureAirports is not null)
            {
                foreach (var airport in scenarioDto.HeldDepartureAirports)
                {
                    Scenario.HeldDepartureAirports.Add(airport);
                }
            }

            if (scenarioDto.ReleaseQueue is not null)
            {
                foreach (var r in scenarioDto.ReleaseQueue)
                {
                    Scenario.ReleaseQueue.Add(
                        new ScheduledRelease
                        {
                            Airport = r.Airport,
                            Callsign = r.Callsign,
                            FireAtSeconds = r.FireAtSeconds,
                        }
                    );
                }
            }

            Scenario.NextTimerId = scenarioDto.NextTimerId;
            if (scenarioDto.ActiveTimers is not null)
            {
                foreach (var t in scenarioDto.ActiveTimers)
                {
                    Scenario.ActiveTimers.Add(
                        new ActiveTimer
                        {
                            Id = t.Id,
                            Callsign = t.Callsign,
                            Message = t.Message,
                            FireAtSeconds = t.FireAtSeconds,
                            TotalSeconds = t.TotalSeconds,
                        }
                    );
                }
            }

            if (scenarioDto.TriggerQueue is not null)
            {
                foreach (var t in scenarioDto.TriggerQueue)
                {
                    Scenario.TriggerQueue.Add(new ScheduledTrigger { Command = t.Command, FireAtSeconds = t.FireAtSeconds });
                }
            }

            if (scenarioDto.PresetQueue is not null)
            {
                foreach (var p in scenarioDto.PresetQueue)
                {
                    Scenario.PresetQueue.Add(
                        new ScheduledPreset
                        {
                            Callsign = p.Callsign,
                            Command = p.Command,
                            FireAtSeconds = p.FireAtSeconds,
                        }
                    );
                }
            }

            if (scenarioDto.DelayedHandoffQueue is not null)
            {
                foreach (var h in scenarioDto.DelayedHandoffQueue)
                {
                    Scenario.DelayedHandoffQueue.Add(
                        new DelayedHandoff
                        {
                            Callsign = h.Callsign,
                            Target = TrackOwner.FromSnapshot(h.Target),
                            FireAtSeconds = h.FireAtSeconds,
                        }
                    );
                }
            }

            if (scenarioDto.Generators is not null)
            {
                foreach (var g in scenarioDto.Generators)
                {
                    var config = JsonSerializer.Deserialize<ScenarioGeneratorConfig>(g.ConfigJson)!;
                    Scenario.Generators.Add(
                        new GeneratorState
                        {
                            Config = config,
                            Runway = RunwayInfo.FromSnapshot(g.Runway),
                            NextSpawnSeconds = g.NextSpawnSeconds,
                            IsExhausted = g.IsExhausted,
                        }
                    );
                }
            }

            CoordinationChannelSnapshotMapper.RestoreChannels(Scenario.CoordinationChannels, scenarioDto.CoordinationChannels);
        }

        // Reset engine-level state, then restore from snapshot if available
        ConsolidationState.Clear();
        ConflictAlerts.Conflicts.Clear();
        SoloTrainingEvaluator.Reset();
        BeaconCodePool.Clear();

        if (snapshot.Server is not null)
        {
            RestoreServerSnapshot(snapshot.Server);
        }

        // Advance replay cursors to match the restored scenario time. Without this,
        // a subsequent ReplayOneSecond() would treat actions from t=0 onward as
        // still-pending and re-apply them on top of the restored state. Enables
        // the hybrid-replay pattern: Replay(recording, 0) to load the scenario,
        // RestoreFromSnapshot to jump to a saved state, then ReplayOneSecond to
        // step forward from there with cursors already positioned.
        if (_replayActions is not null && Scenario is not null)
        {
            int restoredSeconds = (int)Scenario.ElapsedSeconds;
            _replayActionCursor = 0;
            _replayPreTickActionCursor = 0;
            _replayPreTickAppliedActionIndexes.Clear();
            while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= restoredSeconds)
            {
                _replayActionCursor++;
            }
            while (_replayPreTickActionCursor < _replayActions.Count && _replayActions[_replayPreTickActionCursor].ElapsedSeconds <= restoredSeconds)
            {
                _replayPreTickActionCursor++;
            }
        }
    }

    private ServerSnapshotDto CaptureServerSnapshot(List<AircraftState> aircraft)
    {
        var consolidation = ConsolidationState
            .GetSnapshot()
            .ToDictionary(kv => kv.Key, kv => new ConsolidationOverrideDto { ReceivingTcpId = kv.Value.ReceivingTcpId, IsBasic = kv.Value.IsBasic });

        var conflicts = ConflictAlerts
            .Conflicts.Values.Select(c => new ActiveConflictDto
            {
                Id = c.Id,
                CallsignA = c.CallsignA,
                CallsignB = c.CallsignB,
                IsAcknowledged = c.IsAcknowledged,
            })
            .ToList();

        var beaconCodes = new Dictionary<uint, string>();
        foreach (var ac in aircraft)
        {
            if (ac.Transponder.AssignedCode > 0)
            {
                beaconCodes[ac.Transponder.AssignedCode] = ac.Callsign;
            }
        }

        return new ServerSnapshotDto
        {
            ConsolidationOverrides = consolidation,
            ActiveConflicts = conflicts,
            BeaconCodePool = new BeaconCodePoolDto
            {
                AssignedCodes = beaconCodes,
                NextCandidate = BeaconCodePool.NextCandidate,
                BankCursors = new Dictionary<int, uint>(BeaconCodePool.BankCursors),
            },
        };
    }

    private void RestoreServerSnapshot(ServerSnapshotDto server)
    {
        if (server.ConsolidationOverrides is not null)
        {
            var overrides = server.ConsolidationOverrides.ToDictionary(
                kv => kv.Key,
                kv => new ConsolidationState.ManualOverride(kv.Value.ReceivingTcpId, kv.Value.IsBasic)
            );
            ConsolidationState.Restore(overrides);
        }

        if (server.ActiveConflicts is not null)
        {
            foreach (var c in server.ActiveConflicts)
            {
                ConflictAlerts.Conflicts[c.Id] = new ActiveConflict
                {
                    Id = c.Id,
                    CallsignA = c.CallsignA,
                    CallsignB = c.CallsignB,
                    IsAcknowledged = c.IsAcknowledged,
                };
            }
        }

        if (server.BeaconCodePool is { } beaconPool)
        {
            if (beaconPool.AssignedCodes is not null)
            {
                foreach (var code in beaconPool.AssignedCodes.Keys)
                {
                    BeaconCodePool.MarkUsed(code);
                }
            }

            BeaconCodePool.RestoreCursors(beaconPool.NextCandidate, beaconPool.BankCursors);
        }
    }

    // --- Scenario loading ---

    public List<string> LoadScenario(string json, int rngSeed)
    {
        World.Clear();
        World.Rng = new SerializableRandom(rngSeed);
        World.ReactionDelayRng = new SerializableRandom(rngSeed);
        World.ReleaseJitterRng = new SerializableRandom(rngSeed);
        ApproachEvaluator.Reset();
        SoloTrainingEvaluator.Reset();

        var result = ScenarioLoader.Load(json, _groundData, World.Rng);

        Scenario = new SimScenarioState
        {
            ScenarioId = ScenarioIdentity.ResolveScenarioId(result.Id, json),
            ScenarioName = result.Name,
            RngSeed = rngSeed,
            OriginalScenarioJson = json,
            PrimaryAirportId = result.PrimaryAirportId,
            ArtccId = result.ArtccId,
            InitialContactTransfers = NavigationDatabase.Instance.InitialContactTransfers,
            WakeDirectives = NavigationDatabase.Instance.WakeDirectives,
            HasSoloParkingInitialCallupSource = result.HasParkingSpawns,
            HasSoloArrivalGeneratorSource = result.HasArrivalGenerators,
        };

        // Add immediate aircraft and dispatch their presets
        foreach (var loaded in result.ImmediateAircraft)
        {
            loaded.State.ScenarioId = Scenario.ScenarioId;
            loaded.State.SpawnedAtSeconds = Scenario.ElapsedSeconds;
            World.AddAircraft(loaded.State);
            DispatchPresetCommands(loaded);
        }

        // Queue delayed aircraft
        foreach (var loaded in result.DelayedAircraft)
        {
            loaded.State.ScenarioId = Scenario.ScenarioId;
            Scenario.DelayedQueue.Add(
                new DelayedSpawn
                {
                    Aircraft = loaded,
                    SpawnAtSeconds = loaded.SpawnDelaySeconds,
                    HeldForRelease = DepartureSpawnClassifier.IsHeldSpawnCandidate(loaded),
                }
            );
        }

        // Queue triggers
        foreach (var trigger in result.Triggers)
        {
            Scenario.TriggerQueue.Add(new ScheduledTrigger { Command = trigger.Command, FireAtSeconds = trigger.TimeOffset });
        }

        // Initialize generators
        _generatorSpawnLog.Clear();
        foreach (var genConfig in result.Generators)
        {
            var runwayId = genConfig.Runway ?? "";
            var runway = NavigationDatabase.Instance.GetRunway(result.PrimaryAirportId ?? "", runwayId);
            if (runway is null)
            {
                result.Warnings.Add($"Generator '{genConfig.Id}': runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} not found");
                continue;
            }

            // The first generator fires on its authored schedule. Each subsequent generator with
            // randomized intervals gets a random initial phase within its first interval, so multiple
            // generators that share a startTimeOffset don't all spawn on the same first tick. Keyed off
            // the count of already-added generators, so a generator skipped for a missing runway above
            // doesn't consume the "first" slot.
            var firstSpawnSeconds = (double)genConfig.StartTimeOffset;
            if (Scenario.Generators.Count > 0 && genConfig.RandomizeInterval)
            {
                firstSpawnSeconds += World.Rng.NextDouble() * genConfig.IntervalTime;
            }

            Scenario.Generators.Add(
                new GeneratorState
                {
                    Config = genConfig,
                    Runway = runway,
                    NextSpawnSeconds = firstSpawnSeconds,
                }
            );
        }

        // Set ground layout
        if (Scenario.PrimaryAirportId is not null)
        {
            World.GroundLayout = _groundData.GetLayout(Scenario.PrimaryAirportId);
        }

        if (result.AutoDeleteMode is not null)
        {
            // Store but engine doesn't process auto-delete
        }

        return result.Warnings;
    }

    // --- Three-phase tick API ---

    /// <summary>
    /// Pre-physics: process delayed spawns, generators, triggers, timed presets,
    /// and ensure ground layout. Returns a list of aircraft spawned this tick.
    /// Terminal entries are accumulated and can be drained via <see cref="DrainTerminalEntries"/>.
    /// </summary>
    public TickPrePhysicsResult TickPrePhysics()
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return new TickPrePhysicsResult([], []);
        }

        var spawned = new List<AircraftState>();
        var generatorSpawns = new List<GeneratorSpawn>();

        ProcessDelayedSpawns(spawned);
        ProcessGenerators(generatorSpawns);
        ApplyArrivalSpacing();
        ProcessTriggers();
        ProcessTimedPresets();
        ProcessReleaseQueue();
        ProcessTimers();
        ProcessReleasedGroundDepartures();

        // Ensure ground layout is set
        if (scenario.PrimaryAirportId is not null && World.GroundLayout is null)
        {
            World.GroundLayout = _groundData.GetLayout(scenario.PrimaryAirportId);
        }

        return new TickPrePhysicsResult(spawned, generatorSpawns);
    }

    /// <summary>
    /// Physics step: runs FlightPhysics.Update and phase runner for all aircraft.
    /// Call multiple times per sim-second for sub-tick granularity.
    /// </summary>
    public void TickPhysics(double delta)
    {
        var sw = Stopwatch.StartNew();
        _occupiedHoldShortNodes = BuildOccupiedHoldShortNodes();
        AccumulateTiming("Physics.BuildHoldShort", sw);

        // Cache scenario mode flags onto the World so FlightPhysics → PilotObservationUpdater
        // can route resolved RTIS/RFIS pilot transmissions to the correct pending list.
        World.SoloTrainingMode = Scenario?.SoloTrainingMode ?? false;
        World.RpoShowPilotSpeech = Scenario?.RpoShowPilotSpeech ?? false;

        sw.Restart();
        World.Tick(delta, PreTick, RecordWorldTiming);
        AccumulateTiming("Physics.WorldTick", sw);

        _occupiedHoldShortNodes = null;

        sw.Restart();
        ProcessDeferredDispatches(delta);
        AccumulateTiming("Physics.Deferred", sw);

        sw.Restart();
        ProcessTriggeredTrackBlocks();
        AccumulateTiming("Physics.TrackBlocks", sw);
    }

    private void RecordWorldTiming(string bucket, double ms)
    {
        if (TickTimings.TryGetValue(bucket, out var entry))
        {
            TickTimings[bucket] = (entry.Count + 1, entry.Ms + ms);
        }
        else
        {
            TickTimings[bucket] = (1, ms);
        }
    }

    /// <summary>
    /// Post-physics: drains warnings, notifications, and approach scores from the world.
    /// The server reads these before calling this method to broadcast them.
    /// </summary>
    public void TickPostPhysics()
    {
        // Airborne-spawn check-ins fire here, before the notification drain, so they emit
        // the same tick they're produced. PilotProactive.TickAirborneCheckIn is idempotent —
        // it sets HasMadeInitialContact on success and no-ops on subsequent ticks.
        // TickReportTriggers (deferred REPORT n-mile-final / at-fix) runs in both solo and RPO
        // mode, so it sits outside the solo-only gate.
        if (Scenario is { } scenario)
        {
            bool solo = scenario.SoloTrainingMode;
            foreach (var ac in World.GetSnapshot())
            {
                if (solo)
                {
                    Pilot.PilotProactive.TickAirborneCheckIn(ac, scenario, LookupAirportPosition);
                    Pilot.PilotProactive.TickArrivalApproachRequest(ac, scenario, LookupAirportPosition);
                    Pilot.PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirportPosition);
                    Pilot.PilotProactive.TickPendingRequests(ac, scenario);
                }

                Pilot.PilotProactive.TickReportTriggers(ac, scenario);
            }
        }

        var warnings = World.DrainAllWarnings();
        foreach (var (callsign, warning) in warnings)
        {
            FireWarningEmitted(callsign, warning);
        }

        var notifications = World.DrainAllNotifications();
        foreach (var (callsign, notification) in notifications)
        {
            EmitTerminal("Response", callsign, notification);
        }

        var readbacks = World.DrainAllPilotReadbacks();
        foreach (var (callsign, readback) in readbacks)
        {
            EmitTerminal("SayReadback", callsign, readback);
        }

        if (Scenario is { SoloTrainingMode: true } activeScenario)
        {
            foreach (var transmission in World.DrainReadyPilotTransmissions(activeScenario.ElapsedSeconds))
            {
                EmitTerminal(ToSayKind(transmission), transmission.Callsign, transmission.Text);
            }
        }
        else
        {
            World.DiscardAllPilotTransmissions();
        }

        World.DrainAllApproachScores();
    }

    /// <summary>
    /// Removes any aircraft whose <see cref="AircraftGroundOps.PendingAutoDelete"/>
    /// flag is set. Returns the callsigns that were removed. Hosting servers (e.g.
    /// yaat-server's <c>TickProcessor.ProcessAutoDelete</c>) call this after their
    /// per-tick post-physics step so they can fan out CRC/SignalR delete broadcasts
    /// for each removed callsign. Standalone Yaat.Sim tests can call this directly
    /// to observe end-to-end <c>ONHS DEL</c> behaviour without a server wrapper.
    /// </summary>
    public IReadOnlyList<string> SweepPendingAutoDeletes()
    {
        var removed = new List<string>();
        foreach (var ac in World.GetSnapshot())
        {
            if (!ac.Ground.PendingAutoDelete)
            {
                continue;
            }

            removed.Add(ac.Callsign);
            World.RemoveAircraft(ac.Callsign);
        }
        return removed;
    }

    private static string ToSayKind(PilotTransmission transmission) =>
        transmission.Kind == PilotTransmissionKind.SayReadback ? "SayReadback" : "SayPilot";

    private static LatLon? LookupAirportPosition(string airportId)
    {
        var pos = NavigationDatabase.Instance.GetFixPosition(airportId);
        return pos.HasValue ? new LatLon(pos.Value.Lat, pos.Value.Lon) : null;
    }

    /// <summary>
    /// Convenience wrapper that runs all three phases for one sim-second.
    /// Used by the client and tests. Drains and discards terminal entries.
    /// </summary>
    public void TickOneSecond()
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        scenario.ElapsedSeconds += 1;

        TickPrePhysics();

        double subDelta = 1.0 / PhysicsSubTickRate;
        for (int sub = 0; sub < PhysicsSubTickRate; sub++)
        {
            TickPhysics(subDelta);
        }

        TickPostPhysics();

        // Discard terminal entries (client doesn't use them yet)
        _terminalEntries.Clear();

        FireTickCompleted((int)scenario.ElapsedSeconds);
    }

    /// <summary>
    /// Advance one physics sub-tick (0.25 s). Does NOT run pre/post-physics or
    /// advance <c>ElapsedSeconds</c> — call this in a manual tick loop for
    /// fine-grained inspection (e.g., tick-by-tick test traces).
    /// </summary>
    public void TickOnce()
    {
        TickPhysics(1.0 / PhysicsSubTickRate);
    }

    // --- Replay ---

    /// <summary>
    /// Replay from t=0 to <paramref name="targetSeconds"/>, applying recorded actions at the correct times.
    /// Resets engine state every call (rewinds to scratch); not a step function — looping this is O(N²)
    /// and trips assertions like the magnetic declination cache. To advance from the current state, use
    /// <see cref="FastForwardTo"/>; to step second-by-second, use <see cref="ReplayOneSecond"/>.
    /// The default action applier skips server-only commands (track, coordination); pass a custom
    /// <paramref name="actionApplier"/> to handle those (server rewind).
    /// </summary>
    public void ReplayFromStartTo(int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier = null)
    {
        ReplayRange(0, targetSeconds, actions, actionApplier);
    }

    /// <summary>
    /// Advance the engine from its current <c>ElapsedSeconds</c> to <paramref name="targetSeconds"/>,
    /// applying recorded actions at the correct times. Does not reset state — the engine must already
    /// be at the start point. Throws <see cref="ArgumentException"/> if <paramref name="targetSeconds"/>
    /// is not strictly greater than the current time (use <see cref="ReplayFromStartTo"/> or restore from
    /// a snapshot to rewind). Updates the replay cursor so subsequent <see cref="ReplayOneSecond"/> calls
    /// continue from <paramref name="targetSeconds"/>.
    /// </summary>
    public void FastForwardTo(int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier = null)
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            throw new InvalidOperationException("FastForwardTo requires a loaded scenario");
        }
        int currentSeconds = (int)scenario.ElapsedSeconds;
        if (targetSeconds <= currentSeconds)
        {
            throw new ArgumentException(
                $"FastForwardTo cannot rewind: current={currentSeconds}s target={targetSeconds}s. "
                    + "Use ReplayFromStartTo or restore from a snapshot to go backward.",
                nameof(targetSeconds)
            );
        }
        ReplayRange(currentSeconds, targetSeconds, actions, actionApplier);

        _replayActions = actions;
        _replayActionCursor = 0;
        _replayPreTickActionCursor = 0;
        _replayPreTickAppliedActionIndexes.Clear();
        _replayHasRecordedAircraftSpawns = _replayActions.Any(static a => a is RecordedAircraftSpawn);
        while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= targetSeconds)
        {
            _replayActionCursor++;
        }
        while (_replayPreTickActionCursor < _replayActions.Count && _replayActions[_replayPreTickActionCursor].ElapsedSeconds <= targetSeconds)
        {
            _replayPreTickActionCursor++;
        }
    }

    /// <summary>
    /// Replays from <paramref name="startSeconds"/> to <paramref name="targetSeconds"/>,
    /// applying actions and ticking physics for each second in the range.
    /// When startSeconds is 0, actions at t=0 are applied first.
    /// </summary>
    public void ReplayRange(int startSeconds, int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier = null)
    {
        ReplayRangeCore(startSeconds, targetSeconds, actions, actionApplier, archiveForVerification: null, drifts: null);
    }

    /// <summary>
    /// Replay variant that compares engine state against snapshots in the supplied
    /// <paramref name="archive"/> at every snapshot timestamp the range covers.
    /// Returns a <see cref="ReplayResult"/> listing the per-snapshot drifts. Empty
    /// drifts list ⇒ every checked snapshot matched within tolerance. Useful for
    /// pinpointing the first tick where replay diverges from a recorded session.
    /// </summary>
    public ReplayResult ReplayRangeWithVerification(
        int startSeconds,
        int targetSeconds,
        List<RecordedAction> actions,
        RecordingArchive archive,
        Action<RecordedAction>? actionApplier = null
    )
    {
        var drifts = new List<SnapshotDriftReport>();
        ReplayRangeCore(startSeconds, targetSeconds, actions, actionApplier, archive, drifts);
        return new ReplayResult(drifts);
    }

    private void ReplayRangeCore(
        int startSeconds,
        int targetSeconds,
        List<RecordedAction> actions,
        Action<RecordedAction>? actionApplier,
        RecordingArchive? archiveForVerification,
        List<SnapshotDriftReport>? drifts
    )
    {
        if (startSeconds == 0)
        {
            _replayTrackApplier.Reset();
        }
        actionApplier ??= ApplyRecordedAction;
        bool previousReplayState = _isReplayingRecordedActions;
        bool previousReplaySpawnState = _replayHasRecordedAircraftSpawns;
        _isReplayingRecordedActions = true;
        _replayHasRecordedAircraftSpawns = actions.Any(static a => a is RecordedAircraftSpawn);

        try
        {
            var verifyByTimestamp = new Dictionary<int, int>();
            if (archiveForVerification is not null && drifts is not null)
            {
                for (int i = 0; i < archiveForVerification.SnapshotTimestamps.Count; i++)
                {
                    int ts = (int)archiveForVerification.SnapshotTimestamps[i].ElapsedSeconds;
                    if (ts > startSeconds && ts <= targetSeconds && !verifyByTimestamp.ContainsKey(ts))
                    {
                        verifyByTimestamp[ts] = i;
                    }
                }
            }

            int actionCursor = 0;
            int preTickActionCursor = 0;
            var preTickAppliedActionIndexes = new HashSet<int>();

            if (startSeconds == 0)
            {
                ApplyRecordedAircraftSpawnsBeforeTick(actions, ref preTickActionCursor, 0, actionApplier, preTickAppliedActionIndexes);

                // Apply actions at t=0 first (settings, immediate commands)
                while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= 0)
                {
                    if (!preTickAppliedActionIndexes.Contains(actionCursor))
                    {
                        actionApplier(actions[actionCursor]);
                    }

                    actionCursor++;
                }
            }
            else
            {
                // Skip actions before the start time
                while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= startSeconds)
                {
                    actionCursor++;
                }

                while (preTickActionCursor < actions.Count && actions[preTickActionCursor].ElapsedSeconds <= startSeconds)
                {
                    preTickActionCursor++;
                }
            }

            double subDelta = 1.0 / PhysicsSubTickRate;
            var sw = new Stopwatch();
            for (int t = startSeconds + 1; t <= targetSeconds; t++)
            {
                Scenario!.ElapsedSeconds = t;

                sw.Restart();
                ApplyRecordedAircraftSpawnsBeforeTick(actions, ref preTickActionCursor, t, actionApplier, preTickAppliedActionIndexes);
                TickPrePhysics();
                AccumulateTiming("PrePhysics", sw);

                for (int sub = 0; sub < PhysicsSubTickRate; sub++)
                {
                    sw.Restart();
                    TickPhysics(subDelta);
                    AccumulateTiming("Physics", sw);
                }

                sw.Restart();
                TickPostPhysics();
                AccumulateTiming("PostPhysics", sw);
                _terminalEntries.Clear();

                // Advance weather timeline if active
                if (Scenario!.WeatherTimeline is { } timeline)
                {
                    World.Weather = timeline.GetWeatherAt(t);
                }

                // Apply actions at this time
                while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= t)
                {
                    if (!preTickAppliedActionIndexes.Contains(actionCursor))
                    {
                        actionApplier(actions[actionCursor]);
                    }

                    actionCursor++;
                }

                if (archiveForVerification is not null && drifts is not null && verifyByTimestamp.TryGetValue(t, out var snapIdx))
                {
                    var snap = archiveForVerification.ReadSnapshot(snapIdx);
                    var report = SnapshotDiff.Compare(t, snap, World.GetSnapshot());
                    if (report.AircraftDrifts.Count > 0)
                    {
                        drifts.Add(report);
                    }
                }

                FireTickCompleted(t);
            }
        }
        finally
        {
            _isReplayingRecordedActions = previousReplayState;
            _replayHasRecordedAircraftSpawns = previousReplaySpawnState;
        }
    }

    private void AccumulateTiming(string bucket, Stopwatch sw)
    {
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds;
        if (TickTimings.TryGetValue(bucket, out var entry))
        {
            TickTimings[bucket] = (entry.Count + 1, entry.Ms + ms);
        }
        else
        {
            TickTimings[bucket] = (1, ms);
        }
    }

    /// <summary>
    /// Formats <see cref="TickTimings"/> for diagnostic output. Sorted by total time desc.
    /// </summary>
    public string DumpTickTimings()
    {
        if (TickTimings.Count == 0)
        {
            return "(no tick timings recorded)";
        }
        var sb = new StringBuilder();
        sb.AppendLine("Tick timings (bucket: count, totalMs, avgMs):");
        foreach (var kvp in TickTimings.OrderByDescending(k => k.Value.Ms))
        {
            double avg = kvp.Value.Ms / Math.Max(1, kvp.Value.Count);
            sb.AppendLine($"  {kvp.Key}: n={kvp.Value.Count}, total={kvp.Value.Ms:F1}ms, avg={avg:F3}ms");
        }
        return sb.ToString();
    }

    public const int SnapshotIntervalSeconds = 5;

    public void Replay(SessionRecording recording, double targetSeconds)
    {
        ReplayWithScenarioOverride(recording, targetSeconds, configureAfterLoad: static _ => { });
    }

    /// <summary>
    /// Replay variant that runs <paramref name="configureAfterLoad"/> on the freshly loaded
    /// scenario before any actions or weather are applied. Useful for tests that need to
    /// override scenario state (e.g. <c>ValidateDctFixes</c>) when replaying older recordings
    /// that predate a setting being persisted in the action log.
    /// </summary>
    public void ReplayWithScenarioOverride(SessionRecording recording, double targetSeconds, Action<SimScenarioState> configureAfterLoad)
    {
        TickTimings.Clear();
        LoadScenario(recording.ScenarioJson, recording.RngSeed);

        // The scenario JSON does not carry the resolved runtime student position (the server sets it
        // at load via InitializeTrackPositions). Restore it from the recording so CanInitiateWithStudent,
        // proactive check-ins, and Class B/C boundary holds replay as they did live.
        if (Scenario is not null && recording.StudentPositionState is { } studentPosition)
        {
            Scenario.StudentPosition = studentPosition.Position;
            Scenario.StudentTcp = studentPosition.Tcp;
            World.StudentTcp = studentPosition.Tcp;
            Scenario.StudentPositionType = studentPosition.PositionType;
            Scenario.IsStudentTowerPosition = studentPosition.IsTowerPosition;
        }

        if (Scenario is not null)
        {
            configureAfterLoad(Scenario);
        }

        // Apply weather if present
        if (recording.WeatherJson is not null)
        {
            ApplyWeatherJson(recording.WeatherJson);
            if (Scenario is not null)
            {
                Scenario.MetarReissuanceEnabled = recording.MetarReissuanceEnabled;
            }
        }

        // Deserialize the bundled ARTCC config so TrackResolver's TCP/ERAM fallback works
        // for AS commands targeting positions outside the scenario's StudentTcp/AtcPositions.
        // Older recordings without the bundle leave this as null; callers can set it manually.
        if (Scenario is not null && recording.ArtccConfigJson is { } artccJson)
        {
            try
            {
                Scenario.ArtccConfig = JsonSerializer.Deserialize<Yaat.Sim.Data.Vnas.ArtccConfigRoot>(artccJson, RecordingJsonOptions.Default);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize bundled ArtccConfig; replay will fall back to scenario-only resolution");
            }
        }

        ReplayFromStartTo((int)targetSeconds, recording.Actions);

        // Store replay cursor so ReplayOneSecond() can continue from here
        _replayActions = recording.Actions;
        _replayActionCursor = 0;
        _replayPreTickActionCursor = 0;
        _replayPreTickAppliedActionIndexes.Clear();
        _replayHasRecordedAircraftSpawns = _replayActions.Any(static a => a is RecordedAircraftSpawn);
        int target = (int)targetSeconds;
        while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= target)
        {
            _replayActionCursor++;
        }
        while (_replayPreTickActionCursor < _replayActions.Count && _replayActions[_replayPreTickActionCursor].ElapsedSeconds <= target)
        {
            _replayPreTickActionCursor++;
        }
    }

    /// <summary>
    /// Advances the replay by one second: ticks physics, applies any recorded
    /// actions at the new time, and advances weather. Call after <see cref="Replay"/>
    /// to continue the recording second-by-second while inspecting state between ticks.
    /// </summary>
    public void ReplayOneSecond()
    {
        var scenario = Scenario;
        if (scenario is null || _replayActions is null)
        {
            return;
        }

        scenario.ElapsedSeconds += 1;
        int t = (int)scenario.ElapsedSeconds;

        var sw = new Stopwatch();
        bool previousReplayState = _isReplayingRecordedActions;
        _isReplayingRecordedActions = true;

        try
        {
            sw.Restart();
            ApplyRecordedAircraftSpawnsBeforeTick(
                _replayActions,
                ref _replayPreTickActionCursor,
                t,
                ApplyRecordedAction,
                _replayPreTickAppliedActionIndexes
            );
            TickPrePhysics();
            AccumulateTiming("PrePhysics", sw);

            double subDelta = 1.0 / PhysicsSubTickRate;
            for (int sub = 0; sub < PhysicsSubTickRate; sub++)
            {
                sw.Restart();
                TickPhysics(subDelta);
                AccumulateTiming("Physics", sw);
            }

            sw.Restart();
            TickPostPhysics();
            AccumulateTiming("PostPhysics", sw);
            _terminalEntries.Clear();

            if (scenario.WeatherTimeline is { } timeline)
            {
                World.Weather = timeline.GetWeatherAt(t);
            }

            while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= t)
            {
                if (!_replayPreTickAppliedActionIndexes.Contains(_replayActionCursor))
                {
                    ApplyRecordedAction(_replayActions[_replayActionCursor]);
                }

                _replayActionCursor++;
            }

            FireTickCompleted(t);
        }
        finally
        {
            _isReplayingRecordedActions = previousReplayState;
        }
    }

    /// <summary>
    /// Advances the replay by one physics sub-tick (0.25 s). This is the
    /// fine-grained version of <see cref="ReplayOneSecond"/> for tests that
    /// need to observe simulation state at sub-second granularity (e.g.
    /// capture the exact tick a phase transitions). Pre- and post-physics
    /// run only at integer-second boundaries, and recorded actions are
    /// applied once per crossed second (never mid-second), matching
    /// <see cref="ReplayOneSecond"/>'s semantics exactly when called four
    /// times in succession starting from an integer second.
    /// </summary>
    public void ReplayOneSubTick()
    {
        var scenario = Scenario;
        if (scenario is null || _replayActions is null)
        {
            return;
        }

        const double eps = 1e-9;
        double prev = scenario.ElapsedSeconds;
        double subDelta = 1.0 / PhysicsSubTickRate;
        scenario.ElapsedSeconds = prev + subDelta;

        // "We just started a new integer second" — the previous ElapsedSeconds
        // sat exactly on an integer, so this sub-tick is the first of four.
        bool atSecondStart = Math.Abs(prev - Math.Round(prev)) < eps;

        // "We just finished an integer second" — the new ElapsedSeconds lands
        // exactly on an integer, so this sub-tick is the last of four.
        bool atSecondEnd = Math.Abs(scenario.ElapsedSeconds - Math.Round(scenario.ElapsedSeconds)) < eps;
        bool previousReplayState = _isReplayingRecordedActions;
        _isReplayingRecordedActions = true;

        try
        {
            if (atSecondStart)
            {
                int t = (int)Math.Ceiling(scenario.ElapsedSeconds);
                ApplyRecordedAircraftSpawnsBeforeTick(
                    _replayActions,
                    ref _replayPreTickActionCursor,
                    t,
                    ApplyRecordedAction,
                    _replayPreTickAppliedActionIndexes
                );
                TickPrePhysics();
            }

            TickPhysics(subDelta);

            if (atSecondEnd)
            {
                // Snap away any floating-point drift accumulated across sub-ticks.
                scenario.ElapsedSeconds = Math.Round(scenario.ElapsedSeconds);
                int t = (int)scenario.ElapsedSeconds;

                TickPostPhysics();
                _terminalEntries.Clear();

                if (scenario.WeatherTimeline is { } timeline)
                {
                    World.Weather = timeline.GetWeatherAt(t);
                }

                while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= t)
                {
                    if (!_replayPreTickAppliedActionIndexes.Contains(_replayActionCursor))
                    {
                        ApplyRecordedAction(_replayActions[_replayActionCursor]);
                    }

                    _replayActionCursor++;
                }

                FireTickCompleted(t);
            }
        }
        finally
        {
            _isReplayingRecordedActions = previousReplayState;
        }
    }

    // --- Commands ---

    public CommandResult SendCommand(string callsign, string command)
    {
        var aircraft = FindAircraft(callsign);
        if (aircraft is null)
        {
            return new CommandResult(false, $"Aircraft '{callsign}' not found");
        }

        var parseResult = CommandParser.ParseCompound(command, aircraft.FlightPlan.Route);
        if (!parseResult.IsSuccess)
        {
            return new CommandResult(false, $"Failed to parse command: {command} — {parseResult.Reason}");
        }

        bool soloTrainingMode = Scenario?.SoloTrainingMode ?? false;

        // Pilot-reaction delay (command-run delay): when active, defer the whole dispatch by a sampled
        // number of seconds and acknowledge immediately so the controller knows the command landed and
        // the sim isn't frozen. The aircraft begins complying when the deferral fires.
        CommandResult result;
        var reactionDelay = TryDeferCommandForReaction(aircraft, parseResult.Value!);
        if (reactionDelay is double reactionSeconds)
        {
            // In solo training mode the student is the pilot's only audience: showing the exact sampled
            // delay would reveal precisely how long the aircraft will take to comply. Suppress the
            // acknowledgement entirely — the pilot's read-back (queued below) is the acknowledgement.
            result = soloTrainingMode
                ? new CommandResult(true, null)
                : new CommandResult(true, $"Pilot complying in {(int)Math.Round(reactionSeconds)}s");
        }
        else
        {
            var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
            var dispatchCtx = new DispatchContext(
                groundLayout,
                World.Rng,
                World.Weather,
                FindAircraft,
                () => World.GetSnapshot(),
                Scenario?.ValidateDctFixes ?? true,
                Scenario?.AutoCrossRunway ?? false,
                Scenario?.SoloTrainingMode ?? false,
                Scenario?.RpoShowPilotSpeech ?? false,
                _terminalEntries.Add,
                Scenario?.ArtccConfig,
                Scenario?.ElapsedSeconds ?? 0,
                PreserveConditionals: false,
                IsScenarioScripted: false
            );
            result = CommandDispatcher.DispatchCompound(parseResult.Value!, aircraft, dispatchCtx);
        }

        if (result.Success)
        {
            Pilot.PilotInitialContactEligibility.RegisterControllerContact(aircraft, Scenario, parseResult.Value!);
            if (soloTrainingMode)
            {
                SoloTrainingEvaluator.RecordControllerCommand(aircraft, parseResult.Value!, Scenario?.ElapsedSeconds ?? 0, World.GetSnapshot());
                PilotRequestTracker.ApplyControllerResponse(aircraft, parseResult.Value!, Scenario?.ElapsedSeconds ?? 0);
                // The controller has just spoken to this aircraft, so the
                // awaiting-controller-response gate (if it was set after this pilot's
                // last proactive call) clears. Commands that produce a readback also
                // arm the readback gate just below; both gates are independent.
                World.AcknowledgeControllerResponse(aircraft.Callsign);
            }
        }
        else if (soloTrainingMode)
        {
            QueueSoloUnableIfNeeded(aircraft, result);
        }

        // Emit pilot readback in solo-training mode. Single hook here in SendCommand (the
        // user-issued live path) means deferred / preset / replay dispatches don't re-fire
        // readbacks. Transparent (squawk/ident/say) and phase-handled paths all funnel
        // through DispatchCompound, so this catches everything successful from the student's
        // perspective.
        if (result.Success && soloTrainingMode)
        {
            var activityLevel = World.ActiveFrequency.GetActivityLevel(Scenario?.ElapsedSeconds ?? 0);
            var readback = Yaat.Sim.Pilot.PilotResponder.BuildReadback(parseResult.Value!, aircraft, PilotPersonality.Varied, activityLevel);
            if (readback is not null)
            {
                World.ExpectPilotReadback(aircraft.Callsign, Scenario?.ElapsedSeconds ?? 0);
                Yaat.Sim.Pilot.PilotResponder.QueueSoloPilotTransmission(
                    aircraft,
                    readback,
                    Yaat.Sim.Pilot.PilotTransmissionKind.Readback,
                    Yaat.Sim.Pilot.PilotResponder.SourceResponse
                );
            }
        }

        return result;
    }

    /// <summary>
    /// If a command-run delay is active, enqueue <paramref name="compound"/> as a pilot-reaction
    /// deferred dispatch and return the delay in seconds; otherwise return null and the caller
    /// dispatches immediately. The delay simulates the time a pilot needs to set up the FMC / autopilot
    /// after the controller issues an instruction.
    ///
    /// Commands carrying explicit leading timing — a WAIT/WAITD or a BEHIND give-way condition — are NOT
    /// reaction-delayed: the controller's explicit timing already models the wait, and those produce
    /// their own deferred dispatch inside <see cref="CommandDispatcher.DispatchCompound"/>.
    ///
    /// Sampling draws from <see cref="SimulationWorld.ReactionDelayRng"/> (never the shared RNG) so it
    /// can't perturb replay-critical emergent events. The returned value is the actual delay applied
    /// (after the order-preserving clamp); the server bakes it into the recorded command so replays
    /// reproduce it exactly rather than re-sampling.
    /// </summary>
    public double? TryDeferCommandForReaction(AircraftState aircraft, CompoundCommand compound)
    {
        var scenario = Scenario;
        if (scenario is null || scenario.CommandRunDelayMaxSeconds <= 0)
        {
            return null;
        }

        if (HasExplicitLeadingTiming(compound))
        {
            return null;
        }

        // Pure frequency-change / radio-contact commands are not reaction-delayed: the AIM (4-2-3)
        // expects a pilot to switch frequency "as soon as possible", and holding the aircraft on the
        // current frequency for several seconds would teach a backwards habit. A mixed compound
        // (e.g. "FH 270; CON TWR") is still delayed as a whole — only a purely-comm compound is exempt.
        if (IsPureCommCompound(compound))
        {
            return null;
        }

        int max = scenario.CommandRunDelayMaxSeconds;
        int min = Math.Clamp(scenario.CommandRunDelayMinSeconds, 0, max);
        int sampled = min >= max ? max : World.ReactionDelayRng.Next(min, max + 1);

        // Preserve issue order: a command issued later must never start complying before one issued
        // earlier. Clamp this command's delay so it fires no sooner than any already-pending reaction
        // deferral on the same aircraft (ProcessDeferredDispatches applies same-tick expiries FIFO).
        double clampFloor = 0;
        foreach (var pending in aircraft.DeferredDispatches)
        {
            if (pending.IsReactionDelay && pending.RemainingSeconds > clampFloor)
            {
                clampFloor = pending.RemainingSeconds;
            }
        }

        double seconds = Math.Max(sampled, clampFloor);
        aircraft.DeferredDispatches.Add(new DeferredDispatch(seconds, compound) { SourceText = compound.SourceText, IsReactionDelay = true });
        return seconds;
    }

    private static bool HasExplicitLeadingTiming(CompoundCommand compound)
    {
        if (compound.Blocks.Count == 0)
        {
            return false;
        }

        var first = compound.Blocks[0];
        if (first.Condition is GiveWayCondition)
        {
            return true;
        }

        foreach (var cmd in first.Commands)
        {
            if (cmd is WaitCommand or WaitDistanceCommand)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPureCommCompound(CompoundCommand compound)
    {
        bool hasAny = false;
        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                hasAny = true;
                if (cmd is not (ContactCommand or FrequencyChangeApprovedCommand or AcknowledgePilotContactCommand))
                {
                    return false;
                }
            }
        }

        return hasAny;
    }

    private static void QueueSoloUnableIfNeeded(AircraftState aircraft, CommandResult result)
    {
        if (result.RejectedCommandType is not { } rejectedType)
        {
            return;
        }

        var definition = CommandRegistry.Get(rejectedType);
        if (definition?.ProducesPilotUnable != true)
        {
            return;
        }

        var transmission = PilotResponder.BuildUnable(aircraft, result.Message);
        PilotResponder.QueueSoloPilotTransmission(aircraft, transmission, PilotTransmissionKind.Readback, PilotResponder.SourceResponse);
    }

    public AircraftState? FindAircraft(string callsign)
    {
        return World.GetSnapshot().FirstOrDefault(a => a.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
    }

    // --- Public mutations ---

    public void WarpAircraft(string callsign, double latitude, double longitude, TrueHeading trueHeading)
    {
        var aircraft = FindAircraft(callsign);
        if (aircraft is null)
        {
            return;
        }

        // Clear stale state
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }
        aircraft.Ground.AssignedTaxiRoute = null;
        aircraft.Ground.Hold = null;
        aircraft.Queue.Blocks.Clear();

        // Place on ground
        aircraft.Position = new LatLon(latitude, longitude);
        aircraft.TrueHeading = trueHeading;
        aircraft.TrueTrack = trueHeading;
        aircraft.IndicatedAirspeed = 0;
        aircraft.IsOnGround = true;
        aircraft.Targets.TargetSpeed = 0;

        // Install ground-idle phase so subsequent commands have phase context
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft));

        aircraft.Ground.Layout = ResolveGroundLayout(aircraft);
    }

    public void AmendFlightPlan(string callsign, FlightPlanAmendment amendment)
    {
        if (!Callsign.IsValid(callsign))
        {
            _logger.LogWarning("AmendFlightPlan rejected invalid callsign '{Callsign}'", callsign);
            return;
        }

        var ac = FindAircraft(callsign);
        if (ac is null)
        {
            return;
        }

        bool wasFiled = ac.FlightPlan.HasFlightPlan;

        if (amendment.AircraftType is not null)
        {
            // Filed FP type only — never the actual physical type. Tower Cab (out-the-window)
            // keeps reading AircraftState.AircraftType, which is fixed at spawn.
            ac.FlightPlan.AircraftType = amendment.AircraftType;
        }
        if (amendment.EquipmentSuffix is not null)
        {
            ac.FlightPlan.EquipmentSuffix = amendment.EquipmentSuffix;
        }
        if (amendment.Departure is not null)
        {
            ac.FlightPlan.Departure = amendment.Departure;
        }
        if (amendment.Destination is not null)
        {
            ac.FlightPlan.Destination = amendment.Destination;
        }
        if (amendment.CruiseSpeed is not null)
        {
            ac.FlightPlan.CruiseSpeed = amendment.CruiseSpeed.Value;
        }
        if (amendment.CruiseAltitude is not null)
        {
            ac.FlightPlan.CruiseAltitude = amendment.CruiseAltitude.Value;
        }
        if (amendment.FlightRules is not null)
        {
            ac.FlightPlan.FlightRules = amendment.FlightRules;
        }
        if (amendment.Route is not null)
        {
            ac.FlightPlan.Route = amendment.Route;
            DepartureClearanceHandler.RefreshStoredDepartureClearance(ac);
            DepartureClearanceHandler.RefreshPendingInitialClimbPhases(ac);
        }
        if (amendment.Remarks is not null)
        {
            ac.FlightPlan.Remarks = amendment.Remarks;
        }
        if (amendment.Scratchpad1 is not null)
        {
            ac.Stars.Scratchpad1 = amendment.Scratchpad1;
            ac.Stars.WasScratchpad1Cleared = string.IsNullOrEmpty(amendment.Scratchpad1);
        }
        if (amendment.Scratchpad2 is not null)
        {
            ac.Stars.Scratchpad2 = amendment.Scratchpad2;
        }
        if (amendment.BeaconCode is not null)
        {
            // Amend only the *assigned* beacon code, never the code the transponder transmits — a
            // controller assigns a beacon; the pilot keeps squawking the current code until told to
            // squawk the new one (matching the auto-assign-on-filing branch below). The resulting
            // beacon mismatch is shown on the data block until the pilot complies.
            ac.Transponder.AssignedCode = amendment.BeaconCode.Value;
        }

        // Resolve ground layout if departure/destination changed
        if (amendment.Departure is not null || amendment.Destination is not null)
        {
            ac.Ground.Layout = ResolveGroundLayout(ac);
        }

        // Editing a flight plan via the Flight Plan Editor on a radar-only (no-plan) target
        // files the plan: establish it and issue a discrete beacon code (VFR draws from the
        // VFR bank, IFR from the IFR bank). Don't flip Transponder.Code — the pilot keeps
        // squawking their current code until the controller issues SQ. This is the single
        // owner of "filing establishes the plan + assigns a beacon"; the typed DA/VP/NEW
        // create path reaches it through its own AmendFlightPlan call.
        if (!wasFiled)
        {
            ac.FlightPlan.HasFlightPlan = true;
            if (ac.Transponder.AssignedCode == 0)
            {
                ac.Transponder.AssignedCode = BeaconCodePool.AssignNextCode(ac.FlightPlan.IsVfr);
            }
        }

        // Bump the revision counter so the strip can render the new value.
        // CRC displays revision regardless of which fields changed — the counter
        // is a "has been edited" signal, not a per-field diff.
        ac.FlightPlan.RevisionNumber++;
    }

    /// <summary>
    /// Releases the aircraft's current assigned beacon code back to the pool and draws a fresh
    /// discrete code (VFR bank for VFR, IFR bank for IFR). Does not flip <c>Transponder.Code</c> —
    /// the pilot keeps squawking their current code until the controller issues <c>SQ</c>. Returns
    /// the new assigned code, or 0 if the aircraft is unknown.
    /// </summary>
    public uint RequestNewBeaconCode(string callsign)
    {
        var ac = FindAircraft(callsign);
        if (ac is null)
        {
            return 0;
        }

        BeaconCodePool.Release(ac.Transponder.AssignedCode);
        var newCode = BeaconCodePool.AssignNextCode(ac.FlightPlan.IsVfr);
        ac.Transponder.AssignedCode = newCode;
        return newCode;
    }

    public AirportGroundLayout? ResolveGroundLayout(AircraftState aircraft)
    {
        // An aircraft physically on the ground taxis on the airport its wheels are on —
        // never on a filed destination. A departure that files a destination but no
        // departure (e.g. a VFR plan created via CRC to KSMF while parked at OAK) would
        // otherwise load the destination's layout and reject every taxiway/parking lookup.
        if (aircraft.IsOnGround)
        {
            var physicalAirport = aircraft.Phases?.AssignedRunway?.AirportId;
            if (string.IsNullOrEmpty(physicalAirport))
            {
                physicalAirport = aircraft.AirportId;
            }

            var physicalLayout = string.IsNullOrEmpty(physicalAirport) ? null : _groundData.GetLayout(physicalAirport);
            if (physicalLayout is not null)
            {
                return physicalLayout;
            }
        }

        var depLayout = string.IsNullOrEmpty(aircraft.FlightPlan.Departure) ? null : _groundData.GetLayout(aircraft.FlightPlan.Departure);
        var destLayout = string.IsNullOrEmpty(aircraft.FlightPlan.Destination) ? null : _groundData.GetLayout(aircraft.FlightPlan.Destination);

        // Cold-call VFR aircraft (pattern work, full-stop requests) frequently file
        // neither departure nor destination. Treat the assigned arrival runway's
        // airport — or the spawn-time operational airport context — as the implicit
        // destination so the ground layout is available for runway exit and taxi
        // after landing, without writing into the flight plan.
        if (depLayout is null && destLayout is null)
        {
            var implicitAirport = aircraft.Phases?.AssignedRunway?.AirportId;
            if (string.IsNullOrEmpty(implicitAirport))
            {
                implicitAirport = aircraft.AirportId;
            }

            return string.IsNullOrEmpty(implicitAirport) ? null : _groundData.GetLayout(implicitAirport);
        }

        if (depLayout is null)
        {
            return destLayout;
        }

        if (destLayout is null || destLayout == depLayout)
        {
            return depLayout;
        }

        var depNode = depLayout.FindNearestNode(aircraft.Position);
        var destNode = destLayout.FindNearestNode(aircraft.Position);

        double depDist = depNode is not null ? GeoMath.DistanceNm(aircraft.Position, depNode.Position) : double.MaxValue;
        double destDist = destNode is not null ? GeoMath.DistanceNm(aircraft.Position, destNode.Position) : double.MaxValue;

        return destDist < depDist ? destLayout : depLayout;
    }

    // --- Private tick methods ---

    private void ProcessDeferredDispatches(double deltaSeconds)
    {
        foreach (var aircraft in World.GetSnapshot())
        {
            if (aircraft.DeferredDispatches.Count == 0)
            {
                continue;
            }

            // Tick timers / evaluate conditions in insertion order and collect the deferrals that are
            // ready this sub-tick. Dispatching FIFO (rather than the old reverse walk) guarantees that
            // several commands expiring on the same sub-tick — e.g. two reaction-delayed commands the
            // order-preserving clamp parked on the same fire time — apply in the order they were issued.
            List<DeferredDispatch>? ready = null;
            foreach (var d in aircraft.DeferredDispatches)
            {
                bool isReady;
                if (d.GiveWayTarget is not null)
                {
                    isReady = IsGiveWayDeferredMet(aircraft, d.GiveWayTarget);
                    if (isReady && aircraft.Ground.Hold is { Kind: HoldKind.GiveWay })
                    {
                        // Condition met — clear any active GIVEWAY hold so the payload can dispatch
                        // cleanly. HoldPosition holds are NOT cleared (a controller's explicit HOLD
                        // should not be overridden by a deferred BEHIND condition firing).
                        aircraft.Ground.Hold = null;
                    }
                }
                else if (d.IsDistanceBased)
                {
                    d.RemainingDistanceNm -= aircraft.GroundSpeed * deltaSeconds / 3600.0;
                    isReady = d.RemainingDistanceNm <= 0;
                }
                else
                {
                    d.RemainingSeconds -= deltaSeconds;
                    isReady = d.RemainingSeconds <= 0;
                }

                if (isReady)
                {
                    (ready ??= []).Add(d);
                }
            }

            if (ready is null)
            {
                continue;
            }

            foreach (var d in ready)
            {
                aircraft.DeferredDispatches.Remove(d);
            }

            // DispatchCompound clears DeferredDispatches to supersede pending waits when a NEW command
            // is issued; a deferred RE-dispatch must not cancel its still-pending siblings (e.g. a second
            // reaction-delayed command waiting its turn). Detach the survivors across the dispatch and
            // restore them ahead of any deferral a payload itself adds, preserving issue order.
            var survivingDeferrals = new List<DeferredDispatch>(aircraft.DeferredDispatches);
            aircraft.DeferredDispatches.Clear();

            foreach (var d in ready)
            {
                // Reaction delays (the command-run delay) fire silently — the controller already saw
                // the "complying in Ns" acknowledgement when the command was issued. WAIT/BEHIND/distance
                // deferrals were explicitly requested, so they still announce themselves.
                if (!d.IsReactionDelay)
                {
                    var payloadDesc = DescribeDeferredPayload(d);
                    string conditionDesc;
                    if (d.GiveWayTarget is not null)
                    {
                        conditionDesc = $"Give-way cleared ({d.GiveWayTarget})";
                    }
                    else if (d.IsDistanceBased)
                    {
                        conditionDesc = "Distance reached";
                    }
                    else
                    {
                        conditionDesc = "WAIT expired";
                    }

                    _logger.LogInformation("[Deferred] {Callsign}: {Condition} → {Payload}", aircraft.Callsign, conditionDesc, payloadDesc);
                    EmitTerminal("System", aircraft.Callsign, $"[Deferred] {conditionDesc} → {payloadDesc}");
                }

                var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
                var deferredCtx = new DispatchContext(
                    groundLayout,
                    World.Rng,
                    World.Weather,
                    FindAircraft,
                    () => World.GetSnapshot(),
                    Scenario?.ValidateDctFixes ?? true,
                    Scenario?.AutoCrossRunway ?? false,
                    Scenario?.SoloTrainingMode ?? false,
                    Scenario?.RpoShowPilotSpeech ?? false,
                    _terminalEntries.Add,
                    Scenario?.ArtccConfig,
                    Scenario?.ElapsedSeconds ?? 0,
                    PreserveConditionals: true,
                    IsScenarioScripted: d.IsScenarioScripted
                );
                var deferredResult = CommandDispatcher.DispatchCompound(d.Payload, aircraft, deferredCtx);
                if (!deferredResult.Success)
                {
                    // A deferred/preset command that fails when it finally fires (e.g. a DVIA whose STAR
                    // never activated) used to vanish silently after the optimistic line above — surface it.
                    _logger.LogWarning("[Deferred] {Callsign}: dispatch failed — {Message}", aircraft.Callsign, deferredResult.Message);
                    EmitTerminal("Warning", aircraft.Callsign, $"[Deferred] could not apply: {deferredResult.Message}");
                }
            }

            if (survivingDeferrals.Count > 0)
            {
                aircraft.DeferredDispatches.InsertRange(0, survivingDeferrals);
            }
        }
    }

    /// <summary>
    /// Routes an immediate (unconditional) preset that is purely track commands straight to the track
    /// engine. Such presets never reach <see cref="CommandDispatcher.EnqueueBlocks"/> — the leading block
    /// applies inline through <see cref="CommandDispatcher.ApplyCommand"/>, which has no track-command arm
    /// (the no-dispatcher-arm default). Conditional or mixed compounds return false and fall
    /// through to the normal dispatcher, where <see cref="ProcessTriggeredTrackBlocks"/> handles any
    /// triggered track commands.
    /// </summary>
    private bool TryDispatchImmediateTrackPreset(CompoundCommand compound, AircraftState aircraft)
    {
        if (compound.Blocks.Count != 1 || compound.Blocks[0].Condition is not null)
        {
            return false;
        }

        var commands = compound.Blocks[0].Commands;
        if (commands.Count == 0 || !commands.TrueForAll(TrackEngine.IsTrackCommand))
        {
            return false;
        }

        var scenario = Scenario!;
        foreach (var command in commands)
        {
            var result = TrackEngine.Dispatch(command, aircraft, identity: null, scenario, scenario.ArtccConfig);
            if (result is { Success: false })
            {
                aircraft.PendingWarnings.Add($"{aircraft.Callsign}: {result.Message}");
            }
        }

        return true;
    }

    /// <summary>
    /// Dispatches track commands (HO/TRACK/DROP/…) carried by triggered command-queue blocks. Track
    /// commands have no arm in <see cref="CommandDispatcher.ApplyCommand"/>; they must reach
    /// <see cref="TrackEngine.Dispatch"/>, which needs the live <see cref="Scenario"/> and ARTCC config.
    /// Runs inside <see cref="TickPhysics"/> (shared by the standalone sim/replay and the server tick) so
    /// the routing fires regardless of host. The block's own <c>ApplyAction</c> deliberately omits track
    /// commands (see <see cref="CommandDispatcher.EnqueueBlocks"/>), so this is the single place they
    /// execute. <see cref="CommandBlock.TrackApplied"/> guards against the per-sub-tick scan re-firing,
    /// and survives snapshot restore.
    /// </summary>
    public void ProcessTriggeredTrackBlocks()
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        foreach (var aircraft in World.GetSnapshot())
        {
            var blocks = aircraft.Queue.Blocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (!block.IsApplied || !block.HasTrackCommand || block.TrackApplied)
                {
                    continue;
                }

                // Mark before dispatching so the scan never re-fires this block, even if dispatch throws.
                block.TrackApplied = true;

                foreach (var trackCommand in ResolveTrackCommandsForBlock(block, aircraft))
                {
                    var result = TrackEngine.Dispatch(trackCommand, aircraft, identity: null, scenario, scenario.ArtccConfig);
                    if (result is { Success: false })
                    {
                        var label = !string.IsNullOrEmpty(block.SourceCommandText) ? block.SourceCommandText : block.NaturalDescription;
                        aircraft.PendingWarnings.Add($"{aircraft.Callsign} {label}: {result.Message}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolves the parsed track commands for a triggered block. Prefers the live
    /// <see cref="CommandBlock.ParsedCommands"/>; when those are absent (the block was restored from a
    /// snapshot, which does not serialize parsed commands) it re-parses
    /// <see cref="CommandBlock.SourceCommandText"/> and recovers the track commands from the matching
    /// sub-block.
    /// </summary>
    private List<ParsedCommand> ResolveTrackCommandsForBlock(CommandBlock block, AircraftState aircraft)
    {
        if (block.ParsedCommands is { } live)
        {
            return live.Where(TrackEngine.IsTrackCommand).ToList();
        }

        if (string.IsNullOrEmpty(block.SourceCommandText))
        {
            return [];
        }

        var reparsed = CommandParser.ParseCompound(block.SourceCommandText, aircraft.FlightPlan.Route);
        if (!reparsed.IsSuccess || reparsed.Value is not { } compound)
        {
            return [];
        }

        var trackBlocks = compound.Blocks.Where(b => b.Commands.Exists(TrackEngine.IsTrackCommand)).ToList();
        if (trackBlocks.Count == 1)
        {
            return trackBlocks[0].Commands.Where(TrackEngine.IsTrackCommand).ToList();
        }

        // Multiple sub-blocks share this source text — disambiguate by the block's at-fix trigger.
        if (block.Trigger is { Type: BlockTriggerType.ReachFix, FixName: { } fixName })
        {
            var match = trackBlocks.Find(b =>
                b.Condition is AtFixCondition at && string.Equals(at.FixName, fixName, StringComparison.OrdinalIgnoreCase)
            );
            if (match is not null)
            {
                return match.Commands.Where(TrackEngine.IsTrackCommand).ToList();
            }
        }

        _logger.LogDebug(
            "[TrackBlock] {Callsign}: could not disambiguate restored track block from source '{Source}'",
            aircraft.Callsign,
            block.SourceCommandText
        );
        return [];
    }

    private static string DescribeDeferredPayload(DeferredDispatch d)
    {
        var parts = new List<string>();
        foreach (var block in d.Payload.Blocks)
        {
            var cmds = string.Join(", ", block.Commands.Select(CommandDescriber.DescribeNatural));
            parts.Add(cmds);
        }

        return string.Join("; then ", parts);
    }

    private bool IsGiveWayDeferredMet(AircraftState aircraft, string targetCallsign)
    {
        var target = FindAircraft(targetCallsign);
        if (target is null || !target.IsOnGround)
        {
            return true; // Target gone or airborne — no conflict
        }

        var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = targetCallsign };
        return FlightPhysics.IsGiveWayMet(aircraft, trigger, FindAircraft);
    }

    private void EmitTerminal(string kind, string callsign, string message)
    {
        _terminalEntries.Add(new TerminalEntry(kind, callsign, message));
    }

    /// <summary>
    /// Computes the hold-short nodes currently occupied by a holding or exiting aircraft from live
    /// aircraft state. Includes runway hold-short nodes an aircraft's tail hangs over while holding
    /// short of a taxiway (issue #172). The per-tick cache is transient, so this recomputes on demand —
    /// for diagnostics and tests querying between ticks.
    /// </summary>
    public IReadOnlySet<int> ComputeOccupiedHoldShortNodes() => BuildOccupiedHoldShortNodes();

    private HashSet<int> BuildOccupiedHoldShortNodes()
    {
        var occupied = new HashSet<int>();
        foreach (var ac in World.GetSnapshot())
        {
            if (ac.Phases?.CurrentPhase is HoldingShortPhase hs)
            {
                occupied.Add(hs.HoldShort.NodeId);

                // Tail-over-runway (issue #172): an aircraft holding short of a taxiway with its tail
                // over a runway also occupies that runway's hold-short node, so arrivals don't plan to
                // use the exit it is blocking. Read from the route — it survives snapshot restore,
                // unlike the phase's reconstructed HoldShort copy.
                if (ac.Ground.AssignedTaxiRoute?.GetHoldShortAt(hs.HoldShort.NodeId)?.TailOverRunwayNodeId is { } tailOverNode)
                {
                    occupied.Add(tailOverNode);
                }
                continue;
            }

            // Aircraft navigating toward an exit are claiming their target hold-short node
            if (ac.Phases?.CurrentPhase is RunwayExitPhase rep && rep.TargetHoldShortNodeId is { } repNodeId)
            {
                occupied.Add(repNodeId);
                continue;
            }

            // Aircraft holding after runway exit occupy their hold-short node
            if (ac.Phases?.CurrentPhase is HoldingAfterExitPhase haep && haep.HoldShortNodeId is { } haepNodeId)
            {
                occupied.Add(haepNodeId);
            }
        }

        return occupied;
    }

    private void PreTick(AircraftState aircraft, double deltaSeconds)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return;
        }

        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        var runway = aircraft.Phases.AssignedRunway;
        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var occupiedNodes = _occupiedHoldShortNodes;

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = cat,
            DeltaSeconds = deltaSeconds,
            Logger = _logger,
            Runway = runway,
            FieldElevation = runway?.ElevationFt ?? CommandDispatcher.ResolveFieldElevation(aircraft, groundLayout),
            GroundLayout = groundLayout,
            Weather = World.Weather,
            ScenarioElapsedSeconds = Scenario?.ElapsedSeconds ?? 0,
            AutoClearedToLand = Scenario?.AutoClearedToLand ?? false,
            AutoPullUpToParallel = Scenario?.AutoPullUpToParallel ?? false,
            SoloTrainingMode = Scenario?.SoloTrainingMode ?? false,
            ScenarioId = Scenario?.ScenarioId,
            SoloParkingInitialCallupRatePercent = Scenario?.SoloParkingInitialCallupRatePercent ?? 100,
            SoloGoAroundProbabilityPercent = Scenario?.SoloGoAroundProbabilityPercent ?? 0,
            Rng = World.Rng,
            TryReserveSoloParkingInitialCallupSlot = TryReserveSoloParkingInitialCallupSlot,
            RpoShowPilotSpeech = Scenario?.RpoShowPilotSpeech ?? false,
            StudentPositionType = Scenario?.StudentPositionType,
            StudentPosition = Scenario?.StudentPosition,
            ArtccId = Scenario?.ArtccId,
            PrimaryAirportId = Scenario?.PrimaryAirportId,
            AtisLetter = PilotResponder.ResolvePrimaryFieldAtisLetter(Scenario),
            InitialContactTransfers = Scenario?.InitialContactTransfers ?? Yaat.Sim.Data.InitialContactTransferCatalog.Empty,
            StudentRadioName = PilotResponder.ResolveStudentRadioName(Scenario),
            IsHoldShortNodeOccupied = occupiedNodes is not null ? nodeId => occupiedNodes.Contains(nodeId) : null,
            OccupiedHoldShortNodes = occupiedNodes,
            MarkHoldShortNodeOccupied = occupiedNodes is not null ? nodeId => occupiedNodes.Add(nodeId) : null,
            TowerPosition = (Scenario?.IsStudentTowerPosition == true) ? Scenario.StudentPosition : null,
            // Phases that consult follow targets (pattern spacing, VfrFollowPhase)
            // need a way to resolve the lead aircraft by callsign.
            AircraftLookup = World.FindAircraft,
        };

        PhaseRunner.Tick(aircraft, ctx);
    }

    private void ProcessDelayedSpawns(List<AircraftState> spawned)
    {
        var scenario = Scenario!;
        for (int i = scenario.DelayedQueue.Count - 1; i >= 0; i--)
        {
            var entry = scenario.DelayedQueue[i];

            // Hold-for-release spawn gate: a held runway/airborne departure does not appear on the
            // scope while its airport is armed — it spawns only when released (REL clears the flag).
            if (HeldReleaseService.IsSpawnHeld(scenario, entry))
            {
                continue;
            }

            if (scenario.ElapsedSeconds >= entry.SpawnAtSeconds)
            {
                scenario.DelayedQueue.RemoveAt(i);
                entry.Aircraft.State.SpawnedAtSeconds = scenario.ElapsedSeconds;
                // A ground (parking/taxiway) departure spawning under an armed airport holds short
                // until released — mark it now so the runway-entry gate withholds LUAW/CTO.
                HeldReleaseService.MarkHeldOnSpawnIfArmed(scenario, entry.Aircraft.State);
                World.AddAircraft(entry.Aircraft.State);
                DispatchPresetCommands(entry.Aircraft);
                spawned.Add(entry.Aircraft.State);

                EmitTerminal("System", entry.Aircraft.State.Callsign, "[Spawn] Delayed");

                foreach (var msg in entry.Aircraft.AutoTrackMessages)
                {
                    EmitTerminal("System", entry.Aircraft.State.Callsign, msg);
                }
            }
        }

        if (spawned.Count > 0 && scenario.DelayedQueue.Count == 0)
        {
            EmitTerminal("System", "", "[Scenario] No delayed spawns left");
        }
    }

    private const double SpawnRetryBackoffSeconds = 5.0;
    private const double FinalCorridorHalfWidthNm = 2.0;
    private const double FinalCorridorMarginNm = 3.0;
    private const double TerminalRadarFloorNm = 3.0;

    private void ProcessGenerators(List<GeneratorSpawn> generatorSpawns)
    {
        var scenario = Scenario!;
        if (
            (_isReplayingRecordedActions && _replayHasRecordedAircraftSpawns)
            || (scenario.IsPlaybackMode && scenario.ActionLog.Any(static a => a is RecordedAircraftSpawn))
        )
        {
            return;
        }

        var ratePercent = ScenarioPacing.ClampArrivalGeneratorPercent(scenario.SoloArrivalGeneratorRatePercent);
        if (ratePercent <= 0)
        {
            return;
        }

        foreach (var gen in scenario.Generators)
        {
            if (gen.IsExhausted)
            {
                continue;
            }
            if (scenario.ElapsedSeconds < gen.Config.StartTimeOffset)
            {
                continue;
            }
            if (gen.Config.MaxTime is { } maxTime && scenario.ElapsedSeconds > maxTime)
            {
                gen.IsExhausted = true;
                _logger.LogInformation("Generator '{Id}' exhausted at t={T}s (maxTime={MaxTime})", gen.Config.Id, scenario.ElapsedSeconds, maxTime);
                continue;
            }

            TrySpawnArrival(gen, ratePercent, generatorSpawns);
        }
    }

    /// <summary>
    /// Time-first spawn: <see cref="ScenarioGeneratorConfig.IntervalTime"/> drives cadence (when the next
    /// arrival is due). When due, the new arrival is placed at the back of the stream at
    /// <c>D = max(InitialDistance, rearmostDistance + gap)</c>, where <c>gap</c> is the larger (binding) of
    /// the configured <c>IntervalDistance</c> and the 7110.65 wake minimum. The placement is capped at
    /// <c>MaxDistance</c>: if no room exists within the cap the spawn waits (retry backoff) so the cap is
    /// never exceeded. An empty corridor has no rearmost, so the arrival spawns exactly at
    /// <c>InitialDistance</c> — the cold start needs no special case.
    /// </summary>
    private void TrySpawnArrival(GeneratorState gen, int ratePercent, List<GeneratorSpawn> generatorSpawns)
    {
        var scenario = Scenario!;
        if (scenario.ElapsedSeconds < gen.NextSpawnSeconds)
        {
            return;
        }

        var engine = ResolveEngine(gen.Config.EngineType);
        var weight = ResolveWeight(gen.Config, engine, World.Rng);
        var rearmost = RearmostInbound(gen);

        double gap;
        double placement;
        if (rearmost is null)
        {
            gap = 0;
            placement = gen.Config.InitialDistance;
        }
        else
        {
            var (leaderDistance, leader) = rearmost.Value;
            gap = SpacingGapNm(gen, leader, weight);
            placement = Math.Max(gen.Config.InitialDistance, leaderDistance + gap);
        }

        if (placement > gen.Config.MaxDistance)
        {
            // No room within the corridor cap — wait and retry so the average rate is preserved.
            if (rearmost is null)
            {
                _logger.LogWarning(
                    "Generator '{Id}' cannot place arrival: InitialDistance {Init}nm exceeds MaxDistance {Max}nm",
                    gen.Config.Id,
                    gen.Config.InitialDistance,
                    gen.Config.MaxDistance
                );
            }
            gen.NextSpawnSeconds = scenario.ElapsedSeconds + SpawnRetryBackoffSeconds;
            return;
        }

        var state = SpawnGeneratedArrival(gen, placement, weight, engine);
        if (state is null)
        {
            gen.NextSpawnSeconds = scenario.ElapsedSeconds + SpawnRetryBackoffSeconds;
            return;
        }

        generatorSpawns.Add(new GeneratorSpawn(state, gen.Config.AutoTrackConfiguration));
        _generatorSpawnLog.Add(
            new GeneratorSpawnRecord(gen.Config.Id, state.Callsign, scenario.ElapsedSeconds, placement, rearmost?.DistanceNm, gap)
        );
        gen.NextSpawnSeconds = scenario.ElapsedSeconds + EffectiveSpawnIntervalSeconds(gen, ratePercent);
    }

    private double EffectiveSpawnIntervalSeconds(GeneratorState gen, int ratePercent)
    {
        var interval = ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(gen.Config.IntervalTime, ratePercent);
        if (gen.Config.RandomizeInterval)
        {
            var jitter = interval * 0.25;
            interval += ((World.Rng.NextDouble() * 2) - 1) * jitter;
        }
        return Math.Max(interval, SpawnRetryBackoffSeconds);
    }

    /// <summary>
    /// Minimum in-trail gap (nm) the new arrival must sit behind the rearmost aircraft inbound to the
    /// runway: the largest (binding) of the generator's configured <c>IntervalDistance</c>, the 3 NM
    /// terminal radar floor, and the 7110.65 Table 5-5-2 wake-turbulence minimum for the leader/follower
    /// pair. The constraints bind, they do not add — a 5 nm author spacing behind a non-wake leader stays
    /// 5 nm, while a heavy leader can widen it to the wake minimum. The follower's specific type is not yet
    /// chosen at placement time, so the wake floor uses the coarse weight-class minima (the leader's class
    /// still reflects its CWT category); ATPA spacing uses the precise per-type CWT minima.
    /// </summary>
    private static double SpacingGapNm(GeneratorState gen, AircraftState leader, WeightClass followerWeight)
    {
        var wakeFloor = WakeTurbulenceData.OnApproachWakeSeparationNm(
            WakeTurbulenceData.WakeClassForType(leader.AircraftType, AircraftCategorization.Categorize(leader.AircraftType)),
            WakeClassForWeight(followerWeight)
        );
        return Math.Max(gen.Config.IntervalDistance, Math.Max(TerminalRadarFloorNm, wakeFloor));
    }

    private static WakeTurbulenceData.WakeClass WakeClassForWeight(WeightClass weight) =>
        weight switch
        {
            WeightClass.Heavy => WakeTurbulenceData.WakeClass.Heavy,
            WeightClass.Small => WakeTurbulenceData.WakeClass.Small,
            // SmallPlus spans CWT G (weightCode Large) and H (weightCode Small); Large is the
            // conservative-realistic coarse class for the on-approach wake floor behind it.
            WeightClass.SmallPlus => WakeTurbulenceData.WakeClass.Large,
            _ => WakeTurbulenceData.WakeClass.Large,
        };

    /// <summary>
    /// Airborne aircraft inside the runway's final-approach corridor (any generator's arrivals plus
    /// manual adds), each with its along-final distance-to-threshold (nm). Used so concurrent streams to
    /// the same runway don't overlap and the cold-start seed doesn't double up on existing traffic.
    /// </summary>
    private List<(double DistanceNm, AircraftState Aircraft)> CorridorAircraft(GeneratorState gen)
    {
        var rwy = gen.Runway;
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        var outbound = new TrueHeading((rwy.TrueHeading.Degrees + 180.0) % 360.0);
        var maxAlong = gen.Config.MaxDistance + FinalCorridorMarginNm;

        var result = new List<(double DistanceNm, AircraftState Aircraft)>();
        foreach (var ac in World.GetSnapshot())
        {
            if (ac.IsOnGround)
            {
                continue;
            }
            var cross = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, outbound));
            if (cross > FinalCorridorHalfWidthNm)
            {
                continue;
            }
            var along = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, outbound);
            if (along <= 0 || along > maxAlong)
            {
                continue;
            }
            result.Add((along, ac));
        }
        return result;
    }

    /// <summary>
    /// Rearmost (greatest distance-to-threshold) aircraft in the runway's final-approach corridor, or
    /// null when the corridor is empty.
    /// </summary>
    private (double DistanceNm, AircraftState Aircraft)? RearmostInbound(GeneratorState gen)
    {
        (double DistanceNm, AircraftState Aircraft)? rearmost = null;
        foreach (var entry in CorridorAircraft(gen))
        {
            if (rearmost is null || entry.DistanceNm > rearmost.Value.DistanceNm)
            {
                rearmost = entry;
            }
        }
        return rearmost;
    }

    /// <summary>
    /// In-trail speed management for the arrival-generator stream — the simulated approach
    /// controller (TRACON) that feeds correctly-spaced traffic to the tower (LC) student. Each
    /// tick, for every generator runway, pairs each generator-arrival follower on final with the
    /// aircraft immediately ahead and stamps a <see cref="ControlTargets.SpeedCeiling"/> so the
    /// follower equalizes to its leader and holds the spawn spacing (<c>SpacingGapNm</c>) down
    /// the final instead of overrunning it (the QXE831/SWA8154 compression). The ceiling only
    /// ever lowers the phase's speed target (<see cref="FlightPhysics.UpdateSpeed"/> applies it
    /// as a continuous <c>min</c>), floors at the follower's Vref, and collapses to Vref by the
    /// threshold, so it never blocks the landing deceleration. Uses no RNG, so replay/rewind stay
    /// deterministic; it runs during replay too (old recordings have <c>IsGeneratorArrival</c>
    /// false and are unaffected).
    /// </summary>
    private void ApplyArrivalSpacing()
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        foreach (var gen in scenario.Generators)
        {
            var stream = CorridorAircraft(gen)
                .Where(e => string.Equals(e.Aircraft.Phases?.AssignedRunway?.Designator, gen.Runway.Designator, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.DistanceNm)
                .ToList();

            for (int i = 0; i < stream.Count; i++)
            {
                var (followerDist, follower) = stream[i];

                // Scope: only generator arrivals actively on final are managed as followers.
                if (!follower.IsGeneratorArrival || follower.Phases?.CurrentPhase is not FinalApproachPhase)
                {
                    continue;
                }

                // Override: once the controller touches this aircraft's speed or the student
                // takes the track, hand speed authority back for good (one-way latch).
                if (follower.Approach.AutoSpacingReleased || ShouldReleaseAutoSpacing(follower, scenario))
                {
                    follower.Approach.AutoSpacingReleased = true;
                    ReleaseManagedSpeedCeiling(follower);
                    continue;
                }

                // The lead aircraft of the stream has no one to follow — fly the normal profile.
                if (i == 0)
                {
                    ReleaseManagedSpeedCeiling(follower);
                    continue;
                }

                var (leaderDist, leader) = stream[i - 1];
                double vref = AircraftPerformance.ApproachSpeed(follower.AircraftType, AircraftCategorization.Categorize(follower.AircraftType));
                double scheduled = ArrivalSpacingManager.ScheduledFinalSpeedKts(vref, followerDist);
                double wakeFloor = WakeTurbulenceData.OnApproachWakeSeparationNm(
                    leader.AircraftType,
                    AircraftCategorization.Categorize(leader.AircraftType),
                    follower.AircraftType,
                    AircraftCategorization.Categorize(follower.AircraftType)
                );
                double target = Math.Max(gen.Config.IntervalDistance, Math.Max(TerminalRadarFloorNm, wakeFloor));
                double gap = followerDist - leaderDist;

                follower.Targets.SpeedCeiling = ArrivalSpacingManager.SpacingCeilingKts(leader.IndicatedAirspeed, gap, target, vref, scheduled);
            }
        }
    }

    /// <summary>
    /// True when the in-trail spacing manager should hand speed authority back for this
    /// generator arrival: a manual speed command was issued, its speed restrictions were
    /// deleted, or the student controller now owns the track (the simulated TRACON spaces an
    /// arrival only while it owns it).
    /// </summary>
    private static bool ShouldReleaseAutoSpacing(AircraftState aircraft, SimScenarioState scenario)
    {
        if (aircraft.Targets.HasExplicitSpeedCommand || aircraft.Procedure.SpeedRestrictionsDeleted)
        {
            return true;
        }

        return aircraft.Track.Owner is { } owner && scenario.StudentPosition is { } student && owner.MatchesPosition(student);
    }

    /// <summary>
    /// Clears a <see cref="ControlTargets.SpeedCeiling"/> the spacing manager owns. Generator
    /// arrivals spawn directly on final with their navigation route cleared, so they carry no
    /// crossing-speed or procedural ceiling — the manager is the sole non-manual ceiling source.
    /// Skips when the controller has set an explicit speed (which owns the ceiling).
    /// </summary>
    private static void ReleaseManagedSpeedCeiling(AircraftState aircraft)
    {
        if (!aircraft.Targets.HasExplicitSpeedCommand && aircraft.Targets.SpeedCeiling is not null)
        {
            aircraft.Targets.SpeedCeiling = null;
        }
    }

    /// <summary>
    /// Builds, adds, records, and announces one generated arrival placed <c>OnFinal</c> at
    /// <paramref name="distanceNm"/> with the already-resolved <paramref name="weight"/> and
    /// <paramref name="engine"/>. Returns the spawned state, or null if generation failed.
    /// </summary>
    private AircraftState? SpawnGeneratedArrival(GeneratorState gen, double distanceNm, WeightClass weight, EngineKind engine)
    {
        var scenario = Scenario!;
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = weight,
            Engine = engine,
            PositionType = SpawnPositionType.OnFinal,
            RunwayId = gen.Config.Runway,
            FinalDistanceNm = distanceNm,
            PreferredAirlineAirportId = scenario.PrimaryAirportId,
        };

        var existing = World.GetSnapshot();
        var groundLayout = scenario.PrimaryAirportId is not null ? _groundData.GetLayout(scenario.PrimaryAirportId) : null;
        var (state, error) = AircraftGenerator.Generate(request, scenario.PrimaryAirportId, existing, groundLayout, World.Rng);

        if (state is null)
        {
            _logger.LogWarning("Generator '{Id}' spawn failed at t={T}s: {Error}", gen.Config.Id, scenario.ElapsedSeconds, error);
            return null;
        }

        state.ScenarioId = scenario.ScenarioId;
        state.Ground.Layout = groundLayout;
        state.SpawnedAtSeconds = scenario.ElapsedSeconds;
        state.IsGeneratorArrival = true;

        World.AddAircraft(state);

        // A generator without autotrack has no owner/scratchpad to wait for, so record it now. When the
        // generator carries an AutoTrackConfiguration, the server applies it then calls RecordGeneratedSpawn
        // so the recorded snapshot captures the owner/scratchpad and replays with them intact.
        if (gen.Config.AutoTrackConfiguration is null)
        {
            RecordGeneratedAircraftSpawn(state);
        }

        EmitTerminal("System", state.Callsign, $"[Spawn] Generated ({gen.Config.Id})");

        _logger.LogInformation(
            "Generator '{Id}' spawned {Callsign} ({Type}) at {Dist:F1}nm on RWY {Runway}, t={T}s",
            gen.Config.Id,
            state.Callsign,
            state.AircraftType,
            distanceNm,
            gen.Config.Runway,
            scenario.ElapsedSeconds
        );

        return state;
    }

    private bool TryReserveSoloParkingInitialCallupSlot(double nowSeconds)
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return true;
        }

        return ScenarioPacing.TryReserveParkingInitialCallupSlot(scenario, nowSeconds);
    }

    public void ApplySoloPacingRates(
        int parkingInitialCallupRatePercent,
        int arrivalGeneratorRatePercent,
        int goAroundProbabilityPercent,
        bool rescheduleFromNow
    )
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        var oldParkingRate = ScenarioPacing.ClampParkingInitialCallupPercent(scenario.SoloParkingInitialCallupRatePercent);
        var newParkingRate = ScenarioPacing.ClampParkingInitialCallupPercent(parkingInitialCallupRatePercent);
        var parkingChanged = oldParkingRate != newParkingRate;
        var oldArrivalRate = ScenarioPacing.ClampArrivalGeneratorPercent(scenario.SoloArrivalGeneratorRatePercent);
        var newArrivalRate = ScenarioPacing.ClampArrivalGeneratorPercent(arrivalGeneratorRatePercent);
        var arrivalChanged = oldArrivalRate != newArrivalRate;

        scenario.SoloParkingInitialCallupRatePercent = newParkingRate;
        scenario.SoloArrivalGeneratorRatePercent = newArrivalRate;
        scenario.SoloGoAroundProbabilityPercent = ScenarioPacing.ClampGoAroundProbabilityPercent(goAroundProbabilityPercent);

        if (rescheduleFromNow && parkingChanged)
        {
            RescheduleSoloParkingInitialCallupsFromNow(scenario, oldParkingRate, newParkingRate);
        }

        if (rescheduleFromNow && arrivalChanged)
        {
            RescheduleArrivalGeneratorsFromNow(scenario);
        }
    }

    private static void RescheduleSoloParkingInitialCallupsFromNow(SimScenarioState scenario, int oldRate, int newRate)
    {
        if (newRate <= 0)
        {
            scenario.NextSoloParkingInitialCallupSlotSeconds = double.PositiveInfinity;
            return;
        }

        var now = scenario.ElapsedSeconds;
        if ((oldRate <= 0) || (newRate > oldRate))
        {
            scenario.NextSoloParkingInitialCallupSlotSeconds = now;
            return;
        }

        if (newRate < oldRate)
        {
            var slowerSlot = now + ScenarioPacing.EffectiveParkingInitialCallupIntervalSeconds(newRate);
            scenario.NextSoloParkingInitialCallupSlotSeconds = double.IsPositiveInfinity(scenario.NextSoloParkingInitialCallupSlotSeconds)
                ? slowerSlot
                : Math.Max(scenario.NextSoloParkingInitialCallupSlotSeconds, slowerSlot);
        }
    }

    private static void RescheduleArrivalGeneratorsFromNow(SimScenarioState scenario)
    {
        var rate = ScenarioPacing.ClampArrivalGeneratorPercent(scenario.SoloArrivalGeneratorRatePercent);
        foreach (var gen in scenario.Generators)
        {
            if (gen.IsExhausted)
            {
                continue;
            }

            gen.NextSpawnSeconds =
                rate <= 0
                    ? double.PositiveInfinity
                    : scenario.ElapsedSeconds + ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(gen.Config.IntervalTime, rate);
        }
    }

    private static WeightClass ResolveWeight(ScenarioGeneratorConfig config, EngineKind engine, Random rng)
    {
        var baseWeight = ParseWeightCategory(config.WeightCategory);
        return config.RandomizeWeightCategory ? RandomWeightForEngine(engine, baseWeight, rng) : baseWeight;
    }

    private static WeightClass ParseWeightCategory(string category) =>
        category switch
        {
            "Small" => WeightClass.Small,
            "SmallPlus" => WeightClass.SmallPlus,
            "Heavy" => WeightClass.Heavy,
            _ => WeightClass.Large,
        };

    /// <summary>
    /// The weight classes a randomize-weight generator may roll, with their relative shares, bounded to a
    /// band around the generator's configured base weight (aviation-reviewed). Bounding keeps a generator
    /// from feeding a runway an aircraft it can't take — a Small/SmallPlus generator (short runway) never
    /// rolls a mainline jet, and a Large/Heavy generator never drops below the upper-small tier:
    /// <list type="bullet">
    /// <item>Small / SmallPlus — {Small, SmallPlus} (light GA + upper-small business jets / commuters).</item>
    /// <item>Large — {SmallPlus, Large, Heavy}.</item>
    /// <item>Heavy — {Large, Heavy}.</item>
    /// </list>
    /// The configured base class always carries the plurality of the mix.
    /// </summary>
    private static IReadOnlyList<(WeightClass Weight, double Share)> BaseWeightBand(WeightClass baseWeight) =>
        baseWeight switch
        {
            WeightClass.Small => [(WeightClass.Small, 0.65), (WeightClass.SmallPlus, 0.35)],
            WeightClass.SmallPlus => [(WeightClass.Small, 0.35), (WeightClass.SmallPlus, 0.65)],
            WeightClass.Large => [(WeightClass.SmallPlus, 0.10), (WeightClass.Large, 0.80), (WeightClass.Heavy, 0.10)],
            WeightClass.Heavy => [(WeightClass.Large, 0.40), (WeightClass.Heavy, 0.60)],
            _ => [(WeightClass.Large, 1.0)],
        };

    /// <summary>
    /// Rolls a random arrival weight class for the <c>randomizeWeightCategory</c> option, bounded to the
    /// <see cref="BaseWeightBand"/> around the generator's configured base weight and then intersected with
    /// the classes that actually have a type pool for the generator's fixed <paramref name="engine"/> (per
    /// <see cref="AircraftGenerator.GetTypesForCombo"/>). The intersection is what keeps a randomized
    /// turboprop/piston generator from rolling a class that would only degrade through the fallback chain to
    /// a nonsensical type — no Large/Heavy turboprop exists, and piston is Small singles or Large twins with
    /// nothing between. A Small-class roll resolves to general-aviation types (bizjets, light pistons, light
    /// turboprops) that no scheduled airline operates, so those spawns come up under N-number callsigns.
    /// </summary>
    public static WeightClass RandomWeightForEngine(EngineKind engine, WeightClass baseWeight, Random rng)
    {
        var band = BaseWeightBand(baseWeight).Where(e => AircraftGenerator.GetTypesForCombo(e.Weight, engine) is not null).ToList();

        if (band.Count == 0)
        {
            // Misconfigured base/engine combo (e.g. a Heavy turboprop generator, whose whole band has no
            // pool). Fall back to a uniform roll over the classes the engine does have a pool for, so the
            // spawn still resolves to a real type instead of degrading through the fallback chain.
            band = Enum.GetValues<WeightClass>()
                .Where(w => AircraftGenerator.GetTypesForCombo(w, engine) is not null)
                .Select(w => (Weight: w, Share: 1.0))
                .ToList();
        }

        var pick = rng.NextDouble() * band.Sum(e => e.Share);
        foreach (var entry in band)
        {
            pick -= entry.Share;
            if (pick <= 0)
            {
                return entry.Weight;
            }
        }
        return band[^1].Weight;
    }

    private static EngineKind ResolveEngine(string engineType)
    {
        return engineType switch
        {
            "Piston" => EngineKind.Piston,
            "Turboprop" => EngineKind.Turboprop,
            _ => EngineKind.Jet,
        };
    }

    private void ProcessReleaseQueue()
    {
        var scenario = Scenario!;
        if (scenario.ReleaseQueue.Count == 0)
        {
            return;
        }

        var due = scenario.ReleaseQueue.Where(r => scenario.ElapsedSeconds >= r.FireAtSeconds).OrderBy(r => r.FireAtSeconds).ToList();
        if (due.Count == 0)
        {
            return;
        }

        scenario.ReleaseQueue.RemoveAll(r => scenario.ElapsedSeconds >= r.FireAtSeconds);

        foreach (var r in due)
        {
            var result = HeldReleaseService.Release(scenario, World, World.Rng, r.Callsign ?? r.Airport, null);
            if (result.Success)
            {
                EmitTerminal("System", r.Callsign ?? "", $"[HFR] {result.Message}");
            }
        }
    }

    /// <summary>
    /// Fires due TIMER countdowns (set via the TIMER command). Mirrors <see cref="ProcessReleaseQueue"/>:
    /// timers are gated on <see cref="SimScenarioState.ElapsedSeconds"/> so they count in sim time
    /// (paused with the sim, scaled by sim rate). On expiry each emits a green SAY-style terminal
    /// entry — the free-text message, or "timer expired" when none was given. Per-aircraft timers
    /// whose aircraft has been deleted are dropped silently so they never attribute a SAY to a gone
    /// aircraft.
    /// </summary>
    private void ProcessTimers()
    {
        var scenario = Scenario!;
        if (scenario.ActiveTimers.Count == 0)
        {
            return;
        }

        scenario.ActiveTimers.RemoveAll(t => t.Callsign is not null && World.FindAircraft(t.Callsign) is null);

        var due = scenario.ActiveTimers.Where(t => scenario.ElapsedSeconds >= t.FireAtSeconds).OrderBy(t => t.FireAtSeconds).ToList();
        if (due.Count == 0)
        {
            return;
        }

        scenario.ActiveTimers.RemoveAll(t => scenario.ElapsedSeconds >= t.FireAtSeconds);

        foreach (var t in due)
        {
            var message = string.IsNullOrWhiteSpace(t.Message) ? "timer expired" : t.Message;
            EmitTerminal("Say", t.Callsign ?? "TIMER", message);
        }
    }

    /// <summary>
    /// Auto-issues a takeoff clearance to released hold-for-release ground departures once they are
    /// holding short of their departure runway, after a short deterministic tower-readback jitter.
    /// </summary>
    internal void ProcessReleasedGroundDepartures()
    {
        var scenario = Scenario!;
        foreach (var ac in World.GetSnapshot())
        {
            if (!ac.Ground.ReleasedForDeparture)
            {
                continue;
            }

            // Only once it has reached the hold-short line of its departure runway.
            if (ac.Phases?.CurrentPhase is not HoldingShortPhase hs || hs.HoldShort.Reason != HoldShortReason.DestinationRunway)
            {
                continue;
            }

            if (scenario.ElapsedSeconds - ac.Ground.ReleasedAtSeconds < ReleaseAutoCtoJitterSeconds(ac.Callsign))
            {
                continue;
            }

            ac.Ground.ReleasedForDeparture = false;
            AutoIssueTakeoffClearance(ac);
        }
    }

    /// <summary>Deterministic 5–20 s tower-readback jitter from the callsign (FNV-1a; replay-safe, no RNG state).</summary>
    private static double ReleaseAutoCtoJitterSeconds(string callsign)
    {
        uint h = 2166136261u;
        foreach (var c in callsign)
        {
            h = (h ^ c) * 16777619u;
        }
        return HeldReleaseService.MinGroundReleaseAutoCtoJitterSeconds + (h % HeldReleaseService.GroundReleaseAutoCtoJitterRangeSeconds);
    }

    private void AutoIssueTakeoffClearance(AircraftState aircraft)
    {
        var parsed = CommandParser.ParseCompound("CTO", aircraft.FlightPlan.Route);
        if (!parsed.IsSuccess)
        {
            _logger.LogWarning("Auto-CTO parse failed for released departure {Callsign}", aircraft.Callsign);
            return;
        }

        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var ctx = new DispatchContext(
            groundLayout,
            World.Rng,
            World.Weather,
            FindAircraft,
            () => World.GetSnapshot(),
            Scenario!.ValidateDctFixes,
            Scenario!.AutoCrossRunway,
            Scenario!.SoloTrainingMode,
            Scenario!.RpoShowPilotSpeech,
            _terminalEntries.Add,
            Scenario!.ArtccConfig,
            Scenario!.ElapsedSeconds,
            PreserveConditionals: false,
            // The takeoff clearance is issued by the automated tower, not by the student (who only
            // lifted the hold-for-release). It is not the student establishing two-way comms, so it
            // must not mark initial contact — the departure still checks in after takeoff.
            IsScenarioScripted: true
        );
        CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, ctx);
        EmitTerminal("System", aircraft.Callsign, "[HFR] Released — cleared for takeoff");
    }

    private void ProcessTimedPresets()
    {
        var scenario = Scenario!;
        if (scenario.PresetQueue.Count == 0)
        {
            return;
        }

        List<AircraftState>? snapshot = null;

        for (int i = scenario.PresetQueue.Count - 1; i >= 0; i--)
        {
            var preset = scenario.PresetQueue[i];
            if (scenario.ElapsedSeconds < preset.FireAtSeconds)
            {
                continue;
            }

            scenario.PresetQueue.RemoveAt(i);
            snapshot ??= World.GetSnapshot();

            var aircraft = snapshot.FirstOrDefault(a => a.Callsign.Equals(preset.Callsign, StringComparison.OrdinalIgnoreCase));
            if (aircraft is null)
            {
                continue;
            }

            var timedResult = CommandParser.ParseCompound(preset.Command, aircraft.FlightPlan.Route);
            if (!timedResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Timed preset parse failed for {Callsign}: \"{Command}\" — {Reason}",
                    preset.Callsign,
                    preset.Command,
                    timedResult.Reason
                );
                EmitTerminal("Warning", preset.Callsign, $"[Preset] Unparseable: {preset.Command}");
                continue;
            }

            var compound = timedResult.Value!;

            if (TryDispatchImmediateTrackPreset(compound, aircraft))
            {
                EmitTerminal("System", preset.Callsign, $"[Preset] {preset.Command}");
                continue;
            }

            var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
            var presetCtx = new DispatchContext(
                groundLayout,
                World.Rng,
                World.Weather,
                FindAircraft,
                () => World.GetSnapshot(),
                scenario.ValidateDctFixes,
                scenario.AutoCrossRunway,
                scenario.SoloTrainingMode,
                scenario.RpoShowPilotSpeech,
                _terminalEntries.Add,
                scenario.ArtccConfig,
                scenario.ElapsedSeconds,
                PreserveConditionals: false,
                IsScenarioScripted: true
            );
            CommandDispatcher.DispatchCompound(compound, aircraft, presetCtx);

            EmitTerminal("System", preset.Callsign, $"[Preset] {preset.Command}");
        }
    }

    private void ProcessTriggers()
    {
        var scenario = Scenario!;
        for (int i = scenario.TriggerQueue.Count - 1; i >= 0; i--)
        {
            var trigger = scenario.TriggerQueue[i];
            if (scenario.ElapsedSeconds >= trigger.FireAtSeconds)
            {
                scenario.TriggerQueue.RemoveAt(i);
                ExecuteGlobalCommand(trigger.Command);
            }
        }
    }

    private void ExecuteGlobalCommand(string command)
    {
        var globalResult = CommandParser.Parse(command);
        if (!globalResult.IsSuccess)
        {
            _logger.LogWarning("Unknown trigger command: {Cmd} — {Reason}", command, globalResult.Reason);
            return;
        }

        var parsed = globalResult.Value!;
        if (parsed is SquawkAllCommand or SquawkNormalAllCommand or SquawkStandbyAllCommand)
        {
            var result = HandleGlobalSquawkCommand(parsed);
            EmitTerminal("System", "", $"[Trigger] {result}");
        }
    }

    private string HandleGlobalSquawkCommand(ParsedCommand command)
    {
        var count = 0;
        foreach (var ac in World.GetSnapshot())
        {
            switch (command)
            {
                case SquawkAllCommand:
                    ac.Transponder.Code = ac.Transponder.AssignedCode;
                    break;
                case SquawkNormalAllCommand:
                    ac.Transponder.Mode = "C";
                    break;
                case SquawkStandbyAllCommand:
                    ac.Transponder.Mode = "Standby";
                    break;
            }

            count++;
        }

        var verb = command switch
        {
            SquawkAllCommand => "SQALL",
            SquawkNormalAllCommand => "SNALL",
            SquawkStandbyAllCommand => "SSALL",
            _ => "?",
        };

        return $"{verb}: {count} aircraft updated";
    }

    /// <summary>
    /// Optional callback invoked per aircraft before its presets are dispatched.
    /// Tests can use this to replace, modify, or clear presets for specific aircraft.
    /// </summary>
    public Action<LoadedAircraft>? PresetOverride { get; set; }

    private void DispatchSinglePreset(string command, AircraftState aircraft)
    {
        var presetResult = CommandParser.ParseCompound(command, aircraft.FlightPlan.Route);
        if (!presetResult.IsSuccess)
        {
            _logger.LogWarning("Preset parse failed for {Callsign}: \"{Command}\" — {Reason}", aircraft.Callsign, command, presetResult.Reason);
            EmitTerminal("Warning", aircraft.Callsign, $"[Preset] Unparseable: {command}");
            return;
        }

        var compound = presetResult.Value!;

        if (TryDispatchImmediateTrackPreset(compound, aircraft))
        {
            EmitTerminal("System", aircraft.Callsign, $"[Preset] {command}");
            return;
        }

        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var singlePresetCtx = new DispatchContext(
            groundLayout,
            World.Rng,
            World.Weather,
            FindAircraft,
            () => World.GetSnapshot(),
            Scenario!.ValidateDctFixes,
            Scenario!.AutoCrossRunway,
            Scenario!.SoloTrainingMode,
            Scenario!.RpoShowPilotSpeech,
            _terminalEntries.Add,
            Scenario!.ArtccConfig,
            Scenario!.ElapsedSeconds,
            PreserveConditionals: false,
            IsScenarioScripted: true
        );
        CommandDispatcher.DispatchCompound(compound, aircraft, singlePresetCtx);

        EmitTerminal("System", aircraft.Callsign, $"[Preset] {command}");
    }

    public void DispatchPresetCommands(LoadedAircraft loaded)
    {
        var scenario = Scenario!;

        PresetOverride?.Invoke(loaded);

        // Backstop for filed flight plans missing a destination: fall back to the
        // scenario's primary airport so arrivals show up in STARS arrival lists.
        // Skipped for cold-call aircraft (HasFlightPlan == false) — those must
        // remain destination-less until a controller files via DA / VP.
        if (
            loaded.State.FlightPlan.HasFlightPlan
            && string.IsNullOrWhiteSpace(loaded.State.FlightPlan.Destination)
            && !string.IsNullOrWhiteSpace(scenario.PrimaryAirportId)
        )
        {
            loaded.State.FlightPlan.Destination = scenario.PrimaryAirportId;
        }

        // Separate immediate presets from delayed ones.
        var immediatePresets = new List<string>();
        foreach (var preset in loaded.PresetCommands)
        {
            if (preset.TimeOffset > 0)
            {
                scenario.PresetQueue.Add(
                    new ScheduledPreset
                    {
                        Callsign = loaded.State.Callsign,
                        Command = preset.Command,
                        FireAtSeconds = scenario.ElapsedSeconds + preset.TimeOffset,
                    }
                );
            }
            else
            {
                immediatePresets.Add(preset.Command);
            }
        }

        // CFIX is additive — it stamps the named route fix in place — so multiple CFIX presets
        // can be dispatched independently and all their crossing restrictions land at spawn.
        // Compose into a single sequential compound only when a CFIX is followed by a non-CFIX
        // command (e.g. "CFIX ...; CAPP"): that later command must wait until the crossing fix
        // is reached, otherwise it would rebuild the route and lose the CFIX restrictions.
        bool allCfix = immediatePresets.All(p => p.TrimStart().StartsWith("CFIX ", StringComparison.OrdinalIgnoreCase));
        if (!allCfix && immediatePresets.Count >= 2 && immediatePresets[0].TrimStart().StartsWith("CFIX ", StringComparison.OrdinalIgnoreCase))
        {
            var composed = string.Join("; ", immediatePresets);
            DispatchSinglePreset(composed, loaded.State);
            return;
        }

        foreach (var cmd in immediatePresets)
        {
            DispatchSinglePreset(cmd, loaded.State);
        }
    }

    // --- Replay helpers ---

    /// <summary>
    /// Records a generated arrival's spawn for replay AFTER the server has applied its autotrack
    /// configuration, so the recorded snapshot carries the owner / scratchpad / temporary altitude.
    /// Generated arrivals without an autotrack configuration are instead recorded eagerly at spawn
    /// (see <see cref="SpawnGeneratedArrival"/>); this method is only for the autotrack-bearing path.
    /// </summary>
    public void RecordGeneratedSpawn(AircraftState state) => RecordGeneratedAircraftSpawn(state);

    private void RecordGeneratedAircraftSpawn(AircraftState state)
    {
        var scenario = Scenario;
        if (scenario is null || _isReplayingRecordedActions || scenario.IsPlaybackMode)
        {
            return;
        }

        scenario.ActionLog.Add(new RecordedAircraftSpawn(scenario.ElapsedSeconds, state.ToSnapshot()));
    }

    private static void ApplyRecordedAircraftSpawnsBeforeTick(
        List<RecordedAction> actions,
        ref int actionCursor,
        int elapsedSeconds,
        Action<RecordedAction> actionApplier,
        HashSet<int> appliedActionIndexes
    )
    {
        while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= elapsedSeconds)
        {
            if (actions[actionCursor] is RecordedAircraftSpawn spawn)
            {
                actionApplier(spawn);
                appliedActionIndexes.Add(actionCursor);
            }

            actionCursor++;
        }
    }

    private void ApplyRecordedAction(RecordedAction action)
    {
        switch (action)
        {
            case RecordedAircraftSpawn spawn:
                ApplyRecordedAircraftSpawn(spawn);
                break;
            case RecordedCommand cmd:
                ReplayCommand(cmd);
                break;
            case RecordedAmendFlightPlan amend:
                AmendFlightPlan(amend.Callsign, amend.Amendment);
                break;
            case RecordedRequestNewBeaconCode recycle:
                RequestNewBeaconCode(recycle.Callsign);
                break;
            case RecordedWeatherChange weather:
                if (weather.WeatherJson is not null)
                {
                    ApplyWeatherJson(weather.WeatherJson);
                    if (Scenario is not null)
                    {
                        Scenario.MetarReissuanceEnabled = weather.ReconstructMetars;
                    }
                }
                else
                {
                    World.Weather = null;
                    if (Scenario is not null)
                    {
                        Scenario.WeatherTimeline = null;
                        Scenario.WeatherSourceJson = null;
                        Scenario.MetarReissuanceEnabled = false;
                    }
                }
                break;
            case RecordedSettingChange setting:
                ApplySettingChange(setting);
                break;
            case RecordedArrivalGeneratorsChange generators:
                ApplyArrivalGeneratorsJson(generators.GeneratorsJson);
                break;
        }
    }

    private void ApplyRecordedAircraftSpawn(RecordedAircraftSpawn spawn)
    {
        AirportGroundLayout? groundLayout = null;
        if (spawn.Aircraft.Ground.LayoutAirportId is { } layoutAirportId)
        {
            groundLayout = _groundData.GetLayout(layoutAirportId);
        }
        else if (Scenario?.PrimaryAirportId is { } primaryAirportId)
        {
            groundLayout = _groundData.GetLayout(primaryAirportId);
        }

        var state = AircraftState.FromSnapshot(spawn.Aircraft, groundLayout);
        if (spawn.IsSynthetic)
        {
            NormalizeSyntheticAircraftSpawn(state);
        }

        World.AddAircraft(state);
    }

    private static void NormalizeSyntheticAircraftSpawn(AircraftState state)
    {
        var baseType = AircraftState.StripTypePrefix(state.AircraftType).Trim().ToUpperInvariant();
        if (!AircraftSiblingMap.TryResolve(baseType, out var sibling))
        {
            return;
        }

        state.AircraftType = sibling;
        if (
            string.IsNullOrWhiteSpace(state.FlightPlan.AircraftType)
            || state.FlightPlan.AircraftType.Equals(baseType, StringComparison.OrdinalIgnoreCase)
        )
        {
            state.FlightPlan.AircraftType = sibling;
        }

        var category = AircraftCategorization.Categorize(sibling);
        var defaultSpeed = AircraftPerformance.DefaultSpeed(sibling, category, state.Altitude, targetAltitude: null);
        if (!state.IsOnGround && state.IndicatedAirspeed > defaultSpeed)
        {
            state.IndicatedAirspeed = defaultSpeed;
        }

        if (state.Targets.TargetSpeed is { } targetSpeed && targetSpeed > defaultSpeed)
        {
            state.Targets.TargetSpeed = defaultSpeed;
        }
    }

    // Public for tests (replay-determinism of the command-run delay); production drives this through
    // the Replay / FastForwardTo entry points.
    public void ReplayCommand(RecordedCommand cmd)
    {
        // Track and AS-prefix commands run before the aircraft-exists guard so per-connection
        // active-position state still updates when the addressed aircraft hasn't spawned yet
        // (e.g. auto-accept sims firing for delayed-spawn aircraft). The applier safely no-ops
        // any per-aircraft mutation when aircraft is null.
        var asPrefixCheck = TrackResolver.ExtractAsPrefix(cmd.Command);
        var firstParse = CommandParser.Parse(asPrefixCheck.Remainder);
        if (
            firstParse.IsSuccess
            && firstParse.Value is not null
            && (TrackEngine.IsTrackCommand(firstParse.Value) || asPrefixCheck.AsOverrideTcp is not null)
        )
        {
            _replayTrackApplier.Apply(cmd.Command, FindAircraft(cmd.Callsign), cmd.ConnectionId, Scenario);
            return;
        }

        var (kind, parsed) = RecordedCommandClassifier.Classify(cmd.Command);

        switch (kind)
        {
            case RecordedCommandKind.SayOrShow:
                return;

            case RecordedCommandKind.Delete:
                // Before aircraft-exists guard: target may be in the delayed queue only.
                Scenario?.DelayedQueue.RemoveAll(e => e.Aircraft.State.Callsign.Equals(cmd.Callsign, StringComparison.OrdinalIgnoreCase));
                World.RemoveAircraft(cmd.Callsign);
                return;

            case RecordedCommandKind.SpawnNow:
                // Before aircraft-exists guard: a manual spawn pulls the aircraft FROM the delayed
                // queue, so it is intentionally not active yet. Gating it behind FindAircraft would
                // silently drop every recorded manual spawn on replay (and snapshot regeneration).
                HandleSpawnNow(cmd.Callsign);
                return;

            case RecordedCommandKind.SpawnDelay:
                // Before aircraft-exists guard: re-times a still-queued delayed spawn.
                HandleSpawnDelay(cmd.Callsign, ((SpawnDelayCommand)parsed!).Seconds);
                return;

            case RecordedCommandKind.Timer:
                if (Scenario is not null && parsed is TimerCommand timerCmd)
                {
                    TimerCommandReplayer.Apply(timerCmd, Scenario, World, cmd.Callsign);
                }

                return;

            case RecordedCommandKind.HoldForRelease:
                if (Scenario is not null && parsed is HoldForReleaseCommand hfr)
                {
                    HeldReleaseService.Arm(Scenario, World, hfr.Airport);
                }

                return;

            case RecordedCommandKind.DisarmHoldForRelease:
                if (Scenario is not null && parsed is DisarmHoldForReleaseCommand hfrOff)
                {
                    HeldReleaseService.Disarm(Scenario, World, hfrOff.Airport);
                }

                return;

            case RecordedCommandKind.ReleaseDeparture:
                if (Scenario is not null && parsed is ReleaseDepartureCommand rel)
                {
                    HeldReleaseService.ReplayRelease(Scenario, World, rel.Target, rel.IntervalSeconds, cmd.SpawnJitterSeconds);
                }

                return;

            case RecordedCommandKind.Coordination:
                // RD/RDH/RDR/RDACK/RDAUTO mutate state owned by yaat-server only.
                _logger.LogDebug("Replay: skipping coordination command {Cmd} for {Callsign} (no Sim-side handler)", cmd.Command, cmd.Callsign);
                return;

            case RecordedCommandKind.GhostTrack:
            case RecordedCommandKind.Strip:
            case RecordedCommandKind.TrackOwnership:
            case RecordedCommandKind.Consolidate:
            case RecordedCommandKind.Deconsolidate:
            case RecordedCommandKind.AcceptAllHandoffs:
            case RecordedCommandKind.InitiateHandoffAll:
                // Server-only handlers; Sim has no state to mutate.
                return;
        }

        var aircraft = FindAircraft(cmd.Callsign);
        if (aircraft is null)
        {
            return;
        }

        switch (kind)
        {
            case RecordedCommandKind.DeleteQueued:
                ReplayDeleteQueued(aircraft, ((DeleteQueuedCommand)parsed!).BlockNumber);
                return;

            case RecordedCommandKind.SquawkAll:
                HandleGlobalSquawkCommand(parsed!);
                return;

            case RecordedCommandKind.Note:
                aircraft.Note = AircraftState.TruncateNote(((NoteCommand)parsed!).Text);
                return;
        }

        var replayResult = CommandParser.ParseCompound(cmd.Command, aircraft.FlightPlan.Route);
        if (!replayResult.IsSuccess)
        {
            _logger.LogDebug(
                "[Replay] {Callsign}: recorded command '{Command}' failed to parse on replay — {Reason}",
                cmd.Callsign,
                cmd.Command,
                replayResult.Reason
            );
            return;
        }

        // Recorded command-run delay: reproduce the exact delay sampled at the live run rather than
        // re-rolling (a re-roll would draw from a divergent RNG state and break determinism). The
        // deferral fires through ProcessDeferredDispatches during replay ticking, exactly as it did live.
        if (cmd.ReactionDelaySeconds is double recordedReactionSeconds)
        {
            aircraft.DeferredDispatches.Add(
                new DeferredDispatch(recordedReactionSeconds, replayResult.Value!) { SourceText = cmd.Command, IsReactionDelay = true }
            );
            // Mirror the live SendCommand path: a deferred command still counts as accepted, so it
            // establishes two-way comms at issue time (not when the deferral later fires). Without this
            // a replayed/reconstructed vector never clears the Class B/C boundary-hold gate.
            Pilot.PilotInitialContactEligibility.RegisterControllerContact(aircraft, Scenario, replayResult.Value!);
            if (Scenario?.SoloTrainingMode == true)
            {
                SoloTrainingEvaluator.RecordControllerCommand(aircraft, replayResult.Value!, Scenario?.ElapsedSeconds ?? 0, World.GetSnapshot());
                PilotRequestTracker.ApplyControllerResponse(aircraft, replayResult.Value!, Scenario?.ElapsedSeconds ?? 0);
            }
            return;
        }

        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var replayCtx = new DispatchContext(
            groundLayout,
            World.Rng,
            World.Weather,
            FindAircraft,
            () => World.GetSnapshot(),
            Scenario?.ValidateDctFixes ?? true,
            Scenario?.AutoCrossRunway ?? false,
            Scenario?.SoloTrainingMode ?? false,
            Scenario?.RpoShowPilotSpeech ?? false,
            _terminalEntries.Add,
            Scenario?.ArtccConfig,
            Scenario?.ElapsedSeconds ?? 0,
            PreserveConditionals: false,
            IsScenarioScripted: false
        );
        var replayDispatchResult = CommandDispatcher.DispatchCompound(replayResult.Value!, aircraft, replayCtx);
        if (replayDispatchResult.Success)
        {
            // Mirror the live SendCommand path so a replayed/reconstructed instruction establishes the
            // two-way comms that clears the Class B/C boundary-hold gate.
            Pilot.PilotInitialContactEligibility.RegisterControllerContact(aircraft, Scenario, replayResult.Value!);
            if (replayCtx.SoloTrainingMode)
            {
                SoloTrainingEvaluator.RecordControllerCommand(aircraft, replayResult.Value!, Scenario?.ElapsedSeconds ?? 0, World.GetSnapshot());
                PilotRequestTracker.ApplyControllerResponse(aircraft, replayResult.Value!, Scenario?.ElapsedSeconds ?? 0);
            }
        }
        else
        {
            // Debug, not Warning: recordings faithfully replay commands that were rejected during the
            // live session too (e.g. TDLSS to a parked aircraft), so a rejection here is usually
            // expected, not a divergence. Enable this category at Debug to surface a command that
            // stopped taking effect because the replay layout drifted from the captured one.
            _logger.LogDebug(
                "[Replay] {Callsign}: recorded command '{Command}' was rejected on replay — {Message}",
                aircraft.Callsign,
                cmd.Command,
                replayDispatchResult.Message
            );
        }
    }

    private void HandleSpawnNow(string callsign)
    {
        var scenario = Scenario!;
        var entry = scenario.DelayedQueue.FirstOrDefault(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        scenario.DelayedQueue.Remove(entry);
        entry.Aircraft.State.SpawnedAtSeconds = scenario.ElapsedSeconds;
        World.AddAircraft(entry.Aircraft.State);
        DispatchPresetCommands(entry.Aircraft);
    }

    private void HandleSpawnDelay(string callsign, int seconds)
    {
        var scenario = Scenario!;
        var entry = scenario.DelayedQueue.FirstOrDefault(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        entry.SpawnAtSeconds = (int)scenario.ElapsedSeconds + seconds;
    }

    private void ApplySettingChange(RecordedSettingChange setting)
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        // Mirrors yaat-server's SimControlService recorders. Every setting the
        // server records mid-session must round-trip through replay so bundle
        // playback (and snapshot regeneration at export time) matches what the
        // user actually saw live.
        switch (setting.Setting)
        {
            case "AutoClearedToLand":
                if (bool.TryParse(setting.Value, out var ctl))
                {
                    scenario.AutoClearedToLand = ctl;
                }
                break;
            case "AutoCrossRunway":
                if (bool.TryParse(setting.Value, out var acr))
                {
                    scenario.AutoCrossRunway = acr;
                    World.ApplyAutoCrossToActiveTaxiRoutes(acr);
                }
                break;
            case "AutoPullUpToParallel":
                // Only affects future landing exits — no active-route walk needed.
                if (bool.TryParse(setting.Value, out var apup))
                {
                    scenario.AutoPullUpToParallel = apup;
                }
                break;
            case "AutoAcceptDelay":
                if (int.TryParse(setting.Value, out var seconds))
                {
                    scenario.AutoAcceptDelay = seconds < 0 ? TimeSpan.FromSeconds(-1) : TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60));
                }
                break;
            case "CommandRunDelayMinSeconds":
                if (int.TryParse(setting.Value, out var crdMin))
                {
                    scenario.CommandRunDelayMinSeconds = Math.Clamp(crdMin, 0, 60);
                }
                break;
            case "CommandRunDelayMaxSeconds":
                if (int.TryParse(setting.Value, out var crdMax))
                {
                    scenario.CommandRunDelayMaxSeconds = Math.Clamp(crdMax, 0, 60);
                }
                break;
            case "AutoDeleteMode":
                // Server writes ClientAutoDeleteOverride, not ScenarioAutoDeleteMode.
                // Null/empty string is a valid value: it means "clear the override and
                // fall back to the scenario default".
                scenario.ClientAutoDeleteOverride = string.IsNullOrEmpty(setting.Value) ? null : setting.Value;
                break;
            case "ValidateDctFixes":
                if (bool.TryParse(setting.Value, out var validate))
                {
                    scenario.ValidateDctFixes = validate;
                }
                break;
            case "SoloTrainingMode":
                if (bool.TryParse(setting.Value, out var soloTrainingMode))
                {
                    scenario.SoloTrainingMode = soloTrainingMode;
                }
                break;
            case "SoloParkingInitialCallupRatePercent":
                if (int.TryParse(setting.Value, out var parkingRate))
                {
                    ApplySoloPacingRates(
                        parkingRate,
                        scenario.SoloArrivalGeneratorRatePercent,
                        scenario.SoloGoAroundProbabilityPercent,
                        rescheduleFromNow: setting.ElapsedSeconds > 0
                    );
                }
                break;
            case "SoloArrivalGeneratorRatePercent":
                if (int.TryParse(setting.Value, out var arrivalRate))
                {
                    ApplySoloPacingRates(
                        scenario.SoloParkingInitialCallupRatePercent,
                        arrivalRate,
                        scenario.SoloGoAroundProbabilityPercent,
                        rescheduleFromNow: setting.ElapsedSeconds > 0
                    );
                }
                break;
            case "SoloGoAroundProbabilityPercent":
                if (int.TryParse(setting.Value, out var goAroundPct))
                {
                    ApplySoloPacingRates(
                        scenario.SoloParkingInitialCallupRatePercent,
                        scenario.SoloArrivalGeneratorRatePercent,
                        goAroundPct,
                        rescheduleFromNow: setting.ElapsedSeconds > 0
                    );
                }
                break;
            case "RpoShowPilotSpeech":
                if (bool.TryParse(setting.Value, out var rpoShowPilotSpeech))
                {
                    scenario.RpoShowPilotSpeech = rpoShowPilotSpeech;
                }
                break;
        }
    }

    /// <summary>
    /// Replaces the live arrival-generator list with the parsed JSON. Each generator is
    /// rescheduled "from now" — NextSpawnSeconds = elapsed + intervalTime, IsExhausted = false.
    /// Already-spawned aircraft keep flying. Returns warnings; the swap is best-effort per generator
    /// (entries with unresolvable runways are dropped with a warning).
    /// </summary>
    public List<string> ApplyArrivalGeneratorsJson(string generatorsJson)
    {
        var warnings = new List<string>();
        var scenario = Scenario;
        if (scenario is null)
        {
            warnings.Add("No active scenario");
            return warnings;
        }

        List<ScenarioGeneratorConfig>? configs;
        try
        {
            configs = JsonSerializer.Deserialize<List<ScenarioGeneratorConfig>>(generatorsJson);
        }
        catch (JsonException ex)
        {
            warnings.Add($"Invalid generators JSON: {ex.Message}");
            return warnings;
        }

        if (configs is null)
        {
            warnings.Add("Generators JSON deserialized to null");
            return warnings;
        }

        var navDb = NavigationDatabase.Instance;
        var airportId = scenario.PrimaryAirportId ?? "";
        var newStates = new List<GeneratorState>();
        foreach (var cfg in configs)
        {
            var runwayId = cfg.Runway ?? "";
            var runway = navDb.GetRunway(airportId, runwayId);
            if (runway is null)
            {
                warnings.Add($"Generator '{cfg.Id}': runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} not found at {airportId}");
                continue;
            }

            newStates.Add(
                new GeneratorState
                {
                    Config = cfg,
                    Runway = runway,
                    NextSpawnSeconds =
                        scenario.ElapsedSeconds
                        + ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(cfg.IntervalTime, scenario.SoloArrivalGeneratorRatePercent),
                    IsExhausted = false,
                }
            );
        }

        scenario.Generators.Clear();
        scenario.Generators.AddRange(newStates);
        return warnings;
    }

    private void ApplyWeatherJson(string weatherJson)
    {
        var parseResult = WeatherTimelineParser.Parse(weatherJson);
        if (parseResult.IsTimeline)
        {
            if (Scenario is not null)
            {
                Scenario.WeatherTimeline = parseResult.Timeline;
                Scenario.WeatherSourceJson = weatherJson;
            }
            World.Weather = parseResult.Timeline!.GetWeatherAt(Scenario?.ElapsedSeconds ?? 0);
        }
        else if (parseResult.IsProfile)
        {
            if (Scenario is not null)
            {
                Scenario.WeatherTimeline = null;
                Scenario.WeatherSourceJson = weatherJson;
            }
            World.Weather = parseResult.Profile;
        }
    }

    private static void ReplayDeleteQueued(AircraftState aircraft, int? blockNumber)
    {
        // Mirror the live DELAT/DELCOND handler exactly (queue blocks + deferred dispatches)
        // so replay reproduces deletions deterministically.
        ConditionalList.Delete(aircraft, blockNumber);
    }
}
