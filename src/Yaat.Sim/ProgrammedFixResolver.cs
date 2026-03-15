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
        IReadOnlyList<string>? activeApproachFixNames,
        string? activeStarId,
        string? destinationRunway
    )
    {
        var fixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(route))
        {
            ExpandRouteInto(fixes, route);
        }

        string? resolvedRunway = destinationRunway;

        var navDb = NavigationDatabase.Instance;

        if (!string.IsNullOrEmpty(expectedApproach))
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

                    // Derive runway from approach if not explicitly set
                    if (resolvedRunway is null && procedure.Runway is not null)
                    {
                        resolvedRunway = procedure.Runway;
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

        // Expand STAR runway transition fixes if we have both a STAR and a runway
        if (!string.IsNullOrEmpty(activeStarId) && resolvedRunway is not null)
        {
            string airport = !string.IsNullOrEmpty(destination) ? destination : (departure ?? "");
            if (!string.IsNullOrEmpty(airport))
            {
                var rwFixes = navDb.GetStarRunwayTransitions(airport, activeStarId, resolvedRunway);
                if (rwFixes is not null)
                {
                    foreach (var name in rwFixes)
                    {
                        fixes.Add(name);
                    }
                }
            }
        }

        return fixes;
    }

    /// <summary>
    /// Expands a filed route string into fix names. Handles dot-notation (FIX.AIRWAY) and
    /// bare airways, but does NOT expand SID/STAR procedure tokens — those are handled
    /// separately via activeStarId and the approach pipeline.
    /// </summary>
    private static void ExpandRouteInto(HashSet<string> fixes, string route)
    {
        var navDb = NavigationDatabase.Instance;
        var tokens = route.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];

            // Skip numeric-only tokens (altitude/speed constraints)
            if (double.TryParse(token.Split('.')[0], out _))
            {
                continue;
            }

            // Dot-notation: FIX.AIRWAY (e.g., "PORTE.V25")
            int dotIndex = token.IndexOf('.');
            if (dotIndex >= 0)
            {
                string fixName = token[..dotIndex];
                string airwayId = token[(dotIndex + 1)..];
                if (!string.IsNullOrEmpty(fixName))
                {
                    fixes.Add(fixName);
                }

                // Expand airway segment from this fix to the next token (exit fix)
                if (!string.IsNullOrEmpty(airwayId) && i + 1 < tokens.Length)
                {
                    string nextToken = tokens[i + 1];
                    int nextDot = nextToken.IndexOf('.');
                    string exitFix = nextDot >= 0 ? nextToken[..nextDot] : nextToken;
                    var segment = navDb.ExpandAirwaySegment(airwayId, fixName, exitFix);
                    foreach (var segFix in segment)
                    {
                        fixes.Add(segFix);
                    }
                }

                continue;
            }

            // Bare airway token: "ENTRY AIRWAY EXIT" format
            if (navDb.IsAirway(token) && i > 0 && i + 1 < tokens.Length)
            {
                string prevToken = tokens[i - 1];
                int prevDot = prevToken.IndexOf('.');
                string entryFix = prevDot >= 0 ? prevToken[..prevDot] : prevToken;
                string nextToken = tokens[i + 1];
                int nextDot = nextToken.IndexOf('.');
                string exitFix = nextDot >= 0 ? nextToken[..nextDot] : nextToken;
                var segment = navDb.ExpandAirwaySegment(token, entryFix, exitFix);
                foreach (var segFix in segment)
                {
                    fixes.Add(segFix);
                }

                continue;
            }

            // Plain fix or procedure name — add as-is
            fixes.Add(token);
        }
    }
}
