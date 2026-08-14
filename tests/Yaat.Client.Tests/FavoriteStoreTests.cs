using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// FavoriteStore unit tests. Every test gets its own throwaway root directory, so tests are
/// isolated from each other and from the per-process YAAT_APPDATA_DIR.
/// </summary>
public class FavoriteStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-favstore-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FavoriteStore NewStore() => new(_root);

    private static FavoriteCommand Fav(string label) => new() { Label = label, CommandText = label };

    [Fact]
    public void NewStore_CreatesGlobalSet_AndReportsEmptyLoad()
    {
        var store = NewStore();

        Assert.True(store.LoadedFromEmpty);
        Assert.Equal(FavoriteSetKind.Global, store.GlobalSet.Kind);
        Assert.Equal("Global", store.GlobalSet.DisplayName);

        var reloaded = NewStore();
        Assert.False(reloaded.LoadedFromEmpty);
        Assert.Equal(store.GlobalSet.Id, reloaded.GlobalSet.Id);
    }

    [Fact]
    public void SaveFavorite_AssignsId_AndRoundTripsThroughDisk()
    {
        var store = NewStore();
        var favorite = new FavoriteCommand
        {
            Label = "T3 B",
            CommandText = "T T3 B",
            GroundCommandText = "TAXI T3 B",
            Category = FavoriteCommandCategory.Ground,
            BackgroundColor = "#112233",
            TextColor = "#445566",
            ButtonWidth = 123,
            ButtonHeight = 45,
        };

        store.SaveFavorite(favorite);
        Assert.Matches("^[0-9a-f]{8}$", favorite.Id);
        store.AddToSet(store.GlobalSet.Id, favorite.Id);

        var reloaded = NewStore();
        var restored = Assert.Single(reloaded.GetSetFavorites(reloaded.GlobalSet.Id));
        Assert.Equal(favorite.Id, restored.Id);
        Assert.Equal("T3 B", restored.Label);
        Assert.Equal("T T3 B", restored.CommandText);
        Assert.Equal("TAXI T3 B", restored.GroundCommandText);
        Assert.Equal(FavoriteCommandCategory.Ground, restored.Category);
        Assert.Equal("#112233", restored.BackgroundColor);
        Assert.Equal("#445566", restored.TextColor);
        Assert.Equal(123, restored.ButtonWidth);
        Assert.Equal(45, restored.ButtonHeight);
    }

    [Fact]
    public void FavoriteFileName_CarriesLabelAndId_AndFollowsRename()
    {
        var store = NewStore();
        var favorite = Fav("FH 270");
        store.SaveFavorite(favorite);

        var commandsDir = Path.Combine(_root, "commands");
        Assert.True(File.Exists(Path.Combine(commandsDir, $"FH 270.{favorite.Id}.json")));

        favorite.Label = "FH 090";
        store.SaveFavorite(favorite);

        Assert.True(File.Exists(Path.Combine(commandsDir, $"FH 090.{favorite.Id}.json")));
        Assert.False(File.Exists(Path.Combine(commandsDir, $"FH 270.{favorite.Id}.json")));
    }

    [Theory]
    [InlineData("T/T3:B?", "T_T3_B_")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("...", "favorite")]
    [InlineData("", "favorite")]
    public void SanitizeFileName_ReplacesIllegalCharacters(string input, string expected)
    {
        Assert.Equal(expected, FavoriteStore.SanitizeFileName(input, "favorite"));
    }

    [Fact]
    public void CreateNamedSet_RejectsBlankAndCaseInsensitiveCollision()
    {
        var store = NewStore();
        Assert.NotNull(store.CreateNamedSet("S1 Training"));

        Assert.Null(store.CreateNamedSet("s1 training"));
        Assert.Null(store.CreateNamedSet("  "));
    }

    [Fact]
    public void RenameNamedSet_RenamesFileAndRejectsCollisions()
    {
        var store = NewStore();
        var setA = store.CreateNamedSet("Alpha")!;
        store.CreateNamedSet("Bravo");

        Assert.False(store.RenameNamedSet(setA.Id, "bravo"));
        Assert.False(store.RenameNamedSet(store.GlobalSet.Id, "Anything"));
        Assert.True(store.RenameNamedSet(setA.Id, "Charlie"));

        var setsDir = Path.Combine(_root, "sets");
        Assert.True(File.Exists(Path.Combine(setsDir, $"Charlie.{setA.Id}.json")));
        Assert.False(File.Exists(Path.Combine(setsDir, $"Alpha.{setA.Id}.json")));
    }

    [Fact]
    public void DeleteSet_RemovesFileButKeepsFavoriteEntities()
    {
        var store = NewStore();
        var set = store.CreateNamedSet("Doomed")!;
        var favorite = Fav("Survivor");
        store.SaveFavorite(favorite);
        store.AddToSet(set.Id, favorite.Id);

        Assert.True(store.DeleteSet(set.Id));
        Assert.False(store.DeleteSet(store.GlobalSet.Id));

        Assert.NotNull(store.GetFavorite(favorite.Id));
        Assert.Contains(favorite, store.GetOrphanFavorites());
        Assert.False(File.Exists(Path.Combine(_root, "sets", $"Doomed.{set.Id}.json")));
    }

    [Fact]
    public void Membership_AddInsertRemove_KeepOrderAndDedupe()
    {
        var store = NewStore();
        var set = store.CreateNamedSet("Order")!;
        var a = Fav("A");
        var b = Fav("B");
        var c = Fav("C");
        store.SaveFavorite(a);
        store.SaveFavorite(b);
        store.SaveFavorite(c);

        store.AddToSet(set.Id, a.Id);
        store.AddToSet(set.Id, b.Id);
        store.AddToSet(set.Id, a.Id);
        store.InsertInSet(set.Id, c.Id, 1);

        Assert.Equal(["A", "C", "B"], store.GetSetFavorites(set.Id).Select(f => f.Label));

        store.RemoveFromSet(set.Id, c.Id);
        Assert.Equal(["A", "B"], store.GetSetFavorites(set.Id).Select(f => f.Label));
    }

    [Fact]
    public void ReplaceSetFavorites_PrunesUnknownIdsAndDuplicates()
    {
        var store = NewStore();
        var set = store.CreateNamedSet("Pruned")!;
        var a = Fav("A");
        store.SaveFavorite(a);

        store.ReplaceSetFavorites(set.Id, [a.Id, "deadbeef", a.Id]);

        Assert.Equal([a.Id], store.GetSet(set.Id)!.FavoriteIds);
    }

    [Fact]
    public void DeleteFavorite_RemovesEntityFromEverySet()
    {
        var store = NewStore();
        var set = store.CreateNamedSet("Holder")!;
        var favorite = Fav("Everywhere");
        store.SaveFavorite(favorite);
        store.AddToSet(store.GlobalSet.Id, favorite.Id);
        store.AddToSet(set.Id, favorite.Id);

        Assert.True(store.DeleteFavorite(favorite.Id));

        Assert.Null(store.GetFavorite(favorite.Id));
        Assert.Empty(store.GlobalSet.FavoriteIds);
        Assert.Empty(store.GetSet(set.Id)!.FavoriteIds);
        Assert.False(store.DeleteFavorite(favorite.Id));
    }

    [Fact]
    public void GetMembershipSetIds_ListsEverySetHoldingTheFavorite()
    {
        var store = NewStore();
        var set = store.CreateNamedSet("Second")!;
        var favorite = Fav("Shared");
        store.SaveFavorite(favorite);
        store.AddToSet(store.GlobalSet.Id, favorite.Id);
        store.AddToSet(set.Id, favorite.Id);

        var memberships = store.GetMembershipSetIds(favorite.Id);
        Assert.Equal(2, memberships.Count);
        Assert.Contains(store.GlobalSet.Id, memberships);
        Assert.Contains(set.Id, memberships);
    }

    [Fact]
    public void ComposeDisplay_OrdersGlobalAirportScenarioThenLoadedSets()
    {
        var store = NewStore();
        var airport = store.GetOrCreateAirportSet("oak");
        var scenario = store.GetOrCreateScenarioSet("SCN-1", "Practice");
        var named = store.CreateNamedSet("Extras")!;

        var g = Fav("G");
        var a = Fav("A");
        var s = Fav("S");
        var n = Fav("N");
        foreach (var fav in new[] { g, a, s, n })
        {
            store.SaveFavorite(fav);
        }
        store.AddToSet(store.GlobalSet.Id, g.Id);
        store.AddToSet(airport.Id, a.Id);
        store.AddToSet(scenario.Id, s.Id);
        store.AddToSet(named.Id, n.Id);

        var display = store.ComposeDisplay("SCN-1", "OAK", [named.Id]);
        Assert.Equal(["G", "A", "S", "N"], display.Select(e => e.Favorite.Label));

        var withoutContext = store.ComposeDisplay(null, null, []);
        Assert.Equal(["G"], withoutContext.Select(e => e.Favorite.Label));

        var unknownLoadedId = store.ComposeDisplay(null, null, ["deadbeef"]);
        Assert.Equal(["G"], unknownLoadedId.Select(e => e.Favorite.Label));
    }

    [Fact]
    public void ComposeDisplay_ShowsSharedFavoriteOncePerVisibleContainer()
    {
        var store = NewStore();
        var named = store.CreateNamedSet("Both")!;
        var favorite = Fav("Twice");
        store.SaveFavorite(favorite);
        store.AddToSet(store.GlobalSet.Id, favorite.Id);
        store.AddToSet(named.Id, favorite.Id);

        var display = store.ComposeDisplay(null, null, [named.Id]);

        Assert.Equal(2, display.Count);
        Assert.All(display, e => Assert.Same(favorite, e.Favorite));
        Assert.Equal([store.GlobalSet.Id, named.Id], display.Select(e => e.SetId));
    }

    [Fact]
    public void GetOrCreateScenarioSet_RefreshesDisplayName()
    {
        var store = NewStore();
        var created = store.GetOrCreateScenarioSet("SCN-9", "SCN-9");

        var refreshed = store.GetOrCreateScenarioSet("SCN-9", "Friendly Name");

        Assert.Equal(created.Id, refreshed.Id);
        Assert.Equal("Scenario (Friendly Name)", refreshed.DisplayName);
    }

    [Fact]
    public void Load_PrunesMembershipOfMissingFavoriteFiles()
    {
        var store = NewStore();
        var keep = Fav("Keep");
        var lost = Fav("Lost");
        store.SaveFavorite(keep);
        store.SaveFavorite(lost);
        store.AddToSet(store.GlobalSet.Id, keep.Id);
        store.AddToSet(store.GlobalSet.Id, lost.Id);

        File.Delete(Path.Combine(_root, "commands", $"Lost.{lost.Id}.json"));

        var reloaded = NewStore();
        Assert.Equal(["Keep"], reloaded.GetSetFavorites(reloaded.GlobalSet.Id).Select(f => f.Label));
    }

    [Fact]
    public void OrderedSets_SortGlobalAirportsScenariosThenNamed()
    {
        var store = NewStore();
        store.CreateNamedSet("Zulu");
        store.CreateNamedSet("alpha");
        store.GetOrCreateAirportSet("SFO");
        store.GetOrCreateAirportSet("OAK");
        store.GetOrCreateScenarioSet("SCN-2", "Bravo");

        Assert.Equal(["Global", "Airport (OAK)", "Airport (SFO)", "Scenario (Bravo)", "alpha", "Zulu"], store.OrderedSets.Select(s => s.DisplayName));
    }
}
