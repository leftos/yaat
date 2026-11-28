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

    // How many copy names the unexported-changes question lists before "and N more".
    private const int UnexportedNamesShown = 5;

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
        ScenarioBrowserRowViewModel[] marked = MarkedRows();
        if (marked.Length == 0)
        {
            await OnUiAsync(() => StatusText = NothingToExportText).ConfigureAwait(false);
            return;
        }

        await ExportMarkedAsync(FullJobs(artcc, marked)).ConfigureAwait(false);
    }

    private static ExportJob[] FullJobs(string artcc, IEnumerable<ScenarioBrowserRowViewModel> rows) =>
        [.. rows.Select(r => new ExportJob(artcc, r, ExportKind.Full))];

    // The shown ARTCC's working copies with unexported changes, corrupt ones left out.
    private ScenarioBrowserRowViewModel[] MarkedRows() => [.. LocalRows.Where(r => r.HasUnexportedChanges && !r.IsCorrupt)];

    // Export all, answered at the unexported-changes question: true once every marked copy was written; a cancelled
    // folder pick or guard stays, and a copy that failed stays with the status line naming it.
    private async Task<bool> ExportAllBeforeLeavingAsync(ExportJob[] jobs)
    {
        IReadOnlyList<ExportFailure>? failures = await ExportMarkedAsync(jobs).ConfigureAwait(false);
        if (failures is null)
        {
            return false;
        }

        if (failures.Count == 0)
        {
            return true;
        }

        ExportFailure first = failures[0];
        await OnUiAsync(() => StatusText = $"Could not export {first.Name}: {first.Reason}").ConfigureAwait(false);
        return false;
    }

    // Picks the folder, asks each copy's guard, exports every job's copy into it, then re-reads the shown ARTCC's rows.
    // Null when the pick was cancelled or a guard refused (nothing written); else the copies that were not written.
    private async Task<IReadOnlyList<ExportFailure>?> ExportMarkedAsync(ExportJob[] jobs)
    {
        string? folder = await PromptAsync<string?>("Export all", () => _services.Prompts.PickExportFolderAsync(), null).ConfigureAwait(false);
        if ((folder is null) || !await AllGuardsAllowAsync(jobs).ConfigureAwait(false))
        {
            return null;
        }

        var export = new ScenarioExportViewModel(_services.Store, _services.Clock);
        bool severalArtccs = jobs.Select(j => j.Artcc).Distinct(StringComparer.Ordinal).Skip(1).Any();
        List<ExportFailure> failures = [];
        foreach (ExportJob job in jobs)
        {
            string path = Path.Combine(folder, BulkFileName(job, severalArtccs));
            if (await ExportOneAsync(export, job, path).ConfigureAwait(false) is { } reason)
            {
                failures.Add(new ExportFailure(job.Row.Name, reason));
            }
        }

        int written = jobs.Length - failures.Count;
        await OnUiAsync(() => StatusText = $"Exported {written} of {jobs.Length} {(jobs.Length == 1 ? "scenario" : "scenarios")} to {folder}.")
            .ConfigureAwait(false);
        if (_artcc is { } shown)
        {
            await ReloadAsync(shown, null).ConfigureAwait(false);
        }

        return failures;
    }

    // A job's file name in Export all: "{name}-{id}.json", led by "{ARTCC}-" when the jobs span several ARTCCs so that two
    // copies with the same name and id do not write the same file.
    private static string BulkFileName(ExportJob job, bool withArtcc)
    {
        string name = ScenarioExportViewModel.BulkFileName(job.Row.Name, job.Row.Id);
        return withArtcc ? $"{job.Artcc}-{name}" : name;
    }

    // One copy's bulk export: null once written, else why it was not.
    private async Task<string?> ExportOneAsync(ScenarioExportViewModel export, ExportJob job, string path)
    {
        PreparedExport prepared = await PrepareExportAsync(export, job).ConfigureAwait(false);
        if (prepared.Result is not { } result)
        {
            return prepared.Failure;
        }

        return await WriteExportAsync(export, job, result, path).ConfigureAwait(false);
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
        ExportResult? result = (await PrepareExportAsync(export, job).ConfigureAwait(false)).Result;
        string suggested = ScenarioExportViewModel.FileName(row.Name, row.Id);
        string? path =
            (result is null)
                ? null
                : await PromptAsync<string?>(row.Name, () => _services.Prompts.PickExportPathAsync(suggested), null).ConfigureAwait(false);
        if ((path is not null) && (result is { } prepared) && (await WriteExportAsync(export, job, prepared, path).ConfigureAwait(false) is null))
        {
            await OnUiAsync(() => StatusText = $"Exported {row.Name} to {path}.").ConfigureAwait(false);
        }

        await ReloadAsync(job.Artcc, null).ConfigureAwait(false);
    }

    // Asks the guard for each copy in turn (only the open copy with unsaved edits asks the user); false at the first no.
    private async Task<bool> AllGuardsAllowAsync(ExportJob[] jobs)
    {
        foreach (ExportJob job in jobs)
        {
            if (!await PromptAsync(job.Row.Name, () => ExportGuard(job.Artcc, job.Row.Id), false).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    // The prepared export when it can be written, else why not; a refused or corrupt copy is a notice, a failure to read
    // it too.
    private async Task<PreparedExport> PrepareExportAsync(ScenarioExportViewModel export, ExportJob job)
    {
        (ExportResult? result, string? failure) = await TryStepAsync(
                job.Row.Name,
                "preparing the export of",
                () => export.Prepare(job.Artcc, job.Row.Id, job.Kind)
            )
            .ConfigureAwait(false);
        if (result is null)
        {
            return new PreparedExport(null, failure ?? "it could not be read");
        }

        if (result.Outcome != ExportOutcome.Written)
        {
            await OnUiAsync(() => AddNotice(ScenarioExportViewModel.RefusalNotice(job.Row.Name, result))).ConfigureAwait(false);
            return new PreparedExport(null, ScenarioExportViewModel.RefusalReason(result));
        }

        return new PreparedExport(result, null);
    }

    // Writes and records the export, then posts its warnings: null once written. A failed write is a notice, records
    // nothing and gives its reason.
    private async Task<string?> WriteExportAsync(ScenarioExportViewModel export, ExportJob job, ExportResult result, string path)
    {
        if (
            await TryStepAsync(job.Row.Name, "exporting", () => export.Write(job.Artcc, job.Row.Id, result, path)).ConfigureAwait(false) is
            { } failure
        )
        {
            return failure;
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

        return null;
    }

    // One copy's export: its ARTCC, its row and the kind.
    private sealed record ExportJob(string Artcc, ScenarioBrowserRowViewModel Row, ExportKind Kind);

    // A prepared export, or null and why it cannot be written.
    private sealed record PreparedExport(ExportResult? Result, string? Failure);

    // A marked copy Export all did not write: its name and why.
    private sealed record ExportFailure(string Name, string Reason);
}
