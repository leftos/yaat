using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Client mirror of the room's ERAM CRR groups, ordered by label (<see cref="ServerConnection.EramCrrGroupsChanged"/> and
    /// the <c>RoomStateDto.EramCrrGroups</c> join seed). Server-authoritative and replaced whole on every push; a restart, a
    /// rewind and a recording load reach it through the server's resend, and leaving the scenario empties it. Each member's
    /// label is on its <see cref="Models.AircraftModel.EramCrrGroup"/>.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<EramCrrGroupHubDto> _crrGroups = [];

    internal void OnEramCrrGroupsChanged(EramCrrGroupsChangedDto dto) => Dispatcher.UIThread.Post(() => ApplyCrrGroups(dto.Groups));

    /// <summary>Replaces the CRR groups from a server push or the join-time room-state seed; null reads as none.</summary>
    public void ApplyCrrGroups(List<EramCrrGroupHubDto>? groups) => CrrGroups = [.. groups ?? []];
}
