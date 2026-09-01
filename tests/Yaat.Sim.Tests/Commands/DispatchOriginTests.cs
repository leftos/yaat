using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>The synthetic AI connection id and the origin derived from it at every dispatch site.</summary>
public class DispatchOriginTests
{
    [Fact]
    public void Format_AndParse_RoundTrip()
    {
        var id = AiConnectionId.Format("01GEAMCGAZ0000000000000000");

        Assert.Equal("AI:01GEAMCGAZ0000000000000000", id);
        Assert.True(AiConnectionId.IsAi(id));
        Assert.True(AiConnectionId.TryParse(id, out var positionId));
        Assert.Equal("01GEAMCGAZ0000000000000000", positionId);
        Assert.Equal(DispatchOrigin.ControllerAi, AiConnectionId.OriginOf(id));
    }

    [Theory]
    [InlineData("test-conn")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ai:lowercase-is-not-the-prefix")]
    public void HumanConnectionIds_AreHuman(string? connectionId)
    {
        Assert.False(AiConnectionId.IsAi(connectionId));
        Assert.False(AiConnectionId.TryParse(connectionId, out _));
        Assert.Equal(DispatchOrigin.Human, AiConnectionId.OriginOf(connectionId));
    }

    [Fact]
    public void BarePrefix_IsNotAPositionId()
    {
        Assert.False(AiConnectionId.TryParse("AI:", out _));
        Assert.Throws<ArgumentException>(() => AiConnectionId.Format(" "));
    }
}
