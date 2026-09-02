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

        var (currentLane, rolling) = ResolveCurrentLane(layout, position, currentTaxiway);
        if ((currentLane is null) || !SameLaneFamily(currentLane, lane))
        {
            Log.LogDebug("[Reposition] {Lane} is not a sibling of current lane {Current}; no ramp cut", lane, currentLane ?? "(none)");
            return null;
        }

        var family = LaneFamily(layout, lane);
        var candidates = RankTargets(layout, position, rolling ? heading.Degrees : null, lane, family);
        if (candidates.Count == 0)
        {
            Log.LogDebug("[Reposition] no {Lane} node within {Max:F0} ft of the aircraft across open apron", lane, MaxCrossingFt);
            return null;
        }

        foreach (var target in candidates)
        {
            var tail = TaxiPathfinder.ResolveExplicitPathDetailed(layout, target.Id, path.ToList(), out var tailFailure, options, category);
            if (tail is null)
            {
                Log.LogDebug("[Reposition] route from {Lane} node {Node} does not resolve: {Reason}", lane, target.Id, tailFailure?.HumanMessage);
                continue;
            }

            return BuildPlan(position, currentLane, lane, target, tail);
        }

        return null;
    }

    private static RampLaneRepositionPlan BuildPlan(LatLon position, string currentLane, string lane, GroundNode target, TaxiRoute tail)
    {
        double crossingFt = DistanceFt(position, target.Position);
        var crossing = VirtualNode.CreateSegment(VirtualNode.Create(position.Lat, position.Lon), target, tail.Segments[0].TaxiwayName);
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
        AircraftCategory category
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

        var (destinationLane, leadIn) = LeadOut(destination);
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

        var family = LaneFamily(layout, lane);
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

        foreach (var origin in origins)
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

            var head = ResolveHeadTo(layout, startNodeId, path, origin, options, category);
            if (head is null)
            {
                Log.LogDebug("[Reposition] clearance does not resolve to {Lane} node {Node}; trying the next origin", lane, origin.Id);
                continue;
            }

            foreach (var (target, crossingFt) in reachable)
            {
                var tail = TaxiPathfinder.FindRoute(layout, target.Id, destination.Id, category);
                if (tail is null)
                {
                    continue;
                }

                return BuildDestinationCutPlan(destination, lane, destinationLane, origin, target, crossingFt, head, tail);
            }
        }

        Log.LogDebug("[Reposition] no {DestLane} node within {Max:F0} ft of {Lane} across open apron", destinationLane, MaxCrossingFt, lane);
        return null;
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
        var head = TaxiPathfinder.ResolveExplicitPathDetailed(layout, startNodeId, path.ToList(), out _, headOptions, category);
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
        var crossing = VirtualNode.CreateSegment(origin, target, "RAMP");
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
    /// A ramp taxilane: its name is either several letters (<c>TE</c>, <c>TC</c>) or one letter followed by digits
    /// (<c>M3</c>, <c>M4</c>), it is not a runway, it carries no runway holding position (a lane with a hold-short
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

    private static bool HasTaxilaneNameForm(string name)
    {
        int letters = 0;
        while ((letters < name.Length) && char.IsAsciiLetter(name[letters]))
        {
            letters++;
        }

        if (letters == 0)
        {
            return false;
        }

        for (int j = letters; j < name.Length; j++)
        {
            if (!char.IsAsciiDigit(name[j]))
            {
                return false;
            }
        }

        return (letters >= 2) || (letters < name.Length);
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

        var nearest = layout.FindNearestNode(position);
        if ((nearest is { Type: GroundNodeType.Parking }) && (DistanceFt(position, nearest.Position) <= ParkedToleranceFt))
        {
            return (LeadOutLane(nearest), false);
        }

        var edge = layout.FindNearestTaxiEdge(position);
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
            var (node, depth) = queue.Dequeue();
            foreach (var edge in node.Edges)
            {
                string? lane = FirstNamedTaxiway(edge);
                if (lane is not null)
                {
                    return (lane, rampNodes);
                }

                var next = edge.OtherNode(node);
                if (((depth + 1) <= MaxLeadOutHops) && visited.Add(next.Id))
                {
                    rampNodes.Add(next);
                    queue.Enqueue((next, depth + 1));
                }
            }
        }

        return (null, rampNodes);
    }

    /// <summary>The first non-RAMP, non-runway name an edge carries (a membership arc <c>M3 - RAMP</c> yields <c>M3</c>).</summary>
    private static string? FirstNamedTaxiway(IGroundEdge edge)
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

    private static string[] EdgeNames(IGroundEdge edge) => edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];

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
        foreach (var node in layout.GetNodesOnTaxiway(lane))
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
            return reachable.Select(c => c.Node).ToList();
        }

        double marginFt = reachable[0].Ft + CandidateMarginFt;
        bool Ahead((GroundNode Node, double Ft) c) =>
            (c.Ft <= marginFt) && (GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, c.Node.Position), noseBearing.Value) <= MaxReversalDeg);
        return reachable.Where(Ahead).Concat(reachable.Where(c => !Ahead(c))).Select(c => c.Node).ToList();
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
        foreach (var edge in layout.AllEdges)
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
