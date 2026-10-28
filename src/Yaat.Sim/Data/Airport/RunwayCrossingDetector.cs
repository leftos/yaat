using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Detects taxiway-runway crossings and inserts hold-short nodes at runway boundaries.
/// Based on AC 150/5300-13B Table 3-2 hold-short distance standards.
/// </summary>
internal static class RunwayCrossingDetector
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwayCrossingDetector");

    /// <summary>Tolerance (nm) for runway boundary classification (~6ft).</summary>
    private const double RunwayTolerance = 0.001;

    /// <summary>
    /// Tolerance (ft) for reusing/snapping an existing node as hold-short instead of placing a
    /// fresh node at the exact standoff. Tied to the fillet coincident-node threshold: reuse is
    /// equivalent to exact placement only within this distance, because a fresh node closer than
    /// this to a neighbor would be collapsed into it by the later fillet coincident-node merge
    /// anyway. A larger tolerance would let the node snap to whatever shape point happens to sit
    /// nearby, scattering the standoff by up to the tolerance and pulling acute-exit holds inside
    /// the runway safety area boundary.
    /// </summary>
    private const double HoldShortSnapFt = Fillet.FilletConstants.CoincidentNodeThresholdFt;

    /// <summary>
    /// Tolerance for a reused node on the runway side of the paint line; well inside the four-line runway holding position
    /// marking (lines 6 or 12 in apart, AIM 2-3-5.a.1), so the nose stays on the marking.
    /// </summary>
    private const double PaintRunwaySideToleranceFt = 1.0;

    /// <summary>Maximum hops the junction walker takes while searching for a segment that crosses the ideal hold-short distance.</summary>
    internal const int HoldShortWalkMaxHops = 8;

    /// <summary>A paint snap that moves a bar farther than this (ft) from where the rule would have seated it is logged.</summary>
    private const double LargeSnapFt = 40.0;

    /// <summary>
    /// Backoff distance (ft) from the farthest reachable node when the walker dead-ends before reaching the ideal hold-short
    /// distance.
    /// </summary>
    private const double HoldShortFallbackBufferFt = 25.0;

    /// <summary>
    /// How far (ft) beyond another runway's base separation S a point still touches that runway's holding area
    /// (<see cref="TouchesHoldingArea"/>), so a bar seated on that runway's hold line counts as touching it.
    /// </summary>
    private const double OtherRunwayAreaMarginFt = 5.0;

    /// <summary>The fraction range a minted node is clamped to along its edge, so it never lands on an endpoint.</summary>
    private const double MinEdgeFraction = 0.01;

    /// <summary>The upper bound of the fraction range a minted node is clamped to along its edge (<see cref="MinEdgeFraction"/>).</summary>
    private const double MaxEdgeFraction = 0.99;

    /// <summary>
    /// The runway's parse-time facts: its navdata width, its base separation S (<see cref="ResolveStandoff"/>, warning
    /// when the rule overrides the authored value) and its rectangle. Every runway is planned before any runway's bars are
    /// seated, so each crossing can see every other runway's holding area.
    /// </summary>
    internal static RunwayPlan PlanRunway(GeoJsonParser.RunwayFeature rwy, string airportId, string? runwayAirportCode)
    {
        var combinedId = RunwayIdentifier.Parse(rwy.Name);

        // The rule's runway inputs from navdata; without navdata the width is the default and the length long.
        var inputs = RunwayStandoffInputs.Lookup(NavigationDatabase.InstanceOrNull, runwayAirportCode, combinedId);
        HoldShortStandoffResult standoff = ResolveStandoff(rwy, inputs);
        HoldShortStandoff.WarnWhenRuleOverAuthored(airportId, rwy.Name, rwy.HoldShortDistanceFt, standoff);
        return new RunwayPlan(rwy, BuildRunwayRectangle(rwy, inputs.WidthFt, standoff.Ft, combinedId), inputs.WidthFt, standoff.Ft);
    }

    /// <summary>
    /// Seats the planned runway's hold-short bars on every taxiway that crosses its edge and links its on-runway nodes.
    /// <paramref name="runways"/> is every runway of the layout, planned (<see cref="PlanRunway"/>), whose holding areas
    /// an angled bar keeps out of. A crossing whose taxiway carries a painted bar of <paramref name="videoMap"/> that pairs
    /// with it (the first of <see cref="RunwayPaintPairing.Candidates"/> that <see cref="PairWithPaint"/> can seat) has its
    /// bar seated on the paint; with no map every bar is seated by the rule. Returns the runway width used, the bars the
    /// dead-end fallback placed short of the holding distance at a junction, for <see cref="DropRedundantFallbackBars"/> once
    /// every runway's bars are in, and the bars seated on paint.
    /// </summary>
    internal static RunwayCrossingResult DetectRunwayCrossings(
        RunwayPlan plan,
        IReadOnlyList<RunwayPlan> runways,
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        TowerCabVideoMap? videoMap,
        ref int nextNodeId
    )
    {
        RunwayRectangle rect = plan.Rect;
        PaintSource? paint =
            (videoMap is { } map)
                ? new PaintSource(
                    map.Segments,
                    [.. runways.Select(other => (IReadOnlyList<LatLon>)[.. other.Feature.Coords.Select(c => new LatLon(c.Lat, c.Lon))])]
                )
                : null;

        // Classify every node as on-runway or off-runway
        var onRunwayNodes = new HashSet<int>();
        foreach ((int nodeId, GroundNode? node) in layout.Nodes)
        {
            if (IsOnRunway(node.Position, rect))
            {
                onRunwayNodes.Add(nodeId);
            }
        }

        // Snapshot edges — we mutate during iteration
        var edgeSnapshot = new List<GroundEdge>(layout.Edges);
        var processed = new HashSet<(int, int)>();
        var seating = new RunwaySeating(new CrossingContext(layout, coordIndex), runways, paint, [], []);

        foreach (GroundEdge edge in edgeSnapshot)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            bool fromOn = onRunwayNodes.Contains(edge.Nodes[0].Id);
            bool toOn = onRunwayNodes.Contains(edge.Nodes[1].Id);

            // Only process boundary edges (one on, one off)
            if (fromOn == toOn)
            {
                continue;
            }

            int onId = fromOn ? edge.Nodes[0].Id : edge.Nodes[1].Id;
            int offId = fromOn ? edge.Nodes[1].Id : edge.Nodes[0].Id;

            // Avoid processing the same boundary pair twice
            (int, int) key = (Math.Min(onId, offId), Math.Max(onId, offId));
            if (!processed.Add(key))
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(onId, out GroundNode? onNode) || !layout.Nodes.TryGetValue(offId, out GroundNode? offNode))
            {
                continue;
            }

            SeatCrossing(seating, new Crossing(edge, onNode, offNode), rect, ref nextNodeId);
        }

        // Connect on-runway nodes with centerline edges so that taxiways
        // crossing the same runway are linked (e.g., D and F at OAK both cross
        // 15/33 but have no GeoJSON edges between them).
        ConnectOnRunwayNodes(layout, rect, coordIndex, ref nextNodeId);

        return new RunwayCrossingResult(plan.WidthFt, plan.StandoffFt, seating.FallbackBars, seating.SnappedBars);
    }

    /// <summary>
    /// Seats one crossing's bar: at D_paint when the crossing pairs with a painted bar (<see cref="PairWithPaint"/>), else at
    /// the rule's centerline standoff D_rule (<see cref="CrossingStandoffNm"/>). A bar seated on paint is recorded with its
    /// segment (<see cref="GroundNode.PaintSegmentIndex"/>).
    /// </summary>
    private static void SeatCrossing(RunwaySeating seating, Crossing crossing, in RunwayRectangle rect, ref int nextNodeId)
    {
        AirportGroundLayout layout = seating.Context.Layout;
        var ruleTarget = new BarTarget(rect, CrossingStandoffNm(layout, crossing, rect, seating.Runways), SnapPoint: null);
        PaintPair? pair = (seating.Paint is { } paint) ? PairWithPaint(layout, crossing, ruleTarget, paint) : null;
        BarTarget target = (pair is { } paired) ? new BarTarget(rect, paired.PaintFt / GeoMath.FeetPerNm, paired.SnapPoint) : ruleTarget;
        BarPlacement placement = ProcessBoundaryEdge(seating.Context, crossing, target, ref nextNodeId);
        if (placement.Fallback is { } fallbackBar)
        {
            seating.FallbackBars.Add(fallbackBar);
        }

        if ((pair is { } snapped) && (placement.Bar is { } bar) && (bar.RunwayId is { } barRunway) && barRunway.Equals(rect.CombinedId))
        {
            bar.PaintSegmentIndex = snapped.SegmentIndex;
            seating.SnappedBars.Add(new SnappedBar(bar, rect, snapped.PaintFt));
        }
    }

    /// <summary>
    /// The innermost painted bar the crossing's point can pair with (<see cref="RunwayPaintPairing.Candidates"/>) that the
    /// placement can seat the point on. A candidate is passed over when the walk toward its D_paint would dead-end or run
    /// out of hops, or would meet D_paint farther than <see cref="HoldShortSnapFt"/> from its snap point (on another segment
    /// of a chain that turns back toward the runway); null when every one is. Logs a snap that moves the point more than
    /// <see cref="LargeSnapFt"/> from where the rule seats it.
    /// </summary>
    private static PaintPair? PairWithPaint(AirportGroundLayout layout, Crossing crossing, BarTarget ruleTarget, PaintSource paint)
    {
        RunwayRectangle rect = ruleTarget.Rect;
        double ruleFt = ruleTarget.IdealNm * GeoMath.FeetPerNm;
        foreach (PaintPair candidate in RunwayPaintPairing.Candidates(layout, crossing, rect, ruleFt, paint))
        {
            if (CanSeatOn(layout, crossing, rect, candidate))
            {
                LogLargeSnap(layout, crossing, ruleTarget, candidate);
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Whether the placement walk toward the candidate's D_paint meets it within <see cref="HoldShortSnapFt"/> of its snap point.</summary>
    private static bool CanSeatOn(AirportGroundLayout layout, Crossing crossing, in RunwayRectangle rect, PaintPair candidate)
    {
        (_, LatLon? seat) = ProbeStandoffPoint(layout, crossing, rect, candidate.PaintFt / GeoMath.FeetPerNm);
        double seatToSnapFt = (seat is { } point) ? GeoMath.DistanceNm(point, candidate.SnapPoint) * GeoMath.FeetPerNm : double.PositiveInfinity;
        if (seatToSnapFt <= HoldShortSnapFt)
        {
            return true;
        }

        Log.LogDebug(
            "Paint segment {Segment} passed over for {Runway} on {Taxiway}: the placement cannot seat the bar on it "
                + "(D_paint={PaintFt:F1} ft, {Off:F1} ft off)",
            candidate.SegmentIndex,
            rect.CombinedId,
            crossing.Edge.TaxiwayName,
            candidate.PaintFt,
            seatToSnapFt
        );
        return false;
    }

    /// <summary>Logs a snap that moves the bar more than <see cref="LargeSnapFt"/> from where the rule seats it.</summary>
    private static void LogLargeSnap(AirportGroundLayout layout, Crossing crossing, BarTarget ruleTarget, PaintPair pair)
    {
        if (!Log.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        RunwayRectangle rect = ruleTarget.Rect;
        double ruleFt = ruleTarget.IdealNm * GeoMath.FeetPerNm;
        (_, LatLon? rulePoint) = ProbeStandoffPoint(layout, crossing, rect, ruleTarget.IdealNm);
        double moveFt =
            (rulePoint is { } rulePosition) ? GeoMath.DistanceNm(rulePosition, pair.SnapPoint) * GeoMath.FeetPerNm : Math.Abs(pair.PaintFt - ruleFt);
        if (moveFt > LargeSnapFt)
        {
            Log.LogDebug(
                "Paint snap at {Airport} moves the {Runway} bar on {Taxiway} {MoveFt:F0} ft: D_rule={RuleFt:F1} ft, D_paint={PaintFt:F1} ft",
                layout.AirportId,
                rect.CombinedId,
                crossing.Edge.TaxiwayName,
                moveFt,
                ruleFt,
                pair.PaintFt
            );
        }
    }

    /// <summary>
    /// The runway's base hold-short separation S from its authored <c>holdShortDistance</c> and its navdata
    /// <paramref name="inputs"/> (<see cref="HoldShortStandoff.BaseSeparationFt"/>), with no paint calibration.
    /// </summary>
    internal static HoldShortStandoffResult ResolveStandoff(GeoJsonParser.RunwayFeature rwy, RunwayStandoffInputs inputs) =>
        HoldShortStandoff.BaseSeparationFt(null, rwy.HoldShortDistanceFt, inputs);

    internal static RunwayRectangle BuildRunwayRectangle(
        GeoJsonParser.RunwayFeature rwy,
        double widthFt,
        double holdShortFt,
        RunwayIdentifier combinedId
    )
    {
        double bearing = GeoMath.BearingTo(rwy.Coords[0].Lat, rwy.Coords[0].Lon, rwy.Coords[^1].Lat, rwy.Coords[^1].Lon);
        double lengthNm = GeoMath.DistanceNm(rwy.Coords[0].Lat, rwy.Coords[0].Lon, rwy.Coords[^1].Lat, rwy.Coords[^1].Lon);
        double halfWidthNm = (widthFt / 2.0) / GeoMath.FeetPerNm;
        double holdShortNm = holdShortFt / GeoMath.FeetPerNm;

        return new RunwayRectangle
        {
            RefLat = rwy.Coords[0].Lat,
            RefLon = rwy.Coords[0].Lon,
            TrueHeading = new TrueHeading(bearing),
            LengthNm = lengthNm,
            HalfWidthNm = halfWidthNm,
            HoldShortNm = holdShortNm,
            CombinedId = combinedId,
        };
    }

    internal static RunwayRectangle BuildRunwayRectangle(GroundRunway rwy)
    {
        List<(double Lat, double Lon)> coords = rwy.Coordinates;
        double bearing = GeoMath.BearingTo(coords[0].Lat, coords[0].Lon, coords[^1].Lat, coords[^1].Lon);
        double lengthNm = GeoMath.DistanceNm(coords[0].Lat, coords[0].Lon, coords[^1].Lat, coords[^1].Lon);
        double halfWidthNm = (rwy.WidthFt / 2.0) / GeoMath.FeetPerNm;
        double holdShortNm = (rwy.HoldShortStandoffFt ?? rwy.HoldShortDistanceFt ?? HoldShortDistanceForWidth(rwy.WidthFt)) / GeoMath.FeetPerNm;

        return new RunwayRectangle
        {
            RefLat = coords[0].Lat,
            RefLon = coords[0].Lon,
            TrueHeading = new TrueHeading(bearing),
            LengthNm = lengthNm,
            HalfWidthNm = halfWidthNm,
            HoldShortNm = holdShortNm,
            CombinedId = RunwayIdentifier.Parse(rwy.Name),
        };
    }

    /// <summary>
    /// Marks every hold-short node of <paramref name="layout"/> whose runway is in the layout as a short bar or its runway's
    /// hold line (<see cref="GroundNode.IsShortBar"/>), by its cross-track from the runway centerline; a bar seated on paint
    /// (<paramref name="snappedIds"/>) is always its runway's hold line. Runs last in the build, once fillets have settled
    /// every node's position.
    /// </summary>
    internal static void MarkShortBars(AirportGroundLayout layout, IReadOnlySet<int> snappedIds)
    {
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if (
                (node.Type != GroundNodeType.RunwayHoldShort)
                || (node.RunwayId is not { } id)
                || (layout.Runways.FirstOrDefault(candidate => candidate.Id.Equals(id)) is not { } runway)
            )
            {
                continue;
            }

            node.IsShortBar =
                !snappedIds.Contains(node.Id) && !AirportGroundLayout.IsAtHoldingDistanceByCrossTrack(BuildRunwayRectangle(runway), node);
        }
    }

    internal static bool IsOnRunway(double lat, double lon, in RunwayRectangle rect)
    {
        double crossTrack = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(lat, lon, rect.RefLat, rect.RefLon, rect.TrueHeading));
        double alongTrack = GeoMath.AlongTrackDistanceNm(lat, lon, rect.RefLat, rect.RefLon, rect.TrueHeading);

        return crossTrack <= rect.HalfWidthNm + RunwayTolerance && alongTrack >= -RunwayTolerance && alongTrack <= rect.LengthNm + RunwayTolerance;
    }

    internal static bool IsOnRunway(LatLon position, in RunwayRectangle rect) => IsOnRunway(position.Lat, position.Lon, rect);

    /// <summary>
    /// Whether <paramref name="position"/> touches the runway's holding area: no farther from its centerline than its base
    /// separation S plus <see cref="OtherRunwayAreaMarginFt"/>, alongside its length (within <see cref="RunwayTolerance"/>
    /// of either end).
    /// </summary>
    internal static bool TouchesHoldingArea(LatLon position, in RunwayRectangle rect)
    {
        double crossNm = CrossTrackNm(rect, position);
        double alongNm = GeoMath.AlongTrackDistanceNm(position.Lat, position.Lon, rect.RefLat, rect.RefLon, rect.TrueHeading);
        bool alongside = (alongNm >= -RunwayTolerance) && (alongNm <= rect.LengthNm + RunwayTolerance);
        return alongside && (crossNm <= rect.HoldShortNm + (OtherRunwayAreaMarginFt / GeoMath.FeetPerNm));
    }

    /// <summary>
    /// Whether the straight segment <paramref name="from"/> → <paramref name="to"/> touches the runway's holding area
    /// (<see cref="TouchesHoldingArea"/>) at its point nearest the runway centerline: where it crosses the centerline,
    /// else its nearer end. Judging the segment rather than its nodes keeps the verdict the same whether or not another
    /// runway's bar has split it yet.
    /// </summary>
    internal static bool SegmentTouchesHoldingArea(LatLon from, LatLon to, in RunwayRectangle rect) =>
        TouchesHoldingArea(NearestToCenterline(from, to, rect), rect);

    /// <summary>The segment's point nearest the runway centerline: where it crosses the centerline, else its nearer end.</summary>
    private static LatLon NearestToCenterline(LatLon from, LatLon to, in RunwayRectangle rect)
    {
        var reference = new LatLon(rect.RefLat, rect.RefLon);
        double fromCross = GeoMath.SignedCrossTrackDistanceNm(from, reference, rect.TrueHeading);
        double toCross = GeoMath.SignedCrossTrackDistanceNm(to, reference, rect.TrueHeading);
        if ((fromCross * toCross < 0.0) && (Math.Abs(fromCross - toCross) > 1e-12))
        {
            double fraction = fromCross / (fromCross - toCross);
            return new LatLon(from.Lat + (fraction * (to.Lat - from.Lat)), from.Lon + (fraction * (to.Lon - from.Lon)));
        }

        return Math.Abs(fromCross) <= Math.Abs(toCross) ? from : to;
    }

    /// <summary>
    /// The runway width the rectangle carries, feet, rounded to a micro-foot so the nm round trip cannot drop a width
    /// on a bucket edge (150 ft) into the bucket below.
    /// </summary>
    internal static double RunwayWidthFt(in RunwayRectangle rect) => Math.Round(2.0 * rect.HalfWidthNm * GeoMath.FeetPerNm, 6);

    /// <summary>
    /// The angle θ (degrees, 0-180) between the runway centerline and the taxiway centerline at the hold line: the bearing
    /// of the segment where the centerline reaches the runway's base separation S, found by following the walker's chain
    /// outward (the boundary edge <paramref name="onNode"/> → <paramref name="offNode"/>, then
    /// <see cref="PickWalkContinuation"/> hops). The hold line is painted perpendicular to the taxiway centerline there, so
    /// that segment's direction is the one the term needs. When the chain ends short of S, the last segment it reached.
    /// Measured at parse time, before fillet arcs exist.
    /// </summary>
    internal static double MeasureCrossingAngleDeg(
        AirportGroundLayout layout,
        GroundEdge edge,
        GroundNode onNode,
        GroundNode offNode,
        in RunwayRectangle rect
    )
    {
        GroundNode prev = onNode;
        GroundNode current = offNode;
        var visited = new HashSet<int> { onNode.Id, offNode.Id };
        for (int hop = 0; hop < HoldShortWalkMaxHops; hop++)
        {
            if (CrossTrackNm(rect, current.Position) >= rect.HoldShortNm)
            {
                break;
            }

            if (PickWalkContinuation(layout, new WalkStep(edge.TaxiwayName, prev, current), visited, rect) is not { } next)
            {
                break;
            }

            prev = current;
            current = next.Far;
            visited.Add(current.Id);
        }

        double segmentBearing = GeoMath.BearingTo(prev.Position, current.Position);
        return Math.Abs(NormalizeBearingDelta(segmentBearing - rect.TrueHeading.Degrees));
    }

    /// <summary>
    /// The crossing's centerline standoff, nm: D, the runway's base separation S plus the angle term for the taxiway's
    /// angle to the runway (<see cref="HoldShortStandoff.CenterlineStandoffFt"/>), unless the taxiway path from the crossing
    /// to the D point touches another runway's holding area at a node or along a segment (<see cref="FirstTouch"/>); then S.
    /// </summary>
    private static double CrossingStandoffNm(
        AirportGroundLayout layout,
        Crossing crossing,
        in RunwayRectangle rect,
        IReadOnlyList<RunwayPlan> runways
    )
    {
        double angleDeg = MeasureCrossingAngleDeg(layout, crossing.Edge, crossing.On, crossing.Off, rect);
        double baseFt = rect.HoldShortNm * GeoMath.FeetPerNm;
        double idealFt = HoldShortStandoff.CenterlineStandoffFt(baseFt, RunwayWidthFt(rect), angleDeg);
        Log.LogDebug(
            "Hold-short standoff for {Runway} on {Taxiway}: θ={Angle:F1}°, S={BaseFt:F1} ft, D={IdealFt:F1} ft",
            rect.CombinedId,
            crossing.Edge.TaxiwayName,
            angleDeg,
            baseFt,
            idealFt
        );
        if (idealFt <= baseFt)
        {
            return rect.HoldShortNm;
        }

        (List<GroundNode> passed, LatLon? point) = ProbeStandoffPoint(layout, crossing, rect, idealFt / GeoMath.FeetPerNm);
        List<(string Label, LatLon Position)> path = [.. passed.Select(node => ($"node {node.Id}", node.Position))];
        if (point is { } dPoint)
        {
            path.Add(("the D point", dPoint));
        }

        foreach (RunwayPlan other in runways)
        {
            if (!other.Rect.CombinedId.Equals(rect.CombinedId) && (FirstTouch(path, other.Rect) is { } touch))
            {
                Log.LogDebug(
                    "Hold-short standoff for {Runway} on {Taxiway}: D={IdealFt:F1} ft dropped to S, {Touch} touches the {Other} holding area "
                        + "({TouchFt:F1} ft from its centerline, S={OtherFt:F1} ft)",
                    rect.CombinedId,
                    crossing.Edge.TaxiwayName,
                    idealFt,
                    touch.Label,
                    other.Rect.CombinedId,
                    touch.CrossFt,
                    other.StandoffFt
                );
                return rect.HoldShortNm;
            }
        }

        return idealFt / GeoMath.FeetPerNm;
    }

    /// <summary>
    /// The first part of <paramref name="path"/> that touches the runway's holding area, by the segment from a point to
    /// the next (<see cref="SegmentTouchesHoldingArea"/>) or, for the last point alone, the point itself: its label and
    /// the touching point's distance from the runway centerline, feet. Null when none does.
    /// </summary>
    private static (string Label, double CrossFt)? FirstTouch(List<(string Label, LatLon Position)> path, in RunwayRectangle rect)
    {
        for (int i = 0; i < path.Count; i++)
        {
            LatLon nearest = (i + 1 < path.Count) ? NearestToCenterline(path[i].Position, path[i + 1].Position, rect) : path[i].Position;
            if (TouchesHoldingArea(nearest, rect))
            {
                string label = (i + 1 < path.Count) ? $"{path[i].Label} → {path[i + 1].Label}" : path[i].Label;
                return (label, CrossTrackNm(rect, nearest) * GeoMath.FeetPerNm);
            }
        }

        return null;
    }

    /// <summary>
    /// How far (ft) an existing node lies from where the target seats the bar, for the reuse tolerance: straight-line to the
    /// snap point for a bar seated on paint, else the difference between the node's cross-track
    /// <paramref name="nodeCrossFt"/> and the ideal distance.
    /// </summary>
    private static double ReuseDistanceFt(BarTarget target, GroundNode node, double nodeCrossFt) =>
        (target.SnapPoint is { } snap)
            ? GeoMath.DistanceNm(node.Position, snap) * GeoMath.FeetPerNm
            : Math.Abs(nodeCrossFt - (target.IdealNm * GeoMath.FeetPerNm));

    /// <summary>
    /// Whether an existing node at cross-track <paramref name="nodeCrossFt"/> can be reused as the target's bar: within
    /// <see cref="HoldShortSnapFt"/> of where the target seats it (<see cref="ReuseDistanceFt"/>), and, for a bar seated on
    /// paint, no more than <see cref="PaintRunwaySideToleranceFt"/> on the runway side of the snap point.
    /// </summary>
    private static bool IsReusableAt(BarTarget target, GroundNode node, double nodeCrossFt) =>
        (ReuseDistanceFt(target, node, nodeCrossFt) <= HoldShortSnapFt)
        && ((target.SnapPoint is null) || (nodeCrossFt >= (target.IdealNm * GeoMath.FeetPerNm) - PaintRunwaySideToleranceFt));

    private static BarPlacement ProcessBoundaryEdge(CrossingContext context, Crossing crossing, BarTarget target, ref int nextNodeId)
    {
        (AirportGroundLayout layout, CoordinateIndex coordIndex) = context;
        (RunwayRectangle rect, double idealNm, _) = target;
        (GroundEdge edge, GroundNode onNode, GroundNode offNode) = crossing;
        double crossOff = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(offNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading));
        double crossOn = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(onNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading));

        bool offReusable = IsReusableAt(target, offNode, crossOff * GeoMath.FeetPerNm);

        // Don't reuse junction nodes (connected to multiple taxiways) as hold-short —
        // aircraft holding short would block other taxiways. Only reuse simple
        // intermediate nodes that serve a single taxiway.
        bool isJunction = HasMultipleTaxiwayConnections(offNode.Id, layout);

        if (offReusable && (offNode.Type != GroundNodeType.RunwayHoldShort) && !isJunction)
        {
            // Existing node is close enough — upgrade it to hold-short in place
            offNode.Type = GroundNodeType.RunwayHoldShort;
            offNode.RunwayId = rect.CombinedId;

            Log.LogDebug("Reused node {NodeId} as hold-short for {Runway} on {Taxiway}", offNode.Id, rect.CombinedId, edge.TaxiwayName);
            return new BarPlacement(offNode, Fallback: null);
        }

        if (crossOff < idealNm)
        {
            // Off-node is still inside the ideal hold-short band. Walk outward
            // along the same taxiway to find a segment that straddles the ideal
            // distance, then interpolate on that segment. Only activates for
            // wide runways where the ideal exceeds typical boundary-edge length.
            return FindHoldShortInsertionPoint(context, crossing, target, ref nextNodeId);
        }

        // Interpolate a new HS node at the correct cross-track distance within
        // the current boundary edge (off-node is beyond the ideal hold-short distance).
        double denom = crossOff - crossOn;
        if (Math.Abs(denom) < 1e-9)
        {
            return new BarPlacement(Bar: null, Fallback: null);
        }

        double fraction = (idealNm - crossOn) / denom;
        fraction = Math.Clamp(fraction, MinEdgeFraction, MaxEdgeFraction);

        double hsLat = onNode.Position.Lat + fraction * (offNode.Position.Lat - onNode.Position.Lat);
        double hsLon = onNode.Position.Lon + fraction * (offNode.Position.Lon - onNode.Position.Lon);

        int hsId = nextNodeId++;
        var hsNode = new GroundNode
        {
            Id = hsId,
            Position = new LatLon(hsLat, hsLon),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rect.CombinedId,
            Origin = $"RunwayCrossing:hold-short@{rect.CombinedId}",
        };
        layout.Nodes[hsId] = hsNode;
        coordIndex.Add(hsLat, hsLon, hsId);

        SplitEdgeAtOneNode(layout, edge, hsNode);

        Log.LogDebug(
            "Runway crossing: {Taxiway} boundary at {Runway} — hold-short node {NodeId} at ({Lat:F6}, {Lon:F6})",
            edge.TaxiwayName,
            rect.CombinedId,
            hsId,
            hsLat,
            hsLon
        );
        return new BarPlacement(hsNode, Fallback: null);
    }

    /// <summary>
    /// After HS node insertion, connect the on-runway side of each HS node pair
    /// with RWY centerline edges. For each HS node, identifies the neighbor that's
    /// closer to the runway centerline (the on-runway dead-end), sorts them by
    /// along-track position, and links consecutive ones.
    /// </summary>
    private static void ConnectOnRunwayNodes(AirportGroundLayout layout, in RunwayRectangle rect, CoordinateIndex coordIndex, ref int nextNodeId)
    {
        string rwyEdgeName = $"RWY{rect.CombinedId}";

        // Classify all nodes as on/off runway for walk lookups
        var onRunwaySet = new HashSet<int>();
        foreach ((int nid, GroundNode? n) in layout.Nodes)
        {
            if (IsOnRunway(n.Position, rect))
            {
                onRunwaySet.Add(nid);
            }
        }

        // For each HS node, walk from the on-runway neighbor toward the
        // centerline to find the best representative node. Then project that
        // node onto the actual runway centerline and create a new node there.
        // This ensures runway edges follow the straight centerline exactly.
        var centerlineNodes = new List<(int Id, double AlongTrack)>();
        var seen = new HashSet<int>();

        // Track which on-runway nodes we projected so we can connect them
        // to their centerline projection with a short perpendicular edge.

        foreach ((int nodeId, GroundNode? node) in layout.Nodes.ToList())
        {
            if (node.Type != GroundNodeType.RunwayHoldShort)
            {
                continue;
            }

            if (node.RunwayId is not { } rId || !rId.Equals(rect.CombinedId))
            {
                continue;
            }

            int bestId = FindCenterlineNode(nodeId, layout, rect, onRunwaySet);

            if (bestId == -1 || !seen.Add(bestId))
            {
                continue;
            }

            GroundNode bestNode = layout.Nodes[bestId];
            double alongTrack = GeoMath.AlongTrackDistanceNm(bestNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading);
            double crossTrack = GeoMath.SignedCrossTrackDistanceNm(bestNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading);
            double crossTrackFt = Math.Abs(crossTrack) * GeoMath.FeetPerNm;

            if (crossTrackFt < 5.0)
            {
                centerlineNodes.Add((bestId, alongTrack));
            }
            else
            {
                int projId = ResolveCenterlineProjectionNode(layout, coordIndex, bestNode, alongTrack, rect, ref nextNodeId);
                centerlineNodes.Add((projId, alongTrack));
            }
        }

        if (centerlineNodes.Count < 2)
        {
            return;
        }

        centerlineNodes.Sort((a, b) => a.AlongTrack.CompareTo(b.AlongTrack));

        for (int i = 0; i < centerlineNodes.Count - 1; i++)
        {
            int fromId = centerlineNodes[i].Id;
            int toId = centerlineNodes[i + 1].Id;
            if (fromId == toId)
            {
                continue;
            }

            GroundNode from = layout.Nodes[fromId];
            GroundNode to = layout.Nodes[toId];
            double dist = GeoMath.DistanceNm(from.Position, to.Position);

            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [from, to],
                    TaxiwayName = rwyEdgeName,
                    DistanceNm = dist,
                    Origin = $"RunwayCrossing:rwy-edge@{rect.CombinedId}",
                }
            );

            Log.LogDebug("Runway centerline edge: {From}->{To} on {Runway} ({DistFt:F0}ft)", fromId, toId, rect.CombinedId, dist * GeoMath.FeetPerNm);
        }
    }

    /// <summary>
    /// Resolves the node to use as a runway-centerline projection for <paramref name="bestNode"/>.
    /// If a pre-existing taxiway intersection already sits on the projected centerline point (within
    /// <see cref="Fillet.FilletConstants.CoincidentNodeThresholdFt"/>), that node is reused — instead
    /// of minting a coincident node the post-execute fillet pass would otherwise have to merge.
    /// Either way a short <c>RWY…:link</c> edge connects the on-runway node to the projection.
    /// </summary>
    private static int ResolveCenterlineProjectionNode(
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        GroundNode bestNode,
        double alongTrack,
        in RunwayRectangle rect,
        ref int nextNodeId
    )
    {
        (double clLat, double clLon) = GeoMath.ProjectPointRaw(rect.RefLat, rect.RefLon, rect.TrueHeading.Degrees, alongTrack);
        int bestId = bestNode.Id;

        int reuseId = FindCoincidentIntersection(layout, clLat, clLon, Fillet.FilletConstants.CoincidentNodeThresholdFt, bestId);
        if (reuseId != -1)
        {
            GroundNode reuseNode = layout.Nodes[reuseId];
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [bestNode, reuseNode],
                    TaxiwayName = $"RWY{rect.CombinedId}:link",
                    DistanceNm = GeoMath.DistanceNm(bestNode.Position, reuseNode.Position),
                    Origin = $"RunwayCrossing:centerline-link@{rect.CombinedId} #{bestId}↔#{reuseId}",
                }
            );
            Log.LogDebug("Reused #{ReuseId} as centerline projection for #{BestId} on {Runway}", reuseId, bestId, rect.CombinedId);
            return reuseId;
        }

        int clId = nextNodeId++;
        var clNode = new GroundNode
        {
            Id = clId,
            Position = new LatLon(clLat, clLon),
            Type = GroundNodeType.TaxiwayIntersection,
            Origin = $"RunwayCrossing:centerline-projection@{rect.CombinedId} from #{bestId}",
        };
        layout.Nodes[clId] = clNode;
        coordIndex.Add(clLat, clLon, clId);

        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [bestNode, clNode],
                TaxiwayName = $"RWY{rect.CombinedId}:link",
                DistanceNm = GeoMath.DistanceNm(bestNode.Position, new LatLon(clLat, clLon)),
                Origin = $"RunwayCrossing:centerline-link@{rect.CombinedId} #{bestId}↔#{clId}",
            }
        );

        Log.LogDebug("Projected #{OnId} onto centerline as #{ClId} ({AlongFt:F0}ft along)", bestId, clId, alongTrack * GeoMath.FeetPerNm);
        return clId;
    }

    /// <summary>
    /// Returns the id of an existing <see cref="GroundNodeType.TaxiwayIntersection"/> node within
    /// <paramref name="thresholdFt"/> of (<paramref name="lat"/>, <paramref name="lon"/>), excluding
    /// <paramref name="excludeId"/>; -1 if none. Used so the centerline projection can reuse a
    /// coincident node rather than create one. Mirrors the intersection-only domain the retired
    /// post-execute coincident-node merge operated on.
    /// </summary>
    private static int FindCoincidentIntersection(AirportGroundLayout layout, double lat, double lon, double thresholdFt, int excludeId)
    {
        int bestId = -1;
        double bestFt = double.MaxValue;
        var target = new LatLon(lat, lon);
        foreach ((int id, GroundNode? node) in layout.Nodes)
        {
            if ((id == excludeId) || (node.Type != GroundNodeType.TaxiwayIntersection))
            {
                continue;
            }

            double distFt = GeoMath.DistanceNm(node.Position, target) * GeoMath.FeetPerNm;
            if ((distFt <= thresholdFt) && (distFt < bestFt))
            {
                bestFt = distFt;
                bestId = id;
            }
        }

        return bestId;
    }

    /// <summary>
    /// Width-only hold-short distance from runway centerline (ft), the runway width standing in for the Aircraft Design Group.
    /// Read only by <see cref="BuildRunwayRectangle(GroundRunway)"/> for a runway with neither a stored resolved standoff
    /// (<see cref="GroundRunway.HoldShortStandoffFt"/>) nor an authored <c>holdShortDistance</c>, as in a layout serialised
    /// before the standoff was stored. The layout build resolves S by <see cref="HoldShortStandoff.BaseSeparationFt"/>,
    /// which also reads the runway's length, ILS category and elevation.
    /// </summary>
    internal static double HoldShortDistanceForWidth(double runwayWidthFt)
    {
        if (runwayWidthFt < 75.0)
        {
            return 125.0; // ADG I/II small GA
        }

        if (runwayWidthFt < 100.0)
        {
            return 150.0; // ADG II/III regional
        }

        if (runwayWidthFt < 150.0)
        {
            return 200.0; // ADG III narrow-body
        }

        if (runwayWidthFt < 200.0)
        {
            return 250.0; // ADG IV/V
        }

        return 280.0; // ADG V/VI wide body / CAT III
    }

    /// <summary>
    /// Starting from an HS node, walks along same-taxiway edges (through
    /// shape-point nodes, which may be off-runway) toward the runway centerline.
    /// Returns the node with the smallest cross-track distance to the centerline.
    /// </summary>
    private static int FindCenterlineNode(int hsNodeId, AirportGroundLayout layout, in RunwayRectangle rect, HashSet<int> onRunwaySet)
    {
        // Find which taxiway this hold-short is on
        string? hsTaxiway = null;
        foreach (GroundEdge edge in layout.Edges)
        {
            if (edge.HasNode(hsNodeId) && !edge.IsRunwayCenterline)
            {
                hsTaxiway = edge.TaxiwayName;
                break;
            }
        }

        if (hsTaxiway is null)
        {
            Log.LogDebug("  FindCenterline(HS#{Hs}): no taxiway edge found", hsNodeId);
            return -1;
        }

        // BFS walk along same-taxiway edges toward the centerline.
        // Walk through all nodes (including off-runway shape-points) but
        // only consider on-runway nodes as centerline candidates.
        int bestId = -1;
        double bestCrossTrack = double.MaxValue;
        var visited = new HashSet<int> { hsNodeId };

        var queue = new Queue<int>();
        queue.Enqueue(hsNodeId);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();

            foreach (GroundEdge edge in layout.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    continue;
                }

                if (!edge.HasNode(current))
                {
                    continue;
                }

                // Follow same-taxiway edges to stay on this taxiway's path
                if (edge.TaxiwayName != hsTaxiway)
                {
                    continue;
                }

                int nextId = edge.OtherNodeId(current);

                if (!visited.Add(nextId))
                {
                    continue;
                }

                if (!layout.Nodes.TryGetValue(nextId, out GroundNode? nextNode))
                {
                    continue;
                }

                double ct = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(nextNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading));

                Log.LogDebug(
                    "  FindCenterline(HS#{Hs}): walked to #{Next} via {Tw} crossTrack={Ct:F0}ft onRunway={On} (best: #{Best} at {BestCt:F0}ft)",
                    hsNodeId,
                    nextId,
                    edge.TaxiwayName,
                    ct * GeoMath.FeetPerNm,
                    onRunwaySet.Contains(nextId),
                    bestId,
                    bestCrossTrack * GeoMath.FeetPerNm
                );

                // Only on-runway nodes are candidates for the centerline representative
                if (onRunwaySet.Contains(nextId) && (ct < bestCrossTrack))
                {
                    bestCrossTrack = ct;
                    bestId = nextId;
                }

                // Keep walking — the next node might be closer to centerline
                // Stop when we've crossed the centerline and are moving away
                // (cross-track increasing past the best), or hit a non-taxiway node
                if ((bestId != -1) && (ct > bestCrossTrack * 2))
                {
                    continue; // Don't enqueue — too far past centerline
                }

                queue.Enqueue(nextId);
            }
        }

        return bestId;
    }

    /// <summary>
    /// Walks outward from the off-node along the same taxiway until a segment
    /// straddles the ideal hold-short distance, then interpolates and inserts
    /// a hold-short node there. Used when the boundary edge's off-node is
    /// still inside the FAA hold-short band — common at wide CAT III runways
    /// where the ideal distance is 250-280 ft from centerline. <paramref name="target"/> carries the crossing's
    /// centerline standoff D.
    /// </summary>
    private static BarPlacement FindHoldShortInsertionPoint(CrossingContext context, Crossing crossing, BarTarget target, ref int nextNodeId)
    {
        (AirportGroundLayout layout, _) = context;
        (RunwayRectangle rect, double idealNm, _) = target;
        (GroundEdge boundaryEdge, GroundNode startOnNode, GroundNode startOffNode) = crossing;
        string taxiwayName = boundaryEdge.TaxiwayName;
        GroundNode prevNode = startOnNode;
        GroundNode currentNode = startOffNode;
        GroundEdge lastEdge = boundaryEdge;
        var visited = new HashSet<int> { startOnNode.Id, startOffNode.Id };

        for (int hop = 0; hop < HoldShortWalkMaxHops; hop++)
        {
            double currentCrossNm = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(currentNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading)
            );

            if (currentCrossNm >= idealNm)
            {
                // lastEdge straddles the ideal band. Interpolate on it.
                return new BarPlacement(
                    InterpolateAndInsert(context, new Crossing(lastEdge, prevNode, currentNode), target, ref nextNodeId),
                    Fallback: null
                );
            }

            if (PickWalkContinuation(layout, new WalkStep(taxiwayName, prevNode, currentNode), visited, rect) is not { } next)
            {
                // Dead end: no same-taxiway continuation.
                return new BarPlacement(
                    Bar: null,
                    DeadEndFallback(context, new DeadEnd(prevNode, currentNode, lastEdge, taxiwayName), target, ref nextNodeId)
                );
            }

            prevNode = currentNode;
            currentNode = next.Far;
            lastEdge = next.Edge;
            visited.Add(next.Far.Id);
        }

        // Exhausted hop budget — fall back at the last reached node.
        return new BarPlacement(
            Bar: null,
            DeadEndFallback(context, new DeadEnd(prevNode, currentNode, lastEdge, taxiwayName), target, ref nextNodeId)
        );
    }

    /// <summary>
    /// The walker's next edge out of the step's current node: same taxiway, not the runway centerline, to a node not yet
    /// visited, the straightest continuation of the incoming hop, ties to the larger cross-track (more outward). Null at a
    /// dead end.
    /// </summary>
    internal static (GroundEdge Edge, GroundNode Far)? PickWalkContinuation(
        AirportGroundLayout layout,
        WalkStep step,
        HashSet<int> visited,
        in RunwayRectangle rect
    )
    {
        double incomingBearing = GeoMath.BearingTo(step.Prev.Position, step.Current.Position);
        (GroundEdge Edge, GroundNode Far)? best = null;
        double bestTurnDelta = double.MaxValue;
        double bestFarCross = -1.0;
        foreach (GroundEdge candidate in layout.Edges)
        {
            if (candidate.IsRunwayCenterline || !candidate.MatchesTaxiway(step.Taxiway) || !candidate.HasNode(step.Current.Id))
            {
                continue;
            }

            int farId = candidate.OtherNodeId(step.Current.Id);
            if (visited.Contains(farId) || !layout.Nodes.TryGetValue(farId, out GroundNode? farNode))
            {
                continue;
            }

            double turnDelta = Math.Abs(NormalizeBearingDelta(GeoMath.BearingTo(step.Current.Position, farNode.Position) - incomingBearing));
            double farCross = CrossTrackNm(rect, farNode.Position);
            if ((turnDelta < bestTurnDelta - 0.5) || ((Math.Abs(turnDelta - bestTurnDelta) <= 0.5) && (farCross > bestFarCross)))
            {
                bestTurnDelta = turnDelta;
                bestFarCross = farCross;
                best = (candidate, farNode);
            }
        }

        return best;
    }

    private static double CrossTrackNm(in RunwayRectangle rect, LatLon position) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading));

    /// <summary>
    /// Where the placement would seat a bar at <paramref name="idealNm"/> for <paramref name="crossing"/>, without
    /// changing the layout: the nodes the outward walk passes (the boundary edge's off-node when short of the ideal
    /// distance, and every hop short of the
    /// ideal distance) and the interpolated point, or a null point when the walk dead-ends first. Follows the same edges
    /// as <see cref="ProcessBoundaryEdge"/> and <see cref="FindHoldShortInsertionPoint"/>.
    /// </summary>
    private static (List<GroundNode> Passed, LatLon? Point) ProbeStandoffPoint(
        AirportGroundLayout layout,
        Crossing crossing,
        in RunwayRectangle rect,
        double idealNm
    )
    {
        var passed = new List<GroundNode>();
        if (CrossTrackNm(rect, crossing.Off.Position) < idealNm)
        {
            passed.Add(crossing.Off);
        }

        GroundNode prev = crossing.On;
        GroundNode current = crossing.Off;
        var visited = new HashSet<int> { crossing.On.Id, crossing.Off.Id };
        for (int hop = 0; hop < HoldShortWalkMaxHops; hop++)
        {
            if (CrossTrackNm(rect, current.Position) >= idealNm)
            {
                return (passed, InterpolateAtCrossTrack(rect, prev.Position, current.Position, idealNm));
            }

            if (PickWalkContinuation(layout, new WalkStep(crossing.Edge.TaxiwayName, prev, current), visited, rect) is not { } next)
            {
                return (passed, null);
            }

            prev = current;
            current = next.Far;
            visited.Add(current.Id);
            if (CrossTrackNm(rect, current.Position) < idealNm)
            {
                passed.Add(current);
            }
        }

        return (passed, null);
    }

    /// <summary>
    /// The point between <paramref name="near"/> and <paramref name="far"/> at <paramref name="idealNm"/> from the
    /// centerline, as the placement interpolates it.
    /// </summary>
    private static LatLon InterpolateAtCrossTrack(in RunwayRectangle rect, LatLon near, LatLon far, double idealNm)
    {
        double crossNear = CrossTrackNm(rect, near);
        double denom = CrossTrackNm(rect, far) - crossNear;
        double fraction = (Math.Abs(denom) < 1e-12) ? MaxEdgeFraction : Math.Clamp((idealNm - crossNear) / denom, MinEdgeFraction, MaxEdgeFraction);
        return new LatLon(near.Lat + (fraction * (far.Lat - near.Lat)), near.Lon + (fraction * (far.Lon - near.Lon)));
    }

    /// <summary>
    /// Where along a segment (0 at its near node, 1 at its far node) a minted hold-short node goes, from the raw
    /// interpolation <paramref name="rawFraction"/>. The fraction is clamped to 0.01-0.99, off the endpoints, except for a
    /// bar seated on paint (<paramref name="onPaint"/>) whose snap point lies within the clamp of an endpoint refused for
    /// reuse (<see cref="IsReusableAt"/>): that bar is minted on its snap point.
    /// </summary>
    internal static double MintFraction(double rawFraction, bool onPaint, bool nearRefused, bool farRefused)
    {
        bool onPaintNearTheNearEnd = onPaint && nearRefused && (rawFraction >= 0.0) && (rawFraction < MinEdgeFraction);
        bool onPaintNearTheFarEnd = onPaint && farRefused && (rawFraction > MaxEdgeFraction) && (rawFraction <= 1.0);
        return (onPaintNearTheNearEnd || onPaintNearTheFarEnd) ? rawFraction : Math.Clamp(rawFraction, MinEdgeFraction, MaxEdgeFraction);
    }

    /// <summary>
    /// Interpolates a hold-short node on <paramref name="segment"/>'s edge between its near node (toward the runway) and
    /// its far node at <paramref name="target"/>'s ideal cross-track distance. If the ideal lies within
    /// <see cref="HoldShortSnapFt"/> of either endpoint, upgrades that endpoint in place; a paint-paired bar refuses an
    /// endpoint on the runway side of the paint and mints on the paint (<see cref="MintFraction"/>). Returns the bar's
    /// node, or null when the segment has no extent across the runway to interpolate on.
    /// </summary>
    private static GroundNode? InterpolateAndInsert(CrossingContext context, Crossing segment, BarTarget target, ref int nextNodeId)
    {
        (AirportGroundLayout layout, CoordinateIndex coordIndex) = context;
        (RunwayRectangle rect, double idealNm, _) = target;
        (GroundEdge edge, GroundNode nearNode, GroundNode farNode) = segment;
        double crossNearFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(nearNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading))
            * GeoMath.FeetPerNm;
        double crossFarFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(farNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading))
            * GeoMath.FeetPerNm;
        double idealFt = idealNm * GeoMath.FeetPerNm;

        bool nearReusable = IsReusableAt(target, nearNode, crossNearFt);
        bool farReusable = IsReusableAt(target, farNode, crossFarFt);

        // Prefer upgrading an endpoint the target can reuse.
        if (nearReusable && TryUpgradeToHoldShort(nearNode, rect, layout))
        {
            return nearNode;
        }

        if (farReusable && TryUpgradeToHoldShort(farNode, rect, layout))
        {
            return farNode;
        }

        double denom = crossFarFt - crossNearFt;
        if (Math.Abs(denom) < 1e-9)
        {
            return null;
        }

        double fraction = MintFraction(
            (idealFt - crossNearFt) / denom,
            onPaint: target.SnapPoint is not null,
            nearRefused: !nearReusable,
            farRefused: !farReusable
        );
        double hsLat = nearNode.Position.Lat + fraction * (farNode.Position.Lat - nearNode.Position.Lat);
        double hsLon = nearNode.Position.Lon + fraction * (farNode.Position.Lon - nearNode.Position.Lon);

        int hsId = nextNodeId++;
        var hsNode = new GroundNode
        {
            Id = hsId,
            Position = new LatLon(hsLat, hsLon),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rect.CombinedId,
            Origin = $"RunwayCrossing:hold-short@{rect.CombinedId}",
        };
        layout.Nodes[hsId] = hsNode;
        coordIndex.Add(hsLat, hsLon, hsId);

        SplitEdgeAtOneNode(layout, edge, hsNode);

        Log.LogDebug(
            "Runway crossing (walker): {Taxiway} boundary at {Runway} — hold-short node {NodeId} at ({Lat:F6}, {Lon:F6})",
            edge.TaxiwayName,
            rect.CombinedId,
            hsId,
            hsLat,
            hsLon
        );
        return hsNode;
    }

    /// <summary>
    /// Upgrades an existing non-junction, non-HS node to a RunwayHoldShort for
    /// the given runway. Returns false if the node is already HS or is a junction
    /// between multiple taxiways (upgrading would block other taxi paths).
    /// </summary>
    private static bool TryUpgradeToHoldShort(GroundNode node, in RunwayRectangle rect, AirportGroundLayout layout)
    {
        if (node.Type == GroundNodeType.RunwayHoldShort)
        {
            return true; // already HS for this or another runway — nothing to do
        }

        if (HasMultipleTaxiwayConnections(node.Id, layout))
        {
            return false;
        }

        node.Type = GroundNodeType.RunwayHoldShort;
        node.RunwayId = rect.CombinedId;
        Log.LogDebug("Upgraded node {NodeId} to hold-short for {Runway}", node.Id, rect.CombinedId);
        return true;
    }

    /// <summary>
    /// Dead-end fallback: the walker couldn't reach the ideal hold-short distance.
    /// Prefer upgrading the farthest reached node (currentNode) in place. If that
    /// node is a junction, try upgrading prevNode. Only split and interpolate as a
    /// last resort. A bar placed short of the junction where the taxiway meets another
    /// one is returned so <see cref="DropRedundantFallbackBars"/> can drop it when the
    /// junction's other routes all reach a bar of the runway.
    /// </summary>
    private static FallbackBar? DeadEndFallback(CrossingContext context, DeadEnd deadEnd, BarTarget target, ref int nextNodeId)
    {
        (AirportGroundLayout layout, CoordinateIndex coordIndex) = context;
        (RunwayRectangle rect, double idealNm, _) = target;
        (GroundNode prevNode, GroundNode currentNode, GroundEdge lastEdge, string taxiwayName) = deadEnd;
        double idealFt = idealNm * GeoMath.FeetPerNm;
        double currentCrossFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(currentNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading))
            * GeoMath.FeetPerNm;

        // Best outcome: upgrade the farthest reached node (it's closest to ideal). It serves this taxiway only, so no other
        // taxiway joins there.
        if (TryUpgradeToHoldShort(currentNode, rect, layout))
        {
            Log.LogDebug(
                "Hold-short dead-end: upgraded {Node} for {Runway} on {Taxiway} (ideal={Ideal:F0}ft, actual={Actual:F0}ft)",
                currentNode.Id,
                rect.CombinedId,
                taxiwayName,
                idealFt,
                currentCrossFt
            );
            return null;
        }

        // Second choice: upgrade prevNode if it's within reuse tolerance of ideal
        double prevCrossFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(prevNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading))
            * GeoMath.FeetPerNm;
        GroundNodeType prevType = prevNode.Type;
        if (Math.Abs(prevCrossFt - idealFt) <= HoldShortSnapFt && TryUpgradeToHoldShort(prevNode, rect, layout))
        {
            Log.LogDebug(
                "Hold-short dead-end: upgraded {Node} (prev) for {Runway} on {Taxiway} (ideal={Ideal:F0}ft, actual={Actual:F0}ft)",
                prevNode.Id,
                rect.CombinedId,
                taxiwayName,
                idealFt,
                prevCrossFt
            );
            return (prevType == GroundNodeType.RunwayHoldShort)
                ? null
                : new FallbackBar(prevNode, currentNode, rect, prevType, Split: null, RuleCeilingFt(rect));
        }

        // Last resort: interpolate 25 ft before the dead-end on the last edge.
        // But skip if either endpoint is a junction — splitting a junction-to-junction
        // edge creates near-coincident nodes that get collapsed by fillet merge.
        bool nearIsJunction = HasMultipleTaxiwayConnections(prevNode.Id, layout);
        bool farIsJunction = HasMultipleTaxiwayConnections(currentNode.Id, layout);
        double edgeLengthFt = GeoMath.DistanceNm(prevNode.Position, currentNode.Position) * GeoMath.FeetPerNm;
        if (nearIsJunction && farIsJunction)
        {
            Log.LogWarning(
                "Hold-short dead-end on {Taxiway} for {Runway}: both endpoints are junctions and edge is too short ({Len:F0}ft), skipping",
                taxiwayName,
                rect.CombinedId,
                edgeLengthFt
            );
            return null;
        }

        double backoffFraction = Math.Clamp(1.0 - (HoldShortFallbackBufferFt / edgeLengthFt), 0.01, 0.99);
        double hsLat = prevNode.Position.Lat + backoffFraction * (currentNode.Position.Lat - prevNode.Position.Lat);
        double hsLon = prevNode.Position.Lon + backoffFraction * (currentNode.Position.Lon - prevNode.Position.Lon);

        int hsId = nextNodeId++;
        var hsNode = new GroundNode
        {
            Id = hsId,
            Position = new LatLon(hsLat, hsLon),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rect.CombinedId,
            Origin = $"RunwayCrossing:hold-short@{rect.CombinedId}",
        };
        layout.Nodes[hsId] = hsNode;
        coordIndex.Add(hsLat, hsLon, hsId);

        int lastEdgeIndex = layout.Edges.IndexOf(lastEdge);
        GroundEdge? successor = (lastEdgeIndex + 1 < layout.Edges.Count) ? layout.Edges[lastEdgeIndex + 1] : null;
        SplitEdgeAtOneNode(layout, lastEdge, hsNode);

        Log.LogWarning(
            "Hold-short junction clamp on {Taxiway} for {Runway}: ideal={Ideal:F0}ft reached={Reached:F0}ft, placing at dead-end backoff",
            taxiwayName,
            rect.CombinedId,
            idealFt,
            currentCrossFt
        );
        return new FallbackBar(
            hsNode,
            currentNode,
            rect,
            GroundNodeType.TaxiwayIntersection,
            new SplitEdgeRecord(lastEdge, successor),
            RuleCeilingFt(rect)
        );
    }

    /// <summary>
    /// Drops each dead-end fallback bar whose junction needs no bar of its own: every route out of the junction, other
    /// than back down the branch to the bar and onto the runway, reaches another bar of the runway within the holding
    /// distance. OAK P ends at J, whose 28R bar is a few feet on. Runs once every runway's bars are placed, so the verdict
    /// does not depend on the order runways and taxiways were processed in; a fallback bar never counts as the bar a
    /// route reaches. A dropped bar reverts to the node it was, keeping every node id.
    /// </summary>
    internal static void DropRedundantFallbackBars(AirportGroundLayout layout, List<FallbackBar> fallbackBars)
    {
        if (fallbackBars.Count == 0)
        {
            return;
        }

        Dictionary<int, List<GroundEdge>> adjacency = BuildTaxiwayAdjacency(layout);
        var fallbackIds = new HashSet<int>(fallbackBars.Select(fallback => fallback.Bar.Id));
        foreach (FallbackBar fallback in fallbackBars)
        {
            if (!EveryRouteOutReachesBar(fallback, adjacency, fallbackIds))
            {
                Log.LogDebug(
                    "Hold-short dead-end bar #{Bar} for {Runway} kept: a route out of junction #{Junction} passes the holding distance without a bar",
                    fallback.Bar.Id,
                    fallback.Rect.CombinedId,
                    fallback.Junction.Id
                );
                continue;
            }

            RemoveFallbackBar(layout, fallback);
            Log.LogDebug(
                "Hold-short dead-end bar #{Bar} for {Runway} dropped: every route out of junction #{Junction} reaches a bar of the runway",
                fallback.Bar.Id,
                fallback.Rect.CombinedId,
                fallback.Junction.Id
            );
        }
    }

    /// <summary>
    /// Undoes a fallback bar: a node upgraded in place gets its type back; a node the fallback split into an edge is
    /// removed and the edge restored whole, so the graph is the one no bar was placed on (the node's id stays unused).
    /// When a later split took one of the halves, the node stays as a plain taxiway node instead.
    /// </summary>
    private static void RemoveFallbackBar(AirportGroundLayout layout, FallbackBar fallback)
    {
        GroundNode bar = fallback.Bar;
        bar.Type = fallback.PreviousType;
        bar.RunwayId = null;
        if (fallback.Split is not { } split)
        {
            return;
        }

        List<GroundEdge> halves = [.. layout.Edges.Where(edge => edge.HasNode(bar.Id))];
        bool halvesIntact =
            (halves.Count == 2)
            && halves.All(half => half.HasNode(split.Edge.Nodes[0].Id) || half.HasNode(split.Edge.Nodes[1].Id))
            && (halves[0].OtherNodeId(bar.Id) != halves[1].OtherNodeId(bar.Id));
        if (!halvesIntact)
        {
            return;
        }

        foreach (GroundEdge half in halves)
        {
            layout.Edges.Remove(half);
        }

        int successorIndex = (split.Successor is { } successor) ? layout.Edges.IndexOf(successor) : -1;
        layout.Edges.Insert((successorIndex >= 0) ? successorIndex : layout.Edges.Count, split.Edge);
        layout.Nodes.Remove(bar.Id);
    }

    /// <summary>Every node's non-centerline edges (node adjacency lists are not wired up until after crossing detection).</summary>
    private static Dictionary<int, List<GroundEdge>> BuildTaxiwayAdjacency(AirportGroundLayout layout)
    {
        var adjacency = new Dictionary<int, List<GroundEdge>>();
        foreach (GroundEdge edge in layout.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            foreach (GroundNode node in edge.Nodes)
            {
                if (!adjacency.TryGetValue(node.Id, out List<GroundEdge>? edges))
                {
                    edges = [];
                    adjacency[node.Id] = edges;
                }

                edges.Add(edge);
            }
        }

        return adjacency;
    }

    /// <summary>
    /// Shortest-path walk out of the fallback's junction, not back to its bar: true when every route stops at a bar of
    /// the runway (not another fallback bar), on the runway, or at a dead end before passing the holding distance or the
    /// holding distance's worth of taxiing.
    /// </summary>
    private static bool EveryRouteOutReachesBar(FallbackBar fallback, Dictionary<int, List<GroundEdge>> adjacency, HashSet<int> fallbackIds)
    {
        var bestFt = new Dictionary<int, double> { [fallback.Junction.Id] = 0 };
        var frontier = new PriorityQueue<GroundNode, double>();
        frontier.Enqueue(fallback.Junction, 0);
        while (frontier.TryDequeue(out GroundNode? node, out double distFt))
        {
            if (distFt > bestFt[node.Id])
            {
                continue;
            }

            RouteNode verdict = ClassifyRouteNode(fallback, node, distFt, fallbackIds);
            if (verdict == RouteNode.Escapes)
            {
                return false;
            }

            if (verdict == RouteNode.Closed)
            {
                Log.LogDebug(
                    "  route out of junction #{Junction} ends at #{Node} ({Type}) after {Dist:F0} ft",
                    fallback.Junction.Id,
                    node.Id,
                    node.Type,
                    distFt
                );
                continue;
            }

            foreach (GroundEdge edge in adjacency.GetValueOrDefault(node.Id, []))
            {
                GroundNode next = (edge.Nodes[0].Id == node.Id) ? edge.Nodes[1] : edge.Nodes[0];
                double nextFt = distFt + (edge.DistanceNm * GeoMath.FeetPerNm);
                bool backToBar = (node.Id == fallback.Junction.Id) && (next.Id == fallback.Bar.Id);
                if (backToBar || (bestFt.TryGetValue(next.Id, out double knownFt) && (knownFt <= nextFt)))
                {
                    continue;
                }

                bestFt[next.Id] = nextFt;
                frontier.Enqueue(next, nextFt);
            }
        }

        return true;
    }

    internal enum RouteNode
    {
        Open,
        Closed,
        Escapes,
    }

    internal static RouteNode ClassifyRouteNode(FallbackBar fallback, GroundNode node, double distFt, HashSet<int> fallbackIds)
    {
        RunwayRectangle rect = fallback.Rect;
        if ((node.Id != fallback.Junction.Id) && IsOnRunway(node.Position, rect))
        {
            return RouteNode.Closed;
        }

        // The budget binds before the bar: a bar farther along than the holding distance is not the junction's marking.
        // The holding distance here is the fallback's ceiling: the angle term's, S + W_twy / 2, since an angled bar sits out
        // to it, raised to the farthest bar of the runway seated on paint.
        double holdingFt = fallback.CeilingFt;
        if (distFt > holdingFt)
        {
            return RouteNode.Escapes;
        }

        bool isRunwayBar = (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } id) && id.Equals(rect.CombinedId);
        if (isRunwayBar && !fallbackIds.Contains(node.Id))
        {
            return RouteNode.Closed;
        }

        double crossFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(node.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading)) * GeoMath.FeetPerNm;
        // A node between S and an angled bar at D is still inside the holding area along its taxiway, so it escapes only past the ceiling.
        return (crossFt >= holdingFt - AirportGroundLayout.HoldingDistanceToleranceFt) ? RouteNode.Escapes : RouteNode.Open;
    }

    /// <summary>The rule's holding-distance ceiling for a fallback bar, feet: the angle term's, S + W_twy / 2.</summary>
    internal static double RuleCeilingFt(in RunwayRectangle rect) =>
        (rect.HoldShortNm * GeoMath.FeetPerNm) + (HoldShortStandoff.TaxiwayWidthProxyFt(RunwayWidthFt(rect)) / 2.0);

    /// <summary>
    /// The fallback bars with each one's ceiling raised to the largest D_paint among its runway's bars seated on paint, so
    /// a route out of a fallback junction that reaches such a bar beyond S + W_twy / 2 is judged against it. With no bar
    /// seated on paint every ceiling stays the rule's.
    /// </summary>
    internal static List<FallbackBar> RaiseCeilingsToSnappedBars(List<FallbackBar> fallbackBars, List<SnappedBar> snappedBars)
    {
        var raised = new List<FallbackBar>(fallbackBars.Count);
        foreach (FallbackBar fallback in fallbackBars)
        {
            double ceilingFt = fallback.CeilingFt;
            foreach (SnappedBar snapped in snappedBars)
            {
                if (snapped.Rect.CombinedId.Equals(fallback.Rect.CombinedId))
                {
                    ceilingFt = Math.Max(ceilingFt, snapped.PaintFt);
                }
            }

            raised.Add(fallback with { CeilingFt = ceilingFt });
        }

        return raised;
    }

    /// <summary>Normalizes a bearing delta in degrees to the range (-180, 180].</summary>
    private static double NormalizeBearingDelta(double deltaDeg)
    {
        double d = deltaDeg % 360.0;
        if (d > 180.0)
        {
            d -= 360.0;
        }
        else if (d <= -180.0)
        {
            d += 360.0;
        }

        return d;
    }

    /// <summary>
    /// Returns true if the node has edges connecting to more than one distinct
    /// non-runway taxiway, making it a junction that shouldn't be reused as hold-short.
    /// Checks layout.Edges directly because node adjacency lists (GroundNode.Edges)
    /// are not populated until after crossing detection completes.
    /// </summary>
    private static bool HasMultipleTaxiwayConnections(int nodeId, AirportGroundLayout layout)
    {
        string? firstTaxiway = null;
        foreach (GroundEdge edge in layout.Edges)
        {
            if (!edge.HasNode(nodeId))
            {
                continue;
            }

            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            if (firstTaxiway is null)
            {
                firstTaxiway = edge.TaxiwayName;
            }
            else if (!edge.MatchesTaxiway(firstTaxiway))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits an edge into two segments through one intermediate node.
    /// Replaces: from-to with from-mid, mid-to.
    /// </summary>
    internal static void SplitEdgeAtOneNode(AirportGroundLayout layout, GroundEdge edge, GroundNode midNode)
    {
        layout.Edges.Remove(edge);

        GroundNode nodeA = edge.Nodes[0];
        GroundNode nodeB = edge.Nodes[1];

        var edgeA = new GroundEdge
        {
            Nodes = [nodeA, midNode],
            TaxiwayName = edge.TaxiwayName,
            DistanceNm = GeoMath.DistanceNm(nodeA.Position, midNode.Position),
            Origin = $"RunwayCrossing:split-edge({edge.Origin ?? edge.TaxiwayName})",
        };

        var edgeB = new GroundEdge
        {
            Nodes = [midNode, nodeB],
            TaxiwayName = edge.TaxiwayName,
            DistanceNm = GeoMath.DistanceNm(midNode.Position, nodeB.Position),
            Origin = $"RunwayCrossing:split-edge({edge.Origin ?? edge.TaxiwayName})",
        };

        layout.Edges.Add(edgeA);
        layout.Edges.Add(edgeB);
        // Node adjacency lists are wired up in GeoJsonParser Step 7.
    }
}

/// <summary>
/// Geometric representation of a runway as an oriented rectangle for node classification.
/// </summary>
internal readonly struct RunwayRectangle
{
    public required double RefLat { get; init; }
    public required double RefLon { get; init; }
    public required TrueHeading TrueHeading { get; init; }
    public required double LengthNm { get; init; }
    public required double HalfWidthNm { get; init; }
    public required double HoldShortNm { get; init; }
    public required RunwayIdentifier CombinedId { get; init; }
}

/// <summary>
/// What <see cref="RunwayCrossingDetector.DetectRunwayCrossings"/> found for one runway: its width, the base hold-short
/// separation S its bars were seated at (<see cref="HoldShortStandoff.BaseSeparationFt"/>), its junction fallback bars and
/// its bars seated on paint.
/// </summary>
internal readonly record struct RunwayCrossingResult(
    double WidthFt,
    double HoldShortStandoffFt,
    List<FallbackBar> FallbackBars,
    List<SnappedBar> SnappedBars
);

/// <summary>
/// A bar the dead-end fallback placed short of the holding distance on a taxiway that ends at a junction with another
/// taxiway, and what the node was before, so the bar can be dropped. <see cref="CeilingFt"/> is the holding distance a
/// route out of the junction is judged against (<see cref="RunwayCrossingDetector.RaiseCeilingsToSnappedBars"/>).
/// </summary>
internal readonly record struct FallbackBar(
    GroundNode Bar,
    GroundNode Junction,
    RunwayRectangle Rect,
    GroundNodeType PreviousType,
    SplitEdgeRecord? Split,
    double CeilingFt
);

/// <summary>A bar seated on a painted bar: its node, its runway's rectangle and D_paint, feet.</summary>
internal readonly record struct SnappedBar(GroundNode Bar, RunwayRectangle Rect, double PaintFt);

/// <summary>
/// Where <see cref="RunwayCrossingDetector"/> seated a crossing's bar (null when it seated none) and the fallback bar it
/// left, if any.
/// </summary>
internal readonly record struct BarPlacement(GroundNode? Bar, FallbackBar? Fallback);

/// <summary>
/// One runway's bar seating: the layout and its coordinate index, every planned runway, the paint to pair with (null with
/// no videomap) and the fallback bars and paint-seated bars collected so far.
/// </summary>
internal sealed record RunwaySeating(
    CrossingContext Context,
    IReadOnlyList<RunwayPlan> Runways,
    PaintSource? Paint,
    List<FallbackBar> FallbackBars,
    List<SnappedBar> SnappedBars
);

/// <summary>
/// A runway planned for bar placement (<see cref="RunwayCrossingDetector.PlanRunway"/>): its feature, its parse-time
/// rectangle (carrying S), its navdata width and its base separation S in feet.
/// </summary>
internal readonly record struct RunwayPlan(GeoJsonParser.RunwayFeature Feature, RunwayRectangle Rect, double WidthFt, double StandoffFt);

/// <summary>
/// A taxiway edge and its two ends, the one nearer the runway first: a boundary edge crossing a runway's edge (its node
/// on the runway, then its node off it), or a walk segment straddling the ideal distance.
/// </summary>
internal readonly record struct Crossing(GroundEdge Edge, GroundNode On, GroundNode Off);

/// <summary>
/// Where a crossing's bar goes: the runway's parse-time rectangle (carrying S), the ideal distance, nm, and for a bar seated
/// on paint its snap point, which an existing node must lie within the reuse tolerance of to be reused.
/// </summary>
internal readonly record struct BarTarget(RunwayRectangle Rect, double IdealNm, LatLon? SnapPoint);

/// <summary>The layout a crossing's bar is seated in and its coordinate index.</summary>
internal readonly record struct CrossingContext(AirportGroundLayout Layout, CoordinateIndex CoordIndex);

/// <summary>One step of the hold-short walker: the taxiway it follows, the node it came from and the node it is at.</summary>
internal readonly record struct WalkStep(string Taxiway, GroundNode Prev, GroundNode Current);

/// <summary>Where the hold-short walker stopped short of the holding distance: the last two nodes, the edge between them and the taxiway.</summary>
internal readonly record struct DeadEnd(GroundNode Prev, GroundNode Current, GroundEdge LastEdge, string Taxiway);

/// <summary>An edge the fallback split for its bar, and the edge that followed it in the layout's edge list (the order later passes see).</summary>
internal readonly record struct SplitEdgeRecord(GroundEdge Edge, GroundEdge? Successor);
