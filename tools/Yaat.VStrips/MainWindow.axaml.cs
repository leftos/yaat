using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Yaat.Client.Logging;
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

        // Construct the VM here (rather than in App.axaml.cs) so the WindowGeometryHelper
        // shares the same UserPreferences instance the rest of the app mutates. Two
        // separate UserPreferences instances would each carry independent in-memory
        // snapshots and overwrite each other on save (e.g. removing a saved server in
        // the Connect dialog would be silently undone when the window closed and saved
        // its geometry from a stale snapshot).
        var vm = new StandaloneViewModel();
        DataContext = vm;

        _geometryHelper = new WindowGeometryHelper(this, vm.Preferences, "VStripsStandalone", 1000, 700);
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

        var crcItem = this.FindControl<MenuItem>("ConfigureCrcMenuItem");
        if (crcItem is not null)
        {
            crcItem.Click += OnConfigureCrcClick;
        }

        Opened += OnOpened;
    }

    private async void OnConfigureCrcClick(object? sender, RoutedEventArgs e)
    {
        if (!CrcConfigService.IsCrcInstalled())
        {
            await ShowMessageAsync("CRC is not installed on this computer.");
            return;
        }

        if (CrcConfigService.AreYaatEntriesPresent())
        {
            await ShowMessageAsync("CRC already has YAAT server environments configured.");
            return;
        }

        CrcConfigService.Configure();
        await ShowMessageAsync("YAAT server environments added to CRC. Restart CRC to pick up changes.");
    }

    private async Task ShowMessageAsync(string message)
    {
        var box = MessageBoxManager.GetMessageBoxStandard("YAAT Flight Strips", message, ButtonEnum.Ok);
        await box.ShowWindowDialogAsync(this);
    }

    private async void OnOpened(object? sender, System.EventArgs e)
    {
        Opened -= OnOpened;

        if (App.AutoConnectTarget is { } target && DataContext is StandaloneViewModel vm)
        {
            await AutoConnectAsync(vm, target);
        }
    }

    private static async Task AutoConnectAsync(StandaloneViewModel vm, string target)
    {
        var log = AppLog.CreateLogger("AutoConnect");

        string url;
        var match = vm.Preferences.SavedServers.FirstOrDefault(s => s.Name.Equals(target, System.StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            url = match.Url;
        }
        else if (System.Uri.TryCreate(target, System.UriKind.Absolute, out _))
        {
            url = target;
        }
        else
        {
            vm.StatusText = $"--autoconnect: '{target}' is not a saved server name or valid URL";
            return;
        }

        const int maxAttempts = 30;
        const int delayMs = 2000;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var error = await vm.AttemptConnectAsync(url, System.Threading.CancellationToken.None);
            if (error is null)
            {
                log.LogInformation("AutoConnect succeeded on attempt {Attempt}", attempt);
                return;
            }

            if (attempt < maxAttempts)
            {
                vm.StatusText = $"--autoconnect: waiting for server... ({attempt}/{maxAttempts})";
                await Task.Delay(delayMs);
            }
        }

        log.LogWarning("AutoConnect gave up after {Max} attempts", maxAttempts);
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
            vm.Preferences.VatsimCid,
            vm.Preferences.UserInitials,
            vm.Preferences.ArtccId,
            connectAction: vm.AttemptConnectAsync,
            saveAction: (servers, lastUrl) => vm.Preferences.SetSavedServers(servers, lastUrl),
            identitySaveAction: (cid, initials, artcc) =>
            {
                vm.Preferences.SetVatsimCid(cid);
                vm.Preferences.SetUserInitials(initials);
                vm.Preferences.SetArtccId(artcc);
            },
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
