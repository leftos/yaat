using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Low approach: aircraft flies normal glideslope but does not land.
/// At category-specific AGL (Jet 100ft, Turboprop 75ft, Piston 50ft),
/// initiates go-around climb. Uses same climb profile as GoAroundPhase.
/// Completes at 1500ft AGL (self-clear).
/// </summary>
public sealed class LowApproachPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("LowApproachPhase");

    private const double SelfClearAgl = 1500.0;

    private double _fieldElevation;
    private TrueHeading _runwayHeading;
    private double _goAroundAgl;
    private double _thresholdLat;
    private double _thresholdLon;
    private bool _climbingOut;

    // Retarget-to-diverging-runway state (#292). When _retargetToDifferentRunway is set, the low
    // approach is the lead-in to a landing on a different runway B; instead of the straight climb-out
    // to 1500 ft AGL it completes so the appended PatternEntryPhase(B) can turn onto B's final. The
    // turn must begin while the aircraft can still reach B's final on the approach side, so the phase
    // completes when the aircraft's projection onto B's final reaches the gate distance (last feasible
    // turn point) or the low-pass floor — whichever comes first.
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
    /// reachable (its gate, <paramref name="gateNm"/> out on B's final) or at the low-pass floor,
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

    public override PhaseDto ToSnapshot() =>
        new LowApproachPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            FieldElevation = _fieldElevation,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            GoAroundAgl = _goAroundAgl,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            ClimbingOut = _climbingOut,
            RetargetToDifferentRunway = _retargetToDifferentRunway,
            RetargetGateLat = _retargetGateLat,
            RetargetGateLon = _retargetGateLon,
            RetargetRunwayHeadingBDeg = _retargetRunwayHeadingB.Degrees,
            RetargetGateNm = _retargetGateNm,
        };

    public static LowApproachPhase FromSnapshot(LowApproachPhaseDto dto)
    {
        var phase = new LowApproachPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._fieldElevation = dto.FieldElevation;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._goAroundAgl = dto.GoAroundAgl;
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
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
        _goAroundAgl = CategoryPerformance.LowApproachAltitudeAgl(ctx.Category);

        if (ctx.Runway is not null)
        {
            _thresholdLat = ctx.Runway.ThresholdLatitude;
            _thresholdLon = ctx.Runway.ThresholdLongitude;
        }

        // Continue on glideslope toward threshold
        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        double approachSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        ctx.Targets.TargetSpeed = approachSpeed;

        // Drop any approach speed floor/ceiling (including the 5nm-final gate ceiling) so
        // the climb-out at the end of the low pass is not capped at the approach speed.
        ctx.Targets.SpeedFloor = null;
        ctx.Targets.SpeedCeiling = null;

        Log.LogDebug(
            "[LowApproach] {Callsign}: started, goAroundAgl={Agl:F0}ft, rwyHdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            _goAroundAgl,
            _runwayHeading.Degrees
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;

        if (!_climbingOut)
        {
            // Descend toward go-around altitude using proportional rate
            double distNm = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon));

            double goAroundAlt = _fieldElevation + _goAroundAgl;
            ctx.Targets.TargetAltitude = goAroundAlt;

            double timeToThresholdSec = ctx.Aircraft.GroundSpeed > 1 ? distNm / (ctx.Aircraft.GroundSpeed / 3600.0) : 1.0;
            double altToLose = ctx.Aircraft.Altitude - goAroundAlt;
            if (altToLose > 0 && timeToThresholdSec > 1)
            {
                double requiredFpm = (altToLose / timeToThresholdSec) * 60.0;
                double maxFpm = distNm > 2.0 ? 2500 : 1500;
                double clampedFpm = Math.Clamp(requiredFpm, 200, maxFpm);
                ctx.Targets.DesiredVerticalRate = -clampedFpm;
            }

            // Retarget to a diverging runway (#292): fly the low pass as low as the geometry allows,
            // but hand off to the runway-change turn once the aircraft reaches the last point from
            // which runway B's final is still reachable on the approach side (its gate). Turning any
            // later would put B's final behind the aircraft. The low-pass floor is the backstop for
            // well-separated pairs where the aircraft can descend all the way before that point.
            if (_retargetToDifferentRunway)
            {
                // The aircraft is landing on B, not going around — configure and slow to final
                // approach speed well ahead of the turn (cap it so the low pass can't accelerate),
                // which also tightens the turn radius for the short-final intercept onto B.
                double retargetApproachSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
                ctx.Targets.TargetSpeed = retargetApproachSpeed;
                ctx.Targets.SpeedCeiling = retargetApproachSpeed;

                double alongPastGate = GeoMath.AlongTrackDistanceNm(
                    ctx.Aircraft.Position,
                    new LatLon(_retargetGateLat, _retargetGateLon),
                    _retargetRunwayHeadingB.ToReciprocal()
                );
                if ((alongPastGate <= 0) || (agl <= _goAroundAgl))
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

            if (agl <= _goAroundAgl)
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

        bool complete = agl >= SelfClearAgl;
        if (complete)
        {
            Log.LogDebug("[LowApproach] {Callsign}: complete at {Agl:F0}ft AGL", ctx.Aircraft.Callsign, agl);
        }

        return complete;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return CommandAcceptance.ClearsPhase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
