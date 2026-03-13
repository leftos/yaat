namespace Yaat.Sim.Data;

public interface IFixLookup
{
    (double Lat, double Lon)? GetFixPosition(string name);
    double? GetAirportElevation(string code);

    /// <summary>
    /// Expands a route string into constituent fix names,
    /// expanding SID/STAR identifiers to their body + transition fixes.
    /// Used for autocomplete highlighting where all fixes are needed.
    /// </summary>
    IReadOnlyList<string> ExpandRoute(string route);

    /// <summary>
    /// Expands a route string for navigation purposes.
    /// Only emits ordered body fixes for published SIDs/STARs (body &gt; 1 fix).
    /// Skips radar-vector SIDs/STARs entirely (body ≤ 1 fix).
    /// Deduplicates adjacent identical fix names.
    /// </summary>
    IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport);

    /// <summary>
    /// Returns the ordered body fix names for a SID, or null if unknown.
    /// </summary>
    IReadOnlyList<string>? GetSidBody(string sidId) => null;

    /// <summary>
    /// Returns all transitions for a SID with their endpoint fix name and ordered fix list.
    /// Returns null if the SID is unknown.
    /// </summary>
    IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetSidTransitions(string sidId) => null;

    /// <summary>
    /// Returns the ordered body fix names for a STAR, or null if unknown.
    /// </summary>
    IReadOnlyList<string>? GetStarBody(string starId);

    /// <summary>
    /// Returns all transitions for a STAR with their entry fix name and ordered fix list.
    /// Returns null if the STAR is unknown.
    /// </summary>
    IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId);

    /// <summary>
    /// Returns the ordered fix names for an airway, or null if unknown.
    /// </summary>
    IReadOnlyList<string>? GetAirwayFixes(string airwayId) => null;
}
