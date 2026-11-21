using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="AirportMapDocument"/> reads a vNAS airport map (one combined GeoJSON FeatureCollection) and writes it back
/// JSON-equal: every feature in source order, its properties and foreign members, and each coordinate as the same double.
/// </summary>
public class AirportMapDocumentRoundTripTests
{
    private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");

    private static readonly string OakPath = Path.Combine(TestDataDir, "oak.geojson");

    private static readonly JsonDocumentOptions LenientOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Every committed airport layout: the top-level <c>TestData/*.geojson</c> files.</summary>
    public static TheoryData<string> CorpusLayouts()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(TestDataDir, "*.geojson").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Fact]
    public void Combined_Oak_ReadThenWrite_IsJsonEqual_CoordinatesExact()
    {
        string original = File.ReadAllText(OakPath);

        var document = AirportMapDocument.Read(original);
        string written = document.Write();

        JsonNode source = JsonNode.Parse(original)!;
        JsonNode output = JsonNode.Parse(written)!;
        Assert.True(JsonNode.DeepEquals(source, output), "oak.geojson: the written map differs from the source");
        JsonArray sourceFeatures = source["features"]!.AsArray();
        JsonArray outputFeatures = output["features"]!.AsArray();
        Assert.Equal(sourceFeatures.Count, document.Features.Count);
        for (int i = 0; i < sourceFeatures.Count; i++)
        {
            double[] expected = Flatten(sourceFeatures[i]!["geometry"]!["coordinates"]!);
            Assert.Equal(expected, FlattenPositions(document.Features[i]));
            Assert.Equal(expected, Flatten(outputFeatures[i]!["geometry"]!["coordinates"]!));
        }
    }

    [Theory]
    [MemberData(nameof(CorpusLayouts))]
    public void Corpus_EveryCommittedLayout_RoundTrips(string fileName)
    {
        string original = File.ReadAllText(Path.Combine(TestDataDir, fileName));

        string written = AirportMapDocument.Read(original).Write();

        var source = JsonNode.Parse(GeoJsonMapReader.SanitizeJson(original), documentOptions: LenientOptions);
        Assert.True(JsonNode.DeepEquals(source, JsonNode.Parse(written)), $"{fileName}: the written map differs from the source");
    }

    [Fact]
    public void ForeignMembersAndUnknownTypeFeature_AreKept()
    {
        JsonNode source = JsonNode.Parse(File.ReadAllText(OakPath))!;
        source["name"] = "oakEdited";
        source["crs"] = new JsonObject
        {
            ["type"] = "name",
            ["properties"] = new JsonObject { ["name"] = "urn:ogc:def:crs:OGC:1.3:CRS84" },
        };
        source["features"]![0]!["id"] = 7;
        JsonNode unknown = source["features"]![1]!.DeepClone();
        unknown["properties"]!["type"] = "unknownthing";
        source["features"]!.AsArray().Add(unknown);
        string original = source.ToJsonString();

        var document = AirportMapDocument.Read(original);
        string written = document.Write();

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(written)), "the written map differs from the source");
        Assert.Equal(["name", "crs"], document.ForeignMembers.Select(m => m.Name));
        Assert.Equal("id", Assert.Single(document.Features[0].ForeignMembers).Name);
        Assert.Equal("unknownthing", document.Features[^1].FeatureType);
    }

    [Fact]
    public void LeadingZeroNumberAndTrailingComma_AreRead()
    {
        string original = File.ReadAllText(OakPath);
        const string Stand = "\"properties\": {\"type\": \"parking\", \"name\": \"25\", \"heading\": 68}";
        Assert.Contains(Stand, original);
        string edited = original.Replace(
            Stand,
            "\"properties\": {\"type\": \"parking\", \"name\": \"25\", \"heading\": 068,}",
            StringComparison.Ordinal
        );

        var document = AirportMapDocument.Read(edited);

        Assert.Equal(68, document.Features[0].CloneProperties()!["heading"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(document.Write())), "the edited map does not read as the source");
    }

    [Fact]
    public void FeatureOrder_IsKept()
    {
        JsonArray sourceFeatures = JsonNode.Parse(File.ReadAllText(OakPath))!["features"]!.AsArray();

        var document = AirportMapDocument.Read(File.ReadAllText(OakPath));
        JsonArray writtenFeatures = JsonNode.Parse(document.Write())!["features"]!.AsArray();

        string[] expected = [.. sourceFeatures.Select(f => Key(f!["properties"]!))];
        Assert.Equal(expected, document.Features.Select(f => Key(f.CloneProperties()!)));
        Assert.Equal(expected, writtenFeatures.Select(f => Key(f!["properties"]!)));
    }

    /// <summary>
    /// FSM is a QGIS export whose coordinates carry 17-18 significant digits: an unedited feature writes them as read, a
    /// feature rebuilt through the public constructor (as every edit is) writes each double's shortest round-trip text.
    /// </summary>
    [Fact]
    public void FeatureRebuiltThroughConstructor_WritesShortestRoundTripDoubles()
    {
        var document = AirportMapDocument.Read(File.ReadAllText(Path.Combine(TestDataDir, "FSM.geojson")));
        MapFeature source = document.Features[0];
        var rebuilt = new MapFeature(source.CloneProperties(), source.GeometryType, source.Coordinates, source.ForeignMembers);
        var edited = new AirportMapDocument(document.Features.SetItem(0, rebuilt), document.ForeignMembers);

        JsonNode sourceCoordinates = JsonNode.Parse(document.Write())!["features"]![0]!["geometry"]!["coordinates"]!;
        JsonNode writtenCoordinates = JsonNode.Parse(edited.Write())!["features"]![0]!["geometry"]!["coordinates"]!;

        string[] sourceTexts = RawNumbers(sourceCoordinates);
        string[] writtenTexts = RawNumbers(writtenCoordinates);
        double[] sourceDoubles = Flatten(sourceCoordinates);
        Assert.Equal(sourceDoubles, FlattenPositions(rebuilt));
        Assert.Equal(sourceDoubles, Flatten(writtenCoordinates));
        Assert.Equal(sourceDoubles.Select(d => d.ToString("R", CultureInfo.InvariantCulture)), writtenTexts);
        Assert.Contains(sourceTexts.Zip(writtenTexts), pair => pair.First != pair.Second);
    }

    private static string[] RawNumbers(JsonNode coordinates) =>
        coordinates is JsonArray array ? [.. array.SelectMany(child => RawNumbers(child!))] : [coordinates.GetValue<JsonElement>().GetRawText()];

    private static string Key(JsonNode properties) => $"{properties["type"]}/{properties["name"]}";

    private static double[] FlattenPositions(MapFeature feature) => [.. feature.Coordinates.SelectMany(p => new[] { p.Lon, p.Lat })];

    private static double[] Flatten(JsonNode coordinates) =>
        coordinates is JsonArray array ? [.. array.SelectMany(child => Flatten(child!))] : [coordinates.GetValue<double>()];
}
