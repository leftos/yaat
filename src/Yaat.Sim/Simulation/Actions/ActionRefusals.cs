using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>The results the router and the Sim hosts return for an action no body on this run can apply.</summary>
public static class ActionRefusals
{
    /// <summary>
    /// No body in the track table answers this verb on this run — the parsed command reached
    /// <see cref="Commands.TrackEngine.Dispatch"/> (or the deferred-command applier) and it returned null. No arm is a
    /// host slot any more, so this is a gap in the table rather than a body the live room owns.
    /// </summary>
    public static CommandResult HostOnly(ParsedCommand command) => HostOnly(CommandDescriber.DescribeCommand(command));

    public static CommandResult HostOnly(string verb) => new(false, $"{verb} is not available here — only the live server dispatches it");

    public static CommandResult NoScenario() => new(false, "No scenario loaded");

    /// <summary>A verb that acts as a position, issued by a connection that has selected none.</summary>
    public static CommandResult NoActivePosition() => new(false, "No active position — use AS to set one");

    public static CommandResult AircraftNotFound(string callsign) => new(false, $"Aircraft '{callsign}' not found");
}
