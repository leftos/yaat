using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// UserPreferences writes to YaatPaths.AppDataRoot, which ModuleInit redirects to a per-process temp
// directory. A fresh UserPreferences instance proves the disk round-trip.
public class UserPreferencesAutoArrivalSpacingTests
{
    [Theory]
    [InlineData("GND", true)]
    [InlineData("TWR", true)]
    [InlineData("APP", false)] // the student is the approach controller — nobody left to do the spacing
    [InlineData("CTR", false)]
    [InlineData("gnd", true)] // case-insensitive
    [InlineData(null, true)] // no student position (an RPO-only room) reads the tower value
    [InlineData("OBS", true)]
    public void GetAutoArrivalSpacingOnOccupiedRunway_DefaultsByPositionType(string? positionType, bool expected)
    {
        var prefs = new UserPreferences();

        Assert.Equal(expected, prefs.GetAutoArrivalSpacingOnOccupiedRunway(positionType));
    }

    [Fact]
    public void SetSimulationShortcuts_PersistsAutoArrivalSpacingPerType()
    {
        var prefs = new UserPreferences();
        try
        {
            // Invert both defaults so a stale-default read would fail, one at a time so the two keys cannot be
            // satisfied by a single shared field.
            SetAutoArrivalSpacing(prefs, gnd: false, twr: true);

            Assert.False(prefs.GetAutoArrivalSpacingOnOccupiedRunway("GND"));
            Assert.True(prefs.GetAutoArrivalSpacingOnOccupiedRunway("TWR"));

            SetAutoArrivalSpacing(prefs, gnd: false, twr: false);

            // Fresh instance reads preferences.json from disk, proving persistence.
            var reader = new UserPreferences();
            Assert.False(reader.GetAutoArrivalSpacingOnOccupiedRunway("GND"));
            Assert.False(reader.GetAutoArrivalSpacingOnOccupiedRunway("TWR"));
            Assert.False(reader.GetAutoArrivalSpacingOnOccupiedRunway(null));
        }
        finally
        {
            // Tests share one per-process preferences.json; restore factory defaults so the order-independent
            // defaults test above never reads these inverted values.
            SetAutoArrivalSpacing(prefs, gnd: true, twr: true);
        }
    }

    /// <summary>
    /// Writes the two auto arrival spacing keys through the shared simulation-shortcuts setter, passing every other
    /// shortcut back unchanged so the round-trip cannot disturb the preferences the rest of the suite reads.
    /// </summary>
    private static void SetAutoArrivalSpacing(UserPreferences prefs, bool gnd, bool twr) =>
        prefs.SetSimulationShortcuts(
            prefs.AutoClearedToLandGnd,
            prefs.AutoClearedToLandTwr,
            prefs.AutoClearedToLandApp,
            prefs.AutoClearedToLandCtr,
            prefs.AutoCrossRunway,
            prefs.AutoPullUpToParallel,
            prefs.AutoGoAroundOnOccupiedRunway,
            prefs.AutoRejectTakeoffOnOccupiedRunway,
            gnd,
            twr
        );
}
