using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft crosses a runway at ~10 kts along the taxiway alignment.
/// Completes when the aircraft reaches the far-side taxiway node.
/// </summary>
public sealed class CrossingRunwayPhase : Phase
{
    private const double ArrivalThresholdNm = 0.005;
    private const double LogIntervalSeconds = 3.0;

    private readonly int _targetNodeId;
    private double _targetLat;
    private double _targetLon;
    private bool _initialized;
    private double _timeSinceLastLog;

    public CrossingRunwayPhase(int targetNodeId)
    {
        _targetNodeId = targetNodeId;
    }

    public override string Name => "Crossing Runway";

    public override void OnStart(PhaseContext ctx)
    {
        if (ctx.GroundLayout is not null && ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var node))
        {
            _targetLat = node.Latitude;
            _targetLon = node.Longitude;
            _initialized = true;
        }

        double crossSpeed = CategoryPerformance.RunwayCrossingSpeed(ctx.Category);
        ctx.Targets.TargetSpeed = crossSpeed;
        ctx.Aircraft.IsOnGround = true;

        ctx.Logger.LogDebug(
            "[Crossing] {Callsign}: crossing runway, target nodeId={NodeId}, initialized={Init}",
            ctx.Aircraft.Callsign,
            _targetNodeId,
            _initialized
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_initialized)
        {
            return true;
        }

        if (ctx.Aircraft.IsHeld)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            return false;
        }

        double crossSpeed = CategoryPerformance.RunwayCrossingSpeed(ctx.Category);

        // Accelerate to crossing speed
        double accelRate = CategoryPerformance.TaxiAccelRate(ctx.Category);
        if (ctx.Aircraft.IndicatedAirspeed < crossSpeed)
        {
            ctx.Aircraft.IndicatedAirspeed = Math.Min(crossSpeed, ctx.Aircraft.IndicatedAirspeed + accelRate * ctx.DeltaSeconds);
        }

        // Turn toward target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);

        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);

        // Check arrival
        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);

        if (dist <= ArrivalThresholdNm)
        {
            return true;
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            ctx.Logger.LogDebug("[Crossing] {Callsign}: dist={Dist:F4}nm, gs={Gs:F1}kts", ctx.Aircraft.Callsign, dist, ctx.Aircraft.GroundSpeed);
        }

        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        ctx.Logger.LogDebug("[Crossing] {Callsign}: OnEnd ({Status})", ctx.Aircraft.Callsign, endStatus);

        if (endStatus == PhaseStatus.Completed)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new CrossingRunwayPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            TargetNodeId = _targetNodeId,
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            Initialized = _initialized,
            TimeSinceLastLog = _timeSinceLastLog,
        };

    public static CrossingRunwayPhase FromSnapshot(CrossingRunwayPhaseDto dto)
    {
        var phase = new CrossingRunwayPhase(dto.TargetNodeId);
        phase._targetLat = dto.TargetLat;
        phase._targetLon = dto.TargetLon;
        phase._initialized = dto.Initialized;
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
