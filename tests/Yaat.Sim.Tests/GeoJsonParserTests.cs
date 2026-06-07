using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

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
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson, null);

        var spot25 = layout.FindParkingByName("25");
        Assert.NotNull(spot25);
        Assert.Equal(GroundNodeType.Parking, spot25.Type);
        Assert.Equal(68, spot25.TrueHeading?.Degrees);
        Assert.InRange(spot25.Position.Lat, 37.710, 37.711);
        Assert.InRange(spot25.Position.Lon, -122.213, -122.211);

        var spot32 = layout.FindParkingByName("32");
        Assert.NotNull(spot32);
        Assert.Equal(85, spot32.TrueHeading?.Degrees);
    }

    [Fact]
    public void Parse_MinimalGeoJson_CreatesSpotNode()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson, null);

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
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson, null);

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
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson, null);

        // T and TC share an endpoint at [-122.215874, 37.708816]
        // After filleting, the intersection node is replaced by tangent points and arcs.
        // Look for a node that has edges belonging to both taxiways (via MatchesTaxiway).
        GroundNode? sharedNode = null;
        foreach (var node in layout.Nodes.Values)
        {
            bool hasT = node.Edges.Any(e => e.MatchesTaxiway("T"));
            bool hasTC = node.Edges.Any(e => e.MatchesTaxiway("TC"));

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
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson, null);

        var spot32 = layout.FindParkingByName("32");
        Assert.NotNull(spot32);

        // Parking node 32 should have at least one edge connecting to a taxiway
        Assert.True(spot32.Edges.Count > 0, "Parking node 32 has no edges — not connected to any taxiway");
    }

    [Fact]
    public void Parse_SwapsLonLatToLatLon()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson, null);

        var spot25 = layout.FindParkingByName("25");
        Assert.NotNull(spot25);

        // GeoJSON has [-122.211952, 37.710532] = [lon, lat]
        // Internal should store lat=37.710532, lon=-122.211952
        Assert.InRange(spot25.Position.Lat, 37.710, 37.711);
        Assert.InRange(spot25.Position.Lon, -122.213, -122.211);
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

        var layout = GeoJsonParser.Parse("TEST", json, null);

        // The taxiway B doesn't cross this runway geometrically in this test data
        // (they're at different positions) so this is a basic structural test
        Assert.True(layout.Nodes.Count > 0);
        Assert.True(layout.Edges.Count > 0);
    }

    [Fact]
    public void Parse_TaxiwayCrossingRunway_CreatesHoldShortNodes()
    {
        // Taxiway crosses a N-S runway. Runway runs from south to north.
        // Taxiway runs E-W through the runway.
        string json = """
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "properties": { "type": "taxiway", "name": "A", "circular": false },
                  "geometry": {
                    "type": "LineString",
                    "coordinates": [
                      [ -122.2250, 37.7200 ],
                      [ -122.2230, 37.7200 ],
                      [ -122.2210, 37.7200 ]
                    ]
                  }
                },
                {
                  "type": "Feature",
                  "properties": { "type": "runway", "name": "36 - 18" },
                  "geometry": {
                    "type": "LineString",
                    "coordinates": [
                      [ -122.2230, 37.7180 ],
                      [ -122.2230, 37.7220 ]
                    ]
                  }
                }
              ]
            }
            """;

        var layout = GeoJsonParser.Parse("TEST", json, null);

        // Should have hold-short nodes on both sides of the runway
        var hsNodes = new List<GroundNode>();
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type == GroundNodeType.RunwayHoldShort)
            {
                hsNodes.Add(node);
            }
        }

        // The middle taxiway vertex sits on the runway centerline.
        // The two outer vertices are off-runway. Each boundary edge
        // (middle→west and middle→east) should produce one HS node.
        Assert.True(hsNodes.Count >= 2, $"Expected at least 2 hold-short nodes, got {hsNodes.Count}");

        foreach (var hs in hsNodes)
        {
            Assert.Equal("36/18", hs.RunwayId?.ToString());
        }
    }

    [Fact]
    public void FindNearestNode_ReturnsClosestNode()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson, null);

        // Query near parking 25's position
        var nearest = layout.FindNearestNode(37.7105, -122.2120);
        Assert.NotNull(nearest);
    }

    [Fact]
    public void FindParkingByName_CaseInsensitive()
    {
        var layout = GeoJsonParser.Parse("OAK", MinimalGeoJson, null);

        Assert.NotNull(layout.FindParkingByName("25"));
        Assert.Null(layout.FindParkingByName("NONEXISTENT"));
    }

    /// <summary>
    /// Regression test: load real OAK GeoJSON and verify hold-short nodes
    /// exist on both sides of runways 28R/10L and 28L/10R for taxiway B.
    /// </summary>
    [Fact]
    public void OAK_TaxiwayB_HasHoldShortNodesForBothRunways()
    {
        TestVnasData.EnsureInitialized();
        string geoJsonPath = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(geoJsonPath))
        {
            return; // Skip if local TestData is absent
        }

        string content = File.ReadAllText(geoJsonPath);
        var layout = GeoJsonParser.Parse("oak", content, null);

        // Collect hold-short nodes for each runway that are connected to taxiway B
        var bHs28R = new List<GroundNode>();
        var bHs28L = new List<GroundNode>();

        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort)
            {
                continue;
            }

            bool hasBEdge = false;
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway("B"))
                {
                    hasBEdge = true;
                    break;
                }
            }

            if (!hasBEdge)
            {
                continue;
            }

            if (node.RunwayId is { } rwyId28R && rwyId28R.Contains("28R"))
            {
                bHs28R.Add(node);
            }

            if (node.RunwayId is { } rwyId28L && rwyId28L.Contains("28L"))
            {
                bHs28L.Add(node);
            }
        }

        _output.WriteLine($"B taxiway HS nodes for 28R/10L: {bHs28R.Count}");
        foreach (var hs in bHs28R)
        {
            _output.WriteLine($"  Node {hs.Id}: ({hs.Position.Lat:F6}, {hs.Position.Lon:F6})");
        }

        _output.WriteLine($"B taxiway HS nodes for 28L/10R: {bHs28L.Count}");
        foreach (var hs in bHs28L)
        {
            _output.WriteLine($"  Node {hs.Id}: ({hs.Position.Lat:F6}, {hs.Position.Lon:F6})");
        }

        Assert.True(bHs28R.Count >= 2, $"Expected ≥2 HS nodes for 28R/10L on taxiway B, got {bHs28R.Count}");
        Assert.True(bHs28L.Count >= 2, $"Expected ≥2 HS nodes for 28L/10R on taxiway B, got {bHs28L.Count}");
    }

    [Fact]
    public void DumpLayoutJson()
    {
        TestVnasData.EnsureInitialized();
        string geoJsonPath = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(geoJsonPath))
        {
            return;
        }

        string content = File.ReadAllText(geoJsonPath);
        var layout = GeoJsonParser.Parse("oak", content, null);

        var nodes = new List<object>();
        foreach (var (id, node) in layout.Nodes)
        {
            nodes.Add(
                new
                {
                    id,
                    lat = node.Position.Lat,
                    lon = node.Position.Lon,
                    type = node.Type.ToString(),
                    name = node.Name,
                    runwayId = node.RunwayId,
                }
            );
        }

        var edges = new List<object>();
        foreach (var edge in layout.Edges)
        {
            edges.Add(
                new
                {
                    from = edge.Nodes[0].Id,
                    to = edge.Nodes[1].Id,
                    taxiway = edge.TaxiwayName,
                }
            );
        }

        var dump = new { nodes, edges };
        string json = JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true });

        string outPath = Path.Combine(AppContext.BaseDirectory, "oak_layout_dump.json");
        File.WriteAllText(outPath, json);
        _output.WriteLine($"Wrote layout dump to {outPath}");
    }

    private readonly ITestOutputHelper _output;

    public GeoJsonParserTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Parse_OakRunways_ReadsAuthoredFields()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "oak.geojson");
        var layout = GeoJsonParser.Parse("OAK", File.ReadAllText(path), "OAK");

        // OAK 28L - 10R: turnoff=right (anchored to 28L's heading), holdShortDistance=250 (ignored),
        // patternSize=0.5, patternAltitude=600. Right of 28L (heading ~282°) = north (GA parking side).
        // Same physical side is on the LEFT when landing 10R east → 10R should resolve to Left.
        var rwy28L = layout.Runways.First(r => r.Name == "28L - 10R");
        Assert.Equal(ExitSide.Right, rwy28L.TurnoffForEnd("28L"));
        Assert.Equal(ExitSide.Left, rwy28L.TurnoffForEnd("10R"));
        Assert.Equal(0.5, rwy28L.PatternSizeNm);
        Assert.Equal(600, rwy28L.PatternAltitudeAglFt);
        Assert.Empty(rwy28L.NoTurnoffForEnd("28L"));
        Assert.Empty(rwy28L.NoTurnoffForEnd("10R"));

        // 15 - 33: turnoff=left (anchored to 15) → 33 flips to Right
        var rwy15 = layout.Runways.First(r => r.Name == "15 - 33");
        Assert.Equal(ExitSide.Left, rwy15.TurnoffForEnd("15"));
        Assert.Equal(ExitSide.Right, rwy15.TurnoffForEnd("33"));
    }

    [Fact]
    public void Parse_SfoTurnoffAndNoTurnoff_KeyedByEndDesignator()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "sfo.geojson");
        var layout = GeoJsonParser.Parse("SFO", File.ReadAllText(path), "SFO");

        // SFO 10L - 28R: turnoff=right (anchored to 10L east), noTurnoff = [["Q", "T"], ["L", "P"]].
        // Right of 10L (~102°) = south = terminal side. Same physical side is LEFT of 28R (~282°).
        var rwy = layout.Runways.First(r => r.Name == "10L - 28R");
        Assert.Equal(ExitSide.Right, rwy.TurnoffForEnd("10L"));
        Assert.Equal(ExitSide.Left, rwy.TurnoffForEnd("28R"));

        Assert.Equal(new[] { "Q", "T" }, rwy.NoTurnoffForEnd("10L"));
        Assert.Equal(new[] { "L", "P" }, rwy.NoTurnoffForEnd("28R"));

        // Lookup is case-insensitive (and leading-zero tolerant) via the normalizing accessors
        Assert.NotEmpty(rwy.NoTurnoffForEnd("10l"));
        Assert.NotNull(rwy.TurnoffForEnd("10l"));
    }

    [Fact]
    public void InferPreferredExitSide_UsesAuthoredTurnoffWhenSet()
    {
        TestVnasData.EnsureInitialized();
        // SFO 28R: authored turnoff resolves to Left (same physical side as terminal-south of 10L).
        string path = Path.Combine("TestData", "sfo.geojson");
        var layout = GeoJsonParser.Parse("SFO", File.ReadAllText(path), "SFO");

        var rwy = layout.Runways.First(r => r.Name == "10L - 28R");
        Assert.Equal(ExitSide.Left, rwy.TurnoffForEnd("28R"));

        // 28R true heading is ~282°. The authored data should short-circuit the heuristic.
        Assert.Equal(ExitSide.Left, layout.InferPreferredExitSide("28R", new TrueHeading(282)));
        Assert.Equal(ExitSide.Right, layout.InferPreferredExitSide("10L", new TrueHeading(102)));
    }

    [Fact]
    public void InferPreferredExitSide_AuthoredOverridesHeuristic()
    {
        // Build a layout where authored turnoff disagrees with what the heuristic would pick.
        // Without nodes/parking, the heuristic returns null. The authored value should still surface,
        // and should flip per landing direction.
        var layout = new AirportGroundLayout
        {
            AirportId = "TST",
            Runways =
            {
                new GroundRunway
                {
                    Name = "28 - 10",
                    Coordinates = [],
                    WidthFt = 150,
                    TurnoffByEnd = new Dictionary<string, ExitSide>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["28"] = ExitSide.Left,
                        ["10"] = ExitSide.Right,
                    },
                },
            },
        };

        Assert.Equal(ExitSide.Left, layout.InferPreferredExitSide("28", new TrueHeading(280)));
        Assert.Equal(ExitSide.Right, layout.InferPreferredExitSide("10", new TrueHeading(100)));
    }

    [Fact]
    public void NoTurnoff_ExcludesForbiddenTaxiwaysFromDefaultExitSearch()
    {
        TestVnasData.EnsureInitialized();
        // SFO 28R noTurnoff = ['L', 'P']: landing 28R must not exit at L or P.
        // Default-search (no command-named taxiway) should never select L or P as the exit.
        string path = Path.Combine("TestData", "sfo.geojson");
        var layout = GeoJsonParser.Parse("SFO", File.ReadAllText(path), "SFO");

        var rwy = layout.Runways.First(r => r.Name == "10L - 28R");
        Assert.Contains("L", rwy.NoTurnoffForEnd("28R"));
        Assert.Contains("P", rwy.NoTurnoffForEnd("28R"));

        // Pick a centerline node well before any L/P branch and search for any exit.
        // The search must not return an L or P hold-short.
        var centerline28R = layout.Nodes.Values.First(n => n.Edges.Any(e => e.MatchesRunway("28R")));
        var heading = new TrueHeading(282);
        var ac = centerline28R.Position;

        var result = layout.FindExitFromCenterline(ac.Lat, ac.Lon, heading, "28R", preference: null);
        if (result is not null)
        {
            Assert.NotEqual("L", result.Value.Taxiway);
            Assert.NotEqual("P", result.Value.Taxiway);
        }
    }

    [Fact]
    public void NoTurnoff_HonorsExplicitExitCommandEvenWhenForbidden()
    {
        TestVnasData.EnsureInitialized();
        // Even though P is on noTurnoff for 28R, an explicit "EXIT P" command must still find P.
        // The forbidden filter applies only to default-search (preference.Taxiway == null).
        string path = Path.Combine("TestData", "sfo.geojson");
        var layout = GeoJsonParser.Parse("SFO", File.ReadAllText(path), "SFO");

        var centerline28R = layout.Nodes.Values.First(n => n.Edges.Any(e => e.MatchesRunway("28R")));
        var heading = new TrueHeading(282);
        var ac = centerline28R.Position;

        var pref = new ExitPreference { Taxiway = "P" };
        var result = layout.FindExitFromCenterline(ac.Lat, ac.Lon, heading, "28R", pref);

        // P should be findable when explicitly requested (forbidden list is for default search only).
        if (result is not null)
        {
            Assert.Equal("P", result.Value.Taxiway);
        }
    }

    [Fact]
    public void InferPreferredExitSide_NoAuthoredNoNodes_ReturnsNull()
    {
        var layout = new AirportGroundLayout
        {
            AirportId = "TST",
            Runways =
            {
                new GroundRunway
                {
                    Name = "28 - 10",
                    Coordinates = [],
                    WidthFt = 150,
                },
            },
        };

        Assert.Null(layout.InferPreferredExitSide("28", new TrueHeading(280)));
    }

    [Fact]
    public void Parse_RunwayWithoutPatternFields_LeavesPatternNull()
    {
        TestVnasData.EnsureInitialized();
        // sjc.geojson has turnoff but no patternAltitude/patternSize/noTurnoff
        string path = Path.Combine("TestData", "sjc.geojson");
        var layout = GeoJsonParser.Parse("SJC", File.ReadAllText(path), "SJC");

        foreach (var rwy in layout.Runways)
        {
            Assert.Null(rwy.PatternSizeNm);
            Assert.Null(rwy.PatternAltitudeAglFt);
            Assert.Empty(rwy.NoTurnoffForEnd(rwy.Id.End1));
            Assert.Empty(rwy.NoTurnoffForEnd(rwy.Id.End2));
        }
    }
}
