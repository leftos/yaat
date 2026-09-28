namespace Yaat.Sim;

public sealed class ConflictAlertState
{
    public Dictionary<string, ActiveConflict> Conflicts { get; } = [];

    /// <summary>
    /// The active alert between <paramref name="callsignA"/> and <paramref name="callsignB"/>, in either order, or null
    /// when there is none.
    /// </summary>
    public ActiveConflict? FindPair(string callsignA, string callsignB) =>
        Conflicts.GetValueOrDefault(ConflictAlertDetector.MakeConflictId(callsignA, callsignB));

    /// <summary>
    /// <c>CASUP</c> suppressions that outlive their alert, keyed by conflict id. A suppressed alert the detector clears (a
    /// single diverging pass, say) is latched here; if the same pair alerts again while the latch holds, the new alert
    /// re-uses the suppression instead of sounding fresh. The latch ends when the pair leaves the 3.3 nm / 1,100 ft
    /// hysteresis box or a track stops being eligible (<see cref="ConflictAlertDetector.IsSuppressionLatchHeld"/>).
    /// </summary>
    public Dictionary<string, LatchedConflictSuppression> LatchedSuppressions { get; } = [];
}

/// <summary>The pair a latched <c>CASUP</c> suppression belongs to.</summary>
public sealed record LatchedConflictSuppression(string CallsignA, string CallsignB);

public sealed class ActiveConflict
{
    public required string Id { get; init; }
    public required string CallsignA { get; init; }
    public required string CallsignB { get; init; }
    public bool IsAcknowledged { get; set; }

    /// <summary>
    /// Set and cleared by <c>CASUP</c>: a suppressed alert is withheld from CRC's STARS conflict list. It belongs to this
    /// alert only: when the pair stops being detected the alert is removed, so a later conflict between the same pair
    /// alerts again. ERAM STCA is not affected.
    /// </summary>
    public bool Suppressed { get; set; }

    /// <summary>
    /// The <see cref="Suppressed"/> value the last <see cref="Simulation.SimulationEngine.TickConflictAlerts"/> diff
    /// reported. When the two differ, the next pass reports the alert suppressed or restored and brings this up to date,
    /// so the host publishes a <c>CASUP</c> on every run kind, live or played back.
    /// </summary>
    public bool PublishedSuppressed { get; set; }
}
