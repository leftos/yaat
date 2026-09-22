using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A taxi clearance honoured by cutting across the ramp: the lane the aircraft repositions onto, the graph
/// node where it re-acquires painted lines, the length of the free-space leg, and the full route (that
/// virtual leg followed by the graph route resolved from <see cref="TargetNode"/>).
/// </summary>
public sealed record RampLaneRepositionPlan(GroundNode TargetNode, string Lane, double CrossingFt, TaxiRoute Route);

/// <summary>
/// A parking/spot clearance honoured by cutting across the ramp at the far end: the cleared lane is taxied as far
/// as <see cref="FromNode"/>, the pilot then drives straight across open apron to <see cref="ToNode"/> on the
/// destination's own lane (<see cref="DestinationLane"/>) and follows the graph to the stand. The route is the head
/// along the clearance, the free-space leg between two layout nodes, and the graph tail.
/// </summary>
public sealed record RampLaneDestinationCutPlan(
    GroundNode FromNode,
    GroundNode ToNode,
    string Lane,
    string DestinationLane,
    double CrossingFt,
    TaxiRoute Route
);

/// <summary>
/// Lets the pilot switch between parallel ramp taxilanes the ground map does not connect. SFO's Terminal 1
/// ramp has M3 / M4 / M5 side by side with open apron between them and no painted connectors; the graph
/// joins M4 to the rest of the field only at M1, so <c>TAXI M4 …</c> from a gate on M3's alley — or from an
/// aircraft already rolling on M3 — resolves to "M4 is not connected". Ramps and aprons are normally
/// nonmovement area: ATC does not specify the path there, and routing is the pilot's discretion in
/// coordination with ramp control (7110.65 §3-7-2 NOTE 2; AIM 4-3-20.g.7), and the apron between parallel
/// taxilanes is aircraft-usable pavement (AIM 2-3-4.c.2). So the pilot drives straight across to the named
/// lane and picks up the graph route there. The numeric suffix is the clue: only a lane sharing the current
/// lane's letter prefix (M3 → M4, M5) qualifies, the cut is bounded by <see cref="MaxCrossingFt"/>, and it must
/// not cross a runway holding-position marking, a runway centerline, or any taxiway outside that lane family —
/// a taxiway across the field stays a hard rejection. The plan is a real <see cref="TaxiRoute"/> whose first
/// segment is a <see cref="VirtualNode"/> leg, so the navigator, snapshots and the client overlay need nothing
/// special. Known limitation: a heavy at a nose-in stand still pivots out instead of being pushed first (YAAT
/// has no pushback model for a gate <c>TAXI</c>); the diagonal is the right net displacement, not the tug leg.
/// </summary>
public static class RampLaneReposition
{
    private static readonly ILogger Log = SimLog.CreateLogger("RampLaneReposition");

    /// <summary>
    /// Sanity bound on the free-space leg. No regulation limits apron transit; the family / apron-only /
    /// runway guards do the real work, and this only refuses cuts long enough that "adjacent lanes on one
    /// ramp" has stopped being true (≈2× a B777 span, ≈1.8× the widest SFO M-lane spacing; B20S → M4 is ≈410 ft).
    /// </summary>
    public const double MaxCrossingFt = 450.0;

    /// <summary>Candidates within this margin of the nearest reachable lane node compete on heading, not distance alone.</summary>
    private const double CandidateMarginFt = 50.0;

    /// <summary>A crossing that starts more than this far behind a rolling aircraft's nose is a reversal, not a lane switch.</summary>
    private const double MaxReversalDeg = 100.0;

    /// <summary>How far a parked aircraft's ramp lead-out is walked to find the lane it opens onto.</summary>
    private const int MaxLeadOutHops = 12;

    /// <summary>How many points on the cleared lane, nearest the stand first, are tried as the start of a destination cut.</summary>
    private const int MaxCutOrigins = 4;

    /// <summary>An aircraft this close to a parking node is treated as parked there for lane inference.</summary>
    private const double ParkedToleranceFt = 30.0;

    /// <summary>
    /// Floor on <see cref="SpotAlignmentRunFt"/>. Not a published figure: 100 ft is a fuselage length for the
    /// regional jets and turboprops that use SFO's spot lanes, and a run shorter than one of those is short
    /// for anything. The length of an aircraft the FAA database has no record of comes from
    /// <see cref="HoldShortAnnotator.CwtFallbackLengthFt"/>, which reads the type's wake category.
    /// </summary>
    private const double DefaultAircraftLengthFt = 100.0;

    /// <summary>
    /// How much run along its own lane an arrival at a spot needs before the spot node. A spot marking is
    /// entered along the lane it sits on — that is what makes the aircraft end up parked along the lane rather
    /// than across it — so a crossing may only land far enough up the lane for the aircraft to be straight by
    /// the time it reaches the marking. One fuselage of run, because that is the length that has to end up on
    /// the line: a 242 ft widebody landing 110 ft up the lane is still crossing it when it reaches the marking,
    /// where a 106 ft regional jet is straight.
    /// </summary>
    /// <param name="aircraftLengthFt">Fuselage length of the arriving aircraft, in feet.</param>
    /// <returns>Required run in feet.</returns>
    private static double SpotAlignmentRunFt(double aircraftLengthFt) => Math.Max(DefaultAircraftLengthFt, aircraftLengthFt);

    /// <summary>
    /// How many times longer than the straight drive the remaining graph route must be before a resolved route is
    /// re-cut. Measured, not published: nothing in 7110.65 or the AIM says when a pilot leaves the painted line for
    /// the apron, so the bound comes from the cases either side of it — SFO D2 → $5A cuts at 5.36, B20S → M4 at
    /// 3.75 and mid-M3 → M4 at 11.62, while OAK <c>V T TE @23</c> (1.10) and <c>V T TC @22</c> (≈1.0) must not.
    /// </summary>
    private const double MinDetourRatio = 2.0;

    /// <summary>
    /// How much shorter the cut must make the route, on top of <see cref="MinDetourRatio"/>. Also measured: it is
    /// what separates OAK <c>V T TE @23</c>'s 19 ft from SFO D2 → $5A's 310 ft, and keeps a short lane whose ratio
    /// happens to look good from being replaced by a crossing that saves the pilot nothing.
    /// </summary>
    private const double MinDetourSavingFt = 100.0;

    /// <summary>How far from the aircraft the lane it currently occupies may be when inferred from geometry.</summary>
    private const double CurrentLaneMaxFt = 150.0;

    /// <summary>
    /// Plan a lane switch for a clearance whose first taxiway the resolver reported as not connected. Null when
    /// the failure is something else, the taxiway is not a sibling ramp lane of the aircraft's current lane, no
    /// lane node lies within <see cref="MaxCrossingFt"/> across open apron, or the graph route from that node
    /// does not resolve — the caller then falls through to its other recoveries.
    /// </summary>
    public static RampLaneRepositionPlan? TryPlan(
        AirportGroundLayout layout,
        LatLon position,
        TrueHeading heading,
        string? currentTaxiway,
        IReadOnlyList<string> path,
        PathfindingFailure failure,
        ExplicitPathOptions options,
        AircraftCategory category
    )
    {
        if ((path.Count == 0) || !IsLaneUnreachableFailure(failure, path[0]))
        {
            return null;
        }

        string lane = path[0];
        if (!IsRampTaxilane(layout, lane))
        {
            return null;
        }

        (string? currentLane, bool rolling) = ResolveCurrentLane(layout, position, currentTaxiway);
        if ((currentLane is null) || !SameLaneFamily(currentLane, lane))
        {
            Log.LogDebug("[Reposition] {Lane} is not a sibling of current lane {Current}; no ramp cut", lane, currentLane ?? "(none)");
            return null;
        }

        HashSet<string> family = LaneFamily(layout, lane);
        List<GroundNode> candidates = RankTargets(layout, position, rolling ? heading.Degrees : null, lane, family);
        if (candidates.Count == 0)
        {
            Log.LogDebug("[Reposition] no {Lane} node within {Max:F0} ft of the aircraft across open apron", lane, MaxCrossingFt);
            return null;
        }

        foreach (GroundNode target in candidates)
        {
            TaxiRoute? tail = TaxiPathfinder.ResolveExplicitPathDetailed(
                layout,
                target.Id,
                [.. path],
                out PathfindingFailure? tailFailure,
                options,
                category
            );
            if (tail is null)
            {
                Log.LogDebug("[Reposition] route from {Lane} node {Node} does not resolve: {Reason}", lane, target.Id, tailFailure?.HumanMessage);
                continue;
            }

            // Re-acquiring the painted line on a spot marking with nothing left to taxi would end the clearance
            // on the spot at the crossing's own heading — across the lane. A spot is entered along its lane.
            if ((target.Type == GroundNodeType.Spot) && (tail.Segments.Count == 0))
            {
                Log.LogDebug("[Reposition] {Lane} node {Node} is a spot the clearance would end on from the side; trying the next", lane, target.Id);
                continue;
            }

            return BuildPlan(position, currentLane, lane, target, tail);
        }

        return null;
    }

    private static RampLaneRepositionPlan BuildPlan(LatLon position, string currentLane, string lane, GroundNode target, TaxiRoute tail)
    {
        double crossingFt = DistanceFt(position, target.Position);
        TaxiRouteSegment crossing = VirtualNode.CreateSegment(VirtualNode.Create(position.Lat, position.Lon), target, tail.Segments[0].TaxiwayName);
        var route = new TaxiRoute
        {
            Segments = [crossing, .. tail.Segments],
            HoldShortPoints = tail.HoldShortPoints,
            Warnings = tail.Warnings,
            MandatoryConnectorCount = tail.MandatoryConnectorCount,
            DestinationParking = tail.DestinationParking,
            DestinationSpot = tail.DestinationSpot,
        };
        Log.LogInformation(
            "[Reposition] cutting across the ramp from {Current} onto {Lane} at node {Node} ({Ft:F0} ft), then {Summary}",
            currentLane,
            lane,
            target.Id,
            crossingFt,
            tail.ToSummary()
        );
        return new RampLaneRepositionPlan(target, lane, crossingFt, route);
    }

    /// <summary>
    /// Plan a cut at the destination end of a parking / spot clearance whose named path resolved but whose last
    /// lane the graph does not join to the stand's lane: OAK <c>TAXI V T TE @22</c>, where TE's ramp end and TC's
    /// (spot 22's lane) are ~330 ft apart across open apron with no painted connector. The aircraft taxis the
    /// clearance to the point on the last lane nearest the stand, crosses to the nearest reachable node on the
    /// stand's lane (or the RAMP lead-in the stand hangs off), and follows the graph in. The caller decides when
    /// a cut is wanted (the server: the resolver reported the destination unreachable without blaming a taxiway;
    /// the client overlay: the graph route it rebuilt doubles back). Null when the last lane or the stand's lane
    /// is not a ramp taxilane, the two are not siblings, no crossing within <see cref="MaxCrossingFt"/> over open
    /// apron exists, or either graph half does not resolve — the caller then falls through to its other recoveries.
    /// </summary>
    public static RampLaneDestinationCutPlan? TryPlanDestinationCut(
        AirportGroundLayout layout,
        int startNodeId,
        IReadOnlyList<string> path,
        GroundNode destination,
        ExplicitPathOptions options,
        AircraftCategory category,
        double aircraftLengthFt
    )
    {
        if (destination.Type is not (GroundNodeType.Parking or GroundNodeType.Spot or GroundNodeType.Helipad))
        {
            return null;
        }

        string? lane = path.LastOrDefault(t => !t.StartsWith('#'));
        if ((lane is null) || !IsRampTaxilane(layout, lane))
        {
            return null;
        }

        (string? destinationLane, List<GroundNode>? leadIn) = LeadOut(destination);
        if ((destinationLane is null) || !IsRampTaxilane(layout, destinationLane) || !AreSiblingLanes(lane, destinationLane))
        {
            Log.LogDebug(
                "[Reposition] {Dest} sits on {DestLane}, not a sibling ramp lane of {Lane}; no destination cut",
                destination.Name,
                destinationLane ?? "(none)",
                lane
            );
            return null;
        }

        HashSet<string> family = LaneFamily(layout, lane);
        var targets = layout
            .GetNodesOnTaxiway(destinationLane)
            .Where(n => HasStraightEdgeOf(n, destinationLane))
            .Concat(leadIn)
            .Where(n => !AirportGroundLayout.HasRunwayCenterlineEdge(n))
            .ToList();
        var origins = layout
            .GetNodesOnTaxiway(lane)
            .Where(n => HasStraightEdgeOf(n, lane) && !AirportGroundLayout.HasRunwayCenterlineEdge(n))
            .OrderBy(n => DistanceFt(n.Position, destination.Position))
            .Take(MaxCutOrigins)
            .ToList();

        foreach (GroundNode? origin in origins)
        {
            var reachable = targets
                .Select(t => (Node: t, Ft: DistanceFt(origin.Position, t.Position)))
                .Where(c =>
                    (c.Ft <= MaxCrossingFt)
                    && !layout.RunwayCenterlineBetween(origin.Position, c.Node.Position)
                    && !CrossesForeignPavement(layout, origin.Position, c.Node, family)
                )
                .OrderBy(c => c.Ft)
                .ToList();
            if (reachable.Count == 0)
            {
                continue;
            }

            TaxiRoute? head = ResolveHeadTo(layout, startNodeId, path, origin, options, category);
            if (head is null)
            {
                Log.LogDebug("[Reposition] clearance does not resolve to {Lane} node {Node}; trying the next origin", lane, origin.Id);
                continue;
            }

            foreach ((GroundNode? target, double crossingFt) in reachable)
            {
                TaxiRoute? tail = TaxiPathfinder.FindRoute(layout, target.Id, destination.Id, category);
                if ((tail is null) || !EntersSpotAlongItsLane(destination, destinationLane, tail, aircraftLengthFt))
                {
                    continue;
                }

                return BuildDestinationCutPlan(destination, lane, destinationLane, origin, target, crossingFt, head, tail);
            }
        }

        Log.LogDebug("[Reposition] no {DestLane} node within {Max:F0} ft of {Lane} across open apron", destinationLane, MaxCrossingFt, lane);
        return null;
    }

    /// <summary>
    /// Improve a parking route that already resolved but only reaches the stand the long way round: OAK
    /// <c>TAXI @22</c> out of a stand whose alley the graph joins to the rest of the ramp only at its far end.
    /// Every node the route passes that carries a lane of the stand's own family is tried as the point where
    /// the pilot leaves the painted line and drives straight across the apron to the stand; the shortest
    /// <em>crossing</em> wins — ties to the earlier point on the route — because the crossing is unmodelled pavement
    /// with no graph guidance, so the aircraft stays on the painted line as far as it goes and then steps the
    /// shortest distance across, the way a ramp is actually driven. The crossing keeps the existing bounds —
    /// <see cref="MaxCrossingFt"/>, no runway centerline, no pavement outside "family ∪ RAMP" (see
    /// <see cref="CrossesForeignPavement"/>) — so which cuts are drivable at all is decided exactly as it already
    /// was; only <em>when</em> one is worth making is new, and
    /// <see cref="MinDetourRatio"/> / <see cref="MinDetourSavingFt"/> are measured thresholds, not published values.
    /// Null when the destination is not a stand on a ramp taxilane, or no candidate clears both bars — the caller
    /// then keeps the route the graph gave it.
    ///
    /// <para>A spot is never re-cut to. The crossing here always lands on the destination node itself, and a spot
    /// is entered along the lane it sits on (see <see cref="EntersSpotAlongItsLane"/>), so there is no candidate
    /// landing point this shape could offer: SFO <c>TAXI $5A</c> from gate D2 takes the long way round the five
    /// alley and arrives up T5A, rather than stopping across the lane 71 ft after leaving spot 5.</para>
    /// </summary>
    public static RampLaneDestinationCutPlan? TryPlanResolvedRouteCut(AirportGroundLayout layout, TaxiRoute resolvedRoute, GroundNode destination)
    {
        if ((resolvedRoute.Segments.Count == 0) || (destination.Type is not (GroundNodeType.Parking or GroundNodeType.Helipad)))
        {
            if (destination.Type == GroundNodeType.Spot)
            {
                Log.LogDebug("[Reposition] {Dest} is a spot — entered along its own lane, never cut to from the side", destination.Name);
            }

            return null;
        }

        (string? destinationLane, List<GroundNode> _) = LeadOut(destination);
        if ((destinationLane is null) || !IsRampTaxilane(layout, destinationLane))
        {
            return null;
        }

        HashSet<string> family = LaneFamily(layout, destinationLane);
        double totalFt = resolvedRoute.TotalDistanceFt;
        (GroundNode Node, int HeadSegments, double CutFt, double ResultFt)? best = null;
        for (int i = 0; i <= resolvedRoute.Segments.Count; i++)
        {
            GroundNode node = i == 0 ? resolvedRoute.Segments[0].Edge.FromNode : resolvedRoute.Segments[i - 1].Edge.ToNode;
            double prefixFt = resolvedRoute.PrefixDistanceFt(i);
            double cutFt = DistanceFt(node.Position, destination.Position);
            if (!IsWorthCutting(totalFt - prefixFt, cutFt) || !IsCuttableFrom(layout, node, destination, family))
            {
                continue;
            }

            if ((best is null) || (cutFt < best.Value.CutFt))
            {
                best = (node, i, cutFt, prefixFt + cutFt);
            }
        }

        if (best is null)
        {
            Log.LogDebug("[Reposition] no node on the route to {Dest} is worth cutting to the stand from", destination.Name);
            return null;
        }

        return BuildResolvedRouteCutPlan(resolvedRoute, destination, destinationLane, family, best.Value);
    }

    /// <summary>
    /// The graph is enough longer than the drive to justify leaving the painted line: at least
    /// <see cref="MinDetourRatio"/> times as long and <see cref="MinDetourSavingFt"/> longer, with the drive itself
    /// inside <see cref="MaxCrossingFt"/>. A ratio alone would re-cut a route that is barely longer than the
    /// straight line; a saving alone would re-cut a long taxi to save a rounding error at the end.
    /// </summary>
    private static bool IsWorthCutting(double remainingGraphFt, double cutFt) =>
        (cutFt <= MaxCrossingFt) && (remainingGraphFt >= (cutFt * MinDetourRatio)) && ((remainingGraphFt - cutFt) >= MinDetourSavingFt);

    /// <summary>
    /// <paramref name="node"/> is a point the pilot can leave the route at: it carries a straight edge of the
    /// stand's lane family (so the aircraft is on that ramp's pavement, not passing it on a taxiway), and the
    /// straight line from it to the stand crosses only apron and family lanes.
    /// </summary>
    private static bool IsCuttableFrom(AirportGroundLayout layout, GroundNode node, GroundNode destination, HashSet<string> family) =>
        (node.Id != destination.Id)
        && family.Any(lane => HasStraightEdgeOf(node, lane))
        && !layout.RunwayCenterlineBetween(node.Position, destination.Position)
        && !CrossesForeignPavement(layout, node.Position, destination, family);

    private static RampLaneDestinationCutPlan BuildResolvedRouteCutPlan(
        TaxiRoute resolvedRoute,
        GroundNode destination,
        string destinationLane,
        HashSet<string> family,
        (GroundNode Node, int HeadSegments, double CutFt, double ResultFt) cut
    )
    {
        var head = resolvedRoute.Segments.Take(cut.HeadSegments).ToList();
        // The crossing is apron, not the lane: named RAMP so the broadcast taxiway sequence stays the pavement the
        // aircraft actually follows, and the client rebuilds the same cut from the destination.
        TaxiRouteSegment crossing = VirtualNode.CreateSegment(cut.Node, destination, "RAMP");
        var route = new TaxiRoute
        {
            Segments = [.. head, crossing],
            HoldShortPoints = [.. resolvedRoute.HoldShortPoints.Where(hs => head.Any(s => s.ToNodeId == hs.NodeId))],
            Warnings = resolvedRoute.Warnings,
            MandatoryConnectorCount = resolvedRoute.MandatoryConnectorCount,
            DestinationParking = destination.Name,
        };
        string lane = family.Order(StringComparer.Ordinal).FirstOrDefault(l => HasStraightEdgeOf(cut.Node, l)) ?? destinationLane;
        Log.LogInformation(
            "[Reposition] leaving {Lane} at node {Node} and driving {Ft:F0} ft across the ramp to {Dest} on {DestLane}: "
                + "{ResultFt:F0} ft instead of the {GraphFt:F0} ft the graph resolved",
            lane,
            cut.Node.Id,
            cut.CutFt,
            destination.Name,
            destinationLane,
            cut.ResultFt,
            resolvedRoute.TotalDistanceFt
        );
        return new RampLaneDestinationCutPlan(cut.Node, destination, lane, destinationLane, cut.CutFt, route);
    }

    /// <summary>The clearance resolved so that it ends exactly at <paramref name="origin"/>, or null.</summary>
    private static TaxiRoute? ResolveHeadTo(
        AirportGroundLayout layout,
        int startNodeId,
        IReadOnlyList<string> path,
        GroundNode origin,
        ExplicitPathOptions options,
        AircraftCategory category
    )
    {
        var headOptions = new ExplicitPathOptions
        {
            ExplicitHoldShorts = options.ExplicitHoldShorts,
            DestinationRunway = null,

            DestinationHintNode = origin,
            DiagnosticLog = options.DiagnosticLog,
            PathTurnHints = options.PathTurnHints,
            StartHeadingTrue = options.StartHeadingTrue,
        };
        TaxiRoute? head = TaxiPathfinder.ResolveExplicitPathDetailed(layout, startNodeId, [.. path], out _, headOptions, category);
        if (head is null)
        {
            return null;
        }

        if (head.Segments.Count == 0)
        {
            return startNodeId == origin.Id ? head : null;
        }

        if (head.Segments[^1].ToNodeId != origin.Id)
        {
            head = head.TruncateAt(origin.Id);
        }

        return head.Segments[^1].ToNodeId == origin.Id ? head : null;
    }

    private static RampLaneDestinationCutPlan BuildDestinationCutPlan(
        GroundNode destination,
        string lane,
        string destinationLane,
        GroundNode origin,
        GroundNode target,
        double crossingFt,
        TaxiRoute head,
        TaxiRoute tail
    )
    {
        // The crossing is apron, not the lane: named RAMP so the broadcast taxiway sequence and the readback stay
        // the clearance as issued ("V T TE"), and the client reconstructs the cut from the destination instead.
        TaxiRouteSegment crossing = VirtualNode.CreateSegment(origin, target, "RAMP");
        var route = new TaxiRoute
        {
            Segments = [.. head.Segments, crossing, .. tail.Segments],
            HoldShortPoints = [.. head.HoldShortPoints, .. tail.HoldShortPoints],
            Warnings = [.. head.Warnings, .. tail.Warnings],
            MandatoryConnectorCount = head.MandatoryConnectorCount + tail.MandatoryConnectorCount,
            DestinationParking = destination.Type == GroundNodeType.Spot ? null : destination.Name,
            DestinationSpot = destination.Type == GroundNodeType.Spot ? destination.Name : null,
        };
        Log.LogInformation(
            "[Reposition] taxiing {Lane} to node {Origin}, cutting across the ramp onto {DestLane} at node {Target} ({Ft:F0} ft), then {Summary}",
            lane,
            origin.Id,
            destinationLane,
            target.Id,
            crossingFt,
            tail.ToSummary()
        );
        return new RampLaneDestinationCutPlan(origin, target, lane, destinationLane, crossingFt, route);
    }

    /// <summary>
    /// The graph tail brings the aircraft into a spot along the spot's own lane, with at least
    /// <see cref="SpotAlignmentRunFt"/> of run to straighten out on: the last edge is an edge of that lane
    /// ending at the spot node. A crossing that lands on the spot itself, or one node short of it, leaves the
    /// aircraft stopped on the marking at whatever heading the free-space leg happened to arrive on — which is
    /// across its own lane, not along it. Only spots are constrained; a gate is entered on its stand heading.
    /// </summary>
    /// <param name="destination">The stand or spot the clearance ends at.</param>
    /// <param name="destinationLane">The lane that destination sits on.</param>
    /// <param name="tail">The graph route from the crossing's landing point to the destination.</param>
    /// <param name="aircraftLengthFt">Fuselage length of the arriving aircraft, in feet.</param>
    /// <returns>True when the tail enters the spot along its lane with enough run.</returns>
    internal static bool EntersSpotAlongItsLane(GroundNode destination, string destinationLane, TaxiRoute tail, double aircraftLengthFt)
    {
        if (destination.Type != GroundNodeType.Spot)
        {
            return true;
        }

        if (tail.Segments.Count == 0)
        {
            return false;
        }

        TaxiRouteSegment last = tail.Segments[^1];
        return (last.ToNodeId == destination.Id)
            && last.Edge.Edge.MatchesTaxiway(destinationLane)
            && (tail.TotalDistanceFt >= SpotAlignmentRunFt(aircraftLengthFt));
    }

    private static bool HasStraightEdgeOf(GroundNode node, string lane) => node.Edges.Any(e => (e is GroundEdge) && e.MatchesTaxiway(lane));

    /// <summary>
    /// The resolver could not get onto <paramref name="lane"/>: either it said so outright (no bridge onto the
    /// first taxiway), or — from mid-lane, where it first tries a connector detour around the missing leg — it
    /// ran out of route before the destination without ever naming another taxiway as the culprit. A failure
    /// that blames a later taxiway ("A does not reach 28L") is never a lane problem. Other unnamed dead ends
    /// (a node reference that does not exist, no hold-short for the runway) reach the geometry gates and then
    /// fail again when the tail is re-resolved from the lane, so they cannot produce a cut.
    /// </summary>
    private static bool IsLaneUnreachableFailure(PathfindingFailure failure, string lane)
    {
        bool blamesLane = string.Equals(failure.InfeasibleTaxiway, lane, StringComparison.OrdinalIgnoreCase);
        return failure.Kind switch
        {
            FailureKind.TaxiwayNotConnected => blamesLane,
            FailureKind.DestinationUnreachable => blamesLane || (failure.InfeasibleTaxiway is null),
            _ => false,
        };
    }

    /// <summary>
    /// A ramp taxilane: its name is either several letters (<c>TE</c>, <c>TC</c>) or a letter followed by digits and
    /// an optional trailing letter group (<c>M3</c>, <c>M4</c>, and SFO's alley sub-lanes <c>T5A</c>, <c>T6B</c>), it
    /// is not a runway, it carries no runway holding position (a lane with a hold-short
    /// bar is a movement-area runway connector — OAK <c>W3</c>, SFO <c>A1</c>, <c>GL</c> — whatever its name), and it
    /// or a sibling lane touches RAMP pavement. The family test matters: SFO's M4 has no gate drawn on it, so it
    /// touches RAMP only through M3 / M5. Never a bare-letter taxiway or a node reference.
    /// </summary>
    public static bool IsRampTaxilane(AirportGroundLayout layout, string name) =>
        HasTaxilaneNameForm(name)
        && !layout.TryGetRunwayCenterlineName(name, out _)
        && !HasRunwayHoldShort(layout, name)
        && (TouchesRamp(layout, name) || layout.AllTaxiwayNames.Any(other => AreSiblingLanes(name, other) && TouchesRamp(layout, other)));

    /// <summary>Two distinct ramp-taxilane names on one ramp: same leading letter (<c>TE</c> / <c>TC</c>, <c>M3</c> / <c>M5</c>).</summary>
    public static bool AreSiblingLanes(string a, string b) => SameLaneFamily(a, b) && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Both names have the taxilane form and share their leading letter — the same lane counts, since an aircraft
    /// already reported on the cleared lane (or on its stub) may still need the cut to pick up its painted line.
    /// </summary>
    private static bool SameLaneFamily(string a, string b) =>
        HasTaxilaneNameForm(a) && HasTaxilaneNameForm(b) && (char.ToUpperInvariant(a[0]) == char.ToUpperInvariant(b[0]));

    /// <summary>
    /// Leading letters, then at most one digit run, then an optional trailing letter group, and nothing else:
    /// <c>TE</c>, <c>M3</c>, <c>T5A</c>, <c>T41E</c>. A bare single letter (<c>A</c>) is a taxiway, not a lane;
    /// a second digit run (<c>T5A6</c>) or any non-alphanumeric character disqualifies the name.
    /// </summary>
    private static bool HasTaxilaneNameForm(string name)
    {
        int i = 0;
        while ((i < name.Length) && char.IsAsciiLetter(name[i]))
        {
            i++;
        }

        int letters = i;
        if (letters == 0)
        {
            return false;
        }

        while ((i < name.Length) && char.IsAsciiDigit(name[i]))
        {
            i++;
        }

        int digits = i - letters;

        while ((i < name.Length) && char.IsAsciiLetter(name[i]))
        {
            i++;
        }

        if (i != name.Length)
        {
            return false;
        }

        return (letters >= 2) || (digits > 0);
    }

    private static bool TouchesRamp(AirportGroundLayout layout, string name) =>
        layout.GetNodesOnTaxiway(name).Any(node => node.Edges.Any(e => EdgeNames(e).Any(n => n.Equals("RAMP", StringComparison.OrdinalIgnoreCase))));

    private static bool HasRunwayHoldShort(AirportGroundLayout layout, string name) =>
        layout.GetNodesOnTaxiway(name).Any(node => node.Type == GroundNodeType.RunwayHoldShort);

    /// <summary>The lane itself plus every ramp taxilane sharing its leading letter — the pavement one ramp is made of.</summary>
    private static HashSet<string> LaneFamily(AirportGroundLayout layout, string lane)
    {
        var family = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lane };
        foreach (string name in layout.AllTaxiwayNames)
        {
            if (AreSiblingLanes(lane, name) && IsRampTaxilane(layout, name))
            {
                family.Add(name);
            }
        }

        return family;
    }

    /// <summary>
    /// The lane the aircraft is on now and whether it is rolling along it. A reported current taxiway wins;
    /// a parked aircraft's lane is the one its ramp lead-out opens onto; otherwise it is the nearest straight
    /// taxi edge within <see cref="CurrentLaneMaxFt"/>.
    /// </summary>
    private static (string? Lane, bool Rolling) ResolveCurrentLane(AirportGroundLayout layout, LatLon position, string? currentTaxiway)
    {
        if (!string.IsNullOrEmpty(currentTaxiway) && !currentTaxiway.Equals("RAMP", StringComparison.OrdinalIgnoreCase))
        {
            return (currentTaxiway, true);
        }

        GroundNode? nearest = layout.FindNearestNode(position);
        if ((nearest is { Type: GroundNodeType.Parking }) && (DistanceFt(position, nearest.Position) <= ParkedToleranceFt))
        {
            return (LeadOutLane(nearest), false);
        }

        AirportGroundLayout.NearestTaxiEdge? edge = layout.FindNearestTaxiEdge(position);
        if ((edge is { } e) && ((e.DistNm * GeoMath.FeetPerNm) <= CurrentLaneMaxFt))
        {
            return (e.Edge.TaxiwayName, true);
        }

        return (null, false);
    }

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>Walk RAMP pavement out of a parking node to the first named taxiway it reaches.</summary>
    private static string? LeadOutLane(GroundNode parking) => LeadOut(parking).Lane;

    /// <summary>
    /// The first named taxiway a parking node's RAMP lead-out reaches, plus every RAMP node walked on the way
    /// (the stand's lead-in, excluding the stand itself) — the pavement a crossing may aim for.
    /// </summary>
    private static (string? Lane, List<GroundNode> RampNodes) LeadOut(GroundNode parking)
    {
        var rampNodes = new List<GroundNode>();
        var visited = new HashSet<int> { parking.Id };
        var queue = new Queue<(GroundNode Node, int Depth)>();
        queue.Enqueue((parking, 0));
        while (queue.Count > 0)
        {
            (GroundNode? node, int depth) = queue.Dequeue();
            foreach (IGroundEdge edge in node.Edges)
            {
                string? lane = FirstNamedTaxiway(edge);
                if (lane is not null)
                {
                    return (lane, rampNodes);
                }

                GroundNode next = edge.OtherNode(node);
                if (((depth + 1) <= MaxLeadOutHops) && visited.Add(next.Id))
                {
                    rampNodes.Add(next);
                    queue.Enqueue((next, depth + 1));
                }
            }
        }

        return (null, rampNodes);
    }

    /// <summary>
    /// The first non-RAMP, non-runway name an edge carries (a membership arc <c>M3 - RAMP</c> yields <c>M3</c>).
    /// Only the first: a caller that has to see every name an edge carries — SFO's <c>M1</c> arc is also
    /// taxiway <c>Y</c> — reads <see cref="EdgeNames"/> instead.
    /// </summary>
    internal static string? FirstNamedTaxiway(IGroundEdge edge)
    {
        if (edge.IsRunwayCenterline)
        {
            return null;
        }

        foreach (string name in EdgeNames(edge))
        {
            if (!name.Equals("RAMP", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Every taxiway name an edge carries — an arc may be shared pavement and carry several.</summary>
    internal static string[] EdgeNames(IGroundEdge edge) => edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];

    /// <summary>
    /// Lane nodes to try re-acquiring, best first: every node carrying a straight edge of <paramref name="lane"/>
    /// within <see cref="MaxCrossingFt"/> whose straight-line approach crosses only apron and lanes of the same
    /// family, nearest first — except that a rolling aircraft (<paramref name="noseBearing"/> set) moves any
    /// candidate within <see cref="CandidateMarginFt"/> of the nearest reachable one that lies ahead of its
    /// nose to the front, so the cut is a lane change rather than a reversal. A parked aircraft has no
    /// meaningful nose and simply takes them nearest first.
    /// </summary>
    private static List<GroundNode> RankTargets(AirportGroundLayout layout, LatLon position, double? noseBearing, string lane, HashSet<string> family)
    {
        var reachable = new List<(GroundNode Node, double Ft)>();
        foreach (GroundNode node in layout.GetNodesOnTaxiway(lane))
        {
            if (AirportGroundLayout.HasRunwayCenterlineEdge(node) || !node.Edges.Any(e => (e is GroundEdge) && e.MatchesTaxiway(lane)))
            {
                continue;
            }

            double ft = DistanceFt(position, node.Position);
            bool clear = !layout.RunwayCenterlineBetween(position, node.Position) && !CrossesForeignPavement(layout, position, node, family);
            if ((ft <= MaxCrossingFt) && clear)
            {
                reachable.Add((node, ft));
            }
        }

        reachable.Sort((a, b) => a.Ft.CompareTo(b.Ft));
        if ((noseBearing is null) || (reachable.Count == 0))
        {
            return [.. reachable.Select(c => c.Node)];
        }

        double marginFt = reachable[0].Ft + CandidateMarginFt;
        bool Ahead((GroundNode Node, double Ft) c) =>
            (c.Ft <= marginFt) && (GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, c.Node.Position), noseBearing.Value) <= MaxReversalDeg);
        return [.. reachable.Where(Ahead).Concat(reachable.Where(c => !Ahead(c))).Select(c => c.Node)];
    }

    /// <summary>
    /// True when the straight cut from <paramref name="from"/> to <paramref name="target"/> crosses pavement it
    /// may not: any edge that is neither apron (RAMP) nor a lane of the same family — a lettered taxiway or a
    /// runway edge lying between the lanes means this is not one ramp — or any edge touching a runway
    /// holding-position node, family or not: no part of the aircraft may pass the holding-position marking
    /// without a crossing clearance (AIM 2-3-5.a.1, 4-3-18.a.5). The GeoJSON does not carry continuous
    /// (aircraft-prohibited) versus dashed (apron) edge markings, so "family ∪ RAMP" is the usable-pavement proxy.
    /// </summary>
    private static bool CrossesForeignPavement(AirportGroundLayout layout, LatLon from, GroundNode target, HashSet<string> family)
    {
        foreach (IGroundEdge edge in layout.AllEdges)
        {
            if (edge.HasNode(target.Id))
            {
                continue;
            }

            if (!TouchesHoldShort(edge) && (edge.IsRamp || IsFamilyEdge(edge, family)))
            {
                continue;
            }

            if (GeoMath.SegmentsIntersect(from, target.Position, edge.Nodes[0].Position, edge.Nodes[1].Position) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TouchesHoldShort(IGroundEdge edge) =>
        (edge.Nodes[0].Type == GroundNodeType.RunwayHoldShort) || (edge.Nodes[1].Type == GroundNodeType.RunwayHoldShort);

    private static bool IsFamilyEdge(IGroundEdge edge, HashSet<string> family)
    {
        if (edge.IsRunwayCenterline)
        {
            return false;
        }

        foreach (string name in EdgeNames(edge))
        {
            if (!family.Contains(name) && !name.Equals("RAMP", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
