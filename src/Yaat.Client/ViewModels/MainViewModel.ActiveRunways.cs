using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Client mirror of the room's active runways (<c>ARWY</c>) and the prompt that asks the loading mentor for them.
/// Server-authoritative: every payload that carries the list (the load result, <see cref="ServerConnection.ScenarioLoaded"/>,
/// the join's <see cref="RoomStateDto"/>, a rewind or recording load's <see cref="RewindResultDto"/>,
/// <see cref="ServerConnection.ActiveRunwaysChanged"/> and the restart's <see cref="ScenarioRestartedDto"/>) replaces it
/// wholesale, so whichever arrives last wins; leaving the scenario clears it. The prompt opens only on the mentor's own
/// load when the server asks (<see cref="LoadScenarioResultDto.ActiveRunwaysPromptNeeded"/>), never in a live session,
/// and answers with ordinary <c>ARWY</c> commands.
/// </summary>
public partial class MainViewModel
{
    private const string ActiveRunwaysNotSetHint = "Active runways not set; use ARWY or Scenario › Active Runways…";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoActiveRunways = new Dictionary<string, IReadOnlyList<string>>();

    private IReadOnlyDictionary<string, IReadOnlyList<string>> _roomActiveRunways = NoActiveRunways;

    private Dictionary<string, List<string>> _activeRunwaysPromptPrefill = [];

    private Dictionary<string, RunwayUseCountsDto> _activeRunwaysPromptAssigned = [];

    /// <summary>Bumped on every open and close of the prompt, so an answer still sending can tell its prompt is gone.</summary>
    private int _activeRunwaysPromptGeneration;

    /// <summary>
    /// The room's active runways: each airport naming at least one end, keyed by FAA id, its ends as the server spells
    /// them (<c>30</c> both ways, <c>D28L</c> departures only, <c>A28R</c> arrivals only). Empty with no scenario.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RoomActiveRunways
    {
        get => _roomActiveRunways;
        private set => SetProperty(ref _roomActiveRunways, value);
    }

    /// <summary>The prompt's rows: the primary airport first, then every other airport the server's guess names.</summary>
    public ObservableCollection<ActiveRunwaysRow> ActiveRunwaysPromptRows { get; } = [];

    /// <summary>
    /// The prompt's notes, for the airports still shown in row order: each airport's line on the aircraft that already have
    /// a runway (<see cref="ActiveRunwaysEditor.AssignedNote"/>), then its notes on the guess (<see cref="ActiveRunwaysEditor.Notes"/>).
    /// </summary>
    public ObservableCollection<string> ActiveRunwaysPromptNotes { get; } = [];

    [ObservableProperty]
    private bool _showActiveRunwaysPrompt;

    /// <summary>
    /// Bumped whenever the scenario's active-runways editing scope ends or is replaced — another load, a recording load,
    /// an unload, leaving the room — and never by a restart: what the Scenario menu's window closes on. An id cannot say
    /// it, because reloading a scenario and loading a recording of it keep the same id.
    /// </summary>
    [ObservableProperty]
    private int _activeRunwaysScope;

    /// <summary>Replaces the room's active runways with <paramref name="byAirport"/>, as every feed does.</summary>
    internal void ApplyActiveRunways(Dictionary<string, List<string>> byAirport) =>
        RoomActiveRunways = byAirport.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)[.. entry.Value], StringComparer.Ordinal);

    /// <summary>A change while the prompt is open updates the list and leaves the prompt as it is.</summary>
    internal void OnActiveRunwaysChanged(ActiveRunwaysChangedDto dto) => Dispatcher.UIThread.Post(() => ApplyActiveRunways(dto.ByAirport));

    /// <summary>
    /// The loader's own result: the room's list as loaded, and the prompt opened with the server's guess when the server
    /// asks and this client is a mentor outside a live session; any earlier prompt closes otherwise.
    /// </summary>
    internal void ApplyLoadResultActiveRunways(LoadScenarioResultDto result)
    {
        ApplyActiveRunways(result.ActiveRunways);
        if (result.ActiveRunwaysPromptNeeded && (!IsNonMentor) && (!result.IsLiveSession))
        {
            OpenActiveRunwaysPrompt(result.PrimaryAirportId, result.ActiveRunwaysPrefill, result.ActiveRunwaysAssigned);
        }
        else
        {
            CloseActiveRunwaysPrompt();
        }
    }

    /// <summary>
    /// Sends the prompt's answer through <paramref name="send"/>, one <c>ARWY</c> per airport, once every row reads (a row
    /// that does not shows the parser's message and nothing is sent). Each accepted row leaves the prompt; a refused one
    /// stays with the server's message; the prompt closes when none is left. Once the prompt closes or reopens mid-send
    /// (an unload, leaving the room, another load), nothing more is sent.
    /// </summary>
    internal async Task SubmitActiveRunwaysPromptAsync(NavigationDatabase? navDb, Func<string, Task<CommandResultDto>> send)
    {
        int generation = _activeRunwaysPromptGeneration;
        List<(ActiveRunwaysRow Row, string? Command)>? answers = ActiveRunwaysEditor.ReadRows(ActiveRunwaysPromptRows, navDb);
        if (answers is null)
        {
            return;
        }

        foreach ((ActiveRunwaysRow row, string? command) in answers)
        {
            CommandResultDto result = command is null ? new CommandResultDto(true, null) : await SendActiveRunwaysAnswerAsync(send, command);
            if (generation != _activeRunwaysPromptGeneration)
            {
                return;
            }

            if (result.Success)
            {
                row.PropertyChanged -= OnActiveRunwaysRowChanged;
                ActiveRunwaysPromptRows.Remove(row);
            }
            else
            {
                row.Error = result.Message ?? $"ARWY {row.Airport} was refused";
            }
        }

        if (ActiveRunwaysPromptRows.Count == 0)
        {
            CloseActiveRunwaysPrompt();
        }
        else
        {
            RefreshActiveRunwaysPromptNotes();
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmActiveRunwaysPrompt))]
    private Task ConfirmActiveRunwaysPromptAsync() =>
        SubmitActiveRunwaysPromptAsync(
            NavigationDatabase.InstanceOrNull,
            command => _connection.SendCommandAsync("", command, _preferences.UserInitials)
        );

    private bool CanConfirmActiveRunwaysPrompt() => ActiveRunwaysEditor.CanConfirm(ActiveRunwaysPromptRows, NavigationDatabase.InstanceOrNull);

    /// <summary>Leaves the room's list as the server seeded it and the room unanswered, and says how to set it later.</summary>
    [RelayCommand]
    private void CancelActiveRunwaysPrompt()
    {
        CloseActiveRunwaysPrompt();
        AddSystemEntry(ActiveRunwaysNotSetHint);
    }

    private async Task<CommandResultDto> SendActiveRunwaysAnswerAsync(Func<string, Task<CommandResultDto>> send, string command)
    {
        try
        {
            return await send(command);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Active runways answer '{Command}' failed", command);
            return new CommandResultDto(false, ex.Message);
        }
    }

    private void OpenActiveRunwaysPrompt(
        string? primaryAirportId,
        Dictionary<string, List<string>> prefill,
        Dictionary<string, RunwayUseCountsDto> assigned
    )
    {
        CloseActiveRunwaysPrompt();
        _activeRunwaysPromptGeneration++;
        _activeRunwaysPromptPrefill = prefill;
        _activeRunwaysPromptAssigned = assigned;
        foreach (string airport in ActiveRunwaysPromptAirports(primaryAirportId, prefill))
        {
            var row = new ActiveRunwaysRow(airport, ActiveRunwaysEditor.ToText(prefill.GetValueOrDefault(airport) ?? []));
            row.PropertyChanged += OnActiveRunwaysRowChanged;
            ActiveRunwaysPromptRows.Add(row);
        }

        RefreshActiveRunwaysPromptNotes();
        ShowActiveRunwaysPrompt = true;
        ConfirmActiveRunwaysPromptCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The primary airport first, whether or not the guess names it, then every other airport the guess names.</summary>
    private static List<string> ActiveRunwaysPromptAirports(string? primaryAirportId, Dictionary<string, List<string>> prefill)
    {
        List<string> airports = [];
        if (!string.IsNullOrWhiteSpace(primaryAirportId))
        {
            airports.Add(NavigationDatabase.NormalizeAirport(primaryAirportId));
        }

        airports.AddRange(prefill.Keys.Where(airport => !airports.Contains(airport, StringComparer.Ordinal)));
        return airports;
    }

    private void OnActiveRunwaysRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveRunwaysRow.Text))
        {
            ConfirmActiveRunwaysPromptCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshActiveRunwaysPromptNotes()
    {
        ActiveRunwaysPromptNotes.Clear();
        foreach (ActiveRunwaysRow row in ActiveRunwaysPromptRows)
        {
            ActiveRunwaysPromptNotes.Add(ActiveRunwaysEditor.AssignedNote(row.Airport, _activeRunwaysPromptAssigned.GetValueOrDefault(row.Airport)));
            foreach (string note in ActiveRunwaysEditor.Notes([row.Airport], _activeRunwaysPromptPrefill))
            {
                ActiveRunwaysPromptNotes.Add(note);
            }
        }
    }

    private void CloseActiveRunwaysPrompt()
    {
        ActiveRunwaysScope++;
        _activeRunwaysPromptGeneration++;
        ShowActiveRunwaysPrompt = false;
        foreach (ActiveRunwaysRow row in ActiveRunwaysPromptRows)
        {
            row.PropertyChanged -= OnActiveRunwaysRowChanged;
        }

        ActiveRunwaysPromptRows.Clear();
        ActiveRunwaysPromptNotes.Clear();
        _activeRunwaysPromptPrefill = [];
        _activeRunwaysPromptAssigned = [];
    }

    /// <summary>Every way out of a scenario: the room has no active runways, and the prompt for them closes.</summary>
    private void ClearActiveRunways()
    {
        RoomActiveRunways = NoActiveRunways;
        CloseActiveRunwaysPrompt();
    }
}
