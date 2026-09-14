using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// The open extra Radar/Ground view windows persist as plain ordinal lists (≥ 2; the docked view is #1),
// so the next launch can reopen exactly those windows and find their geometry under "RadarView#n".
// UserPreferences writes to YaatPaths.AppDataRoot, redirected to a per-process temp dir by ModuleInit;
// a fresh instance proves the disk round-trip and every mutating test restores the default in a finally.
public class UserPreferencesExtraViewOrdinalsTests
{
    [Fact]
    public void ExtraViewOrdinals_DefaultEmpty()
    {
        var prefs = new UserPreferences();

        Assert.Empty(prefs.ExtraRadarViewOrdinals);
        Assert.Empty(prefs.ExtraGroundViewOrdinals);
    }

    [Fact]
    public void SetExtraRadarViewOrdinals_PersistsToDisk()
    {
        var prefs = new UserPreferences();
        try
        {
            prefs.SetExtraRadarViewOrdinals([2, 4]);

            Assert.Equal([2, 4], prefs.ExtraRadarViewOrdinals);
            Assert.Equal([2, 4], new UserPreferences().ExtraRadarViewOrdinals);
        }
        finally
        {
            prefs.SetExtraRadarViewOrdinals([]);
        }
    }

    [Fact]
    public void SetExtraGroundViewOrdinals_PersistsToDisk()
    {
        var prefs = new UserPreferences();
        try
        {
            prefs.SetExtraGroundViewOrdinals([3]);

            Assert.Equal([3], prefs.ExtraGroundViewOrdinals);
            Assert.Equal([3], new UserPreferences().ExtraGroundViewOrdinals);
        }
        finally
        {
            prefs.SetExtraGroundViewOrdinals([]);
        }
    }

    [Fact]
    public void SetExtraRadarViewOrdinals_StoresACopy_NotTheCallersList()
    {
        var prefs = new UserPreferences();
        try
        {
            var ordinals = new List<int> { 2 };
            prefs.SetExtraRadarViewOrdinals(ordinals);
            ordinals.Add(3);

            // A later mutation of the caller's list must not sneak an unsaved ordinal into preferences.
            Assert.Equal([2], prefs.ExtraRadarViewOrdinals);
        }
        finally
        {
            prefs.SetExtraRadarViewOrdinals([]);
        }
    }
}
