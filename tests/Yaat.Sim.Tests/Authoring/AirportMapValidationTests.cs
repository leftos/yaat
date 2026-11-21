using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="AirportMapValidation"/> on the committed OAK map: the clean map reports nothing, a stand dragged just past the
/// parser's connection range reports the validator's orphan-node warning with that feature's index, and a document the parser
/// cannot read is reported as an issue naming the feature instead of thrown.
/// </summary>
public class AirportMapValidationTests
{
    private static readonly string OakPath = Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson");

    // OAK's first parking feature, stand "25", pinned by index in the committed map.
    private const int Stand25Index = 0;

    public AirportMapValidationTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void CleanOak_ReportsNoIssues()
    {
        AirportMapValidationResult result = AirportMapValidation.Run(Oak(), "OAK", "OAK", TestLayoutInputs.For("OAK", null));

        Assert.Empty(result.Issues);
        Assert.NotNull(result.Layout);
    }

    [Fact]
    public void ParkingMovedOffEveryTaxiway_ReportsOrphanNodeWithItsFeatureIndex()
    {
        AirportMapDocument document = Oak();
        MapPosition stand = document.Features[Stand25Index].Coordinates[0];
        MapPosition away = new(stand.Lon, stand.Lat + (0.2 / 60.0));
        AirportMapDocument edited = MapEdits.MoveVertex(document, Stand25Index, 0, away);

        AirportMapValidationResult result = AirportMapValidation.Run(edited, "OAK", "OAK", TestLayoutInputs.For("OAK", null));

        AirportMapIssue orphan = Assert.Single(result.Issues, issue => issue.Code == "orphan-node");
        Assert.Equal(Stand25Index, orphan.FeatureIndex);
        Assert.Contains("GeoJson:parking", orphan.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Layout);
    }

    [Fact]
    public void NonObjectGeometryFeature_IsReportedAsAnIssueInsteadOfThrown()
    {
        var document = AirportMapDocument.Read(StringGeometryMap);
        Assert.Null(document.Features[0].GeometryType);

        AirportMapValidationResult result = AirportMapValidation.Run(document, "OAK", "OAK", TestLayoutInputs.For("OAK", null));

        AirportMapIssue issue = Assert.Single(result.Issues);
        Assert.Equal("malformed-feature", issue.Code);
        Assert.Equal(0, issue.FeatureIndex);
        Assert.NotNull(result.Layout);
    }

    [Fact]
    public void FeatureWithoutGeometry_IsReportedAsAParseFailureNamingItsFeature()
    {
        var document = AirportMapDocument.Read(FeatureWithoutGeometryMap);
        Assert.Null(document.Features[0].GeometryType);

        AirportMapValidationResult result = AirportMapValidation.Run(document, "OAK", "OAK", TestLayoutInputs.For("OAK", null));

        AirportMapIssue issue = Assert.Single(result.Issues);
        Assert.Equal("parse-failure", issue.Code);
        Assert.Equal(0, issue.FeatureIndex);
        Assert.Contains("geometry", issue.Message, StringComparison.Ordinal);
        Assert.Null(result.Layout);
    }

    [Theory]
    [MemberData(nameof(MapsWithoutPropertiesObject))]
    public void FeatureWithoutPropertiesObject_IsReportedAsAParseFailureNamingItsFeature(string map)
    {
        var document = AirportMapDocument.Read(map);

        AirportMapValidationResult result = AirportMapValidation.Run(document, "OAK", "OAK", TestLayoutInputs.For("OAK", null));

        AirportMapIssue issue = Assert.Single(result.Issues);
        Assert.Equal("parse-failure", issue.Code);
        Assert.Equal(0, issue.FeatureIndex);
        Assert.Contains("properties", issue.Message, StringComparison.Ordinal);
        Assert.Null(result.Layout);
    }

    public static TheoryData<string> MapsWithoutPropertiesObject()
    {
        var data = new TheoryData<string> { NoPropertiesMap, StringPropertiesMap };
        return data;
    }

    [Fact]
    public void NonIntegerStandHeading_IsReportedAsAParseFailureInsteadOfThrown()
    {
        AirportMapDocument document = MapEdits.SetProperty(Oak(), Stand25Index, "heading", JsonValue.Create(90.5));

        AirportMapValidationResult result = AirportMapValidation.Run(document, "OAK", "OAK", TestLayoutInputs.For("OAK", null));

        AirportMapIssue issue = Assert.Single(result.Issues);
        Assert.Equal("parse-failure", issue.Code);
        Assert.Contains("the airport map could not be parsed", issue.Message, StringComparison.Ordinal);
        Assert.Null(result.Layout);
    }

    [Fact]
    public void RunwayNameTooLong_IsReportedAsAParseFailureInsteadOfThrown()
    {
        AirportMapDocument oak = Oak();
        MapPosition threshold = oak.Features[Stand25Index].Coordinates[0];
        MapPosition far = new(threshold.Lon, threshold.Lat + 0.01);
        MapFeature runway = MapEdits.NewFeature(oak, "runway", "12345678901234567890", [threshold, far]);
        AirportMapDocument document = MapEdits.AddFeature(oak, runway, null);

        AirportMapValidationResult result = AirportMapValidation.Run(document, "OAK", "OAK", TestLayoutInputs.For("OAK", null));

        AirportMapIssue issue = Assert.Single(result.Issues);
        Assert.Equal("parse-failure", issue.Code);
        Assert.Contains("the airport map could not be parsed", issue.Message, StringComparison.Ordinal);
        Assert.Null(result.Layout);
    }

    private const string StringGeometryMap = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "type": "parking", "name": "X" },
              "geometry": "Point"
            }
          ]
        }
        """;

    private const string FeatureWithoutGeometryMap = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "type": "parking", "name": "X" }
            }
          ]
        }
        """;

    private const string NoPropertiesMap = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "geometry": { "type": "Point", "coordinates": [1.0, 2.0] }
            }
          ]
        }
        """;

    private const string StringPropertiesMap = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": "x",
              "geometry": { "type": "Point", "coordinates": [1.0, 2.0] }
            }
          ]
        }
        """;

    private static AirportMapDocument Oak() => AirportMapDocument.Read(File.ReadAllText(OakPath));
}
