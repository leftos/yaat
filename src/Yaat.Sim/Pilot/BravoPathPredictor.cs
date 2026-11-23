using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Pilot;

/// <summary>One sample of a predicted path: where the aircraft is, at what altitude, how far along and how many seconds in.</summary>
public readonly record struct PathPoint(LatLon Position, double AltitudeFt, double AlongNm, double Seconds);

/// <summary>
/// The path a running phase will fly from the aircraft's present position, sampled once per second of travel at the present
/// ground speed (<see cref="BravoPathPredictor.Predict"/>). Turns are flown at a turn rate, so each turn is an arc; the
/// first point is the present position.
/// </summary>
public sealed record PredictedPath(IReadOnlyList<PathPoint> Points);

/// <summary>Where a predicted path first enters a Class B volume whose band holds the path's altitude there.</summary>
/// <param name="Vertical">The path was already inside the volume's footprint, below or above its band, at the sample before.</param>
public sealed record BravoPathEntry(AirspaceVolume Volume, LatLon Point, double AlongNm, double Seconds, double AltitudeFt, bool Vertical);

/// <summary>
/// Predicts the path a running pattern or approach phase will fly and finds where it first enters a Class B. The plan is
/// the phase's waypoints and those of the Pending phases behind it: a pattern entry's lead-in and entry point; the upwind,
/// crosswind and downwind legs' end points; the base's final-turn point (as <see cref="BasePhase"/> aims it) and the
/// threshold; an approach's remaining fixes; a final's threshold. The
/// plan stops at a full-stop landing's threshold; otherwise the path flies on straight to the horizon: on runway heading
/// past a low approach, touch-and-go or option threshold, along the downwind heading on an extended downwind or one already
/// past its base turn, and on its last track after a missed approach's last fix or before a phase with no path source.
/// <para>
/// The path is flown by a 1 s kinematic walk at the present ground speed, turning toward each waypoint at its leg's turn
/// rate (the pattern rate on pattern legs, standard rate otherwise, the running phase's own override first) and leading a
/// fly-by turn by its turn radius (a fly-over fix gets no lead), so turns are arcs and samples are 1 s apart. The altitude
/// follows the present vertical speed toward the planned altitude (the altitude target, else the phase's pattern altitude),
/// stops there, and holds the present altitude while the vertical speed points away from it. A running phase with no path
/// source gets null: the caller keeps the straight ray.
/// </para>
/// </summary>
public static class BravoPathPredictor
{
    private static readonly ILogger Log = SimLog.CreateLogger("BravoPathPredictor");

    /// <summary>The trigger's horizon: how far ahead, in seconds of travel, the in-phase hold and the RPO warning look.</summary>
    public const double TriggerLookaheadSeconds = 60.0;

    /// <summary>
    /// The self-release's horizon (<see cref="PredictResume"/>): long enough for the whole remaining pattern or approach at
    /// pattern speeds; a plan that stops earlier ends where it stops.
    /// </summary>
    public const double ReleaseHorizonSeconds = 600.0;

    /// <summary>The smallest turn margin (<see cref="TurnMarginNm"/>), as <c>BasePhase</c> floors its radius.</summary>
    public const double MinTurnMarginNm = 0.15;

    /// <summary>The ground speed the walk never goes below, so a slow or stationary aircraft still advances.</summary>
    private const double MinWalkSpeedKt = 30.0;

    /// <summary>Each Class B volume's lat/lon box, built once per volume for <see cref="AnyBravoWithin"/>.</summary>
    private static readonly ConditionalWeakTable<AirspaceVolume, VolumeBox> Boxes = [];

    /// <summary>
    /// The path from the aircraft's present position through <paramref name="from"/> and the phases behind it in
    /// <paramref name="phases"/>, <paramref name="horizonSeconds"/> of travel long or shorter where it stops at a full-stop
    /// threshold; null when <paramref name="from"/> has no path source (VFR follow, helicopter approach, departures, a
    /// pattern leg without waypoints, an approach with no fix left).
    /// </summary>
    public static PredictedPath? Predict(AircraftState aircraft, PhaseList phases, Phase from, PhaseContext ctx, double horizonSeconds) =>
        PredictFrom(
            aircraft,
            phases,
            new PlanStart(phases.Phases.IndexOf(from), from, ctx.Targets.NavigationRoute, ResumedBy: null),
            ctx,
            horizonSeconds
        );

    /// <summary>
    /// Whether <paramref name="phase"/> is a kind the predictor builds a path for: a pattern entry, an approach's fixes, a
    /// final, or an upwind, crosswind, downwind or base leg. Any other phase (a teardrop re-entry, a midfield crossing, a
    /// procedure turn, a hold, a climb-out, a go-around) has none.
    /// </summary>
    public static bool HasPathSource(Phase phase) =>
        phase is PatternEntryPhase or ApproachNavigationPhase or FinalApproachPhase or DownwindPhase or BasePhase or UpwindPhase or CrosswindPhase;

    /// <summary>
    /// The path the end of <paramref name="hold"/>, the boundary hold standing in front of <see cref="PhaseList.HeldPhase"/>,
    /// would fly, from the aircraft's present position, <see cref="ReleaseHorizonSeconds"/> long: <paramref name="resumed"/>
    /// (the held phase itself, or the restart copy its <see cref="Phase.ResumeAfterBoundaryHold"/> builds, not started) and the
    /// phases behind the held one, with the route the hold stashed read as the running phase's route. The walk flies a
    /// controller altitude issued during the hold, which the hold's end leaves standing, else the plan's own altitude (the
    /// altitude the resumed leg targets), else the live target (the orbit's level-off); from level flight, or a vertical speed
    /// pointing away from it, it climbs or descends there at the category's rate. Null when no phase is held or
    /// <paramref name="resumed"/> has no path source.
    /// </summary>
    public static PredictedPath? PredictResume(
        AircraftState aircraft,
        PhaseList phases,
        Phase resumed,
        PhaseContext ctx,
        AirspaceBoundaryHoldPhase hold
    )
    {
        if (phases.HeldPhase is not { } held)
        {
            return null;
        }

        var start = new PlanStart(phases.Phases.IndexOf(held), resumed, hold.StashedRoute, ResumedBy: hold);
        return PredictFrom(aircraft, phases, start, ctx, ReleaseHorizonSeconds);
    }

    /// <summary>
    /// Whether no sample of <paramref name="path"/> lies inside a volume of the Class B <paramref name="area"/> whose band
    /// holds the path's altitude there, or within <paramref name="marginNm"/> of its footprint: the self-release's test,
    /// the present position (the first sample) included.
    /// </summary>
    public static bool KeepsClearOf(PredictedPath path, AirspaceDatabase airspace, string area, double marginNm)
    {
        IReadOnlyList<AirspaceVolume> volumes = airspace.ClassBArea(area);
        foreach (PathPoint point in path.Points)
        {
            foreach (AirspaceVolume volume in volumes)
            {
                if (Boxes.GetValue(volume, VolumeBox.Of).IsNear(point.Position, marginNm) && Holds(volume, point, marginNm))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static PredictedPath? PredictFrom(AircraftState aircraft, PhaseList phases, PlanStart start, PhaseContext ctx, double horizonSeconds)
    {
        PathPlan? plan = BuildPlan(aircraft, phases, start, ctx);
        if (plan is null)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("{Callsign}: {Phase} has no path source, the ray is used", aircraft.Callsign, start.First.Name);
            }

            return null;
        }

        // A resumed plan flies a controller altitude issued during the hold, else its own planned altitude: the live target is
        // the orbit's level-off, not where the phase goes.
        VerticalPlan vertical = start.ResumedBy is { } hold
            ? new VerticalPlan(hold.ControllerAltitudeLeftStanding(ctx) ?? plan.PlannedAltitude ?? ctx.Targets.TargetAltitude, ctx.Category)
            : new VerticalPlan(ctx.Targets.TargetAltitude ?? plan.PlannedAltitude, null);
        return new PredictedPath(Walk(aircraft, plan, vertical, horizonSeconds));
    }

    /// <summary>The part of <paramref name="path"/> no later than <paramref name="horizonSeconds"/>, by whole-second sample time.</summary>
    public static PredictedPath Cut(PredictedPath path, double horizonSeconds) => new([.. path.Points.Where(p => p.Seconds <= horizonSeconds)]);

    /// <summary>
    /// The first sample of <paramref name="path"/> inside a Class B volume (within <paramref name="marginNm"/> of its
    /// footprint when the margin is positive) whose band holds the path's altitude there, entered from a sample that was
    /// not; a volume the present position is already inside is skipped. Any clip counts: there is no minimum penetration.
    /// <paramref name="onlyArea"/> limits the test to one Class B area; null tests them all.
    /// </summary>
    public static BravoPathEntry? FirstEntry(PredictedPath path, AirspaceDatabase airspace, double marginNm, string? onlyArea)
    {
        if (path.Points.Count == 0)
        {
            return null;
        }

        List<AirspaceVolume> volumes = CandidateVolumes(airspace, path.Points[0], marginNm, onlyArea);
        for (int i = 1; (i < path.Points.Count) && (volumes.Count > 0); i++)
        {
            foreach (AirspaceVolume volume in volumes)
            {
                if (EntryAt(volume, path.Points[i - 1], path.Points[i], marginNm) is { } entry)
                {
                    return entry;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The radius of the orbit the aircraft flies at its present ground speed and its category's turn rate
    /// (<see cref="CategoryPerformance.TurnRate"/>), floored at <see cref="MinTurnMarginNm"/>; no planning-speed floor.
    /// </summary>
    public static double TurnMarginNm(AircraftState aircraft)
    {
        double rate = CategoryPerformance.TurnRate(AircraftCategorization.Categorize(aircraft.AircraftType));
        return Math.Max(TurnShape.RadiusNm(aircraft.GroundSpeed, rate), MinTurnMarginNm);
    }

    /// <summary>
    /// The cheap gate in front of the phase context: whether any Class B volume's lat/lon box lies within the distance
    /// <paramref name="horizonSeconds"/> of travel at the present ground speed covers, plus <see cref="TurnMarginNm"/>.
    /// </summary>
    public static bool AnyBravoWithin(AirspaceDatabase airspace, AircraftState aircraft, double horizonSeconds)
    {
        double reachNm = (WalkSpeedKt(aircraft) * horizonSeconds / 3600.0) + TurnMarginNm(aircraft);
        LatLon at = aircraft.Position;
        double dLat = reachNm / 60.0;
        double dLon = reachNm / (60.0 * Math.Max(Math.Cos(at.Lat * Math.PI / 180.0), 0.01));
        foreach (IReadOnlyList<AirspaceVolume> area in airspace.ClassBAreas.Values)
        {
            foreach (AirspaceVolume volume in area)
            {
                VolumeBox box = Boxes.GetValue(volume, VolumeBox.Of);
                bool near =
                    (at.Lat + dLat >= box.MinLat) && (at.Lat - dLat <= box.MaxLat) && (at.Lon + dLon >= box.MinLon) && (at.Lon - dLon <= box.MaxLon);
                if (near)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A volume's lat/lon box.</summary>
    private sealed record VolumeBox(double MinLat, double MaxLat, double MinLon, double MaxLon)
    {
        public static VolumeBox Of(AirspaceVolume volume)
        {
            IEnumerable<LatLon> points = volume.Rings.SelectMany(r => r);
            return points.Any()
                ? new VolumeBox(points.Min(p => p.Lat), points.Max(p => p.Lat), points.Min(p => p.Lon), points.Max(p => p.Lon))
                : new VolumeBox(double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
        }

        /// <summary>Whether <paramref name="position"/> lies within <paramref name="marginNm"/> of this box (a cheap reject).</summary>
        public bool IsNear(LatLon position, double marginNm)
        {
            double dLat = marginNm / 60.0;
            double dLon = marginNm / (60.0 * Math.Max(Math.Cos(position.Lat * Math.PI / 180.0), 0.01));
            return (position.Lat + dLat >= MinLat)
                && (position.Lat - dLat <= MaxLat)
                && (position.Lon + dLon >= MinLon)
                && (position.Lon - dLon <= MaxLon);
        }
    }

    /// <summary>
    /// Where a plan starts: the index in the phase list the phases behind it follow, the phase whose leg comes first (the
    /// phase at that index, or a restart copy of it), the route read as that phase's live route, and the boundary hold whose
    /// end the plan resumes from (null for the running phase's own plan).
    /// </summary>
    private readonly record struct PlanStart(int Index, Phase First, IReadOnlyList<NavigationTarget> Route, AirspaceBoundaryHoldPhase? ResumedBy);

    /// <summary>One waypoint of a plan: where, at what turn rate its leg is flown, and whether it is flown over (no lead).</summary>
    private readonly record struct PathWaypoint(LatLon Position, double TurnRateDegPerSec, bool IsFlyOver);

    /// <summary>
    /// The waypoints a path walks, the altitude it plans to fly, and what follows the last waypoint: a stop
    /// (<paramref name="Stops"/>), or straight on to the horizon, on <paramref name="EndHeading"/> or on the last track.
    /// </summary>
    private sealed record PathPlan(List<PathWaypoint> Waypoints, double? PlannedAltitude, bool Stops, TrueHeading? EndHeading);

    /// <summary>How a phase's leg ends the plan: it does not, at a threshold, straight on a heading, or straight on the last track.</summary>
    private enum LegEnd
    {
        Continue,
        Threshold,
        StraightOnHeading,
        StraightOnLastTrack,
    }

    /// <summary>What one phase adds to the plan: its waypoints, its planned altitude, how it ends the plan, and the heading after.</summary>
    private sealed record PhaseLeg(IReadOnlyList<PathWaypoint> Waypoints, double? PlannedAltitude, LegEnd End, TrueHeading? EndHeading = null);

    /// <summary>
    /// The running phase's turn rate (<paramref name="IsCurrent"/>): its own override first, else the leg's default; and the
    /// route read as the running phase's live route (<paramref name="Route"/>); and the altitude target standing as the first
    /// leg starts (<paramref name="StandingTargetFt"/>, <see cref="StandingTargetFt(PlanStart, PhaseContext)"/>).
    /// </summary>
    private readonly record struct LegContext(
        AircraftState Aircraft,
        PhaseContext Ctx,
        bool IsCurrent,
        IReadOnlyList<NavigationTarget> Route,
        double? StandingTargetFt
    )
    {
        public double Rate(double legDefault) => IsCurrent ? (Ctx.Targets.TurnRateOverride ?? legDefault) : legDefault;

        public double StandardRate => AircraftPerformance.TurnRate(Aircraft.AircraftType, Ctx.Category);
    }

    /// <summary>
    /// The altitude target standing as the plan's first leg starts: the one the end of the hold it resumes from leaves
    /// (<see cref="AirspaceBoundaryHoldPhase.TargetAltitudeAfterEnd"/>), else the live one.
    /// </summary>
    private static double? StandingTargetFt(PlanStart start, PhaseContext ctx) =>
        start.ResumedBy is { } hold ? hold.TargetAltitudeAfterEnd(ctx) : ctx.Targets.TargetAltitude;

    private static PathPlan? BuildPlan(AircraftState aircraft, PhaseList phases, PlanStart start, PhaseContext ctx)
    {
        double? standingFt = StandingTargetFt(start, ctx);
        PhaseLeg? first = start.Index < 0 ? null : LegOf(start.First, new LegContext(aircraft, ctx, IsCurrent: true, start.Route, standingFt));
        if (first is null)
        {
            return null;
        }

        var waypoints = new List<PathWaypoint>();
        double? plannedAltitude = null;
        PhaseLeg? leg = first;
        var behind = new LegContext(aircraft, ctx, IsCurrent: false, start.Route, standingFt);
        for (int i = start.Index; leg is not null; leg = (++i < phases.Phases.Count) ? LegOf(phases.Phases[i], behind) : null)
        {
            waypoints.AddRange(leg.Waypoints);
            plannedAltitude ??= leg.PlannedAltitude;
            if (leg.End != LegEnd.Continue)
            {
                (bool stops, TrueHeading? heading) = EndOf(leg, phases, i);
                return new PathPlan(waypoints, plannedAltitude, stops, heading);
            }
        }

        // The phases ran out, or the next one has no path source: straight on the last track.
        return new PathPlan(waypoints, plannedAltitude, Stops: false, EndHeading: null);
    }

    /// <summary>
    /// How the plan ends after <paramref name="leg"/>: a threshold stops it for a full-stop landing (cleared to land or for a
    /// stop-and-go, or no landing clearance yet with a landing or stop-and-go next) and otherwise goes on on runway heading.
    /// </summary>
    private static (bool Stops, TrueHeading? Heading) EndOf(PhaseLeg leg, PhaseList phases, int index)
    {
        if (leg.End != LegEnd.Threshold)
        {
            return (false, leg.End == LegEnd.StraightOnHeading ? leg.EndHeading : null);
        }

        // A base leg ends at the threshold itself, so the final behind it adds nothing: the landing kind is what follows.
        Phase? next = phases.Phases.Skip(index + 1).FirstOrDefault(p => p is not FinalApproachPhase);
        bool fullStop = phases.LandingClearance switch
        {
            ClearanceType.ClearedToLand or ClearanceType.ClearedStopAndGo => true,
            null => next is LandingPhase or StopAndGoPhase,
            _ => false,
        };
        return (fullStop, leg.EndHeading);
    }

    private static PhaseLeg? LegOf(Phase phase, LegContext lc) =>
        phase switch
        {
            PatternEntryPhase entry => PatternEntryLeg(entry, lc),
            ApproachNavigationPhase approach => ApproachLeg(approach, lc),
            FinalApproachPhase => lc.Ctx.Runway is { } runway
                ? new PhaseLeg([new PathWaypoint(Threshold(runway), lc.Rate(lc.StandardRate), false)], null, LegEnd.Threshold, runway.TrueHeading)
                : null,
            DownwindPhase downwind => DownwindLeg(downwind, lc),
            BasePhase basePhase => BaseLeg(basePhase, lc),
            _ => UpwindOrCrosswindLeg(phase, lc),
        };

    /// <summary>A pattern entry's lead-in (until the live route shows it passed) and entry point, at the pattern altitude.</summary>
    private static PhaseLeg PatternEntryLeg(PatternEntryPhase entry, LegContext lc)
    {
        double rate = lc.StandardRate;
        var points = new List<PathWaypoint>(2);
        IReadOnlyList<NavigationTarget> route = lc.Route;
        bool leadInPassed =
            lc.IsCurrent && (route.Count > 0) && !route.Any(t => string.Equals(t.Name, PatternEntryPhase.LeadInTargetName, StringComparison.Ordinal));
        if ((entry.LeadInLat is { } lat) && (entry.LeadInLon is { } lon) && !leadInPassed)
        {
            points.Add(new PathWaypoint(new LatLon(lat, lon), rate, false));
        }

        points.Add(new PathWaypoint(new LatLon(entry.EntryLat, entry.EntryLon), rate, false));
        return new PhaseLeg(points, entry.PatternAltitude, LegEnd.Continue);
    }

    /// <summary>An approach's fixes from the current one (all of them for a phase not yet running); a missed approach flies on.</summary>
    private static PhaseLeg? ApproachLeg(ApproachNavigationPhase approach, LegContext lc)
    {
        int first = lc.IsCurrent ? Math.Max(approach.CurrentFixIndex, 0) : 0;
        if (first >= approach.Fixes.Count)
        {
            return lc.IsCurrent ? null : new PhaseLeg([], null, LegEnd.Continue);
        }

        double rate = lc.Rate(lc.StandardRate);
        List<PathWaypoint> points =
        [
            .. approach.Fixes.Skip(first).Select(f => new PathWaypoint(new LatLon(f.Latitude, f.Longitude), rate, f.IsFlyOver)),
        ];
        return new PhaseLeg(points, null, approach.IsMissedApproach ? LegEnd.StraightOnLastTrack : LegEnd.Continue);
    }

    /// <summary>
    /// A downwind's base turn point; an extended downwind, or a running one already at or past its base turn point along
    /// the downwind heading, never turns base on its own: straight on along the downwind heading. Planned at the altitude the
    /// leg targets (<see cref="DownwindPhase.PlannedLegAltitudeFt"/>).
    /// </summary>
    private static PhaseLeg? DownwindLeg(DownwindPhase downwind, LegContext lc)
    {
        if (downwind.Waypoints is not { } wp)
        {
            return null;
        }

        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        bool pastBase = lc.IsCurrent && (GeoMath.AlongTrackDistanceNm(lc.Aircraft.Position, baseTurn, wp.DownwindHeading) >= 0);
        if (downwind.IsExtended || pastBase)
        {
            return new PhaseLeg([], downwind.PlannedLegAltitudeFt, LegEnd.StraightOnHeading, wp.DownwindHeading);
        }

        return new PhaseLeg([new PathWaypoint(baseTurn, PatternRate(wp, lc), false)], downwind.PlannedLegAltitudeFt, LegEnd.Continue);
    }

    /// <summary>
    /// A base leg's final-turn point and the threshold, the final-turn point as <see cref="BasePhase"/> aims it:
    /// <see cref="BasePhase.FinalDistanceNm"/> out on the final when set, else the running aircraft's own distance out along
    /// the final (floored at one pattern turn radius), else the base turn point's.
    /// </summary>
    private static PhaseLeg? BaseLeg(BasePhase basePhase, LegContext lc)
    {
        if (basePhase.Waypoints is not { } wp)
        {
            return null;
        }

        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        TrueHeading outbound = wp.FinalHeading.ToReciprocal();
        LatLon from = lc.IsCurrent ? lc.Aircraft.Position : new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        double ownOut = GeoMath.AlongTrackDistanceNm(from, threshold, outbound);
        double outNm =
            basePhase.FinalDistanceNm
            ?? (lc.IsCurrent ? Math.Max(ownOut, BasePhase.TurnRadiusNm(lc.Aircraft.GroundSpeed, lc.Ctx.Category, wp)) : ownOut);
        double rate = PatternRate(wp, lc);
        PathWaypoint[] points = [new(GeoMath.ProjectPoint(threshold, outbound, outNm), rate, false), new(threshold, rate, false)];
        return new PhaseLeg(points, wp.PatternAltitude, LegEnd.Threshold, wp.FinalHeading);
    }

    /// <summary>
    /// An upwind's crosswind turn point or a crosswind's downwind start, planned at the altitude the leg targets
    /// (<see cref="UpwindPhase.PlannedLegAltitudeFt"/>, <see cref="CrosswindPhase.PlannedLegAltitudeFt"/>); a running leg
    /// already past it adds nothing.
    /// </summary>
    private static PhaseLeg? UpwindOrCrosswindLeg(Phase phase, LegContext lc)
    {
        (PatternWaypoints? wp, bool upwind, double? plannedFt) = phase switch
        {
            UpwindPhase u => (u.Waypoints, true, u.PlannedLegAltitudeFt),
            CrosswindPhase c => (c.Waypoints, false, c.PlannedLegAltitudeFt(lc.Aircraft.Altitude, lc.StandingTargetFt)),
            _ => (null, false, null),
        };
        if (wp is null)
        {
            return null;
        }

        LatLon end = upwind ? new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon) : new LatLon(wp.DownwindStartLat, wp.DownwindStartLon);
        TrueHeading heading = upwind ? wp.UpwindHeading : wp.CrosswindHeading;
        bool passed = lc.IsCurrent && (GeoMath.AlongTrackDistanceNm(lc.Aircraft.Position, end, heading) >= 0);
        IReadOnlyList<PathWaypoint> points = passed ? [] : [new PathWaypoint(end, PatternRate(wp, lc), false)];
        return new PhaseLeg(points, plannedFt, LegEnd.Continue);
    }

    private static double PatternRate(PatternWaypoints wp, LegContext lc) => lc.Rate(PatternCompression.TurnRate(wp, lc.Ctx.Category));

    private static LatLon Threshold(RunwayInfo runway) => new(runway.ThresholdLatitude, runway.ThresholdLongitude);

    private static double WalkSpeedKt(AircraftState aircraft) => Math.Max(aircraft.GroundSpeed, MinWalkSpeedKt);

    /// <summary>The moving state of the 1 s walk.</summary>
    private struct WalkState
    {
        public LatLon Position;
        public TrueHeading Track;
        public double Altitude;
        public double AlongNm;
        public double Seconds;
    }

    private static List<PathPoint> Walk(AircraftState aircraft, PathPlan plan, VerticalPlan vertical, double horizonSeconds)
    {
        double speedKt = WalkSpeedKt(aircraft);
        double stepNm = speedKt / 3600.0;
        var state = new WalkState
        {
            Position = aircraft.Position,
            Track = aircraft.TrueTrack,
            Altitude = aircraft.Altitude,
        };
        var points = new List<PathPoint> { Sample(state) };
        List<PathWaypoint> waypoints = plan.Waypoints;
        double lastRate =
            waypoints.Count > 0
                ? waypoints[^1].TurnRateDegPerSec
                : CategoryPerformance.TurnRate(AircraftCategorization.Categorize(aircraft.AircraftType));
        int next = 0;
        while (state.Seconds < horizonSeconds)
        {
            if ((next < waypoints.Count) && IsPassed(state, waypoints, next, speedKt))
            {
                next++;
                continue;
            }

            if ((next >= waypoints.Count) && plan.Stops)
            {
                break;
            }

            (TrueHeading desired, double rate) =
                next < waypoints.Count
                    ? (new TrueHeading(GeoMath.BearingTo(state.Position, waypoints[next].Position)), waypoints[next].TurnRateDegPerSec)
                    : (plan.EndHeading ?? state.Track, lastRate);
            Step(ref state, desired, rate, stepNm);
            state.Altitude = NextAltitude(aircraft, state.Altitude, vertical);
            points.Add(Sample(state));
        }

        return points;
    }

    private static PathPoint Sample(WalkState state) => new(state.Position, state.Altitude, state.AlongNm, state.Seconds);

    /// <summary>
    /// Whether waypoint <paramref name="index"/> is reached: within one step, inside the circle the aircraft would turn on
    /// toward it, measured from that turn's centre, whatever its bearing (turning toward it would only circle it), or, for a
    /// fly-by waypoint, within the lead of the turn onto the next leg.
    /// </summary>
    private static bool IsPassed(WalkState state, List<PathWaypoint> waypoints, int index, double speedKt)
    {
        PathWaypoint target = waypoints[index];
        double stepNm = speedKt / 3600.0;
        double distanceNm = GeoMath.DistanceNm(state.Position, target.Position);
        var inbound = new TrueHeading(GeoMath.BearingTo(state.Position, target.Position));
        double radiusNm = TurnShape.RadiusNm(speedKt, target.TurnRateDegPerSec);
        if ((distanceNm <= stepNm) || IsInsideTurnCircle(state, target.Position, inbound, radiusNm))
        {
            return true;
        }

        if (target.IsFlyOver || (index + 1 >= waypoints.Count))
        {
            return false;
        }

        PathWaypoint after = waypoints[index + 1];
        var outbound = new TrueHeading(GeoMath.BearingTo(target.Position, after.Position));
        double turnDeg = Math.Min(inbound.AbsAngleTo(outbound), 150.0);
        return distanceNm <= TurnShape.RadiusNm(speedKt, after.TurnRateDegPerSec) * Math.Tan(turnDeg * Math.PI / 360.0);
    }

    /// <summary>
    /// Whether <paramref name="point"/> lies inside the circle of <paramref name="radiusNm"/> the aircraft turns on toward
    /// it (<paramref name="inbound"/>), centred abeam the aircraft on the side it turns to.
    /// </summary>
    private static bool IsInsideTurnCircle(WalkState state, LatLon point, TrueHeading inbound, double radiusNm)
    {
        double side = state.Track.SignedAngleTo(inbound) >= 0 ? 90.0 : -90.0;
        LatLon centre = GeoMath.ProjectPoint(state.Position, new TrueHeading(state.Track.Degrees + side), radiusNm);
        return GeoMath.DistanceNm(centre, point) < radiusNm;
    }

    /// <summary>One second of flight: turn toward <paramref name="desired"/> at the turn rate, then advance one step.</summary>
    private static void Step(ref WalkState state, TrueHeading desired, double rateDegPerSec, double stepNm)
    {
        double turn = Math.Clamp(state.Track.SignedAngleTo(desired), -rateDegPerSec, rateDegPerSec);
        state.Track = new TrueHeading(state.Track.Degrees + turn);
        state.Position = GeoMath.ProjectPoint(state.Position, state.Track, stepNm);
        state.AlongNm += stepNm;
        state.Seconds += 1.0;
    }

    /// <summary>
    /// One second of the vertical speed toward the planned altitude (<see cref="VerticalSpeedFpm"/>), stopped there; a
    /// vertical speed pointing away from it (or none) holds the present altitude. With no planned altitude the vertical
    /// speed is flown.
    /// </summary>
    private static double NextAltitude(AircraftState aircraft, double altitude, VerticalPlan vertical)
    {
        double verticalSpeed = VerticalSpeedFpm(aircraft, altitude, vertical);
        double next = altitude + (verticalSpeed / 60.0);
        if (vertical.PlannedAltitude is not { } planned)
        {
            return next;
        }

        if (Math.Sign(verticalSpeed) != Math.Sign(planned - altitude))
        {
            return altitude;
        }

        bool passes = ((altitude <= planned) && (next > planned)) || ((altitude >= planned) && (next < planned));
        return passes ? planned : next;
    }

    /// <summary>
    /// The vertical speed the walk flies: the present one, except on a resumed plan (<see cref="VerticalPlan.ClimbCategory"/>
    /// set) whose present vertical speed is about zero (under <see cref="LevelVerticalSpeedFpm"/>) or points away from its
    /// planned altitude: then, toward it, the larger of the type's initial climb rate
    /// (<see cref="AircraftPerformance.InitialClimbRate"/>, which a resumed upwind or crosswind climbs at) and the category's
    /// climb rate, or the slower of the category's descent rate and its pattern descent rate
    /// (<see cref="CategoryPerformance.PatternDescentRate"/>), as the resumed phase would fly.
    /// </summary>
    private static double VerticalSpeedFpm(AircraftState aircraft, double altitude, VerticalPlan vertical)
    {
        double present = aircraft.VerticalSpeed;
        if ((vertical.ClimbCategory is not { } category) || (vertical.PlannedAltitude is not { } planned) || (Math.Abs(planned - altitude) < 1.0))
        {
            return present;
        }

        int toward = Math.Sign(planned - altitude);
        if ((Math.Abs(present) >= LevelVerticalSpeedFpm) && (Math.Sign(present) == toward))
        {
            return present;
        }

        return toward > 0
            ? Math.Max(AircraftPerformance.InitialClimbRate(aircraft.AircraftType, category), CategoryPerformance.ClimbRate(category, altitude))
            : -Math.Min(CategoryPerformance.DescentRate(category), CategoryPerformance.PatternDescentRate(category));
    }

    /// <summary>The vertical speed under which a resumed plan's walk reads the aircraft as level.</summary>
    private const double LevelVerticalSpeedFpm = 100.0;

    /// <summary>
    /// The altitude a walk plans to fly and, for a resumed plan, the category whose climb and descent rates take it there
    /// from level flight (null for the trigger's walk, which flies the present vertical speed only).
    /// </summary>
    private readonly record struct VerticalPlan(double? PlannedAltitude, AircraftCategory? ClimbCategory);

    /// <summary>The Class B volumes the walk tests: in <paramref name="onlyArea"/> when given, and not already around the start.</summary>
    private static List<AirspaceVolume> CandidateVolumes(AirspaceDatabase airspace, PathPoint start, double marginNm, string? onlyArea)
    {
        var volumes = new List<AirspaceVolume>();
        IEnumerable<IReadOnlyList<AirspaceVolume>> areas = onlyArea is null ? airspace.ClassBAreas.Values : [airspace.ClassBArea(onlyArea)];
        foreach (IReadOnlyList<AirspaceVolume> area in areas)
        {
            foreach (AirspaceVolume volume in area)
            {
                if (!Holds(volume, start, marginNm))
                {
                    volumes.Add(volume);
                }
            }
        }

        return volumes;
    }

    private static BravoPathEntry? EntryAt(AirspaceVolume volume, PathPoint before, PathPoint at, double marginNm)
    {
        if (!Holds(volume, at, marginNm) || Holds(volume, before, marginNm))
        {
            return null;
        }

        bool vertical = IsNearOrInside(volume, before.Position, marginNm);
        return new BravoPathEntry(volume, at.Position, at.AlongNm, at.Seconds, at.AltitudeFt, vertical);
    }

    private static bool Holds(AirspaceVolume volume, PathPoint point, double marginNm) =>
        volume.ContainsAltitude(point.AltitudeFt) && IsNearOrInside(volume, point.Position, marginNm);

    /// <summary>Inside the footprint of <paramref name="volume"/>, or within <paramref name="marginNm"/> of one of its rings.</summary>
    private static bool IsNearOrInside(AirspaceVolume volume, LatLon position, double marginNm)
    {
        if (volume.ContainsLateral(position))
        {
            return true;
        }

        return (marginNm > 0) && volume.Rings.Any(ring => DistanceToRingNm(position, ring) <= marginNm);
    }

    private static double DistanceToRingNm(LatLon position, IReadOnlyList<LatLon> ring)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            LatLon a = ring[i];
            LatLon b = ring[(i + 1) % ring.Count];
            best = Math.Min(best, GeoMath.DistanceToSegmentFt(position, a, b) / GeoMath.FeetPerNm);
        }

        return best;
    }
}
