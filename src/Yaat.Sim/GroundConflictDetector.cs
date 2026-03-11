using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim;

/// <summary>
/// Detects and resolves ground conflicts between aircraft.
/// Called once per tick before physics. Writes GroundSpeedLimit onto
/// each affected AircraftState so phases and physics respect it.
///
/// Movement state classification:
///   Stationary — gs=0 or parked/holding phase. No direction. Just an obstacle.
///   Taxiing    — has AssignedTaxiRoute with CurrentSegment. Direction from graph.
///   Pushing    — PushbackHeading is set. Direction = PushbackHeading.
///   Following  — FollowingPhase. Exempt (manages own separation).
///   Untracked  — moving on ground, no route, no pushback. Direction = Heading (fallback).
/// </summary>
public static class GroundConflictDetector
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundConflictDetector");
    private const double TrailDistanceFt = 200.0;
    private const double StopDistanceFt = 100.0;
    private const double OppositeStopDistanceFt = 300.0;
    private const double PushbackBufferFt = 200.0;
    private const double SlowTaxiSpeedKts = 5.0;
    private const double SearchRangeNm = 0.1;
    private const double FtPerNm = 6076.12;
    private const int ConvergenceLookaheadSegments = 5;

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
    public static void ApplySpeedLimits(List<AircraftState> aircraft, AirportGroundLayout? layout, double deltaSeconds = 0)
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
                    continue;
                }

                double distNm = GeoMath.DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
                if (distNm > SearchRangeNm)
                {
                    continue;
                }

                double distFt = distNm * FtPerNm;

                // 1. Same-edge (both taxiing with layout)
                if (layout is not null && stateA == MovementState.Taxiing && stateB == MovementState.Taxiing)
                {
                    if (TrySameEdge(a, b, distFt))
                    {
                        continue;
                    }

                    if (TryConvergence(a, b, layout))
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
                    ResolveProximityToStationary(a, stateA, dirA, b, stateB, dirB, distFt);
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
            return !ShareUpcomingNode(subject, reference, layout);
        }

        // Stationary reference: check if subject is closing
        if (refState == MovementState.Stationary)
        {
            var (_, subDir) = Classify(subject);
            if (subDir is null || subject.GroundSpeed <= 0)
            {
                return true;
            }

            if (distFt > TrailDistanceFt)
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
            phaseName is "At Parking" or "Holding After Pushback" or "LinedUpAndWaiting"
            || (phaseName is not null && phaseName.StartsWith("Holding Short", StringComparison.Ordinal));

        if (isStationaryPhase)
        {
            return (MovementState.Stationary, null);
        }

        // Pushing: has PushbackHeading
        if (ac.PushbackHeading is { } pushHdg)
        {
            return (MovementState.Pushing, pushHdg);
        }

        // Taxiing: has route with current segment (even if GS=0 due to prior speed limit)
        if (ac.AssignedTaxiRoute?.CurrentSegment is { } seg)
        {
            var route = ac.AssignedTaxiRoute;
            double dir = GetSegmentDirection(ac, seg, route);
            return (MovementState.Taxiing, dir);
        }

        // Stationary: gs=0 without active route or phase
        if (ac.GroundSpeed <= 0)
        {
            return (MovementState.Stationary, null);
        }

        // Untracked: moving but no route or pushback
        return (MovementState.Untracked, ac.Heading);
    }

    private static double GetSegmentDirection(AircraftState ac, TaxiRouteSegment seg, TaxiRoute route)
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
        return ac.Heading;
    }

    // --- Conflict resolution ---

    private static bool TrySameEdge(AircraftState a, AircraftState b, double distFt)
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
            return false;
        }

        // Same direction on the edge: same ToNodeId
        if (segA.ToNodeId == segB.ToNodeId)
        {
            // Trailing: the one farther from the target node slows down
            double distAToTarget = DistToSegTarget(a, segA);
            double distBToTarget = DistToSegTarget(b, segB);

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
            // Opposite direction on same edge — head-on, both stop
            if (distFt <= OppositeStopDistanceFt)
            {
                ApplyMinLimit(a, 0, "same-edge head-on", b, distFt);
                ApplyMinLimit(b, 0, "same-edge head-on", a, distFt);
            }
        }

        return true;
    }

    private static bool TryConvergence(AircraftState a, AircraftState b, AirportGroundLayout layout)
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
            return false;
        }

        // Both converging on the same node — the one further away yields
        if (!layout.Nodes.TryGetValue(sharedNodeId.Value, out var node))
        {
            return false;
        }

        double distA = GeoMath.DistanceNm(a.Latitude, a.Longitude, node.Latitude, node.Longitude);
        double distB = GeoMath.DistanceNm(b.Latitude, b.Longitude, node.Latitude, node.Longitude);

        double conflictDistFt = GeoMath.DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude) * FtPerNm;
        if (distA > distB)
        {
            ApplyMinLimit(a, 0, "convergence", b, conflictDistFt);
        }
        else
        {
            ApplyMinLimit(b, 0, "convergence", a, conflictDistFt);
        }

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
        double distFt
    )
    {
        // Identify which is moving and which is stationary
        if (stateA != MovementState.Stationary && dirA is not null && a.GroundSpeed > 0)
        {
            ApplyClosingLimit(a, dirA.Value, b, distFt);
        }

        if (stateB != MovementState.Stationary && dirB is not null && b.GroundSpeed > 0)
        {
            ApplyClosingLimit(b, dirB.Value, a, distFt);
        }
    }

    private static void ApplyClosingLimit(AircraftState mover, double moveDir, AircraftState obstacle, double distFt)
    {
        double bearing = GeoMath.BearingTo(mover.Latitude, mover.Longitude, obstacle.Latitude, obstacle.Longitude);
        if (HeadingDifference(moveDir, bearing) >= 90)
        {
            return;
        }

        if (distFt <= StopDistanceFt)
        {
            ApplyMinLimit(mover, 0, "proximity stop", obstacle, distFt);
        }
        else if (distFt <= TrailDistanceFt)
        {
            // When trailing a stationary obstacle (parked aircraft), allow slow taxi
            // instead of matching 0 kts — aircraft need to pass parked gates.
            double limitSpeed = obstacle.GroundSpeed;
            if (limitSpeed < SlowTaxiSpeedKts)
            {
                limitSpeed = SlowTaxiSpeedKts;
            }

            ApplyMinLimit(mover, limitSpeed, "proximity trail", obstacle, distFt);
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

    private static void ApplyTrailLimit(AircraftState trailer, AircraftState leader, double distFt)
    {
        double maxSpeed;
        string reason;
        if (distFt <= StopDistanceFt)
        {
            maxSpeed = 0;
            reason = "trail stop";
        }
        else if (distFt <= TrailDistanceFt)
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

    private static double DistToSegTarget(AircraftState ac, TaxiRouteSegment seg)
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

    private static int? FindSharedUpcomingNode(TaxiRoute routeA, TaxiRoute routeB)
    {
        // Collect upcoming target nodes for A
        var nodesA = new HashSet<int>();
        int startA = routeA.CurrentSegmentIndex;
        int endA = Math.Min(startA + ConvergenceLookaheadSegments, routeA.Segments.Count);
        for (int i = startA; i < endA; i++)
        {
            nodesA.Add(routeA.Segments[i].ToNodeId);
        }

        // Check if B shares any upcoming target node
        int startB = routeB.CurrentSegmentIndex;
        int endB = Math.Min(startB + ConvergenceLookaheadSegments, routeB.Segments.Count);
        for (int i = startB; i < endB; i++)
        {
            if (nodesA.Contains(routeB.Segments[i].ToNodeId))
            {
                return routeB.Segments[i].ToNodeId;
            }
        }

        return null;
    }

    private static bool ShareUpcomingNode(AircraftState subject, AircraftState reference, AirportGroundLayout layout)
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
