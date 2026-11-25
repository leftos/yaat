using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

// The toolbar and context-menu commands. Each reads its row and ARTCC on the UI thread when it starts, runs its store
// and file work on a pool thread, and asks its prompts and applies its results through the post. Every failure, a
// prompt's included, becomes a notice naming the scenario; none throws.
public sealed partial class ScenarioBrowserViewModel
{
    /// <summary>The name a new scenario starts with.</summary>
    public const string NewScenarioName = "New scenario";

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>Re-reads whether a scenario can be tested in the room; the window calls it when that changes.</summary>
    public void RefreshTestInRoom() => TestInRoomCommand.NotifyCanExecuteChanged();

    private bool CanUseArtcc() => _artcc is not null;

    private bool CanDuplicate() => SelectedRow is { Kind: ScenarioRowKind.WorkingCopy, IsCorrupt: false };

    private bool CanDelete() => SelectedRow is { Kind: ScenarioRowKind.WorkingCopy };

    private bool CanSaveCopy() => SelectedRow is not null;

    private bool CanStartWorkingCopy() => SelectedRow is { Kind: ScenarioRowKind.Server, IsCorrupt: false, HasWorkingCopy: false };

    private bool CanOpen() => (SelectedRow is { IsCorrupt: false } row) && !HasCorruptCopy(row);

    private bool CanTestInRoom() => (SelectedRow is { Kind: ScenarioRowKind.WorkingCopy, IsCorrupt: false }) && _services.CanLoadInRoom();

    // A server row whose working copy is corrupt, or not listed, opens nothing.
    private bool HasCorruptCopy(ScenarioBrowserRowViewModel row) =>
        (row.Kind == ScenarioRowKind.Server) && row.HasWorkingCopy && (LocalRows.FirstOrDefault(r => r.Id == row.Id) is not { IsCorrupt: false });

    /// <summary>Creates a local-only scenario named <see cref="NewScenarioName"/> with a fresh id and selects it.</summary>
    [RelayCommand(CanExecute = nameof(CanUseArtcc))]
    private async Task NewAsync()
    {
        string artcc = _artcc!;
        string id = NextId();
        string json = new JsonObject
        {
            ["id"] = id,
            ["name"] = NewScenarioName,
            ["artccId"] = artcc,
        }.ToJsonString(IndentedJson);
        LocalScenarioStore store = _services.Store;
        if (await TryDoAsync(NewScenarioName, "creating", () => store.CreateLocalScenario(artcc, json, id)).ConfigureAwait(false))
        {
            await ReloadAsync(artcc, new RowSelection(ScenarioBrowserTab.Local, id)).ConfigureAwait(false);
        }
    }

    /// <summary>Copies the selected working copy under a fresh id as a local-only copy and selects it.</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private async Task DuplicateAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string artcc = _artcc!;
        string id = NextId();
        LocalScenarioStore store = _services.Store;
        if (await TryDoAsync(row.Name, "duplicating", () => store.DuplicateWorkingCopy(artcc, row.Id, id)).ConfigureAwait(false))
        {
            await ReloadAsync(artcc, new RowSelection(ScenarioBrowserTab.Local, id)).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the selected working copy once the user confirms, then raises <see cref="WorkingCopyDeleted"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string artcc = _artcc!;
        if (!await PromptAsync(row.Name, () => _services.Prompts.ConfirmDeleteAsync(row), false).ConfigureAwait(false))
        {
            return;
        }

        LocalScenarioStore store = _services.Store;
        if (await TryDoAsync(row.Name, "deleting", () => store.DeleteWorkingCopy(artcc, row.Id)).ConfigureAwait(false))
        {
            await OnUiAsync(() => WorkingCopyDeleted?.Invoke(this, new ScenarioWorkingCopyDeletedEventArgs(artcc, row.Id))).ConfigureAwait(false);
            await ReloadAsync(artcc, null).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the selected scenario's stored file, byte for byte, where the user picks.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveCopy))]
    private async Task SaveCopyAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string artcc = _artcc!;
        string suggested = ScenarioExportViewModel.FileName(row.Name, row.Id);
        string? target = await PromptAsync<string?>(row.Name, () => _services.Prompts.PickSavePathAsync(suggested), null).ConfigureAwait(false);
        if (target is not null)
        {
            await TryDoAsync(row.Name, "saving a copy", () => File.WriteAllBytes(target, File.ReadAllBytes(StoredPath(artcc, row))))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Starts the selected server scenario's working copy and selects it on the Local tab.</summary>
    [RelayCommand(CanExecute = nameof(CanStartWorkingCopy))]
    private async Task StartWorkingCopyAsync()
    {
        if (SelectedRow is { } row)
        {
            await CreateWorkingCopyAsync(_artcc!, row).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Raises <see cref="OpenRequested"/> for the selected working copy. A server row opens its working copy, started
    /// first when there is none, and selects it on the Local tab; a corrupt row or copy opens nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if ((SelectedRow is not { } row) || !CanOpen())
        {
            return;
        }

        string artcc = _artcc!;
        if (row.Kind == ScenarioRowKind.Server)
        {
            bool ready = row.HasWorkingCopy
                ? TrySelect(new RowSelection(ScenarioBrowserTab.Local, row.Id))
                : await CreateWorkingCopyAsync(artcc, row).ConfigureAwait(false);
            if (!ready)
            {
                return;
            }
        }

        await OnUiAsync(() => OpenRequested?.Invoke(this, new ScenarioOpenRequestedEventArgs(artcc, row.Id, ScenarioRowKind.WorkingCopy)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the selected working copy into the room through the room seam. The <see cref="ExportGuard"/> is asked first,
    /// so an open copy's unsaved edits are saved (or dropped) and the room runs what the editor shows; then, while the room
    /// runs a scenario, the user is asked whether to replace it, and a no leaves the room alone.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestInRoom))]
    private async Task TestInRoomAsync()
    {
        if (SelectedRow is not { Kind: ScenarioRowKind.WorkingCopy, IsCorrupt: false } row)
        {
            return;
        }

        string artcc = _artcc!;
        if (!await PromptAsync(row.Name, () => ExportGuard(artcc, row.Id), false).ConfigureAwait(false))
        {
            return;
        }

        LocalScenarioStore store = _services.Store;
        (bool read, string? json) = await TryStoreAsync(row.Name, "reading", () => store.ReadWorkingCopyText(artcc, row.Id)).ConfigureAwait(false);
        if (!read || (json is null) || !await PromptAsync(row.Name, () => MayReplaceRunningAsync(row), false).ConfigureAwait(false))
        {
            return;
        }

        await PromptAsync(row.Name, () => LoadInRoomAsync(json, row), false).ConfigureAwait(false);
    }

    // True at once with no scenario running; else the user's answer to replacing it.
    private Task<bool> MayReplaceRunningAsync(ScenarioBrowserRowViewModel row) =>
        _services.RoomHasScenario() ? _services.Prompts.ConfirmReplaceRunningScenarioAsync(row.Name) : Task.FromResult(true);

    private async Task<bool> LoadInRoomAsync(string json, ScenarioBrowserRowViewModel row)
    {
        await _services.LoadInRoom(json, row.Name, row.OriginId).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> CreateWorkingCopyAsync(string artcc, ScenarioBrowserRowViewModel row)
    {
        LocalScenarioStore store = _services.Store;
        if (!await TryDoAsync(row.Name, "starting a working copy", () => store.CreateWorkingCopy(artcc, row.Id)).ConfigureAwait(false))
        {
            return false;
        }

        await ReloadAsync(artcc, new RowSelection(ScenarioBrowserTab.Local, row.Id)).ConfigureAwait(false);
        return true;
    }

    private string NextId() => ScenarioIdGenerator.Create(_services.Clock.GetUtcNow(), _services.Random);

    private string StoredPath(string artcc, ScenarioBrowserRowViewModel row) =>
        row.Kind == ScenarioRowKind.WorkingCopy ? _services.Store.GetWorkingCopyPath(artcc, row.Id) : _services.Store.GetMirrorPath(artcc, row.Id);

    // Runs the store or file work on a pool thread; a failure is logged and posted as a notice naming the scenario.
    private async Task<(bool Ok, T? Value)> TryStoreAsync<T>(string name, string what, Func<T> work)
    {
        try
        {
            return (true, await Task.Run(work).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            LogFailure(ex, what + " " + name);
            await OnUiAsync(() => AddNotice($"{name}: {ex.Message}")).ConfigureAwait(false);
            return (false, default);
        }
    }

    private async Task<bool> TryDoAsync(string name, string what, Action work)
    {
        (bool ok, _) = await TryStoreAsync(
                name,
                what,
                () =>
                {
                    work();
                    return true;
                }
            )
            .ConfigureAwait(false);
        return ok;
    }

    // Runs a prompt (or other UI work) through the post; a failure is a notice naming the scenario and gives the fallback.
    private async Task<T> PromptAsync<T>(string name, Func<Task<T>> prompt, T fallback)
    {
        try
        {
            return await OnUiAsync(prompt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "asking about " + name);
            await OnUiAsync(() => AddNotice($"{name}: {ex.Message}")).ConfigureAwait(false);
            return fallback;
        }
    }

    private void NotifyCommands()
    {
        NewCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        SaveCopyCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        StartWorkingCopyCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        TestInRoomCommand.NotifyCanExecuteChanged();
        ExportJsonCommand.NotifyCanExecuteChanged();
        ExportCleanJsonCommand.NotifyCanExecuteChanged();
        ExportAllUnexportedCommand.NotifyCanExecuteChanged();
    }
}
