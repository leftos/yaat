using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Terminal message emitted during tick processing (presets, spawns, triggers, generators).
/// Drained by the server for broadcasting; discarded by client's convenience wrapper.
/// </summary>
public record TerminalEntry(string Kind, string Callsign, string Message);

/// <summary>
/// Result from <see cref="SimulationEngine.TickPrePhysics"/>. Lists aircraft spawned this tick
/// so the server can broadcast spawn events.
/// </summary>
public record struct TickPrePhysicsResult(List<AircraftState> SpawnedAircraft);

public sealed class SimulationEngine
{
    private const int PhysicsSubTickRate = 4;

    private readonly IAirportGroundData _groundData;

    private readonly ILogger _logger;
    private readonly List<TerminalEntry> _terminalEntries = [];

    // Replay cursor state — set by Replay(), consumed by ReplayOneSecond()
    private List<RecordedAction>? _replayActions;
    private int _replayActionCursor;

    // Holds the set of hold-short node IDs currently occupied by aircraft.
    // Built at the start of each TickPhysics, used by PreTick to prevent stacking.
    private HashSet<int>? _occupiedHoldShortNodes;

    public SimulationWorld World { get; } = new();
    public SimScenarioState? Scenario { get; set; }
    public ConsolidationState ConsolidationState { get; } = new();
    public ApproachEvaluator ApproachEvaluator { get; } = new();
    public BeaconCodePool BeaconCodePool { get; } = new();
    public TowerListTracker TowerListTracker { get; } = new();
    public ConflictAlertState ConflictAlerts { get; } = new();

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
            Scenario.ValidateDctFixes = scenarioDto.ValidateDctFixes;
            Scenario.IsPaused = scenarioDto.IsPaused;
            Scenario.SimRate = scenarioDto.SimRate;
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
        }

        // Reset engine-level state
        ConsolidationState.Clear();
        ConflictAlerts.Conflicts.Clear();
    }

    // --- Scenario loading ---

    public List<string> LoadScenario(string json, int rngSeed)
    {
        World.Clear();
        World.Rng = new SerializableRandom(rngSeed);

        var result = ScenarioLoader.Load(json, _groundData, World.Rng);

        Scenario = new SimScenarioState
        {
            ScenarioId = result.Id,
            ScenarioName = result.Name,
            RngSeed = rngSeed,
            OriginalScenarioJson = json,
            PrimaryAirportId = result.PrimaryAirportId,
        };

        // Add immediate aircraft and dispatch their presets
        foreach (var loaded in result.ImmediateAircraft)
        {
            loaded.State.ScenarioId = Scenario.ScenarioId;
            World.AddAircraft(loaded.State);
            DispatchPresetCommands(loaded);
        }

        // Queue delayed aircraft
        foreach (var loaded in result.DelayedAircraft)
        {
            loaded.State.ScenarioId = Scenario.ScenarioId;
            Scenario.DelayedQueue.Add(new DelayedSpawn { Aircraft = loaded, SpawnAtSeconds = loaded.SpawnDelaySeconds });
        }

        // Queue triggers
        foreach (var trigger in result.Triggers)
        {
            Scenario.TriggerQueue.Add(new ScheduledTrigger { Command = trigger.Command, FireAtSeconds = trigger.TimeOffset });
        }

        // Initialize generators
        foreach (var genConfig in result.Generators)
        {
            var runway = NavigationDatabase.Instance.GetRunway(result.PrimaryAirportId ?? "", genConfig.Runway);
            if (runway is null)
            {
                result.Warnings.Add($"Generator '{genConfig.Id}': runway {genConfig.Runway} not found");
                continue;
            }

            Scenario.Generators.Add(
                new GeneratorState
                {
                    Config = genConfig,
                    Runway = runway,
                    NextSpawnSeconds = genConfig.StartTimeOffset,
                    NextSpawnDistance = genConfig.InitialDistance,
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
            return new TickPrePhysicsResult([]);
        }

        var spawned = new List<AircraftState>();

        ProcessDelayedSpawns(spawned);
        ProcessGenerators(spawned);
        ProcessTriggers();
        ProcessTimedPresets();

        // Ensure ground layout is set
        if (scenario.PrimaryAirportId is not null && World.GroundLayout is null)
        {
            World.GroundLayout = _groundData.GetLayout(scenario.PrimaryAirportId);
        }

        return new TickPrePhysicsResult(spawned);
    }

    /// <summary>
    /// Physics step: runs FlightPhysics.Update and phase runner for all aircraft.
    /// Call multiple times per sim-second for sub-tick granularity.
    /// </summary>
    public void TickPhysics(double delta)
    {
        _occupiedHoldShortNodes = BuildOccupiedHoldShortNodes();
        World.Tick(delta, PreTick);
        _occupiedHoldShortNodes = null;
        ProcessDeferredDispatches(delta);
    }

    /// <summary>
    /// Post-physics: drains warnings, notifications, and approach scores from the world.
    /// The server reads these before calling this method to broadcast them.
    /// </summary>
    public void TickPostPhysics()
    {
        World.DrainAllWarnings();
        World.DrainAllNotifications();
        World.DrainAllApproachScores();
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
    }

    // --- Replay ---

    /// <summary>
    /// Fast-forward replay from t=0 to <paramref name="targetSeconds"/>, applying recorded actions
    /// at the correct times. The default action applier skips server-only commands (track, coordination).
    /// Pass a custom <paramref name="actionApplier"/> to handle those (server rewind).
    /// </summary>
    public void ReplayTo(int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier = null)
    {
        ReplayRange(0, targetSeconds, actions, actionApplier);
    }

    /// <summary>
    /// Replays from <paramref name="startSeconds"/> to <paramref name="targetSeconds"/>,
    /// applying actions and ticking physics for each second in the range.
    /// When startSeconds is 0, actions at t=0 are applied first.
    /// </summary>
    public void ReplayRange(int startSeconds, int targetSeconds, List<RecordedAction> actions, Action<RecordedAction>? actionApplier = null)
    {
        actionApplier ??= ApplyRecordedAction;

        int actionCursor = 0;

        if (startSeconds == 0)
        {
            // Apply actions at t=0 first (settings, immediate commands)
            while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= 0)
            {
                actionApplier(actions[actionCursor]);
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
        }

        double subDelta = 1.0 / PhysicsSubTickRate;
        for (int t = startSeconds + 1; t <= targetSeconds; t++)
        {
            Scenario!.ElapsedSeconds = t;

            TickPrePhysics();

            for (int sub = 0; sub < PhysicsSubTickRate; sub++)
            {
                TickPhysics(subDelta);
            }

            TickPostPhysics();
            _terminalEntries.Clear();

            // Advance weather timeline if active
            if (Scenario!.WeatherTimeline is { } timeline)
            {
                World.Weather = timeline.GetWeatherAt(t);
            }

            // Apply actions at this time
            while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= t)
            {
                actionApplier(actions[actionCursor]);
                actionCursor++;
            }
        }
    }

    public const int SnapshotIntervalSeconds = 5;

    public List<TimedSnapshot> ReplayWithSnapshots(int targetSeconds, List<RecordedAction> actions, Action<RecordedAction> actionApplier)
    {
        var snapshots = new List<TimedSnapshot>((targetSeconds / SnapshotIntervalSeconds) + 2);

        ReplayWithSnapshotCallback(
            targetSeconds,
            actions,
            actionApplier,
            (elapsed, actionIndex, state) =>
            {
                snapshots.Add(
                    new TimedSnapshot
                    {
                        ElapsedSeconds = elapsed,
                        ActionIndex = actionIndex,
                        State = state,
                    }
                );
            }
        );

        return snapshots;
    }

    /// <summary>
    /// Replays the simulation and invokes <paramref name="snapshotCallback"/> each time a snapshot
    /// is captured. The callback receives (elapsedSeconds, actionIndex, stateSnapshot) and can
    /// serialize/flush the snapshot immediately — the state is eligible for GC after the callback returns.
    /// </summary>
    public void ReplayWithSnapshotCallback(
        int targetSeconds,
        List<RecordedAction> actions,
        Action<RecordedAction> actionApplier,
        Action<double, int, StateSnapshotDto> snapshotCallback
    )
    {
        // Capture initial state at t=0
        int actionCursor = 0;
        while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= 0)
        {
            actionApplier(actions[actionCursor]);
            actionCursor++;
        }

        snapshotCallback(0, actionCursor - 1, CaptureSnapshot(actionCursor - 1));

        double subDelta = 1.0 / PhysicsSubTickRate;
        for (int t = 1; t <= targetSeconds; t++)
        {
            Scenario!.ElapsedSeconds = t;

            TickPrePhysics();

            for (int sub = 0; sub < PhysicsSubTickRate; sub++)
            {
                TickPhysics(subDelta);
            }

            TickPostPhysics();
            _terminalEntries.Clear();

            if (Scenario!.WeatherTimeline is { } timeline)
            {
                World.Weather = timeline.GetWeatherAt(t);
            }

            while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= t)
            {
                actionApplier(actions[actionCursor]);
                actionCursor++;
            }

            if ((t % SnapshotIntervalSeconds == 0) || (t == targetSeconds))
            {
                int idx = Math.Max(0, actionCursor - 1);
                snapshotCallback(t, idx, CaptureSnapshot(idx));
            }
        }
    }

    public void Replay(SessionRecording recording, double targetSeconds)
    {
        LoadScenario(recording.ScenarioJson, recording.RngSeed);

        // Apply weather if present
        if (recording.WeatherJson is not null)
        {
            ApplyWeatherJson(recording.WeatherJson);
        }

        ReplayTo((int)targetSeconds, recording.Actions);

        // Store replay cursor so ReplayOneSecond() can continue from here
        _replayActions = recording.Actions;
        _replayActionCursor = 0;
        int target = (int)targetSeconds;
        while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= target)
        {
            _replayActionCursor++;
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

        TickPrePhysics();

        double subDelta = 1.0 / PhysicsSubTickRate;
        for (int sub = 0; sub < PhysicsSubTickRate; sub++)
        {
            TickPhysics(subDelta);
        }

        TickPostPhysics();
        _terminalEntries.Clear();

        if (scenario.WeatherTimeline is { } timeline)
        {
            World.Weather = timeline.GetWeatherAt(t);
        }

        while (_replayActionCursor < _replayActions.Count && _replayActions[_replayActionCursor].ElapsedSeconds <= t)
        {
            ApplyRecordedAction(_replayActions[_replayActionCursor]);
            _replayActionCursor++;
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

        var parseResult = CommandParser.ParseCompound(command, aircraft.Route);
        if (!parseResult.IsSuccess)
        {
            return new CommandResult(false, $"Failed to parse command: {command} — {parseResult.Reason}");
        }

        var groundLayout = aircraft.GroundLayout ?? ResolveGroundLayout(aircraft);
        return CommandDispatcher.DispatchCompound(
            parseResult.Value!,
            aircraft,
            groundLayout,
            World.Rng,
            Scenario?.ValidateDctFixes ?? true,
            Scenario?.AutoCrossRunway ?? false
        );
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
        aircraft.AssignedTaxiRoute = null;
        aircraft.IsHeld = false;
        aircraft.Queue.Blocks.Clear();

        // Place on ground
        aircraft.Latitude = latitude;
        aircraft.Longitude = longitude;
        aircraft.TrueHeading = trueHeading;
        aircraft.TrueTrack = trueHeading;
        aircraft.IndicatedAirspeed = 0;
        aircraft.IsOnGround = true;
        aircraft.Targets.TargetSpeed = 0;

        // Install ground-idle phase so subsequent commands have phase context
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft));

        aircraft.GroundLayout = ResolveGroundLayout(aircraft);
    }

    public void AmendFlightPlan(string callsign, FlightPlanAmendment amendment)
    {
        var ac = FindAircraft(callsign);
        if (ac is null)
        {
            return;
        }

        if (amendment.AircraftType is not null)
        {
            ac.AircraftType = amendment.AircraftType;
        }
        if (amendment.EquipmentSuffix is not null)
        {
            ac.EquipmentSuffix = amendment.EquipmentSuffix;
        }
        if (amendment.Departure is not null)
        {
            ac.Departure = amendment.Departure;
        }
        if (amendment.Destination is not null)
        {
            ac.Destination = amendment.Destination;
        }
        if (amendment.CruiseSpeed is not null)
        {
            ac.CruiseSpeed = amendment.CruiseSpeed.Value;
        }
        if (amendment.CruiseAltitude is not null)
        {
            ac.CruiseAltitude = amendment.CruiseAltitude.Value;
        }
        if (amendment.FlightRules is not null)
        {
            ac.FlightRules = amendment.FlightRules;
        }
        if (amendment.Route is not null)
        {
            ac.Route = amendment.Route;
        }
        if (amendment.Remarks is not null)
        {
            ac.Remarks = amendment.Remarks;
        }
        if (amendment.Scratchpad1 is not null)
        {
            ac.Scratchpad1 = amendment.Scratchpad1;
            ac.WasScratchpad1Cleared = string.IsNullOrEmpty(amendment.Scratchpad1);
        }
        if (amendment.Scratchpad2 is not null)
        {
            ac.Scratchpad2 = amendment.Scratchpad2;
        }
        if (amendment.BeaconCode is not null)
        {
            ac.BeaconCode = amendment.BeaconCode.Value;
            ac.AssignedBeaconCode = amendment.BeaconCode.Value;
        }

        // Resolve ground layout if departure/destination changed
        if (amendment.Departure is not null || amendment.Destination is not null)
        {
            ac.GroundLayout = ResolveGroundLayout(ac);
        }
    }

    public AirportGroundLayout? ResolveGroundLayout(AircraftState aircraft)
    {
        var depLayout = string.IsNullOrEmpty(aircraft.Departure) ? null : _groundData.GetLayout(aircraft.Departure);
        var destLayout = string.IsNullOrEmpty(aircraft.Destination) ? null : _groundData.GetLayout(aircraft.Destination);

        if (depLayout is null)
        {
            return destLayout;
        }

        if (destLayout is null || destLayout == depLayout)
        {
            return depLayout;
        }

        var depNode = depLayout.FindNearestNode(aircraft.Latitude, aircraft.Longitude);
        var destNode = destLayout.FindNearestNode(aircraft.Latitude, aircraft.Longitude);

        double depDist = depNode is not null
            ? GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, depNode.Latitude, depNode.Longitude)
            : double.MaxValue;
        double destDist = destNode is not null
            ? GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, destNode.Latitude, destNode.Longitude)
            : double.MaxValue;

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

            for (int i = aircraft.DeferredDispatches.Count - 1; i >= 0; i--)
            {
                var d = aircraft.DeferredDispatches[i];

                if (d.IsDistanceBased)
                {
                    double distNm = aircraft.GroundSpeed * deltaSeconds / 3600.0;
                    d.RemainingDistanceNm -= distNm;
                    if (d.RemainingDistanceNm > 0)
                    {
                        continue;
                    }
                }
                else
                {
                    d.RemainingSeconds -= deltaSeconds;
                    if (d.RemainingSeconds > 0)
                    {
                        continue;
                    }
                }

                aircraft.DeferredDispatches.RemoveAt(i);
                var groundLayout = aircraft.GroundLayout ?? ResolveGroundLayout(aircraft);
                CommandDispatcher.DispatchCompound(
                    d.Payload,
                    aircraft,
                    groundLayout,
                    World.Rng,
                    Scenario?.ValidateDctFixes ?? true,
                    Scenario?.AutoCrossRunway ?? false
                );
            }
        }
    }

    private void EmitTerminal(string kind, string callsign, string message)
    {
        _terminalEntries.Add(new TerminalEntry(kind, callsign, message));
    }

    private HashSet<int> BuildOccupiedHoldShortNodes()
    {
        var occupied = new HashSet<int>();
        foreach (var ac in World.GetSnapshot())
        {
            if (ac.Phases?.CurrentPhase is HoldingShortPhase hs)
            {
                occupied.Add(hs.HoldShort.NodeId);
                continue;
            }

            // Aircraft holding after runway exit are also occupying their hold-short node
            if ((ac.Phases?.CurrentPhase is HoldingAfterExitPhase) && (ac.GroundSpeed < 1) && (ac.GroundLayout is not null))
            {
                var nearestNode = ac.GroundLayout.FindNearestNode(ac.Latitude, ac.Longitude);
                if (nearestNode is not null && nearestNode.Type == GroundNodeType.RunwayHoldShort)
                {
                    double dist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, nearestNode.Latitude, nearestNode.Longitude);
                    if (dist < 0.02)
                    {
                        occupied.Add(nearestNode.Id);
                    }
                }
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
        var groundLayout = aircraft.GroundLayout ?? ResolveGroundLayout(aircraft);
        var occupiedNodes = _occupiedHoldShortNodes;

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = cat,
            DeltaSeconds = deltaSeconds,
            Logger = _logger,
            Runway = runway,
            FieldElevation = runway?.ElevationFt ?? 0,
            GroundLayout = groundLayout,
            Weather = World.Weather,
            ScenarioElapsedSeconds = Scenario?.ElapsedSeconds ?? 0,
            AutoClearedToLand = Scenario?.AutoClearedToLand ?? false,
            IsHoldShortNodeOccupied = occupiedNodes is not null ? nodeId => occupiedNodes.Contains(nodeId) : null,
        };

        PhaseRunner.Tick(aircraft, ctx);
    }

    private void ProcessDelayedSpawns(List<AircraftState> spawned)
    {
        var scenario = Scenario!;
        for (int i = scenario.DelayedQueue.Count - 1; i >= 0; i--)
        {
            var entry = scenario.DelayedQueue[i];
            if (scenario.ElapsedSeconds >= entry.SpawnAtSeconds)
            {
                scenario.DelayedQueue.RemoveAt(i);
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
    }

    private void ProcessGenerators(List<AircraftState> spawned)
    {
        var scenario = Scenario!;

        foreach (var gen in scenario.Generators)
        {
            if (gen.IsExhausted)
            {
                continue;
            }

            if (scenario.ElapsedSeconds < gen.NextSpawnSeconds)
            {
                continue;
            }

            if (scenario.ElapsedSeconds > gen.Config.MaxTime)
            {
                gen.IsExhausted = true;
                _logger.LogInformation(
                    "Generator '{Id}' exhausted at t={T}s (maxTime={MaxTime})",
                    gen.Config.Id,
                    scenario.ElapsedSeconds,
                    gen.Config.MaxTime
                );
                continue;
            }

            var weight = ResolveWeight(gen.Config, World.Rng);
            var engine = ResolveEngine(gen.Config.EngineType);

            var request = new SpawnRequest
            {
                Rules = FlightRulesKind.Ifr,
                Weight = weight,
                Engine = engine,
                PositionType = SpawnPositionType.OnFinal,
                RunwayId = gen.Config.Runway,
                FinalDistanceNm = gen.NextSpawnDistance,
            };

            var existing = World.GetSnapshot();
            var groundLayout = scenario.PrimaryAirportId is not null ? _groundData.GetLayout(scenario.PrimaryAirportId) : null;
            var (state, error) = AircraftGenerator.Generate(request, scenario.PrimaryAirportId, existing, groundLayout, World.Rng);

            if (state is null)
            {
                _logger.LogWarning("Generator '{Id}' spawn failed at t={T}s: {Error}", gen.Config.Id, scenario.ElapsedSeconds, error);
                AdvanceGenerator(gen, World.Rng);
                continue;
            }

            state.ScenarioId = scenario.ScenarioId;
            state.Destination = scenario.PrimaryAirportId ?? "";
            state.GroundLayout = groundLayout;

            World.AddAircraft(state);
            spawned.Add(state);

            EmitTerminal("System", state.Callsign, $"[Spawn] Generated ({gen.Config.Id})");

            _logger.LogInformation(
                "Generator '{Id}' spawned {Callsign} ({Type}) at {Dist}nm on RWY {Runway}, t={T}s",
                gen.Config.Id,
                state.Callsign,
                state.AircraftType,
                gen.NextSpawnDistance,
                gen.Config.Runway,
                scenario.ElapsedSeconds
            );

            AdvanceGenerator(gen, World.Rng);
        }
    }

    private static void AdvanceGenerator(GeneratorState gen, Random rng)
    {
        var interval = (double)gen.Config.IntervalTime;
        if (gen.Config.RandomizeInterval)
        {
            double jitter = interval * 0.25;
            interval += (rng.NextDouble() * 2 - 1) * jitter;
            interval = Math.Max(interval, 30);
        }

        gen.NextSpawnSeconds += interval;

        gen.NextSpawnDistance += gen.Config.IntervalDistance;
        if (gen.NextSpawnDistance > gen.Config.MaxDistance)
        {
            gen.NextSpawnDistance = gen.Config.InitialDistance;
        }
    }

    private static WeightClass ResolveWeight(ScenarioGeneratorConfig config, Random rng)
    {
        if (config.RandomizeWeightCategory)
        {
            var roll = rng.NextDouble();
            return roll switch
            {
                < 0.15 => WeightClass.Small,
                < 0.85 => WeightClass.Large,
                _ => WeightClass.Heavy,
            };
        }

        return config.WeightCategory switch
        {
            "Small" => WeightClass.Small,
            "SmallPlus" => WeightClass.Large,
            "Heavy" => WeightClass.Heavy,
            _ => WeightClass.Large,
        };
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

            var timedResult = CommandParser.ParseCompound(preset.Command, aircraft.Route);
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
            // Check for single SAY command — emit as Say terminal entry, don't dispatch
            if (compound.Blocks is [{ Commands: [SayCommand timedSay], Condition: null }])
            {
                EmitTerminal("Say", preset.Callsign, timedSay.Text);
                continue;
            }

            var groundLayout = aircraft.GroundLayout ?? ResolveGroundLayout(aircraft);
            CommandDispatcher.DispatchCompound(compound, aircraft, groundLayout, World.Rng, scenario.ValidateDctFixes, scenario.AutoCrossRunway);

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
                    ac.BeaconCode = ac.AssignedBeaconCode;
                    break;
                case SquawkNormalAllCommand:
                    ac.TransponderMode = "C";
                    break;
                case SquawkStandbyAllCommand:
                    ac.TransponderMode = "Standby";
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

    public void DispatchPresetCommands(LoadedAircraft loaded)
    {
        var scenario = Scenario!;

        // Ensure destination is set from scenario primary airport for arrivals
        if (string.IsNullOrWhiteSpace(loaded.State.Destination) && !string.IsNullOrWhiteSpace(scenario.PrimaryAirportId))
        {
            loaded.State.Destination = scenario.PrimaryAirportId;
        }

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
                continue;
            }

            var presetResult = CommandParser.ParseCompound(preset.Command, loaded.State.Route);
            if (!presetResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Preset parse failed for {Callsign}: \"{Command}\" — {Reason}",
                    loaded.State.Callsign,
                    preset.Command,
                    presetResult.Reason
                );
                EmitTerminal("Warning", loaded.State.Callsign, $"[Preset] Unparseable: {preset.Command}");
                continue;
            }

            var compound = presetResult.Value!;
            // Check for single SAY command — emit as Say terminal entry, don't dispatch
            if (compound.Blocks is [{ Commands: [SayCommand say], Condition: null }])
            {
                EmitTerminal("Say", loaded.State.Callsign, say.Text);
                continue;
            }

            var groundLayout = loaded.State.GroundLayout ?? ResolveGroundLayout(loaded.State);
            CommandDispatcher.DispatchCompound(compound, loaded.State, groundLayout, World.Rng, scenario.ValidateDctFixes, scenario.AutoCrossRunway);

            EmitTerminal("System", loaded.State.Callsign, $"[Preset] {preset.Command}");
        }
    }

    // --- Replay helpers ---

    private void ApplyRecordedAction(RecordedAction action)
    {
        switch (action)
        {
            case RecordedCommand cmd:
                ReplayCommand(cmd);
                break;
            case RecordedAmendFlightPlan amend:
                AmendFlightPlan(amend.Callsign, amend.Amendment);
                break;
            case RecordedWeatherChange weather:
                if (weather.WeatherJson is not null)
                {
                    ApplyWeatherJson(weather.WeatherJson);
                }
                else
                {
                    World.Weather = null;
                    if (Scenario is not null)
                    {
                        Scenario.WeatherTimeline = null;
                    }
                }
                break;
            case RecordedSettingChange setting:
                ApplySettingChange(setting);
                break;
        }
    }

    private void ReplayCommand(RecordedCommand cmd)
    {
        var aircraft = FindAircraft(cmd.Callsign);
        if (aircraft is null)
        {
            return;
        }

        var simpleResult = CommandParser.Parse(cmd.Command);
        if (!simpleResult.IsSuccess || simpleResult.Value is SayCommand or ShowQueuedCommand)
        {
            return;
        }

        var simpleParsed = simpleResult.Value!;
        if (simpleParsed is DeleteQueuedCommand delAtCmd)
        {
            ReplayDeleteQueued(aircraft, delAtCmd.BlockNumber);
            return;
        }

        if (simpleParsed is DeleteCommand)
        {
            World.RemoveAircraft(cmd.Callsign);
            return;
        }

        // Skip track commands (server-only: ownership, handoffs, scratchpads, etc.)
        // For complete snapshots, use ReplayWithSnapshots with the server's action applier.
        if (IsTrackCommand(simpleParsed))
        {
            return;
        }

        // Skip coordination commands (server-only)
        if (IsCoordinationCommand(simpleParsed))
        {
            return;
        }

        if (simpleParsed is ConsolidateCommand or DeconsolidateCommand)
        {
            return;
        }

        if (simpleParsed is AcceptAllHandoffsCommand or InitiateHandoffAllCommand)
        {
            return;
        }

        if (simpleParsed is SpawnNowCommand)
        {
            HandleSpawnNow(cmd.Callsign);
            return;
        }

        if (simpleParsed is SpawnDelayCommand spawnDelay)
        {
            HandleSpawnDelay(cmd.Callsign, spawnDelay.Seconds);
            return;
        }

        if (simpleParsed is SquawkAllCommand or SquawkNormalAllCommand or SquawkStandbyAllCommand)
        {
            HandleGlobalSquawkCommand(simpleParsed);
            return;
        }

        var replayResult = CommandParser.ParseCompound(cmd.Command, aircraft.Route);
        if (!replayResult.IsSuccess)
        {
            return;
        }

        var groundLayout = aircraft.GroundLayout ?? ResolveGroundLayout(aircraft);
        CommandDispatcher.DispatchCompound(
            replayResult.Value!,
            aircraft,
            groundLayout,
            World.Rng,
            Scenario?.ValidateDctFixes ?? true,
            Scenario?.AutoCrossRunway ?? false
        );
    }

    private static bool IsTrackCommand(ParsedCommand cmd) => TrackEngine.IsTrackCommand(cmd);

    private static bool IsCoordinationCommand(ParsedCommand cmd) => TrackEngine.IsCoordinationCommand(cmd);

    private void HandleSpawnNow(string callsign)
    {
        var scenario = Scenario!;
        var entry = scenario.DelayedQueue.FirstOrDefault(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        scenario.DelayedQueue.Remove(entry);
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
                }
                break;
        }
    }

    private void ApplyWeatherJson(string weatherJson)
    {
        var parseResult = WeatherTimelineParser.Parse(weatherJson);
        if (parseResult.IsTimeline)
        {
            if (Scenario is not null)
            {
                Scenario.WeatherTimeline = parseResult.Timeline;
            }
            World.Weather = parseResult.Timeline!.GetWeatherAt(Scenario?.ElapsedSeconds ?? 0);
        }
        else if (parseResult.IsProfile)
        {
            if (Scenario is not null)
            {
                Scenario.WeatherTimeline = null;
            }
            World.Weather = parseResult.Profile;
        }
    }

    private static void ReplayDeleteQueued(AircraftState aircraft, int? blockNumber)
    {
        var queue = aircraft.Queue;
        int pendingStart = queue.CurrentBlockIndex + 1;
        int pendingCount = queue.Blocks.Count - pendingStart;

        if (pendingCount <= 0)
        {
            return;
        }

        if (blockNumber is null)
        {
            queue.Blocks.RemoveRange(pendingStart, pendingCount);
        }
        else
        {
            int idx = blockNumber.Value;
            if (idx >= 1 && idx <= pendingCount)
            {
                queue.Blocks.RemoveAt(pendingStart + idx - 1);
            }
        }
    }
}
