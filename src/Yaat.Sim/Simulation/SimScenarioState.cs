using Yaat.Sim.Scenarios;

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
}
