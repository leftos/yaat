namespace Yaat.Sim.Data;

public interface IFixLookup
{
    (double Lat, double Lon)? GetFixPosition(string name);
    double? GetAirportElevation(string code);

    /// <summary>
    /// Expands a route string into constituent fix names,
    /// expanding SID/STAR identifiers to their body + transition fixes.
    /// </summary>
    IReadOnlyList<string> ExpandRoute(string route);
}
