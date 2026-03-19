using Microsoft.Extensions.Logging;
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

        ctx.Logger.LogDebug(
            "[ApproachNav] {Callsign}: started, {Count} fixes [{Names}]",
            ctx.Aircraft.Callsign,
            Fixes.Count,
            string.Join(" → ", Fixes.Select(f => f.Name))
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_currentFixIndex >= Fixes.Count)
        {
            return true;
        }

        var fix = Fixes[_currentFixIndex];
        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, fix.Latitude, fix.Longitude);

        // Determine sequencing threshold: fly-by fixes with a following fix use anticipation
        double threshold = FixArrivalThresholdNm;
        bool hasNextFix = _currentFixIndex < Fixes.Count - 1;
        double anticipationNm = 0;

        if (hasNextFix && !fix.IsFlyOver)
        {
            var nextFix = Fixes[_currentFixIndex + 1];
            double currentBearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, fix.Latitude, fix.Longitude);
            double nextBearing = GeoMath.BearingTo(fix.Latitude, fix.Longitude, nextFix.Latitude, nextFix.Longitude);
            double turnRate =
                ctx.Targets.TurnRateOverride ?? AircraftPerformance.TurnRate(ctx.AircraftType, AircraftCategorization.Categorize(ctx.AircraftType));
            anticipationNm = FlightPhysics.ComputeAnticipationDistanceNm(ctx.Aircraft.GroundSpeed, turnRate, currentBearing, nextBearing);
            threshold = Math.Max(anticipationNm, FixArrivalThresholdNm);
        }

        bool inAnticipationZone = anticipationNm > FixArrivalThresholdNm && dist < threshold;

        // For fly-by with anticipation, wait until along-track past the waypoint
        bool shouldSequence;
        if (inAnticipationZone)
        {
            var nextFix = Fixes[_currentFixIndex + 1];
            double nextBearing = GeoMath.BearingTo(fix.Latitude, fix.Longitude, nextFix.Latitude, nextFix.Longitude);
            double alongTrack = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, fix.Latitude, fix.Longitude, nextBearing);
            shouldSequence = alongTrack >= 0;
        }
        else
        {
            shouldSequence = dist < FixArrivalThresholdNm;
        }

        if (shouldSequence)
        {
            ctx.Logger.LogDebug(
                "[ApproachNav] {Callsign}: reached fix {Fix} ({Idx}/{Total}), alt={Alt:F0}ft, IAS={Ias:F0}kts",
                ctx.Aircraft.Callsign,
                fix.Name,
                _currentFixIndex + 1,
                Fixes.Count,
                ctx.Aircraft.Altitude,
                ctx.Aircraft.IndicatedAirspeed
            );

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

        // Add remaining fixes so UpdateSpeedPlanning can scan ahead for speed constraints.
        // Only SpeedRestriction is set — the phase handles altitude constraints itself.
        for (int i = _currentFixIndex + 1; i < Fixes.Count; i++)
        {
            var future = Fixes[i];
            ctx.Targets.NavigationRoute.Add(
                new NavigationTarget
                {
                    Name = future.Name,
                    Latitude = future.Latitude,
                    Longitude = future.Longitude,
                    SpeedRestriction = future.SpeedKts is { } kts ? new CifpSpeedRestriction(kts, IsMaximum: true) : null,
                    IsFlyOver = future.IsFlyOver,
                }
            );
        }

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
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            // Speed/altitude adjust targets without leaving the approach
            CanonicalCommandType.Speed => CommandAcceptance.Allowed,
            CanonicalCommandType.Mach => CommandAcceptance.Allowed,
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
    CifpFixRole Role = CifpFixRole.None,
    bool IsFlyOver = false
);
