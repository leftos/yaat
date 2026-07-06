using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests;

// Covers TerminalPanelView.FormatEntry timestamp-mode rendering: wall-clock, sim-elapsed (with the
// --:-- fallback when no sim-time exists), and both. Message/kind layout is incidental here.
public class TerminalPanelFormatTests
{
    private static TerminalEntry Entry(double? elapsedSeconds) =>
        new()
        {
            Timestamp = new DateTime(2026, 7, 5, 14, 32, 7, DateTimeKind.Local),
            ElapsedSeconds = elapsedSeconds,
            Initials = "LF",
            Kind = TerminalEntryKind.Command,
            Callsign = "AAL100",
            Message = "H270",
        };

    [Fact]
    public void WallClock_ShowsClockTimeOnly()
    {
        var line = TerminalPanelView.FormatEntry(Entry(312), TerminalTimestampMode.WallClock);
        Assert.StartsWith("14:32:07", line);
        Assert.DoesNotContain("[", line);
        Assert.Contains("H270", line);
    }

    [Fact]
    public void SimElapsed_ShowsElapsedTime()
    {
        var line = TerminalPanelView.FormatEntry(Entry(312), TerminalTimestampMode.SimElapsed); // 312s = 5:12
        Assert.StartsWith("5:12", line);
        Assert.DoesNotContain("14:32:07", line);
    }

    [Fact]
    public void SimElapsed_NullElapsed_ShowsDashes()
    {
        var line = TerminalPanelView.FormatEntry(Entry(null), TerminalTimestampMode.SimElapsed);
        Assert.StartsWith("--:--", line);
    }

    [Fact]
    public void Both_ShowsClockAndElapsed()
    {
        var line = TerminalPanelView.FormatEntry(Entry(312), TerminalTimestampMode.Both);
        Assert.StartsWith("14:32:07", line);
        Assert.Contains("[5:12]", line);
    }
}
