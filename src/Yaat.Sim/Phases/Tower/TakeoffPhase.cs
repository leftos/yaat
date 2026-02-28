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
    private int? _assignedHeading;
    private TurnDirection? _assignedTurn;

    public override string Name => "Takeoff";

    /// <summary>Heading assigned by CTO (null = fly runway heading).</summary>
    public int? AssignedHeading { get; private set; }

    /// <summary>Turn direction from CTOR/CTOL variants.</summary>
    public TurnDirection? AssignedTurn { get; private set; }

    /// <summary>
    /// Called by the dispatcher when CTO is issued during LinedUpAndWaiting.
    /// </summary>
    public void SetAssignedDeparture(int? heading, TurnDirection? turn)
    {
        AssignedHeading = heading;
        AssignedTurn = turn;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.Heading;
        _assignedHeading = AssignedHeading;
        _assignedTurn = AssignedTurn;

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

            // Apply assigned heading if given
            if (_assignedHeading is not null)
            {
                ctx.Targets.TargetHeading = _assignedHeading.Value;
                ctx.Targets.PreferredTurnDirection = _assignedTurn;
            }
        }

        return false;
    }

    private bool TickAirborneClimb(PhaseContext ctx, double agl)
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
