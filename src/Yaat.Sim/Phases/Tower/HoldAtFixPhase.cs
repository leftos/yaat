using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Hold at fix: navigate to a fix, then orbit via 360° turns (HFIXL/HFIXR)
/// or hover for helicopters (HFIX).
/// Decelerates to AIM max holding speed on arrival. Standard turn rate.
/// Never self-completes — waits for RPO to issue a new command.
/// </summary>
public sealed class HoldAtFixPhase : Phase
{
    private const double ArrivalNm = 0.5;

    private bool _atFix;
    private double _orbitHeading;
    private double _cumulativeTurn;
    private double _lastHeading;

    /// <summary>Fix name (for display).</summary>
    public required string FixName { get; init; }

    /// <summary>Fix latitude.</summary>
    public required double FixLat { get; init; }

    /// <summary>Fix longitude.</summary>
    public required double FixLon { get; init; }

    /// <summary>Turn direction for winged aircraft. Null = helicopter hover.</summary>
    public TurnDirection? OrbitDirection { get; init; }

    public override string Name => _atFix ? "HoldingAtFix" : "ProceedToFix";

    public override void OnStart(PhaseContext ctx)
    {
        _lastHeading = ctx.Aircraft.Heading;

        // Navigate to the fix
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.NavigationRoute.Add(new NavigationTarget
        {
            Name = FixName,
            Latitude = FixLat,
            Longitude = FixLon,
        });
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_atFix)
        {
            double dist = FlightPhysics.DistanceNm(
                ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
                FixLat, FixLon);

            if (dist < ArrivalNm)
            {
                _atFix = true;
                ctx.Targets.NavigationRoute.Clear();

                // Decelerate to holding speed
                double maxHold = CategoryPerformance.MaxHoldingSpeed(ctx.Aircraft.Altitude);
                if (ctx.Aircraft.GroundSpeed > maxHold)
                {
                    ctx.Targets.TargetSpeed = maxHold;
                }

                _orbitHeading = ctx.Aircraft.Heading;

                if (OrbitDirection is null)
                {
                    // Helicopter hover
                    ctx.Targets.TargetSpeed = 0;
                    ctx.Targets.TargetHeading = ctx.Aircraft.Heading;
                    ctx.Targets.PreferredTurnDirection = null;
                }
                else
                {
                    SetOrbitTarget(ctx);
                }
            }

            return false;
        }

        if (OrbitDirection is null)
        {
            return false;
        }

        // Track cumulative turn
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
        double offset = OrbitDirection == TurnDirection.Left ? -1 : 1;
        double targetHdg = ((_orbitHeading + offset) % 360 + 360) % 360;

        ctx.Targets.TargetHeading = targetHdg;
        ctx.Targets.PreferredTurnDirection = OrbitDirection;
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
