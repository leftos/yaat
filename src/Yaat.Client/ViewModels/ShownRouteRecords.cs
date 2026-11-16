using Yaat.Sim;

namespace Yaat.Client.ViewModels;

/// <summary>
/// A drawn waypoint on a radar route overlay. <see cref="ResolvedName"/> is empty for synthetic
/// arc-densification vertices — those draw as bare polyline points (no diamond, label, or
/// restriction). <see cref="RestrictionTokens"/> holds the crossing altitude/speed label lines for
/// a fix that carries one, each line its tokens left to right, a latent token drawn dimmed (null
/// when unrestricted, in route-drawing mode, or when the server sent no tokens).
/// </summary>
public record DrawnWaypoint(
    string ResolvedName,
    double Lat,
    double Lon,
    IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>>? RestrictionTokens = null
);

/// <summary>
/// A heading-vector arrow segment drawn from <see cref="FromLat"/>/<see cref="FromLon"/> on
/// <see cref="HeadingMag"/> (magnetic) for <see cref="LengthNm"/> nautical miles.
/// </summary>
public record VectorTail(double FromLat, double FromLon, double HeadingMag, double LengthNm = 5.0);
