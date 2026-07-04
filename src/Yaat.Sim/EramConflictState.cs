namespace Yaat.Sim;

/// <summary>
/// ERAM Short-Term Conflict Alert (STCA) set — the en-route conflict detector's output, kept separate from
/// the terminal STARS <see cref="ConflictAlertState"/> because ERAM uses a different model (4-minute
/// trajectory probe, 5 nm / 3 nm-≤FL230 lateral, data-block-altitude vertical envelope, no approach-corridor
/// suppression). Keyed by <see cref="EramConflictDetector.MakeConflictId"/>.
/// </summary>
public sealed class EramConflictState
{
    public Dictionary<string, EramActiveConflict> Conflicts { get; } = [];
}

/// <summary>
/// One active ERAM STCA pair. <see cref="OwnerFacilityA"/>/<see cref="OwnerFacilityB"/> record the ERAM
/// facility currently owning each target (refreshed each detection tick) so the broadcast can apply the
/// §377 facility gate — "alerts are only generated if one of the two targets is owned by a controller in
/// your ERAM facility" — per subscriber without re-resolving ownership. Null = not ERAM-owned.
/// </summary>
public sealed class EramActiveConflict
{
    public required string Id { get; init; }
    public required string CallsignA { get; init; }
    public required string CallsignB { get; init; }
    public string? OwnerFacilityA { get; set; }
    public string? OwnerFacilityB { get; set; }
}
