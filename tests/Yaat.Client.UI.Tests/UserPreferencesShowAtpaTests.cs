using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to this test's own preferences file. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesShowAtpaTests
{
    [Fact]
    public void ShowAtpa_DefaultsFalse()
    {
        var prefs = new UserPreferences();

        Assert.False(prefs.ShowAtpa);
    }

    [Fact]
    public void SetShowAtpa_PersistsToDisk()
    {
        var prefs = new UserPreferences();
        prefs.SetShowAtpa(true);

        Assert.True(prefs.ShowAtpa);
        Assert.True(new UserPreferences().ShowAtpa);
    }
}
