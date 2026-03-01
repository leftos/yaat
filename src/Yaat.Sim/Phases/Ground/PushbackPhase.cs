using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft pushes back from parking at ~5 kts in reverse.
/// Pushback direction is opposite the parking heading (nose-in).
/// Completes after the aircraft has pushed back a short distance
/// or reaches a target heading.
/// </summary>
public sealed class PushbackPhase : Phase
{
    private const double DefaultPushbackDistanceNm = 0.015;

    private double _startLat;
    private double _startLon;
    private double _pushbackHeading;
    private int? _targetHeading;

    /// <summary>
    /// Optional target heading the aircraft should face when pushback completes.
    /// If null, pushes back a default distance.
    /// </summary>
    public int? TargetHeading
    {
        get => _targetHeading;
        init => _targetHeading = value;
    }

    public override string Name => "Pushback";

    public override void OnStart(PhaseContext ctx)
    {
        _startLat = ctx.Aircraft.Latitude;
        _startLon = ctx.Aircraft.Longitude;

        // Push back in the direction opposite the aircraft's current heading
        _pushbackHeading = (ctx.Aircraft.Heading + 180.0) % 360.0;

        double pushSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
        ctx.Targets.TargetSpeed = pushSpeed;
        ctx.Aircraft.IsOnGround = true;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double pushSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
        double turnRate = CategoryPerformance.GroundTurnRate(ctx.Category);

        // Move the aircraft backward (heading stays nose-forward,
        // position moves opposite to heading)
        double speedNmPerSec = pushSpeed / 3600.0;
        double distThisTick = speedNmPerSec * ctx.DeltaSeconds;

        double pushRad = _pushbackHeading * Math.PI / 180.0;
        double latRad = ctx.Aircraft.Latitude * Math.PI / 180.0;
        double nmPerDegLat = 60.0;

        ctx.Aircraft.Latitude += distThisTick * Math.Cos(pushRad) / nmPerDegLat;
        ctx.Aircraft.Longitude +=
            distThisTick * Math.Sin(pushRad) / (nmPerDegLat * Math.Cos(latRad));
        ctx.Aircraft.GroundSpeed = pushSpeed;

        // If we have a target heading, rotate the aircraft toward it
        if (_targetHeading is { } tgt)
        {
            double current = ctx.Aircraft.Heading;
            double diff = tgt - current;
            while (diff > 180) diff -= 360;
            while (diff < -180) diff += 360;

            double maxTurn = turnRate * ctx.DeltaSeconds;
            if (Math.Abs(diff) <= maxTurn)
            {
                ctx.Aircraft.Heading = tgt;
                return true;
            }

            ctx.Aircraft.Heading = (current + Math.Sign(diff) * maxTurn + 360) % 360;
            return false;
        }

        // Default: push back a fixed distance
        double distPushed = GeoMath.DistanceNm(
            _startLat, _startLon,
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);

        return distPushed >= DefaultPushbackDistanceNm;
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
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }
}
