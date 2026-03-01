using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// After landing rollout completes, steers the aircraft off the runway
/// onto the nearest taxiway exit. Generates a "clear of runway" notification
/// on completion.
/// </summary>
public sealed class RunwayExitPhase : Phase
{
    private const double ArrivalThresholdNm = 0.005;

    private GroundNode? _exitNode;
    private string? _exitTaxiway;
    private string? _runwayId;

    public override string Name => "Runway Exit";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;

        _runwayId = ctx.Aircraft.Phases?.AssignedRunway?.RunwayId;

        // Find the nearest exit using the ground layout
        if (ctx.GroundLayout is null)
        {
            return;
        }

        double heading = ctx.Aircraft.Heading;
        _exitNode = ctx.GroundLayout.FindNearestExit(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, heading);

        if (_exitNode is not null)
        {
            _exitTaxiway = ctx.GroundLayout.GetExitTaxiwayName(_exitNode);
        }
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_exitNode is null)
        {
            // No ground layout or no exit found — just stop
            ctx.Aircraft.GroundSpeed = 0;
            return true;
        }

        double exitSpeed = CategoryPerformance.RunwayExitSpeed(ctx.Category);

        // Decelerate to exit speed if faster
        if (ctx.Aircraft.GroundSpeed > exitSpeed)
        {
            double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
            ctx.Aircraft.GroundSpeed = Math.Max(
                exitSpeed,
                ctx.Aircraft.GroundSpeed - decelRate * ctx.DeltaSeconds);
        }
        else if (ctx.Aircraft.GroundSpeed < exitSpeed)
        {
            double accelRate = CategoryPerformance.TaxiAccelRate(ctx.Category);
            ctx.Aircraft.GroundSpeed = Math.Min(
                exitSpeed,
                ctx.Aircraft.GroundSpeed + accelRate * ctx.DeltaSeconds);
        }

        ctx.Targets.TargetSpeed = exitSpeed;

        // Turn toward exit node
        double bearing = GeoMath.BearingTo(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _exitNode.Latitude, _exitNode.Longitude);
        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category)
            * ctx.DeltaSeconds;
        ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(
            ctx.Aircraft.Heading, bearing, maxTurn);

        // Check arrival
        double dist = GeoMath.DistanceNm(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _exitNode.Latitude, _exitNode.Longitude);

        if (dist <= ArrivalThresholdNm)
        {
            ctx.Aircraft.CurrentTaxiway = _exitTaxiway;
            return true;
        }

        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        if (endStatus == PhaseStatus.Completed)
        {
            ctx.Aircraft.GroundSpeed = 0;
            ctx.Targets.TargetSpeed = 0;

            // Generate "clear of runway" notification
            string rwy = _runwayId ?? "unknown";
            string taxiway = _exitTaxiway ?? "taxiway";
            ctx.Aircraft.PendingWarnings.Add(
                $"{ctx.Aircraft.Callsign} clear of runway {rwy} at {taxiway}");
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }
}
