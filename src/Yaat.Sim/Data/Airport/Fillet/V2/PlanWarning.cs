namespace Yaat.Sim.Data.Airport.Fillet.V2;

public sealed record PlanWarning(int? JunctionNodeId, int? CornerId, string Code, string Message)
{
    public const string DegenerateRadius = "DEGENERATE_RADIUS";
    public const string SingleCutRejected = "SINGLE_CUT_REJECTED";
    public const string CornerDemoted = "CORNER_DEMOTED";
    public const string SharedArmScaled = "SHARED_ARM_SCALED";
    public const string NoOwningCut = "NO_OWNING_CUT";
}
