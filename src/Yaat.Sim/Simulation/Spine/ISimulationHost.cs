namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// The host: the collaborator a run supplies to the spine. It provides each host step's body
/// (<see cref="IHostSteps"/>), consumes each sim step's result (<see cref="IHostConsumers"/>) and answers the action
/// router's slots, queries and consumers (<see cref="Actions.IActionHost"/>) for the recorded actions its steps apply.
/// Four exist — the bare test host and the replay host in this assembly, the live-room host and the reconstruction
/// host in yaat-server. What kind of run this is lives on <see cref="SimulationEngine.RunProfile"/>, deliberately not
/// here: a host answers "what happens at this step", never "which run is this".
/// </summary>
public interface ISimulationHost : IHostSteps, IHostConsumers, Actions.IActionHost;
