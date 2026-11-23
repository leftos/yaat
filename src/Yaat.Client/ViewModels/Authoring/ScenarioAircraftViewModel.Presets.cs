using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The detail panel's preset commands: one row per preset of the selected aircraft, in the document's order. Add appends
/// an empty command at offset 0 with a new id; Duplicate inserts a copy with a new id directly after its source; Remove,
/// Move up and Move down act on their row; each is one undo step "&lt;verb&gt; preset". A row's command and offset commit
/// as "Edit preset command" and "Edit preset offset"; a commit that changes nothing re-shows only that row's field.
/// </summary>
public sealed partial class ScenarioAircraftViewModel
{
    /// <summary>Gets the selected aircraft's preset commands, one row each, in the document's order.</summary>
    public ObservableCollection<ScenarioPresetRowViewModel> Presets { get; } = [];

    /// <summary>Writes the row's command text as every text field's commit takes it (trimmed at its ends, blank as empty).</summary>
    /// <param name="row">The row committed.</param>
    internal void CommitPresetCommand(ScenarioPresetRowViewModel row) =>
        WithFieldReload(
            a => ReloadPreset(a, row, p => row.LoadCommand(p.Command)),
            () => CommitText("Edit preset command", row.Command, (a, v) => PresetOf(a, row)?.Command = v ?? "")
        );

    /// <summary>Writes the row's time offset, a ScenarioTime; a text that is not one sets the row's error and writes nothing.</summary>
    /// <param name="row">The row committed.</param>
    internal void CommitPresetOffset(ScenarioPresetRowViewModel row) =>
        WithFieldReload(
            a => ReloadPreset(a, row, p => row.LoadOffset(p.TimeOffset)),
            () =>
                CommitParsed(
                    ScenarioFieldText.Time,
                    row.Offset,
                    e => row.OffsetError = e,
                    "Edit preset offset",
                    (a, v) => PresetOf(a, row)?.TimeOffset = v
                )
        );

    /// <summary>Removes the row's preset.</summary>
    /// <param name="row">The row.</param>
    internal void RemovePreset(ScenarioPresetRowViewModel row) =>
        ApplyToSelected(
            "Remove preset",
            a =>
            {
                if (PresetIndex(a, row) is var i and >= 0)
                {
                    a.PresetCommands.RemoveAt(i);
                }
            }
        );

    /// <summary>Swaps the row's preset with its neighbour one step up (-1) or down (+1); a move past either end writes nothing.</summary>
    /// <param name="row">The row.</param>
    /// <param name="step">-1 or +1.</param>
    internal void MovePreset(ScenarioPresetRowViewModel row, int step) =>
        ApplyToSelected(
            "Move preset",
            a =>
            {
                int i = PresetIndex(a, row);
                int j = i + step;
                if ((i >= 0) && (j >= 0) && (j < a.PresetCommands.Count))
                {
                    (a.PresetCommands[i], a.PresetCommands[j]) = (a.PresetCommands[j], a.PresetCommands[i]);
                }
            }
        );

    /// <summary>Inserts a copy of the row's preset (its command, offset and unmodelled fields) with a new id directly after it.</summary>
    /// <param name="row">The row.</param>
    internal void DuplicatePreset(ScenarioPresetRowViewModel row)
    {
        string id = NewPresetId();
        ApplyToSelected(
            "Duplicate preset",
            a =>
            {
                if (PresetIndex(a, row) is var i and >= 0)
                {
                    a.PresetCommands.Insert(i + 1, CopyOf(a.PresetCommands[i], id));
                }
            }
        );
    }

    /// <summary>Appends an empty preset at offset 0 with a new id.</summary>
    [RelayCommand]
    private void AddPreset()
    {
        string id = NewPresetId();
        ApplyToSelected("Add preset", a => (a.PresetCommands ??= []).Add(new PresetCommand { Id = id }));
    }

    private string NewPresetId() => ScenarioIdGenerator.Create(_services.Clock.GetUtcNow(), _services.Random);

    private static PresetCommand CopyOf(PresetCommand source, string id) =>
        new()
        {
            Id = id,
            Command = source.Command,
            TimeOffset = source.TimeOffset,
            ExtensionData = (source.ExtensionData is { } extension) ? new Dictionary<string, JsonElement>(extension, StringComparer.Ordinal) : null,
        };

    // The rows from the aircraft's presets: refreshed in place while the presets have the rows' ids in the rows' order,
    // made anew otherwise.
    private void LoadPresets(ScenarioAircraft aircraft)
    {
        List<PresetCommand> presets = aircraft.PresetCommands ?? [];
        if ((presets.Count == Presets.Count) && presets.Select(p => p.Id).SequenceEqual(Presets.Select(r => r.Id), StringComparer.Ordinal))
        {
            for (int i = 0; i < presets.Count; i++)
            {
                Presets[i].Update(presets[i], i, presets.Count);
            }

            return;
        }

        Presets.Clear();
        for (int i = 0; i < presets.Count; i++)
        {
            Presets.Add(new ScenarioPresetRowViewModel(this, presets[i], i, presets.Count));
        }
    }

    private static void ReloadPreset(ScenarioAircraft aircraft, ScenarioPresetRowViewModel row, Action<PresetCommand> load)
    {
        if (PresetOf(aircraft, row) is { } preset)
        {
            load(preset);
        }
    }

    private static PresetCommand? PresetOf(ScenarioAircraft aircraft, ScenarioPresetRowViewModel row) =>
        (PresetIndex(aircraft, row) is var i and >= 0) ? aircraft.PresetCommands[i] : null;

    // The row's preset's index in the aircraft's presets: the row's own index while it still holds that preset, else the
    // first preset with its id; -1 when the aircraft has none with it.
    private static int PresetIndex(ScenarioAircraft aircraft, ScenarioPresetRowViewModel row)
    {
        List<PresetCommand> presets = aircraft.PresetCommands ?? [];
        if ((row.Index < presets.Count) && string.Equals(presets[row.Index].Id, row.Id, StringComparison.Ordinal))
        {
            return row.Index;
        }

        return presets.FindIndex(p => string.Equals(p.Id, row.Id, StringComparison.Ordinal));
    }
}
