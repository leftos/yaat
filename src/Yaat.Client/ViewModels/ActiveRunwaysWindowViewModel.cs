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
/// <c>ARWY</c> per changed row (a second Apply waits for the first) and leaves the window open. It closes when the
/// scenario it was opened on goes away — an unload, leaving the room, another scenario load and a recording load all end
/// it, as they close the load prompt, which <see cref="MainViewModel.ActiveRunwaysScope"/> says — never on a restart.
/// </summary>
public partial class ActiveRunwaysWindowViewModel : ObservableObject
{
    private static readonly ILogger Log = AppLog.CreateLogger("ActiveRunwaysWindow");

    private readonly MainViewModel? _owner;
    private readonly int _scope;

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
        _scope = owner.ActiveRunwaysScope;
        _owner.PropertyChanged += OnOwnerPropertyChanged;
        RefreshRows();
    }

    /// <summary>The airports being edited, in the order the window shows them.</summary>
    public ObservableCollection<ActiveRunwaysRow> Rows { get; } = [];

    /// <summary>False once the window is done: the scenario it was opened on is gone.</summary>
    [ObservableProperty]
    private bool _isOpen = true;

    /// <summary>True from the start of an Apply to its end: a second Apply is refused and the button is disabled on it.</summary>
    [ObservableProperty]
    private bool _isApplying;

    /// <summary>
    /// Sends one <c>ARWY</c> per row whose text differs from the room's list, through <paramref name="send"/>, once every
    /// such row reads with the parser the server's <c>ARWY</c> uses (a row that does not shows its message and nothing is
    /// sent, while the rows that already match the list are left alone). An accepted row stays in the window and follows
    /// the room's own list and spelling again; a row the server refuses keeps its text with the server's message under it,
    /// and the other rows still go. A second Apply while one is in flight does nothing, and once the window closes
    /// mid-send nothing more is sent.
    /// </summary>
    public async Task ApplyAsync(NavigationDatabase? navDb, Func<string, Task<CommandResultDto>> send)
    {
        if ((_owner is null) || IsApplying)
        {
            return;
        }

        IsApplying = true;
        try
        {
            List<ActiveRunwaysRow> changed = [];
            foreach (ActiveRunwaysRow row in Rows)
            {
                if (!string.Equals(row.Text, TextOf(row.Airport), StringComparison.Ordinal))
                {
                    changed.Add(row);
                    continue;
                }

                _typedIn.Remove(row);
                row.Error = null;
            }

            List<(ActiveRunwaysRow Row, string? Command)>? answers = ActiveRunwaysEditor.ReadRows(changed, navDb);
            if (answers is null)
            {
                return;
            }

            foreach ((ActiveRunwaysRow row, string? command) in answers)
            {
                if (command is null)
                {
                    continue;
                }

                string sentText = row.Text;
                CommandResultDto result = await SendAnswerAsync(send, command);
                if (!IsOpen)
                {
                    return;
                }

                if (result.Success)
                {
                    ApplySent(row, sentText);
                }
                else
                {
                    row.Error = result.Message ?? $"ARWY {row.Airport} was refused";
                }
            }
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>
    /// Marks a sent row as following the room again and gives it the room's own spelling of what was sent — unless the
    /// text moved while the answer was in flight, in which case the row the user retyped keeps its text and stays typed in.
    /// </summary>
    private void ApplySent(ActiveRunwaysRow row, string sentText)
    {
        if (!string.Equals(row.Text, sentText, StringComparison.Ordinal))
        {
            return;
        }

        _typedIn.Remove(row);
        _refreshing = true;
        try
        {
            row.Text = TextOf(row.Airport);
        }
        finally
        {
            _refreshing = false;
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

        if (e.PropertyName == nameof(MainViewModel.ActiveRunwaysScope))
        {
            // Every way the scenario's active-runways scope ends or is replaced bumps it: another load, a recording load,
            // an unload, leaving the room. A restart re-activates the same scenario without one, so the window stays.
            if (_scope != _owner.ActiveRunwaysScope)
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

    /// <summary>
    /// Brings the rows in line with the room's list: an airport the list newly names gains a row in its place in the order
    /// and one it no longer names loses its own row, unless the user has typed in that row; a row the user typed in keeps
    /// its text and every other row takes the list's.
    /// </summary>
    private void RefreshRows()
    {
        List<string> airports = TargetAirports();
        _refreshing = true;
        try
        {
            for (int index = Rows.Count - 1; index >= 0; index--)
            {
                ActiveRunwaysRow row = Rows[index];
                if (airports.Contains(row.Airport, StringComparer.Ordinal) || _typedIn.Contains(row))
                {
                    continue;
                }

                row.PropertyChanged -= OnRowPropertyChanged;
                Rows.RemoveAt(index);
            }

            for (int index = 0; index < airports.Count; index++)
            {
                string airport = airports[index];
                ActiveRunwaysRow? row = Rows.FirstOrDefault(candidate => string.Equals(candidate.Airport, airport, StringComparison.Ordinal));
                if (row is null)
                {
                    row = new ActiveRunwaysRow(airport, TextOf(airport));
                    row.PropertyChanged += OnRowPropertyChanged;
                    Rows.Insert(index, row);
                    continue;
                }

                int current = Rows.IndexOf(row);
                if (current != index)
                {
                    Rows.Move(current, index);
                }

                if (!_typedIn.Contains(row))
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
