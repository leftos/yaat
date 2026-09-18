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
        FavoriteStore store = NewStore("source");
        FavoriteSet set = store.CreateNamedSet("Tower Stuff")!;
        FavoriteCommand a = Fav("FH 270");
        FavoriteCommand b = Fav("CM 014");
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
        FavoriteStore source = NewStore("source");
        FavoriteSet set = source.CreateNamedSet("Shared")!;
        FavoriteCommand a = Fav("First");
        FavoriteCommand b = Fav("Second");
        source.SaveFavorite(a);
        source.SaveFavorite(b);
        source.AddToSet(set.Id, a.Id);
        source.AddToSet(set.Id, b.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportSet(source, set.Id, buffer);
        buffer.Position = 0;

        FavoriteStore target = NewStore("target");
        FavoriteImportResult? result = FavoriteExport.ImportFile(
            target,
            $"Shared{FavoriteExport.SetExportExtension}",
            buffer,
            FavoriteImportMode.Merge
        );

        Assert.NotNull(result);
        Assert.Equal(2, result.FavoritesAdded);
        Assert.Equal(1, result.SetsAdded);
        Assert.Equal(0, result.MissingReferences);

        FavoriteSet? imported = target.FindNamedSet("Shared");
        Assert.NotNull(imported);
        Assert.Equal(set.Id, imported.Id);
        Assert.Equal(["First", "Second"], target.GetSetFavorites(imported.Id).Select(f => f.Label));
        // A set zip carries its favorites inside the set, not the base Global set.
        Assert.Empty(target.GlobalSet.FavoriteIds);
    }

    [Fact]
    public void SetZip_ReImport_IsIdempotent()
    {
        FavoriteStore source = NewStore("source");
        FavoriteSet set = source.CreateNamedSet("Twice")!;
        FavoriteCommand a = Fav("Only");
        source.SaveFavorite(a);
        source.AddToSet(set.Id, a.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportSet(source, set.Id, buffer);

        FavoriteStore target = NewStore("target");
        buffer.Position = 0;
        FavoriteExport.ImportFile(target, "Twice.yaat-favset.zip", buffer, FavoriteImportMode.Merge);
        buffer.Position = 0;
        FavoriteImportResult? second = FavoriteExport.ImportFile(target, "Twice.yaat-favset.zip", buffer, FavoriteImportMode.Merge);

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
        FavoriteStore source = NewStore("source");
        FavoriteSet set = source.CreateNamedSet("Clash")!;
        FavoriteCommand a = Fav("Theirs");
        source.SaveFavorite(a);
        source.AddToSet(set.Id, a.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportSet(source, set.Id, buffer);
        buffer.Position = 0;

        FavoriteStore target = NewStore("target");
        target.CreateNamedSet("Clash");
        FavoriteExport.ImportFile(target, "Clash.yaat-favset.zip", buffer, FavoriteImportMode.Merge);

        Assert.NotNull(target.FindNamedSet("Clash"));
        FavoriteSet? suffixed = target.FindNamedSet("Clash (2)");
        Assert.NotNull(suffixed);
        Assert.Equal(["Theirs"], target.GetSetFavorites(suffixed.Id).Select(f => f.Label));
    }

    [Fact]
    public void LibraryZip_RoundTripsSetsOrphansAndLoadedHints()
    {
        FavoriteStore source = NewStore("source");
        FavoriteSet named = source.CreateNamedSet("Pack")!;
        FavoriteSet airport = source.GetOrCreateAirportSet("OAK");
        FavoriteCommand inSet = Fav("InSet");
        FavoriteCommand inAirport = Fav("AtOak");
        FavoriteCommand orphan = Fav("Loose");
        foreach (FavoriteCommand? fav in new[] { inSet, inAirport, orphan })
        {
            source.SaveFavorite(fav);
        }
        source.AddToSet(named.Id, inSet.Id);
        source.AddToSet(airport.Id, inAirport.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportLibrary(source, [named.Id], buffer);
        buffer.Position = 0;

        FavoriteStore target = NewStore("target");
        FavoriteImportResult? result = FavoriteExport.ImportFile(
            target,
            $"favorites{FavoriteExport.LibraryExportExtension}",
            buffer,
            FavoriteImportMode.Merge
        );

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
        FavoriteStore source = NewStore("source");
        FavoriteSet airport = source.GetOrCreateAirportSet("OAK");
        FavoriteCommand theirs = Fav("Theirs");
        source.SaveFavorite(theirs);
        source.AddToSet(airport.Id, theirs.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportLibrary(source, [], buffer);
        buffer.Position = 0;

        FavoriteStore target = NewStore("target");
        FavoriteSet localAirport = target.GetOrCreateAirportSet("OAK");
        FavoriteCommand mine = Fav("Mine");
        target.SaveFavorite(mine);
        target.AddToSet(localAirport.Id, mine.Id);

        FavoriteExport.ImportFile(target, "favorites.yaat-favlibrary.zip", buffer, FavoriteImportMode.Merge);

        // One OAK container, existing membership first, imported appended.
        var oakSets = target.OrderedSets.Where(s => s.Kind == FavoriteSetKind.Airport).ToList();
        FavoriteSet oak = Assert.Single(oakSets);
        Assert.Equal(localAirport.Id, oak.Id);
        Assert.Equal(["Mine", "Theirs"], target.GetSetFavorites(oak.Id).Select(f => f.Label));
    }

    [Fact]
    public void SingleFavoriteJson_ImportsIntoGlobal_AndSameIdUpdatesInPlace()
    {
        FavoriteStore store = NewStore("store");
        FavoriteCommand favorite = Fav("Solo");
        favorite.Id = "0123abcd";
        string json = System.Text.Json.JsonSerializer.Serialize(
            favorite,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }
        );

        using (var first = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
        {
            FavoriteImportResult? result = FavoriteExport.ImportFile(store, "Solo.json", first, FavoriteImportMode.Merge);
            Assert.NotNull(result);
            Assert.Equal(1, result.FavoritesAdded);
        }
        Assert.Equal(["Solo"], store.GetSetFavorites(store.GlobalSet.Id).Select(f => f.Label));

        string edited = json.Replace("Solo", "Renamed");
        using (var second = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(edited)))
        {
            FavoriteImportResult? result = FavoriteExport.ImportFile(store, "Renamed.json", second, FavoriteImportMode.Merge);
            Assert.NotNull(result);
            Assert.Equal(1, result.FavoritesUpdated);
        }

        // Same id: updated in place, still exactly one Global entry.
        Assert.Equal(["Renamed"], store.GetSetFavorites(store.GlobalSet.Id).Select(f => f.Label));
    }

    [Fact]
    public void SetJsonWithoutItsFavorites_ReportsMissingReferences()
    {
        FavoriteStore source = NewStore("source");
        FavoriteSet set = source.CreateNamedSet("Refs")!;
        FavoriteCommand a = Fav("Gone");
        source.SaveFavorite(a);
        source.AddToSet(set.Id, a.Id);
        string json = System.Text.Json.JsonSerializer.Serialize(
            set,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }
        );

        FavoriteStore target = NewStore("target");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        FavoriteImportResult? result = FavoriteExport.ImportFile(target, "Refs.json", stream, FavoriteImportMode.Merge);

        Assert.NotNull(result);
        Assert.Equal(1, result.SetsAdded);
        Assert.Equal(1, result.MissingReferences);
        Assert.Empty(target.GetSetFavorites(target.FindNamedSet("Refs")!.Id));
    }

    [Fact]
    public void UnrecognizedContent_ReturnsNull()
    {
        FavoriteStore store = NewStore("store");

        using var garbageJson = new MemoryStream("not json"u8.ToArray());
        Assert.Null(FavoriteExport.ImportFile(store, "garbage.json", garbageJson, FavoriteImportMode.Merge));

        using var garbageZip = new MemoryStream([1, 2, 3, 4]);
        Assert.Null(FavoriteExport.ImportFile(store, "garbage.zip", garbageZip, FavoriteImportMode.Merge));
    }

    [Fact]
    public void LibraryZip_Replace_DropsPreexistingFavoritesAndSets_LoadsManifestSets()
    {
        FavoriteStore source = NewStore("source");
        FavoriteSet pack = source.CreateNamedSet("Pack")!;
        FavoriteCommand inSet = Fav("InSet");
        FavoriteCommand inGlobal = Fav("Everywhere");
        source.SaveFavorite(inSet);
        source.SaveFavorite(inGlobal);
        source.AddToSet(pack.Id, inSet.Id);
        source.AddToSet(source.GlobalSet.Id, inGlobal.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportLibrary(source, [pack.Id], buffer);
        buffer.Position = 0;

        FavoriteStore target = NewStore("target");
        FavoriteSet old = target.CreateNamedSet("Old")!;
        FavoriteCommand mine = Fav("Mine");
        FavoriteCommand stale = Fav("Stale");
        target.SaveFavorite(mine);
        target.SaveFavorite(stale);
        target.AddToSet(target.GlobalSet.Id, mine.Id);
        target.AddToSet(old.Id, stale.Id);

        FavoriteImportResult? result = FavoriteExport.ImportFile(
            target,
            $"favorites{FavoriteExport.LibraryExportExtension}",
            buffer,
            FavoriteImportMode.Replace
        );

        Assert.NotNull(result);
        Assert.Equal(["Everywhere", "InSet"], target.AllFavorites.Select(f => f.Label).OrderBy(l => l, StringComparer.Ordinal));
        Assert.Null(target.FindNamedSet("Old"));
        Assert.Equal(["Global", "Pack"], target.OrderedSets.Select(s => s.DisplayName));
        Assert.Equal(["Everywhere"], target.GetSetFavorites(target.GlobalSet.Id).Select(f => f.Label));
        Assert.Equal(["InSet"], target.GetSetFavorites(target.FindNamedSet("Pack")!.Id).Select(f => f.Label));
        Assert.Equal([pack.Id], result.NewSetIdsToLoad);

        // Nothing of the old library survives a reload either.
        FavoriteStore reloaded = NewStore("target");
        Assert.Equal(["Everywhere", "InSet"], reloaded.AllFavorites.Select(f => f.Label).OrderBy(l => l, StringComparer.Ordinal));
        Assert.Equal(["Global", "Pack"], reloaded.OrderedSets.Select(s => s.DisplayName));
    }

    [Fact]
    public void SetZip_Replace_LoadsTheImportedSet()
    {
        FavoriteStore source = NewStore("source");
        FavoriteSet set = source.CreateNamedSet("Shared")!;
        FavoriteCommand a = Fav("First");
        source.SaveFavorite(a);
        source.AddToSet(set.Id, a.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportSet(source, set.Id, buffer);
        buffer.Position = 0;

        FavoriteStore target = NewStore("target");
        FavoriteCommand mine = Fav("Mine");
        target.SaveFavorite(mine);
        target.AddToSet(target.GlobalSet.Id, mine.Id);

        FavoriteImportResult? result = FavoriteExport.ImportFile(
            target,
            $"Shared{FavoriteExport.SetExportExtension}",
            buffer,
            FavoriteImportMode.Replace
        );

        Assert.NotNull(result);
        // A set zip carries no library manifest, so the replace has to load the set it brought in.
        Assert.Contains(target.FindNamedSet("Shared")!.Id, result.NewSetIdsToLoad);
        Assert.Equal(["First"], target.AllFavorites.Select(f => f.Label));
        Assert.Empty(target.GlobalSet.FavoriteIds);
    }

    [Fact]
    public void Replace_UnrecognizedFile_LeavesStoreIntact()
    {
        FavoriteStore target = NewStore("target");
        FavoriteCommand keep = Fav("Keep");
        target.SaveFavorite(keep);
        target.AddToSet(target.GlobalSet.Id, keep.Id);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("notes.txt").Open());
            writer.Write("nothing importable here");
        }
        buffer.Position = 0;

        Assert.Null(FavoriteExport.ImportFile(target, "notes.zip", buffer, FavoriteImportMode.Replace));

        Assert.Equal(["Keep"], target.AllFavorites.Select(f => f.Label));
        Assert.Equal(["Keep"], target.GetSetFavorites(target.GlobalSet.Id).Select(f => f.Label));
    }

    [Fact]
    public void LibraryZip_Merge_KeepsPreexisting()
    {
        FavoriteStore source = NewStore("source");
        FavoriteSet pack = source.CreateNamedSet("Pack")!;
        FavoriteCommand inSet = Fav("InSet");
        source.SaveFavorite(inSet);
        source.AddToSet(pack.Id, inSet.Id);

        using var buffer = new MemoryStream();
        FavoriteExport.ExportLibrary(source, [pack.Id], buffer);
        buffer.Position = 0;

        FavoriteStore target = NewStore("target");
        FavoriteSet old = target.CreateNamedSet("Old")!;
        FavoriteCommand mine = Fav("Mine");
        target.SaveFavorite(mine);
        target.AddToSet(target.GlobalSet.Id, mine.Id);
        target.AddToSet(old.Id, mine.Id);

        FavoriteImportResult? result = FavoriteExport.ImportFile(
            target,
            $"favorites{FavoriteExport.LibraryExportExtension}",
            buffer,
            FavoriteImportMode.Merge
        );

        Assert.NotNull(result);
        Assert.Equal(["InSet", "Mine"], target.AllFavorites.Select(f => f.Label).OrderBy(l => l, StringComparer.Ordinal));
        Assert.Equal(["Global", "Old", "Pack"], target.OrderedSets.Select(s => s.DisplayName));
        Assert.Equal(["Mine"], target.GetSetFavorites(target.GlobalSet.Id).Select(f => f.Label));
        Assert.Equal(["Mine"], target.GetSetFavorites(old.Id).Select(f => f.Label));
        Assert.Equal(["InSet"], target.GetSetFavorites(target.FindNamedSet("Pack")!.Id).Select(f => f.Label));
    }

    /// <summary>
    /// The library a user attached to GitHub #437 ("import does nothing"): 452 favorites, all in
    /// Global. The store-level import was never the failing part (the view's prompt was), so this
    /// pins the merge of a real, large, spacer-heavy library against a fresh store.
    /// </summary>
    [Fact]
    public void Issue437_UserLibraryZip_ImportsEveryFavoriteIntoGlobal()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "favorites", "issue-437.yaat-favlibrary.zip");
        Assert.True(File.Exists(path), $"Fixture missing: {path}");

        FavoriteStore target = NewStore("target");
        using FileStream stream = File.OpenRead(path);
        FavoriteImportResult? result = FavoriteExport.ImportFile(target, Path.GetFileName(path), stream, FavoriteImportMode.Merge);

        Assert.NotNull(result);
        Assert.Equal(452, result.FavoritesAdded);
        Assert.Equal(0, result.MissingReferences);
        Assert.Equal(1, result.SetsUpdated);
        Assert.Equal(452, target.AllFavorites.Count);
        Assert.Equal(452, target.GetSetFavorites(target.GlobalSet.Id).Count);
        Assert.Contains(target.GetSetFavorites(target.GlobalSet.Id), f => f.Label == "Rwy 28R" && f.CommandText == "taxi 28R; HS 1L; HS 1R");
    }

    [Fact]
    public void NormalizeFavoriteCategory_MapsUndefinedToAir()
    {
        Assert.Equal(FavoriteCommandCategory.Ground, MainViewModel.NormalizeFavoriteCategory(FavoriteCommandCategory.Ground));
        Assert.Equal(FavoriteCommandCategory.Air, MainViewModel.NormalizeFavoriteCategory((FavoriteCommandCategory)999));
    }
}
