namespace Yaat.Sim.Data.Airport;

/// <summary>
/// One aircraft as the tug planner's neighbour check reads it: the plain facts that decide whether it is a parked or held
/// neighbour of a tug move and, if so, how the planner sweeps against it. The simulation fills it from an
/// <see cref="AircraftState"/>; the client fills it from the aircraft as they arrive on the wire, so both plan against
/// the same neighbours.
/// </summary>
public sealed record NeighbourCandidate
{
    /// <summary>Its callsign; tells the moving aircraft apart from itself and names it in a refusal.</summary>
    public required string Callsign { get; init; }

    /// <summary>Where it stands.</summary>
    public required LatLon Position { get; init; }

    /// <summary>Its nose heading, degrees true.</summary>
    public required double TrueHeadingDeg { get; init; }

    /// <summary>ICAO type designator; sets its outline.</summary>
    public required string AircraftType { get; init; }

    /// <summary>The stand it is parked on, or null when it is not on a named stand.</summary>
    public required string? StandName { get; init; }

    /// <summary>It is under a controller hold.</summary>
    public required bool IsImmobile { get; init; }

    /// <summary>Its current phase's name, or null when it has none.</summary>
    public required string? PhaseName { get; init; }

    /// <summary>Its ground speed, knots.</summary>
    public required double GroundSpeedKts { get; init; }

    /// <summary>The speed it is commanding, knots, or null when it commands none.</summary>
    public required double? TargetSpeedKts { get; init; }

    /// <summary>The candidate a simulated aircraft is, where it stands now.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <returns>Its candidate.</returns>
    public static NeighbourCandidate From(AircraftState aircraft) =>
        new()
        {
            Callsign = aircraft.Callsign,
            Position = aircraft.Position,
            TrueHeadingDeg = aircraft.TrueHeading.Degrees,
            AircraftType = aircraft.AircraftType,
            StandName = aircraft.Ground.ParkingSpot,
            IsImmobile = aircraft.Ground.IsImmobile,
            PhaseName = aircraft.Phases?.CurrentPhase?.Name,
            GroundSpeedKts = aircraft.GroundSpeed,
            TargetSpeedKts = aircraft.Targets.TargetSpeed,
        };
}
