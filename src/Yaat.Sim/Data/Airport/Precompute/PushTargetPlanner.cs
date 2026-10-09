using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// Computes an airport's precomputed push targets: for every named push-back stand with a heading and every design group
/// with an envelope (a taxi-out stand, <see cref="StandDepartures.StandDepartureOf"/>, gets an empty list), the bare
/// <c>PUSH &lt;twy&gt;</c> and <c>PUSH $spot</c> targets within <see cref="TugMovePlanner.MaxGoalDistanceFt"/> that the tug
/// planner accepts, each with the plan it made. The plans are made exactly as a live push off the stand would make them,
/// with no other aircraft about, and with the footprint of the group's envelope
/// (<see cref="DesignGroupEnvelopes.FootprintOf(DesignGroupEnvelope)"/>), never the FAA database. The movement-area
/// classification is built from the sidecars the caller passes, the same ones the entry's key hashes. An aircraft held
/// where a push left it has no stand: <see cref="ComputeHeld"/> plans the same kinds of target from its held pose, as a
/// push that does not start at a stand, leaving out any target that would end where it already is.
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

    /// <summary>A held target ending within this distance of the held position leaves the aircraft where it is, feet.</summary>
    public const double StayPutDistanceFt = 10.0;

    /// <summary>A held target ending within this angle of the held heading leaves the aircraft where it is, degrees.</summary>
    public const double StayPutHeadingDeg = 10.0;

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
    /// The request a live push from <paramref name="start"/> makes for <paramref name="goal"/>, with no other aircraft
    /// about: off a stand, or from where an aircraft is held after a pushback.
    /// </summary>
    /// <param name="start">The position and true heading the push starts from: the stand's, or the held pose.</param>
    /// <param name="footprint">The aircraft's dimensions.</param>
    /// <param name="movementArea">The movement-area classification the plan reads.</param>
    /// <param name="goal">The goal.</param>
    /// <param name="startsAtStand">Whether <paramref name="start"/> is a stand the aircraft pushes off.</param>
    /// <returns>The request.</returns>
    public static TugRequest RequestFor(
        PushbackPose start,
        AircraftFootprint footprint,
        MovementAreaClassification movementArea,
        TugGoal goal,
        bool startsAtStand
    ) =>
        new()
        {
            Start = start,
            StartsAtStand = startsAtStand,
            Footprint = footprint,
            MovementArea = movementArea,
            Goals = [goal],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };

    /// <summary>
    /// <see cref="Compute"/> for one stand and one design group, planned on the calling thread: the entry
    /// <see cref="ComputeStands"/> produces for that stand and group, with only that group's envelope planned. Null when
    /// the layout carries no such stand, the stand has no heading, or the envelopes carry no envelope for
    /// <paramref name="group"/>. The airport-wide warnings <see cref="StandDepartures.WarnAboutOverrides"/> holds are
    /// not emitted here, because a caller planning one stand as a menu opens does not want them on every call; a caller
    /// that wants them runs that once per layout.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="designGroupEnvelopes">The envelope per design group.</param>
    /// <param name="sidecars">The airport sidecars the movement-area classification is built from.</param>
    /// <param name="standName">The stand's name, ordinal.</param>
    /// <param name="group">The aircraft's design group.</param>
    /// <returns>The stand's entry for that group, or null when there is nothing to plan it from.</returns>
    public static PushTargetEntry? ComputeStand(
        AirportGroundLayout layout,
        DesignGroupEnvelopes designGroupEnvelopes,
        AirportSidecarCatalog sidecars,
        string standName,
        AirplaneDesignGroup group
    )
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(designGroupEnvelopes);
        ArgumentNullException.ThrowIfNull(sidecars);
        ArgumentException.ThrowIfNullOrWhiteSpace(standName);

        GroundNode? stand = Stands(layout).FirstOrDefault(n => string.Equals(n.Name, standName, StringComparison.Ordinal));
        if (stand is null)
        {
            Log.LogDebug("Stand {Stand} at {Airport} is not a named parking node; no push targets", standName, layout.AirportId);
            return null;
        }

        if (stand.TrueHeading is not { } heading)
        {
            Log.LogDebug("Stand {Name} at {Airport} has no heading; no push targets", stand.Name, layout.AirportId);
            return null;
        }

        if (EnvelopeFor(designGroupEnvelopes, group, $"stand {stand.Name}", layout) is not { } envelope)
        {
            return null;
        }

        if (StandDepartures.StandDepartureOf(layout, stand, sidecars) == StandDeparture.TaxiOut)
        {
            Log.LogDebug("Stand {Name} at {Airport} is a taxi-out stand; no push targets", stand.Name, layout.AirportId);
            return new PushTargetEntry(stand.Name!, envelope.Group, []);
        }

        var classification = MovementAreaClassification.Build(layout, sidecars);
        var origin = OriginContext.ForStand(layout, classification, stand, heading.Degrees);
        return new PushTargetEntry(stand.Name!, envelope.Group, PlanGroup(origin, envelope));
    }

    /// <summary>
    /// <see cref="ComputeStand"/> for an aircraft held where a push left it: the taxiway and spot targets around
    /// <paramref name="heldPose"/>, planned on the calling thread from that pose as a push that does not start at a stand,
    /// each taxiway's exit found from the held position. A target whose plan would end where the aircraft already is
    /// (within <see cref="StayPutDistanceFt"/> and <see cref="StayPutHeadingDeg"/> of <paramref name="heldPose"/>, as a push
    /// to the spot it sits on) is left out. Null when the envelopes carry no envelope for <paramref name="group"/>.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="designGroupEnvelopes">The envelope per design group.</param>
    /// <param name="sidecars">The airport sidecars the movement-area classification is built from.</param>
    /// <param name="heldPose">The aircraft's position and true heading where it is held.</param>
    /// <param name="group">The aircraft's design group.</param>
    /// <returns>
    /// The held position's targets for that group, sorted as a stand's are, or null when there is nothing to plan them with.
    /// </returns>
    public static IReadOnlyList<PrecomputedPushTarget>? ComputeHeld(
        AirportGroundLayout layout,
        DesignGroupEnvelopes designGroupEnvelopes,
        AirportSidecarCatalog sidecars,
        PushbackPose heldPose,
        AirplaneDesignGroup group
    )
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(designGroupEnvelopes);
        ArgumentNullException.ThrowIfNull(sidecars);

        string description = HeldDescription(heldPose);
        if (EnvelopeFor(designGroupEnvelopes, group, description, layout) is not { } envelope)
        {
            return null;
        }

        var classification = MovementAreaClassification.Build(layout, sidecars);
        return PlanGroup(OriginContext.ForHeld(layout, classification, heldPose, description), envelope);
    }

    /// <summary>The envelope of <paramref name="group"/>, or null, logged, when the envelopes carry none.</summary>
    private static DesignGroupEnvelope? EnvelopeFor(
        DesignGroupEnvelopes designGroupEnvelopes,
        AirplaneDesignGroup group,
        string originDescription,
        AirportGroundLayout layout
    )
    {
        DesignGroupEnvelope? envelope = designGroupEnvelopes.Envelopes.FirstOrDefault(e =>
            string.Equals(e.Group, group.ToString(), StringComparison.Ordinal)
        );
        if (envelope is null)
        {
            Log.LogDebug(
                "Design group {Group} has no envelope; no push targets from {Origin} at {Airport}",
                group,
                originDescription,
                layout.AirportId
            );
        }

        return envelope;
    }

    /// <summary>A held pose as the log names it: <c>the held position {lat},{lon} heading {deg}</c>.</summary>
    /// <param name="heldPose">The aircraft's position and true heading where it is held.</param>
    /// <returns>The description, invariant culture.</returns>
    public static string HeldDescription(PushbackPose heldPose) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"the held position {heldPose.Position.Lat:F6},{heldPose.Position.Lon:F6} heading {heldPose.NoseTrueDeg:F0}"
        );

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
        List<PushTargetEntry>[] perStand = PlanInParallel(
            stands,
            parallelOptions,
            stand => PlanStand(layout, designGroupEnvelopes, sidecars, classification, stand)
        );

        return [.. perStand.SelectMany(e => e).OrderBy(e => e.StandName, StringComparer.Ordinal).ThenBy(e => e.DesignGroup, StringComparer.Ordinal)];
    }

    /// <summary>One stand's entries, one per design group, and none at all when the stand has no heading.</summary>
    private static List<PushTargetEntry> PlanStand(
        AirportGroundLayout layout,
        DesignGroupEnvelopes designGroupEnvelopes,
        AirportSidecarCatalog sidecars,
        MovementAreaClassification classification,
        GroundNode stand
    )
    {
        if (stand.TrueHeading is not { } heading)
        {
            Log.LogDebug("Stand {Name} at {Airport} has no heading; no push targets", stand.Name, layout.AirportId);
            return [];
        }

        if (StandDepartures.StandDepartureOf(layout, stand, sidecars) == StandDeparture.TaxiOut)
        {
            Log.LogDebug("Stand {Name} at {Airport} is a taxi-out stand; no push targets", stand.Name, layout.AirportId);
            return [.. designGroupEnvelopes.Envelopes.Select(e => new PushTargetEntry(stand.Name!, e.Group, []))];
        }

        var context = OriginContext.ForStand(layout, classification, stand, heading.Degrees);
        return [.. designGroupEnvelopes.Envelopes.Select(e => new PushTargetEntry(stand.Name!, e.Group, PlanGroup(context, e)))];
    }

    /// <summary>
    /// The origin's targets for one design group's envelope, planned as a live push from the origin, sorted and capped.
    /// </summary>
    private static List<PrecomputedPushTarget> PlanGroup(OriginContext origin, DesignGroupEnvelope envelope)
    {
        var context = new PlanContext
        {
            Layout = origin.Layout,
            MovementArea = origin.MovementArea,
            OriginDescription = origin.Description,
            Group = envelope.Group,
            Start = origin.Start,
            StartsAtStand = origin.StartsAtStand,
            Footprint = DesignGroupEnvelopes.FootprintOf(envelope),
        };
        List<PrecomputedPushTarget> planned = [.. origin.Candidates.Select(c => Plan(context, c)).OfType<PrecomputedPushTarget>()];
        return Capped(Sorted(planned), context);
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

    /// <summary>The origin's taxiway goals, the same for every design group, by name, each exit found from the origin's position.</summary>
    private static List<OriginCandidate> TaxiwayCandidates(
        AirportGroundLayout layout,
        MovementAreaClassification classification,
        PushbackPose start,
        string originDescription
    )
    {
        var candidates = new List<OriginCandidate>();
        IEnumerable<string> taxiways = layout
            .Edges.OfType<GroundEdge>()
            .Where(e => !e.IsRamp && !e.IsRunwayCenterline && !e.IsRunwayCrossingLink)
            .Where(e => e.Nodes.Any(n => WithinSearch(start, n)))
            .Select(e => e.TaxiwayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
        foreach (string taxiway in taxiways)
        {
            if (layout.FindExitByTaxiway(start.Position, taxiway) is not { } exitNode)
            {
                Log.LogDebug("PUSH {Taxiway} from {Origin} at {Airport} dropped: no exit onto it", taxiway, originDescription, layout.AirportId);
                continue;
            }

            PushTargetKind kind = classification.IsMovementArea(taxiway) ? PushTargetKind.Taxiway : PushTargetKind.Taxilane;
            candidates.Add(new OriginCandidate(kind, taxiway, TugGoal.StraightBackTo(exitNode, taxiway), TaxiwayFacings(exitNode, taxiway)));
        }

        return candidates;
    }

    /// <summary>The origin's spot goals, the same for every design group, by name.</summary>
    private static List<OriginCandidate> SpotCandidates(AirportGroundLayout layout, PushbackPose start, string originDescription)
    {
        var candidates = new List<OriginCandidate>();
        IEnumerable<string> spots = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Spot) && !string.IsNullOrEmpty(n.Name) && WithinSearch(start, n))
            .Select(n => n.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
        foreach (string spot in spots)
        {
            if (layout.FindSpotNodeByName(spot) is not { } spotNode)
            {
                Log.LogDebug(
                    "PUSH ${Spot} from {Origin} at {Airport} dropped: no spot node resolves by that name",
                    spot,
                    originDescription,
                    layout.AirportId
                );
                continue;
            }

            candidates.Add(new OriginCandidate(PushTargetKind.Spot, spot, TugGoal.Spot(spotNode), []));
        }

        return candidates;
    }

    private static PrecomputedPushTarget? Plan(PlanContext context, OriginCandidate candidate)
    {
        string command = candidate.Kind == PushTargetKind.Spot ? $"PUSH ${candidate.Name}" : $"PUSH {candidate.Name}";
        if (
            TugMovePlanner.Plan(
                context.Layout,
                RequestFor(context.Start, context.Footprint, context.MovementArea, candidate.Goal, context.StartsAtStand),
                out string refusal
            )
            is not { } plan
        )
        {
            Log.LogDebug(
                "{Command} from {Origin} at {Airport} for group {Group} dropped: {Refusal}",
                command,
                context.OriginDescription,
                context.Layout.AirportId,
                context.Group,
                refusal
            );
            return null;
        }

        if (!context.StartsAtStand && StaysPut(plan.End, context.Start))
        {
            Log.LogDebug(
                "{Command} from {Origin} at {Airport} for group {Group} dropped: it ends where the aircraft already is",
                command,
                context.OriginDescription,
                context.Layout.AirportId,
                context.Group
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
                "{Command} from {Origin} at {Airport} for group {Group} dropped: its {PathFt} ft path is over the {CapFt:0.0} ft cap",
                target.Command,
                context.OriginDescription,
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
                "{Origin} at {Airport} has no push targets for group {Group}",
                context.OriginDescription,
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

        double bearing = GeoMath.BearingTo(exitNode.Position, EdgeGeometry.PointsFrom(edge, exitNode)[1]);
        return [.. new[] { RoundBearing(bearing), RoundBearing(bearing + 180.0) }.Order()];
    }

    private static double RoundBearing(double degrees)
    {
        double rounded = Math.Round(new TrueHeading(degrees).Degrees, 1);
        return rounded >= 360.0 ? 0.0 : rounded;
    }

    private static bool WithinSearch(PushbackPose start, GroundNode node) =>
        (GeoMath.DistanceNm(start.Position, node.Position) * GeoMath.FeetPerNm) <= CandidateSearchFt;

    /// <summary>
    /// Whether a plan ending at <paramref name="end"/> leaves an aircraft at <paramref name="start"/> where it is: within
    /// <see cref="StayPutDistanceFt"/> and <see cref="StayPutHeadingDeg"/> of it.
    /// </summary>
    private static bool StaysPut(PushbackPose end, PushbackPose start) =>
        ((GeoMath.DistanceNm(start.Position, end.Position) * GeoMath.FeetPerNm) <= StayPutDistanceFt)
        && (new TrueHeading(start.NoseTrueDeg).AbsAngleTo(new TrueHeading(end.NoseTrueDeg)) <= StayPutHeadingDeg);

    private sealed record OriginCandidate(PushTargetKind Kind, string Name, TugGoal Goal, double[] Facings);

    /// <summary>
    /// What every plan of one origin (a stand, or where an aircraft is held after a push) shares, whatever the design
    /// group: how the log names it, the pose the moves start from, whether that start is a stand push-off, and the
    /// candidate goals searched around the start's position, so an origin's candidates are built once.
    /// </summary>
    private sealed record OriginContext(
        AirportGroundLayout Layout,
        MovementAreaClassification MovementArea,
        string Description,
        PushbackPose Start,
        bool StartsAtStand
    )
    {
        public List<OriginCandidate> Candidates { get; } =
        [.. TaxiwayCandidates(Layout, MovementArea, Start, Description), .. SpotCandidates(Layout, Start, Description)];

        /// <summary>A push off <paramref name="stand"/>, a named parking node, on its true heading.</summary>
        public static OriginContext ForStand(
            AirportGroundLayout layout,
            MovementAreaClassification classification,
            GroundNode stand,
            double headingDeg
        ) => new(layout, classification, $"stand {stand.Name}", new PushbackPose(stand.Position, headingDeg), StartsAtStand: true);

        /// <summary>A push from where an aircraft is held after a pushback, which starts at no stand.</summary>
        public static OriginContext ForHeld(
            AirportGroundLayout layout,
            MovementAreaClassification classification,
            PushbackPose heldPose,
            string description
        ) => new(layout, classification, description, heldPose, StartsAtStand: false);
    }

    /// <summary>What every plan of one origin and design group shares.</summary>
    private sealed record PlanContext
    {
        public required AirportGroundLayout Layout { get; init; }

        public required MovementAreaClassification MovementArea { get; init; }

        public required string OriginDescription { get; init; }

        public required string Group { get; init; }

        public required PushbackPose Start { get; init; }

        public required bool StartsAtStand { get; init; }

        public required AircraftFootprint Footprint { get; init; }
    }
}
