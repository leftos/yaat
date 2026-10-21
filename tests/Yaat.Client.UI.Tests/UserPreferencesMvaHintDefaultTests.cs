using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to this test's own preferences file. A fresh UserPreferences instance proves the disk round-trip.
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
}
