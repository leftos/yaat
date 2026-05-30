using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="GroundNavigatorV2.StraightArrivalThresholdNm"/> — the tangent
/// corner-rounding arrival threshold, including the short-leg guard that prevents an inverted
/// <c>Math.Clamp</c> range.
/// </summary>
public class GroundNavigatorV2ThresholdTests
{
    // Mirror of the GroundNavigatorV2 private constants the threshold falls back to.
    private const double FinalNodeArrivalThresholdNm = 0.0003;
    private const double NodeArrivalThresholdNm = 0.015;

    [Fact]
    public void ShortLegWithSharpCorner_DoesNotThrow_FallsBackToFinalThreshold()
    {
        // A leg so short that 0.45*len <= FinalNodeArrivalThresholdNm has no room to round. Regression
        // for the Math.Clamp(min>max) crash surfaced by the all-V2 sweep on a near-zero-length edge:
        // before the guard this combination (sharp corner + tiny leg) threw ArgumentException.
        double threshold = GroundNavigatorV2.StraightArrivalThresholdNm(
            cornerTurnDeg: 90.0,
            edgeLengthNm: 1e-6,
            category: AircraftCategory.Jet,
            isLastSegment: false,
            isStopTarget: false,
            shortEdge: true,
            nextSegmentIsArc: false,
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
        double threshold = GroundNavigatorV2.StraightArrivalThresholdNm(
            cornerTurnDeg: 90.0,
            edgeLengthNm: 0.05,
            category: AircraftCategory.Jet,
            isLastSegment: false,
            isStopTarget: false,
            shortEdge: false,
            nextSegmentIsArc: false,
            out bool roundingActive
        );

        Assert.True(roundingActive);
        double rFt = CategoryPerformance.NoseWheelTurnRadiusFt(AircraftCategory.Jet);
        double expectedNm = (rFt * Math.Tan(45.0 * Math.PI / 180.0)) / GeoMath.FeetPerNm;
        Assert.Equal(expectedNm, threshold, precision: 6);
    }

    [Fact]
    public void GentleCorner_NoRounding_UsesStandardThreshold()
    {
        // A corner shallower than the entry-alignment threshold does not round.
        double threshold = GroundNavigatorV2.StraightArrivalThresholdNm(
            cornerTurnDeg: 20.0,
            edgeLengthNm: 0.05,
            category: AircraftCategory.Jet,
            isLastSegment: false,
            isStopTarget: false,
            shortEdge: false,
            nextSegmentIsArc: false,
            out bool roundingActive
        );

        Assert.False(roundingActive);
        Assert.Equal(NodeArrivalThresholdNm, threshold, precision: 9);
    }
}
