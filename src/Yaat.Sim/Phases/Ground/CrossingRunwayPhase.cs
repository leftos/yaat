using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft crosses a runway at ~10 kts along the taxiway alignment.
/// Completes when the aircraft reaches the far-side taxiway node.
/// </summary>
public sealed class CrossingRunwayPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("CrossingRunwayPhase");

    private const double ArrivalThresholdNm = 0.001;
    private const double LogIntervalSeconds = 3.0;

    private readonly int _approachNodeId;
    private readonly int _targetNodeId;
    private double _targetLat;
    private double _targetLon;
    private bool _initialized;
    private double _timeSinceLastLog;

    public CrossingRunwayPhase(int approachNodeId, int targetNodeId)
    {
        _approachNodeId = approachNodeId;
        _targetNodeId = targetNodeId;
    }

    public override string Name => "Crossing Runway";

    public override void OnStart(PhaseContext ctx)
    {
        if (ctx.GroundLayout is not null && ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var node))
        {
            // Offset ½ aircraft length past the target node so the tail clears the runway edge.
            double lengthFt = FaaAircraftDatabase.Get(ctx.Aircraft.AircraftType)?.LengthFt ?? 60.0;
            double halfLengthNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;

            if (ctx.GroundLayout.Nodes.TryGetValue(_approachNodeId, out var approachNode))
            {
                var vn = VirtualNode.OffsetPast(ctx.GroundLayout, node, approachNode, halfLengthNm);
                _targetLat = vn.Position.Lat;
                _targetLon = vn.Position.Lon;
            }
            else
            {
                _targetLat = node.Position.Lat;
                _targetLon = node.Position.Lon;
            }

            _initialized = true;
        }

        double crossSpeed = CategoryPerformance.RunwayCrossingSpeed(ctx.Category);
        ctx.Targets.TargetSpeed = crossSpeed;
        ctx.Aircraft.IsOnGround = true;

        Log.LogDebug(
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

        if (ctx.Aircraft.Ground.IsHeld)
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
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));

        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);

        // Check arrival
        double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));

        if (dist <= ArrivalThresholdNm)
        {
            return true;
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            Log.LogTrace("[Crossing] {Callsign}: dist={Dist:F4}nm, gs={Gs:F1}kts", ctx.Aircraft.Callsign, dist, ctx.Aircraft.GroundSpeed);
        }

        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        Log.LogDebug("[Crossing] {Callsign}: OnEnd ({Status})", ctx.Aircraft.Callsign, endStatus);
        // Speed targets are owned by the next phase. The typical successor is
        // TaxiingPhase (TaxiingPhase.cs BuildResumePhases) — zeroing IAS here
        // would force a stop the aircraft has to re-accelerate from. If the
        // route ends after the crossing, the inserted HoldingInPositionPhase
        // / AtParkingPhase will brake to zero on its own.
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is crossing a runway; only HOLD or a new TAXI apply until the crossing completes"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new CrossingRunwayPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            ApproachNodeId = _approachNodeId,
            TargetNodeId = _targetNodeId,
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            Initialized = _initialized,
            TimeSinceLastLog = _timeSinceLastLog,
        };

    public static CrossingRunwayPhase FromSnapshot(CrossingRunwayPhaseDto dto)
    {
        var phase = new CrossingRunwayPhase(dto.ApproachNodeId, dto.TargetNodeId);
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
