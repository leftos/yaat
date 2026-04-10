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
    /// Lateral anchor offset (NM) at which the MAP fix is treated as a parallel-offset reference
    /// rather than the runway threshold. Below this distance, the MAP is considered "at the threshold"
    /// and lateral guidance uses the threshold itself.
    /// </summary>
    private const double ParallelOffsetThresholdNm = 0.3;

    /// <summary>
    /// Extracts the final approach course (and optional lateral anchor) for the given procedure.
    /// </summary>
    /// <param name="procedure">Parsed CIFP approach procedure.</param>
    /// <param name="runway">Runway being used by the approach.</param>
    /// <returns>
    /// A result containing the true-heading final approach course and, when the published MAP
    /// is laterally offset from the runway threshold, the anchor coordinates that
    /// <see cref="Phases.Tower.FinalApproachPhase"/> should use as the cross-track reference.
    /// </returns>
    public static FinalApproachCourseResult Extract(CifpApproachProcedure procedure, RunwayInfo runway)
    {
        double declination = MagneticDeclination.GetDeclination(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var navDb = NavigationDatabase.Instance;

        var (mahpLeg, mahpIndex) = FindMahpLeg(procedure.CommonLegs);
        if (mahpLeg is null)
        {
            Log.LogDebug("[FacExtract] {ApproachId}: no MAHP leg found in CommonLegs, falling back to runway heading", procedure.ApproachId);
            return new FinalApproachCourseResult(runway.TrueHeading, AnchorLat: null, AnchorLon: null);
        }

        TrueHeading? course = mahpLeg.PathTerminator switch
        {
            CifpPathTerminator.CF or CifpPathTerminator.FA => CourseFromOutboundField(mahpLeg, declination),
            CifpPathTerminator.TF or CifpPathTerminator.DF => CourseFromBearing(procedure.CommonLegs, mahpIndex, runway, navDb),
            _ => null,
        };

        if (course is null)
        {
            Log.LogDebug(
                "[FacExtract] {ApproachId}: could not extract course from MAHP leg ({Pt} {Fix}), falling back to runway heading",
                procedure.ApproachId,
                mahpLeg.PathTerminator,
                mahpLeg.FixIdentifier
            );
            course = runway.TrueHeading;
        }

        var (anchorLat, anchorLon) = DetermineAnchor(mahpLeg, runway, navDb);

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

    private static (CifpLeg? Leg, int Index) FindMahpLeg(IReadOnlyList<CifpLeg> legs)
    {
        for (int i = 0; i < legs.Count; i++)
        {
            if (legs[i].FixRole == CifpFixRole.MAHP)
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

    private static TrueHeading? CourseFromBearing(IReadOnlyList<CifpLeg> legs, int mahpIndex, RunwayInfo runway, NavigationDatabase navDb)
    {
        var prev = FindPreviousLeg(legs, mahpIndex);
        if (prev is null)
        {
            return null;
        }

        var prevPos = ResolveFixPosition(prev.FixIdentifier, runway, navDb);
        var mahpPos = ResolveFixPosition(legs[mahpIndex].FixIdentifier, runway, navDb);
        if (prevPos is null || mahpPos is null)
        {
            return null;
        }

        double bearing = GeoMath.BearingTo(prevPos.Value.Lat, prevPos.Value.Lon, mahpPos.Value.Lat, mahpPos.Value.Lon);
        return new TrueHeading(bearing);
    }

    private static CifpLeg? FindPreviousLeg(IReadOnlyList<CifpLeg> legs, int mahpIndex)
    {
        for (int i = mahpIndex - 1; i >= 0; i--)
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

    private static (double? Lat, double? Lon) DetermineAnchor(CifpLeg mahpLeg, RunwayInfo runway, NavigationDatabase navDb)
    {
        // Runway pseudo-fixes always anchor at the threshold — no offset.
        if (mahpLeg.FixIdentifier.StartsWith("RW", StringComparison.Ordinal))
        {
            return (null, null);
        }

        var pos = navDb.GetFixPosition(mahpLeg.FixIdentifier);
        if (pos is null)
        {
            return (null, null);
        }

        // If the MAP fix is more than ParallelOffsetThresholdNm from the runway threshold,
        // treat it as a parallel-offset anchor (e.g. KDCA LDA-X 19 ZAXEB).
        double distNm = GeoMath.DistanceNm(runway.ThresholdLatitude, runway.ThresholdLongitude, pos.Value.Lat, pos.Value.Lon);
        if (distNm > ParallelOffsetThresholdNm)
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
