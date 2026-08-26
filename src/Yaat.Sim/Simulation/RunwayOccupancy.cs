using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Simulation;

/// <summary>
/// How an aircraft is using a runway, in priority order: when more than one kind fits, the earlier one wins
/// (a rolling departure is <see cref="Departing"/>, never also <see cref="OnSurface"/>).
/// </summary>
public enum RunwayUseKind
{
    /// <summary>On the pavement, aligned with the runway axis, and rolling (takeoff roll has started — 7110.65 §3-9-6).</summary>
    Departing,

    /// <summary>Airborne over the pavement below threshold-crossing height (P/CG THRESHOLD CROSSING HEIGHT).</summary>
    Landing,

    /// <summary>On the pavement and aligned with the runway axis: lined up, holding in position, rolling out, back-taxiing.</summary>
    OnSurface,

    /// <summary>Airborne inside two miles of the landing threshold on the final approach course (7110.65 §3-7-6).</summary>
    ShortFinal,

    /// <summary>
    /// Airborne on the final approach course inside the 6 nm traffic-advisory ring (<see cref="RunwayOccupancy.IsOnFinal"/>).
    /// Never produced by <see cref="RunwayOccupancy.ClassifyByGeometry"/>; consumers that need the wider ring (the solo evaluator)
    /// synthesize it.
    /// </summary>
    OnFinal,

    /// <summary>On the pavement but not aligned with it — taxiing across, or leaving via an exit.</summary>
    Crossing,
}

/// <summary>One aircraft's use of one runway.</summary>
public sealed record RunwayUse(string Callsign, RunwayInfo Runway, RunwayUseKind Kind);

/// <summary>
/// Classifies an aircraft against a runway independently of its phases, so runway consumers (occupancy advisories,
/// ground-conflict priority, the occupied-runway go-around) can see aircraft the phase machinery does not drive.
///
/// Phase evidence takes precedence over geometry: an aircraft in <see cref="LinedUpAndWaitingPhase"/> is
/// <see cref="RunwayUseKind.OnSurface"/> whether or not its position passes the pavement test (it may be holding at a
/// far-side taxiway node), and a phase-driven aircraft whose phase says nothing about the runway is at most a
/// <see cref="RunwayUseKind.Crossing"/> — never an occupant — so phase-driven traffic keeps the answers the consumers gave
/// before this classifier existed. Geometry alone decides only for aircraft with no phases at all.
///
/// Datums follow the 7110.65: the <em>pavement</em> rectangle for surface kinds (the pavement behind a displaced
/// threshold is usable for takeoff and rollout, AIM 2-3-3.b.8.2) and the <em>landing</em> threshold
/// (<see cref="LandingThreshold"/>) for <see cref="RunwayUseKind.ShortFinal"/> and the distance/time helpers (§3-10-3 is
/// written from the landing threshold). AGL is measured from the aligned runway end's elevation, not the field's.
/// </summary>
public static class RunwayOccupancy
{
    /// <summary>
    /// Lateral slack (feet) beyond the runway half-width for the pavement test — covers graph-node vs centerline
    /// placement jitter without reaching a parallel taxiway. Parallel runways are at least 300 ft apart (§3-8-3), so
    /// the slack can never bleed into one.
    /// </summary>
    public const double LateralSlackFt = 30.0;

    /// <summary>
    /// Axis tolerance (degrees, modulo 180) separating an aircraft using the runway from one crossing or leaving it.
    /// Tight enough that a standard 30° high-speed exit reads as <see cref="RunwayUseKind.Crossing"/>; modulo 180 so a
    /// back-taxi (P/CG BACK-TAXI, §3-1-3.d) is <see cref="RunwayUseKind.OnSurface"/>.
    /// </summary>
    public const double SurfaceAxisToleranceDeg = 20.0;

    /// <summary>
    /// Track tolerance (degrees) for "on the final approach course". §5-9-2 TBL 5-9-1 permits intercepts up to 30°
    /// for fixed-wing and 45° for helicopters; one number for both, shared with the approach handler's on-final test.
    /// </summary>
    public const double FinalTrackToleranceDeg = 45.0;

    /// <summary>§3-7-6: the runway environment protected while an arrival is inside two miles of the threshold.</summary>
    public const double ShortFinalDistanceNm = 2.0;

    /// <summary>Cross-track cap for <see cref="RunwayUseKind.ShortFinal"/>, so a parallel-runway arrival never matches.</summary>
    public const double ShortFinalCrossTrackNm = 0.3;

    /// <summary>AGL ceiling for <see cref="RunwayUseKind.ShortFinal"/> — turbine traffic-pattern altitude (AIM 4-3-3).</summary>
    public const double ShortFinalAglCeilingFt = 1500.0;

    /// <summary>Track tolerance for <see cref="IsOnFinal"/> — an engineering tolerance for sampled tracks joining the final, not a rule.</summary>
    public const double OnFinalTrackToleranceDeg = 30.0;

    /// <summary>
    /// Cross-track cap for <see cref="IsOnFinal"/>: a 10° wedge from the threshold, floored at 0.3 nm and capped at 1 nm. Parallel
    /// runways are closer than that (OAK 28L/28R ≈ 0.4 nm), so callers pick the runway with <see cref="ClosestFinal"/>.
    /// </summary>
    public const double OnFinalCrossTrackNm = 1.0;
    public const double OnFinalCrossTrackFloorNm = 0.3;
    public const double OnFinalWedgeDeg = 10.0;

    /// <summary>Threshold-crossing height; below it an airborne aircraft over the pavement is landing.</summary>
    public const double LandingAglCeilingFt = 50.0;

    /// <summary>Ground speed (knots) above which an aligned aircraft on the pavement has started its takeoff roll.</summary>
    public const double DepartingMinGroundSpeedKts = 35.0;

    /// <summary>
    /// Vertical-speed cap (fpm) for "not climbing" on short final. Level at MDA and a level low approach are legal
    /// (P/CG FINAL APPROACH POINT, §3-10-10), so only a climbing aircraft is excluded.
    /// </summary>
    public const double NotClimbingMaxFpm = 100.0;

    /// <summary>
    /// Classifies <paramref name="ac"/> against <paramref name="runway"/>. Phase evidence first
    /// (<see cref="ClassifyByPhase"/>); geometry (<see cref="ClassifyByGeometry"/>) only for aircraft with no phases;
    /// a phase-driven aircraft on the pavement in any other phase is a <see cref="RunwayUseKind.Crossing"/>.
    /// <paramref name="layout"/> supplies the landing-threshold displacement and may be null (pavement threshold).
    /// </summary>
    public static RunwayUse? Classify(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout)
    {
        RunwayUseKind? kind = ClassifyByPhase(ac, runway);
        if (kind is null)
        {
            kind = ac.Phases is null ? ClassifyByGeometry(ac, runway, layout) : (IsOnPavement(ac, runway) ? RunwayUseKind.Crossing : null);
        }

        if (kind == RunwayUseKind.Departing && ac.LiveTraffic is { LandedOnRunway: true })
        {
            // The runway-use observer saw this shadow land: its rollout is not a takeoff roll (geometry cannot tell).
            kind = RunwayUseKind.OnSurface;
        }

        return kind is { } k ? new RunwayUse(ac.Callsign, runway, k) : null;
    }

    /// <summary>
    /// What the aircraft's current phase says about its runway use, or null when the phase is not a runway phase.
    /// When <paramref name="runway"/> is given the phase's own runway (departure, else assigned) must name the same
    /// pavement; with null the phase alone decides — except <see cref="HoldingInPositionPhase"/>, which YAAT also uses as
    /// a generic ground-idle hold and therefore only counts when it is physically on the given runway's pavement.
    /// </summary>
    public static RunwayUseKind? ClassifyByPhase(AircraftState ac, RunwayInfo? runway)
    {
        var phases = ac.Phases;
        var phase = phases?.CurrentPhase;
        if ((phases is null) || (phase is null))
        {
            return null;
        }

        if (runway is not null)
        {
            var own = phases.DepartureRunway ?? phases.AssignedRunway;
            bool sameRunway = (own is not null) && SameAirport(own.AirportId, runway.AirportId) && own.Id.Overlaps(runway.Id);
            if (!sameRunway)
            {
                return null;
            }
        }

        return phase switch
        {
            TakeoffPhase => RunwayUseKind.Departing,
            LandingPhase => ac.IsOnGround ? RunwayUseKind.OnSurface : RunwayUseKind.Landing,
            LineUpPhase or LinedUpAndWaitingPhase or StopAndGoPhase or TouchAndGoPhase => RunwayUseKind.OnSurface,
            RunwayExitPhase { IsOnCenterline: true } => RunwayUseKind.OnSurface,
            HoldingInPositionPhase when (runway is not null) && IsOnPavement(ac, runway) => RunwayUseKind.OnSurface,
            _ => null,
        };
    }

    /// <summary>
    /// Geometry-only classification: pavement containment and axis alignment on the ground; threshold-crossing height
    /// and the final approach course in the air. Rotorcraft are never <see cref="RunwayUseKind.Landing"/> or
    /// <see cref="RunwayUseKind.ShortFinal"/> — they air-taxi below 100 ft and arrive at runway points from any
    /// direction (§3-11-1, §3-11-6).
    /// </summary>
    public static RunwayUseKind? ClassifyByGeometry(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout)
    {
        if (ac.IsOnGround)
        {
            if (!IsOnPavement(ac, runway))
            {
                return null;
            }

            if (AxisDeviationDeg(ac.TrueHeading.Degrees, runway) > SurfaceAxisToleranceDeg)
            {
                return RunwayUseKind.Crossing;
            }

            return ac.GroundSpeed >= DepartingMinGroundSpeedKts ? RunwayUseKind.Departing : RunwayUseKind.OnSurface;
        }

        if (AircraftCategorization.Categorize(ac.AircraftType) == AircraftCategory.Helicopter)
        {
            return null;
        }

        var alignedEnd = AlignedEnd(ac.TrueTrack.Degrees, runway);
        if (GeoMath.AbsBearingDifference(ac.TrueTrack.Degrees, alignedEnd.TrueHeading.Degrees) > FinalTrackToleranceDeg)
        {
            return null;
        }

        double agl = ac.Altitude - alignedEnd.ElevationFt;
        if (IsOverPavement(ac, runway) && (agl < LandingAglCeilingFt))
        {
            return RunwayUseKind.Landing;
        }

        if ((ac.VerticalSpeed > NotClimbingMaxFpm) || (agl > ShortFinalAglCeilingFt))
        {
            return null;
        }

        var landingThreshold = LandingThreshold.Resolve(alignedEnd, layout);
        double along = GeoMath.AlongTrackDistanceNm(ac.Position, landingThreshold, alignedEnd.TrueHeading);
        double cross = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, landingThreshold, alignedEnd.TrueHeading));
        bool onFinal = (along < 0) && (-along <= ShortFinalDistanceNm) && (cross <= ShortFinalCrossTrackNm);
        return onFinal ? RunwayUseKind.ShortFinal : null;
    }

    /// <summary>On the ground and within the runway half-width plus <see cref="LateralSlackFt"/> of the centerline segment.</summary>
    public static bool IsOnPavement(AircraftState ac, RunwayInfo runway) => ac.IsOnGround && IsWithinPavement(ac.Position, runway);

    /// <summary>True for the kinds that make the aircraft a runway occupant for clearance and priority purposes.</summary>
    public static bool OccupiesSurface(RunwayUseKind? kind) => kind is RunwayUseKind.Departing or RunwayUseKind.Landing or RunwayUseKind.OnSurface;

    /// <summary>
    /// Distance (nm) from the aircraft to <paramref name="runway"/>'s landing threshold along the final approach course
    /// of the runway end the aircraft is tracking toward. Negative once past the threshold.
    /// </summary>
    public static double DistanceToLandingThresholdNm(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout)
    {
        var alignedEnd = AlignedEnd(ac.TrueTrack.Degrees, runway);
        var landingThreshold = LandingThreshold.Resolve(alignedEnd, layout);
        return -GeoMath.AlongTrackDistanceNm(ac.Position, landingThreshold, alignedEnd.TrueHeading);
    }

    /// <summary>
    /// Seconds until the aircraft reaches the landing threshold at its present ground speed — the tower's own unit for
    /// runway separation (§3-9-6, §3-9-7). Positive infinity when stopped, or negative once past the threshold.
    /// </summary>
    public static double SecondsToLandingThreshold(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout)
    {
        double groundSpeed = ac.GroundSpeed;
        if (groundSpeed <= 0)
        {
            return double.PositiveInfinity;
        }

        return DistanceToLandingThresholdNm(ac, runway, layout) / groundSpeed * 3600.0;
    }

    private static bool IsWithinPavement(LatLon position, RunwayInfo runway)
    {
        double distFt = GeoMath.DistanceToSegmentFt(position, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2));
        return distFt <= (runway.WidthFt / 2.0) + LateralSlackFt;
    }

    /// <summary>Airborne counterpart of <see cref="IsOnPavement"/>: between the two ends and inside the pavement edge.</summary>
    private static bool IsOverPavement(AircraftState ac, RunwayInfo runway)
    {
        var (_, _, clamped) = GeoMath.FootOfPerpendicular(ac.Position, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2));
        return (!clamped) && IsWithinPavement(ac.Position, runway);
    }

    /// <summary>Angle (0–90°) between a heading and the runway axis, ignoring which end is ahead.</summary>
    private static double AxisDeviationDeg(double headingDeg, RunwayInfo runway)
    {
        double diff = GeoMath.AbsBearingDifference(headingDeg, runway.TrueHeading1.Degrees);
        return Math.Min(diff, 180.0 - diff);
    }

    /// <summary>
    /// Airborne, on the approach side of <paramref name="runway"/>'s landing threshold within <paramref name="maxNm"/>,
    /// within <see cref="OnFinalTrackToleranceDeg"/> of the final course and <see cref="OnFinalCrossTrackNm"/> of the
    /// extended centreline, and not climbing — the traffic §3-9-4.d / §3-10-5.f wants exchanged. Wider than
    /// <see cref="RunwayUseKind.ShortFinal"/>, which protects the runway itself.
    /// </summary>
    public static bool IsOnFinal(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout, double maxNm)
    {
        if (ac.IsOnGround || ac.VerticalSpeed > NotClimbingMaxFpm)
        {
            return false;
        }

        var end = AlignedEnd(ac.TrueTrack.Degrees, runway);
        if (GeoMath.AbsBearingDifference(ac.TrueTrack.Degrees, end.TrueHeading.Degrees) > OnFinalTrackToleranceDeg)
        {
            return false;
        }

        var threshold = LandingThreshold.Resolve(end, layout);
        double distNm = -GeoMath.AlongTrackDistanceNm(ac.Position, threshold, end.TrueHeading);
        double crossTrackNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, end.TrueHeading));
        double wedgeNm = Math.Min(OnFinalCrossTrackNm, Math.Max(OnFinalCrossTrackFloorNm, distNm * Math.Tan(OnFinalWedgeDeg * Math.PI / 180.0)));
        return distNm > 0 && distNm <= maxNm && crossTrackNm <= wedgeNm;
    }

    /// <summary>
    /// Among <paramref name="runways"/> (pavements), the one whose final <paramref name="ac"/> is on (<see cref="IsOnFinal"/>) with the
    /// smallest cross-track — so an arrival between parallel finals is reported for one runway, not both. Null when on none.
    /// </summary>
    public static RunwayInfo? ClosestFinal(AircraftState ac, IReadOnlyList<RunwayInfo> runways, AirportGroundLayout? layout, double maxNm)
    {
        RunwayInfo? best = null;
        double bestXtk = double.MaxValue;
        foreach (var pavement in runways)
        {
            var end = AlignedEnd(ac.TrueTrack.Degrees, pavement);
            if (!IsOnFinal(ac, end, layout, maxNm))
            {
                continue;
            }

            double xtk = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, LandingThreshold.Resolve(end, layout), end.TrueHeading));
            if (xtk < bestXtk)
            {
                bestXtk = xtk;
                best = end;
            }
        }

        return best;
    }

    /// <summary>
    /// Runways of an airport given as FAA (OAK) or ICAO (KOAK) code — the nav DB keys on the FAA form. Empty when the
    /// nav DB is not initialized (best-effort callers such as the ground conflict detector).
    /// </summary>
    public static IReadOnlyList<RunwayInfo> AirportRunways(string? airport)
    {
        if (string.IsNullOrWhiteSpace(airport) || NavigationDatabase.InstanceOrNull is not { } navDb)
        {
            return [];
        }

        var runways = navDb.GetRunways(airport);
        if (runways.Count == 0 && airport.Length == 4 && airport.StartsWith('K'))
        {
            runways = navDb.GetRunways(airport[1..]);
        }

        return runways;
    }

    /// <summary>
    /// Best (highest-priority) runway use of <paramref name="ac"/> over <paramref name="runways"/> (pavements, oriented to
    /// the track-aligned end) — <see cref="RunwayUseKind.Crossing"/> included as the lowest priority (on the pavement is on the
    /// pavement); null when it uses none of them.
    /// </summary>
    public static RunwayUse? ClassifyBest(AircraftState ac, IReadOnlyList<RunwayInfo> runways, AirportGroundLayout? layout)
    {
        RunwayUse? best = null;
        foreach (var pavement in runways)
        {
            var use = Classify(ac, AlignedEnd(ac.TrueTrack.Degrees, pavement), layout);
            if (use is null || (best is not null && use.Kind >= best.Kind))
            {
                continue;
            }

            best = use;
        }

        return best;
    }

    /// <summary>The runway end whose heading is closest to <paramref name="trackDeg"/>, oriented for that end.</summary>
    public static RunwayInfo AlignedEnd(double trackDeg, RunwayInfo runway)
    {
        double toEnd1 = GeoMath.AbsBearingDifference(trackDeg, runway.TrueHeading1.Degrees);
        double toEnd2 = GeoMath.AbsBearingDifference(trackDeg, runway.TrueHeading2.Degrees);
        string end = toEnd1 <= toEnd2 ? runway.Id.End1 : runway.Id.End2;
        return runway.IsActiveEnd(end) ? runway : runway.ForApproach(end);
    }

    private static bool SameAirport(string a, string b) => NavigationDatabase.AirportIdsMatch(a, b);
}
