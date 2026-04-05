using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// After landing rollout completes, rolls the aircraft forward along runway
/// centerline edges until a hold-short exit is found, then follows the exit
/// path using <see cref="GroundNavigator"/> with exit-appropriate speed.
///
/// States:
///   RollingOnCenterline — following RWY edges forward, checking for exits
///   FollowingExitPath — GroundNavigator follows exit taxiway edges to hold-short
/// </summary>
public sealed class RunwayExitPhase : Phase
{
    private const double CenterlineNodeArrivalNm = 0.015;
    private const double LogIntervalSeconds = 3.0;

    /// <summary>
    /// How far ahead (nm) to search the next centerline node for exits.
    /// This enables turn anticipation: the aircraft starts pre-turning before
    /// reaching the intersection, creating a smooth arc instead of a sharp snap.
    /// </summary>
    private const double LookAheadSearchNm = 0.10;

    private enum ExitState
    {
        RollingOnCenterline,
        FollowingExitPath,
    }

    private ExitState _state = ExitState.RollingOnCenterline;
    private string? _runwayId;
    private TrueHeading _runwayHeading;
    private ExitPreference? _lastResolvedPreference;
    private double _coastSpeed;
    private double _timeSinceLastLog;

    // Centerline walking state
    private GroundNode? _currentCenterlineNode;
    private GroundNode? _nextCenterlineNode;

    // Exit target (set when a hold-short is found)
    private GroundNode? _holdShortNode;
    private string? _exitTaxiway;

    // Full exit path including branch point: [branchNode, wp1, wp2, ..., holdShort]
    private List<GroundNode>? _exitPath;

    // Look-ahead: when an exit is found at the next centerline node, this stores
    // that node so we can compute the turn lead distance and start pre-turning.
    private GroundNode? _pendingBranchNode;

    // Exit path navigation (FollowingExitPath state)
    private TaxiRoute? _exitRoute;
    private GroundNavigator? _navigator;

    public override string Name => "Runway Exit";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        _runwayId = ctx.Aircraft.Phases?.AssignedRunway?.Designator;
        _runwayHeading = ctx.Aircraft.TrueHeading;
        _lastResolvedPreference = ctx.Aircraft.Phases?.RequestedExit;
        _coastSpeed = CategoryPerformance.RolloutCoastSpeed(ctx.Category);

        if (ctx.GroundLayout is null)
        {
            ctx.Logger.LogDebug("[Exit] {Callsign}: no ground layout, will stop immediately", ctx.Aircraft.Callsign);
            return;
        }

        // If LandingPhase committed to a specific exit, store the path.
        // Navigation starts on the first OnTick.
        if (ctx.Aircraft.Phases?.ResolvedExit is { } committed)
        {
            ctx.Aircraft.Phases.ResolvedExit = null;
            _holdShortNode = committed.HoldShortNode;
            _exitTaxiway = committed.TaxiwayName;
            _exitPath = committed.Path;

            ctx.Logger.LogDebug(
                "[Exit] {Callsign}: committed exit {Twy}, {PathCount} nodes to hold-short {HsId}",
                ctx.Aircraft.Callsign,
                _exitTaxiway,
                _exitPath.Count,
                _holdShortNode.Id
            );
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
        if (_state == ExitState.FollowingExitPath)
        {
            return TickFollowingExitPath(ctx);
        }

        // Re-check preference if changed mid-phase
        var currentPref = ctx.Aircraft.Phases?.RequestedExit;
        if (currentPref != _lastResolvedPreference && _holdShortNode is null)
        {
            _lastResolvedPreference = currentPref;
            _holdShortNode = null;
            _exitTaxiway = null;
            _exitPath = null;
            TryFindHoldShort(ctx);
        }

        // Exit found and no pending look-ahead — start following exit path
        if (_holdShortNode is not null && _pendingBranchNode is null && _state == ExitState.RollingOnCenterline)
        {
            if (StartExitNavigation(ctx))
            {
                return TickFollowingExitPath(ctx);
            }
            return true;
        }

        return TickRolling(ctx);
    }

    private bool TickRolling(PhaseContext ctx)
    {
        if (_nextCenterlineNode is null)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Logger.LogDebug("[Exit] {Callsign}: end of runway centerline, stopping", ctx.Aircraft.Callsign);
            return true;
        }

        double distToNext = GeoMath.DistanceNm(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            _nextCenterlineNode.Latitude,
            _nextCenterlineNode.Longitude
        );

        // Look-ahead: search the next centerline node for exits before arriving
        if (_holdShortNode is null && ctx.GroundLayout is not null && distToNext < LookAheadSearchNm)
        {
            TryFindHoldShortFromNode(ctx, _nextCenterlineNode);
            if (_holdShortNode is not null)
            {
                _pendingBranchNode = _nextCenterlineNode;
                ctx.Logger.LogDebug(
                    "[Exit] {Callsign}: look-ahead found {Twy} at next node {NodeId}, pre-turning",
                    ctx.Aircraft.Callsign,
                    _exitTaxiway ?? "?",
                    _nextCenterlineNode.Id
                );
            }
        }

        // Compute steering: either pre-turn toward exit or follow centerline
        double steerBearing;
        if (_holdShortNode is not null && _pendingBranchNode is not null && _exitPath is not null && _exitPath.Count > 1)
        {
            var firstWaypoint = _exitPath[1];
            double exitBearing = GeoMath.BearingTo(
                _pendingBranchNode.Latitude,
                _pendingBranchNode.Longitude,
                firstWaypoint.Latitude,
                firstWaypoint.Longitude
            );
            double headingChangeRad = Math.Abs(ctx.Aircraft.TrueHeading.SignedAngleTo(new TrueHeading(exitBearing))) * Math.PI / 180.0;
            double turnRateRadPerSec = CategoryPerformance.GroundTurnRate(ctx.Category) * Math.PI / 180.0;
            double speedNmPerSec = ctx.Aircraft.GroundSpeed / 3600.0;
            double leadDistNm = speedNmPerSec * headingChangeRad / turnRateRadPerSec;

            double distToBranch = GeoMath.DistanceNm(
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _pendingBranchNode.Latitude,
                _pendingBranchNode.Longitude
            );

            if (distToBranch <= leadDistNm)
            {
                steerBearing = exitBearing;
                if (headingChangeRad > 0.5) // ~30°
                {
                    AdjustSpeed(ctx, CategoryPerformance.StandardExitSpeed(ctx.Category));
                }
                else
                {
                    AdjustSpeed(ctx, _coastSpeed);
                }
            }
            else
            {
                steerBearing = GeoMath.BearingTo(
                    ctx.Aircraft.Latitude,
                    ctx.Aircraft.Longitude,
                    _nextCenterlineNode.Latitude,
                    _nextCenterlineNode.Longitude
                );
                AdjustSpeed(ctx, _coastSpeed);
            }
        }
        else
        {
            steerBearing = GeoMath.BearingTo(
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _nextCenterlineNode.Latitude,
                _nextCenterlineNode.Longitude
            );
            AdjustSpeed(ctx, _coastSpeed);
        }

        ctx.Targets.TargetSpeed = null;
        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, steerBearing, maxTurn);

        if (distToNext <= CenterlineNodeArrivalNm)
        {
            ctx.Aircraft.Latitude = _nextCenterlineNode.Latitude;
            ctx.Aircraft.Longitude = _nextCenterlineNode.Longitude;

            _currentCenterlineNode = _nextCenterlineNode;
            _nextCenterlineNode = ctx.GroundLayout?.FindCenterlineNeighborAhead(_currentCenterlineNode, _runwayHeading, _runwayId);

            if (_pendingBranchNode is not null && _holdShortNode is not null)
            {
                _pendingBranchNode = null;
            }
            else
            {
                TryFindHoldShort(ctx);
            }
        }

        LogPeriodic(ctx, distToNext);
        return false;
    }

    /// <summary>
    /// Build a TaxiRoute from the exit path and start GroundNavigator with
    /// exit-appropriate speed (HighSpeedExitSpeed for shallow exits).
    /// </summary>
    private bool StartExitNavigation(PhaseContext ctx)
    {
        if (_exitPath is null || _exitPath.Count < 2 || _exitTaxiway is null || ctx.GroundLayout is null)
        {
            ctx.Logger.LogWarning("[Exit] {Callsign}: cannot build exit route", ctx.Aircraft.Callsign);
            return false;
        }

        var segments = new List<TaxiRouteSegment>();
        for (int i = 0; i < _exitPath.Count - 1; i++)
        {
            var fromNode = _exitPath[i];
            var toNode = _exitPath[i + 1];
            var edge = FindEdgeBetween(fromNode, toNode.Id);
            if (edge is null)
            {
                ctx.Logger.LogWarning("[Exit] {Callsign}: no edge between nodes {From} and {To}", ctx.Aircraft.Callsign, fromNode.Id, toNode.Id);
                return false;
            }

            segments.Add(
                new TaxiRouteSegment
                {
                    FromNodeId = fromNode.Id,
                    ToNodeId = toNode.Id,
                    TaxiwayName = _exitTaxiway,
                    Edge = edge,
                }
            );
        }

        _exitRoute = new TaxiRoute { Segments = segments, HoldShortPoints = [] };

        double exitAngle = ctx.GroundLayout.ComputeExitAngle(_holdShortNode!, _exitTaxiway, _runwayHeading) ?? 90;
        double maxSpeed = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, exitAngle);

        _navigator = new GroundNavigator { MaxSpeedKts = maxSpeed, CornerSpeedKts = CategoryPerformance.TaxiCornerSpeed(ctx.Category) };
        _navigator.SetupSegment(_exitRoute, ctx, _ => true);

        _state = ExitState.FollowingExitPath;
        ctx.Aircraft.CurrentTaxiway = _exitTaxiway;

        ctx.Logger.LogDebug(
            "[Exit] {Callsign}: following exit path, {SegCount} segments on {Twy}, maxSpeed={Speed:F0}kts, path=[{Path}]",
            ctx.Aircraft.Callsign,
            segments.Count,
            _exitTaxiway,
            maxSpeed,
            string.Join("→", _exitPath.Select(n => n.Id))
        );
        return true;
    }

    private bool TickFollowingExitPath(PhaseContext ctx)
    {
        if (_exitRoute is null || _navigator is null)
        {
            return true;
        }

        bool isLastSegment = _exitRoute.CurrentSegmentIndex + 1 >= _exitRoute.Segments.Count;
        var result = _navigator.Tick(ctx, isLastSegment, _ => true);

        if (result == NavigatorResult.ArrivedAtNode)
        {
            if (_exitRoute.CurrentSegment is { } seg)
            {
                ctx.Aircraft.CurrentTaxiway = seg.TaxiwayName;
            }

            _exitRoute.CurrentSegmentIndex++;

            if (_exitRoute.IsComplete)
            {
                return CompleteExit(ctx);
            }

            _navigator.SetupSegment(_exitRoute, ctx, _ => true);
        }

        return false;
    }

    private bool CompleteExit(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;

        // Snap to the hold-short node
        if (_holdShortNode is not null && ctx.GroundLayout is not null && ctx.GroundLayout.Nodes.TryGetValue(_holdShortNode.Id, out var finalNode))
        {
            ctx.Aircraft.Latitude = finalNode.Latitude;
            ctx.Aircraft.Longitude = finalNode.Longitude;
        }

        // Offset forward by half the aircraft length so the tail clears the hold-short line
        double lengthFt = FaaAircraftDatabase.Get(ctx.Aircraft.AircraftType)?.LengthFt ?? 60.0;
        double halfLengthNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;
        var (newLat, newLon) = GeoMath.ProjectPoint(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, ctx.Aircraft.TrueHeading, halfLengthNm);
        ctx.Aircraft.Latitude = newLat;
        ctx.Aircraft.Longitude = newLon;

        // Broadcast "clear of runway"
        string rwy = _runwayId ?? "unknown";
        string twy = _exitTaxiway ?? "taxiway";
        ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} clear of runway {rwy} at {twy}");

        // Insert HoldingAfterExitPhase
        ctx.Aircraft.Phases?.InsertAfterCurrent(new HoldingAfterExitPhase(_runwayId, _exitTaxiway));

        ctx.Logger.LogDebug(
            "[Exit] {Callsign}: exit complete on {Twy}, holding at ({Lat:F6},{Lon:F6}), hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            _exitTaxiway,
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            ctx.Aircraft.TrueHeading.Degrees
        );

        return true;
    }

    private static GroundEdge? FindEdgeBetween(GroundNode fromNode, int toNodeId)
    {
        foreach (var edge in fromNode.Edges)
        {
            int other = edge.FromNodeId == fromNode.Id ? edge.ToNodeId : edge.FromNodeId;
            if (other == toNodeId)
            {
                return edge;
            }
        }
        return null;
    }

    private void TryFindHoldShortFromNode(PhaseContext ctx, GroundNode centerlineNode)
    {
        if (ctx.GroundLayout is null)
        {
            return;
        }

        var result = ctx.GroundLayout.FindAdjacentHoldShort(centerlineNode, _runwayId, _runwayHeading, _lastResolvedPreference);
        if (result is null)
        {
            return;
        }

        double? exitAngle = ctx.GroundLayout.ComputeExitAngle(result.Value.Node, result.Value.Taxiway, _runwayHeading);
        bool isExplicit = (_lastResolvedPreference?.Taxiway is not null) || (_lastResolvedPreference?.Side is not null);
        if ((exitAngle is not null) && (exitAngle.Value > 90) && !isExplicit)
        {
            return;
        }
        if (ctx.IsHoldShortNodeOccupied?.Invoke(result.Value.Node.Id) == true)
        {
            return;
        }

        _holdShortNode = result.Value.Node;
        _exitTaxiway = result.Value.Taxiway;
        _exitPath = result.Value.Path;
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

        double? exitAngle = ctx.GroundLayout.ComputeExitAngle(result.Value.Node, result.Value.Taxiway, _runwayHeading);
        bool hasMoreRunway = _nextCenterlineNode is not null;
        bool isExplicit = (_lastResolvedPreference?.Taxiway is not null) || (_lastResolvedPreference?.Side is not null);

        if ((exitAngle is not null) && (exitAngle.Value > 90) && hasMoreRunway && !isExplicit)
        {
            ctx.Logger.LogDebug(
                "[Exit] {Callsign}: skipping {Twy} (angle={Angle:F0}° > 90°)",
                ctx.Aircraft.Callsign,
                result.Value.Taxiway,
                exitAngle.Value
            );
            return;
        }

        if (hasMoreRunway && (ctx.IsHoldShortNodeOccupied?.Invoke(result.Value.Node.Id) == true))
        {
            ctx.Logger.LogDebug("[Exit] {Callsign}: skipping {Twy} (hold-short occupied)", ctx.Aircraft.Callsign, result.Value.Taxiway);
            return;
        }

        _holdShortNode = result.Value.Node;
        _exitTaxiway = result.Value.Taxiway;
        _exitPath = result.Value.Path;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        ctx.Logger.LogDebug("[Exit] {Callsign}: OnEnd ({Status}), taxiway={Twy}", ctx.Aircraft.Callsign, endStatus, _exitTaxiway ?? "none");
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
            ctx.Logger.LogTrace(
                "[Exit] {Callsign}: rolling, dist={Dist:F4}nm, gs={Gs:F1}kts, hdg={Hdg:F0}",
                ctx.Aircraft.Callsign,
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
            ReachedExitNode = _state == ExitState.FollowingExitPath,
            ExitTaxiway = _exitTaxiway,
            RunwayId = _runwayId,
            LastResolvedPreference = (int?)_lastResolvedPreference?.Side,
            LastResolvedPreferenceTaxiway = _lastResolvedPreference?.Taxiway,
            ExitWaypointNodeIds = _exitPath?.Select(n => n.Id).ToList(),
            ExitWaypointIndex = _exitRoute?.CurrentSegmentIndex ?? 0,
            ExitSpeed = _coastSpeed,
            TimeSinceLastLog = _timeSinceLastLog,
            StoppedForLahso = false,
            Braking = false,
            VirtualTargetLat = null,
            VirtualTargetLon = null,
            CurrentCenterlineNodeId = _currentCenterlineNode?.Id,
            NextCenterlineNodeId = _nextCenterlineNode?.Id,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            ExitStateValue = (int)_state,
            Navigator = _navigator?.ToSnapshot(),
        };

    public static RunwayExitPhase FromSnapshot(RunwayExitPhaseDto dto, AirportGroundLayout? groundLayout)
    {
        var phase = new RunwayExitPhase();
        phase._exitTaxiway = dto.ExitTaxiway;
        phase._runwayId = dto.RunwayId;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._state = (ExitState)dto.ExitStateValue;
        phase._lastResolvedPreference = dto.LastResolvedPreference.HasValue
            ? new ExitPreference { Side = (ExitSide)dto.LastResolvedPreference.Value, Taxiway = dto.LastResolvedPreferenceTaxiway }
            : null;
        phase._coastSpeed = dto.ExitSpeed;
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
            if (dto.ExitWaypointNodeIds is not null)
            {
                var path = new List<GroundNode>();
                foreach (int id in dto.ExitWaypointNodeIds)
                {
                    if (groundLayout.Nodes.TryGetValue(id, out var n))
                    {
                        path.Add(n);
                    }
                }
                if (path.Count > 0)
                {
                    phase._exitPath = path;
                }
            }
            if (dto.Navigator is not null)
            {
                phase._navigator = GroundNavigator.FromSnapshot(dto.Navigator);
            }
        }

        return phase;
    }
}
