using System.Text;
using System.Text.RegularExpressions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet.V2;

namespace Yaat.Sim.Tests.Helpers;

public sealed record StructuralValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public readonly record struct CornerBucketKey(int JunctionId, string TaxiwayKey, int BrgLoBucketDeg, int BrgHiBucketDeg);

public sealed record CornerBucketMismatch(CornerBucketKey Key, double LegacyMinRadiusFt, double V2MinRadiusFt, double RelativeDelta);

public sealed record RunwayBearingMismatch(int NodeIdA, int NodeIdB, string TaxiwayName, double LegacyBearingDeg, double V2BearingDeg);

public sealed record FilletGateResults(
    StructuralValidationResult Structural,
    bool RepairCountersZero,
    HashSet<int> HoldShortReachableStableIds,
    HashSet<int> ParkingReachableToHoldShort,
    IReadOnlyDictionary<CornerBucketKey, double> CornerBucketMinRadiusFt,
    IReadOnlyDictionary<(int NodeIdA, int NodeIdB, string Taxiway), double> RunwayEdgeBearingsDeg,
    IReadOnlyDictionary<string, int> WarningCountsByCode
);

public sealed record FilletComparisonGateReport(
    IReadOnlyDictionary<string, FilletGateResults> GatesByGeneratorId,
    bool HoldShortConnectivityMatch,
    bool ParkingConnectivityMatch,
    IReadOnlyList<CornerBucketMismatch> CornerBucketMismatches,
    IReadOnlyList<RunwayBearingMismatch> RunwayBearingMismatches
);

/// <summary>Pass-5 parity gates for <see cref="FilletComparison"/>.</summary>
public static class FilletComparisonGates
{
    private const double CornerBearingToleranceDeg = 5.0;
    private const double CornerRadiusToleranceRatio = 0.10;
    private const double RunwayBearingToleranceDeg = 1.0;
    private const double CoincidentNodeThresholdNm = 5.0 / GeoMath.FeetPerNm;

    private static readonly Regex V2CornerOriginRegex = new(
        @"^V2:corner@J(?<junction>\d+)/(?<twyA>[^/]+)/(?<twyB>[^/]+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    public static FilletGateResults Evaluate(AirportGroundLayout preFillet, AirportGroundLayout layout, FilletStatistics stats)
    {
        var warningCounts = stats.Warnings.GroupBy(w => w.Code).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new FilletGateResults(
            ValidateStructural(layout),
            RepairCountersZero(stats),
            Reachability.ReachableStableIdsFromHoldShorts(preFillet, layout),
            Reachability.ParkingReachableToHoldShort(layout),
            IndexCornerBuckets(layout),
            IndexRunwayEdgeBearings(layout),
            warningCounts
        );
    }

    public static FilletComparisonGateReport CompareGenerators(IReadOnlyDictionary<string, FilletGateResults> gatesByGeneratorId)
    {
        var ids = gatesByGeneratorId.Keys.ToList();
        bool holdShortMatch =
            ids.Count <= 1 || AllReachabilitySetsEqual(ids.Select(id => gatesByGeneratorId[id].HoldShortReachableStableIds).ToList());
        bool parkingMatch = ids.Count <= 1 || AllReachabilitySetsEqual(ids.Select(id => gatesByGeneratorId[id].ParkingReachableToHoldShort).ToList());

        var cornerMismatches = new List<CornerBucketMismatch>();
        var runwayMismatches = new List<RunwayBearingMismatch>();

        if (gatesByGeneratorId.TryGetValue("legacy", out var legacy) && gatesByGeneratorId.TryGetValue("v2", out var v2))
        {
            cornerMismatches.AddRange(CompareCornerBuckets(legacy.CornerBucketMinRadiusFt, v2.CornerBucketMinRadiusFt));
            runwayMismatches.AddRange(CompareRunwayBearings(legacy.RunwayEdgeBearingsDeg, v2.RunwayEdgeBearingsDeg));
        }

        return new FilletComparisonGateReport(gatesByGeneratorId, holdShortMatch, parkingMatch, cornerMismatches, runwayMismatches);
    }

    public static bool RepairCountersZero(FilletStatistics stats) =>
        (stats.OrphansRescued == 0)
        && (stats.RedundantPreserveEdgesRemoved == 0)
        && (stats.DuplicateCornerArcsRemoved == 0)
        && (stats.ParallelBypassEdgesRemoved == 0)
        && (stats.DirectShortensAdded == 0);

    public static StructuralValidationResult ValidateStructural(AirportGroundLayout layout)
    {
        var errors = new List<string>();

        foreach (var edge in layout.Edges)
        {
            if (!layout.Nodes.TryGetValue(edge.Nodes[0].Id, out var n0) || !layout.Nodes.TryGetValue(edge.Nodes[1].Id, out var n1))
            {
                errors.Add($"Edge {edge.TaxiwayName} references missing node ({edge.Nodes[0].Id} or {edge.Nodes[1].Id})");
                continue;
            }

            if ((n0.Type == GroundNodeType.TaxiwayIntersection) && (n1.Type == GroundNodeType.TaxiwayIntersection))
            {
                double actualDistFt = GeoMath.DistanceNm(n0.Position, n1.Position) * GeoMath.FeetPerNm;
                if (actualDistFt < 1.0)
                {
                    errors.Add($"Degenerate edge {edge.TaxiwayName} ({n0.Id}->{n1.Id}): {actualDistFt:F1}ft");
                }
            }
        }

        var intersectionNodes = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.TaxiwayIntersection).ToList();
        for (int i = 0; i < intersectionNodes.Count; i++)
        {
            for (int j = i + 1; j < intersectionNodes.Count; j++)
            {
                double dist = GeoMath.DistanceNm(intersectionNodes[i].Position, intersectionNodes[j].Position);
                if (dist <= CoincidentNodeThresholdNm)
                {
                    errors.Add($"Coincident intersections ({intersectionNodes[i].Id}, {intersectionNodes[j].Id}): {dist * GeoMath.FeetPerNm:F1}ft");
                }
            }
        }

        foreach (var arc in layout.Arcs)
        {
            if (!layout.Nodes.ContainsKey(arc.Nodes[0].Id) || !layout.Nodes.ContainsKey(arc.Nodes[1].Id))
            {
                errors.Add($"Arc references missing node ({arc.Nodes[0].Id}->{arc.Nodes[1].Id})");
                continue;
            }

            if (arc.MinRadiusOfCurvatureFt <= 0)
            {
                errors.Add($"Arc {arc.Nodes[0].Id}->{arc.Nodes[1].Id} MinRadius={arc.MinRadiusOfCurvatureFt:F1}ft");
            }

            if (arc.DistanceNm <= 0)
            {
                errors.Add($"Arc {arc.Nodes[0].Id}->{arc.Nodes[1].Id} DistanceNm={arc.DistanceNm}");
            }
        }

        foreach (var node in layout.Nodes.Values)
        {
            if (
                (node.Type != GroundNodeType.Spot)
                && (node.Type != GroundNodeType.Helipad)
                && (node.Type != GroundNodeType.Parking)
                && (node.Edges.Count == 0)
            )
            {
                errors.Add($"Node {node.Id} ({node.Type}) has no edges");
            }
        }

        return new StructuralValidationResult(errors.Count == 0, errors);
    }

    public static IReadOnlyDictionary<CornerBucketKey, double> IndexCornerBuckets(AirportGroundLayout layout)
    {
        var buckets = new Dictionary<CornerBucketKey, double>();

        foreach (var arc in layout.Arcs)
        {
            if (!TryGetCornerArcIdentity(arc, out int junctionId, out string taxiwayKey))
            {
                continue;
            }

            double b0 = arc.EdgeBearingAtNode0Deg;
            double b1 = arc.EdgeBearingAtNode1Deg;
            (double lo, double hi) = b0 <= b1 ? (b0, b1) : (b1, b0);
            var key = new CornerBucketKey(junctionId, taxiwayKey, BucketBearingDeg(lo), BucketBearingDeg(hi));
            if (!buckets.TryGetValue(key, out double existing) || arc.MinRadiusOfCurvatureFt < existing)
            {
                buckets[key] = arc.MinRadiusOfCurvatureFt;
            }
        }

        return buckets;
    }

    public static IReadOnlyList<CornerBucketMismatch> CompareCornerBuckets(
        IReadOnlyDictionary<CornerBucketKey, double> legacy,
        IReadOnlyDictionary<CornerBucketKey, double> v2
    )
    {
        var mismatches = new List<CornerBucketMismatch>();
        foreach (var (key, legacyRadius) in legacy)
        {
            if (!v2.TryGetValue(key, out double v2Radius))
            {
                continue;
            }

            if (legacyRadius <= 0)
            {
                continue;
            }

            double rel = Math.Abs(v2Radius - legacyRadius) / legacyRadius;
            if (rel > CornerRadiusToleranceRatio)
            {
                mismatches.Add(new CornerBucketMismatch(key, legacyRadius, v2Radius, rel));
            }
        }

        return mismatches;
    }

    public static IReadOnlyDictionary<(int NodeIdA, int NodeIdB, string Taxiway), double> IndexRunwayEdgeBearings(AirportGroundLayout layout)
    {
        var bearings = new Dictionary<(int, int, string), double>();
        foreach (var edge in layout.Edges)
        {
            if (!edge.IsRunwayCenterline)
            {
                continue;
            }

            int a = edge.Nodes[0].Id;
            int b = edge.Nodes[1].Id;
            int lo = Math.Min(a, b);
            int hi = Math.Max(a, b);
            double bearing = GeoMath.BearingTo(edge.Nodes[0].Position, edge.Nodes[1].Position);
            bearings[(lo, hi, edge.TaxiwayName)] = bearing;
        }

        return bearings;
    }

    public static IReadOnlyList<RunwayBearingMismatch> CompareRunwayBearings(
        IReadOnlyDictionary<(int NodeIdA, int NodeIdB, string Taxiway), double> legacy,
        IReadOnlyDictionary<(int NodeIdA, int NodeIdB, string Taxiway), double> v2
    )
    {
        var mismatches = new List<RunwayBearingMismatch>();
        foreach (var (key, legacyBearing) in legacy)
        {
            if (!v2.TryGetValue(key, out double v2Bearing))
            {
                continue;
            }

            if (GeoMath.AbsBearingDifference(legacyBearing, v2Bearing) > RunwayBearingToleranceDeg)
            {
                mismatches.Add(new RunwayBearingMismatch(key.NodeIdA, key.NodeIdB, key.Taxiway, legacyBearing, v2Bearing));
            }
        }

        return mismatches;
    }

    public static void AppendGateReport(StringBuilder sb, FilletComparisonReport report)
    {
        sb.AppendLine($"  Hold-short reachability match: {report.Gates.HoldShortConnectivityMatch}");
        if (
            !report.Gates.HoldShortConnectivityMatch
            && report.Gates.GatesByGeneratorId.TryGetValue("legacy", out var legacyGates)
            && report.Gates.GatesByGeneratorId.TryGetValue("v2", out var v2Gates)
        )
        {
            int onlyLegacy = legacyGates.HoldShortReachableStableIds.Except(v2Gates.HoldShortReachableStableIds).Count();
            int onlyV2 = v2Gates.HoldShortReachableStableIds.Except(legacyGates.HoldShortReachableStableIds).Count();
            sb.AppendLine(
                $"    stable reachable: legacy={legacyGates.HoldShortReachableStableIds.Count} v2={v2Gates.HoldShortReachableStableIds.Count} "
                    + $"only-legacy={onlyLegacy} only-v2={onlyV2}"
            );
            foreach (int id in legacyGates.HoldShortReachableStableIds.Except(v2Gates.HoldShortReachableStableIds).Take(5))
            {
                sb.AppendLine($"    only-legacy sample: node {id}");
            }
        }
        sb.AppendLine($"  Parking→hold-short reachability match: {report.Gates.ParkingConnectivityMatch}");

        foreach (var run in report.Runs)
        {
            if (!report.Gates.GatesByGeneratorId.TryGetValue(run.GeneratorId, out var gates))
            {
                continue;
            }

            sb.AppendLine(
                $"  [{run.GeneratorId}] structural={(gates.Structural.IsValid ? "ok" : "FAIL")} repairCountersZero={gates.RepairCountersZero}"
            );
            if (!gates.Structural.IsValid)
            {
                foreach (var err in gates.Structural.Errors.Take(5))
                {
                    sb.AppendLine($"    structural: {err}");
                }
            }

            if (gates.WarningCountsByCode.Count > 0)
            {
                sb.AppendLine($"    warnings: {string.Join(", ", gates.WarningCountsByCode.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }
        }

        if (report.Gates.CornerBucketMismatches.Count > 0)
        {
            sb.AppendLine($"  Corner bucket mismatches (>{CornerRadiusToleranceRatio:P0}): {report.Gates.CornerBucketMismatches.Count}");
            foreach (var m in report.Gates.CornerBucketMismatches.Take(8))
            {
                sb.AppendLine(
                    $"    J{m.Key.JunctionId} {m.Key.TaxiwayKey} brg={m.Key.BrgLoBucketDeg}/{m.Key.BrgHiBucketDeg}: legacy={m.LegacyMinRadiusFt:F1}ft v2={m.V2MinRadiusFt:F1}ft ({m.RelativeDelta:P1})"
                );
            }
        }

        if (report.Gates.RunwayBearingMismatches.Count > 0)
        {
            sb.AppendLine($"  Runway bearing mismatches (>{RunwayBearingToleranceDeg}°): {report.Gates.RunwayBearingMismatches.Count}");
            foreach (var m in report.Gates.RunwayBearingMismatches.Take(5))
            {
                sb.AppendLine($"    {m.TaxiwayName} #{m.NodeIdA}↔#{m.NodeIdB}: legacy={m.LegacyBearingDeg:F1}° v2={m.V2BearingDeg:F1}°");
            }
        }
    }

    private static bool AllReachabilitySetsEqual(IReadOnlyList<HashSet<int>> sets)
    {
        if (sets.Count == 0)
        {
            return true;
        }

        var first = sets[0];
        return sets.Skip(1).All(s => s.SetEquals(first));
    }

    private static int BucketBearingDeg(double bearingDeg)
    {
        int bucket = (int)(Math.Round(bearingDeg / CornerBearingToleranceDeg) * CornerBearingToleranceDeg);
        return ((bucket % 360) + 360) % 360;
    }

    private static bool TryGetCornerArcIdentity(GroundArc arc, out int junctionId, out string taxiwayKey)
    {
        if (arc.FilletProvenance is CornerArcProvenance prov)
        {
            junctionId = prov.IntersectionId;
            taxiwayKey = prov.NormalizedTaxiwayKey;
            return true;
        }

        string? origin = arc.Origin;
        if (origin is not null)
        {
            var match = V2CornerOriginRegex.Match(origin);
            if (match.Success)
            {
                string twyA = match.Groups["twyA"].Value;
                string twyB = match.Groups["twyB"].Value;
                junctionId = int.Parse(match.Groups["junction"].Value);
                taxiwayKey = string.CompareOrdinal(twyA, twyB) <= 0 ? $"{twyA}/{twyB}" : $"{twyB}/{twyA}";
                return true;
            }
        }

        junctionId = 0;
        taxiwayKey = "";
        return false;
    }

    private static class Reachability
    {
        /// <summary>
        /// Pre-fillet node IDs still present in the filleted layout and reachable from hold shorts.
        /// Tangent nodes created during fillet are excluded so Legacy vs V2 compares operational connectivity.
        /// </summary>
        public static HashSet<int> ReachableStableIdsFromHoldShorts(AirportGroundLayout preFillet, AirportGroundLayout layout)
        {
            var stableIds = preFillet.Nodes.Keys.Where(layout.Nodes.ContainsKey).ToHashSet();
            var seeds = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).Select(n => n.Id).ToList();
            if (seeds.Count == 0)
            {
                return stableIds;
            }

            var reachable = BfsFrom(seeds, layout);
            reachable.IntersectWith(stableIds);
            return reachable;
        }

        public static HashSet<int> ParkingReachableToHoldShort(AirportGroundLayout layout)
        {
            var holdShortIds = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).Select(n => n.Id).ToHashSet();
            if (holdShortIds.Count == 0)
            {
                return layout.Nodes.Values.Where(n => n.Type == GroundNodeType.Parking).Select(n => n.Id).ToHashSet();
            }

            var reachableFromHoldShort = BfsFrom(holdShortIds, layout);
            return layout
                .Nodes.Values.Where(n => n.Type == GroundNodeType.Parking && reachableFromHoldShort.Contains(n.Id))
                .Select(n => n.Id)
                .ToHashSet();
        }

        private static HashSet<int> BfsFrom(IEnumerable<int> seeds, AirportGroundLayout layout)
        {
            var reachable = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (int seed in seeds)
            {
                if (reachable.Add(seed))
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
}
