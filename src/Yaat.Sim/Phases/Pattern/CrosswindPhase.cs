using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Crosswind leg: turn from upwind heading to crosswind heading,
/// fly to downwind start point. Continues climb to pattern altitude.
/// Completes when reaching the downwind start waypoint.
/// </summary>
public sealed class CrosswindPhase : Phase
{
    private const double ArrivalNm = 0.3;

    private double _targetLat;
    private double _targetLon;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When true, the aircraft continues on the current heading past the turn point
    /// until a turn command (TD) or another EXT-clearing command is issued.
    /// </summary>
    public bool IsExtended { get; set; }

    public override string Name => "Crosswind";

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        _targetLat = Waypoints.DownwindStartLat;
        _targetLon = Waypoints.DownwindStartLon;

        var turnDir = Waypoints.Direction == PatternDirection.Left
            ? TurnDirection.Left : TurnDirection.Right;

        ctx.Targets.TargetHeading = Waypoints.CrosswindHeading;
        ctx.Targets.PreferredTurnDirection = turnDir;
        ctx.Targets.NavigationRoute.Clear();

        // Continue climbing to pattern altitude if not there yet
        if (ctx.Aircraft.Altitude < Waypoints.PatternAltitude - 50)
        {
            ctx.Targets.TargetAltitude = Waypoints.PatternAltitude;
            ctx.Targets.DesiredVerticalRate = CategoryPerformance.InitialClimbRate(ctx.Category);
        }
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
