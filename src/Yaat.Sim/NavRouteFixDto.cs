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
/// (<c>CFIX</c>) or procedure-published restriction, or null when the fix is unrestricted. Older clients draw from it;
/// it carries no latency.</para>
///
/// <para><see cref="RestrictionTokens"/> holds the same label lines as tokens (one inner list per line, its tokens left to
/// right, from <see cref="Data.Vnas.CrossingRestrictionLabel.BuildTokenLines"/>), each marked latent when it is a published
/// altitude restriction not in force under the current clearance; speed tokens are always active. Null when the fix is
/// unrestricted; when an older server omits it, System.Text.Json supplies null for the missing constructor
/// parameter.</para>
/// </summary>
public record NavRouteFixDto(
    string Name,
    double Lat,
    double Lon,
    List<string>? RestrictionLines,
    List<List<NavRouteRestrictionToken>>? RestrictionTokens
);
