using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation.Transport;

/// <summary>
/// The session-clock verbs over the engine — <c>PAUSE</c>, <c>UNPAUSE</c>, <c>SIMRATE</c> — mapped onto
/// <see cref="SimulationEngine.Pause"/> / <see cref="SimulationEngine.Resume"/> /
/// <see cref="SimulationEngine.SetSimRate"/>, the same bodies the server's own pause and resume call, so both paths
/// mark the clock dirty alike and the host broadcasts once. The message is the terminal response.
///
/// <para>
/// What the bodies touched reaches the host as <see cref="Spine.IStateChangeConsumer.OnSimStateChanged"/>.
/// </para>
/// </summary>
public static class TransportCommandHandler
{
    public static CommandResult Handle(SimulationEngine engine, ParsedCommand command) =>
        command switch
        {
            PauseCommand => engine.Pause(),
            UnpauseCommand => engine.Resume(),
            SimRateCommand simRate => engine.SetSimRate(simRate.Rate),
            _ => new CommandResult(false, $"{CommandDescriber.DescribeCommand(command)} is not a transport command"),
        };
}
