using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// Unified cost function used at every decision point in the pathfinder. The scalar is a
/// generic route cost, not strictly nautical miles. For the distance-based preferences
/// (<see cref="RoutePreference.Shortest"/> / <see cref="RoutePreference.FewestTurns"/>) every term is
/// nm-equivalent, so the straight-line <see cref="Heuristic"/> is both admissible (never overestimates)
/// and informative. The <see cref="RoutePreference.Fastest"/> branch additionally adds a time-equivalent
/// term onto the same scalar — traversal at the edge's own speed (a straight at taxi speed, a fillet along
/// its local cornering profile) plus the corner's speed dip and pivot sweep, in seconds — which dominates
/// the nm terms by ~2 orders of magnitude: the nm heuristic still never overestimates that larger cost
/// (so A* stays optimal), but it provides little guidance, so Fastest searches degrade toward Dijkstra.
/// That is acceptable — Fastest is never the default preference (<see cref="TaxiPathfinder.FindRoute"/>
/// uses FewestTurns) and the routes returned are correct, just found with more expansions.
/// Constants are hardcoded; calibrate by running OAK + SFO grids and adjusting in code.
/// </summary>
public static class RouteCostFunction
{
    // --- Base weights ---

    /// <summary>Weight for raw segment distance (nm/nm = 1.0 — identity).</summary>
    public const double DistanceWeight = 1.0;

    /// <summary>Turn budget: 180° ≈ 0.09 nm, ~540 ft equivalent.</summary>
    public const double TurnBudgetWeightNmPerDeg = 0.0005;

    /// <summary>
    /// Soft first-hop heading bias (nm-equivalent per degree). The very first edge of a search has no
    /// prior edge, so the turn-budget term below is skipped and the edge is admitted in any direction —
    /// nothing otherwise stops a taxi from starting with an unmotivated turn (or U-turn) away from the
    /// direction the aircraft is physically facing. When the real heading is known
    /// (<see cref="SearchContext.StartHeadingTrue"/>) and no explicit <c>&gt;</c>/<c>&lt;</c> turn hint
    /// governs the first taxiway, this gently prefers the first edge that continues that heading.
    /// Turn-budget scale (180° ≈ 0.09 nm) — kept well below the avoided (5 nm) / hint (50 nm) /
    /// unresolvable (1000 nm) penalties so it only breaks near-ties and never overrides a genuinely
    /// cheaper route: a forced reversal, where every first-edge candidate pays it equally, still wins.
    /// Added to the g-score only (never the <see cref="Heuristic"/>), so A* stays admissible.
    /// </summary>
    public const double FirstHopHeadingBiasNmPerDeg = 0.0005;

    /// <summary>Each taxiway transition: ~300 ft equivalent.</summary>
    public const double TaxiwayTransitionCostNm = 0.05;

    /// <summary>Each runway crossing: ~1800 ft equivalent — strong enough to prefer a longer no-crossing route.</summary>
    public const double RunwayCrossingCostNm = 0.3;

    /// <summary>Each direction reversal: ~3000 ft — strong disincentive.</summary>
    public const double DirectionReversalCostNm = 0.5;

    /// <summary>First use of each unauthorized letter taxiway — encourages bridging through one rather than none.</summary>
    public const double UnauthorizedTaxiwayFirstUseCostNm = 0.2;

    /// <summary>
    /// First use of an ARTCC-avoided taxiway on the soft-penalty pass (the pass that runs only when
    /// hard exclusion found no avoiding route). Sized far above any plausible taxi-distance spread so
    /// avoided mileage is minimised, yet finite so a destination reachable only through the taxiway
    /// still resolves. Must never be infinite. First-use only (see <see cref="IsAvoidedTaxiwayAlreadyVisited"/>)
    /// so a long avoided stretch is not multiplied edge-by-edge.
    /// </summary>
    public const double AvoidedTaxiwayFirstUseCostNm = 5.0;

    /// <summary>
    /// Penalty (nm-equivalent) for traversing a membership taxiway-junction arc ("X - Y", both
    /// taxiways) as a CONTINUATION of the walked taxiway rather than the turn onto the next
    /// instructed one. req ①: a single-name continuation must win over such an arc (a turn OFF
    /// the taxiway onto a crossing one). Sized to dominate intra-segment distance/turn spread so
    /// single-name is preferred, yet finite so the arc stays usable as a last resort — a
    /// resolvable clearance never fails. Runway-crossing arcs (IsRunwayJunction) are continuations
    /// and are NOT penalised.
    /// </summary>
    public const double MembershipJunctionArcContinuationCostNm = 0.5;

    /// <summary>Runway centerline multiplier on top of base distance — makes on-runway transit ~10× worse.</summary>
    public const double RunwayCenterlineDistanceMultiplier = 10.0;

    // --- Preference multipliers ---

    /// <summary>FewestTurns: multiply turn and transition weights by this factor.</summary>
    public const double FewestTurnsWeightMultiplier = 5.0;

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

        // Heading change at the current head node: from the previous edge's arrival bearing onto this
        // edge's departure bearing (an arc's entry tangent). Null on the first edge, which has no prior.
        double? headTurnDeg = null;
        if (current.LastEdge is not null && FindPrevNode(current, candidate) is not null)
        {
            GroundNode headNode = candidate.Nodes[0].Id == current.HeadNodeId ? candidate.Nodes[0] : candidate.Nodes[1];
            double departureBearing = GeometricAdmissibility.GetDepartureBearing(candidate, headNode, nextNode);
            headTurnDeg = HeadingDelta(current.ArrivalBearing, departureBearing);
        }

        // Fastest time-cost (seconds): the edge at its own speed ceiling plus what the corner into it costs
        // — the speed dip down to the cornering speed and back, and the sweep of a nose-wheel-radius pivot.
        // Added on top of the nm distance component; this mixes units into the scalar by design — see the
        // class summary; the term dominates, so the nm heuristic stays admissible but weak for Fastest.
        if (ctx.Preference == RoutePreference.Fastest)
        {
            cost += TraversalTimeSeconds(candidate, ctx.Category) + CornerTimeSeconds(candidate, headTurnDeg, ctx.Category);
        }

        // Turn budget: the heading change at the head node plus, for a fillet arc, the arc's own sweep.
        // An arc's tangents match the adjoining straights, so its head-node delta is ~0 and the sweep is
        // the whole turn; charging it prices a turn the same whether the route takes the painted fillet or
        // pivots square at the junction centre, and keeps a sharp fillet (a U-turn-like arc at a ramp
        // interchange) from reading as a free turn.
        if (ctx.Preference != RoutePreference.Shortest)
        {
            double turnDeg = (headTurnDeg ?? 0.0) + (candidate is GroundArc sweptArc ? sweptArc.TurnAngleDeg : 0.0);
            cost += turnDeg * turnWeight;
        }

        // First-hop heading bias: the turn penalty above is skipped on the first edge (no prior edge),
        // so its direction is otherwise cost-only and can point away from where the aircraft is facing.
        // When the real heading is known and no explicit turn hint governs the first taxiway, softly
        // steer the first edge toward that heading. See FirstHopHeadingBiasNmPerDeg for why this is safe
        // (soft, finite, first-edge-only, g-score-only, suppressed under an explicit hint).
        if (
            ctx.Preference != RoutePreference.Shortest
            && current.LastEdge is null
            && ctx.StartHeadingTrue is { } firstHopHeading
            && !FirstWaypointHasTurnHint(ctx)
        )
        {
            GroundNode firstHeadNode = candidate.Nodes[0].Id == current.HeadNodeId ? candidate.Nodes[0] : candidate.Nodes[1];
            double firstDepartureBearing = GeometricAdmissibility.GetDepartureBearing(candidate, firstHeadNode, nextNode);
            double firstHopDelta = HeadingDelta(firstHopHeading, firstDepartureBearing);
            cost += firstHopDelta * FirstHopHeadingBiasNmPerDeg;
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

        // Avoided-taxiway soft penalty (SoftPenalty pass only): first use of an ARTCC-avoided taxiway
        // when the hard-exclude pass found no avoiding route. First-use-only and finite so the
        // destination stays reachable while avoided mileage is minimised.
        if (ctx.AvoidMode == AvoidTaxiwayMode.SoftPenalty && ctx.Preference != RoutePreference.Shortest)
        {
            string edgeTaxiway = ResolveTaxiwayName(candidate, current.HeadNodeId);
            if (ctx.AvoidedTaxiways.Contains(edgeTaxiway) && !IsAvoidedTaxiwayAlreadyVisited(current, edgeTaxiway))
            {
                cost += AvoidedTaxiwayFirstUseCostNm;
            }
        }

        return cost;
    }

    /// <summary>
    /// Seconds to cover <paramref name="edge"/> as the navigator flies it: a fillet along its local
    /// cornering-speed profile (<see cref="GroundArc.TraversalSeconds"/>), a straight at the category's taxi speed.
    /// </summary>
    private static double TraversalTimeSeconds(IGroundEdge edge, AircraftCategory category)
    {
        if (edge is GroundArc arc)
        {
            return arc.TraversalSeconds(category);
        }

        return edge.DistanceNm / (CategoryPerformance.TaxiSpeed(category) / 3600.0);
    }

    /// <summary>
    /// Seconds a corner costs beyond covering the ground, priced the way the navigator flies it. A fillet
    /// arc: the dip from taxi speed down to the arc's <em>tightest</em> cornering speed
    /// (<see cref="GroundArc.MaxSafeSpeedKts"/>) and back — the aircraft must reach that speed somewhere on
    /// the curve, so the dip is real and only its location is approximated; the arc's sweep is already in
    /// <see cref="TraversalTimeSeconds"/>. A bend between two straights: the dip to the corner speed,
    /// and — when the bend is sharper than <see cref="GroundNavigator.EntryAlignmentThresholdDeg"/>, so the
    /// navigator rounds it with a nose-wheel-radius slow-turn — that pivot's v/R-coupled sweep, which takes
    /// turn/ω regardless of geometry. Without this term a square pivot through a junction centre priced
    /// as two free straights beat the painted fillet under Fastest.
    /// </summary>
    private static double CornerTimeSeconds(IGroundEdge candidate, double? headTurnDeg, AircraftCategory category)
    {
        double taxiKts = CategoryPerformance.TaxiSpeed(category);
        if (candidate is GroundArc arc)
        {
            return SpeedDipSeconds(category, taxiKts, arc.MaxSafeSpeedKts(category));
        }

        if (headTurnDeg is not { } turnDeg)
        {
            return 0.0;
        }

        bool slowTurn = turnDeg > GroundNavigator.EntryAlignmentThresholdDeg;
        double cornerKts = slowTurn
            ? CategoryPerformance.TurnRateLimitedSpeedKts(category, CategoryPerformance.NoseWheelTurnRadiusFt(category))
            : CategoryPerformance.CornerSpeedForAngle(category, turnDeg);
        double sweepSeconds = slowTurn ? turnDeg / CategoryPerformance.GroundTurnRate(category) : 0.0;
        return sweepSeconds + SpeedDipSeconds(category, taxiKts, cornerKts);
    }

    /// <summary>
    /// Seconds lost slowing from <paramref name="fromKts"/> to <paramref name="toKts"/> and accelerating
    /// back, relative to holding <paramref name="fromKts"/> over the same ground: Δv² / (2·v) · (1/a_decel + 1/a_accel).
    /// Treats the corner as a point (any dwell at the low speed is <see cref="TraversalTimeSeconds"/>' job)
    /// and assumes the adjoining straight is long enough to regain <paramref name="fromKts"/>; when it is
    /// not, the real loss is smaller, so the term only ever over-prices a corner.
    /// </summary>
    private static double SpeedDipSeconds(AircraftCategory category, double fromKts, double toKts)
    {
        double dvKts = fromKts - toKts;
        if ((dvKts <= 0.0) || (fromKts <= 0.0))
        {
            return 0.0;
        }

        double accelSecondsPerKt = (1.0 / CategoryPerformance.TaxiDecelRate(category)) + (1.0 / CategoryPerformance.TaxiAccelRate(category));
        return dvKts * dvKts / (2.0 * fromKts) * accelSecondsPerKt;
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

    private static bool IsAvoidedTaxiwayAlreadyVisited(PartialRoute route, string taxiwayName)
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

    /// <summary>
    /// True when the controller supplied an explicit <c>&gt;</c>/<c>&lt;</c> turn hint on the first
    /// taxiway. In that case the existing turn-hint machinery directs the first move, so the default
    /// <see cref="FirstHopHeadingBiasNmPerDeg"/> is suppressed to leave hinted routes unchanged.
    /// </summary>
    private static bool FirstWaypointHasTurnHint(SearchContext ctx) => ctx.WaypointTurnHints is { Count: > 0 } hints && hints[0] is not null;

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
