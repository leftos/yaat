using System.Text.Json;
using Xunit;
using Yaat.Sim.Authoring.Routes;

namespace Yaat.Sim.Tests.Authoring.Routes;

/// <summary>
/// <see cref="RouteLibrary"/> keeps one JSON file per saved route, named by the route's id, in a folder of its own; each test
/// gets a fresh temporary folder.
/// </summary>
public sealed class RouteLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-route-library-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static SavedRoute Route(string name, string artccId, params RoutePoint[] points) =>
        new(Guid.NewGuid().ToString("D"), name, "#FF8800", artccId, points);

    [Fact]
    public void SaveThenList_ReturnsTheRouteWithExactPoints()
    {
        var library = new RouteLibrary(_root);
        SavedRoute route = Route(
            "Bay arrivals",
            "ZOA",
            RoutePoint.AtFix("SUNOL"),
            RoutePoint.AtFrd("SFO", 90, 10),
            RoutePoint.AtLatLon(new LatLon(37.123456789012, -122.987654321098))
        );

        library.Save(route);
        RouteLibraryListing listing = library.List(null);

        SavedRoute loaded = Assert.Single(listing.Routes);
        Assert.Empty(listing.CorruptFiles);
        Assert.Equal(route.Id, loaded.Id);
        Assert.Equal("Bay arrivals", loaded.Name);
        Assert.Equal("#FF8800", loaded.Color);
        Assert.Equal("ZOA", loaded.ArtccId);
        Assert.Equal(3, loaded.Points.Count);
        Assert.Equal(route.Points[0], loaded.Points[0]);
        Assert.Equal(route.Points[1], loaded.Points[1]);
        Assert.Equal(RoutePointKind.LatLon, loaded.Points[2].Kind);
        Assert.Equal(37.123456789012, loaded.Points[2].Lat!.Value, 1e-9);
        Assert.Equal(-122.987654321098, loaded.Points[2].Lon!.Value, 1e-9);
    }

    [Fact]
    public void SaveTwiceWithOneId_ReplacesAndTwoIdsCoexist()
    {
        var library = new RouteLibrary(_root);
        SavedRoute first = Route("First", "ZOA", RoutePoint.AtFix("SUNOL"));
        SavedRoute other = Route("Other", "ZOA", RoutePoint.AtFix("SFO"));

        library.Save(first);
        library.Save(first with { Name = "Renamed" });
        library.Save(other);

        IReadOnlyList<SavedRoute> routes = library.List(null).Routes;
        Assert.Equal(2, routes.Count);
        Assert.Equal("Renamed", Assert.Single(routes, r => r.Id == first.Id).Name);
        Assert.Equal("Other", Assert.Single(routes, r => r.Id == other.Id).Name);
    }

    [Fact]
    public void ListByArtcc_ReturnsOnlyThatArtccAndNullReturnsAll()
    {
        var library = new RouteLibrary(_root);
        SavedRoute zoa = Route("Oakland", "ZOA", RoutePoint.AtFix("SUNOL"));
        SavedRoute zla = Route("Los Angeles", "ZLA", RoutePoint.AtFix("SFO"));
        library.Save(zoa);
        library.Save(zla);

        Assert.Equal(zoa.Id, Assert.Single(library.List("ZOA").Routes).Id);
        Assert.Equal(zoa.Id, Assert.Single(library.List("zoa").Routes).Id);
        Assert.Equal(2, library.List(null).Routes.Count);
    }

    [Fact]
    public void CorruptFile_IsReportedByNameAndOthersStillLoad()
    {
        var library = new RouteLibrary(_root);
        SavedRoute good = Route("Good", "ZOA", RoutePoint.AtFix("SUNOL"));
        library.Save(good);
        string corruptName = Guid.NewGuid().ToString("D") + ".json";
        File.WriteAllText(Path.Combine(_root, corruptName), "{ not json");

        RouteLibraryListing listing = library.List(null);

        Assert.Equal(good.Id, Assert.Single(listing.Routes).Id);
        Assert.Equal(corruptName, Assert.Single(listing.CorruptFiles));
    }

    [Fact]
    public void SavedJson_HoldsExactlyTheGeometryKeys()
    {
        var library = new RouteLibrary(_root);
        SavedRoute route = Route("Keys", "ZOA", RoutePoint.AtFix("SUNOL"));

        library.Save(route);

        string path = Assert.Single(Directory.GetFiles(_root, "*.json"));
        Assert.Equal(route.Id + ".json", Path.GetFileName(path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        string[] keys = [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
        Assert.Equal(["id", "name", "color", "artccId", "points"], keys);
    }

    [Fact]
    public void FileNotNamedByItsRouteGuid_IsReportedAsCorrupt()
    {
        var library = new RouteLibrary(_root);
        SavedRoute good = Route("Good", "ZOA", RoutePoint.AtFix("SUNOL"));
        library.Save(good);
        string goodJson = File.ReadAllText(Path.Combine(_root, good.Id + ".json"));
        string copyName = good.Id + " - Copy.json";
        string otherName = Guid.NewGuid().ToString("D") + ".json";
        File.WriteAllText(Path.Combine(_root, copyName), goodJson);
        File.WriteAllText(Path.Combine(_root, otherName), goodJson);
        File.WriteAllText(Path.Combine(_root, "abc.json"), goodJson.Replace(good.Id, "abc", StringComparison.Ordinal));

        RouteLibraryListing listing = library.List(null);

        Assert.Equal(good.Id, Assert.Single(listing.Routes).Id);
        Assert.Equal(new[] { copyName, otherName, "abc.json" }.Order(StringComparer.Ordinal), listing.CorruptFiles.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("""{ "id": "ID", "artccId": "ZOA", "points": [] }""")]
    [InlineData("""{ "id": "ID", "name": "N", "points": [] }""")]
    [InlineData("""{ "id": "ID", "name": "N", "artccId": "ZOA", "points": [null] }""")]
    public void FileMissingAFieldOrWithANullPoint_IsReportedAsCorrupt(string json)
    {
        var library = new RouteLibrary(_root);
        string id = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, id + ".json"), json.Replace("ID", id, StringComparison.Ordinal));

        RouteLibraryListing listing = library.List(null);

        Assert.Empty(listing.Routes);
        Assert.Equal(id + ".json", Assert.Single(listing.CorruptFiles));
    }

    [Fact]
    public void SaveOfAnIncompleteRoute_ThrowsAndWritesNothing()
    {
        var library = new RouteLibrary(_root);
        SavedRoute route = Route("Incomplete", "ZOA", RoutePoint.AtFix("SUNOL"));

        Assert.Throws<ArgumentException>(() => library.Save(route with { Name = null! }));
        Assert.Throws<ArgumentException>(() => library.Save(route with { ArtccId = null! }));
        Assert.Throws<ArgumentException>(() => library.Save(route with { Points = [null!] }));
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFiles(_root).Any());
    }

    [Fact]
    public void NullColour_IsStillWrittenAsAKey()
    {
        var library = new RouteLibrary(_root);
        SavedRoute route = Route("No colour", "ZOA", RoutePoint.AtFix("SUNOL")) with { Color = null };

        library.Save(route);

        using var doc = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "*.json"))));
        string[] keys = [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
        Assert.Equal(["id", "name", "color", "artccId", "points"], keys);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("color").ValueKind);
        Assert.Null(Assert.Single(library.List(null).Routes).Color);
    }

    [Fact]
    public void LoadAndDelete_ByIdAndANonGuidIdIsRefused()
    {
        var library = new RouteLibrary(_root);
        SavedRoute route = Route("Loaded", "ZOA", RoutePoint.AtFix("SUNOL"));
        library.Save(route);

        Assert.Equal("Loaded", library.Load(route.Id)?.Name);
        Assert.True(library.Delete(route.Id));
        Assert.Null(library.Load(route.Id));
        Assert.False(library.Delete(route.Id));
        Assert.Throws<ArgumentException>(() => library.Save(route with { Id = "../escape" }));
    }
}
