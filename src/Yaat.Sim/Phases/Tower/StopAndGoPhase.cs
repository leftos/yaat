using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Stop-and-go: full stop on runway, brief pause, then takeoff from zero.
/// Pause duration is category-dependent (Jet 10s, Turboprop 7s, Piston 5s).
/// After pause, same takeoff profile as normal (GroundAccelRate to Vr, liftoff).
/// Completes at 400ft AGL.
/// </summary>
public sealed class StopAndGoPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("StopAndGoPhase");

    private const double LiftoffAgl = 400.0;
    private const double CenterlineGainDegPerNm = 150.0;
    private const double MaxCenterlineCorrectionDeg = 10.0;

    private double _fieldElevation;
    private TrueHeading _runwayHeading;
    private double _thresholdLat;
    private double _thresholdLon;
    private double _pauseDuration;
    private double _pauseElapsed;
    private bool _stopped;
    private bool _reaccelerating;
    private bool _airborne;
    private bool _goTriggered;

    public override string Name => "StopAndGo";

    public override PhaseDto ToSnapshot() =>
        new StopAndGoPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            FieldElevation = _fieldElevation,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            PauseDuration = _pauseDuration,
            PauseElapsed = _pauseElapsed,
            Stopped = _stopped,
            Reaccelerating = _reaccelerating,
            Airborne = _airborne,
            GoTriggered = _goTriggered,
        };

    public static StopAndGoPhase FromSnapshot(StopAndGoPhaseDto dto)
    {
        var phase = new StopAndGoPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._fieldElevation = dto.FieldElevation;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._pauseDuration = dto.PauseDuration;
        phase._pauseElapsed = dto.PauseElapsed;
        phase._stopped = dto.Stopped;
        phase._reaccelerating = dto.Reaccelerating;
        phase._airborne = dto.Airborne;
        phase._goTriggered = dto.GoTriggered;
        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.TrueHeading;
        _thresholdLat = ctx.Runway?.ThresholdLatitude ?? ctx.Aircraft.Position.Lat;
        _thresholdLon = ctx.Runway?.ThresholdLongitude ?? ctx.Aircraft.Position.Lon;
        _pauseDuration = CategoryPerformance.StopAndGoPauseSeconds(ctx.Category);

        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.TargetAltitude = _fieldElevation;
        ctx.Targets.DesiredVerticalRate = null;

        // Decelerate to zero
        ctx.Targets.TargetSpeed = 0;

        // Drop any approach speed floor/ceiling (including the 5nm-final gate ceiling) so
        // the climb-out after the stop is not capped at the approach speed.
        ctx.Targets.SpeedFloor = null;
        ctx.Targets.SpeedCeiling = null;

        Log.LogDebug(
            "[StopAndGo] {Callsign}: started, pause={Pause:F1}s, rwyHdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            _pauseDuration,
            _runwayHeading.Degrees
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Steer toward runway centerline while on the ground
        if (!_airborne)
        {
            double signedXte = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon), _runwayHeading);
            double correction = Math.Clamp(signedXte * CenterlineGainDegPerNm, -MaxCenterlineCorrectionDeg, MaxCenterlineCorrectionDeg);
            ctx.Targets.TargetTrueHeading = new TrueHeading(_runwayHeading.Degrees - correction);
        }

        if (!_stopped)
        {
            if (ctx.Aircraft.IndicatedAirspeed < 3)
            {
                _stopped = true;
                ctx.Aircraft.IndicatedAirspeed = 0;
                ctx.Targets.TargetSpeed = 0;
                Log.LogDebug("[StopAndGo] {Callsign}: full stop, pausing {Pause:F1}s", ctx.Aircraft.Callsign, _pauseDuration);
            }
            return false;
        }

        if (!_reaccelerating)
        {
            _pauseElapsed += ctx.DeltaSeconds;
            if (_pauseElapsed >= _pauseDuration || _goTriggered)
            {
                _reaccelerating = true;
                Log.LogDebug("[StopAndGo] {Callsign}: pause complete, reaccelerating", ctx.Aircraft.Callsign);
            }
            return false;
        }

        if (!_airborne)
        {
            double vr = AircraftPerformance.RotationSpeed(ctx.AircraftType, ctx.Category);
            double accelRate = AircraftPerformance.GroundAccelRate(ctx.AircraftType, ctx.Category);

            double targetSpeed = ctx.Aircraft.IndicatedAirspeed + accelRate * ctx.DeltaSeconds;
            if (targetSpeed >= vr)
            {
                targetSpeed = vr;
            }
            ctx.Aircraft.IndicatedAirspeed = targetSpeed;
            ctx.Targets.TargetSpeed = null;

            if (ctx.Aircraft.IndicatedAirspeed >= vr)
            {
                _airborne = true;
                ctx.Aircraft.IsOnGround = false;
                Log.LogDebug("[StopAndGo] {Callsign}: airborne at Vr={Vr:F0}kts", ctx.Aircraft.Callsign, vr);

                double climbRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
                double climbSpeed = AircraftPerformance.InitialClimbSpeed(ctx.AircraftType, ctx.Category);
                double targetAlt = _fieldElevation + LiftoffAgl;

                ctx.Targets.TargetAltitude = targetAlt;
                ctx.Targets.DesiredVerticalRate = climbRate;
                ctx.Targets.TargetSpeed = climbSpeed;
            }
            return false;
        }

        double agl = ctx.Aircraft.Altitude - _fieldElevation;
        return agl >= LiftoffAgl;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Go => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is in a stop-and-go on the runway; only GA, GO (once stopped), or DEL apply"),
        };
    }

    internal void TriggerGo()
    {
        _goTriggered = true;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
