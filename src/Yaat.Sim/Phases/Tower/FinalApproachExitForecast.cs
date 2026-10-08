using System.Runtime.CompilerServices;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Phases.Tower;

/// <summary>Where a landing is forecast to touch down: the point on the runway, its distance past the threshold, the wheel speed.</summary>
public sealed record TouchdownForecast(LatLon Position, double AlongFt, double WheelSpeedKts);

/// <summary>
/// The exits-ahead list on the last miles of final (YAAT-463): which named exits the arrival will be able to make, judged as the
/// first rollout tick would judge an exit the controller named before touchdown (<c>LandingPhase.GiveUpUnreachableNamedExit</c>:
/// the named-exit search at the firm limit) — from a projected touchdown rather than the aircraft's position. The search itself is
/// the rollout's (<see cref="LandingPhase.ListExitsAhead(LandingPhase.ExitCandidateQuery, LatLon, Func{string, ExitSide, bool})"/>);
/// only the plan and the touchdown are built here, since the landing phase builds its plan only when it starts.
/// <para>
/// Listed while the current phase is <see cref="FinalApproachPhase"/> with a full-stop <see cref="LandingPhase"/> next, the aircraft
/// on final for its assigned runway (<see cref="RunwayOccupancy.IsOnFinal"/>) more than <see cref="WindowInnerNm"/> and at most
/// <see cref="WindowOuterNm"/> from the landing threshold, every fixed-wing category; never under <c>CLANDF</c>, for a helicopter, or
/// without the runway's layout and hold-short data. Under LAHSO the exits past the hold-short point are left out.
/// </para>
/// </summary>
public static class FinalApproachExitForecast
{
    /// <summary>Furthest from the landing threshold the list shows on final (aviation review, judgement).</summary>
    public const double WindowOuterNm = 5.0;

    /// <summary>
    /// Closest to the landing threshold the list shows on final: inside it there is no list until the rollout's (7110.65 3-10-9.a
    /// NOTE, exit instructions are not issued prior to touchdown).
    /// </summary>
    public const double WindowInnerNm = 1.0;

    /// <summary>
    /// The named exits <paramref name="aircraft"/> will be able to make once it touches down, the distance to each measured from the
    /// landing threshold and only the exit the controller's instruction (<see cref="PhaseList.RequestedExit"/>) would have the crew
    /// take marked planned. Null when no list applies (see the class summary); empty when one applies and nothing is makeable.
    /// <paramref name="layout"/> is the assigned runway's own airport layout; <paramref name="weather"/> and
    /// <paramref name="simTimeSeconds"/> give the surface wind. Read-only.
    /// </summary>
    public static IReadOnlyList<ExitAheadDto>? ListExitsAhead(
        AircraftState aircraft,
        AirportGroundLayout? layout,
        WeatherProfile? weather,
        double simTimeSeconds
    )
    {
        if (
            (aircraft.Phases is not { ForceLanding: false, AssignedRunway: { } runway, CurrentPhase: FinalApproachPhase } phases)
            || !IsFullStopNext(phases)
            || (layout is null)
            || (AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter)
            || !IsInWindow(aircraft, runway, layout)
            || (layout.GetRunwayHoldShortNodes(runway.Designator).Count == 0)
        )
        {
            return null;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        LandingPlan plan = BuildPlan(aircraft, category, runway, layout, weather);
        TouchdownForecast touchdown = ProjectTouchdown(plan, aircraft, category, weather, simTimeSeconds);
        double limit = RolloutBraking.NamedExitBrakingLimit(category, expedite: false);
        var query = new LandingPhase.ExitCandidateQuery
        {
            Aircraft = aircraft,
            Category = category,
            Layout = layout,
            Plan = plan,
            RwyDesignator = runway.Designator,
            SearchPref = null,
            SidePref = null,
            ExcludeHoldShortNodes = null,
            BrakingLimitForTurnOffSpeed = _ => limit,
            IncludeGivenUp = true,
            IgnoreLahso = false,
            From = new LandingPhase.ExitReachOrigin(touchdown.Position, touchdown.WheelSpeedKts, touchdown.WheelSpeedKts),
            ExcludeBranchPoints = null,
            LahsoStopLimitNm = phases.LahsoHoldShort is { } lahso
                ? LandingPhase.LahsoStopLimitNm(lahso.DistFromThresholdNm, aircraft.AircraftType)
                : null,
        };

        ExitPreference? requested = phases.RequestedExit;
        var key = new ForecastKey(
            layout,
            runway.Designator,
            aircraft.AircraftType,
            touchdown,
            requested?.Side,
            requested?.Taxiway,
            query.LahsoStopLimitNm
        );
        if (LastForecasts.TryGetValue(aircraft, out CachedForecast? cached) && (cached.Key == key))
        {
            return cached.List;
        }

        ResolvedExitInfo? planned = RequestedExitAhead(query, requested);
        IReadOnlyList<ExitAheadDto> list = LandingPhase.ListExitsAhead(
            query,
            new LatLon(plan.ThresholdLat, plan.ThresholdLon),
            (taxiway, side) =>
                (planned is { Side: { } plannedSide })
                && (side == plannedSide)
                && string.Equals(taxiway, planned.TaxiwayName, StringComparison.OrdinalIgnoreCase)
        );
        LastForecasts.AddOrUpdate(aircraft, new CachedForecast(key, list));
        return list;
    }

    /// <summary>
    /// Everything the forecast list is a function of, once the window and phase gates have passed: the layout and runway, the type,
    /// the projected touchdown (which carries the surface wind), the controller's exit instruction and the LAHSO stop limit. The
    /// aircraft's position on final is not among them, so the list holds still down the final until one of these changes.
    /// </summary>
    private readonly record struct ForecastKey(
        AirportGroundLayout Layout,
        string Runway,
        string AircraftType,
        TouchdownForecast Touchdown,
        ExitSide? RequestedSide,
        string? RequestedTaxiway,
        double? LahsoStopLimitNm
    );

    private sealed record CachedForecast(ForecastKey Key, IReadOnlyList<ExitAheadDto> List);

    /// <summary>
    /// Each aircraft's last forecast and the inputs it came from: the search costs milliseconds, so the list is recomputed only when
    /// its <see cref="ForecastKey"/> does. In a steady wind the key holds down the final; under gusts or a variable wind the surface
    /// wind, and with it the projected touchdown, changes every second, so the cache misses every second. A pure function of the key,
    /// so a restored or replayed aircraft computes the very list the cache would have held.
    /// </summary>
    private static readonly ConditionalWeakTable<AircraftState, CachedForecast> LastForecasts = [];

    /// <summary>
    /// Where <paramref name="aircraft"/> is forecast to touch down on its assigned runway, and its wheel speed there; null with no
    /// assigned runway. See <see cref="ProjectTouchdown(LandingPlan, AircraftState, AircraftCategory, WeatherProfile?, double)"/>.
    /// </summary>
    public static TouchdownForecast? ProjectTouchdown(
        AircraftState aircraft,
        AirportGroundLayout? layout,
        WeatherProfile? weather,
        double simTimeSeconds
    )
    {
        if (aircraft.Phases?.AssignedRunway is not { } runway)
        {
            return null;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        return ProjectTouchdown(BuildPlan(aircraft, category, runway, layout, weather), aircraft, category, weather, simTimeSeconds);
    }

    /// <summary>
    /// The exit the controller's instruction (<paramref name="requested"/>) would have the crew take, judged from the projected
    /// touchdown by the search the rollout judges a named exit with: a bare <c>EXIT &lt;twy&gt;</c> on the side the crew expects to
    /// turn off (<see cref="AirportGroundLayout.InferPreferredExitSide(string, TrueHeading)"/>), the other side only when the taxiway
    /// has no makeable connection there. Null without an instruction naming a taxiway, or when none of its connections is makeable.
    /// </summary>
    private static ResolvedExitInfo? RequestedExitAhead(LandingPhase.ExitCandidateQuery query, ExitPreference? requested)
    {
        if (requested is not { Taxiway: not null })
        {
            return null;
        }

        ExitSide? inferred = requested.Side is null ? query.Layout.InferPreferredExitSide(query.RwyDesignator, query.Plan.RunwayHeading) : null;
        return LandingPhase.FindWithInferredSide(query with { SearchPref = requested, SidePref = requested.Side ?? inferred }, inferred);
    }

    /// <summary>
    /// The plan the landing will fly, built as <c>LandingPhase.OnStart</c> builds it: the assigned runway's heading and field
    /// elevation, its landing threshold (<see cref="LandingThreshold.Resolve(RunwayInfo, AirportGroundLayout?)"/>) and the category's
    /// constants.
    /// </summary>
    private static LandingPlan BuildPlan(
        AircraftState aircraft,
        AircraftCategory category,
        RunwayInfo runway,
        AirportGroundLayout? layout,
        WeatherProfile? weather
    )
    {
        LatLon threshold = LandingThreshold.Resolve(runway, layout);
        var geometry = new LandingPhase.LandingGeometry(runway.ElevationFt, runway.TrueHeading, threshold.Lat, threshold.Lon);
        return LandingPhase.BuildPlan(aircraft, category, weather, geometry);
    }

    /// <summary>
    /// The projected touchdown: the glidepath's aiming point plus the flare's float, i.e. the flare entry on the glidepath
    /// (<see cref="CategoryPerformance.WheelCrossingHeightFt"/> less the expected flare entry height, <see cref="ExpectedFlareEntryAgl"/>,
    /// over the glidepath's slope) plus the
    /// flare's ground run (<see cref="FlareSeconds"/> at the mean of the arrival and touchdown speeds, as true airspeed less the
    /// headwind), measured along the runway heading from the landing threshold — never the aircraft's crabbed heading.
    /// <para>
    /// The arrival speed is what the flare starts from: the final approach speed <see cref="FinalApproachPhase"/> flies (the type's
    /// <see cref="AircraftPerformance.ApproachSpeed"/> plus <see cref="AircraftPerformance.WindApproachAdditive"/>), never above the
    /// plan's Vref, which the flare's speed ramp starts at. The touchdown speed is the plan's Vtd, never above the arrival speed —
    /// <c>LandingPhase.TickTouchdown</c> takes the lower of the two — and the wheel speed is its true airspeed less the headwind, as
    /// <see cref="GroundFrame.EnterGround"/> converts it at touchdown.
    /// </para>
    /// </summary>
    private static TouchdownForecast ProjectTouchdown(
        LandingPlan plan,
        AircraftState aircraft,
        AircraftCategory category,
        WeatherProfile? weather,
        double simTimeSeconds
    )
    {
        double tanAngle = Math.Tan(GlideSlopeGeometry.AngleForCategory(category) * Math.PI / 180.0);

        double phaseSeconds = WindVariation.PhaseSecondsFor(aircraft.Callsign);
        WindAtAltitude wind = WindInterpolator.GetWindAt(weather, plan.FieldElevation, simTimeSeconds, phaseSeconds);
        double headwindKts = WindInterpolator.HeadwindComponentKts(wind.DirectionDeg, wind.SpeedKts, plan.RunwayHeading.Degrees);

        double finalApproachIas =
            AircraftPerformance.ApproachSpeed(aircraft.AircraftType, category)
            + AircraftPerformance.WindApproachAdditive(weather, plan.RunwayHeading.Degrees);
        double arrivalIas = Math.Min(plan.Vref, finalApproachIas);
        double touchdownIas = Math.Min(arrivalIas, plan.Vtd);

        double approachGroundSpeedKts = Math.Max(0, WindInterpolator.IasToTas(finalApproachIas, plan.FieldElevation) - headwindKts);
        double flareEntryAgl = ExpectedFlareEntryAgl(plan, approachGroundSpeedKts * GeoMath.FeetPerNm / 3600.0 * tanAngle);
        double flareEntryAlongFt = (CategoryPerformance.WheelCrossingHeightFt(category) - flareEntryAgl) / tanAngle;

        double flareIas = (arrivalIas + touchdownIas) / 2.0;
        double flareGroundSpeedKts = Math.Max(0, WindInterpolator.IasToTas(flareIas, plan.FieldElevation) - headwindKts);
        double flareRunFt = flareGroundSpeedKts * GeoMath.FeetPerNm / 3600.0 * FlareSeconds(plan, flareEntryAgl);
        double alongFt = flareEntryAlongFt + flareRunFt;

        double wheelSpeedKts = Math.Max(0, WindInterpolator.IasToTas(touchdownIas, plan.FieldElevation) - headwindKts);
        LatLon position = GeoMath.ProjectPoint(new LatLon(plan.ThresholdLat, plan.ThresholdLon), plan.RunwayHeading, alongFt / GeoMath.FeetPerNm);
        return new TouchdownForecast(position, alongFt, wheelSpeedKts);
    }

    /// <summary>
    /// The height the flare is expected to start at. <c>LandingPhase</c> checks for the flare once a second, so the aircraft is
    /// caught somewhere in the second's descent below <see cref="LandingPlan.FlareEntryAgl"/>: on average half a second's glidepath
    /// sink (<paramref name="sinkFtPerSecond"/>, at the final approach ground speed) inside the band.
    /// </summary>
    private static double ExpectedFlareEntryAgl(LandingPlan plan, double sinkFtPerSecond) =>
        Math.Max(0, plan.FlareEntryAgl - (sinkFtPerSecond / 2.0));

    /// <summary>
    /// How long a flare started at <paramref name="entryAgl"/> lasts. <c>LandingPhase.TickFlare</c> commands a sink proportional to
    /// height (<see cref="LandingPlan.FlareFpm"/> × agl / <see cref="LandingPlan.FlareEntryAgl"/>), so height decays exponentially
    /// with time constant 60·<see cref="LandingPlan.FlareEntryAgl"/>/rate seconds, and physics puts the wheels on the runway once the
    /// remaining height is inside its altitude snap (<see cref="FlightPhysics.AltitudeSnapFt"/>) or the touchdown gate, whichever is
    /// higher.
    /// </summary>
    private static double FlareSeconds(LandingPlan plan, double entryAgl)
    {
        double landedAgl = Math.Max(FlightPhysics.AltitudeSnapFt, plan.TouchdownAgl);
        if ((entryAgl <= landedAgl) || (plan.FlareFpm <= 0) || (plan.FlareEntryAgl <= 0))
        {
            return 0;
        }

        return 60.0 * plan.FlareEntryAgl / plan.FlareFpm * Math.Log(entryAgl / landedAgl);
    }

    /// <summary>A full-stop landing is next: the phase after the current one is a <see cref="LandingPhase"/> that has not started.</summary>
    private static bool IsFullStopNext(PhaseList phases)
    {
        int next = phases.CurrentIndex + 1;
        return (next < phases.Phases.Count) && (phases.Phases[next] is LandingPhase { Status: PhaseStatus.Pending });
    }

    /// <summary>
    /// On final for <paramref name="runway"/> (aligned and inside its wedge, <see cref="RunwayOccupancy.IsOnFinal"/>), more than
    /// <see cref="WindowInnerNm"/> and at most <see cref="WindowOuterNm"/> from its landing threshold along the runway heading.
    /// </summary>
    private static bool IsInWindow(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout layout)
    {
        if (!RunwayOccupancy.IsOnFinal(aircraft, runway, layout, WindowOuterNm))
        {
            return false;
        }

        double distNm = -GeoMath.AlongTrackDistanceNm(aircraft.Position, LandingThreshold.Resolve(runway, layout), runway.TrueHeading);
        return (distNm > WindowInnerNm) && (distNm <= WindowOuterNm);
    }
}
