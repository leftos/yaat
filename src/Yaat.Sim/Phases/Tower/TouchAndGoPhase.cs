using Yaat.Sim.Commands;

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

    private double _fieldElevation;
    private double _runwayHeading;
    private double _rolloutDuration;
    private double _rolloutElapsed;
    private bool _reaccelerating;
    private bool _airborne;

    public override string Name => "TouchAndGo";

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.Heading;
        _rolloutDuration = CategoryPerformance.TouchAndGoRolloutSeconds(ctx.Category);

        // Start decelerating on the runway
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.TargetAltitude = _fieldElevation;
        ctx.Targets.DesiredVerticalRate = null;

        // Decelerate briefly
        double minSpeed = CategoryPerformance.TouchdownSpeed(ctx.Category)
            - CategoryPerformance.RolloutDecelRate(ctx.Category) * _rolloutDuration;
        ctx.Targets.TargetSpeed = Math.Max(minSpeed, 40);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        _rolloutElapsed += ctx.DeltaSeconds;

        if (!_reaccelerating && _rolloutElapsed >= _rolloutDuration)
        {
            _reaccelerating = true;
        }

        if (_reaccelerating && !_airborne)
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
