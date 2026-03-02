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
    IReadOnlyList<string> ExpandRouteForNavigation(
        string route, string? departureAirport);
}
