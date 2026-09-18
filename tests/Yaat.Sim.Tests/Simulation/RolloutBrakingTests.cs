using Xunit;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The braking arithmetic shared by <c>LandingPhase</c> (planning deceleration toward a chosen exit) and
/// <c>RunwayExitPhase</c> (deciding whether a late exit change is still something the aircraft can make).
/// </summary>
public class RolloutBrakingTests
{
    [Fact]
    public void RequiredDecel_AndBrakingDistance_AreInverses()
    {
        const double From = 40.0;
        const double To = 15.0;
        const double Rate = 5.0;

        double distanceNm = RolloutBraking.BrakingDistanceNm(From, To, Rate);
        double required = RolloutBraking.RequiredDecelKtsPerSec(From, To, distanceNm);

        Assert.Equal(Rate, required, 6);
    }

    /// <summary>
    /// The jet case the retarget braking gate is sized against: 40 kt coast down to a 15 kt standard turn-off
    /// takes ~232 ft at the firm rate.
    /// </summary>
    [Fact]
    public void BrakingDistance_JetCoastToStandardTurnOff_IsAboutTwoHundredThirtyFeet()
    {
        double distFt = RolloutBraking.BrakingDistanceNm(40.0, 15.0, RolloutBraking.FirmBrakingRateKtsPerSec) * GeoMath.FeetPerNm;

        Assert.InRange(distFt, 225.0, 240.0);
    }

    [Fact]
    public void RequiredDecel_TightensAsDistanceShrinks()
    {
        double far = RolloutBraking.RequiredDecelKtsPerSec(40.0, 15.0, 0.1);
        double near = RolloutBraking.RequiredDecelKtsPerSec(40.0, 15.0, 0.02);

        Assert.True(near > far, $"expected a shorter run to demand harder braking, got near={near:F2} far={far:F2}");
    }

    /// <summary>
    /// No room left is not a division by zero — the answer is "at least firm braking", which every caller
    /// compares against its own limit.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    public void RequiredDecel_WithNoDistanceLeft_ReportsFirmBraking(double distanceNm) =>
        Assert.Equal(RolloutBraking.FirmBrakingRateKtsPerSec, RolloutBraking.RequiredDecelKtsPerSec(40.0, 15.0, distanceNm));

    [Fact]
    public void BrakingDistance_WithNoDecelRate_IsZeroRatherThanInfinite() => Assert.Equal(0.0, RolloutBraking.BrakingDistanceNm(40.0, 15.0, 0.0));

    /// <summary>Already at or below the target speed needs no room.</summary>
    [Fact]
    public void BrakingDistance_WhenAlreadySlowEnough_IsNotPositive() =>
        Assert.True(RolloutBraking.BrakingDistanceNm(15.0, 15.0, RolloutBraking.FirmBrakingRateKtsPerSec) <= 0.0);

    /// <summary>
    /// The LAHSO speed ceiling is the braking distance read backwards: whatever
    /// <see cref="RolloutBraking.MaxEntrySpeedKts"/> allows has to stop in exactly the distance it was given.
    /// </summary>
    [Theory]
    [InlineData(0.05, 5.0)]
    [InlineData(0.2, 5.0)]
    [InlineData(0.008, 7.5)]
    public void MaxEntrySpeed_IsTheInverseOfBrakingDistance(double distanceNm, double rate)
    {
        double entrySpeed = RolloutBraking.MaxEntrySpeedKts(distanceNm, rate);

        Assert.Equal(distanceNm, RolloutBraking.BrakingDistanceNm(entrySpeed, 0.0, rate), 9);
    }

    /// <summary>No room left means no speed is slow enough — the caller's ceiling collapses to a stop.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.01)]
    public void MaxEntrySpeed_WithNoDistanceLeft_IsZero(double distanceNm) =>
        Assert.Equal(0.0, RolloutBraking.MaxEntrySpeedKts(distanceNm, RolloutBraking.FirmBrakingRateKtsPerSec));

    /// <summary>
    /// An aircraft that cannot brake cannot stop, however much runway is left: the ceiling is zero rather than
    /// infinite, so a caller that passes a helicopter's zero rollout rate gets a stop target, not a free pass.
    /// </summary>
    [Fact]
    public void MaxEntrySpeed_WithNoBrakingRate_IsZero() => Assert.Equal(0.0, RolloutBraking.MaxEntrySpeedKts(0.2, 0.0));
}
