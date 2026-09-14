using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a per-process temp
// directory. A fresh UserPreferences instance proves the disk round-trip. Tests share that one file,
// so every mutating test restores the factory default in a finally.
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
        try
        {
            prefs.SetShowFavoritesBar(false);

            Assert.False(prefs.ShowFavoritesBar);
            Assert.False(new UserPreferences().ShowFavoritesBar);
        }
        finally
        {
            prefs.SetShowFavoritesBar(true);
        }
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
        try
        {
            prefs.SetFavoritesPanelOpen(true);

            Assert.True(prefs.IsFavoritesPanelOpen);
            Assert.True(new UserPreferences().IsFavoritesPanelOpen);
        }
        finally
        {
            prefs.SetFavoritesPanelOpen(false);
        }
    }
}
