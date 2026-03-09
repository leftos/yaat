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
        Action closeAction
    )
    {
        Servers = new ObservableCollection<SavedServer>(servers);
        _connectAction = connectAction;
        _saveAction = saveAction;
        _closeAction = closeAction;
        SelectedServer = Servers.FirstOrDefault(s => s.Url == lastUsedUrl) ?? Servers.FirstOrDefault();

        // Subscribe to property changes on existing servers
        foreach (var server in Servers)
        {
            server.PropertyChanged += OnServerPropertyChanged;
        }
    }

    private void OnServerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SavedServer.Name) or nameof(SavedServer.Url))
        {
            SaveServers();
        }
    }

    [RelayCommand]
    private void AddServer()
    {
        var entry = new SavedServer("New Server", "http://localhost:5000");
        entry.PropertyChanged += OnServerPropertyChanged;
        Servers.Add(entry);
        SelectedServer = entry;
        SaveServers();
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveServer()
    {
        if (SelectedServer is null)
        {
            return;
        }

        int idx = Servers.IndexOf(SelectedServer);
        SelectedServer.PropertyChanged -= OnServerPropertyChanged;
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.ElementAtOrDefault(Math.Max(0, idx - 1));
        SaveServers();
    }

    private bool CanRemove() => SelectedServer is not null;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedServer is null)
        {
            return;
        }

        int idx = Servers.IndexOf(SelectedServer);
        if (idx <= 0)
        {
            return;
        }

        var items = Servers.ToList();
        items.RemoveAt(idx);
        items.Insert(idx - 1, SelectedServer);
        Servers.Clear();
        foreach (var item in items)
        {
            Servers.Add(item);
        }

        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        SaveServers();
    }

    private bool CanMoveUp() => SelectedServer is not null && Servers.IndexOf(SelectedServer) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedServer is null)
        {
            return;
        }

        int idx = Servers.IndexOf(SelectedServer);
        if (idx < 0 || idx >= Servers.Count - 1)
        {
            return;
        }

        var items = Servers.ToList();
        items.RemoveAt(idx);
        items.Insert(idx + 1, SelectedServer);
        Servers.Clear();
        foreach (var item in items)
        {
            Servers.Add(item);
        }

        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        SaveServers();
    }

    private bool CanMoveDown() => SelectedServer is not null && Servers.IndexOf(SelectedServer) < Servers.Count - 1;

    private void SaveServers()
    {
        _saveAction(Servers, SelectedServer?.Url ?? Servers.FirstOrDefault()?.Url ?? "http://localhost:5000");
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedServer is null)
        {
            return;
        }

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
