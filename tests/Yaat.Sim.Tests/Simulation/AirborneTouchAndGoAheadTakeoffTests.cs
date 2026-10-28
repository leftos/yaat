using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Simulation.RejectedTakeoffTestRig;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// 7110.65 3-8-2: a touch-and-go is an arrival until it touches down and a departure thereafter. A departure rolling
/// behind one that has lifted off is behind a preceding departure, judged by <see cref="PrecedingDepartureBlock"/>, not
/// behind traffic on the runway: it must not abort its takeoff for an aircraft already flying away down the runway.
/// </summary>
public class AirborneTouchAndGoAheadTakeoffTests
{
    public AirborneTouchAndGoAheadTakeoffTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Rolling_BehindAirborneTouchAndGoBeyondThe396aDistance_DoesNotReject()
    {
        RunwayInfo runway = Runway28R();
        AircraftState dep = MakeRollingDeparture(runway, iasKts: 60);
        AircraftState lead = MakeAirborneLeader(runway, downfieldFt: 7000, iasKts: 150, aglFt: 300);
        lead.Phases = new PhaseList { AssignedRunway = runway };
        lead.Phases.Add(LiftedOffOptionPhases.TouchAndGo(runway));
        PhaseContext ctx = Ctx(dep, runway, lead, autoReject: true);
        dep.Phases!.Start(ctx);

        (bool airborne, double _) = RunRoll(dep, ctx, lead, seconds: 120);

        Assert.True(
            airborne,
            $"An airborne touch-and-go 7,000 ft ahead must not abort the roll (phase {dep.Phases?.CurrentPhase?.Name ?? "(none)"})"
        );
    }

    /// <summary>
    /// 1,500 ft ahead is short of 3-9-6.a's 6,000 ft, but a preceding departure already flying away down the runway is no
    /// traffic on the runway to abort for.
    /// </summary>
    [Fact]
    public void Rolling_BehindAirborneTouchAndGoShortOfThe396aDistance_DoesNotReject() =>
        Assert.True(RollsBehindAirborneLeader(LiftedOffOptionPhases.TouchAndGo(Runway28R())));

    /// <summary>A low approach past the landing threshold is a departure too: the same judgement as an airborne touch-and-go.</summary>
    [Fact]
    public void Rolling_BehindLowApproachPastTheThresholdShortOfThe396aDistance_DoesNotReject() =>
        Assert.True(RollsBehindAirborneLeader(new LowApproachPhase()));

    /// <summary>
    /// A departure rolling at 60 kt behind <paramref name="leaderPhase"/> flown airborne 1,500 ft ahead; true when it gets
    /// airborne without aborting.
    /// </summary>
    private static bool RollsBehindAirborneLeader(Phase leaderPhase)
    {
        RunwayInfo runway = Runway28R();
        AircraftState dep = MakeRollingDeparture(runway, iasKts: 60);
        AircraftState lead = MakeAirborneLeader(runway, downfieldFt: 1500, iasKts: 150, aglFt: 300);
        lead.Phases = new PhaseList { AssignedRunway = runway };
        lead.Phases.Add(leaderPhase);
        lead.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        PhaseContext ctx = Ctx(dep, runway, lead, autoReject: true);
        dep.Phases!.Start(ctx);

        (bool airborne, double _) = RunRoll(dep, ctx, lead, seconds: 120);
        return airborne;
    }
}
