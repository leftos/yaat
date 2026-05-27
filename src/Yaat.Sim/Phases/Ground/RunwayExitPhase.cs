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
///   FollowingExitPath — navigator follows exit taxiway edges to hold-short
/// </summary>
public sealed class RunwayExitPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwayExitPhase");

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

    /// <summary>
    /// The runway being exited. Captured in <see cref="OnStart"/> from the
    /// aircraft's assigned runway. Used by the client info text to render
    /// "Exiting runway {id} via {taxiway}".
    /// </summary>
    public string? RunwayId => _runwayId;

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
            Log.LogDebug(
                "[Exit] {Callsign}: inferred exit side for {Rwy} = {Side}",
                ctx.Aircraft.Callsign,
                _runwayId,
                _inferredSide?.ToString() ?? "none"
            );
            if (_inferredSide is not null)
            {
                _lastResolvedPreference = new ExitPreference { Side = _inferredSide.Value, Taxiway = _lastResolvedPreference?.Taxiway };
            }
        }

        if (ctx.GroundLayout is null)
        {
            Log.LogDebug("[Exit] {Callsign}: no ground layout, will stop immediately", ctx.Aircraft.Callsign);
            return;
        }

        // If LandingPhase committed a resolved exit, honor it — but only if
        // the hold-short isn't currently occupied. LandingPhase plans without
        // regard to occupancy so the pilot continues to brake for the commanded
        // exit, only deciding to skip at the last moment. RunwayExitPhase is
        // that last moment: if another aircraft is now claiming the committed
        // hold-short, drop the commit and fall through to a fresh analog search
        // that excludes occupied nodes.
        var committed = ctx.Aircraft.Phases?.ResolvedExit;
        if (committed is not null)
        {
            ctx.Aircraft.Phases!.ResolvedExit = null;

            bool occupied = ctx.OccupiedHoldShortNodes?.Contains(committed.HoldShortNode.Id) ?? false;
            if (!occupied)
            {
                _holdShortNode = committed.HoldShortNode;
                _exitTaxiway = committed.TaxiwayName;
                _exitPath = committed.Path;

                Log.LogDebug(
                    "[Exit] {Callsign}: using committed exit {Twy}, path=[{Path}]",
                    ctx.Aircraft.Callsign,
                    _exitTaxiway,
                    string.Join("→", _exitPath.Select(n => n.Id))
                );
            }
            else
            {
                Log.LogDebug(
                    "[Exit] {Callsign}: committed exit {Twy} is now occupied, falling back to analog search",
                    ctx.Aircraft.Callsign,
                    committed.TaxiwayName
                );
            }
        }

        if (_holdShortNode is null)
        {
            // Search for exits ahead immediately.
            TryFindExitAhead(ctx);
        }

        Log.LogDebug(
            "[Exit] {Callsign}: rwy {Rwy}, hdg={Hdg:F0}, holdShort={HS}",
            ctx.Aircraft.Callsign,
            _runwayId ?? "?",
            _runwayHeading.Degrees,
            _holdShortNode?.Id.ToString() ?? "searching"
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.Ground.IsImmobile)
        {
            ctx.Targets.TargetSpeed = 0;
            ctx.Targets.DesiredDecelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
            return false;
        }

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
    /// walking) and continuously search for exits ahead. Writes ControlTargets
    /// and lets FlightPhysics integrate — no direct pose or IAS writes. Safe
    /// from the StationaryGroundSpeedKts guard because rolling is always at
    /// coast speed (≥ 15 kt helicopter, ≥ 40 kt jet), never approaching the
    /// 0.1 kt floor.
    /// </summary>
    private bool TickRolling(PhaseContext ctx)
    {
        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.TargetSpeed = _coastSpeed;
        // Use ground rollout decel (category-specific, 2.5 kt/s jet, 1.5 kt/s
        // piston) rather than the airborne default from AircraftPerformance.DecelRate.
        ctx.Targets.DesiredDecelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
        // Use ground turn rate (20 deg/s jet, 35 deg/s piston) rather than the
        // airborne turn rate (~2.5 deg/s). On the ground FlightPhysics uses this
        // override via TurnRateOverride; V1 achieved the same effect by writing
        // Aircraft.TrueHeading directly with GroundTurnRate.
        ctx.Targets.TurnRateOverride = CategoryPerformance.GroundTurnRate(ctx.Category);

        // Continuously search for exits ahead
        if (_holdShortNode is null)
        {
            TryFindExitAhead(ctx);
        }

        // Terminal-end safety stop: if no exit was found and the aircraft is
        // running out of runway, brake to a halt rather than coasting off the
        // end. Without this backstop, a missing or unreachable forward exit
        // (typically a geojson defect or an aircraft that landed past every
        // exit) leaves the phase looping at coast speed indefinitely.
        if ((_holdShortNode is null) && (ctx.Aircraft.Phases?.AssignedRunway is { } rwy))
        {
            double distToEndNm = GeoMath.AlongTrackDistanceNm(new LatLon(rwy.EndLatitude, rwy.EndLongitude), ctx.Aircraft.Position, _runwayHeading);

            // 0.15 nm ≈ 911 ft — enough headroom for the aircraft to slow from
            // coast speed (40 kts jet) to a stop at the firm braking rate
            // before the runway end, without prematurely stopping on a runway
            // where exits are still being searched for.
            const double TerminalStopBufferNm = 0.15;
            if (distToEndNm <= TerminalStopBufferNm)
            {
                ctx.Targets.TargetSpeed = 0;
                // Firm braking (5 kts/s) — same rate LandingPhase uses for explicit
                // exit commands. From 40 kts coast, this stops the aircraft in
                // about 0.044 nm (260 ft) — comfortably inside the 0.15 nm buffer.
                ctx.Targets.DesiredDecelRate = 5.0;
                if (_timeSinceLastLog >= LogIntervalSeconds)
                {
                    _timeSinceLastLog = 0;
                    Log.LogWarning(
                        "[Exit] {Callsign}: no exit found, {DistFt:F0}ft to runway end — braking to stop",
                        ctx.Aircraft.Callsign,
                        distToEndNm * 6076.12
                    );
                }
            }
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            Log.LogTrace(
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
    ///
    /// Lookahead rule (mirrors <see cref="LandingPhase.ResolveNextCandidate"/>):
    /// when there is a side preference (explicit or inferred) and the BFS at a
    /// given centerline returns an off-side hold-short (because the on-side was
    /// occupied or doesn't exist there), defer that candidate and continue
    /// walking forward for an on-side option. Only commit the deferred off-side
    /// fallback when the walk exhausts.
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
            if (TryRunSearchWithLookahead(ctx, tiebreakerPref, occupied, _inferredSide))
            {
                return;
            }
        }

        // Try with current preference, then relax until we find something.
        var preference = _lastResolvedPreference;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ExitSide? sidePref = preference?.Side ?? _inferredSide;
            if (TryRunSearchWithLookahead(ctx, preference, occupied, sidePref))
            {
                return;
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
    /// Walk centerlines forward looking for an exit that satisfies the side
    /// preference. Defers off-side candidates while searching for an on-side
    /// option, falling back to the deferred off-side if none is found.
    /// Returns true on commit.
    /// </summary>
    private bool TryRunSearchWithLookahead(PhaseContext ctx, ExitPreference? preference, HashSet<int>? occupied, ExitSide? sidePref)
    {
        if (ctx.GroundLayout is null || _runwayId is null)
        {
            return false;
        }

        bool isExplicit = (preference?.Taxiway is not null) || (preference?.Side is not null);

        var found = ctx.GroundLayout.FindOnSidePreferredExit(
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            _runwayHeading,
            _runwayId,
            preference,
            sidePref,
            excludeBranchPoints: null,
            excludeHoldShortNodes: occupied,
            filter: candidate =>
            {
                // Skip backward exits when no explicit preference
                if ((candidate.ExitAngle > 100) && !isExplicit)
                {
                    return AirportGroundLayout.CandidateVerdict.Skip;
                }
                return AirportGroundLayout.CandidateVerdict.Accept;
            }
        );

        if (found is null)
        {
            return false;
        }

        CommitFoundExit(ctx, found.Value.HoldShort, found.Value.Taxiway, found.Value.Path, found.Value.ExitAngle, preference);
        return true;
    }

    private void CommitFoundExit(
        PhaseContext ctx,
        GroundNode holdShort,
        string taxiway,
        List<GroundNode> path,
        double exitAngle,
        ExitPreference? preference
    )
    {
        if ((preference != _lastResolvedPreference) && (_lastResolvedPreference?.Taxiway is not null))
        {
            Log.LogDebug(
                "[Exit] {Callsign}: preferred exit {Twy} not ahead, relaxed to {Actual}",
                ctx.Aircraft.Callsign,
                _lastResolvedPreference.Taxiway,
                taxiway
            );
        }

        _holdShortNode = holdShort;
        _exitTaxiway = taxiway;
        _exitPath = path;

        Log.LogDebug(
            "[Exit] {Callsign}: found exit {Twy}, angle={Angle:F0}°, path=[{Path}]",
            ctx.Aircraft.Callsign,
            _exitTaxiway,
            exitAngle,
            string.Join("→", _exitPath.Select(n => n.Id))
        );
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
            Log.LogWarning("[Exit] {Callsign}: cannot build exit route", ctx.Aircraft.Callsign);
            return false;
        }

        var segments = new List<TaxiRouteSegment>();
        var branchNode = _exitPath[0];

        // Virtual approach segment: [aircraft position → branch node].
        // Always added — gives the navigator inbound bearing context for turn
        // anticipation at the branch node, whether the aircraft is far away
        // (analog search) or right at the branch (committed exit from LandingPhase).
        var virtualFromNode = VirtualNode.Create(ctx.Aircraft.Position.Lat, ctx.Aircraft.Position.Lon);
        double distToBranch = GeoMath.DistanceNm(ctx.Aircraft.Position, branchNode.Position);
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
                Log.LogWarning("[Exit] {Callsign}: no edge between nodes {From} and {To}", ctx.Aircraft.Callsign, fromNode.Id, toNode.Id);
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
            virtualTarget = VirtualNode.OffsetPast(
                ctx.GroundLayout!,
                holdShortNode,
                ctx.Aircraft.Position.Lat,
                ctx.Aircraft.Position.Lon,
                halfLengthNm
            );
        }

        segments.Add(VirtualNode.CreateSegment(holdShortNode, virtualTarget, _exitTaxiway));

        _exitRoute = new TaxiRoute { Segments = segments, HoldShortPoints = [] };

        // Max speed = coast speed so the aircraft maintains rollout speed during
        // the approach segment. The navigator's braking constraints handle
        // deceleration into the turn at the branch node.
        double maxSpeed = _coastSpeed;

        _navigator = new GroundNavigator { MaxSpeedKts = maxSpeed };
        _navigator.SetupSegment(_exitRoute, ctx, _ => true);

        _state = ExitState.FollowingExitPath;
        ctx.Aircraft.Ground.CurrentTaxiway = _exitTaxiway;

        Log.LogDebug(
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
                ctx.Aircraft.Ground.CurrentTaxiway = seg.TaxiwayName;
            }

            int extraAdvance = _navigator.ExtraSegmentsToAdvance;
            _exitRoute.CurrentSegmentIndex += 1 + extraAdvance;
            if (_exitRoute.CurrentSegmentIndex > _exitRoute.Segments.Count)
            {
                _exitRoute.CurrentSegmentIndex = _exitRoute.Segments.Count;
            }

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

        Log.LogDebug(
            "[Exit] {Callsign}: exit complete on {Twy}, holding at ({Lat:F6},{Lon:F6}), hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            _exitTaxiway,
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
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
        Log.LogDebug("[Exit] {Callsign}: OnEnd ({Status}), taxiway={Twy}", ctx.Aircraft.Callsign, endStatus, _exitTaxiway ?? "none");
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is rolling out / exiting the runway; only EL/ER/EXIT or a new TAXI apply"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new RunwayExitPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            ExitNodeId = _holdShortNode?.Id,
            ReachedExitNode = _state == ExitState.FollowingExitPath,
            ExitTaxiway = _exitTaxiway,
            RunwayId = _runwayId,
            LastResolvedPreference = (int?)_lastResolvedPreference?.Side,
            LastResolvedPreferenceTaxiway = _lastResolvedPreference?.Taxiway,
            ExitWaypointNodeIds = _exitPath?.Select(n => n.Id).ToList(),
            ExitWaypointIndex = _exitRoute?.CurrentSegmentIndex ?? 0,
            ExitSpeed = _coastSpeed,
            TimeSinceLastLog = _timeSinceLastLog,
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
