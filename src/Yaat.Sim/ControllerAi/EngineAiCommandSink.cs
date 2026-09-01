using Yaat.Sim.Simulation;

namespace Yaat.Sim.ControllerAi;

/// <summary>
/// The pure-engine command sink: dispatches each request synchronously through
/// <see cref="SimulationEngine.DispatchAiCommand"/> (the same pipeline a human command takes, under
/// <c>DispatchOrigin.ControllerAi</c>) and keeps the outcome for the next AI tick. Tests and the non-room soak path
/// use it; a live room uses the server's queueing sink instead.
/// </summary>
public sealed class EngineAiCommandSink(SimulationEngine engine) : IAiCommandSink
{
    private readonly List<AiCommandOutcome> _outcomes = [];

    public void Issue(AiCommandRequest request)
    {
        var result = engine.DispatchAiCommand(request.From, request.Callsign, request.Canonical);
        _outcomes.Add(new AiCommandOutcome(request, result.Success, result.Success ? null : result.Message));
    }

    public IReadOnlyList<AiCommandOutcome> DrainOutcomes()
    {
        var drained = _outcomes.ToList();
        _outcomes.Clear();
        return drained;
    }
}
