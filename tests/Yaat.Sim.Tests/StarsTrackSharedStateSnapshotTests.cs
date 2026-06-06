using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Round-trip tests for <see cref="StarsTrackSharedState"/> ↔ <c>SharedStateDto</c>. Every per-TCP
/// display field must survive snapshot serialization so rewind/replay reconstructs the student's
/// STARS scope faithfully.
/// </summary>
public class StarsTrackSharedStateSnapshotTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new StarsTrackSharedState
        {
            ForceFdb = true,
            IsHighlighted = true,
            LeaderDirection = 6,
            IsQueriedUntil = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc),
            WasPreviouslyOwned = true,
            TpaType = 2,
            TpaSize = 3.5,
            IsRecentlyAcceptedIncomingPointout = true,
        };

        var restored = StarsTrackSharedState.FromSnapshot(original.ToSnapshot());

        Assert.Equal(original.ForceFdb, restored.ForceFdb);
        Assert.Equal(original.IsHighlighted, restored.IsHighlighted);
        Assert.Equal(original.LeaderDirection, restored.LeaderDirection);
        Assert.Equal(original.IsQueriedUntil, restored.IsQueriedUntil);
        Assert.Equal(original.WasPreviouslyOwned, restored.WasPreviouslyOwned);
        Assert.Equal(original.TpaType, restored.TpaType);
        Assert.Equal(original.TpaSize, restored.TpaSize);
        Assert.Equal(original.IsRecentlyAcceptedIncomingPointout, restored.IsRecentlyAcceptedIncomingPointout);
    }

    [Fact]
    public void RoundTrip_DefaultRecentlyAcceptedPointout_IsFalse()
    {
        var restored = StarsTrackSharedState.FromSnapshot(new StarsTrackSharedState().ToSnapshot());

        Assert.False(restored.IsRecentlyAcceptedIncomingPointout);
    }
}
