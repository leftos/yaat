using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a per-process temp
// directory. A fresh UserPreferences instance proves the disk round-trip. Tests share that one file,
// so every mutating test restores the factory default in a finally.
public class UserPreferencesDiscordRichPresenceTests
{
    [Fact]
    public void DiscordRichPresenceEnabled_DefaultsOn_AndRoundTrips()
    {
        var prefs = new UserPreferences();
        Assert.True(prefs.DiscordRichPresenceEnabled);

        try
        {
            prefs.SetDiscordRichPresenceEnabled(false);

            Assert.False(prefs.DiscordRichPresenceEnabled);
            Assert.False(new UserPreferences().DiscordRichPresenceEnabled);
        }
        finally
        {
            prefs.SetDiscordRichPresenceEnabled(true);
        }

        Assert.True(new UserPreferences().DiscordRichPresenceEnabled);
    }
}
