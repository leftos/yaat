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

/// <summary>
/// A countdown timer scheduled by the TIMER command. Fired by <c>ProcessTimers</c> against
/// <c>ElapsedSeconds</c> (mirroring <see cref="ScheduledRelease"/>): on expiry it emits a green
/// SAY-style terminal entry and is removed. Room-level (lives on <c>SimScenarioState</c>), so it
/// ticks in sim time and survives snapshot round-trips.
/// </summary>
public sealed class ActiveTimer
{
    public required int Id { get; init; }

    /// <summary>Null = global/instructor timer; set = the aircraft the expiry SAY is attributed to.</summary>
    public string? Callsign { get; init; }

    /// <summary>Free-text message; null/empty renders as "timer expired" at fire time.</summary>
    public string? Message { get; init; }

    public required double FireAtSeconds { get; init; }

    /// <summary>Original duration in seconds — drives the countdown panel's total/label only.</summary>
    public required double TotalSeconds { get; init; }
}

public sealed class ScheduledPreset
{
    public required string Callsign { get; init; }
    public required string Command { get; init; }
    public required double FireAtSeconds { get; init; }
}

/// <summary>
/// The runtime cursor every traffic generator carries, so the per-tick activation edge is evaluated once
/// for all generator kinds rather than duplicated per kind.
/// </summary>
public interface IGeneratorRuntimeState
{
    IGeneratorConfig ConfigBase { get; }

    /// <summary>Next scheduled spawn time (<c>IntervalTime</c> cadence; bumped on a no-room defer).</summary>
    double NextSpawnSeconds { get; set; }

    /// <summary>Activation on the previous tick, so a transition is logged once instead of every tick.</summary>
    bool WasActive { get; set; }
}

public sealed class GeneratorState : IGeneratorRuntimeState
{
    public required ScenarioGeneratorConfig Config { get; init; }
    public required RunwayInfo Runway { get; init; }
    public double NextSpawnSeconds { get; set; }
    public bool WasActive { get; set; }

    public IGeneratorConfig ConfigBase => Config;
}

public sealed class VfrArrivalGeneratorState : IGeneratorRuntimeState
{
    public required VfrArrivalGeneratorConfig Config { get; init; }
    public double NextSpawnSeconds { get; set; }
    public bool WasActive { get; set; }

    public IGeneratorConfig ConfigBase => Config;
}

public sealed class OverflightGeneratorState : IGeneratorRuntimeState
{
    public required OverflightGeneratorConfig Config { get; init; }
    public double NextSpawnSeconds { get; set; }
    public bool WasActive { get; set; }

    public IGeneratorConfig ConfigBase => Config;
}
