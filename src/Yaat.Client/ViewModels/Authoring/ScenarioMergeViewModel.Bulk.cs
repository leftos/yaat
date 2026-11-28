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

    // Every choice waits while the MERGED AIRCRAFT editor is open.
    private bool CanChoose() => MergedAircraft is null;

    // Opens the row's editor (✎), pre-filled from the side it takes; an order row takes mine or theirs only.
    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void EditRow(ScenarioMergeRowViewModel? row)
    {
        if (CanChoose() && (row is { CanEdit: true, Editor: null }))
        {
            OpenEditor(row);
        }
    }

    // An editor under every clash at once, each kept or cancelled on its own; order rows, aircraft one side holds and the rows
    // of a replaced aircraft (resolved, not clashes) get none.
    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void EditEveryClash()
    {
        if (!CanChoose())
        {
            return;
        }

        foreach (ScenarioMergeRowViewModel row in Rows.Where(row => row.IsClash && row.CanEdit && (row.Editor is null) && !IsReplaced(row)))
        {
            if (!((row.Section == AircraftSectionName) && row.IsItemRow))
            {
                OpenEditor(row);
            }
        }
    }

    private void OpenEditor(ScenarioMergeRowViewModel row)
    {
        JsonNode? value =
            (Replacement(row) is { } replacement) ? ReplacementValue(replacement, row) : row.PrefillValue(_choices.GetValueOrDefault(row.Path));
        var seed = new ScenarioMergeEditorSeed(row.FieldEditor, value, CommandContext(row));
        row.Editor = new ScenarioMergedValueViewModel(seed, _pickers, edited => KeepEdit(row, edited), () => row.Editor = null);
    }

    // The flight plan of the row's aircraft on the side its editor pre-fills from (its replacement under a replaced aircraft),
    // for a preset command to resolve against.
    private ScenarioMergeCommandContext? CommandContext(ScenarioMergeRowViewModel row)
    {
        if ((row.FieldEditor.Kind != ScenarioMergeEditorKind.Command) || (row.Section != AircraftSectionName) || (row.ItemKey is not { } key))
        {
            return null;
        }

        if (Replacement(row) is { } replacement)
        {
            return ScenarioMergeCommandContext.Of(replacement["flightplan"] as JsonObject);
        }

        string text = (row.PressedSide == MergeChoiceKind.KeepTheirs) ? Request.Texts.Theirs : Request.Texts.Mine;
        List<JsonObject> aircraft = Items(JsonNode.Parse(text, documentOptions: ParseOptions)!.AsObject(), AircraftKey);
        return ScenarioMergeCommandContext.Of(AircraftWithKey(aircraft, key)?["flightplan"] as JsonObject);
    }

    private static JsonObject? AircraftWithKey(List<JsonObject> aircraft, string key) => ScenarioMergeItemKeys.Find(aircraft, key);

    // The merged-aircraft replacement over the row's aircraft; null for a row of an aircraft not replaced.
    private JsonObject? Replacement(ScenarioMergeRowViewModel row) =>
        ((row.Section == AircraftSectionName) && (row.ItemKey is { } key) && _aircraftEdits.TryGetValue(key, out JsonObject? replacement))
            ? replacement
            : null;

    // The value at the row's field in its aircraft's replacement; null where the replacement lacks it.
    private JsonNode? ReplacementValue(JsonObject replacement, ScenarioMergeRowViewModel row)
    {
        var place = new ReplacementPlace(replacement, SideAircraft(Request.Texts.Mine, row.ItemKey), SideAircraft(Request.Texts.Theirs, row.ItemKey));
        foreach (string segment in row.Path.Split('/').Skip(3))
        {
            place = place.Child(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }

        return place.Edit?.DeepClone();
    }

    private static JsonObject? SideAircraft(string text, string? key) =>
        (key is null) ? null : AircraftWithKey(Items(JsonNode.Parse(text, documentOptions: ParseOptions)!.AsObject(), AircraftKey), key);

    /// <summary>
    /// One place along a row's path in an aircraft's replacement and in each side's aircraft. A list whose items all carry
    /// distinct ids on both sides is numbered as the engine walks it (mine's ids, then the ids only theirs holds), so its item
    /// is found by that id; any other list is numbered by position.
    /// </summary>
    private sealed record ReplacementPlace(JsonNode? Edit, JsonNode? Mine, JsonNode? Theirs)
    {
        public ReplacementPlace Child(string name)
        {
            bool isIndex = int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int index);
            if (!isIndex || (Edit is not JsonArray))
            {
                return new(Member(Edit, name), Member(Mine, name), Member(Theirs, name));
            }

            List<JsonObject?> mine = Elements(Mine);
            List<JsonObject?> theirs = Elements(Theirs);
            if (!HasDistinctIds(mine) || !HasDistinctIds(theirs))
            {
                return new(Elements(Edit).ElementAtOrDefault(index), mine.ElementAtOrDefault(index), theirs.ElementAtOrDefault(index));
            }

            List<string> ids = [.. mine.Select(item => IdOf(item)!)];
            ids.AddRange([.. theirs.Select(item => IdOf(item)!).Where(id => !ids.Contains(id))]);
            string? id = ids.ElementAtOrDefault(index);
            return new(WithId(Elements(Edit), id), WithId(mine, id), WithId(theirs, id));
        }

        private static JsonNode? Member(JsonNode? node, string name) => (node is JsonObject obj) ? obj[name] : null;

        private static List<JsonObject?> Elements(JsonNode? node) => (node is JsonArray list) ? [.. list.Select(item => item as JsonObject)] : [];

        private static bool HasDistinctIds(List<JsonObject?> items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return items.All(item => (IdOf(item) is string id) && seen.Add(id));
        }

        private static JsonObject? WithId(List<JsonObject?> items, string? id) =>
            (id is null) ? null : items.FirstOrDefault(item => IdOf(item) == id);
    }

    // A row's edit waits while the MERGED AIRCRAFT editor is open; its editor stays open.
    private void KeepEdit(ScenarioMergeRowViewModel row, JsonNode? value)
    {
        if (!CanChoose())
        {
            return;
        }

        _choices[row.Path] = MergeChoice.Edit(value);
        DropAircraftEdits([row]);
        row.Editor = null;
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void KeepMineForEveryClash() => KeepForEveryClash(MergeChoiceKind.KeepMine);

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void KeepTheirsForEveryClash() => KeepForEveryClash(MergeChoiceKind.KeepTheirs);

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void TakeMineForEverything() => TakeForEverything(MergeChoice.KeepMine);

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void TakeTheirsForEverything() => TakeForEverything(MergeChoice.KeepTheirs);

    // The engine's clash-only choices replace each clash's choice; an auto row keeps its own side or its choice, and the rows
    // of a replaced aircraft (resolved, not clashes) keep theirs.
    private void KeepForEveryClash(MergeChoiceKind side)
    {
        if (!CanChoose())
        {
            return;
        }

        var replaced = new HashSet<string>(Rows.Where(IsReplaced).Select(row => row.Path), StringComparer.Ordinal);
        foreach ((string path, MergeChoice choice) in Plan.ChooseAll(side).Where(pair => !replaced.Contains(pair.Key)))
        {
            _choices[path] = choice;
        }

        Refresh();
    }

    // Every row, auto and order rows included, takes the side; every earlier choice and merged-aircraft replacement is dropped.
    private void TakeForEverything(MergeChoice choice)
    {
        if (!CanChoose())
        {
            return;
        }

        _choices.Clear();
        _aircraftEdits.Clear();
        foreach (ScenarioMergeRowViewModel row in Rows)
        {
            _choices[row.Path] = choice;
        }

        Refresh();
    }
}
