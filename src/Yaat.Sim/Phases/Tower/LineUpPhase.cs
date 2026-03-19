using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Taxis the aircraft from the hold-short point onto the runway centerline
/// and aligns with the runway heading. Completes when aligned.
/// Two-stage navigation: first follows the taxiway edge from hold-short to the
/// on-runway node, then corrects laterally to the centerline projection.
/// Inserted before LinedUpAndWaitingPhase when LUAW/CTO is issued.
/// </summary>
public sealed class LineUpPhase : Phase
{
    private const double CenterlineArrivalThresholdNm = 0.003;
    private const double OnRunwayNodeThresholdNm = 0.015;
    private const double HeadingToleranceDeg = 2.0;
    private const double LogIntervalSeconds = 3.0;

    private readonly int? _holdShortNodeId;

    private TrueHeading _runwayHeading;
    private bool _initialized;
    private double _timeSinceLastLog;

    // Stage 1: navigate to on-runway node (if available)
    private double _stage1Lat;
    private double _stage1Lon;
    private bool _hasStage1;
    private bool _stage1Complete;

    // Stage 2: navigate to centerline projection
    private double _centerlineLat;
    private double _centerlineLon;
    private bool _aligningOnly;

    public LineUpPhase(int? holdShortNodeId = null)
    {
        _holdShortNodeId = holdShortNodeId;
    }

    public override string Name => "LiningUp";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;

        if (ctx.Runway is null)
        {
            ctx.Logger.LogWarning("[LineUp] {Callsign}: no runway context, skipping", ctx.Aircraft.Callsign);
            return;
        }

        _runwayHeading = ctx.Runway.TrueHeading;

        ComputeCenterlineTarget(ctx);
        FindOnRunwayNode(ctx);

        _initialized = true;

        ctx.Logger.LogDebug(
            "[LineUp] {Callsign}: stage1={HasStage1}, centerline ({CLat:F6}, {CLon:F6}), rwy hdg {Hdg:F0}",
            ctx.Aircraft.Callsign,
            _hasStage1,
            _centerlineLat,
            _centerlineLon,
            _runwayHeading
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_initialized)
        {
            return true;
        }

        // Stage 1: navigate to on-runway node
        if (_hasStage1 && !_stage1Complete)
        {
            double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _stage1Lat, _stage1Lon);

            if (dist > OnRunwayNodeThresholdNm)
            {
                NavigateToTarget(ctx, _stage1Lat, _stage1Lon);
                LogPeriodic(ctx);
                return false;
            }

            _stage1Complete = true;

            // Recompute centerline target from current position — the on-runway node
            // may be at a different along-track position than the initial hold-short.
            // Without this, Stage 2 would navigate back toward the threshold.
            ComputeCenterlineTarget(ctx);

            ctx.Logger.LogDebug("[LineUp] {Callsign}: reached on-runway node, correcting to centerline", ctx.Aircraft.Callsign);
        }

        // Stage 2: navigate to centerline projection
        if (!_aligningOnly)
        {
            double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _centerlineLat, _centerlineLon);

            if (dist > CenterlineArrivalThresholdNm)
            {
                NavigateToTarget(ctx, _centerlineLat, _centerlineLon);
                LogPeriodic(ctx);
                return false;
            }

            _aligningOnly = true;
        }

        // Align with runway heading
        double headingDiff = _runwayHeading.AbsAngleTo(ctx.Aircraft.TrueHeading);

        if (headingDiff > HeadingToleranceDeg)
        {
            double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
            ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, _runwayHeading.Degrees, maxTurn);
            AdjustSpeed(ctx, CategoryPerformance.TaxiSpeed(ctx.Category) * 0.5);
            LogPeriodic(ctx);
            return false;
        }

        // Aligned — snap heading and stop
        ctx.Aircraft.TrueHeading = _runwayHeading;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;

        ctx.Logger.LogDebug("[LineUp] {Callsign}: aligned on runway, heading {Hdg:F0}", ctx.Aircraft.Callsign, _runwayHeading.Degrees);
        return true;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.CancelTakeoffClearance => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    private void LogPeriodic(PhaseContext ctx)
    {
        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            double clDist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _centerlineLat, _centerlineLon);
            double hdgDiff = _runwayHeading.AbsAngleTo(ctx.Aircraft.TrueHeading);
            ctx.Logger.LogDebug(
                "[LineUp] {Callsign}: clDist={Dist:F4}nm, hdgDiff={Diff:F1}, gs={Gs:F1}kts",
                ctx.Aircraft.Callsign,
                clDist,
                hdgDiff,
                ctx.Aircraft.GroundSpeed
            );
        }
    }

    private void ComputeCenterlineTarget(PhaseContext ctx)
    {
        double threshLat = ctx.Runway!.ThresholdLatitude;
        double threshLon = ctx.Runway.ThresholdLongitude;

        double along = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, threshLat, threshLon, _runwayHeading);

        // Bias the target forward along the runway so the aircraft curves
        // toward the centerline instead of turning in place. Without this,
        // TurnHeadingToward may pick the wrong 180° turn direction when the
        // hold-short heading is perpendicular to the runway, causing brief
        // backtracking toward the threshold.
        double crossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, threshLat, threshLon, _runwayHeading)
        );
        along += crossTrack + 0.02;

        var target = GeoMath.ProjectPoint(threshLat, threshLon, _runwayHeading, along);
        _centerlineLat = target.Lat;
        _centerlineLon = target.Lon;
    }

    private void FindOnRunwayNode(PhaseContext ctx)
    {
        if (_holdShortNodeId is not { } nodeId)
        {
            return;
        }

        if (ctx.GroundLayout is not { } layout)
        {
            return;
        }

        if (!layout.Nodes.TryGetValue(nodeId, out var holdShortNode))
        {
            return;
        }

        double holdShortAlong = GeoMath.AlongTrackDistanceNm(
            holdShortNode.Latitude,
            holdShortNode.Longitude,
            ctx.Runway!.ThresholdLatitude,
            ctx.Runway.ThresholdLongitude,
            _runwayHeading
        );

        double holdShortCrossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(
                holdShortNode.Latitude,
                holdShortNode.Longitude,
                ctx.Runway.ThresholdLatitude,
                ctx.Runway.ThresholdLongitude,
                _runwayHeading
            )
        );

        GroundNode? bestNeighbor = null;
        double bestCrossTrack = holdShortCrossTrack;

        foreach (var edge in holdShortNode.Edges)
        {
            int neighborId = edge.FromNodeId == nodeId ? edge.ToNodeId : edge.FromNodeId;

            if (!layout.Nodes.TryGetValue(neighborId, out var neighbor))
            {
                continue;
            }

            // Skip neighbors that are behind the hold-short along the runway.
            // Navigating backward toward the threshold is never correct for line-up.
            double neighborAlong = GeoMath.AlongTrackDistanceNm(
                neighbor.Latitude,
                neighbor.Longitude,
                ctx.Runway.ThresholdLatitude,
                ctx.Runway.ThresholdLongitude,
                _runwayHeading
            );

            if (neighborAlong < holdShortAlong - 0.005)
            {
                continue;
            }

            double crossTrack = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    neighbor.Latitude,
                    neighbor.Longitude,
                    ctx.Runway.ThresholdLatitude,
                    ctx.Runway.ThresholdLongitude,
                    _runwayHeading
                )
            );

            if (crossTrack < bestCrossTrack)
            {
                bestCrossTrack = crossTrack;
                bestNeighbor = neighbor;
            }
        }

        if (bestNeighbor is not null)
        {
            _stage1Lat = bestNeighbor.Latitude;
            _stage1Lon = bestNeighbor.Longitude;
            _hasStage1 = true;

            ctx.Logger.LogDebug(
                "[LineUp] {Callsign}: on-runway node {NodeId} at ({Lat:F6}, {Lon:F6})",
                ctx.Aircraft.Callsign,
                bestNeighbor.Id,
                _stage1Lat,
                _stage1Lon
            );
        }
    }

    private void NavigateToTarget(PhaseContext ctx, double targetLat, double targetLon)
    {
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, targetLat, targetLon);

        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        double diff = ctx.Aircraft.TrueHeading.SignedAngleTo(new TrueHeading(bearing));

        if (Math.Abs(diff) > 90)
        {
            // Large turn needed — prefer the direction that passes through the
            // runway heading first, avoiding backtracking toward the threshold.
            double rwyDiff = ctx.Aircraft.TrueHeading.SignedAngleTo(_runwayHeading);
            double turnDir = rwyDiff >= 0 ? 1.0 : -1.0;
            ctx.Aircraft.TrueHeading = new TrueHeading(ctx.Aircraft.TrueHeading.Degrees + turnDir * maxTurn);
        }
        else
        {
            ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);
        }

        // Near-normal taxi speed, reduced during sharp turns
        double angleDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearing));
        double maxSpeed = CategoryPerformance.TaxiSpeed(ctx.Category) * 0.8;
        double speedFraction = Math.Clamp(1.0 - (angleDiff / 120.0), 0.4, 1.0);
        AdjustSpeed(ctx, maxSpeed * speedFraction);
    }

    private static void AdjustSpeed(PhaseContext ctx, double targetSpeed)
    {
        double current = ctx.Aircraft.IndicatedAirspeed;
        if (current < targetSpeed)
        {
            double rate = CategoryPerformance.TaxiAccelRate(ctx.Category);
            ctx.Aircraft.IndicatedAirspeed = Math.Min(targetSpeed, current + rate * ctx.DeltaSeconds);
        }
        else if (current > targetSpeed)
        {
            double rate = CategoryPerformance.TaxiDecelRate(ctx.Category);
            ctx.Aircraft.IndicatedAirspeed = Math.Max(targetSpeed, current - rate * ctx.DeltaSeconds);
        }
    }
}
