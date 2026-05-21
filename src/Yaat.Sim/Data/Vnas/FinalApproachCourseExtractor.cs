using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Derives the published final approach course (and lateral anchor for offset approaches)
/// from a parsed CIFP approach procedure.
///
/// Most consumers historically used <c>RunwayInfo.TrueHeading</c> as the "final approach course",
/// which is wrong for offset approaches (LDA, RNAV with offset legs, VOR offset). The CIFP itself
/// publishes the correct course on each leg's <c>OutboundCourse</c> field for CF/FA terminators,
/// or implicitly via the bearing between consecutive fixes for TF/DF terminators.
///
/// See <c>docs/plans/...</c> (or git history of this file) for the research findings that led
/// to this design — KSFO R10L (TF legs, no published course), KCCR S19R (CF legs, 18° offset),
/// KDCA X19-Z (parallel-offset LDA), and KMCE B12 (LOC BC) were the canonical test cases.
/// </summary>
public static class FinalApproachCourseExtractor
{
    private static readonly ILogger Log = SimLog.CreateLogger("FinalApproachCourseExtractor");

    /// <summary>
    /// Lateral cross-track distance (NM) at which the MAP fix is treated as a parallel-offset
    /// anchor rather than the runway threshold. Compared against the displacement of the MAP
    /// fix from the runway-extended-centerline (NOT the straight-line distance to the
    /// threshold), so MAP fixes that are short of the threshold but on the centerline are
    /// correctly classified as "not offset".
    ///
    /// 0.05 NM ≈ 300 ft, comfortably wider than the widest runway (~200 ft) and CIFP
    /// coordinate truncation noise (~1.8 m), but narrow enough to catch real LDA offsets
    /// (KDCA LDA-X 19 ZAXEB is ~600 ft laterally offset from runway 19's extended centerline).
    /// </summary>
    private const double ParallelOffsetCrossTrackNm = 0.05;

    /// <summary>
    /// Extracts the final approach course (and optional lateral anchor) for the given procedure.
    /// </summary>
    /// <param name="procedure">Parsed CIFP approach procedure.</param>
    /// <param name="runway">Runway being used by the approach.</param>
    /// <param name="navDb">Navigation database used to resolve TF/DF MAP-leg predecessor fix positions and to decide whether the MAP is laterally offset from the runway centerline.</param>
    /// <returns>
    /// A result containing the true-heading final approach course and, when the published MAP
    /// is laterally offset from the runway threshold, the anchor coordinates that
    /// <see cref="Phases.Tower.FinalApproachPhase"/> should use as the cross-track reference.
    /// </returns>
    public static FinalApproachCourseResult Extract(CifpApproachProcedure procedure, RunwayInfo runway, NavigationDatabase navDb)
    {
        double declination = MagneticDeclination.GetDeclination(runway.ThresholdLatitude, runway.ThresholdLongitude);

        var (mapLeg, mapIndex) = FindMapLeg(procedure.CommonLegs);
        if (mapLeg is null)
        {
            Log.LogDebug("[FacExtract] {ApproachId}: no MAP leg found in CommonLegs, falling back to runway heading", procedure.ApproachId);
            return new FinalApproachCourseResult(runway.TrueHeading, AnchorLat: null, AnchorLon: null);
        }

        TrueHeading? course = mapLeg.PathTerminator switch
        {
            CifpPathTerminator.CF or CifpPathTerminator.FA => CourseFromOutboundField(mapLeg, declination),
            CifpPathTerminator.TF or CifpPathTerminator.DF => CourseFromBearing(procedure.CommonLegs, mapIndex, runway, navDb),
            _ => null,
        };

        if (course is null)
        {
            Log.LogDebug(
                "[FacExtract] {ApproachId}: could not extract course from MAP leg ({Pt} {Fix}), falling back to runway heading",
                procedure.ApproachId,
                mapLeg.PathTerminator,
                mapLeg.FixIdentifier
            );
            course = runway.TrueHeading;
        }

        var (anchorLat, anchorLon) = DetermineAnchor(mapLeg, runway, navDb);

        Log.LogDebug(
            "[FacExtract] {ApproachId}: course={Course:F1} (rwy {RwyHdg:F1}, declination {Dec:F1}), anchor={Anchor}",
            procedure.ApproachId,
            course.Value.Degrees,
            runway.TrueHeading.Degrees,
            declination,
            anchorLat is null ? "threshold" : $"({anchorLat:F4}, {anchorLon:F4})"
        );

        return new FinalApproachCourseResult(course.Value, anchorLat, anchorLon);
    }

    private static (CifpLeg? Leg, int Index) FindMapLeg(IReadOnlyList<CifpLeg> legs)
    {
        for (int i = 0; i < legs.Count; i++)
        {
            if (legs[i].FixRole == CifpFixRole.MAP)
            {
                return (legs[i], i);
            }
        }
        return (null, -1);
    }

    private static TrueHeading? CourseFromOutboundField(CifpLeg leg, double declination)
    {
        if (leg.OutboundCourse is not { } magCourse)
        {
            return null;
        }
        return new MagneticHeading(magCourse).ToTrue(declination);
    }

    private static TrueHeading? CourseFromBearing(IReadOnlyList<CifpLeg> legs, int mapIndex, RunwayInfo runway, NavigationDatabase navDb)
    {
        var prev = FindPreviousLeg(legs, mapIndex);
        if (prev is null)
        {
            return null;
        }

        var prevPos = ResolveFixPosition(prev.FixIdentifier, runway, navDb);
        var mapPos = ResolveFixPosition(legs[mapIndex].FixIdentifier, runway, navDb);
        if (prevPos is null || mapPos is null)
        {
            return null;
        }

        double bearing = GeoMath.BearingTo(prevPos.Value.Lat, prevPos.Value.Lon, mapPos.Value.Lat, mapPos.Value.Lon);
        return new TrueHeading(bearing);
    }

    private static CifpLeg? FindPreviousLeg(IReadOnlyList<CifpLeg> legs, int mapIndex)
    {
        for (int i = mapIndex - 1; i >= 0; i--)
        {
            var leg = legs[i];
            // Skip continuation records (parser tags them with PathTerminator.Other and may have empty fix id)
            if (leg.PathTerminator == CifpPathTerminator.Other || string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }
            return leg;
        }
        return null;
    }

    private static (double Lat, double Lon)? ResolveFixPosition(string fixId, RunwayInfo runway, NavigationDatabase navDb)
    {
        if (string.IsNullOrEmpty(fixId))
        {
            return null;
        }
        // Runway pseudo-fixes (RW28R, RW10L, etc.) are not in NavigationDatabase — fall back to threshold.
        if (fixId.StartsWith("RW", StringComparison.Ordinal))
        {
            return (runway.ThresholdLatitude, runway.ThresholdLongitude);
        }
        return navDb.GetFixPosition(fixId);
    }

    private static (double? Lat, double? Lon) DetermineAnchor(CifpLeg mapLeg, RunwayInfo runway, NavigationDatabase navDb)
    {
        // Runway pseudo-fixes always anchor at the threshold — no offset.
        if (mapLeg.FixIdentifier.StartsWith("RW", StringComparison.Ordinal))
        {
            return (null, null);
        }

        var pos = navDb.GetFixPosition(mapLeg.FixIdentifier);
        if (pos is null)
        {
            return (null, null);
        }

        // Check lateral displacement of the MAP fix from the runway extended centerline
        // (not absolute distance — a MAP fix short of the threshold but on the centerline
        // should NOT be treated as parallel-offset). The reference line is the runway heading
        // through the threshold; cross-track distance is how far the MAP fix sits beside it.
        double xte = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(pos.Value.Lat, pos.Value.Lon, runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading)
        );

        if (xte > ParallelOffsetCrossTrackNm)
        {
            return (pos.Value.Lat, pos.Value.Lon);
        }

        return (null, null);
    }
}

/// <summary>
/// Result of <see cref="FinalApproachCourseExtractor.Extract"/>.
/// </summary>
/// <param name="Course">True heading the aircraft should track on final approach.</param>
/// <param name="AnchorLat">
/// Latitude of the lateral anchor for cross-track guidance, or null if the anchor is the runway threshold.
/// Non-null for parallel-offset approaches whose published MAP fix is laterally offset from the threshold.
/// </param>
/// <param name="AnchorLon">Longitude of the lateral anchor; see <see cref="AnchorLat"/>.</param>
public sealed record FinalApproachCourseResult(TrueHeading Course, double? AnchorLat, double? AnchorLon);
