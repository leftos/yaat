namespace Yaat.Sim.Data.Airport.Fillet.V2;

/// <summary>Walk along a taxiway chain from an intersection (legacy-equivalent).</summary>
internal static class TaxiwayWalk
{
    internal sealed record WalkStep(GroundEdge Edge, GroundNode FarNode, double CumulativeDistFt, bool HasOtherTaxiways, bool IsProtected);

    internal sealed record WalkResult(double AvailableLengthFt, GroundNode TerminalNode, IReadOnlyList<WalkStep> Steps);

    internal static WalkResult Walk(GroundEdge startEdge, GroundNode intersection, HashSet<int> manualArcNodes)
    {
        var otherNode = startEdge.OtherNode(intersection);
        double firstEdgeFt = GeoMath.DistanceNm(intersection.Position, otherNode.Position) * GeoMath.FeetPerNm;

        bool hasOtherTw = otherNode.Edges.Any(e => (e is GroundEdge ge) && (ge.TaxiwayName != startEdge.TaxiwayName));
        bool isProtected = manualArcNodes.Contains(otherNode.Id) || startEdge.IsRunwayCenterline;
        var steps = new List<WalkStep> { new(startEdge, otherNode, firstEdgeFt, hasOtherTw, isProtected) };

        var visited = new HashSet<int> { intersection.Id, otherNode.Id };
        var currentNode = otherNode;
        var prevEdge = startEdge;
        double cumDist = firstEdgeFt;

        while (true)
        {
            GroundEdge? continuation = null;
            int count = 0;
            foreach (var e in currentNode.Edges)
            {
                if ((e is GroundEdge ge) && (ge != prevEdge) && (ge.TaxiwayName == startEdge.TaxiwayName))
                {
                    continuation = ge;
                    count++;
                }
            }

            if ((count != 1) || (currentNode.Type != GroundNodeType.TaxiwayIntersection))
            {
                break;
            }

            var nextNode = continuation!.OtherNode(currentNode);
            if (!visited.Add(nextNode.Id))
            {
                break;
            }

            double edgeFt = GeoMath.DistanceNm(currentNode.Position, nextNode.Position) * GeoMath.FeetPerNm;
            cumDist += edgeFt;

            bool nextHasOtherTw = nextNode.Edges.Any(e => (e is GroundEdge ge) && (ge.TaxiwayName != startEdge.TaxiwayName));
            bool nextIsProtected = manualArcNodes.Contains(nextNode.Id) || continuation.IsRunwayCenterline;
            steps.Add(new WalkStep(continuation, nextNode, cumDist, nextHasOtherTw, nextIsProtected));

            prevEdge = continuation;
            currentNode = nextNode;
        }

        return new WalkResult(cumDist, currentNode, steps);
    }

    /// <summary>
    /// Cap tangent distance at the first step where another taxiway branches off.
    /// Steps before <see cref="FilletConstants.MaxTangentDistFt"/> are ignored so nearby
    /// branch nodes do not change consumption (legacy Phase A behavior).
    /// </summary>
    internal static double DistToFirstIntersectionFt(WalkResult walk)
    {
        foreach (var step in walk.Steps)
        {
            if (step.HasOtherTaxiways && (step.CumulativeDistFt >= FilletConstants.MaxTangentDistFt))
            {
                return step.CumulativeDistFt;
            }
        }

        return double.MaxValue;
    }

    internal static (LatLon Position, double BearingTowardJunctionDeg) InterpolateAtDistanceFt(
        WalkResult walk,
        GroundNode intersection,
        double targetDistFt
    )
    {
        double remaining = targetDistFt;
        GroundEdge? prevEdge = null;

        for (int i = 0; i < walk.Steps.Count; i++)
        {
            var step = walk.Steps[i];
            double stepLenFt = i == 0 ? step.CumulativeDistFt : step.CumulativeDistFt - walk.Steps[i - 1].CumulativeDistFt;

            if (remaining <= stepLenFt + 1e-6)
            {
                var fromNode = i == 0 ? intersection : walk.Steps[i - 1].FarNode;
                var toNode = step.FarNode;
                double frac = stepLenFt > 0 ? remaining / stepLenFt : 0;
                double lat = fromNode.Position.Lat + (frac * (toNode.Position.Lat - fromNode.Position.Lat));
                double lon = fromNode.Position.Lon + (frac * (toNode.Position.Lon - fromNode.Position.Lon));
                double bearingToJunction = GeoMath.BearingTo(new LatLon(lat, lon), intersection.Position);
                return (new LatLon(lat, lon), bearingToJunction);
            }

            remaining -= stepLenFt;
            prevEdge = step.Edge;
        }

        var terminal = walk.TerminalNode;
        return (terminal.Position, GeoMath.BearingTo(terminal.Position, intersection.Position));
    }

    internal static TaxiwayArmTerminus ClassifyTerminus(GroundNode terminal, WalkResult walk, bool isRunwayCenterline)
    {
        if (isRunwayCenterline)
        {
            return TaxiwayArmTerminus.RunwayCenterline;
        }

        if (terminal.Type == GroundNodeType.RunwayHoldShort)
        {
            return TaxiwayArmTerminus.HoldShort;
        }

        if (terminal.Type == GroundNodeType.Parking || terminal.Type == GroundNodeType.Spot)
        {
            return TaxiwayArmTerminus.Parking;
        }

        bool hasOtherTw = terminal.Edges.Any(e => e is GroundEdge ge && ge.TaxiwayName != walk.Steps[0].Edge.TaxiwayName);
        if (hasOtherTw)
        {
            return TaxiwayArmTerminus.OtherIntersection;
        }

        if (ManualArcDetector.IsShapePointNode(terminal))
        {
            return TaxiwayArmTerminus.ShapePointTerminus;
        }

        return TaxiwayArmTerminus.DeadEnd;
    }
}
