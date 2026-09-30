using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// The CLANDF forced-landing profile: where a forced aircraft aims to touch down, how fast it may descend, and how
/// it brakes afterwards. <b>These numbers are an instructor-override rule, not aircraft performance.</b> CLANDF
/// exists so an instructor can put an aircraft on the runway from any energy state — including one already past the
/// threshold — and every constant here serves that promise ("always land and stop on the runway") rather than
/// modelling what a real crew could do. Normal landings never read them.
/// </summary>
public static class ForcedLandingProfile
{
    /// <summary>Airborne deceleration (kts/s) toward the approach speed while the forced landing is in progress.</summary>
    public const double AirborneDecelKtsPerSec = 5.0;

    /// <summary>Path angle (degrees) that sets the preferred aim point: the aircraft's along-track + AGL / tan(6°).</summary>
    public const double AimPathAngleDeg = 6.0;

    /// <summary>The aim point is never closer to the landing threshold than this (ft).</summary>
    public const double MinAimPastThresholdFt = 1000.0;

    /// <summary>The aim point is never closer to the aircraft than this (ft), before the runway-length pull-back.</summary>
    public const double MinAimAheadFt = 500.0;

    /// <summary>Braking rate (kts/s) the runway-length pull-back plans the stop at.</summary>
    public const double StopPlanningDecelKtsPerSec = 6.0;

    /// <summary>Runway (ft) the pull-back keeps beyond the planned stop.</summary>
    public const double StopPlanningMarginFt = 500.0;

    /// <summary>Descent-rate cap (fpm) above <see cref="LowDescentCapAglFt"/>.</summary>
    public const double DescentCapFpm = 3000.0;

    /// <summary>Descent-rate cap (fpm) at or below <see cref="LowDescentCapAglFt"/>.</summary>
    public const double LowDescentCapFpm = 1000.0;

    /// <summary>AGL (ft) at or below which <see cref="LowDescentCapFpm"/> applies.</summary>
    public const double LowDescentCapAglFt = 100.0;

    /// <summary>Floor (kts/s) of the forced rollout's braking toward an exit or the runway-end stop.</summary>
    public const double RolloutMinDecelKtsPerSec = 3.0;

    /// <summary>Ceiling (kts/s) of the forced rollout's braking toward an exit; an exit that needs more is not usable.</summary>
    public const double RolloutMaxDecelKtsPerSec = 6.0;

    /// <summary>The forced rollout reaches the exit's turn-off speed this far (ft) before the exit's branch point.</summary>
    public const double ExitBrakingMarginFt = 300.0;

    /// <summary>With no usable exit the forced rollout stops this far (ft) before the runway end.</summary>
    public const double RunwayEndStopMarginFt = 300.0;

    /// <summary>
    /// Braking (kts/s) the runway-end stop may use. Exceeded only when even this rate would leave the runway, because
    /// the forced aircraft must never leave the pavement.
    /// </summary>
    public const double RunwayEndStopMaxDecelKtsPerSec = 10.0;

    /// <summary>Ground speed floor (kts) for the descent-rate arithmetic, so a near-zero reading cannot divide by zero.</summary>
    private const double MinGuidanceGroundSpeedKts = 60.0;

    /// <summary>
    /// Landing distance (ft) from the landing threshold to the runway end: the pavement less any displacement, which
    /// is not available to the arrival (AIM 2-3-3.h.2).
    /// </summary>
    public static double LandingDistanceFt(RunwayInfo runway, AirportGroundLayout? layout) =>
        runway.PavementLengthFt - LandingThreshold.DisplacementFt(runway, layout);

    /// <summary>
    /// The latest along-track point (ft past the landing threshold) the aircraft may touch down at and still stop at
    /// <see cref="StopPlanningDecelKtsPerSec"/> with <see cref="StopPlanningMarginFt"/> of runway to spare, from
    /// <paramref name="groundSpeedKts"/>. Never behind the aircraft.
    /// </summary>
    public static double LatestTouchdownAlongFt(double aircraftAlongFt, double groundSpeedKts, double runwayLengthFt)
    {
        double stopFt = RolloutBraking.BrakingDistanceNm(groundSpeedKts, 0, StopPlanningDecelKtsPerSec) * GeoMath.FeetPerNm;
        return Math.Max(aircraftAlongFt, runwayLengthFt - stopFt - StopPlanningMarginFt);
    }

    /// <summary>
    /// The aim point (ft past the landing threshold; negative before it): the furthest of the 6° point ahead of the
    /// aircraft, the threshold + 1,000 ft and the aircraft + 500 ft, pulled back toward the aircraft to
    /// <see cref="LatestTouchdownAlongFt"/> when the runway beyond it is too short to stop on.
    /// </summary>
    public static double AimPointAlongFt(double aircraftAlongFt, double aglFt, double groundSpeedKts, double runwayLengthFt) =>
        Math.Min(PreferredAimAlongFt(aircraftAlongFt, aglFt), LatestTouchdownAlongFt(aircraftAlongFt, groundSpeedKts, runwayLengthFt));

    /// <summary>The aim point before the runway-length pull-back (ft past the landing threshold).</summary>
    private static double PreferredAimAlongFt(double aircraftAlongFt, double aglFt)
    {
        double sixDegreePointFt = aircraftAlongFt + (Math.Max(aglFt, 0) / Math.Tan(AimPathAngleDeg * Math.PI / 180.0));
        return Math.Max(Math.Max(sixDegreePointFt, MinAimPastThresholdFt), aircraftAlongFt + MinAimAheadFt);
    }

    /// <summary>
    /// Descent rate (fpm, positive down) toward the aim point. The rate is capped at <see cref="DescentCapFpm"/>
    /// (<see cref="LowDescentCapFpm"/> at or below <see cref="LowDescentCapAglFt"/>) as long as the capped path still
    /// reaches the ground by <see cref="LatestTouchdownAlongFt"/>; when it cannot, the cap gives way to the rate that
    /// does, however steep — the forced aircraft lands on the runway whatever it takes.
    /// </summary>
    public static double DescentRateFpm(double aglFt, double aircraftAlongFt, double groundSpeedKts, double runwayLengthFt)
    {
        if (aglFt <= 1.0)
        {
            return 0;
        }

        double feetPerMinute = Math.Max(groundSpeedKts, MinGuidanceGroundSpeedKts) * GeoMath.FeetPerNm / 60.0;
        double aimFt = AimPointAlongFt(aircraftAlongFt, aglFt, groundSpeedKts, runwayLengthFt);
        double requiredFpm = RateToReachFpm(aglFt, aimFt - aircraftAlongFt, feetPerMinute);
        double capFpm = aglFt > LowDescentCapAglFt ? DescentCapFpm : LowDescentCapFpm;
        if (requiredFpm <= capFpm)
        {
            return requiredFpm;
        }

        double latestFt = LatestTouchdownAlongFt(aircraftAlongFt, groundSpeedKts, runwayLengthFt);
        double cappedTouchdownFt = aircraftAlongFt + (aglFt / capFpm * feetPerMinute);
        return cappedTouchdownFt <= latestFt ? capFpm : RateToReachFpm(aglFt, latestFt - aircraftAlongFt, feetPerMinute);
    }

    /// <summary>
    /// Writes the airborne forced-landing targets: descend to <paramref name="thresholdElevationFt"/> at
    /// <see cref="DescentRateFpm"/> and slow to the approach speed (Vref plus the wind additive, never below Vref) at
    /// <see cref="AirborneDecelKtsPerSec"/>. The forced landing owns the speed profile, so a SPEEDF issued to a forced
    /// aircraft is superseded.
    /// </summary>
    public static void ApplyAirborneGuidance(PhaseContext ctx, LatLon landingThreshold, TrueHeading runwayHeading, double thresholdElevationFt)
    {
        (double aglFt, double alongFt, double runwayLengthFt) = Measure(ctx, landingThreshold, runwayHeading, thresholdElevationFt);

        ctx.Targets.TargetAltitude = thresholdElevationFt;
        ctx.Targets.DesiredVerticalRate = -DescentRateFpm(aglFt, alongFt, ctx.Aircraft.GroundSpeed, runwayLengthFt);
        ctx.Targets.TargetSpeed =
            AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category)
            + AircraftPerformance.WindApproachAdditive(ctx.Weather, runwayHeading.Degrees);
        ctx.Targets.DesiredDecelRate = AirborneDecelKtsPerSec;
    }

    /// <summary>
    /// The descent rate (fpm, positive down) a forced aircraft's flare may not undercut, or null when the flare may
    /// float as usual. Non-null only while the aim point is pulled back for runway length: then the rate is the one
    /// that still meets the runway by <see cref="LatestTouchdownAlongFt"/>, because a float would carry the aircraft
    /// past the last point it can stop from.
    /// </summary>
    public static double? FlareFloorFpm(PhaseContext ctx, LatLon landingThreshold, TrueHeading runwayHeading, double thresholdElevationFt)
    {
        (double aglFt, double alongFt, double runwayLengthFt) = Measure(ctx, landingThreshold, runwayHeading, thresholdElevationFt);
        double latestFt = LatestTouchdownAlongFt(alongFt, ctx.Aircraft.GroundSpeed, runwayLengthFt);
        if ((aglFt <= 1.0) || (PreferredAimAlongFt(alongFt, aglFt) <= latestFt))
        {
            return null;
        }

        double feetPerMinute = Math.Max(ctx.Aircraft.GroundSpeed, MinGuidanceGroundSpeedKts) * GeoMath.FeetPerNm / 60.0;
        return RateToReachFpm(aglFt, latestFt - alongFt, feetPerMinute);
    }

    /// <summary>
    /// The aircraft's AGL and along-track position, and the runway length the guidance plans on: the landing distance
    /// less one sub-tick of travel. The landing phase sees the wheels on the runway one sub-tick after physics puts
    /// them there, and the aircraft rolls on through that sub-tick, so the ground has to be met one sub-tick of travel
    /// before the latest touchdown point for the recorded touchdown to be at or before it.
    /// </summary>
    private static (double AglFt, double AlongFt, double RunwayLengthFt) Measure(
        PhaseContext ctx,
        LatLon landingThreshold,
        TrueHeading runwayHeading,
        double thresholdElevationFt
    )
    {
        double aglFt = ctx.Aircraft.Altitude - thresholdElevationFt;
        double alongFt = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, landingThreshold, runwayHeading) * GeoMath.FeetPerNm;
        double subTickTravelFt = ctx.Aircraft.GroundSpeed * GeoMath.FeetPerNm / 3600.0 * ctx.DeltaSeconds;
        double runwayLengthFt = ctx.Runway is { } runway ? LandingDistanceFt(runway, ctx.GroundLayout) - subTickTravelFt : double.PositiveInfinity;
        return (aglFt, alongFt, runwayLengthFt);
    }

    private static double RateToReachFpm(double aglFt, double distanceFt, double feetPerMinute)
    {
        double minutes = distanceFt / feetPerMinute;
        return minutes > (1.0 / 240.0) ? aglFt / minutes : aglFt * 240.0;
    }
}
