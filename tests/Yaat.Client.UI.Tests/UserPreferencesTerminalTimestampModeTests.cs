using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Covers the terminal timestamp-mode preference (wall-clock / sim-elapsed / both) added for the
// terminal-scrub feature. UserPreferences writes to YaatPaths.AppDataRoot, redirected by ModuleInit
// to a per-process temp dir; a fresh instance proves the disk round-trip. The mutating test restores
// the default so the Defaults test stays order-tolerant against the shared preferences.json.
public class UserPreferencesTerminalTimestampModeTests
{
    [Fact]
    public void Default_IsWallClock()
    {
        Assert.Equal(TerminalTimestampMode.WallClock, new UserPreferences().TerminalTimestampMode);
    }

    [Fact]
    public void SetTerminalTimestampMode_PersistsAcrossInstances()
    {
        var prefs = new UserPreferences();

        prefs.SetTerminalTimestampMode(TerminalTimestampMode.Both);
        Assert.Equal(TerminalTimestampMode.Both, prefs.TerminalTimestampMode);
        Assert.Equal(TerminalTimestampMode.Both, new UserPreferences().TerminalTimestampMode);

        prefs.SetTerminalTimestampMode(TerminalTimestampMode.SimElapsed);
        Assert.Equal(TerminalTimestampMode.SimElapsed, new UserPreferences().TerminalTimestampMode);

        // Restore the default so the Defaults test is order-independent.
        prefs.SetTerminalTimestampMode(TerminalTimestampMode.WallClock);
        Assert.Equal(TerminalTimestampMode.WallClock, new UserPreferences().TerminalTimestampMode);
    }
}
