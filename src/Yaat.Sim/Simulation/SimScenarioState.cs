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
}
