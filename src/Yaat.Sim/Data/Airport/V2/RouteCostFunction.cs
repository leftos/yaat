namespace Yaat.Sim.Data.Airport.V2;

/// <summary>
/// Unified cost function used at every decision point in the v2 pathfinder.
/// All costs are in nm-equivalent units so the A* heuristic remains admissible.
/// Constants are hardcoded; calibrate by running OAK + SFO grids and adjusting in code.
/// </summary>
public static class RouteCostFunction
{
    // --- Base weights ---

    /// <summary>Weight for raw segment distance (nm/nm = 1.0 — identity).</summary>
    public const double DistanceWeight = 1.0;

    /// <summary>Turn budget: 180° ≈ 0.09 nm, ~540 ft equivalent.</summary>
    public const double TurnBudgetWeightNmPerDeg = 0.0005;

    /// <summary>Each taxiway transition: ~300 ft equivalent.</summary>
    public const double TaxiwayTransitionCostNm = 0.05;

    /// <summary>Each runway crossing: ~1800 ft equivalent — strong enough to prefer a longer no-crossing route.</summary>
    public const double RunwayCrossingCostNm = 0.3;

    /// <summary>Each direction reversal: ~3000 ft — strong disincentive.</summary>
    public const double DirectionReversalCostNm = 0.5;

    /// <summary>Each reverse-arc traversal: stronger than reversal, arcs produce the hardest spins.</summary>
    public const double ReverseArcCostNm = 0.8;

    /// <summary>First use of each unauthorized letter taxiway — encourages bridging through one rather than none.</summary>
    public const double UnauthorizedTaxiwayFirstUseCostNm = 0.2;

    /// <summary>Runway centerline multiplier on top of base distance — makes on-runway transit ~10× worse.</summary>
    public const double RunwayCenterlineDistanceMultiplier = 10.0;

    // --- Preference multipliers ---

    /// <summary>FewestTurns: multiply turn and transition weights by this factor.</summary>
    public const double FewestTurnsWeightMultiplier = 5.0;

    /// <summary>
    /// Standard taxi turn rate used to convert <see cref="IGroundEdge.MaxSafeSpeedKts"/> on arcs
    /// to a scalar for the Fastest time-cost term. ~3 deg/sec is typical ground taxi.
    /// </summary>
    public const double TaxiTurnRateDegPerSec = 3.0;

    /// <summary>
    /// Compute the incremental cost of extending <paramref name="current"/> by one edge to <paramref name="nextNode"/>.
    /// This is the single cost function called by all search decision points.
    /// </summary>
    public static double IncrementalCost(PartialRoute current, IGroundEdge candidate, GroundNode nextNode, SearchContext ctx)
    {
        double turnWeight = TurnBudgetWeightNmPerDeg;
        double transitionWeight = TaxiwayTransitionCostNm;

        if (ctx.Preference == RoutePreference.FewestTurns)
        {
            turnWeight *= FewestTurnsWeightMultiplier;
            transitionWeight *= FewestTurnsWeightMultiplier;
        }
        else if (ctx.Preference == RoutePreference.Shortest)
        {
            turnWeight = 0.0;
            transitionWeight = 0.0;
        }

        double cost = 0.0;

        // Distance component.
        double distanceCost = candidate.DistanceNm * DistanceWeight;
        if (candidate.IsRunwayCenterline)
        {
            distanceCost += candidate.DistanceNm * (RunwayCenterlineDistanceMultiplier - 1.0);
        }

        cost += distanceCost;

        // Fix B — Fastest time-cost: distance / maxSafeSpeed gives a time-equivalent penalty.
        // Applied on top of the distance component so slower arcs cost proportionally more.
        if (ctx.Preference == RoutePreference.Fastest)
        {
            double maxSafeSpeedKts = candidate.MaxSafeSpeedKts(TaxiTurnRateDegPerSec);
            if (maxSafeSpeedKts > 0.0 && !double.IsPositiveInfinity(maxSafeSpeedKts))
            {
                double maxSafeSpeedNmPerSec = maxSafeSpeedKts / 3600.0;
                cost += candidate.DistanceNm / maxSafeSpeedNmPerSec;
            }
        }

        // Turn penalty: heading change at the current head node.
        if (ctx.Preference != RoutePreference.Shortest && current.LastEdge is not null)
        {
            double headNodeId = current.HeadNodeId;
            GroundNode headNode = candidate.Nodes[0].Id == (int)headNodeId ? candidate.Nodes[0] : candidate.Nodes[1];
            GroundNode? prevNode = FindPrevNode(current, candidate);

            if (prevNode is not null)
            {
                double arrivalBearing = current.ArrivalBearing;
                double departureBearing = GeometricAdmissibility.GetDepartureBearing(candidate, headNode, nextNode);
                double delta = HeadingDelta(arrivalBearing, departureBearing);
                cost += delta * turnWeight;
            }
        }

        // Taxiway transition penalty.
        // Fix D — skip when Depth == 0 (no previous edge): LastTaxiwayName is empty at start
        // and comparing empty string against the first edge's name would produce a phantom penalty.
        if (ctx.Preference != RoutePreference.Shortest && current.Depth > 0)
        {
            string prevTaxiway = current.LastTaxiwayName;
            string nextTaxiway = ResolveTaxiwayName(candidate, current.HeadNodeId);
            if (!string.Equals(prevTaxiway, nextTaxiway, StringComparison.OrdinalIgnoreCase))
            {
                cost += transitionWeight;
            }
        }

        // Runway crossing penalty: applies when crossing a hold-short node on an unrelated runway.
        // Fix A — skip the penalty when this hold-short IS the destination (not a crossing, just lineup).
        if (nextNode.Type == GroundNodeType.RunwayHoldShort && ctx.Preference != RoutePreference.Shortest)
        {
            bool isDestinationHoldShort =
                ctx.Destination.Kind == DestinationKind.Runway
                && ctx.Destination.RunwayId is { } destRunwayId
                && nextNode.RunwayId is { } nodeRwyId
                && nodeRwyId.Contains(destRunwayId);

            if (!isDestinationHoldShort)
            {
                cost += RunwayCrossingCostNm;
            }
        }

        // Reverse arc penalty.
        if (candidate is GroundArc arc)
        {
            GroundNode headNode2 = candidate.Nodes[0].Id == current.HeadNodeId ? candidate.Nodes[0] : candidate.Nodes[1];
            bool isReverseArc = arc.Nodes[0].Id != headNode2.Id;
            if (isReverseArc && ctx.Preference != RoutePreference.Shortest)
            {
                cost += ReverseArcCostNm;
            }
        }

        // Fix C — Direction reversal penalty is NOT applied here. Applying a per-edge
        // penalty for edges pointing away from the start→destination bearing causes
        // A* to explore exponentially more nodes on cross-airport routes (which must
        // temporarily go "backward" to cross runways or navigate ramp topology). The
        // DirectionReversalCostNm constant is retained for use by SegmentExpander's
        // local searches where the bounded search space makes it safe.

        // Unauthorized taxiway penalty: first use only of a letter taxiway not in the authorized set.
        if (ctx.AuthorizedTaxiways is not null && ctx.Preference != RoutePreference.Shortest)
        {
            string edgeTaxiway = ResolveTaxiwayName(candidate, current.HeadNodeId);
            if (
                SearchContext.IsLetterOnlyTaxiway(edgeTaxiway)
                && !ctx.AuthorizedTaxiways.Contains(edgeTaxiway)
                && !IsUnauthorizedTaxiwayAlreadyVisited(current, edgeTaxiway)
            )
            {
                cost += UnauthorizedTaxiwayFirstUseCostNm;
            }
        }

        return cost;
    }

    /// <summary>
    /// Admissible heuristic for A*: straight-line distance in nm.
    /// Never overestimates because no path can be shorter than the straight-line distance,
    /// and <see cref="DistanceWeight"/> is 1.0.
    /// </summary>
    public static double Heuristic(GroundNode current, GroundNode destination) => GeoMath.DistanceNm(current.Position, destination.Position);

    /// <summary>
    /// Compute the absolute heading change between two bearings.
    /// Wraps correctly for 0°/360° boundary.
    /// </summary>
    public static double HeadingDelta(double fromBearing, double toBearing)
    {
        double delta = Math.Abs(toBearing - fromBearing) % 360.0;
        return delta > 180.0 ? 360.0 - delta : delta;
    }

    /// <summary>
    /// Resolve the taxiway name that applies when traversing <paramref name="edge"/>
    /// departing from <paramref name="fromNodeId"/>. For junction arcs the arriving side's
    /// taxiway is returned.
    /// </summary>
    public static string ResolveTaxiwayName(IGroundEdge edge, int fromNodeId)
    {
        if (edge is GroundArc arc && arc.TaxiwayNames.Length == 2)
        {
            GroundNode fromNode = arc.Nodes[0].Id == fromNodeId ? arc.Nodes[0] : arc.Nodes[1];
            GroundNode toNode = arc.Nodes[0].Id == fromNodeId ? arc.Nodes[1] : arc.Nodes[0];

            foreach (var adjacentEdge in fromNode.Edges)
            {
                if (adjacentEdge == edge)
                {
                    continue;
                }

                foreach (string name in arc.TaxiwayNames)
                {
                    if (adjacentEdge.MatchesTaxiway(name))
                    {
                        return name;
                    }
                }
            }

            foreach (string name in arc.TaxiwayNames)
            {
                if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return edge.TaxiwayName;
    }

    private static bool IsUnauthorizedTaxiwayAlreadyVisited(PartialRoute route, string taxiwayName)
    {
        var cursor = route;
        while (cursor.LastEdge is not null)
        {
            string name = ResolveTaxiwayName(cursor.LastEdge, cursor.Previous?.HeadNodeId ?? cursor.HeadNodeId);
            if (string.Equals(name, taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            cursor = cursor.Previous!;
        }

        return false;
    }

    private static GroundNode? FindPrevNode(PartialRoute current, IGroundEdge candidate)
    {
        if (current.LastEdge is null)
        {
            return null;
        }

        int headNodeId = current.HeadNodeId;
        foreach (var n in current.LastEdge.Nodes)
        {
            if (n.Id == headNodeId)
            {
                return n;
            }
        }

        foreach (var n in candidate.Nodes)
        {
            if (n.Id == headNodeId)
            {
                return n;
            }
        }

        return null;
    }
}
