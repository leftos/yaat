using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Self-completing turn phase: aircraft turns a specified number of degrees
/// in the given direction, then resumes the previous heading.
/// Used for L360/R360/L270/R270 commands.
/// </summary>
public sealed class MakeTurnPhase : Phase
{
    private const double CompletionToleranceDeg = 5.0;
    private const double ExitAlignmentDeg = 10.0;

    private double _startHeading;
    private double _cumulativeTurn;
    private double _lastHeading;
    private bool _exiting;

    public required TurnDirection Direction { get; init; }
    public required double TargetDegrees { get; init; }

    public override string Name => $"Turn{(Direction == TurnDirection.Left ? "L" : "R")}{TargetDegrees:F0}";

    public override void OnStart(PhaseContext ctx)
    {
        _startHeading = ctx.Aircraft.Heading;
        _lastHeading = ctx.Aircraft.Heading;

        // Set initial turn target 1° past start in the turn direction
        double offset = Direction == TurnDirection.Left ? -1 : 1;
        double targetHdg = ((_startHeading + offset) % 360 + 360) % 360;

        ctx.Targets.TargetHeading = targetHdg;
        ctx.Targets.PreferredTurnDirection = Direction;
        ctx.Targets.NavigationRoute.Clear();
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double currentHeading = ctx.Aircraft.Heading;
        double delta = currentHeading - _lastHeading;
        if (delta > 180)
        {
            delta -= 360;
        }
        if (delta < -180)
        {
            delta += 360;
        }
        _cumulativeTurn += Math.Abs(delta);
        _lastHeading = currentHeading;

        if (!_exiting && _cumulativeTurn >= TargetDegrees - ExitAlignmentDeg)
        {
            _exiting = true;
            double exitHeading = ComputeExitHeading();
            ctx.Targets.TargetHeading = exitHeading;
            ctx.Targets.PreferredTurnDirection = Direction;
        }

        return _cumulativeTurn >= TargetDegrees - CompletionToleranceDeg;
    }

    private double ComputeExitHeading()
    {
        if (Math.Abs(TargetDegrees - 360) < 1)
        {
            return _startHeading;
        }

        // 270° turn: exit heading is 90° opposite to turn direction
        double offset = Direction == TurnDirection.Left ? 90 : -90;
        return ((_startHeading + offset) % 360 + 360) % 360;
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
