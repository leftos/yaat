using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft pushes back from parking at pushback speed.
/// Three modes:
///   1. No target: push straight back ~80 feet.
///   2. Heading only: push back while rotating to target heading.
///   3. Target position (taxiway): move toward target, then optionally rotate to heading.
/// </summary>
public sealed class PushbackPhase : Phase
{
    private const double DefaultPushbackDistanceNm = 0.015;
    private const double TargetReachedThresholdNm = 0.005;

    private double _startLat;
    private double _startLon;
    private double _pushbackHeading;
    private bool _reachedTarget;

    public int? TargetHeading { get; init; }
    public double? TargetLatitude { get; init; }
    public double? TargetLongitude { get; init; }

    public override string Name => "Pushback";

    public override void OnStart(PhaseContext ctx)
    {
        _startLat = ctx.Aircraft.Latitude;
        _startLon = ctx.Aircraft.Longitude;
        _pushbackHeading = (ctx.Aircraft.Heading + 180.0) % 360.0;

        ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
        ctx.Aircraft.IsOnGround = true;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.IsHeld)
        {
            ctx.Aircraft.GroundSpeed = 0;
            return false;
        }

        double pushSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
        double turnRate = CategoryPerformance.GroundTurnRate(ctx.Category);

        if (TargetLatitude is not null && TargetLongitude is not null)
        {
            return TickTargetedPushback(ctx, pushSpeed, turnRate);
        }

        return TickSimplePushback(ctx, pushSpeed, turnRate);
    }

    private bool TickTargetedPushback(PhaseContext ctx, double pushSpeed, double turnRate)
    {
        if (!_reachedTarget)
        {
            double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLatitude!.Value, TargetLongitude!.Value);
            double distThisTick = pushSpeed / 3600.0 * ctx.DeltaSeconds;

            if (dist <= distThisTick + TargetReachedThresholdNm)
            {
                ctx.Aircraft.Latitude = TargetLatitude!.Value;
                ctx.Aircraft.Longitude = TargetLongitude!.Value;
                ctx.Aircraft.GroundSpeed = 0;
                _reachedTarget = true;
            }
            else
            {
                MoveToward(ctx, TargetLatitude!.Value, TargetLongitude!.Value, distThisTick);
                ctx.Aircraft.GroundSpeed = pushSpeed;
            }

            if (TargetHeading is { } tgt && !_reachedTarget)
            {
                RotateHeadingToward(ctx, tgt, turnRate);
            }
        }

        if (!_reachedTarget)
        {
            return false;
        }

        if (TargetHeading is not { } finalHdg)
        {
            return true;
        }

        return RotateHeadingToward(ctx, finalHdg, CategoryPerformance.GroundTurnRate(ctx.Category));
    }

    private bool TickSimplePushback(PhaseContext ctx, double pushSpeed, double turnRate)
    {
        MoveInDirection(ctx, _pushbackHeading, pushSpeed);

        if (TargetHeading is { } tgt)
        {
            return RotateHeadingToward(ctx, tgt, turnRate);
        }

        double distPushed = GeoMath.DistanceNm(_startLat, _startLon, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
        return distPushed >= DefaultPushbackDistanceNm;
    }

    private static void MoveToward(PhaseContext ctx, double targetLat, double targetLon, double distThisTick)
    {
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, targetLat, targetLon);
        double rad = bearing * Math.PI / 180.0;
        double latRad = ctx.Aircraft.Latitude * Math.PI / 180.0;
        ctx.Aircraft.Latitude += distThisTick * Math.Cos(rad) / 60.0;
        ctx.Aircraft.Longitude += distThisTick * Math.Sin(rad) / (60.0 * Math.Cos(latRad));
    }

    private static void MoveInDirection(PhaseContext ctx, double direction, double speed)
    {
        double distThisTick = speed / 3600.0 * ctx.DeltaSeconds;
        double rad = direction * Math.PI / 180.0;
        double latRad = ctx.Aircraft.Latitude * Math.PI / 180.0;
        ctx.Aircraft.Latitude += distThisTick * Math.Cos(rad) / 60.0;
        ctx.Aircraft.Longitude += distThisTick * Math.Sin(rad) / (60.0 * Math.Cos(latRad));
        ctx.Aircraft.GroundSpeed = speed;
    }

    private static bool RotateHeadingToward(PhaseContext ctx, double target, double turnRate)
    {
        double current = ctx.Aircraft.Heading;
        double diff = target - current;
        while (diff > 180)
        {
            diff -= 360;
        }

        while (diff < -180)
        {
            diff += 360;
        }

        double maxTurn = turnRate * ctx.DeltaSeconds;
        if (Math.Abs(diff) <= maxTurn)
        {
            ctx.Aircraft.Heading = target;
            return true;
        }

        ctx.Aircraft.Heading = (current + Math.Sign(diff) * maxTurn + 360) % 360;
        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        ctx.Aircraft.GroundSpeed = 0;
        ctx.Targets.TargetSpeed = 0;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }
}
