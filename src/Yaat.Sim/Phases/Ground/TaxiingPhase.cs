using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft follows a TaxiRoute edge-by-edge at taxi speed.
/// Turns at nodes using ground turn rate.
/// Auto-stops at hold-short points (inserts HoldingShortPhase).
/// Completes when all segments have been traversed.
/// </summary>
public sealed class TaxiingPhase : Phase
{
    private const double NodeArrivalThresholdNm = 0.015;
    private const double OvershootDetectionNm = 0.03;
    private const double LogIntervalSeconds = 3.0;

    private int _targetNodeId;
    private double _targetLat;
    private double _targetLon;
    private bool _initialized;
    private double _timeSinceLastLog;
    private double _prevDistToTarget = double.MaxValue;

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
            AdjustSpeed(ctx, 0);
            return false;
        }

        // Navigate toward the current target node
        double dist = GeoMath.DistanceNm(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _targetLat, _targetLon);

        bool overshot = dist > _prevDistToTarget
            && _prevDistToTarget < OvershootDetectionNm;
        _prevDistToTarget = dist;

        if (dist <= NodeArrivalThresholdNm || overshot)
        {
            if (overshot)
            {
                ctx.Logger.LogDebug(
                    "[Taxi] {Callsign}: overshoot detected at node {NodeId} (dist={Dist:F4}nm, prev={Prev:F4}nm)",
                    ctx.Aircraft.Callsign, _targetNodeId, dist, _prevDistToTarget);
            }

            _prevDistToTarget = double.MaxValue;
            return ArriveAtNode(ctx, route);
        }

        // Turn toward target
        double bearing = GeoMath.BearingTo(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _targetLat, _targetLon);
        TurnToward(ctx, bearing);

        // Speed scales with turn sharpness: straight = full, sharp turn = crawl
        double angleDiff = Math.Abs(FlightPhysics.NormalizeAngle(
            bearing - ctx.Aircraft.Heading));
        double maxSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        double speedFraction = Math.Clamp(1.0 - (angleDiff / 120.0), 0.15, 1.0);
        AdjustSpeed(ctx, maxSpeed * speedFraction);

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
        _prevDistToTarget = double.MaxValue;
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

            ApplyDepartureClearanceIfPending(ctx);
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

    private static void AdjustSpeed(PhaseContext ctx, double targetSpeed)
    {
        double current = ctx.Aircraft.GroundSpeed;
        if (current < targetSpeed)
        {
            double rate = CategoryPerformance.TaxiAccelRate(ctx.Category);
            ctx.Aircraft.GroundSpeed = Math.Min(
                targetSpeed, current + rate * ctx.DeltaSeconds);
        }
        else if (current > targetSpeed)
        {
            double rate = CategoryPerformance.TaxiDecelRate(ctx.Category);
            ctx.Aircraft.GroundSpeed = Math.Max(
                targetSpeed, current - rate * ctx.DeltaSeconds);
        }
    }

    private static void ApplyDepartureClearanceIfPending(PhaseContext ctx)
    {
        var phases = ctx.Aircraft.Phases;
        var dep = phases?.DepartureClearance;
        if (dep is null || phases is null)
        {
            return;
        }

        var lineup = new LineUpPhase();
        var luaw = new LinedUpAndWaitingPhase();
        var takeoff = new TakeoffPhase();
        var climb = new InitialClimbPhase();
        phases.InsertAfterCurrent(new Phase[] { lineup, luaw, takeoff, climb });

        if (dep.Type == ClearanceType.ClearedForTakeoff)
        {
            luaw.SatisfyClearance(ClearanceType.ClearedForTakeoff);
            luaw.AssignedHeading = dep.AssignedHeading;
            luaw.AssignedTurn = dep.AssignedTurn;
            takeoff.SetAssignedDeparture(dep.AssignedHeading, dep.AssignedTurn);

            if (dep.TrafficPattern is { } patDir
                && phases.AssignedRunway is { } rwy)
            {
                phases.TrafficDirection = patDir;
                var cat = AircraftCategorization.Categorize(
                    ctx.Aircraft.AircraftType);
                var circuit = PatternBuilder.BuildCircuit(
                    rwy, cat, patDir, PatternEntryLeg.Upwind, true);
                phases.Phases.AddRange(circuit);
            }
        }

        phases.DepartureClearance = null;
        ctx.Logger.LogDebug(
            "[Taxi] {Callsign}: departure clearance {Type} applied at route end",
            ctx.Aircraft.Callsign, dep.Type);
    }

}
