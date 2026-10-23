using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// Whether an aircraft on an instrument approach to a runway is inside the final approach fix: established on the
/// final — out on the approach side of the threshold (<see cref="AirborneFollowHelper.AlongFinalNm"/> at or beyond
/// zero) and within <see cref="AirborneFollowHelper.OnFinalMaxCrossTrackNm"/> of the extended centerline — and no
/// further out than a limit measured to the landing threshold (the pavement threshold plus the published displacement,
/// the datum the approach is flown on). Off the final (abeam, out on a feeder, past the threshold) is outside it
/// (7110.65 §5-7-1.b.4: inside the final approach fix <em>on final</em>). Two tests share those checks and differ in the
/// limit:
/// <list type="bullet">
/// <item><see cref="IsInside"/> limits to <see cref="ApproachGateDatabase.InsideFafLimitNm"/>, the published FAF capped at
/// five miles: the speed rule's test (7110.65 §5-7-1.b.4) and the cross-runway FOLLOW refusal's.</item>
/// <item><see cref="IsInsidePublishedFaf"/> limits to the published FAF distance uncapped (five miles when the runway has no
/// published FAF): the test the approach altitude hold and the bare go-around refusal read.</item>
/// </list>
/// <paramref name="layout"/> carries the runway end's published threshold displacement; pass null when no airport map is
/// available, which reads the runway as undisplaced.
/// </summary>
public static class FinalApproachFix
{
    public static bool IsInside(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout? layout) =>
        IsInsideWithin(aircraft, runway, layout, ApproachGateDatabase.InsideFafLimitNm);

    /// <summary>
    /// <see cref="IsInside"/> against the published FAF distance with no five-mile cap; a runway with no published FAF is
    /// limited to <see cref="ApproachGateDatabase.InsideFafCeilingNm"/>.
    /// </summary>
    public static bool IsInsidePublishedFaf(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout? layout) =>
        IsInsideWithin(aircraft, runway, layout, fafNm => fafNm ?? ApproachGateDatabase.InsideFafCeilingNm);

    private static bool IsInsideWithin(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout? layout, Func<double?, double> limitNm)
    {
        double displacementNm = LandingThreshold.DisplacementFt(runway, layout) / GeoMath.FeetPerNm;
        double alongNm = AirborneFollowHelper.AlongFinalNm(aircraft.Position, runway);
        double? fafNm = ApproachGateDatabase.GetFafDistanceNm(runway.AirportId, runway.Designator, displacementNm);
        if ((alongNm < 0.0) || (alongNm + displacementNm > limitNm(fafNm)))
        {
            return false;
        }

        return CrossTrackNm(aircraft, runway) <= AirborneFollowHelper.OnFinalMaxCrossTrackNm;
    }

    /// <summary>The aircraft's absolute distance from the runway's extended centerline, the cross-track <see cref="IsInside"/> tests.</summary>
    public static double CrossTrackNm(AircraftState aircraft, RunwayInfo runway)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        return Math.Abs(GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, threshold, runway.TrueHeading));
    }
}
