using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// Navigates the aircraft through an approach procedure's fix sequence
/// (IAF → IF → FAF). Respects altitude/speed restrictions from CIFP legs.
/// Completes when reaching the last fix (typically the FAF), handing off
/// to FinalApproachPhase.
/// </summary>
public sealed class ApproachNavigationPhase : Phase
{
    private const double FixArrivalThresholdNm = 0.5;

    private int _currentFixIndex;

    /// <summary>Ordered fix sequence to fly (name, lat, lon, altitude, speed).</summary>
    public required IReadOnlyList<ApproachFix> Fixes { get; init; }

    public override string Name => "ApproachNav";

    public override void OnStart(PhaseContext ctx)
    {
        _currentFixIndex = 0;
        NavigateToCurrentFix(ctx);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_currentFixIndex >= Fixes.Count)
        {
            return true;
        }

        var fix = Fixes[_currentFixIndex];
        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, fix.Latitude, fix.Longitude);

        if (dist < FixArrivalThresholdNm)
        {
            _currentFixIndex++;

            if (_currentFixIndex >= Fixes.Count)
            {
                return true;
            }

            NavigateToCurrentFix(ctx);
        }

        return false;
    }

    private void NavigateToCurrentFix(PhaseContext ctx)
    {
        var fix = Fixes[_currentFixIndex];

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = fix.Name,
                Latitude = fix.Latitude,
                Longitude = fix.Longitude,
            }
        );

        // Apply altitude restriction
        if (fix.Altitude is { } alt)
        {
            ApplyAltitudeRestriction(ctx, alt);
        }

        // Apply speed restriction
        if (fix.SpeedKts is { } speed)
        {
            ctx.Targets.TargetSpeed = speed;
        }
    }

    private static void ApplyAltitudeRestriction(PhaseContext ctx, CifpAltitudeRestriction alt)
    {
        switch (alt.Type)
        {
            case CifpAltitudeRestrictionType.At:
            case CifpAltitudeRestrictionType.GlideSlopeIntercept:
                ctx.Targets.TargetAltitude = alt.Altitude1Ft;
                break;

            case CifpAltitudeRestrictionType.AtOrAbove:
                if (ctx.Aircraft.Altitude < alt.Altitude1Ft)
                {
                    ctx.Targets.TargetAltitude = alt.Altitude1Ft;
                }
                break;

            case CifpAltitudeRestrictionType.AtOrBelow:
                if (ctx.Aircraft.Altitude > alt.Altitude1Ft)
                {
                    ctx.Targets.TargetAltitude = alt.Altitude1Ft;
                }
                break;

            case CifpAltitudeRestrictionType.Between:
                if (alt.Altitude2Ft is { } lower)
                {
                    if (ctx.Aircraft.Altitude > alt.Altitude1Ft)
                    {
                        ctx.Targets.TargetAltitude = alt.Altitude1Ft;
                    }
                    else if (ctx.Aircraft.Altitude < lower)
                    {
                        ctx.Targets.TargetAltitude = lower;
                    }
                }
                break;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            // Approach-related commands pass through
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            // Speed/altitude adjust targets without leaving the approach
            CanonicalCommandType.Speed => CommandAcceptance.Allowed,
            CanonicalCommandType.ClimbMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.DescendMaintain => CommandAcceptance.Allowed,
            // Everything else (heading, direct-to, etc.) takes the aircraft off the approach
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}

/// <summary>
/// A fix in the approach navigation sequence with optional restrictions.
/// </summary>
public sealed record ApproachFix(
    string Name,
    double Latitude,
    double Longitude,
    CifpAltitudeRestriction? Altitude = null,
    int? SpeedKts = null,
    CifpFixRole Role = CifpFixRole.None
);
