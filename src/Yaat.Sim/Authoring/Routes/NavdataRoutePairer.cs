using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// The pairing behind <see cref="NavdataRouteSource"/>: every SID at the departure against every STAR at the destination,
/// the shortest transition pair of each, the plausibility cut, twin SIDs merged, and the rank order.
/// </summary>
internal static class NavdataRoutePairer
{
    /// <summary>A route may be at most this many times the airport-to-airport great circle (at and above 200 NM).</summary>
    internal const double MaxRatio = 1.25;

    /// <summary>A route may exceed the great circle by this much whatever its ratio (below 200 NM).</summary>
    internal const double ShortHopAllowanceNm = 50.0;

    /// <summary>How many routes the editor shows before "show all".</summary>
    internal const int FirstPageSize = 10;

    /// <summary>How many of the first page's routes one SID, or one STAR, may hold.</summary>
    internal const int MaxRowsPerProcedureOnFirstPage = 2;

    /// <summary>
    /// A direct leg may be at most this long for an aircraft whose equipment suffix is not RNAV-capable: AIM 5-1-6c.3(b)
    /// allows navaids "not more than 260 NM apart" off established routes from 18,000 feet MSL to FL 450, the largest of its
    /// bands, so a longer leg fails every band whatever the filed altitude.
    /// </summary>
    internal const double MaxDirectLegNm = 260.0;

    private static readonly ILogger Log = SimLog.CreateLogger("NavdataRoutePairer");

    private static readonly IReadOnlyDictionary<string, string?> NoRunwayEndFixes = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Equipment suffixes 7110.65 TBL 2-3-10 defines without RNAV or GNSS, which drop RNAV procedures. Its RNAV-capable
    /// suffixes (/Z /L /Y /C /I /V /S /G), /H and /O, and letters it does not define keep them.
    /// </summary>
    private static readonly HashSet<char> NonRnavSuffixes = ['A', 'B', 'D', 'M', 'N', 'P', 'T', 'U', 'W', 'X'];

    /// <summary>One SID or STAR as the pairing sees it.</summary>
    /// <param name="Id">The procedure identifier, e.g. "SSTIK5".</param>
    /// <param name="Kind">How it is flown.</param>
    /// <param name="Runways">Its runway transitions without the "RW" prefix, ordinal-sorted; empty when it codes none.</param>
    /// <param name="RunwaysNotCoded">True for a SID with neither a runway transition nor a common route.</param>
    /// <param name="JoinFixes">A SID's exit fixes or a STAR's entry fixes; a null entry means the route has no such token.</param>
    /// <param name="TwinKey">For a SID with runway transitions, its path without them: SIDs with equal keys are twins.</param>
    /// <param name="RunwayEndFixes">
    /// A runway-only SID's last fix per coded runway (without the "RW" prefix), since its transitions may end at different
    /// fixes; empty for every other procedure, whose runways share one path.
    /// </param>
    private sealed record ProcedureOption(
        string Id,
        ProcedureRouteKind Kind,
        IReadOnlyList<string> Runways,
        bool RunwaysNotCoded,
        IReadOnlyList<string?> JoinFixes,
        string? TwinKey,
        IReadOnlyDictionary<string, string?> RunwayEndFixes
    );

    private sealed record Candidate(ProcedureOption Sid, ProcedureOption Star, string RouteText, double LengthNm, double Ratio)
    {
        public ProcedureOption? Twin { get; init; }

        /// <summary>The exit fix the route text names after the SID, or null when it names none.</summary>
        public string? Exit { get; init; }
    }

    private sealed record PairingContext(
        NavigationDatabase NavDb,
        RouteQuery Query,
        (double Lat, double Lon) Departure,
        (double Lat, double Lon) Destination,
        double GreatCircleNm
    );

    /// <summary>
    /// The published routes for <paramref name="query"/>, in rank order; empty when either airport is unknown, has no
    /// procedures on its side, or both are the same airport.
    /// </summary>
    internal static List<FoundRoute> FindRoutes(NavigationDatabase navDb, RouteQuery query, string sourceName, CancellationToken ct)
    {
        PairingContext? ctx = CreateContext(navDb, query);
        if (ctx is null)
        {
            return [];
        }

        List<ProcedureOption> sids =
        [
            .. navDb
                .GetSids(query.Departure)
                .Select(sid => DescribeSid(navDb, sid, query.DepartureRunways))
                .Where(sid => IsAllowed(sid, query.EquipmentSuffix, query.DepartureRunways))
                .OrderBy(sid => sid.Id, StringComparer.Ordinal),
        ];
        List<ProcedureOption> stars =
        [
            .. navDb
                .GetStars(query.Destination)
                .Select(DescribeStar)
                .Where(star => IsAllowed(star, query.EquipmentSuffix, query.ArrivalRunways))
                .OrderBy(star => star.Id, StringComparer.Ordinal),
        ];

        List<Candidate> survivors = [];
        foreach (ProcedureOption sid in sids)
        {
            foreach (ProcedureOption star in stars)
            {
                ct.ThrowIfCancellationRequested();
                if ((BestPair(ctx, sid, star) is { } best) && IsPlausible(best.LengthNm, ctx.GreatCircleNm))
                {
                    survivors.Add(best);
                }
            }
        }

        List<Candidate> ranked = Rank(MergeTwins(survivors));
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "Navdata routes {Departure}-{Destination}: {Sids} SIDs x {Stars} STARs, {Survivors} plausible, {Offered} offered",
                query.Departure,
                query.Destination,
                sids.Count,
                stars.Count,
                survivors.Count,
                ranked.Count
            );
        }

        return [.. ranked.Select(candidate => ToFoundRoute(candidate, sourceName))];
    }

    /// <summary>True when <paramref name="lengthNm"/> is at most the larger of 1.25 x great circle and great circle + 50 NM.</summary>
    internal static bool IsPlausible(double lengthNm, double greatCircleNm) =>
        lengthNm <= Math.Max(MaxRatio * greatCircleNm, greatCircleNm + ShortHopAllowanceNm);

    /// <summary>
    /// True when <paramref name="suffix"/> keeps RNAV procedures, with or without its slash: unknown (null, blank, or a
    /// letter 7110.65 TBL 2-3-10 does not define), or any suffix the table does not mark as without RNAV.
    /// </summary>
    internal static bool IsRnavCapable(string? suffix)
    {
        string letters = (suffix ?? "").Trim().TrimStart('/').ToUpperInvariant();
        return (letters.Length == 0) || !NonRnavSuffixes.Contains(letters[0]);
    }

    /// <summary>
    /// True when the coded runway (a runway transition without "RW", e.g. "28L" or "01B") serves <paramref name="wanted"/>
    /// (e.g. "28L", "1L" or "RW01L"): the same runway number, compared as a number, and the same side letter, or a "B"
    /// code (every parallel). A designator that does not start with a number must equal the code.
    /// </summary>
    internal static bool RunwayMatches(string coded, string wanted)
    {
        string runway = wanted.Trim().ToUpperInvariant();
        if (runway.StartsWith("RW", StringComparison.Ordinal))
        {
            runway = runway[2..];
        }

        if (!TrySplitRunway(coded, out int codedNumber, out string codedSide) || !TrySplitRunway(runway, out int number, out string side))
        {
            return string.Equals(coded, runway, StringComparison.OrdinalIgnoreCase);
        }

        return (codedNumber == number) && (string.Equals(codedSide, side, StringComparison.OrdinalIgnoreCase) || (codedSide == "B"));
    }

    /// <summary>Splits a runway designator ("01L", "6R", "28") into its number and its side letters ("" when none).</summary>
    private static bool TrySplitRunway(string designator, out int number, out string side)
    {
        int digits = 0;
        while ((digits < designator.Length) && char.IsAsciiDigit(designator[digits]))
        {
            digits++;
        }

        side = designator[digits..].ToUpperInvariant();
        return int.TryParse(designator.AsSpan(0, digits), out number);
    }

    private static PairingContext? CreateContext(NavigationDatabase navDb, RouteQuery query)
    {
        if (string.Equals(query.Departure, query.Destination, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if ((navDb.GetAirportPosition(query.Departure) is not { } departure) || (navDb.GetAirportPosition(query.Destination) is not { } destination))
        {
            return null;
        }

        double greatCircle = Distance(departure, destination);
        return (greatCircle > 0) ? new PairingContext(navDb, query, departure, destination, greatCircle) : null;
    }

    private static ProcedureOption DescribeSid(NavigationDatabase navDb, CifpSidProcedure sid, IReadOnlyList<string> wantedRunways) =>
        new(
            sid.ProcedureId,
            sid.RouteKind,
            RunwayCodes(sid.RunwayTransitions),
            RunwaysNotCoded: (sid.RunwayTransitions.Count == 0) && (sid.CommonLegs.Count == 0),
            SidExits(navDb, sid, wantedRunways),
            TwinKey: HasTwinablePath(sid) ? TwinKey(sid) : null,
            RunwayEndFixes: RunwayOnlyEndFixes(sid)
        );

    /// <summary>
    /// A runway-only SID's last fix per coded runway: it has no common legs, no enroute transitions and is not flown on
    /// radar vectors, so the whole path after departure is the runway transition. Empty for every other SID, whose runways
    /// share one path.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> RunwayOnlyEndFixes(CifpSidProcedure sid)
    {
        bool runwayOnly =
            (sid.RunwayTransitions.Count > 0)
            && (sid.CommonLegs.Count == 0)
            && (sid.EnrouteTransitions.Count == 0)
            && (sid.RouteKind != ProcedureRouteKind.RadarVector);
        if (!runwayOnly)
        {
            return NoRunwayEndFixes;
        }

        return sid.RunwayTransitions.ToDictionary(
            transition => RunwayCode(transition.Key),
            transition => LastFix(transition.Value.Legs),
            StringComparer.Ordinal
        );
    }

    private static ProcedureOption DescribeStar(CifpStarProcedure star) =>
        new(
            star.ProcedureId,
            star.RouteKind,
            RunwayCodes(star.RunwayTransitions),
            RunwaysNotCoded: false,
            StarEntries(star),
            TwinKey: null,
            RunwayEndFixes: NoRunwayEndFixes
        );

    private static bool IsAllowed(ProcedureOption procedure, string? equipmentSuffix, IReadOnlyList<string> wantedRunways)
    {
        if ((procedure.Kind == ProcedureRouteKind.Rnav) && !IsRnavCapable(equipmentSuffix))
        {
            return false;
        }

        return (wantedRunways.Count == 0)
            || (procedure.Runways.Count == 0)
            || procedure.Runways.Any(coded => wantedRunways.Any(wanted => RunwayMatches(coded, wanted)));
    }

    private static List<string> RunwayCodes(IReadOnlyDictionary<string, CifpTransition> runwayTransitions) =>
        [.. runwayTransitions.Keys.Select(RunwayCode).Order(StringComparer.Ordinal)];

    /// <summary>
    /// A SID's exit fixes, the shortest pair with the STAR's entries winning: each enroute transition's last fix; for a
    /// radar-vector SID without enroute transitions, the exit fixes vNAS stores for it plus the no-exit form (null: the
    /// STAR entry follows the SID). The no-exit form goes straight to the entry and is never longer, so a vNAS exit fix
    /// wins only when the query's fix names it (or, on a tie, by route text). A body-only SID exits at its last common fix, else at
    /// each distinct end fix of its runway transitions, only the matching runways' when the query carries runways.
    /// </summary>
    private static List<string?> SidExits(NavigationDatabase navDb, CifpSidProcedure sid, IReadOnlyList<string> wantedRunways)
    {
        if (sid.EnrouteTransitions.Count > 0)
        {
            return JoinFixes(sid.EnrouteTransitions, LastFix);
        }

        if (sid.RouteKind == ProcedureRouteKind.RadarVector)
        {
            return VectorSidExits(navDb, sid.ProcedureId);
        }

        if (LastFix(sid.CommonLegs) is { } bodyExit)
        {
            return [bodyExit];
        }

        return JoinFixes(RunwayTransitionsFor(sid.RunwayTransitions, wantedRunways), LastFix);
    }

    /// <summary>The runway transitions serving one of <paramref name="wantedRunways"/>; all of them when it is empty.</summary>
    private static Dictionary<string, CifpTransition> RunwayTransitionsFor(
        IReadOnlyDictionary<string, CifpTransition> runwayTransitions,
        IReadOnlyList<string> wantedRunways
    ) =>
        runwayTransitions
            .Where(entry => (wantedRunways.Count == 0) || wantedRunways.Any(wanted => RunwayMatches(RunwayCode(entry.Key), wanted)))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static string RunwayCode(string transitionName) =>
        transitionName.StartsWith("RW", StringComparison.OrdinalIgnoreCase) ? transitionName[2..] : transitionName;

    /// <summary>A STAR's entry fixes: each enroute transition's first fix, else its first body fix.</summary>
    private static List<string?> StarEntries(CifpStarProcedure star)
    {
        if (star.EnrouteTransitions.Count > 0)
        {
            return JoinFixes(star.EnrouteTransitions, FirstFix);
        }

        return [FirstFix(star.CommonLegs) ?? FirstOrdinal(star.RunwayTransitions, FirstFix)];
    }

    private static List<string?> JoinFixes(IReadOnlyDictionary<string, CifpTransition> transitions, Func<IReadOnlyList<CifpLeg>, string?> pick)
    {
        List<string?> fixes =
        [
            .. transitions
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => pick(entry.Value.Legs))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        return (fixes.Count > 0) ? fixes : [null];
    }

    private static List<string?> VectorSidExits(NavigationDatabase navDb, string sidId)
    {
        string? vnasId = navDb.ResolveSidId(sidId);
        IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)> hints = ((vnasId is null) ? null : navDb.GetSidTransitions(vnasId)) ?? [];
        List<string?> exits =
        [
            .. hints
                .Select(hint => (hint.Fixes.Count > 0) ? hint.Fixes[^1] : null)
                .OfType<string>()
                .Where(fix => fix.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal),
        ];
        exits.Add(null);
        return exits;
    }

    private static string? FirstOrdinal(IReadOnlyDictionary<string, CifpTransition> transitions, Func<IReadOnlyList<CifpLeg>, string?> pick) =>
        transitions
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => pick(entry.Value.Legs))
            .FirstOrDefault(fix => fix is not null);

    private static string? LastFix(IReadOnlyList<CifpLeg> legs) => legs.LastOrDefault(leg => !string.IsNullOrEmpty(leg.FixIdentifier))?.FixIdentifier;

    private static string? FirstFix(IReadOnlyList<CifpLeg> legs) =>
        legs.FirstOrDefault(leg => !string.IsNullOrEmpty(leg.FixIdentifier))?.FixIdentifier;

    /// <summary>
    /// True when the SID has runway transitions and a path beyond them (common or enroute legs), so another SID with the
    /// same path differs from it only by runway. A SID coded with runway transitions alone (SUNNE1, OAK7) has no such
    /// path and is nobody's twin.
    /// </summary>
    private static bool HasTwinablePath(CifpSidProcedure sid) =>
        (sid.RunwayTransitions.Count > 0) && ((sid.CommonLegs.Count > 0) || (sid.EnrouteTransitions.Count > 0));

    /// <summary>A SID's path without its runway transitions: its common fixes and its enroute transitions with their fixes.</summary>
    private static string TwinKey(CifpSidProcedure sid)
    {
        IEnumerable<string> enroute = sid
            .EnrouteTransitions.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Key + ":" + string.Join(',', entry.Value.Legs.Select(leg => leg.FixIdentifier)));
        return string.Join(',', sid.CommonLegs.Select(leg => leg.FixIdentifier)) + "|" + string.Join('|', enroute);
    }

    /// <summary>
    /// The shortest flyable route joining <paramref name="sid"/> to <paramref name="star"/> over every exit and entry
    /// fix pair, ties broken by route text (ordinal); null when no pair expands to known fixes through the query's fix.
    /// </summary>
    private static Candidate? BestPair(PairingContext ctx, ProcedureOption sid, ProcedureOption star)
    {
        bool nonRnav = !IsRnavCapable(ctx.Query.EquipmentSuffix);
        Candidate? best = null;
        foreach (string? exit in sid.JoinFixes)
        {
            foreach (string? entry in star.JoinFixes)
            {
                string text = RouteText(sid.Id, exit, entry, star.Id);
                if (Measure(ctx, text) is not { } length)
                {
                    continue;
                }

                if (nonRnav && HasLongDirectLeg(ctx, text, sid))
                {
                    if (Log.IsEnabled(LogLevel.Debug))
                    {
                        Log.LogDebug("Navdata route {Route} dropped: a direct leg is over {Max} NM", text, MaxDirectLegNm);
                    }

                    continue;
                }

                if (IsBetter(length, text, best))
                {
                    best = new Candidate(sid, star, text, length, length / ctx.GreatCircleNm) { Exit = exit };
                }
            }
        }

        return best;
    }

    /// <summary>A leg of a route not flown along a published procedure: its two points and their great-circle length.</summary>
    internal readonly record struct DirectLeg(string From, string To, double Nm);

    /// <summary>
    /// The route's direct legs, the ones not flown along a published SID, STAR or transition: the departure airport to a
    /// radar-vector SID's first fix, then every leg from the end of the SID's path (a radar-vector SID's vNAS body only, its
    /// adapted-route hints not being published procedure) to the STAR. Passing does not prove the route flyable at a lower
    /// altitude, where AIM 5-1-6c.3's navaid-spacing bands are tighter.
    /// </summary>
    internal static List<DirectLeg> DirectLegs(NavigationDatabase navDb, string departure, string routeText, string sidId, ProcedureRouteKind sidKind)
    {
        List<string> path = RouteExpander.Expand(routeText, navDb, includeAllTransitionsOnMismatch: false);
        List<RouteExpander.ProcedureSpan> spans = RouteExpander.ProcedureSpans(routeText, navDb, includeAllTransitionsOnMismatch: false);
        int sidFixes =
            (sidKind == ProcedureRouteKind.RadarVector)
                ? BodyFixesAtPathHead(navDb, sidId, path)
                : spans.Where(span => span.IsSid).Select(span => span.Fixes.Count).FirstOrDefault();
        int starFixes = spans.Where(span => !span.IsSid).Select(span => span.Fixes.Count).FirstOrDefault();
        List<DirectLeg> legs = [];
        if (AirportLeg(navDb, departure, sidKind, path) is { } airportLeg)
        {
            legs.Add(airportLeg);
        }

        int starStart = path.Count - starFixes;
        for (int i = 0; (i < path.Count - 1); i++)
        {
            if (((i + 1) < sidFixes) || (i >= starStart))
            {
                continue;
            }

            if (LegBetween(navDb, path[i], path[i + 1]) is { } leg)
            {
                legs.Add(leg);
            }
        }

        return legs;
    }

    /// <summary>
    /// How many fixes at the head of <paramref name="path"/> are the radar-vector SID's vNAS body: its adapted-route hints are
    /// synthetic transitions rather than published procedure, so every leg after the body is a direct leg.
    /// </summary>
    private static int BodyFixesAtPathHead(NavigationDatabase navDb, string sidId, List<string> path)
    {
        IReadOnlyList<string> body = ((navDb.ResolveSidId(sidId) is { } vnasId) ? navDb.GetSidBody(vnasId) : null) ?? [];
        int matched = 0;
        string? previous = null;
        foreach (string fix in body)
        {
            if (string.Equals(fix, previous, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            previous = fix;
            if ((matched >= path.Count) || !string.Equals(path[matched], fix, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            matched++;
        }

        return matched;
    }

    /// <summary>
    /// The departure airport to a radar-vector SID's first fix: the vectors end with a clearance direct to it, and the
    /// airport is the only deterministic start of that leg.
    /// </summary>
    private static DirectLeg? AirportLeg(NavigationDatabase navDb, string departure, ProcedureRouteKind sidKind, List<string> path) =>
        ((sidKind == ProcedureRouteKind.RadarVector) && (path.Count > 0))
        && (navDb.GetAirportPosition(departure) is { } airport)
        && (navDb.GetFixPosition(path[0]) is { } first)
            ? new DirectLeg(departure, path[0], Distance(airport, first))
            : null;

    /// <summary>The leg between two fixes, or null when the navdata does not know one of them.</summary>
    private static DirectLeg? LegBetween(NavigationDatabase navDb, string from, string to) =>
        ((navDb.GetFixPosition(from) is { } start) && (navDb.GetFixPosition(to) is { } end)) ? new DirectLeg(from, to, Distance(start, end)) : null;

    /// <summary>True when one of the route's direct legs (see <see cref="DirectLegs"/>) is longer than <see cref="MaxDirectLegNm"/>.</summary>
    private static bool HasLongDirectLeg(PairingContext ctx, string routeText, ProcedureOption sid) =>
        DirectLegs(ctx.NavDb, ctx.Query.Departure, routeText, sid.Id, sid.Kind).Any(leg => leg.Nm > MaxDirectLegNm);

    private static bool IsBetter(double length, string text, Candidate? best) =>
        (best is null) || (length < best.LengthNm) || ((length == best.LengthNm) && (string.CompareOrdinal(text, best.RouteText) < 0));

    /// <summary>"SID EXIT ENTRY STAR": a missing exit or entry is left out, and an entry equal to the exit is written once.</summary>
    private static string RouteText(string sidId, string? exit, string? entry, string starId)
    {
        List<string> tokens = [sidId];
        if (exit is not null)
        {
            tokens.Add(exit);
        }

        if ((entry is not null) && !string.Equals(entry, exit, StringComparison.OrdinalIgnoreCase))
        {
            tokens.Add(entry);
        }

        tokens.Add(starId);
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// The route's length in NM, departure airport to every expanded fix to destination airport; null when it expands to
    /// nothing, to a fix the navdata does not know, or not through the query's fix.
    /// </summary>
    private static double? Measure(PairingContext ctx, string routeText)
    {
        List<string> fixes = RouteExpander.Expand(routeText, ctx.NavDb, includeAllTransitionsOnMismatch: false);
        if (fixes.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(ctx.Query.Fix) && !fixes.Contains(ctx.Query.Fix.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        double length = 0;
        (double Lat, double Lon) previous = ctx.Departure;
        foreach (string fix in fixes)
        {
            if (ctx.NavDb.GetFixPosition(fix) is not { } position)
            {
                if (Log.IsEnabled(LogLevel.Debug))
                {
                    Log.LogDebug("Navdata route {Route} dropped: the navdata does not know its fix {Fix}", routeText, fix);
                }

                return null;
            }

            length += Distance(previous, position);
            previous = position;
        }

        return length + Distance(previous, ctx.Destination);
    }

    private static double Distance((double Lat, double Lon) from, (double Lat, double Lon) to) =>
        GeoMath.DistanceNm(from.Lat, from.Lon, to.Lat, to.Lon);

    /// <summary>
    /// Merges each route of a SID with the same STAR's route of its twin (same path, other runways): the ordinal-first
    /// SID keeps the row and names the other as its twin.
    /// </summary>
    private static List<Candidate> MergeTwins(List<Candidate> survivors)
    {
        var merged = new List<Candidate>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Candidate candidate in survivors.OrderBy(c => c.Sid.Id, StringComparer.Ordinal).ThenBy(c => c.Star.Id, StringComparer.Ordinal))
        {
            string? key = candidate.Sid.TwinKey is null ? null : candidate.Sid.TwinKey + "#" + candidate.Star.Id;
            if ((key is null) || !byKey.TryGetValue(key, out int index))
            {
                if (key is not null)
                {
                    byKey[key] = merged.Count;
                }

                merged.Add(candidate);
            }
            else if (merged[index].Twin is null)
            {
                merged[index] = merged[index] with { Twin = candidate.Sid };
            }
        }

        return merged;
    }

    /// <summary>
    /// Sorts by ratio, length, SID and STAR (ordinal), then fills the first page with routes whose SID and STAR each
    /// hold fewer than two of its rows; every other route follows in the same order.
    /// </summary>
    private static List<Candidate> Rank(List<Candidate> candidates)
    {
        var firstPage = new List<Candidate>();
        var rest = new List<Candidate>();
        var perProcedure = new Dictionary<string, int>(StringComparer.Ordinal);
        IEnumerable<Candidate> sorted = candidates
            .OrderBy(c => c.Ratio)
            .ThenBy(c => c.LengthNm)
            .ThenBy(c => c.Sid.Id, StringComparer.Ordinal)
            .ThenBy(c => c.Star.Id, StringComparer.Ordinal);
        foreach (Candidate candidate in sorted)
        {
            string sidKey = "SID " + candidate.Sid.Id;
            string starKey = "STAR " + candidate.Star.Id;
            bool fits =
                (firstPage.Count < FirstPageSize)
                && (perProcedure.GetValueOrDefault(sidKey) < MaxRowsPerProcedureOnFirstPage)
                && (perProcedure.GetValueOrDefault(starKey) < MaxRowsPerProcedureOnFirstPage);
            if (!fits)
            {
                rest.Add(candidate);
                continue;
            }

            firstPage.Add(candidate);
            perProcedure[sidKey] = perProcedure.GetValueOrDefault(sidKey) + 1;
            perProcedure[starKey] = perProcedure.GetValueOrDefault(starKey) + 1;
        }

        return [.. firstPage, .. rest];
    }

    private static FoundRoute ToFoundRoute(Candidate candidate, string sourceName)
    {
        var published = new PublishedRouteInfo(
            candidate.Sid.Id,
            candidate.Twin?.Id,
            candidate.Star.Id,
            candidate.LengthNm,
            candidate.Ratio,
            candidate.Sid.Kind,
            candidate.Star.Kind,
            RunwaysEndingAt(candidate),
            candidate.Twin?.Runways ?? [],
            candidate.Star.Runways,
            candidate.Sid.RunwaysNotCoded
        );
        return new FoundRoute(candidate.RouteText, Count: 0, [sourceName], published, Preferred: null);
    }

    /// <summary>
    /// The SID's runways whose transition ends at the row's exit: a runway-only SID's transitions end at different fixes,
    /// so another runway's transition does not fly this row. Every other SID, and a row with no exit fix, keeps its full
    /// runway list.
    /// </summary>
    private static IReadOnlyList<string> RunwaysEndingAt(Candidate candidate)
    {
        if ((candidate.Exit is not { } exit) || (candidate.Sid.RunwayEndFixes.Count == 0))
        {
            return candidate.Sid.Runways;
        }

        return
        [
            .. candidate.Sid.Runways.Where(runway =>
                candidate.Sid.RunwayEndFixes.TryGetValue(runway, out string? endFix)
                && string.Equals(endFix, exit, StringComparison.OrdinalIgnoreCase)
            ),
        ];
    }
}
