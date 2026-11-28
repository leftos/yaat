using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

// Import: several files at once, each read, checked and imported on a pool thread. A file whose id is free keeps it; a
// clash asks Compare… / New id / Keep id / Skip, and an answer applied to the remaining files answers the later clashes
// of the same kind (working copy, server scenario or both); a clash of another kind asks again. Compare… opens the merge
// window two-way over the scenario holding the id (mine) and the file (theirs), then asks the clash again; it is never
// applied to the remaining files. A file that cannot be read, is rejected, or names another ARTCC is a notice.
public sealed partial class ScenarioBrowserViewModel
{
    /// <summary>The merge window's note when an import is compared with the scenario holding its id.</summary>
    // How a merged text is written back when its id takes the stored letter case: as the store rewrites an id.
    private static readonly JsonSerializerOptions StoredIdOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const string ImportCompareTwoWayNote = "The file and this scenario share no saved base version, so every difference is yours to choose.";

    /// <summary>Imports the scenario files the user picks into the shown ARTCC and selects the last one imported.</summary>
    [RelayCommand(CanExecute = nameof(CanUseArtcc))]
    private async Task ImportAsync()
    {
        string artcc = _artcc!;
        IReadOnlyList<string> paths = await PromptAsync<IReadOnlyList<string>>("Import", () => _services.Prompts.PickImportFilesAsync(), [])
            .ConfigureAwait(false);
        if (paths.Count == 0)
        {
            return;
        }

        var run = new ImportRun();
        for (int i = 0; i < paths.Count; i++)
        {
            await ImportFileAsync(artcc, paths[i], paths.Count - i - 1, run).ConfigureAwait(false);
        }

        await ReloadAsync(artcc, run.LastId is { } id ? new RowSelection(ScenarioBrowserTab.Local, id) : null).ConfigureAwait(false);
    }

    private async Task ImportFileAsync(string artcc, string path, int remaining, ImportRun run)
    {
        (bool read, ImportFile? file) = await TryStoreAsync(Path.GetFileName(path), "reading", () => ReadImportFile(path)).ConfigureAwait(false);
        if (!read || (file is null))
        {
            return;
        }

        LocalScenarioStore store = _services.Store;
        (bool checkedId, ScenarioIdConflict? conflict) = await TryStoreAsync(file.Label, "checking the id of", () => ConflictOf(store, artcc, file))
            .ConfigureAwait(false);
        if (!checkedId)
        {
            return;
        }

        (ImportIdChoice choice, conflict) = await ChooseImportIdAsync(artcc, file, conflict, remaining, run).ConfigureAwait(false);
        if (!await MayReplaceAsync(artcc, file, conflict, choice).ConfigureAwait(false))
        {
            return;
        }

        (_, ImportResult? result) = await TryStoreAsync(file.Label, "importing", () => store.ImportScenario(artcc, file.Text, choice, NextId))
            .ConfigureAwait(false);
        if (result is not null)
        {
            run.LastId = result.Id ?? run.LastId;
            await OnUiAsync(() => NoteImport(artcc, file, result)).ConfigureAwait(false);
        }
    }

    // Keeping the id of a file whose working copy exists replaces that copy: the ReplaceGuard is asked first (the editor
    // asks when it has the copy open with unsaved edits); false skips this file only.
    private async Task<bool> MayReplaceAsync(string artcc, ImportFile file, ScenarioIdConflict? conflict, ImportIdChoice choice)
    {
        if (
            (choice != ImportIdChoice.KeepId)
            || (conflict is not (ScenarioIdConflict.WorkingCopy or ScenarioIdConflict.Both))
            || (file.Id is not { } id)
        )
        {
            return true;
        }

        return await PromptAsync(file.Label, () => ReplaceGuard(artcc, id), false).ConfigureAwait(false);
    }

    private static ImportFile ReadImportFile(string path)
    {
        string text = File.ReadAllText(path);
        (string? id, string? name) = PeekIdAndName(text);
        return new ImportFile(path, text, id, name);
    }

    // What holds the file's id; null when the file has no id that can name a file, so it takes a new one.
    private static ScenarioIdConflict? ConflictOf(LocalScenarioStore store, string artcc, ImportFile file) =>
        ((file.Id is not null) && LocalScenarioStore.IsValidId(file.Id)) ? store.FindIdConflict(artcc, file.Id) : null;

    // The file's id and name when it is a JSON object holding them as text; the store judges the rest.
    private static (string? Id, string? Name) PeekIdAndName(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject scenario ? (TextField(scenario, "id"), TextField(scenario, "name")) : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? TextField(JsonObject scenario, string key) =>
        ((scenario[key] is { } node) && (node.GetValueKind() == JsonValueKind.String)) ? node.GetValue<string>() : null;

    // A free id is kept; an id that cannot name a file gets a new one; a clash is asked, or answered by an applied answer
    // of the same clash kind. A Compare answer opens the merge window, then asks again, with what holds the id as it is
    // after the merge; the clash kind returned is that one.
    private async Task<(ImportIdChoice Choice, ScenarioIdConflict? Conflict)> ChooseImportIdAsync(
        string artcc,
        ImportFile file,
        ScenarioIdConflict? conflict,
        int remaining,
        ImportRun run
    )
    {
        if (conflict is not { } kind)
        {
            return (ImportIdChoice.NewId, conflict);
        }

        if (kind == ScenarioIdConflict.None)
        {
            return (ImportIdChoice.KeepId, conflict);
        }

        if (run.Applied.TryGetValue(kind, out ImportIdChoice applied))
        {
            return (applied, kind);
        }

        while (true)
        {
            var question = new ImportConflictQuestion(Path.GetFileName(file.Path), file.Id!, file.Name ?? file.Id!, kind, remaining);
            var skip = new ImportConflictAnswer(ImportIdChoice.Skip, ApplyToRemaining: false, Compare: false);
            ImportConflictAnswer answer = await PromptAsync(file.Label, () => _services.Prompts.AskImportConflictAsync(question), skip)
                .ConfigureAwait(false);
            if (!answer.Compare)
            {
                if (answer.ApplyToRemaining)
                {
                    run.Applied[kind] = answer.Choice;
                }

                return (answer.Choice, kind);
            }

            kind = await CompareImportAsync(artcc, file, kind, run).ConfigureAwait(false);
        }
    }

    // Compare…: the merge window over the scenario holding the id (its working copy, after the MergeGuard settles an open
    // copy's unsaved edits; else the server version) and the file, two-way, under the id as stored. Returns what holds the
    // id afterwards: an applied merge over a server-only id makes its working copy.
    private async Task<ScenarioIdConflict> CompareImportAsync(string artcc, ImportFile file, ScenarioIdConflict kind, ImportRun run)
    {
        bool hasCopy = kind is ScenarioIdConflict.WorkingCopy or ScenarioIdConflict.Both;
        if (await CompareIdAsync(artcc, file, hasCopy).ConfigureAwait(false) is not { } id)
        {
            return kind;
        }

        LocalScenarioStore store = _services.Store;
        (bool read, string? mine) = await TryStoreAsync(
                file.Label,
                "reading the scenario to compare with",
                () => hasCopy ? store.ReadWorkingCopyText(artcc, id) : store.ReadMirrorText(artcc, id)
            )
            .ConfigureAwait(false);
        if (!read || (mine is null))
        {
            return kind;
        }

        ScenarioMergeRequest request = ImportCompareRequest(artcc, file, id, new ScenarioMergeTexts(null, mine, file.Text), hasCopy);
        if (await PromptAsync(file.Label, () => _services.Prompts.ShowMergeAsync(request), null).ConfigureAwait(false) is null)
        {
            return kind;
        }

        run.LastId = id;
        (_, ScenarioIdConflict? now) = await TryStoreAsync(file.Label, "checking the id of", () => ConflictOf(store, artcc, file))
            .ConfigureAwait(false);
        return now ?? (hasCopy ? kind : ScenarioIdConflict.Both);
    }

    // The id as the store holds it, matched ignoring case as the clash was (a working copy's first, else a mirror's), once
    // the MergeGuard lets a working copy be read; null when the id cannot be read or the guard says no.
    private async Task<string?> CompareIdAsync(string artcc, ImportFile file, bool hasCopy)
    {
        LocalScenarioStore store = _services.Store;
        (bool found, string? id) = await TryStoreAsync(file.Label, "reading the id of", () => StoredIdOf(store, artcc, file.Id!))
            .ConfigureAwait(false);
        if (!found || (id is null))
        {
            return null;
        }

        return (!hasCopy || await PromptAsync(file.Label, () => MergeGuard(artcc, id), false).ConfigureAwait(false)) ? id : null;
    }

    private static string StoredIdOf(LocalScenarioStore store, string artcc, string id)
    {
        ArtccStoreListing listing = store.Load(artcc);
        return SameId(listing.WorkingCopies.Select(c => c.Id), id) ?? SameId(listing.Mirrors.Select(m => m.Mirror.Id), id) ?? id;
    }

    // The id equal to id, else the first equal ignoring case, else null.
    private static string? SameId(IEnumerable<string> ids, string id)
    {
        List<string> matches = [.. ids.Where(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase))];
        return matches.Contains(id, StringComparer.Ordinal) ? id : matches.FirstOrDefault();
    }

    private ScenarioMergeRequest ImportCompareRequest(string artcc, ImportFile file, string id, ScenarioMergeTexts texts, bool hasCopy)
    {
        var captions = new ScenarioMergeCaptions(
            $"Compare import: {file.Label}",
            hasCopy ? "working copy" : "server version",
            "imported file",
            "none",
            ImportCompareTwoWayNote
        );
        return new ScenarioMergeRequest(artcc, id, texts, captions, merged => WriteImportMergeAsync(artcc, file, id, hasCopy, merged));
    }

    // Apply: onto the working copy, which an open editor then reloads; or, over a server-only id, as that server
    // scenario's working copy (as Keep id makes one, with its other-ARTCC notice). A merge whose id is not the stored one,
    // or a refused write, throws to the merge window, which shows it; nothing is written.
    private async Task WriteImportMergeAsync(string artcc, ImportFile file, string id, bool hasCopy, string merged)
    {
        if (!string.Equals(PeekIdAndName(merged).Id, id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Not written: the merged scenario's id is not {id}");
        }

        LocalScenarioStore store = _services.Store;
        if (!hasCopy)
        {
            ImportResult result = await Task.Run(() => store.ImportScenario(artcc, merged, ImportIdChoice.KeepId, NextId)).ConfigureAwait(false);
            if (result.Outcome != ImportOutcome.Imported)
            {
                throw new InvalidOperationException($"Not written: {result.Reason}");
            }

            if (result.ArtccMismatch)
            {
                await OnUiAsync(() => AddNotice($"{file.Label}: its artccId names another ARTCC; imported into {artcc}")).ConfigureAwait(false);
            }

            return;
        }

        string text = WithStoredId(merged, id);
        await Task.Run(() => store.SaveWorkingCopy(artcc, id, text)).ConfigureAwait(false);
        await OnUiAsync(() => WorkingCopyReplaced?.Invoke(this, new ScenarioWorkingCopyReplacedEventArgs(artcc, id))).ConfigureAwait(false);
    }

    // The merged text as it is when its id is the stored one, else with the stored id written in (as Keep id does), so a
    // copy never holds its id in another letter case. The text was checked to be a scenario object with that id.
    private static string WithStoredId(string merged, string id)
    {
        if (string.Equals(PeekIdAndName(merged).Id, id, StringComparison.Ordinal))
        {
            return merged;
        }

        JsonObject scenario = JsonNode.Parse(merged)!.AsObject();
        scenario["id"] = id;
        return scenario.ToJsonString(StoredIdOptions);
    }

    private void NoteImport(string artcc, ImportFile file, ImportResult result)
    {
        if ((result.Outcome == ImportOutcome.Imported) && result.Overwrote && (result.Id is { } id))
        {
            WorkingCopyReplaced?.Invoke(this, new ScenarioWorkingCopyReplacedEventArgs(artcc, id));
        }

        if (result.Outcome == ImportOutcome.Rejected)
        {
            AddNotice($"{file.Label}: not imported: {result.Reason}");
        }
        else if ((result.Outcome == ImportOutcome.Imported) && result.ArtccMismatch)
        {
            AddNotice($"{file.Label}: its artccId names another ARTCC; imported into {artcc}");
        }
    }

    // One picked file: its path, text, and the id and name it holds when it is a scenario object.
    private sealed record ImportFile(string Path, string Text, string? Id, string? Name)
    {
        public string Label => Name ?? System.IO.Path.GetFileName(Path);
    }

    // The state one import carries from file to file.
    private sealed class ImportRun
    {
        public Dictionary<ScenarioIdConflict, ImportIdChoice> Applied { get; } = [];

        public string? LastId { get; set; }
    }
}
