namespace Yaat.Sim.Data.Airport.Fillet.V2;

/// <summary>
/// A redirected fillet endpoint: either a surviving planning cut (resolved to a new tangent-cut
/// node by the executor) or a pre-existing stable anchor node (looked up directly in the layout).
/// Used as the value type for the redirect map and for post-redirect arm endpoints on op records.
/// </summary>
internal abstract record FilletEndpoint
{
    private FilletEndpoint() { }

    /// <summary>A surviving cut endpoint, resolved via the cut-node map at execution time.</summary>
    internal sealed record Cut(CutId Id) : FilletEndpoint;

    /// <summary>A pre-existing stable anchor node, resolved via <see cref="AirportGroundLayout.Nodes"/> at execution time.</summary>
    internal sealed record Node(int NodeId) : FilletEndpoint;
}
