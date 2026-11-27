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

        MapFeature stand = Assert.Single(document.Features, f => IsStand(f, "25"));
        Assert.Equal(68, stand.CloneProperties()!["heading"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(document.Write())), "the edited map does not read as the source");
    }

    /// <summary>
    /// The leading-zero cleanup the reader runs before parsing must touch number literals only: a string or a comment is
    /// passed through byte for byte, so a timestamp or free text holding <c>: 0N</c> survives a read and a save.
    /// </summary>
    /// <param name="input">The map text.</param>
    /// <param name="expected">The text the reader must parse.</param>
    [Theory]
    [InlineData("\"a\": 01", "\"a\": 1")]
    [InlineData("[01, 007]", "[1, 7]")]
    [InlineData("\"a\": 0.5", "\"a\": 0.5")]
    [InlineData("\"a\": 0", "\"a\": 0")]
    [InlineData("\"ts\": \"2026-07-15T15:22:01Z\"", "\"ts\": \"2026-07-15T15:22:01Z\"")]
    [InlineData("\"x\": \"a\\\": 01\"", "\"x\": \"a\\\": 01\"")]
    [InlineData("// \"unbalanced quote\n\"a\": 01", "// \"unbalanced quote\n\"a\": 1")]
    [InlineData("/* , 01 */", "/* , 01 */")]
    public void SanitizeJson_LeavesStringsAndCommentsAlone(string input, string expected) =>
        Assert.Equal(expected, GeoJsonMapReader.SanitizeJson(input));

    [Fact]
    public void Roc_OsmDataTimestamp_SurvivesRoundTrip()
    {
        string original = File.ReadAllText(Path.Combine(TestDataDir, "ROC.geojson"));

        JsonNode written = JsonNode.Parse(AirportMapDocument.Read(original).Write())!;

        Assert.Equal("2026-07-15T15:22:01Z", written["metadata"]!["osmDataTimestamp"]!.GetValue<string>());
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

    /// <summary>
    /// The ATCTrainer-style map a PascalCase <c>Type</c> key comes from: the reader classifies it through a
    /// case-insensitive lookup, so <see cref="MapFeature.FeatureType"/> reads it the same way.
    /// </summary>
    [Fact]
    public void FeatureType_ReadsCapitalisedTypeKey()
    {
        JsonNode feature = OakParkingPointFeature();
        var renamed = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> member in feature["properties"]!.AsObject())
        {
            renamed[(member.Key == "type") ? "Type" : member.Key] = member.Value?.DeepClone();
        }

        feature["properties"] = renamed;
        string original = new JsonObject { ["type"] = "FeatureCollection", ["features"] = new JsonArray(feature) }.ToJsonString();

        var document = AirportMapDocument.Read(original);

        Assert.Equal("parking", document.Features[0].FeatureType);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(document.Write())), "the written map differs from the source");
    }

    [Fact]
    public void Read_MalformedJson_ThrowsJsonException() =>
        Assert.ThrowsAny<JsonException>(() => AirportMapDocument.Read("{\"type\": \"FeatureCollection\", \"features\": ["));

    [Fact]
    public void Read_NotAFeatureCollection_ThrowsJsonException() =>
        Assert.Throws<JsonException>(() => AirportMapDocument.Read("{\"type\": \"Feature\", \"features\": []}"));

    [Fact]
    public void Read_FeatureThatIsNotAFeature_NamesItsIndex()
    {
        JsonException thrown = Assert.Throws<JsonException>(() =>
            AirportMapDocument.Read("{\"type\": \"FeatureCollection\", \"features\": [{\"type\": \"Point\"}]}")
        );

        Assert.Contains("feature 0", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AFeatureRepeatingAPropertyKey_ThrowsJsonException()
    {
        const string RepeatsUnderProperties = """
            {"type": "FeatureCollection", "features": [
              {"type": "Feature", "properties": {"type": "parking", "name": "A1", "name": "A2"},
               "geometry": {"type": "Point", "coordinates": [-122.2, 37.7]}}
            ]}
            """;
        const string RepeatsOnTheFeature = """
            {"type": "FeatureCollection", "features": [
              {"type": "Feature", "type": "Feature", "properties": {"type": "parking", "name": "A1"},
               "geometry": {"type": "Point", "coordinates": [-122.2, 37.7]}}
            ]}
            """;

        const string RepeatsInTheSecondFeature = """
            {"type": "FeatureCollection", "features": [
              {"type": "Feature", "properties": {"type": "parking", "name": "A1"},
               "geometry": {"type": "Point", "coordinates": [-122.2, 37.7]}},
              {"type": "Feature", "properties": {"type": "parking", "name": "B1", "name": "B2"},
               "geometry": {"type": "Point", "coordinates": [-122.2, 37.7]}}
            ]}
            """;

        JsonException underProperties = Assert.Throws<JsonException>(() => AirportMapDocument.Read(RepeatsUnderProperties));
        Assert.Contains("'name'", underProperties.Message, StringComparison.Ordinal);
        JsonException onTheFeature = Assert.Throws<JsonException>(() => AirportMapDocument.Read(RepeatsOnTheFeature));
        Assert.Contains("'type'", onTheFeature.Message, StringComparison.Ordinal);
        JsonException inTheSecondFeature = Assert.Throws<JsonException>(() => AirportMapDocument.Read(RepeatsInTheSecondFeature));
        Assert.Contains("'name'", inTheSecondFeature.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapFeature_InvalidGeometry_ThrowsArgumentException()
    {
        var properties = new JsonObject { ["type"] = "parking" };

        Assert.Throws<ArgumentException>(() => new MapFeature(properties, "Point", [new MapPosition(0, 0), new MapPosition(1, 1)], []));
        Assert.Throws<ArgumentException>(() => new MapFeature(properties, "Polygon", [], []));
    }

    [Fact]
    public void MapFeature_LineStringWithFewerThanTwoPositions_ThrowsArgumentException()
    {
        var properties = new JsonObject { ["type"] = "taxiway" };

        ArgumentException none = Assert.Throws<ArgumentException>(() => new MapFeature(properties, "LineString", [], []));
        ArgumentException one = Assert.Throws<ArgumentException>(() => new MapFeature(properties, "LineString", [new MapPosition(0, 0)], []));

        Assert.Contains("cannot hold 0 positions", none.Message, StringComparison.Ordinal);
        Assert.Contains("cannot hold 1 positions", one.Message, StringComparison.Ordinal);
        Assert.Contains("at least 2", none.Message, StringComparison.Ordinal);
        Assert.Contains("at least 2", one.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapReader_OneVertexLineString_StillReadsAndRoundTrips()
    {
        const string Original = """
            {"type": "FeatureCollection", "features": [
              {"type": "Feature", "properties": {"type": "taxiway", "name": "DEG"},
               "geometry": {"type": "LineString", "coordinates": [[-122.2, 37.7]]}}
            ]}
            """;

        var document = AirportMapDocument.Read(Original);

        MapFeature feature = Assert.Single(document.Features);
        Assert.Equal("LineString", feature.GeometryType);
        Assert.Equal(new MapPosition(-122.2, 37.7), Assert.Single(feature.Coordinates));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Original), JsonNode.Parse(document.Write())), "the written map differs from the source");
    }

    [Fact]
    public void MapFeature_NonFiniteCoordinate_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapFeature(null, "Point", [new MapPosition(double.NaN, 0)], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapFeature(null, "Point", [new MapPosition(0, double.PositiveInfinity)], []));
    }

    /// <summary>
    /// A feature whose geometry is not a two-number Point or LineString keeps <c>bbox</c>, its 3D position and a JSON
    /// <c>null</c> <c>properties</c> verbatim: what the editor cannot type is written back exactly as read.
    /// </summary>
    [Fact]
    public void FeatureWithBboxThreeDimensionalPositionAndNullProperties_IsKeptVerbatim()
    {
        JsonNode feature = OakParkingPointFeature();
        feature["bbox"] = new JsonArray(-122.3, 37.7, -122.2, 37.8);
        feature["geometry"]!["coordinates"] = new JsonArray(-122.21195241132057, 37.71053202339726, 0.0);
        feature["properties"] = null;
        string original = new JsonObject { ["type"] = "FeatureCollection", ["features"] = new JsonArray(feature) }.ToJsonString();

        var document = AirportMapDocument.Read(original);
        string written = document.Write();

        Assert.Null(document.Features[0].GeometryType);
        Assert.Null(document.Features[0].FeatureType);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(written)), "the written map differs from the source");
    }

    // Oak's first parking feature drawn as a point: picked by its properties and geometry, never by position in the file.
    private static JsonNode OakParkingPointFeature()
    {
        JsonArray features = JsonNode.Parse(File.ReadAllText(OakPath))!["features"]!.AsArray();
        JsonNode feature = features.First(f =>
            (f!["properties"]?["type"]?.GetValue<string>() == "parking") && (f["geometry"]?["type"]?.GetValue<string>() == "Point")
        )!;

        return feature.DeepClone();
    }

    private static bool IsStand(MapFeature feature, string name)
    {
        JsonNode? node = feature.CloneProperties()?["name"];
        return (feature.FeatureType == "parking") && (node is JsonValue value) && (value.TryGetValue(out string? text)) && (text == name);
    }

    private static string[] RawNumbers(JsonNode coordinates) =>
        coordinates is JsonArray array ? [.. array.SelectMany(child => RawNumbers(child!))] : [coordinates.GetValue<JsonElement>().GetRawText()];

    private static string Key(JsonNode properties) => $"{properties["type"]}/{properties["name"]}";

    private static double[] FlattenPositions(MapFeature feature) => [.. feature.Coordinates.SelectMany(p => new[] { p.Lon, p.Lat })];

    private static double[] Flatten(JsonNode coordinates) =>
        coordinates is JsonArray array ? [.. array.SelectMany(child => Flatten(child!))] : [coordinates.GetValue<double>()];
}
