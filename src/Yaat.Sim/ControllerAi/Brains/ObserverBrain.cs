using Yaat.Sim.ControllerAi.Rules;

namespace Yaat.Sim.ControllerAi.Brains;

/// <summary>
/// A brain that issues no commands: it runs the watchdog rules over its jurisdiction and opens anomalies. Observer
/// mode finds sim bugs (stuck taxiers, requests nobody answers, stale handoffs, conflict alerts) with the AI in the
/// loop but silent, which is the CA0 acceptance gate and the soak harness's baseline.
/// </summary>
public sealed class ObserverBrain(AiPositionConfig position) : IPositionBrain
{
    private readonly IDecisionRule[] _rules =
    [
        new StuckAircraftRule(),
        new UnansweredPilotRequestRule(),
        new HandoffUnacceptedRule(),
        new ConflictAlertInAiJurisdictionRule(),
    ];

    private readonly Dictionary<string, AiAircraftMemo> _memos = new(StringComparer.Ordinal);

    public AiPositionConfig Position => position;

    public void Tick(AiTickContext context)
    {
        var present = new HashSet<string>(context.Snapshot.Select(ac => ac.Callsign), StringComparer.Ordinal);
        foreach (var callsign in _memos.Keys.Where(c => !present.Contains(c)).ToList())
        {
            _memos.Remove(callsign);
        }

        var scope = new AiRuleScope
        {
            Tick = context,
            Position = Position,
            Jurisdiction = context.View.Jurisdiction(Position),
            Memos = _memos,
        };
        foreach (var rule in _rules)
        {
            rule.Evaluate(scope);
        }
    }

    public void Reset() => _memos.Clear();
}
