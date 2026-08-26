using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// Live traffic is not controllable: every dispatcher command on a shadow is rejected with a
/// message that names the way out (ASSUME), including phase-transparent ones like SQ that
/// would otherwise apply without consulting phases.
/// </summary>
public class LiveTrafficCommandGateTests
{
    public LiveTrafficCommandGateTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState Shadow() =>
        LiveTrafficKinematics.CreateShadow(
            "UAL123",
            "B738",
            new LiveTrafficSample(0, 37.0, -122.0, 10_000, 250, 90, -600, LiveTrafficSource.Stars, 4521),
            new AircraftFlightPlan { HasFlightPlan = true }
        );

    [Theory]
    [InlineData("H 180")]
    [InlineData("SQ 1234")]
    [InlineData("DM 50")]
    [InlineData("SA")]
    public void Dispatch_RejectsEveryCommandOnAShadow(string input)
    {
        var ac = Shadow();
        var parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var result = CommandDispatcher.Dispatch(parsed.Value!, ac, TestDispatch.Context(new Random(1)));

        Assert.False(result.Success);
        Assert.Contains("ASSUME UAL123", result.Message, StringComparison.Ordinal);
        Assert.Equal(4521u, ac.Transponder.Code);
        Assert.Null(ac.Targets.TargetTrueHeading);
    }

    [Fact]
    public void DispatchCompound_RejectsOnAShadow()
    {
        var ac = Shadow();
        var compound = CommandParser.ParseCompound("H 180 ; DM 50");
        Assert.True(compound.IsSuccess, compound.Reason);

        var result = CommandDispatcher.DispatchCompound(compound.Value!, ac, TestDispatch.Context(new Random(1)));

        Assert.False(result.Success);
        Assert.Contains("ASSUME UAL123", result.Message, StringComparison.Ordinal);
        Assert.Empty(ac.Queue.Blocks);
    }
}
