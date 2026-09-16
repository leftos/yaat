using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The client half of the playback-divergence gate: <see cref="ServerConnection.WouldTakeControl"/> decides whether a
/// command about to be sent would cut short a tape the room is playing back, and so whether the user is asked first. It
/// has to answer exactly as the server's own gate does — a false positive prompts before a command the server would
/// never take control for, and a false negative sends a real write with no warning and deletes the rest of the recording.
/// </summary>
public class ServerConnectionTakeControlGateTests
{
    /// <summary>The commands the action router never records: they cannot diverge the tape, so they never prompt.</summary>
    public static TheoryData<string> NonDivergingCommands => ["UNPAUSE", "PAUSE", "SIMRATE 4", "BM Marker", "SHOWAT", "** UNPAUSE"];

    /// <summary>The writes that take control: a real command still ends the playback, prefixed or not.</summary>
    public static TheoryData<string> DivergingCommands => ["H 270", "TRACK", "** H 270"];

    [Theory]
    [MemberData(nameof(NonDivergingCommands))]
    [MemberData(nameof(DivergingCommands))]
    public void OffPlayback_NothingTakesControl(string command)
    {
        var connection = new ServerConnection { IsPlaybackMode = false };

        Assert.False(connection.WouldTakeControl(command));
    }

    [Theory]
    [MemberData(nameof(NonDivergingCommands))]
    public void InPlayback_ACommandTheRouterNeverRecords_TakesNoControl(string command)
    {
        var connection = new ServerConnection { IsPlaybackMode = true };

        Assert.False(connection.WouldTakeControl(command));
    }

    [Theory]
    [MemberData(nameof(DivergingCommands))]
    public void InPlayback_ARecordedWrite_TakesControl(string command)
    {
        var connection = new ServerConnection { IsPlaybackMode = true };

        Assert.True(connection.WouldTakeControl(command));
    }
}
