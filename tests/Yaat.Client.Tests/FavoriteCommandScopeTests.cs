using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

public class FavoriteCommandScopeTests
{
    [Fact]
    public void GlobalFavorite_IsAlwaysVisible()
    {
        var favorite = new FavoriteCommand();

        Assert.True(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: null, activeAirportId: null));
        Assert.True(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: "SCN1", activeAirportId: "OAK"));
    }

    [Fact]
    public void ScenarioFavorite_OnlyMatchesActiveScenario()
    {
        var favorite = new FavoriteCommand { ScenarioId = "SCN1" };

        Assert.True(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: "SCN1", activeAirportId: "OAK"));
        Assert.False(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: "SCN2", activeAirportId: "OAK"));
        Assert.False(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: null, activeAirportId: "OAK"));
    }

    [Fact]
    public void AirportFavorite_OnlyMatchesActivePrimaryAirport()
    {
        var favorite = new FavoriteCommand { AirportId = "OAK" };

        Assert.True(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: "SCN1", activeAirportId: "oak"));
        Assert.False(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: "SCN1", activeAirportId: "SFO"));
        Assert.False(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: "SCN1", activeAirportId: null));
    }

    [Fact]
    public void ScenarioScope_TakesPrecedenceOverAirportScope_WhenBothArePresent()
    {
        var favorite = new FavoriteCommand { ScenarioId = "SCN1", AirportId = "SFO" };

        Assert.True(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: "SCN1", activeAirportId: "OAK"));
        Assert.False(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: "SCN2", activeAirportId: "SFO"));
    }

    [Fact]
    public void SpacerFavorite_UsesSameAirportScopeFiltering()
    {
        var favorite = new FavoriteCommand { IsSpacer = true, AirportId = "FLL" };

        Assert.True(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: "SCN1", activeAirportId: "fll"));
        Assert.False(MainViewModel.IsFavoriteVisible(favorite, activeScenarioId: "SCN1", activeAirportId: "MIA"));
    }
}
