using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Navigates an airborne aircraft to a pattern entry point, descending to pattern
/// altitude and decelerating to pattern speed. Inserted before the first pattern
/// leg phase (downwind, base, etc.) when the aircraft is far from the pattern.
/// Completes when the entry point is reached (NavigationRoute drained by FlightPhysics).
/// </summary>
public sealed class PatternEntryPhase : Phase
{
    public required double EntryLat { get; init; }
    public required double EntryLon { get; init; }
    public required double PatternAltitude { get; init; }

    public override string Name => "Pattern Entry";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.NavigationRoute.Add(new NavigationTarget
        {
            Latitude = EntryLat,
            Longitude = EntryLon,
            Name = "PTN-ENTRY",
        });
        ctx.Targets.TargetHeading = null;
        ctx.Targets.TurnRateOverride = null;
        ctx.Targets.PreferredTurnDirection = null;

        // Descend/climb to pattern altitude
        ctx.Targets.TargetAltitude = PatternAltitude;
        if (ctx.Aircraft.Altitude > PatternAltitude + 100)
        {
            ctx.Targets.DesiredVerticalRate = -CategoryPerformance.PatternDescentRate(ctx.Category);
        }
        else if (ctx.Aircraft.Altitude < PatternAltitude - 100)
        {
            ctx.Targets.DesiredVerticalRate = CategoryPerformance.InitialClimbRate(ctx.Category);
        }

        // Decelerate toward pattern speed
        ctx.Targets.TargetSpeed = CategoryPerformance.DownwindSpeed(ctx.Category);

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, EntryLat, EntryLon);
        ctx.Logger.LogDebug(
            "[PatternEntry] {Callsign}: navigating to entry, dist={Dist:F1}nm, alt={Alt:F0}ft, tgtAlt={TgtAlt:F0}ft",
            ctx.Aircraft.Callsign, dist, ctx.Aircraft.Altitude, PatternAltitude);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // FlightPhysics drains NavigationRoute as waypoints are reached
        return ctx.Targets.NavigationRoute.Count == 0;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }
}
