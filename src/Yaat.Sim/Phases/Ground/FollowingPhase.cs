using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Continuously follows a target aircraft on the ground.
/// Matches the target's speed with a safe following distance.
/// Completes when the target is deleted or no longer on the ground.
/// Requires PhaseContext.AircraftLookup to resolve the target.
/// </summary>
public sealed class FollowingPhase : Phase
{
    private const double FollowDistanceNm = 0.03; // ~180 ft
    private const double StopDistanceNm = 0.015; // ~90 ft
    private const double HoldShortDetectionNm = 0.02; // ~120 ft
    private const double HoldShortAngleThreshold = 90.0;
    private const double LogIntervalSeconds = 3.0;

    private readonly string _targetCallsign;
    private double _timeSinceLastLog;

    public FollowingPhase(string targetCallsign)
    {
        _targetCallsign = targetCallsign;
    }

    public string TargetCallsign => _targetCallsign;

    public override string Name => $"Following {_targetCallsign}";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Logger.LogDebug("[Follow] {Callsign}: following {Target}", ctx.Aircraft.Callsign, _targetCallsign);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Check if held
        if (ctx.Aircraft.IsHeld)
        {
            double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
            ctx.Aircraft.IndicatedAirspeed = Math.Max(0, ctx.Aircraft.IndicatedAirspeed - decelRate * ctx.DeltaSeconds);
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        if (CheckRunwayHoldShort(ctx))
        {
            return true;
        }

        var target = ctx.AircraftLookup?.Invoke(_targetCallsign);
        if (target is null || !target.IsOnGround)
        {
            ctx.Logger.LogDebug(
                "[Follow] {Callsign}: target {Target} {Reason}, stopping",
                ctx.Aircraft.Callsign,
                _targetCallsign,
                target is null ? "not found" : "no longer on ground"
            );
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;

            // Insert idle ground phase so the aircraft remains in a state that accepts commands
            ctx.Aircraft.Phases?.InsertAfterCurrent(new HoldingInPositionPhase());
            return true;
        }

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, target.Latitude, target.Longitude);

        // Turn toward the target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, target.Latitude, target.Longitude);
        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);

        // Speed: match target with distance-based adjustment
        double accelRate = CategoryPerformance.TaxiAccelRate(ctx.Category);
        double decelRate2 = CategoryPerformance.TaxiDecelRate(ctx.Category);

        if (dist <= StopDistanceNm)
        {
            ctx.Aircraft.IndicatedAirspeed = Math.Max(0, ctx.Aircraft.IndicatedAirspeed - decelRate2 * ctx.DeltaSeconds);
        }
        else if (dist <= FollowDistanceNm)
        {
            double targetSpeed = target.GroundSpeed;
            if (ctx.Aircraft.IndicatedAirspeed > targetSpeed)
            {
                ctx.Aircraft.IndicatedAirspeed = Math.Max(targetSpeed, ctx.Aircraft.IndicatedAirspeed - decelRate2 * ctx.DeltaSeconds);
            }
            else if (ctx.Aircraft.IndicatedAirspeed < targetSpeed)
            {
                ctx.Aircraft.IndicatedAirspeed = Math.Min(targetSpeed, ctx.Aircraft.IndicatedAirspeed + accelRate * ctx.DeltaSeconds);
            }
        }
        else
        {
            double taxiSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
            if (ctx.Aircraft.IndicatedAirspeed < taxiSpeed)
            {
                ctx.Aircraft.IndicatedAirspeed = Math.Min(taxiSpeed, ctx.Aircraft.IndicatedAirspeed + accelRate * ctx.DeltaSeconds);
            }
        }

        ctx.Targets.TargetSpeed = ctx.Aircraft.IndicatedAirspeed;

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            ctx.Logger.LogTrace(
                "[Follow] {Callsign}: dist={Dist:F4}nm to {Target}, gs={Gs:F1}kts, targetGs={TGs:F1}kts",
                ctx.Aircraft.Callsign,
                dist,
                _targetCallsign,
                ctx.Aircraft.GroundSpeed,
                target.GroundSpeed
            );
        }

        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.CrossRunway => CommandAcceptance.Allowed,
            CanonicalCommandType.FollowGround => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new FollowingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            TargetCallsign = _targetCallsign,
            TimeSinceLastLog = _timeSinceLastLog,
        };

    public static FollowingPhase FromSnapshot(FollowingPhaseDto dto)
    {
        var phase = new FollowingPhase(dto.TargetCallsign);
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }

    /// <summary>
    /// Check if the aircraft is approaching a runway hold-short node.
    /// If so, insert HoldingShortPhase + new FollowingPhase and stop.
    /// Returns true if a hold-short was triggered (phase should complete).
    /// </summary>
    private bool CheckRunwayHoldShort(PhaseContext ctx)
    {
        if (ctx.GroundLayout is null || ctx.Aircraft.GroundSpeed <= 0)
        {
            return false;
        }

        foreach (var node in ctx.GroundLayout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, node.Latitude, node.Longitude);
            if (dist > HoldShortDetectionNm)
            {
                continue;
            }

            // Check approach angle: aircraft heading should point toward the node
            double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, node.Latitude, node.Longitude);
            double angleDiff = Math.Abs(GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, bearing));
            if (angleDiff > HoldShortAngleThreshold)
            {
                continue;
            }

            // Approaching a runway hold-short — stop and insert phases
            ctx.Logger.LogDebug("[Follow] {Callsign}: hold short triggered at runway node {NodeId}", ctx.Aircraft.Callsign, node.Id);
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;

            var holdShort = new HoldShortPoint
            {
                NodeId = node.Id,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = node.RunwayId?.ToString(),
            };

            var holdPhase = new HoldingShortPhase(holdShort);
            var resumeFollow = new FollowingPhase(_targetCallsign);
            ctx.Aircraft.Phases?.InsertAfterCurrent(new Phase[] { holdPhase, resumeFollow });
            return true;
        }

        return false;
    }
}
