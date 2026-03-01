using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases;

public sealed class PhaseContext
{
    public required AircraftState Aircraft { get; init; }
    public required ControlTargets Targets { get; init; }
    public required AircraftCategory Category { get; init; }
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
}
