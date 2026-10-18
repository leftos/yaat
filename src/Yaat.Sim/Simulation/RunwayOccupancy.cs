using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.LiveTraffic;
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

    /// <summary>
    /// On the ground off the pavement with part of it past the runway's holding position marking (AIM 2-3-5.a.1): approaching
    /// the runway to hold short with a part more than <see cref="RunwayOccupancy.HoldLineFoulToleranceFt"/> past the marking, or
    /// stopped or restricted short of the marking with a part that far past it — leaving the runway, or cleared past the marking
    /// to cross it or line up. Moving unrestricted, a leaver or a cleared aircraft is not a fouler. Only a phase-driven aircraft with a
    /// ground layout is classified so (<see cref="RunwayOccupancy.Classify"/>); the controller-side consumers opt in through
    /// <see cref="RunwayOccupancy.FoulsRunway"/>, and <see cref="RunwayOccupancy.OccupiesSurface"/> stays false for it.
    /// </summary>
    Fouling,
}

/// <summary>One aircraft's use of one runway.</summary>
public sealed record RunwayUse(string Callsign, RunwayInfo Runway, RunwayUseKind Kind);

/// <summary>
/// Classifies an aircraft against a runway independently of its phases, so runway consumers (occupancy advisories,
/// ground-conflict priority, the occupied-runway go-around) can see aircraft the phase machinery does not drive.
///
/// Phase evidence takes precedence over geometry: an aircraft in <see cref="LinedUpAndWaitingPhase"/> is
/// <see cref="RunwayUseKind.OnSurface"/> whether or not its position passes the pavement test (it may be holding at a
/// far-side taxiway node), and a phase-driven aircraft whose phase says nothing about the runway is never an occupant: a
/// <see cref="RunwayUseKind.Crossing"/> on its pavement, a <see cref="RunwayUseKind.Fouling"/> off it past its holding position
/// marking, else nothing — so phase-driven traffic keeps the occupant answers the consumers gave before this classifier existed.
/// Geometry alone decides only for aircraft with no phases at all.
///
/// Datums follow the 7110.65: the <em>pavement</em> rectangle for surface kinds (the pavement behind a displaced
/// threshold is usable for takeoff and rollout, AIM 2-3-3.h.2) and the <em>landing</em> threshold
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
    /// How far (ft) a part of an aircraft must be past a runway's holding position marking — nearer the centreline than the
    /// line of the nearest bar on its side, within <see cref="HoldLineFootprint.MarkedBarReachFt"/> of that bar along the
    /// runway — to count as past it (<see cref="RunwayUseKind.Fouling"/>). Clears the nose of a correct hold, which stops
    /// within <see cref="GroundNavigator.SetBackStopMarginFt"/> of the marking.
    /// </summary>
    public const double HoldLineFoulToleranceFt = 5.0;

    /// <summary>
    /// Ground speed (kts) at or below which an aircraft counts as stopped: a leaving aircraft stopped with part of it still inside
    /// a hold line fouls the runway, and a stopped occupant is never projected clear of it by the occupied-runway go-around.
    /// </summary>
    public const double StoppedGroundSpeedKts = 1.0;

    /// <summary>
    /// Heading tolerance (degrees, modulo 180) of the runway axis within which an aircraft past a hold line is neither approaching
    /// nor leaving the runway — on a parallel taxiway, or turning about — and fouls it only when stopped or restricted. Keeps a
    /// few degrees of drift from flipping the direction. Past a runway end, where the distance is measured to the end, the same
    /// angle off the line of constant distance makes a taxiway passing the end neither approaching nor leaving.
    /// </summary>
    public const double DirectionlessAxisToleranceDeg = 30.0;

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

    /// <summary>
    /// Air-taxi ceiling (P/CG AIR TAXI, §3-11-1.c, AIM 4-3-17.b.3: "normally not above 100 feet AGL"). A rotorcraft over
    /// the pavement below it is a surface movement (§3-11-3 NOTE: air-taxiing helicopters "are considered to be taxiing
    /// aircraft"; AIM 4-3-17.b: taxi, hover taxi and air taxi "are considered to be ground movements"), not an aircraft
    /// in flight. Hover taxi is a subset of air taxi, so the ceiling is not the 25 ft hover-taxi request height.
    /// </summary>
    public const double RotorcraftAirTaxiCeilingFt = 100.0;

    /// <summary>
    /// Hover-taxi speed (§3-11-1.b, P/CG HOVER TAXI: "below 20 knots"). Below it a rotorcraft is stationary over a point
    /// and its heading says nothing about whether it is vacating the runway (§3-11-5 point operations, §3-11-6.a diverse
    /// directions) — a hover check or a crosswind spot landing is still on the runway.
    /// </summary>
    public const double RotorcraftHoverTaxiMaxGroundSpeedKts = 20.0;

    /// <summary>
    /// Ground speed (knots) above which an aligned aircraft on the pavement is unambiguously on its takeoff roll: above
    /// every taxi and back-taxi speed (crews back-taxi at 20–30 kt, §3-7-2.b.10 "without delay"). A jet reaches it 5–7 s
    /// after brake release, a light single ~12 s — the fallback once <see cref="RollStartAccelerationKtPerSec"/> can no
    /// longer be measured, and the only test when there is no sample history.
    /// </summary>
    public const double DepartingMinGroundSpeedKts = 35.0;

    /// <summary>
    /// Earlier roll-start test for feed-driven aircraft: at or above this speed (where a taxi has finished accelerating —
    /// numerically the §3-11-1.b hover-taxi figure) <em>and</em> still accelerating at
    /// <see cref="RollStartAccelerationKtPerSec"/> over <see cref="RollStartWindowSeconds"/>. 2.5 kt/s (≈ 0.12 g) is
    /// below the slowest real takeoff (a C172 at ~3 kt/s) and above any taxi-up. The 4 s window is the surveillance
    /// noise floor, not a preference; the result is "roll start + ~4 s ± 1 s", always late, never early, and halves the
    /// category spread the fixed gate has (jet 6 s vs light single 12 s) that the §3-9-6 / §3-10-3 clocks inherit.
    /// </summary>
    public const double RollStartMinGroundSpeedKts = 20.0;
    public const double RollStartAccelerationKtPerSec = 2.5;
    public const double RollStartWindowSeconds = 4.0;

    /// <summary>
    /// Vertical-speed cap (fpm) for "not climbing" on short final. Level at MDA and a level low approach are legal
    /// (P/CG FINAL APPROACH POINT, §3-10-10), so only a climbing aircraft is excluded.
    /// </summary>
    public const double NotClimbingMaxFpm = 100.0;

    /// <summary>
    /// Classifies <paramref name="ac"/> against <paramref name="runway"/>. Phase evidence first
    /// (<see cref="ClassifyByPhase"/>); geometry (<see cref="ClassifyByGeometry"/>) only for aircraft with no phases;
    /// a phase-driven aircraft on the pavement in any other phase is a <see cref="RunwayUseKind.Crossing"/>, and off it past the
    /// runway's holding position marking a <see cref="RunwayUseKind.Fouling"/> (<see cref="FoulsHoldLine"/>).
    /// <paramref name="layout"/> supplies the landing-threshold displacement and the hold-short bars, and may be null (pavement
    /// threshold, and no aircraft is fouling).
    /// </summary>
    public static RunwayUse? Classify(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout)
    {
        RunwayUseKind? kind =
            ClassifyByPhase(ac, runway) ?? (ac.Phases is null ? ClassifyByGeometry(ac, runway, layout) : ClassifyByPosition(ac, runway, layout));
        if (kind == RunwayUseKind.Departing && ac.LiveTraffic is { LandedOnRunway: true })
        {
            // The runway-use observer saw this shadow land: its rollout is not a takeoff roll (geometry cannot tell).
            kind = RunwayUseKind.OnSurface;
        }

        return kind is { } k ? new RunwayUse(ac.Callsign, runway, k) : null;
    }

    /// <summary>
    /// A phase-driven aircraft whose phase says nothing about <paramref name="runway"/>: <see cref="RunwayUseKind.Crossing"/> on
    /// its pavement, <see cref="RunwayUseKind.Fouling"/> past its hold line (<see cref="FoulsHoldLine"/>), else nothing.
    /// </summary>
    private static RunwayUseKind? ClassifyByPosition(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout)
    {
        if (IsOnPavement(ac, runway))
        {
            return RunwayUseKind.Crossing;
        }

        return ((layout is not null) && IsLayoutOf(layout, runway) && FoulsHoldLine(ac, runway, layout)) ? RunwayUseKind.Fouling : null;
    }

    /// <summary>
    /// Whether <paramref name="layout"/> is the ground layout of <paramref name="runway"/>'s airport. Designators repeat across
    /// airports, and the bars of another airport's runway of the same name say nothing about this one.
    /// </summary>
    internal static bool IsLayoutOf(AirportGroundLayout layout, RunwayInfo runway) => SameAirport(runway.AirportId, layout.AirportId);

    /// <summary>
    /// Whether an aircraft on the ground off <paramref name="runway"/>'s pavement fouls it: a judged part of its body
    /// (<see cref="PartsJudged"/>) more than <see cref="HoldLineFoulToleranceFt"/> past the holding position marking
    /// (<see cref="DepthPastMarkingFt"/>), and either stopped or restricted short of the marking
    /// (<see cref="IsStoppedOrRestricted"/>) or approaching the runway to hold short of it (<see cref="IsApproachingToHoldShort"/>).
    /// A moving aircraft with nothing stopping it is not a fouler when it is leaving the runway (clear of it) or cleared past this
    /// marking (a crossing or line-up under way, which becomes a runway use once on the pavement).
    /// </summary>
    private static bool FoulsHoldLine(AircraftState ac, RunwayInfo runway, AirportGroundLayout layout)
    {
        if (!ac.IsOnGround)
        {
            return false;
        }

        var body = AircraftBody.Of(ac.AircraftType);
        (LatLon, double) pose = (ac.Position, ac.TrueHeading.Degrees);
        bool pastTheMarking = AnyPartPastTheMarking(layout, runway, body, PartsJudged(ac, runway, layout, body), pose);
        return pastTheMarking && (IsStoppedOrRestricted(ac, runway, layout, body) || IsApproachingToHoldShort(ac, runway, layout, body));
    }

    /// <summary>Which way a pose points relative to a runway's centreline (<see cref="FacingOf"/>).</summary>
    private enum Facing
    {
        /// <summary>Neither toward nor away: along the runway, turning about, or passing a runway end.</summary>
        Neither,

        /// <summary>Toward the centreline: approaching the runway.</summary>
        Toward,

        /// <summary>Away from the centreline: leaving the runway.</summary>
        Away,
    }

    /// <summary>The nose alone: the part an aircraft approaching a runway leads with.</summary>
    private static readonly AircraftBodyPart[] NoseOnly = [AircraftBodyPart.Nose];

    /// <summary>The tail alone: the last part of an aircraft leaving a runway to clear its marking.</summary>
    private static readonly AircraftBodyPart[] TailOnly = [AircraftBodyPart.Tail];

    /// <summary>
    /// The parts of the aircraft judged against <paramref name="runway"/>'s holding position marking — the nose when approaching
    /// the runway, the tail when leaving it: the nose holding short at one of this runway's bars, the tail on an exit from this
    /// runway or a hold after it, else as its pose says (<see cref="PartsJudgedAt"/>). A correct hold on a stub angled to the
    /// runway leaves the wingtip on the runway side nearer the centreline than the bar; it is not judged.
    /// </summary>
    private static AircraftBodyPart[] PartsJudged(AircraftState ac, RunwayInfo runway, AirportGroundLayout layout, AircraftBody body)
    {
        if ((ac.Phases?.CurrentPhase is HoldingShortPhase holding) && IsBarOf(layout, holding.HoldShort.NodeId, runway))
        {
            return NoseOnly;
        }

        return IsLeavingByPhase(ac, runway) ? TailOnly : PartsJudgedAt(body, (ac.Position, ac.TrueHeading.Degrees), runway);
    }

    /// <summary>
    /// The parts judged for a pose with no phase evidence of its direction (<see cref="FacingOf"/>): the nose facing toward the
    /// centreline, the tail facing away from it, and every part when it faces neither way (so a wingtip may lead).
    /// </summary>
    private static AircraftBodyPart[] PartsJudgedAt(AircraftBody body, (LatLon Position, double HeadingDeg) pose, RunwayInfo runway) =>
        FacingOf(body, pose, runway) switch
        {
            Facing.Toward => NoseOnly,
            Facing.Away => TailOnly,
            _ => AircraftBody.AllParts,
        };

    /// <summary>
    /// Which way <paramref name="body"/> at <paramref name="pose"/> (main-gear position, heading deg true) points relative to
    /// <paramref name="runway"/>'s centreline: <see cref="Facing.Neither"/> when the heading is within
    /// <see cref="DirectionlessAxisToleranceDeg"/> of the runway axis or its nose is no more than the sine of that angle times its
    /// length nearer or farther from the centreline than its tail — abeam the runway the same test, and past a runway end, where
    /// the distance is measured to the end, a taxiway passing the end; else toward or away as the nose lies nearer or farther.
    /// </summary>
    private static Facing FacingOf(AircraftBody body, (LatLon Position, double HeadingDeg) pose, RunwayInfo runway)
    {
        if (AxisDeviationDeg(pose.HeadingDeg, runway) <= DirectionlessAxisToleranceDeg)
        {
            return Facing.Neither;
        }

        double noseFt = RunwayHoldBars.CentrelineFt(runway, body.PointOf(AircraftBodyPart.Nose, pose.Position, pose.HeadingDeg));
        double tailFt = RunwayHoldBars.CentrelineFt(runway, body.PointOf(AircraftBodyPart.Tail, pose.Position, pose.HeadingDeg));
        double closingFt = tailFt - noseFt;
        if (Math.Abs(closingFt) <= (body.LengthFt * Math.Sin(DirectionlessAxisToleranceDeg * Math.PI / 180.0)))
        {
            return Facing.Neither;
        }

        return (closingFt > 0.0) ? Facing.Toward : Facing.Away;
    }

    /// <summary>
    /// Approaching <paramref name="runway"/> with no clearance past its holding position marking: its phase is not about leaving
    /// or crossing this runway, no cleared hold-short of this runway's bar beside it is on its route
    /// (<see cref="IsClearedPastTheMarking"/>), and it faces toward the centreline (<see cref="FacingOf"/>).
    /// </summary>
    private static bool IsApproachingToHoldShort(AircraftState ac, RunwayInfo runway, AirportGroundLayout layout, AircraftBody body)
    {
        if (IsLeavingOrCrossingByPhase(ac, runway, layout) || IsClearedPastTheMarking(ac, runway, layout))
        {
            return false;
        }

        return FacingOf(body, (ac.Position, ac.TrueHeading.Degrees), runway) == Facing.Toward;
    }

    /// <summary>
    /// Whether any of <paramref name="parts"/> of <paramref name="body"/> at <paramref name="pose"/> (main-gear position, heading
    /// deg true) lies more than <see cref="HoldLineFoulToleranceFt"/> past <paramref name="runway"/>'s holding position marking
    /// (<see cref="DepthPastMarkingFt"/>), against the nearest of the runway's bars on the pose's side
    /// (<see cref="RunwayHoldBars.NearestSameSideBar"/>, found once for the pose). False when that side has no bar.
    /// </summary>
    private static bool AnyPartPastTheMarking(
        AirportGroundLayout layout,
        RunwayInfo runway,
        AircraftBody body,
        AircraftBodyPart[] parts,
        (LatLon Position, double HeadingDeg) pose
    )
    {
        if (RunwayHoldBars.NearestSameSideBar(layout, runway, pose.Position) is not { } bar)
        {
            return false;
        }

        return parts.Any(part => DepthPastMarkingFt(runway, bar, body.PointOf(part, pose.Position, pose.HeadingDeg)) > HoldLineFoulToleranceFt);
    }

    /// <summary>
    /// How far (ft) <paramref name="point"/> lies past <paramref name="runway"/>'s holding position marking toward the centreline
    /// (negative short of it): the line of <paramref name="bar"/> (<see cref="RunwayHoldBars.CentrelineFt"/> of the bar). Null when
    /// the bar is more than <see cref="HoldLineFootprint.MarkedBarReachFt"/> from the point along the runway — no marking is painted
    /// over the grass between taxiways.
    /// </summary>
    private static double? DepthPastMarkingFt(RunwayInfo runway, GroundNode bar, LatLon point)
    {
        if (AlongRunwayFt(runway, point, bar) > HoldLineFootprint.MarkedBarReachFt)
        {
            return null;
        }

        return RunwayHoldBars.CentrelineFt(runway, bar.Position) - RunwayHoldBars.CentrelineFt(runway, point);
    }

    /// <summary>
    /// The phase governs the aircraft's direction for <paramref name="runway"/>: an exit or a hold after it on this runway
    /// (leaving), or a crossing of this runway (cleared past its marking on the near side, leaving it on the far side).
    /// </summary>
    private static bool IsLeavingOrCrossingByPhase(AircraftState ac, RunwayInfo runway, AirportGroundLayout layout) =>
        IsLeavingByPhase(ac, runway) || ((ac.Phases?.CurrentPhase is CrossingRunwayPhase crossing) && IsBarOf(layout, crossing.TargetNodeId, runway));

    /// <summary>The phase is an exit from <paramref name="runway"/> or a hold after it: the aircraft is leaving this runway.</summary>
    private static bool IsLeavingByPhase(AircraftState ac, RunwayInfo runway) =>
        (ac.Phases is { CurrentPhase: RunwayExitPhase or HoldingAfterExitPhase } phases) && PhaseRunwayIs(phases, runway);

    /// <summary>Whether node <paramref name="nodeId"/> is a hold-short bar protecting <paramref name="runway"/>.</summary>
    internal static bool IsBarOf(AirportGroundLayout layout, int nodeId, RunwayInfo runway) =>
        layout.Nodes.TryGetValue(nodeId, out GroundNode? node)
        && (node.Type == GroundNodeType.RunwayHoldShort)
        && (node.RunwayId is { } protectedRunway)
        && protectedRunway.Overlaps(runway.Id);

    /// <summary>
    /// A taxiing aircraft cleared past <paramref name="runway"/>'s holding position marking beside it: its route carries a cleared
    /// hold-short at any of this runway's bars on its side within <see cref="HoldLineFootprint.MarkedBarReachFt"/> of it along the
    /// runway — a crossing cleared before one bar of a wide junction, or a line-up or takeoff clearance that cleared the destination
    /// runway's hold-short.
    /// </summary>
    private static bool IsClearedPastTheMarking(AircraftState ac, RunwayInfo runway, AirportGroundLayout layout)
    {
        if (ac.Ground.AssignedTaxiRoute is not { } route)
        {
            return false;
        }

        return RunwayHoldBars
            .SameSideBars(layout, runway, ac.Position)
            .Any(bar =>
                (AlongRunwayFt(runway, ac.Position, bar) <= HoldLineFootprint.MarkedBarReachFt)
                && (route.GetHoldShortAt(bar.Id) is { IsCleared: true })
            );
    }

    /// <summary>How far (ft) apart <paramref name="point"/> and <paramref name="bar"/> lie measured along <paramref name="runway"/>.</summary>
    private static double AlongRunwayFt(RunwayInfo runway, LatLon point, GroundNode bar) =>
        Math.Abs(GeoMath.AlongTrackDistanceNm(point, bar.Position, runway.TrueHeading)) * GeoMath.FeetPerNm;

    /// <summary>
    /// Stopped (<see cref="StoppedGroundSpeedKts"/>) or restricted short of <paramref name="runway"/>'s holding position marking
    /// (<see cref="IsRestrictedShortOfTheMarking"/>).
    /// </summary>
    private static bool IsStoppedOrRestricted(AircraftState ac, RunwayInfo runway, AirportGroundLayout layout, AircraftBody body) =>
        (ac.GroundSpeed <= StoppedGroundSpeedKts) || IsRestricted(ac, runway, layout, body);

    /// <summary>
    /// Restricted short of <paramref name="runway"/>'s holding position marking, whatever its speed: a hold in effect
    /// (<see cref="IsHoldInEffect"/>), holding short, yielding to traffic by the ground-conflict detector (a zero speed limit with a
    /// yield target — a path blocked by traffic), or with its taxi route stopping it before its body is clear of the marking.
    /// </summary>
    public static bool IsRestrictedShortOfTheMarking(AircraftState ac, RunwayInfo runway, AirportGroundLayout layout) =>
        IsRestricted(ac, runway, layout, AircraftBody.Of(ac.AircraftType));

    private static bool IsRestricted(AircraftState ac, RunwayInfo runway, AirportGroundLayout layout, AircraftBody body)
    {
        AircraftGroundOps ground = ac.Ground;
        if (IsHoldInEffect(ac) || (ac.Phases?.CurrentPhase is HoldingShortPhase))
        {
            return true;
        }

        if ((ground.SpeedLimit is { } limitKts) && (limitKts <= 0.0) && (ground.AutoYieldTarget is not null))
        {
            return true;
        }

        return RouteStopsShortOfTheMarking(ac, runway, layout, body);
    }

    /// <summary>
    /// A HOLD or GIVEWAY that is acting on the aircraft now. A GIVEWAY does not act on its landing runway
    /// (<see cref="AircraftState.HasGiveWayPendingOnLandingRunway"/>) nor during a runway crossing, which runs on until the tail is
    /// past the far marking (<see cref="CrossingRunwayPhase"/>); both apply it once taxiing beyond.
    /// </summary>
    private static bool IsHoldInEffect(AircraftState ac) =>
        ac.Ground.IsImmobile
        && (!ac.HasGiveWayPendingOnLandingRunway)
        && !((ac.Ground.Hold?.Kind == HoldKind.GiveWay) && (ac.Phases?.CurrentPhase is CrossingRunwayPhase));

    /// <summary>
    /// Whether the aircraft's taxi route stops it before its body is clear of <paramref name="runway"/>'s holding position marking:
    /// the first stop ahead — an uncleared hold-short, with the nose at it (<see cref="NoseAt"/>), else the route's end, with the
    /// main gear on its node — leaves a judged part (<see cref="PartsJudgedAt"/>) more than <see cref="HoldLineFoulToleranceFt"/>
    /// past the marking, with the aircraft at that stop facing along the segment ending there.
    /// A route that ends on one of this runway's own bars does not count: the navigator stops it there tail-clear, not centred on
    /// the bar node.
    /// </summary>
    private static bool RouteStopsShortOfTheMarking(AircraftState ac, RunwayInfo runway, AirportGroundLayout layout, AircraftBody body)
    {
        if (ac.Ground.AssignedTaxiRoute is not { } route)
        {
            return false;
        }

        for (int i = Math.Max(0, route.CurrentSegmentIndex); i < route.Segments.Count; i++)
        {
            TaxiRouteSegment segment = route.Segments[i];
            HoldShortPoint? holdShort = route.GetHoldShortAt(segment.ToNodeId);
            bool isLast = i == (route.Segments.Count - 1);
            if ((holdShort is not { IsCleared: false }) && !isLast)
            {
                continue;
            }

            bool uncleared = holdShort is { IsCleared: false };
            if ((!uncleared) && IsBarOf(layout, segment.ToNodeId, runway))
            {
                return false;
            }

            LatLon stop =
                (holdShort is { IsCleared: false, Latitude: { } lat, Longitude: { } lon }) ? new LatLon(lat, lon) : segment.Edge.ToNode.Position;
            double headingDeg = GeoMath.BearingTo(segment.Edge.FromNode.Position, segment.Edge.ToNode.Position);
            LatLon position = uncleared ? NoseAt(stop, headingDeg, body) : stop;
            return AnyPartPastTheMarking(layout, runway, body, PartsJudgedAt(body, (position, headingDeg), runway), (position, headingDeg));
        }

        return false;
    }

    /// <summary>
    /// The main-gear position of <paramref name="body"/> facing <paramref name="headingDeg"/> with its nose at <paramref name="stop"/>:
    /// where the navigator holds an aircraft short (AIM 2-3-5.a.1), half its length back from the stop.
    /// </summary>
    private static LatLon NoseAt(LatLon stop, double headingDeg, AircraftBody body) =>
        GeoMath.ProjectPoint(stop, new TrueHeading(headingDeg).ToReciprocal(), body.LengthFt / 2.0 / GeoMath.FeetPerNm);

    /// <summary>The phase list's own runway (departure, else assigned) names the same pavement as <paramref name="runway"/>.</summary>
    private static bool PhaseRunwayIs(PhaseList phases, RunwayInfo runway)
    {
        RunwayInfo? own = phases.DepartureRunway ?? phases.AssignedRunway;
        return (own is not null) && SameAirport(own.AirportId, runway.AirportId) && own.Id.Overlaps(runway.Id);
    }

    /// <summary>
    /// What the aircraft's current phase says about its runway use, or null when the phase is not a runway phase.
    /// When <paramref name="runway"/> is given the phase's own runway (departure, else assigned) must name the same
    /// pavement; with null the phase alone decides — except <see cref="HoldingInPositionPhase"/>, which YAAT also uses as
    /// a generic ground-idle hold and therefore only counts when it is physically on the given runway's pavement, and
    /// <see cref="AirTaxiPhase"/>, a relocation with no runway of its own that is a surface movement (§3-11-3 NOTE) on
    /// whichever pavement it is over.
    /// </summary>
    public static RunwayUseKind? ClassifyByPhase(AircraftState ac, RunwayInfo? runway)
    {
        PhaseList? phases = ac.Phases;
        Phase? phase = phases?.CurrentPhase;
        if ((phases is null) || (phase is null))
        {
            return null;
        }

        if (phase is AirTaxiPhase)
        {
            return (runway is not null) && IsOverOrOnPavement(ac, runway) ? RunwayUseKind.OnSurface : null;
        }

        if ((runway is not null) && !PhaseRunwayIs(phases, runway))
        {
            return null;
        }

        return phase switch
        {
            TakeoffPhase => RunwayUseKind.Departing,
            LandingPhase or HelicopterLandingPhase => ac.IsOnGround ? RunwayUseKind.OnSurface : RunwayUseKind.Landing,
            LineUpPhase or LinedUpAndWaitingPhase or StopAndGoPhase or TouchAndGoPhase => RunwayUseKind.OnSurface,
            RejectedTakeoffPhase => RunwayUseKind.OnSurface,
            RunwayExitPhase { IsOnCenterline: true } => RunwayUseKind.OnSurface,
            HoldingAfterExitPhase { StoppedInsideHoldingDistance: true } => RunwayUseKind.OnSurface,
            HoldingInPositionPhase when (runway is not null) && IsOnPavement(ac, runway) => RunwayUseKind.OnSurface,
            _ => null,
        };
    }

    /// <summary>
    /// Geometry-only classification: pavement containment and axis alignment on the ground; threshold-crossing height
    /// and the final approach course in the air. Rotorcraft (<see cref="AircraftCategory.Helicopter"/> — an unmapped
    /// rotorcraft type falls to the fixed-wing rules, and powered-lift has no category yet) never take off by rolling
    /// (<see cref="RunwayUseKind.Departing"/>) and are never <see cref="RunwayUseKind.ShortFinal"/> — they do fly
    /// straight-in finals (§3-11-6.a), but the only consequence of that kind is a fixed-wing sequencing signal, and they
    /// arrive at runway points from any direction. Over the pavement below <see cref="RotorcraftAirTaxiCeilingFt"/> they
    /// are a surface movement: descending is <see cref="RunwayUseKind.Landing"/>; below hover-taxi speed
    /// <see cref="RunwayUseKind.OnSurface"/> regardless of heading; otherwise along the axis <see cref="RunwayUseKind.OnSurface"/>
    /// and across it <see cref="RunwayUseKind.Crossing"/>.
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

            return IsRolling(ac) ? RunwayUseKind.Departing : RunwayUseKind.OnSurface;
        }

        if (IsRotorcraft(ac))
        {
            return ClassifyAirborneRotorcraft(ac, runway);
        }

        RunwayInfo alignedEnd = AlignedEnd(ac.TrueTrack.Degrees, runway);
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

        LatLon landingThreshold = LandingThreshold.Resolve(alignedEnd, layout);
        double along = GeoMath.AlongTrackDistanceNm(ac.Position, landingThreshold, alignedEnd.TrueHeading);
        double cross = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, landingThreshold, alignedEnd.TrueHeading));
        bool onFinal = (along < 0) && (-along <= ShortFinalDistanceNm) && (cross <= ShortFinalCrossTrackNm);
        return onFinal ? RunwayUseKind.ShortFinal : null;
    }

    private static RunwayUseKind? ClassifyAirborneRotorcraft(AircraftState ac, RunwayInfo runway)
    {
        // Heading, not track: a hovering rotorcraft has no track, and the datum must not flip between the runway's ends.
        double agl = ac.Altitude - AlignedEnd(ac.TrueHeading.Degrees, runway).ElevationFt;
        if ((agl > RotorcraftAirTaxiCeilingFt) || !IsOverPavement(ac, runway))
        {
            return null;
        }

        if (ac.VerticalSpeed < -NotClimbingMaxFpm)
        {
            return RunwayUseKind.Landing;
        }

        if (ac.GroundSpeed < RotorcraftHoverTaxiMaxGroundSpeedKts)
        {
            return RunwayUseKind.OnSurface;
        }

        return AxisDeviationDeg(ac.TrueHeading.Degrees, runway) > SurfaceAxisToleranceDeg ? RunwayUseKind.Crossing : RunwayUseKind.OnSurface;
    }

    /// <summary>
    /// The takeoff roll has started: past <see cref="DepartingMinGroundSpeedKts"/>, or — for a feed-driven aircraft with
    /// enough history — past <see cref="RollStartMinGroundSpeedKts"/> and accelerating like a takeoff. Rotorcraft never
    /// roll. Does not latch: a rejected takeoff decelerating back through the gate stops being a departure.
    /// </summary>
    public static bool IsRolling(AircraftState ac)
    {
        if (IsRotorcraft(ac))
        {
            return false;
        }

        if (ac.GroundSpeed >= DepartingMinGroundSpeedKts)
        {
            return true;
        }

        return (ac.GroundSpeed >= RollStartMinGroundSpeedKts) && (GroundAccelerationKtPerSec(ac) >= RollStartAccelerationKtPerSec);
    }

    /// <summary>Measured ground acceleration (kt/s) from a shadow's sample history over <see cref="RollStartWindowSeconds"/>; null otherwise.</summary>
    public static double? GroundAccelerationKtPerSec(AircraftState ac) =>
        ac.LiveTraffic is { } lt ? LiveTrafficKinematics.GroundAcceleration(lt, RollStartWindowSeconds) : null;

    public static bool IsRotorcraft(AircraftState ac) => AircraftCategorization.Categorize(ac.AircraftType) == AircraftCategory.Helicopter;

    /// <summary>Pavement containment for an aircraft that may be on the ground or air-taxiing just above it.</summary>
    public static bool IsOverOrOnPavement(AircraftState ac, RunwayInfo runway) =>
        ac.IsOnGround ? IsOnPavement(ac, runway) : IsOverPavement(ac, runway);

    /// <summary>On the ground and within the runway half-width plus <see cref="LateralSlackFt"/> of the centerline segment.</summary>
    public static bool IsOnPavement(AircraftState ac, RunwayInfo runway) => ac.IsOnGround && IsWithinPavement(ac.Position, runway);

    /// <summary>
    /// On the pavement of one of <paramref name="runways"/> and aligned with its axis (within
    /// <see cref="SurfaceAxisToleranceDeg"/>): taxiing along it, back-taxiing, or holding on it — a use of the runway
    /// "for purposes other than crossing", which 7110.65 §3-1-3.a.4 gives to local control whatever the ground phase.
    /// </summary>
    public static bool IsAlongRunway(AircraftState ac, IReadOnlyList<RunwayInfo> runways) =>
        runways.Any(runway => IsOnPavement(ac, runway) && (AxisDeviationDeg(ac.TrueHeading.Degrees, runway) <= SurfaceAxisToleranceDeg));

    /// <summary>True for the kinds that make the aircraft a runway occupant for clearance and priority purposes.</summary>
    public static bool OccupiesSurface(RunwayUseKind? kind) => kind is RunwayUseKind.Departing or RunwayUseKind.Landing or RunwayUseKind.OnSurface;

    /// <summary>
    /// True for an aircraft past the runway's holding position marking off its pavement (<see cref="RunwayUseKind.Fouling"/>):
    /// the runway is not clear for the controller's clearances and separation, though the aircraft is not on it.
    /// </summary>
    public static bool FoulsRunway(RunwayUseKind? kind) => kind == RunwayUseKind.Fouling;

    /// <summary>
    /// Distance (nm) from the aircraft to <paramref name="runway"/>'s landing threshold along the final approach course
    /// of the runway end the aircraft is tracking toward. Negative once past the threshold.
    /// </summary>
    public static double DistanceToLandingThresholdNm(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout)
    {
        RunwayInfo alignedEnd = AlignedEnd(ac.TrueTrack.Degrees, runway);
        LatLon landingThreshold = LandingThreshold.Resolve(alignedEnd, layout);
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

    /// <summary>
    /// Distance (nm) from the aircraft to <paramref name="runway"/>'s landing threshold measured along
    /// <em>that runway's</em> final approach course — for a caller that already knows which runway the aircraft is
    /// landing on and must not have the datum move. Negative once past the threshold.
    ///
    /// <para>The difference from <see cref="DistanceToLandingThresholdNm"/> is which end is measured to.
    /// That helper derives the end from the aircraft's own track, which is what its short-final callers want (they
    /// ask which end an aircraft is <em>using</em>); here the end comes from the assignment. An arrival still on
    /// downwind or base for 28R tracks ~117° and aligns to the 10L end, so the track-derived figure measures to the
    /// reciprocal threshold and reports a negative distance; at 90° off axis it flips between the two ends on a 1°
    /// track wobble. An arrival-sequencing caller ordering a stream by it would put that aircraft at the front and
    /// flap the order tick to tick.</para>
    /// </summary>
    public static double DistanceToAssignedThresholdNm(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout) =>
        -GeoMath.AlongTrackDistanceNm(ac.Position, LandingThreshold.Resolve(runway, layout), runway.TrueHeading);

    /// <summary>
    /// Seconds until the aircraft reaches <paramref name="runway"/>'s landing threshold at its present ground speed,
    /// measured along that runway's own course (<see cref="DistanceToAssignedThresholdNm"/>) rather than the
    /// aircraft's track. Positive infinity when stopped, negative once past the threshold.
    /// </summary>
    public static double SecondsToAssignedThreshold(AircraftState ac, RunwayInfo runway, AirportGroundLayout? layout)
    {
        double groundSpeed = ac.GroundSpeed;
        if (groundSpeed <= 0)
        {
            return double.PositiveInfinity;
        }

        return DistanceToAssignedThresholdNm(ac, runway, layout) / groundSpeed * 3600.0;
    }

    /// <summary>A position within the runway half-width plus <see cref="LateralSlackFt"/> of the centerline segment.</summary>
    public static bool IsWithinPavement(LatLon position, RunwayInfo runway)
    {
        double distFt = GeoMath.DistanceToSegmentFt(position, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2));
        return distFt <= (runway.WidthFt / 2.0) + LateralSlackFt;
    }

    /// <summary>Airborne counterpart of <see cref="IsOnPavement"/>: between the two ends and inside the pavement edge.</summary>
    private static bool IsOverPavement(AircraftState ac, RunwayInfo runway)
    {
        (LatLon _, double _, bool clamped) = GeoMath.FootOfPerpendicular(
            ac.Position,
            new LatLon(runway.Lat1, runway.Lon1),
            new LatLon(runway.Lat2, runway.Lon2)
        );
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

        RunwayInfo end = AlignedEnd(ac.TrueTrack.Degrees, runway);
        if (GeoMath.AbsBearingDifference(ac.TrueTrack.Degrees, end.TrueHeading.Degrees) > OnFinalTrackToleranceDeg)
        {
            return false;
        }

        LatLon threshold = LandingThreshold.Resolve(end, layout);
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
        foreach (RunwayInfo pavement in runways)
        {
            RunwayInfo end = AlignedEnd(ac.TrueTrack.Degrees, pavement);
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

        IReadOnlyList<RunwayInfo> runways = navDb.GetRunways(airport);
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
        foreach (RunwayInfo pavement in runways)
        {
            RunwayUse? use = Classify(ac, AlignedEnd(ac.TrueTrack.Degrees, pavement), layout);
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
