using Avalonia.Threading;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Client mirror of the room's active terminal conflict-alert pairs
    /// (<see cref="ServerConnection.ConflictAlertsChanged"/> and the <c>RoomStateDto.ConflictAlerts</c>
    /// join seed). Server-authoritative and replaced wholesale on every broadcast.
    /// </summary>
    private List<ConflictAlertDto> _conflictAlerts = [];

    private void OnConflictAlertsChanged(ConflictAlertsChangedDto dto) => Dispatcher.UIThread.Post(() => ApplyConflictAlerts(dto.Conflicts));

    /// <summary>
    /// Replace the conflict set from a server broadcast or the join-time room-state seed, then project
    /// it onto <see cref="Models.AircraftModel.ConflictPeerCallsign"/> for every aircraft — the radar
    /// renderer reads that per-aircraft field rather than the pair list, so it needs no room-level state.
    /// <para>
    /// Every aircraft is visited, not just the conflicting ones: a cleared conflict has to blank the
    /// field on aircraft that are no longer in the list.
    /// </para>
    /// </summary>
    public void ApplyConflictAlerts(List<ConflictAlertDto>? conflicts)
    {
        _conflictAlerts = conflicts ?? [];

        var peerByCallsign = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in _conflictAlerts)
        {
            peerByCallsign[c.CallsignA] = c.CallsignB;
            peerByCallsign[c.CallsignB] = c.CallsignA;
        }

        foreach (var ac in Aircraft)
        {
            ac.ConflictPeerCallsign = peerByCallsign.GetValueOrDefault(ac.Callsign);
        }
    }

    /// <summary>
    /// Re-applies the current conflict set to a single aircraft. Called when an aircraft is first added
    /// to <see cref="Aircraft"/>, since a conflict broadcast that arrived before the aircraft existed
    /// would otherwise never reach it.
    /// </summary>
    private void SeedConflictPeer(Models.AircraftModel ac)
    {
        foreach (var c in _conflictAlerts)
        {
            if (string.Equals(c.CallsignA, ac.Callsign, StringComparison.Ordinal))
            {
                ac.ConflictPeerCallsign = c.CallsignB;
                return;
            }

            if (string.Equals(c.CallsignB, ac.Callsign, StringComparison.Ordinal))
            {
                ac.ConflictPeerCallsign = c.CallsignA;
                return;
            }
        }
    }
}
