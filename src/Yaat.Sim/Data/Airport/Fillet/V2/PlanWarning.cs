namespace Yaat.Sim.Data.Airport.Fillet.V2;

public sealed record PlanWarning(int? JunctionNodeId, int? CornerId, string Code, string Message)
{
    public const string DegenerateRadius = "DEGENERATE_RADIUS";
    public const string SingleCutRejected = "SINGLE_CUT_REJECTED";
    public const string CornerDemoted = "CORNER_DEMOTED";
    public const string SharedArmScaled = "SHARED_ARM_SCALED";
    public const string CoincidentCutMerged = "COINCIDENT_CUT_MERGED";
    public const string NoOwningCut = "NO_OWNING_CUT";
    public const string SubThresholdCutSkipped = "SUB_THRESHOLD_CUT_SKIPPED";
    public const string UnconsumedIncidentEdge = "UNCONSUMED_INCIDENT_EDGE";
    public const string UnconsumedReconnectSafetyNet = "UNCONSUMED_RECONNECT_SAFETY_NET";
}
