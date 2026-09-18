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
        FavoriteStore store = NewStore();

        Assert.True(store.LoadedFromEmpty);
        Assert.Equal(FavoriteSetKind.Global, store.GlobalSet.Kind);
        Assert.Equal("Global", store.GlobalSet.DisplayName);

        FavoriteStore reloaded = NewStore();
        Assert.False(reloaded.LoadedFromEmpty);
        Assert.Equal(store.GlobalSet.Id, reloaded.GlobalSet.Id);
    }

    [Fact]
    public void SaveFavorite_AssignsId_AndRoundTripsThroughDisk()
    {
        FavoriteStore store = NewStore();
        var favorite = new FavoriteCommand
        {
            Label = "T3 B",
            CommandText = "T T3 B",
            GroundCommandText = "TAXI T3 B",
            Category = FavoriteCommandCategory.Ground,
            BackgroundColor = "#112233",
            TextColor = "#445566",
            ButtonHeight = 45,
        };

        store.SaveFavorite(favorite);
        Assert.Matches("^[0-9a-f]{8}$", favorite.Id);
        store.AddToSet(store.GlobalSet.Id, favorite.Id);

        FavoriteStore reloaded = NewStore();
        FavoriteCommand restored = Assert.Single(reloaded.GetSetFavorites(reloaded.GlobalSet.Id));
        Assert.Equal(favorite.Id, restored.Id);
        Assert.Equal("T3 B", restored.Label);
        Assert.Equal("T T3 B", restored.CommandText);
        Assert.Equal("TAXI T3 B", restored.GroundCommandText);
        Assert.Equal(FavoriteCommandCategory.Ground, restored.Category);
        Assert.Equal("#112233", restored.BackgroundColor);
        Assert.Equal("#445566", restored.TextColor);
        Assert.Equal(45, restored.ButtonHeight);
    }

    [Fact]
    public void FavoriteFileName_CarriesLabelAndId_AndFollowsRename()
    {
        FavoriteStore store = NewStore();
        FavoriteCommand favorite = Fav("FH 270");
        store.SaveFavorite(favorite);

        string commandsDir = Path.Combine(_root, "commands");
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
        FavoriteStore store = NewStore();
        Assert.NotNull(store.CreateNamedSet("S1 Training"));

        Assert.Null(store.CreateNamedSet("s1 training"));
        Assert.Null(store.CreateNamedSet("  "));
    }

    [Fact]
    public void RenameNamedSet_RenamesFileAndRejectsCollisions()
    {
        FavoriteStore store = NewStore();
        FavoriteSet setA = store.CreateNamedSet("Alpha")!;
        store.CreateNamedSet("Bravo");

        Assert.False(store.RenameNamedSet(setA.Id, "bravo"));
        Assert.False(store.RenameNamedSet(store.GlobalSet.Id, "Anything"));
        Assert.True(store.RenameNamedSet(setA.Id, "Charlie"));

        string setsDir = Path.Combine(_root, "sets");
        Assert.True(File.Exists(Path.Combine(setsDir, $"Charlie.{setA.Id}.json")));
        Assert.False(File.Exists(Path.Combine(setsDir, $"Alpha.{setA.Id}.json")));
    }

    [Fact]
    public void DeleteSet_RemovesFileButKeepsFavoriteEntities()
    {
        FavoriteStore store = NewStore();
        FavoriteSet set = store.CreateNamedSet("Doomed")!;
        FavoriteCommand favorite = Fav("Survivor");
        store.SaveFavorite(favorite);
        store.AddToSet(set.Id, favorite.Id);

        Assert.True(store.DeleteSet(set.Id));
        Assert.False(store.DeleteSet(store.GlobalSet.Id));

        Assert.NotNull(store.GetFavorite(favorite.Id));
        Assert.Contains(favorite, store.GetOrphanFavorites());
        Assert.False(File.Exists(Path.Combine(_root, "sets", $"Doomed.{set.Id}.json")));
    }

    [Fact]
    public void Clear_RemovesEverything_LeavesEmptyGlobal()
    {
        FavoriteStore store = NewStore();
        FavoriteSet named = store.CreateNamedSet("Doomed")!;
        FavoriteSet airport = store.GetOrCreateAirportSet("OAK");
        FavoriteCommand a = Fav("A");
        FavoriteCommand b = Fav("B");
        store.SaveFavorite(a);
        store.SaveFavorite(b);
        store.AddToSet(store.GlobalSet.Id, a.Id);
        store.AddToSet(named.Id, a.Id);
        store.AddToSet(airport.Id, b.Id);

        int changes = 0;
        store.Changed += () => changes++;
        store.Clear();

        Assert.Equal(1, changes);
        Assert.Empty(store.AllFavorites);
        FavoriteSet global = Assert.Single(store.OrderedSets);
        Assert.Equal(FavoriteSetKind.Global, global.Kind);
        Assert.Empty(global.FavoriteIds);

        // Nothing but the Global set file is left behind, so a fresh store loads the same state.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "commands"), "*.json"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "sets"), "*.json"));

        FavoriteStore reloaded = NewStore();
        Assert.Empty(reloaded.AllFavorites);
        FavoriteSet reloadedGlobal = Assert.Single(reloaded.OrderedSets);
        Assert.Equal(global.Id, reloadedGlobal.Id);
        Assert.Empty(reloadedGlobal.FavoriteIds);
    }

    [Fact]
    public void Membership_AddInsertRemove_KeepOrderAndDedupe()
    {
        FavoriteStore store = NewStore();
        FavoriteSet set = store.CreateNamedSet("Order")!;
        FavoriteCommand a = Fav("A");
        FavoriteCommand b = Fav("B");
        FavoriteCommand c = Fav("C");
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
        FavoriteStore store = NewStore();
        FavoriteSet set = store.CreateNamedSet("Pruned")!;
        FavoriteCommand a = Fav("A");
        store.SaveFavorite(a);

        store.ReplaceSetFavorites(set.Id, [a.Id, "deadbeef", a.Id]);

        Assert.Equal([a.Id], store.GetSet(set.Id)!.FavoriteIds);
    }

    [Fact]
    public void DeleteFavorite_RemovesEntityFromEverySet()
    {
        FavoriteStore store = NewStore();
        FavoriteSet set = store.CreateNamedSet("Holder")!;
        FavoriteCommand favorite = Fav("Everywhere");
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
        FavoriteStore store = NewStore();
        FavoriteSet set = store.CreateNamedSet("Second")!;
        FavoriteCommand favorite = Fav("Shared");
        store.SaveFavorite(favorite);
        store.AddToSet(store.GlobalSet.Id, favorite.Id);
        store.AddToSet(set.Id, favorite.Id);

        List<string> memberships = store.GetMembershipSetIds(favorite.Id);
        Assert.Equal(2, memberships.Count);
        Assert.Contains(store.GlobalSet.Id, memberships);
        Assert.Contains(set.Id, memberships);
    }

    [Fact]
    public void ComposeDisplay_OrdersGlobalAirportScenarioThenLoadedSets()
    {
        FavoriteStore store = NewStore();
        FavoriteSet airport = store.GetOrCreateAirportSet("oak");
        FavoriteSet scenario = store.GetOrCreateScenarioSet("SCN-1", "Practice");
        FavoriteSet named = store.CreateNamedSet("Extras")!;

        FavoriteCommand g = Fav("G");
        FavoriteCommand a = Fav("A");
        FavoriteCommand s = Fav("S");
        FavoriteCommand n = Fav("N");
        foreach (FavoriteCommand? fav in new[] { g, a, s, n })
        {
            store.SaveFavorite(fav);
        }
        store.AddToSet(store.GlobalSet.Id, g.Id);
        store.AddToSet(airport.Id, a.Id);
        store.AddToSet(scenario.Id, s.Id);
        store.AddToSet(named.Id, n.Id);

        List<FavoriteDisplayEntry> display = store.ComposeDisplay("SCN-1", "OAK", [named.Id]);
        Assert.Equal(["G", "A", "S", "N"], display.Select(e => e.Favorite.Label));

        List<FavoriteDisplayEntry> withoutContext = store.ComposeDisplay(null, null, []);
        Assert.Equal(["G"], withoutContext.Select(e => e.Favorite.Label));

        List<FavoriteDisplayEntry> unknownLoadedId = store.ComposeDisplay(null, null, ["deadbeef"]);
        Assert.Equal(["G"], unknownLoadedId.Select(e => e.Favorite.Label));
    }

    [Fact]
    public void ComposeDisplay_ShowsSharedFavoriteOncePerVisibleContainer()
    {
        FavoriteStore store = NewStore();
        FavoriteSet named = store.CreateNamedSet("Both")!;
        FavoriteCommand favorite = Fav("Twice");
        store.SaveFavorite(favorite);
        store.AddToSet(store.GlobalSet.Id, favorite.Id);
        store.AddToSet(named.Id, favorite.Id);

        List<FavoriteDisplayEntry> display = store.ComposeDisplay(null, null, [named.Id]);

        Assert.Equal(2, display.Count);
        Assert.All(display, e => Assert.Same(favorite, e.Favorite));
        Assert.Equal([store.GlobalSet.Id, named.Id], display.Select(e => e.SetId));
    }

    [Fact]
    public void GetOrCreateScenarioSet_RefreshesDisplayName()
    {
        FavoriteStore store = NewStore();
        FavoriteSet created = store.GetOrCreateScenarioSet("SCN-9", "SCN-9");

        FavoriteSet refreshed = store.GetOrCreateScenarioSet("SCN-9", "Friendly Name");

        Assert.Equal(created.Id, refreshed.Id);
        Assert.Equal("Scenario (Friendly Name)", refreshed.DisplayName);
    }

    [Fact]
    public void Load_PrunesMembershipOfMissingFavoriteFiles()
    {
        FavoriteStore store = NewStore();
        FavoriteCommand keep = Fav("Keep");
        FavoriteCommand lost = Fav("Lost");
        store.SaveFavorite(keep);
        store.SaveFavorite(lost);
        store.AddToSet(store.GlobalSet.Id, keep.Id);
        store.AddToSet(store.GlobalSet.Id, lost.Id);

        File.Delete(Path.Combine(_root, "commands", $"Lost.{lost.Id}.json"));

        FavoriteStore reloaded = NewStore();
        Assert.Equal(["Keep"], reloaded.GetSetFavorites(reloaded.GlobalSet.Id).Select(f => f.Label));
    }

    [Fact]
    public void OrderedSets_SortGlobalAirportsScenariosThenNamed()
    {
        FavoriteStore store = NewStore();
        store.CreateNamedSet("Zulu");
        store.CreateNamedSet("alpha");
        store.GetOrCreateAirportSet("SFO");
        store.GetOrCreateAirportSet("OAK");
        store.GetOrCreateScenarioSet("SCN-2", "Bravo");

        Assert.Equal(["Global", "Airport (OAK)", "Airport (SFO)", "Scenario (Bravo)", "alpha", "Zulu"], store.OrderedSets.Select(s => s.DisplayName));
    }

    [Theory]
    [InlineData("buttonWidth")]
    [InlineData("ButtonWidth")]
    public void RetiredButtonWidthField_IsIgnoredOnLoad_AndDroppedOnRewrite(string fieldName)
    {
        string commandsDir = Path.Combine(_root, "commands");
        Directory.CreateDirectory(commandsDir);
        string path = Path.Combine(commandsDir, "Legacy.0123abcd.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "id": "0123abcd",
              "isSpacer": false,
              "label": "Legacy",
              "commandText": "FH 270",
              "groundCommandText": "",
              "category": "Ground",
              "backgroundColor": "#112233",
              "textColor": "#445566",
              "{{fieldName}}": 150,
              "buttonHeight": 45
            }
            """
        );

        FavoriteStore store = NewStore();
        FavoriteCommand? favorite = store.GetFavorite("0123abcd");

        Assert.NotNull(favorite);
        Assert.Equal("Legacy", favorite.Label);
        Assert.Equal("FH 270", favorite.CommandText);
        Assert.Equal(FavoriteCommandCategory.Ground, favorite.Category);
        Assert.Equal(45, favorite.ButtonHeight);

        // The retired width field is unknown on read, so a rewrite of the entity drops it.
        store.SaveFavorite(favorite);
        Assert.DoesNotContain("buttonWidth", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }
}
