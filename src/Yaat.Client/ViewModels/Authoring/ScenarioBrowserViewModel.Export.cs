using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

// Export JSON, Export clean JSON and Export all unexported. Each asks the ExportGuard first (the editor asks Save /
// Discard / Cancel when the copy is open with unsaved edits), then prepares the export from the saved file on a pool
// thread: a refused or corrupt copy is a notice and writes nothing; a written one names its YAAT-only parts and
// warnings in notices and sets the status line. A failed write is a notice and records nothing; the rows are re-read
// after every export, so the Unexported badge follows.
public sealed partial class ScenarioBrowserViewModel
{
    /// <summary>The status line when Export all unexported finds no copy with unexported changes.</summary>
    public const string NothingToExportText = "Nothing to export.";

    /// <summary>
    /// Gets or sets what is asked, on the UI thread, before a working copy (its ARTCC and id) is exported: true to export
    /// its saved file, false to stop. The editor sets it to ask when that copy is open with unsaved edits; with no editor
    /// it always answers true.
    /// </summary>
    public Func<string, string, Task<bool>> ExportGuard { get; set; } = (_, _) => Task.FromResult(true);

    private bool CanExport() => ExportTarget() is not null;

    // Every export is off for a corrupt row and for a server row with no readable working copy.
    private bool CanExportAll() => (_artcc is not null) && ((SelectedRow is null) || CanExport());

    // The working copy the selected row stands for: the row itself, or a server row's copy; null when there is none or
    // it is corrupt.
    private ScenarioBrowserRowViewModel? ExportTarget() =>
        SelectedRow switch
        {
            { Kind: ScenarioRowKind.WorkingCopy, IsCorrupt: false } copy => copy,
            { Kind: ScenarioRowKind.Server, HasWorkingCopy: true } server => LocalRows.FirstOrDefault(r => r.Id == server.Id)
                is { IsCorrupt: false } copy
                ? copy
                : null,
            _ => null,
        };

    /// <summary>Exports the selected working copy as vNAS JSON with every field kept, where the user picks.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportJsonAsync() => ExportSelectedAsync(ExportKind.Full);

    /// <summary>Exports the selected working copy as vNAS JSON without the fields YAAT does not model and the YAAT-only parts.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportCleanJsonAsync() => ExportSelectedAsync(ExportKind.Clean);

    /// <summary>
    /// Exports every working copy with unexported changes as full vNAS JSON into the folder the user picks, each named after
    /// its scenario and id. With none marked it only says so; a Cancel at the open copy's question stops it before any write.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportAll))]
    private async Task ExportAllUnexportedAsync()
    {
        string artcc = _artcc!;
        ScenarioBrowserRowViewModel[] marked = [.. LocalRows.Where(r => r.HasUnexportedChanges && !r.IsCorrupt)];
        if (marked.Length == 0)
        {
            await OnUiAsync(() => StatusText = NothingToExportText).ConfigureAwait(false);
            return;
        }

        string? folder = await PromptAsync<string?>("Export all", () => _services.Prompts.PickExportFolderAsync(), null).ConfigureAwait(false);
        if ((folder is null) || !await AllGuardsAllowAsync(artcc, marked).ConfigureAwait(false))
        {
            return;
        }

        var export = new ScenarioExportViewModel(_services.Store, _services.Clock);
        int written = 0;
        foreach (ScenarioBrowserRowViewModel row in marked)
        {
            var job = new ExportJob(artcc, row, ExportKind.Full);
            string path = Path.Combine(folder, ScenarioExportViewModel.BulkFileName(row.Name, row.Id));
            if (
                (await PrepareExportAsync(export, job).ConfigureAwait(false) is { } result)
                && await WriteExportAsync(export, job, result, path).ConfigureAwait(false)
            )
            {
                written++;
            }
        }

        await OnUiAsync(() => StatusText = $"Exported {written} of {marked.Length} {(marked.Length == 1 ? "scenario" : "scenarios")} to {folder}.")
            .ConfigureAwait(false);
        await ReloadAsync(artcc, null).ConfigureAwait(false);
    }

    private async Task ExportSelectedAsync(ExportKind kind)
    {
        if (ExportTarget() is not { } row)
        {
            return;
        }

        var job = new ExportJob(_artcc!, row, kind);
        if (!await PromptAsync(row.Name, () => ExportGuard(job.Artcc, row.Id), false).ConfigureAwait(false))
        {
            return;
        }

        var export = new ScenarioExportViewModel(_services.Store, _services.Clock);
        ExportResult? result = await PrepareExportAsync(export, job).ConfigureAwait(false);
        string suggested = ScenarioExportViewModel.FileName(row.Name, row.Id);
        string? path =
            (result is null)
                ? null
                : await PromptAsync<string?>(row.Name, () => _services.Prompts.PickExportPathAsync(suggested), null).ConfigureAwait(false);
        if ((path is not null) && (result is { } prepared) && await WriteExportAsync(export, job, prepared, path).ConfigureAwait(false))
        {
            await OnUiAsync(() => StatusText = $"Exported {row.Name} to {path}.").ConfigureAwait(false);
        }

        await ReloadAsync(job.Artcc, null).ConfigureAwait(false);
    }

    // Asks the guard for each copy in turn (only the open copy with unsaved edits asks the user); false at the first no.
    private async Task<bool> AllGuardsAllowAsync(string artcc, IReadOnlyList<ScenarioBrowserRowViewModel> rows)
    {
        foreach (ScenarioBrowserRowViewModel row in rows)
        {
            if (!await PromptAsync(row.Name, () => ExportGuard(artcc, row.Id), false).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    // The prepared export when it can be written; a refused or corrupt copy is a notice, a failure to read it too.
    private async Task<ExportResult?> PrepareExportAsync(ScenarioExportViewModel export, ExportJob job)
    {
        (bool read, ExportResult? result) = await TryStoreAsync(
                job.Row.Name,
                "preparing the export of",
                () => export.Prepare(job.Artcc, job.Row.Id, job.Kind)
            )
            .ConfigureAwait(false);
        if (!read || (result is null))
        {
            return null;
        }

        if (result.Outcome != ExportOutcome.Written)
        {
            await OnUiAsync(() => AddNotice(ScenarioExportViewModel.RefusalNotice(job.Row.Name, result))).ConfigureAwait(false);
            return null;
        }

        return result;
    }

    // Writes and records the export, then posts its warnings; a failed write is a notice and records nothing.
    private async Task<bool> WriteExportAsync(ScenarioExportViewModel export, ExportJob job, ExportResult result, string path)
    {
        if (!await TryDoAsync(job.Row.Name, "exporting", () => export.Write(job.Artcc, job.Row.Id, result, path)).ConfigureAwait(false))
        {
            return false;
        }

        IReadOnlyList<string> notices = ScenarioExportViewModel.WarningNotices(job.Row.Name, job.Kind, result);
        if (notices.Count > 0)
        {
            await OnUiAsync(() =>
                {
                    foreach (string notice in notices)
                    {
                        AddNotice(notice);
                    }
                })
                .ConfigureAwait(false);
        }

        return true;
    }

    // One copy's export: its ARTCC, its row and the kind.
    private sealed record ExportJob(string Artcc, ScenarioBrowserRowViewModel Row, ExportKind Kind);
}
