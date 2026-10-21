using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to this test's own preferences file. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesFavoritesBarTests
{
    [Fact]
    public void ShowFavoritesBar_DefaultsTrue()
    {
        var prefs = new UserPreferences();

        Assert.True(prefs.ShowFavoritesBar);
    }

    [Fact]
    public void SetShowFavoritesBar_PersistsToDisk()
    {
        var prefs = new UserPreferences();
        prefs.SetShowFavoritesBar(false);

        Assert.False(prefs.ShowFavoritesBar);
        Assert.False(new UserPreferences().ShowFavoritesBar);
    }

    [Fact]
    public void IsFavoritesPanelOpen_DefaultsFalse()
    {
        var prefs = new UserPreferences();

        Assert.False(prefs.IsFavoritesPanelOpen);
    }

    [Fact]
    public void SetFavoritesPanelOpen_PersistsToDisk()
    {
        var prefs = new UserPreferences();
        prefs.SetFavoritesPanelOpen(true);

        Assert.True(prefs.IsFavoritesPanelOpen);
        Assert.True(new UserPreferences().IsFavoritesPanelOpen);
    }
}
