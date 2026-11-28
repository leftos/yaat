using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

// Merge server changes: a working copy whose server scenario changed opens the merge window over the copy (mine), the
// server version (theirs) and the version the copy started from (the base, when kept). The editor's MergeGuard settles
// the open copy's unsaved edits first. Apply writes the merge onto the server version, so the badge clears; Cancel
// writes nothing. The window's Fresh copy beside makes a local-only copy of the server version.
public sealed partial class ScenarioBrowserViewModel
{
    /// <summary>The merge window's note when the working copy has no base version kept.</summary>
    public const string MergeTwoWayNote = "This copy has no saved base version, so every difference is yours to choose.";

    /// <summary>The notice Compare with… posts when the ARTCC has no working copy to be mine.</summary>
    public const string CompareNeedsCopyNotice = "Compare needs a working copy on the Mine side: start one first.";

    /// <summary>The merge window's note when Compare's two scenarios share no base.</summary>
    public const string CompareTwoWayNote = "These two scenarios share no saved base version, so every difference is yours to choose.";

    private bool CanResolveServerChanges() =>
        (SelectedRow is { Kind: ScenarioRowKind.WorkingCopy, IsCorrupt: false } row) && row.Badges.Contains(ScenarioBadge.ServerChanged);

    /// <summary>
    /// Opens the merge window over the selected server-changed working copy, once the <see cref="MergeGuard"/> lets it
    /// read the copy. Apply writes the merged text onto the server version and the copy stops reading server-changed;
    /// a failed write throws to the merge window, which shows it and stays open; nothing is written.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanResolveServerChanges))]
    private async Task ResolveServerChangesAsync()
    {
        if (!CanResolveServerChanges() || (SelectedRow is not { } row))
        {
            return;
        }

        string artcc = _artcc!;
        if (!await PromptAsync(row.Name, () => MergeGuard(artcc, row.Id), false).ConfigureAwait(false))
        {
            return;
        }

        LocalScenarioStore store = _services.Store;
        string mirrorId = row.OriginId ?? row.Id;
        (bool read, ScenarioMergeTexts? texts) = await TryStoreAsync(
                row.Name,
                "reading the versions of",
                () =>
                    new ScenarioMergeTexts(
                        store.ReadBaseText(artcc, row.Id),
                        store.ReadWorkingCopyText(artcc, row.Id),
                        store.ReadMirrorText(artcc, mirrorId)
                    )
            )
            .ConfigureAwait(false);
        if (!read || (texts is null))
        {
            return;
        }

        ScenarioMergeRequest request = ServerChangesRequest(artcc, row, mirrorId, texts);
        await PromptAsync(row.Name, () => _services.Prompts.ShowMergeAsync(request), null).ConfigureAwait(false);
    }

    private ScenarioMergeRequest ServerChangesRequest(string artcc, ScenarioBrowserRowViewModel row, string mirrorId, ScenarioMergeTexts texts)
    {
        var captions = new ScenarioMergeCaptions(
            $"Merge server changes: {row.Name}",
            "working copy",
            "server version",
            "the server version this copy started from",
            MergeTwoWayNote
        );
        return new ScenarioMergeRequest(artcc, row.Id, texts, captions, merged => WriteServerMergeAsync(artcc, row.Id, texts.Theirs, merged))
        {
            FreshCopy = () => FreshCopyBesideAsync(artcc, mirrorId),
        };
    }

    // Writes the merge onto the server version; then the rows are re-read and an editor with the copy open reloads it. A
    // copy the store refuses (one with no server origin) throws to the merge window, which shows the failure and stays
    // open; nothing is written.
    private async Task WriteServerMergeAsync(string artcc, string id, string theirs, string merged)
    {
        LocalScenarioStore store = _services.Store;
        string version = LocalScenarioStore.ComputeVersion(theirs);
        await Task.Run(() => store.ApplyMerge(artcc, id, merged, theirs, version)).ConfigureAwait(false);
        await ReloadAsync(artcc, new RowSelection(ScenarioBrowserTab.Local, id)).ConfigureAwait(false);
        await OnUiAsync(() => WorkingCopyReplaced?.Invoke(this, new ScenarioWorkingCopyReplacedEventArgs(artcc, id))).ConfigureAwait(false);
    }

    /// <summary>
    /// Compare with…: the picker over any two scenarios of the shown ARTCC, the selected row preselected; mine is a working
    /// copy. Once the <see cref="MergeGuard"/> lets theirs (when it is a working copy) and then mine be read, the merge
    /// window opens over the pair. Mine with its own server scenario is Merge server changes by another door: three-way
    /// over mine's stored base, and Apply writes onto the server version, so the base moves forward and the badge clears.
    /// Every other pair is two-way and Apply writes mine under its stored id. With no working copy it posts
    /// <see cref="CompareNeedsCopyNotice"/> and opens nothing.
    /// </summary>
    [RelayCommand]
    private async Task CompareAsync()
    {
        if (_artcc is not { } artcc)
        {
            return;
        }

        var picker = new ScenarioComparePickerViewModel(LocalRows, ServerRows, SelectedRow);
        if (picker.MineOptions.Count == 0)
        {
            AddNotice(CompareNeedsCopyNotice);
            return;
        }

        ScenarioComparePick? pick = await PromptAsync("Compare", () => _services.Prompts.PickCompareAsync(picker), null).ConfigureAwait(false);
        if (pick is not null)
        {
            await CompareOverAsync(artcc, pick).ConfigureAwait(false);
        }
    }

    // A picked pair: theirs' file asked for when it is the file entry, the guards asked, then the merge window.
    private async Task CompareOverAsync(string artcc, ScenarioComparePick pick)
    {
        (bool go, string? path) = await CompareFileAsync(pick).ConfigureAwait(false);
        if (!go || !await CompareGuardsAsync(artcc, pick).ConfigureAwait(false))
        {
            return;
        }

        LocalScenarioStore store = _services.Store;
        (bool read, ScenarioMergeTexts? texts) = await TryStoreAsync(
                pick.Mine.Name,
                "reading the versions to compare of",
                () => ScenarioComparePickerViewModel.ReadTexts(store, artcc, pick, path)
            )
            .ConfigureAwait(false);
        if (!read || (texts is null))
        {
            return;
        }

        string theirsName = (path is null) ? pick.Theirs.Name : Path.GetFileName(path);
        if ((path is not null) && NamesOtherArtcc(texts.Theirs, artcc))
        {
            await OnUiAsync(() => AddNotice(OtherArtccNotice(theirsName, "compared in", artcc))).ConfigureAwait(false);
        }

        ScenarioMergeRequest request = CompareRequest(artcc, pick, theirsName, texts);
        await PromptAsync(pick.Mine.Name, () => _services.Prompts.ShowMergeAsync(request), null).ConfigureAwait(false);
    }

    // The file theirs is, asked with the import picker when theirs is the file entry: go on with its path (null when theirs
    // is no file), or stop when the author cancelled the picker.
    private async Task<(bool Go, string? Path)> CompareFileAsync(ScenarioComparePick pick)
    {
        if (pick.Theirs.Kind != ScenarioCompareSideKind.File)
        {
            return (true, null);
        }

        IReadOnlyList<string> paths = await PromptAsync<IReadOnlyList<string>>("Compare", () => _services.Prompts.PickImportFilesAsync(), [])
            .ConfigureAwait(false);
        return (paths.Count > 0) ? (true, paths[0]) : (false, null);
    }

    // The MergeGuard over theirs when it is a working copy, then over mine (mine last, since the editor records the copy a
    // merge reloads at each ask); false when either says no.
    private async Task<bool> CompareGuardsAsync(string artcc, ScenarioComparePick pick)
    {
        if (
            (pick.Theirs.Kind == ScenarioCompareSideKind.WorkingCopy)
            && !await PromptAsync(pick.Theirs.Name, () => MergeGuard(artcc, pick.Theirs.Id), false).ConfigureAwait(false)
        )
        {
            return false;
        }

        return await PromptAsync(pick.Mine.Name, () => MergeGuard(artcc, pick.Mine.Id), false).ConfigureAwait(false);
    }

    // Whether a scenario text's artccId names another ARTCC than the shown one, as the store judges an import; false for a
    // text that is not a scenario object.
    private static bool NamesOtherArtcc(string text, string artcc)
    {
        try
        {
            return (JsonNode.Parse(text) is JsonObject scenario)
                && (TextField(scenario, "artccId") is { } named)
                && !string.IsNullOrWhiteSpace(named)
                && !string.Equals(named, artcc, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private ScenarioMergeRequest CompareRequest(string artcc, ScenarioComparePick pick, string theirsName, ScenarioMergeTexts texts)
    {
        string theirsCaption = pick.Theirs.Kind switch
        {
            ScenarioCompareSideKind.Server => "server version",
            ScenarioCompareSideKind.WorkingCopy => "working copy",
            _ => "file",
        };
        var captions = new ScenarioMergeCaptions(
            $"Compare: {pick.Mine.Name} with {theirsName}",
            "working copy",
            theirsCaption,
            pick.SharesOrigin ? "the server version this copy started from" : "none",
            pick.SharesOrigin ? MergeTwoWayNote : CompareTwoWayNote
        );
        string id = pick.Mine.Id;
        return new ScenarioMergeRequest(
            artcc,
            id,
            texts,
            captions,
            merged =>
                pick.SharesOrigin
                    ? WriteServerMergeAsync(artcc, id, texts.Theirs, WithStoredId(merged, id))
                    : WriteCompareMergeAsync(artcc, id, merged)
        )
        {
            FreshCopy = pick.SharesOrigin ? () => FreshCopyBesideAsync(artcc, pick.Theirs.Id) : null,
        };
    }

    // Apply: the merged text onto mine under its stored id (written into the JSON, as an import's Compare does); then the
    // rows are re-read with mine selected and an editor with it open reloads it. A failed write throws to the merge window.
    private async Task WriteCompareMergeAsync(string artcc, string id, string merged)
    {
        string text = ScenarioNullNormalizer.Repair(WithStoredId(merged, id), out _);
        LocalScenarioStore store = _services.Store;
        await Task.Run(() => store.SaveWorkingCopy(artcc, id, text)).ConfigureAwait(false);
        await ReloadAsync(artcc, new RowSelection(ScenarioBrowserTab.Local, id)).ConfigureAwait(false);
        await OnUiAsync(() => WorkingCopyReplaced?.Invoke(this, new ScenarioWorkingCopyReplacedEventArgs(artcc, id))).ConfigureAwait(false);
    }

    // Fresh copy beside: a local-only copy of the server version under a fresh id, selected on the Local tab. A failure
    // (the mirror gone) throws to the merge window, which shows it; nothing is written.
    private async Task FreshCopyBesideAsync(string artcc, string mirrorId)
    {
        string newId = string.Empty;
        await OnUiAsync(() => newId = NextId()).ConfigureAwait(false);
        LocalScenarioStore store = _services.Store;
        await Task.Run(() => store.CreateFreshCopyFromMirror(artcc, mirrorId, newId)).ConfigureAwait(false);
        await ReloadAsync(artcc, new RowSelection(ScenarioBrowserTab.Local, newId)).ConfigureAwait(false);
    }
}
