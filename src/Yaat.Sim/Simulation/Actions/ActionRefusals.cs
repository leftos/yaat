using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>The results the router and the Sim hosts return for an action no body on this run can apply.</summary>
public static class ActionRefusals
{
    /// <summary>The verb's body is the live server's; a bare or replay run has nothing to apply it to.</summary>
    public static CommandResult HostOnly(ParsedCommand command) => HostOnly(CommandDescriber.DescribeCommand(command));

    public static CommandResult HostOnly(string verb) => new(false, $"{verb} is not available here — only the live server dispatches it");

    public static CommandResult NoScenario() => new(false, "No scenario loaded");

    /// <summary>A verb that acts as a position, issued by a connection that has selected none.</summary>
    public static CommandResult NoActivePosition() => new(false, "No active position — use AS to set one");

    public static CommandResult AircraftNotFound(string callsign) => new(false, $"Aircraft '{callsign}' not found");
}
