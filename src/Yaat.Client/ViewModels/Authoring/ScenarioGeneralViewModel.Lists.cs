using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

public sealed partial class ScenarioGeneralViewModel
{
    private IReadOnlyDictionary<string, ArtccPositionRow> _catalogRows = new Dictionary<string, ArtccPositionRow>(StringComparer.Ordinal);
    private IReadOnlyList<ScenarioPickOption> _bayOptions = [];

    /// <summary>Gets the scenario's ATC positions, one row per entry, in the document's order.</summary>
    public ObservableCollection<ScenarioAtcRowViewModel> AtcPositions { get; } = [];

    /// <summary>Gets the scenario's initialization triggers, one row per entry, in the document's order.</summary>
    public ObservableCollection<InitializationTriggerRowViewModel> Triggers { get; } = [];

    /// <summary>Gets the scenario's strip placements, one row per entry, in the document's order.</summary>
    public ObservableCollection<StripPlacementRowViewModel> StripPlacements { get; } = [];

    /// <summary>Offers a value the options do not list, so a picker still shows the document's value.</summary>
    /// <param name="options">The listed options.</param>
    /// <param name="value">The document's value; empty for none.</param>
    /// <param name="label">The text shown for it when it is not listed.</param>
    /// <returns>The options, with the value appended when it is not empty and not listed.</returns>
    internal static IReadOnlyList<ScenarioPickOption> WithCurrent(IReadOnlyList<ScenarioPickOption> options, string value, string label) =>
        ((value.Length == 0) || options.Any(o => string.Equals(o.Value, value, StringComparison.Ordinal)))
            ? options
            : [.. options, new ScenarioPickOption(value, label)];

    [RelayCommand]
    private void AddAtcPosition()
    {
        var atc = new ScenarioAtc { Id = NewItemId(), ArtccId = _session.ArtccId };
        ApplyList("Add ATC position", s => (s.Atc ??= []).Add(atc));
    }

    [RelayCommand]
    private void RemoveAtcPosition(ScenarioAtcRowViewModel? row) =>
        ForCurrentRow("Remove ATC position", row?.IsCurrent == true, row?.Index ?? -1, i => ApplyList("Remove ATC position", s => s.Atc.RemoveAt(i)));

    [RelayCommand]
    private void MoveAtcPositionUp(ScenarioAtcRowViewModel? row) =>
        ForCurrentRow("Move ATC position", row?.IsCurrent == true, row?.Index ?? -1, i => Move("Move ATC position", s => s.Atc, i, -1));

    [RelayCommand]
    private void MoveAtcPositionDown(ScenarioAtcRowViewModel? row) =>
        ForCurrentRow("Move ATC position", row?.IsCurrent == true, row?.Index ?? -1, i => Move("Move ATC position", s => s.Atc, i, 1));

    [RelayCommand]
    private void AddTrigger()
    {
        var trigger = new InitializationTrigger { Id = NewItemId() };
        ApplyList("Add trigger", s => (s.InitializationTriggers ??= []).Add(trigger));
    }

    [RelayCommand]
    private void RemoveTrigger(InitializationTriggerRowViewModel? row) =>
        ForCurrentRow(
            "Remove trigger",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => ApplyList("Remove trigger", s => s.InitializationTriggers.RemoveAt(i))
        );

    [RelayCommand]
    private void MoveTriggerUp(InitializationTriggerRowViewModel? row) =>
        ForCurrentRow("Move trigger", row?.IsCurrent == true, row?.Index ?? -1, i => Move("Move trigger", s => s.InitializationTriggers, i, -1));

    [RelayCommand]
    private void MoveTriggerDown(InitializationTriggerRowViewModel? row) =>
        ForCurrentRow("Move trigger", row?.IsCurrent == true, row?.Index ?? -1, i => Move("Move trigger", s => s.InitializationTriggers, i, 1));

    [RelayCommand]
    private void AddStripPlacement()
    {
        var strip = new FlightStripConfiguration { Id = NewItemId() };
        ApplyList("Add strip placement", s => (s.FlightStripConfigurations ??= []).Add(strip));
    }

    [RelayCommand]
    private void RemoveStripPlacement(StripPlacementRowViewModel? row) =>
        ForCurrentRow(
            "Remove strip placement",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => ApplyList("Remove strip placement", s => s.FlightStripConfigurations.RemoveAt(i))
        );

    [RelayCommand]
    private void MoveStripPlacementUp(StripPlacementRowViewModel? row) =>
        ForCurrentRow(
            "Move strip placement",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => Move("Move strip placement", s => s.FlightStripConfigurations, i, -1)
        );

    [RelayCommand]
    private void MoveStripPlacementDown(StripPlacementRowViewModel? row) =>
        ForCurrentRow(
            "Move strip placement",
            row?.IsCurrent == true,
            row?.Index ?? -1,
            i => Move("Move strip placement", s => s.FlightStripConfigurations, i, 1)
        );

    private string NewItemId() => ScenarioIdGenerator.Create(_services.Clock.GetUtcNow(), _services.Random);

    // Runs a row command on the row's index when there is a row and it still wraps its item; otherwise does nothing (a
    // view without a selection, or a row kept past an undo, redo or list edit).
    private static void ForCurrentRow(string what, bool isCurrent, int index, Action<int> act)
    {
        if (isCurrent)
        {
            act(index);
            return;
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Scenario editor: {What} skipped: no row, or the row no longer wraps its item", what);
        }
    }

    // Swaps the item with its neighbour one step up (-1) or down (+1); a move past either end writes nothing.
    private void Move<T>(string description, Func<Scenario, List<T>> list, int index, int step)
    {
        int other = index + step;
        if ((other < 0) || (other >= list(_session.Document.Scenario).Count))
        {
            return;
        }

        ApplyList(description, s => (list(s)[index], list(s)[other]) = (list(s)[other], list(s)[index]));
    }

    private void ApplyList(string description, Action<Scenario> change)
    {
        Apply(description, change);
        RebuildRows();
        RebuildRowOptions();
    }

    // One row per item of each list, wrapping the item at its index in the document as it is now; a list the document
    // holds as null has no rows (and stays null until an add).
    private void RebuildRows()
    {
        Scenario scenario = _session.Document.Scenario;
        AtcPositions.Clear();
        for (int i = 0; i < (scenario.Atc ?? []).Count; i++)
        {
            AtcPositions.Add(new ScenarioAtcRowViewModel(_session, i, Apply, FindPosition));
        }

        Triggers.Clear();
        for (int i = 0; i < (scenario.InitializationTriggers ?? []).Count; i++)
        {
            Triggers.Add(new InitializationTriggerRowViewModel(_session, i, Apply));
        }

        StripPlacements.Clear();
        for (int i = 0; i < (scenario.FlightStripConfigurations ?? []).Count; i++)
        {
            StripPlacements.Add(new StripPlacementRowViewModel(_session, i, Apply));
        }
    }

    private void RebuildRowOptions()
    {
        foreach (ScenarioAtcRowViewModel row in AtcPositions)
        {
            row.UpdateOptions(_catalogOptions);
        }

        foreach (StripPlacementRowViewModel row in StripPlacements)
        {
            row.UpdateOptions(_bayOptions);
        }
    }

    private ArtccPositionRow? FindPosition(string positionId) => _catalogRows.GetValueOrDefault(positionId);

    // The bays of the ARTCC's own facility tree, one option per facility and bay, in the config's order.
    private static List<ScenarioPickOption> StripBays(ArtccConfigRoot config)
    {
        var bays = new List<ScenarioPickOption>();
        WalkFacilities(
            config.Facility,
            facility =>
            {
                foreach (StripBayConfig bay in facility.FlightStripsConfiguration?.StripBays ?? [])
                {
                    bays.Add(new ScenarioPickOption(StripPlacementRowViewModel.BayValueOf(facility.Id, bay.Id), $"{bay.Name} ({facility.Id})"));
                }
            }
        );
        return [.. bays.DistinctBy(b => b.Value, StringComparer.Ordinal)];
    }
}
