using System.IO.Compression;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Zip export/import round-trips for favorites. Each test builds isolated source/target stores
/// in throwaway directories.
/// </summary>
public class FavoriteExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-favexport-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FavoriteStore NewStore(string name) => new(Path.Combine(_root, name));

    private static FavoriteCommand Fav(string label) => new() { Label = label, CommandText = label };

    [Fact]
    public void ExportSet_WritesSetJsonAndOnePerFavorite_WithReadableNames()
    {
        var store = NewStore("source");
        var set = store.CreateNamedSet("Tower Stuff")!;
        var a = Fav("FH 270");
        var b = Fav("CM 014");
        store.SaveFavorite(a);
        store.SaveFavorite(b);
        store.AddToSet(set.Id, a.Id);
        store.AddToSet(set.Id, b.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportSet(store, set.Id, buffer);
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("set.json", names);
        Assert.Contains($"favorites/FH 270.{a.Id}.json", names);
        Assert.Contains($"favorites/CM 014.{b.Id}.json", names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void SetZip_RoundTrips_IntoAFreshStore()
    {
        var source = NewStore("source");
        var set = source.CreateNamedSet("Shared")!;
        var a = Fav("First");
        var b = Fav("Second");
        source.SaveFavorite(a);
        source.SaveFavorite(b);
        source.AddToSet(set.Id, a.Id);
        source.AddToSet(set.Id, b.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportSet(source, set.Id, buffer);
        buffer.Position = 0;

        var target = NewStore("target");
        var result = FavoriteExport.ImportFile(target, $"Shared{FavoriteExport.SetExportExtension}", buffer);

        Assert.NotNull(result);
        Assert.Equal(2, result.FavoritesAdded);
        Assert.Equal(1, result.SetsAdded);
        Assert.Equal(0, result.MissingReferences);

        var imported = target.FindNamedSet("Shared");
        Assert.NotNull(imported);
        Assert.Equal(set.Id, imported.Id);
        Assert.Equal(["First", "Second"], target.GetSetFavorites(imported.Id).Select(f => f.Label));
        // A set zip carries its favorites inside the set, not the base Global set.
        Assert.Empty(target.GlobalSet.FavoriteIds);
    }

    [Fact]
    public void SetZip_ReImport_IsIdempotent()
    {
        var source = NewStore("source");
        var set = source.CreateNamedSet("Twice")!;
        var a = Fav("Only");
        source.SaveFavorite(a);
        source.AddToSet(set.Id, a.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportSet(source, set.Id, buffer);

        var target = NewStore("target");
        buffer.Position = 0;
        FavoriteExport.ImportFile(target, "Twice.yaat-favset.zip", buffer);
        buffer.Position = 0;
        var second = FavoriteExport.ImportFile(target, "Twice.yaat-favset.zip", buffer);

        Assert.NotNull(second);
        Assert.Equal(0, second.FavoritesAdded);
        Assert.Equal(1, second.FavoritesUpdated);
        Assert.Equal(0, second.SetsAdded);
        Assert.Single(target.OrderedSets, s => s.Kind == FavoriteSetKind.Named);
        Assert.Equal(["Only"], target.GetSetFavorites(target.FindNamedSet("Twice")!.Id).Select(f => f.Label));
    }

    [Fact]
    public void SetZip_NameCollisionWithDifferentSet_AutoSuffixes()
    {
        var source = NewStore("source");
        var set = source.CreateNamedSet("Clash")!;
        var a = Fav("Theirs");
        source.SaveFavorite(a);
        source.AddToSet(set.Id, a.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportSet(source, set.Id, buffer);
        buffer.Position = 0;

        var target = NewStore("target");
        target.CreateNamedSet("Clash");
        FavoriteExport.ImportFile(target, "Clash.yaat-favset.zip", buffer);

        Assert.NotNull(target.FindNamedSet("Clash"));
        var suffixed = target.FindNamedSet("Clash (2)");
        Assert.NotNull(suffixed);
        Assert.Equal(["Theirs"], target.GetSetFavorites(suffixed.Id).Select(f => f.Label));
    }

    [Fact]
    public void LibraryZip_RoundTripsSetsOrphansAndLoadedHints()
    {
        var source = NewStore("source");
        var named = source.CreateNamedSet("Pack")!;
        var airport = source.GetOrCreateAirportSet("OAK");
        var inSet = Fav("InSet");
        var inAirport = Fav("AtOak");
        var orphan = Fav("Loose");
        foreach (var fav in new[] { inSet, inAirport, orphan })
        {
            source.SaveFavorite(fav);
        }
        source.AddToSet(named.Id, inSet.Id);
        source.AddToSet(airport.Id, inAirport.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportLibrary(source, [named.Id], buffer);
        buffer.Position = 0;

        var target = NewStore("target");
        var result = FavoriteExport.ImportFile(target, $"favorites{FavoriteExport.LibraryExportExtension}", buffer);

        Assert.NotNull(result);
        Assert.Equal(3, result.FavoritesAdded);
        Assert.Equal([named.Id], result.NewSetIdsToLoad);
        Assert.Equal(["InSet"], target.GetSetFavorites(target.FindNamedSet("Pack")!.Id).Select(f => f.Label));
        Assert.Equal(["AtOak"], target.GetSetFavorites(target.FindAirportSet("OAK")!.Id).Select(f => f.Label));
        Assert.Equal(["Loose"], target.GetOrphanFavorites().Select(f => f.Label));
    }

    [Fact]
    public void LibraryZip_MergesScopeSetsIntoExistingLocalContainers()
    {
        var source = NewStore("source");
        var airport = source.GetOrCreateAirportSet("OAK");
        var theirs = Fav("Theirs");
        source.SaveFavorite(theirs);
        source.AddToSet(airport.Id, theirs.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportLibrary(source, [], buffer);
        buffer.Position = 0;

        var target = NewStore("target");
        var localAirport = target.GetOrCreateAirportSet("OAK");
        var mine = Fav("Mine");
        target.SaveFavorite(mine);
        target.AddToSet(localAirport.Id, mine.Id);

        FavoriteExport.ImportFile(target, "favorites.yaat-favlibrary.zip", buffer);

        // One OAK container, existing membership first, imported appended.
        var oakSets = target.OrderedSets.Where(s => s.Kind == FavoriteSetKind.Airport).ToList();
        var oak = Assert.Single(oakSets);
        Assert.Equal(localAirport.Id, oak.Id);
        Assert.Equal(["Mine", "Theirs"], target.GetSetFavorites(oak.Id).Select(f => f.Label));
    }

    [Fact]
    public void SingleFavoriteJson_ImportsIntoGlobal_AndSameIdUpdatesInPlace()
    {
        var store = NewStore("store");
        var favorite = Fav("Solo");
        favorite.Id = "0123abcd";
        var json = System.Text.Json.JsonSerializer.Serialize(
            favorite,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }
        );

        using (var first = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
        {
            var result = FavoriteExport.ImportFile(store, "Solo.json", first);
            Assert.NotNull(result);
            Assert.Equal(1, result.FavoritesAdded);
        }
        Assert.Equal(["Solo"], store.GetSetFavorites(store.GlobalSet.Id).Select(f => f.Label));

        var edited = json.Replace("Solo", "Renamed");
        using (var second = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(edited)))
        {
            var result = FavoriteExport.ImportFile(store, "Renamed.json", second);
            Assert.NotNull(result);
            Assert.Equal(1, result.FavoritesUpdated);
        }

        // Same id: updated in place, still exactly one Global entry.
        Assert.Equal(["Renamed"], store.GetSetFavorites(store.GlobalSet.Id).Select(f => f.Label));
    }

    [Fact]
    public void SetJsonWithoutItsFavorites_ReportsMissingReferences()
    {
        var source = NewStore("source");
        var set = source.CreateNamedSet("Refs")!;
        var a = Fav("Gone");
        source.SaveFavorite(a);
        source.AddToSet(set.Id, a.Id);
        var json = System.Text.Json.JsonSerializer.Serialize(
            set,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }
        );

        var target = NewStore("target");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var result = FavoriteExport.ImportFile(target, "Refs.json", stream);

        Assert.NotNull(result);
        Assert.Equal(1, result.SetsAdded);
        Assert.Equal(1, result.MissingReferences);
        Assert.Empty(target.GetSetFavorites(target.FindNamedSet("Refs")!.Id));
    }

    [Fact]
    public void UnrecognizedContent_ReturnsNull()
    {
        var store = NewStore("store");

        using var garbageJson = new MemoryStream("not json"u8.ToArray());
        Assert.Null(FavoriteExport.ImportFile(store, "garbage.json", garbageJson));

        using var garbageZip = new MemoryStream([1, 2, 3, 4]);
        Assert.Null(FavoriteExport.ImportFile(store, "garbage.zip", garbageZip));
    }

    [Fact]
    public void NormalizeFavoriteCategory_MapsUndefinedToAir()
    {
        Assert.Equal(FavoriteCommandCategory.Ground, MainViewModel.NormalizeFavoriteCategory(FavoriteCommandCategory.Ground));
        Assert.Equal(FavoriteCommandCategory.Air, MainViewModel.NormalizeFavoriteCategory((FavoriteCommandCategory)999));
    }
}
