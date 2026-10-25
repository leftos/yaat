using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// <see cref="MainViewModel.TerminalEntryFromBroadcast"/> keeps the server's <see cref="TerminalBroadcastDto.PilotKind"/> on the
/// terminal line, so a reader tells a pilot's own call from a readback, while the line's kind still collapses to Say.
/// </summary>
public class MainViewModelTerminalPilotKindTests
{
    [Theory]
    [InlineData("SayPilot", "Proactive")]
    [InlineData("SayPilot", "Report")]
    [InlineData("SayPilot", "Readback")]
    [InlineData("SayReadback", "SayReadback")]
    public void PilotLine_KeepsItsPilotKind_AndMapsToSay(string kind, string pilotKind)
    {
        TerminalEntry entry = MainViewModel.TerminalEntryFromBroadcast(
            new TerminalBroadcastDto("", kind, "N45912", "with you", DateTime.UtcNow, 10, pilotKind),
            isHistory: false
        );

        Assert.Equal(pilotKind, entry.PilotKind);
        Assert.Equal(TerminalEntryKind.Say, entry.Kind);
    }

    [Fact]
    public void NonPilotLine_HasNoPilotKind()
    {
        TerminalEntry entry = MainViewModel.TerminalEntryFromBroadcast(
            new TerminalBroadcastDto("AB", "Command", "N45912", "FH 270", DateTime.UtcNow, 10, PilotKind: null),
            isHistory: false
        );

        Assert.Null(entry.PilotKind);
        Assert.Equal(TerminalEntryKind.Command, entry.Kind);
    }
}
