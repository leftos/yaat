using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Ground roll (accelerate to Vr) then liftoff and climb.
/// Completes at 400ft AGL.
/// </summary>
public sealed class TakeoffPhase : Phase
{
    private const double CompletionAgl = 400.0;

    private bool _airborne;
    private double _fieldElevation;
    private double _runwayHeading;
    private DepartureInstruction? _departure;

    public override string Name => "Takeoff";

    /// <summary>Departure instruction from CTO command.</summary>
    public DepartureInstruction? Departure { get; private set; }

    /// <summary>
    /// Called by the dispatcher when CTO is issued.
    /// </summary>
    public void SetAssignedDeparture(DepartureInstruction? departure)
    {
        Departure = departure;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.Heading;
        _departure = Departure;

        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;

        if (!_airborne)
        {
            return TickGroundRoll(ctx);
        }

        return TickAirborneClimb(ctx, agl);
    }

    private bool TickGroundRoll(PhaseContext ctx)
    {
        double vr = CategoryPerformance.RotationSpeed(ctx.Category);
        double accelRate = CategoryPerformance.GroundAccelRate(ctx.Category);

        // Accelerate toward Vr using ground acceleration rate
        double targetSpeed = ctx.Aircraft.GroundSpeed + accelRate * ctx.DeltaSeconds;
        if (targetSpeed >= vr)
        {
            targetSpeed = vr;
        }
        ctx.Aircraft.GroundSpeed = targetSpeed;
        ctx.Targets.TargetSpeed = null;

        // Liftoff at Vr
        if (ctx.Aircraft.GroundSpeed >= vr)
        {
            _airborne = true;
            ctx.Aircraft.IsOnGround = false;

            // Set climb targets
            double climbRate = CategoryPerformance.InitialClimbRate(ctx.Category);
            double climbSpeed = CategoryPerformance.InitialClimbSpeed(ctx.Category);
            ctx.Targets.TargetAltitude = _fieldElevation + CompletionAgl;
            ctx.Targets.DesiredVerticalRate = climbRate;
            ctx.Targets.TargetSpeed = climbSpeed;

            ApplyDepartureHeading(ctx);
        }

        return false;
    }

    private void ApplyDepartureHeading(PhaseContext ctx)
    {
        switch (_departure)
        {
            case RelativeTurnDeparture rel:
                int relHdg =
                    rel.Direction == TurnDirection.Right
                        ? FlightPhysics.NormalizeHeadingInt(_runwayHeading + rel.Degrees)
                        : FlightPhysics.NormalizeHeadingInt(_runwayHeading - rel.Degrees);
                ctx.Targets.TargetHeading = relHdg;
                ctx.Targets.PreferredTurnDirection = rel.Direction;
                break;

            case FlyHeadingDeparture fh:
                ctx.Targets.TargetHeading = fh.Heading;
                ctx.Targets.PreferredTurnDirection = fh.Direction;
                break;

            // DefaultDeparture, RunwayHeadingDeparture, OnCourseDeparture,
            // DirectFixDeparture, ClosedTrafficDeparture: keep runway heading.
            // Navigation is set up by InitialClimbPhase.
        }
    }

    private static bool TickAirborneClimb(PhaseContext ctx, double agl)
    {
        return agl >= CompletionAgl;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        if (!_airborne)
        {
            // During ground roll, reject most commands
            return cmd switch
            {
                CanonicalCommandType.CancelTakeoffClearance => CommandAcceptance.Allowed,
                CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
                _ => CommandAcceptance.Rejected,
            };
        }

        // Once airborne, most commands clear the phase
        return cmd switch
        {
            CanonicalCommandType.GoAround => CommandAcceptance.Rejected,
            _ => CommandAcceptance.ClearsPhase,
        };
    }
}
