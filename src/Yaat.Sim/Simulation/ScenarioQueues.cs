using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation;

public sealed class DelayedSpawn
{
    public required LoadedAircraft Aircraft { get; init; }
    public required int SpawnAtSeconds { get; set; }
}

public sealed class ScheduledTrigger
{
    public required string Command { get; init; }
    public required int FireAtSeconds { get; init; }
}

public sealed class ScheduledPreset
{
    public required string Callsign { get; init; }
    public required string Command { get; init; }
    public required double FireAtSeconds { get; init; }
}

public sealed class GeneratorState
{
    public required ScenarioGeneratorConfig Config { get; init; }
    public required RunwayInfo Runway { get; init; }
    public double NextSpawnSeconds { get; set; }
    public double NextSpawnDistance { get; set; }
    public bool IsExhausted { get; set; }
}
