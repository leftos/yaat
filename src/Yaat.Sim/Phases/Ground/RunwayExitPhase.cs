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
    private const double LogIntervalSeconds = 3.0;

    /// <summary>
    /// Sentinel node ID for the virtual approach segment. The segment from the
    /// aircraft's current position to the exit branch point uses this as FromNodeId.
    /// It never needs to be looked up in the layout — the navigator only resolves
    /// ToNodeId for target coordinates.
    /// </summary>
    private enum ExitState
    {
        RollingOnCenterline,
        FollowingExitPath,
    }

    private ExitState _state = ExitState.RollingOnCenterline;
    private string? _runwayId;
    private TrueHeading _runwayHeading;
    private ExitPreference? _lastResolvedPreference;
    private ExitSide? _inferredSide;
    private double _coastSpeed;
    private double _timeSinceLastLog;

    // Exit target (set when a hold-short is found)
    private GroundNode? _holdShortNode;
    private string? _exitTaxiway;

    // Full exit path including branch point: [branchNode, wp1, wp2, ..., holdShort]
    private List<GroundNode>? _exitPath;

    // Exit path navigation (FollowingExitPath state)
    private TaxiRoute? _exitRoute;
    private GroundNavigator? _navigator;

    /// <summary>
    /// The hold-short node ID this aircraft is targeting (or null if still searching).
    /// Used by <see cref="SimulationEngine"/> to mark the exit as occupied so other
    /// aircraft don't select the same exit.
    /// </summary>
    public int? TargetHoldShortNodeId => _holdShortNode?.Id;

    /// <summary>
    /// True while the aircraft is rolling along the runway centerline searching for
    /// an exit. False once it has committed to an exit and is following the taxiway
    /// path. Used by <see cref="GroundConflictDetector"/> to decide whether the
    /// aircraft should be exempt from ground conflict checks (only while on centerline).
    /// </summary>
    public bool IsOnCenterline => _state == ExitState.RollingOnCenterline;

    public override string Name => "Runway Exit";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        _runwayId = ctx.Aircraft.Phases?.AssignedRunway?.Designator;
        _runwayHeading = ctx.Aircraft.TrueHeading;
        _lastResolvedPreference = ctx.Aircraft.Phases?.RequestedExit;
        _coastSpeed = CategoryPerformance.RolloutCoastSpeed(ctx.Category);

        // Infer a side from runway layout. For default (no preference), merge directly.
        // For taxiway-only (EXIT K), store separately — TryFindExitAhead uses it as
        // a soft tiebreaker so taxiways that only exist on one side still work.
        if ((_lastResolvedPreference?.Side is null) && (ctx.GroundLayout is not null) && (_runwayId is not null))
        {
            _inferredSide = ctx.GroundLayout.InferPreferredExitSide(_runwayId, _runwayHeading);
            if ((_inferredSide is not null) && (_lastResolvedPreference?.Taxiway is null))
            {
                _lastResolvedPreference = new ExitPreference { Side = _inferredSide.Value };
            }
        }

        if (ctx.GroundLayout is null)
        {
            ctx.Logger.LogDebug("[Exit] {Callsign}: no ground layout, will stop immediately", ctx.Aircraft.Callsign);
            return;
        }

        // Clear any stale resolved exit — RunwayExitPhase always finds the exit
        // fresh via analog search so the virtual approach segment provides proper
        // turn context from the aircraft's actual position.
        if (ctx.Aircraft.Phases?.ResolvedExit is not null)
        {
            ctx.Aircraft.Phases.ResolvedExit = null;
        }

        // Search for exits ahead immediately.
        TryFindExitAhead(ctx);

        ctx.Logger.LogDebug(
            "[Exit] {Callsign}: rwy {Rwy}, hdg={Hdg:F0}, holdShort={HS}",
            ctx.Aircraft.Callsign,
            _runwayId ?? "?",
            _runwayHeading.Degrees,
            _holdShortNode?.Id.ToString() ?? "searching"
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
            TryFindExitAhead(ctx);
        }

        // Exit found — build route with virtual approach segment and hand to navigator
        if (_holdShortNode is not null && _state == ExitState.RollingOnCenterline)
        {
            if (StartExitNavigation(ctx))
            {
                return TickFollowingExitPath(ctx);
            }

            // Route construction failed. Clear and keep searching.
            _holdShortNode = null;
            _exitTaxiway = null;
            _exitPath = null;
        }

        return TickRolling(ctx);
    }

    /// <summary>
    /// Analog centerline rolling: steer along the runway heading (no node
    /// walking) and continuously search for exits ahead.
    /// </summary>
    private bool TickRolling(PhaseContext ctx)
    {
        // Steer along runway heading at coast speed
        AdjustSpeed(ctx, _coastSpeed);
        ctx.Targets.TargetSpeed = null;
        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, _runwayHeading.Degrees, maxTurn);

        // Continuously search for exits ahead
        if (_holdShortNode is null)
        {
            TryFindExitAhead(ctx);
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            ctx.Logger.LogTrace(
                "[Exit] {Callsign}: rolling, gs={Gs:F1}kts, hdg={Hdg:F0}",
                ctx.Aircraft.Callsign,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.TrueHeading.Degrees
            );
        }

        return false;
    }

    /// <summary>
    /// Search for the nearest exit ahead of the aircraft using the runway
    /// centerline graph. If the preferred taxiway isn't found ahead, relaxes
    /// the preference (taxiway → side → any) until an exit is found.
    /// Sets _holdShortNode/_exitTaxiway/_exitPath if found.
    /// </summary>
    private void TryFindExitAhead(PhaseContext ctx)
    {
        if (ctx.GroundLayout is null || _runwayId is null)
        {
            return;
        }

        // Occupied hold-short nodes are excluded at the BFS level so the finder
        // returns the next-best unoccupied exit at each centerline node, rather
        // than returning an occupied exit that we'd have to skip post-hoc (which
        // would miss other exits from the same centerline node).
        var occupied = ctx.OccupiedHoldShortNodes;

        // Soft tiebreaker: when the preference has a taxiway but no side, try
        // with the inferred side first. If nothing found, fall through to the
        // normal relaxation loop with the original preference.
        if ((_lastResolvedPreference is { Taxiway: not null, Side: null }) && (_inferredSide is not null))
        {
            var tiebreakerPref = new ExitPreference { Taxiway = _lastResolvedPreference.Taxiway, Side = _inferredSide.Value };
            var tiebreakerResult = ctx.GroundLayout.FindExitFromCenterline(
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _runwayHeading,
                _runwayId,
                tiebreakerPref,
                excludeHoldShortNodes: occupied
            );

            if (tiebreakerResult is not null)
            {
                _holdShortNode = tiebreakerResult.Value.HoldShort;
                _exitTaxiway = tiebreakerResult.Value.Taxiway;
                _exitPath = tiebreakerResult.Value.Path;
                return;
            }
        }

        // Try with current preference, then relax until we find something
        var preference = _lastResolvedPreference;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var result = ctx.GroundLayout.FindExitFromCenterline(
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _runwayHeading,
                _runwayId,
                preference,
                excludeHoldShortNodes: occupied
            );

            if (result is not null)
            {
                bool isExplicit = (preference?.Taxiway is not null) || (preference?.Side is not null);
                if ((result.Value.ExitAngle > 100) && !isExplicit)
                {
                    // Skip backward exits when no explicit preference
                }
                else
                {
                    if ((preference != _lastResolvedPreference) && (_lastResolvedPreference?.Taxiway is not null))
                    {
                        ctx.Logger.LogDebug(
                            "[Exit] {Callsign}: preferred exit {Twy} not ahead, relaxed to {Actual}",
                            ctx.Aircraft.Callsign,
                            _lastResolvedPreference.Taxiway,
                            result.Value.Taxiway
                        );
                    }

                    _holdShortNode = result.Value.HoldShort;
                    _exitTaxiway = result.Value.Taxiway;
                    _exitPath = result.Value.Path;

                    ctx.Logger.LogDebug(
                        "[Exit] {Callsign}: found exit {Twy}, angle={Angle:F0}°, path=[{Path}]",
                        ctx.Aircraft.Callsign,
                        _exitTaxiway,
                        result.Value.ExitAngle,
                        string.Join("→", _exitPath.Select(n => n.Id))
                    );
                    return;
                }
            }

            // Relax preference: taxiway → side → any
            if (preference?.Taxiway is not null)
            {
                preference = new ExitPreference { Side = preference.Side };
            }
            else if (preference?.Side is not null)
            {
                preference = null;
            }
            else
            {
                break; // Already at "any", nothing more to relax
            }
        }
    }

    /// <summary>
    /// Build a TaxiRoute from the exit path with a virtual approach segment
    /// from the aircraft's current position to the branch node, and start the
    /// GroundNavigator. The virtual segment gives the navigator inbound bearing
    /// context so it can anticipate the turn at the branch node.
    /// </summary>
    private bool StartExitNavigation(PhaseContext ctx)
    {
        if (_exitPath is null || _exitPath.Count < 2 || _exitTaxiway is null || ctx.GroundLayout is null)
        {
            ctx.Logger.LogWarning("[Exit] {Callsign}: cannot build exit route", ctx.Aircraft.Callsign);
            return false;
        }

        var segments = new List<TaxiRouteSegment>();
        var branchNode = _exitPath[0];

        // Virtual approach segment: [aircraft position → branch node].
        // Always added — gives the navigator inbound bearing context for turn
        // anticipation at the branch node, whether the aircraft is far away
        // (analog search) or right at the branch (committed exit from LandingPhase).
        var virtualFromNode = VirtualNode.Create(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
        double distToBranch = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, branchNode.Latitude, branchNode.Longitude);
        var approachEdge = new GroundEdge
        {
            Nodes = [virtualFromNode, branchNode],
            TaxiwayName = $"RWY{_runwayId}",
            DistanceNm = Math.Max(distToBranch, 0.001),
        };
        segments.Add(new TaxiRouteSegment { TaxiwayName = _exitTaxiway, Edge = approachEdge.Directed(virtualFromNode, branchNode) });

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

            segments.Add(new TaxiRouteSegment { TaxiwayName = _exitTaxiway, Edge = edge.Directed(fromNode, toNode) });
        }

        // Append a virtual segment past the hold-short node so the aircraft's tail
        // clears the hold-short line. The virtual node is offset along the graph edge.
        var holdShortNode = _exitPath[^1];
        double lengthFt = FaaAircraftDatabase.Get(ctx.Aircraft.AircraftType)?.LengthFt ?? 60.0;
        double halfLengthNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;

        GroundNode virtualTarget;
        if (_exitPath.Count >= 2)
        {
            virtualTarget = VirtualNode.OffsetPast(ctx.GroundLayout!, holdShortNode, _exitPath[^2], halfLengthNm);
        }
        else
        {
            virtualTarget = VirtualNode.OffsetPast(ctx.GroundLayout!, holdShortNode, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, halfLengthNm);
        }

        segments.Add(VirtualNode.CreateSegment(holdShortNode, virtualTarget, _exitTaxiway));

        _exitRoute = new TaxiRoute { Segments = segments, HoldShortPoints = [] };

        // Max speed = coast speed so the aircraft maintains rollout speed during
        // the approach segment. The navigator's braking constraints handle
        // deceleration into the turn at the branch node.
        double maxSpeed = _coastSpeed;

        _navigator = new GroundNavigator { MaxSpeedKts = maxSpeed, CornerSpeedKts = CategoryPerformance.TaxiCornerSpeed(ctx.Category) };
        _navigator.SetupSegment(_exitRoute, ctx, _ => true);

        _state = ExitState.FollowingExitPath;
        ctx.Aircraft.CurrentTaxiway = _exitTaxiway;

        ctx.Logger.LogDebug(
            "[Exit] {Callsign}: following exit path, {SegCount} segments on {Twy}, maxSpeed={Speed:F0}kts, path=[virtual→{Path}]",
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

        // No position snap — the GroundNavigator already brakes to 0 at the
        // final node (FinalNodeArrivalThresholdNm ≈ 1.8ft). The aircraft is
        // close enough; teleporting to exact node coords causes overlap when
        // another aircraft is already there.

        // Mark this hold-short as occupied so same-tick aircraft see it
        if (_holdShortNode is not null)
        {
            ctx.MarkHoldShortNodeOccupied?.Invoke(_holdShortNode.Id);
        }

        // Insert HoldingAfterExitPhase
        ctx.Aircraft.Phases?.InsertAfterCurrent(new HoldingAfterExitPhase(_runwayId, _exitTaxiway, _holdShortNode?.Id));

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

    private static IGroundEdge? FindEdgeBetween(GroundNode fromNode, int toNodeId)
    {
        foreach (var edge in fromNode.Edges)
        {
            if (edge.OtherNodeId(fromNode.Id) == toNodeId)
            {
                return edge;
            }
        }
        return null;
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
            CurrentCenterlineNodeId = null,
            NextCenterlineNodeId = null,
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
            // CurrentCenterlineNodeId and NextCenterlineNodeId are legacy snapshot
            // fields — the analog approach no longer uses centerline node walking.
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
