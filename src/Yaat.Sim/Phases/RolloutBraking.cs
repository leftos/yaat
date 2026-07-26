namespace Yaat.Sim.Phases;

/// <summary>
/// Braking kinematics and limits shared by the two rollout phases — <see cref="Tower.LandingPhase"/>, which
/// plans deceleration toward a chosen exit, and <see cref="Ground.RunwayExitPhase"/>, which has to decide
/// whether a late exit change is still something the aircraft could brake for. Both answer the same question
/// ("can this aircraft be at that exit's turn-off speed by its branch point?"), so the arithmetic lives in one
/// place rather than being restated per phase.
/// </summary>
public static class RolloutBraking
{
    /// <summary>
    /// Deceleration ceiling (kts/s) a pilot will use for an exit the controller named explicitly — firmer than
    /// the comfortable rate used for the pilot's own default selection, but short of the max-effort
    /// <see cref="CategoryPerformance.ExpediteExitDecelRate"/> reserved for <c>EXP</c>.
    /// </summary>
    public const double FirmBrakingRateKtsPerSec = 5.0;

    /// <summary>
    /// Speed margin (kts) over an exit's turn-off speed that still counts as slow enough to take it. Absorbs
    /// discrete-tick overshoot so a candidate is not rejected over a fraction of a knot.
    /// </summary>
    public const double TurnOffSpeedToleranceKts = 3.0;

    /// <summary>
    /// Deceleration (kts/s) needed to go from <paramref name="currentGroundSpeedKts"/> to
    /// <paramref name="targetSpeedKts"/> over <paramref name="distanceNm"/>, from v_f² = v_i² - 2·a·d.
    /// A non-positive distance yields <see cref="FirmBrakingRateKtsPerSec"/> — there is no room left, so the
    /// answer is "at least firm braking" rather than a division by zero.
    /// </summary>
    public static double RequiredDecelKtsPerSec(double currentGroundSpeedKts, double targetSpeedKts, double distanceNm)
    {
        double currentFps = currentGroundSpeedKts * GeoMath.FeetPerNm / 3600.0;
        double targetFps = targetSpeedKts * GeoMath.FeetPerNm / 3600.0;
        double distFt = distanceNm * GeoMath.FeetPerNm;

        if (distFt <= 0)
        {
            return FirmBrakingRateKtsPerSec;
        }

        double requiredDecelFps2 = ((currentFps * currentFps) - (targetFps * targetFps)) / (2.0 * distFt);
        return requiredDecelFps2 * 3600.0 / GeoMath.FeetPerNm;
    }

    /// <summary>
    /// Distance (nm) needed to brake between two speeds at a given rate — the inverse of
    /// <see cref="RequiredDecelKtsPerSec"/>: d = (v_i² - v_f²) / (2·a).
    /// </summary>
    public static double BrakingDistanceNm(double fromSpeedKts, double toSpeedKts, double decelRateKtsPerSec)
    {
        if (decelRateKtsPerSec <= 0)
        {
            return 0;
        }

        double fromFps = fromSpeedKts * GeoMath.FeetPerNm / 3600.0;
        double toFps = toSpeedKts * GeoMath.FeetPerNm / 3600.0;
        double decelFps2 = decelRateKtsPerSec * GeoMath.FeetPerNm / 3600.0;
        double distFt = ((fromFps * fromFps) - (toFps * toFps)) / (2.0 * decelFps2);
        return distFt / GeoMath.FeetPerNm;
    }
}
