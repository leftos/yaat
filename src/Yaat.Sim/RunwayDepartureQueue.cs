using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim;

/// <summary>
/// Computes each departing aircraft's 1-based position in the physical line at its destination-runway
/// hold-short node, writing it to <see cref="AircraftGroundOps.RunwayQueuePosition"/>. A "line" is keyed
/// by hold-short node, so two intersections feeding the same runway are two independent queues. Membership
/// is holding-short-of-the-runway plus still-taxiing-within-<see cref="ProximityNm"/>; an aircraft that has
/// lined up / is rolling has left the line (its position drops to 0 and the aircraft behind it moves up).
/// A line of one aircraft is left at 0 — a number only disambiguates an actual clump.
///
/// Runs once per sim-second from <see cref="Simulation.SimulationEngine.TickPrePhysics"/> over the full world
/// snapshot — the same computed-with-world / read-as-own pattern as <see cref="GroundConflictDetector"/>.
/// (TickPrePhysics, not post-physics, because it is the one per-second hook the live server shares with the
/// standalone tick path — the server runs its own post-physics.) Display only: never gates movement,
/// clearances, or physics.
/// </summary>
public static class RunwayDepartureQueue
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwayDepartureQueue");

    /// <summary>
    /// Max distance (nm) from its destination-runway hold-short node for a still-taxiing departure to count
    /// as "in line". Aircraft farther out are approaching but not yet clumped at the runway, so they get no
    /// number. Holding-short aircraft are at the node and always count regardless of this gate.
    /// </summary>
    public const double ProximityNm = 0.5;

    public static void UpdatePositions(IReadOnlyList<AircraftState> aircraft)
    {
        foreach (var ac in aircraft)
        {
            ac.Ground.RunwayQueuePosition = 0;
        }

        var lines = new Dictionary<(string Airport, int NodeId), List<Member>>();
        foreach (var ac in aircraft)
        {
            if (Classify(ac) is not { } member)
            {
                continue;
            }

            var key = (member.AirportId, member.NodeId);
            if (!lines.TryGetValue(key, out var members))
            {
                members = [];
                lines[key] = members;
            }
            members.Add(member);
        }

        foreach (var (_, members) in lines)
        {
            if (members.Count < 2)
            {
                continue;
            }

            members.Sort(CompareMembers);
            for (int i = 0; i < members.Count; i++)
            {
                members[i].Aircraft.Ground.RunwayQueuePosition = i + 1;
                Log.LogTrace(
                    "[RunwayQueue] {Callsign}: #{Position} in line at node {NodeId} ({Airport}), tier={Tier}, dist={Dist:F2}nm",
                    members[i].Aircraft.Callsign,
                    i + 1,
                    members[i].NodeId,
                    members[i].AirportId,
                    members[i].Tier,
                    members[i].DistanceNm
                );
            }
        }
    }

    /// <summary>
    /// A candidate in a departure line: which hold-short node it queues at, a phase tier (holding short
    /// outranks still-taxiing), and its distance to that node. Aircraft that are not departing near a
    /// runway hold-short are not candidates.
    /// </summary>
    private readonly record struct Member(
        AircraftState Aircraft,
        string AirportId,
        int NodeId,
        int Tier,
        double DistanceNm,
        double StationarySeconds
    );

    private static Member? Classify(AircraftState ac)
    {
        if (!ac.IsOnGround || ac.Ground.Layout is not { } layout)
        {
            return null;
        }

        var phase = ac.Phases?.CurrentPhase;

        // Tier 0 — holding short of the destination runway: at the hold-short node, front of its line.
        if (phase is HoldingShortPhase { HoldShort: { Reason: HoldShortReason.DestinationRunway } holdShort })
        {
            if (!TryNodePosition(layout, holdShort.NodeId, out var pos))
            {
                return null;
            }
            return new Member(ac, layout.AirportId, holdShort.NodeId, Tier: 0, GeoMath.DistanceNm(ac.Position, pos), ac.Ground.StationarySeconds);
        }

        // Tier 1 — still taxiing (or idling in position) toward the destination runway, within the
        // proximity gate. Aircraft that have lined up / are rolling are neither phase and drop out.
        if (phase is TaxiingPhase or HoldingInPositionPhase)
        {
            var destination = ac.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.DestinationRunway);
            if (destination is null || !TryNodePosition(layout, destination.NodeId, out var pos))
            {
                return null;
            }

            double distanceNm = GeoMath.DistanceNm(ac.Position, pos);
            if (distanceNm > ProximityNm)
            {
                return null;
            }
            return new Member(ac, layout.AirportId, destination.NodeId, Tier: 1, distanceNm, ac.Ground.StationarySeconds);
        }

        return null;
    }

    private static bool TryNodePosition(AirportGroundLayout layout, int nodeId, out LatLon position)
    {
        if (layout.Nodes.TryGetValue(nodeId, out var node))
        {
            position = node.Position;
            return true;
        }
        position = default;
        return false;
    }

    /// <summary>
    /// Front-of-line first: holding-short (tier 0) before still-taxiing (tier 1), then nearer the
    /// hold-short node, then longest-stopped (FIFO-ish), then callsign for a stable order.
    /// </summary>
    private static int CompareMembers(Member a, Member b)
    {
        int byTier = a.Tier.CompareTo(b.Tier);
        if (byTier != 0)
        {
            return byTier;
        }

        int byDistance = a.DistanceNm.CompareTo(b.DistanceNm);
        if (byDistance != 0)
        {
            return byDistance;
        }

        int byWait = b.StationarySeconds.CompareTo(a.StationarySeconds);
        if (byWait != 0)
        {
            return byWait;
        }

        return string.CompareOrdinal(a.Aircraft.Callsign, b.Aircraft.Callsign);
    }
}
