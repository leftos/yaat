using System.Diagnostics;
using System.Text;
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
            Scenario.ValidateDctFixes = scenarioDto.ValidateDctFixes;
            Scenario.SoloTrainingMode = scenarioDto.SoloTrainingMode;
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
            Scenario.Generators.Clear();

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

                    Scenario.DelayedQueue.Add(new DelayedSpawn { Aircraft = aircraft, SpawnAtSeconds = d.SpawnAtSeconds });
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
                            NextSpawnDistance = g.NextSpawnDistance,
                            IsExhausted = g.IsExhausted,
                        }
                    );
                }
            }
        }

        // Reset engine-level state, then restore from snapshot if available
        ConsolidationState.Clear();
        ConflictAlerts.Conflicts.Clear();
        BeaconCodePool.Clear();

        if (snapshot.Server is not null)
        {
            RestoreServerSnapshot(snapshot.Server);
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
            BeaconCodePool = new BeaconCodePoolDto { AssignedCodes = beaconCodes },
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

        if (server.BeaconCodePool?.AssignedCodes is not null)
        {
            foreach (var code in server.BeaconCodePool.AssignedCodes.Keys)
            {
                BeaconCodePool.MarkUsed(code);
            }
        }
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
        var sw = Stopwatch.StartNew();
        _occupiedHoldShortNodes = BuildOccupiedHoldShortNodes();
        AccumulateTiming("Physics.BuildHoldShort", sw);

        sw.Restart();
        World.Tick(delta, PreTick, RecordWorldTiming);
        AccumulateTiming("Physics.WorldTick", sw);

        _occupiedHoldShortNodes = null;

        sw.Restart();
        ProcessDeferredDispatches(delta);
        AccumulateTiming("Physics.Deferred", sw);
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
        if (Scenario is { SoloTrainingMode: true } scenario)
        {
            foreach (var ac in World.GetSnapshot())
            {
                Pilot.PilotProactive.TickAirborneCheckIn(ac, scenario, LookupAirportPosition);
            }
        }

        World.DrainAllWarnings();

        var notifications = World.DrainAllNotifications();
        foreach (var (callsign, notification) in notifications)
        {
            EmitTerminal("Response", callsign, notification);
        }

        World.DrainAllApproachScores();
    }

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
        var sw = new Stopwatch();
        for (int t = startSeconds + 1; t <= targetSeconds; t++)
        {
            Scenario!.ElapsedSeconds = t;

            sw.Restart();
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
                actionApplier(actions[actionCursor]);
                actionCursor++;
            }

            FireTickCompleted(t);
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
        TickTimings.Clear();
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

        var sw = new Stopwatch();

        sw.Restart();
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
            ApplyRecordedAction(_replayActions[_replayActionCursor]);
            _replayActionCursor++;
        }

        FireTickCompleted(t);
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

        if (atSecondStart)
        {
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
                ApplyRecordedAction(_replayActions[_replayActionCursor]);
                _replayActionCursor++;
            }

            FireTickCompleted(t);
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

        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var dispatchCtx = new DispatchContext(
            groundLayout,
            World.Rng,
            World.Weather,
            FindAircraft,
            Scenario?.ValidateDctFixes ?? true,
            Scenario?.AutoCrossRunway ?? false,
            Scenario?.SoloTrainingMode ?? false,
            _terminalEntries.Add
        );
        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, aircraft, dispatchCtx);

        // Emit pilot readback in solo-training mode. Single hook here in SendCommand (the
        // user-issued live path) means deferred / preset / replay dispatches don't re-fire
        // readbacks. Transparent (squawk/ident/say) and phase-handled paths all funnel
        // through DispatchCompound, so this catches everything successful from the student's
        // perspective.
        if (result.Success && dispatchCtx.SoloTrainingMode)
        {
            var readback = Yaat.Sim.Pilot.PilotResponder.BuildReadback(parseResult.Value!, aircraft);
            if (!string.IsNullOrEmpty(readback))
            {
                aircraft.PendingNotifications.Add(readback);
            }
        }

        return result;
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
        aircraft.Ground.IsHeld = false;
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
            ac.Transponder.Code = amendment.BeaconCode.Value;
            ac.Transponder.AssignedCode = amendment.BeaconCode.Value;
        }

        // Resolve ground layout if departure/destination changed
        if (amendment.Departure is not null || amendment.Destination is not null)
        {
            ac.Ground.Layout = ResolveGroundLayout(ac);
        }

        // Bump the revision counter so the strip can render the new value.
        // CRC displays revision regardless of which fields changed — the counter
        // is a "has been edited" signal, not a per-field diff.
        ac.FlightPlan.RevisionNumber++;
    }

    public AirportGroundLayout? ResolveGroundLayout(AircraftState aircraft)
    {
        var depLayout = string.IsNullOrEmpty(aircraft.FlightPlan.Departure) ? null : _groundData.GetLayout(aircraft.FlightPlan.Departure);
        var destLayout = string.IsNullOrEmpty(aircraft.FlightPlan.Destination) ? null : _groundData.GetLayout(aircraft.FlightPlan.Destination);

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

            for (int i = aircraft.DeferredDispatches.Count - 1; i >= 0; i--)
            {
                var d = aircraft.DeferredDispatches[i];

                if (d.GiveWayTarget is not null)
                {
                    if (!IsGiveWayDeferredMet(aircraft, d.GiveWayTarget))
                    {
                        continue;
                    }

                    // Condition met — clear the aircraft's give-way hold state
                    aircraft.Ground.GiveWayTarget = null;
                    aircraft.Ground.IsHeld = false;
                }
                else if (d.IsDistanceBased)
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

                var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
                var deferredCtx = new DispatchContext(
                    groundLayout,
                    World.Rng,
                    World.Weather,
                    FindAircraft,
                    Scenario?.ValidateDctFixes ?? true,
                    Scenario?.AutoCrossRunway ?? false,
                    Scenario?.SoloTrainingMode ?? false,
                    _terminalEntries.Add
                );
                CommandDispatcher.DispatchCompound(d.Payload, aircraft, deferredCtx);
            }
        }
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
            FieldElevation = runway?.ElevationFt ?? 0,
            GroundLayout = groundLayout,
            Weather = World.Weather,
            ScenarioElapsedSeconds = Scenario?.ElapsedSeconds ?? 0,
            AutoClearedToLand = Scenario?.AutoClearedToLand ?? false,
            SoloTrainingMode = Scenario?.SoloTrainingMode ?? false,
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

        if (spawned.Count > 0 && scenario.DelayedQueue.Count == 0)
        {
            EmitTerminal("System", "", "[Scenario] No delayed spawns left");
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
            state.FlightPlan.Destination = scenario.PrimaryAirportId ?? "";
            state.Ground.Layout = groundLayout;

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

            var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
            var presetCtx = new DispatchContext(
                groundLayout,
                World.Rng,
                World.Weather,
                FindAircraft,
                scenario.ValidateDctFixes,
                scenario.AutoCrossRunway,
                scenario.SoloTrainingMode,
                _terminalEntries.Add
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

        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var singlePresetCtx = new DispatchContext(
            groundLayout,
            World.Rng,
            World.Weather,
            FindAircraft,
            Scenario!.ValidateDctFixes,
            Scenario!.AutoCrossRunway,
            Scenario!.SoloTrainingMode,
            _terminalEntries.Add
        );
        CommandDispatcher.DispatchCompound(compound, aircraft, singlePresetCtx);

        EmitTerminal("System", aircraft.Callsign, $"[Preset] {command}");
    }

    public void DispatchPresetCommands(LoadedAircraft loaded)
    {
        var scenario = Scenario!;

        PresetOverride?.Invoke(loaded);

        // Ensure destination is set from scenario primary airport for arrivals
        if (string.IsNullOrWhiteSpace(loaded.State.FlightPlan.Destination) && !string.IsNullOrWhiteSpace(scenario.PrimaryAirportId))
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

        // CFIX preset composition: when the first preset is a CFIX command and
        // there are multiple presets, compose them into a single compound command.
        // Without this, each DispatchCompound call clears conflicting dimensions
        // from the previous (e.g. CAPP clears CFIX's lateral+vertical, losing
        // the speed target). Composing keeps them as sequential blocks in one queue.
        if (immediatePresets.Count >= 2 && immediatePresets[0].TrimStart().StartsWith("CFIX ", StringComparison.OrdinalIgnoreCase))
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
        // Single-command parse handles type-specific shortcuts (DEL, track, spawn,
        // global squawk, etc.) that bypass the compound dispatch path. Compound
        // commands like "DCT VPCOL; ERD 28R" intentionally fail this parse — they
        // fall through to ParseCompound below.
        var simpleResult = CommandParser.Parse(cmd.Command);
        var simpleParsed = simpleResult.IsSuccess ? simpleResult.Value : null;

        if (simpleParsed is SayCommand or ShowQueuedCommand)
        {
            return;
        }

        // DEL before the aircraft-exists guard: target may be in the delayed queue only.
        if (simpleParsed is DeleteCommand)
        {
            Scenario?.DelayedQueue.RemoveAll(e => e.Aircraft.State.Callsign.Equals(cmd.Callsign, StringComparison.OrdinalIgnoreCase));
            World.RemoveAircraft(cmd.Callsign);
            return;
        }

        var aircraft = FindAircraft(cmd.Callsign);
        if (aircraft is null)
        {
            return;
        }

        if (simpleParsed is DeleteQueuedCommand delAtCmd)
        {
            ReplayDeleteQueued(aircraft, delAtCmd.BlockNumber);
            return;
        }

        if (simpleParsed is not null)
        {
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
        }

        var replayResult = CommandParser.ParseCompound(cmd.Command, aircraft.FlightPlan.Route);
        if (!replayResult.IsSuccess)
        {
            return;
        }

        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var replayCtx = new DispatchContext(
            groundLayout,
            World.Rng,
            World.Weather,
            FindAircraft,
            Scenario?.ValidateDctFixes ?? true,
            Scenario?.AutoCrossRunway ?? false,
            Scenario?.SoloTrainingMode ?? false,
            _terminalEntries.Add
        );
        CommandDispatcher.DispatchCompound(replayResult.Value!, aircraft, replayCtx);
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
                }
                break;
            case "AutoAcceptDelay":
                if (int.TryParse(setting.Value, out var seconds))
                {
                    scenario.AutoAcceptDelay = seconds < 0 ? TimeSpan.FromSeconds(-1) : TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60));
                }
                break;
            case "AutoDeleteMode":
                // Server writes ClientAutoDeleteOverride, not ScenarioAutoDeleteMode.
                // Null/empty string is a valid value: it means "clear the override and
                // fall back to the scenario default".
                scenario.ClientAutoDeleteOverride = string.IsNullOrEmpty(setting.Value) ? null : setting.Value;
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
