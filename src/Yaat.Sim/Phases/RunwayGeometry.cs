using Yaat.Sim.Data;

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
    /// Maximum runway-heading difference (degrees) between two runways for them to be parallel: the runway-transition
    /// classification and the EF side-step both read it. True parallels are typically &lt;1°; CIFP / mag-var rounding can
    /// push apparent deltas to a few degrees; 5° is a safe cap that still excludes non-parallel pairs.
    /// </summary>
    public const double MaxParallelHeadingDeltaDeg = 5.0;

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are parallel runways: two runways of one
    /// airport (normalised ids, so <c>KOAK</c> and <c>OAK</c> name one field) whose headings differ by at
    /// most <see cref="MaxParallelHeadingDeltaDeg"/>. There is no spacing bound — the AIM P/CG definition
    /// of parallel runways carries none, and AIM §4-3-6.a NOTE 2 lets widely separated parallels be
    /// designated non-parallel, so the designator is no test either. This is the partition the follow and
    /// pattern-transition logic reads: a parallel pair continues the upwind and turns crosswind beyond both
    /// departure ends (AIM §4-3-3 FIG 4-3-3 keys 7 and 8); every other pair crosses midfield.
    /// </summary>
    public static bool AreParallelRunways(RunwayInfo a, RunwayInfo b) =>
        NavigationDatabase.AirportIdsMatch(a.AirportId, b.AirportId) && (a.TrueHeading.AbsAngleTo(b.TrueHeading) <= MaxParallelHeadingDeltaDeg);

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are parallel (<see cref="AreParallelRunways"/>)
    /// and their centerlines are within <see cref="MaxSidestepCenterlineSeparationNm"/>. This is the
    /// side-step geometry only (AIM §5-4-19); the pattern runway transition between two parallel runways
    /// uses <see cref="AreParallelRunways"/>, whose answer does not depend on spacing.
    /// </summary>
    public static bool AreCloseParallels(RunwayInfo a, RunwayInfo b) =>
        AreParallelRunways(a, b) && (CenterlineSeparationNm(a, b) <= MaxSidestepCenterlineSeparationNm);

    private static double CenterlineSeparationNm(RunwayInfo a, RunwayInfo b) =>
        Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(
                new LatLon(b.ThresholdLatitude, b.ThresholdLongitude),
                new LatLon(a.ThresholdLatitude, a.ThresholdLongitude),
                a.TrueHeading
            )
        );
}
