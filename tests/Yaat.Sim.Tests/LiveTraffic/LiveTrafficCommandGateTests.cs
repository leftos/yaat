using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// The shadow gate: a control instruction issued to a live-traffic shadow assumes it first and then applies,
/// so the instructor never has to type <c>ASSUME</c> before taking a target. The seeded state is the one an
/// explicit <c>ASSUME</c> would have produced; a chain assumes once and applies every block; a read-only
/// <c>SAY*</c> query is still refused, because a query must not take control as a side effect.
/// </summary>
public class LiveTrafficCommandGateTests
{
    public LiveTrafficCommandGateTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState Shadow(string callsign) =>
        LiveTrafficKinematics.CreateShadow(
            callsign,
            "B738",
            new LiveTrafficSample(0, 37.0, -122.0, 10_000, 250, 90, -600, LiveTrafficSource.Stars, 4521),
            new AircraftFlightPlan { HasFlightPlan = true }
        );

    private static DispatchContext Ctx(AircraftState ac) =>
        TestDispatch.Context(new Random(1), findAircraft: cs => cs == ac.Callsign ? ac : null, listAircraft: () => [ac]);

    private static CommandResult Send(AircraftState ac, string input) => Send(ac, input, Ctx(ac));

    private static CommandResult Send(AircraftState ac, string input, DispatchContext ctx)
    {
        var parsed = CommandParser.ParseCompound(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
    }

    [Theory]
    [InlineData("H 180")]
    [InlineData("SQ 1234")]
    [InlineData("DM 5000")]
    [InlineData("SPD 210")]
    public void AnyCommandOnAShadow_AssumesItFirst_ThenApplies(string input)
    {
        var ac = Shadow("UAL123");

        var result = CommandDispatcher.Dispatch(CommandParser.Parse(input).Value!, ac, Ctx(ac));

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsShadow);
        Assert.Contains("UAL123 assumed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAutoAssumedState_IsTheOneAnExplicitAssumeProduces()
    {
        var auto = Shadow("UAL123");
        var explicitly = Shadow("UAL123");

        var autoResult = Send(auto, "H 180");
        Assert.True(CommandDispatcher.Dispatch(new AssumeCommand(), explicitly, Ctx(explicitly)).Success);
        var explicitResult = Send(explicitly, "H 180");

        Assert.True(autoResult.Success, autoResult.Message);
        Assert.True(explicitResult.Success, explicitResult.Message);
        Assert.False(auto.IsShadow);
        Assert.Equal(explicitly.Targets.TargetTrueHeading?.Degrees, auto.Targets.TargetTrueHeading?.Degrees);
        Assert.Equal(explicitly.Targets.TargetAltitude, auto.Targets.TargetAltitude);
        Assert.Equal(explicitly.Targets.AssignedAltitude, auto.Targets.AssignedAltitude);
        Assert.Equal(explicitly.Targets.TargetSpeed, auto.Targets.TargetSpeed);
        Assert.Equal(explicitly.Targets.DesiredVerticalRate, auto.Targets.DesiredVerticalRate);
        Assert.Equal(explicitly.Phases?.CurrentPhase?.GetType(), auto.Phases?.CurrentPhase?.GetType());
    }

    /// <summary>
    /// One hand-off for the whole compound: a parallel block applies both commands under it, and the reported
    /// beacon code the feed gave the shadow survives.
    /// </summary>
    [Fact]
    public void AParallelBlock_AssumesOnce_AndAppliesEveryCommand()
    {
        var ac = Shadow("UAL123");

        var result = Send(ac, "FH 070, DM 3000");

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsShadow);
        Assert.Contains("UAL123 assumed", result.Message, StringComparison.Ordinal);
        Assert.Equal(70, ac.Targets.AssignedMagneticHeading?.Degrees);
        Assert.Equal(3_000, ac.Targets.TargetAltitude);
        Assert.Equal(4521u, ac.Transponder.Code);
    }

    /// <summary>
    /// The <c>;</c> form assumes once too: the head applies now and the tail rides the queue until the turn
    /// completes (docs/command-chaining.md), instead of the whole chain being refused.
    /// </summary>
    [Fact]
    public void ASequentialChain_AssumesOnce_AndKeepsItsQueuedTail()
    {
        var ac = Shadow("UAL123");

        var result = Send(ac, "FH 070; DM 3000");

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsShadow);
        Assert.Equal(70, ac.Targets.AssignedMagneticHeading?.Degrees);
        Assert.Equal(["FH 070", "DM 3000"], ac.Queue.Blocks.Select(b => b.Description));
    }

    /// <summary>
    /// A question must not take control as a side effect. <c>SAYEXIT</c> is included even though it is the
    /// dispatcher's own verb rather than one of the router's <c>SAY</c> arm.
    /// </summary>
    [Theory]
    [InlineData("SALT")]
    [InlineData("SSPD")]
    [InlineData("SAYEXIT")]
    public void AReadOnlyQuery_IsStillRefused_AndNeverAssumes(string input)
    {
        var ac = Shadow("UAL123");

        var result = Send(ac, input);

        Assert.False(result.Success);
        Assert.Contains("ASSUME UAL123", result.Message, StringComparison.Ordinal);
        Assert.True(ac.IsShadow);
    }

    /// <summary>
    /// Taking a real aircraft is the RPO's decision. A scenario preset or an AI controller dispatching to a shadow
    /// gets the refusal instead, so nothing the scenario runs on its own can assume live traffic.
    /// </summary>
    [Fact]
    public void AScriptedDispatch_NeverAssumes()
    {
        var ac = Shadow("UAL123");
        var scripted = TestDispatch.Context(
            new Random(1),
            findAircraft: cs => cs == ac.Callsign ? ac : null,
            listAircraft: () => [ac],
            isScenarioScripted: true
        );

        var result = Send(ac, "FH 070", scripted);

        Assert.False(result.Success);
        Assert.Contains("ASSUME UAL123", result.Message, StringComparison.Ordinal);
        Assert.True(ac.IsShadow);
        Assert.Null(ac.Targets.AssignedMagneticHeading);
    }

    /// <summary>
    /// A surface shadow is not auto-assumed — the client offers no assume for one either
    /// (<c>AircraftCommandApplicability.CanAssume</c> is airborne-only). A typed <c>ASSUME</c> still works: it is
    /// answered before the gate.
    /// </summary>
    [Fact]
    public void AGroundShadow_IsRefused_ButATypedAssumeStillWorks()
    {
        var ac = Shadow("UAL123");
        ac.IsOnGround = true;

        var result = Send(ac, "TAXI 28R");

        Assert.False(result.Success);
        Assert.Contains("ASSUME UAL123", result.Message, StringComparison.Ordinal);
        Assert.True(ac.IsShadow);

        Assert.True(CommandDispatcher.Dispatch(new AssumeCommand(), ac, Ctx(ac)).Success);
        Assert.False(ac.IsShadow);
    }

    /// <summary>
    /// The seed inference has nothing to read — no sample history, a coasting track — and the hand-off still
    /// happens: <c>ASSUME</c> is never refused, so neither is the automatic one.
    /// </summary>
    [Fact]
    public void ACoastingShadowWithNoHistory_StillAssumes()
    {
        var ac = Shadow("UAL123");
        ac.LiveTraffic!.IsCoasting = true;
        ac.LiveTraffic.History.Clear();

        var result = Send(ac, "H 180");

        Assert.True(result.Success, result.Message);
        Assert.False(ac.IsShadow);
        Assert.Contains("UAL123 assumed", result.Message, StringComparison.Ordinal);
    }
}
