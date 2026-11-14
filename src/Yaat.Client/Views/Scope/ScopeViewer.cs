using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The position the Scope is viewed from, with its ERAM sector: the instructor's active position (the scenario's student
/// position by default, the one a persistent <c>AS</c> picked after that).
/// </summary>
/// <param name="Acting">The acting position, as <c>MenuSession.Acting</c> resolves it; null before the active TCP is known.</param>
/// <param name="EramFacilityId">The facility the position's tracks are owned under (its track facility, else its listing facility).</param>
/// <param name="EramSectorId">The position's ERAM sector id (<c>"44"</c>, no <c>C</c>); null for a position without one.</param>
public sealed record ScopeViewer(ActingPosition? Acting, string? EramFacilityId, string? EramSectorId)
{
    /// <summary>No viewer: no active position is known.</summary>
    public static ScopeViewer None { get; } = new(null, null, null);
}
