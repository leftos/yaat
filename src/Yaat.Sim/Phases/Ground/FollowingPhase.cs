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
    private static readonly ILogger Log = SimLog.CreateLogger("FollowingPhase");

    /// <summary>Gap at which the follower stops matching taxi speed and starts closing up on the lead (~180 ft).</summary>
    public const double FollowDistanceNm = 0.03;

    /// <summary>Gap the follower stops at behind the lead (~90 ft).</summary>
    public const double StopDistanceNm = 0.015;

    /// <summary>
    /// Walking-pace speed the follower closes the last stretch at when the lead is stopped or crawling — matching
    /// the lead's speed verbatim would freeze the follower at the follow distance behind a stationary lead.
    /// </summary>
    private const double FollowCloseUpSpeedKts = 5.0;

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
        Log.LogDebug("[Follow] {Callsign}: following {Target}", ctx.Aircraft.Callsign, _targetCallsign);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.Ground.IsImmobile)
        {
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
            Log.LogDebug(
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

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, target.Position);

        // Turn toward the target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, target.Position);
        double maxTurn = CategoryPerformance.GroundYawRateAtSpeed(ctx.Category, ctx.Aircraft.GroundSpeed) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);

        // Speed: match target with distance-based adjustment. Publish the target and let physics close
        // the gap at the ground accel/brake rates — the phase never integrates speed itself.
        if (dist <= StopDistanceNm)
        {
            ctx.Targets.TargetSpeed = 0;
        }
        else if (dist <= FollowDistanceNm)
        {
            // Brake curve to the stop gap rather than a flat close-up speed: v = sqrt(2·a·d) decays to 0
            // exactly at StopDistanceNm, so the follower rolls to a stop instead of holding 5 kt until the
            // gap trips the branch above and slamming the target to 0 (bang-bang at the boundary). The
            // lead's own speed still wins when it is moving — a follower never falls behind a rolling lead.
            double remainingToStopNm = Math.Max(0.0, dist - StopDistanceNm);
            double brakeCurveKts = Math.Sqrt(2.0 * CategoryPerformance.TaxiDecelRate(ctx.Category) * remainingToStopNm * 3600.0);
            ctx.Targets.TargetSpeed = Math.Max(target.GroundSpeed, Math.Min(FollowCloseUpSpeedKts, brakeCurveKts));
        }
        else
        {
            ctx.Targets.TargetSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            Log.LogTrace(
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
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.CrossRunway => CommandAcceptance.Allowed,
            CanonicalCommandType.FollowGround => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is following another; only HOLD/RES, CROSS, a new FOLLOWG, or a new TAXI apply"),
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

            double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, node.Position);
            if (dist > HoldShortDetectionNm)
            {
                continue;
            }

            // Check approach angle: aircraft heading should point toward the node
            double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, node.Position);
            double angleDiff = Math.Abs(GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, bearing));
            if (angleDiff > HoldShortAngleThreshold)
            {
                continue;
            }

            // Approaching a runway hold-short — stop and insert phases
            Log.LogDebug("[Follow] {Callsign}: hold short triggered at runway node {NodeId}", ctx.Aircraft.Callsign, node.Id);
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
