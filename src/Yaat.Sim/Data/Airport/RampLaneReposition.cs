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
        if (!IsNumberedLane(lane) || layout.TryGetRunwayCenterlineName(lane, out _))
        {
            return null;
        }

        var (currentLane, rolling) = ResolveCurrentLane(layout, position, currentTaxiway);
        if ((currentLane is null) || !string.Equals(LanePrefix(currentLane), LanePrefix(lane), StringComparison.OrdinalIgnoreCase))
        {
            Log.LogDebug("[Reposition] {Lane} is not a sibling of current lane {Current}; no ramp cut", lane, currentLane ?? "(none)");
            return null;
        }

        var family = LaneFamily(layout, LanePrefix(lane));
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

    /// <summary>Letters followed by digits — a numbered lane (<c>M4</c>, <c>T5</c>); never a bare letter taxiway or a node reference.</summary>
    public static bool IsNumberedLane(string name)
    {
        int i = 0;
        while ((i < name.Length) && char.IsAsciiLetter(name[i]))
        {
            i++;
        }

        if ((i == 0) || (i == name.Length))
        {
            return false;
        }

        for (int j = i; j < name.Length; j++)
        {
            if (!char.IsAsciiDigit(name[j]))
            {
                return false;
            }
        }

        return true;
    }

    private static string LanePrefix(string name)
    {
        int i = 0;
        while ((i < name.Length) && char.IsAsciiLetter(name[i]))
        {
            i++;
        }

        return name[..i];
    }

    private static HashSet<string> LaneFamily(AirportGroundLayout layout, string prefix)
    {
        var family = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in layout.AllTaxiwayNames)
        {
            if (IsNumberedLane(name) && string.Equals(LanePrefix(name), prefix, StringComparison.OrdinalIgnoreCase))
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
    private static string? LeadOutLane(GroundNode parking)
    {
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
                    return lane;
                }

                var next = edge.OtherNode(node);
                if (((depth + 1) <= MaxLeadOutHops) && visited.Add(next.Id))
                {
                    queue.Enqueue((next, depth + 1));
                }
            }
        }

        return null;
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
