using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// The phase context a test hands <see cref="Pilot.PilotProactive.TickAirspaceBoundaryRespect"/> when it has no engine to
/// build one: the dispatcher's minimal context with no active runways.
/// </summary>
internal static class MinimalPhaseContext
{
    public static PhaseContext For(AircraftState aircraft) => CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
}
