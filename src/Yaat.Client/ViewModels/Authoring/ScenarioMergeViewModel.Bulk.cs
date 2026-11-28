using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>The merge window's whole-scenario choices, and the MERGED VALUE editors with the pickers they offer.</summary>
public sealed partial class ScenarioMergeViewModel
{
    private ScenarioMergePickers _pickers = ScenarioMergePickers.FreeText;

    /// <summary>
    /// Loads the position catalog and the airports once, off the UI thread, and applies them through the services' post;
    /// an editor opened before they apply, or after a load that failed, offers free text for those fields.
    /// </summary>
    /// <param name="services">The ARTCC configs and the UI-thread post.</param>
    /// <param name="ct">Stops the load; nothing is applied after it.</param>
    /// <returns>A task finished once the pickers are applied.</returns>
    public async Task LoadPickersAsync(ScenarioBrowserServices services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        string artccId = Request.ArtccId;
        ScenarioMergePickers pickers;
        try
        {
            ArtccConfigSource configs = services.Configs;
            ScenarioGeneralViewModel.PickerData data = await Task.Run(() => ScenarioGeneralViewModel.ReadPickersAsync(configs, artccId, ct), ct)
                .ConfigureAwait(false);
            pickers = ScenarioMergePickers.Of(data);
            if ((data.Positions is null) && Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(
                    "Merge window: the ARTCC config of {ArtccId} gave no positions ({Problem}); they are free text",
                    artccId,
                    data.Problem
                );
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Merge window: loading the ARTCC config of {ArtccId} failed; positions and airports are free text", artccId);
            }

            pickers = ScenarioMergePickers.FreeText;
        }

        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.Post(() =>
        {
            if (!ct.IsCancellationRequested)
            {
                _pickers = pickers;
            }

            applied.SetResult();
        });
        await applied.Task.ConfigureAwait(false);
    }

    // Opens the row's editor (✎), pre-filled from the side it takes; an order row takes mine or theirs only.
    [RelayCommand]
    private void EditRow(ScenarioMergeRowViewModel? row)
    {
        if (row is { CanEdit: true, Editor: null })
        {
            OpenEditor(row);
        }
    }

    // An editor under every clash at once, each kept or cancelled on its own; order rows and aircraft one side holds get none.
    [RelayCommand]
    private void EditEveryClash()
    {
        foreach (ScenarioMergeRowViewModel row in Rows.Where(row => row.IsClash && row.CanEdit && (row.Editor is null)))
        {
            if (!((row.Section == AircraftSectionName) && row.IsItemRow))
            {
                OpenEditor(row);
            }
        }
    }

    private void OpenEditor(ScenarioMergeRowViewModel row)
    {
        JsonNode? value = row.PrefillValue(_choices.GetValueOrDefault(row.Path));
        var seed = new ScenarioMergeEditorSeed(row.FieldEditor, value, CommandContext(row));
        row.Editor = new ScenarioMergedValueViewModel(seed, _pickers, edited => KeepEdit(row, edited), () => row.Editor = null);
    }

    // The flight plan of the row's aircraft on the side its editor pre-fills from, for a preset command to resolve against.
    private ScenarioMergeCommandContext? CommandContext(ScenarioMergeRowViewModel row)
    {
        if ((row.FieldEditor.Kind != ScenarioMergeEditorKind.Command) || (row.Section != AircraftSectionName) || (row.ItemKey is not { } key))
        {
            return null;
        }

        string text = (row.PressedSide == MergeChoiceKind.KeepTheirs) ? Request.Texts.Theirs : Request.Texts.Mine;
        List<JsonObject> aircraft = Items(JsonNode.Parse(text, documentOptions: ParseOptions)!.AsObject(), AircraftKey);
        return ScenarioMergeCommandContext.Of(AircraftWithKey(aircraft, key)?["flightplan"] as JsonObject);
    }

    private static JsonObject? AircraftWithKey(List<JsonObject> aircraft, string key) =>
        (key.StartsWith('#') && int.TryParse(key[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            ? aircraft.ElementAtOrDefault(index)
            : aircraft.FirstOrDefault(item => IdOf(item) == key);

    private void KeepEdit(ScenarioMergeRowViewModel row, JsonNode? value)
    {
        _choices[row.Path] = MergeChoice.Edit(value);
        row.Editor = null;
        Refresh();
    }

    [RelayCommand]
    private void KeepMineForEveryClash() => KeepForEveryClash(MergeChoiceKind.KeepMine);

    [RelayCommand]
    private void KeepTheirsForEveryClash() => KeepForEveryClash(MergeChoiceKind.KeepTheirs);

    [RelayCommand]
    private void TakeMineForEverything() => TakeForEverything(MergeChoice.KeepMine);

    [RelayCommand]
    private void TakeTheirsForEverything() => TakeForEverything(MergeChoice.KeepTheirs);

    // The engine's clash-only choices replace each clash's choice; an auto row keeps its own side or its choice.
    private void KeepForEveryClash(MergeChoiceKind side)
    {
        foreach ((string path, MergeChoice choice) in Plan.ChooseAll(side))
        {
            _choices[path] = choice;
        }

        Refresh();
    }

    // Every row, auto and order rows included, takes the side; every earlier choice is dropped.
    private void TakeForEverything(MergeChoice choice)
    {
        _choices.Clear();
        foreach (ScenarioMergeRowViewModel row in Rows)
        {
            _choices[row.Path] = choice;
        }

        Refresh();
    }
}
