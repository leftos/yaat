using Xunit;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The LAHSO exit filter judges an exit by where its turn-off arc leaves the runway pavement, not by where it branches.
///
/// <para>
/// AIM 2-3-5.a.2 keeps the whole aircraft short of the runway holding position marking, so an exit running out to the
/// runway edge still crosses the marking's extent after the branch point. A shallow high-speed exit branching just
/// inside the stop limit runs on past it and does not clear the marking; the run-on is half the runway width over the
/// tangent of the exit angle.
/// </para>
/// </summary>
public class LahsoTurnOffArcTests
{
    /// <summary>The LAHSO stop limit every row is judged against, in nautical miles from the landing threshold.</summary>
    private const double StopLimitNm = 1.000;

    [Theory]
    // A right-angle exit leaves the pavement at its branch point.
    [InlineData(1.000, 90.0, 150.0, true)]
    [InlineData(1.001, 90.0, 150.0, false)]
    // A 30-degree exit from a 200 ft runway runs on 173.2 ft (0.0285 nm).
    [InlineData(0.980, 30.0, 200.0, false)]
    [InlineData(0.970, 30.0, 200.0, true)]
    // A 45-degree exit from a 150 ft runway runs on 75 ft (0.01234 nm).
    [InlineData(0.990, 45.0, 150.0, false)]
    [InlineData(0.987, 45.0, 150.0, true)]
    // A back exit leaves the pavement at the branch point, like the right angle.
    [InlineData(1.000, 120.0, 150.0, true)]
    // A degenerate angle is judged at the 10-degree clamp: 425.3 ft (0.0700 nm) on a 150 ft runway.
    [InlineData(0.929, 0.0, 150.0, true)]
    [InlineData(0.935, 0.0, 150.0, false)]
    // An unknown runway width leaves the judge at the branch point alone.
    [InlineData(1.000, 30.0, 0.0, true)]
    public void TurnOffFitsBefore_JudgesTheTurnOffArcNotTheBranchPoint(
        double branchFromThresholdNm,
        double exitAngleDeg,
        double runwayWidthFt,
        bool expected
    )
    {
        bool fits = LandingPhase.TurnOffFitsBefore(branchFromThresholdNm, exitAngleDeg, runwayWidthFt, StopLimitNm);

        Assert.Equal(expected, fits);
    }

    [Theory]
    [InlineData(90.0, 150.0, 0.0)]
    [InlineData(120.0, 150.0, 0.0)]
    [InlineData(30.0, 200.0, 173.2)]
    [InlineData(45.0, 150.0, 75.0)]
    [InlineData(0.0, 150.0, 425.3)]
    [InlineData(10.0, 150.0, 425.3)]
    [InlineData(30.0, 0.0, 0.0)]
    [InlineData(30.0, -10.0, 0.0)]
    public void TurnOffRunOnNm_IsHalfTheWidthOverTheTangentOfTheAngle(double exitAngleDeg, double runwayWidthFt, double expectedFt)
    {
        double runOnFt = LandingPhase.TurnOffRunOnNm(exitAngleDeg, runwayWidthFt) * GeoMath.FeetPerNm;

        Assert.Equal(expectedFt, runOnFt, 0.1);
    }
}
