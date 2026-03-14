using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim;

public static class ProgrammedFixResolver
{
    public static HashSet<string> Resolve(
        string? route,
        string? expectedApproach,
        string? destination,
        string? departure,
        NavigationDatabase? navDb,
        IReadOnlyList<string>? activeApproachFixNames,
        NavigationDatabase? fixDb
    )
    {
        var fixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(route))
        {
            ExpandRouteInto(fixes, route, fixDb);
        }

        if (!string.IsNullOrEmpty(expectedApproach) && navDb is not null)
        {
            string airport = !string.IsNullOrEmpty(destination) ? destination : (departure ?? "");
            if (!string.IsNullOrEmpty(airport))
            {
                string resolvedId = navDb.ResolveApproachId(airport, expectedApproach) ?? expectedApproach;
                var procedure = navDb.GetApproach(airport, resolvedId);
                if (procedure is not null)
                {
                    foreach (var name in ApproachCommandHandler.GetApproachFixNames(procedure))
                    {
                        fixes.Add(name);
                    }
                }
            }
        }

        if (activeApproachFixNames is not null)
        {
            foreach (var name in activeApproachFixNames)
            {
                fixes.Add(name);
            }
        }

        return fixes;
    }

    private static void ExpandRouteInto(HashSet<string> fixes, string route, NavigationDatabase? fixDb)
    {
        var tokens = route.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];

            // Token with dot: "FIX.AIRWAY" format (e.g., "PORTE.V25")
            var dotIndex = token.IndexOf('.');
            if (dotIndex >= 0)
            {
                var fixName = token[..dotIndex];
                var airwayId = token[(dotIndex + 1)..];
                if (!string.IsNullOrEmpty(fixName))
                {
                    fixes.Add(fixName);
                }

                // Expand airway segment from this fix to the next token (exit fix)
                if (fixDb is not null && !string.IsNullOrEmpty(airwayId) && i + 1 < tokens.Length)
                {
                    var nextToken = tokens[i + 1];
                    var nextDot = nextToken.IndexOf('.');
                    var exitFix = nextDot >= 0 ? nextToken[..nextDot] : nextToken;
                    var segment = fixDb.ExpandAirwaySegment(airwayId, fixName, exitFix);
                    foreach (var segFix in segment)
                    {
                        fixes.Add(segFix);
                    }
                }

                continue;
            }

            // Bare airway token: "FIX AIRWAY FIX" format (e.g., "OAK V25 SAC")
            if (fixDb is not null && fixDb.IsAirway(token) && i > 0 && i + 1 < tokens.Length)
            {
                var prevToken = tokens[i - 1];
                var prevDot = prevToken.IndexOf('.');
                var entryFix = prevDot >= 0 ? prevToken[..prevDot] : prevToken;
                var nextToken = tokens[i + 1];
                var nextDot = nextToken.IndexOf('.');
                var exitFix = nextDot >= 0 ? nextToken[..nextDot] : nextToken;
                var segment = fixDb.ExpandAirwaySegment(token, entryFix, exitFix);
                foreach (var segFix in segment)
                {
                    fixes.Add(segFix);
                }

                continue;
            }

            // Plain fix name
            if (!string.IsNullOrEmpty(token))
            {
                fixes.Add(token);
            }
        }
    }
}
