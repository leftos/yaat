using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

public class FavoriteSetTests
{
    [Fact]
    public void Compose_AppendsLoadedSetFavorites_AfterScopeFilteredBasePool()
    {
        var basePool = new List<FavoriteCommand>
        {
            Fav("Global"),
            new FavoriteCommand
            {
                Label = "OtherAirport",
                CommandText = "X",
                AirportId = "SFO",
            },
        };
        var sets = new List<FavoriteCommandSet>
        {
            new() { Name = "S1", Favorites = [Fav("SetA"), Fav("SetB")] },
        };

        var composed = MainViewModel.ComposeDisplayFavorites(basePool, sets, ["S1"], null, "OAK");

        Assert.Equal(["Global", "SetA", "SetB"], composed.Select(f => f.Label));
    }

    [Fact]
    public void Compose_ShowsSetFavorites_RegardlessOfTheirScope()
    {
        var sets = new List<FavoriteCommandSet>
        {
            new()
            {
                Name = "S1",
                Favorites =
                [
                    new FavoriteCommand
                    {
                        Label = "ScopedToOtherScenario",
                        CommandText = "X",
                        ScenarioId = "SCN-OTHER",
                    },
                    new FavoriteCommand
                    {
                        Label = "ScopedToOtherAirport",
                        CommandText = "X",
                        AirportId = "SFO",
                    },
                ],
            },
        };

        var composed = MainViewModel.ComposeDisplayFavorites([], sets, ["S1"], "SCN-1", "OAK");

        Assert.Equal(["ScopedToOtherScenario", "ScopedToOtherAirport"], composed.Select(f => f.Label));
    }

    [Fact]
    public void Compose_FollowsLoadOrder_NotSetStoreOrder()
    {
        var sets = new List<FavoriteCommandSet>
        {
            new() { Name = "A", Favorites = [Fav("FromA")] },
            new() { Name = "B", Favorites = [Fav("FromB")] },
        };

        var composed = MainViewModel.ComposeDisplayFavorites([], sets, ["B", "A"], null, null);

        Assert.Equal(["FromB", "FromA"], composed.Select(f => f.Label));
    }

    [Fact]
    public void Compose_SkipsUnloadedSets_AndUnknownLoadedNames()
    {
        var sets = new List<FavoriteCommandSet>
        {
            new() { Name = "Loaded", Favorites = [Fav("Shown")] },
            new() { Name = "Unloaded", Favorites = [Fav("Hidden")] },
        };

        var composed = MainViewModel.ComposeDisplayFavorites([], sets, ["Loaded", "Ghost"], null, null);

        Assert.Equal(["Shown"], composed.Select(f => f.Label));
    }

    [Fact]
    public void MergeSets_Append_MergesSameNameCaseInsensitively_AndAddsNewSets()
    {
        var existing = new List<FavoriteCommandSet>
        {
            new() { Name = "S1", Favorites = [Fav("Old")] },
        };
        var imported = new List<FavoriteCommandSet>
        {
            new() { Name = "s1", Favorites = [Fav("New")] },
            new() { Name = "S2", Favorites = [Fav("Fresh")] },
        };

        var merged = MainViewModel.MergeImportedFavoriteSets(existing, imported, FavoriteImportMode.Append);

        Assert.Equal(2, merged.Count);
        var s1 = merged.Single(s => string.Equals(s.Name, "S1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Old", "New"], s1.Favorites.Select(f => f.Label));
        Assert.Equal(["Fresh"], merged.Single(s => s.Name == "S2").Favorites.Select(f => f.Label));
    }

    [Fact]
    public void MergeSets_Append_DoesNotMutateExistingStore()
    {
        var existingSet = new FavoriteCommandSet { Name = "S1", Favorites = [Fav("Old")] };
        var imported = new List<FavoriteCommandSet>
        {
            new() { Name = "S1", Favorites = [Fav("New")] },
        };

        MainViewModel.MergeImportedFavoriteSets([existingSet], imported, FavoriteImportMode.Append);

        Assert.Equal(["Old"], existingSet.Favorites.Select(f => f.Label));
    }

    [Fact]
    public void MergeSets_Replace_KeepsOnlyImported()
    {
        var existing = new List<FavoriteCommandSet>
        {
            new() { Name = "S1", Favorites = [Fav("Old")] },
        };
        var imported = new List<FavoriteCommandSet>
        {
            new() { Name = "S2", Favorites = [Fav("New")] },
        };

        var merged = MainViewModel.MergeImportedFavoriteSets(existing, imported, FavoriteImportMode.Replace);

        Assert.Equal(["S2"], merged.Select(s => s.Name));
    }

    [Fact]
    public void MergeSets_DropsBlankNamedSets_AndNullFavorites()
    {
        var imported = new List<FavoriteCommandSet>
        {
            new() { Name = " ", Favorites = [Fav("Dropped")] },
            null!,
            new() { Name = "Kept", Favorites = [Fav("A"), null!] },
        };

        var merged = MainViewModel.MergeImportedFavoriteSets([], imported, FavoriteImportMode.Append);

        var kept = Assert.Single(merged);
        Assert.Equal("Kept", kept.Name);
        Assert.Equal(["A"], kept.Favorites.Select(f => f.Label));
    }

    [Fact]
    public void Normalize_DropsLoadedNamesWithoutMatchingSet()
    {
        var sets = new List<FavoriteCommandSet> { Set("S1") };
        var loaded = new List<string> { "S1", "Gone" };

        UserPreferences.NormalizeFavoriteSets(sets, loaded);

        Assert.Equal(["S1"], loaded);
    }

    [Fact]
    public void Normalize_DedupesLoadedNames_CaseInsensitively_KeepingFirstOccurrence()
    {
        var sets = new List<FavoriteCommandSet> { Set("S1"), Set("S2") };
        var loaded = new List<string> { "S2", "s2", "S1" };

        UserPreferences.NormalizeFavoriteSets(sets, loaded);

        Assert.Equal(["S2", "S1"], loaded);
    }

    [Fact]
    public void Normalize_DropsBlankNamedSets_AndSortsByName()
    {
        var sets = new List<FavoriteCommandSet> { Set("Zulu"), Set("  "), Set("alpha") };
        var loaded = new List<string>();

        UserPreferences.NormalizeFavoriteSets(sets, loaded);

        Assert.Equal(["alpha", "Zulu"], sets.Select(s => s.Name));
    }

    [Fact]
    public void Normalize_PreservesLoadOrder_NotSetSortOrder()
    {
        var sets = new List<FavoriteCommandSet> { Set("A"), Set("B"), Set("C") };
        var loaded = new List<string> { "C", "A" };

        UserPreferences.NormalizeFavoriteSets(sets, loaded);

        Assert.Equal(["C", "A"], loaded);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var sets = new List<FavoriteCommandSet> { Set("B"), Set("a") };
        var loaded = new List<string> { "B", "a" };

        UserPreferences.NormalizeFavoriteSets(sets, loaded);
        var namesAfterFirst = sets.Select(s => s.Name).ToList();
        var loadedAfterFirst = loaded.ToList();

        UserPreferences.NormalizeFavoriteSets(sets, loaded);

        Assert.Equal(namesAfterFirst, sets.Select(s => s.Name));
        Assert.Equal(loadedAfterFirst, loaded);
    }

    [Fact]
    public void Normalize_MatchesLoadedNamesAgainstSets_IgnoringCase()
    {
        var sets = new List<FavoriteCommandSet> { Set("S1 Training") };
        var loaded = new List<string> { " s1 training " };

        UserPreferences.NormalizeFavoriteSets(sets, loaded);

        Assert.Equal(["s1 training"], loaded);
    }

    [Fact]
    public void Clone_ProducesIndependentCopy()
    {
        var original = new FavoriteCommand
        {
            Label = "T3 B",
            CommandText = "T T3 B",
            GroundCommandText = "TAXI T3 B",
            ScenarioId = "SCN-123",
            AirportId = "FLL",
            Category = FavoriteCommandCategory.Ground,
            BackgroundColor = "#112233",
            TextColor = "#445566",
            ButtonWidth = 123,
            ButtonHeight = 45,
        };

        var clone = original.Clone();
        clone.Label = "Changed";
        clone.CommandText = "Changed";

        Assert.NotSame(original, clone);
        Assert.Equal("T3 B", original.Label);
        Assert.Equal("T T3 B", original.CommandText);
        Assert.Equal(original.GroundCommandText, clone.GroundCommandText);
        Assert.Equal(original.ScenarioId, clone.ScenarioId);
        Assert.Equal(original.AirportId, clone.AirportId);
        Assert.Equal(original.Category, clone.Category);
        Assert.Equal(original.BackgroundColor, clone.BackgroundColor);
        Assert.Equal(original.TextColor, clone.TextColor);
        Assert.Equal(original.ButtonWidth, clone.ButtonWidth);
        Assert.Equal(original.ButtonHeight, clone.ButtonHeight);
    }

    [Fact]
    public void Parse_ArrayRoot_IsFlatPayload()
    {
        var payload = FavoriteSetSerialization.Parse("""[{"label":"A","commandText":"A"}]""");

        Assert.NotNull(payload);
        Assert.Null(payload.Bundle);
        Assert.NotNull(payload.FlatFavorites);
        Assert.Equal("A", Assert.Single(payload.FlatFavorites).Label);
    }

    [Fact]
    public void Parse_ObjectRoot_IsBundlePayload()
    {
        var json = """
            {
              "version": 1,
              "loadedSetNames": ["S1"],
              "sets": [ { "name": "S1", "favorites": [ { "label": "A", "commandText": "A" } ] } ]
            }
            """;

        var payload = FavoriteSetSerialization.Parse(json);

        Assert.NotNull(payload);
        Assert.Null(payload.FlatFavorites);
        Assert.NotNull(payload.Bundle);
        Assert.Equal(["S1"], payload.Bundle.LoadedSetNames);
        var set = Assert.Single(payload.Bundle.Sets);
        Assert.Equal("S1", set.Name);
        Assert.Equal("A", Assert.Single(set.Favorites).Label);
    }

    [Fact]
    public void Parse_RejectsGarbage_EmptyObject_AndNonJson()
    {
        Assert.Null(FavoriteSetSerialization.Parse("not json"));
        Assert.Null(FavoriteSetSerialization.Parse("{}"));
        Assert.Null(FavoriteSetSerialization.Parse("42"));
        Assert.Null(FavoriteSetSerialization.Parse("""{"sets": []}"""));
    }

    [Fact]
    public void SerializeBundle_RoundTrips_ThroughParse()
    {
        var sets = new List<FavoriteCommandSet>
        {
            new()
            {
                Name = "S1 Training",
                Favorites =
                [
                    new FavoriteCommand
                    {
                        Label = "A",
                        CommandText = "A",
                        AirportId = "OAK",
                    },
                ],
            },
            new() { Name = "S2", Favorites = [] },
        };

        var json = FavoriteSetSerialization.SerializeBundle(sets, ["S1 Training"]);
        var payload = FavoriteSetSerialization.Parse(json);

        Assert.NotNull(payload);
        Assert.NotNull(payload.Bundle);
        Assert.Equal(["S1 Training"], payload.Bundle.LoadedSetNames);
        Assert.Equal(["S1 Training", "S2"], payload.Bundle.Sets.Select(s => s.Name));
        Assert.Equal("OAK", Assert.Single(payload.Bundle.Sets[0].Favorites).AirportId);
    }

    private static FavoriteCommandSet Set(string name) => new() { Name = name };

    private static FavoriteCommand Fav(string label) => new() { Label = label, CommandText = label };
}
