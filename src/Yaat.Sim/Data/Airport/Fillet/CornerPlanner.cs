using System.Diagnostics;

namespace Yaat.Sim.Data.Airport.Fillet;

internal static class CornerPlanner
{
    public static (IReadOnlyList<CornerSpec> Corners, IReadOnlyList<(int, int)> CollinearPairs) PlanCorners(
        GroundNode junctionNode,
        IReadOnlyList<TaxiwayArm> arms,
        FilletReportCollector report
    )
    {
        var corners = new List<CornerSpec>();
        var collinear = new List<(int, int)>();
        int cornerId = 0;
        for (int i = 0; i < arms.Count; i++)
        {
            for (int j = i + 1; j < arms.Count; j++)
            {
                TaxiwayArm armA = arms[i];
                TaxiwayArm armB = arms[j];
                PairPlan pair = PlanPair(junctionNode, armA, armB, cornerId);
                if (pair.Corner is not null)
                {
                    corners.Add(pair.Corner);
                    cornerId++;
                    report.CornerPlanned(junctionNode, armA, armB, pair.Corner);
                    continue;
                }

                if (pair.Skip is not { } skip)
                {
                    throw new UnreachableException($"PlanPair returned neither a corner nor a skip at J{junctionNode.Id}");
                }

                if (skip == FilletSkipReason.Collinear)
                {
                    collinear.Add((armA.Id, armB.Id));
                }

                report.PairSkipped(junctionNode, armA, armB, skip, pair.RequestedRadiusFt);
            }
        }

        return (corners, collinear);
    }

    /// <summary>The corner the arm pair gets, or why it gets none (with the radius chosen before a radius-floor skip).</summary>
    private readonly record struct PairPlan(CornerSpec? Corner, FilletSkipReason? Skip, double? RequestedRadiusFt);

    private static PairPlan PlanPair(GroundNode junctionNode, TaxiwayArm armA, TaxiwayArm armB, int cornerId)
    {
        GroundEdge edgeA = armA.RootEdge;
        GroundEdge edgeB = armB.RootEdge;
        GroundNode otherA = edgeA.OtherNode(junctionNode);
        GroundNode otherB = edgeB.OtherNode(junctionNode);

        if (otherA.Id == otherB.Id)
        {
            return new PairPlan(null, FilletSkipReason.SameFarNode, null);
        }

        double turnAngle = FilletGeometry.ComputeTurnAngle(armA.BearingFromJunctionDeg, armB.BearingFromJunctionDeg);
        if (turnAngle < FilletConstants.CollinearThresholdDeg)
        {
            return new PairPlan(null, FilletSkipReason.Collinear, null);
        }

        if (turnAngle < FilletConstants.MinFilletAngleDeg)
        {
            return new PairPlan(null, FilletSkipReason.BelowMinAngle, null);
        }

        // Near-hairpin: the arms are within (180 − threshold)° of parallel — two taxiways
        // grazing at an acute angle, not a real turn. The fillet would collapse to an
        // un-taxiable straight chord (a corner no aircraft can fly through), so emit no
        // corner; the pair stays connected via the taxiway that already bridges them.
        if (turnAngle > FilletConstants.NearHairpinThresholdDeg)
        {
            return new PairPlan(null, FilletSkipReason.NearHairpin, null);
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
            return new PairPlan(null, FilletSkipReason.RadiusFloor, typeMax);
        }

        var corner = new CornerSpec(
            CornerId: cornerId,
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
        );
        return new PairPlan(corner, null, null);
    }
}
