using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The Aircraft tab over one <see cref="ScenarioEditSession"/>: one read-only row per scenario aircraft in document order,
/// the selected row, and Add, Duplicate and Remove, each one undo step. The rows are rebuilt from the session's document
/// after every edit and on <see cref="Reload"/> (undo or redo replaced the document), and the selection is held by
/// aircraft id: when its aircraft is gone, the row now at its index is selected, else the last row, else none. A reload
/// that brings back exactly one aircraft selects it; one that takes away exactly one aircraft this tab added selects
/// what was selected before that add. The grid's sort is the view's own and never reorders the document.
/// </summary>
public sealed partial class ScenarioAircraftViewModel : ObservableObject
{
    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioAircraftViewModel>();

    private readonly ScenarioEditSession _session;
    private readonly ScenarioBrowserServices _services;

    // Each aircraft Add or Duplicate made, by its id, with the id selected before it (null for none).
    private readonly Dictionary<string, string?> _priorSelection = new(StringComparer.Ordinal);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateAircraftCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveAircraftCommand))]
    private ScenarioAircraftRowViewModel? _selected;

    /// <summary>Initializes a new instance of the <see cref="ScenarioAircraftViewModel"/> class with nothing selected.</summary>
    /// <param name="session">The open working copy's session.</param>
    /// <param name="services">The clock and random source new aircraft ids are made from, and the grid layout preference.</param>
    public ScenarioAircraftViewModel(ScenarioEditSession session, ScenarioBrowserServices services)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Rebuild(null, -1);
    }

    /// <summary>Raised after a command wrote to the document.</summary>
    public event EventHandler? Edited;

    /// <summary>Gets the scenario's aircraft, one row each, in the document's order.</summary>
    public ObservableCollection<ScenarioAircraftRowViewModel> Rows { get; } = [];

    /// <summary>Gets the grid's saved column order, widths and sort; null when none was saved.</summary>
    public SavedGridLayout? GridLayout => _services.Preferences.ScenarioEditorAircraftGrid;

    /// <summary>Saves the grid's column order, widths and sort; a view-only preference, never an edit.</summary>
    /// <param name="layout">The layout the grid shows now.</param>
    public void SaveGridLayout(SavedGridLayout layout) => _services.Preferences.SetScenarioEditorAircraftGrid(layout);

    /// <summary>
    /// Rebuilds the rows from the session's document after undo or redo replaced it. Exactly one aircraft id new since
    /// the last rows is selected (an undone Remove, a redone Add); else exactly one id gone that this tab added selects
    /// what was selected before the add; else the selected aircraft stays selected, by the selection rule.
    /// </summary>
    public void Reload()
    {
        var before = new HashSet<string>(Rows.Select(r => r.Id), StringComparer.Ordinal);
        var after = new HashSet<string>(_session.Document.Scenario.Aircraft.Select(a => a.Id), StringComparer.Ordinal);
        string[] appeared = [.. after.Where(id => !before.Contains(id))];
        string[] gone = [.. before.Where(id => !after.Contains(id))];
        string? id = Selected?.Id;
        if (appeared.Length == 1)
        {
            id = appeared[0];
        }
        else if ((gone.Length == 1) && _priorSelection.TryGetValue(gone[0], out string? prior))
        {
            id = prior;
        }

        Rebuild(id, Selected?.Index ?? -1);
    }

    /// <summary>Appends a new aircraft (a Parking start) and selects it.</summary>
    [RelayCommand]
    private void AddAircraft()
    {
        ScenarioAircraft aircraft = ScenarioAircraftFactory.NewAircraft(_session.Document.Scenario, _services.Clock.GetUtcNow(), _services.Random);
        _priorSelection[aircraft.Id] = Selected?.Id;
        Apply("Add aircraft", s => s.Aircraft.Add(aircraft), aircraft.Id);
    }

    /// <summary>Inserts a copy of the selected aircraft (new ids, a callsign no other aircraft has) right after it and selects it.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateAircraft()
    {
        if (SelectedDocumentIndex("Duplicate aircraft") is not { } index)
        {
            return;
        }

        Scenario scenario = _session.Document.Scenario;
        ScenarioAircraft copy = ScenarioAircraftFactory.Duplicate(scenario, scenario.Aircraft[index], _services.Clock.GetUtcNow(), _services.Random);
        _priorSelection[copy.Id] = Selected?.Id;
        Apply("Duplicate aircraft", s => s.Aircraft.Insert(index + 1, copy), copy.Id);
    }

    /// <summary>Removes the selected aircraft; the row now at its index is selected, else the last row, else none.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveAircraft()
    {
        if ((SelectedDocumentIndex("Remove aircraft") is not { } index) || (Selected is not { } selected))
        {
            return;
        }

        Apply("Remove aircraft", s => s.Aircraft.RemoveAt(index), selected.Id);
    }

    private bool HasSelection() => Selected is not null;

    // The selected aircraft's index in the document: the row's own index while it still holds that aircraft, else the
    // first aircraft with its id; null with nothing selected, or (logged) when the document no longer holds it.
    private int? SelectedDocumentIndex(string what)
    {
        if (Selected is not { } selected)
        {
            return null;
        }

        List<ScenarioAircraft> aircraft = _session.Document.Scenario.Aircraft;
        if ((selected.Index < aircraft.Count) && string.Equals(aircraft[selected.Index].Id, selected.Id, StringComparison.Ordinal))
        {
            return selected.Index;
        }

        int found = aircraft.FindIndex(a => string.Equals(a.Id, selected.Id, StringComparison.Ordinal));
        if (found >= 0)
        {
            return found;
        }

        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("Scenario editor: {What} skipped: the selected aircraft {Id} is not in the document", what, selected.Id);
        }

        return null;
    }

    // One edit as one undo step, then the rows rebuilt with selectId selected (by the selection rule when it is gone).
    private void Apply(string description, Action<Scenario> change, string selectId)
    {
        int oldIndex = Selected?.Index ?? -1;
        _session.Edit(description, change);
        Rebuild(selectId, oldIndex);
        Edited?.Invoke(this, EventArgs.Empty);
    }

    // The rows from the document as it is now, with the aircraft of the id selected; when it is gone, the row at the old
    // index, else the last row, else none. No id selects none.
    private void Rebuild(string? id, int oldIndex)
    {
        Rows.Clear();
        List<ScenarioAircraft> aircraft = _session.Document.Scenario.Aircraft;
        for (int i = 0; i < aircraft.Count; i++)
        {
            Rows.Add(new ScenarioAircraftRowViewModel(aircraft[i], i));
        }

        Selected = Pick(id, oldIndex);
    }

    private ScenarioAircraftRowViewModel? Pick(string? id, int oldIndex)
    {
        if ((id is null) || (Rows.Count == 0))
        {
            return null;
        }

        ScenarioAircraftRowViewModel? same = Rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (same is not null)
        {
            return same;
        }

        return ((oldIndex >= 0) && (oldIndex < Rows.Count)) ? Rows[oldIndex] : Rows[^1];
    }
}
