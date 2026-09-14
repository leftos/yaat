using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a per-process temp
// directory. A fresh UserPreferences instance proves the disk round-trip. Tests share that one file,
// so every mutating test restores the factory default in a finally.
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
        try
        {
            prefs.SetShowAtpa(true);

            Assert.True(prefs.ShowAtpa);
            Assert.True(new UserPreferences().ShowAtpa);
        }
        finally
        {
            prefs.SetShowAtpa(false);
        }
    }
}
