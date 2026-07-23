namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>Shared fillet geometry thresholds.</summary>
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

    /// <summary>
    /// Fillet radius (ft) for a shallow-angle (≤45°) runway-to-taxiway junction — a rapid-exit taxiway.
    /// Sized so the arc's <see cref="GroundArc.MaxSafeSpeedKts"/> reaches a real high-speed turn-off speed
    /// (~30 kt for a jet) at the ~0.12–0.13 g lateral acceleration implied by ICAO Annex 14's rapid-exit
    /// radius/speed pairs (and FAA AC 150/5300-13B): r = v²/a = (30 kt)² / 0.13 g ≈ 613 ft, rounded to 600 ft (~29.7 kt, floored to 30 by the
    /// <see cref="CategoryPerformance.CornerSpeedForAngle"/> angle ceiling for ≤30° turns). A tighter radius
    /// modeled the exit as an ordinary ~15-kt taxiway corner, forcing aircraft to crawl through a rapid exit.
    /// Self-limiting: <see cref="FilletGeometry.ComputeIdealTangentFt"/> clamps the radius to what the arm
    /// tangents and <see cref="MaxTangentDistFt"/> allow, so junctions with short taxiway arms auto-shrink.
    /// </summary>
    public const double HighSpeedExitRadiusFt = 600.0;
    public const double RunwayExitRadiusFt = 100.0;
    public const double RampRadiusFt = 50.0;
    public const double MaxTangentDistFt = 150.0;

    public const double CoincidentNodeThresholdFt = 5.0;
    public const double CoincidentNodeThresholdNm = CoincidentNodeThresholdFt / GeoMath.FeetPerNm;

    /// <summary>Multi-cut ideal coalesce only; kept below <see cref="MinArmSegmentGapFt"/> so gap enforcement can demote.</summary>
    public const double IdealCoalesceThresholdFt = 2.0;

    public const double CoincidentTangentMergeThresholdFt = 1.0;
    public const double CoincidentTangentMergeThresholdNm = CoincidentTangentMergeThresholdFt / GeoMath.FeetPerNm;

    /// <summary>
    /// Minimum straight-line clearance (ft) between the opposing tangent-cut sets of two adjacent
    /// junctions on a shared arm. A straight shorter than the navigator's pure-pursuit look-ahead
    /// cap is an orbit trap: the look-ahead point jumps past the whole segment before the aircraft
    /// can converge on it (observed at OAK: a 9 ft runway-centerline sliver between two junctions'
    /// widened cuts that a taxiing aircraft circled indefinitely). Opposing cut sets that would
    /// leave less than this are scaled down to restore the clearance; sets already overlapping or
    /// within <see cref="CoincidentNodeThresholdFt"/> instead fuse into a single shared tangent
    /// node (no straight at all), which the navigator handles as an arc-to-arc transition.
    /// </summary>
    public const double MinSharedArmClearGapFt = Phases.Ground.GroundNavigator.LookAheadCapFt;

    public const double RadiusFloorFt = 5.0;
    public const double DistortionThreshold = 2.0;
    public const double AsymmetryThreshold = 2.0;
    public const double MinArmSegmentGapFt = 5.0;
}
