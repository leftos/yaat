// =============================================================================
// GroundConflictDetector
//
// Per-tick airport ground conflict detector. Classifies each pair of ground
// aircraft into exactly ONE pair kind, runs one handler, writes SpeedLimit on
// the affected aircraft. Phases and physics honor that limit.
//
// Pair classification:
//
//   • Distant           — beyond search range; skip.
//   • Stationary        — both aircraft parked / holding; skip.
//   • Pushback          — at least one pushing back; pushback-buffer logic only.
//   • SameEdgeTrailing  — both on same edge, same direction. Trailer slows to
//     leader's speed (or stops if too close).
//   • SameEdgeHeadOn    — same edge, opposite direction. Pick a deterministic
//     holder (aircraft with more route remaining; tie-break by callsign). One
//     aircraft proceeds, one holds — avoids the mutual-stop deadlock that the
//     earlier "both stop" rule produced once two routes resolve to a single
//     single-lane segment.
//   • Converging        — routes share an upcoming node from different edges.
//     Yielder is whichever aircraft is farther from the shared node. Closing
//     proximity still runs as the physical-overlap safety net (the
//     wingspan-lateral-clearance bypass handles the merge geometry); head-on
//     is suppressed for this pair this tick.
//   • Crossing          — close in space, no shared route node, not same-edge.
//     Apply closing-proximity in both directions; apply head-on fallback IF
//     both are moving with diff > 120° and not behind each other.
//
// Self-pin recovery: a routed aircraft pinned at gs≈0 / SpeedLimit≈0 with no
// active Hold classifies as Stationary. Treats the pinned aircraft as a parked
// obstacle so the lateral-clearance bypass opens for nearby movers on the next
// tick.
//
// Hold classification: a routed aircraft with Ground.Hold set (HOLDPOSITION or
// GIVEWAY) classifies as Stationary. It won't move until the resume condition
// fires (FlightPhysics.UpdateGiveWayResume for GIVEWAY, operator RES for either),
// so other aircraft can pass laterally with wingspan clearance. The diagnostic
// log distinguishes the kind of hold so DebugSink consumers can tell whether the
// stop is intent-bearing ("Yielding to SWA123") or unconditional ("HoldPosition").
//
// Public API:
//   - ApplySpeedLimits(List<AircraftState>, AirportGroundLayout?, double, Action<string>?)
//   - IsClearOf(AircraftState, AircraftState, AirportGroundLayout?)
//   - DebugSink, WingspanLateralCheckEnabled, WingspanLateralCheckRequireStationary
// =============================================================================

using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim;

public static class GroundConflictDetector
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundConflictDetector");

    private const double DefaultTrailDistanceFt = 200.0;
    private const double DefaultStopDistanceFt = 100.0;
    private const double DefaultAircraftLengthFt = 60.0;
    private const double StopBufferFt = 25.0;
    private const double WingtipBufferFt = 25.0;
    private const double OppositeStopDistanceFt = 300.0;
    private const double PushbackBufferFt = 200.0;
    private const double SlowTaxiSpeedKts = 5.0;
    private const double FtPerNm = 6076.12;
    private const double ConvergenceLookaheadFt = 1500.0;
    private const double ConvergenceSlowdownFt = 400.0;
    private const double SearchRangeNm = 0.3;
    private const double SelfPinSpeedEpsilonKts = 0.5;
    private const double SelfPinLimitEpsilonKts = 0.5;

    public static Action<string>? DebugSink { get; set; }
    public static bool WingspanLateralCheckEnabled { get; set; } = true;
    public static bool WingspanLateralCheckRequireStationary { get; set; } = true;

    private enum MovementState
    {
        Stationary,
        Taxiing,
        Pushing,
        Following,
        Untracked,
    }

    private enum PairKind
    {
        Distant,
        Stationary,
        Pushback,
        SameEdgeTrailing,
        SameEdgeHeadOn,
        Converging,
        Crossing,
    }

    /// <summary>
    /// Detect ground conflicts and set GroundSpeedLimit on affected aircraft.
    /// Clears all limits first, then classifies each pair into exactly one
    /// <see cref="PairKind"/> and runs the corresponding resolution.
    /// </summary>
    public static void ApplySpeedLimits(
        List<AircraftState> aircraft,
        AirportGroundLayout? layout,
        double deltaSeconds = 0,
        Action<string>? diagnosticLog = null
    )
    {
        var explicitLog = diagnosticLog;
        var sink = DebugSink;
        if (sink is not null)
        {
            diagnosticLog = explicitLog is null
                ? sink
                : line =>
                {
                    explicitLog(line);
                    sink(line);
                };
        }

        for (int i = 0; i < aircraft.Count; i++)
        {
            aircraft[i].Ground.SpeedLimit = null;

            if (aircraft[i].Ground.ConflictBreakRemainingSeconds > 0)
            {
                aircraft[i].Ground.ConflictBreakRemainingSeconds = Math.Max(0, aircraft[i].Ground.ConflictBreakRemainingSeconds - deltaSeconds);
            }
        }

        var entries = new List<(AircraftState Ac, MovementState State, double? MoveDir)>();
        for (int i = 0; i < aircraft.Count; i++)
        {
            var ac = aircraft[i];
            if (!ac.IsOnGround)
            {
                continue;
            }

            var (state, dir) = Classify(ac);
            entries.Add((ac, state, dir));
            string holdReason = ac.Ground.Hold switch
            {
                { Kind: HoldKind.GiveWay, YieldTarget: { } t } => $" hold=GiveWay→{t}",
                { Kind: HoldKind.HoldPosition } => " hold=HoldPosition",
                _ => string.Empty,
            };
            diagnosticLog?.Invoke(
                $"[Classify] {ac.Callsign}: {state}{holdReason}, dir={dir?.ToString("F0") ?? "null"}, gs={ac.GroundSpeed:F1}, phase={ac.Phases?.CurrentPhase?.Name ?? "null"}, route={ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex.ToString() ?? "null"}/{ac.Ground.AssignedTaxiRoute?.Segments.Count.ToString() ?? "null"}"
            );
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var (a, stateA, dirA) = entries[i];

            if (stateA == MovementState.Following)
            {
                continue;
            }

            if (a.Ground.ConflictBreakRemainingSeconds > 0)
            {
                continue;
            }

            for (int j = i + 1; j < entries.Count; j++)
            {
                var (b, stateB, dirB) = entries[j];

                if (stateB == MovementState.Following)
                {
                    continue;
                }

                if (b.Ground.ConflictBreakRemainingSeconds > 0)
                {
                    continue;
                }

                double distNm = GeoMath.DistanceNm(a.Position, b.Position);
                if (distNm > SearchRangeNm)
                {
                    continue;
                }

                double distFt = distNm * FtPerNm;
                var kind = ClassifyPair(a, stateA, b, stateB, distFt, layout);

                // Make the controller-supplied GIVEWAY relationship visible. The pair
                // resolution is still Stationary-driven (one aircraft is held, so it
                // already classifies as Stationary), but the operator sees who is
                // yielding to whom rather than an anonymous "Stationary" pair.
                if (a.Ground.Hold is { } ha && ha.IsGiveWayFor(b.Callsign))
                {
                    diagnosticLog?.Invoke($"[Pair] ControllerGiveWay {a.Callsign}→{b.Callsign}");
                }
                else if (b.Ground.Hold is { } hb && hb.IsGiveWayFor(a.Callsign))
                {
                    diagnosticLog?.Invoke($"[Pair] ControllerGiveWay {b.Callsign}→{a.Callsign}");
                }

                diagnosticLog?.Invoke($"[Pair] {a.Callsign}({stateA})+{b.Callsign}({stateB}): dist={distFt:F0}ft → {kind}");

                switch (kind)
                {
                    case PairKind.Distant:
                    case PairKind.Stationary:
                        break;

                    case PairKind.Pushback:
                        // Pushback is its own world — the dedicated buffer logic is
                        // sufficient; we don't run closing/head-on on top.
                        ResolvePushbackYield(a, stateA, dirA, b, stateB, dirB, distFt);
                        break;

                    case PairKind.SameEdgeTrailing:
                        ResolveSameEdgeTrailing(a, b, distFt, layout!, diagnosticLog);
                        break;

                    case PairKind.SameEdgeHeadOn:
                        ResolveSameEdgeHeadOn(a, b, distFt, diagnosticLog);
                        break;

                    case PairKind.Converging:
                        ResolveConvergence(a, b, layout!, diagnosticLog);
                        // Closing proximity is the physical-overlap safety net even
                        // when convergence already picked a yielder — if the winner
                        // is geometrically about to hit the yielder it must still
                        // stop. The wingspan-lateral-clearance bypass in
                        // ApplyClosingLimit handles the merge geometry; what we get
                        // from the v2 Classify() upgrade is that a self-pinned
                        // yielder reclassifies as Stationary on the next tick so
                        // the bypass opens up naturally. We skip head-on here —
                        // convergence already resolved the pair-level decision.
                        ApplyCrossingChecks(a, stateA, dirA, b, stateB, dirB, distFt, skipHeadOn: true, diagnosticLog);
                        break;

                    case PairKind.Crossing:
                        ResolveCrossing(a, stateA, dirA, b, stateB, dirB, distFt, diagnosticLog);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Returns true if <paramref name="subject"/> can proceed without conflicting
    /// with <paramref name="reference"/>.
    /// </summary>
    public static bool IsClearOf(AircraftState subject, AircraftState reference, AirportGroundLayout? layout)
    {
        double distNm = GeoMath.DistanceNm(subject.Position, reference.Position);
        double distFt = distNm * FtPerNm;

        var (refState, _) = Classify(reference);

        if (refState == MovementState.Pushing)
        {
            return distFt > PushbackBufferFt;
        }

        var (subState, _) = Classify(subject);
        if (layout is not null && subState == MovementState.Taxiing && refState == MovementState.Taxiing)
        {
            return !ShareUpcomingNode(subject, reference);
        }

        if (refState == MovementState.Stationary)
        {
            var (_, subDir) = Classify(subject);
            if (subDir is null || subject.GroundSpeed <= 0)
            {
                return true;
            }

            var (_, refTrailDist) = GetSeparation(reference, subject);
            if (distFt > refTrailDist)
            {
                return true;
            }

            double bearing = GeoMath.BearingTo(subject.Position, reference.Position);
            return HeadingDifference(subDir.Value, bearing) >= 90;
        }

        return distFt > OppositeStopDistanceFt;
    }

    // --- Classification ---

    private static (MovementState State, double? MoveDirection) Classify(AircraftState ac)
    {
        string? phaseName = ac.Phases?.CurrentPhase?.Name;

        if (phaseName is not null && phaseName.StartsWith("Following", StringComparison.Ordinal))
        {
            return (MovementState.Following, null);
        }

        if (IsStationaryPhase(phaseName))
        {
            return (MovementState.Stationary, null);
        }

        // Controller-held aircraft (HOLDPOSITION or GIVEWAY) are functionally parked
        // until released — they won't move until the resume condition fires
        // (FlightPhysics.UpdateGiveWayResume for GIVEWAY geometry, or operator RES
        // for either). Treat them as Stationary so other aircraft can pass laterally
        // beside them when wingspan clearance allows. Without this, a held aircraft
        // on a taxiway would block every passing aircraft via closing-proximity.
        if (ac.Ground.IsImmobile)
        {
            return (MovementState.Stationary, null);
        }

        if (ac.Ground.PushbackTrueHeading is { } pushHdg)
        {
            return (MovementState.Pushing, pushHdg.Degrees);
        }

        // Self-pin recovery: a route-bearing aircraft that has been clamped to
        // gs≈0 and SpeedLimit≈0 without any explicit hold is effectively parked
        // until the conflict resolves. Treating it as Stationary lets the
        // wingspan-lateral-clearance bypass open up for nearby movers, so a
        // third aircraft (or the original mover-pair) can pass beside it on
        // the next tick. Aircraft that ARE legitimately held caught earlier;
        // this catches the unexpected pin case where the detector itself
        // bottomed an aircraft out.
        if (ac.Ground.AssignedTaxiRoute?.CurrentSegment is not null)
        {
            bool selfPinned =
                ac.GroundSpeed <= SelfPinSpeedEpsilonKts && ac.Ground.SpeedLimit is { } lim && lim <= SelfPinLimitEpsilonKts && !ac.Ground.IsImmobile;
            if (selfPinned)
            {
                return (MovementState.Stationary, null);
            }

            return (MovementState.Taxiing, ac.TrueHeading.Degrees);
        }

        if (ac.GroundSpeed <= 0)
        {
            return (MovementState.Stationary, null);
        }

        return (MovementState.Untracked, ac.TrueHeading.Degrees);
    }

    private static PairKind ClassifyPair(
        AircraftState a,
        MovementState stateA,
        AircraftState b,
        MovementState stateB,
        double distFt,
        AirportGroundLayout? layout
    )
    {
        if (stateA == MovementState.Stationary && stateB == MovementState.Stationary)
        {
            return PairKind.Stationary;
        }

        if (stateA == MovementState.Pushing || stateB == MovementState.Pushing)
        {
            return PairKind.Pushback;
        }

        if (layout is not null && stateA == MovementState.Taxiing && stateB == MovementState.Taxiing)
        {
            var segA = a.Ground.AssignedTaxiRoute?.CurrentSegment;
            var segB = b.Ground.AssignedTaxiRoute?.CurrentSegment;
            if (segA is not null && segB is not null)
            {
                bool sameEdge =
                    (segA.FromNodeId == segB.FromNodeId && segA.ToNodeId == segB.ToNodeId)
                    || (segA.FromNodeId == segB.ToNodeId && segA.ToNodeId == segB.FromNodeId);

                if (sameEdge)
                {
                    return segA.ToNodeId == segB.ToNodeId ? PairKind.SameEdgeTrailing : PairKind.SameEdgeHeadOn;
                }
            }

            var routeA = a.Ground.AssignedTaxiRoute;
            var routeB = b.Ground.AssignedTaxiRoute;
            if (routeA is not null && routeB is not null && FindSharedUpcomingNode(routeA, routeB) is not null)
            {
                return PairKind.Converging;
            }
        }

        return PairKind.Crossing;
    }

    // --- Conflict resolution ---

    private static void ResolveSameEdgeTrailing(
        AircraftState a,
        AircraftState b,
        double distFt,
        AirportGroundLayout layout,
        Action<string>? diagnosticLog
    )
    {
        var segA = a.Ground.AssignedTaxiRoute!.CurrentSegment!;
        var segB = b.Ground.AssignedTaxiRoute!.CurrentSegment!;

        double distAToTarget = DistToSegTarget(a, segA, layout);
        double distBToTarget = DistToSegTarget(b, segB, layout);

        diagnosticLog?.Invoke(
            $"  [SameEdgeTrailing] edge={segA.FromNodeId}→{segA.ToNodeId}: {a.Callsign} d2t={distAToTarget:F4}nm, {b.Callsign} d2t={distBToTarget:F4}nm"
        );

        if (distAToTarget > distBToTarget)
        {
            ApplyTrailLimit(a, b, distFt);
        }
        else
        {
            ApplyTrailLimit(b, a, distFt);
        }
    }

    private static void ResolveSameEdgeHeadOn(AircraftState a, AircraftState b, double distFt, Action<string>? diagnosticLog)
    {
        if (distFt > OppositeStopDistanceFt)
        {
            diagnosticLog?.Invoke($"  [SameEdgeHeadOn] {distFt:F0}ft > {OppositeStopDistanceFt:F0}ft, no action yet");
            return;
        }

        // Pick the holder deterministically so the pair doesn't deadlock. Real
        // ATC would re-route here, but the sim's job is at least to leave one
        // aircraft able to proceed instead of pinning both indefinitely.
        // Holder = aircraft with the higher remaining-segment count (more route
        // left to fly), since it has more reason to wait. Ties broken by callsign.
        var routeA = a.Ground.AssignedTaxiRoute;
        var routeB = b.Ground.AssignedTaxiRoute;
        int remA = routeA is null ? 0 : routeA.Segments.Count - routeA.CurrentSegmentIndex;
        int remB = routeB is null ? 0 : routeB.Segments.Count - routeB.CurrentSegmentIndex;

        AircraftState holder;
        AircraftState mover;
        if (remA != remB)
        {
            holder = remA > remB ? a : b;
            mover = remA > remB ? b : a;
        }
        else
        {
            holder = string.CompareOrdinal(a.Callsign, b.Callsign) >= 0 ? a : b;
            mover = ReferenceEquals(holder, a) ? b : a;
        }

        diagnosticLog?.Invoke($"  [SameEdgeHeadOn] dist={distFt:F0}ft, remA={remA}/{remB}, holder={holder.Callsign}, mover={mover.Callsign}");

        ApplyMinLimit(holder, 0, "same-edge head-on hold", mover, distFt);
        // mover gets no limit from this layer; closing-proximity (if it fires
        // next tick when the geometry shifts) is the safety net for actual
        // overlap, but the holder being stopped should resolve the standoff.
    }

    private static void ResolveConvergence(AircraftState a, AircraftState b, AirportGroundLayout layout, Action<string>? diagnosticLog)
    {
        var routeA = a.Ground.AssignedTaxiRoute!;
        var routeB = b.Ground.AssignedTaxiRoute!;

        int? sharedNodeId = FindSharedUpcomingNode(routeA, routeB);
        if (sharedNodeId is null || !layout.Nodes.TryGetValue(sharedNodeId.Value, out var node))
        {
            return;
        }

        double distA = GeoMath.DistanceNm(a.Position, node.Position);
        double distB = GeoMath.DistanceNm(b.Position, node.Position);
        double distAFt = distA * FtPerNm;
        double distBFt = distB * FtPerNm;
        double conflictDistFt = GeoMath.DistanceNm(a.Position, b.Position) * FtPerNm;

        AircraftState yielder = distA > distB ? a : b;
        AircraftState winner = distA > distB ? b : a;
        double yielderDistFt = Math.Max(distAFt, distBFt);

        double limitSpeed;
        if (conflictDistFt <= DefaultStopDistanceFt)
        {
            limitSpeed = 0;
        }
        else if (conflictDistFt <= ConvergenceSlowdownFt)
        {
            limitSpeed = SlowTaxiSpeedKts;
        }
        else
        {
            double t = Math.Clamp((yielderDistFt - ConvergenceSlowdownFt) / (ConvergenceLookaheadFt - ConvergenceSlowdownFt), 0, 1);
            limitSpeed = SlowTaxiSpeedKts + t * (15.0 - SlowTaxiSpeedKts);
        }

        diagnosticLog?.Invoke(
            $"  [Convergence] shared node={sharedNodeId.Value}: {a.Callsign} {distAFt:F0}ft away, {b.Callsign} {distBFt:F0}ft away → {yielder.Callsign} yields to {winner.Callsign}, pairDist={conflictDistFt:F0}ft, limit={limitSpeed:F1}"
        );

        ApplyMinLimit(yielder, limitSpeed, "convergence", winner, conflictDistFt);
        // No closing-proximity for the winner in v2 — convergence is
        // authoritative for this pair. The next tick reclassifies; if the
        // geometry shifts to truly crossing/head-on, that layer will catch it.
    }

    private static void ResolvePushbackYield(
        AircraftState a,
        MovementState stateA,
        double? dirA,
        AircraftState b,
        MovementState stateB,
        double? dirB,
        double distFt
    )
    {
        if (distFt > PushbackBufferFt)
        {
            return;
        }

        if (stateA == MovementState.Pushing && dirA is not null)
        {
            double bearing = GeoMath.BearingTo(a.Position, b.Position);
            if (HeadingDifference(dirA.Value, bearing) < 90)
            {
                ApplyMinLimit(a, 0, "pushback yield", b, distFt);
            }
        }

        if (stateB == MovementState.Pushing && dirB is not null)
        {
            double bearing = GeoMath.BearingTo(b.Position, a.Position);
            if (HeadingDifference(dirB.Value, bearing) < 90)
            {
                ApplyMinLimit(b, 0, "pushback yield", a, distFt);
            }
        }

        if (stateA != MovementState.Pushing && stateA != MovementState.Stationary && dirA is not null)
        {
            double bearing = GeoMath.BearingTo(a.Position, b.Position);
            if (HeadingDifference(dirA.Value, bearing) < 90)
            {
                ApplyTrailLimit(a, b, distFt);
            }
        }

        if (stateB != MovementState.Pushing && stateB != MovementState.Stationary && dirB is not null)
        {
            double bearing = GeoMath.BearingTo(b.Position, a.Position);
            if (HeadingDifference(dirB.Value, bearing) < 90)
            {
                ApplyTrailLimit(b, a, distFt);
            }
        }
    }

    /// <summary>
    /// Runs <see cref="ApplyClosingLimit"/> in both directions and (optionally)
    /// <see cref="ResolveHeadOn"/>. Used by the Crossing kind and as the safety
    /// net for Converging.
    /// </summary>
    private static void ApplyCrossingChecks(
        AircraftState a,
        MovementState stateA,
        double? dirA,
        AircraftState b,
        MovementState stateB,
        double? dirB,
        double distFt,
        bool skipHeadOn,
        Action<string>? diagnosticLog
    )
    {
        if (dirA is not null)
        {
            ApplyClosingLimit(a, dirA.Value, b, stateB, distFt, diagnosticLog);
        }
        if (dirB is not null)
        {
            ApplyClosingLimit(b, dirB.Value, a, stateA, distFt, diagnosticLog);
        }
        if (!skipHeadOn && dirA is not null && dirB is not null && a.GroundSpeed > 0 && b.GroundSpeed > 0)
        {
            ResolveHeadOn(a, dirA.Value, b, dirB.Value, distFt);
        }
    }

    /// <summary>
    /// Computes the closing-proximity speed limit <paramref name="mover"/> should
    /// receive for <paramref name="obstacle"/>, or null when no limit applies (not
    /// closing, can pass laterally, or the on-runway exemption). Pure — does not
    /// mutate state — so a caller can arbitrate between the two directions before
    /// committing a limit (see <see cref="ResolveCrossing"/>).
    /// </summary>
    private static (double Limit, string Reason)? ComputeClosingLimit(
        AircraftState mover,
        double moveDir,
        AircraftState obstacle,
        MovementState obstacleState,
        double distFt,
        Action<string>? diagnosticLog
    )
    {
        double bearing = GeoMath.BearingTo(mover.Position, obstacle.Position);
        double angleDiff = HeadingDifference(moveDir, bearing);
        if (angleDiff >= 90)
        {
            diagnosticLog?.Invoke($"    [Closing] {mover.Callsign}→{obstacle.Callsign}: diff={angleDiff:F0}° ≥90, not closing");
            return null;
        }

        double? moverWing = FaaAircraftDatabase.Get(mover.AircraftType)?.WingspanFt;
        double? obstacleWing = FaaAircraftDatabase.Get(obstacle.AircraftType)?.WingspanFt;
        bool isStationary = obstacleState == MovementState.Stationary;
        bool stationaryGate = !WingspanLateralCheckRequireStationary || isStationary;
        if (WingspanLateralCheckEnabled && stationaryGate && moverWing.HasValue && obstacleWing.HasValue)
        {
            double lateralFt = distFt * Math.Sin(angleDiff * Math.PI / 180.0);
            double requiredLateralFt = (moverWing.Value / 2) + (obstacleWing.Value / 2) + WingtipBufferFt;
            if (lateralFt > requiredLateralFt)
            {
                diagnosticLog?.Invoke(
                    $"    [Closing] {mover.Callsign}→{obstacle.Callsign}: lateral={lateralFt:F0}ft > clearance({requiredLateralFt:F0}ft), can pass"
                );
                return null;
            }
        }

        if (IsOnRunway(mover) && !IsOnRunway(obstacle) && obstacle.GroundSpeed <= 0)
        {
            diagnosticLog?.Invoke($"    [Closing] {mover.Callsign}→{obstacle.Callsign}: mover on runway, obstacle off-runway, skip");
            return null;
        }

        var (stopDist, trailDist) = GetSeparation(obstacle, mover);
        if (distFt <= stopDist)
        {
            diagnosticLog?.Invoke($"    [Closing] {mover.Callsign}→{obstacle.Callsign}: {distFt:F0}ft ≤ stop({stopDist:F0}ft) → limit=0");
            return (0, "proximity stop");
        }

        if (distFt <= trailDist)
        {
            double limitSpeed = Math.Max(obstacle.GroundSpeed, SlowTaxiSpeedKts);
            diagnosticLog?.Invoke(
                $"    [Closing] {mover.Callsign}→{obstacle.Callsign}: {distFt:F0}ft ≤ trail({trailDist:F0}ft) → limit={limitSpeed:F1}"
            );
            return (limitSpeed, "proximity trail");
        }

        return null;
    }

    private static void ApplyClosingLimit(
        AircraftState mover,
        double moveDir,
        AircraftState obstacle,
        MovementState obstacleState,
        double distFt,
        Action<string>? diagnosticLog
    )
    {
        if (ComputeClosingLimit(mover, moveDir, obstacle, obstacleState, distFt, diagnosticLog) is { } result)
        {
            ApplyMinLimit(mover, result.Limit, result.Reason, obstacle, distFt);
        }
    }

    /// <summary>
    /// Resolve a Crossing pair (close in space, paths not on a shared edge or node).
    /// Real ground ops resolve a path conflict to "one holds, one goes" — never a
    /// symmetric mutual crawl, and never an indefinite creep (7110.65 3-7-2 HOLD/
    /// FOLLOW phraseology; AIM 4-3-18.b pilot give-way = a definite stop, not a
    /// crawl). Three rules:
    /// <list type="number">
    /// <item>An aircraft on the runway surface has priority — a plain ground crosser
    /// yields. Never strand an aircraft clearing the runway (AIM 4-3-21.a).</item>
    /// <item>Closing direction uses the aircraft's heading even when it is momentarily
    /// stopped (a taxiing/exiting aircraft that has braked for the conflict), so its
    /// hold stays latched instead of clearing and re-pinning each tick. Genuinely
    /// parked/held aircraft contribute no closing direction — they remain passable
    /// obstacles.</item>
    /// <item>If both would have to stop for each other (a crossing collision course),
    /// pick ONE deterministic holder (callsign) and let the other proceed, instead of
    /// stopping both into a slow-motion gridlock.</item>
    /// </list>
    /// </summary>
    private static void ResolveCrossing(
        AircraftState a,
        MovementState stateA,
        double? dirA,
        AircraftState b,
        MovementState stateB,
        double? dirB,
        double distFt,
        Action<string>? diagnosticLog
    )
    {
        // Heading-based closing direction: a non-parked aircraft keeps a direction
        // even at gs=0 so a yielder that has braked for the conflict stays pinned.
        double? closeDirA = IsParkedOrHeld(a) ? null : a.TrueHeading.Degrees;
        double? closeDirB = IsParkedOrHeld(b) ? null : b.TrueHeading.Degrees;

        // Rule 1: an aircraft on the runway surface proceeds; the other yields.
        if (IsOnRunway(a) != IsOnRunway(b))
        {
            var onRunway = IsOnRunway(a) ? a : b;
            var onRunwayDir = IsOnRunway(a) ? closeDirA : closeDirB;
            var onRunwayState = IsOnRunway(a) ? stateA : stateB;
            var yielder = IsOnRunway(a) ? b : a;
            var yielderDir = IsOnRunway(a) ? closeDirB : closeDirA;
            var yielderState = IsOnRunway(a) ? stateB : stateA;

            // Only override when the yielder can give way (a mover); a genuinely
            // parked obstacle keeps the normal closing/lateral treatment so the
            // runway aircraft still stops rather than driving through it.
            if (yielderDir is { } yd2)
            {
                bool conflict =
                    (onRunwayDir is { } od && ComputeClosingLimit(onRunway, od, yielder, yielderState, distFt, null) is not null)
                    || ComputeClosingLimit(yielder, yd2, onRunway, onRunwayState, distFt, null) is not null;
                if (conflict)
                {
                    diagnosticLog?.Invoke($"  [Crossing] {onRunway.Callsign} on runway → {yielder.Callsign} yields");
                    ApplyMinLimit(yielder, 0, "yield to runway aircraft", onRunway, distFt);
                }

                return;
            }
        }

        var limitForA = closeDirA is { } da ? ComputeClosingLimit(a, da, b, stateB, distFt, diagnosticLog) : null;
        var limitForB = closeDirB is { } db ? ComputeClosingLimit(b, db, a, stateA, distFt, diagnosticLog) : null;

        if (limitForA is { Limit: <= 0 } && limitForB is { Limit: <= 0 })
        {
            // Crossing collision course: both would stop. Pick one holder
            // deterministically so one proceeds instead of a mutual deadlock.
            var holder = string.CompareOrdinal(a.Callsign, b.Callsign) >= 0 ? a : b;
            var mover = ReferenceEquals(holder, a) ? b : a;
            diagnosticLog?.Invoke($"  [Crossing] mutual stop: {holder.Callsign} holds, {mover.Callsign} proceeds");
            ApplyMinLimit(holder, 0, "crossing hold", mover, distFt);
        }
        else
        {
            if (limitForA is { } resultA)
            {
                ApplyMinLimit(a, resultA.Limit, resultA.Reason, b, distFt);
            }
            if (limitForB is { } resultB)
            {
                ApplyMinLimit(b, resultB.Limit, resultB.Reason, a, distFt);
            }
        }

        // Head-on fallback: two aircraft actually moving toward each other.
        if (closeDirA is { } da3 && closeDirB is { } db3 && a.GroundSpeed > 0 && b.GroundSpeed > 0)
        {
            ResolveHeadOn(a, da3, b, db3, distFt);
        }
    }

    private static void ResolveHeadOn(AircraftState a, double dirA, AircraftState b, double dirB, double distFt)
    {
        double headingDiff = HeadingDifference(dirA, dirB);
        if (headingDiff <= 120)
        {
            return;
        }

        if (distFt > OppositeStopDistanceFt)
        {
            return;
        }

        double bearingAtoB = GeoMath.BearingTo(a.Position, b.Position);
        if (HeadingDifference(dirA, bearingAtoB) < 90)
        {
            ApplyMinLimit(a, 0, "head-on", b, distFt);
            ApplyMinLimit(b, 0, "head-on", a, distFt);
        }
    }

    // --- Helpers ---

    private static bool IsOnRunway(AircraftState ac)
    {
        string? phase = ac.Phases?.CurrentPhase?.Name;
        if (phase is "Landing" or "Takeoff" or "LiningUp" or "LinedUpAndWaiting" or "StopAndGo" or "TouchAndGo")
        {
            return true;
        }

        if (phase is "Runway Exit" && ac.Phases?.CurrentPhase is RunwayExitPhase rep)
        {
            return rep.IsOnCenterline;
        }

        return false;
    }

    /// <summary>
    /// True when the aircraft is intentionally stationary — parked, holding, or
    /// lining up — by phase. These aircraft are passable obstacles, not active
    /// movers, so they contribute no closing direction in crossing resolution.
    /// </summary>
    private static bool IsStationaryPhase(string? phaseName) =>
        phaseName is "At Parking" or "Holding After Pushback" or "Holding After Exit" or "Holding In Position" or "LinedUpAndWaiting" or "LiningUp"
        || (phaseName is not null && phaseName.StartsWith("Holding Short", StringComparison.Ordinal));

    /// <summary>
    /// True when the aircraft is parked, holding, lining up, or under any controller
    /// hold. Such an aircraft is genuinely at rest (a passable obstacle); a taxiing or
    /// runway-exiting aircraft momentarily stopped for a conflict is NOT — it is
    /// yielding and keeps its heading-based closing direction.
    /// </summary>
    private static bool IsParkedOrHeld(AircraftState ac) => ac.Ground.IsImmobile || IsStationaryPhase(ac.Phases?.CurrentPhase?.Name);

    private static (double StopFt, double TrailFt) GetSeparation(AircraftState leader, AircraftState trailer)
    {
        double leaderLength = FaaAircraftDatabase.Get(leader.AircraftType)?.LengthFt ?? DefaultAircraftLengthFt;
        double trailerLength = FaaAircraftDatabase.Get(trailer.AircraftType)?.LengthFt ?? DefaultAircraftLengthFt;
        double stopDist = Math.Max(DefaultStopDistanceFt, ((leaderLength + trailerLength) / 2) + StopBufferFt);
        double trailDist = Math.Max(DefaultTrailDistanceFt, stopDist + 100.0);
        return (stopDist, trailDist);
    }

    private static void ApplyTrailLimit(AircraftState trailer, AircraftState leader, double distFt)
    {
        var (stopDist, trailDist) = GetSeparation(leader, trailer);
        double maxSpeed;
        string reason;
        if (distFt <= stopDist)
        {
            maxSpeed = 0;
            reason = "trail stop";
        }
        else if (distFt <= trailDist)
        {
            maxSpeed = leader.GroundSpeed;
            reason = "trail match";
        }
        else
        {
            return;
        }

        ApplyMinLimit(trailer, maxSpeed, reason, leader, distFt);
    }

    private static void ApplyMinLimit(
        AircraftState aircraft,
        double maxSpeed,
        string? reason = null,
        AircraftState? other = null,
        double? distFt = null
    )
    {
        double? existing = aircraft.Ground.SpeedLimit;
        if (existing is { } ex)
        {
            aircraft.Ground.SpeedLimit = Math.Min(ex, maxSpeed);
        }
        else
        {
            aircraft.Ground.SpeedLimit = maxSpeed;
        }

        if (existing is null || maxSpeed < existing)
        {
            Log.LogTrace(
                "[Conflict] {Callsign}: limit={Limit:F0}kts, reason={Reason}, other={Other}, dist={Dist:F0}ft",
                aircraft.Callsign,
                maxSpeed,
                reason ?? "proximity",
                other?.Callsign ?? "?",
                distFt ?? 0
            );
        }
    }

    private static double HeadingDifference(double h1, double h2)
    {
        double diff = Math.Abs(h1 - h2);
        if (diff > 180)
        {
            diff = 360 - diff;
        }
        return diff;
    }

    private static double DistToSegTarget(AircraftState ac, TaxiRouteSegment seg, AirportGroundLayout layout)
    {
        if (layout.Nodes.TryGetValue(seg.ToNodeId, out var node))
        {
            return GeoMath.DistanceNm(ac.Position, node.Position);
        }

        return seg.Edge.DistanceNm;
    }

    private static int? FindSharedUpcomingNode(TaxiRoute routeA, TaxiRoute routeB)
    {
        double lookaheadNm = ConvergenceLookaheadFt / FtPerNm;

        var nodesA = new Dictionary<int, int>();
        double cumulativeA = 0;
        for (int i = routeA.CurrentSegmentIndex; i < routeA.Segments.Count; i++)
        {
            var seg = routeA.Segments[i];
            cumulativeA += seg.Edge.DistanceNm;
            if (cumulativeA > lookaheadNm)
            {
                break;
            }
            nodesA.TryAdd(seg.ToNodeId, seg.FromNodeId);
        }

        double cumulativeB = 0;
        for (int i = routeB.CurrentSegmentIndex; i < routeB.Segments.Count; i++)
        {
            var seg = routeB.Segments[i];
            cumulativeB += seg.Edge.DistanceNm;
            if (cumulativeB > lookaheadNm)
            {
                break;
            }

            if (nodesA.TryGetValue(seg.ToNodeId, out int fromA) && fromA != seg.FromNodeId)
            {
                return seg.ToNodeId;
            }
        }

        return null;
    }

    private static bool ShareUpcomingNode(AircraftState subject, AircraftState reference)
    {
        var routeA = subject.Ground.AssignedTaxiRoute;
        var routeB = reference.Ground.AssignedTaxiRoute;
        if (routeA is null || routeB is null)
        {
            return false;
        }

        return FindSharedUpcomingNode(routeA, routeB) is not null;
    }
}
