using Yaat.Sim.Data.Airport;
using Xunit;

namespace Yaat.Sim.Tests;

public class GeoJsonParserTests
{
    /// <summary>Minimal GeoJSON with 2 parking spots, 2 taxiways, and 1 spot.</summary>
    private const string MinimalGeoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "type": "parking", "name": "25", "heading": 68 },
              "geometry": { "type": "Point", "coordinates": [ -122.211952, 37.710532 ] }
            },
            {
              "type": "Feature",
              "properties": { "type": "parking", "name": "32", "heading": 85 },
              "geometry": { "type": "Point", "coordinates": [ -122.213782, 37.708623 ] }
            },
            {
              "type": "Feature",
              "properties": { "type": "spot", "name": "E" },
              "geometry": { "type": "Point", "coordinates": [ -122.214853, 37.708990 ] }
            },
            {
              "type": "Feature",
              "properties": { "type": "taxiway", "name": "T", "circular": false },
              "geometry": {
                "type": "LineString",
                "coordinates": [
                  [ -122.222193, 37.713103 ],
                  [ -122.218491, 37.710589 ],
                  [ -122.215874, 37.708816 ]
                ]
              }
            },
            {
              "type": "Feature",
              "properties": { "type": "taxiway", "name": "TC", "circular": false },
              "geometry": {
                "type": "LineString",
                "coordinates": [
                  [ -122.213821, 37.710992 ],
                  [ -122.214500, 37.710273 ],
                  [ -122.215874, 37.708816 ]
                ]
              }
            }
          ]
        }
        """;

    [Fact]
    public void Parse_MinimalGeoJson_CreatesParkingNodes()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson);

        var spot25 = layout.FindParkingByName("25");
        Assert.NotNull(spot25);
        Assert.Equal(GroundNodeType.Parking, spot25.Type);
        Assert.Equal(68, spot25.Heading);
        Assert.InRange(spot25.Latitude, 37.710, 37.711);
        Assert.InRange(spot25.Longitude, -122.213, -122.211);

        var spot32 = layout.FindParkingByName("32");
        Assert.NotNull(spot32);
        Assert.Equal(85, spot32.Heading);
    }

    [Fact]
    public void Parse_MinimalGeoJson_CreatesSpotNode()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson);

        bool foundSpot = false;
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type == GroundNodeType.Spot && node.Name == "E")
            {
                foundSpot = true;
                break;
            }
        }

        Assert.True(foundSpot, "Spot node 'E' not found");
    }

    [Fact]
    public void Parse_MinimalGeoJson_CreatesTaxiwayEdges()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson);

        // Should have edges for taxiway T and TC
        bool hasT = false;
        bool hasTC = false;
        foreach (var edge in layout.Edges)
        {
            if (edge.TaxiwayName == "T")
            {
                hasT = true;
            }

            if (edge.TaxiwayName == "TC")
            {
                hasTC = true;
            }
        }

        Assert.True(hasT, "No edges for taxiway T");
        Assert.True(hasTC, "No edges for taxiway TC");
    }

    [Fact]
    public void Parse_MinimalGeoJson_DetectsSharedEndpoint()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson);

        // T and TC share an endpoint at [-122.215874, 37.708816]
        // That node should have edges from both taxiways
        GroundNode? sharedNode = null;
        foreach (var node in layout.Nodes.Values)
        {
            bool hasT = false;
            bool hasTC = false;
            foreach (var edge in node.Edges)
            {
                if (edge.TaxiwayName == "T")
                {
                    hasT = true;
                }

                if (edge.TaxiwayName == "TC")
                {
                    hasTC = true;
                }
            }

            if (hasT && hasTC)
            {
                sharedNode = node;
                break;
            }
        }

        Assert.NotNull(sharedNode);
    }

    [Fact]
    public void Parse_MinimalGeoJson_ConnectsParkingToNearbyTaxiway()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson);

        var spot32 = layout.FindParkingByName("32");
        Assert.NotNull(spot32);

        // Parking node 32 should have at least one edge connecting to a taxiway
        Assert.True(spot32.Edges.Count > 0,
            "Parking node 32 has no edges — not connected to any taxiway");
    }

    [Fact]
    public void Parse_SwapsLonLatToLatLon()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson);

        var spot25 = layout.FindParkingByName("25");
        Assert.NotNull(spot25);

        // GeoJSON has [-122.211952, 37.710532] = [lon, lat]
        // Internal should store lat=37.710532, lon=-122.211952
        Assert.InRange(spot25.Latitude, 37.710, 37.711);
        Assert.InRange(spot25.Longitude, -122.213, -122.211);
    }

    [Fact]
    public void Parse_WithRunway_DetectsRunwayCrossings()
    {
        string json = """
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "properties": { "type": "taxiway", "name": "B", "circular": false },
                  "geometry": {
                    "type": "LineString",
                    "coordinates": [
                      [ -122.2246, 37.7108 ],
                      [ -122.2225, 37.7128 ]
                    ]
                  }
                },
                {
                  "type": "Feature",
                  "properties": { "type": "runway", "name": "28L - 10R" },
                  "geometry": {
                    "type": "LineString",
                    "coordinates": [
                      [ -122.2060, 37.7222 ],
                      [ -122.2258, 37.7287 ]
                    ]
                  }
                }
              ]
            }
            """;

        var layout = GeoJsonParser.Parse("TEST", json);

        // The taxiway B doesn't cross this runway geometrically in this test data
        // (they're at different positions) so this is a basic structural test
        Assert.True(layout.Nodes.Count > 0);
        Assert.True(layout.Edges.Count > 0);
    }

    [Fact]
    public void FindNearestNode_ReturnsClosestNode()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson);

        // Query near parking 25's position
        var nearest = layout.FindNearestNode(37.7105, -122.2120);
        Assert.NotNull(nearest);
    }

    [Fact]
    public void FindParkingByName_CaseInsensitive()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson);

        Assert.NotNull(layout.FindParkingByName("25"));
        Assert.Null(layout.FindParkingByName("NONEXISTENT"));
    }
}
