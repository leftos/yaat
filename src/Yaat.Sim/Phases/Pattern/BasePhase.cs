using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Base leg: turn from downwind onto base heading, begin descent.
/// Decelerates to base speed, descends toward approach altitude.
/// Completes when reaching the final turn waypoint.
/// </summary>
public sealed class BasePhase : Phase
{
    private const double ArrivalNm = 0.3;

    private double _targetLat;
    private double _targetLon;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When set, overrides the default final turn target to a point on the
    /// extended centerline at this distance from the threshold.
    /// </summary>
    public double? FinalDistanceNm { get; set; }

    /// <summary>
    /// When true, the aircraft continues on the current heading past the turn point
    /// until a turn-final command or another EXT-clearing command is issued.
    /// </summary>
    public bool IsExtended { get; set; }

    public override string Name => "Base";

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        if (FinalDistanceNm is not null)
        {
            double reciprocal = ((Waypoints.FinalHeading + 180.0) % 360.0 + 360.0) % 360.0;
            var target = FlightPhysics.ProjectPoint(
                Waypoints.ThresholdLat, Waypoints.ThresholdLon,
                reciprocal, FinalDistanceNm.Value);
            _targetLat = target.Lat;
            _targetLon = target.Lon;
        }
        else
        {
            _targetLat = Waypoints.FinalTurnLat;
            _targetLon = Waypoints.FinalTurnLon;
        }

        var turnDir = Waypoints.Direction == PatternDirection.Left
            ? TurnDirection.Left : TurnDirection.Right;

        ctx.Targets.TargetHeading = Waypoints.BaseHeading;
        ctx.Targets.PreferredTurnDirection = turnDir;
        ctx.Targets.NavigationRoute.Clear();

        // Begin descent
        double descentRate = CategoryPerformance.PatternDescentRate(ctx.Category);
        ctx.Targets.DesiredVerticalRate = -descentRate;

        // Approximate target altitude: halfway between pattern and threshold
        double thresholdElev = ctx.Runway?.ElevationFt ?? ctx.FieldElevation;
        double midAlt = thresholdElev + (Waypoints.PatternAltitude - thresholdElev) * 0.5;
        ctx.Targets.TargetAltitude = midAlt;

        // Slow to base speed
        ctx.Targets.TargetSpeed = CategoryPerformance.BaseSpeed(ctx.Category);
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
