using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Situation;

/// <summary>
/// Whether an uncleared IFR arrival is established on the final approach course of its expected instrument approach
/// (<see cref="SituationFlags.EstablishedOnExpectedFinal"/>), by geometry against the procedure CAPP would resolve from
/// that expectation: on its published final course, tracking it, outside its final approach fix within range, at or above
/// the segment's published altitude, and closer to that course than to any other runway's final. The cheap tests run
/// first; the navdata is read only for an aircraft that passes them and is near its destination.
/// </summary>
public static class ExpectedFinalGeometry
{
    private static readonly ILogger Log = SimLog.CreateLogger("ExpectedFinalGeometry");

    /// <summary>
    /// Farthest (NM) out from the threshold, along the final approach course, an aircraft counts as established on it.
    /// AIM 1-1-9.b.5: the localizer provides course guidance … from a distance of 18 NM; applied to every approach type for
    /// consistency.
    /// </summary>
    public const double MaxRangeNm = 18.0;

    /// <summary>
    /// How far (NM) beyond <see cref="MaxRangeNm"/> from the destination's reference point the geometry is still computed: a
    /// judgement call covering the distance from the airport reference point to a runway threshold.
    /// </summary>
    public const double AirportGateMarginNm = 5.0;

    /// <summary>
    /// How far (ft) below the segment's published altitude an aircraft still counts as established. 7110.65 §4-8-1.b.2
    /// NOTE 3: an aircraft is not established on an approach until at or above an altitude published on that segment; the
    /// 100 ft margin is a judgement call.
    /// </summary>
    public const double SegmentAltitudeMarginFt = 100.0;

    /// <summary>
    /// Whether <paramref name="ac"/> is established on its expected approach's final course with no approach clearance:
    /// airborne, IFR, not going around, an instrument (not visual) approach expected, not cleared and no clearance pending,
    /// near the destination, and on the final of at least one procedure the expectation resolves to there.
    /// </summary>
    /// <param name="ac">The aircraft.</param>
    /// <param name="clearedForDescent">Whether the aircraft holds an approach clearance with descent on it this second.</param>
    public static bool IsEstablished(AircraftState ac, bool clearedForDescent)
    {
        if ((ExpectedInstrumentApproach(ac, clearedForDescent) is not { } expected) || (TryGetDestination(ac) is not { } destination))
        {
            return false;
        }

        foreach (string id in destination.NavDb.ResolveApproachCandidates(destination.Airport, expected))
        {
            if ((ResolveFinal(destination, id) is { } final) && IsEstablishedOn(ac, final.Procedure, final.Runway, destination.NavDb))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The expected approach of an airborne IFR aircraft, not going around, with no approach clearance with descent (a
    /// JFAC/JLOC lateral join is none) and none pending; null when any of that fails or the expectation is a visual.
    /// </summary>
    private static string? ExpectedInstrumentApproach(AircraftState ac, bool clearedForDescent) =>
        IsUnclearedIfrAirborne(ac, clearedForDescent)
        && (ac.Approach.Expected is { Length: > 0 } expected)
        && (!ApproachIds.TryParseVisual(expected, out _))
            ? expected
            : null;

    private static bool IsUnclearedIfrAirborne(AircraftState ac, bool clearedForDescent) =>
        (!ac.IsOnGround)
        && (!ac.FlightPlan.IsVfr)
        && (ac.Phases?.CurrentPhase is not GoAroundPhase)
        && (!clearedForDescent)
        && (ac.Approach.PendingClearance is null);

    /// <summary>
    /// The destination as the navdata knows it, when the aircraft is within <see cref="MaxRangeNm"/> plus
    /// <see cref="AirportGateMarginNm"/> of its reference point; null with no destination, no navdata, no airport position
    /// (fails closed) or farther out.
    /// </summary>
    private static Destination? TryGetDestination(AircraftState ac)
    {
        string destination = ac.FlightPlan.Destination;
        if (string.IsNullOrEmpty(destination) || (NavigationDatabase.InstanceOrNull is not { } navDb))
        {
            return null;
        }

        string airport = navDb.TryResolveAirport(destination, out string canonical) ? canonical : destination;
        if (navDb.GetAirportPosition(airport) is not { } reference)
        {
            return null;
        }

        double distanceNm = GeoMath.DistanceNm(ac.Position, new LatLon(reference.Lat, reference.Lon));
        return (distanceNm <= (MaxRangeNm + AirportGateMarginNm)) ? new Destination(navDb, airport) : null;
    }

    /// <summary>The procedure <paramref name="approachId"/> names at the destination and its runway; null for a circling-only one.</summary>
    private static ResolvedFinal? ResolveFinal(Destination destination, string approachId) =>
        (destination.NavDb.GetApproach(destination.Airport, approachId) is { Runway: { } runwayId } procedure)
        && (destination.NavDb.GetRunway(destination.Airport, runwayId) is { } runway)
            ? new ResolvedFinal(procedure, runway)
            : null;

    /// <summary>
    /// On <paramref name="procedure"/>'s published final course (measured from its lateral anchor, else the threshold) and
    /// tracking it, outside its final approach fix and within <see cref="MaxRangeNm"/> of the threshold, at or above the
    /// segment's altitude, and closer to that course than to any other runway's final.
    /// </summary>
    private static bool IsEstablishedOn(AircraftState ac, CifpApproachProcedure procedure, RunwayInfo runway, NavigationDatabase navDb)
    {
        var frame = FinalFrame.Of(runway, FinalApproachCourseExtractor.Extract(procedure, runway, navDb));
        double crossTrackNm = Math.Abs(frame.CrossTrackNm(ac.Position));
        if (
            (crossTrackNm > AirborneFollowHelper.OnFinalMaxCrossTrackNm)
            || (ac.TrueTrack.AbsAngleTo(frame.Course) > AirborneFollowHelper.OnFinalMaxTrackOffDeg)
        )
        {
            return false;
        }

        if (FafOutNm(procedure, frame, navDb) is not { } fafOutNm)
        {
            if (Log.IsEnabled(LogLevel.Trace))
            {
                Log.LogTrace("[ExpectedFinal] {ApproachId}: no coded final approach fix, skipped", procedure.ApproachId);
            }
            return false;
        }

        double outNm = frame.OutNm(ac.Position);
        if ((outNm <= fafOutNm) || (outNm > MaxRangeNm))
        {
            return false;
        }

        return IsAtOrAboveSegmentAltitude(ac, procedure, frame, outNm, navDb) && IsNearestFinal(ac, runway, crossTrackNm, navDb);
    }

    /// <summary>How far the procedure's final approach fix is out along the final; null when it has none or it cannot be placed.</summary>
    private static double? FafOutNm(CifpApproachProcedure procedure, FinalFrame frame, NavigationDatabase navDb)
    {
        for (int i = 0; i < procedure.CommonLegs.Count; i++)
        {
            CifpLeg leg = procedure.CommonLegs[i];
            if (leg.FixRole == CifpFixRole.FAF)
            {
                return (leg.ResolveFixPosition(navDb) is { } position) ? frame.OutNm(new LatLon(position.Lat, position.Lon)) : null;
            }
        }

        return null;
    }

    /// <summary>
    /// At or above, less <see cref="SegmentAltitudeMarginFt"/>, the published minimum (at-or-above, mandatory or
    /// glideslope-intercept, the last commonly coded at an ILS's FAF or IF) of the nearest fix ahead of the aircraft along
    /// the final, up to and including the final approach fix, counting only fixes on the final course; true when no such
    /// fix carries one. Taking the nearest fix ahead is a deliberate, lenient judgement call: the strict reading of
    /// 7110.65 §4-8-1.b.2 NOTE 3 uses the segment's fix behind the aircraft.
    /// </summary>
    /// <param name="ac">The aircraft.</param>
    /// <param name="procedure">The approach procedure.</param>
    /// <param name="frame">The procedure's final approach course.</param>
    /// <param name="outNm">How far the aircraft is out along the final.</param>
    /// <param name="navDb">The navigation database the fixes are placed from.</param>
    private static bool IsAtOrAboveSegmentAltitude(
        AircraftState ac,
        CifpApproachProcedure procedure,
        FinalFrame frame,
        double outNm,
        NavigationDatabase navDb
    )
    {
        double nearestOutNm = double.NegativeInfinity;
        int? floorFt = null;
        foreach (CifpLeg leg in procedure.CommonLegs)
        {
            if ((QualifyingFix(leg, frame, navDb) is { } fix) && (fix.OutNm < outNm) && (fix.OutNm > nearestOutNm))
            {
                nearestOutNm = fix.OutNm;
                floorFt = fix.FloorFt;
            }

            if (leg.FixRole == CifpFixRole.FAF)
            {
                break;
            }
        }

        return (floorFt is not { } ft) || (ac.Altitude >= (ft - SegmentAltitudeMarginFt));
    }

    /// <summary>
    /// The leg's fix, how far it is out along the final and its published minimum, when it carries one and lies within
    /// <see cref="AirborneFollowHelper.OnFinalMaxCrossTrackNm"/> of the final course; null otherwise.
    /// </summary>
    private static (double OutNm, int FloorFt)? QualifyingFix(CifpLeg leg, FinalFrame frame, NavigationDatabase navDb)
    {
        if ((leg.Altitude is not { } altitude) || (!IsPublishedMinimum(altitude.Type)) || (leg.ResolveFixPosition(navDb) is not { } position))
        {
            return null;
        }

        var point = new LatLon(position.Lat, position.Lon);
        return (Math.Abs(frame.CrossTrackNm(point)) <= AirborneFollowHelper.OnFinalMaxCrossTrackNm)
            ? (frame.OutNm(point), altitude.Altitude1Ft)
            : null;
    }

    private static bool IsPublishedMinimum(CifpAltitudeRestrictionType type) =>
        type is CifpAltitudeRestrictionType.AtOrAbove or CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.GlideSlopeIntercept;

    /// <summary>
    /// No other runway end at the airport has a final the aircraft is closer to than <paramref name="crossTrackNm"/>, its
    /// cross-track from the procedure's final course: an end within <see cref="AirborneFollowHelper.OnFinalMaxTrackOffDeg"/>
    /// of the aircraft's track that it is on the approach side of and within <see cref="MaxRangeNm"/> of, along its
    /// centerline.
    /// </summary>
    private static bool IsNearestFinal(AircraftState ac, RunwayInfo runway, double crossTrackNm, NavigationDatabase navDb)
    {
        foreach (RunwayInfo pavement in navDb.GetRunways(runway.AirportId))
        {
            if ((OtherFinalCrossTrackNm(ac, pavement, runway) is { } otherNm) && (otherNm < crossTrackNm))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The aircraft's cross-track from the centerline of <paramref name="pavement"/>'s end aligned with its track, when that
    /// end is not <paramref name="runway"/>, is within <see cref="AirborneFollowHelper.OnFinalMaxTrackOffDeg"/> of the track,
    /// and the aircraft is on its approach side within <see cref="MaxRangeNm"/>; null otherwise.
    /// </summary>
    private static double? OtherFinalCrossTrackNm(AircraftState ac, RunwayInfo pavement, RunwayInfo runway)
    {
        RunwayInfo end = RunwayOccupancy.AlignedEnd(ac.TrueTrack.Degrees, pavement);
        if (AirborneFollowHelper.IsSameRunway(end, runway) || (ac.TrueTrack.AbsAngleTo(end.TrueHeading) > AirborneFollowHelper.OnFinalMaxTrackOffDeg))
        {
            return null;
        }

        var threshold = new LatLon(end.ThresholdLatitude, end.ThresholdLongitude);
        double outNm = -GeoMath.AlongTrackDistanceNm(ac.Position, threshold, end.TrueHeading);
        return ((outNm > 0.0) && (outNm <= MaxRangeNm))
            ? Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, end.TrueHeading))
            : null;
    }

    /// <summary>The destination's canonical id and the navdata it was resolved from.</summary>
    private sealed record Destination(NavigationDatabase NavDb, string Airport);

    /// <summary>A procedure with a runway, and that runway.</summary>
    private sealed record ResolvedFinal(CifpApproachProcedure Procedure, RunwayInfo Runway);

    /// <summary>
    /// A final approach course: its threshold, along which distances out are measured, and its lateral anchor (the threshold
    /// unless the procedure publishes an offset one), from which cross-track is measured.
    /// </summary>
    private readonly record struct FinalFrame(LatLon Threshold, LatLon Anchor, TrueHeading Course)
    {
        public static FinalFrame Of(RunwayInfo runway, FinalApproachCourseResult fac)
        {
            var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
            LatLon anchor = ((fac.AnchorLat is { } lat) && (fac.AnchorLon is { } lon)) ? new LatLon(lat, lon) : threshold;
            return new FinalFrame(threshold, anchor, fac.Course);
        }

        /// <summary>How far <paramref name="point"/> is out from the threshold along the course (negative past it).</summary>
        public double OutNm(LatLon point) => -GeoMath.AlongTrackDistanceNm(point, Threshold, Course);

        /// <summary>How far <paramref name="point"/> is right of the course, measured from the anchor.</summary>
        public double CrossTrackNm(LatLon point) => GeoMath.SignedCrossTrackDistanceNm(point, Anchor, Course);
    }
}
