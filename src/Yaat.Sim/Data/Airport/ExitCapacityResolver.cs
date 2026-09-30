using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// One <see cref="ExitCapacityRule"/> resolved against a concrete layout: the stretch of the rule's taxiway from the
/// landing runway's exit hold-short (<see cref="ExitBarNodeId"/>) to the parallel runway's hold-short reached along it
/// (<see cref="ParallelBarNodeId"/>). Compared by reference — each layout instance resolves its own segments.
/// </summary>
public sealed class ExitCapacitySegment(ExitCapacityRule rule, int exitBarNodeId, int parallelBarNodeId, string parallelRunwayId)
{
    public ExitCapacityRule Rule { get; } = rule;

    /// <summary>The landing runway's hold-short on the rule's taxiway — the bar an arrival exits across.</summary>
    public int ExitBarNodeId { get; } = exitBarNodeId;

    /// <summary>The parallel runway's near-side hold-short an aircraft pulls up to after exiting.</summary>
    public int ParallelBarNodeId { get; } = parallelBarNodeId;

    /// <summary>The parallel runway the segment ends at, e.g. "28L/10R".</summary>
    public string ParallelRunwayId { get; } = parallelRunwayId;

    /// <summary>True when the rule governs arrivals landing on <paramref name="runwayDesignator"/>.</summary>
    public bool AppliesToRunway(string runwayDesignator) =>
        string.Equals(RunwayIdentifier.NormalizeDesignator(runwayDesignator.Trim().ToUpperInvariant()), Rule.Runway, StringComparison.Ordinal);

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
}

/// <summary>
/// Resolves the per-airport <see cref="ExitCapacityRule"/>s against a concrete <see cref="AirportGroundLayout"/> into
/// <see cref="ExitCapacitySegment"/>s, using the same forward walk as the automatic pull-up to a parallel runway
/// (<see cref="AirportGroundLayout.FindParallelRunwayCrossing"/>). Layouts come from the live vNAS airport map, so a rule
/// that no longer resolves to exactly one segment logs an Error and is dropped for that layout — exits there behave as
/// if the rule were absent — rather than failing a running session; <c>ExitCapacitySidecarTests</c> is the check that
/// fails on it. Resolved segments are cached per layout instance, so the Error logs once per layout, mirroring
/// <see cref="AdwResolver"/>.
/// </summary>
public static class ExitCapacityResolver
{
    private static readonly ILogger Log = SimLog.CreateLogger("ExitCapacityResolver");

    private static readonly ConditionalWeakTable<AirportGroundLayout, IReadOnlyList<ExitCapacitySegment>> Cache = [];

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
            if (ResolveRule(layout, rule) is { } segment)
            {
                segments.Add(segment);
            }
        }

        return segments;
    }

    private static ExitCapacitySegment? ResolveRule(AirportGroundLayout layout, ExitCapacityRule rule)
    {
        List<ExitCapacitySegment> found = [];
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if ((node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } runwayId) && runwayId.Contains(rule.Runway))
            {
                AddSegmentsFromExitBar(layout, rule, node, found);
            }
        }

        if (found.Count != 1)
        {
            string detail =
                found.Count == 0
                    ? $"no {rule.Runway} hold-short on taxiway {rule.Taxiway} leads along it to a parallel runway's hold-short"
                    : $"it matches {found.Count} segments ({string.Join(", ", found.Select(s => $"#{s.ExitBarNodeId}→#{s.ParallelBarNodeId}"))}), expected one";
            Log.LogError(
                "Airport {Airport}: exitCapacity entry for runway {Rwy} taxiway {Twy} does not resolve: {Detail}. The rule is ignored on this "
                    + "layout, so exits there behave as if it were absent. Fix the entry in the airport's sidecar under Data/ARTCCs/*/Airports/.",
                layout.AirportId,
                rule.Runway,
                rule.Taxiway,
                detail
            );
            return null;
        }

        return LogResolved(layout, rule, found[0]);
    }

    /// <summary>
    /// Adds to <paramref name="found"/> every segment that starts at <paramref name="exitBar"/>, one of the landing
    /// runway's hold-shorts: each edge off it on the rule's taxiway that leads forward along the taxiway to a parallel
    /// runway's hold-short, once per distinct parallel bar.
    /// </summary>
    private static void AddSegmentsFromExitBar(AirportGroundLayout layout, ExitCapacityRule rule, GroundNode exitBar, List<ExitCapacitySegment> found)
    {
        foreach (IGroundEdge edge in exitBar.Edges)
        {
            if (edge.IsRunwayCenterline || !edge.MatchesTaxiway(rule.Taxiway))
            {
                continue;
            }

            (
                GroundNode NearHoldShort,
                GroundNode FarHoldShort,
                string ParallelRunwayId,
                List<GroundNode> PullUpPath,
                List<GroundNode> CrossingPath
            )? crossing = layout.FindParallelRunwayCrossing(exitBar, edge.OtherNode(exitBar), rule.Taxiway, rule.Runway);
            if ((crossing is { } xing) && !found.Any(s => (s.ExitBarNodeId == exitBar.Id) && (s.ParallelBarNodeId == xing.NearHoldShort.Id)))
            {
                found.Add(new ExitCapacitySegment(rule, exitBar.Id, xing.NearHoldShort.Id, xing.ParallelRunwayId));
            }
        }
    }

    private static ExitCapacitySegment LogResolved(AirportGroundLayout layout, ExitCapacityRule rule, ExitCapacitySegment segment)
    {
        Log.LogDebug(
            "{Airport}: exit capacity {Rwy} {Twy} resolved to HS #{Exit} → HS #{Parallel} ({ParallelRwy})",
            layout.AirportId,
            rule.Runway,
            rule.Taxiway,
            segment.ExitBarNodeId,
            segment.ParallelBarNodeId,
            segment.ParallelRunwayId
        );
        return segment;
    }
}
