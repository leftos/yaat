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

    // Merged-aircraft replacements by aircraft line key, swapped into the merged text by position or id.
    private readonly Dictionary<string, JsonObject> _aircraftEdits = new(StringComparer.Ordinal);

    private readonly TaskCompletionSource<ScenarioMergeResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _applying;

    /// <summary>The footer's error line: why the last Apply refused the merged text or could not check or write it; null for none.</summary>
    [ObservableProperty]
    private string? _applyError;

    /// <summary>Gets the window's outcome: the applied result, or null once the author cancels. The window closes when it ends.</summary>
    public Task<ScenarioMergeResult?> Completion => _completion.Task;

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

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!_applying && _completion.TrySetResult(null))
        {
            Log.LogInformation("Merge window: cancelled; nothing written");
        }
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
        string merged = MergedText();
        if (Refusal(merged) is { } refusal)
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

    // The choices, with mine for the open rows of an aircraft the merged-aircraft editor replaced, then the replacements.
    private string MergedText()
    {
        var choices = new Dictionary<string, MergeChoice>(_choices, StringComparer.Ordinal);
        foreach (ScenarioMergeRowViewModel row in Rows.Where(row => row.IsOpen && (row.ItemKey is { } key) && _aircraftEdits.ContainsKey(key)))
        {
            if (row.Section == AircraftSectionName)
            {
                choices[row.Path] = MergeChoice.KeepMine;
            }
        }

        string merged = Plan.Apply(choices);
        return (_aircraftEdits.Count == 0) ? merged : WithAircraftEdits(merged);
    }

    private string WithAircraftEdits(string merged)
    {
        JsonObject root = JsonNode.Parse(merged)!.AsObject();
        if (root[AircraftKey] is not JsonArray aircraft)
        {
            return merged;
        }

        foreach ((string key, JsonObject edit) in _aircraftEdits)
        {
            int index = IndexOfKey(aircraft, key);
            if (index >= 0)
            {
                aircraft[index] = edit.DeepClone();
            }
        }

        return root.ToJsonString(MergedWriteOptions);
    }

    private static int IndexOfKey(JsonArray aircraft, string key)
    {
        if (key.StartsWith('#') && int.TryParse(key[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return (index < aircraft.Count) ? index : -1;
        }

        List<JsonObject?> items = [.. aircraft.Select(item => item as JsonObject)];
        return items.FindIndex(item => IdOf(item) == key);
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
            return $"Can't apply: {FieldNamed(merged, ex.Path)} does not hold a value a scenario can read.";
        }
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

    [GeneratedRegex(@"\.(?<name>[^.\[]+)|\[(?<index>\d+)\]|\['(?<quoted>[^']*)'\]", RegexOptions.CultureInvariant)]
    private static partial Regex ReaderPathSegment();
}
