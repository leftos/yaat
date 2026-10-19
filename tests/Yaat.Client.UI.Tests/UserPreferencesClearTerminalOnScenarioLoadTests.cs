using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a per-process temp
// directory. A fresh UserPreferences instance proves the disk round-trip. Tests share that one file,
// so every mutating test restores the factory default in a finally.
public class UserPreferencesClearTerminalOnScenarioLoadTests
{
    [Fact]
    public void ClearTerminalOnScenarioLoad_DefaultsOff_AndRoundTrips()
    {
        var prefs = new UserPreferences();
        Assert.False(prefs.ClearTerminalOnScenarioLoad);
        Assert.False(UserPreferences.CreateDefaults().ClearTerminalOnScenarioLoad);

        try
        {
            prefs.SetClearTerminalOnScenarioLoad(true);

            Assert.True(prefs.ClearTerminalOnScenarioLoad);
            Assert.True(new UserPreferences().ClearTerminalOnScenarioLoad);
        }
        finally
        {
            prefs.SetClearTerminalOnScenarioLoad(false);
        }

        Assert.False(new UserPreferences().ClearTerminalOnScenarioLoad);
    }
}
