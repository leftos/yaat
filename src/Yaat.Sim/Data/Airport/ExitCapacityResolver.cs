using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// One <see cref="ExitCapacityRule"/> resolved against a concrete layout: the stretch of the rule's taxiway from the
/// rule's runway's hold-short (<see cref="ExitBarNodeId"/>) to the parallel runway's hold-short reached along it
/// (<see cref="ParallelBarNodeId"/>), with the walked nodes from one bar to the other (<see cref="PathNodes"/>). A
/// crossover rule's segment is shared by both runways' arrivals and stands for the pavement between its two bars, so
/// either bar is an exit bar (<see cref="ExitBarFor"/>). Compared by reference — each layout instance resolves its own
/// segments.
/// </summary>
public sealed class ExitCapacitySegment(
    ExitCapacityRule rule,
    int exitBarNodeId,
    int parallelBarNodeId,
    string parallelRunwayId,
    IReadOnlyList<GroundNode> pathNodes
)
{
    public ExitCapacityRule Rule { get; } = rule;

    /// <summary>
    /// The hold-short of the rule's <see cref="ExitCapacityRule.Runway"/> on the rule's taxiway — the bar its arrivals
    /// exit across.
    /// </summary>
    public int ExitBarNodeId { get; } = exitBarNodeId;

    /// <summary>
    /// The parallel runway's near-side hold-short an aircraft pulls up to after exiting the rule's runway; for a
    /// crossover rule, also the bar arrivals on <see cref="ExitCapacityRule.CrossoverRunway"/> exit across.
    /// </summary>
    public int ParallelBarNodeId { get; } = parallelBarNodeId;

    /// <summary>The parallel runway the segment ends at, seen from the rule's runway, e.g. "28L/10R".</summary>
    public string ParallelRunwayId { get; } = parallelRunwayId;

    /// <summary>The walked nodes from <see cref="ExitBarNodeId"/> to <see cref="ParallelBarNodeId"/>, both bars included.</summary>
    public IReadOnlyList<GroundNode> PathNodes { get; } = pathNodes;

    /// <summary>
    /// The length in feet of the walked edges from bar to bar (<see cref="PathNodes"/>): the pavement the aircraft on the
    /// segment share nose to tail.
    /// </summary>
    public double StripLengthFt { get; } = WalkedLengthFt(pathNodes);

    /// <summary>
    /// A circle holding every point <see cref="ContainsPosition"/> can accept: centred on the mean of the walked nodes, its
    /// radius the farthest node's distance plus <see cref="AirportGroundLayout.OnTaxiEdgeMaxOffsetFt"/>.
    /// </summary>
    private readonly (LatLon Centre, double RadiusFt) _bounds = BoundingCircle(pathNodes);

    /// <summary>True when the rule governs arrivals landing on <paramref name="runwayDesignator"/>.</summary>
    public bool AppliesToRunway(string runwayDesignator)
    {
        string runway = Normalize(runwayDesignator);
        return string.Equals(runway, Rule.Runway, StringComparison.Ordinal) || string.Equals(runway, Rule.CrossoverRunway, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bar an arrival on <paramref name="runwayDesignator"/> exits across into the segment: <see cref="ParallelBarNodeId"/>
    /// for a crossover rule's second runway, <see cref="ExitBarNodeId"/> otherwise.
    /// </summary>
    public int ExitBarFor(string runwayDesignator) =>
        string.Equals(Normalize(runwayDesignator), Rule.CrossoverRunway, StringComparison.Ordinal) ? ParallelBarNodeId : ExitBarNodeId;

    /// <summary>The segment's bar at the other end from <paramref name="bar"/>, one of its two bars.</summary>
    public int OtherBar(int bar) => (bar == ExitBarNodeId) ? ParallelBarNodeId : ExitBarNodeId;

    /// <summary>The bearing from the segment's other bar straight to <paramref name="bar"/>, one of its two bars.</summary>
    public TrueHeading BearingToward(int bar) =>
        (bar == ExitBarNodeId)
            ? new TrueHeading(GeoMath.BearingTo(PathNodes[^1].Position, PathNodes[0].Position))
            : new TrueHeading(GeoMath.BearingTo(PathNodes[0].Position, PathNodes[^1].Position));

    /// <summary>
    /// True when <paramref name="position"/> lies on the segment's pavement: within
    /// <see cref="AirportGroundLayout.OnTaxiEdgeMaxOffsetFt"/> of one of the walked edges between consecutive
    /// <see cref="PathNodes"/>, ends included. A position outside the segment's bounding circle is rejected on one distance.
    /// </summary>
    public bool ContainsPosition(LatLon position)
    {
        if ((GeoMath.DistanceNm(position, _bounds.Centre) * GeoMath.FeetPerNm) > _bounds.RadiusFt)
        {
            return false;
        }

        for (int i = 1; i < PathNodes.Count; i++)
        {
            if (GeoMath.DistanceToSegmentFt(position, PathNodes[i - 1].Position, PathNodes[i].Position) <= AirportGroundLayout.OnTaxiEdgeMaxOffsetFt)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How far along the walked path from <paramref name="bar"/>, one of the segment's two bars, the point of the path
    /// nearest <paramref name="position"/> lies, in feet, between 0 and <see cref="StripLengthFt"/>.
    /// </summary>
    public double DistanceAlongFromBarFt(int bar, LatLon position)
    {
        double nearestOffsetNm = double.MaxValue;
        double fromExitBarNm = 0;
        double edgeStartNm = 0;
        for (int i = 1; i < PathNodes.Count; i++)
        {
            LatLon from = PathNodes[i - 1].Position;
            LatLon to = PathNodes[i].Position;
            (LatLon foot, double alongNm, bool _) = GeoMath.FootOfPerpendicular(position, from, to);
            double offsetNm = GeoMath.DistanceNm(position, foot);
            if (offsetNm < nearestOffsetNm)
            {
                nearestOffsetNm = offsetNm;
                fromExitBarNm = edgeStartNm + alongNm;
            }

            edgeStartNm += GeoMath.DistanceNm(from, to);
        }

        double fromExitBarFt = Math.Clamp(fromExitBarNm * GeoMath.FeetPerNm, 0, StripLengthFt);
        return (bar == ExitBarNodeId) ? fromExitBarFt : StripLengthFt - fromExitBarFt;
    }

    /// <summary>True when <paramref name="nodeId"/> is one of the segment's two bars.</summary>
    public bool IsBar(int nodeId) => (nodeId == ExitBarNodeId) || (nodeId == ParallelBarNodeId);

    /// <summary>
    /// True when <paramref name="cwt"/> is at or below the rule's threshold: a letter at or after it (A heaviest, I
    /// lightest). An unknown CWT counts as above the threshold.
    /// </summary>
    public bool IsAtOrBelowThreshold(string? cwt) =>
        (cwt is { Length: 1 }) && (string.CompareOrdinal(cwt.ToUpperInvariant(), Rule.CwtThreshold) >= 0);

    /// <summary>
    /// How many aircraft the segment holds when <paramref name="arrivalCwt"/> joins its occupants:
    /// <see cref="ExitCapacityRule.MaxAircraft"/> while the arrival and every occupant are at or below the threshold
    /// (<paramref name="occupantsAtOrBelowThreshold"/>, each judged by <see cref="IsAtOrBelowThreshold"/>), otherwise
    /// <see cref="ExitCapacityRule.MaxAircraftAboveCwt"/>.
    /// </summary>
    public int CapacityFor(string? arrivalCwt, bool occupantsAtOrBelowThreshold) =>
        (IsAtOrBelowThreshold(arrivalCwt) && occupantsAtOrBelowThreshold) ? Rule.MaxAircraft : Rule.MaxAircraftAboveCwt;

    private static string Normalize(string runwayDesignator) => RunwayIdentifier.NormalizeDesignator(runwayDesignator.Trim().ToUpperInvariant());

    private static double WalkedLengthFt(IReadOnlyList<GroundNode> nodes)
    {
        double lengthNm = 0;
        for (int i = 1; i < nodes.Count; i++)
        {
            lengthNm += GeoMath.DistanceNm(nodes[i - 1].Position, nodes[i].Position);
        }

        return lengthNm * GeoMath.FeetPerNm;
    }

    private static (LatLon Centre, double RadiusFt) BoundingCircle(IReadOnlyList<GroundNode> nodes)
    {
        var centre = new LatLon(nodes.Average(n => n.Position.Lat), nodes.Average(n => n.Position.Lon));
        double farthestFt = nodes.Max(n => GeoMath.DistanceNm(centre, n.Position)) * GeoMath.FeetPerNm;
        return (centre, farthestFt + AirportGroundLayout.OnTaxiEdgeMaxOffsetFt);
    }
}

/// <summary>
/// Resolves the per-airport <see cref="ExitCapacityRule"/>s against a concrete <see cref="AirportGroundLayout"/> into
/// <see cref="ExitCapacitySegment"/>s, using the same forward walk as the automatic pull-up to a parallel runway
/// (<see cref="AirportGroundLayout.FindParallelRunwayCrossing"/>). A crossover rule is walked bar to near bar
/// (<see cref="AirportGroundLayout.FindParallelNearHoldShort"/>) from each runway's side, and both walks must name the
/// same pair of bars. Layouts come from the live vNAS airport map, so a rule that no longer
/// resolves to exactly one segment logs an Error and is dropped for that layout — exits there behave as if the rule were
/// absent — rather than failing a running session; <c>ExitCapacitySidecarTests</c> is the check that fails on it.
/// Resolved segments are cached per layout instance, so the Error logs once per layout, mirroring
/// <see cref="AdwResolver"/>.
/// </summary>
public static class ExitCapacityResolver
{
    private static readonly ILogger Log = SimLog.CreateLogger("ExitCapacityResolver");

    private static readonly ConditionalWeakTable<AirportGroundLayout, IReadOnlyList<ExitCapacitySegment>> Cache = [];

    private const string IgnoredAdvice =
        "is ignored on this layout, so exits there behave as if it were absent. "
        + "Fix the entry in the airport's sidecar under Data/ARTCCs/*/Airports/.";

    /// <summary>One walk from a runway's hold-short along a taxiway to a parallel runway's near hold-short.</summary>
    private sealed record Stretch(GroundNode ExitBar, GroundNode ParallelBar, string ParallelRunwayId, List<GroundNode> Path);

    /// <summary>
    /// What a stretch search looks for: <see cref="Taxiway"/> from one of <see cref="Runway"/>'s hold-shorts to a parallel
    /// runway's near hold-short — <see cref="ParallelRunway"/>'s when given, which also makes it a crossover walk.
    /// </summary>
    private sealed record StretchQuery(string Taxiway, string Runway, string? ParallelRunway);

    /// <summary>
    /// Exit-capacity segments for <paramref name="layout"/>, cached. Reads the airport's rules from the global
    /// <see cref="NavigationDatabase"/>; empty when no database is initialized or the airport has none.
    /// </summary>
    public static IReadOnlyList<ExitCapacitySegment> GetSegments(AirportGroundLayout layout) => Cache.GetValue(layout, BuildForLayout);

    private static IReadOnlyList<ExitCapacitySegment> BuildForLayout(AirportGroundLayout layout)
    {
        NavigationDatabase? db = NavigationDatabase.InstanceOrNull;
        IReadOnlyList<ExitCapacityRule> rules = db?.AirportSidecars.GetExitCapacity(layout.AirportId) ?? [];
        return Resolve(layout, rules);
    }

    /// <summary>
    /// Pure resolution of <paramref name="rules"/> against <paramref name="layout"/>. Unit-testable without a database. A
    /// rule that does not resolve to exactly one segment logs an Error and is left out of the result.
    /// </summary>
    public static IReadOnlyList<ExitCapacitySegment> Resolve(AirportGroundLayout layout, IReadOnlyList<ExitCapacityRule> rules)
    {
        if (rules.Count == 0)
        {
            return [];
        }

        var segments = new List<ExitCapacitySegment>(rules.Count);
        foreach (ExitCapacityRule rule in rules)
        {
            ExitCapacitySegment? segment = (rule.CrossoverRunway is { } other) ? ResolveCrossover(layout, rule, other) : ResolveRule(layout, rule);
            if (segment is not null)
            {
                segments.Add(segment);
            }
        }

        return segments;
    }

    private static ExitCapacitySegment? ResolveRule(AirportGroundLayout layout, ExitCapacityRule rule)
    {
        List<Stretch> found = FindStretches(layout, rule.Taxiway, rule.Runway, parallelRunway: null);
        if (found.Count != 1)
        {
            string detail =
                found.Count == 0
                    ? $"no {rule.Runway} hold-short on taxiway {rule.Taxiway} leads along it to a parallel runway's hold-short"
                    : $"it matches {found.Count} segments ({Describe(found)}), expected one";
            Log.LogError(
                "Airport {Airport}: exitCapacity entry for runway {Rwy} taxiway {Twy} does not resolve: {Detail}. The rule " + IgnoredAdvice,
                layout.AirportId,
                rule.Runway,
                rule.Taxiway,
                detail
            );
            return null;
        }

        return LogResolved(layout, rule, ToSegment(rule, found[0]));
    }

    /// <summary>
    /// Resolves a crossover rule into one shared segment: walks from <see cref="ExitCapacityRule.Runway"/>'s side and from
    /// <paramref name="other"/>'s, each of which must find exactly one stretch to the other runway, and both must name the
    /// same pair of bars.
    /// </summary>
    private static ExitCapacitySegment? ResolveCrossover(AirportGroundLayout layout, ExitCapacityRule rule, string other)
    {
        List<Stretch> fromFirst = FindStretches(layout, rule.Taxiway, rule.Runway, other);
        List<Stretch> fromSecond = FindStretches(layout, rule.Taxiway, other, rule.Runway);
        if ((fromFirst.Count == 1) && (fromSecond.Count == 1) && SameBars(fromFirst[0], fromSecond[0]))
        {
            return LogResolved(layout, rule, ToSegment(rule, fromFirst[0]));
        }

        Log.LogError(
            "Airport {Airport}: exitCapacity crossover {Rwy}/{Other} taxiway {Twy} does not resolve to one shared segment: from {FirstRwy}: "
                + "{FromFirst}; from {SecondRwy}: {FromSecond}. The taxiway "
                + IgnoredAdvice,
            layout.AirportId,
            rule.Runway,
            other,
            rule.Taxiway,
            rule.Runway,
            DescribeWalk(fromFirst, rule.Taxiway, rule.Runway, other),
            other,
            DescribeWalk(fromSecond, rule.Taxiway, other, rule.Runway)
        );
        return null;
    }

    private static bool SameBars(Stretch a, Stretch b) => (a.ExitBar.Id == b.ParallelBar.Id) && (a.ParallelBar.Id == b.ExitBar.Id);

    private static ExitCapacitySegment ToSegment(ExitCapacityRule rule, Stretch stretch) =>
        new(rule, stretch.ExitBar.Id, stretch.ParallelBar.Id, stretch.ParallelRunwayId, stretch.Path);

    private static string Describe(List<Stretch> stretches) => string.Join(", ", stretches.Select(s => $"#{s.ExitBar.Id}→#{s.ParallelBar.Id}"));

    private static string DescribeWalk(List<Stretch> stretches, string taxiway, string runway, string parallelRunway) =>
        stretches.Count switch
        {
            0 => $"no {runway} hold-short on taxiway {taxiway} leads along it to a {parallelRunway} hold-short",
            1 => Describe(stretches),
            _ => $"{stretches.Count} segments ({Describe(stretches)})",
        };

    /// <summary>
    /// Every stretch of <paramref name="taxiway"/> from one of <paramref name="runway"/>'s hold-shorts forward along it to
    /// a parallel runway's near hold-short — <paramref name="parallelRunway"/>'s when given — once per distinct pair of bars.
    /// </summary>
    private static List<Stretch> FindStretches(AirportGroundLayout layout, string taxiway, string runway, string? parallelRunway)
    {
        var query = new StretchQuery(taxiway, runway, parallelRunway);
        List<Stretch> found = [];
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if ((node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } runwayId) && runwayId.Contains(runway))
            {
                AddStretchesFromExitBar(layout, query, node, found);
            }
        }

        return found;
    }

    /// <summary>
    /// Adds to <paramref name="found"/> every stretch that starts at <paramref name="exitBar"/>, one of the landing
    /// runway's hold-shorts: each edge off it on the query's taxiway that leads forward along the taxiway to a parallel
    /// runway's hold-short (the query's parallel runway's when given), once per distinct parallel bar.
    /// </summary>
    private static void AddStretchesFromExitBar(AirportGroundLayout layout, StretchQuery query, GroundNode exitBar, List<Stretch> found)
    {
        foreach (IGroundEdge edge in exitBar.Edges)
        {
            if (edge.IsRunwayCenterline || !edge.MatchesTaxiway(query.Taxiway))
            {
                continue;
            }

            if (
                (WalkToParallelBar(layout, query, exitBar, edge.OtherNode(exitBar)) is { } stretch)
                && !found.Any(s => (s.ExitBar.Id == exitBar.Id) && (s.ParallelBar.Id == stretch.ParallelBar.Id))
            )
            {
                found.Add(stretch);
            }
        }
    }

    /// <summary>
    /// One walk from <paramref name="exitBar"/> away from <paramref name="comeFrom"/>. A one-runway rule (no parallel
    /// runway in the query) uses the auto-pull-up's own walk, which also requires the taxiway to carry on across the
    /// parallel runway; a crossover is only the pavement from bar to near bar, so it walks that far and requires the bar
    /// reached to be the query's parallel runway's.
    /// </summary>
    private static Stretch? WalkToParallelBar(AirportGroundLayout layout, StretchQuery query, GroundNode exitBar, GroundNode comeFrom)
    {
        if (query.ParallelRunway is not { } parallelRunway)
        {
            return (layout.FindParallelRunwayCrossing(exitBar, comeFrom, query.Taxiway, query.Runway) is { } xing)
                ? new Stretch(exitBar, xing.NearHoldShort, xing.ParallelRunwayId, xing.PullUpPath)
                : null;
        }

        return (
            (layout.FindParallelNearHoldShort(exitBar, comeFrom, query.Taxiway, query.Runway) is { } near)
            && (near.NearHoldShort.RunwayId?.Contains(parallelRunway) == true)
        )
            ? new Stretch(exitBar, near.NearHoldShort, near.ParallelRunwayId.ToString(), near.Path)
            : null;
    }

    private static ExitCapacitySegment LogResolved(AirportGroundLayout layout, ExitCapacityRule rule, ExitCapacitySegment segment)
    {
        Log.LogDebug(
            "{Airport}: exit capacity {Rwy} {Twy} resolved to HS #{Exit} → HS #{Parallel} ({ParallelRwy}){Shared}",
            layout.AirportId,
            rule.Runway,
            rule.Taxiway,
            segment.ExitBarNodeId,
            segment.ParallelBarNodeId,
            segment.ParallelRunwayId,
            rule.IsCrossover ? $", shared with {rule.CrossoverRunway}" : ""
        );
        return segment;
    }
}
