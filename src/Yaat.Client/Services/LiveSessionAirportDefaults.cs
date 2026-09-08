namespace Yaat.Client.Services;

/// <summary>
/// Which airports the live-session picker offers for a position, and which one it pre-selects. A facility offers its own
/// airport (a tower), then every airport its STARS configuration names, then the airports of its subtree — so a TRACON
/// offers what it controls and a center every tower in the ARTCC. The default is the airport the position's callsign names
/// (OAK_APP → OAK, SFO_B_APP → SFO) when the facility offers it, else the facility's own airport, else the primary airport
/// of its STARS configuration (SJC for NCT, SMF for MC1), else the primary of the busiest child facility (a center lands on
/// its largest TRACON's primary rather than whichever tower the tree lists first).
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

        if (PositionAirport(root, positionId, airports) is { } named)
        {
            return new Choice(airports, named);
        }

        var preferred = PreferredAirport(facility);
        return new Choice(airports, preferred is not null && airports.Contains(preferred) ? preferred : airports.FirstOrDefault());
    }

    /// <summary>The offered airport the selected position's callsign names (OAK_APP → OAK), or null when it names none.</summary>
    public static string? PositionAirport(FacilityTreeDto root, string positionId, IReadOnlyList<string> airports)
    {
        if (FindPositionSummary(root, positionId) is not { } position)
        {
            return null;
        }

        var prefix = CallsignPrefix(position.Callsign);
        return airports.FirstOrDefault(a => string.Equals(a, prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The token before the first underscore of a position callsign — its airport whenever it names one.</summary>
    public static string CallsignPrefix(string callsign)
    {
        var underscore = callsign.IndexOf('_');
        return underscore < 0 ? callsign : callsign[..underscore];
    }

    /// <summary>The position with <paramref name="positionId"/> anywhere in the tree, or null.</summary>
    public static PositionSummaryDto? FindPositionSummary(FacilityTreeDto node, string positionId)
    {
        if (node.Positions.FirstOrDefault(p => p.Id == positionId) is { } position)
        {
            return position;
        }

        foreach (var child in node.Children)
        {
            if (FindPositionSummary(child, positionId) is { } found)
            {
                return found;
            }
        }

        return null;
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
