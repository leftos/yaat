using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation;

public sealed class DelayedHandoff
{
    public required string Callsign { get; init; }
    public required TrackOwner Target { get; init; }
    public required int FireAtSeconds { get; init; }
}

public sealed class DelayedSpawn
{
    public required LoadedAircraft Aircraft { get; init; }
    public required int SpawnAtSeconds { get; set; }

    /// <summary>
    /// True when this is a not-yet-airborne IFR departure (spawns on the runway / airborne-imminent)
    /// that is eligible to be held for release. While true AND its departure airport is armed for
    /// hold-for-release, <c>ProcessDelayedSpawns</c> skips it regardless of <see cref="SpawnAtSeconds"/>
    /// so it appears on the scope only when released. Set at load via
    /// <see cref="Scenarios.DepartureSpawnClassifier"/>; the runtime armed-airport check decides whether
    /// it is actually held.
    /// </summary>
    public bool HeldForRelease { get; set; }
}

/// <summary>
/// A scheduled hold-for-release release (one entry per departure when an airport's whole held queue
/// is released auto-spaced by an interval). Fired by <c>ProcessReleaseQueue</c> against
/// <c>ElapsedSeconds</c>, mirroring <see cref="ScheduledPreset"/>.
/// </summary>
public sealed class ScheduledRelease
{
    public required string Airport { get; init; }

    /// <summary>Specific callsign to release, or null to release the next-pending held entry at <see cref="Airport"/>.</summary>
    public string? Callsign { get; init; }

    public required double FireAtSeconds { get; init; }
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
