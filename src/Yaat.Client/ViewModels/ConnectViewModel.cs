using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

public partial class ConnectViewModel : ObservableObject
{
    private readonly Func<string, CancellationToken, Task<bool>> _connectAction;
    private readonly Action<IList<SavedServer>, string> _saveAction;
    private readonly Action _closeAction;
    private CancellationTokenSource? _currentCts;

    public ObservableCollection<SavedServer> Servers { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private SavedServer? _selectedServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    private string? _errorMessage;

    public ConnectViewModel(
        IReadOnlyList<SavedServer> servers,
        string lastUsedUrl,
        Func<string, CancellationToken, Task<bool>> connectAction,
        Action<IList<SavedServer>, string> saveAction,
        Action closeAction)
    {
        Servers = new ObservableCollection<SavedServer>(servers);
        _connectAction = connectAction;
        _saveAction = saveAction;
        _closeAction = closeAction;
        SelectedServer = Servers.FirstOrDefault(s => s.Url == lastUsedUrl) ?? Servers.FirstOrDefault();
    }

    [RelayCommand]
    private void AddServer()
    {
        var entry = new SavedServer("New Server", "http://localhost:5000");
        Servers.Add(entry);
        SelectedServer = entry;
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveServer()
    {
        if (SelectedServer is null)
            return;
        int idx = Servers.IndexOf(SelectedServer);
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.ElementAtOrDefault(Math.Max(0, idx - 1));
    }

    private bool CanRemove() => SelectedServer is not null;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedServer is null)
            return;
        int idx = Servers.IndexOf(SelectedServer);
        if (idx <= 0)
            return;
        Servers.Move(idx, idx - 1);
    }

    private bool CanMoveUp() => SelectedServer is not null && Servers.IndexOf(SelectedServer) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedServer is null)
            return;
        int idx = Servers.IndexOf(SelectedServer);
        if (idx < 0 || idx >= Servers.Count - 1)
            return;
        Servers.Move(idx, idx + 1);
    }

    private bool CanMoveDown() =>
        SelectedServer is not null && Servers.IndexOf(SelectedServer) < Servers.Count - 1;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedServer is null)
            return;

        using var cts = new CancellationTokenSource();
        _currentCts = cts;
        IsConnecting = true;
        ErrorMessage = null;

        bool success = await _connectAction(SelectedServer.Url, cts.Token);
        IsConnecting = false;
        _currentCts = null;

        if (success)
        {
            _saveAction(Servers, SelectedServer.Url);
            _closeAction();
        }
    }

    private bool CanConnect() => SelectedServer is not null && !IsConnecting;

    [RelayCommand]
    private void CancelConnect()
    {
        _currentCts?.Cancel();
    }
}
