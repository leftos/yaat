using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Data.Airport;

/// <summary>A point of an aircraft's body the hold-line footprint check samples (<see cref="AircraftBody"/>).</summary>
public enum AircraftBodyPart
{
    Nose,
    LeftWingtip,
    RightWingtip,
    Tail,
}

/// <summary>
/// An aircraft's body as the hold-line footprint check sees it (AIM 2-3-5.a.1: no part of the aircraft beyond the holding
/// position marking). The nose and tail lie half the type's length ahead of and behind the main-gear reference position along
/// the heading (<see cref="AircraftLength.ResolveFt"/>), the body <see cref="FollowingPhase.RunwayInsideHoldLine"/> judges;
/// both wingtips lie half the span abeam it, at right angles to the heading (<see cref="AircraftFootprint.ResolveWingspanFt"/>).
/// The horizontal stabiliser is not modelled.
/// </summary>
/// <param name="LengthFt">Fuselage length (ft).</param>
/// <param name="WingspanFt">Wingspan (ft).</param>
public readonly record struct AircraftBody(double LengthFt, double WingspanFt)
{
    /// <summary>Every part, nose first.</summary>
    public static readonly AircraftBodyPart[] AllParts =
    [
        AircraftBodyPart.Nose,
        AircraftBodyPart.LeftWingtip,
        AircraftBodyPart.RightWingtip,
        AircraftBodyPart.Tail,
    ];

    /// <summary>The parts that lead a straight roll: the nose and both wingtips; the tail trails.</summary>
    public static readonly AircraftBodyPart[] LeadingParts = [AircraftBodyPart.Nose, AircraftBodyPart.LeftWingtip, AircraftBodyPart.RightWingtip];

    /// <summary>The body of an ICAO type, from its FAA record and the category fallbacks.</summary>
    /// <param name="aircraftType">ICAO type designator, prefixes and suffixes allowed.</param>
    /// <returns>The body.</returns>
    public static AircraftBody Of(string aircraftType) =>
        new(AircraftLength.ResolveFt(aircraftType), AircraftFootprint.ResolveWingspanFt(aircraftType));

    /// <summary>Where <paramref name="part"/> lies for the main gear at <paramref name="position"/> facing <paramref name="headingDeg"/>.</summary>
    /// <param name="part">The part.</param>
    /// <param name="position">The main-gear reference position.</param>
    /// <param name="headingDeg">The heading (deg true).</param>
    /// <returns>The part's position.</returns>
    public LatLon PointOf(AircraftBodyPart part, LatLon position, double headingDeg) =>
        part switch
        {
            AircraftBodyPart.Nose => Offset(position, headingDeg, LengthFt / 2.0),
            AircraftBodyPart.LeftWingtip => Offset(position, headingDeg + 270.0, WingspanFt / 2.0),
            AircraftBodyPart.RightWingtip => Offset(position, headingDeg + 90.0, WingspanFt / 2.0),
            AircraftBodyPart.Tail => Offset(position, headingDeg + 180.0, LengthFt / 2.0),
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unknown aircraft body part"),
        };

    /// <summary>The part's name as a log line reads it: "nose", "left wingtip", "right wingtip", "tail".</summary>
    /// <param name="part">The part.</param>
    /// <returns>The name.</returns>
    public static string Describe(AircraftBodyPart part) =>
        part switch
        {
            AircraftBodyPart.Nose => "nose",
            AircraftBodyPart.LeftWingtip => "left wingtip",
            AircraftBodyPart.RightWingtip => "right wingtip",
            AircraftBodyPart.Tail => "tail",
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unknown aircraft body part"),
        };

    private static LatLon Offset(LatLon position, double bearingDeg, double distanceFt) =>
        GeoMath.ProjectPoint(position, new TrueHeading(((bearingDeg % 360.0) + 360.0) % 360.0), distanceFt / GeoMath.FeetPerNm);
}
