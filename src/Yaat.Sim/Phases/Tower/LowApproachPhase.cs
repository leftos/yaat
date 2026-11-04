using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Low approach (AIM 4-3-12.a): the aircraft flies the glidepath but does not land. It levels off at a
/// category height before the threshold (<see cref="CategoryPerformance.LowApproachLevelOffAgl"/>;
/// <see cref="FinalApproachPhase"/> clamps its glidepath at <see cref="FloorMslFt"/> while this phase is
/// pending), crosses the threshold level and holds level for <see cref="LevelHoldSeconds"/> past it, then
/// climbs straight ahead on runway heading at the initial climb rate and speed, completing at 1500 ft AGL
/// (self-clear).
/// </summary>
public sealed class LowApproachPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("LowApproachPhase");

    private const double SelfClearAgl = 1500.0;

    /// <summary>Seconds the low approach holds level past the threshold before it climbs.</summary>
    public const double LevelHoldSeconds = 10.0;

    /// <summary>Vertical rate (fpm) at which an aircraft above the floor settles down to it.</summary>
    private const double SettleToFloorFpm = -500.0;

    private double _fieldElevation;
    private TrueHeading _runwayHeading;
    private bool _climbingOut;

    // Retarget-to-diverging-runway state (#292). When _retargetToDifferentRunway is set, the low
    // approach is the lead-in to a landing on a different runway B; instead of the straight climb-out
    // to 1500 ft AGL it completes so the appended PatternEntryPhase(B) can turn onto B's final. The
    // turn must begin while the aircraft can still reach B's final on the approach side, so the phase
    // completes when the aircraft's projection onto B's final reaches the gate distance (last feasible
    // turn point) or the end of the level hold — whichever comes first.
    private bool _retargetToDifferentRunway;
    private double _retargetGateLat;
    private double _retargetGateLon;
    private TrueHeading _retargetRunwayHeadingB;
    private double _retargetGateNm;

    public override string Name => "LowApproach";

    /// <summary>
    /// Marks this low approach as the lead-in to a landing on a DIFFERENT, diverging runway
    /// (7110.65 §3-10-5 "change to runway", issue #292). Instead of the straight climb-out to
    /// 1500 ft AGL, the phase completes at the last point from which runway B's final is still
    /// reachable (its gate, <paramref name="gateNm"/> out on B's final) or at the end of the level hold,
    /// so the appended <see cref="PatternEntryPhase"/> for runway B can turn onto its final and land.
    /// Set by <see cref="Commands.PatternCommandHandler.TryClearedToLand"/>.
    /// </summary>
    public void EnableRetargetToDifferentRunway(double gateLat, double gateLon, TrueHeading runwayHeadingB, double gateNm)
    {
        _retargetToDifferentRunway = true;
        _retargetGateLat = gateLat;
        _retargetGateLon = gateLon;
        _retargetRunwayHeadingB = runwayHeadingB;
        _retargetGateNm = gateNm;
    }

    /// <summary>
    /// The altitude (feet MSL) a low approach levels off at before the threshold and holds across it. Static, because
    /// <see cref="FinalApproachPhase"/> reads it while this phase is still pending.
    /// </summary>
    public static double FloorMslFt(double runwayElevationFt, AircraftCategory category) =>
        runwayElevationFt + CategoryPerformance.LowApproachLevelOffAgl(category);

    /// <summary>
    /// The altitude (feet MSL) a restricted low approach (<c>LA 600</c>, 7110.65 3-10-10) is flown at or above, or null for
    /// an unrestricted one. "At or above" is a clearance limit: an aircraft below it climbs to it, where the category floor
    /// alone never climbs an aircraft up to it.
    /// </summary>
    public int? AtOrAboveFt { get; init; }

    /// <summary>
    /// This low approach's floor (feet MSL): the category floor (<see cref="FloorMslFt"/>), raised to
    /// <see cref="AtOrAboveFt"/> when restricted. The one floor <see cref="FinalApproachPhase"/>, this phase and the landing
    /// gate (<see cref="Commands.PatternCommandHandler.IsTooHighToLandFromLowApproach"/>) read.
    /// </summary>
    public double FloorMsl(double runwayElevationFt, AircraftCategory category) =>
        Math.Max(FloorMslFt(runwayElevationFt, category), AtOrAboveFt ?? double.MinValue);

    /// <summary>
    /// The altitude (feet MSL) to fly while this low approach holds the aircraft level, the one rule
    /// <see cref="FinalApproachPhase"/>'s glidepath clamp and this phase's level hold share. A
    /// <paramref name="pathAltitudeFt"/> (the glidepath height on final; null in the level hold) at or above the floor
    /// (<see cref="FloorMsl"/>) is followed; below it the aircraft levels at the floor, and one already below the category
    /// floor holds its own <paramref name="aircraftAltitudeFt"/> and never climbs back up to it. A restriction
    /// (<see cref="AtOrAboveFt"/>) is a clearance limit, so the result is never below it: an aircraft under it climbs to it.
    /// </summary>
    public double HoldAltitudeMsl(double runwayElevationFt, AircraftCategory category, double? pathAltitudeFt, double aircraftAltitudeFt)
    {
        double floor = FloorMsl(runwayElevationFt, category);
        double level = ((pathAltitudeFt is { } path) && (path >= floor)) ? path : Math.Min(floor, aircraftAltitudeFt);
        return Math.Max(level, AtOrAboveFt ?? double.MinValue);
    }

    public override PhaseDto ToSnapshot() =>
        new LowApproachPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? [.. Requirements.Select(r => r.ToSnapshot())] : null,
            FieldElevation = _fieldElevation,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            ClimbingOut = _climbingOut,
            AtOrAboveFt = AtOrAboveFt,
            RetargetToDifferentRunway = _retargetToDifferentRunway,
            RetargetGateLat = _retargetGateLat,
            RetargetGateLon = _retargetGateLon,
            RetargetRunwayHeadingBDeg = _retargetRunwayHeadingB.Degrees,
            RetargetGateNm = _retargetGateNm,
        };

    public static LowApproachPhase FromSnapshot(LowApproachPhaseDto dto)
    {
        var phase = new LowApproachPhase
        {
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            AtOrAboveFt = dto.AtOrAboveFt,
        };
        phase.RestoreRequirements(dto.Requirements);
        phase._fieldElevation = dto.FieldElevation;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._climbingOut = dto.ClimbingOut;
        phase._retargetToDifferentRunway = dto.RetargetToDifferentRunway;
        phase._retargetGateLat = dto.RetargetGateLat;
        phase._retargetGateLon = dto.RetargetGateLon;
        phase._retargetRunwayHeadingB = new TrueHeading(dto.RetargetRunwayHeadingBDeg);
        phase._retargetGateNm = dto.RetargetGateNm;
        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.TrueHeading;

        // Fly runway heading; OnTick holds the level-off floor.
        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        double approachSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        ctx.Targets.TargetSpeed = approachSpeed;

        // Drop any approach speed floor/ceiling (including the 5nm-final gate ceiling) so
        // the climb-out at the end of the low pass is not capped at the approach speed.
        FlightCommandHandler.EndBoundWithTheAssignment(ctx.Targets, ctx.Targets.SpeedFloor);
        FlightCommandHandler.EndBoundWithTheAssignment(ctx.Targets, ctx.Targets.SpeedCeiling);
        ctx.Targets.SpeedFloor = null;
        ctx.Targets.SpeedCeiling = null;

        Log.LogDebug(
            "[LowApproach] {Callsign}: started, levelOffAgl={Agl:F0}ft, rwyHdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            CategoryPerformance.LowApproachLevelOffAgl(ctx.Category),
            _runwayHeading.Degrees
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;

        if (!_climbingOut)
        {
            // Level at the floor: a high aircraft settles down to it without diving; one at or below it
            // holds its own altitude and never climbs up to the category floor during the hold. A restriction
            // (AtOrAboveFt) is a clearance limit, so an aircraft below it climbs to it at its normal rate.
            double floor = FloorMsl(_fieldElevation, ctx.Category);
            if (ctx.Aircraft.Altitude > floor + FlightPhysics.AltitudeSnapFt)
            {
                ctx.Targets.TargetAltitude = floor;
                ctx.Targets.DesiredVerticalRate = SettleToFloorFpm;
            }
            else
            {
                ctx.Targets.TargetAltitude = HoldAltitudeMsl(_fieldElevation, ctx.Category, pathAltitudeFt: null, ctx.Aircraft.Altitude);
                ctx.Targets.DesiredVerticalRate = null;
            }

            // Retarget to a diverging runway (#292): fly the low pass level at the floor, but hand off
            // to the runway-change turn once the aircraft reaches the last point from which runway B's
            // final is still reachable on the approach side (its gate). Turning any later would put B's
            // final behind the aircraft. The end of the level hold is the backstop for well-separated
            // pairs where the gate lies further on: the pass ends where a plain low approach would climb.
            if (_retargetToDifferentRunway)
            {
                // The aircraft is landing on B, not going around — configure and slow to final approach
                // speed well ahead of the turn, which also tightens the turn radius for the short-final
                // intercept onto B. No speed ceiling: the appended PatternEntryPhase for B is a Final
                // entry and commands approach speed itself, and a ceiling set here outlives the phase.
                ctx.Targets.TargetSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);

                double alongPastGate = GeoMath.AlongTrackDistanceNm(
                    ctx.Aircraft.Position,
                    new LatLon(_retargetGateLat, _retargetGateLon),
                    _retargetRunwayHeadingB.ToReciprocal()
                );
                if ((alongPastGate <= 0) || (ElapsedSeconds >= LevelHoldSeconds))
                {
                    Log.LogDebug(
                        "[LowApproach] {Callsign}: handing off to runway-change turn at {Agl:F0}ft AGL (pastGate={Past:F2}nm)",
                        ctx.Aircraft.Callsign,
                        agl,
                        alongPastGate
                    );
                    return true;
                }

                return false;
            }

            if (ElapsedSeconds >= LevelHoldSeconds)
            {
                _climbingOut = true;
                Log.LogDebug("[LowApproach] {Callsign}: climbing out at {Agl:F0}ft AGL", ctx.Aircraft.Callsign, agl);

                double climbRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
                double climbSpeed = AircraftPerformance.InitialClimbSpeed(ctx.AircraftType, ctx.Category);
                double targetAlt = _fieldElevation + SelfClearAgl;

                ctx.Targets.TargetAltitude = targetAlt;
                ctx.Targets.DesiredVerticalRate = climbRate;
                ctx.Targets.TargetSpeed = climbSpeed;
                ctx.Targets.TargetTrueHeading = _runwayHeading;
            }

            return false;
        }

        // Retarget issued after the aircraft already began its straight climb-out (a late
        // CLAND <divergingRunway>): the low pass is done, so complete now and let the appended
        // PatternEntryPhase(B) take over the turn to the new runway.
        if (_retargetToDifferentRunway)
        {
            Log.LogDebug("[LowApproach] {Callsign}: retarget after climb-out began, handing off to runway-change turn", ctx.Aircraft.Callsign);
            return true;
        }

        // The next circuit is built when this phase completes, so the cap is the circuit just flown.
        PatternCompression.HoldClimbCap(ctx, PatternCompression.FlownCircuit(ctx.Aircraft.Phases));
        bool complete = agl >= SelfClearAgl;
        if (complete)
        {
            Log.LogDebug("[LowApproach] {Callsign}: complete at {Agl:F0}ft AGL", ctx.Aircraft.Callsign, agl);
        }

        return complete;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd) => CommandAcceptance.ClearsPhase;

    protected override List<ClearanceRequirement> CreateRequirements() => [];
}
