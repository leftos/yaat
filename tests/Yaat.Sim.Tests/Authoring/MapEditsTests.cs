using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="MapEdits"/> on the committed OAK map: every operation returns a new document that shares the features it does
/// not change, leaves the original alone, throws on an invalid target, and undo and redo through a mixed sequence hand back
/// each earlier document.
/// </summary>
public class MapEditsTests
{
    private static readonly string OakPath = Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson");

    // OAK features pinned by index in the committed map.
    private const int Stand25Index = 0;

    private const int RunwayIndex = 168;

    private const int TaxiwayWIndex = 188;

    private const int TwoVertexTaxiwayIndex = 207;

    [Fact]
    public void MoveVertex_ReturnsANewDocumentAndLeavesTheOriginalAlone()
    {
        AirportMapDocument document = Oak();
        MapPosition from = document.Features[Stand25Index].Coordinates[0];
        MapPosition to = new(from.Lon + 0.0001, from.Lat);

        AirportMapDocument moved = MapEdits.MoveVertex(document, Stand25Index, 0, to);

        Assert.NotSame(document, moved);
        Assert.Equal(from, document.Features[Stand25Index].Coordinates[0]);
        Assert.Equal(to, moved.Features[Stand25Index].Coordinates[0]);
        Assert.NotSame(document.Features[Stand25Index], moved.Features[Stand25Index]);
        Assert.Same(document.Features[1], moved.Features[1]);
        Assert.Equal(document.Features.Count, moved.Features.Count);
    }

    [Fact]
    public void InsertVertex_InsertsBeforeTheGivenIndex()
    {
        AirportMapDocument document = Oak();
        MapFeature line = document.Features[TaxiwayWIndex];
        MapPosition at = new(line.Coordinates[3].Lon + 0.0001, line.Coordinates[3].Lat);

        AirportMapDocument inserted = MapEdits.InsertVertex(document, TaxiwayWIndex, 3, at);

        MapFeature after = inserted.Features[TaxiwayWIndex];
        Assert.Equal(line.Coordinates.Length + 1, after.Coordinates.Length);
        Assert.Equal(line.Coordinates.Take(3), after.Coordinates.Take(3));
        Assert.Equal(at, after.Coordinates[3]);
        Assert.Equal(line.Coordinates.Skip(3), after.Coordinates.Skip(4));
        Assert.Equal(line.Coordinates.Length, document.Features[TaxiwayWIndex].Coordinates.Length);

        AirportMapDocument appended = MapEdits.InsertVertex(document, TaxiwayWIndex, line.Coordinates.Length, at);
        Assert.Equal(at, appended.Features[TaxiwayWIndex].Coordinates[^1]);
    }

    [Fact]
    public void DeleteVertex_RemovesTheVertex()
    {
        AirportMapDocument document = Oak();
        MapFeature line = document.Features[TaxiwayWIndex];

        AirportMapDocument deleted = MapEdits.DeleteVertex(document, TaxiwayWIndex, 3);

        MapFeature after = deleted.Features[TaxiwayWIndex];
        Assert.Equal(line.Coordinates.Length - 1, after.Coordinates.Length);
        Assert.Equal(line.Coordinates[4], after.Coordinates[3]);
        Assert.Equal(line.Coordinates.Length, document.Features[TaxiwayWIndex].Coordinates.Length);
    }

    [Fact]
    public void DeleteVertex_ThatWouldLeaveFewerThanTwoVertices_Throws()
    {
        AirportMapDocument document = Oak();
        Assert.Equal(2, document.Features[TwoVertexTaxiwayIndex].Coordinates.Length);

        Assert.Throws<InvalidOperationException>(() => MapEdits.DeleteVertex(document, TwoVertexTaxiwayIndex, 0));
    }

    [Fact]
    public void AddFeature_AppendsOrInserts()
    {
        AirportMapDocument document = Oak();
        int originalCount = document.Features.Count;
        MapPosition position = OffsetNorth(document.Features[Stand25Index].Coordinates[0], 300.0);
        MapFeature feature = MapEdits.NewFeature(document, "parking", "99", [position]);

        AirportMapDocument appended = MapEdits.AddFeature(document, feature, null);
        Assert.Equal(originalCount + 1, appended.Features.Count);
        Assert.Same(feature, appended.Features[^1]);
        Assert.Equal(originalCount, document.Features.Count);

        AirportMapDocument inserted = MapEdits.AddFeature(document, feature, 0);
        Assert.Same(feature, inserted.Features[0]);
        Assert.Same(document.Features[0], inserted.Features[1]);

        Assert.Throws<ArgumentOutOfRangeException>(() => MapEdits.AddFeature(document, feature, document.Features.Count + 1));
    }

    [Fact]
    public void DeleteFeature_RemovesIt()
    {
        AirportMapDocument document = Oak();
        MapFeature removed = document.Features[TwoVertexTaxiwayIndex];
        string before = document.Write();

        AirportMapDocument deleted = MapEdits.DeleteFeature(document, TwoVertexTaxiwayIndex);

        Assert.Equal(document.Features.Count - 1, deleted.Features.Count);
        Assert.DoesNotContain(deleted.Features, feature => ReferenceEquals(feature, removed));
        Assert.Same(document.Features[TwoVertexTaxiwayIndex + 1], deleted.Features[TwoVertexTaxiwayIndex]);
        Assert.Equal(before, document.Write());
    }

    [Fact]
    public void SetProperty_SetsAddsAndRemoves()
    {
        AirportMapDocument document = Oak();

        AirportMapDocument withHeading = MapEdits.SetProperty(document, Stand25Index, "heading", JsonValue.Create(90));
        Assert.Equal(90, withHeading.Features[Stand25Index].CloneProperties()!["heading"]!.GetValue<int>());
        Assert.Equal(68, document.Features[Stand25Index].CloneProperties()!["heading"]!.GetValue<int>());

        AirportMapDocument withNote = MapEdits.SetProperty(withHeading, Stand25Index, "note", JsonValue.Create("edited"));
        Assert.Equal("edited", withNote.Features[Stand25Index].CloneProperties()!["note"]!.GetValue<string>());

        AirportMapDocument without = MapEdits.SetProperty(withNote, Stand25Index, "heading", null);
        JsonObject properties = without.Features[Stand25Index].CloneProperties()!;
        Assert.Null(properties["heading"]);
        Assert.Equal(["type", "name", "note"], properties.Select(member => member.Key));

        Assert.Same(document, MapEdits.SetProperty(document, Stand25Index, "heading", JsonValue.Create(68)));
        Assert.Same(document, MapEdits.SetProperty(document, Stand25Index, "missing", null));
    }

    [Theory]
    [InlineData("parking", "Point", "type,name,heading")]
    [InlineData("taxiway", "LineString", "type,name,circular")]
    [InlineData("spot", "Point", "type,name")]
    [InlineData("runway", "LineString", "type,name,threshold,turnoff")]
    [InlineData("helipad", "Point", "type,name")]
    public void NewFeature_CopiesItsTypesTemplateKeysAndSetsTheName(string featureType, string geometryType, string expectedKeys)
    {
        AirportMapDocument document = Oak();
        MapPosition position = OffsetNorth(document.Features[Stand25Index].Coordinates[0], 300.0);
        ImmutableArray<MapPosition> positions = [position];
        if (geometryType != "Point")
        {
            positions = [position, OffsetNorth(position, 100.0)];
        }

        MapFeature feature = MapEdits.NewFeature(document, featureType, "NEW", positions);

        Assert.Equal(featureType, feature.FeatureType);
        Assert.Equal(geometryType, feature.GeometryType);
        JsonObject properties = feature.CloneProperties()!;
        Assert.Equal(expectedKeys.Split(','), properties.Select(member => member.Key));
        Assert.Equal("NEW", properties["name"]!.GetValue<string>());
        foreach (KeyValuePair<string, JsonNode?> member in properties)
        {
            if (member.Key is not ("type" or "name"))
            {
                Assert.Null(member.Value);
            }
        }
    }

    [Fact]
    public void NewFeature_RefusesALineStringWithFewerThanTwoPositions()
    {
        AirportMapDocument document = Oak();
        MapPosition position = OffsetNorth(document.Features[Stand25Index].Coordinates[0], 300.0);

        Assert.Throws<ArgumentException>(() => MapEdits.NewFeature(document, "taxiway", "X", []));
        Assert.Throws<ArgumentException>(() => MapEdits.NewFeature(document, "taxiway", "X", [position]));
        Assert.Throws<ArgumentException>(() => MapEdits.NewFeature(document, "runway", "X", []));
        Assert.Throws<ArgumentException>(() => MapEdits.NewFeature(document, "runway", "X", [position]));
        Assert.Throws<ArgumentException>(() => MapEdits.NewFeature(document, "taxiway", "X", default));
    }

    [Fact]
    public void SetProperty_ReplacesACapitalisedKeyUnderItsAuthoredCase()
    {
        AirportMapDocument oak = Oak();
        MapFeature stand = oak.Features[Stand25Index];
        var renamed = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> member in stand.CloneProperties()!)
        {
            renamed[(member.Key == "heading") ? "Heading" : member.Key] = member.Value?.DeepClone();
        }

        var document = new AirportMapDocument(
            oak.Features.SetItem(Stand25Index, new MapFeature(renamed, stand.GeometryType, stand.Coordinates, stand.ForeignMembers)),
            oak.ForeignMembers
        );

        AirportMapDocument edited = MapEdits.SetProperty(document, Stand25Index, "heading", JsonValue.Create(90));

        JsonObject properties = edited.Features[Stand25Index].CloneProperties()!;
        Assert.Equal(90, properties["Heading"]!.GetValue<int>());
        Assert.DoesNotContain("heading", properties.Select(member => member.Key));
        Assert.Equal(["type", "name", "Heading"], properties.Select(member => member.Key));
    }

    [Fact]
    public void SplitTaxiway_ReturnsTheNewFeaturesIndex()
    {
        AirportMapDocument document = Oak();
        MapFeature taxiway = document.Features[TaxiwayWIndex];

        (AirportMapDocument split, int newIndex) = MapEdits.SplitTaxiway(document, TaxiwayWIndex, 5);

        Assert.Equal(TaxiwayWIndex + 1, newIndex);
        Assert.Equal(document.Features.Count + 1, split.Features.Count);
        MapFeature first = split.Features[TaxiwayWIndex];
        MapFeature second = split.Features[newIndex];
        Assert.Equal(taxiway.Coordinates.Take(6), first.Coordinates);
        Assert.Equal(taxiway.Coordinates.Skip(5), second.Coordinates);
        Assert.Equal("W", second.CloneProperties()!["name"]!.GetValue<string>());
        Assert.Equal(["type", "name", "circular"], second.CloneProperties()!.Select(member => member.Key));
        Assert.Same(document.Features[TaxiwayWIndex + 1], split.Features[newIndex + 1]);
        Assert.Equal(taxiway.Coordinates.Length, document.Features[TaxiwayWIndex].Coordinates.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => MapEdits.SplitTaxiway(document, TaxiwayWIndex, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapEdits.SplitTaxiway(document, TaxiwayWIndex, taxiway.Coordinates.Length - 1));
    }

    [Fact]
    public void SplitTaxiway_OnARunway_Throws()
    {
        AirportMapDocument document = Oak();

        Assert.Throws<InvalidOperationException>(() => MapEdits.SplitTaxiway(document, RunwayIndex, 4));
    }

    [Fact]
    public void Edit_OnAnUnknownFeatureIndex_Throws()
    {
        AirportMapDocument document = Oak();
        MapPosition position = document.Features[Stand25Index].Coordinates[0];

        Assert.Throws<ArgumentOutOfRangeException>(() => MapEdits.MoveVertex(document, document.Features.Count, 0, position));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapEdits.MoveVertex(document, -1, 0, position));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapEdits.InsertVertex(document, document.Features.Count, 0, position));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapEdits.DeleteVertex(document, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapEdits.DeleteFeature(document, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapEdits.SetProperty(document, document.Features.Count, "x", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapEdits.SplitTaxiway(document, -1, 1));
    }

    [Fact]
    public void UndoRedo_ThroughMixedEdits_ReturnsEachEarlierDocument()
    {
        AirportMapDocument d0 = Oak();
        var stack = new UndoStack<AirportMapDocument>();
        var states = new List<AirportMapDocument> { d0 };
        AirportMapDocument current = d0;

        stack.Record("move vertex", current);
        current = MapEdits.MoveVertex(current, Stand25Index, 0, OffsetNorth(current.Features[Stand25Index].Coordinates[0], 20.0));
        states.Add(current);

        stack.Record("add stand", current);
        MapFeature added = MapEdits.NewFeature(current, "parking", "99", [OffsetNorth(current.Features[Stand25Index].Coordinates[0], 60.0)]);
        current = MapEdits.AddFeature(current, added, null);
        states.Add(current);

        stack.Record("split taxiway", current);
        (current, _) = MapEdits.SplitTaxiway(current, TaxiwayWIndex, 5);
        states.Add(current);

        stack.Record("set property", current);
        current = MapEdits.SetProperty(current, Stand25Index, "note", JsonValue.Create("edited"));
        states.Add(current);

        stack.Record("delete feature", current);
        current = MapEdits.DeleteFeature(current, current.Features.Count - 1);
        states.Add(current);

        for (int i = states.Count - 2; i >= 0; i--)
        {
            current = Assert.IsType<AirportMapDocument>(stack.Undo(current));
            Assert.Same(states[i], current);
            AssertJsonEqual(states[i], current);
        }

        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);

        for (int i = 1; i < states.Count; i++)
        {
            current = Assert.IsType<AirportMapDocument>(stack.Redo(current));
            Assert.Same(states[i], current);
            AssertJsonEqual(states[i], current);
        }

        Assert.False(stack.CanRedo);
        Assert.Same(d0.Features[1], current.Features[1]);
        Assert.Same(d0.Features[RunwayIndex], current.Features[RunwayIndex]);
    }

    private static AirportMapDocument Oak() => AirportMapDocument.Read(File.ReadAllText(OakPath));

    private static MapPosition OffsetNorth(MapPosition position, double feet) => new(position.Lon, position.Lat + (feet / GeoMath.FeetPerNm / 60.0));

    private static void AssertJsonEqual(AirportMapDocument expected, AirportMapDocument actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.Write()), JsonNode.Parse(actual.Write())), "the documents differ");
}
