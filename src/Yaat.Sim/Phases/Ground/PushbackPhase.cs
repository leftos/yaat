using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft pushes back from parking at pushback speed.
/// Sets AircraftState.PushbackHeading so FlightPhysics moves the aircraft
/// backward while the nose heading stays forward (or rotates to target).
/// Three modes:
///   1. No target: push straight back ~80 feet.
///   2. Heading only: push back along a curved arc while rotating nose to target heading.
///   3. Target position (taxiway): arc toward target, then optionally rotate to heading.
/// </summary>
public sealed class PushbackPhase : Phase
{
    private const double DefaultPushbackDistanceNm = 0.015;
    private const double TargetReachedThresholdNm = 0.005;
    private const double HeadingReachedDeg = 0.5;
    private const double LogIntervalSeconds = 3.0;

    private double _startLat;
    private double _startLon;
    private bool _reachedTarget;
    private double _timeSinceLastLog;

    public int? TargetHeading { get; init; }
    public double? TargetLatitude { get; init; }
    public double? TargetLongitude { get; init; }

    public override string Name => "Pushback";

    public override void OnStart(PhaseContext ctx)
    {
        _startLat = ctx.Aircraft.Latitude;
        _startLon = ctx.Aircraft.Longitude;

        ctx.Aircraft.PushbackHeading = (ctx.Aircraft.Heading + 180.0) % 360.0;
        ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
        ctx.Targets.TargetHeading = null;
        ctx.Aircraft.IsOnGround = true;

        ctx.Logger.LogDebug(
            "[Push] {Callsign}: started, pushHdg={PushHdg:F0}, noseHdg={NoseHdg:F0}, targetHdg={TargetHdg}, pos=({Lat:F6},{Lon:F6})",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.PushbackHeading,
            ctx.Aircraft.Heading,
            TargetHeading?.ToString() ?? "none",
            _startLat,
            _startLon
        );
        if (TargetLatitude is not null && TargetLongitude is not null)
        {
            ctx.Logger.LogDebug(
                "[Push] {Callsign}: target position ({TLat:F6},{TLon:F6})",
                ctx.Aircraft.Callsign,
                TargetLatitude.Value,
                TargetLongitude.Value
            );
        }
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.IsHeld)
        {
            ctx.Aircraft.GroundSpeed = 0;
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        // Continuously reassert desired speed so FlightPhysics.UpdateSpeed can ramp
        // back up after a GroundConflictDetector limit clears.
        ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);

        double turnRate = CategoryPerformance.PushbackTurnRate(ctx.Category);

        bool result;
        if (TargetLatitude is not null && TargetLongitude is not null)
        {
            result = TickTargetedPushback(ctx, turnRate);
        }
        else
        {
            result = TickSimplePushback(ctx, turnRate);
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (!result && _timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            double distPushed = GeoMath.DistanceNm(_startLat, _startLon, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
            ctx.Logger.LogDebug(
                "[Push] {Callsign}: dist={Dist:F4}nm, gs={Gs:F1}kts, pushHdg={PushHdg:F0}, noseHdg={NoseHdg:F0}, pos=({Lat:F6},{Lon:F6})",
                ctx.Aircraft.Callsign,
                distPushed,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.PushbackHeading ?? 0,
                ctx.Aircraft.Heading,
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude
            );
        }

        return result;
    }

    private bool TickTargetedPushback(PhaseContext ctx, double turnRate)
    {
        if (!_reachedTarget)
        {
            double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLatitude!.Value, TargetLongitude!.Value);

            if (dist <= TargetReachedThresholdNm)
            {
                ctx.Aircraft.Latitude = TargetLatitude!.Value;
                ctx.Aircraft.Longitude = TargetLongitude!.Value;
                ctx.Aircraft.GroundSpeed = 0;
                ctx.Targets.TargetSpeed = 0;
                _reachedTarget = true;
                ctx.Logger.LogDebug("[Push] {Callsign}: reached target position, rotating to heading", ctx.Aircraft.Callsign);
            }
            else
            {
                // Gradually steer PushbackHeading toward the target in an arc
                // (tug curving the tail toward the taxiway, not a straight-line slide).
                double bearingToTarget = GeoMath.BearingTo(
                    ctx.Aircraft.Latitude,
                    ctx.Aircraft.Longitude,
                    TargetLatitude!.Value,
                    TargetLongitude!.Value
                );
                double maxArcTurn = turnRate * ctx.DeltaSeconds;
                ctx.Aircraft.PushbackHeading = GeoMath.TurnHeadingToward(
                    ctx.Aircraft.PushbackHeading ?? (ctx.Aircraft.Heading + 180.0) % 360.0,
                    bearingToTarget,
                    maxArcTurn
                );
            }

            if (TargetHeading is { } tgt && !_reachedTarget)
            {
                TurnNoseToward(ctx, tgt, turnRate);
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

        return TurnNoseToward(ctx, finalHdg, turnRate);
    }

    private bool TickSimplePushback(PhaseContext ctx, double turnRate)
    {
        if (TargetHeading is { } tgt)
        {
            bool headingReached = TurnNoseToward(ctx, tgt, turnRate);

            // Couple pushback direction to nose after rotation: as the nose rotates, the arc curves.
            ctx.Aircraft.PushbackHeading = (ctx.Aircraft.Heading + 180.0) % 360.0;

            double distPushed = GeoMath.DistanceNm(_startLat, _startLon, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
            return headingReached && distPushed >= DefaultPushbackDistanceNm;
        }

        double dist = GeoMath.DistanceNm(_startLat, _startLon, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
        return dist >= DefaultPushbackDistanceNm;
    }

    private static bool TurnNoseToward(PhaseContext ctx, double target, double turnRate)
    {
        double maxTurn = turnRate * ctx.DeltaSeconds;
        ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(ctx.Aircraft.Heading, target, maxTurn);
        double diff = FlightPhysics.NormalizeAngle(target - ctx.Aircraft.Heading);
        return Math.Abs(diff) < HeadingReachedDeg;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        double distPushed = GeoMath.DistanceNm(_startLat, _startLon, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
        ctx.Logger.LogDebug(
            "[Push] {Callsign}: OnEnd ({Status}), total dist={Dist:F4}nm, hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            endStatus,
            distPushed,
            ctx.Aircraft.Heading
        );

        ctx.Aircraft.GroundSpeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Aircraft.PushbackHeading = null;
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
