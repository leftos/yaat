using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using static Yaat.Sim.Data.Airport.AirportGroundLayout;

namespace Yaat.Sim.Tests;

/// <summary>
/// Validates fillet arc generation across the full range of intersection angles (20°–160°).
/// Each test case builds a synthetic runway with a single taxiway branch at the given angle,
/// applies filleting, and verifies structural correctness of the resulting graph.
/// </summary>
public class FilletAngleSweepTests
{
    private const double CenterLat = 37.73;
    private const double CenterLon = -122.22;
    private const double EdgeLenNm = 0.03; // ~182ft per edge

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(140)]
    [InlineData(160)]
    public void FilletAtAngle_ProducesValidArcAndConnectedGraph(double exitAngle)
    {
        var layout = BuildRunwayWithBranch(exitAngle);

        FilletArcGenerator.Apply(layout);

        // 1. At least one arc created (runway-to-taxiway fillet)
        Assert.True(layout.Arcs.Count >= 1, $"Expected ≥1 arc at {exitAngle}°, got {layout.Arcs.Count}");

        // 2. Arc geometry valid
        foreach (var arc in layout.Arcs)
        {
            Assert.True(arc.MinRadiusOfCurvatureFt > 0, $"MinRadiusOfCurvatureFt should be > 0 at {exitAngle}°");
            Assert.True(arc.DistanceNm > 0, $"DistanceNm should be > 0 at {exitAngle}°");
        }

        // 3. Bezier endpoints match arc node positions
        foreach (var arc in layout.Arcs)
        {
            var bezier = arc.ToBezier();
            var (p0Lat, p0Lon) = bezier.Evaluate(0);
            var (p3Lat, p3Lon) = bezier.Evaluate(1);

            double startDist = GeoMath.DistanceNm(new LatLon(p0Lat, p0Lon), arc.Nodes[0].Position);
            double endDist = GeoMath.DistanceNm(new LatLon(p3Lat, p3Lon), arc.Nodes[1].Position);

            Assert.True(startDist < 0.0001, $"Bezier P0 should match Nodes[0] at {exitAngle}°, dist={startDist:E2}nm");
            Assert.True(endDist < 0.0001, $"Bezier P3 should match Nodes[1] at {exitAngle}°, dist={endDist:E2}nm");
        }

        // 4. All remaining nodes have at least one edge (graph connected)
        foreach (var node in layout.Nodes.Values)
        {
            Assert.True(node.Edges.Count > 0, $"Node {node.Id} has no edges at {exitAngle}°");
        }

        // 5. Radius plausible — should be ≤ max configured for runway exit (100ft) or fit constraint
        foreach (var arc in layout.Arcs)
        {
            Assert.True(arc.MinRadiusOfCurvatureFt <= 200, $"MinRadius {arc.MinRadiusOfCurvatureFt:F0}ft seems too large at {exitAngle}°");
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(90)]
    [InlineData(160)]
    public void FilletAtAngle_ArcCurvesInward(double exitAngle)
    {
        var layout = BuildRunwayWithBranch(exitAngle);

        FilletArcGenerator.Apply(layout);

        // The arc midpoint should be closer to the original intersection than the chord midpoint.
        // This verifies the arc curves inward (toward the intersection) not outward.
        foreach (var arc in layout.Arcs)
        {
            var bezier = arc.ToBezier();
            var (midLat, midLon) = bezier.Evaluate(0.5);
            double chordMidLat = (arc.Nodes[0].Position.Lat + arc.Nodes[1].Position.Lat) / 2;
            double chordMidLon = (arc.Nodes[0].Position.Lon + arc.Nodes[1].Position.Lon) / 2;

            double arcMidToCenter = GeoMath.DistanceNm(midLat, midLon, CenterLat, CenterLon);
            double chordMidToCenter = GeoMath.DistanceNm(chordMidLat, chordMidLon, CenterLat, CenterLon);

            Assert.True(
                arcMidToCenter < chordMidToCenter,
                $"Arc midpoint should be closer to intersection than chord midpoint at {exitAngle}°. "
                    + $"Arc={arcMidToCenter:F6}nm, Chord={chordMidToCenter:F6}nm"
            );
        }
    }

    private static AirportGroundLayout BuildRunwayWithBranch(double exitAngle)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        var (lLat, lLon) = GeoMath.ProjectPoint(CenterLat, CenterLon, new TrueHeading(270), EdgeLenNm);
        var (rLat, rLon) = GeoMath.ProjectPoint(CenterLat, CenterLon, new TrueHeading(90), EdgeLenNm);

        var nodeL = new GroundNode
        {
            Id = 0,
            Position = new LatLon(lLat, lLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeC = new GroundNode
        {
            Id = 1,
            Position = new LatLon(CenterLat, CenterLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var nodeR = new GroundNode
        {
            Id = 2,
            Position = new LatLon(rLat, rLon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        layout.Nodes[nodeL.Id] = nodeL;
        layout.Nodes[nodeC.Id] = nodeC;
        layout.Nodes[nodeR.Id] = nodeR;

        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [nodeL, nodeC],
                TaxiwayName = "RWY10/28",
                DistanceNm = GeoMath.DistanceNm(lLat, lLon, CenterLat, CenterLon),
            }
        );
        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [nodeC, nodeR],
                TaxiwayName = "RWY10/28",
                DistanceNm = GeoMath.DistanceNm(CenterLat, CenterLon, rLat, rLon),
            }
        );

        // Branch taxiway at the given angle relative to runway heading (090)
        double branchBearing = 90 + exitAngle;
        var (bLat, bLon) = GeoMath.ProjectPoint(CenterLat, CenterLon, new TrueHeading(branchBearing), EdgeLenNm);
        var nodeB = new GroundNode
        {
            Id = 3,
            Position = new LatLon(bLat, bLon),
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = RunwayIdentifier.Parse("10/28"),
        };
        layout.Nodes[nodeB.Id] = nodeB;

        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [nodeC, nodeB],
                TaxiwayName = $"T{exitAngle:F0}",
                DistanceNm = GeoMath.DistanceNm(CenterLat, CenterLon, bLat, bLon),
            }
        );

        layout.RebuildAdjacencyLists();
        return layout;
    }
}
