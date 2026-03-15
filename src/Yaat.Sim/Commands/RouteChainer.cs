using Yaat.Sim.Data;

namespace Yaat.Sim.Commands;

public static class RouteChainer
{
    /// <summary>
    /// If the last resolved fix appears in the aircraft's filed route,
    /// appends all subsequent route fixes. This is how "DCT SUNOL"
    /// automatically picks up the rest of the filed route after SUNOL.
    /// </summary>
    public static void AppendRouteRemainder(List<ResolvedFix> resolved, string aircraftRoute)
    {
        if (resolved.Count == 0)
        {
            return;
        }

        var lastFix = resolved[^1].Name;
        var routeTokens = aircraftRoute.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int matchIndex = -1;

        for (int i = 0; i < routeTokens.Length; i++)
        {
            // Route entries may have altitude/speed constraints like "FIX.A50"
            var fixPart = routeTokens[i].Split('.')[0].ToUpperInvariant();
            if (fixPart == lastFix)
            {
                matchIndex = i;
                break;
            }
        }

        if (matchIndex < 0 || matchIndex >= routeTokens.Length - 1)
        {
            return;
        }

        // Join remainder tokens and expand via RouteExpander
        var navDb = NavigationDatabase.Instance;
        var remainder = string.Join(' ', routeTokens.Skip(matchIndex + 1));
        var expanded = RouteExpander.Expand(remainder);

        foreach (var fixName in expanded)
        {
            if (resolved.Count > 0 && fixName.Equals(resolved[^1].Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pos = navDb.GetFixPosition(fixName);
            if (pos is not null)
            {
                resolved.Add(new ResolvedFix(fixName, pos.Value.Lat, pos.Value.Lon));
            }
        }
    }
}
