using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.VStrips;

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

        var newWindowItem = this.FindControl<MenuItem>("NewWindowMenuItem");
        if (newWindowItem is not null)
        {
            newWindowItem.Click += OnNewWindowClick;
        }
    }

    /// <summary>
    /// Opens a popup of accessible facilities and spawns a dedicated
    /// <see cref="VStripsViewWindow"/> for the chosen one. Each additional
    /// window owns its own <see cref="VStripsViewModel"/> scoped to that
    /// facility and persists its geometry under a facility-keyed entry.
    /// </summary>
    private void OnNewWindowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || DataContext is not StandaloneViewModel vm || vm.VStrips.AccessibleFacilities.Count == 0)
        {
            return;
        }

        var menu = new MenuFlyout();
        foreach (var facility in vm.VStrips.AccessibleFacilities)
        {
            var header = facility.IsStudentFacility ? $"{facility.FacilityName} (own)" : facility.FacilityName;
            var item = new MenuItem { Header = header, Tag = facility };
            item.Click += async (_, _) =>
            {
                if (item.Tag is not AccessibleFacilityDto f)
                {
                    return;
                }
                var addlVm = await vm.OpenAdditionalFacilityAsync(f.FacilityId);
                var window = new VStripsViewWindow(vm.Preferences, f.FacilityId, f.FacilityName) { DataContext = addlVm };
                window.Show();
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
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
