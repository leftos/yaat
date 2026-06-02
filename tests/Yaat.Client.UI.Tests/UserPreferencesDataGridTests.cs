using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, redirected by ModuleInit to a per-process
// temp directory. A fresh UserPreferences instance reads preferences.json from disk and proves
// the round-trip.
public class UserPreferencesDataGridTests
{
    [Fact]
    public void DataGridAlternatingRowColor_DefaultsTrue()
    {
        var prefs = new UserPreferences();

        Assert.True(prefs.DataGridAlternatingRowColor);
    }

    [Fact]
    public void SetDataGridAlternatingRowColor_PersistsAcrossInstances()
    {
        var prefs = new UserPreferences();
        prefs.SetDataGridAlternatingRowColor(false);

        Assert.False(prefs.DataGridAlternatingRowColor);
        Assert.False(new UserPreferences().DataGridAlternatingRowColor);

        prefs.SetDataGridAlternatingRowColor(true);

        Assert.True(prefs.DataGridAlternatingRowColor);
        Assert.True(new UserPreferences().DataGridAlternatingRowColor);
    }
}
