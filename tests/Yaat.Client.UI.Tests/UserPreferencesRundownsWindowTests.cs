using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to this test's own preferences file. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesRundownsWindowTests
{
    [Fact]
    public void IsRundownsWindowOpen_DefaultsFalse()
    {
        var prefs = new UserPreferences();

        Assert.False(prefs.IsRundownsWindowOpen);
    }

    [Fact]
    public void SetRundownsWindowOpen_PersistsToDisk()
    {
        var prefs = new UserPreferences();
        prefs.SetRundownsWindowOpen(true);

        Assert.True(prefs.IsRundownsWindowOpen);
        Assert.True(new UserPreferences().IsRundownsWindowOpen);
    }
}
