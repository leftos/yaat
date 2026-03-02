using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Taxis the aircraft from the hold-short point onto the runway centerline
/// and aligns with the runway heading. Completes when aligned.
/// Inserted before LinedUpAndWaitingPhase when LUAW/CTO is issued.
/// </summary>
public sealed class LineUpPhase : Phase
{
    private const double ArrivalThresholdNm = 0.008;
    private const double HeadingToleranceDeg = 2.0;

    private double _targetLat;
    private double _targetLon;
    private double _runwayHeading;
    private bool _initialized;
    private bool _aligningOnly;

    public override string Name => "LiningUp";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;

        if (ctx.Runway is null)
        {
            ctx.Logger.LogWarning(
                "[LineUp] {Callsign}: no runway context, skipping",
                ctx.Aircraft.Callsign);
            return;
        }

        _runwayHeading = ctx.Runway.TrueHeading;

        // Compute target: project aircraft position onto runway centerline.
        // This gives the point on the centerline nearest to the aircraft.
        double headingRad = _runwayHeading * Math.PI / 180.0;
        double threshLat = ctx.Runway.ThresholdLatitude;
        double threshLon = ctx.Runway.ThresholdLongitude;

        double along = GeoMath.AlongTrackDistanceNm(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            threshLat, threshLon, _runwayHeading);

        double latRad = threshLat * Math.PI / 180.0;
        _targetLat = threshLat
            + along * Math.Cos(headingRad) / 60.0;
        _targetLon = threshLon
            + along * Math.Sin(headingRad) / (60.0 * Math.Cos(latRad));

        _initialized = true;

        ctx.Logger.LogDebug(
            "[LineUp] {Callsign}: target ({TLat:F6}, {TLon:F6}), rwy hdg {Hdg:F0}",
            ctx.Aircraft.Callsign, _targetLat, _targetLon, _runwayHeading);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_initialized)
        {
            return true;
        }

        if (!_aligningOnly)
        {
            double dist = GeoMath.DistanceNm(
                ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
                _targetLat, _targetLon);

            if (dist > ArrivalThresholdNm)
            {
                NavigateToTarget(ctx, dist);
                return false;
            }

            // Arrived at centerline — switch to heading alignment
            _aligningOnly = true;
        }

        // Align with runway heading
        double headingDiff = Math.Abs(FlightPhysics.NormalizeAngle(
            _runwayHeading - ctx.Aircraft.Heading));

        if (headingDiff > HeadingToleranceDeg)
        {
            double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category)
                * ctx.DeltaSeconds;
            ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(
                ctx.Aircraft.Heading, _runwayHeading, maxTurn);
            AdjustSpeed(ctx, CategoryPerformance.TaxiSpeed(ctx.Category) * 0.2);
            return false;
        }

        // Aligned — snap heading and stop
        ctx.Aircraft.Heading = _runwayHeading;
        ctx.Aircraft.GroundSpeed = 0;
        ctx.Targets.TargetSpeed = 0;

        ctx.Logger.LogDebug(
            "[LineUp] {Callsign}: aligned on runway, heading {Hdg:F0}",
            ctx.Aircraft.Callsign, _runwayHeading);
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

    private void NavigateToTarget(PhaseContext ctx, double dist)
    {
        double bearing = GeoMath.BearingTo(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _targetLat, _targetLon);

        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category)
            * ctx.DeltaSeconds;
        ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(
            ctx.Aircraft.Heading, bearing, maxTurn);

        // Slow taxi speed, reduced during sharp turns
        double angleDiff = Math.Abs(FlightPhysics.NormalizeAngle(
            bearing - ctx.Aircraft.Heading));
        double maxSpeed = CategoryPerformance.TaxiSpeed(ctx.Category) * 0.5;
        double speedFraction = Math.Clamp(1.0 - (angleDiff / 90.0), 0.2, 1.0);
        AdjustSpeed(ctx, maxSpeed * speedFraction);
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
}
