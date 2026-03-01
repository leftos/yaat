using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft follows a TaxiRoute edge-by-edge at taxi speed.
/// Turns at nodes using ground turn rate.
/// Auto-stops at hold-short points (inserts HoldingShortPhase).
/// Completes when all segments have been traversed.
/// </summary>
public sealed class TaxiingPhase : Phase
{
    private const double NodeArrivalThresholdNm = 0.005;
    private const double LogIntervalSeconds = 3.0;

    private int _targetNodeId;
    private double _targetLat;
    private double _targetLon;
    private bool _initialized;
    private double _timeSinceLastLog;

    public override string Name => "Taxiing";

    public override void OnStart(PhaseContext ctx)
    {
        var route = ctx.Aircraft.AssignedTaxiRoute;
        if (route is null || route.IsComplete)
        {
            ctx.Logger.LogWarning(
                "[Taxi] {Callsign}: OnStart but route is {State}",
                ctx.Aircraft.Callsign,
                route is null ? "null" : "already complete");
            return;
        }

        ctx.Aircraft.IsOnGround = true;
        SetupCurrentSegment(ctx);

        ctx.Logger.LogDebug(
            "[Taxi] {Callsign}: started, {SegCount} segments, first target node {NodeId} at ({Lat:F6}, {Lon:F6})",
            ctx.Aircraft.Callsign, route.Segments.Count,
            _targetNodeId, _targetLat, _targetLon);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        var route = ctx.Aircraft.AssignedTaxiRoute;
        if (route is null || route.IsComplete)
        {
            ctx.Logger.LogDebug(
                "[Taxi] {Callsign}: OnTick exit — route {State}",
                ctx.Aircraft.Callsign,
                route is null ? "null" : "complete");
            return true;
        }

        if (!_initialized)
        {
            ctx.Logger.LogDebug(
                "[Taxi] {Callsign}: late init in OnTick (groundLayout {HasLayout})",
                ctx.Aircraft.Callsign,
                ctx.GroundLayout is not null ? "present" : "NULL");
            SetupCurrentSegment(ctx);
        }

        // Check if held
        if (ctx.Aircraft.IsHeld)
        {
            Decelerate(ctx);
            return false;
        }

        // Navigate toward the current target node
        double dist = GeoMath.DistanceNm(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _targetLat, _targetLon);

        if (dist <= NodeArrivalThresholdNm)
        {
            return ArriveAtNode(ctx, route);
        }

        // Turn toward target
        double bearing = GeoMath.BearingTo(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _targetLat, _targetLon);
        TurnToward(ctx, bearing);

        // Accelerate toward taxi speed
        Accelerate(ctx);

        // Update current taxiway name
        var seg = route.CurrentSegment;
        if (seg is not null)
        {
            ctx.Aircraft.CurrentTaxiway = seg.TaxiwayName;
        }

        // Periodic logging
        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            ctx.Logger.LogDebug(
                "[Taxi] {Callsign}: seg {SegIdx}/{SegCount} on {Taxiway}, target node {NodeId}, dist={Dist:F4}nm, gs={Gs:F1}kts, hdg={Hdg:F0}, bearing={Brg:F0}, pos=({Lat:F6},{Lon:F6})",
                ctx.Aircraft.Callsign,
                route.CurrentSegmentIndex, route.Segments.Count,
                seg?.TaxiwayName ?? "?",
                _targetNodeId, dist,
                ctx.Aircraft.GroundSpeed, ctx.Aircraft.Heading,
                bearing,
                ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
        }

        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        ctx.Logger.LogDebug(
            "[Taxi] {Callsign}: OnEnd ({Status})",
            ctx.Aircraft.Callsign, endStatus);

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
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.CrossRunway => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    private void SetupCurrentSegment(PhaseContext ctx)
    {
        var route = ctx.Aircraft.AssignedTaxiRoute;
        if (route?.CurrentSegment is null)
        {
            ctx.Logger.LogWarning(
                "[Taxi] {Callsign}: SetupCurrentSegment — no current segment (index={Idx})",
                ctx.Aircraft.Callsign,
                route?.CurrentSegmentIndex ?? -1);
            return;
        }

        var seg = route.CurrentSegment;
        _targetNodeId = seg.ToNodeId;

        if (ctx.GroundLayout is not null
            && ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var targetNode))
        {
            _targetLat = targetNode.Latitude;
            _targetLon = targetNode.Longitude;
        }
        else
        {
            ctx.Logger.LogWarning(
                "[Taxi] {Callsign}: cannot resolve node {NodeId} — groundLayout {HasLayout}",
                ctx.Aircraft.Callsign, _targetNodeId,
                ctx.GroundLayout is not null ? "present but node missing" : "NULL");
        }

        _initialized = true;
    }

    private bool ArriveAtNode(PhaseContext ctx, TaxiRoute route)
    {
        ctx.Logger.LogDebug(
            "[Taxi] {Callsign}: arrived at node {NodeId} (seg {SegIdx}/{SegCount})",
            ctx.Aircraft.Callsign, _targetNodeId,
            route.CurrentSegmentIndex, route.Segments.Count);

        // Check if this node is a hold-short point
        var holdShort = route.GetHoldShortAt(_targetNodeId);
        if (holdShort is not null && !holdShort.IsCleared)
        {
            ctx.Logger.LogDebug(
                "[Taxi] {Callsign}: hold short at node {NodeId} (rwy {Rwy}, reason {Reason})",
                ctx.Aircraft.Callsign, _targetNodeId,
                holdShort.RunwayId, holdShort.Reason);

            // Insert a HoldingShortPhase before continuing
            ctx.Aircraft.GroundSpeed = 0;
            ctx.Targets.TargetSpeed = 0;

            var holdPhase = new HoldingShortPhase(holdShort);
            ctx.Aircraft.Phases?.InsertAfterCurrent(holdPhase);
            return true;
        }

        // Advance to next segment
        route.CurrentSegmentIndex++;

        if (route.IsComplete)
        {
            ctx.Logger.LogDebug(
                "[Taxi] {Callsign}: route complete after {SegCount} segments",
                ctx.Aircraft.Callsign, route.Segments.Count);
            return true;
        }

        SetupCurrentSegment(ctx);
        return false;
    }

    private static void TurnToward(PhaseContext ctx, double targetBearing)
    {
        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category)
            * ctx.DeltaSeconds;
        ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(
            ctx.Aircraft.Heading, targetBearing, maxTurn);
    }

    private static void Accelerate(PhaseContext ctx)
    {
        double targetSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        double accelRate = CategoryPerformance.TaxiAccelRate(ctx.Category);

        if (ctx.Aircraft.GroundSpeed < targetSpeed)
        {
            ctx.Aircraft.GroundSpeed = Math.Min(
                targetSpeed,
                ctx.Aircraft.GroundSpeed + accelRate * ctx.DeltaSeconds);
        }

        ctx.Targets.TargetSpeed = targetSpeed;
    }

    private static void Decelerate(PhaseContext ctx)
    {
        double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
        ctx.Aircraft.GroundSpeed = Math.Max(
            0, ctx.Aircraft.GroundSpeed - decelRate * ctx.DeltaSeconds);
        ctx.Targets.TargetSpeed = 0;
    }

}
