using System.Diagnostics;

namespace Yaat.Sim.Data.Airport.Fillet;

internal static class CornerPlanner
{
    /// <summary>The smallest turn, degrees, a forced pair may make: below it there is no corner to fillet.</summary>
    internal const double MinForcedTurnDeg = 1.0;

    /// <summary>
    /// Plans every arm pair of the junction, applying <paramref name="edits"/>: a suppressed pair is returned with the
    /// suppressed pairs (so the junction node and every arm's stub stay), an override's radius replaces the edge types'
    /// maximum, and a forced pair skips the collinear, minimum-angle, near-hairpin and radius-floor checks unless it turns
    /// less than <see cref="MinForcedTurnDeg"/>.
    /// </summary>
    public static (
        IReadOnlyList<CornerSpec> Corners,
        IReadOnlyList<(int, int)> CollinearPairs,
        IReadOnlyList<(int, int)> SuppressedPairs
    ) PlanCorners(GroundNode junctionNode, IReadOnlyList<TaxiwayArm> arms, FilletCornerEdits edits, FilletReportCollector report)
    {
        var corners = new List<CornerSpec>();
        var collinear = new List<(int, int)>();
        var suppressed = new List<(int, int)>();
        for (int i = 0; i < arms.Count; i++)
        {
            for (int j = i + 1; j < arms.Count; j++)
            {
                TaxiwayArm armA = arms[i];
                TaxiwayArm armB = arms[j];
                PairPlan pair = PlanEditedPair(junctionNode, armA, armB, corners.Count, edits);
                if (pair.Corner is not null)
                {
                    corners.Add(pair.Corner);
                    report.CornerPlanned(junctionNode, armA, armB, pair.Corner);
                    continue;
                }

                FilletSkipReason skip =
                    pair.Skip ?? throw new UnreachableException($"PlanPair returned neither a corner nor a skip at J{junctionNode.Id}");
                if (skip == FilletSkipReason.Collinear)
                {
                    collinear.Add((armA.Id, armB.Id));
                }
                else if (skip == FilletSkipReason.Suppressed)
                {
                    suppressed.Add((armA.Id, armB.Id));
                }

                report.PairSkipped(junctionNode, armA, armB, skip, pair.RequestedRadiusFt);
            }
        }

        return (corners, collinear, suppressed);
    }

    /// <summary>The corner the arm pair gets, or why it gets none (with the radius chosen before a radius-floor skip).</summary>
    private readonly record struct PairPlan(CornerSpec? Corner, FilletSkipReason? Skip, double? RequestedRadiusFt);

    private static PairPlan PlanEditedPair(GroundNode junctionNode, TaxiwayArm armA, TaxiwayArm armB, int cornerId, FilletCornerEdits edits)
    {
        CornerFilletOverride? edit = edits.At(junctionNode.Id, armA.Id, armB.Id);
        return (edit is { Suppress: true })
            ? new PairPlan(null, FilletSkipReason.Suppressed, null)
            : PlanPair(junctionNode, armA, armB, cornerId, edit);
    }

    private static PairPlan PlanPair(GroundNode junctionNode, TaxiwayArm armA, TaxiwayArm armB, int cornerId, CornerFilletOverride? edit)
    {
        bool force = edit is { Force: true };
        double turnAngle = FilletGeometry.ComputeTurnAngle(armA.BearingFromJunctionDeg, armB.BearingFromJunctionDeg);
        if (EarlySkip(junctionNode, armA, armB, force, turnAngle) is { } early)
        {
            return new PairPlan(null, early, null);
        }

        double requestedRadiusFt = edit?.RadiusFt ?? FilletGeometry.SelectMaxRadius(armA.RootEdge, armB.RootEdge, turnAngle);
        double idealTangentFt = IdealTangentFt(armA, armB, turnAngle, requestedRadiusFt);
        bool belowFloor = (idealTangentFt / Math.Tan((turnAngle / 2.0) * (Math.PI / 180.0))) < FilletConstants.RadiusFloorFt;
        if ((!force) && belowFloor)
        {
            return new PairPlan(null, FilletSkipReason.RadiusFloor, requestedRadiusFt);
        }

        bool plannedOnlyByForce = (AngleSkip(turnAngle) is not null) || belowFloor || (!FilletEligibility.IsEligible(junctionNode));
        var corner = new CornerSpec(
            CornerId: cornerId,
            JunctionNodeId: junctionNode.Id,
            ArmIdA: armA.Id,
            ArmIdB: armB.Id,
            EdgeA: armA.RootEdge,
            EdgeB: armB.RootEdge,
            TurnAngleDeg: turnAngle,
            RequestedRadiusFt: requestedRadiusFt,
            IdealTangentFt: idealTangentFt,
            BearingAToJunctionDeg: armA.BearingFromJunctionDeg,
            BearingBToJunctionDeg: armB.BearingFromJunctionDeg,
            Force: ForceOf(force, plannedOnlyByForce)
        );
        return new PairPlan(corner, null, null);
    }

    /// <summary>
    /// The skip the pair gets before any radius is chosen: two arms reaching one node (never forceable), a failed angle
    /// check (unless forced), or, for a forced pair, a turn below <see cref="MinForcedTurnDeg"/>.
    /// </summary>
    private static FilletSkipReason? EarlySkip(GroundNode junctionNode, TaxiwayArm armA, TaxiwayArm armB, bool force, double turnAngle)
    {
        if (armA.RootEdge.OtherNode(junctionNode).Id == armB.RootEdge.OtherNode(junctionNode).Id)
        {
            return FilletSkipReason.SameFarNode;
        }

        if (!force)
        {
            return AngleSkip(turnAngle);
        }

        return (turnAngle < MinForcedTurnDeg) ? FilletSkipReason.NoTurn : null;
    }

    /// <summary>How an override forces the corner: not at all, as a corner the planner plans anyway, or only by the force.</summary>
    private static CornerForce ForceOf(bool force, bool plannedOnlyByForce)
    {
        if (!force)
        {
            return CornerForce.None;
        }

        return plannedOnlyByForce ? CornerForce.Added : CornerForce.Kept;
    }

    /// <summary>The angle check a turn of <paramref name="turnAngle"/> degrees fails, or null when it is a corner to fillet.</summary>
    private static FilletSkipReason? AngleSkip(double turnAngle)
    {
        if (turnAngle < FilletConstants.CollinearThresholdDeg)
        {
            return FilletSkipReason.Collinear;
        }

        if (turnAngle < FilletConstants.MinFilletAngleDeg)
        {
            return FilletSkipReason.BelowMinAngle;
        }

        // Near-hairpin: the arms are within (180 − threshold)° of parallel — two taxiways
        // grazing at an acute angle, not a real turn. The fillet would collapse to an
        // un-taxiable straight chord (a corner no aircraft can fly through), so emit no
        // corner; the pair stays connected via the taxiway that already bridges them.
        return (turnAngle > FilletConstants.NearHairpinThresholdDeg) ? FilletSkipReason.NearHairpin : null;
    }

    /// <summary>The tangent distance a fillet of <paramref name="radiusFt"/> needs at the turn, capped by both arms.</summary>
    private static double IdealTangentFt(TaxiwayArm armA, TaxiwayArm armB, double turnAngle, double radiusFt)
    {
        bool capA = FilletEligibility.IsEligible(armA.Walk.TerminalNode) && (armA.Walk.TerminalNode.SourceIntersectionPosition is null);
        bool capB = FilletEligibility.IsEligible(armB.Walk.TerminalNode) && (armB.Walk.TerminalNode.SourceIntersectionPosition is null);
        return FilletGeometry.ComputeIdealTangentFt(
            turnAngle,
            radiusFt,
            armA.LengthFt,
            armB.LengthFt,
            capA,
            capB,
            armA.IntersectionCapFt,
            armB.IntersectionCapFt
        );
    }
}
