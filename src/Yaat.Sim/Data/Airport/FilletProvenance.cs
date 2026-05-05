namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Discriminated record describing how a fillet-pipeline element was created.
/// Attached as a non-serialized property on <see cref="GroundNode"/>,
/// <see cref="GroundEdge"/>, and <see cref="GroundArc"/> so post-pass cleanups
/// can pattern-match on the type instead of parsing <see cref="GroundNode.Origin"/>
/// (and friends) strings. The <see cref="DisplayString"/> property is used to
/// populate the human-readable Origin field at construction time.
/// </summary>
public abstract record FilletProvenance
{
    /// <summary>Human-readable form for log output and the diagnostic Origin field.</summary>
    public abstract string DisplayString { get; }
}

/// <summary>Tangent node created by <see cref="FilletArcGenerator"/> at an intersection.</summary>
public sealed record TangentNodeProvenance(int IntersectionId, string Taxiway, int DestinationNodeId) : FilletProvenance
{
    public override string DisplayString => $"Fillet:tangent-node@{IntersectionId} on-{Taxiway}(→{DestinationNodeId})";
}

/// <summary>Corner arc (Phase C) connecting two tangent nodes at an intersection.</summary>
public sealed record CornerArcProvenance(int IntersectionId, string TaxiwayA, string TaxiwayB) : FilletProvenance
{
    public override string DisplayString => $"Fillet:phase-c-arc@{IntersectionId} {TaxiwayA}/{TaxiwayB}";

    /// <summary>
    /// Order-independent taxiway-pair key (alphabetic-sorted) so edge-A/edge-B order
    /// doesn't matter when grouping arcs by physical corner.
    /// </summary>
    public string NormalizedTaxiwayKey =>
        string.CompareOrdinal(TaxiwayA, TaxiwayB) <= 0 ? $"{TaxiwayA}/{TaxiwayB}" : $"{TaxiwayB}/{TaxiwayA}";
}

/// <summary>The kind of edge produced by a fillet-pipeline phase.</summary>
public enum FilletEdgeKind
{
    /// <summary>From the original far node to the farthest tangent (standard walk).</summary>
    Shorten,

    /// <summary>Direct shorten added by AddDirectShortensFromArcAnchors to bypass tangent chains.</summary>
    ShortenDirect,

    /// <summary>From a passthrough (junction) node to a tangent — preserves chain connectivity.</summary>
    Passthrough,

    /// <summary>Between consecutive tangent nodes on the same edge.</summary>
    TangentLink,

    /// <summary>Sub-edge created when splitting a manual-arc chain edge at a tangent.</summary>
    ArcSplit,

    /// <summary>Merged collinear pair into a single edge (Phase D2).</summary>
    Merge,

    /// <summary>Reconnects an orphaned non-fillet edge to a tangent (Phase D3).</summary>
    Reconnect,

    /// <summary>Stub from a preserved intersection node to a tangent (preserve mode).</summary>
    Preserve,

    /// <summary>RescueOrphanedTangentNodes: invented edge to fix a connectivity gap.</summary>
    RescueOrphan,
}

/// <summary>Edge or arc-side connection produced by a fillet pipeline phase.</summary>
public sealed record FilletEdgeProvenance(
    int IntersectionId,
    FilletEdgeKind Kind,
    string Taxiway,
    int? FromNodeId = null,
    int? ToNodeId = null
) : FilletProvenance
{
    public override string DisplayString
    {
        get
        {
            string label = Kind switch
            {
                FilletEdgeKind.Shorten => "phase-d-shorten",
                FilletEdgeKind.ShortenDirect => "phase-d-shorten-direct",
                FilletEdgeKind.Passthrough => "phase-d-passthrough",
                FilletEdgeKind.TangentLink => "phase-d-tangent-link",
                FilletEdgeKind.ArcSplit => "phase-d-arc-split",
                FilletEdgeKind.Merge => "phase-d-merge",
                FilletEdgeKind.Reconnect => "phase-d-reconnect",
                FilletEdgeKind.Preserve => "phase-d-preserve",
                FilletEdgeKind.RescueOrphan => "rescue-orphan",
                _ => Kind.ToString(),
            };

            string suffix = (FromNodeId is { } from && ToNodeId is { } to) ? $" #{from}↔#{to}" : "";

            // RescueOrphan has no intersection context — it operates globally.
            return Kind == FilletEdgeKind.RescueOrphan
                ? $"Fillet:{label}{suffix}"
                : $"Fillet:{label}@{IntersectionId} {Taxiway}{suffix}";
        }
    }
}
