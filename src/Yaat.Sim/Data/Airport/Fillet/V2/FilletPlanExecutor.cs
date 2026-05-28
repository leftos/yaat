using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport.Fillet;

namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal static class FilletPlanExecutor
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletPlanExecutor");

    public sealed record ExecuteResult(int ArcsCreated, int CollinearMerges, int FilletedNodes);

    public static ExecuteResult Execute(
        AirportGroundLayout layout,
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctionPlans,
        NextNodeIdCounter idCounter
    )
    {
        var cutNode = MaterializeCutNodes(layout, plan, junctionPlans, idCounter);

        var cornerByKey = new Dictionary<(int Junction, int Corner), CornerSpec>();
        var junctionPosById = new Dictionary<int, LatLon>();
        foreach (var jp in junctionPlans)
        {
            junctionPosById[jp.JunctionNodeId] = jp.JunctionNode.Position;
            foreach (var corner in jp.Corners)
            {
                cornerByKey[(jp.JunctionNodeId, corner.CornerId)] = corner;
            }
        }

        GroundNode? ResolveId(int id) => cutNode.TryGetValue(id, out var n) ? n : layout.Nodes.GetValueOrDefault(id);
        GroundNode? ResolveEndpoint(int? cutId, int? nodeId) =>
            cutId is int c ? cutNode.GetValueOrDefault(c)
            : nodeId is int nid ? layout.Nodes.GetValueOrDefault(nid)
            : null;

        // Remove the original edges the split consumes BEFORE adding survivors so a surviving
        // sub-segment identical to a consumed edge is not deduped away then orphaned.
        layout.Edges.RemoveAll(e => plan.EdgesToRemove.Contains(e));

        foreach (var op in plan.SurvivingEdges)
        {
            var from = ResolveEndpoint(op.FromCutId, op.FromNodeId);
            var to = ResolveEndpoint(op.ToCutId, op.ToNodeId);
            if ((from is null) || (to is null))
            {
                continue;
            }

            AddEdge(layout, from, to, op.TaxiwayName, op.Origin);
        }

        int arcsCreated = 0;
        foreach (var arcOp in plan.CornerArcs)
        {
            if (!cornerByKey.TryGetValue((arcOp.JunctionNodeId, arcOp.CornerId), out var corner))
            {
                continue;
            }

            var tanA = ResolveId(arcOp.CutIdAtArmA);
            var tanB = ResolveId(arcOp.CutIdAtArmB);
            if ((tanA is null) || (tanB is null) || (tanA.Id == tanB.Id))
            {
                continue;
            }

            // Build a clean circular arc from the actual tangent geometry: size the curve to the
            // radius the cut spacing supports (capped by the corner's policy radius), so the stored
            // MinRadiusOfCurvatureFt is honest instead of an over-bulged requested-radius bezier.
            double arcRadiusFt = corner.RequestedRadiusFt;
            if (junctionPosById.TryGetValue(arcOp.JunctionNodeId, out var junctionPos))
            {
                double taFt = GeoMath.DistanceNm(junctionPos, tanA.Position) * GeoMath.FeetPerNm;
                double tbFt = GeoMath.DistanceNm(junctionPos, tanB.Position) * GeoMath.FeetPerNm;
                double effectiveR = FilletGeometry.EffectiveMinRadiusFt(
                    taFt,
                    tbFt,
                    corner.BearingAToJunctionDeg,
                    corner.BearingBToJunctionDeg,
                    tanA.Position,
                    tanB.Position
                );
                arcRadiusFt = Math.Min(corner.RequestedRadiusFt, effectiveR);
            }

            var bez = FilletGeometry.BuildBezier(
                tanA.Position,
                tanB.Position,
                corner.BearingAToJunctionDeg,
                corner.BearingBToJunctionDeg,
                arcRadiusFt
            );

            // A too-tight fillet degrades to the sharp corner: emit the chord so the cuts stay
            // connected rather than relying on a degenerate arc the normalizer would delete.
            if (bez.MinRadiusFt < FilletConstants.RadiusFloorFt)
            {
                AddEdge(layout, tanA, tanB, corner.EdgeA.TaxiwayName, $"V2:corner-chord@J{arcOp.JunctionNodeId}/{corner.EdgeA.TaxiwayName}");
                continue;
            }

            bool sameTaxiway = corner.EdgeA.SharesTaxiway(corner.EdgeB);
            layout.Arcs.Add(
                new GroundArc
                {
                    Nodes = [tanA, tanB],
                    TaxiwayNames = sameTaxiway ? [corner.EdgeA.TaxiwayName] : [corner.EdgeA.TaxiwayName, corner.EdgeB.TaxiwayName],
                    P1Lat = bez.P1Lat,
                    P1Lon = bez.P1Lon,
                    P2Lat = bez.P2Lat,
                    P2Lon = bez.P2Lon,
                    MinRadiusOfCurvatureFt = bez.MinRadiusFt,
                    DistanceNm = bez.ArcLengthNm,
                    EdgeBearingAtNode0Deg = bez.BearingAFromTangentDeg,
                    EdgeBearingAtNode1Deg = bez.BearingBFromTangentDeg,
                    TurnAngleDeg = bez.EffectiveTurnDeg,
                    Origin = $"V2:corner@J{arcOp.JunctionNodeId}/{corner.EdgeA.TaxiwayName}/{corner.EdgeB.TaxiwayName}",
                }
            );
            arcsCreated++;
        }

        foreach (var op in plan.StraightConnectors)
        {
            var tanA = ResolveId(op.CutIdAtArmA);
            var tanB = ResolveId(op.CutIdAtArmB);
            if ((tanA is null) || (tanB is null) || (tanA.Id == tanB.Id))
            {
                continue;
            }

            AddEdge(layout, tanA, tanB, op.TaxiwayName, $"V2:straight-connector@J{op.JunctionNodeId}/{op.TaxiwayName}");
        }

        foreach (int intId in plan.JunctionNodesToRemove)
        {
            layout.Edges.RemoveAll(e => (e.Nodes[0].Id == intId) || (e.Nodes[1].Id == intId));
            layout.Arcs.RemoveAll(a => (a.Nodes[0].Id == intId) || (a.Nodes[1].Id == intId));
            layout.Nodes.Remove(intId);
        }

        int collinearMerges = junctionPlans.Where(jp => !plan.JunctionNodesToRemove.Contains(jp.JunctionNodeId)).Sum(jp => jp.CollinearPairs.Count);
        int filletedNodes = junctionPlans.Count(jp =>
            plan.Cuts.Values.Any(c => c.JunctionNodeId == jp.JunctionNodeId) || (jp.CollinearPairs.Count > 0)
        );

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "V2 executor: {Arcs} arcs, {Surviving} surviving edges, {Collinear} collinear, {Nodes} junctions",
                arcsCreated,
                plan.SurvivingEdges.Count,
                collinearMerges,
                filletedNodes
            );
        }

        return new ExecuteResult(arcsCreated, collinearMerges, filletedNodes);
    }

    private static Dictionary<int, GroundNode> MaterializeCutNodes(
        AirportGroundLayout layout,
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctionPlans,
        NextNodeIdCounter idCounter
    )
    {
        var cutNode = new Dictionary<int, GroundNode>();
        foreach (var (cutId, cut) in plan.Cuts)
        {
            var junctionPlan = junctionPlans.FirstOrDefault(j => j.JunctionNodeId == cut.JunctionNodeId);
            var junctionPos = junctionPlan?.JunctionNode.Position ?? cut.Position;

            int id = idCounter.Next++;
            while (layout.Nodes.ContainsKey(id))
            {
                id = idCounter.Next++;
            }

            var tanNode = new GroundNode
            {
                Id = id,
                Position = cut.Position,
                Type = GroundNodeType.TaxiwayIntersection,
                SourceIntersectionPosition = (junctionPos.Lat, junctionPos.Lon),
                Origin = $"V2:tangent-cut@J{cut.JunctionNodeId}/{(junctionPlan is not null ? GetTaxiwayName(junctionPlan, cut.ArmId) : "?")}",
            };
            layout.Nodes[id] = tanNode;
            cutNode[cutId] = tanNode;
        }

        return cutNode;
    }

    private static void AddEdge(AirportGroundLayout layout, GroundNode a, GroundNode b, string taxiwayName, string origin)
    {
        if ((a.Id == b.Id) || (!layout.Nodes.ContainsKey(a.Id)) || (!layout.Nodes.ContainsKey(b.Id)))
        {
            return;
        }

        bool alreadyExists = layout.Edges.Any(e =>
            string.Equals(e.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase)
            && (((e.Nodes[0].Id == a.Id) && (e.Nodes[1].Id == b.Id)) || ((e.Nodes[0].Id == b.Id) && (e.Nodes[1].Id == a.Id)))
        );
        if (alreadyExists)
        {
            return;
        }

        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [a, b],
                TaxiwayName = taxiwayName,
                DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
                Origin = origin,
            }
        );
    }

    private static string GetTaxiwayName(JunctionPlan junction, int armId) => junction.Arms.FirstOrDefault(a => a.Id == armId)?.TaxiwayName ?? "?";

    internal sealed class NextNodeIdCounter
    {
        public int Next { get; set; }
    }
}
