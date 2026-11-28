using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The merge window's Apply and Cancel: the merged text from the choices and the merged-aircraft replacements, refused when it
/// does not read as a scenario, then the checks, their findings shown, and the write; Cancel ends the window with nothing written.
/// </summary>
public sealed partial class ScenarioMergeViewModel
{
    private static readonly JsonSerializerOptions MergedWriteOptions = new() { WriteIndented = true };

    // Merged-aircraft replacements of aircraft both sides hold, by aircraft line key, placed by the aircraft's identity.
    private readonly Dictionary<string, JsonObject> _aircraftEdits = new(StringComparer.Ordinal);

    private readonly TaskCompletionSource<ScenarioMergeResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _applying;

    // The open MERGED AIRCRAFT editor's refusal of Keep this, kept on the footer while the editor stays open.
    private string? _editorRefusal;

    /// <summary>The footer's error line: why the last Apply refused the merged text or could not check or write it; null for none.</summary>
    [ObservableProperty]
    private string? _applyError;

    /// <summary>The MERGED AIRCRAFT editor open over the merge so far; null while none is. Apply waits while one is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private ScenarioMergedAircraftViewModel? _mergedAircraft;

    // The member of the stand-in a one-sided aircraft's item row is given to find where the aircraft merges; its value is
    // fresh for each search, so an aircraft that happens to carry the member is not taken for it.
    private const string PlaceMarker = "$yaatMergePlace";

    /// <summary>Gets the window's outcome: the applied result, or null once the author cancels. The window closes when it ends.</summary>
    public Task<ScenarioMergeResult?> Completion => _completion.Task;

    /// <summary>Gets the merged-aircraft replacements of aircraft both sides hold, by aircraft line key.</summary>
    public IReadOnlyDictionary<string, JsonObject> AircraftEdits => _aircraftEdits;

    /// <summary>Runs the Scenario Editor's checks over a merged text, read through a session opened over it.</summary>
    /// <param name="request">The request the window opened over: the ARTCC and the scenario id.</param>
    /// <param name="merged">The merged scenario text.</param>
    /// <param name="inputs">What the checks read: navdata, the position ids and the ground layouts.</param>
    /// <returns>The checks' findings, empty for none.</returns>
    /// <exception cref="JsonException">The text is not a readable scenario.</exception>
    public static IReadOnlyList<ScenarioEditIssue> CheckMerged(ScenarioMergeRequest request, string merged, ScenarioEditInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(merged);
        ArgumentNullException.ThrowIfNull(inputs);
        var session = ScenarioEditSession.OverText(request.ArtccId, request.ScenarioId, merged);
        return ScenarioEditChecks.Run(session.Document.Scenario, inputs);
    }

    /// <summary>
    /// Opens the MERGED AIRCRAFT editor on one aircraft over the merge so far: every choice and every replacement, mine for
    /// each clash still open, and the aircraft from the side holding it when only one side does. The store is never written
    /// while it is open. Keep this stores a changed aircraft (a replacement when both sides hold it, an edit on its item row
    /// otherwise) and closes it; Cancel closes it with nothing changed. Every choice waits while it is open.
    /// </summary>
    /// <param name="itemKey">The aircraft line's key.</param>
    /// <param name="services">What the editor's aircraft view and live checks work through.</param>
    /// <returns>
    /// The editor; null while one is open or an Apply runs, and null with &lt;see cref="ApplyError"/&gt; set when the merge so far
    /// does not read as a scenario or the aircraft cannot be found in it.
    /// </returns>
    public ScenarioMergedAircraftViewModel? EditAircraft(string itemKey, ScenarioMergedAircraftServices services)
    {
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentNullException.ThrowIfNull(services);
        if ((MergedAircraft is not null) || _applying)
        {
            return null;
        }

        ApplyError = null;
        if (MergeSoFar(itemKey, out string text, out int index) is { } refusal)
        {
            ApplyError = refusal;
            return null;
        }

        try
        {
            var seed = new ScenarioMergedAircraftSeed(itemKey, Request.ArtccId, Request.ScenarioId, text, index);
            MergedAircraft = new ScenarioMergedAircraftViewModel(seed, services, editor => KeepMergedAircraft(itemKey, editor), CancelMergedAircraft);
            ApplyPickers(MergedAircraft.Aircraft);
        }
        catch (JsonException ex)
        {
            Log.LogWarning(ex, "Merge window: the merge so far does not read as a scenario; the merged aircraft editor stays closed");
            ApplyError = $"Can't edit the merged aircraft {CallsignOf(itemKey)}: {ex.Message}";
        }

        return MergedAircraft;
    }

    /// <summary>
    /// Applies the merge: builds the merged text, refuses it with &lt;see cref="ApplyError"/&gt; set when it does not read as a
    /// scenario, runs the checks and shows any findings, then writes it. A write that throws leaves the window open, its
    /// choices kept and the error on the footer. Ends &lt;see cref="Completion"/&gt; with the result once written.
    /// </summary>
    /// <param name="checks">The editor's checks on the merged scenario and the box that shows their findings.</param>
    /// <returns>What was written; null when Apply is not offered, the text was refused or the write failed.</returns>
    public async Task<ScenarioMergeResult?> ApplyAsync(ScenarioMergeChecks checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (!CanApply || _completion.Task.IsCompleted)
        {
            return null;
        }

        SetApplying(true);
        try
        {
            return await RunApplyAsync(checks).ConfigureAwait(true);
        }
        finally
        {
            SetApplying(false);
        }
    }

    private bool CanCancel() => !_applying;

    // Closes the MERGED AIRCRAFT editor first, then ends the window with nothing written.
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_applying)
        {
            return;
        }

        CloseMergedAircraft();
        if (_completion.TrySetResult(null))
        {
            Log.LogInformation("Merge window: cancelled; nothing written");
        }
    }

    partial void OnMergedAircraftChanged(ScenarioMergedAircraftViewModel? value) => LockChoices(value is not null);

    // Every choice command, and Previous/Next, waits while the MERGED AIRCRAFT editor is open.
    private void LockChoices(bool locked)
    {
        foreach (ScenarioMergeRowViewModel row in Rows)
        {
            row.Lock(locked);
        }

        foreach (ScenarioMergeSectionViewModel section in Sections.Append(AircraftSection))
        {
            section.Lock(locked);
        }

        foreach (ScenarioMergeAircraftLineViewModel line in AircraftLines)
        {
            line.Lock(locked);
        }

        EditRowCommand.NotifyCanExecuteChanged();
        EditEveryClashCommand.NotifyCanExecuteChanged();
        NextClashCommand.NotifyCanExecuteChanged();
        PreviousClashCommand.NotifyCanExecuteChanged();
        KeepMineForEveryClashCommand.NotifyCanExecuteChanged();
        KeepTheirsForEveryClashCommand.NotifyCanExecuteChanged();
        TakeMineForEverythingCommand.NotifyCanExecuteChanged();
        TakeTheirsForEverythingCommand.NotifyCanExecuteChanged();
    }

    private void SetApplying(bool applying)
    {
        _applying = applying;
        OnPropertyChanged(nameof(CanApply));
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void WatchEditors()
    {
        foreach (ScenarioMergeRowViewModel row in Rows)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ScenarioMergeRowViewModel.IsEditing))
                {
                    OnPropertyChanged(nameof(CanApply));
                }
            };
        }
    }

    private async Task<ScenarioMergeResult?> RunApplyAsync(ScenarioMergeChecks checks)
    {
        ApplyError = null;
        (string merged, string? unplaced) = MergedText();
        if ((unplaced ?? Refusal(merged)) is { } refusal)
        {
            ApplyError = refusal;
            if (Log.IsEnabled(LogLevel.Information))
            {
                Log.LogInformation("Merge window: Apply refused: {Reason}", refusal);
            }

            return null;
        }

        IReadOnlyList<ScenarioEditIssue>? warnings = await CheckedAsync(checks, merged).ConfigureAwait(true);
        if ((warnings is null) || !await WroteAsync(merged).ConfigureAwait(true))
        {
            return null;
        }

        var result = new ScenarioMergeResult(merged, warnings);
        if (!_completion.TrySetResult(result) && Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("Merge window: scenario {Id} was written after the window had already ended", Request.ScenarioId);
        }

        return result;
    }

    // The checks' findings, shown when there are any; null, with the footer's error set, when the checks or the box threw.
    private async Task<IReadOnlyList<ScenarioEditIssue>?> CheckedAsync(ScenarioMergeChecks checks, string merged)
    {
        try
        {
            IReadOnlyList<ScenarioEditIssue> warnings = checks.Run(merged);
            if (warnings.Count > 0)
            {
                await checks.ShowWarnings(warnings).ConfigureAwait(true);
            }

            return warnings;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Merge window: the checks at Apply failed");
            ApplyError = "Couldn't check the merged scenario: " + ex.Message;
            return null;
        }
    }

    private async Task<bool> WroteAsync(string merged)
    {
        try
        {
            await Request.Write(merged).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Merge window: writing the merged scenario {Id} failed", Request.ScenarioId);
            }

            ApplyError = "Couldn't write the merged scenario: " + ex.Message;
            return false;
        }
    }

    private void CloseMergedAircraft()
    {
        _editorRefusal = null;
        MergedAircraft?.Dispose();
        MergedAircraft = null;
    }

    // A Cancel on the editor that is open closes it; one on an editor already closed does nothing.
    private void CancelMergedAircraft(ScenarioMergedAircraftViewModel editor)
    {
        if (ReferenceEquals(MergedAircraft, editor))
        {
            CloseMergedAircraft();
        }
    }

    // A changed aircraft: a replacement for one both sides hold, an edit on the item row of one only one side holds. Refused,
    // with the editor left open, when the aircraft was removed in it; nothing on an editor already closed.
    private void KeepMergedAircraft(string key, ScenarioMergedAircraftViewModel editor)
    {
        if (!ReferenceEquals(MergedAircraft, editor))
        {
            return;
        }

        if (!editor.TryTakeEdited(out JsonObject? aircraft))
        {
            _editorRefusal = $"The merged aircraft {editor.Callsign} was removed in the editor; Cancel to close it.";
            ApplyError = _editorRefusal;
            return;
        }

        if ((_editorRefusal is not null) && (ApplyError == _editorRefusal))
        {
            ApplyError = null;
        }

        CloseMergedAircraft();
        if (aircraft is null)
        {
            return;
        }

        if (ItemRow(key) is { } item)
        {
            _choices[item.Path] = MergeChoice.Edit(aircraft);
        }
        else
        {
            _aircraftEdits[key] = aircraft;
        }

        Refresh();
    }

    // A per-field choice on an aircraft's row drops that aircraft's replacement.
    private void DropAircraftEdits(IEnumerable<ScenarioMergeRowViewModel> rows)
    {
        foreach (ScenarioMergeRowViewModel row in rows.Where(row => row.Section == AircraftSectionName))
        {
            if (row.ItemKey is { } key)
            {
                _ = _aircraftEdits.Remove(key);
            }
        }
    }

    // A row of an aircraft the merged-aircraft editor replaced: resolved, its ✎ pressed.
    private bool IsReplaced(ScenarioMergeRowViewModel row) =>
        (row.Section == AircraftSectionName) && (row.ItemKey is { } key) && _aircraftEdits.ContainsKey(key);

    private IEnumerable<ScenarioMergeRowViewModel> AircraftRows(string key) =>
        Rows.Where(row => (row.Section == AircraftSectionName) && (row.ItemKey == key));

    // The whole-aircraft row of an aircraft only one side holds; null for one both sides hold.
    private ScenarioMergeRowViewModel? ItemRow(string key) => AircraftRows(key).FirstOrDefault(row => row.IsItemRow);

    private static MergeChoice HeldSide(ScenarioMergeRowViewModel item) => item.Row.InMine ? MergeChoice.KeepMine : MergeChoice.KeepTheirs;

    private (string Text, string? Refusal) MergedText()
    {
        (string text, string? unplaced) = WithAircraftEdits(Plan.Apply(EngineChoices()));
        return (text, (unplaced is null) ? null : $"Can't apply: the merged aircraft {unplaced} could not be placed.");
    }

    // An aircraft's callsign in mine, else in theirs, else its key.
    private string CallsignOf(string key)
    {
        foreach (string side in (string[])[Request.Texts.Mine, Request.Texts.Theirs])
        {
            List<JsonObject> aircraft = Items(JsonNode.Parse(side, documentOptions: ParseOptions)!.AsObject(), AircraftKey);
            if (TextOf(ScenarioMergeItemKeys.Find(aircraft, key), "aircraftId") is { } callsign)
            {
                return callsign;
            }
        }

        return key;
    }

    // The choices the engine is handed: every choice, and mine for every row of a replaced aircraft, so that aircraft merges
    // as mine's element and is found by matching it; its replacement then overwrites it whole.
    private Dictionary<string, MergeChoice> EngineChoices()
    {
        var choices = new Dictionary<string, MergeChoice>(_choices, StringComparer.Ordinal);
        foreach (ScenarioMergeRowViewModel row in Rows.Where(IsReplaced))
        {
            choices[row.Path] = MergeChoice.KeepMine;
        }

        return choices;
    }

    // The merge so far for the editor on one aircraft and that aircraft's index in it; null when both are found, else why not.
    private string? MergeSoFar(string key, out string text, out int index)
    {
        Dictionary<string, MergeChoice> choices = EngineChoices();
        foreach (ScenarioMergeRowViewModel row in Rows.Where(row => row.IsOpen))
        {
            choices[row.Path] = MergeChoice.KeepMine;
        }

        if ((ItemRow(key) is { } item) && (_choices.GetValueOrDefault(item.Path)?.Kind != MergeChoiceKind.Edit))
        {
            choices[item.Path] = HeldSide(item);
        }

        (text, string? unplaced) = WithAircraftEdits(Plan.Apply(choices));
        index = IndexSoFar(choices, key, text);
        string callsign = CallsignOf(key);
        if (unplaced is not null)
        {
            return $"Can't edit the merged aircraft {callsign}: the merged aircraft {unplaced} could not be placed.";
        }

        return (index < 0) ? $"Can't edit the merged aircraft {callsign}: it could not be found." : null;
    }

    // The aircraft's index in the merge so far; -1 when it is not found. One only one side holds is found by merging a
    // stand-in on its item row, so the aircraft other one-sided rows drop before it are counted out.
    private int IndexSoFar(Dictionary<string, MergeChoice> choices, string key, string text)
    {
        if (ItemRow(key) is not { } item)
        {
            return Locate(JsonNode.Parse(text)![AircraftKey] as JsonArray ?? [], key) ?? -1;
        }

        string stamp = Guid.NewGuid().ToString("N");
        var marked = new Dictionary<string, MergeChoice>(choices, StringComparer.Ordinal)
        {
            [item.Path] = MergeChoice.Edit(new JsonObject { [PlaceMarker] = stamp }),
        };
        JsonArray aircraft = JsonNode.Parse(Plan.Apply(marked))![AircraftKey] as JsonArray ?? [];
        return aircraft.Select(node => TextOf(node as JsonObject, PlaceMarker)).ToList().FindIndex(text => text == stamp);
    }

    // The merged text with each replacement placed over its aircraft; the callsign (else the key) of one that cannot be placed.
    private (string Text, string? Unplaced) WithAircraftEdits(string merged)
    {
        if (_aircraftEdits.Count == 0)
        {
            return (merged, null);
        }

        JsonObject root = JsonNode.Parse(merged)!.AsObject();
        JsonArray aircraft = root[AircraftKey] as JsonArray ?? [];
        var places = new List<(int Index, JsonObject Edit)>();
        foreach ((string key, JsonObject edit) in _aircraftEdits)
        {
            if (Locate(aircraft, key) is not { } index)
            {
                return (merged, TextOf(edit, "aircraftId") ?? key);
            }

            places.Add((index, edit));
        }

        foreach ((int index, JsonObject edit) in places)
        {
            aircraft[index] = edit.DeepClone();
        }

        return (root.ToJsonString(MergedWriteOptions), null);
    }

    // The merged aircraft an item key of an aircraft both sides hold names: the one with its id, or for a positional key the
    // one at that index, since the engine pairs positional slots by index and keeps every slot both sides hold; null for none.
    private static int? Locate(JsonArray merged, string key)
    {
        if (ScenarioMergeItemKeys.Position(key) is { } position)
        {
            return (position < merged.Count) ? position : null;
        }

        int found = merged.Select(item => item as JsonObject).ToList().FindIndex(item => IdOf(item) == key);
        return (found >= 0) ? found : null;
    }

    // Null when the text reads as a scenario; else the footer's reason, naming the field that does not read when it can.
    private static string? Refusal(string merged)
    {
        if (LocalScenarioStore.CheckScenario(merged, out _) is { } reason)
        {
            return $"Can't apply: {reason}.";
        }

        try
        {
            _ = ScenarioDocument.Read(merged);
            return null;
        }
        catch (JsonException ex)
        {
            string field = FieldNamed(merged, ex.Path);
            return (AircraftNamed(merged, ex.Path) is { } callsign)
                ? $"Can't apply: {field} of {callsign} does not hold a value a scenario can read."
                : $"Can't apply: {field} does not hold a value a scenario can read.";
        }
    }

    // The aircraft a reader's path ("$.aircraft[2].spawnDelay") is a field of, by its callsign, else its id, else its place;
    // null for a path outside an aircraft.
    private static string? AircraftNamed(string merged, string? path)
    {
        Match match = AircraftFieldPath().Match(path ?? "");
        if (!match.Success)
        {
            return null;
        }

        int index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
        var aircraft = (JsonNode.Parse(merged)![AircraftKey] as JsonArray)?.ElementAtOrDefault(index) as JsonObject;
        return TextOf(aircraft, "aircraftId") ?? IdOf(aircraft) ?? "#" + index.ToString(CultureInfo.InvariantCulture);
    }

    // The editor's label of the member at a reader's path ("$.aircraft[2].spawnDelay"): the one row of the merged text planned
    // against itself without that member.
    internal static string FieldNamed(string merged, string? path)
    {
        const string Unnamed = "a field";
        JsonObject root = JsonNode.Parse(merged)!.AsObject();
        if ((path is null) || (Parent(root, path) is not { } found))
        {
            return Unnamed;
        }

        (JsonNode parent, object key) = found;
        switch (parent)
        {
            case JsonObject obj when key is string name:
                obj.Remove(name);
                break;
            case JsonArray list when key is int index:
                list.RemoveAt(index);
                break;
            default:
                return Unnamed;
        }

        return (ScenarioMerge.Plan(null, merged, root.ToJsonString()).Rows is [MergeRow first, ..]) ? first.Label : Unnamed;
    }

    // The node holding the path's last member and that member's name or index, or for a null list element the list's own
    // member; null when the path does not resolve.
    private static (JsonNode Parent, object Key)? Parent(JsonObject root, string path)
    {
        List<object> keys = [.. ReaderPathSegment().Matches(path).Select(Segment)];
        List<JsonNode> nodes = [root];
        for (int i = 0; i < (keys.Count - 1); i++)
        {
            if (Child(nodes[^1], keys[i]) is not { } next)
            {
                return null;
            }

            nodes.Add(next);
        }

        if (keys.Count == 0)
        {
            return null;
        }

        JsonNode parent = nodes[^1];
        if ((Child(parent, keys[^1]) is not null) || HoldsNull(parent, keys[^1]))
        {
            return (parent, keys[^1]);
        }

        bool nullElement = (parent is JsonArray list) && (keys[^1] is int index) && (index < list.Count) && (nodes.Count > 1);
        return nullElement ? (nodes[^2], keys[^2]) : null;
    }

    private static object Segment(Match match) =>
        match.Groups["index"].Success ? int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture)
        : match.Groups["name"].Success ? match.Groups["name"].Value
        : match.Groups["quoted"].Value;

    private static JsonNode? Child(JsonNode node, object key) =>
        (node, key) switch
        {
            (JsonObject obj, string name) => obj[name],
            (JsonArray list, int index) when index < list.Count => list[index],
            _ => null,
        };

    private static bool HoldsNull(JsonNode node, object key) => (node is JsonObject obj) && (key is string name) && obj.ContainsKey(name);

    [GeneratedRegex(@"^\$\.aircraft\[(?<index>\d+)\][.\[]", RegexOptions.CultureInvariant)]
    private static partial Regex AircraftFieldPath();

    [GeneratedRegex(@"\.(?<name>[^.\[]+)|\[(?<index>\d+)\]|\['(?<quoted>[^']*)'\]", RegexOptions.CultureInvariant)]
    private static partial Regex ReaderPathSegment();
}
