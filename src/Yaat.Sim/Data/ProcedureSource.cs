namespace Yaat.Sim.Data;

/// <summary>
/// Which non-current-cycle source supplied a procedure's coded legs.
/// </summary>
public enum ProcedureSourceKind
{
    /// <summary>A cached prior AIRAC cycle within the recency cap.</summary>
    PriorCycle,

    /// <summary>An ARTCC-supplied CIFP fragment committed under <c>Data/ARTCCs/{ARTCC}/Procedures</c>.</summary>
    ArtccCustom,
}

/// <summary>
/// Where a procedure's coded legs came from when they did not come from the current FAA CIFP cycle.
/// <see cref="Label"/> is the AIRAC cycle id (e.g. <c>"2604"</c>) for <see cref="ProcedureSourceKind.PriorCycle"/>
/// and the ARTCC id (e.g. <c>"ZOA"</c>) for <see cref="ProcedureSourceKind.ArtccCustom"/>. A null
/// <c>ProcedureSource</c> means the current cycle supplied the procedure — the normal case, no advisory.
/// </summary>
public sealed record ProcedureSource(ProcedureSourceKind Kind, string Label);
