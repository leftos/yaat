using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// What <see cref="NavdataRouteSource"/> knows about a published route it offers: a SID joined direct to a STAR.
/// </summary>
/// <param name="Sid">The SID the route text starts with.</param>
/// <param name="TwinSid">
/// A SID that differs from <paramref name="Sid"/> only by the runways it serves (e.g. WESLA5 beside SSTIK5) and was merged
/// into this route, or null when there is none. When three or more SIDs share the path, only the second in ordinal order
/// is named here, and the others' runways appear in neither runway list.
/// </param>
/// <param name="Star">The STAR the route text ends with.</param>
/// <param name="LengthNm">The expanded route's length: departure airport, every fix, arrival airport, great-circle legs summed.</param>
/// <param name="Ratio"><paramref name="LengthNm"/> divided by the airport-to-airport great-circle distance.</param>
/// <param name="SidKind">How the SID is flown (conventional, RNAV or radar vector).</param>
/// <param name="StarKind">How the STAR is flown (conventional or RNAV).</param>
/// <param name="SidRunways">
/// The runways <paramref name="Sid"/> itself is coded for, from its runway transitions ("01B" is every parallel 01),
/// ordinal-sorted; empty when it codes none.
/// </param>
/// <param name="TwinSidRunways">The runways <paramref name="TwinSid"/> is coded for, ordinal-sorted; empty when there is no twin.</param>
/// <param name="StarRunways">The runways the STAR is coded for, ordinal-sorted; empty when it serves every runway.</param>
/// <param name="SidRunwaysNotCoded">
/// True when the SID has neither a runway transition nor a common route, so which runways it serves is not coded; such a
/// SID is treated as serving every runway (shown as <see cref="RunwaysNotCodedTag"/>).
/// </param>
public sealed record PublishedRouteInfo(
    string Sid,
    string? TwinSid,
    string Star,
    double LengthNm,
    double Ratio,
    ProcedureRouteKind SidKind,
    ProcedureRouteKind StarKind,
    IReadOnlyList<string> SidRunways,
    IReadOnlyList<string> TwinSidRunways,
    IReadOnlyList<string> StarRunways,
    bool SidRunwaysNotCoded
)
{
    /// <summary>The tag shown for a SID whose runways are not coded.</summary>
    public const string RunwaysNotCodedTag = "runways not coded";
}
