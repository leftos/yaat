using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// After landing rollout completes, rolls the aircraft forward along runway
/// centerline edges until a hold-short exit is found, then follows the graph
/// edges off the runway and stops at the hold-short node.
///
/// States:
///   RollingOnCenterline — following RWY edges forward, checking for exits
///   TurningOff — following taxiway edges from centerline to hold-short node
/// </summary>
public sealed class RunwayExitPhase : Phase
{
    private const double ArrivalThresholdNm = 0.015;
    private const double LogIntervalSeconds = 3.0;

    private enum ExitState
    {
        RollingOnCenterline,
        TurningOff,
    }

    private ExitState _state = ExitState.RollingOnCenterline;
    private string? _runwayId;
    private TrueHeading _runwayHeading;
    private ExitPreference? _lastResolvedPreference;
    private double _exitSpeed;
    private double _timeSinceLastLog;

    // Centerline walking state
    private GroundNode? _currentCenterlineNode;
    private GroundNode? _nextCenterlineNode;

    // Exit target (set when a hold-short is found)
    private GroundNode? _holdShortNode;
    private string? _exitTaxiway;

    public override string Name => "Runway Exit";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        _runwayId = ctx.Aircraft.Phases?.AssignedRunway?.Designator;
        _runwayHeading = ctx.Aircraft.TrueHeading;
        _lastResolvedPreference = ctx.Aircraft.Phases?.RequestedExit;
        _exitSpeed = CategoryPerformance.RolloutCoastSpeed(ctx.Category);

        if (ctx.GroundLayout is null)
        {
            ctx.Logger.LogDebug("[Exit] {Callsign}: no ground layout, will stop immediately", ctx.Aircraft.Callsign);
            return;
        }

        _currentCenterlineNode = ctx.GroundLayout.FindNearestCenterlineNode(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _runwayHeading, _runwayId);
        if (_currentCenterlineNode is null)
        {
            ctx.Logger.LogDebug("[Exit] {Callsign}: no centerline node found", ctx.Aircraft.Callsign);
            return;
        }

        _nextCenterlineNode = ctx.GroundLayout.FindCenterlineNeighborAhead(_currentCenterlineNode, _runwayHeading, _runwayId);
        TryFindHoldShort(ctx);

        ctx.Logger.LogDebug(
            "[Exit] {Callsign}: rwy {Rwy}, centerline={CNode}, next={NNode}, holdShort={HS}",
            ctx.Aircraft.Callsign,
            _runwayId ?? "?",
            _currentCenterlineNode.Id,
            _nextCenterlineNode?.Id.ToString() ?? "none",
            _holdShortNode?.Id.ToString() ?? "none"
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Re-check preference if changed mid-phase
        var currentPref = ctx.Aircraft.Phases?.RequestedExit;
        if (currentPref != _lastResolvedPreference && _state == ExitState.RollingOnCenterline)
        {
            _lastResolvedPreference = currentPref;
            _holdShortNode = null;
            _exitTaxiway = null;
            TryFindHoldShort(ctx);
        }

        return _state switch
        {
            ExitState.RollingOnCenterline => TickRolling(ctx),
            ExitState.TurningOff => TickTurningOff(ctx),
            _ => true,
        };
    }

    private bool TickRolling(PhaseContext ctx)
    {
        if (_holdShortNode is not null)
        {
            _state = ExitState.TurningOff;
            ctx.Logger.LogDebug(
                "[Exit] {Callsign}: turning off toward hold-short {NodeId} on {Twy}",
                ctx.Aircraft.Callsign,
                _holdShortNode.Id,
                _exitTaxiway ?? "?"
            );
            return TickTurningOff(ctx);
        }

        if (_nextCenterlineNode is null)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Logger.LogDebug("[Exit] {Callsign}: end of runway centerline, stopping", ctx.Aircraft.Callsign);
            return true;
        }

        // Maintain rollout coast speed while searching for exit
        AdjustSpeed(ctx, _exitSpeed);
        ctx.Targets.TargetSpeed = _exitSpeed;

        // Steer toward next centerline node
        double bearing = GeoMath.BearingTo(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            _nextCenterlineNode.Latitude,
            _nextCenterlineNode.Longitude
        );
        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);

        // Check arrival at next centerline node
        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _nextCenterlineNode.Latitude, _nextCenterlineNode.Longitude);

        if (dist <= ArrivalThresholdNm)
        {
            ctx.Aircraft.Latitude = _nextCenterlineNode.Latitude;
            ctx.Aircraft.Longitude = _nextCenterlineNode.Longitude;

            _currentCenterlineNode = _nextCenterlineNode;
            _nextCenterlineNode = ctx.GroundLayout?.FindCenterlineNeighborAhead(_currentCenterlineNode, _runwayHeading, _runwayId);

            TryFindHoldShort(ctx);
        }

        LogPeriodic(ctx, dist);
        return false;
    }

    private bool TickTurningOff(PhaseContext ctx)
    {
        if (_holdShortNode is null)
        {
            return true;
        }

        // Decelerate toward zero — stop at the hold-short node
        AdjustSpeed(ctx, 0);
        ctx.Targets.TargetSpeed = 0;

        // Keep minimum speed to actually reach the node
        if (ctx.Aircraft.IndicatedAirspeed < 5)
        {
            ctx.Aircraft.IndicatedAirspeed = 5;
        }

        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _holdShortNode.Latitude, _holdShortNode.Longitude);
        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _holdShortNode.Latitude, _holdShortNode.Longitude);

        if (dist <= ArrivalThresholdNm)
        {
            ctx.Aircraft.Latitude = _holdShortNode.Latitude;
            ctx.Aircraft.Longitude = _holdShortNode.Longitude;
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Aircraft.CurrentTaxiway = _exitTaxiway;
            return true;
        }

        LogPeriodic(ctx, dist);
        return false;
    }

    private void TryFindHoldShort(PhaseContext ctx)
    {
        if (_currentCenterlineNode is null || ctx.GroundLayout is null)
        {
            return;
        }

        var result = ctx.GroundLayout.FindAdjacentHoldShort(_currentCenterlineNode, _runwayId, _runwayHeading, _lastResolvedPreference);

        if (result is null)
        {
            return;
        }

        // Check exit angle — skip exits requiring >90° turn if more runway ahead
        double? exitAngle = ctx.GroundLayout.ComputeExitAngle(result.Value.Node, result.Value.Taxiway, _runwayHeading);
        bool hasMoreRunway = _nextCenterlineNode is not null;
        bool isExplicitPreference = (_lastResolvedPreference?.Taxiway is not null) || (_lastResolvedPreference?.Side is not null);

        if ((exitAngle is not null) && (exitAngle.Value > 90) && hasMoreRunway && !isExplicitPreference)
        {
            ctx.Logger.LogDebug(
                "[Exit] {Callsign}: skipping {Twy} (angle={Angle:F0}° > 90°, more runway ahead)",
                ctx.Aircraft.Callsign,
                result.Value.Taxiway,
                exitAngle.Value
            );
            return;
        }

        _holdShortNode = result.Value.Node;
        _exitTaxiway = result.Value.Taxiway;
        _exitSpeed = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, exitAngle);
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        ctx.Logger.LogDebug("[Exit] {Callsign}: OnEnd ({Status}), taxiway={Twy}", ctx.Aircraft.Callsign, endStatus, _exitTaxiway ?? "none");

        if (endStatus == PhaseStatus.Completed)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;

            // Face away from the runway
            if (_holdShortNode is not null && _exitTaxiway is not null && ctx.GroundLayout is not null)
            {
                double? awayBearing = FindBearingAwayFromRunway(ctx.GroundLayout, _holdShortNode);
                if (awayBearing is not null)
                {
                    ctx.Aircraft.TrueHeading = new TrueHeading(awayBearing.Value);
                }
            }

            string rwy = _runwayId ?? "unknown";
            string taxiway = _exitTaxiway ?? "taxiway";
            ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} clear of runway {rwy} at {taxiway}");
        }
    }

    /// <summary>
    /// Find the bearing along an edge at the hold-short node that leads AWAY
    /// from the runway (neighbor is not on the runway centerline).
    /// </summary>
    private static double? FindBearingAwayFromRunway(AirportGroundLayout layout, GroundNode holdShortNode)
    {
        foreach (var edge in holdShortNode.Edges)
        {
            if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int neighborId = edge.FromNodeId == holdShortNode.Id ? edge.ToNodeId : edge.FromNodeId;
            if (!layout.Nodes.TryGetValue(neighborId, out var neighbor))
            {
                continue;
            }

            // Skip neighbors that are on the runway centerline (back toward the runway)
            bool neighborOnRunway = false;
            foreach (var nEdge in neighbor.Edges)
            {
                if (nEdge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    neighborOnRunway = true;
                    break;
                }
            }

            if (!neighborOnRunway)
            {
                return GeoMath.BearingTo(holdShortNode.Latitude, holdShortNode.Longitude, neighbor.Latitude, neighbor.Longitude);
            }
        }

        return null;
    }

    private static void AdjustSpeed(PhaseContext ctx, double targetSpeed)
    {
        if (ctx.Aircraft.IndicatedAirspeed > targetSpeed)
        {
            double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
            ctx.Aircraft.IndicatedAirspeed = Math.Max(targetSpeed, ctx.Aircraft.IndicatedAirspeed - decelRate * ctx.DeltaSeconds);
        }
        else if (ctx.Aircraft.IndicatedAirspeed < targetSpeed)
        {
            double accelRate = CategoryPerformance.TaxiAccelRate(ctx.Category);
            ctx.Aircraft.IndicatedAirspeed = Math.Min(targetSpeed, ctx.Aircraft.IndicatedAirspeed + accelRate * ctx.DeltaSeconds);
        }
    }

    private void LogPeriodic(PhaseContext ctx, double dist)
    {
        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            ctx.Logger.LogDebug(
                "[Exit] {Callsign}: {State}, dist={Dist:F4}nm, gs={Gs:F1}kts, hdg={Hdg:F0}",
                ctx.Aircraft.Callsign,
                _state,
                dist,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.TrueHeading.Degrees
            );
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

    public override PhaseDto ToSnapshot() =>
        new RunwayExitPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            ExitNodeId = _holdShortNode?.Id,
            ClearNodeId = null,
            ReachedExitNode = _state == ExitState.TurningOff,
            ExitTaxiway = _exitTaxiway,
            RunwayId = _runwayId,
            LastResolvedPreference = (int?)_lastResolvedPreference?.Side,
            ExitSpeed = _exitSpeed,
            TimeSinceLastLog = _timeSinceLastLog,
            StoppedForLahso = false,
            CurrentCenterlineNodeId = _currentCenterlineNode?.Id,
            NextCenterlineNodeId = _nextCenterlineNode?.Id,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            ExitStateValue = (int)_state,
        };

    public static RunwayExitPhase FromSnapshot(RunwayExitPhaseDto dto, AirportGroundLayout? groundLayout)
    {
        var phase = new RunwayExitPhase();
        phase._exitTaxiway = dto.ExitTaxiway;
        phase._runwayId = dto.RunwayId;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._state = (ExitState)dto.ExitStateValue;
        phase._lastResolvedPreference = dto.LastResolvedPreference.HasValue
            ? new ExitPreference { Side = (ExitSide)dto.LastResolvedPreference.Value }
            : null;
        phase._exitSpeed = dto.ExitSpeed;
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);

        if (groundLayout is not null)
        {
            if (dto.CurrentCenterlineNodeId.HasValue)
            {
                phase._currentCenterlineNode = groundLayout.Nodes.GetValueOrDefault(dto.CurrentCenterlineNodeId.Value);
            }

            if (dto.NextCenterlineNodeId.HasValue)
            {
                phase._nextCenterlineNode = groundLayout.Nodes.GetValueOrDefault(dto.NextCenterlineNodeId.Value);
            }

            if (dto.ExitNodeId.HasValue)
            {
                phase._holdShortNode = groundLayout.Nodes.GetValueOrDefault(dto.ExitNodeId.Value);
            }
        }

        return phase;
    }
}
