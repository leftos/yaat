using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

public class FilletComparisonGateTests
{
    [Fact]
    public void RepairCountersZero_ApplyOnThreeWay_ReturnsTrue()
    {
        var layout = BuildThreeWayLayout();
        var stats = new FilletArcGenerator().Apply(layout);
        Assert.True(FilletComparisonGates.RepairCountersZero(stats));
    }

    [Fact]
    public void ValidateStructural_Simple90Filleted_IsValid()
    {
        var layout = BuildSimple90Layout();
        new FilletArcGenerator().Apply(layout);
        var result = FilletComparisonGates.ValidateStructural(layout);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void IndexCornerBuckets_90Degree_ProducesBuckets()
    {
        var layout = BuildSimple90Layout();
        new FilletArcGenerator().Apply(layout);

        var buckets = FilletComparisonGates.IndexCornerBuckets(layout);

        Assert.NotEmpty(buckets);
    }

    [Fact]
    public void CompareCornerBuckets_Within10Percent_NoMismatch()
    {
        var expected = new Dictionary<CornerBucketKey, double> { [new CornerBucketKey(1, "A/B", 0, 90)] = 75.0 };
        var actual = new Dictionary<CornerBucketKey, double> { [new CornerBucketKey(1, "A/B", 0, 90)] = 72.0 };
        Assert.Empty(FilletComparisonGates.CompareCornerBuckets(expected, actual));
    }

    [Fact]
    public void CompareCornerBuckets_Beyond10Percent_ReportsMismatch()
    {
        var expected = new Dictionary<CornerBucketKey, double> { [new CornerBucketKey(1, "A/B", 0, 90)] = 75.0 };
        var actual = new Dictionary<CornerBucketKey, double> { [new CornerBucketKey(1, "A/B", 0, 90)] = 50.0 };
        var mismatches = FilletComparisonGates.CompareCornerBuckets(expected, actual);
        Assert.Single(mismatches);
    }

    [Theory]
    [InlineData("oak")]
    [InlineData("sfo")]
    public void Evaluate_RealAirport_StructuralValid(string shortId)
    {
        var preFillet = LoadPreFilletLayout(shortId);
        if (preFillet is null)
        {
            return;
        }

        var layout = LayoutCloner.DeepClone(preFillet);
        var stats = new FilletArcGenerator().Apply(layout);
        var gates = FilletComparisonGates.Evaluate(preFillet, layout, stats);
        Assert.True(gates.Structural.IsValid, string.Join("; ", gates.Structural.Errors.Take(3)));
    }

    private static AirportGroundLayout? LoadPreFilletLayout(string shortId)
    {
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
    }

    private static AirportGroundLayout BuildSimple90Layout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var intersection = new GroundNode
        {
            Id = 0,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = intersection;

        for (int i = 0; i < 2; i++)
        {
            int id = i + 1;
            var node = new GroundNode
            {
                Id = id,
                Position = new LatLon(i == 0 ? 0.01 : 0, i == 0 ? 0 : 0.01),
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[id] = node;
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [intersection, node],
                    TaxiwayName = $"T{id}",
                    DistanceNm = GeoMath.DistanceNm(LatLon.Zero, node.Position),
                }
            );
        }

        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static AirportGroundLayout BuildThreeWayLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var junction = new GroundNode
        {
            Id = 0,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = junction;

        double distNm = 800.0 / GeoMath.FeetPerNm;
        foreach (var (id, bearing) in new[] { (1, 0.0), (2, 100.0), (3, 200.0) })
        {
            var pos = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(bearing), distNm);
            var node = new GroundNode
            {
                Id = id,
                Position = pos,
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[id] = node;
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [junction, node],
                    TaxiwayName = $"T{id}",
                    DistanceNm = distNm,
                }
            );
        }

        layout.RebuildAdjacencyLists();
        return layout;
    }
}
