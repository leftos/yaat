using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Fillet;

public class FilletGeneratorInterfaceTests
{
    [Theory]
    [InlineData(FilletMode.None, "none", 0)]
    [InlineData(FilletMode.Legacy, "legacy", 1)]
    public void Factory_Create_ReturnsExpectedId(FilletMode mode, string expectedId, int expectedArcsOnSimpleLayout)
    {
        var generator = FilletGeneratorFactory.Create(mode);
        Assert.Equal(expectedId, generator.Id);

        var layout = BuildSimpleIntersectionLayout();
        var stats = generator.Apply(layout);

        Assert.Equal(expectedArcsOnSimpleLayout, layout.Arcs.Count);
        if (mode == FilletMode.None)
        {
            Assert.Equal(FilletStatistics.Empty, stats);
        }
        else
        {
            Assert.True(stats.ArcsCreated >= 1);
        }
    }

    [Fact]
    public void Registry_All_ContainsImplementedGenerators()
    {
        var ids = FilletArcGeneratorRegistry.All.Select(g => g.Id).ToList();
        Assert.Equal(["none", "legacy", "v2"], ids);
    }

    [Fact]
    public void Factory_V2_AppliesFilletOnSimpleLayout()
    {
        var layout = BuildSimpleIntersectionLayout();
        var stats = FilletGeneratorFactory.Create(FilletMode.V2).Apply(layout);
        Assert.True(stats.ArcsCreated >= 1);
        Assert.Equal(0, stats.OrphansRescued);
        Assert.Equal(0, stats.DirectShortensAdded);
    }

    [Fact]
    public void Router_UseV2_SwitchesCurrentImplementation()
    {
        var previous = FilletArcGeneratorRouter.Current;
        try
        {
            FilletArcGeneratorRouter.UseV2 = true;
            Assert.Equal("v2", FilletArcGeneratorRouter.Current.Id);

            FilletArcGeneratorRouter.UseV2 = false;
            Assert.Equal("legacy", FilletArcGeneratorRouter.Current.Id);
        }
        finally
        {
            FilletArcGeneratorRouter.Current = previous;
        }
    }

    [Fact]
    public void GeoJsonParser_None_SkipsFilletPass()
    {
        var layout = GeoJsonParser.Parse("TEST", MinimalGeoJson(), null, FilletMode.None);
        Assert.Empty(layout.Arcs);
    }

    [Fact]
    public void GeoJsonParser_Legacy_MatchesBoolApplyFilletsTrue()
    {
        var withMode = GeoJsonParser.Parse("TEST", MinimalGeoJson(), null, FilletMode.Legacy);
        var withBool = GeoJsonParser.Parse("TEST", MinimalGeoJson(), null, applyFillets: true);
        Assert.Equal(withBool.Arcs.Count, withMode.Arcs.Count);
        Assert.Equal(withBool.Nodes.Count, withMode.Nodes.Count);
    }

    private static AirportGroundLayout BuildSimpleIntersectionLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var intersection = new GroundNode
        {
            Id = 0,
            Position = new LatLon(0, 0),
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

    private static string MinimalGeoJson() =>
        """
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "properties": { "type": "taxiway", "name": "A" },
                  "geometry": {
                    "type": "LineString",
                    "coordinates": [[-122.0, 37.0], [-122.001, 37.0], [-122.001, 37.001]]
                  }
                }
              ]
            }
            """;
}
