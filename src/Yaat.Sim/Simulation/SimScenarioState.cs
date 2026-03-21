using System.Text.Json;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

public sealed class SimScenarioState
{
    public required string ScenarioId { get; init; }
    public required string ScenarioName { get; init; }
    public required int RngSeed { get; init; }
    public required string OriginalScenarioJson { get; init; }
    public string? PrimaryAirportId { get; set; }
    public double ElapsedSeconds { get; set; }

    // Queues
    public List<DelayedSpawn> DelayedQueue { get; } = [];
    public List<ScheduledTrigger> TriggerQueue { get; } = [];
    public List<ScheduledPreset> PresetQueue { get; } = [];
    public List<GeneratorState> Generators { get; } = [];

    // Settings affecting command dispatch
    public bool AutoClearedToLand { get; set; }
    public bool AutoCrossRunway { get; set; }
    public bool ValidateDctFixes { get; set; } = true;

    // Weather timeline (v2 time-based weather evolution)
    public WeatherTimeline? WeatherTimeline { get; set; }

    // Scenario metadata
    public string? InitialWeatherJson { get; set; }
    public List<RecordedAction> ActionLog { get; } = [];
    public bool IsPlaybackMode { get; set; }
    public int PlaybackCursor { get; set; }
    public double PlaybackEndSeconds { get; set; }
    public string? ArtccId { get; set; }
    public string? ScenarioAutoDeleteMode { get; set; }
    public string? ClientAutoDeleteOverride { get; set; }
    public string? EffectiveAutoDeleteMode => ClientAutoDeleteOverride ?? ScenarioAutoDeleteMode;

    // Simulation control
    public bool IsPaused { get; set; } = true;
    public double SimRate { get; set; } = 1.0;

    // State snapshots loaded from a v2 recording (null for live sessions and v1 recordings)
    public List<TimedSnapshot>? LoadedSnapshots { get; set; }

    // Handoff tracking
    public List<DelayedHandoff> DelayedHandoffQueue { get; } = [];

    // ATC positions
    public TrackOwner? StudentPosition { get; set; }
    public Tcp? StudentTcp { get; set; }
    public string? StudentPositionType { get; set; }
    public List<ResolvedAtcPosition> AtcPositions { get; set; } = [];

    // Timing and settings
    public TimeSpan AutoAcceptDelay { get; set; } = TimeSpan.FromSeconds(5);
    public bool IsStudentTowerPosition { get; set; }
    public Dictionary<string, CoordinationChannel> CoordinationChannels { get; set; } = [];

    public ScenarioSnapshotDto ToSnapshot() =>
        new()
        {
            ScenarioId = ScenarioId,
            ScenarioName = ScenarioName,
            RngSeed = RngSeed,
            PrimaryAirportId = PrimaryAirportId,
            ElapsedSeconds = ElapsedSeconds,
            AutoClearedToLand = AutoClearedToLand,
            AutoCrossRunway = AutoCrossRunway,
            ValidateDctFixes = ValidateDctFixes,
            IsPaused = IsPaused,
            SimRate = SimRate,
            AutoAcceptDelaySeconds = AutoAcceptDelay.TotalSeconds,
            IsStudentTowerPosition = IsStudentTowerPosition,
            ScenarioAutoDeleteMode = ScenarioAutoDeleteMode,
            ClientAutoDeleteOverride = ClientAutoDeleteOverride,
            ArtccId = ArtccId,
            StudentPosition = StudentPosition?.ToSnapshot(),
            StudentTcp = StudentTcp?.ToSnapshot(),
            StudentPositionType = StudentPositionType,
            DelayedQueue =
                DelayedQueue.Count > 0
                    ? DelayedQueue
                        .Select(d => new DelayedSpawnDto { AircraftJson = JsonSerializer.Serialize(d.Aircraft), SpawnAtSeconds = d.SpawnAtSeconds })
                        .ToList()
                    : null,
            TriggerQueue =
                TriggerQueue.Count > 0
                    ? TriggerQueue.Select(t => new ScheduledTriggerDto { Command = t.Command, FireAtSeconds = t.FireAtSeconds }).ToList()
                    : null,
            PresetQueue =
                PresetQueue.Count > 0
                    ? PresetQueue
                        .Select(p => new ScheduledPresetDto
                        {
                            Callsign = p.Callsign,
                            Command = p.Command,
                            FireAtSeconds = p.FireAtSeconds,
                        })
                        .ToList()
                    : null,
            Generators =
                Generators.Count > 0
                    ? Generators
                        .Select(g => new GeneratorStateDto
                        {
                            ConfigJson = JsonSerializer.Serialize(g.Config),
                            Runway = g.Runway.ToSnapshot(),
                            NextSpawnSeconds = g.NextSpawnSeconds,
                            NextSpawnDistance = g.NextSpawnDistance,
                            IsExhausted = g.IsExhausted,
                        })
                        .ToList()
                    : null,
            DelayedHandoffQueue =
                DelayedHandoffQueue.Count > 0
                    ? DelayedHandoffQueue
                        .Select(h => new DelayedHandoffDto
                        {
                            Callsign = h.Callsign,
                            Target = h.Target.ToSnapshot(),
                            FireAtSeconds = h.FireAtSeconds,
                        })
                        .ToList()
                    : null,
        };
}
