using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Client mirror of the room's STARS coordination (RD) lists, unfiltered by position
    /// (<see cref="ServerConnection.CoordinationListsChanged"/> and the <c>RoomStateDto.CoordinationLists</c> join seed).
    /// Server-authoritative and replaced whole on every push; a scenario load, restart, rewind and recording load reach
    /// it through the server's resend, and leaving the scenario empties it. The Radar view's merged RUNDOWN list reads it.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<CoordinationListHubDto> _coordinationLists = [];

    internal void OnCoordinationListsChanged(CoordinationListsChangedDto dto) => Dispatcher.UIThread.Post(() => ApplyCoordinationLists(dto.Lists));

    /// <summary>
    /// Replaces the coordination lists from a server push or the join-time room-state seed; null reads as none. A status
    /// this client does not know draws as steady list text, and each one is logged once per push, never per row build.
    /// </summary>
    public void ApplyCoordinationLists(IReadOnlyList<CoordinationListHubDto>? lists)
    {
        CoordinationLists = [.. lists ?? []];

        IEnumerable<string> statuses = CoordinationLists.SelectMany(list => list.Items).Select(item => item.Status).Distinct(StringComparer.Ordinal);
        foreach (string status in statuses)
        {
            if (!RundownRows.IsKnownStatus(status))
            {
                _log.LogWarning("Unknown coordination status {Status}; its row draws as steady list text", status);
            }
        }
    }
}
