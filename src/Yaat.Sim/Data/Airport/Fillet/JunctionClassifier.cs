namespace Yaat.Sim.Data.Airport.Fillet;

internal static class JunctionClassifier
{
    public static JunctionPlan Classify(
        GroundNode node,
        bool preserveNode,
        HashSet<int> manualArcNodes,
        FilletCornerEdits edits,
        FilletReportCollector report
    )
    {
        IReadOnlyList<TaxiwayArm> arms = TaxiwayArmBuilder.BuildArms(node, manualArcNodes);
        (IReadOnlyList<CornerSpec> corners, IReadOnlyList<(int, int)> collinear, IReadOnlyList<(int, int)> suppressed) = CornerPlanner.PlanCorners(
            node,
            arms,
            edits,
            report
        );

        bool preserve = preserveNode || (collinear.Count > 0) || (suppressed.Count > 0);
        JunctionKind kind;
        if ((corners.Count == 0) && (collinear.Count == 0) && (suppressed.Count == 0))
        {
            kind = JunctionKind.Skip;
        }
        else if (preserve)
        {
            kind = JunctionKind.Preserve;
        }
        else if (corners.Count <= 1)
        {
            kind = JunctionKind.Simple;
        }
        else
        {
            kind = JunctionKind.MultiCorner;
        }

        return new JunctionPlan(node.Id, node, kind, preserve, arms, corners, collinear, suppressed);
    }
}
