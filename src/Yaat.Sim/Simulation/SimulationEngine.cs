using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation;

public sealed class SimulationEngine
{
    private const int PhysicsSubTickRate = 4;

    private readonly IFixLookup _fixes;
    private readonly IRunwayLookup _runways;
    private readonly IAirportGroundData _groundData;
    private readonly IApproachLookup? _approachLookup;
    private readonly IProcedureLookup? _procedureLookup;
    private readonly ILogger _logger;

    public SimulationWorld World { get; } = new();
    public SimScenarioState? Scenario { get; set; }
    public ConsolidationState ConsolidationState { get; } = new();
    public ApproachEvaluator ApproachEvaluator { get; } = new();
    public BeaconCodePool BeaconCodePool { get; } = new();
    public TowerListTracker TowerListTracker { get; } = new();
    public ConflictAlertState ConflictAlerts { get; } = new();

    public SimulationEngine(
        IFixLookup fixes,
        IRunwayLookup runways,
        IAirportGroundData groundData,
        IApproachLookup? approachLookup = null,
        IProcedureLookup? procedureLookup = null,
        ILogger? logger = null
    )
    {
        _fixes = fixes;
        _runways = runways;
        _groundData = groundData;
        _approachLookup = approachLookup;
        _procedureLookup = procedureLookup;
        _logger = logger ?? SimLog.CreateLogger<SimulationEngine>();
    }

    public List<string> LoadScenario(string json, int rngSeed)
    {
        World.Clear();
        World.Rng = new Random(rngSeed);

        var result = ScenarioLoader.Load(json, _fixes, _runways, _groundData, World.Rng);

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
            var runway = _runways.GetRunway(result.PrimaryAirportId ?? "", genConfig.Runway);
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

    public void TickOneSecond()
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        scenario.ElapsedSeconds += 1;

        ProcessDelayedSpawns();
        ProcessTimedPresets();
        ProcessTriggers();

        // Ensure ground layout is set
        if (scenario.PrimaryAirportId is not null && World.GroundLayout is null)
        {
            World.GroundLayout = _groundData.GetLayout(scenario.PrimaryAirportId);
        }

        double subDelta = 1.0 / PhysicsSubTickRate;
        for (int sub = 0; sub < PhysicsSubTickRate; sub++)
        {
            World.Tick(subDelta, PreTick);
        }

        // Drain warnings/notifications to prevent accumulation
        World.DrainAllWarnings();
        World.DrainAllNotifications();
        World.DrainAllApproachScores();
    }

    public void Replay(SessionRecording recording, double targetSeconds)
    {
        LoadScenario(recording.ScenarioJson, recording.RngSeed);

        // Apply weather if present
        if (recording.WeatherJson is not null)
        {
            var profile = JsonSerializer.Deserialize<WeatherProfile>(
                recording.WeatherJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
            if (profile is not null)
            {
                World.Weather = profile;
            }
        }

        // Apply actions at t=0 first (settings, immediate commands)
        var actions = recording.Actions;
        int actionCursor = 0;
        while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= 0)
        {
            ApplyRecordedAction(actions[actionCursor]);
            actionCursor++;
        }

        // Replay each second
        int target = (int)targetSeconds;
        for (int t = 1; t <= target; t++)
        {
            Scenario!.ElapsedSeconds = t;

            ProcessDelayedSpawns();
            ProcessTimedPresets();
            ProcessTriggers();

            if (Scenario.PrimaryAirportId is not null && World.GroundLayout is null)
            {
                World.GroundLayout = _groundData.GetLayout(Scenario.PrimaryAirportId);
            }

            double subDelta = 1.0 / PhysicsSubTickRate;
            for (int sub = 0; sub < PhysicsSubTickRate; sub++)
            {
                World.Tick(subDelta, PreTick);
            }

            World.DrainAllWarnings();
            World.DrainAllNotifications();
            World.DrainAllApproachScores();

            // Apply actions at this time
            while (actionCursor < actions.Count && actions[actionCursor].ElapsedSeconds <= t)
            {
                ApplyRecordedAction(actions[actionCursor]);
                actionCursor++;
            }
        }
    }

    public CommandResult SendCommand(string callsign, string command)
    {
        var aircraft = FindAircraft(callsign);
        if (aircraft is null)
        {
            return new CommandResult(false, $"Aircraft '{callsign}' not found");
        }

        var compound = CommandParser.ParseCompound(command, _fixes, aircraft.Route);
        if (compound is null)
        {
            return new CommandResult(false, $"Failed to parse command: {command}");
        }

        var groundLayout = aircraft.GroundLayout ?? ResolveGroundLayout(aircraft);
        return CommandDispatcher.DispatchCompound(
            compound,
            aircraft,
            _runways,
            groundLayout,
            _fixes,
            World.Rng,
            _approachLookup,
            _procedureLookup,
            Scenario?.ValidateDctFixes ?? true,
            Scenario?.AutoCrossRunway ?? false
        );
    }

    public AircraftState? FindAircraft(string callsign)
    {
        return World.GetSnapshot().FirstOrDefault(a => a.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
    }

    // --- Private methods ---

    private void PreTick(AircraftState aircraft, double deltaSeconds)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return;
        }

        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        var runway = aircraft.Phases.AssignedRunway;
        var groundLayout = aircraft.GroundLayout ?? ResolveGroundLayout(aircraft);

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
        };

        PhaseRunner.Tick(aircraft, ctx);
    }

    private AirportGroundLayout? ResolveGroundLayout(AircraftState aircraft)
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

    private void ProcessDelayedSpawns()
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
            }
        }
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

            var parsed = CommandParser.Parse(preset.Command);
            if (parsed is null or SayCommand)
            {
                continue;
            }

            var groundLayout = aircraft.GroundLayout ?? ResolveGroundLayout(aircraft);
            CommandDispatcher.Dispatch(
                parsed,
                aircraft,
                _runways,
                groundLayout,
                _fixes,
                World.Rng,
                _approachLookup,
                autoCrossRunway: scenario.AutoCrossRunway
            );
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
        var parsed = CommandParser.Parse(command);
        if (parsed is null)
        {
            return;
        }

        if (parsed is SquawkAllCommand or SquawkNormalAllCommand or SquawkStandbyAllCommand)
        {
            HandleGlobalSquawkCommand(parsed);
        }
    }

    private void HandleGlobalSquawkCommand(ParsedCommand command)
    {
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
        }
    }

    private void DispatchPresetCommands(LoadedAircraft loaded)
    {
        var scenario = Scenario!;
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

            var parsed = CommandParser.Parse(preset.Command);
            if (parsed is null or SayCommand)
            {
                continue;
            }

            var groundLayout = loaded.State.GroundLayout ?? ResolveGroundLayout(loaded.State);
            CommandDispatcher.Dispatch(
                parsed,
                loaded.State,
                _runways,
                groundLayout,
                _fixes,
                World.Rng,
                _approachLookup,
                autoCrossRunway: scenario.AutoCrossRunway
            );
        }
    }

    private void ApplyRecordedAction(RecordedAction action)
    {
        switch (action)
        {
            case RecordedCommand cmd:
                ReplayCommand(cmd);
                break;
            case RecordedSpawn spawn:
                ReplaySpawn(spawn.Args);
                break;
            case RecordedDelete del:
                World.RemoveAircraft(del.Callsign);
                break;
            case RecordedWarp warp:
                WarpAircraft(warp.Callsign, warp.Latitude, warp.Longitude, warp.Heading);
                break;
            case RecordedAmendFlightPlan amend:
                AmendFlightPlan(amend.Callsign, amend.Amendment);
                break;
            case RecordedWeatherChange weather:
                if (weather.WeatherJson is not null)
                {
                    var profile = JsonSerializer.Deserialize<WeatherProfile>(
                        weather.WeatherJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (profile is not null)
                    {
                        World.Weather = profile;
                    }
                }
                else
                {
                    World.Weather = null;
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

        var simpleParsed = CommandParser.Parse(cmd.Command);
        if (simpleParsed is null or SayCommand or ShowQueuedCommand)
        {
            return;
        }

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

        var compound = CommandParser.ParseCompound(cmd.Command, _fixes, aircraft.Route);
        if (compound is null)
        {
            return;
        }

        var groundLayout = aircraft.GroundLayout ?? ResolveGroundLayout(aircraft);
        CommandDispatcher.DispatchCompound(
            compound,
            aircraft,
            _runways,
            groundLayout,
            _fixes,
            World.Rng,
            _approachLookup,
            _procedureLookup,
            Scenario?.ValidateDctFixes ?? true,
            Scenario?.AutoCrossRunway ?? false
        );
    }

    private static bool IsTrackCommand(ParsedCommand cmd) => TrackEngine.IsTrackCommand(cmd);

    private static bool IsCoordinationCommand(ParsedCommand cmd) => TrackEngine.IsCoordinationCommand(cmd);

    private void ReplaySpawn(string args)
    {
        var scenario = Scenario!;
        var (request, _) = SpawnParser.Parse(args);
        if (request is null)
        {
            return;
        }

        var existing = World.GetSnapshot();
        var groundLayout = scenario.PrimaryAirportId is not null ? _groundData.GetLayout(scenario.PrimaryAirportId) : null;
        var (state, _) = AircraftGenerator.Generate(request, scenario.PrimaryAirportId, _fixes, _runways, existing, groundLayout, World.Rng);

        if (state is null)
        {
            return;
        }

        state.ScenarioId = scenario.ScenarioId;
        state.GroundLayout = groundLayout;
        World.AddAircraft(state);
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

    private void WarpAircraft(string callsign, double latitude, double longitude, double heading)
    {
        var aircraft = FindAircraft(callsign);
        if (aircraft is null)
        {
            return;
        }

        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
            aircraft.Phases = null;
        }
        aircraft.AssignedTaxiRoute = null;
        aircraft.Targets.TurnRateOverride = null;
        aircraft.Latitude = latitude;
        aircraft.Longitude = longitude;
        aircraft.Heading = heading;
        aircraft.Track = heading;
    }

    private void AmendFlightPlan(string callsign, FlightPlanAmendment amendment)
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
