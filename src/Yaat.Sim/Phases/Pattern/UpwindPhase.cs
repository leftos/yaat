using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Upwind leg: climb from runway heading to pattern altitude,
/// continue past departure end to crosswind turn point.
/// Completes when reaching the crosswind turn waypoint.
/// </summary>
public sealed class UpwindPhase : Phase
{
    private const double ArrivalNm = 0.3;

    private double _targetLat;
    private double _targetLon;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When true, the aircraft continues on the current heading past the turn point
    /// until a turn command (TC) or another EXT-clearing command is issued.
    /// </summary>
    public bool IsExtended { get; set; }

    public override string Name => "Upwind";

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        _targetLat = Waypoints.CrosswindTurnLat;
        _targetLon = Waypoints.CrosswindTurnLon;

        ctx.Targets.TargetHeading = Waypoints.UpwindHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        // Climb to pattern altitude
        ctx.Targets.TargetAltitude = Waypoints.PatternAltitude;
        ctx.Targets.DesiredVerticalRate = CategoryPerformance.InitialClimbRate(ctx.Category);

        // Accelerate toward downwind speed
        ctx.Targets.TargetSpeed = CategoryPerformance.DownwindSpeed(ctx.Category);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (IsExtended)
        {
            return false;
        }

        double dist = FlightPhysics.DistanceNm(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _targetLat, _targetLon);

        return dist < ArrivalNm;
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

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
