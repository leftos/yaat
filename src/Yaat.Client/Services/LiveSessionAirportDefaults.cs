namespace Yaat.Client.Services;

/// <summary>
/// Which airports the live-session picker offers for a position, and which one it pre-selects. A facility offers its own
/// airport (a tower), then every airport its STARS configuration names, then the airports of its subtree — so a TRACON
/// offers what it controls and a center every tower in the ARTCC. The default is the facility's own airport, else the
/// primary airport of its STARS configuration (SFO for NCT/O90, SMF for MC1), else the primary of the busiest child
/// facility (a center lands on its largest TRACON's primary rather than whichever tower the tree lists first).
/// </summary>
public static class LiveSessionAirportDefaults
{
    public sealed record Choice(IReadOnlyList<string> Airports, string? Default);

    public static Choice Resolve(FacilityTreeDto root, string positionId)
    {
        var facility = FindFacilityOfPosition(root, positionId) ?? root;
        var airports = CollectAirports(facility);
        if (airports.Count == 0)
        {
            facility = root;
            airports = CollectAirports(root);
        }

        var preferred = PreferredAirport(facility);
        return new Choice(airports, preferred is not null && airports.Contains(preferred) ? preferred : airports.FirstOrDefault());
    }

    /// <summary>The facility whose position list holds <paramref name="positionId"/>, or null.</summary>
    public static FacilityTreeDto? FindFacilityOfPosition(FacilityTreeDto node, string positionId)
    {
        if (node.Positions.Any(p => p.Id == positionId))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (FindFacilityOfPosition(child, positionId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Own airport, STARS airports, then the subtree's, in tree order, without duplicates.</summary>
    public static List<string> CollectAirports(FacilityTreeDto node)
    {
        var airports = new List<string>();
        Collect(node, airports);
        return airports;
    }

    /// <summary>Own airport, else the STARS primary, else the busiest child's preference, recursively.</summary>
    public static string? PreferredAirport(FacilityTreeDto node)
    {
        if (node.AirportId is { } own)
        {
            return own;
        }

        if (node.PrimaryAirportId is { } primary)
        {
            return primary;
        }

        return node.Children.OrderByDescending(PositionCount).Select(PreferredAirport).FirstOrDefault(a => a is not null);
    }

    /// <summary>Positions in the facility and everything under it — the size proxy for "the ARTCC's main TRACON".</summary>
    public static int PositionCount(FacilityTreeDto node) => node.Positions.Count + node.Children.Sum(PositionCount);

    private static void Collect(FacilityTreeDto node, List<string> airports)
    {
        Add(airports, node.AirportId);
        foreach (var airport in node.Airports)
        {
            Add(airports, airport);
        }

        foreach (var child in node.Children)
        {
            Collect(child, airports);
        }
    }

    private static void Add(List<string> airports, string? airport)
    {
        if (!string.IsNullOrWhiteSpace(airport) && !airports.Contains(airport, StringComparer.OrdinalIgnoreCase))
        {
            airports.Add(airport);
        }
    }
}
