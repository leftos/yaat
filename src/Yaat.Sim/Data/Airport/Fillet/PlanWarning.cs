namespace Yaat.Sim.Data.Airport.Fillet;

public sealed record PlanWarning(int? JunctionNodeId, int? CornerId, string Code, string Message)
{
    public const string DegenerateRadius = "DEGENERATE_RADIUS";
    public const string SingleCutRejected = "SINGLE_CUT_REJECTED";
    public const string CornerDemoted = "CORNER_DEMOTED";
    public const string SharedArmScaled = "SHARED_ARM_SCALED";
    public const string CoincidentCutMerged = "COINCIDENT_CUT_MERGED";
    public const string NoOwningCut = "NO_OWNING_CUT";

    /// <summary>A forced through pair is filleted, so its junction loses the preserved node it has without the force.</summary>
    public const string ForcedCornerUnpreserves = "FORCED_CORNER_UNPRESERVES";
    public const string SubThresholdCutSkipped = "SUB_THRESHOLD_CUT_SKIPPED";
    public const string UnconsumedIncidentEdge = "UNCONSUMED_INCIDENT_EDGE";
    public const string UnconsumedReconnectSafetyNet = "UNCONSUMED_RECONNECT_SAFETY_NET";
}
