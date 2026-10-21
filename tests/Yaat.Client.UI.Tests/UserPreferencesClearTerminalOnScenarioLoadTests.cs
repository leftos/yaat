using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to this test's own preferences file. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesClearTerminalOnScenarioLoadTests
{
    [Fact]
    public void ClearTerminalOnScenarioLoad_DefaultsOff_AndRoundTrips()
    {
        var prefs = new UserPreferences();
        Assert.False(prefs.ClearTerminalOnScenarioLoad);
        Assert.False(UserPreferences.CreateDefaults().ClearTerminalOnScenarioLoad);

        prefs.SetClearTerminalOnScenarioLoad(true);

        Assert.True(prefs.ClearTerminalOnScenarioLoad);
        Assert.True(new UserPreferences().ClearTerminalOnScenarioLoad);
    }
}
