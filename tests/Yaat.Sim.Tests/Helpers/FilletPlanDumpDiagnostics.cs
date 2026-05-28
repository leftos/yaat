using System.Text;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Data.Airport.Fillet.V2;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>Per-junction plan slice for connectivity decode (round-5).</summary>
public static class FilletPlanDumpDiagnostics
{
    internal sealed record BuildArtifacts(
        AirportGroundLayout PreLayout,
        IReadOnlyList<JunctionPlan> JunctionPlans,
        IReadOnlyList<ArmCutResolver.JunctionCutResult> CutResults,
        FilletPlan Plan,
        IReadOnlyList<ArmChainEdgeOp> ArmChainsAfterRedirect,
        IReadOnlyList<ArmChainEdgeOp> ArmChainsAfterFromToDrop,
        IReadOnlySet<int> JunctionNodesToRemove
    );

    internal sealed record GapJunctionResolution(int? ConsumingJunctionId, string Report);

    /// <summary>Walk pre-fillet adjacency from probe toward gap next node to find which removed junction consumed the link.</summary>
    internal static GapJunctionResolution ResolveJunctionForGapFromPreFillet(BuildArtifacts artifacts, int probeNodeId, int gapNextNodeId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== gap junction resolution probe={probeNodeId} next={gapNextNodeId} ===");

        if (!artifacts.PreLayout.Nodes.TryGetValue(probeNodeId, out var probe))
        {
            return new GapJunctionResolution(null, sb.AppendLine("probe node missing from pre-fillet layout").ToString());
        }

        int? consuming = null;

        sb.AppendLine("Probe pre-fillet edges:");
        foreach (var edge in probe.Edges.OfType<GroundEdge>())
        {
            int other = edge.OtherNodeId(probeNodeId);
            bool otherRemoved = artifacts.JunctionNodesToRemove.Contains(other);
            bool otherInFilletSet = artifacts.JunctionPlans.Any(j => j.JunctionNodeId == other);
            sb.AppendLine($"  {probeNodeId}->{other} twy={edge.TaxiwayName} otherInRemoveSet={otherRemoved} inActiveFilletSet={otherInFilletSet}");
            if (otherRemoved && (consuming is null))
            {
                consuming = other;
            }
        }

        bool directEdge = probe.Edges.OfType<GroundEdge>().Any(e => e.OtherNodeId(probeNodeId) == gapNextNodeId);
        sb.AppendLine($"Direct pre-fillet edge {probeNodeId}->{gapNextNodeId}: {directEdge}");

        if (artifacts.JunctionNodesToRemove.Contains(gapNextNodeId))
        {
            sb.AppendLine($"Gap next node {gapNextNodeId} is in JunctionNodesToRemove");
            consuming ??= gapNextNodeId;
        }

        if (artifacts.PreLayout.Nodes.TryGetValue(gapNextNodeId, out var nextNode))
        {
            sb.AppendLine($"Gap next node {gapNextNodeId} pre-fillet edges:");
            foreach (var edge in nextNode.Edges.OfType<GroundEdge>())
            {
                int other = edge.OtherNodeId(gapNextNodeId);
                bool otherRemoved = artifacts.JunctionNodesToRemove.Contains(other);
                sb.AppendLine($"  {gapNextNodeId}->{other} twy={edge.TaxiwayName} otherInRemoveSet={otherRemoved}");
                if (otherRemoved && (consuming is null))
                {
                    consuming = other;
                }
            }
        }

        sb.AppendLine("Removed junctions touching probe or gap-next (plan arms/reconnect):");
        foreach (var jp in artifacts.JunctionPlans.Where(j => artifacts.JunctionNodesToRemove.Contains(j.JunctionNodeId)))
        {
            bool planTouch =
                artifacts.Plan.ReconnectEdges.Any(r =>
                    (r.JunctionNodeId == jp.JunctionNodeId) && ((r.OtherNodeId == probeNodeId) || (r.OtherNodeId == gapNextNodeId))
                )
                || jp.Arms.Any(a =>
                {
                    int remote = a.RootEdge.OtherNode(jp.JunctionNode).Id;
                    return (remote == probeNodeId)
                        || (remote == gapNextNodeId)
                        || (a.TerminalNode.Id == probeNodeId)
                        || (a.TerminalNode.Id == gapNextNodeId);
                });

            if (planTouch)
            {
                sb.AppendLine($"  J{jp.JunctionNodeId} PreserveNode={jp.PreserveNode}");
                consuming ??= jp.JunctionNodeId;
            }
        }

        if (consuming is int jId)
        {
            sb.AppendLine($"Consuming junction: J{jId}");
        }
        else
        {
            sb.AppendLine("Consuming junction: none identified (edge may be unconsumed input stable↔stable)");
        }

        return new GapJunctionResolution(consuming, sb.ToString());
    }

    /// <summary>One-hop neighborhood trace: pre-fillet adjacency vs post-V2 node/edge survival (round-7 OAK decode).</summary>
    internal static string FormatPreFilletNeighborhoodTrace(BuildArtifacts artifacts, int probeNodeId, int maxDepth)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== pre-fillet neighborhood trace from node {probeNodeId} (depth={maxDepth}) ===");

        var v2 = LayoutCloner.DeepClone(artifacts.PreLayout);
        _ = new FilletArcGeneratorV2().Apply(v2);

        var visited = new HashSet<int>();
        TraceNode(artifacts.PreLayout, v2, artifacts, probeNodeId, 0, maxDepth, visited, sb);

        if (artifacts.PreLayout.Nodes.TryGetValue(probeNodeId, out var probe))
        {
            sb.AppendLine($"Probe {probeNodeId} in V2 after apply: {v2.Nodes.ContainsKey(probeNodeId)}");
            if (v2.Nodes.TryGetValue(probeNodeId, out var v2Probe))
            {
                sb.AppendLine($"  V2 edge count: {v2Probe.Edges.Count}");
            }
        }

        return sb.ToString();
    }

    private static void TraceNode(
        AirportGroundLayout pre,
        AirportGroundLayout v2,
        BuildArtifacts artifacts,
        int nodeId,
        int depth,
        int maxDepth,
        HashSet<int> visited,
        StringBuilder sb
    )
    {
        if (!visited.Add(nodeId))
        {
            return;
        }

        if (!pre.Nodes.TryGetValue(nodeId, out var preNode))
        {
            sb.AppendLine($"{new string(' ', depth * 2)}node {nodeId}: missing from pre-fillet");
            return;
        }

        bool inV2 = v2.Nodes.ContainsKey(nodeId);
        int v2Edges = inV2 ? v2.Nodes[nodeId].Edges.Count : 0;
        bool inRemoveSet = artifacts.JunctionNodesToRemove.Contains(nodeId);
        bool inFilletSet = artifacts.JunctionPlans.Any(j => j.JunctionNodeId == nodeId);
        string indent = new string(' ', depth * 2);

        sb.AppendLine(
            $"{indent}node {nodeId} type={preNode.Type} inV2={inV2} v2Edges={v2Edges} " + $"inRemoveSet={inRemoveSet} inFilletSet={inFilletSet}"
        );

        if (depth >= maxDepth)
        {
            return;
        }

        foreach (var edge in preNode.Edges.OfType<GroundEdge>())
        {
            int other = edge.OtherNodeId(nodeId);
            bool edgeSurvivesInV2 =
                inV2 && v2.Nodes.TryGetValue(other, out _) && v2.Nodes[nodeId].Edges.Any(e => e is GroundEdge ge && ge.OtherNodeId(nodeId) == other);
            sb.AppendLine($"{indent}  edge {nodeId}->{other} twy={edge.TaxiwayName} survivesInV2={edgeSurvivesInV2}");
            TraceNode(pre, v2, artifacts, other, depth + 1, maxDepth, visited, sb);
        }
    }

    internal static string FormatPreservedJunctionPostExecuteCheck(string shortId, int junctionNodeId)
    {
        var artifacts = TryBuild(shortId);
        if (artifacts is null)
        {
            return $"{shortId}: layout missing";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== {shortId} preserved junction J{junctionNodeId} post-execute check ===");

        var jp = artifacts.JunctionPlans.FirstOrDefault(j => j.JunctionNodeId == junctionNodeId);
        sb.AppendLine($"  inActiveFilletSet={jp is not null}");
        if (jp is not null)
        {
            sb.AppendLine($"  PreserveNode={jp.PreserveNode} Kind={jp.Kind}");
        }

        sb.AppendLine($"  inJunctionNodesToRemove={artifacts.JunctionNodesToRemove.Contains(junctionNodeId)}");

        var v2 = LayoutCloner.DeepClone(artifacts.PreLayout);
        _ = new FilletArcGeneratorV2().Apply(v2);
        bool inV2 = v2.Nodes.ContainsKey(junctionNodeId);
        int edgeCount = inV2 ? v2.Nodes[junctionNodeId].Edges.Count : 0;
        sb.AppendLine($"  inV2AfterApply={inV2} v2EdgeCount={edgeCount}");

        if (inV2)
        {
            sb.AppendLine("  V2 incident edges:");
            foreach (var edge in v2.Nodes[junctionNodeId].Edges.OfType<GroundEdge>())
            {
                int other = edge.OtherNodeId(junctionNodeId);
                sb.AppendLine($"    {junctionNodeId}->{other} twy={edge.TaxiwayName} origin={edge.Origin ?? "<input>"}");
            }
        }

        return sb.ToString();
    }

    internal static string FormatNodeEdgesAfterV2(string shortId, params int[] nodeIds)
    {
        var artifacts = TryBuild(shortId);
        if (artifacts is null)
        {
            return $"{shortId}: layout missing";
        }

        var v2 = LayoutCloner.DeepClone(artifacts.PreLayout);
        _ = new FilletArcGeneratorV2().Apply(v2);

        var sb = new StringBuilder();
        sb.AppendLine($"=== {shortId} V2 edges at nodes after apply ===");
        foreach (int nodeId in nodeIds)
        {
            if (!v2.Nodes.TryGetValue(nodeId, out var node))
            {
                sb.AppendLine($"  node {nodeId}: MISSING from V2");
                continue;
            }

            sb.AppendLine($"  node {nodeId}: type={node.Type} edgeCount={node.Edges.Count}");
            foreach (var edge in node.Edges.OfType<GroundEdge>())
            {
                int other = edge.OtherNodeId(nodeId);
                sb.AppendLine($"    {nodeId}->{other} twy={edge.TaxiwayName} origin={edge.Origin ?? "<input>"}");
            }
        }

        return sb.ToString();
    }

    internal static string FormatProbePreFilletEdges(AirportGroundLayout pre, int nodeId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== pre-fillet edges at node {nodeId} ===");
        if (!pre.Nodes.TryGetValue(nodeId, out var node))
        {
            sb.AppendLine("  MISSING");
            return sb.ToString();
        }

        sb.AppendLine($"  type={node.Type}");
        foreach (var edge in node.Edges.OfType<GroundEdge>())
        {
            int other = edge.OtherNodeId(nodeId);
            sb.AppendLine($"  {nodeId}->{other} twy={edge.TaxiwayName} origin={edge.Origin ?? "<input>"}");
        }

        return sb.ToString();
    }

    internal static string FormatGapTargetNodeTypes(AirportGroundLayout pre, params int[] nodeIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== gap-target node types (pre-fillet) ===");
        foreach (int id in nodeIds)
        {
            if (pre.Nodes.TryGetValue(id, out var n))
            {
                sb.AppendLine($"  node {id}: type={n.Type} edges={n.Edges.Count}");
            }
            else
            {
                sb.AppendLine($"  node {id}: MISSING");
            }
        }

        return sb.ToString();
    }

    internal static int? ResolveJunctionForGap(BuildArtifacts artifacts, int probeNodeId) =>
        artifacts
            .JunctionPlans.FirstOrDefault(jp =>
                artifacts.Plan.ReconnectEdges.Any(r => (r.JunctionNodeId == jp.JunctionNodeId) && (r.OtherNodeId == probeNodeId))
                || artifacts.Plan.ArmChainEdges.Any(c =>
                    (c.JunctionNodeId == jp.JunctionNodeId)
                    && (
                        ((c.FromCutId is null) && jp.Arms.Any(a => (a.Id == c.ArmId) && (a.RootEdge.OtherNode(jp.JunctionNode).Id == probeNodeId)))
                        || (c.TerminalNodeId == probeNodeId)
                    )
                )
                || jp.Arms.Any(a => a.RootEdge.OtherNode(jp.JunctionNode).Id == probeNodeId)
            )
            ?.JunctionNodeId;

    internal static BuildArtifacts? TryBuild(string shortId)
    {
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        var layout = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
        layout.RebuildAdjacencyLists();
        var manualArcNodes = ManualArcDetector.Detect(layout);
        var junctionPlans = new List<JunctionPlan>();
        var cutResults = new List<ArmCutResolver.JunctionCutResult>();
        int nextCutId = 1;

        foreach (var node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            if (manualArcNodes.Contains(node.Id))
            {
                continue;
            }

            if (!FilletEligibility.IsEligible(node, out bool preserve))
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(node.Id, out var current) || (current.Edges.Count < 2))
            {
                continue;
            }

            var junction = JunctionClassifier.Classify(current, preserve, manualArcNodes);
            if (junction.Kind == JunctionKind.Skip)
            {
                continue;
            }

            var cutResult = ArmCutResolver.Resolve(junction, ref nextCutId);
            if ((cutResult.CornerArcs.Count == 0) && (junction.CollinearPairs.Count == 0))
            {
                continue;
            }

            junctionPlans.Add(junction);
            cutResults.Add(cutResult);
        }

        var plan = FilletPlanBuilder.Build(layout, junctionPlans, cutResults);

        var nodesToRemove = plan.JunctionNodesToRemove.ToHashSet();
        var cuts = new Dictionary<int, ResolvedArmCut>();
        for (int i = 0; i < junctionPlans.Count; i++)
        {
            foreach (var (id, cut) in cutResults[i].Cuts)
            {
                cuts[id] = cut;
            }
        }

        var redirect = FilletPlanCutRedirect.BuildSurvivorMap(plan.TangentMerges);
        var preFilletStableNodes = layout
            .Nodes.Where(kv => FilletPlanCutRedirect.IsStableAnchorTarget(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        FilletPlanCutRedirect.ExtendWithStableAnchors(
            redirect,
            cuts,
            preFilletStableNodes,
            Yaat.Sim.Data.Airport.Fillet.FilletConstants.CoincidentNodeThresholdFt
        );
        var prunedCuts = FilletPlanCutRedirect.PruneCuts(cuts, redirect);
        var preFilletIds = layout.Nodes.Keys.ToHashSet();
        var rawChains = FilletArmChainPlanner.BuildChainEdges(layout, junctionPlans, prunedCuts, redirect, nodesToRemove, preFilletIds);
        var afterRedirect = FilletPlanCutRedirect.RedirectArmChainEdges(rawChains, redirect).ToList();
        var afterFromToDrop = afterRedirect.Where(op => !((op.FromCutId is int from) && (op.ToCutId is int to) && (from == to))).ToList();

        return new BuildArtifacts(layout, junctionPlans, cutResults, plan, afterRedirect, afterFromToDrop, nodesToRemove);
    }

    internal static string FormatJunctionDump(string shortId, int junctionNodeId, int probeNodeId, BuildArtifacts artifacts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {shortId} plan dump J{junctionNodeId} (probe node {probeNodeId}) ===");

        var jp = artifacts.JunctionPlans.FirstOrDefault(j => j.JunctionNodeId == junctionNodeId);
        if (jp is null)
        {
            sb.AppendLine("  junction not in active fillet set");
            return sb.ToString();
        }

        sb.AppendLine($"  PreserveNode={jp.PreserveNode} Kind={jp.Kind} CollinearPairs={jp.CollinearPairs.Count}");
        sb.AppendLine("  Arms:");
        foreach (var arm in jp.Arms)
        {
            sb.AppendLine(
                $"    arm {arm.Id} twy={arm.TaxiwayName} terminus={arm.Terminus} "
                    + $"remote={arm.RootEdge.OtherNode(jp.JunctionNode).Id} terminal={arm.TerminalNode.Id}"
            );
        }

        sb.AppendLine("  Cuts (survivor plan.Cuts):");
        foreach (var cut in artifacts.Plan.Cuts.Values.Where(c => c.JunctionNodeId == junctionNodeId).OrderBy(c => c.ArmId))
        {
            sb.AppendLine(
                $"    cut {cut.CutId} arm={cut.ArmId} distFt={cut.DistanceAlongArmFt:F1} " + $"pos=({cut.Position.Lat:F6},{cut.Position.Lon:F6})"
            );
        }

        void DumpChains(string label, IEnumerable<ArmChainEdgeOp> ops)
        {
            var list = ops.Where(o => o.JunctionNodeId == junctionNodeId).ToList();
            sb.AppendLine($"  ArmChainEdgeOp {label} ({list.Count}):");
            foreach (var op in list)
            {
                sb.AppendLine(
                    $"    arm={op.ArmId} from={op.FromCutId?.ToString() ?? op.FromStableNodeId?.ToString() ?? "remote"} "
                        + $"to={op.ToCutId?.ToString() ?? (op.TerminalNodeId?.ToString() ?? "?")} twy={op.TaxiwayName}"
                );
            }
        }

        DumpChains("after-redirect", artifacts.ArmChainsAfterRedirect);
        DumpChains("after-from==to-drop", artifacts.ArmChainsAfterFromToDrop);
        var dropped = artifacts
            .ArmChainsAfterRedirect.Where(o => o.JunctionNodeId == junctionNodeId)
            .Where(o => (o.FromCutId is int from) && (o.ToCutId is int to) && (from == to))
            .ToList();
        if (dropped.Count > 0)
        {
            sb.AppendLine($"  ArmChain dropped by from==to filter ({dropped.Count}):");
            foreach (var op in dropped)
            {
                sb.AppendLine($"    arm={op.ArmId} cut={op.FromCutId}");
            }
        }

        sb.AppendLine("  ReconnectEdgeOp:");
        foreach (var r in artifacts.Plan.ReconnectEdges.Where(r => r.JunctionNodeId == junctionNodeId))
        {
            sb.AppendLine($"    other={r.OtherNodeId} targetCut={r.TargetCutId?.ToString() ?? "preserve-junction"} twy={r.TaxiwayName}");
        }

        sb.AppendLine("  StraightConnector / CornerArc / Bypass:");
        foreach (var s in artifacts.Plan.StraightConnectors.Where(s => s.JunctionNodeId == junctionNodeId))
        {
            sb.AppendLine($"    straight corner={s.CornerId} cuts={s.CutIdAtArmA}/{s.CutIdAtArmB} twy={s.TaxiwayName}");
        }

        foreach (var corner in jp.Corners)
        {
            var arc = artifacts.Plan.CornerArcs.FirstOrDefault(a => a.CornerId == corner.CornerId);
            if (arc is not null)
            {
                sb.AppendLine($"    corner {arc.CornerId} cuts={arc.CutIdAtArmA}/{arc.CutIdAtArmB}");
            }
        }

        foreach (var b in artifacts.Plan.ArmBypasses.Where(b => b.JunctionNodeId == junctionNodeId))
        {
            sb.AppendLine($"    bypass arm={b.ArmId} remote={b.RemoteNodeId} terminal={b.TerminalNodeId}");
        }

        sb.AppendLine($"  JunctionNodesToRemove contains J{junctionNodeId}: {artifacts.JunctionNodesToRemove.Contains(junctionNodeId)}");

        var safetyWarnings = artifacts
            .Plan.Warnings.Where(w => w.JunctionNodeId == junctionNodeId || w.Message.Contains($"node {probeNodeId}", StringComparison.Ordinal))
            .Where(w =>
                w.Code == PlanWarning.UnconsumedReconnectSafetyNet
                || w.Code == PlanWarning.UnconsumedIncidentEdge
                || w.Code == PlanWarning.NoOwningCut
            )
            .ToList();
        sb.AppendLine($"  Planner warnings at/near junction ({safetyWarnings.Count}):");
        foreach (var w in safetyWarnings)
        {
            sb.AppendLine($"    [{w.Code}] {w.Message}");
        }

        var v2 = LayoutCloner.DeepClone(artifacts.PreLayout);
        _ = new FilletArcGeneratorV2().Apply(v2);
        sb.AppendLine($"  V2 edges touching probe node {probeNodeId}:");
        if (v2.Nodes.TryGetValue(probeNodeId, out var probe))
        {
            foreach (var edge in probe.Edges.OfType<GroundEdge>())
            {
                int other = edge.OtherNodeId(probeNodeId);
                sb.AppendLine($"    {probeNodeId}->{other} twy={edge.TaxiwayName} origin={edge.Origin ?? "?"}");
            }
        }
        else
        {
            sb.AppendLine("    (probe node absent after V2 apply)");
        }

        return sb.ToString();
    }

    internal static string FormatPlanNodeTrace(BuildArtifacts artifacts, params int[] nodeIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== plan trace for nodes [{string.Join(", ", nodeIds)}] ===");

        foreach (int nodeId in nodeIds)
        {
            sb.AppendLine($"--- node {nodeId} ---");
            bool inPre = artifacts.PreLayout.Nodes.ContainsKey(nodeId);
            sb.AppendLine($"  pre-fillet present: {inPre}");
            if (inPre)
            {
                sb.AppendLine($"  pre-fillet type: {artifacts.PreLayout.Nodes[nodeId].Type} edges={artifacts.PreLayout.Nodes[nodeId].Edges.Count}");
            }

            sb.AppendLine($"  JunctionNodesToRemove: {artifacts.JunctionNodesToRemove.Contains(nodeId)}");
            var filletJunction = artifacts.JunctionPlans.FirstOrDefault(j => j.JunctionNodeId == nodeId);
            if (filletJunction is not null)
            {
                sb.AppendLine($"  active fillet junction J{nodeId} PreserveNode={filletJunction.PreserveNode}");
            }

            var edgeRemovals = artifacts.Plan.EdgesToRemove.Where(e => (e.Nodes[0].Id == nodeId) || (e.Nodes[1].Id == nodeId)).ToList();
            sb.AppendLine($"  EdgesToRemove touching node ({edgeRemovals.Count}):");
            foreach (var er in edgeRemovals.Take(12))
            {
                sb.AppendLine($"    {er.Nodes[0].Id}<->{er.Nodes[1].Id} twy={er.TaxiwayName}");
            }

            sb.AppendLine($"  TangentMerges (cut-id pairs, count={artifacts.Plan.TangentMerges.Count})");

            var chains = artifacts
                .ArmChainsAfterFromToDrop.Where(c => (c.TerminalNodeId == nodeId) || (c.FromStableNodeId == nodeId) || (c.JunctionNodeId == nodeId))
                .ToList();
            sb.AppendLine($"  ArmChainEdgeOp ({chains.Count}):");
            foreach (var c in chains.Take(12))
            {
                sb.AppendLine(
                    $"    J{c.JunctionNodeId} arm={c.ArmId} from={c.FromCutId?.ToString() ?? c.FromStableNodeId?.ToString() ?? "remote"} "
                        + $"to={c.ToCutId?.ToString() ?? c.TerminalNodeId?.ToString() ?? "?"} twy={c.TaxiwayName}"
                );
            }

            var reconnects = artifacts.Plan.ReconnectEdges.Where(r => r.OtherNodeId == nodeId).ToList();
            sb.AppendLine($"  ReconnectEdgeOp as other ({reconnects.Count}):");
            foreach (var r in reconnects.Take(8))
            {
                sb.AppendLine($"    J{r.JunctionNodeId} targetCut={r.TargetCutId?.ToString() ?? "preserve"} twy={r.TaxiwayName}");
            }
        }

        return sb.ToString();
    }
}
