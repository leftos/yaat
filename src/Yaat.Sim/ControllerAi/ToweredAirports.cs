using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.ControllerAi;

/// <summary>
/// The airports the room's loaded ARTCC configs (its own, its scenario roster's and its geographic neighbours') give a
/// tower: a tower-cab facility (ATCT, ATCT/TRACON, ATCT/RAPCON) whose id is the airport's, or whose positions' callsign
/// prefix is (the same rule as <see cref="AiPositionResolver.IsTowered"/>). Every other airport reads as untowered,
/// including one no loaded config names. The one towered query the command dispatcher and the client's room DTO share.
/// </summary>
public sealed class ToweredAirports
{
    private ToweredAirports(IReadOnlyList<string> ids) => Ids = ids;

    /// <summary>No config loaded: every airport reads as untowered.</summary>
    public static ToweredAirports Empty { get; } = new([]);

    /// <summary>The towered airport ids, sorted ordinally and distinct, as the configs spell them.</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>The towered airports of <paramref name="configs"/>.</summary>
    public static ToweredAirports From(IEnumerable<ArtccConfigRoot> configs)
    {
        var ids = new List<string>();
        foreach (ArtccConfigRoot config in configs)
        {
            foreach (FacilityConfig cab in AiPositionResolver.TowerCabs(config.Facility))
            {
                ids.Add(cab.Id);
                ids.AddRange(cab.Positions.Select(p => CallsignPrefix(p.Callsign)).OfType<string>());
            }
        }

        return FromIds(ids);
    }

    /// <summary>The set holding <paramref name="ids"/> (a recording's stored list).</summary>
    public static ToweredAirports FromIds(IEnumerable<string> ids) =>
        new([.. ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim().ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal)]);

    /// <summary>
    /// True when <paramref name="airportId"/> has a tower, in either spelling: <c>OAK</c> or <c>KOAK</c>, and outside the
    /// CONUS <c>ANC</c> or <c>PANC</c>, matched through the navigation data's FAA/ICAO index (the K-prefix rule alone with
    /// no navigation data loaded).
    /// </summary>
    public bool IsTowered(string airportId) =>
        NavigationDatabase.InstanceOrNull is { } navDb
            ? Ids.Any(id => navDb.AirportIdsMatchResolved(id, airportId))
            : Ids.Any(id => NavigationDatabase.AirportIdsMatch(id, airportId));

    private static string? CallsignPrefix(string callsign)
    {
        int underscore = callsign.IndexOf('_');
        return underscore > 0 ? callsign[..underscore] : null;
    }
}
