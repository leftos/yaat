namespace Yaat.Sim.Simulation.Tdls;

/// <summary>
/// The clearance a PDC carries: the nine canonical <c>TDLSS</c> payload fields. This is the simulation's clearance
/// model — the one a <see cref="TdlsItemRecord"/> holds and the snapshot round-trips; the server projects it onto
/// the CRC wire <c>ClearanceDto</c> on its way out. A null field is one the controller left empty.
/// <para>
/// Named properties rather than constructor parameters, like the wire DTO: the canonical command's field order is
/// not this declaration order (its field 7 is <see cref="DepFreq"/> and its field 8 <see cref="LocalInfo"/>), so a
/// positional construction would be a standing invitation to swap the two.
/// </para>
/// </summary>
public sealed record TdlsClearance
{
    public string? Expect { get; init; }
    public string? Sid { get; init; }
    public string? Transition { get; init; }
    public string? Climbout { get; init; }
    public string? Climbvia { get; init; }
    public string? InitialAlt { get; init; }
    public string? ContactInfo { get; init; }
    public string? LocalInfo { get; init; }
    public string? DepFreq { get; init; }
}
