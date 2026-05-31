using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="GroundNavigator.StraightArrivalThresholdNm"/> — the tangent
/// corner-rounding arrival threshold, including the short-leg guard that prevents an inverted
/// <c>Math.Clamp</c> range.
/// </summary>
public class GroundNavigatorThresholdTests
{
    // Mirror of the GroundNavigator private constants the threshold falls back to.
    private const double FinalNodeArrivalThresholdNm = 0.0003;
    private const double NodeArrivalThresholdNm = 0.015;

    [Fact]
    public void ShortLegWithSharpCorner_DoesNotThrow_FallsBackToFinalThreshold()
    {
        // A leg so short that 0.45*len <= FinalNodeArrivalThresholdNm has no room to round. Regression
        // for the Math.Clamp(min>max) crash surfaced by the ground-stack sweep on a near-zero-length
        // edge: before the guard this combination (sharp corner + tiny leg) threw ArgumentException.
        double threshold = GroundNavigator.StraightArrivalThresholdNm(
            cornerTurnDeg: 90.0,
            edgeLengthNm: 1e-6,
            category: AircraftCategory.Jet,
            roundingRadiusFt: CategoryPerformance.NoseWheelTurnRadiusFt(AircraftCategory.Jet),
            isLastSegment: false,
            isStopTarget: false,
            shortEdge: true,
            nextSegmentIsArc: false,
            nextSegmentIsShort: false,
            out bool roundingActive
        );

        Assert.False(roundingActive);
        Assert.Equal(FinalNodeArrivalThresholdNm, threshold, precision: 9);
    }

    [Fact]
    public void LongLegWithSharpCorner_RoundsAtTangentLength()
    {
        // A normal-length leg with a 90° corner rounds: threshold = clamp(r*tan(45°), floor, 0.45*len).
        // r(jet) ≈ 25 ft, tan(45°)=1, so T ≈ 25 ft, well within [floor, 0.45*0.05nm].
        double threshold = GroundNavigator.StraightArrivalThresholdNm(
            cornerTurnDeg: 90.0,
            edgeLengthNm: 0.05,
            category: AircraftCategory.Jet,
            roundingRadiusFt: CategoryPerformance.NoseWheelTurnRadiusFt(AircraftCategory.Jet),
            isLastSegment: false,
            isStopTarget: false,
            shortEdge: false,
            nextSegmentIsArc: false,
            nextSegmentIsShort: false,
            out bool roundingActive
        );

        Assert.True(roundingActive);
        double rFt = CategoryPerformance.NoseWheelTurnRadiusFt(AircraftCategory.Jet);
        double expectedNm = (rFt * Math.Tan(45.0 * Math.PI / 180.0)) / GeoMath.FeetPerNm;
        Assert.Equal(expectedNm, threshold, precision: 6);
    }

    [Fact]
    public void AdaptiveCornerRadius_TightLeg_TightensTowardFloor()
    {
        // 118° turn (SFO M2→A) with only ~22 ft of approach between the B and A crossings: the comfortable
        // 25 ft tangent (41.6 ft) doesn't fit, so the radius tightens to fit — clamped at the 15 ft jet floor.
        double r = GroundNavigator.AdaptiveCornerRadiusFt(AircraftCategory.Jet, deflectionDeg: 118.0, incomingRunFt: 22.0, outgoingRunFt: 44.0);
        Assert.Equal(CategoryPerformance.TightTurnFloorRadiusFt(AircraftCategory.Jet), r, precision: 3);
    }

    [Fact]
    public void AdaptiveCornerRadius_AmpleLegs_StaysComfortable()
    {
        // A 90° turn with 100 ft legs has ample room — no tightening, comfortable nose-wheel radius.
        double r = GroundNavigator.AdaptiveCornerRadiusFt(AircraftCategory.Jet, deflectionDeg: 90.0, incomingRunFt: 100.0, outgoingRunFt: 100.0);
        Assert.Equal(CategoryPerformance.NoseWheelTurnRadiusFt(AircraftCategory.Jet), r, precision: 3);
    }

    [Fact]
    public void TightLeg_RelaxesCapToWholeLeg_RoundsAcrossIt()
    {
        // A leg shorter than the comfortable tangent (118° corner, 22 ft leg vs 41.6 ft comfortable tangent)
        // relaxes the 0.45·leg cap to the whole leg, so the tightened arc rounds across it and exits aligned.
        double edgeNm = 22.0 / GeoMath.FeetPerNm;
        double rFt = GroundNavigator.AdaptiveCornerRadiusFt(AircraftCategory.Jet, 118.0, 22.0, 44.0);
        double threshold = GroundNavigator.StraightArrivalThresholdNm(
            cornerTurnDeg: 118.0,
            edgeLengthNm: edgeNm,
            category: AircraftCategory.Jet,
            roundingRadiusFt: rFt,
            isLastSegment: false,
            isStopTarget: false,
            shortEdge: false,
            nextSegmentIsArc: false,
            nextSegmentIsShort: false,
            out bool roundingActive
        );

        Assert.True(roundingActive);
        // r·tan(59°) = 15·1.664 ≈ 25 ft > the 22 ft leg, so the back-off is capped at the whole leg.
        Assert.Equal(edgeNm, threshold, precision: 6);
    }

    [Fact]
    public void GentleCorner_NoRounding_UsesStandardThreshold()
    {
        // A corner shallower than the entry-alignment threshold does not round.
        double threshold = GroundNavigator.StraightArrivalThresholdNm(
            cornerTurnDeg: 20.0,
            edgeLengthNm: 0.05,
            category: AircraftCategory.Jet,
            roundingRadiusFt: CategoryPerformance.NoseWheelTurnRadiusFt(AircraftCategory.Jet),
            isLastSegment: false,
            isStopTarget: false,
            shortEdge: false,
            nextSegmentIsArc: false,
            nextSegmentIsShort: false,
            out bool roundingActive
        );

        Assert.False(roundingActive);
        Assert.Equal(NodeArrivalThresholdNm, threshold, precision: 9);
    }
}
