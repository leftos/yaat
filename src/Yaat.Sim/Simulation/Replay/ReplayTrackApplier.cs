using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation.Replay;

/// <summary>
/// Applies one track or <c>AS</c> command against an engine: a bare <c>AS {tcp}</c> selects the connection's position
/// through <see cref="SimulationEngine.SelectPosition"/>; anything else resolves its identity through
/// <see cref="SimulationEngine.ResolveIdentity"/> (the AS-prefix override, the AI connection's own position, the
/// selection, the student — in that order) and routes the parsed command to <see cref="TrackEngine.Dispatch"/>.
/// Two callers share it: <see cref="SimulationEngine.ReplayCommand"/> (replay) and
/// <see cref="SimulationEngine.DispatchAiCommand"/> (live). State mutations only — no broadcasts.
/// </summary>
internal static class ReplayTrackApplier
{
    private static readonly ILogger Log = SimLog.CreateLogger("ReplayTrackApplier");

    /// <summary>
    /// Returns the dispatch result so a live caller (the AI command sink) can report it; replay ignores it. Null
    /// when there was nothing to dispatch (no scenario, a bare AS that only selected the connection's position, or
    /// an aircraft that has not spawned yet).
    /// </summary>
    public static CommandResult? Apply(SimulationEngine engine, string rawCommand, AircraftState? aircraft, string connectionId)
    {
        var scenario = engine.Scenario;
        if (scenario is null)
        {
            return null;
        }

        var (remainder, asOverrideTcp) = TrackResolver.ExtractAsPrefix(rawCommand);

        var parseResult = CommandParser.Parse(remainder);
        if (!parseResult.IsSuccess || parseResult.Value is null)
        {
            Log.LogDebug("Replay: failed to parse track command remainder '{Remainder}' (raw='{Raw}')", remainder, rawCommand);
            return new CommandResult(false, $"Failed to parse track command: {rawCommand}");
        }

        var parsed = parseResult.Value;

        if (parsed is SetActivePositionCommand setPos)
        {
            var selection = engine.SelectPosition(connectionId, setPos.TcpCode);
            if (!selection.Success)
            {
                Log.LogDebug("Replay: AS '{Tcp}' did not resolve to a position", setPos.TcpCode);
                return selection;
            }

            return null;
        }

        if (aircraft is null)
        {
            return null;
        }

        var identity = engine.ResolveIdentity(connectionId, asOverrideTcp);
        return TrackEngine.Dispatch(parsed, aircraft, identity, scenario);
    }
}
