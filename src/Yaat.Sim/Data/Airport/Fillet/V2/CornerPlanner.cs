namespace Yaat.Sim.Data.Airport.Fillet.V2;

internal static class CornerPlanner
{
    public static (IReadOnlyList<CornerSpec> Corners, IReadOnlyList<(int, int)> CollinearPairs) PlanCorners(
        GroundNode junctionNode,
        IReadOnlyList<TaxiwayArm> arms
    )
    {
        var corners = new List<CornerSpec>();
        var collinear = new List<(int, int)>();
        int cornerId = 0;
        for (int i = 0; i < arms.Count; i++)
        {
            for (int j = i + 1; j < arms.Count; j++)
            {
                var armA = arms[i];
                var armB = arms[j];
                var edgeA = armA.RootEdge;
                var edgeB = armB.RootEdge;
                var otherA = edgeA.OtherNode(junctionNode);
                var otherB = edgeB.OtherNode(junctionNode);

                if (otherA.Id == otherB.Id)
                {
                    continue;
                }

                double turnAngle = FilletGeometry.ComputeTurnAngle(armA.BearingFromJunctionDeg, armB.BearingFromJunctionDeg);
                if (turnAngle < FilletConstants.CollinearThresholdDeg)
                {
                    collinear.Add((armA.Id, armB.Id));
                    continue;
                }

                if (turnAngle < FilletConstants.MinFilletAngleDeg)
                {
                    continue;
                }

                double typeMax = FilletGeometry.SelectMaxRadius(edgeA, edgeB, turnAngle);
                bool capA = FilletEligibility.IsEligible(armA.Walk.TerminalNode) && (armA.Walk.TerminalNode.SourceIntersectionPosition is null);
                bool capB = FilletEligibility.IsEligible(armB.Walk.TerminalNode) && (armB.Walk.TerminalNode.SourceIntersectionPosition is null);

                double idealTangentFt = FilletGeometry.ComputeIdealTangentFt(
                    turnAngle,
                    typeMax,
                    armA.LengthFt,
                    armB.LengthFt,
                    capA,
                    capB,
                    armA.IntersectionCapFt,
                    armB.IntersectionCapFt
                );

                double radiusFromIdeal = idealTangentFt / Math.Tan((turnAngle / 2.0) * (Math.PI / 180.0));
                if (radiusFromIdeal < FilletConstants.RadiusFloorFt)
                {
                    continue;
                }

                corners.Add(
                    new CornerSpec(
                        CornerId: cornerId++,
                        JunctionNodeId: junctionNode.Id,
                        ArmIdA: armA.Id,
                        ArmIdB: armB.Id,
                        EdgeA: edgeA,
                        EdgeB: edgeB,
                        TurnAngleDeg: turnAngle,
                        RequestedRadiusFt: typeMax,
                        IdealTangentFt: idealTangentFt,
                        BearingAToJunctionDeg: armA.BearingFromJunctionDeg,
                        BearingBToJunctionDeg: armB.BearingFromJunctionDeg
                    )
                );
            }
        }

        return (corners, collinear);
    }
}
