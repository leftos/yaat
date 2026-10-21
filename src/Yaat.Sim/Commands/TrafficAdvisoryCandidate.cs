using Yaat.Sim.Phases;

namespace Yaat.Sim.Commands;

/// <summary>
/// Where an aircraft sits in a traffic pattern: the leg it is flying, the side that leg is on (null on final, where a
/// straight-in has no left or right), and its distance to the threshold of its assigned runway.
/// </summary>
public sealed record PatternPosition(PatternEntryLeg Leg, PatternDirection? Side, double DistanceNm);

/// <summary>
/// The fields of one aircraft that <see cref="TrafficAdvisoryMatcher"/> reads, as a plain record so the server's dispatch
/// (over <see cref="AircraftState"/>) and the client (over the training-hub aircraft DTO) run the same matcher.
/// <paramref name="AssignedRunwayId"/> is the assigned runway's designator ("28R"), null when none is assigned;
/// <paramref name="PatternPosition"/> is null when the aircraft is not flying a pattern or final-approach phase.
/// </summary>
public sealed record TrafficAdvisoryCandidate(
    string Callsign,
    LatLon Position,
    double TrueHeadingDeg,
    double TrueTrackDeg,
    double AltitudeFt,
    string AircraftType,
    string BaseAircraftType,
    bool IsManeuvering,
    string? AssignedRunwayId,
    PatternPosition? PatternPosition
)
{
    /// <summary>Builds the candidate the matcher sees for <paramref name="aircraft"/>.</summary>
    public static TrafficAdvisoryCandidate From(AircraftState aircraft) =>
        new(
            aircraft.Callsign,
            aircraft.Position,
            aircraft.TrueHeading.Degrees,
            aircraft.TrueTrack.Degrees,
            aircraft.Altitude,
            aircraft.AircraftType,
            aircraft.BaseAircraftType,
            TrafficAdvisoryMatcher.IsManeuvering(aircraft.BankAngle),
            aircraft.Phases?.AssignedRunway?.Designator,
            PatternPositionOf(aircraft)
        );

    /// <summary>
    /// Where <paramref name="aircraft"/> sits in the traffic pattern of its assigned runway; null when it has no assigned
    /// runway or is not flying a pattern or final-approach phase.
    /// </summary>
    public static PatternPosition? PatternPositionOf(AircraftState aircraft) =>
        aircraft.Phases?.AssignedRunway is { } runway ? PatternCommandHandler.ClassifyPatternPosition(aircraft, runway) : null;

    /// <summary>Builds a candidate for every aircraft in <paramref name="aircraft"/>; null stays null (the list is unavailable).</summary>
    public static IReadOnlyList<TrafficAdvisoryCandidate>? FromAll(IReadOnlyList<AircraftState>? aircraft) =>
        aircraft is null ? null : [.. aircraft.Select(From)];
}
