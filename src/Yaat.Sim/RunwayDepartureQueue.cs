using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim;

/// <summary>
/// Computes each departing aircraft's 1-based position in the physical line at its destination-runway
/// hold-short node, writing it (plus the runway designator and, for an intersection departure, the taxiway
/// it enters from) to <see cref="AircraftGroundOps.RunwayQueuePosition"/> /
/// <see cref="AircraftGroundOps.RunwayQueueRunway"/> / <see cref="AircraftGroundOps.RunwayQueueIntersection"/>
/// — see <see cref="Data.Airport.RunwayEntryPoint"/>. A "line" is keyed by hold-short node, so two
/// intersections feeding the same runway are two independent queues.
///
/// <para>Membership takes three routes in, and every one of them requires a departure clearance ending at
/// that node plus, for anyone not already stopped at it, presence within <see cref="ProximityNm"/> of it:
/// holding short of the destination runway (tier 0, front of the line); still taxiing or idling toward it
/// (tier 1); and following an aircraft already in the line, which takes the place directly behind its leader
/// (see <see cref="RankFollowers"/>) or, failing that, is ranked on its own clearance like any other taxiing
/// departure. An aircraft that has lined up / is rolling has left the line — its position drops to 0 and the
/// aircraft behind it move up. Even a lone aircraft first in line gets #1: the ordinal tells the RPO who is
/// next up, not only that a clump exists.</para>
///
/// <para>The ordinal is the physical order at the bar, not a sequencing decision — nothing here chooses who
/// departs next, and the numbers re-derive from scratch every second.</para>
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
    /// as "in line". Kept tight (~600 ft) so only aircraft physically bunched at the hold short are numbered —
    /// an RPO cares about the few aircraft next up, not everyone taxiing toward the runway. Holding-short
    /// aircraft are at the node and always count regardless of this gate.
    /// </summary>
    public const double ProximityNm = 0.1;

    public static void UpdatePositions(IReadOnlyList<AircraftState> aircraft)
    {
        foreach (var ac in aircraft)
        {
            ac.Ground.RunwayQueuePosition = 0;
            ac.Ground.RunwayQueueRunway = "";
            ac.Ground.RunwayQueueIntersection = "";
        }

        // Pass 1 — everyone whose own phase and route put them in a line.
        var lines = new Dictionary<(string Airport, int NodeId), List<Member>>();
        var ranked = new Dictionary<string, Member>(StringComparer.OrdinalIgnoreCase);
        foreach (var ac in aircraft)
        {
            if (Classify(ac) is { } member)
            {
                AddToLine(lines, ranked, member);
            }
        }

        // Pass 2 — everyone whose place in a line comes from the aircraft they are following.
        RankFollowers(aircraft, lines, ranked);

        foreach (var (_, members) in lines)
        {
            members.Sort(CompareMembers);
            PublishLine(members);
        }
    }

    private static void PublishLine(List<Member> members)
    {
        // One lookup per line, not per aircraft: the entry point is a property of the hold-short node, so
        // resolving it once also guarantees everyone in the same line shows the same label. The front
        // aircraft is the one physically at the node, so its taxiway is the authoritative tie-breaker, and
        // its runway end labels the whole line for the same reason — one line at one bar is one runway.
        var front = members[0];
        string runway = front.Runway;
        string intersection = RunwayEntryPoint.Resolve(front.Layout, front.NodeId, runway, front.Aircraft.Ground.CurrentTaxiway) ?? "";

        for (int i = 0; i < members.Count; i++)
        {
            members[i].Aircraft.Ground.RunwayQueuePosition = i + 1;
            members[i].Aircraft.Ground.RunwayQueueRunway = runway;
            members[i].Aircraft.Ground.RunwayQueueIntersection = intersection;
            Log.LogTrace(
                "[RunwayQueue] {Callsign}: #{Position} for {Runway}{Intersection} at node {NodeId} ({Airport}), tier={Tier}, dist={Dist:F2}nm",
                members[i].Aircraft.Callsign,
                i + 1,
                runway,
                intersection.Length > 0 ? $"@{intersection}" : "",
                members[i].NodeId,
                members[i].AirportId,
                members[i].Tier,
                members[i].DistanceNm
            );
        }
    }

    private static void AddToLine(Dictionary<(string Airport, int NodeId), List<Member>> lines, Dictionary<string, Member> ranked, Member member)
    {
        var key = (member.AirportId, member.NodeId);
        if (!lines.TryGetValue(key, out var members))
        {
            members = [];
            lines[key] = members;
        }
        members.Add(member);
        ranked[member.Aircraft.Callsign] = member;
    }

    /// <summary>
    /// Ranks every aircraft that is following another one. A follower sits in <see cref="FollowingPhase"/>,
    /// which is neither queue-eligible phase, so without this pass it would be unnumbered and the aircraft
    /// behind it would read as next up — exactly wrong for an RPO merging two taxi flows onto one bar by
    /// handing each trailer to the aircraft ahead.
    ///
    /// <para>A follower takes the place directly behind its leader only when all three hold: the leader is
    /// itself in a line, the follower's own clearance ends at that same bar, and the follower is inside
    /// <see cref="ProximityNm"/> of it. It then inherits the leader's line, node, runway and tier and sits
    /// one physical gap farther from the bar, which is what <see cref="CompareMembers"/> orders on inside a
    /// tier. The same-bar requirement is what keeps an arrival trailing a departure, or a follower bound for
    /// a different intersection, out of the line.</para>
    ///
    /// <para>Two sub-passes, in this order. Inheritance runs first, to a fixpoint, so a follower of a
    /// follower is placed on the pass after its own leader — the bound is one pass per aircraft (the longest
    /// possible chain) and it exits as soon as a pass places nobody, so a following-cycle terminates instead
    /// of spinning. Only then does the fallback run, for whoever is still following but could not inherit:
    /// they are ranked on their own clearance via <see cref="ClassifyByOwnRoute"/>, because a leader that has
    /// lined up and left does not strand the departure still sitting at the bar behind it. Running the
    /// fallback first would rank a mid-chain follower on its own geometry before its leader was ever
    /// considered.</para>
    /// </summary>
    private static void RankFollowers(
        IReadOnlyList<AircraftState> aircraft,
        Dictionary<(string Airport, int NodeId), List<Member>> lines,
        Dictionary<string, Member> ranked
    )
    {
        for (int pass = 0; pass < aircraft.Count; pass++)
        {
            bool placedAny = false;
            foreach (var ac in aircraft)
            {
                if (ranked.ContainsKey(ac.Callsign) || ClassifyInheritedFollower(ac, ranked) is not { } member)
                {
                    continue;
                }

                AddToLine(lines, ranked, member);
                placedAny = true;
            }

            if (!placedAny)
            {
                break;
            }
        }

        foreach (var ac in aircraft)
        {
            if (ranked.ContainsKey(ac.Callsign) || ClassifyStrandedFollower(ac) is not { } member)
            {
                continue;
            }

            AddToLine(lines, ranked, member);
        }
    }

    /// <summary>A follower that can take its leader's line, or null when it is not following or cannot inherit.</summary>
    private static Member? ClassifyInheritedFollower(AircraftState ac, Dictionary<string, Member> ranked)
    {
        if (!ac.IsOnGround || ac.Ground.Layout is not { } layout || GoverningFollow(ac) is not { } following)
        {
            return null;
        }

        return InheritLeaderLine(ac, layout, following.TargetCallsign, ranked);
    }

    /// <summary>A follower whose leader is not in any line, ranked on its own clearance like any other taxiing departure.</summary>
    private static Member? ClassifyStrandedFollower(AircraftState ac)
    {
        if (!ac.IsOnGround || ac.Ground.Layout is not { } layout || GoverningFollow(ac) is null)
        {
            return null;
        }

        return ClassifyByOwnRoute(ac, layout);
    }

    /// <summary>
    /// The follow instruction currently governing this aircraft: its active <see cref="FollowingPhase"/>, or
    /// the one queued immediately behind a <see cref="HoldingShortPhase"/> it has stopped at — the shape
    /// <c>FollowingPhase.CheckRunwayHoldShort</c> leaves when a follower reaches a bar, where the aircraft is
    /// still following its leader but is not, for those seconds, in the phase.
    /// </summary>
    private static FollowingPhase? GoverningFollow(AircraftState ac)
    {
        if (ac.Phases is not { } phases)
        {
            return null;
        }

        if (phases.CurrentPhase is FollowingPhase active)
        {
            return active;
        }

        int next = phases.CurrentIndex + 1;
        if (phases.CurrentPhase is HoldingShortPhase && next < phases.Phases.Count)
        {
            return phases.Phases[next] as FollowingPhase;
        }

        return null;
    }

    private static Member? InheritLeaderLine(AircraftState ac, AirportGroundLayout layout, string leaderCallsign, Dictionary<string, Member> ranked)
    {
        if (!ranked.TryGetValue(leaderCallsign, out var leader))
        {
            return null;
        }

        // Only a line the follower is actually bound for. Following an aircraft headed elsewhere is not a
        // shared queue, and an arrival following a departure has no destination bar at all.
        if (DestinationBarOf(ac) is not { } destination || (destination.NodeId != leader.NodeId))
        {
            return null;
        }

        // The same gate a taxiing aircraft faces, measured on the follower's own distance to the bar rather
        // than on the gap to its leader: a follower half a mile back is not in the line yet.
        if (!TryNodePosition(layout, leader.NodeId, out var barPosition))
        {
            return null;
        }

        double ownDistanceNm = GeoMath.DistanceNm(ac.Position, barPosition);
        if (ownDistanceNm > ProximityNm)
        {
            return null;
        }

        double gapNm = GeoMath.DistanceNm(ac.Position, leader.Aircraft.Position);
        Log.LogTrace(
            "[RunwayQueue] {Callsign}: following {Leader}, joins the {Runway} line at node {NodeId} ({Airport}) behind it, "
                + "tier={Tier}, dist={Dist:F2}nm (leader {LeaderDist:F2}nm + gap {Gap:F2}nm), own dist to bar {Own:F2}nm",
            ac.Callsign,
            leaderCallsign,
            leader.Runway,
            leader.NodeId,
            leader.AirportId,
            leader.Tier,
            leader.DistanceNm + gapNm,
            leader.DistanceNm,
            gapNm,
            ownDistanceNm
        );

        return new Member(
            ac,
            leader.Layout,
            leader.AirportId,
            leader.NodeId,
            leader.Runway,
            leader.Tier,
            leader.DistanceNm + gapNm,
            ac.Ground.StationarySeconds
        );
    }

    /// <summary>
    /// A candidate in a departure line: which hold-short node it queues at, a phase tier (holding short
    /// outranks still-taxiing), and its distance to that node. Aircraft that are not departing near a
    /// runway hold-short are not candidates.
    /// </summary>
    private readonly record struct Member(
        AircraftState Aircraft,
        AirportGroundLayout Layout,
        string AirportId,
        int NodeId,
        string Runway,
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
            var runway = DepartureDesignator(ac, holdShort.TargetName);
            return new Member(
                ac,
                layout,
                layout.AirportId,
                holdShort.NodeId,
                runway,
                Tier: 0,
                GeoMath.DistanceNm(ac.Position, pos),
                ac.Ground.StationarySeconds
            );
        }

        // Tier 1 — still taxiing (or idling in position) toward the destination runway, within the
        // proximity gate. Aircraft that have lined up / are rolling are neither phase and drop out.
        if (phase is TaxiingPhase or HoldingInPositionPhase)
        {
            return ClassifyByOwnRoute(ac, layout);
        }

        return null;
    }

    /// <summary>
    /// Tier 1 — ranked on the aircraft's own clearance: the destination-runway bar its assigned route ends at,
    /// and its straight-line distance to that bar, admitted only inside <see cref="ProximityNm"/>. Shared by
    /// the taxiing branch of <see cref="Classify"/> and by the follower fallback, so a follower that cannot
    /// inherit a line is ranked on exactly the terms the aircraft taxiing beside it would be.
    /// </summary>
    private static Member? ClassifyByOwnRoute(AircraftState ac, AirportGroundLayout layout)
    {
        if (DestinationBarOf(ac) is not { } destination || !TryNodePosition(layout, destination.NodeId, out var pos))
        {
            return null;
        }

        double distanceNm = GeoMath.DistanceNm(ac.Position, pos);
        if (distanceNm > ProximityNm)
        {
            return null;
        }

        var runway = DepartureDesignator(ac, destination.TargetName);
        return new Member(ac, layout, layout.AirportId, destination.NodeId, runway, Tier: 1, distanceNm, ac.Ground.StationarySeconds);
    }

    /// <summary>
    /// The runway end this place in the line is labelled with. A hold-short bar is named after the pavement
    /// it protects, so the same node is reached as "28L" on one aircraft's clearance and as the combined
    /// "10R/28L" on another's — two runways for one line, where the RPO needs the end the aircraft will
    /// actually depart from. Taken from the departure runway the phases assigned, then from the
    /// destination-runway bar on the aircraft's own taxi clearance, then from <paramref name="barTargetName"/>:
    /// the first of those that names a single end, so a combined pavement id is only ever the last resort.
    /// </summary>
    private static string DepartureDesignator(AircraftState ac, string? barTargetName)
    {
        string barLabel = RunwayIdentifier.ToDisplayDesignator(barTargetName ?? "");
        string[] candidates = [ac.Phases?.DepartureRunway?.Designator ?? "", DestinationBarOf(ac)?.TargetName ?? "", barLabel];

        foreach (string candidate in candidates)
        {
            string designator = RunwayIdentifier.ToDisplayDesignator(candidate);
            if (IsSingleEnd(designator))
            {
                return designator;
            }
        }

        return barLabel;
    }

    /// <summary>
    /// True when <paramref name="designator"/> names one runway end ("28L", "9") rather than a combined
    /// pavement id ("10R/28L", "28R - 10L") or nothing at all.
    /// </summary>
    private static bool IsSingleEnd(string designator)
    {
        int digits = 0;
        while (digits < designator.Length && char.IsAsciiDigit(designator[digits]))
        {
            digits++;
        }

        if (digits is 0 or > 2)
        {
            return false;
        }

        return (digits == designator.Length) || ((digits == designator.Length - 1) && char.ToUpperInvariant(designator[digits]) is 'L' or 'R' or 'C');
    }

    /// <summary>
    /// The destination-runway bar the aircraft's assigned taxi route ends at, or null when it is not taxiing
    /// to a runway at all — an arrival taxiing to the gate has no such bar, and must never be numbered.
    /// </summary>
    private static HoldShortPoint? DestinationBarOf(AircraftState ac) =>
        ac.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.DestinationRunway);

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
