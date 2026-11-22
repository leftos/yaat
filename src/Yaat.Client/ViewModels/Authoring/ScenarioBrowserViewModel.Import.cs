using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

// Import: several files at once, each read, checked and imported on a pool thread. A file whose id is free keeps it; a
// clash asks New id / Keep id / Skip, and an answer applied to the remaining files answers the later clashes of the
// same kind (working copy, server scenario or both); a clash of another kind asks again. A file that cannot be read,
// is rejected, or names another ARTCC is a notice.
public sealed partial class ScenarioBrowserViewModel
{
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

        ImportIdChoice choice = await ChooseImportIdAsync(file, conflict, remaining, run).ConfigureAwait(false);
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
    // of the same clash kind.
    private async Task<ImportIdChoice> ChooseImportIdAsync(ImportFile file, ScenarioIdConflict? conflict, int remaining, ImportRun run)
    {
        if (conflict is not { } kind)
        {
            return ImportIdChoice.NewId;
        }

        if (kind == ScenarioIdConflict.None)
        {
            return ImportIdChoice.KeepId;
        }

        if (run.Applied.TryGetValue(kind, out ImportIdChoice applied))
        {
            return applied;
        }

        var question = new ImportConflictQuestion(Path.GetFileName(file.Path), file.Id!, file.Name ?? file.Id!, kind, remaining);
        var skip = new ImportConflictAnswer(ImportIdChoice.Skip, ApplyToRemaining: false);
        ImportConflictAnswer answer = await PromptAsync(file.Label, () => _services.Prompts.AskImportConflictAsync(question), skip)
            .ConfigureAwait(false);
        if (answer.ApplyToRemaining)
        {
            run.Applied[kind] = answer.Choice;
        }

        return answer.Choice;
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
