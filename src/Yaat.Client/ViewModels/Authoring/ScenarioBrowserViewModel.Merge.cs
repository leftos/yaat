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
        return new ScenarioMergeRequest(artcc, row.Id, texts, captions, merged => WriteServerMergeAsync(artcc, row, texts.Theirs, merged))
        {
            FreshCopy = () => FreshCopyBesideAsync(artcc, mirrorId),
        };
    }

    // Writes the merge onto the server version; then the rows are re-read and an editor with the copy open reloads it. A
    // copy the store refuses (one with no server origin) throws to the merge window, which shows the failure and stays
    // open; nothing is written.
    private async Task WriteServerMergeAsync(string artcc, ScenarioBrowserRowViewModel row, string theirs, string merged)
    {
        LocalScenarioStore store = _services.Store;
        string version = LocalScenarioStore.ComputeVersion(theirs);
        await Task.Run(() => store.ApplyMerge(artcc, row.Id, merged, theirs, version)).ConfigureAwait(false);
        await ReloadAsync(artcc, new RowSelection(ScenarioBrowserTab.Local, row.Id)).ConfigureAwait(false);
        await OnUiAsync(() => WorkingCopyReplaced?.Invoke(this, new ScenarioWorkingCopyReplacedEventArgs(artcc, row.Id))).ConfigureAwait(false);
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
