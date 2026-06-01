using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Online-controller list shown in the Controllers window: the controllers present in the
/// current room (live CRC clients plus the scenario's auto-connect ATC positions). Refreshed
/// on demand and whenever the CRC membership or loaded scenario changes.
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<OnlineControllerDto> OnlineControllers { get; } = [];

    [RelayCommand]
    private async Task RefreshOnlineControllersAsync()
    {
        if (!IsConnected || ActiveRoomId is null)
        {
            Dispatcher.UIThread.Post(OnlineControllers.Clear);
            return;
        }

        try
        {
            var controllers = await _connection.GetOnlineControllersAsync();
            Dispatcher.UIThread.Post(() =>
            {
                OnlineControllers.Clear();
                foreach (var c in controllers)
                {
                    OnlineControllers.Add(c);
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to refresh online controllers");
        }
    }
}
