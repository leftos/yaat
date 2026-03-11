using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft pushes backwards along a multi-segment taxi route to a named parking spot.
/// Movement is via PushbackHeading (tail-first), steering through each route segment.
/// At segment boundaries with large heading changes, stops and pivots before resuming.
/// On arrival at the final node, rotates nose to the target heading.
/// </summary>
public sealed class PushbackToSpotPhase : Phase
{
    private const double NodeArrivalThresholdNm = 0.008;
    private const double HeadingReachedDeg = 2.0;
    private const double PivotThresholdDeg = 20.0;
    private const double LogIntervalSeconds = 3.0;

    private readonly TaxiRoute _route;
    private readonly int? _targetHeading;

    private int _targetNodeId;
    private double _targetLat;
    private double _targetLon;
    private bool _initialized;
    private bool _reachedFinalNode;
    private bool _pivoting;
    private double _pivotTargetHeading;
    private double _timeSinceLastLog;

    public PushbackToSpotPhase(TaxiRoute route, int? targetHeading)
    {
        _route = route;
        _targetHeading = targetHeading;
    }

    public override string Name => "Pushback to Spot";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Aircraft.AssignedTaxiRoute = _route;
        SetupCurrentSegment(ctx);

        ctx.Logger.LogDebug(
            "[PushSpot] {Callsign}: started, {SegCount} segments, targetHdg={TargetHdg}",
            ctx.Aircraft.Callsign,
            _route.Segments.Count,
            _targetHeading?.ToString() ?? "parking"
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.IsHeld)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
            ctx.Aircraft.PushbackHeading = null;
            return false;
        }

        double turnRate = CategoryPerformance.PushbackTurnRate(ctx.Category);

        if (_reachedFinalNode)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
            ctx.Aircraft.PushbackHeading = null;

            if (_targetHeading is not { } finalHdg)
            {
                return true;
            }

            return TurnNoseToward(ctx, finalHdg, turnRate);
        }

        if (!_initialized)
        {
            SetupCurrentSegment(ctx);
        }

        // Pivoting in place at a node before resuming movement
        if (_pivoting)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;

            double currentPush = ctx.Aircraft.PushbackHeading ?? (ctx.Aircraft.Heading + 180.0) % 360.0;
            double maxTurn = turnRate * ctx.DeltaSeconds;
            ctx.Aircraft.PushbackHeading = GeoMath.TurnHeadingToward(currentPush, _pivotTargetHeading, maxTurn);

            double desiredNose = (ctx.Aircraft.PushbackHeading.Value + 180.0) % 360.0;
            ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(ctx.Aircraft.Heading, desiredNose, maxTurn);

            double diff = Math.Abs(FlightPhysics.NormalizeAngle(_pivotTargetHeading - ctx.Aircraft.PushbackHeading.Value));
            if (diff < HeadingReachedDeg)
            {
                _pivoting = false;
                ctx.Logger.LogDebug(
                    "[PushSpot] {Callsign}: pivot complete, pushHdg={PushHdg:F0}, resuming movement",
                    ctx.Aircraft.Callsign,
                    ctx.Aircraft.PushbackHeading.Value
                );
            }

            return false;
        }

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);

        if (dist <= NodeArrivalThresholdNm)
        {
            return ArriveAtNode(ctx);
        }

        // Steer PushbackHeading toward current target (tail moves toward target)
        double bearingToTarget = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);
        double maxArcTurn = turnRate * ctx.DeltaSeconds;
        ctx.Aircraft.PushbackHeading = GeoMath.TurnHeadingToward(
            ctx.Aircraft.PushbackHeading ?? (ctx.Aircraft.Heading + 180.0) % 360.0,
            bearingToTarget,
            maxArcTurn
        );

        // Keep nose facing opposite of pushback direction
        double desNose = (ctx.Aircraft.PushbackHeading.Value + 180.0) % 360.0;
        ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(ctx.Aircraft.Heading, desNose, turnRate * ctx.DeltaSeconds);

        ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);

        // Periodic logging
        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            ctx.Logger.LogDebug(
                "[PushSpot] {Callsign}: seg {SegIdx}/{SegCount}, dist={Dist:F4}nm, gs={Gs:F1}kts, pushHdg={PushHdg:F0}, noseHdg={NoseHdg:F0}",
                ctx.Aircraft.Callsign,
                _route.CurrentSegmentIndex,
                _route.Segments.Count,
                dist,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.PushbackHeading ?? 0,
                ctx.Aircraft.Heading
            );
        }

        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        ctx.Logger.LogDebug("[PushSpot] {Callsign}: OnEnd ({Status}), hdg={Hdg:F0}", ctx.Aircraft.Callsign, endStatus, ctx.Aircraft.Heading);

        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Aircraft.PushbackHeading = null;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Pushback => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    private void SetupCurrentSegment(PhaseContext ctx)
    {
        var seg = _route.CurrentSegment;
        if (seg is null)
        {
            return;
        }

        _targetNodeId = seg.ToNodeId;
        if (ctx.GroundLayout is not null && ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var targetNode))
        {
            _targetLat = targetNode.Latitude;
            _targetLon = targetNode.Longitude;
        }

        _initialized = true;
    }

    private bool ArriveAtNode(PhaseContext ctx)
    {
        ctx.Logger.LogDebug(
            "[PushSpot] {Callsign}: arrived at node {NodeId} (seg {SegIdx}/{SegCount})",
            ctx.Aircraft.Callsign,
            _targetNodeId,
            _route.CurrentSegmentIndex,
            _route.Segments.Count
        );

        // Snap to node position
        if (ctx.GroundLayout is not null && ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var node))
        {
            ctx.Aircraft.Latitude = node.Latitude;
            ctx.Aircraft.Longitude = node.Longitude;
        }

        _route.CurrentSegmentIndex++;

        if (_route.IsComplete)
        {
            _reachedFinalNode = true;
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
            ctx.Aircraft.PushbackHeading = null;
            ctx.Logger.LogDebug("[PushSpot] {Callsign}: reached destination, rotating to final heading", ctx.Aircraft.Callsign);

            if (_targetHeading is null)
            {
                return true;
            }

            return false;
        }

        SetupCurrentSegment(ctx);

        // Check if the next segment requires a large heading change — pivot in place
        double nextBearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);
        double currentPush = ctx.Aircraft.PushbackHeading ?? (ctx.Aircraft.Heading + 180.0) % 360.0;
        double headingChange = Math.Abs(FlightPhysics.NormalizeAngle(nextBearing - currentPush));

        if (headingChange > PivotThresholdDeg)
        {
            _pivoting = true;
            _pivotTargetHeading = nextBearing;
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;

            ctx.Logger.LogDebug(
                "[PushSpot] {Callsign}: pivoting {HeadingChange:F0}° from {Current:F0} to {Target:F0}",
                ctx.Aircraft.Callsign,
                headingChange,
                currentPush,
                nextBearing
            );
        }

        return false;
    }

    private static bool TurnNoseToward(PhaseContext ctx, double target, double turnRate)
    {
        double maxTurn = turnRate * ctx.DeltaSeconds;
        ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(ctx.Aircraft.Heading, target, maxTurn);
        double diff = FlightPhysics.NormalizeAngle(target - ctx.Aircraft.Heading);
        return Math.Abs(diff) < HeadingReachedDeg;
    }
}
