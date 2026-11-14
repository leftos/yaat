using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Helpers.FollowTestHelpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A follower's own go-around ends the follow: accepting a follow means maneuvering to keep in-trail separation
/// (AIM 4-4-14.b), and a go-around abandons it, leaving the pilot to ask ATC for the next action (AIM 5-5-5.a.6).
/// Without the clear, <see cref="GoAroundPhase"/> keeps running the lead lifecycle and the re-entered pattern legs
/// re-chase the lead. Real KOAK 28R navdata.
/// </summary>
public class GoAroundClearsFollowTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
    }

    /// <summary>
    /// An approach follower 6 nm out filed under <paramref name="flightRules"/>, chasing <see cref="Leader"/> 2 nm out on the
    /// 28R final: the follow is set.
    /// </summary>
    private static AircraftState FollowingFollower(SimulationEngine engine, string flightRules)
    {
        RunwayInfo rwy = NavigationDatabase.Instance.GetRunway("KOAK", "28R") ?? throw new InvalidOperationException("KOAK 28R missing from navdata");
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        follower.FlightPlan.HasFlightPlan = true;
        follower.FlightPlan.FlightRules = flightRules;
        FollowKeepApproachTests.AddFinalLead(engine, rwy, 2.0);
        CommandResult follow = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        Assert.True(follow.Success, follow.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        return follower;
    }

    private static bool MentionsLeadLifecycle(string text) =>
        text.Contains("lost sight", StringComparison.OrdinalIgnoreCase) || text.Contains("breaking off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// With the lead gone from the world, a follow that survived the go-around runs the lead-lost lifecycle and speaks; a
    /// cleared follow has nothing to chase.
    /// </summary>
    private static void AssertNoLeadLifecycle(AircraftState follower)
    {
        Assert.DoesNotContain(follower.PendingPilotTransmissions, t => MentionsLeadLifecycle(t.Text));
        Assert.DoesNotContain(follower.PendingWarnings, w => MentionsLeadLifecycle(w));
    }

    [Fact]
    public void InstructedGoAround_ClearsTheFollow()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowingFollower(engine, "VFR");

        CommandResult goAround = engine.SendCommand(Follower, "GA");

        Assert.True(goAround.Success, goAround.Message);
        Assert.IsType<GoAroundPhase>(follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);

        engine.World.RemoveAircraft(Leader);
        TickSeconds(engine, 10);

        Assert.Null(follower.Approach.FollowingCallsign);
        AssertNoLeadLifecycle(follower);
    }

    [Fact]
    public void AutomaticGoAround_ClearsTheFollow()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowingFollower(engine, "VFR");

        GoAroundHelper.Trigger(CommandDispatcher.BuildMinimalContext(follower, ActiveRunways.Empty), "test");

        Assert.IsType<GoAroundPhase>(follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);

        engine.World.RemoveAircraft(Leader);
        TickSeconds(engine, 10);

        Assert.Null(follower.Approach.FollowingCallsign);
        AssertNoLeadLifecycle(follower);
    }

    [Fact]
    public void IfrGoAround_NotReenteringPattern_ClearsTheFollow()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowingFollower(engine, "IFR");

        CommandResult goAround = engine.SendCommand(Follower, "GA");

        Assert.True(goAround.Success, goAround.Message);
        GoAroundPhase phase = Assert.IsType<GoAroundPhase>(follower.Phases!.CurrentPhase);
        Assert.False(phase.ReenterPattern);
        Assert.Null(follower.Approach.FollowingCallsign);
    }
}
