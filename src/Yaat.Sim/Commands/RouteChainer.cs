using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;

namespace Yaat.Sim.Commands;

public static class RouteChainer
{
    private static readonly ILogger Log = SimLog.CreateLogger("RouteChainer");

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

        string lastFix = resolved[^1].Name;
        string[] routeTokens = aircraftRoute.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int matchIndex = -1;

        for (int i = 0; i < routeTokens.Length; i++)
        {
            // Route entries may have altitude/speed constraints like "FIX.A50"
            string fixPart = routeTokens[i].Split('.')[0].ToUpperInvariant();
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

        // Join remainder tokens and expand via RouteExpander. Flight-plan context.
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string remainder = string.Join(' ', routeTokens.Skip(matchIndex + 1));
        List<string> expanded = RouteExpander.Expand(remainder, navDb, includeAllTransitionsOnMismatch: false);

        foreach (string fixName in expanded)
        {
            if (resolved.Count > 0 && fixName.Equals(resolved[^1].Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // ResolveFixOrFrd so fix/radial/distance tokens in the filed route chain like any other
            // fix, matching ArrivalRouteResolver. Unresolvable names are logged rather than dropped
            // in silence — this path previously discarded them with no trace at all.
            (double Lat, double Lon)? pos = navDb.ResolveFixOrFrd(fixName);
            if (pos is null)
            {
                Log.LogWarning("Could not resolve nav fix '{Fix}' while chaining the filed route; skipping", fixName);
                continue;
            }

            resolved.Add(new ResolvedFix(fixName, pos.Value.Lat, pos.Value.Lon));
        }
    }
}
