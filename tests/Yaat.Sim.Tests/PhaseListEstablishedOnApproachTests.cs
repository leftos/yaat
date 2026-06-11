using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="PhaseList.IsEstablishedOnApproach"/> drives the client's MVA-tint inhibit: it is true only
/// when the aircraft is being descended below the MVA by an approach it is cleared to fly. A lateral-only
/// localizer join (JFAC/JLOC) holds its assigned altitude and must NOT inhibit the tint; neither may a
/// go-around (climbing out) or a pattern leg. The phase only inspects <see cref="PhaseList.CurrentPhase"/>'s
/// type and <see cref="PhaseList.ActiveApproach"/>, so the list is built by adding a single phase (CurrentIndex
/// defaults to 0) without running OnStart.
/// </summary>
public class PhaseListEstablishedOnApproachTests
{
    private static ApproachClearance Clearance(bool lateralOnly) =>
        new()
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = new TrueHeading(280),
            LateralInterceptOnly = lateralOnly,
        };

    private static PhaseList Make(Phase? current, ApproachClearance? approach, ClearanceType? landing = null)
    {
        var phases = new PhaseList { ActiveApproach = approach, LandingClearance = landing };
        if (current is not null)
        {
            phases.Add(current);
        }
        return phases;
    }

    private static InterceptCoursePhase Intercept() =>
        new()
        {
            FinalApproachCourse = new TrueHeading(280),
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
        };

    [Fact]
    public void LateralOnlyJoin_OnFinal_StillHoldingAltitude_IsNotEstablished()
    {
        // JFAC/JLOC reached FinalApproachPhase but holds its assigned altitude until laterally
        // established — the MVA still applies, so the tint must keep firing. A naive
        // "CurrentPhase is FinalApproachPhase" check would wrongly return true here.
        var phases = Make(new FinalApproachPhase(), Clearance(lateralOnly: true));

        Assert.False(phases.IsEstablishedOnApproach());
    }

    [Fact]
    public void OnFinal_ClearedApproach_IsEstablished()
    {
        var phases = Make(new FinalApproachPhase(), Clearance(lateralOnly: false));

        Assert.True(phases.IsEstablishedOnApproach());
    }

    [Fact]
    public void ApproachNav_ClearedApproach_IsEstablished()
    {
        var phases = Make(new ApproachNavigationPhase { Fixes = [] }, Clearance(lateralOnly: false));

        Assert.True(phases.IsEstablishedOnApproach());
    }

    [Fact]
    public void VectoredIntercept_ClearedApproach_IsEstablished()
    {
        var phases = Make(Intercept(), Clearance(lateralOnly: false));

        Assert.True(phases.IsEstablishedOnApproach());
    }

    [Fact]
    public void LateralOnlyJoin_OnIntercept_IsNotEstablished()
    {
        var phases = Make(Intercept(), Clearance(lateralOnly: true));

        Assert.False(phases.IsEstablishedOnApproach());
    }

    [Fact]
    public void ApproachPhase_WithoutActiveClearance_IsNotEstablished()
    {
        var phases = Make(new ApproachNavigationPhase { Fixes = [] }, approach: null);

        Assert.False(phases.IsEstablishedOnApproach());
    }

    [Fact]
    public void GoAround_EvenWithActiveApproach_IsNotEstablished()
    {
        // Missed approach / go-around is climbing away from the runway — MVA applies again.
        var phases = Make(new GoAroundPhase(), Clearance(lateralOnly: false));

        Assert.False(phases.IsEstablishedOnApproach());
    }

    [Fact]
    public void PatternDownwind_ClearedToLand_IsNotEstablished()
    {
        // A pattern aircraft cleared to land on downwind has no active approach procedure
        // stepping it down — it is not "established on an approach", so the tint stays on.
        var phases = Make(new DownwindPhase(), approach: null, landing: ClearanceType.ClearedToLand);

        Assert.False(phases.IsEstablishedOnApproach());
    }

    [Fact]
    public void NoCurrentPhase_IsNotEstablished()
    {
        var phases = Make(current: null, approach: null);

        Assert.False(phases.IsEstablishedOnApproach());
    }
}
