using Yaat.Sim.Data;

namespace Yaat.Sim.Commands;

public static class RouteChainer
{
    /// <summary>
    /// If the last resolved fix appears in the aircraft's filed route,
    /// appends all subsequent route fixes. This is how "DCT SUNOL"
    /// automatically picks up the rest of the filed route after SUNOL.
    /// </summary>
    public static void AppendRouteRemainder(
        List<ResolvedFix> resolved, string aircraftRoute, IFixLookup fixes)
    {
        if (resolved.Count == 0)
        {
            return;
        }

        var lastFix = resolved[^1].Name;
        var routeFixes = aircraftRoute.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int matchIndex = -1;

        for (int i = 0; i < routeFixes.Length; i++)
        {
            // Route entries may have altitude/speed constraints like "FIX.A50"
            var fixPart = routeFixes[i].Split('.')[0].ToUpperInvariant();
            if (fixPart == lastFix)
            {
                matchIndex = i;
                break;
            }
        }

        if (matchIndex >= 0 && matchIndex < routeFixes.Length - 1)
        {
            for (int i = matchIndex + 1; i < routeFixes.Length; i++)
            {
                var fixPart = routeFixes[i].Split('.')[0].ToUpperInvariant();
                var pos = fixes.GetFixPosition(fixPart);
                if (pos is not null)
                {
                    resolved.Add(new ResolvedFix(fixPart, pos.Value.Lat, pos.Value.Lon));
                }
            }
        }
    }
}
