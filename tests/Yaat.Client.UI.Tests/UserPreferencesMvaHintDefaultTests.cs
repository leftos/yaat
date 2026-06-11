using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a per-process temp
// directory. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesMvaHintDefaultTests
{
    [Theory]
    [InlineData("APP", true)]
    [InlineData("CTR", true)]
    [InlineData("GND", false)]
    [InlineData("TWR", false)]
    [InlineData("app", true)] // case-insensitive
    [InlineData(null, false)] // unrecognized/absent position type → no hint by default
    [InlineData("OBS", false)]
    public void GetMvaHintDefault_DefaultsByPositionType(string? positionType, bool expected)
    {
        var prefs = new UserPreferences();

        Assert.Equal(expected, prefs.GetMvaHintDefault(positionType));
    }

    [Fact]
    public void SetMvaHintDefaults_PersistsPerType()
    {
        var prefs = new UserPreferences();
        try
        {
            // Invert every default so a stale-default read would fail.
            prefs.SetMvaHintDefaults(app: false, ctr: false, gnd: true, twr: true);

            Assert.False(prefs.GetMvaHintDefault("APP"));
            Assert.False(prefs.GetMvaHintDefault("CTR"));
            Assert.True(prefs.GetMvaHintDefault("GND"));
            Assert.True(prefs.GetMvaHintDefault("TWR"));

            // Fresh instance reads preferences.json from disk, proving persistence.
            var reader = new UserPreferences();
            Assert.False(reader.GetMvaHintDefault("APP"));
            Assert.False(reader.GetMvaHintDefault("CTR"));
            Assert.True(reader.GetMvaHintDefault("GND"));
            Assert.True(reader.GetMvaHintDefault("TWR"));
        }
        finally
        {
            // Tests share one per-process preferences.json; restore factory defaults so the
            // order-independent defaults test above never reads these inverted values.
            prefs.SetMvaHintDefaults(app: true, ctr: true, gnd: false, twr: false);
        }
    }
}
