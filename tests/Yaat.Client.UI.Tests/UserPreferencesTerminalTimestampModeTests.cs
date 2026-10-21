using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Covers the terminal timestamp-mode preference (wall-clock / sim-elapsed / both) added for the
// terminal-scrub feature. UserPreferences writes to this test's own preferences file; a fresh instance
// proves the disk round-trip.
public class UserPreferencesTerminalTimestampModeTests
{
    [Fact]
    public void Default_IsWallClock() => Assert.Equal(TerminalTimestampMode.WallClock, new UserPreferences().TerminalTimestampMode);

    [Fact]
    public void SetTerminalTimestampMode_PersistsAcrossInstances()
    {
        var prefs = new UserPreferences();

        prefs.SetTerminalTimestampMode(TerminalTimestampMode.Both);
        Assert.Equal(TerminalTimestampMode.Both, prefs.TerminalTimestampMode);
        Assert.Equal(TerminalTimestampMode.Both, new UserPreferences().TerminalTimestampMode);

        prefs.SetTerminalTimestampMode(TerminalTimestampMode.SimElapsed);
        Assert.Equal(TerminalTimestampMode.SimElapsed, new UserPreferences().TerminalTimestampMode);
    }
}
