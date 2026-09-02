using Yaat.Sim.ControllerAi;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>A sink that dispatches nothing: rule tests read what a brain wanted to say, and hand back the outcomes they choose.</summary>
internal sealed class RecordingAiCommandSink : IAiCommandSink
{
    private readonly List<AiCommandOutcome> _outcomes = [];

    public List<AiCommandRequest> Issued { get; } = [];

    public void Issue(AiCommandRequest request) => Issued.Add(request);

    public void Complete(AiCommandRequest request, bool success, string? reason) => _outcomes.Add(new AiCommandOutcome(request, success, reason));

    public IReadOnlyList<AiCommandOutcome> DrainOutcomes()
    {
        var drained = _outcomes.ToList();
        _outcomes.Clear();
        return drained;
    }
}
