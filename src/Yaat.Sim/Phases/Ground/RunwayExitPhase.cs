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
    private ExitPreference? _lastResolvedPreference;

    public override string Name => "Runway Exit";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;

        _runwayId = ctx.Aircraft.Phases?.AssignedRunway?.RunwayId;

        if (ctx.GroundLayout is null)
        {
            return;
        }

        ResolveExit(ctx);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Re-resolve if the controller changed the exit preference mid-phase
        var currentPref = ctx.Aircraft.Phases?.RequestedExit;
        if (currentPref != _lastResolvedPreference && ctx.GroundLayout is not null)
        {
            ResolveExit(ctx);
        }

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

    private void ResolveExit(PhaseContext ctx)
    {
        var requested = ctx.Aircraft.Phases?.RequestedExit;
        _lastResolvedPreference = requested;
        double heading = ctx.Aircraft.Heading;

        if (requested?.Taxiway is { } taxiway)
        {
            _exitNode = ctx.GroundLayout!.FindExitByTaxiway(
                ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, taxiway);
        }
        else if (requested?.Side is { } side)
        {
            _exitNode = ctx.GroundLayout!.FindExitBySide(
                ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, heading, side);
        }
        else
        {
            _exitNode = ctx.GroundLayout!.FindNearestExit(
                ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, heading);
        }

        _exitTaxiway = _exitNode is not null
            ? ctx.GroundLayout!.GetExitTaxiwayName(_exitNode)
            : null;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }
}
