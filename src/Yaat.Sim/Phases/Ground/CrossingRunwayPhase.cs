using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft crosses a runway at ~10 kts along the taxiway alignment.
/// Completes when the aircraft reaches the far-side taxiway node.
/// </summary>
public sealed class CrossingRunwayPhase : Phase
{
    private const double ArrivalThresholdNm = 0.005;

    private readonly int _targetNodeId;
    private double _targetLat;
    private double _targetLon;
    private bool _initialized;

    public CrossingRunwayPhase(int targetNodeId)
    {
        _targetNodeId = targetNodeId;
    }

    public override string Name => "Crossing Runway";

    public override void OnStart(PhaseContext ctx)
    {
        if (ctx.GroundLayout is not null
            && ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var node))
        {
            _targetLat = node.Latitude;
            _targetLon = node.Longitude;
            _initialized = true;
        }

        double crossSpeed = CategoryPerformance.RunwayCrossingSpeed(ctx.Category);
        ctx.Targets.TargetSpeed = crossSpeed;
        ctx.Aircraft.IsOnGround = true;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_initialized)
        {
            return true;
        }

        double crossSpeed = CategoryPerformance.RunwayCrossingSpeed(ctx.Category);

        // Accelerate to crossing speed
        double accelRate = CategoryPerformance.TaxiAccelRate(ctx.Category);
        if (ctx.Aircraft.GroundSpeed < crossSpeed)
        {
            ctx.Aircraft.GroundSpeed = Math.Min(
                crossSpeed,
                ctx.Aircraft.GroundSpeed + accelRate * ctx.DeltaSeconds);
        }

        // Turn toward target
        double bearing = GeoMath.BearingTo(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _targetLat, _targetLon);

        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category)
            * ctx.DeltaSeconds;
        ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(
            ctx.Aircraft.Heading, bearing, maxTurn);

        // Check arrival
        double dist = GeoMath.DistanceNm(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _targetLat, _targetLon);

        return dist <= ArrivalThresholdNm;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        if (endStatus == PhaseStatus.Completed)
        {
            ctx.Aircraft.GroundSpeed = 0;
            ctx.Targets.TargetSpeed = 0;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

}
