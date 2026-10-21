using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The client's side of the traffic-advisory matcher's input: each aircraft the client holds as the
/// <see cref="TrafficAdvisoryCandidate"/> the sim builds from its own state (<see cref="TrafficAdvisoryCandidate.From"/>), read
/// from the values the server sends. The maneuvering flag and the pattern position are the sim's own, never re-derived here.
/// </summary>
public static class AdvisoryCandidates
{
    /// <summary>
    /// The candidate for <paramref name="aircraft"/>: its true heading and track, its altitude, both type forms, the sim's
    /// maneuvering flag and pattern position, and its assigned runway as a designator (<c>8R</c> reads <c>08R</c>), null when it
    /// has none.
    /// </summary>
    public static TrafficAdvisoryCandidate From(IMenuAircraft aircraft) =>
        new(
            aircraft.Callsign,
            aircraft.Position,
            aircraft.HeadingDegrees,
            aircraft.TrueTrack,
            aircraft.AltitudeFeet,
            aircraft.AircraftType,
            aircraft.BaseAircraftType,
            aircraft.IsManeuvering,
            string.IsNullOrWhiteSpace(aircraft.AssignedRunway) ? null : RunwayIdentifier.NormalizeDesignator(aircraft.AssignedRunway),
            aircraft.PatternPosition
        );
}
