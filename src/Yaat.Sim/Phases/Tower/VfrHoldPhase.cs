using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// VFR hold: orbit via continuous 360° turns at current position (HPP) or at a fix (HFIX).
/// Helicopters hover (zero speed, no turn). Standard turn rate per category.
/// Never self-completes — waits for RPO to issue a new command.
/// </summary>
public sealed class VfrHoldPhase : Phase
{
    private const double ArrivalNm = 0.5;

    private bool _atFix;
    private double _cumulativeTurn;
    private double _lastHeading;

    /// <summary>Fix name (for display). Null = hold present position.</summary>
    public string? FixName { get; init; }

    /// <summary>Fix latitude. Required when FixName is set.</summary>
    public double? FixLat { get; init; }

    /// <summary>Fix longitude. Required when FixName is set.</summary>
    public double? FixLon { get; init; }

    /// <summary>Turn direction for winged aircraft. Null = helicopter hover.</summary>
    public TurnDirection? OrbitDirection { get; init; }

    public override string Name
    {
        get
        {
            if (FixName is not null)
            {
                return _atFix ? "HoldingAtFix" : "ProceedToFix";
            }

            return OrbitDirection switch
            {
                TurnDirection.Left => "HPP-L",
                TurnDirection.Right => "HPP-R",
                _ => "HPP",
            };
        }
    }

    public override void OnStart(PhaseContext ctx)
    {
        _lastHeading = ctx.Aircraft.Heading;

        if (FixName is not null)
        {
            // Navigate to the fix first
            ctx.Targets.NavigationRoute.Clear();
            ctx.Targets.NavigationRoute.Add(
                new NavigationTarget
                {
                    Name = FixName,
                    Latitude = FixLat!.Value,
                    Longitude = FixLon!.Value,
                }
            );

            ctx.Logger.LogDebug(
                "[VfrHold] {Callsign}: started, fix={Fix}, orbit={Dir}",
                ctx.Aircraft.Callsign,
                FixName,
                OrbitDirection?.ToString() ?? "hover"
            );
        }
        else
        {
            // Hold present position — start orbiting immediately
            ctx.Targets.NavigationRoute.Clear();

            if (OrbitDirection is null)
            {
                ctx.Targets.TargetSpeed = 0;
                ctx.Targets.TargetHeading = ctx.Aircraft.Heading;
                ctx.Targets.PreferredTurnDirection = null;
            }
            else
            {
                SetOrbitTarget(ctx);
            }

            ctx.Logger.LogDebug(
                "[VfrHold] {Callsign}: started, orbit={Dir}, alt={Alt:F0}ft",
                ctx.Aircraft.Callsign,
                OrbitDirection?.ToString() ?? "hover",
                ctx.Aircraft.Altitude
            );
        }
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Navigate-to-fix phase: wait until arrival
        if (FixName is not null && !_atFix)
        {
            double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, FixLat!.Value, FixLon!.Value);

            if (dist < ArrivalNm)
            {
                _atFix = true;
                ctx.Targets.NavigationRoute.Clear();
                ctx.Logger.LogDebug("[VfrHold] {Callsign}: arrived at {Fix}, holding", ctx.Aircraft.Callsign, FixName);

                double maxHold = AircraftPerformance.HoldingSpeed(ctx.AircraftType, ctx.Aircraft.Altitude);
                if (ctx.Targets.TargetSpeed is null || ctx.Targets.TargetSpeed > maxHold)
                {
                    ctx.Targets.TargetSpeed = maxHold;
                }

                if (OrbitDirection is null)
                {
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

        // Keep the target 180° ahead every tick
        SetOrbitTarget(ctx);

        // Track cumulative turn for orbit counting
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

        if (_cumulativeTurn >= 350)
        {
            _cumulativeTurn -= 360;
        }

        return false;
    }

    private void SetOrbitTarget(PhaseContext ctx)
    {
        double offset = OrbitDirection == TurnDirection.Left ? -180 : 180;
        double targetHdg = ((ctx.Aircraft.Heading + offset) % 360 + 360) % 360;

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
