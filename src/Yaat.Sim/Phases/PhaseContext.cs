using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases;

public sealed class PhaseContext
{
    public required AircraftState Aircraft { get; init; }
    public required ControlTargets Targets { get; init; }
    public required AircraftCategory Category { get; init; }

    /// <summary>Shortcut for <c>Aircraft.AircraftType</c>.</summary>
    public string AircraftType => Aircraft.AircraftType;
    public required double DeltaSeconds { get; init; }
    public RunwayInfo? Runway { get; init; }
    public double FieldElevation { get; init; }
    public AirportGroundLayout? GroundLayout { get; init; }

    /// <summary>
    /// Lookup function to find other aircraft by callsign.
    /// Used by FollowingPhase and ground conflict detection.
    /// </summary>
    public Func<string, AircraftState?>? AircraftLookup { get; init; }

    /// <summary>
    /// Logger for phase diagnostics. Provided by the server tick loop.
    /// </summary>
    public required ILogger Logger { get; init; }

    /// <summary>Active weather profile. Null when no weather is loaded.</summary>
    public WeatherProfile? Weather { get; init; }

    /// <summary>Scenario elapsed time in seconds. Used for approach score timestamps.</summary>
    public double ScenarioElapsedSeconds { get; init; }

    /// <summary>When true, aircraft are automatically cleared to land (no CLAND command needed).</summary>
    public bool AutoClearedToLand { get; init; }
}
