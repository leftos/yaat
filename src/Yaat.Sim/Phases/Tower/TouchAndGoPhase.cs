using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Touch-and-go: brief rollout after touchdown, then reaccelerate and take off.
/// Rollout duration is category-dependent (Jet 4s, Turboprop 4s, Piston 3s).
/// After rollout, reaccelerates using GroundAccelRate to Vr, then lifts off.
/// Completes at 400ft AGL (same as TakeoffPhase).
/// </summary>
public sealed class TouchAndGoPhase : Phase
{
    private const double LiftoffAgl = 400.0;
    private const double CenterlineGainDegPerNm = 150.0;
    private const double MaxCenterlineCorrectionDeg = 10.0;

    private double _fieldElevation;
    private TrueHeading _runwayHeading;
    private double _thresholdLat;
    private double _thresholdLon;
    private double _rolloutDuration;
    private double _rolloutElapsed;
    private bool _reaccelerating;
    private bool _airborne;

    public override string Name => "TouchAndGo";

    public override PhaseDto ToSnapshot() =>
        new TouchAndGoPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            FieldElevation = _fieldElevation,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            RolloutDuration = _rolloutDuration,
            RolloutElapsed = _rolloutElapsed,
            Reaccelerating = _reaccelerating,
            Airborne = _airborne,
        };

    public static TouchAndGoPhase FromSnapshot(TouchAndGoPhaseDto dto)
    {
        var phase = new TouchAndGoPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._fieldElevation = dto.FieldElevation;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._rolloutDuration = dto.RolloutDuration;
        phase._rolloutElapsed = dto.RolloutElapsed;
        phase._reaccelerating = dto.Reaccelerating;
        phase._airborne = dto.Airborne;
        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.TrueHeading;
        _thresholdLat = ctx.Runway?.ThresholdLatitude ?? ctx.Aircraft.Latitude;
        _thresholdLon = ctx.Runway?.ThresholdLongitude ?? ctx.Aircraft.Longitude;
        _rolloutDuration = CategoryPerformance.TouchAndGoRolloutSeconds(ctx.Category);

        // Start decelerating on the runway
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.TargetAltitude = _fieldElevation;
        ctx.Targets.DesiredVerticalRate = null;

        // Decelerate briefly
        double minSpeed =
            AircraftPerformance.TouchdownSpeed(ctx.AircraftType, ctx.Category)
            - CategoryPerformance.RolloutDecelRate(ctx.Category) * _rolloutDuration;
        ctx.Targets.TargetSpeed = Math.Max(minSpeed, 40);

        ctx.Logger.LogDebug(
            "[TouchAndGo] {Callsign}: started, rollout={Roll:F1}s, rwyHdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            _rolloutDuration,
            _runwayHeading.Degrees
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Steer toward runway centerline while on the ground
        if (!_airborne)
        {
            double signedXte = GeoMath.SignedCrossTrackDistanceNm(
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _thresholdLat,
                _thresholdLon,
                _runwayHeading
            );
            double correction = Math.Clamp(signedXte * CenterlineGainDegPerNm, -MaxCenterlineCorrectionDeg, MaxCenterlineCorrectionDeg);
            ctx.Targets.TargetTrueHeading = new TrueHeading(_runwayHeading.Degrees - correction);
        }

        _rolloutElapsed += ctx.DeltaSeconds;

        if (!_reaccelerating && _rolloutElapsed >= _rolloutDuration)
        {
            _reaccelerating = true;
            ctx.Logger.LogDebug("[TouchAndGo] {Callsign}: rollout complete, reaccelerating", ctx.Aircraft.Callsign);
        }

        if (_reaccelerating && !_airborne)
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
                ctx.Logger.LogDebug("[TouchAndGo] {Callsign}: airborne at Vr={Vr:F0}kts", ctx.Aircraft.Callsign, vr);

                double climbRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
                double climbSpeed = AircraftPerformance.InitialClimbSpeed(ctx.AircraftType, ctx.Category);
                double targetAlt = _fieldElevation + LiftoffAgl;

                ctx.Targets.TargetAltitude = targetAlt;
                ctx.Targets.DesiredVerticalRate = climbRate;
                ctx.Targets.TargetSpeed = climbSpeed;
            }
        }

        if (_airborne)
        {
            double agl = ctx.Aircraft.Altitude - _fieldElevation;
            return agl >= LiftoffAgl;
        }

        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
