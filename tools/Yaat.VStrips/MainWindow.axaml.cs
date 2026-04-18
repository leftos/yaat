using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.VStrips;

/// <summary>
/// Code-behind for the standalone vStrips app's main window. Owns the connect
/// dialog and room-picker hand-off — every other interaction is bound to
/// <see cref="StandaloneViewModel"/> commands and to <see cref="VStripsViewModel"/>
/// through the center <c>VStripsView</c>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly WindowGeometryHelper _geometryHelper;

    public MainWindow()
    {
        InitializeComponent();
        var prefs = new UserPreferences();
        _geometryHelper = new WindowGeometryHelper(this, prefs, "VStripsStandalone", 1000, 700);
        _geometryHelper.Restore();

        var connectItem = this.FindControl<MenuItem>("ConnectMenuItem");
        if (connectItem is not null)
        {
            connectItem.Click += OnConnectClick;
        }

        var joinItem = this.FindControl<MenuItem>("JoinRoomMenuItem");
        if (joinItem is not null)
        {
            joinItem.Click += OnJoinRoomClick;
        }

        var exitItem = this.FindControl<MenuItem>("ExitMenuItem");
        if (exitItem is not null)
        {
            exitItem.Click += OnExitClick;
        }
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StandaloneViewModel vm)
        {
            return;
        }

        ConnectWindow? connectWindow = null;
        var connectVm = new ConnectViewModel(
            vm.Preferences.SavedServers,
            vm.Preferences.LastUsedServerUrl,
            connectAction: vm.AttemptConnectAsync,
            saveAction: (servers, lastUrl) => vm.Preferences.SetSavedServers(servers, lastUrl),
            closeAction: () => connectWindow?.Close()
        );
        connectWindow = new ConnectWindow(connectVm, vm.Preferences);
        await connectWindow.ShowDialog(this);
    }

    private async void OnJoinRoomClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StandaloneViewModel vm || !vm.IsConnected)
        {
            return;
        }

        var picker = new RoomPickerWindow(vm);
        await picker.ShowDialog(this);

        if (picker.SelectedRoomId is { } roomId)
        {
            await vm.JoinRoomAsync(roomId);
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
