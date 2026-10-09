using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// Computes an airport's precomputed push targets: for every named push-back stand with a heading and every design group
/// with an envelope (a taxi-out stand, <see cref="StandDepartures.StandDepartureOf"/>, gets an empty list), the bare
/// <c>PUSH &lt;twy&gt;</c> and <c>PUSH $spot</c> targets within <see cref="TugMovePlanner.MaxGoalDistanceFt"/> that the tug
/// planner accepts, each with the plan it made. The plans are made exactly as a live push off the stand would make them,
/// with no other aircraft about, and with the footprint of the group's envelope
/// (<see cref="DesignGroupEnvelopes.FootprintOf(DesignGroupEnvelope)"/>), never the FAA database. The movement-area
/// classification is built from the sidecars the caller passes, the same ones the entry's key hashes.
/// </summary>
public static class PushTargetPlanner
{
    private static readonly ILogger Log = SimLog.CreateLogger("PushTargetPlanner");

    /// <summary>How far from a stand a taxiway edge's node or a spot may lie and still be a candidate, feet.</summary>
    public const double CandidateSearchFt = TugMovePlanner.MaxGoalDistanceFt;

    /// <summary>
    /// A target whose path is longer than this many times the envelope's length (and longer than
    /// <see cref="MinPathCapFt"/>) is a tow-out, not a push, and is dropped.
    /// </summary>
    public const double MaxPathLengthsPerAircraftLength = 3.0;

    /// <summary>The cap's floor, feet: a small group's push out to a taxilane a few hundred feet away is still a push.</summary>
    public const double MinPathCapFt = 600.0;

    /// <summary>
    /// The longest path a target of <paramref name="footprint"/> may have and still be stored, feet: the greater of
    /// <see cref="MaxPathLengthsPerAircraftLength"/> times its length and <see cref="MinPathCapFt"/>.
    /// </summary>
    /// <param name="footprint">The envelope's footprint.</param>
    /// <returns>The cap, feet.</returns>
    public static double PathCapFt(AircraftFootprint footprint) => Math.Max(MaxPathLengthsPerAircraftLength * footprint.LengthFt, MinPathCapFt);

    /// <summary>
    /// The push targets of every stand and design group: stands are the named parking nodes (a name several nodes carry
    /// is planned from the first by node id); a stand with no heading is skipped. Targets sort by kind (taxilane, taxiway,
    /// spot), then path length, then name; entries by stand, then design group, both ordinal.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="designGroupEnvelopes">The envelope per design group.</param>
    /// <param name="sidecars">The airport sidecars the movement-area classification is built from.</param>
    /// <param name="parallelOptions">
    /// How the stands are planned at once (<see cref="PlanInParallel{TItem, TResult}"/>). The output is identical for every value.
    /// </param>
    /// <returns>One entry per stand and design group, targets refused by the planner or over the length cap left out.</returns>
    public static IReadOnlyList<PushTargetEntry> Compute(
        AirportGroundLayout layout,
        DesignGroupEnvelopes designGroupEnvelopes,
        AirportSidecarCatalog sidecars,
        ParallelOptions parallelOptions
    )
    {
        ArgumentNullException.ThrowIfNull(layout);
        return ComputeFor(layout, designGroupEnvelopes, sidecars, Stands(layout), parallelOptions);
    }

    /// <summary>
    /// <see cref="Compute"/> for the named stands only (ordinal names; a name the layout does not carry yields nothing). A
    /// whole airport takes minutes, so a caller after a few stands' targets asks for those.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="designGroupEnvelopes">The envelope per design group.</param>
    /// <param name="sidecars">The airport sidecars the movement-area classification is built from.</param>
    /// <param name="standNames">The stands to compute.</param>
    /// <param name="parallelOptions">
    /// How the stands are planned at once (<see cref="PlanInParallel{TItem, TResult}"/>). The output is identical for every value.
    /// </param>
    /// <returns>One entry per named stand and design group, sorted as <see cref="Compute"/> sorts them.</returns>
    public static IReadOnlyList<PushTargetEntry> ComputeStands(
        AirportGroundLayout layout,
        DesignGroupEnvelopes designGroupEnvelopes,
        AirportSidecarCatalog sidecars,
        IReadOnlySet<string> standNames,
        ParallelOptions parallelOptions
    )
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(standNames);
        return ComputeFor(layout, designGroupEnvelopes, sidecars, [.. Stands(layout).Where(n => standNames.Contains(n.Name!))], parallelOptions);
    }

    /// <summary>
    /// The planner's stand loop: <paramref name="plan"/> for each item, at most
    /// <see cref="ParallelOptions.MaxDegreeOfParallelism"/> at once on <see cref="ParallelOptions.TaskScheduler"/>, the
    /// results in item order whatever order they finish in. The loop also runs bodies on the calling thread, so a caller
    /// capping concurrency with a scheduler calls this from a task on that scheduler.
    /// </summary>
    /// <typeparam name="TItem">The item's type.</typeparam>
    /// <typeparam name="TResult">The result's type.</typeparam>
    /// <param name="items">The items.</param>
    /// <param name="parallelOptions">The loop's options; a degree of 1 plans the items one after another.</param>
    /// <param name="plan">The work on one item.</param>
    /// <returns>One result per item, in item order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The degree of parallelism is unbounded (below 1).</exception>
    public static TResult[] PlanInParallel<TItem, TResult>(IReadOnlyList<TItem> items, ParallelOptions parallelOptions, Func<TItem, TResult> plan)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelOptions.MaxDegreeOfParallelism, 1);

        var results = new TResult[items.Count];
        Parallel.ForEach(items, parallelOptions, (item, _, index) => results[(int)index] = plan(item));
        return results;
    }

    /// <summary>
    /// The request a live push off <paramref name="start"/>'s stand makes for <paramref name="goal"/>, with no other
    /// aircraft about.
    /// </summary>
    /// <param name="start">The stand's position and heading.</param>
    /// <param name="footprint">The aircraft's dimensions.</param>
    /// <param name="movementArea">The movement-area classification the plan reads.</param>
    /// <param name="goal">The goal.</param>
    /// <returns>The request.</returns>
    public static TugRequest RequestFor(TugPose start, AircraftFootprint footprint, MovementAreaClassification movementArea, TugGoal goal) =>
        new()
        {
            Start = start,
            StartsAtStand = true,
            Footprint = footprint,
            MovementArea = movementArea,
            Goals = [goal],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };

    private static List<PushTargetEntry> ComputeFor(
        AirportGroundLayout layout,
        DesignGroupEnvelopes designGroupEnvelopes,
        AirportSidecarCatalog sidecars,
        List<GroundNode> stands,
        ParallelOptions parallelOptions
    )
    {
        ArgumentNullException.ThrowIfNull(designGroupEnvelopes);
        ArgumentNullException.ThrowIfNull(sidecars);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelOptions.MaxDegreeOfParallelism, 1);

        var classification = MovementAreaClassification.Build(layout, sidecars);
        StandDepartures.WarnAboutOverrides(layout, sidecars);
        List<PushTargetEntry>[] perStand = PlanInParallel(stands, parallelOptions, PlanStand);

        return [.. perStand.SelectMany(e => e).OrderBy(e => e.StandName, StringComparer.Ordinal).ThenBy(e => e.DesignGroup, StringComparer.Ordinal)];

        List<PushTargetEntry> PlanStand(GroundNode stand)
        {
            var entries = new List<PushTargetEntry>();
            if (stand.TrueHeading is not { } heading)
            {
                Log.LogDebug("Stand {Name} at {Airport} has no heading; no push targets", stand.Name, layout.AirportId);
                return entries;
            }

            if (StandDepartures.StandDepartureOf(layout, stand, sidecars) == StandDeparture.TaxiOut)
            {
                Log.LogDebug("Stand {Name} at {Airport} is a taxi-out stand; no push targets", stand.Name, layout.AirportId);
                entries.AddRange(designGroupEnvelopes.Envelopes.Select(e => new PushTargetEntry(stand.Name!, e.Group, [])));
                return entries;
            }

            List<StandCandidate> candidates = [.. TaxiwayCandidates(layout, classification, stand), .. SpotCandidates(layout, stand)];
            foreach (DesignGroupEnvelope envelope in designGroupEnvelopes.Envelopes)
            {
                var context = new PlanContext
                {
                    Layout = layout,
                    MovementArea = classification,
                    StandName = stand.Name!,
                    Group = envelope.Group,
                    Start = new TugPose(stand.Position, heading.Degrees),
                    Footprint = DesignGroupEnvelopes.FootprintOf(envelope),
                };
                List<PrecomputedPushTarget> planned = [.. candidates.Select(c => Plan(context, c)).OfType<PrecomputedPushTarget>()];
                entries.Add(new PushTargetEntry(stand.Name!, envelope.Group, Capped(Sorted(planned), context)));
            }

            return entries;
        }
    }

    /// <summary>
    /// The named parking nodes, one per name in ordinal name order: a name several nodes carry is planned from the one with
    /// the lowest node id, with a warning.
    /// </summary>
    private static List<GroundNode> Stands(AirportGroundLayout layout)
    {
        var stands = new List<GroundNode>();
        IEnumerable<IGrouping<string, GroundNode>> byName = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && !string.IsNullOrEmpty(n.Name))
            .GroupBy(n => n.Name!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (IGrouping<string, GroundNode> named in byName)
        {
            List<GroundNode> nodes = [.. named.OrderBy(n => n.Id)];
            if (nodes.Count > 1)
            {
                Log.LogWarning(
                    "Stand name {Name} at {Airport} is used by {Count} nodes; push targets use node {Id}",
                    named.Key,
                    layout.AirportId,
                    nodes.Count,
                    nodes[0].Id
                );
            }

            stands.Add(nodes[0]);
        }

        return stands;
    }

    /// <summary>The stand's taxiway goals, the same for every design group, by name.</summary>
    private static List<StandCandidate> TaxiwayCandidates(AirportGroundLayout layout, MovementAreaClassification classification, GroundNode stand)
    {
        var candidates = new List<StandCandidate>();
        IEnumerable<string> taxiways = layout
            .Edges.OfType<GroundEdge>()
            .Where(e => !e.IsRamp && !e.IsRunwayCenterline && !e.IsRunwayCrossingLink)
            .Where(e => e.Nodes.Any(n => WithinSearch(stand, n)))
            .Select(e => e.TaxiwayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
        foreach (string taxiway in taxiways)
        {
            if (layout.FindExitByTaxiway(stand.Position, taxiway) is not { } exitNode)
            {
                Log.LogDebug("PUSH {Taxiway} from stand {Stand} at {Airport} dropped: no exit onto it", taxiway, stand.Name, layout.AirportId);
                continue;
            }

            PushTargetKind kind = classification.IsMovementArea(taxiway) ? PushTargetKind.Taxiway : PushTargetKind.Taxilane;
            candidates.Add(new StandCandidate(kind, taxiway, TugGoal.StraightBackTo(exitNode, taxiway), TaxiwayFacings(exitNode, taxiway)));
        }

        return candidates;
    }

    /// <summary>The stand's spot goals, the same for every design group, by name.</summary>
    private static List<StandCandidate> SpotCandidates(AirportGroundLayout layout, GroundNode stand)
    {
        var candidates = new List<StandCandidate>();
        IEnumerable<string> spots = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Spot) && !string.IsNullOrEmpty(n.Name) && WithinSearch(stand, n))
            .Select(n => n.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
        foreach (string spot in spots)
        {
            if (layout.FindSpotNodeByName(spot) is not { } spotNode)
            {
                Log.LogDebug(
                    "PUSH ${Spot} from stand {Stand} at {Airport} dropped: no spot node resolves by that name",
                    spot,
                    stand.Name,
                    layout.AirportId
                );
                continue;
            }

            candidates.Add(new StandCandidate(PushTargetKind.Spot, spot, TugGoal.Spot(spotNode), []));
        }

        return candidates;
    }

    private static PrecomputedPushTarget? Plan(PlanContext context, StandCandidate candidate)
    {
        string command = candidate.Kind == PushTargetKind.Spot ? $"PUSH ${candidate.Name}" : $"PUSH {candidate.Name}";
        if (
            TugMovePlanner.Plan(
                context.Layout,
                RequestFor(context.Start, context.Footprint, context.MovementArea, candidate.Goal),
                out string refusal
            )
            is not { } plan
        )
        {
            Log.LogDebug(
                "{Command} from stand {Stand} at {Airport} for group {Group} dropped: {Refusal}",
                command,
                context.StandName,
                context.Layout.AirportId,
                context.Group,
                refusal
            );
            return null;
        }

        string note = plan.TaxiwayApproach switch
        {
            TugTaxiwayApproach.Alongside => "alongside",
            TugTaxiwayApproach.Across => "across",
            _ => "",
        };
        return new PrecomputedPushTarget
        {
            Kind = candidate.Kind,
            Name = candidate.Name,
            Note = note,
            Command = command,
            Facings = candidate.Facings,
            PathLengthFt = Math.Round(plan.Moves.Sum(m => m.PathLengthFt), 1),
            Moves = [.. plan.Moves.Select(PushMoveEntry.From)],
        };
    }

    private static List<PrecomputedPushTarget> Sorted(List<PrecomputedPushTarget> targets) =>
        [.. targets.OrderBy(t => t.Kind).ThenBy(t => t.PathLengthFt).ThenBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary><paramref name="sorted"/> without the targets over the length cap (<see cref="PathCapFt"/>), possibly none.</summary>
    private static List<PrecomputedPushTarget> Capped(List<PrecomputedPushTarget> sorted, PlanContext context)
    {
        double capFt = PathCapFt(context.Footprint);
        foreach (PrecomputedPushTarget target in sorted.Where(t => t.PathLengthFt > capFt))
        {
            Log.LogDebug(
                "{Command} from stand {Stand} at {Airport} for group {Group} dropped: its {PathFt} ft path is over the {CapFt:0.0} ft cap",
                target.Command,
                context.StandName,
                context.Layout.AirportId,
                context.Group,
                target.PathLengthFt,
                capFt
            );
        }

        List<PrecomputedPushTarget> kept = [.. sorted.Where(t => t.PathLengthFt <= capFt)];
        if (kept.Count == 0)
        {
            Log.LogDebug(
                "Stand {Stand} at {Airport} has no push targets for group {Group}",
                context.StandName,
                context.Layout.AirportId,
                context.Group
            );
        }

        return kept;
    }

    /// <summary>
    /// The two true bearings along <paramref name="taxiway"/> at <paramref name="exitNode"/>, ascending and rounded to 0.1°,
    /// toward the first point of the first edge of the taxiway that meets the node: its first intermediate point when it
    /// has one, else its far node. Empty when no edge of the taxiway meets the node.
    /// </summary>
    private static double[] TaxiwayFacings(GroundNode exitNode, string taxiway)
    {
        if (exitNode.Edges.OfType<GroundEdge>().FirstOrDefault(e => e.MatchesTaxiway(taxiway)) is not { } edge)
        {
            return [];
        }

        double bearing = GeoMath.BearingTo(exitNode.Position, TugMovePlanner.EdgePointsFrom(edge, exitNode)[1]);
        return [.. new[] { RoundBearing(bearing), RoundBearing(bearing + 180.0) }.Order()];
    }

    private static double RoundBearing(double degrees)
    {
        double rounded = Math.Round(new TrueHeading(degrees).Degrees, 1);
        return rounded >= 360.0 ? 0.0 : rounded;
    }

    private static bool WithinSearch(GroundNode stand, GroundNode node) =>
        (GeoMath.DistanceNm(stand.Position, node.Position) * GeoMath.FeetPerNm) <= CandidateSearchFt;

    private sealed record StandCandidate(PushTargetKind Kind, string Name, TugGoal Goal, double[] Facings);

    /// <summary>What every plan of one stand and design group shares.</summary>
    private sealed record PlanContext
    {
        public required AirportGroundLayout Layout { get; init; }

        public required MovementAreaClassification MovementArea { get; init; }

        public required string StandName { get; init; }

        public required string Group { get; init; }

        public required TugPose Start { get; init; }

        public required AircraftFootprint Footprint { get; init; }
    }
}
