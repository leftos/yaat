namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// The host: the collaborator a run supplies to the spine. It provides each host step's body
/// (<see cref="IHostSteps"/>) and consumes each sim step's result (<see cref="IHostConsumers"/>). Four exist — the
/// bare test host and the replay host in this assembly, the live-room host and the reconstruction host in
/// yaat-server. What kind of run this is lives on <see cref="SimulationEngine.RunProfile"/>, deliberately not here:
/// a host answers "what happens at this step", never "which run is this".
/// </summary>
public interface ISimulationHost : IHostSteps, IHostConsumers;
