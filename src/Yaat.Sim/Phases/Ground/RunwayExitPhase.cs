using Microsoft.Extensions.Logging;
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
    private const double LogIntervalSeconds = 3.0;

    private GroundNode? _exitNode;
    private GroundNode? _clearNode;
    private bool _reachedExitNode;
    private string? _exitTaxiway;
    private string? _runwayId;
    private ExitPreference? _lastResolvedPreference;
    private double _timeSinceLastLog;

    public override string Name => "Runway Exit";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;

        _runwayId = ctx.Aircraft.Phases?.AssignedRunway?.Designator;

        if (ctx.GroundLayout is null)
        {
            ctx.Logger.LogDebug("[Exit] {Callsign}: no ground layout, will stop immediately", ctx.Aircraft.Callsign);
            return;
        }

        ResolveExit(ctx);

        ctx.Logger.LogDebug(
            "[Exit] {Callsign}: exiting rwy {Rwy}, exit node={NodeId}, clear node={ClearId}, taxiway={Twy}",
            ctx.Aircraft.Callsign,
            _runwayId ?? "?",
            _exitNode?.Id.ToString() ?? "none",
            _clearNode?.Id.ToString() ?? "none",
            _exitTaxiway ?? "none"
        );
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
            ctx.Aircraft.GroundSpeed = Math.Max(exitSpeed, ctx.Aircraft.GroundSpeed - decelRate * ctx.DeltaSeconds);
        }
        else if (ctx.Aircraft.GroundSpeed < exitSpeed)
        {
            double accelRate = CategoryPerformance.TaxiAccelRate(ctx.Category);
            ctx.Aircraft.GroundSpeed = Math.Min(exitSpeed, ctx.Aircraft.GroundSpeed + accelRate * ctx.DeltaSeconds);
        }

        ctx.Targets.TargetSpeed = exitSpeed;

        // Determine current target: exit node first, then clear node
        var target = _reachedExitNode && _clearNode is not null ? _clearNode : _exitNode;

        // Turn toward target node
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, target.Latitude, target.Longitude);
        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(ctx.Aircraft.Heading, bearing, maxTurn);

        // Check arrival — use a capture radius that accounts for the distance
        // the aircraft can travel in one tick to prevent overshooting at low speeds.
        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, target.Latitude, target.Longitude);
        double perTickNm = ctx.Aircraft.GroundSpeed / 3600.0 * ctx.DeltaSeconds;
        double captureRadius = Math.Max(ArrivalThresholdNm, perTickNm * 1.5);

        if (dist <= captureRadius)
        {
            // Snap position to the target node to prevent drift/overshoot
            ctx.Aircraft.Latitude = target.Latitude;
            ctx.Aircraft.Longitude = target.Longitude;
            ctx.Aircraft.Heading = bearing;

            if (!_reachedExitNode)
            {
                _reachedExitNode = true;
                if (_clearNode is not null)
                {
                    // Continue to the clear node
                    return false;
                }
            }

            ctx.Aircraft.CurrentTaxiway = _exitTaxiway;
            return true;
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            ctx.Logger.LogDebug(
                "[Exit] {Callsign}: dist={Dist:F4}nm, gs={Gs:F1}kts, hdg={Hdg:F0}",
                ctx.Aircraft.Callsign,
                dist,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.Heading
            );
        }

        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        ctx.Logger.LogDebug("[Exit] {Callsign}: OnEnd ({Status}), taxiway={Twy}", ctx.Aircraft.Callsign, endStatus, _exitTaxiway ?? "none");

        if (endStatus == PhaseStatus.Completed)
        {
            ctx.Aircraft.GroundSpeed = 0;
            ctx.Targets.TargetSpeed = 0;

            // Align heading along the taxiway so the aircraft faces a sensible direction
            var stopNode = _reachedExitNode && _clearNode is not null ? _clearNode : _exitNode;
            if (stopNode is not null && _exitTaxiway is not null && ctx.GroundLayout is not null)
            {
                var taxiwayHdg = ctx.GroundLayout.GetEdgeHeadingForTaxiway(stopNode, _exitTaxiway, ctx.Aircraft.Heading);
                if (taxiwayHdg is not null)
                {
                    ctx.Aircraft.Heading = taxiwayHdg.Value;
                }
            }

            // Generate "clear of runway" notification
            string rwy = _runwayId ?? "unknown";
            string taxiway = _exitTaxiway ?? "taxiway";
            ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} clear of runway {rwy} at {taxiway}");
        }
    }

    private void ResolveExit(PhaseContext ctx)
    {
        var requested = ctx.Aircraft.Phases?.RequestedExit;
        _lastResolvedPreference = requested;
        _reachedExitNode = false;
        double heading = ctx.Aircraft.Heading;

        if (requested?.Taxiway is { } taxiway)
        {
            _exitNode = ctx.GroundLayout!.FindExitByTaxiway(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, taxiway);
        }
        else if (requested?.Side is { } side)
        {
            _exitNode = ctx.GroundLayout!.FindExitBySide(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, heading, side);
        }
        else
        {
            _exitNode = ctx.GroundLayout!.FindNearestExit(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, heading);
        }

        _exitTaxiway = _exitNode is not null ? ctx.GroundLayout!.GetExitTaxiwayName(_exitNode) : null;

        // Find the next node along the taxiway past the exit intersection so the
        // aircraft rolls clear of the runway surface, not just to the boundary node.
        _clearNode = null;
        if (_exitNode is not null && _exitTaxiway is not null)
        {
            _clearNode = ctx.GroundLayout!.FindClearNode(_exitNode, _exitTaxiway, heading);
        }
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
