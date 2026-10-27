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
    /// Speed margin (kts) over an exit's turn-off speed that still counts as slow enough to take it. Absorbs
    /// discrete-tick overshoot so a candidate is not rejected over a fraction of a knot.
    /// </summary>
    public const double TurnOffSpeedToleranceKts = 3.0;

    /// <summary>The largest exit angle (degrees) <see cref="TurnEntryLeadNm"/> measures a turn-entry lead for.</summary>
    public const double MaxTurnEntryAngleDeg = 120.0;

    /// <summary>
    /// True for an exit whose turn-off speed is below <see cref="CategoryPerformance.HighSpeedExitSpeed"/>: a standard exit,
    /// turned onto from its turn-entry point short of the branch rather than at the branch itself.
    /// </summary>
    public static bool IsStandardExit(AircraftCategory category, double turnOffSpeed) =>
        turnOffSpeed < CategoryPerformance.HighSpeedExitSpeed(category);

    /// <summary>
    /// How far before its branch point a standard exit's turn begins (nm): the tangent length R·tan(Δ/2) of the first corner the
    /// ground navigator rounds, R the category's <see cref="CategoryPerformance.MainGearTurnRadiusFt"/> and Δ
    /// <paramref name="exitAngleDeg"/>, the exit angle the turn-off speed was classified by, capped at
    /// <see cref="MaxTurnEntryAngleDeg"/> so an exit doubling back near 180° keeps a bounded lead. Zero for a high-speed exit, whose
    /// turn-off is judged at the branch point. The one turn-entry point the rollout judges, plans, hands off and misses an exit
    /// against, and the one the runway exit brakes a re-target to.
    /// </summary>
    public static double TurnEntryLeadNm(AircraftCategory category, double turnOffSpeed, double exitAngleDeg)
    {
        if (!IsStandardExit(category, turnOffSpeed))
        {
            return 0;
        }

        double halfTan = Math.Tan(Math.Min(exitAngleDeg, MaxTurnEntryAngleDeg) * 0.5 * Math.PI / 180.0);
        return CategoryPerformance.MainGearTurnRadiusFt(category) * halfTan / GeoMath.FeetPerNm;
    }

    /// <summary>
    /// The turn-off rate the runway exit keeps from an exit chosen at <paramref name="selectionRate"/>: capped at the category's
    /// max-effort <see cref="CategoryPerformance.ExpediteExitDecelRate"/> (a <c>CLANDF</c> forced rollout commits its exit at
    /// <see cref="Tower.ForcedLandingProfile.RolloutMaxDecelKtsPerSec"/>), and null — the taxi rate — for no rate or one that is
    /// not positive.
    /// </summary>
    public static double? CaptureTurnOffRate(double? selectionRate, AircraftCategory category) =>
        ((selectionRate is { } rate) && (rate > 0)) ? Math.Min(rate, CategoryPerformance.ExpediteExitDecelRate(category)) : null;

    /// <summary>
    /// Deceleration (kts/s) needed to go from <paramref name="currentGroundSpeedKts"/> to
    /// <paramref name="targetSpeedKts"/> over <paramref name="distanceNm"/>, from v_f² = v_i² - 2·a·d.
    /// A non-positive distance yields <paramref name="category"/>'s <see cref="CategoryPerformance.FirmBrakingRate"/> —
    /// there is no room left, so the answer is "at least firm braking" rather than a division by zero.
    /// </summary>
    public static double RequiredDecelKtsPerSec(double currentGroundSpeedKts, double targetSpeedKts, double distanceNm, AircraftCategory category) =>
        distanceNm <= 0
            ? CategoryPerformance.FirmBrakingRate(category)
            : DecelOverDistanceKtsPerSec(currentGroundSpeedKts, targetSpeedKts, distanceNm);

    /// <summary>
    /// Most the crew brakes for an exit the controller named: <see cref="CategoryPerformance.FirmBrakingRate"/>, or the
    /// max-effort <see cref="CategoryPerformance.ExpediteExitDecelRate"/> when it was ordered without delay (<c>EXP</c>). The one
    /// limit a named exit is judged by — at command time and on the tick, on the rollout and on a late re-target.
    /// </summary>
    public static double NamedExitBrakingLimit(AircraftCategory category, bool expedite) =>
        expedite ? CategoryPerformance.ExpediteExitDecelRate(category) : CategoryPerformance.FirmBrakingRate(category);

    /// <summary>
    /// Deceleration (kts/s) that takes <paramref name="currentGroundSpeedKts"/> down to <paramref name="targetSpeedKts"/> — zero
    /// for a stop — over a positive <paramref name="distanceNm"/>, from v_f² = v_i² - 2·a·d. Category-free: the caller owns
    /// what a non-positive distance means (<see cref="RequiredDecelKtsPerSec"/> answers it with the firm rate).
    /// </summary>
    public static double DecelOverDistanceKtsPerSec(double currentGroundSpeedKts, double targetSpeedKts, double distanceNm)
    {
        double currentFps = currentGroundSpeedKts * GeoMath.FeetPerNm / 3600.0;
        double targetFps = targetSpeedKts * GeoMath.FeetPerNm / 3600.0;
        double distFt = distanceNm * GeoMath.FeetPerNm;
        double requiredDecelFps2 = ((currentFps * currentFps) - (targetFps * targetFps)) / (2.0 * distFt);
        return requiredDecelFps2 * 3600.0 / GeoMath.FeetPerNm;
    }

    /// <summary>
    /// Fastest speed (kts) a stop at <paramref name="decelRateKtsPerSec"/> still fits inside
    /// <paramref name="distanceNm"/>: v = sqrt(2·a·d), the inverse of <see cref="BrakingDistanceNm"/> against a
    /// zero target. A non-positive distance or rate yields 0 — there is no room left, so no speed is slow enough.
    /// </summary>
    public static double MaxEntrySpeedKts(double distanceNm, double decelRateKtsPerSec)
    {
        if ((distanceNm <= 0) || (decelRateKtsPerSec <= 0))
        {
            return 0;
        }

        double distFt = distanceNm * GeoMath.FeetPerNm;
        double decelFps2 = decelRateKtsPerSec * GeoMath.FeetPerNm / 3600.0;
        double speedFps = Math.Sqrt(2.0 * decelFps2 * distFt);
        return speedFps * 3600.0 / GeoMath.FeetPerNm;
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
