using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The sidecar <c>standLeadIns</c> section (YAAT-719): the loader reads and validates each entry, and the layout build connects
/// a listed parking stand to the named taxiway's vertex nearest the entry's point instead of the nearest taxiway edge. An
/// entry the layout cannot honour (no such stand, several, no vertex of the taxiway near the point, or a vertex on the stand)
/// is warned about and the stand keeps the connector it gets without the entry.
/// </summary>
public class StandLeadInTests
{
    private const string ParkingConnector = "GeoJson:parking-connector";

    /// <summary>The shipped SFO entry's point: A's vertex at the A/T6B junction.</summary>
    private static readonly LatLon D12LeadInPoint = new(37.619622, -122.381946);

    private static readonly Lazy<string?> SfoGeoJson = new(() =>
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    });

    [Fact]
    public void Loader_ValidEntry_Parses()
    {
        AirportSidecarLoadResult result = LoadSidecar(
            """{ "airportId": "KSFO", "standLeadIns": [{ "stand": " D12 ", "taxiway": " a ", "point": [-122.381946, 37.619622], "notes": "why" }] }"""
        );

        Assert.Empty(result.Warnings);
        StandLeadIn leadIn = Assert.Single(Assert.Single(result.Airports).StandLeadIns);
        Assert.Equal(new StandLeadIn("D12", "A", D12LeadInPoint, "why"), leadIn);
    }

    /// <summary>An entry missing its stand or taxiway, or whose point is not <c>[lon, lat]</c>, is warned about and skipped.</summary>
    [Fact]
    public void Loader_MalformedEntries_WarnAndSkip()
    {
        AirportSidecarLoadResult result = LoadSidecar(
            """
            { "airportId": "KSFO", "standLeadIns": [
              { "taxiway": "A", "point": [-122.38, 37.61] },
              { "stand": "D12", "point": [-122.38, 37.61] },
              { "stand": "D12", "taxiway": "A", "point": [-122.38] },
              { "stand": "D12", "taxiway": "A" }
            ] }
            """
        );

        Assert.Empty(Assert.Single(result.Airports).StandLeadIns);
        Assert.Equal(4, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Contains("standLeadIns[", w, StringComparison.Ordinal));
    }

    /// <summary>A second entry for the same stand in one file is warned about and wins, keeping one lead-in per stand.</summary>
    [Fact]
    public void Loader_DuplicateStand_WarnsAndLastWins()
    {
        AirportSidecarLoadResult result = LoadSidecar(
            """
            { "airportId": "KSFO", "standLeadIns": [
              { "stand": "D12", "taxiway": "T6B", "point": [-122.38, 37.61] },
              { "stand": "d12", "taxiway": "A", "point": [-122.381946, 37.619622] }
            ] }
            """
        );

        StandLeadIn leadIn = Assert.Single(Assert.Single(result.Airports).StandLeadIns);
        Assert.Equal("A", leadIn.Taxiway);
        Assert.Contains(result.Warnings, w => w.Contains("duplicate stand", StringComparison.Ordinal));
    }

    [Fact]
    public void ShippedSfoSidecar_ConnectsD12ToA()
    {
        StandLeadIn leadIn = Assert.Single(TestStandLeadIns.For("SFO"), l => l.Stand == "D12");

        Assert.Equal("A", leadIn.Taxiway);
        Assert.Equal(D12LeadInPoint, leadIn.Point);
    }

    [Fact]
    public void Layout_ListedStand_ConnectsToTheTaxiwayVertexNearestThePoint()
    {
        if (SfoGeoJson.Value is not { } geoJson)
        {
            return;
        }

        AirportGroundLayout layout = GeoJsonParser.Parse(
            "SFO",
            geoJson,
            null,
            new LayoutInputs([new StandLeadIn("D12", "A", D12LeadInPoint, null)], VideoMap: null),
            FilletMode.None
        );

        GroundNode d12 = Assert.IsType<GroundNode>(layout.FindParkingByName("D12"));
        GroundEdge connector = Assert.Single(layout.Edges, e => (e.Origin == ParkingConnector) && e.Nodes.Contains(d12));
        GroundNode vertex = connector.OtherNode(d12);
        GroundNode nearestOnA = layout
            .Edges.Where(e => !e.IsRamp && (e.TaxiwayName == "A"))
            .SelectMany(e => e.Nodes)
            .MinBy(n => GeoMath.DistanceNm(n.Position, D12LeadInPoint))!;
        Assert.Same(nearestOnA, vertex);
        Assert.Equal("RAMP", connector.TaxiwayName);
    }

    /// <summary>
    /// Each entry the layout cannot honour is warned about, naming the airport and the entry, and every stand keeps the
    /// connector it gets without the entry.
    /// </summary>
    [Theory]
    [InlineData("ZZ99", "A", -122.381946, 37.619622, "names no parking stand")]
    [InlineData("2-2B", "A", -122.381946, 37.619622, "names 2 parking stands")]
    [InlineData("D12", "A", -122.40, 37.60, "no vertex of taxiway A")]
    [InlineData("D12", "QQ", -122.381946, 37.619622, "no vertex of taxiway QQ")]
    public void Layout_EntryItCannotHonour_WarnsAndKeepsTheNearestEdgeConnector(string stand, string taxiway, double lon, double lat, string why)
    {
        if (SfoGeoJson.Value is not { } geoJson)
        {
            return;
        }

        AirportGroundLayout baseline = GeoJsonParser.Parse("SFO", geoJson, null, new LayoutInputs([], VideoMap: null), FilletMode.None);
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = GeoJsonParser.Parse(
            "SFO",
            geoJson,
            null,
            new LayoutInputs([new StandLeadIn(stand, taxiway, new LatLon(lat, lon), null)], VideoMap: null),
            FilletMode.None
        );

        Assert.Equal(ParkingConnectors(baseline), ParkingConnectors(layout));
        Assert.Contains(
            capture.Warnings,
            w =>
                w.Contains("SFO", StringComparison.Ordinal)
                && w.Contains(stand, StringComparison.Ordinal)
                && w.Contains(why, StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A lead-in naming a runway centreline, with its point on a centreline node, never attaches the stand to the runway: no
    /// vertex qualifies, so it is warned about and the stand keeps its own connector.
    /// </summary>
    [Fact]
    public void Layout_LeadInOntoARunwayCentreline_WarnsAndKeepsTheNearestEdgeConnector()
    {
        if (SfoGeoJson.Value is not { } geoJson)
        {
            return;
        }

        AirportGroundLayout baseline = GeoJsonParser.Parse("SFO", geoJson, null, new LayoutInputs([], VideoMap: null), FilletMode.None);
        GroundEdge centreline = baseline.Edges.First(e => e.IsRunwayCenterline);
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = GeoJsonParser.Parse(
            "SFO",
            geoJson,
            null,
            new LayoutInputs([new StandLeadIn("D12", centreline.TaxiwayName, centreline.Nodes[0].Position, null)], VideoMap: null),
            FilletMode.None
        );

        Assert.Equal(ParkingConnectors(baseline), ParkingConnectors(layout));
        Assert.Contains(
            capture.Warnings,
            w => w.Contains("D12", StringComparison.Ordinal) && w.Contains("no vertex of taxiway", StringComparison.Ordinal)
        );
    }

    /// <summary>A lead-in whose vertex sits on the stand would be a zero-length connector: warned about, and the stand keeps its own.</summary>
    [Fact]
    public void Layout_VertexOnTheStand_WarnsAndKeepsTheNearestEdgeConnector()
    {
        const string geoJson = """
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "properties": { "type": "parking", "name": "P1", "heading": 0 },
                  "geometry": { "type": "Point", "coordinates": [ -122.2, 37.7 ] }
                },
                {
                  "type": "Feature",
                  "properties": { "type": "taxiway", "name": "A", "circular": false },
                  "geometry": { "type": "LineString", "coordinates": [ [ -122.2, 37.7 ], [ -122.2, 37.701 ] ] }
                }
              ]
            }
            """;
        AirportGroundLayout baseline = GeoJsonParser.Parse("TST", geoJson, null, new LayoutInputs([], VideoMap: null), FilletMode.None);
        var capture = WarningLogCapture.Install();

        AirportGroundLayout layout = GeoJsonParser.Parse(
            "TST",
            geoJson,
            null,
            new LayoutInputs([new StandLeadIn("P1", "A", new LatLon(37.7, -122.2), null)], VideoMap: null),
            FilletMode.None
        );

        Assert.Equal(ParkingConnectors(baseline), ParkingConnectors(layout));
        Assert.Single(ParkingConnectors(layout));
        Assert.Contains(capture.Warnings, w => w.Contains("P1", StringComparison.Ordinal) && w.Contains("ignored", StringComparison.Ordinal));
    }

    /// <summary>Every parking connector of a layout as (stand node id, taxiway node id), sorted.</summary>
    private static List<(int Stand, int Vertex)> ParkingConnectors(AirportGroundLayout layout) =>
        [
            .. layout
                .Edges.Where(e => e.Origin == ParkingConnector)
                .Select(e => e.Nodes[0].Type == GroundNodeType.Parking ? (e.Nodes[0].Id, e.Nodes[1].Id) : (e.Nodes[1].Id, e.Nodes[0].Id))
                .Order(),
        ];

    private static AirportSidecarLoadResult LoadSidecar(string json)
    {
        string root = Path.Combine(Path.GetTempPath(), "lead-in-" + Guid.NewGuid());
        string airports = Path.Combine(root, "ZOA", "Airports");
        Directory.CreateDirectory(airports);
        try
        {
            File.WriteAllText(Path.Combine(airports, "sfo.json"), json);
            return AirportSidecarLoader.LoadAll(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
