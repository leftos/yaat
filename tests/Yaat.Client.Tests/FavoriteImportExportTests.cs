using System.Text.Json;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

public class FavoriteImportExportTests
{
    [Fact]
    public void Merge_Append_KeepsExistingThenImported_InOrder()
    {
        var existing = new List<FavoriteCommand> { Fav("A"), Fav("B") };
        var imported = new List<FavoriteCommand> { Fav("C"), Fav("D") };

        var merged = MainViewModel.MergeImportedFavorites(existing, imported, FavoriteImportMode.Append);

        Assert.Equal(["A", "B", "C", "D"], merged.Select(f => f.Label));
    }

    [Fact]
    public void Merge_Replace_KeepsOnlyImported()
    {
        var existing = new List<FavoriteCommand> { Fav("A"), Fav("B") };
        var imported = new List<FavoriteCommand> { Fav("C") };

        var merged = MainViewModel.MergeImportedFavorites(existing, imported, FavoriteImportMode.Replace);

        Assert.Equal(["C"], merged.Select(f => f.Label));
    }

    [Fact]
    public void Merge_Replace_WithEmptyImport_ClearsEverything()
    {
        var existing = new List<FavoriteCommand> { Fav("A") };

        var merged = MainViewModel.MergeImportedFavorites(existing, [], FavoriteImportMode.Replace);

        Assert.Empty(merged);
    }

    [Fact]
    public void Merge_Append_WithEmptyImport_LeavesExistingUnchanged()
    {
        var existing = new List<FavoriteCommand> { Fav("A") };

        var merged = MainViewModel.MergeImportedFavorites(existing, [], FavoriteImportMode.Append);

        Assert.Equal(["A"], merged.Select(f => f.Label));
    }

    [Fact]
    public void Merge_DropsNullEntries_FromMalformedFile()
    {
        var existing = new List<FavoriteCommand> { Fav("A"), null! };
        var imported = new List<FavoriteCommand> { Fav("B"), null! };

        var appended = MainViewModel.MergeImportedFavorites(existing, imported, FavoriteImportMode.Append);
        Assert.Equal(["A", "B"], appended.Select(f => f.Label));

        var replaced = MainViewModel.MergeImportedFavorites(existing, imported, FavoriteImportMode.Replace);
        Assert.Equal(["B"], replaced.Select(f => f.Label));
    }

    [Fact]
    public void NormalizeFavoriteCategory_MapsUndefinedToAir()
    {
        Assert.Equal(FavoriteCommandCategory.Ground, MainViewModel.NormalizeFavoriteCategory(FavoriteCommandCategory.Ground));
        Assert.Equal(FavoriteCommandCategory.Air, MainViewModel.NormalizeFavoriteCategory((FavoriteCommandCategory)999));
    }

    [Fact]
    public void JsonRoundTrip_PreservesAllFields_AndUsesSharedFileShape()
    {
        var favorite = new FavoriteCommand
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
        var spacer = new FavoriteCommand { IsSpacer = true, Category = FavoriteCommandCategory.Vehicle };
        var source = new List<FavoriteCommand> { favorite, spacer };

        var json = JsonSerializer.Serialize(source, UserPreferences.JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<List<FavoriteCommand>>(json, UserPreferences.JsonOptions);

        // Exported files must use the same camelCase + string-enum shape as the on-disk
        // favoriteCommands array, so an exported file is import-compatible and vice versa.
        Assert.Contains("\"commandText\"", json);
        Assert.Contains("\"Ground\"", json);

        Assert.NotNull(roundTripped);
        Assert.Equal(2, roundTripped.Count);

        var restored = roundTripped[0];
        Assert.Equal(favorite.Label, restored.Label);
        Assert.Equal(favorite.CommandText, restored.CommandText);
        Assert.Equal(favorite.GroundCommandText, restored.GroundCommandText);
        Assert.Equal(favorite.ScenarioId, restored.ScenarioId);
        Assert.Equal(favorite.AirportId, restored.AirportId);
        Assert.Equal(favorite.Category, restored.Category);
        Assert.Equal(favorite.BackgroundColor, restored.BackgroundColor);
        Assert.Equal(favorite.TextColor, restored.TextColor);
        Assert.Equal(favorite.ButtonWidth, restored.ButtonWidth);
        Assert.Equal(favorite.ButtonHeight, restored.ButtonHeight);
        Assert.False(restored.IsSpacer);

        Assert.True(roundTripped[1].IsSpacer);
        Assert.Equal(FavoriteCommandCategory.Vehicle, roundTripped[1].Category);
    }

    private static FavoriteCommand Fav(string label) => new() { Label = label, CommandText = label };
}
