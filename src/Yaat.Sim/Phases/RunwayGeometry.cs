namespace Yaat.Sim.Phases;

/// <summary>
/// Geometric relationships between two runways at the same airport, shared by the
/// sidestep path and the pattern runway-transition builders.
/// </summary>
public static class RunwayGeometry
{
    /// <summary>
    /// Maximum lateral separation (nm) between two parallel runway centerlines for
    /// EF to be treated as a sidestep instead of a fresh pattern-entry build. AIM
    /// §5-4-19.1 anchors the side-step maneuver at runways "no more than 1200 feet"
    /// between centerlines; 0.25 nm (~1520 ft) leaves a small buffer for nominal
    /// mag-variation differences while still excluding non-parallel pairs (e.g.
    /// OAK 28L/30, where the headings alone already disqualify them).
    /// </summary>
    public const double MaxSidestepCenterlineSeparationNm = 0.25;

    /// <summary>
    /// Maximum runway-heading difference (degrees) between two runways for EF to be
    /// treated as a sidestep. True parallels are typically &lt;1°; CIFP / mag-var
    /// rounding can push apparent deltas to a few degrees; 5° is a safe cap that
    /// still excludes non-parallel pairs.
    /// </summary>
    public const double MaxSidestepHeadingDeltaDeg = 5.0;

    /// <summary>
    /// True when <paramref name="target"/> is parallel to <paramref name="current"/>
    /// and their centerlines are within <see cref="MaxSidestepCenterlineSeparationNm"/>.
    /// Both runways must be at the same airport; the heading delta must be within
    /// <see cref="MaxSidestepHeadingDeltaDeg"/>.
    /// </summary>
    public static bool AreCloseParallels(RunwayInfo a, RunwayInfo b)
    {
        if (!string.Equals(a.AirportId, b.AirportId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (a.TrueHeading.AbsAngleTo(b.TrueHeading) > MaxSidestepHeadingDeltaDeg)
        {
            return false;
        }

        double crossTrackNm = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(
                new LatLon(b.ThresholdLatitude, b.ThresholdLongitude),
                new LatLon(a.ThresholdLatitude, a.ThresholdLongitude),
                a.TrueHeading
            )
        );
        return crossTrackNm <= MaxSidestepCenterlineSeparationNm;
    }
}
