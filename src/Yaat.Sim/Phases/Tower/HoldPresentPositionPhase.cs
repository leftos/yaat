using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Hold present position via continuous 360° turns (HPPL/HPPR)
/// or hover for helicopters (HPP).
/// Maintains current altitude and speed. Standard turn rate per category.
/// Never self-completes — waits for RPO to issue a new command.
/// </summary>
public sealed class HoldPresentPositionPhase : Phase
{
    private double _orbitHeading;
    private double _cumulativeTurn;
    private double _lastHeading;

    /// <summary>Turn direction for winged aircraft. Null = helicopter hover.</summary>
    public TurnDirection? OrbitDirection { get; init; }

    public override string Name => OrbitDirection is not null ? "HPP" : "HPP";

    public override void OnStart(PhaseContext ctx)
    {
        _orbitHeading = ctx.Aircraft.Heading;
        _lastHeading = ctx.Aircraft.Heading;
        ctx.Targets.NavigationRoute.Clear();

        if (OrbitDirection is null)
        {
            // Helicopter hover: hold position, zero speed
            ctx.Targets.TargetSpeed = 0;
            ctx.Targets.TargetHeading = ctx.Aircraft.Heading;
            ctx.Targets.PreferredTurnDirection = null;
        }
        else
        {
            // Winged: start turning, maintain speed and altitude
            SetOrbitTarget(ctx);
        }
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (OrbitDirection is null)
        {
            return false;
        }

        double currentHeading = ctx.Aircraft.Heading;
        double delta = currentHeading - _lastHeading;
        if (delta > 180) { delta -= 360; }
        if (delta < -180) { delta += 360; }
        _cumulativeTurn += Math.Abs(delta);
        _lastHeading = currentHeading;

        if (_cumulativeTurn >= 350)
        {
            _cumulativeTurn -= 360;
            SetOrbitTarget(ctx);
        }

        return false;
    }

    private void SetOrbitTarget(PhaseContext ctx)
    {
        // Target 1 degree past the orbit reference heading to keep the turn going
        double offset = OrbitDirection == TurnDirection.Left ? -1 : 1;
        double targetHdg = ((_orbitHeading + offset) % 360 + 360) % 360;

        ctx.Targets.TargetHeading = targetHdg;
        ctx.Targets.PreferredTurnDirection = OrbitDirection;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Any command clears the hold
        return CommandAcceptance.ClearsPhase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
