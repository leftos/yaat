using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Crosses midfield from the wrong side to the correct pattern side.
/// Per AIM 4-3-3: aircraft crosses at pattern altitude + 500ft (safe altitude
/// above pattern traffic), then enters downwind normally.
/// </summary>
public sealed class MidfieldCrossingPhase : Phase
{
    private const double ArrivalNm = 0.5;
    private const double CrossingAltitudeOffsetFt = 500.0;

    private double _targetLat;
    private double _targetLon;

    public PatternWaypoints? Waypoints { get; set; }

    public override string Name => "MidfieldCrossing";

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        // Target: midfield point on the correct side (midpoint of downwind leg)
        _targetLat = (Waypoints.DownwindStartLat + Waypoints.DownwindAbeamLat) / 2.0;
        _targetLon = (Waypoints.DownwindStartLon + Waypoints.DownwindAbeamLon) / 2.0;

        // Set heading toward midfield target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);
        ctx.Targets.TargetTrueHeading = new TrueHeading(bearing);
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        // Climb/maintain pattern altitude + 500ft for safe crossing
        ctx.Targets.TargetAltitude = Waypoints.PatternAltitude + CrossingAltitudeOffsetFt;
        ctx.Targets.DesiredVerticalRate = null;

        // Downwind speed for the category
        ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);

        ctx.Logger.LogDebug(
            "[MidfieldCrossing] {Callsign}: started, crossingAlt={Alt:F0}ft",
            ctx.Aircraft.Callsign,
            Waypoints.PatternAltitude + CrossingAltitudeOffsetFt
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Continuously update heading toward the midfield target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);
        ctx.Targets.TargetTrueHeading = new TrueHeading(bearing);

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);

        bool complete = dist < ArrivalNm;
        if (complete)
        {
            ctx.Logger.LogDebug("[MidfieldCrossing] {Callsign}: midfield reached, transitioning to downwind", ctx.Aircraft.Callsign);
        }

        return complete;
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
