namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>Shared fillet geometry thresholds (legacy + V2).</summary>
public static class FilletConstants
{
    public const double MinFilletAngleDeg = 15.0;
    public const double CollinearThresholdDeg = 15.0;

    /// <summary>
    /// Symmetric counterpart to <see cref="CollinearThresholdDeg"/>. A corner whose deflection
    /// (<see cref="FilletGeometry.ComputeTurnAngle"/>) exceeds this is a near-hairpin: the two arms
    /// are within (180 − this)° of parallel — taxiways grazing at an acute angle, not a real turn.
    /// Such a corner can't be filleted (it would degrade to a straight chord no aircraft can taxi),
    /// so no corner is emitted between the pair; they stay connected via the taxiway that bridges
    /// them. 165° ⇒ arms within 15° of parallel.
    /// </summary>
    public const double NearHairpinThresholdDeg = 180.0 - CollinearThresholdDeg;
    public const double DefaultRadiusFt = 75.0;
    public const double HighSpeedExitRadiusFt = 150.0;
    public const double RunwayExitRadiusFt = 100.0;
    public const double RampRadiusFt = 50.0;
    public const double MaxTangentDistFt = 150.0;

    public const double CoincidentNodeThresholdFt = 5.0;
    public const double CoincidentNodeThresholdNm = CoincidentNodeThresholdFt / GeoMath.FeetPerNm;

    /// <summary>V2 multi-cut ideal coalesce only; kept below <see cref="MinArmSegmentGapFt"/> so gap enforcement can demote.</summary>
    public const double IdealCoalesceThresholdFt = 2.0;

    public const double CoincidentTangentMergeThresholdFt = 1.0;
    public const double CoincidentTangentMergeThresholdNm = CoincidentTangentMergeThresholdFt / GeoMath.FeetPerNm;

    public const double RadiusFloorFt = 5.0;
    public const double DistortionThreshold = 2.0;
    public const double AsymmetryThreshold = 2.0;
    public const double MinArmSegmentGapFt = 5.0;
}
