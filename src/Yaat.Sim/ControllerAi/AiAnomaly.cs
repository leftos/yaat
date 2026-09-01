namespace Yaat.Sim.ControllerAi;

/// <summary>What the AI noticed going wrong. Only kinds some rule produces today are listed; add a kind with its producer.</summary>
public enum AiAnomalyKind
{
    /// <summary>The dispatcher rejected a command the AI issued.</summary>
    CommandRejected,

    /// <summary>An aircraft in a movement phase made no net progress for the watchdog window.</summary>
    StuckAircraft,

    /// <summary>A pilot request addressed to this position stayed open past the follow-up horizon.</summary>
    UnansweredPilotRequest,

    /// <summary>A handoff to or from this position stayed pending past the auto-accept delay plus a grace period.</summary>
    HandoffUnaccepted,

    /// <summary>A conflict alert fired on an aircraft in this position's jurisdiction.</summary>
    ConflictAlertInAiJurisdiction,
}

public enum AiAnomalyEventKind
{
    Opened,
    Closed,

    /// <summary>A point event (a rejected command): opened and closed in the same instant.</summary>
    Instant,
}

/// <summary>One anomaly transition, in the order it happened. <see cref="DurationSeconds"/> is set on Closed events.</summary>
public sealed record AiAnomalyEvent(
    AiAnomalyKind Kind,
    string PositionId,
    string SubjectKey,
    AiAnomalyEventKind Event,
    double AtSeconds,
    double? DurationSeconds,
    string Detail
);

/// <summary>
/// The scenario's anomaly ledger: an anomaly is an episode keyed by (kind, position, subject) that opens once, stays
/// open while the condition holds, and closes when it clears; <see cref="Drain"/> hands the host the transitions since
/// the last drain, in order. Never snapshotted — cleared on load and restore, and re-derived by the rules.
/// </summary>
public sealed class AiAnomalyLog
{
    private readonly Dictionary<(AiAnomalyKind Kind, string PositionId, string Subject), double> _open = [];
    private readonly List<AiAnomalyEvent> _pending = [];

    public int OpenCount => _open.Count;

    public bool IsOpen(AiAnomalyKind kind, string positionId, string subjectKey) => _open.ContainsKey((kind, positionId, subjectKey));

    /// <summary>The subjects currently open for a kind and position, in ordinal order (so callers close the vanished ones deterministically).</summary>
    public IReadOnlyList<string> OpenSubjects(AiAnomalyKind kind, string positionId) =>
        _open.Keys.Where(k => k.Kind == kind && k.PositionId == positionId).Select(k => k.Subject).OrderBy(s => s, StringComparer.Ordinal).ToList();

    /// <summary>Opens the episode unless it is already open.</summary>
    public void Open(AiAnomalyKind kind, string positionId, string subjectKey, double nowSeconds, string detail)
    {
        var key = (kind, positionId, subjectKey);
        if (_open.ContainsKey(key))
        {
            return;
        }

        _open[key] = nowSeconds;
        _pending.Add(new AiAnomalyEvent(kind, positionId, subjectKey, AiAnomalyEventKind.Opened, nowSeconds, null, detail));
    }

    /// <summary>Closes the episode when it is open; a no-op otherwise.</summary>
    public void Close(AiAnomalyKind kind, string positionId, string subjectKey, double nowSeconds)
    {
        var key = (kind, positionId, subjectKey);
        if (!_open.Remove(key, out var openedAt))
        {
            return;
        }

        _pending.Add(new AiAnomalyEvent(kind, positionId, subjectKey, AiAnomalyEventKind.Closed, nowSeconds, nowSeconds - openedAt, ""));
    }

    /// <summary>Records a point event that has no duration.</summary>
    public void Record(AiAnomalyKind kind, string positionId, string subjectKey, double nowSeconds, string detail) =>
        _pending.Add(new AiAnomalyEvent(kind, positionId, subjectKey, AiAnomalyEventKind.Instant, nowSeconds, 0, detail));

    /// <summary>The transitions since the previous drain, oldest first.</summary>
    public IReadOnlyList<AiAnomalyEvent> Drain()
    {
        var drained = _pending.ToList();
        _pending.Clear();
        return drained;
    }

    public void Clear()
    {
        _open.Clear();
        _pending.Clear();
    }
}
