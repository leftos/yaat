using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to this test's own preferences file. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesDiscordRichPresenceTests
{
    [Fact]
    public void DiscordRichPresenceEnabled_DefaultsOn_AndRoundTrips()
    {
        var prefs = new UserPreferences();
        Assert.True(prefs.DiscordRichPresenceEnabled);

        prefs.SetDiscordRichPresenceEnabled(false);

        Assert.False(prefs.DiscordRichPresenceEnabled);
        Assert.False(new UserPreferences().DiscordRichPresenceEnabled);
    }
}
