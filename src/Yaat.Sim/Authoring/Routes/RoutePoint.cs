namespace Yaat.Sim.Authoring.Routes;

/// <summary>What a <see cref="RoutePoint"/> names: a fix, a fix-radial-distance, or a raw coordinate.</summary>
public enum RoutePointKind
{
    /// <summary>A navdata fix by its ident.</summary>
    Fix,

    /// <summary>A fix-radial-distance: an anchor fix, a magnetic radial and a distance in NM.</summary>
    Frd,

    /// <summary>
    /// A raw latitude and longitude, written as an FRD from a DME NAVAID in range, else as a lat/lon token, when it becomes
    /// nav-path text.
    /// </summary>
    LatLon,
}

/// <summary>
/// One point of a saved route, geometry only. A fix point carries <see cref="Fix"/>; an FRD point carries <see cref="Fix"/>,
/// <see cref="Radial"/> and <see cref="DistanceNm"/>; a coordinate point carries <see cref="Lat"/> and <see cref="Lon"/>.
/// </summary>
/// <param name="Kind">What the point names.</param>
/// <param name="Fix">The fix ident, or the FRD's anchor ident; null for a coordinate point.</param>
/// <param name="Radial">The FRD's magnetic radial from the anchor, 1 to 360; null otherwise.</param>
/// <param name="DistanceNm">The FRD's distance from the anchor in whole NM, 0 to 999; null otherwise.</param>
/// <param name="Lat">The coordinate point's latitude in degrees; null otherwise.</param>
/// <param name="Lon">The coordinate point's longitude in degrees; null otherwise.</param>
public sealed record RoutePoint(RoutePointKind Kind, string? Fix, int? Radial, int? DistanceNm, double? Lat, double? Lon)
{
    /// <summary>A point at a navdata fix.</summary>
    /// <param name="ident">The fix ident.</param>
    /// <returns>The fix point.</returns>
    public static RoutePoint AtFix(string ident) => new(RoutePointKind.Fix, ident, null, null, null, null);

    /// <summary>A fix-radial-distance point.</summary>
    /// <param name="ident">The anchor fix ident.</param>
    /// <param name="radial">The magnetic radial from the anchor, 1 to 360.</param>
    /// <param name="distanceNm">The distance from the anchor in whole NM.</param>
    /// <returns>The FRD point.</returns>
    public static RoutePoint AtFrd(string ident, int radial, int distanceNm) => new(RoutePointKind.Frd, ident, radial, distanceNm, null, null);

    /// <summary>A raw coordinate point.</summary>
    /// <param name="position">Where the point is.</param>
    /// <returns>The coordinate point.</returns>
    public static RoutePoint AtLatLon(LatLon position) => new(RoutePointKind.LatLon, null, null, null, position.Lat, position.Lon);
}
