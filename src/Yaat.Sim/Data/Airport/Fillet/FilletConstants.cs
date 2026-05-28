namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>Shared fillet geometry thresholds (legacy + V2).</summary>
public static class FilletConstants
{
    public const double MinFilletAngleDeg = 15.0;
    public const double CollinearThresholdDeg = 15.0;
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
