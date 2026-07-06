namespace Yaat.Sim;

/// <summary>
/// One fix on the resolved lateral route an aircraft is flying, projected to the client for the
/// radar "Show nav route" overlay. Carries the server-computed geographic position directly (the
/// client draws it verbatim rather than re-resolving the name), so arc-densified points, custom
/// fixes, and FRD fixes render on the exact path the aircraft flies.
///
/// <para><see cref="Name"/> is empty for synthetic arc-densification vertices (RF/AF legs expanded
/// into a polyline). An empty name signals "path vertex only" — the client draws it as part of the
/// route line but hangs no diamond, label, or restriction on it.</para>
///
/// <para><see cref="RestrictionLines"/> holds the pre-formatted crossing altitude/speed label lines
/// (see <see cref="Data.Vnas.CrossingRestrictionLabel"/>) for a real fix that carries a controller
/// (<c>CFIX</c>) or procedure-published restriction, or null when the fix is unrestricted.</para>
/// </summary>
public record NavRouteFixDto(string Name, double Lat, double Lon, List<string>? RestrictionLines);
