using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

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

    public void Apply(string rawCommand, AircraftState? aircraft, string connectionId, SimScenarioState? scenario)
    {
        if (scenario is null)
        {
            return;
        }

        var (remainder, asOverrideTcp) = TrackResolver.ExtractAsPrefix(rawCommand);

        var parseResult = CommandParser.Parse(remainder);
        if (!parseResult.IsSuccess || parseResult.Value is null)
        {
            Log.LogDebug("Replay: failed to parse track command remainder '{Remainder}' (raw='{Raw}')", remainder, rawCommand);
            return;
        }

        var parsed = parseResult.Value;

        if (parsed is SetActivePositionCommand setPos)
        {
            var owner = TrackResolver.ResolveTcpToOwner(scenario, setPos.TcpCode, scenario.ArtccConfig);
            if (owner is null)
            {
                Log.LogDebug("Replay: AS '{Tcp}' did not resolve to a position", setPos.TcpCode);
                return;
            }

            _activeOwnerByConnection[connectionId] = owner;
            return;
        }

        if (aircraft is null)
        {
            return;
        }

        var identity = ResolveEffectiveIdentity(asOverrideTcp, connectionId, scenario);
        TrackEngine.Dispatch(parsed, aircraft, identity, scenario, scenario.ArtccConfig);
    }

    private TrackOwner? ResolveEffectiveIdentity(string? asOverrideTcp, string connectionId, SimScenarioState scenario)
    {
        if (asOverrideTcp is not null)
        {
            return TrackResolver.ResolveTcpToOwner(scenario, asOverrideTcp, scenario.ArtccConfig);
        }

        if (_activeOwnerByConnection.TryGetValue(connectionId, out var active))
        {
            return active;
        }

        return scenario.StudentPosition;
    }
}
