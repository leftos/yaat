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

    /// <summary>
    /// The active alert between <paramref name="callsignA"/> and <paramref name="callsignB"/>, in either order, or null
    /// when there is none.
    /// </summary>
    public EramActiveConflict? FindPair(string callsignA, string callsignB) =>
        Conflicts.GetValueOrDefault(EramConflictDetector.MakeConflictId(callsignA, callsignB));

    // Alert ids removed with an aircraft, keyed by its callsign, until the host's delete broadcast takes them.
    private readonly Dictionary<string, List<string>> _removedWithAircraft = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Removes every alert involving <paramref name="callsign"/>, an aircraft that has just left the world, and holds
    /// their ids for <see cref="TakeRemovedWith"/>. Runs on every run kind, so live, replay and playback drop the same
    /// alerts in the same second.
    /// </summary>
    public void RemoveInvolving(string callsign)
    {
        var ids = Conflicts
            .Values.Where(c =>
                c.CallsignA.Equals(callsign, StringComparison.OrdinalIgnoreCase) || c.CallsignB.Equals(callsign, StringComparison.OrdinalIgnoreCase)
            )
            .Select(c => c.Id)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        foreach (string id in ids)
        {
            Conflicts.Remove(id);
        }

        if (_removedWithAircraft.TryGetValue(callsign, out List<string>? pending))
        {
            pending.AddRange(ids);
        }
        else
        {
            _removedWithAircraft[callsign] = ids;
        }
    }

    /// <summary>
    /// The ids <see cref="RemoveInvolving"/> removed with <paramref name="callsign"/> since the last call, handed to the
    /// host once so it can delete them from its displays; empty when there are none.
    /// </summary>
    public List<string> TakeRemovedWith(string callsign) => _removedWithAircraft.Remove(callsign, out List<string>? ids) ? ids : [];

    /// <summary>
    /// Drops every id <see cref="RemoveInvolving"/> is holding, when the engine state is replaced (a snapshot restore, a
    /// scenario unload), so an id from the discarded state never reaches a later broadcast.
    /// </summary>
    public void ClearRemovedWithAircraft() => _removedWithAircraft.Clear();
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

    /// <summary>
    /// Set and cleared by the ERAM Conflict Suppress entry (<c>CO</c>, 7110.65 §5-13-1c.1): a suppressed alert is withheld
    /// from CRC's conflict list and data-block conflict status. It belongs to this alert only: when the pair stops being
    /// detected the alert is removed, so a later conflict between the same pair alerts again.
    /// </summary>
    public bool Suppressed { get; set; }

    /// <summary>
    /// The <see cref="Suppressed"/> value the last <see cref="Simulation.SimulationEngine.TickEramConflictAlerts"/> diff
    /// reported. When the two differ, the next pass reports the alert suppressed or restored and brings this up to date,
    /// so the host publishes a <c>CO</c> on every run kind, live or played back.
    /// </summary>
    public bool PublishedSuppressed { get; set; }
}
