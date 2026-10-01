using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// True when an aircraft on an instrument approach to a runway is inside the final approach fix: established on the
/// final — out on the approach side of the threshold (<see cref="AirborneFollowHelper.AlongFinalNm"/> at or beyond
/// zero) and within <see cref="AirborneFollowHelper.OnFinalMaxCrossTrackNm"/> of the extended centerline — and no
/// further out than <see cref="ApproachGateDatabase.InsideFafLimitNm"/>, measured to the landing threshold (the
/// pavement threshold plus the published displacement, the datum the approach is flown on). Off the final (abeam,
/// out on a feeder, past the threshold) is outside it (7110.65 §5-7-1.b.4: inside the final approach fix <em>on
/// final</em>). <paramref name="layout"/> carries the runway end's published threshold displacement; pass null when
/// no airport map is available, which reads the runway as undisplaced.
/// </summary>
public static class FinalApproachFix
{
    public static bool IsInside(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout? layout)
    {
        double displacementNm = LandingThreshold.DisplacementFt(runway, layout) / GeoMath.FeetPerNm;
        double alongNm = AirborneFollowHelper.AlongFinalNm(aircraft.Position, runway);
        double? fafNm = ApproachGateDatabase.GetFafDistanceNm(runway.AirportId, runway.Designator, displacementNm);
        if ((alongNm < 0.0) || (alongNm + displacementNm > ApproachGateDatabase.InsideFafLimitNm(fafNm)))
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
