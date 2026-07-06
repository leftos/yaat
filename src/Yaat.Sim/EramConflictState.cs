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

    /// <summary>
    /// When one side of the pair is an untracked <b>and uncorrelated</b> Mode-C target (no owner and no filed
    /// flight plan), this is that side's callsign — the Mode-C "intruder" in a controlled-vs-uncontrolled
    /// conflict (docs/crc/eram.md §Conflict Data Blocks). The tracked side's FDB flashes
    /// <c>ControlledUncontrolled</c> and the intruder renders a callsign-less Conflict Data Block. Null when
    /// both sides are tracked, or when the unowned side still has a flight plan (a correlated target flashes
    /// an ordinary data block, not a CDB — 7110.65 §2-1-6). Refreshed each detection tick.
    /// </summary>
    public string? IntruderCallsign { get; set; }
}
