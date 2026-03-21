using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim;

/// <summary>
/// Detects and resolves ground conflicts between aircraft.
/// Called once per tick before physics. Writes GroundSpeedLimit onto
/// each affected AircraftState so phases and physics respect it.
///
/// Movement state classification:
///   Stationary — gs=0 or parked/holding phase. No direction. Just an obstacle.
///   Taxiing    — has AssignedTaxiRoute with CurrentSegment. Direction from graph.
///   Pushing    — PushbackTrueHeading is set. Direction = PushbackTrueHeading.
///   Following  — FollowingPhase. Exempt (manages own separation).
///   Untracked  — moving on ground, no route, no pushback. Direction = Heading (fallback).
/// </summary>
public static class GroundConflictDetector
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundConflictDetector");
    private const double DefaultTrailDistanceFt = 200.0;
    private const double DefaultStopDistanceFt = 100.0;
    private const double DefaultAircraftLengthFt = 60.0;
    private const double StopBufferFt = 25.0;
    private const double OppositeStopDistanceFt = 300.0;
    private const double PushbackBufferFt = 200.0;
    private const double SlowTaxiSpeedKts = 5.0;
    private const double SearchRangeNm = 0.1;
    private const double FtPerNm = 6076.12;
    private const double ConvergenceLookaheadFt = 1500.0;
    private const double ConvergenceSlowdownFt = 400.0;

    private enum MovementState
    {
        Stationary,
        Taxiing,
        Pushing,
        Following,
        Untracked,
    }

    /// <summary>
    /// Detect ground conflicts and set GroundSpeedLimit on affected aircraft.
    /// Clears all limits first, then classifies movement state, then checks pairs.
    /// Aircraft with an active BREAK override are exempt from all conflict limits.
    /// </summary>
    public static void ApplySpeedLimits(
        List<AircraftState> aircraft,
        AirportGroundLayout? layout,
        double deltaSeconds = 0,
        Action<string>? diagnosticLog = null
    )
    {
        // Clear previous limits and tick down BREAK timers
        for (int i = 0; i < aircraft.Count; i++)
        {
            aircraft[i].GroundSpeedLimit = null;

            if (aircraft[i].ConflictBreakRemainingSeconds > 0)
            {
                aircraft[i].ConflictBreakRemainingSeconds = Math.Max(0, aircraft[i].ConflictBreakRemainingSeconds - deltaSeconds);
            }
        }

        // Build classification array for ground aircraft only
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
            diagnosticLog?.Invoke(
                $"[Classify] {ac.Callsign}: {state}, dir={dir?.ToString("F0") ?? "null"}, gs={ac.GroundSpeed:F1}, phase={ac.Phases?.CurrentPhase?.Name ?? "null"}, route={ac.AssignedTaxiRoute?.CurrentSegmentIndex.ToString() ?? "null"}/{ac.AssignedTaxiRoute?.Segments.Count.ToString() ?? "null"}"
            );
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var (a, stateA, dirA) = entries[i];

            // Following aircraft are exempt from all conflict limits
            if (stateA == MovementState.Following)
            {
                continue;
            }

            // BREAK: aircraft ignoring conflicts
            if (a.ConflictBreakRemainingSeconds > 0)
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

                // BREAK: aircraft ignoring conflicts
                if (b.ConflictBreakRemainingSeconds > 0)
                {
                    continue;
                }

                // Both stationary — nothing to do
                if (stateA == MovementState.Stationary && stateB == MovementState.Stationary)
                {
                    diagnosticLog?.Invoke($"[Pair] {a.Callsign}+{b.Callsign}: both stationary, skip");
                    continue;
                }

                double distNm = GeoMath.DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
                if (distNm > SearchRangeNm)
                {
                    continue;
                }

                double distFt = distNm * FtPerNm;
                diagnosticLog?.Invoke($"[Pair] {a.Callsign}({stateA})+{b.Callsign}({stateB}): dist={distFt:F0}ft");

                // 1. Same-edge (both taxiing with layout)
                if (layout is not null && stateA == MovementState.Taxiing && stateB == MovementState.Taxiing)
                {
                    if (TrySameEdge(a, b, distFt, diagnosticLog))
                    {
                        continue;
                    }

                    if (TryConvergence(a, b, layout, diagnosticLog))
                    {
                        continue;
                    }
                }

                // 2. Pushback yield
                if (stateA == MovementState.Pushing || stateB == MovementState.Pushing)
                {
                    ResolvePushbackYield(a, stateA, dirA, b, stateB, dirB, distFt);
                    continue;
                }

                // 3. Proximity-to-stationary
                if (stateA == MovementState.Stationary || stateB == MovementState.Stationary)
                {
                    ResolveProximityToStationary(a, stateA, dirA, b, stateB, dirB, distFt, diagnosticLog);
                    continue;
                }

                // 4. Head-on fallback (both moving, no layout or untracked)
                if (dirA is not null && dirB is not null && a.GroundSpeed > 0 && b.GroundSpeed > 0)
                {
                    ResolveHeadOn(a, dirA.Value, b, dirB.Value, distFt);
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
        double distNm = GeoMath.DistanceNm(subject.Latitude, subject.Longitude, reference.Latitude, reference.Longitude);
        double distFt = distNm * FtPerNm;

        var (refState, _) = Classify(reference);

        // Pushing aircraft: buffer zone
        if (refState == MovementState.Pushing)
        {
            return distFt > PushbackBufferFt;
        }

        // Both taxiing with layout: check shared upcoming nodes
        var (subState, _) = Classify(subject);
        if (layout is not null && subState == MovementState.Taxiing && refState == MovementState.Taxiing)
        {
            return !ShareUpcomingNode(subject, reference);
        }

        // Stationary reference: check if subject is closing
        if (refState == MovementState.Stationary)
        {
            var (_, subDir) = Classify(subject);
            if (subDir is null || subject.GroundSpeed <= 0)
            {
                return true;
            }

            var (_, refTrailDist) = GetSeparation(reference);
            if (distFt > refTrailDist)
            {
                return true;
            }

            double bearing = GeoMath.BearingTo(subject.Latitude, subject.Longitude, reference.Latitude, reference.Longitude);
            return HeadingDifference(subDir.Value, bearing) >= 90;
        }

        return distFt > OppositeStopDistanceFt;
    }

    // --- Classification ---

    private static (MovementState State, double? MoveDirection) Classify(AircraftState ac)
    {
        string? phaseName = ac.Phases?.CurrentPhase?.Name;

        // Following phase — exempt
        if (phaseName is not null && phaseName.StartsWith("Following", StringComparison.Ordinal))
        {
            return (MovementState.Following, null);
        }

        // Stationary: specific holding/parked phases
        bool isStationaryPhase =
            phaseName is "At Parking" or "Holding After Pushback" or "LinedUpAndWaiting" or "LiningUp"
            || (phaseName is not null && phaseName.StartsWith("Holding Short", StringComparison.Ordinal));

        if (isStationaryPhase)
        {
            return (MovementState.Stationary, null);
        }

        // Pushing: has PushbackTrueHeading
        if (ac.PushbackTrueHeading is { } pushHdg)
        {
            return (MovementState.Pushing, pushHdg.Degrees);
        }

        // Taxiing: has route with current segment (even if GS=0 due to prior speed limit)
        if (ac.AssignedTaxiRoute?.CurrentSegment is not null)
        {
            double dir = GetSegmentDirection(ac);
            return (MovementState.Taxiing, dir);
        }

        // Stationary: gs=0 without active route or phase
        if (ac.GroundSpeed <= 0)
        {
            return (MovementState.Stationary, null);
        }

        // Untracked: moving but no route or pushback
        return (MovementState.Untracked, ac.TrueHeading.Degrees);
    }

    private static double GetSegmentDirection(AircraftState ac)
    {
        // Direction = bearing from current position toward the segment's target node.
        // We don't have the node coordinates directly on the segment, but we can
        // look ahead: if the next segment exists, use its from-node direction too.
        // For simplicity, use the aircraft's heading as a reasonable proxy when
        // the segment is very short (aircraft is near the target node).
        // The primary benefit is for non-trivial segments where the aircraft is
        // traveling along a known edge.

        // Use bearing from current position to the approximate target.
        // We need node positions — but we only have node IDs on the segment.
        // Since we don't have the layout reference here, fall back to heading.
        // The layout-aware checks (same-edge, convergence) use node IDs directly.
        return ac.TrueHeading.Degrees;
    }

    // --- Conflict resolution ---

    private static bool TrySameEdge(AircraftState a, AircraftState b, double distFt, Action<string>? diagnosticLog = null)
    {
        var segA = a.AssignedTaxiRoute?.CurrentSegment;
        var segB = b.AssignedTaxiRoute?.CurrentSegment;
        if (segA is null || segB is null)
        {
            return false;
        }

        // Same edge: both share the same from/to node pair (either direction)
        bool sameEdge =
            (segA.FromNodeId == segB.FromNodeId && segA.ToNodeId == segB.ToNodeId)
            || (segA.FromNodeId == segB.ToNodeId && segA.ToNodeId == segB.FromNodeId);

        if (!sameEdge)
        {
            diagnosticLog?.Invoke(
                $"  [SameEdge] {a.Callsign} seg={segA.FromNodeId}→{segA.ToNodeId}, {b.Callsign} seg={segB.FromNodeId}→{segB.ToNodeId}: not same edge"
            );
            return false;
        }

        // Same direction on the edge: same ToNodeId
        if (segA.ToNodeId == segB.ToNodeId)
        {
            // Trailing: the one farther from the target node slows down
            double distAToTarget = DistToSegTarget(segA);
            double distBToTarget = DistToSegTarget(segB);

            diagnosticLog?.Invoke(
                $"  [SameEdge] same-dir edge {segA.FromNodeId}→{segA.ToNodeId}: {a.Callsign} distToTgt={distAToTarget:F4}nm, {b.Callsign} distToTgt={distBToTarget:F4}nm"
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
        else
        {
            diagnosticLog?.Invoke($"  [SameEdge] head-on on edge, dist={distFt:F0}ft");
            // Opposite direction on same edge — head-on, both stop
            if (distFt <= OppositeStopDistanceFt)
            {
                ApplyMinLimit(a, 0, "same-edge head-on", b, distFt);
                ApplyMinLimit(b, 0, "same-edge head-on", a, distFt);
            }
        }

        return true;
    }

    private static bool TryConvergence(AircraftState a, AircraftState b, AirportGroundLayout layout, Action<string>? diagnosticLog = null)
    {
        var routeA = a.AssignedTaxiRoute;
        var routeB = b.AssignedTaxiRoute;
        if (routeA is null || routeB is null)
        {
            return false;
        }

        // Collect upcoming node IDs for A (up to N segments ahead)
        int? sharedNodeId = FindSharedUpcomingNode(routeA, routeB);
        if (sharedNodeId is null)
        {
            diagnosticLog?.Invoke($"  [Convergence] {a.Callsign}+{b.Callsign}: no shared upcoming node within {ConvergenceLookaheadFt:F0}ft");
            return false;
        }

        // Both converging on the same node — the one further away yields
        if (!layout.Nodes.TryGetValue(sharedNodeId.Value, out var node))
        {
            return false;
        }

        double distA = GeoMath.DistanceNm(a.Latitude, a.Longitude, node.Latitude, node.Longitude);
        double distB = GeoMath.DistanceNm(b.Latitude, b.Longitude, node.Latitude, node.Longitude);
        double distAFt = distA * FtPerNm;
        double distBFt = distB * FtPerNm;

        double conflictDistFt = GeoMath.DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude) * FtPerNm;

        AircraftState yielder = distA > distB ? a : b;
        AircraftState winner = distA > distB ? b : a;
        double yielderDistFt = Math.Max(distAFt, distBFt);

        // Graduated speed: full stop when close, slow taxi when further
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
            // Scale linearly from slow taxi to normal taxi speed based on distance to shared node
            double t = Math.Clamp((yielderDistFt - ConvergenceSlowdownFt) / (ConvergenceLookaheadFt - ConvergenceSlowdownFt), 0, 1);
            limitSpeed = SlowTaxiSpeedKts + t * (15.0 - SlowTaxiSpeedKts);
        }

        diagnosticLog?.Invoke(
            $"  [Convergence] shared node={sharedNodeId.Value}: {a.Callsign} {distAFt:F0}ft away, {b.Callsign} {distBFt:F0}ft away → {yielder.Callsign} yields to {winner.Callsign}, pairDist={conflictDistFt:F0}ft, limit={limitSpeed:F1}"
        );

        ApplyMinLimit(yielder, limitSpeed, "convergence", winner, conflictDistFt);

        return true;
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

        // Pushing aircraft only yields if it's actually closing on the other
        if (stateA == MovementState.Pushing && dirA is not null)
        {
            double bearing = GeoMath.BearingTo(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
            if (HeadingDifference(dirA.Value, bearing) < 90)
            {
                ApplyMinLimit(a, 0, "pushback yield", b, distFt);
            }
        }

        if (stateB == MovementState.Pushing && dirB is not null)
        {
            double bearing = GeoMath.BearingTo(b.Latitude, b.Longitude, a.Latitude, a.Longitude);
            if (HeadingDifference(dirB.Value, bearing) < 90)
            {
                ApplyMinLimit(b, 0, "pushback yield", a, distFt);
            }
        }

        // Moving aircraft approaching a pushing aircraft also slows
        if (stateA != MovementState.Pushing && stateA != MovementState.Stationary && dirA is not null)
        {
            double bearing = GeoMath.BearingTo(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
            if (HeadingDifference(dirA.Value, bearing) < 90)
            {
                ApplyTrailLimit(a, b, distFt);
            }
        }

        if (stateB != MovementState.Pushing && stateB != MovementState.Stationary && dirB is not null)
        {
            double bearing = GeoMath.BearingTo(b.Latitude, b.Longitude, a.Latitude, a.Longitude);
            if (HeadingDifference(dirB.Value, bearing) < 90)
            {
                ApplyTrailLimit(b, a, distFt);
            }
        }
    }

    private static void ResolveProximityToStationary(
        AircraftState a,
        MovementState stateA,
        double? dirA,
        AircraftState b,
        MovementState stateB,
        double? dirB,
        double distFt,
        Action<string>? diagnosticLog = null
    )
    {
        diagnosticLog?.Invoke(
            $"  [Proximity] {a.Callsign}({stateA},gs={a.GroundSpeed:F1})+{b.Callsign}({stateB},gs={b.GroundSpeed:F1}): dist={distFt:F0}ft"
        );

        // Identify which is moving and which is stationary
        if (stateA != MovementState.Stationary && dirA is not null && a.GroundSpeed > 0)
        {
            ApplyClosingLimit(a, dirA.Value, b, distFt, diagnosticLog);
        }

        if (stateB != MovementState.Stationary && dirB is not null && b.GroundSpeed > 0)
        {
            ApplyClosingLimit(b, dirB.Value, a, distFt, diagnosticLog);
        }
    }

    private static void ApplyClosingLimit(
        AircraftState mover,
        double moveDir,
        AircraftState obstacle,
        double distFt,
        Action<string>? diagnosticLog = null
    )
    {
        double bearing = GeoMath.BearingTo(mover.Latitude, mover.Longitude, obstacle.Latitude, obstacle.Longitude);
        double angleDiff = HeadingDifference(moveDir, bearing);
        if (angleDiff >= 90)
        {
            diagnosticLog?.Invoke(
                $"    [Closing] {mover.Callsign}→{obstacle.Callsign}: dir={moveDir:F0} bearing={bearing:F0} diff={angleDiff:F0}° ≥90, not closing"
            );
            return;
        }

        var (stopDist, trailDist) = GetSeparation(obstacle);
        if (distFt <= stopDist)
        {
            diagnosticLog?.Invoke($"    [Closing] {mover.Callsign}→{obstacle.Callsign}: {distFt:F0}ft ≤ stop({stopDist:F0}ft) → limit=0");
            ApplyMinLimit(mover, 0, "proximity stop", obstacle, distFt);
        }
        else if (distFt <= trailDist)
        {
            double limitSpeed = obstacle.GroundSpeed;
            if (limitSpeed < SlowTaxiSpeedKts)
            {
                limitSpeed = SlowTaxiSpeedKts;
            }

            diagnosticLog?.Invoke(
                $"    [Closing] {mover.Callsign}→{obstacle.Callsign}: {distFt:F0}ft ≤ trail({trailDist:F0}ft) → limit={limitSpeed:F1}"
            );
            ApplyMinLimit(mover, limitSpeed, "proximity trail", obstacle, distFt);
        }
        else
        {
            diagnosticLog?.Invoke($"    [Closing] {mover.Callsign}→{obstacle.Callsign}: {distFt:F0}ft > trail({trailDist:F0}ft), no limit");
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

        // Check that they're actually closing
        double bearingAtoB = GeoMath.BearingTo(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
        if (HeadingDifference(dirA, bearingAtoB) < 90)
        {
            ApplyMinLimit(a, 0, "head-on", b, distFt);
            ApplyMinLimit(b, 0, "head-on", a, distFt);
        }
    }

    // --- Helpers ---

    /// <summary>
    /// Returns dimension-aware stop/trail distances based on the leader aircraft's length
    /// from the FAA Aircraft Characteristics Database.
    /// Stop distance = aircraft length + buffer, so the trailing aircraft's nose clears the leader's tail.
    /// Trail distance = stop distance + deceleration margin.
    /// </summary>
    private static (double StopFt, double TrailFt) GetSeparation(AircraftState leader)
    {
        double leaderLength = FaaAircraftDatabase.Get(leader.AircraftType)?.LengthFt ?? DefaultAircraftLengthFt;
        double stopDist = Math.Max(DefaultStopDistanceFt, leaderLength + StopBufferFt);
        double trailDist = Math.Max(DefaultTrailDistanceFt, stopDist + 100.0);
        return (stopDist, trailDist);
    }

    private static void ApplyTrailLimit(AircraftState trailer, AircraftState leader, double distFt)
    {
        var (stopDist, trailDist) = GetSeparation(leader);
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
        double? existing = aircraft.GroundSpeedLimit;
        if (existing is { } ex)
        {
            aircraft.GroundSpeedLimit = Math.Min(ex, maxSpeed);
        }
        else
        {
            aircraft.GroundSpeedLimit = maxSpeed;
        }

        // Only log when this call actually set or lowered the limit
        if (existing is null || maxSpeed < existing)
        {
            Log.LogDebug(
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

    private static double DistToSegTarget(TaxiRouteSegment seg)
    {
        // Approximate: bearing-based distance to segment's ToNode is
        // not available without layout. Use the edge distance minus traversed.
        // Simplest proxy: just use the full edge distance for ordering,
        // but since both aircraft are on the same edge, the one further from
        // the target is the one that traversed less. We can approximate by
        // position on the edge (further from ToNode = bigger distance).
        // Without node coords, compare distance from start of edge.
        return seg.Edge.DistanceNm;
    }

    /// <summary>
    /// Find a node that both routes will pass through within <see cref="ConvergenceLookaheadFt"/>,
    /// but only if the two routes arrive from different edges (true convergence from different
    /// directions). If both routes reach the shared node via the same FromNodeId, it's a
    /// following/trailing situation — not a convergence.
    /// </summary>
    private static int? FindSharedUpcomingNode(TaxiRoute routeA, TaxiRoute routeB)
    {
        double lookaheadNm = ConvergenceLookaheadFt / FtPerNm;

        // Collect upcoming target nodes for A within distance-based lookahead,
        // recording which FromNodeId leads into each target node.
        var nodesA = new Dictionary<int, int>(); // ToNodeId → FromNodeId
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

        // Check if B shares any upcoming target node within distance-based lookahead,
        // but only if B arrives from a different edge than A (different FromNodeId).
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
        var routeA = subject.AssignedTaxiRoute;
        var routeB = reference.AssignedTaxiRoute;
        if (routeA is null || routeB is null)
        {
            return false;
        }

        return FindSharedUpcomingNode(routeA, routeB) is not null;
    }
}
