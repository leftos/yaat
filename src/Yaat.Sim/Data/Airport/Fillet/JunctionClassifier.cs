namespace Yaat.Sim.Data.Airport.Fillet;

internal static class JunctionClassifier
{
    public static JunctionPlan Classify(GroundNode node, bool preserveNode, HashSet<int> manualArcNodes)
    {
        var arms = TaxiwayArmBuilder.BuildArms(node, manualArcNodes);
        var (corners, collinear) = CornerPlanner.PlanCorners(node, arms);

        JunctionKind kind;
        if (corners.Count == 0 && collinear.Count == 0)
        {
            kind = JunctionKind.Skip;
        }
        else if (preserveNode || (collinear.Count > 0))
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

        return new JunctionPlan(node.Id, node, kind, preserveNode || (collinear.Count > 0), arms, corners, collinear);
    }
}
