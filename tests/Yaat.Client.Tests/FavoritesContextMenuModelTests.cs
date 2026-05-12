using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views;

namespace Yaat.Client.Tests;

public class FavoritesContextMenuModelTests
{
    private static AircraftModel Airborne() => new() { Callsign = "UAL238", IsOnGround = false };

    private static AircraftModel OnGround() => new() { Callsign = "N172SP", IsOnGround = true };

    [Fact]
    public void Airborne_ResolvesAirCommandText()
    {
        var fav = new FavoriteCommand
        {
            Label = "Climb",
            CommandText = "CM 230",
            GroundCommandText = "",
        };

        var result = FavoritesContextMenuModel.Build([fav], Airborne());

        var entry = Assert.Single(result);
        Assert.False(entry.IsSpacer);
        Assert.Equal("Climb", entry.Label);
        Assert.Equal("CM 230", entry.CommandText);
    }

    [Fact]
    public void Ground_PrefersGroundCommandText_WhenSet()
    {
        var fav = new FavoriteCommand
        {
            Label = "Taxi via A",
            CommandText = "ignored-air",
            GroundCommandText = "TAXI A",
        };

        var result = FavoritesContextMenuModel.Build([fav], OnGround());

        var entry = Assert.Single(result);
        Assert.Equal("TAXI A", entry.CommandText);
    }

    [Fact]
    public void Ground_FallsBackToAirCommandText_WhenGroundEmpty()
    {
        var fav = new FavoriteCommand
        {
            Label = "Squawk VFR",
            CommandText = "SQVFR",
            GroundCommandText = "",
        };

        var result = FavoritesContextMenuModel.Build([fav], OnGround());

        var entry = Assert.Single(result);
        Assert.Equal("SQVFR", entry.CommandText);
    }

    [Fact]
    public void Airborne_SkipsFavorite_WhenOnlyGroundTextSet()
    {
        var fav = new FavoriteCommand
        {
            Label = "Push",
            CommandText = "",
            GroundCommandText = "PUSH",
        };

        var result = FavoritesContextMenuModel.Build([fav], Airborne());

        Assert.Empty(result);
    }

    [Fact]
    public void SpacerBetweenFavorites_IsPreserved()
    {
        FavoriteCommand[] favs = [new() { Label = "A", CommandText = "AA" }, new() { IsSpacer = true }, new() { Label = "B", CommandText = "BB" }];

        var result = FavoritesContextMenuModel.Build(favs, Airborne());

        Assert.Collection(result, e => Assert.Equal("A", e.Label), e => Assert.True(e.IsSpacer), e => Assert.Equal("B", e.Label));
    }

    [Fact]
    public void LeadingAndTrailingSpacers_AreStripped()
    {
        FavoriteCommand[] favs =
        [
            new() { IsSpacer = true },
            new() { IsSpacer = true },
            new() { Label = "A", CommandText = "AA" },
            new() { IsSpacer = true },
        ];

        var result = FavoritesContextMenuModel.Build(favs, Airborne());

        var entry = Assert.Single(result);
        Assert.Equal("A", entry.Label);
    }

    [Fact]
    public void ConsecutiveSpacers_AreCollapsed()
    {
        FavoriteCommand[] favs =
        [
            new() { Label = "A", CommandText = "AA" },
            new() { IsSpacer = true },
            new() { IsSpacer = true },
            new() { IsSpacer = true },
            new() { Label = "B", CommandText = "BB" },
        ];

        var result = FavoritesContextMenuModel.Build(favs, Airborne());

        Assert.Collection(result, e => Assert.Equal("A", e.Label), e => Assert.True(e.IsSpacer), e => Assert.Equal("B", e.Label));
    }

    [Fact]
    public void EmptyLabel_FallsBackToCommandText()
    {
        var fav = new FavoriteCommand { Label = "", CommandText = "CM 230" };

        var result = FavoritesContextMenuModel.Build([fav], Airborne());

        var entry = Assert.Single(result);
        Assert.Equal("CM 230", entry.Label);
    }

    [Fact]
    public void NullAircraft_TreatsFavoritesAsAirborne()
    {
        FavoriteCommand[] favs =
        [
            new() { Label = "Air", CommandText = "AIR" },
            new()
            {
                Label = "Ground",
                CommandText = "",
                GroundCommandText = "GND",
            },
        ];

        var result = FavoritesContextMenuModel.Build(favs, aircraft: null);

        var entry = Assert.Single(result);
        Assert.Equal("Air", entry.Label);
    }

    [Fact]
    public void DroppedFavorite_DoesNotLeaveDanglingSeparator()
    {
        FavoriteCommand[] favs =
        [
            new() { Label = "A", CommandText = "AA" },
            new() { IsSpacer = true },
            new()
            {
                Label = "GroundOnly",
                CommandText = "",
                GroundCommandText = "GND",
            },
        ];

        var result = FavoritesContextMenuModel.Build(favs, Airborne());

        var entry = Assert.Single(result);
        Assert.Equal("A", entry.Label);
    }
}
