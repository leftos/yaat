using Xunit;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The pilot lens's queued-commands badge reads <c>PendingCommands</c>: every non-empty line not starting <c>[Active]</c> is a
/// pending command, kept verbatim and in order; none means no badge.
/// </summary>
public class ScopeQueueBadgeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[Active] FH 270")]
    public void NoPendingLine_GivesNone(string? pendingCommands) => Assert.Empty(ScopeQueueBadge.PendingLines(pendingCommands));

    [Theory]
    [InlineData("[Active] FH 270\n[1] AT BRIXX DM 040\n[2] WAIT 30 SPD 210")]
    [InlineData("[Active] FH 270\r\n[1] AT BRIXX DM 040\r\n[2] WAIT 30 SPD 210\r\n")]
    public void PendingLines_AreKeptVerbatim_InOrder_WithoutTheActiveBlock(string pendingCommands) =>
        Assert.Equal(["[1] AT BRIXX DM 040", "[2] WAIT 30 SPD 210"], ScopeQueueBadge.PendingLines(pendingCommands));

    [Fact]
    public void OnePendingLine_Alone_GivesThatLine() => Assert.Equal(["[1] AT BRIXX DM 040"], ScopeQueueBadge.PendingLines("[1] AT BRIXX DM 040"));
}
