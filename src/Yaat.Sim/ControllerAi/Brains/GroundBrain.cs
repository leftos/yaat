using Yaat.Sim.ControllerAi.Rules;

namespace Yaat.Sim.ControllerAi.Brains;

/// <summary>
/// The AI Ground position: answers ready-to-taxi and taxi-in requests, clears (or asks Local for) runway crossings
/// along the way, and hands departures to the tower before the hold-short — all as canonical commands through the
/// sink, paced like a real frequency. The watchdog rules run first, unpaced, so a stuck or unanswered aircraft is
/// reported whatever the decision rules are doing. Per-aircraft memos remember what was issued and what is awaited;
/// last tick's outcomes settle them before any rule runs.
/// </summary>
public sealed class GroundBrain : IPositionBrain
{
    private readonly IDecisionRule[] _watchdogs =
    [
        new StuckAircraftRule(),
        new UnansweredPilotRequestRule(),
        new ConflictAlertInAiJurisdictionRule(),
    ];
    private readonly HandToLocalRule _handToLocal = new();
    private readonly IDecisionRule[] _decisions;
    private readonly Dictionary<string, AiAircraftMemo> _memos = new(StringComparer.Ordinal);
    private readonly AiPacing _pacing = new();

    public GroundBrain(AiPositionConfig position)
    {
        Position = position;
        _decisions = [new AnswerTaxiOutRule(), new RunwayCrossingRule(), new AnswerTaxiInRule(), _handToLocal];
    }

    public AiPositionConfig Position { get; }

    /// <summary>The per-aircraft memos, for tests and the decision log.</summary>
    public IReadOnlyDictionary<string, AiAircraftMemo> Memos => _memos;

    public void Tick(AiTickContext context)
    {
        _pacing.BeginTick();
        var present = new HashSet<string>(context.Snapshot.Select(ac => ac.Callsign), StringComparer.Ordinal);
        foreach (var callsign in _memos.Keys.Where(c => !present.Contains(c)).ToList())
        {
            _memos.Remove(callsign);
        }

        SettleOutcomes(context);

        var scope = new AiRuleScope
        {
            Tick = context,
            Position = Position,
            Jurisdiction = context.View.Jurisdiction(Position),
            Memos = _memos,
            Pacing = _pacing,
        };
        foreach (var rule in _watchdogs)
        {
            rule.Evaluate(scope);
        }

        foreach (var rule in _decisions)
        {
            rule.Evaluate(scope);
        }
    }

    public void Reset()
    {
        _memos.Clear();
        _pacing.Reset();
        _handToLocal.Reset();
    }

    /// <summary>
    /// Matches the host's outcomes to the in-flight memos; an in-flight command whose outcome never arrived by its
    /// effect deadline counts as rejected, so the brain never waits on it forever.
    /// </summary>
    private void SettleOutcomes(AiTickContext context)
    {
        foreach (var outcome in context.Outcomes)
        {
            if (!string.Equals(outcome.Request.From.PositionId, Position.PositionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (_memos.TryGetValue(outcome.Request.Callsign, out var memo) && memo.InFlight is { } inFlight && Matches(inFlight, outcome.Request))
            {
                memo.Complete(outcome.Success, context.ElapsedSeconds);
            }
        }

        foreach (var (callsign, memo) in _memos.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (memo.InFlight is { } stale && (context.ElapsedSeconds > memo.EffectDeadlineSeconds))
            {
                context.Anomalies.Record(
                    AiAnomalyKind.CommandRejected,
                    Position.PositionId,
                    callsign,
                    context.ElapsedSeconds,
                    $"{stale.Canonical}: no outcome by the effect deadline ({stale.Intent.Rule})"
                );
                memo.Complete(success: false, context.ElapsedSeconds);
            }
        }
    }

    private static bool Matches(AiCommandRequest inFlight, AiCommandRequest reported) =>
        ReferenceEquals(inFlight, reported)
        || (
            string.Equals(inFlight.Callsign, reported.Callsign, StringComparison.Ordinal)
            && string.Equals(inFlight.Canonical, reported.Canonical, StringComparison.Ordinal)
        );
}
