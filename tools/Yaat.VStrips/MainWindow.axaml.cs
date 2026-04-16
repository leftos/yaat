using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.VStrips;

/// <summary>
/// Code-behind for the standalone vStrips app's main window. Owns only the connect
/// dialog hand-off and exit — every other interaction is bound directly to
/// <see cref="MainViewModel"/> commands (Disconnect, Leave Room, etc.) and to
/// <see cref="VStripsViewModel"/> through the center <c>VStripsView</c>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly WindowGeometryHelper _geometryHelper;

    public MainWindow()
    {
        InitializeComponent();

        // Share window geometry with the embedded Yaat.Client preferences store so
        // the standalone app remembers its size/position across launches using the
        // same preferences.json file YAAT writes. The "VStripsStandalone" key is
        // distinct from the embedded pop-out window's "VStripsView" key so each
        // host tracks its own geometry.
        var prefs = new UserPreferences();
        _geometryHelper = new WindowGeometryHelper(this, prefs, "VStripsStandalone", 1000, 700);
        _geometryHelper.Restore();

        var connectItem = this.FindControl<MenuItem>("ConnectMenuItem");
        if (connectItem is not null)
        {
            connectItem.Click += OnConnectClick;
        }

        var exitItem = this.FindControl<MenuItem>("ExitMenuItem");
        if (exitItem is not null)
        {
            exitItem.Click += OnExitClick;
        }
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
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

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
