namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// One entry of the spine. Two shapes: a <em>sim step</em> is an engine body that may hand its result to the host's
/// consumer view; a <em>host step</em> is a server-owned body the runner invokes through the host's step view. A sim
/// step is given only <see cref="IHostConsumers"/>, so it cannot reach a host slot — the door ADR 0005 closes
/// (a step must not be able to ask which run it is in) stays shut from the sim side as well.
/// </summary>
public readonly struct SpineStep
{
    private readonly Action<SimulationEngine, IHostConsumers>? _sim;
    private readonly Action<IHostSteps>? _host;

    private SpineStep(StepId id, Action<SimulationEngine, IHostConsumers>? sim, Action<IHostSteps>? host)
    {
        Id = id;
        _sim = sim;
        _host = host;
    }

    public StepId Id { get; }

    /// <summary>True for a step whose body the host supplies; false for an engine body.</summary>
    public bool IsHostStep => _host is not null;

    /// <summary>An engine body. Pass a static lambda so the list allocates nothing per tick.</summary>
    public static SpineStep Sim(StepId id, Action<SimulationEngine, IHostConsumers> run) => new(id, run, null);

    /// <summary>A host-supplied body. Pass a static lambda so the list allocates nothing per tick.</summary>
    public static SpineStep Host(StepId id, Action<IHostSteps> run) => new(id, null, run);

    internal void Run(SimulationEngine engine, ISimulationHost host)
    {
        if (_host is not null)
        {
            _host(host);
        }
        else
        {
            _sim!(engine, host);
        }
    }
}
