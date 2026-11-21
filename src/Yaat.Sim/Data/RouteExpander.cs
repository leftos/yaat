using Yaat.Sim.Data.MilitaryRoutes;

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
    /// <param name="route">Route string to expand (e.g. "NIMI5 OAK V6 SAC").</param>
    /// <param name="navDb">Navigation database supplying SIDs, STARs, airways, and fixes.</param>
    /// <param name="includeAllTransitionsOnMismatch">
    /// When <c>true</c> (the default — autocomplete/UI behavior), a SID whose next route token doesn't
    /// match any transition name falls back to emitting every transition's fixes so the result captures
    /// every reachable exit fix. When <c>false</c> (flight-plan / navigation behavior), the fallback is
    /// suppressed: only the body is emitted, and downstream tokens (airway exit fix, direct fixes) are
    /// processed normally. Flight-plan callers must pass <c>false</c> to avoid fabricating turn-backs
    /// through every transition on radar-vectors SIDs whose vNAS protobuf carries adapted-route hints
    /// as synthetic "transitions" (e.g. NIMI5's [OAK,CCR], [OAK,PYE], ...).
    /// </param>
    public static List<string> Expand(string route, NavigationDatabase navDb, bool includeAllTransitionsOnMismatch = true) =>
        ExpandCore(route, navDb, includeAllTransitionsOnMismatch, spans: null);

    /// <summary>A SID or STAR in an expanded route, and the fixes the expansion emitted for it (body and transition).</summary>
    internal readonly record struct ProcedureSpan(string ProcedureId, bool IsSid, List<string> Fixes);

    /// <summary>
    /// The SIDs and STARs <see cref="Expand(string, NavigationDatabase, bool)"/> expands in <paramref name="route"/>, each with
    /// the fixes it emitted for them: the same join point and transition selection, so a fix is on the procedure as filed
    /// exactly when it is in its span. A first fix dropped as a repeat of the fix before it is not in the span.
    /// </summary>
    internal static List<ProcedureSpan> ProcedureSpans(string route, NavigationDatabase navDb, bool includeAllTransitionsOnMismatch)
    {
        var spans = new List<ProcedureSpan>();
        ExpandCore(route, navDb, includeAllTransitionsOnMismatch, spans);
        return spans;
    }

    private static List<string> ExpandCore(string route, NavigationDatabase navDb, bool includeAllTransitionsOnMismatch, List<ProcedureSpan>? spans)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return [];
        }

        string[] tokens = route.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            string[] dotParts = token.Split('.');
            string rawName = dotParts[0];
            string? suffix = dotParts.Length > 1 ? dotParts[1] : null;

            // Skip empty or numeric tokens (altitude/speed constraints like "050", "250")
            if (string.IsNullOrEmpty(rawName) || double.TryParse(rawName, out _))
            {
                continue;
            }

            // 1–2. SID, then STAR
            if (TryExpandProcedure(result, new ProcedureToken(rawName, tokens, i), navDb, includeAllTransitionsOnMismatch, spans))
            {
                continue;
            }

            // 3. Military training route (e.g., "SAT263043 IR149 LRD040028")
            //
            // Checked before the airway branches even though routes are shadowed into the airway
            // index: the bracketing tokens are FRDs rather than names of points on the route, so
            // ExpandAirwaySegment's name matching would find neither anchor, and its bidirectional
            // walk is wrong for a one-way route.
            if (navDb.IsMilitaryRoute(rawName))
            {
                string? entryAnchor = result.Count > 0 ? result[^1] : null;
                string? exitAnchor = FindNextNonNumericToken(tokens, i + 1)?.Split('.')[0];
                foreach (string fix in MilitaryRouteExpander.Expand(rawName, entryAnchor, exitAnchor, navDb))
                {
                    EmitDeduped(result, fix);
                }

                continue;
            }

            // 4. Dot-notation: FIX.AIRWAY (e.g., "PORTE.V25")
            if (suffix is not null && navDb.IsAirway(suffix))
            {
                EmitDeduped(result, rawName);
                string? nextFix = FindNextNonNumericToken(tokens, i + 1);
                if (nextFix is not null)
                {
                    IReadOnlyList<string> segment = navDb.ExpandAirwaySegment(suffix, rawName, nextFix.Split('.')[0]);
                    foreach (string fix in segment)
                    {
                        EmitDeduped(result, fix);
                    }
                }

                continue;
            }

            // 5. Bare airway token
            if (navDb.IsAirway(rawName))
            {
                string? fromFix = result.Count > 0 ? result[^1] : null;
                string? toFix = FindNextNonNumericToken(tokens, i + 1);
                if (fromFix is not null && toFix is not null)
                {
                    IReadOnlyList<string> segment = navDb.ExpandAirwaySegment(rawName, fromFix, toFix.Split('.')[0]);
                    foreach (string fix in segment)
                    {
                        EmitDeduped(result, fix);
                    }
                }

                continue;
            }

            // 6. Plain fix — emit as-is (SID/STAR version matching is handled above)
            EmitDeduped(result, rawName);
        }

        return result;
    }

    /// <summary>A route token at <see cref="Index"/> in <see cref="Tokens"/>, its procedure name without a dot suffix.</summary>
    private readonly record struct ProcedureToken(string RawName, string[] Tokens, int Index);

    /// <summary>
    /// Expands <paramref name="token"/> when it names a SID (checked first) or a STAR, recording the fixes it emitted in
    /// <paramref name="spans"/>; false, nothing emitted, when it names neither. A STAR's span starts at the fix the route
    /// meets it at when the STAR publishes that fix (its body or a transition), since the route emitted it already.
    /// </summary>
    private static bool TryExpandProcedure(
        List<string> result,
        ProcedureToken token,
        NavigationDatabase navDb,
        bool includeAllTransitionsOnMismatch,
        List<ProcedureSpan>? spans
    )
    {
        if (navDb.ResolveSidId(token.RawName) is { } sidId)
        {
            int sidStart = result.Count;
            ExpandSid(result, sidId, token.Tokens, token.Index, navDb, includeAllTransitionsOnMismatch);
            spans?.Add(new ProcedureSpan(sidId, true, result.GetRange(sidStart, result.Count - sidStart)));
            return true;
        }

        if (navDb.ResolveStarId(token.RawName) is not { } starId)
        {
            return false;
        }

        int starStart = ((result.Count > 0) && StarPublishesFix(navDb, starId, result[^1])) ? result.Count - 1 : result.Count;
        ExpandStar(result, starId, navDb);
        spans?.Add(new ProcedureSpan(starId, false, result.GetRange(starStart, result.Count - starStart)));
        return true;
    }

    private static bool StarPublishesFix(NavigationDatabase navDb, string starId, string fix) =>
        (navDb.GetStarBody(starId) ?? []).Contains(fix, StringComparer.OrdinalIgnoreCase)
        || (navDb.GetStarTransitions(starId) ?? []).Any(t => t.Fixes.Contains(fix, StringComparer.OrdinalIgnoreCase));

    private static void ExpandSid(
        List<string> result,
        string sidId,
        string[] tokens,
        int tokenIndex,
        NavigationDatabase navDb,
        bool includeAllTransitionsOnMismatch
    )
    {
        IReadOnlyList<string>? body = navDb.GetSidBody(sidId);
        if (body is null)
        {
            return;
        }

        foreach (string fix in body)
        {
            EmitDeduped(result, fix);
        }

        IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? transitions = navDb.GetSidTransitions(sidId);
        if (transitions is null || transitions.Count == 0)
        {
            return;
        }

        // Find the next non-numeric token to determine which transition to use
        string? nextToken = FindNextNonNumericToken(tokens, tokenIndex + 1);
        if (nextToken is not null)
        {
            string nextFixName = NavigationDatabase.StripTrailingDigits(nextToken.Split('.')[0]);

            foreach ((string Name, IReadOnlyList<string> Fixes) trans in transitions)
            {
                if (trans.Name.Equals(nextFixName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string fix in trans.Fixes)
                    {
                        EmitDeduped(result, fix);
                    }

                    return;
                }
            }
        }

        // No matching transition. In autocomplete/UI context, emit every transition's fixes so callers
        // can index/search by any reachable exit fix. In flight-plan context (includeAllTransitionsOnMismatch=false),
        // emit nothing — fabricating a turn-back through every transition would produce a nonsensical
        // NavigationRoute (e.g. on radar-vectors SIDs whose vNAS protobuf carries adapted-route hints
        // as synthetic transitions). The remaining route tokens (airway, direct fix, ...) are processed
        // by the caller's main loop and handle the real post-SID routing.
        if (!includeAllTransitionsOnMismatch)
        {
            return;
        }

        foreach ((string Name, IReadOnlyList<string> Fixes) trans in transitions)
        {
            foreach (string fix in trans.Fixes)
            {
                EmitDeduped(result, fix);
            }
        }
    }

    private static void ExpandStar(List<string> result, string starId, NavigationDatabase navDb)
    {
        IReadOnlyList<string>? body = navDb.GetStarBody(starId);
        if (body is null)
        {
            return;
        }

        int startIdx = 0;

        // If we have a preceding fix, find it in the STAR body to determine the join point
        if (result.Count > 0)
        {
            string lastFix = result[^1];

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
                IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? transitions = navDb.GetStarTransitions(starId);
                if (transitions is not null)
                {
                    foreach ((string Name, IReadOnlyList<string> Fixes) trans in transitions)
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
