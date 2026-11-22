using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Fillet;

internal static class FilletPlanExecutor
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletPlanExecutor");

    /// <summary>What the executor built.</summary>
    /// <param name="ArcsCreated">The fillet arcs added.</param>
    /// <param name="CollinearMerges">The through pairs of the junctions kept.</param>
    /// <param name="FilletedNodes">The junctions given cuts or through pairs.</param>
    /// <param name="PinnedArcs">The arc built for each pinned corner, so a later drop of one can be reported.</param>
    public sealed record ExecuteResult(
        int ArcsCreated,
        int CollinearMerges,
        int FilletedNodes,
        IReadOnlyList<(int JunctionNodeId, int CornerId, GroundArc Arc)> PinnedArcs
    );

    public static ExecuteResult Execute(
        AirportGroundLayout layout,
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctionPlans,
        NextNodeIdCounter idCounter,
        FilletReportCollector report
    )
    {
        Dictionary<CutId, GroundNode> cutNode = MaterializeCutNodes(layout, plan, junctionPlans, idCounter);

        GroundNode? ResolveEndpoint(FilletEndpoint ep) =>
            ep switch
            {
                FilletEndpoint.Cut cut => cutNode.GetValueOrDefault(cut.Id),
                FilletEndpoint.Node node => layout.Nodes.GetValueOrDefault(node.NodeId),
                _ => throw new InvalidOperationException($"Unknown FilletEndpoint subtype: {ep.GetType().Name}"),
            };

        // Remove the original edges the split consumes BEFORE adding survivors so a surviving
        // sub-segment identical to a consumed edge is not deduped away then orphaned.
        layout.Edges.RemoveAll(e => plan.EdgesToRemove.Contains(e));
        AddSurvivingEdges(layout, plan, ResolveEndpoint);
        (int arcsCreated, List<(int JunctionNodeId, int CornerId, GroundArc Arc)> pinnedArcs) = AddCornerArcs(
            layout,
            plan,
            junctionPlans,
            ResolveEndpoint,
            report
        );
        AddStraightConnectors(layout, plan, ResolveEndpoint);
        RemoveJunctionNodes(layout, plan);

        int collinearMerges = junctionPlans.Where(jp => !plan.JunctionNodesToRemove.Contains(jp.JunctionNodeId)).Sum(jp => jp.CollinearPairs.Count);
        int filletedNodes = junctionPlans.Count(jp =>
            plan.Cuts.Values.Any(c => c.JunctionNodeId == jp.JunctionNodeId) || (jp.CollinearPairs.Count > 0)
        );

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "Fillet executor: {Arcs} arcs, {Surviving} surviving edges, {Collinear} collinear, {Nodes} junctions",
                arcsCreated,
                plan.SurvivingEdges.Count,
                collinearMerges,
                filletedNodes
            );
        }

        return new ExecuteResult(arcsCreated, collinearMerges, filletedNodes, pinnedArcs);
    }

    /// <summary>Adds an edge for each of the plan's surviving edges whose two endpoints resolve to nodes.</summary>
    private static void AddSurvivingEdges(AirportGroundLayout layout, FilletPlan plan, Func<FilletEndpoint, GroundNode?> resolve)
    {
        foreach (SurvivingEdgeOp op in plan.SurvivingEdges)
        {
            GroundNode? from = resolve(op.From);
            GroundNode? to = resolve(op.To);
            if ((from is null) || (to is null))
            {
                continue;
            }

            AddEdge(layout, from, to, op.TaxiwayName, op.Origin);
        }
    }

    /// <summary>
    /// Adds each of the plan's corner arcs, or its chord when the arc is too tight for an unforced corner, and reports each;
    /// returns the arcs added and the arc of each pinned corner.
    /// </summary>
    private static (int ArcsCreated, List<(int JunctionNodeId, int CornerId, GroundArc Arc)> PinnedArcs) AddCornerArcs(
        AirportGroundLayout layout,
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctionPlans,
        Func<FilletEndpoint, GroundNode?> resolve,
        FilletReportCollector report
    )
    {
        (Dictionary<(int Junction, int Corner), CornerSpec> cornerByKey, Dictionary<int, LatLon> junctionPosById) = IndexJunctions(junctionPlans);
        int arcsCreated = 0;
        var pinnedArcs = new List<(int JunctionNodeId, int CornerId, GroundArc Arc)>();
        foreach (CornerArcOp arcOp in plan.CornerArcs)
        {
            if (!cornerByKey.TryGetValue((arcOp.JunctionNodeId, arcOp.CornerId), out CornerSpec? corner))
            {
                continue;
            }

            if (TangentNodes(arcOp, corner, resolve, report) is not { } tangents)
            {
                continue;
            }

            FilletGeometry.BezierBuildResult bez = CornerBezier(
                corner,
                junctionPosById[arcOp.JunctionNodeId],
                tangents.A.Position,
                tangents.B.Position
            );

            // A too-tight fillet degrades to the sharp corner: emit the chord so the cuts stay
            // connected rather than relying on a degenerate arc the normalizer would delete. A
            // forced corner keeps its arc: planning refuses a forced arc too tight to steer.
            if ((!corner.IsForced) && (bez.MinRadiusFt < FilletConstants.RadiusFloorFt))
            {
                string chordOrigin = $"corner-chord@J{arcOp.JunctionNodeId}/{corner.EdgeA.TaxiwayName}";
                AddEdge(layout, tangents.A, tangents.B, corner.EdgeA.TaxiwayName, chordOrigin);
                report.ChordDegraded(arcOp.JunctionNodeId, arcOp.CornerId);
                continue;
            }

            GroundArc arc = CornerArc(corner, arcOp.JunctionNodeId, bez, tangents);
            layout.Arcs.Add(arc);
            if (corner.IsPinned)
            {
                pinnedArcs.Add((arcOp.JunctionNodeId, arcOp.CornerId, arc));
            }

            arcsCreated++;
            report.ArcBuilt(arcOp.JunctionNodeId, arcOp.CornerId, bez.MinRadiusFt);
        }

        return (arcsCreated, pinnedArcs);
    }

    /// <summary>Each planned corner by its junction and corner id, and each junction's position by its node id.</summary>
    private static (Dictionary<(int Junction, int Corner), CornerSpec> CornerByKey, Dictionary<int, LatLon> JunctionPosById) IndexJunctions(
        IReadOnlyList<JunctionPlan> junctionPlans
    )
    {
        var cornerByKey = new Dictionary<(int Junction, int Corner), CornerSpec>();
        var junctionPosById = new Dictionary<int, LatLon>();
        foreach (JunctionPlan jp in junctionPlans)
        {
            junctionPosById[jp.JunctionNodeId] = jp.JunctionNode.Position;
            foreach (CornerSpec corner in jp.Corners)
            {
                cornerByKey[(jp.JunctionNodeId, corner.CornerId)] = corner;
            }
        }

        return (cornerByKey, junctionPosById);
    }

    /// <summary>
    /// The two tangent nodes of <paramref name="arcOp"/>, or null, with the corner reported dropped, when an endpoint resolves
    /// to no node or both resolve to one; a forced corner never loses an endpoint after planning.
    /// </summary>
    private static (GroundNode A, GroundNode B)? TangentNodes(
        CornerArcOp arcOp,
        CornerSpec corner,
        Func<FilletEndpoint, GroundNode?> resolve,
        FilletReportCollector report
    )
    {
        GroundNode? tanA = resolve(arcOp.EndpointAtArmA);
        GroundNode? tanB = resolve(arcOp.EndpointAtArmB);
        if ((tanA is null) || (tanB is null) || (tanA.Id == tanB.Id))
        {
            if (corner.IsForced)
            {
                throw new UnreachableException($"Forced corner J{arcOp.JunctionNodeId}/{arcOp.CornerId} lost an endpoint after planning");
            }

            report.CornerDropped(
                arcOp.JunctionNodeId,
                arcOp.CornerId,
                ((tanA is null) || (tanB is null)) ? FilletSkipReason.UnresolvedEndpoint : FilletSkipReason.CoincidentCuts
            );
            return null;
        }

        return (tanA, tanB);
    }

    /// <summary>Adds an edge for each of the plan's straight connectors whose endpoints resolve to two distinct nodes.</summary>
    private static void AddStraightConnectors(AirportGroundLayout layout, FilletPlan plan, Func<FilletEndpoint, GroundNode?> resolve)
    {
        foreach (StraightConnectorOp op in plan.StraightConnectors)
        {
            GroundNode? tanA = resolve(op.EndpointAtArmA);
            GroundNode? tanB = resolve(op.EndpointAtArmB);
            if ((tanA is null) || (tanB is null) || (tanA.Id == tanB.Id))
            {
                continue;
            }

            AddEdge(layout, tanA, tanB, op.TaxiwayName, $"straight-connector@J{op.JunctionNodeId}/{op.TaxiwayName}");
        }
    }

    /// <summary>Deletes every junction node the plan removes, with its incident edges and arcs.</summary>
    private static void RemoveJunctionNodes(AirportGroundLayout layout, FilletPlan plan)
    {
        foreach (int intId in plan.JunctionNodesToRemove)
        {
            layout.Edges.RemoveAll(e => (e.Nodes[0].Id == intId) || (e.Nodes[1].Id == intId));
            layout.Arcs.RemoveAll(a => (a.Nodes[0].Id == intId) || (a.Nodes[1].Id == intId));
            layout.Nodes.Remove(intId);
        }
    }

    /// <summary>The fillet arc of <paramref name="corner"/> between its tangent nodes, shaped as <paramref name="bez"/>.</summary>
    private static GroundArc CornerArc(
        CornerSpec corner,
        int junctionNodeId,
        FilletGeometry.BezierBuildResult bez,
        (GroundNode A, GroundNode B) tangents
    )
    {
        bool sameTaxiway = corner.EdgeA.SharesTaxiway(corner.EdgeB);
        return new GroundArc
        {
            Nodes = [tangents.A, tangents.B],
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
            Origin = $"corner@J{junctionNodeId}/{corner.EdgeA.TaxiwayName}/{corner.EdgeB.TaxiwayName}",
        };
    }

    private static Dictionary<CutId, GroundNode> MaterializeCutNodes(
        AirportGroundLayout layout,
        FilletPlan plan,
        IReadOnlyList<JunctionPlan> junctionPlans,
        NextNodeIdCounter idCounter
    )
    {
        var cutNode = new Dictionary<CutId, GroundNode>();
        foreach ((CutId cutId, ResolvedArmCut? cut) in plan.Cuts)
        {
            JunctionPlan? junctionPlan = junctionPlans.FirstOrDefault(j => j.JunctionNodeId == cut.JunctionNodeId);
            LatLon junctionPos = junctionPlan?.JunctionNode.Position ?? cut.Position;

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
                Origin = $"tangent-cut@J{cut.JunctionNodeId}/{(junctionPlan is not null ? GetTaxiwayName(junctionPlan, cut.ArmId) : "?")}",
            };
            layout.Nodes[id] = tanNode;
            cutNode[cutId] = tanNode;
        }

        return cutNode;
    }

    /// <summary>
    /// The bezier the executor builds for <paramref name="corner"/> between tangent points <paramref name="tanA"/> and
    /// <paramref name="tanB"/>: a clean circular arc sized to the radius the cut spacing supports, capped by the corner's
    /// requested radius, so the stored MinRadiusOfCurvatureFt is honest instead of an over-bulged requested-radius bezier.
    /// </summary>
    internal static FilletGeometry.BezierBuildResult CornerBezier(CornerSpec corner, LatLon junctionPos, LatLon tanA, LatLon tanB)
    {
        double taFt = GeoMath.DistanceNm(junctionPos, tanA) * GeoMath.FeetPerNm;
        double tbFt = GeoMath.DistanceNm(junctionPos, tanB) * GeoMath.FeetPerNm;
        double effectiveR = FilletGeometry.EffectiveMinRadiusFt(taFt, tbFt, corner.BearingAToJunctionDeg, corner.BearingBToJunctionDeg, tanA, tanB);
        // Both tangents pinned: the radius is the one the pinned cuts allow, whatever the corner asked for.
        double arcRadiusFt = corner.RadiusFromPins ? effectiveR : Math.Min(corner.RequestedRadiusFt, effectiveR);
        return FilletGeometry.BuildBezier(tanA, tanB, corner.BearingAToJunctionDeg, corner.BearingBToJunctionDeg, arcRadiusFt);
    }

    /// <summary>
    /// Where <paramref name="endpoint"/> of a planned arc lands, as the executor resolves it: a surviving cut's position or a
    /// pre-fillet node's; null when it resolves to nothing.
    /// </summary>
    internal static LatLon? PlannedPosition(AirportGroundLayout layout, FilletPlan plan, FilletEndpoint endpoint) =>
        endpoint switch
        {
            FilletEndpoint.Cut cut => plan.Cuts.TryGetValue(cut.Id, out ResolvedArmCut? resolved) ? resolved.Position : null,
            FilletEndpoint.Node node => layout.Nodes.TryGetValue(node.NodeId, out GroundNode? found) ? found.Position : null,
            _ => throw new InvalidOperationException($"Unknown FilletEndpoint subtype: {endpoint.GetType().Name}"),
        };

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
