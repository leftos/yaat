using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Helicopter vertical liftoff: climb from ground to 400ft AGL
/// at initial climb rate with no ground roll. Accelerates to
/// InitialClimbSpeed once airborne. Transitions to InitialClimbPhase.
/// </summary>
public sealed class HelicopterTakeoffPhase : Phase
{
    private const double CompletionAgl = 400.0;

    private double _fieldElevation;
    private double _runwayHeading;
    private DepartureInstruction? _departure;

    public override string Name => "Takeoff-H";

    /// <summary>Departure instruction from CTO command.</summary>
    public DepartureInstruction? Departure { get; private set; }

    public void SetAssignedDeparture(DepartureInstruction? departure)
    {
        Departure = departure;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.Heading;
        _departure = Departure;

        // Immediate liftoff — no ground roll
        ctx.Aircraft.IsOnGround = false;

        double climbRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        double climbSpeed = AircraftPerformance.InitialClimbSpeed(ctx.AircraftType, ctx.Category);

        ctx.Targets.TargetAltitude = _fieldElevation + CompletionAgl;
        ctx.Targets.DesiredVerticalRate = climbRate;
        ctx.Targets.TargetSpeed = climbSpeed;
        ctx.Targets.TargetHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;

        ApplyDepartureHeading(ctx);

        ctx.Logger.LogDebug("[Takeoff-H] {Callsign}: vertical liftoff, targetAlt={Alt:F0}ft", ctx.Aircraft.Callsign, _fieldElevation + CompletionAgl);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;
        bool complete = agl >= CompletionAgl;
        if (complete)
        {
            ctx.Logger.LogDebug("[Takeoff-H] {Callsign}: complete at {Agl:F0}ft AGL", ctx.Aircraft.Callsign, agl);
        }

        return complete;
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
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Once airborne (immediately), most commands clear the phase
        return cmd switch
        {
            CanonicalCommandType.GoAround => CommandAcceptance.Rejected,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }
}
