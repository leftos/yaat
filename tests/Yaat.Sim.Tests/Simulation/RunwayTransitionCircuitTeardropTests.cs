using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The crossing arm of <see cref="PatternBuilder.BuildRunwayTransitionCircuit"/> (a cross-runway
/// takeoff clearance — <c>CTO 33 MRT 28R</c> at OAK — and the auto-cycle's runway transition) joins the
/// pattern runway's downwind through a <see cref="MidfieldCrossingPhase"/>. That aircraft never
/// <em>left</em> the pattern: it climbs out on the runway it used and crosses to the other downwind at
/// traffic-pattern altitude, because AIM 4-3-3.a.2's 1,500 ft AGL crossing is an entry rule for arrivals
/// joining from outside. So there is no entry height to lose and no
/// <see cref="TeardropReentryPhase"/> — for any category.
///
/// Real KOAK runways: 33 crosses 28R/28L; 28R/28L are close parallels.
/// </summary>
public class RunwayTransitionCircuitTeardropTests
{
    public RunwayTransitionCircuitTeardropTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData(AircraftCategory.Jet, "CRJ2")]
    [InlineData(AircraftCategory.Turboprop, "DH8D")]
    [InlineData(AircraftCategory.Piston, "C172")]
    public void CrossingTransition_CrossesAtPatternAltitude_WithNoTeardrop(AircraftCategory category, string aircraftType)
    {
        var phases = BuildTransition("33", "28R", category, aircraftType);
        if (phases is null)
        {
            return;
        }

        var crossing = Assert.IsType<MidfieldCrossingPhase>(phases.Find(p => p is MidfieldCrossingPhase));
        Assert.True(crossing.CrossAtPatternAltitude, "a departure crossing to another runway's pattern never left the pattern");
        Assert.DoesNotContain(phases, p => p is TeardropReentryPhase);

        int crossingIndex = phases.FindIndex(p => p is MidfieldCrossingPhase);
        int downwindIndex = phases.FindIndex(p => p is DownwindPhase);
        Assert.True(downwindIndex == crossingIndex + 1, "the crossing joins the downwind directly");

        var downwind = phases.OfType<DownwindPhase>().First();
        Assert.True(downwind.RejoinTrack, "without a teardrop the downwind has to re-intercept its own track after the crossing");
    }

    [Fact]
    public void CrossingTransition_UpwindBelongsToTheDepartureRunway_ThenTheCrossing()
    {
        var phases = BuildTransition("33", "28R", AircraftCategory.Jet, "CRJ2");
        if (phases is null)
        {
            return;
        }

        Assert.IsType<UpwindPhase>(phases[0]);
        Assert.IsType<MidfieldCrossingPhase>(phases[1]);
    }

    [Theory]
    [InlineData(AircraftCategory.Jet, "CRJ2")]
    [InlineData(AircraftCategory.Piston, "C172")]
    public void ParallelTransition_NeverCrossesAndNeverTeardrops(AircraftCategory category, string aircraftType)
    {
        var phases = BuildTransition("28R", "28L", category, aircraftType);
        if (phases is null)
        {
            return;
        }

        Assert.DoesNotContain(phases, p => p is MidfieldCrossingPhase);
        Assert.DoesNotContain(phases, p => p is TeardropReentryPhase);
        Assert.Contains(phases, p => p is CrosswindPhase);
    }

    private static List<Phase>? BuildTransition(string flownDesignator, string patternDesignator, AircraftCategory category, string aircraftType)
    {
        var navDb = TestVnasData.NavigationDb;
        var flown = navDb?.GetRunway("KOAK", flownDesignator);
        var pattern = navDb?.GetRunway("KOAK", patternDesignator);
        if (navDb is null || flown is null || pattern is null)
        {
            return null;
        }

        return PatternBuilder.BuildRunwayTransitionCircuit(
            flown,
            pattern,
            category,
            aircraftType,
            windSpeedKt: 0,
            PatternDirection.Right,
            touchAndGo: true,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            navDb.GetRunways("KOAK"),
            flownAuthoredRunway: null,
            patternAuthoredRunway: null
        );
    }
}
