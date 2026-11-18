using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Client mirror of the room's active conflict-alert pairs, STARS and ERAM, each with its kind, suppressed and
    /// acknowledged state, intruder and owner facilities (<see cref="ServerConnection.ConflictAlertsChanged"/> and the
    /// <c>RoomStateDto.ConflictAlerts</c> join seed). Server-authoritative and replaced wholesale on every broadcast.
    /// </summary>
    private List<ConflictAlertDto> _conflictAlerts = [];

    /// <summary>A snapshot of the room's conflict-alert pairs, STARS and ERAM, in broadcast order.</summary>
    public IReadOnlyList<ConflictAlertDto> ConflictAlerts => [.. _conflictAlerts];

    /// <summary>
    /// The ERAM pairs of <see cref="ConflictAlerts"/>, replaced with it: the Scope reads them each frame and applies its
    /// viewer's facility gate (<c>ScopeEramConflictGate</c>).
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<ConflictAlertDto> _eramConflictAlerts = [];

    /// <summary>Raised on the UI thread after <see cref="ApplyConflictAlerts"/> replaces the pairs.</summary>
    public event Action? ConflictAlertsApplied;

    private void OnConflictAlertsChanged(ConflictAlertsChangedDto dto) => Dispatcher.UIThread.Post(() => ApplyConflictAlerts(dto.Conflicts));

    /// <summary>
    /// Replace the conflict set from a server broadcast or the join-time room-state seed, then project
    /// its STARS pairs onto <see cref="Models.AircraftModel.ConflictPeerCallsign"/> for every aircraft — the radar
    /// renderer reads that per-aircraft field rather than the pair list, so it needs no room-level state. ERAM pairs never
    /// reach that field: the Scope reads them from <see cref="EramConflictAlerts"/>.
    /// <para>
    /// Every aircraft is visited, not just the conflicting ones: a cleared conflict has to blank the
    /// field on aircraft that are no longer in the list.
    /// </para>
    /// </summary>
    public void ApplyConflictAlerts(List<ConflictAlertDto>? conflicts)
    {
        List<ConflictAlertDto> pairs = conflicts ?? [];
        var pairByCallsign = new Dictionary<string, ConflictAlertDto>(StringComparer.Ordinal);
        foreach (ConflictAlertDto c in pairs.Where(IsStarsPair))
        {
            pairByCallsign[c.CallsignA] = c;
            pairByCallsign[c.CallsignB] = c;
        }

        foreach (AircraftModel ac in Aircraft)
        {
            ProjectStarsPair(ac, pairByCallsign.GetValueOrDefault(ac.Callsign));
        }

        ReplaceConflictAlerts(pairs);
    }

    /// <summary>
    /// Writes <paramref name="pair"/> onto <paramref name="ac"/>, one of its members: the other member as the peer, with the pair's
    /// suppressed and acknowledged state; a null pair clears all three.
    /// </summary>
    private static void ProjectStarsPair(AircraftModel ac, ConflictAlertDto? pair)
    {
        ac.ConflictPeerCallsign =
            (pair is null) ? null : (string.Equals(pair.CallsignA, ac.Callsign, StringComparison.Ordinal) ? pair.CallsignB : pair.CallsignA);
        ac.ConflictSuppressed = pair?.Suppressed ?? false;
        ac.ConflictAcknowledged = pair?.Acknowledged ?? false;
    }

    /// <summary>
    /// The one writer of <see cref="_conflictAlerts"/>: replaces the pairs and raises <see cref="ConflictAlertsApplied"/>, so
    /// every listener (the Scope's LA/CA/MCI list) follows a broadcast and a scenario clear alike.
    /// </summary>
    private void ReplaceConflictAlerts(List<ConflictAlertDto> pairs)
    {
        _conflictAlerts = pairs;
        EramConflictAlerts = [.. pairs.Where(c => c.Kind == ConflictAlertKind.Eram)];
        ConflictAlertsApplied?.Invoke();
    }

    private static bool IsStarsPair(ConflictAlertDto c) => c.Kind == ConflictAlertKind.Stars;

    /// <summary>
    /// Re-applies the current conflict set to a single aircraft. Called when an aircraft is first added
    /// to <see cref="Aircraft"/>, since a conflict broadcast that arrived before the aircraft existed
    /// would otherwise never reach it.
    /// </summary>
    private void SeedConflictPeer(Models.AircraftModel ac)
    {
        ConflictAlertDto? pair = _conflictAlerts
            .Where(IsStarsPair)
            .FirstOrDefault(c =>
                string.Equals(c.CallsignA, ac.Callsign, StringComparison.Ordinal) || string.Equals(c.CallsignB, ac.Callsign, StringComparison.Ordinal)
            );
        if (pair is not null)
        {
            ProjectStarsPair(ac, pair);
        }
    }
}
