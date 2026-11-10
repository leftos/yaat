using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>Commands issued as the solo-training student.</summary>
public static class StudentCommands
{
    /// <summary>A command from a hub connection the host judged to be the student.</summary>
    public static CommandResult IssueAsStudent(this SimulationEngine engine, string callsign, string command) =>
        engine
            .Actions.Issue(
                new ActionInput(callsign, command, SimulationEngine.LocalConnectionId, Initials: "", Baked: null) { IssuedByStudent = true }
            )
            .Result;
}
