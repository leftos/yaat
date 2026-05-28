using System.Text;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>Per-node decode for only-legacy hold-short reachability gaps (round-3 triage).</summary>
public static class FilletReachabilityDiagnostics
{
    public enum V2ProximityKind
    {
        /// <summary>Hold-short BFS on full V2 graph reaches the target (stable-only gate is the artifact).</summary>
        TargetReachableOnV2,

        /// <summary>Reverse BFS from target hits a V2-reachable stable node within one hop of the legacy gap edge.</summary>
        PartialPathNearGap,

        /// <summary>Reverse BFS finds a V2-reachable stable node but it is farther than one hop from the gap.</summary>
        PartialPathDistant,

        /// <summary>No V2-reachable stable node found backward from target — plan reconnect / chain gap.</summary>
        NoV2Path,
    }

    public sealed record MissingLinkAnalysis(
        int TargetStableNodeId,
        int LastV2ReachableNodeId,
        int NextNodeId,
        string? LegacyEdgeTaxiway,
        string? LegacyEdgeOrigin,
        string GapDescription,
        bool TargetReachableOnFullV2Graph,
        int ClosestV2ReachableStableNodeId,
        int ClosestV2HopCount,
        double ClosestV2DistanceFt,
        V2ProximityKind ProximityKind
    );

    public static IReadOnlyList<MissingLinkAnalysis> AnalyzeOnlyLegacyStableNodes(
        AirportGroundLayout preFillet,
        AirportGroundLayout legacyLayout,
        AirportGroundLayout v2Layout,
        int maxSamples
    )
    {
        var legacyReachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(preFillet, legacyLayout);
        var v2ReachableStable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(preFillet, v2Layout);

        var legacyHoldShortSeeds = legacyLayout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).Select(n => n.Id).ToList();
        var v2HoldShortSeeds = v2Layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).Select(n => n.Id).ToList();
        var v2ReachableAll = BfsFrom(v2HoldShortSeeds, v2Layout);

        var onlyLegacy = legacyReachable.Except(v2ReachableStable).Take(maxSamples).ToList();
        var results = new List<MissingLinkAnalysis>();

        foreach (int targetId in onlyLegacy)
        {
            if (!TryFindMissingLink(legacyLayout, v2ReachableStable, legacyHoldShortSeeds, targetId, out var baseAnalysis))
            {
                results.Add(
                    EnrichWithV2Proximity(
                        new MissingLinkAnalysis(
                            targetId,
                            -1,
                            -1,
                            null,
                            null,
                            "no path from hold-short on legacy layout",
                            false,
                            -1,
                            -1,
                            -1,
                            V2ProximityKind.NoV2Path
                        ),
                        v2Layout,
                        v2ReachableStable,
                        v2ReachableAll,
                        targetId,
                        -1
                    )
                );
                continue;
            }

            results.Add(EnrichWithV2Proximity(baseAnalysis, v2Layout, v2ReachableStable, v2ReachableAll, targetId, baseAnalysis.NextNodeId));
        }

        return results;
    }

    public static string FormatAnalysis(string airportId, IReadOnlyList<MissingLinkAnalysis> analyses)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {airportId} only-legacy reachability decode (first {analyses.Count} samples) ===");
        foreach (var a in analyses)
        {
            sb.AppendLine(
                $"target stable node {a.TargetStableNodeId}: last-v2-reachable={a.LastV2ReachableNodeId} "
                    + $"next={a.NextNodeId} twy={a.LegacyEdgeTaxiway ?? "?"} origin={a.LegacyEdgeOrigin ?? "?"}"
            );
            sb.AppendLine($"  gap: {a.GapDescription}");
            sb.AppendLine(
                $"  v2-proximity: kind={a.ProximityKind} target-on-v2-all={a.TargetReachableOnFullV2Graph} "
                    + $"closest-v2-stable={a.ClosestV2ReachableStableNodeId} hops={a.ClosestV2HopCount} distFt={a.ClosestV2DistanceFt:F0}"
            );
        }

        return sb.ToString();
    }

    public sealed record ExtraV2LinkAnalysis(
        int TargetStableNodeId,
        int LastLegacyReachableNodeId,
        int NextNodeId,
        string? V2EdgeTaxiway,
        string? V2EdgeOrigin,
        string GapDescription
    );

    public static IReadOnlyList<int> GetOnlyV2StableNodeIds(
        AirportGroundLayout preFillet,
        AirportGroundLayout legacyLayout,
        AirportGroundLayout v2Layout
    )
    {
        var legacyReachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(preFillet, legacyLayout);
        var v2Reachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(preFillet, v2Layout);
        return v2Reachable.Except(legacyReachable).OrderBy(id => id).ToList();
    }

    public static IReadOnlyList<ExtraV2LinkAnalysis> AnalyzeOnlyV2StableNodes(
        AirportGroundLayout preFillet,
        AirportGroundLayout legacyLayout,
        AirportGroundLayout v2Layout,
        int maxSamples
    )
    {
        var legacyReachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(preFillet, legacyLayout);
        var v2Reachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(preFillet, v2Layout);
        var v2HoldShortSeeds = v2Layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).Select(n => n.Id).ToList();

        var onlyV2 = v2Reachable.Except(legacyReachable).Take(maxSamples).ToList();
        var results = new List<ExtraV2LinkAnalysis>();

        foreach (int targetId in onlyV2)
        {
            if (!TryFindExtraV2Link(v2Layout, legacyReachable, v2HoldShortSeeds, targetId, out var analysis))
            {
                results.Add(new ExtraV2LinkAnalysis(targetId, -1, -1, null, null, "no path from hold-short on V2 layout"));
                continue;
            }

            results.Add(analysis);
        }

        return results;
    }

    public static string FormatOnlyV2Analysis(string airportId, IReadOnlyList<ExtraV2LinkAnalysis> analyses, IReadOnlyList<int> allOnlyV2Ids)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {airportId} only-v2 stable nodes ({allOnlyV2Ids.Count} total) ===");
        if (allOnlyV2Ids.Count > 0)
        {
            sb.AppendLine($"  all ids: {string.Join(", ", allOnlyV2Ids)}");
        }

        foreach (var a in analyses)
        {
            sb.AppendLine(
                $"target stable node {a.TargetStableNodeId}: last-legacy-reachable={a.LastLegacyReachableNodeId} "
                    + $"next={a.NextNodeId} twy={a.V2EdgeTaxiway ?? "?"} origin={a.V2EdgeOrigin ?? "?"}"
            );
            sb.AppendLine($"  gap: {a.GapDescription}");
        }

        return sb.ToString();
    }

    public static string FormatReachabilityDiffSummary(
        string airportId,
        AirportGroundLayout preFillet,
        AirportGroundLayout legacyLayout,
        AirportGroundLayout v2Layout
    )
    {
        var legacyReachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(preFillet, legacyLayout);
        var v2Reachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(preFillet, v2Layout);
        int onlyLegacy = legacyReachable.Except(v2Reachable).Count();
        int onlyV2 = v2Reachable.Except(legacyReachable).Count();
        return $"{airportId}: legacy={legacyReachable.Count} v2={v2Reachable.Count} only-legacy={onlyLegacy} only-v2={onlyV2}";
    }

    private static bool TryFindExtraV2Link(
        AirportGroundLayout v2Layout,
        HashSet<int> legacyReachableStable,
        IReadOnlyList<int> holdShortSeeds,
        int targetId,
        out ExtraV2LinkAnalysis analysis
    )
    {
        analysis = null!;
        if (!TryShortestPath(holdShortSeeds, targetId, v2Layout, out var path))
        {
            return false;
        }

        int lastReachable = path[0];
        for (int i = 0; i < path.Count - 1; i++)
        {
            int from = path[i];
            int to = path[i + 1];
            if (legacyReachableStable.Contains(to))
            {
                lastReachable = to;
                continue;
            }

            var edge = FindEdge(v2Layout, from, to);
            analysis = new ExtraV2LinkAnalysis(
                targetId,
                lastReachable,
                to,
                edge?.TaxiwayName,
                edge?.Origin,
                $"Legacy BFS reaches {lastReachable} but not {to}; V2 edge {from}->{to}"
            );
            return true;
        }

        analysis = new ExtraV2LinkAnalysis(targetId, lastReachable, targetId, null, null, "V2 path found but no legacy gap edge identified");
        return true;
    }

    public static (HashSet<int> LegacyParking, HashSet<int> V2Parking, IReadOnlyList<int> OnlyLegacyParking) CompareParking(
        AirportGroundLayout legacyLayout,
        AirportGroundLayout v2Layout
    )
    {
        var legacyParking = FilletComparisonGates.Evaluate(legacyLayout, legacyLayout, FilletStatistics.Empty).ParkingReachableToHoldShort;
        var v2Parking = FilletComparisonGates.Evaluate(v2Layout, v2Layout, FilletStatistics.Empty).ParkingReachableToHoldShort;
        var onlyLegacy = legacyParking.Except(v2Parking).Take(10).ToList();
        return (legacyParking, v2Parking, onlyLegacy);
    }

    private static bool TryFindMissingLink(
        AirportGroundLayout legacyLayout,
        HashSet<int> v2ReachableStable,
        IReadOnlyList<int> holdShortSeeds,
        int targetId,
        out MissingLinkAnalysis analysis
    )
    {
        analysis = null!;
        if (!TryShortestPath(holdShortSeeds, targetId, legacyLayout, out var path))
        {
            return false;
        }

        int lastReachable = path[0];
        for (int i = 0; i < path.Count - 1; i++)
        {
            int from = path[i];
            int to = path[i + 1];
            if (v2ReachableStable.Contains(to))
            {
                lastReachable = to;
                continue;
            }

            var edge = FindEdge(legacyLayout, from, to);
            analysis = new MissingLinkAnalysis(
                targetId,
                lastReachable,
                to,
                edge?.TaxiwayName,
                edge?.Origin,
                $"V2 BFS reaches {lastReachable} but not {to}; legacy edge {from}->{to}",
                false,
                -1,
                -1,
                -1,
                V2ProximityKind.NoV2Path
            );
            return true;
        }

        analysis = new MissingLinkAnalysis(
            targetId,
            lastReachable,
            targetId,
            null,
            null,
            "legacy path found but no V2 gap edge identified",
            false,
            -1,
            -1,
            -1,
            V2ProximityKind.NoV2Path
        );
        return true;
    }

    private static MissingLinkAnalysis EnrichWithV2Proximity(
        MissingLinkAnalysis analysis,
        AirportGroundLayout v2Layout,
        HashSet<int> v2ReachableStable,
        HashSet<int> v2ReachableAll,
        int targetId,
        int gapNextNodeId
    )
    {
        if (v2ReachableAll.Contains(targetId))
        {
            return analysis with { TargetReachableOnFullV2Graph = true, ProximityKind = V2ProximityKind.TargetReachableOnV2 };
        }

        if (!TryClosestV2ReachableStable(v2Layout, v2ReachableStable, targetId, out int closestId, out int hops, out double distFt))
        {
            return analysis with { ProximityKind = V2ProximityKind.NoV2Path };
        }

        var kind =
            (hops <= 1) && (gapNextNodeId < 0 || gapNextNodeId == closestId || v2Layout.Nodes.ContainsKey(gapNextNodeId))
                ? V2ProximityKind.PartialPathNearGap
                : V2ProximityKind.PartialPathDistant;

        return analysis with
        {
            ClosestV2ReachableStableNodeId = closestId,
            ClosestV2HopCount = hops,
            ClosestV2DistanceFt = distFt,
            ProximityKind = kind,
        };
    }

    /// <summary>Reverse BFS from target on V2 until any hold-short-reachable stable node is hit.</summary>
    private static bool TryClosestV2ReachableStable(
        AirportGroundLayout v2Layout,
        HashSet<int> v2ReachableStable,
        int targetId,
        out int closestStableId,
        out int hopCount,
        out double distanceFt
    )
    {
        closestStableId = -1;
        hopCount = -1;
        distanceFt = -1;

        if (!v2Layout.Nodes.ContainsKey(targetId))
        {
            return false;
        }

        var parent = new Dictionary<int, int> { [targetId] = targetId };
        var queue = new Queue<int>();
        queue.Enqueue(targetId);

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (v2ReachableStable.Contains(id))
            {
                closestStableId = id;
                hopCount = CountHops(parent, targetId, id);
                distanceFt = PathDistanceFt(v2Layout, parent, targetId, id);
                return true;
            }

            if (!v2Layout.Nodes.TryGetValue(id, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                int other = edge.OtherNodeId(id);
                if (parent.ContainsKey(other))
                {
                    continue;
                }

                parent[other] = id;
                queue.Enqueue(other);
            }
        }

        return false;
    }

    private static int CountHops(IReadOnlyDictionary<int, int> parent, int fromId, int toId)
    {
        int hops = 0;
        int cur = toId;
        while (cur != fromId)
        {
            cur = parent[cur];
            hops++;
        }

        return hops;
    }

    private static double PathDistanceFt(AirportGroundLayout layout, IReadOnlyDictionary<int, int> parent, int fromId, int toId)
    {
        double nm = 0;
        int cur = toId;
        while (cur != fromId)
        {
            int prev = parent[cur];
            if (layout.Nodes.TryGetValue(cur, out var nCur) && layout.Nodes.TryGetValue(prev, out var nPrev))
            {
                nm += GeoMath.DistanceNm(nCur.Position, nPrev.Position);
            }

            cur = prev;
        }

        return nm * GeoMath.FeetPerNm;
    }

    private static GroundEdge? FindEdge(AirportGroundLayout layout, int nodeA, int nodeB)
    {
        if (!layout.Nodes.TryGetValue(nodeA, out var a))
        {
            return null;
        }

        foreach (var edge in a.Edges)
        {
            if (edge is GroundEdge ge && ((ge.Nodes[0].Id == nodeB) || (ge.Nodes[1].Id == nodeB)))
            {
                return ge;
            }
        }

        return null;
    }

    private static bool TryShortestPath(IReadOnlyList<int> seeds, int targetId, AirportGroundLayout layout, out List<int> path)
    {
        path = [];
        var parent = new Dictionary<int, int>();
        var queue = new Queue<int>();
        foreach (int seed in seeds)
        {
            if (!layout.Nodes.ContainsKey(seed))
            {
                continue;
            }

            parent[seed] = seed;
            queue.Enqueue(seed);
        }

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (id == targetId)
            {
                break;
            }

            if (!layout.Nodes.TryGetValue(id, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                int other = edge.OtherNodeId(id);
                if (parent.ContainsKey(other))
                {
                    continue;
                }

                parent[other] = id;
                queue.Enqueue(other);
            }
        }

        if (!parent.ContainsKey(targetId))
        {
            return false;
        }

        var reversed = new List<int> { targetId };
        int cur = targetId;
        while (cur != parent[cur])
        {
            cur = parent[cur];
            reversed.Add(cur);
        }

        reversed.Reverse();
        path = reversed;
        return true;
    }

    private static HashSet<int> BfsFrom(IEnumerable<int> seeds, AirportGroundLayout layout)
    {
        var reachable = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (int seed in seeds)
        {
            if (layout.Nodes.ContainsKey(seed) && reachable.Add(seed))
            {
                queue.Enqueue(seed);
            }
        }

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (!layout.Nodes.TryGetValue(id, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                int otherId = edge.OtherNodeId(id);
                if (reachable.Add(otherId))
                {
                    queue.Enqueue(otherId);
                }
            }
        }

        return reachable;
    }
}
