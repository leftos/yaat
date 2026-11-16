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

    [Theory]
    [InlineData("QQ", "110", "UAL238")]
    [InlineData("QT", "UAL238")]
    [InlineData("//UAL238")]
    public void InPlayback_AScopeEramEntryThatChangesState_TakesControl(params string[] tokens)
    {
        var connection = new ServerConnection { IsPlaybackMode = true };

        Assert.True(connection.WouldTakeControl(new ScopeEramEntryDto([.. tokens], false, null)));
    }

    [Theory]
    [InlineData("QF", "UAL238")]
    [InlineData("LA", "UAL238", "SFO")]
    [InlineData("LB", "SFO", "OAK")]
    [InlineData("LC", "UAL238", "SFO")]
    public void InPlayback_AScopeEramReadout_TakesNoControl(params string[] tokens)
    {
        var connection = new ServerConnection { IsPlaybackMode = true };

        Assert.False(connection.WouldTakeControl(new ScopeEramEntryDto([.. tokens], false, null)));
    }

    [Theory]
    [InlineData("LD", "UAL238")]
    [InlineData("LE", "UAL238")]
    [InlineData("UR", "UAL238")]
    [InlineData("SM")]
    [InlineData("RK")]
    public void InPlayback_AnOtherReadout_TakesNoControl(params string[] tokens) =>
        Assert.False(new ServerConnection { IsPlaybackMode = true }.WouldTakeControl(new ScopeEramEntryDto([.. tokens], false, null)));

    [Theory]
    [InlineData("SM", "44", "HELLO")]
    [InlineData("RK", "44", "ON")]
    public void InPlayback_SmOrRkWithOperands_TakesControl(params string[] tokens) =>
        Assert.True(new ServerConnection { IsPlaybackMode = true }.WouldTakeControl(new ScopeEramEntryDto([.. tokens], false, null)));

    [Fact]
    public async Task AServerWithoutTheMethod_AnswersNotSupportedByServer()
    {
        // The message a SignalR server sends for a hub method it does not have (its binder's "Method does not exist.").
        const string Missing = "Failed to invoke 'SendScopeEramEntry' due to an error on the server. HubException: Method does not exist.";

        ScopeEramEntryResultDto result = await ServerConnection.InvokeScopeEramEntryAsync(() =>
            throw new Microsoft.AspNetCore.SignalR.HubException(Missing)
        );

        Assert.False(result.Ok);
        Assert.Equal([ServerConnection.NotSupportedByServerMessage], result.Feedback);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task AnyOtherServerError_IsNotAnsweredNotSupported() =>
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            ServerConnection.InvokeScopeEramEntryAsync(() =>
                throw new Microsoft.AspNetCore.SignalR.HubException("An unexpected error occurred invoking 'SendScopeEramEntry' on the server.")
            )
        );

    [Fact]
    public void OffPlayback_NoScopeEramEntryTakesControl() =>
        Assert.False(new ServerConnection { IsPlaybackMode = false }.WouldTakeControl(new ScopeEramEntryDto(["QQ", "110", "UAL238"], false, null)));
}
