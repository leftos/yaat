using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Stop-and-go: full stop on runway, brief pause, then takeoff from zero.
/// Pause duration is category-dependent (Jet 5s, Turboprop 4s, Piston 3s).
/// After pause, same takeoff profile as normal (GroundAccelRate to Vr, liftoff).
/// Completes at 400ft AGL.
/// </summary>
public sealed class StopAndGoPhase : Phase
{
    private const double LiftoffAgl = 400.0;

    private double _fieldElevation;
    private double _runwayHeading;
    private double _pauseDuration;
    private double _pauseElapsed;
    private bool _stopped;
    private bool _reaccelerating;
    private bool _airborne;

    public override string Name => "StopAndGo";

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.Heading;
        _pauseDuration = CategoryPerformance.StopAndGoPauseSeconds(ctx.Category);

        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.TargetAltitude = _fieldElevation;
        ctx.Targets.DesiredVerticalRate = null;

        // Decelerate to zero
        ctx.Targets.TargetSpeed = 0;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_stopped)
        {
            if (ctx.Aircraft.GroundSpeed < 3)
            {
                _stopped = true;
                ctx.Aircraft.GroundSpeed = 0;
                ctx.Targets.TargetSpeed = 0;
            }
            return false;
        }

        if (!_reaccelerating)
        {
            _pauseElapsed += ctx.DeltaSeconds;
            if (_pauseElapsed >= _pauseDuration)
            {
                _reaccelerating = true;
            }
            return false;
        }

        if (!_airborne)
        {
            double vr = CategoryPerformance.RotationSpeed(ctx.Category);
            double accelRate = CategoryPerformance.GroundAccelRate(ctx.Category);

            double targetSpeed = ctx.Aircraft.GroundSpeed + accelRate * ctx.DeltaSeconds;
            if (targetSpeed >= vr)
            {
                targetSpeed = vr;
            }
            ctx.Aircraft.GroundSpeed = targetSpeed;
            ctx.Targets.TargetSpeed = null;

            if (ctx.Aircraft.GroundSpeed >= vr)
            {
                _airborne = true;
                ctx.Aircraft.IsOnGround = false;

                double climbRate = CategoryPerformance.InitialClimbRate(ctx.Category);
                double climbSpeed = CategoryPerformance.InitialClimbSpeed(ctx.Category);
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
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
