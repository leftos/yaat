using System.Globalization;
using System.Text;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The paint snap: OAK built with its tower-cab videomap seats each hold-short point that pairs with a painted bar on the
/// paint (<see cref="RunwayPaintPairing"/>), and leaves every other point where the standoff rule seats it from the runway's
/// paint-calibrated S.
/// </summary>
public class RunwayPaintSnapTests(ITestOutputHelper output)
{
    private static readonly string OakLayoutPath = Path.Combine("TestData", "oak.geojson");
    private static readonly string OakMapPath = Path.Combine("TestData", "TowerCabMaps", "oak.geojson");

    private const double FeetPerDegree = GeoMath.FeetPerNm * 60.0;

    /// <summary>The nearest shape point (ft) any of OAK's moved bars keeps beside it on its taxiway (F at 15/33).</summary>
    private const double NearestShapePointFt = 15.9;

    [Fact]
    public void Oak_EveryPairedBar_SitsOnItsPaint()
    {
        TowerCabVideoMap map = OakMap();
        AirportGroundLayout layout = ParseOak(map, FilletMode.Standard);
        AirportGroundLayout ruleLayout = ParseOak(map: null, FilletMode.Standard);
        AirportGroundLayout snapGraph = ParseOak(map, FilletMode.None);

        // A bar minted for the snap sits on the paint line, and every paint-seated bar lies within the merge distance of its
        // snap point: an existing shape point that close becomes the bar, moved onto the paint when it sat on the runway side,
        // so no reused bar ends more than the runway-side tolerance on the runway side of the paint and no bar is left with a
        // shape point of its own taxiway beside it on the graph the snap built.
        GroundNode[] snapped = SnappedNodes(layout);
        var report = new StringBuilder();
        var runwaySide = new List<string>();
        bool allOnPaint = true;
        int minted = 0;
        foreach (GroundNode node in snapped)
        {
            VideoMapSegment segment = map.Segments[SegmentOf(node)];
            RunwayRectangle rect = RectOf(layout, node);
            bool isMinted = IsMinted(node);
            minted += isMinted ? 1 : 0;
            (double offLineFt, double beyondEndFt) = OffPaint(node.Position, segment);
            (double toSnapFt, LatLon? snapPoint) = NearestSnap(layout, node, segment);
            bool onPaint = (toSnapFt <= RunwayCrossingDetector.PaintNodeMergeFt) && (!isMinted || (offLineFt <= 1.0));
            allOnPaint &= onPaint && (beyondEndFt <= RunwayPaintPairing.BarExtensionFt);

            // How far the node sits on the runway side of its snap point (negative: on the far side).
            double shortfallFt = (snapPoint is { } snap) ? CrossFt(rect, snap) - CrossFt(rect, node.Position) : double.NaN;
            if (!isMinted && !(shortfallFt <= 1.0))
            {
                runwaySide.Add($"#{node.Id} {node.RunwayId} {TaxiwayOf(layout, node)} {shortfallFt:F1} ft");
            }

            double ruleFt = HoldShortNodes(ruleLayout)
                .Where(other => Equals(other.RunwayId, node.RunwayId))
                .MinBy(other => GeoMath.DistanceNm(other.Position, node.Position))
                is { } ruleBar
                ? CrossFt(rect, ruleBar.Position)
                : double.NaN;
            double nearEndFt = Math.Min(CrossFt(rect, segment.A), CrossFt(rect, segment.B));
            report.AppendLine(
                $"#{node.Id} {node.RunwayId} {TaxiwayOf(layout, node)} seg {node.PaintSegmentIndex} ({(isMinted ? "minted" : "reused")}): "
                    + $"D_paint {CrossFt(rect, node.Position):F1} ft, D_rule (no-map bar) {ruleFt:F1} ft, near end {nearEndFt:F1} ft, "
                    + $"{offLineFt:F2} ft off the line, {toSnapFt:F2} ft from the snap point, {beyondEndFt:F1} ft past an end, "
                    + $"{shortfallFt:F1} ft on the runway side of the snap point"
            );
        }

        report.AppendLine($"{snapped.Length} snapped: {minted} minted, {snapped.Length - minted} reused");
        output.WriteLine(report.ToString());
        Assert.True(allOnPaint, report.ToString());
        Assert.True(
            runwaySide.Count == 0,
            $"reused bars more than 1 ft on the runway side of their paint: {string.Join("; ", runwaySide)}\n{report}"
        );
        List<string> crowded =
        [
            .. SnappedNodes(snapGraph).SelectMany(bar => ShapePointsBeside(snapGraph, bar, RunwayCrossingDetector.PaintNodeMergeFt)),
        ];
        Assert.True(
            crowded.Count == 0,
            $"paint-seated bars with a shape point within {RunwayCrossingDetector.PaintNodeMergeFt} ft: {string.Join("; ", crowded)}"
        );
        Assert.True(snapped.Length == 31, $"{snapped.Length} OAK bars snapped, expected 31:\n{report}");
    }

    /// <summary>
    /// OAK built on its tower-cab videomap: from the north 28R holding position on B, the route to the south one is B straight across
    /// the runway, as on the graph built without the map, for a piston and a jet alike.
    /// </summary>
    [Theory]
    [InlineData(AircraftCategory.Piston)]
    [InlineData(AircraftCategory.Jet)]
    public void Oak_28R_FromTheNorthBarOnB_RoutesToTheSouthBarAlongB(AircraftCategory category)
    {
        AirportGroundLayout layout = ParseOak(OakMap(), FilletMode.Standard);
        List<GroundNode> bars = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "B");
        GroundNode bar28L = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "B")[0];
        GroundNode north = bars.MaxBy(n => GeoMath.DistanceNm(n.Position, bar28L.Position))!;
        GroundNode south = bars.Where(n => n.Id != north.Id).MinBy(n => GeoMath.DistanceNm(n.Position, north.Position))!;

        GoalRoute? across = TaxiPathfinder.FindRouteToNearestGoal(
            layout,
            north.Id,
            category,
            WakeTurbulenceData.WakeClass.Small,
            NearestGoalRequest.To(new HashSet<int> { south.Id })
        );

        List<TaxiRouteSegment> segments = Assert.IsType<TaxiRoute>(across?.Route).Segments;
        string path = string.Join(" ", segments.Select(s => $"#{s.FromNodeId}>#{s.ToNodeId}[{s.Edge.Edge.TaxiwayName}]"));
        Assert.True(
            segments.All(s => s.Edge.Edge.MatchesTaxiway("B")),
            $"the route from the north 28R bar on B (#{north.Id}) to the south one (#{south.Id}) leaves B: {path}"
        );
    }

    /// <summary>
    /// Every OAK edge built on its videomap is at least <see cref="GeometricAdmissibility.NoOpEdgeThresholdNm"/> long: a bar
    /// minted closer than that to a node leaves an edge the pathfinder reads as a zero-length no-op, which blocks every route
    /// that starts on the bar.
    /// </summary>
    [Fact]
    public void Oak_MapBuilt_HasNoEdgeShorterThanTheNoOpThreshold()
    {
        AirportGroundLayout layout = ParseOak(OakMap(), FilletMode.Standard);
        List<string> shortEdges =
        [
            .. layout
                .Edges.Where(edge => edge.DistanceNm < GeometricAdmissibility.NoOpEdgeThresholdNm)
                .Select(edge => $"#{edge.Nodes[0].Id}-#{edge.Nodes[1].Id} [{edge.TaxiwayName}] {edge.DistanceNm * GeoMath.FeetPerNm:F3} ft"),
        ];

        Assert.True(shortEdges.Count == 0, $"{shortEdges.Count} OAK edges are shorter than the no-op threshold: {string.Join("; ", shortEdges)}");
    }

    /// <summary>
    /// OAK's 15/33 bar on C pairs with paint about 1.7 ft from C node #355, which sits on the runway side of it: the node moves
    /// onto the paint and becomes the bar, so no bar is minted beside it.
    /// </summary>
    [Fact]
    public void Oak_15_33_C_BarNearAShapePoint_TakesThatNode() => AssertTheShapePointBecomesTheBar("15", "C", 355);

    /// <summary>
    /// OAK's 28R/10L bar on B pairs with paint about 1.2 ft from B node #188, which sits on the runway side of it: the node moves
    /// onto the paint and becomes the bar, so no bar is minted beside it.
    /// </summary>
    [Fact]
    public void Oak_28R_B_BarNearAShapePoint_TakesThatNode() => AssertTheShapePointBecomesTheBar("28R", "B", 188);

    /// <summary>
    /// On the graph the snap built, the <paramref name="runway"/> bar on <paramref name="taxiway"/> nearest shape point
    /// <paramref name="shapePointId"/>'s unsnapped position lies on its snap point, no bar is minted within
    /// <see cref="RunwayCrossingDetector.PaintNodeMergeFt"/> of that position, and no shape point of the bar's taxiway lies within
    /// <see cref="NearestShapePointFt"/> of the bar.
    /// </summary>
    private static void AssertTheShapePointBecomesTheBar(string runway, string taxiway, int shapePointId)
    {
        TowerCabVideoMap map = OakMap();
        AirportGroundLayout layout = ParseOak(map, FilletMode.None);
        LatLon unsnapped = ParseOak(map: null, FilletMode.None).Nodes[shapePointId].Position;
        GroundNode bar =
            SnappedNodes(layout)
                .Where(node => (node.RunwayId?.ToString().Contains(runway, StringComparison.Ordinal) == true) && (TaxiwayOf(layout, node) == taxiway))
                .MinBy(node => GeoMath.DistanceNm(node.Position, unsnapped))
            ?? throw new Xunit.Sdk.XunitException($"OAK has no snapped {runway} bar on {taxiway}");
        (double toSnapFt, _) = NearestSnap(layout, bar, map.Segments[SegmentOf(bar)]);
        List<string> mintedBeside =
        [
            .. layout
                .Nodes.Values.Where(node =>
                    IsMinted(node) && (GeoMath.DistanceNm(node.Position, unsnapped) * GeoMath.FeetPerNm <= RunwayCrossingDetector.PaintNodeMergeFt)
                )
                .Select(node => $"#{node.Id} ({node.Origin})"),
        ];
        List<string> beside = ShapePointsBeside(layout, bar, NearestShapePointFt);

        string where =
            $"#{bar.Id} ({bar.Origin}): {GeoMath.DistanceNm(bar.Position, unsnapped) * GeoMath.FeetPerNm:F2} ft from #{shapePointId}'s "
            + $"unsnapped position, {toSnapFt:F2} ft from the snap point";
        Assert.True(
            mintedBeside.Count == 0,
            $"bars minted within {RunwayCrossingDetector.PaintNodeMergeFt} ft of #{shapePointId}: {string.Join("; ", mintedBeside)}; {where}"
        );
        Assert.True(toSnapFt <= 0.1, where);
        Assert.True(beside.Count == 0, $"shape points within {NearestShapePointFt} ft of the bar: {string.Join("; ", beside)}; {where}");
    }

    /// <summary>
    /// The shape points of <paramref name="bar"/>'s own taxiway next to it on <paramref name="layout"/>, nodes with two edges of
    /// one taxiway, that lie within <paramref name="withinFt"/> of it, described.
    /// </summary>
    private static List<string> ShapePointsBeside(AirportGroundLayout layout, GroundNode bar, double withinFt) =>
        [
            .. layout
                .Edges.Where(edge => !edge.IsRunwayCenterline && edge.HasNode(bar.Id))
                .Select(edge => layout.Nodes[edge.OtherNodeId(bar.Id)])
                .Where(neighbour =>
                {
                    List<GroundEdge> edges = [.. layout.Edges.Where(edge => !edge.IsRunwayCenterline && edge.HasNode(neighbour.Id))];
                    return (edges.Count == 2) && edges.All(edge => edge.MatchesTaxiway(edges[0].TaxiwayName));
                })
                .Where(neighbour => GeoMath.DistanceNm(neighbour.Position, bar.Position) * GeoMath.FeetPerNm < withinFt)
                .Select(neighbour =>
                    $"#{bar.Id} {bar.RunwayId} {TaxiwayOf(layout, bar)}: #{neighbour.Id} at "
                    + $"{GeoMath.DistanceNm(neighbour.Position, bar.Position) * GeoMath.FeetPerNm:F2} ft"
                ),
        ];

    /// <summary>
    /// A snap point crowds a node's neighbour (<see cref="RunwayCrossingDetector.SnapCrowdsNeighbour"/>) when it lies within
    /// <see cref="FilletConstants.CoincidentNodeThresholdFt"/> of the node at the other end of one of its taxi edges. Staged
    /// on OAK's first paint-seated bar and one of its edge neighbours, from the map-built parse.
    /// </summary>
    [Fact]
    public void SnapCrowdsNeighbour_SnapWithinFiveFeetOfAnEdgeNeighbour_IsTrue_AndFarIsFalse()
    {
        AirportGroundLayout layout = ParseOak(OakMap(), FilletMode.None);
        GroundNode bar = SnappedNodes(layout)[0];
        List<GroundNode> neighbours =
        [
            .. layout.Edges.Where(edge => !edge.IsRunwayCenterline && edge.HasNode(bar.Id)).Select(edge => layout.Nodes[edge.OtherNodeId(bar.Id)]),
        ];
        GroundNode neighbour = neighbours[0];
        double nearestNeighbourFt = neighbours.Min(other => GeoMath.DistanceNm(other.Position, bar.Position) * GeoMath.FeetPerNm);
        Assert.True(
            nearestNeighbourFt > FilletConstants.CoincidentNodeThresholdFt,
            $"bar #{bar.Id}'s nearest edge neighbour is {nearestNeighbourFt:F2} ft away, within {FilletConstants.CoincidentNodeThresholdFt} ft"
        );

        var towardTheBar = new TrueHeading(GeoMath.BearingTo(neighbour.Position, bar.Position));
        LatLon threeFeetFromTheNeighbour = GeoMath.ProjectPoint(neighbour.Position, towardTheBar, 3.0 / GeoMath.FeetPerNm);

        Assert.True(
            RunwayCrossingDetector.SnapCrowdsNeighbour(bar, threeFeetFromTheNeighbour, layout),
            $"a snap point 3 ft from #{neighbour.Id} toward bar #{bar.Id} does not crowd it"
        );
        Assert.False(
            RunwayCrossingDetector.SnapCrowdsNeighbour(bar, bar.Position, layout),
            $"bar #{bar.Id}'s own position crowds a neighbour {nearestNeighbourFt:F2} ft away"
        );
    }

    [Fact]
    public void MintFraction_KeepsTheClampUnlessThatEndpointWasRefusedForReuse()
    {
        // OAK has no paired crossing whose snap point falls within the clamp of a junction endpoint, so the fraction choice is
        // tested directly. A junction (or an existing hold-short) passes the reuse test and is refused only by the upgrade,
        // so it is not "refused for reuse": the mint keeps the clamp off it.
        Assert.Equal(0.01, RunwayCrossingDetector.MintFraction(0.004, onPaint: true, nearRefused: false, farRefused: false));
        Assert.Equal(0.99, RunwayCrossingDetector.MintFraction(0.996, onPaint: true, nearRefused: false, farRefused: false));

        // The contrast: an endpoint refused for reuse (on the runway side of the paint) mints on the snap point.
        Assert.Equal(0.004, RunwayCrossingDetector.MintFraction(0.004, onPaint: true, nearRefused: true, farRefused: false));
        Assert.Equal(0.996, RunwayCrossingDetector.MintFraction(0.996, onPaint: true, nearRefused: false, farRefused: true));

        // The rule path and a refusal at the other end keep the clamp.
        Assert.Equal(0.01, RunwayCrossingDetector.MintFraction(0.004, onPaint: false, nearRefused: true, farRefused: true));
        Assert.Equal(0.01, RunwayCrossingDetector.MintFraction(0.004, onPaint: true, nearRefused: false, farRefused: true));
    }

    /// <summary>
    /// The end clearance (<see cref="RunwayCrossingDetector.ClearOfEdgeEnds"/>) moves a minted bar just past
    /// <see cref="GeometricAdmissibility.NoOpEdgeThresholdNm"/> from the edge end it sits too close to, and leaves a bar clear
    /// of both ends where it is. The positions are real, from the map-built OAK parse: #188 and #189,
    /// the ends of a B edge beside 28R/10L. An edge too short to hold a point clear of both ends, a point on that B edge and a
    /// second point 1 ft further along it, keeps the fraction it was given.
    /// </summary>
    [Theory]
    [InlineData(EndClearance.NearEndMoved)]
    [InlineData(EndClearance.FarEndMoved)]
    [InlineData(EndClearance.ClearOfBothEnds)]
    [InlineData(EndClearance.EdgeTooShort)]
    public void ClearOfEdgeEnds_MovesAMintedBarOffTheEdgeEndItSitsTooCloseTo(EndClearance scenario)
    {
        AirportGroundLayout layout = ParseOak(OakMap(), FilletMode.Standard);
        (LatLon from, LatLon to) = (layout.Nodes[188].Position, layout.Nodes[189].Position);
        if (scenario == EndClearance.EdgeTooShort)
        {
            double edgeFt = GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;
            (from, to) = (Along(from, to, 0.5), Along(from, to, 0.5 + (ShortEdgeFt / edgeFt)));
        }

        RunwayIdentifier runway28R = layout.Runways.Single(runway => runway.Name.Contains("28R", StringComparison.Ordinal)).Id;
        double fraction = scenario switch
        {
            EndClearance.NearEndMoved => 0.05,
            EndClearance.FarEndMoved => 0.95,
            _ => 0.5,
        };
        double thresholdFt = GeometricAdmissibility.NoOpEdgeThresholdNm * GeoMath.FeetPerNm;

        (double cleared, double farEndMoveFt) = RunwayCrossingDetector.ClearOfEdgeEnds(from, to, fraction, "B", runway28R);
        double clearedEndFt;
        switch (scenario)
        {
            case EndClearance.NearEndMoved:
                clearedEndFt = GeoMath.DistanceNm(from, Along(from, to, cleared)) * GeoMath.FeetPerNm;
                Assert.True(cleared > fraction, $"the bar moved to fraction {cleared:F4}, not past {fraction:F4} off the near end");
                Assert.Equal(0.0, farEndMoveFt);
                break;
            case EndClearance.FarEndMoved:
                clearedEndFt = GeoMath.DistanceNm(Along(from, to, cleared), to) * GeoMath.FeetPerNm;
                Assert.True(cleared < fraction, $"the bar moved to fraction {cleared:F4}, not below {fraction:F4} off the far end");
                Assert.True(farEndMoveFt > 0.0, "the far-end move reported no distance");
                break;
            case EndClearance.EdgeTooShort:
                Assert.Equal(fraction, cleared);
                Assert.Equal(0.0, farEndMoveFt);
                double lengthFt = GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;
                Assert.True(
                    lengthFt < (2.0 * thresholdFt),
                    $"the staged edge is {lengthFt:F2} ft, not too short to hold a point {thresholdFt:F2} ft off both ends"
                );
                return;
            default:
                Assert.Equal(fraction, cleared);
                Assert.Equal(0.0, farEndMoveFt);
                return;
        }

        Assert.True(clearedEndFt > thresholdFt, $"{clearedEndFt:F3} ft from the end is not past the no-op threshold {thresholdFt:F3} ft");
        Assert.True(
            clearedEndFt <= (thresholdFt * 1.02),
            $"{clearedEndFt:F3} ft from the end is more than 2% past the no-op threshold {thresholdFt:F3} ft"
        );
    }

    /// <summary>Which end of an edge the end clearance is asked about (<see cref="ClearOfEdgeEnds_MovesAMintedBarOffTheEdgeEndItSitsTooCloseTo"/>).</summary>
    public enum EndClearance
    {
        NearEndMoved,
        FarEndMoved,
        ClearOfBothEnds,
        EdgeTooShort,
    }

    /// <summary>The length (ft) of the edge <see cref="EndClearance.EdgeTooShort"/> stages, under twice the no-op threshold.</summary>
    private const double ShortEdgeFt = 1.0;

    /// <summary>The point <paramref name="fraction"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static LatLon Along(LatLon from, LatLon to, double fraction) =>
        new(from.Lat + (fraction * (to.Lat - from.Lat)), from.Lon + (fraction * (to.Lon - from.Lon)));

    [Fact]
    public void Oak_ARemovedBar_LeavesItsNodeWhereTheCalibratedStandoffSeatsIt_AndEveryOtherPairUnchanged()
    {
        // The 28R/10L J bar: its walk passes the P/J junction the P crossing's walk ends at.
        TowerCabVideoMap map = OakMap();
        AirportGroundLayout full = ParseOak(map, FilletMode.None);
        AirportGroundLayout ruleLayout = ParseOak(map: null, FilletMode.None);
        GroundNode removedBar = SnappedNodes(full)
            .First(node => (node.RunwayId?.ToString().Contains("28R", StringComparison.Ordinal) == true) && (TaxiwayOf(full, node) == "J"));
        int removed = SegmentOf(removedBar);

        AirportGroundLayout reduced = ParseOak(Without(map, removed), FilletMode.None);

        // The unpaired node sits where the rule's no-map bar does, moved out by the runway's calibrated S less the rule's S.
        GroundNode[] unpaired = [.. HoldShortNodes(reduced).Where(node => node.PaintSegmentIndex is null)];
        GroundNode node = Assert.Single(unpaired);
        GroundNode ruleBar = HoldShortNodes(ruleLayout)
            .Where(other => Equals(other.RunwayId, node.RunwayId))
            .MinBy(other => GeoMath.DistanceNm(other.Position, node.Position))!;
        GroundRunway reducedRunway = reduced.Runways.Single(runway => runway.Id.Equals(node.RunwayId));
        GroundRunway ruleRunway = ruleLayout.Runways.Single(runway => runway.Id.Equals(node.RunwayId));
        double calibratedMoveFt = reducedRunway.HoldShortStandoffFt!.Value - ruleRunway.HoldShortStandoffFt!.Value;
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(reducedRunway);
        double beyondRuleFt = CrossFt(rect, node.Position) - CrossFt(rect, ruleBar.Position);
        Assert.True(
            Math.Abs(beyondRuleFt - calibratedMoveFt) <= 0.1,
            $"unpaired J bar sits {beyondRuleFt:F2} ft beyond its no-map bar; calibrated S is {calibratedMoveFt:F2} ft beyond the rule's"
        );

        foreach (GroundNode kept in SnappedNodes(full).Where(bar => bar.Id != removedBar.Id))
        {
            GroundNode match =
                SnappedNodes(reduced).MinBy(bar => GeoMath.DistanceNm(bar.Position, kept.Position))
                ?? throw new Xunit.Sdk.XunitException($"no snapped bar left to match #{kept.Id} once segment {removed} was removed");
            double movedFt = GeoMath.DistanceNm(match.Position, kept.Position) * GeoMath.FeetPerNm;
            int matchIndex = SegmentOf(match);
            int originalIndex = (matchIndex < removed) ? matchIndex : matchIndex + 1;
            Assert.True(movedFt <= 0.1, $"snapped bar #{kept.Id} moved {movedFt:F2} ft when segment {removed} was removed");
            Assert.Equal(kept.PaintSegmentIndex, originalIndex);
        }
    }

    [Fact]
    public void Oak_SnappedBars_AreHoldLines()
    {
        AirportGroundLayout layout = ParseOak(OakMap(), FilletMode.Standard);

        foreach (GroundNode node in SnappedNodes(layout))
        {
            Assert.True(node.IsShortBar == false, $"snapped bar #{node.Id} ({node.RunwayId}) is not marked a hold line");
            RunwayRectangle rect = RectOf(layout, node);
            double crossFt = CrossFt(rect, node.Position);
            double standoffFt = rect.HoldShortNm * GeoMath.FeetPerNm;
            if (crossFt < standoffFt - AirportGroundLayout.HoldingDistanceToleranceFt)
            {
                output.WriteLine($"snapped bar #{node.Id} {node.RunwayId} sits inside S - 5: {crossFt:F1} ft against S {standoffFt:F1} ft");
            }

            Assert.True(
                layout.OutwardWalkBoundFt(rect) >= crossFt,
                $"{rect.CombinedId} outward walk bound {layout.OutwardWalkBoundFt(rect):F1} ft is inside snapped bar #{node.Id} at {crossFt:F1} ft"
            );
        }
    }

    [Fact]
    public void Oak_TwoParses_SameIdsAndPositions()
    {
        TowerCabVideoMap map = OakMap();
        AirportGroundLayout first = ParseOak(map, FilletMode.Standard);
        AirportGroundLayout second = ParseOak(map, FilletMode.Standard);

        Assert.Equal(first.Nodes.Keys.Order(), second.Nodes.Keys.Order());
        foreach ((int id, GroundNode node) in first.Nodes)
        {
            GroundNode other = second.Nodes[id];
            Assert.Equal(node.Position, other.Position);
            Assert.Equal(node.Type, other.Type);
            Assert.Equal(node.PaintSegmentIndex, other.PaintSegmentIndex);
            Assert.Equal(node.IsShortBar, other.IsShortBar);
        }
    }

    [Fact]
    public void Oak_PaintSegmentIndex_SurvivesSerialization_AndANoMapLayoutWritesNone()
    {
        AirportGroundLayout snappedLayout = ParseOak(OakMap(), FilletMode.Standard);
        AirportGroundLayout copy = GroundLayoutSerializer.Deserialize(
            new MemoryStream(GroundLayoutSerializer.Serialize(snappedLayout)),
            snappedLayout.AirportId
        );
        foreach (GroundNode node in snappedLayout.Nodes.Values)
        {
            Assert.Equal(node.PaintSegmentIndex, copy.Nodes[node.Id].PaintSegmentIndex);
        }

        AirportGroundLayout ruleLayout = ParseOak(map: null, FilletMode.Standard);
        Assert.DoesNotContain(ruleLayout.Nodes.Values, node => node.PaintSegmentIndex is not null);
        string ruleJson = Encoding.UTF8.GetString(GroundLayoutSerializer.Serialize(ruleLayout));
        Assert.DoesNotContain(nameof(GroundNode.PaintSegmentIndex), ruleJson);
    }

    [Fact]
    public void Pair_BandEdges_AreInclusive()
    {
        PairingFixture fixture = Fixture();
        VideoMapSegment inward = Translate(fixture.Segment, fixture.Rect, fixture.Pair.SnapPoint, -20.0);
        double paintFt = PairOrFail(fixture, [inward], fixture.RuleFt).PaintFt;

        // D_paint − D_rule ≤ 100 ft.
        Assert.NotNull(PairWith(fixture, [inward], paintFt - 100.0 + 0.01));
        Assert.Null(PairWith(fixture, [inward], paintFt - 100.0 - 0.01));
        Assert.NotNull(PairWith(fixture, [inward], paintFt - 99.0));
        Assert.Null(PairWith(fixture, [inward], paintFt - 101.0));

        // D_rule − D_paint ≤ 60 ft.
        Assert.NotNull(PairWith(fixture, [inward], paintFt + 60.0 - 0.01));
        Assert.Null(PairWith(fixture, [inward], paintFt + 60.0 + 0.01));
        Assert.NotNull(PairWith(fixture, [inward], paintFt + 59.0));
        Assert.Null(PairWith(fixture, [inward], paintFt + 61.0));
    }

    [Fact]
    public void Pair_InsideHalfWidthPlus50_DoesNotPair()
    {
        PairingFixture fixture = Fixture();
        double floorFt = (fixture.Rect.HalfWidthNm * GeoMath.FeetPerNm) + RunwayPaintPairing.MinBeyondRunwayEdgeFt;
        VideoMapSegment inside = Translate(fixture.Segment, fixture.Rect, fixture.Pair.SnapPoint, floorFt - 5.0 - fixture.Pair.PaintFt);
        VideoMapSegment outside = Translate(fixture.Segment, fixture.Rect, fixture.Pair.SnapPoint, floorFt + 5.0 - fixture.Pair.PaintFt);

        Assert.NotNull(PairWith(fixture, [outside], floorFt + 5.0));
        Assert.Null(PairWith(fixture, [inside], floorFt - 5.0));
    }

    [Fact]
    public void Pair_ABarMirroredAcrossTheCenterline_NeverMeetsTheTaxiwayChain()
    {
        PairingFixture fixture = Fixture();
        double signedFt = SignedCrossFt(fixture.Rect, fixture.Pair.SnapPoint);
        VideoMapSegment mirrored = Translate(fixture.Segment, fixture.Rect, fixture.Pair.SnapPoint, -2.0 * Math.Abs(signedFt));

        Assert.Null(PairWith(fixture, [mirrored], fixture.RuleFt));
    }

    [Fact]
    public void Pair_ABarCrossingARunwayCenterline_DoesNotPair()
    {
        // A real OAK hold bar lengthened along its own line until it reaches across a runway centerline: it still meets its
        // taxiway at the same snap point, at the same angle, so only the runway-crossing condition can refuse it.
        PairingFixture fixture = Fixture(candidate => CrossesAnyRunway(Lengthen(candidate.Segment, 6000.0), candidate.Paint.RunwayCenterlines));
        VideoMapSegment longBar = Lengthen(fixture.Segment, 6000.0);
        Assert.True(CrossesAnyRunway(longBar, fixture.Paint.RunwayCenterlines), "the lengthened bar crosses no runway centerline");
        Assert.NotNull(PairWith(fixture, [fixture.Segment], fixture.RuleFt));

        Assert.Null(PairWith(fixture, [longBar], fixture.RuleFt));
    }

    [Fact]
    public void Oak_28R_J_PairsPastThePJJunction()
    {
        PjJunction junction = PjJunctionOf28R();
        PaintPair pair =
            RunwayPaintPairing.Pair(junction.Layout, junction.JCrossing, junction.Rect, junction.RuleFt, junction.Paint)
            ?? throw new Xunit.Sdk.XunitException("the 28R/10L J crossing pairs with no paint");

        Assert.True(pair.PaintFt > junction.JunctionFt, $"J pairs at {pair.PaintFt:F1} ft, not past the P/J junction at {junction.JunctionFt:F1} ft");
        Assert.InRange(pair.PaintFt, junction.RuleFt - RunwayPaintPairing.MaxInsideRuleFt, junction.RuleFt + RunwayPaintPairing.MaxOutsideRuleFt);
    }

    [Fact]
    public void Oak_28R_P_DoesNotPairWithTheJBarPastTheJunction()
    {
        // P ends at the P/J junction; the J hold bar beyond it is another taxiway's paint.
        PjJunction junction = PjJunctionOf28R();
        Assert.NotNull(RunwayPaintPairing.Pair(junction.Layout, junction.JCrossing, junction.Rect, junction.RuleFt, junction.Paint));

        Assert.Null(RunwayPaintPairing.Pair(junction.Layout, junction.PCrossing, junction.Rect, junction.RuleFt, junction.Paint));
    }

    [Fact]
    public void Pair_TwoBars_TheFirstAlongTheWalkWins_AndATieGoesToTheLowerIndex()
    {
        // The real bar and a copy of it 20 ft farther along its taxiway, listed first: the real one is crossed first.
        PairingFixture fixture = Fixture();
        VideoMapSegment outward = Translate(fixture.Segment, fixture.Rect, fixture.Pair.SnapPoint, 20.0);

        List<PaintPair> ranked = RunwayPaintPairing.Candidates(
            fixture.Layout,
            fixture.Crossing,
            fixture.Rect,
            fixture.RuleFt,
            fixture.Paint with
            {
                Segments = [outward, fixture.Segment],
            }
        );
        Assert.Equal([1, 0], ranked.Select(pair => pair.SegmentIndex));
        Assert.True(ranked[0].WalkFt < ranked[1].WalkFt);

        PaintPair tie = PairOrFail(fixture, [fixture.Segment, fixture.Segment], fixture.RuleFt);
        Assert.Equal(0, tie.SegmentIndex);
    }

    [Fact]
    public void InnermostFirst_RanksByWalk_ThenDPaint_ThenFileIndex()
    {
        // A chain that turns back toward the runway can put the bar crossed first at the larger D_paint.
        var origin = new LatLon(37.72, -122.22);
        var firstAlongWalk = new PaintPair(SegmentIndex: 7, origin, PaintFt: 280.0, NearEndFt: 275.0, WalkFt: 150.0);
        var smallerPaint = new PaintPair(SegmentIndex: 3, origin, PaintFt: 260.0, NearEndFt: 255.0, WalkFt: 190.0);
        PaintPair samePlaceSmallerPaint = firstAlongWalk with { SegmentIndex = 9, PaintFt = 270.0 };
        PaintPair samePlaceLowerIndex = firstAlongWalk with { SegmentIndex = 2 };

        List<PaintPair> ranked = [smallerPaint, firstAlongWalk, samePlaceSmallerPaint, samePlaceLowerIndex];
        ranked.Sort(RunwayPaintPairing.InnermostFirst);

        Assert.Equal([9, 2, 7, 3], ranked.Select(pair => pair.SegmentIndex));
    }

    [Fact]
    public void RaiseCeilingsToSnappedBars_LetsARouteReachASnappedBarBeyondTheRuleCeiling()
    {
        AirportGroundLayout layout = ParseOak(OakMap(), FilletMode.None);
        foreach (GroundNode snapped in SnappedNodes(layout))
        {
            RunwayRectangle rect = RectOf(layout, snapped);
            double ruleCeilingFt = RunwayCrossingDetector.RuleCeilingFt(rect);
            double paintFt = CrossFt(rect, snapped.Position);
            double tolerance = AirportGroundLayout.HoldingDistanceToleranceFt;

            // A real OAK node off the runway that the rule ceiling counts as escaped and the snapped bar's does not.
            GroundNode? reach = layout
                .Nodes.Values.Where(node =>
                    (node.Type != GroundNodeType.RunwayHoldShort)
                    && !RunwayCrossingDetector.IsOnRunway(node.Position, rect)
                    && (CrossFt(rect, node.Position) >= ruleCeilingFt - tolerance)
                    && (CrossFt(rect, node.Position) < paintFt - tolerance)
                )
                .OrderBy(node => node.Id)
                .FirstOrDefault();
            if (reach is null)
            {
                continue;
            }

            GroundNode junction = layout.Nodes.Values.First(node => (node.Id != reach.Id) && (node.Id != snapped.Id));
            var fallback = new FallbackBar(snapped, junction, rect, GroundNodeType.TaxiwayIntersection, Split: null, ruleCeilingFt);
            List<FallbackBar> raised = RunwayCrossingDetector.RaiseCeilingsToSnappedBars([fallback], [new SnappedBar(snapped, rect, paintFt)]);
            output.WriteLine(
                $"{rect.CombinedId}: node #{reach.Id} at {CrossFt(rect, reach.Position):F1} ft, "
                    + $"rule ceiling {ruleCeilingFt:F1} ft, snapped bar {paintFt:F1} ft"
            );

            Assert.Equal(RunwayCrossingDetector.RouteNode.Escapes, RunwayCrossingDetector.ClassifyRouteNode(fallback, reach, 0.0, []));
            Assert.Equal(RunwayCrossingDetector.RouteNode.Open, RunwayCrossingDetector.ClassifyRouteNode(raised[0], reach, 0.0, []));
            return;
        }

        throw new Xunit.Sdk.XunitException("no OAK snapped bar sits far enough beyond its runway's rule ceiling for a node between them");
    }

    [Fact]
    public void Oak_FallbackBars_KeepTheirNoMapVerdict()
    {
        AirportGroundLayout snappedLayout = ParseOak(OakMap(), FilletMode.Standard);
        AirportGroundLayout ruleLayout = ParseOak(map: null, FilletMode.Standard);

        foreach (GroundRunway runway in snappedLayout.Runways)
        {
            RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
            double ruleCeilingFt = RunwayCrossingDetector.RuleCeilingFt(rect);
            int beyond = SnappedNodes(snappedLayout)
                .Count(node => Equals(node.RunwayId, runway.Id) && (CrossFt(rect, node.Position) > ruleCeilingFt));
            int shortBars = HoldShortNodes(snappedLayout).Count(node => Equals(node.RunwayId, runway.Id) && (node.IsShortBar == true));
            output.WriteLine($"{runway.Name}: ceiling {ruleCeilingFt:F1} ft, {beyond} snapped bars beyond it, {shortBars} short bars");
        }

        Assert.Equal(ShortBarPositions(ruleLayout), ShortBarPositions(snappedLayout));
    }

    private readonly record struct PairingFixture(
        AirportGroundLayout Layout,
        Crossing Crossing,
        RunwayRectangle Rect,
        double RuleFt,
        PaintSource Paint,
        PaintPair Pair,
        VideoMapSegment Segment
    );

    private static PairingFixture Fixture() => Fixture(_ => true);

    /// <summary>
    /// The first OAK bar (by node id) of the no-map, unfilleted layout whose boundary edge reaches it from the runway, that
    /// pairs with a real painted bar and meets <paramref name="accept"/>, with the bar's own position as D_rule.
    /// </summary>
    private static PairingFixture Fixture(Func<PairingFixture, bool> accept)
    {
        TowerCabVideoMap map = OakMap();
        AirportGroundLayout layout = ParseOak(map: null, FilletMode.None);
        PaintSource paint = PaintOf(layout, map);
        foreach (GroundNode bar in HoldShortNodes(layout).OrderBy(node => node.Id))
        {
            RunwayRectangle rect = RectOf(layout, bar);
            foreach (GroundEdge edge in layout.Edges.Where(edge => !edge.IsRunwayCenterline && edge.HasNode(bar.Id)))
            {
                GroundNode near = layout.Nodes[edge.OtherNodeId(bar.Id)];
                if (!RunwayCrossingDetector.IsOnRunway(near.Position, rect))
                {
                    continue;
                }

                var crossing = new Crossing(edge, near, bar);
                double ruleFt = CrossFt(rect, bar.Position);
                if (RunwayPaintPairing.Pair(layout, crossing, rect, ruleFt, paint) is not { } pair)
                {
                    continue;
                }

                var fixture = new PairingFixture(layout, crossing, rect, ruleFt, paint, pair, map.Segments[pair.SegmentIndex]);
                if (accept(fixture))
                {
                    return fixture;
                }
            }
        }

        throw new Xunit.Sdk.XunitException("no OAK bar pairs with paint across its boundary edge and meets the fixture's condition");
    }

    private readonly record struct PjJunction(
        AirportGroundLayout Layout,
        RunwayRectangle Rect,
        Crossing JCrossing,
        Crossing PCrossing,
        double JunctionFt,
        double RuleFt,
        PaintSource Paint
    );

    /// <summary>
    /// OAK's P/J junction beside 28R/10L in the no-map, unfilleted layout: the node that a J edge and a P edge reach from a
    /// node on the runway. D_rule is the no-map 28R/10L bar on J nearest the junction.
    /// </summary>
    private static PjJunction PjJunctionOf28R()
    {
        TowerCabVideoMap map = OakMap();
        AirportGroundLayout layout = ParseOak(map: null, FilletMode.None);
        GroundRunway runway = layout.Runways.Single(candidate => candidate.Name.Contains("28R", StringComparison.Ordinal));
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        foreach (GroundNode junction in layout.Nodes.Values.OrderBy(node => node.Id))
        {
            if ((BoundaryEdge(layout, junction, "J", rect) is not { } jCrossing) || (BoundaryEdge(layout, junction, "P", rect) is not { } pCrossing))
            {
                continue;
            }

            GroundNode ruleBar =
                HoldShortNodes(layout)
                    .Where(node => Equals(node.RunwayId, runway.Id) && (TaxiwayOf(layout, node) == "J"))
                    .MinBy(node => GeoMath.DistanceNm(node.Position, junction.Position))
                ?? throw new Xunit.Sdk.XunitException("OAK has no no-map 28R/10L bar on J");
            return new PjJunction(
                layout,
                rect,
                jCrossing,
                pCrossing,
                CrossFt(rect, junction.Position),
                CrossFt(rect, ruleBar.Position),
                PaintOf(layout, map)
            );
        }

        throw new Xunit.Sdk.XunitException("OAK has no P/J junction reached from 28R/10L");
    }

    /// <summary>The <paramref name="taxiway"/> edge from a node on the runway to <paramref name="off"/>, as a crossing, or null.</summary>
    private static Crossing? BoundaryEdge(AirportGroundLayout layout, GroundNode off, string taxiway, in RunwayRectangle rect)
    {
        foreach (GroundEdge edge in layout.Edges.Where(edge => !edge.IsRunwayCenterline && (edge.TaxiwayName == taxiway) && edge.HasNode(off.Id)))
        {
            GroundNode on = layout.Nodes[edge.OtherNodeId(off.Id)];
            if (RunwayCrossingDetector.IsOnRunway(on.Position, rect) && !RunwayCrossingDetector.IsOnRunway(off.Position, rect))
            {
                return new Crossing(edge, on, off);
            }
        }

        return null;
    }

    private static PaintSource PaintOf(AirportGroundLayout layout, TowerCabVideoMap map) =>
        new(map.Segments, [.. layout.Runways.Select(runway => (IReadOnlyList<LatLon>)[.. runway.Coordinates.Select(c => new LatLon(c.Lat, c.Lon))])]);

    private static PaintPair? PairWith(PairingFixture fixture, IReadOnlyList<VideoMapSegment> segments, double ruleFt) =>
        RunwayPaintPairing.Pair(fixture.Layout, fixture.Crossing, fixture.Rect, ruleFt, fixture.Paint with { Segments = segments });

    private static PaintPair PairOrFail(PairingFixture fixture, IReadOnlyList<VideoMapSegment> segments, double ruleFt) =>
        PairWith(fixture, segments, ruleFt) ?? throw new Xunit.Sdk.XunitException("the control segment set does not pair");

    /// <summary>The map with segment <paramref name="removed"/> left out, the others in their order.</summary>
    private static TowerCabVideoMap Without(TowerCabVideoMap map, int removed)
    {
        var features = new List<string>();
        for (int i = 0; i < map.Segments.Count; i++)
        {
            if (i == removed)
            {
                continue;
            }

            (LatLon a, LatLon b) = (map.Segments[i].A, map.Segments[i].B);
            features.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{{\"type\":\"Feature\",\"geometry\":{{\"type\":\"LineString\","
                        + $"\"coordinates\":[[{a.Lon:R},{a.Lat:R}],[{b.Lon:R},{b.Lat:R}]]}},\"properties\":{{}}}}"
                )
            );
        }

        return TowerCabVideoMap.Read($"{{\"type\":\"FeatureCollection\",\"features\":[{string.Join(",", features)}]}}");
    }

    /// <summary>
    /// The segment moved <paramref name="outwardFt"/> perpendicular to the runway, away from its centerline on
    /// <paramref name="reference"/>'s side.
    /// </summary>
    private static VideoMapSegment Translate(VideoMapSegment segment, in RunwayRectangle rect, LatLon reference, double outwardFt)
    {
        double headingRad = rect.TrueHeading.Degrees * Math.PI / 180.0;
        (double east, double north) = (Math.Cos(headingRad), -Math.Sin(headingRad));
        LatLon probe = Offset(reference, east, north, 1.0, reference.Lat);
        double sign = (Math.Abs(SignedCrossFt(rect, probe)) > Math.Abs(SignedCrossFt(rect, reference))) ? 1.0 : -1.0;
        return new VideoMapSegment(
            Offset(segment.A, east, north, sign * outwardFt, reference.Lat),
            Offset(segment.B, east, north, sign * outwardFt, reference.Lat)
        );
    }

    /// <summary>The segment extended <paramref name="extraFt"/> at each end along its own line.</summary>
    private static VideoMapSegment Lengthen(VideoMapSegment segment, double extraFt)
    {
        double lat0 = segment.A.Lat;
        double dx = (segment.B.Lon - segment.A.Lon) * FeetPerDegree * Math.Cos(lat0 * Math.PI / 180.0);
        double dy = (segment.B.Lat - segment.A.Lat) * FeetPerDegree;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        return new VideoMapSegment(
            Offset(segment.A, dx / length, dy / length, -extraFt, lat0),
            Offset(segment.B, dx / length, dy / length, extraFt, lat0)
        );
    }

    private static LatLon Offset(LatLon point, double east, double north, double feet, double lat0) =>
        new(point.Lat + (north * feet / FeetPerDegree), point.Lon + (east * feet / (FeetPerDegree * Math.Cos(lat0 * Math.PI / 180.0))));

    private static bool CrossesAnyRunway(VideoMapSegment segment, IReadOnlyList<IReadOnlyList<LatLon>> centerlines)
    {
        foreach (IReadOnlyList<LatLon> centerline in centerlines)
        {
            for (int i = 0; i + 1 < centerline.Count; i++)
            {
                if (SegmentsCross(segment.A, segment.B, centerline[i], centerline[i + 1]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SegmentsCross(LatLon a, LatLon b, LatLon p, LatLon q)
    {
        static double Orientation(LatLon o, LatLon u, LatLon v) => ((u.Lon - o.Lon) * (v.Lat - o.Lat)) - ((u.Lat - o.Lat) * (v.Lon - o.Lon));
        return (Math.Sign(Orientation(a, b, p)) != Math.Sign(Orientation(a, b, q)))
            && (Math.Sign(Orientation(p, q, a)) != Math.Sign(Orientation(p, q, b)));
    }

    /// <summary>How far <paramref name="position"/> lies off the segment's infinite line, and how far past its nearer end along it, feet.</summary>
    private static (double OffLineFt, double BeyondEndFt) OffPaint(LatLon position, VideoMapSegment segment)
    {
        (double bx, double by) = Feet(segment.A, segment.B);
        (double px, double py) = Feet(segment.A, position);
        double length = Math.Sqrt((bx * bx) + (by * by));
        double along = ((px * bx) + (py * by)) / length;
        double offLine = Math.Abs((bx * py) - (by * px)) / length;
        double beyond = (along < 0.0) ? -along : Math.Max(0.0, along - length);
        return (offLine, beyond);
    }

    /// <summary>
    /// How far (ft) the bar node lies from its snap point: where the painted segment's line meets the line of one of the
    /// node's own taxiway edges, the nearest such point.
    /// </summary>
    private static (double DistanceFt, LatLon? Point) NearestSnap(AirportGroundLayout layout, GroundNode node, VideoMapSegment segment)
    {
        (double dx, double dy) = Feet(segment.A, segment.B);
        (double ax, double ay) = Feet(node.Position, segment.A);
        double best = double.PositiveInfinity;
        LatLon? bestPoint = null;
        foreach (GroundEdge edge in layout.Edges.Where(edge => !edge.IsRunwayCenterline && edge.HasNode(node.Id)))
        {
            LatLon other = layout.Nodes[edge.OtherNodeId(node.Id)].Position;
            (double ex, double ey) = Feet(node.Position, other);
            double denominator = (ex * dy) - (ey * dx);
            if (Math.Abs(denominator) < 1e-9)
            {
                continue;
            }

            double t = ((ax * dy) - (ay * dx)) / denominator;
            double distanceFt = Math.Abs(t) * Math.Sqrt((ex * ex) + (ey * ey));
            if (distanceFt < best)
            {
                best = distanceFt;
                bestPoint = new LatLon(
                    node.Position.Lat + (t * (other.Lat - node.Position.Lat)),
                    node.Position.Lon + (t * (other.Lon - node.Position.Lon))
                );
            }
        }

        return (best, bestPoint);
    }

    private static (double X, double Y) Feet(LatLon from, LatLon to) =>
        ((to.Lon - from.Lon) * FeetPerDegree * Math.Cos(from.Lat * Math.PI / 180.0), (to.Lat - from.Lat) * FeetPerDegree);

    private static double SignedCrossFt(in RunwayRectangle rect, LatLon position) =>
        GeoMath.SignedCrossTrackDistanceNm(position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading) * GeoMath.FeetPerNm;

    private static double CrossFt(in RunwayRectangle rect, LatLon position) => Math.Abs(SignedCrossFt(rect, position));

    private static RunwayRectangle RectOf(AirportGroundLayout layout, GroundNode bar) =>
        RunwayCrossingDetector.BuildRunwayRectangle(layout.Runways.Single(candidate => candidate.Id.Equals(bar.RunwayId)));

    private static string TaxiwayOf(AirportGroundLayout layout, GroundNode node) =>
        layout.Edges.FirstOrDefault(edge => !edge.IsRunwayCenterline && edge.HasNode(node.Id))?.TaxiwayName ?? "?";

    private static bool IsMinted(GroundNode node) => node.Origin?.StartsWith("RunwayCrossing:hold-short@", StringComparison.Ordinal) == true;

    private static int SegmentOf(GroundNode node) =>
        node.PaintSegmentIndex ?? throw new Xunit.Sdk.XunitException($"node #{node.Id} was not seated on paint");

    private static GroundNode[] SnappedNodes(AirportGroundLayout layout) =>
        [.. layout.Nodes.Values.Where(node => node.PaintSegmentIndex is not null).OrderBy(node => node.Id)];

    private static IEnumerable<GroundNode> HoldShortNodes(AirportGroundLayout layout) =>
        layout.Nodes.Values.Where(node =>
            (node.Type == GroundNodeType.RunwayHoldShort) && layout.Runways.Any(runway => runway.Id.Equals(node.RunwayId))
        );

    private static List<string> ShortBarPositions(AirportGroundLayout layout) =>
        [
            .. HoldShortNodes(layout)
                .Where(node => node.IsShortBar == true)
                .Select(node => $"{node.RunwayId} {node.Position.Lat:F7} {node.Position.Lon:F7}")
                .Order(),
        ];

    internal static TowerCabVideoMap OakMap() => TowerCabVideoMap.Read(File.ReadAllText(OakMapPath));

    internal static AirportGroundLayout ParseOak(TowerCabVideoMap? map, FilletMode filletMode)
    {
        TestVnasData.EnsureInitialized();
        return GeoJsonParser.Parse("oak", File.ReadAllText(OakLayoutPath), "OAK", new LayoutInputs(TestStandLeadIns.For("oak"), map), filletMode);
    }
}
