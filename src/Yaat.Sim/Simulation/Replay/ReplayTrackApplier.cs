using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation.Replay;

/// <summary>
/// Replay-time dispatcher for track and SetActivePosition commands. Maintains the
/// per-connection active-position map (mirroring the server's
/// TrainingRoom.ActivePositionByConnection) and routes parsed track commands to the
/// shared <see cref="TrackEngine.Dispatch"/> after resolving identity from the AS-prefix
/// override or the per-connection map.
///
/// State mutations only — no SignalR/CRC broadcasts (the server's live path owns those).
/// </summary>
internal sealed class ReplayTrackApplier
{
    private static readonly ILogger Log = SimLog.CreateLogger("ReplayTrackApplier");

    private readonly Dictionary<string, TrackOwner> _activeOwnerByConnection = new(StringComparer.Ordinal);

    public void Reset()
    {
        _activeOwnerByConnection.Clear();
    }

    /// <summary>
    /// Applies one track / AS command. Returns the dispatch result so a live caller (the AI command sink) can report
    /// it; replay ignores it. Null when there was nothing to dispatch (no scenario, a bare AS that only set the
    /// connection's active position, or an aircraft that has not spawned yet).
    /// </summary>
    public CommandResult? Apply(string rawCommand, AircraftState? aircraft, string connectionId, SimScenarioState? scenario)
    {
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
            var owner = TrackResolver.ResolveTcpToOwner(scenario, setPos.TcpCode, scenario.ArtccConfig);
            if (owner is null)
            {
                Log.LogDebug("Replay: AS '{Tcp}' did not resolve to a position", setPos.TcpCode);
                return new CommandResult(false, $"AS '{setPos.TcpCode}' did not resolve to a position");
            }

            _activeOwnerByConnection[connectionId] = owner;
            return null;
        }

        if (aircraft is null)
        {
            return null;
        }

        var identity = ResolveEffectiveIdentity(asOverrideTcp, connectionId, scenario);
        return TrackEngine.Dispatch(parsed, aircraft, identity, scenario, scenario.ArtccConfig);
    }

    /// <summary>
    /// The AS override when given; else, for an AI-controller connection, the position its connection id names
    /// (resolved from the ARTCC config, so it needs no student facility and no AS prefix); else the position the
    /// connection selected earlier; else the student.
    ///
    /// The AI branch is checked before the selected-position map on purpose. An AI position works the position its
    /// connection id names and cannot select another, while this map is shared with the replay caller and keyed only
    /// by connection id — so a recorded active-position selection carrying an AI connection id would otherwise
    /// displace the live AI's identity.
    /// </summary>
    private TrackOwner? ResolveEffectiveIdentity(string? asOverrideTcp, string connectionId, SimScenarioState scenario)
    {
        if (asOverrideTcp is not null)
        {
            return TrackResolver.ResolveTcpToOwner(scenario, asOverrideTcp, scenario.ArtccConfig);
        }

        if (AiConnectionId.TryParse(connectionId, out var positionId) && scenario.ArtccConfig?.ResolvePosition(positionId) is { } aiPosition)
        {
            return aiPosition;
        }

        if (_activeOwnerByConnection.TryGetValue(connectionId, out var active))
        {
            return active;
        }

        return scenario.StudentPosition;
    }
}
