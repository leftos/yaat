namespace Yaat.Sim.Data;

/// <summary>
/// Expands route tokens (SIDs, STARs, airways, fixes) into constituent fix names.
/// Consolidates route expansion logic previously duplicated across NavigationDatabase,
/// ScenarioLoader, RouteChainer, and ProgrammedFixResolver.
/// </summary>
public static class RouteExpander
{
    /// <summary>
    /// Expands a route string into ordered fix names using the global NavigationDatabase.Instance.
    /// </summary>
    public static List<string> Expand(string route) => Expand(route, NavigationDatabase.Instance);

    /// <summary>
    /// Expands a route string into ordered fix names using the provided NavigationDatabase.
    /// Handles SID body + transition matching, STAR join-point logic, airway segment expansion
    /// (bare and dot-notation), digit-stripping fallback for procedure version numbers, and adjacent deduplication.
    /// </summary>
    public static List<string> Expand(string route, NavigationDatabase navDb)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return [];
        }

        var tokens = route.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var dotParts = token.Split('.');
            var rawName = dotParts[0];
            string? suffix = dotParts.Length > 1 ? dotParts[1] : null;

            // Skip empty or numeric tokens (altitude/speed constraints like "050", "250")
            if (string.IsNullOrEmpty(rawName) || double.TryParse(rawName, out _))
            {
                continue;
            }

            // 1. SID check
            var resolvedSidId = navDb.ResolveSidId(rawName);
            if (resolvedSidId is not null)
            {
                ExpandSid(result, resolvedSidId, tokens, i, navDb);
                continue;
            }

            // 2. STAR check
            var resolvedStarId = navDb.ResolveStarId(rawName);
            if (resolvedStarId is not null)
            {
                ExpandStar(result, resolvedStarId, navDb);
                continue;
            }

            // 3. Dot-notation: FIX.AIRWAY (e.g., "PORTE.V25")
            if (suffix is not null && navDb.IsAirway(suffix))
            {
                EmitDeduped(result, rawName);
                string? nextFix = FindNextNonNumericToken(tokens, i + 1);
                if (nextFix is not null)
                {
                    var segment = navDb.ExpandAirwaySegment(suffix, rawName, nextFix.Split('.')[0]);
                    foreach (var fix in segment)
                    {
                        EmitDeduped(result, fix);
                    }
                }

                continue;
            }

            // 4. Bare airway token
            if (navDb.IsAirway(rawName))
            {
                string? fromFix = result.Count > 0 ? result[^1] : null;
                string? toFix = FindNextNonNumericToken(tokens, i + 1);
                if (fromFix is not null && toFix is not null)
                {
                    var segment = navDb.ExpandAirwaySegment(rawName, fromFix, toFix.Split('.')[0]);
                    foreach (var fix in segment)
                    {
                        EmitDeduped(result, fix);
                    }
                }

                continue;
            }

            // 5. Plain fix — emit as-is (SID/STAR version matching is handled above)
            EmitDeduped(result, rawName);
        }

        return result;
    }

    private static void ExpandSid(List<string> result, string sidId, string[] tokens, int tokenIndex, NavigationDatabase navDb)
    {
        var body = navDb.GetSidBody(sidId);
        if (body is null)
        {
            return;
        }

        foreach (var fix in body)
        {
            EmitDeduped(result, fix);
        }

        var transitions = navDb.GetSidTransitions(sidId);
        if (transitions is null || transitions.Count == 0)
        {
            return;
        }

        // Find the next non-numeric token to determine which transition to use
        string? nextToken = FindNextNonNumericToken(tokens, tokenIndex + 1);
        if (nextToken is not null)
        {
            var nextFixName = NavigationDatabase.StripTrailingDigits(nextToken.Split('.')[0]);

            foreach (var trans in transitions)
            {
                if (trans.Name.Equals(nextFixName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var fix in trans.Fixes)
                    {
                        EmitDeduped(result, fix);
                    }

                    return;
                }
            }
        }

        // No matching transition found — emit all transition fixes (for autocomplete/ProgrammedFixResolver)
        foreach (var trans in transitions)
        {
            foreach (var fix in trans.Fixes)
            {
                EmitDeduped(result, fix);
            }
        }
    }

    private static void ExpandStar(List<string> result, string starId, NavigationDatabase navDb)
    {
        var body = navDb.GetStarBody(starId);
        if (body is null)
        {
            return;
        }

        int startIdx = 0;

        // If we have a preceding fix, find it in the STAR body to determine the join point
        if (result.Count > 0)
        {
            var lastFix = result[^1];

            for (int i = 0; i < body.Count; i++)
            {
                if (body[i].Equals(lastFix, StringComparison.OrdinalIgnoreCase))
                {
                    startIdx = i + 1;
                    break;
                }
            }

            // Also check STAR transitions for the join fix
            if (startIdx == 0)
            {
                var transitions = navDb.GetStarTransitions(starId);
                if (transitions is not null)
                {
                    foreach (var trans in transitions)
                    {
                        int transIdx = -1;
                        for (int i = 0; i < trans.Fixes.Count; i++)
                        {
                            if (trans.Fixes[i].Equals(lastFix, StringComparison.OrdinalIgnoreCase))
                            {
                                transIdx = i;
                                break;
                            }
                        }

                        if (transIdx >= 0)
                        {
                            // Emit remaining transition fixes after the join point
                            for (int i = transIdx + 1; i < trans.Fixes.Count; i++)
                            {
                                EmitDeduped(result, trans.Fixes[i]);
                            }

                            startIdx = 0; // Emit full body after transition
                            break;
                        }
                    }
                }
            }
        }

        for (int i = startIdx; i < body.Count; i++)
        {
            EmitDeduped(result, body[i]);
        }
    }

    private static string? FindNextNonNumericToken(string[] tokens, int startIndex)
    {
        for (int j = startIndex; j < tokens.Length; j++)
        {
            if (!double.TryParse(tokens[j].Split('.')[0], out _))
            {
                return tokens[j];
            }
        }

        return null;
    }

    private static void EmitDeduped(List<string> result, string fixName)
    {
        if (result.Count == 0 || !result[^1].Equals(fixName, StringComparison.OrdinalIgnoreCase))
        {
            result.Add(fixName);
        }
    }
}
