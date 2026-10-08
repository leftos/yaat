using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scenario menu's Active Runways window over the room's current list: one row per airport (the primary airport first,
/// then the room's list in ordinal order) holding the same text the load prompt and the <c>ARWY</c> command take. Rows
/// follow every <see cref="MainViewModel.RoomActiveRunways"/> update until the user types in them, and Apply sends one
/// <c>ARWY</c> per changed row and leaves the window open. It closes when the scenario it was opened on goes away: an
/// unload, leaving the room, another scenario load and a recording load all end it, as they close the load prompt.
/// </summary>
public partial class ActiveRunwaysWindowViewModel : ObservableObject
{
    private static readonly ILogger Log = AppLog.CreateLogger("ActiveRunwaysWindow");

    private readonly MainViewModel? _owner;
    private readonly string? _scenarioId;

    /// <summary>The rows the user has typed in: they keep their text while the room's list changes under them.</summary>
    private readonly HashSet<ActiveRunwaysRow> _typedIn = [];

    private bool _refreshing;
    private bool _closed;

    private ActiveRunwaysWindowViewModel() { }

    /// <summary>The Avalonia loader's instance: a window with no room behind it, which shows no rows and sends nothing.</summary>
    public static ActiveRunwaysWindowViewModel CreateEmpty() => new();

    public ActiveRunwaysWindowViewModel(MainViewModel owner)
    {
        _owner = owner;
        _scenarioId = owner.ActiveScenarioId;
        _owner.PropertyChanged += OnOwnerPropertyChanged;
        RefreshRows();
    }

    /// <summary>The airports being edited, in the order the window shows them.</summary>
    public ObservableCollection<ActiveRunwaysRow> Rows { get; } = [];

    /// <summary>False once the window is done: the scenario it was opened on is gone.</summary>
    [ObservableProperty]
    private bool _isOpen = true;

    /// <summary>
    /// Sends one <c>ARWY</c> per row whose text differs from the room's list, through <paramref name="send"/>, once every
    /// row reads with the parser the server's <c>ARWY</c> uses: a row that does not read shows its message and nothing is
    /// sent. An accepted row stays in the window and counts as untouched again, so it follows the room's list once more; a
    /// row the server refuses keeps its text with the server's message under it, and the other rows still go. Once the
    /// window closes mid-send, nothing more is sent.
    /// </summary>
    public async Task ApplyAsync(NavigationDatabase? navDb, Func<string, Task<CommandResultDto>> send)
    {
        if (_owner is null)
        {
            return;
        }

        List<(ActiveRunwaysRow Row, string? Command)>? answers = ActiveRunwaysEditor.ReadRows(Rows, navDb);
        if (answers is null)
        {
            return;
        }

        foreach ((ActiveRunwaysRow row, string? command) in answers)
        {
            if ((command is null) || string.Equals(row.Text, TextOf(row.Airport), StringComparison.Ordinal))
            {
                continue;
            }

            CommandResultDto result = await SendAnswerAsync(send, command);
            if (!IsOpen)
            {
                return;
            }

            if (result.Success)
            {
                _typedIn.Remove(row);
            }
            else
            {
                row.Error = result.Message ?? $"ARWY {row.Airport} was refused";
            }
        }
    }

    /// <summary>Ends the window: no more sends, no further following of the room's list, and the rows are gone.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _owner?.PropertyChanged -= OnOwnerPropertyChanged;

        foreach (ActiveRunwaysRow row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        Rows.Clear();
        _typedIn.Clear();
        IsOpen = false;
    }

    private static async Task<CommandResultDto> SendAnswerAsync(Func<string, Task<CommandResultDto>> send, string command)
    {
        try
        {
            return await send(command);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Active runways answer '{Command}' failed", command);
            return new CommandResultDto(false, ex.Message);
        }
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed || (_owner is null))
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.ActiveScenarioId))
        {
            // Every way a scenario ends or is replaced moves the id: unload and leaving the room null it, another load and a
            // recording load set the new one. A restart re-activates the same scenario, so the window stays.
            if (!string.Equals(_scenarioId, _owner.ActiveScenarioId, StringComparison.Ordinal))
            {
                Close();
            }

            return;
        }

        if ((e.PropertyName == nameof(MainViewModel.RoomActiveRunways)) || (e.PropertyName == nameof(MainViewModel.ActiveScenarioPrimaryAirportId)))
        {
            RefreshRows();
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((!_refreshing) && (e.PropertyName == nameof(ActiveRunwaysRow.Text)) && (sender is ActiveRunwaysRow row))
        {
            _typedIn.Add(row);
        }
    }

    /// <summary>Brings the rows in line with the room's list: a row the user typed in keeps its text, every other row takes
    /// the list's text, an airport the list has newly named gains a row and one it no longer names loses its own.</summary>
    private void RefreshRows()
    {
        List<string> airports = TargetAirports();
        _refreshing = true;
        try
        {
            for (int index = Rows.Count - 1; index >= 0; index--)
            {
                ActiveRunwaysRow row = Rows[index];
                if (airports.Contains(row.Airport, StringComparer.Ordinal))
                {
                    continue;
                }

                row.PropertyChanged -= OnRowPropertyChanged;
                _typedIn.Remove(row);
                Rows.RemoveAt(index);
            }

            foreach (string airport in airports)
            {
                ActiveRunwaysRow? row = Rows.FirstOrDefault(candidate => string.Equals(candidate.Airport, airport, StringComparison.Ordinal));
                if (row is null)
                {
                    row = new ActiveRunwaysRow(airport, TextOf(airport));
                    row.PropertyChanged += OnRowPropertyChanged;
                    Rows.Add(row);
                }
                else if (!_typedIn.Contains(row))
                {
                    row.Text = TextOf(airport);
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>The primary airport first, whether or not the room's list names it, then the rest in ordinal order.</summary>
    private List<string> TargetAirports()
    {
        if (_owner is null)
        {
            return [];
        }

        List<string> airports = [];
        string? primary = _owner.ActiveScenarioPrimaryAirportId;
        if (!string.IsNullOrWhiteSpace(primary))
        {
            airports.Add(NavigationDatabase.NormalizeAirport(primary));
        }

        airports.AddRange(
            _owner.RoomActiveRunways.Keys.Where(airport => !airports.Contains(airport, StringComparer.Ordinal)).Order(StringComparer.Ordinal)
        );
        return airports;
    }

    private string TextOf(string airport) =>
        _owner is null ? string.Empty : ActiveRunwaysEditor.ToText(_owner.RoomActiveRunways.GetValueOrDefault(airport) ?? []);
}
