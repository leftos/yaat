using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests;

// The docked terminal's share of the tabs + terminal height: a preference kept across launches, and a nullable
// field of a saved layout. Each test starts on its own fresh preferences file.
public class UserPreferencesTerminalSplitRatioTests
{
    private static string PreferencesPath => UserPreferences.FilePath;

    [Fact]
    public void TerminalSplitRatio_DefaultsToAQuarter_AndRoundTrips()
    {
        Assert.Equal(0.25, UserPreferences.CreateDefaults().TerminalSplitRatio);

        var prefs = new UserPreferences();
        prefs.SetTerminalSplitRatio(0.4);

        Assert.Equal(0.4, prefs.TerminalSplitRatio);
        Assert.Equal(0.4, new UserPreferences().TerminalSplitRatio);
    }

    [Theory]
    [InlineData(0.05, 0.15)]
    [InlineData(0.15, 0.15)]
    [InlineData(0.85, 0.85)]
    [InlineData(0.95, 0.85)]
    public void SetTerminalSplitRatio_ClampsToFifteenToEightyFivePercent(double requested, double stored)
    {
        var prefs = new UserPreferences();

        prefs.SetTerminalSplitRatio(requested);

        Assert.Equal(stored, prefs.TerminalSplitRatio);
    }

    [Fact]
    public void SavedLayout_TerminalSplitRatio_RoundTripsThroughDisk()
    {
        new UserPreferences().SaveLayout(new SavedLayout { Name = "WPT-TerminalSplit", TerminalSplitRatio = 0.4 });

        SavedLayout? reloaded = new UserPreferences().GetLayout("WPT-TerminalSplit");

        Assert.NotNull(reloaded);
        Assert.Equal(0.4, reloaded.TerminalSplitRatio);
    }

    [Fact]
    public void SavedLayout_JsonWithoutTerminalSplitRatio_ReadsBackNull()
    {
        // A layout saved before the terminal size was captured: applying it leaves the current split as it is.
        var file = new JsonObject
        {
            ["preferencesVersion"] = 1,
            ["layouts"] = new JsonArray
            {
                new JsonObject { ["name"] = "WPT-OldTerminal", ["isTerminalPoppedOut"] = false },
            },
        };

        File.WriteAllText(PreferencesPath, file.ToJsonString());

        SavedLayout? reloaded = new UserPreferences().GetLayout("WPT-OldTerminal");

        Assert.NotNull(reloaded);
        Assert.Null(reloaded.TerminalSplitRatio);
    }
}
