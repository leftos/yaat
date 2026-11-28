using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Scenario Editor window's answers to the browser's questions, each shown over the window:
/// <list type="bullet">
/// <item>a Yes/No box before a delete, and before Test in Room replaces the running scenario;</item>
/// <item>the file pickers for Import, Compare, Save copy and the exports;</item>
/// <item>the import id-conflict dialog, the Compare picker and the merge window;</item>
/// <item>a Yes/No/Cancel box before unsaved edits are left, exported or merged;</item>
/// <item>a Replace/Cancel box before an import replaces the copy open with unsaved edits;</item>
/// <item>before the window closes or the ARTCC switches, an unexported-changes box, or one combined box when the open copy
/// also has unsaved edits.</item>
/// </list>
/// </summary>
/// <param name="owner">The window the boxes and dialogs show over.</param>
/// <param name="picker">The window's file picker.</param>
/// <param name="preferences">Keeps the Compare picker's position and size.</param>
internal sealed class ScenarioBrowserPrompts(Window owner, IFilePickerService picker, UserPreferences preferences) : IScenarioBrowserPrompts
{
    private const string ReplaceButton = "Replace";
    private const string CancelButton = "Cancel";
    private const string ExportAllButton = "Export all";
    private const string SaveAndExportAllButton = "Save and export all";
    private const string UnexportedMessage = "These scenarios have changes you have not exported:";

    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioBrowserPrompts>();

    private static readonly IReadOnlyList<FilePickerFilter> JsonFilter = [new FilePickerFilter("Scenario JSON", ["*.json"])];

    /// <summary>
    /// Gets or sets what the merge window's MERGED AIRCRAFT editor and pickers work through: the browser's services, the
    /// ground layouts and the navdata load. The Scenario Editor window sets it once the browser's services are built.
    /// </summary>
    internal ScenarioMergedAircraftServices? MergeServices { get; set; }

    /// <inheritdoc/>
    public async Task<bool> ConfirmDeleteAsync(ScenarioBrowserRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ButtonResult result = await MessageBoxPresenter.ShowStandardAsync(
            owner,
            "Delete working copy",
            $"Delete the working copy \"{row.Name}\"? This cannot be undone.",
            ButtonEnum.YesNo
        );
        return result == ButtonResult.Yes;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> PickImportFilesAsync() => picker.OpenFilesAsync(new OpenFileOptions("Import scenarios", JsonFilter));

    /// <inheritdoc/>
    public Task<string?> PickCompareFileAsync() => picker.OpenFileAsync(new OpenFileOptions("Compare with a scenario file", JsonFilter));

    /// <inheritdoc/>
    public Task<string?> PickSavePathAsync(string suggestedFileName) =>
        picker.SaveFileAsync(new SaveFileOptions("Save a copy", suggestedFileName, JsonFilter, DefaultExtension: "json"));

    /// <inheritdoc/>
    public Task<string?> PickExportPathAsync(string suggestedFileName) =>
        picker.SaveFileAsync(new SaveFileOptions("Export JSON", suggestedFileName, JsonFilter, DefaultExtension: "json"));

    /// <inheritdoc/>
    public Task<string?> PickExportFolderAsync() => picker.OpenFolderAsync(new OpenFolderOptions("Export all unexported scenarios"));

    /// <inheritdoc/>
    public Task<ImportConflictAnswer> AskImportConflictAsync(ImportConflictQuestion question) => new ImportConflictDialog(question).ShowAsync(owner);

    /// <inheritdoc/>
    public async Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string scenarioName, UnsavedChangesContext context)
    {
        string question = context switch
        {
            UnsavedChangesContext.Export => $"Save your changes to \"{scenarioName}\" before exporting it?",
            UnsavedChangesContext.Merge => $"Save your changes to \"{scenarioName}\" before merging?",
            _ => $"Save your changes to \"{scenarioName}\" before closing it?",
        };
        ButtonResult result = await MessageBoxPresenter.ShowStandardAsync(owner, "Unsaved changes", question, ButtonEnum.YesNoCancel);
        return result switch
        {
            ButtonResult.Yes => UnsavedChangesChoice.Save,
            ButtonResult.No => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel,
        };
    }

    /// <inheritdoc/>
    public async Task<bool> ConfirmReplaceOpenCopyAsync(string scenarioName)
    {
        var parameters = new MessageBoxCustomParams
        {
            ContentTitle = "Replace working copy",
            ContentMessage = $"Replace \"{scenarioName}\"? Your unsaved changes will be lost.",
            ButtonDefinitions = [new ButtonDefinition { Name = ReplaceButton }, new ButtonDefinition { Name = CancelButton, IsCancel = true }],
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        return await MessageBoxPresenter.ShowCustomAsync(owner, parameters) == ReplaceButton;
    }

    /// <inheritdoc/>
    public async Task<bool> ConfirmReplaceRunningScenarioAsync(string scenarioName)
    {
        ButtonResult result = await MessageBoxPresenter.ShowStandardAsync(
            owner,
            "Test in Room",
            $"Replace the running scenario? \"{scenarioName}\" loads in its place.",
            ButtonEnum.YesNo
        );
        return result == ButtonResult.Yes;
    }

    /// <inheritdoc/>
    public async Task<UnexportedChangesChoice> AskUnexportedChangesAsync(IReadOnlyList<string> names, UnexportedChangesContext context)
    {
        ArgumentNullException.ThrowIfNull(names);
        string leave = (context == UnexportedChangesContext.SwitchArtcc) ? "Switch without exporting" : "Close without exporting";
        string answer = await ShowChoicesAsync("Unexported changes", UnexportedBody(names), [ExportAllButton, leave]);
        if (answer == ExportAllButton)
        {
            return UnexportedChangesChoice.ExportAll;
        }

        return (answer == leave) ? UnexportedChangesChoice.Leave : UnexportedChangesChoice.Cancel;
    }

    /// <inheritdoc/>
    public async Task<UnsavedAndUnexportedChoice> AskUnsavedAndUnexportedAsync(
        string openName,
        IReadOnlyList<string> names,
        UnexportedChangesContext context
    )
    {
        ArgumentNullException.ThrowIfNull(names);
        bool switching = context == UnexportedChangesContext.SwitchArtcc;
        string saveAndLeave = switching ? "Save and switch without exporting" : "Save and close without exporting";
        string discardAndLeave = switching ? "Discard and switch" : "Discard and close";
        string body = $"{openName} has unsaved changes.\n\n{UnexportedBody(names)}";
        string answer = await ShowChoicesAsync("Unsaved and unexported changes", body, [SaveAndExportAllButton, saveAndLeave, discardAndLeave]);
        if (answer == SaveAndExportAllButton)
        {
            return UnsavedAndUnexportedChoice.SaveAndExportAll;
        }

        if (answer == saveAndLeave)
        {
            return UnsavedAndUnexportedChoice.SaveAndLeave;
        }

        return (answer == discardAndLeave) ? UnsavedAndUnexportedChoice.DiscardAndLeave : UnsavedAndUnexportedChoice.Cancel;
    }

    /// <inheritdoc/>
    public async Task<ScenarioMergeResult?> ShowMergeAsync(ScenarioMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ScenarioMergedAircraftServices services =
            MergeServices ?? throw new InvalidOperationException("The merge window cannot open: the Scenario Editor gave it no services.");
        // The checks read the window's pickers at Apply, long after it is built, so the variable is declared before it is set.
        ScenarioMergeWindow window = null!;
        window = new ScenarioMergeWindow(
            request,
            preferences,
            merged =>
            {
                // The pickers are read here, on the UI thread, so the check task touches nothing of the window.
                IReadOnlySet<string>? positionIds = window.ViewModel.Pickers.CheckedPositionIds;
                return Task.Run(() => CheckAtApply(request, merged, positionIds));
            }
        )
        {
            AircraftServices = services,
        };
        using var loading = new CancellationTokenSource();
        Task pickers = window.ViewModel.LoadPickersAsync(services.Browser, loading.Token);
        try
        {
            return await window.ShowAsync(owner).ConfigureAwait(true);
        }
        finally
        {
            await loading.CancelAsync().ConfigureAwait(true);
            await AwaitPickersAsync(pickers).ConfigureAwait(true);
        }
    }

    /// <inheritdoc/>
    public Task<ScenarioComparePick?> PickCompareAsync(ScenarioComparePickerViewModel picker) =>
        new ScenarioComparePickerWindow(picker, preferences).ShowAsync(owner);

    // The checks at Apply, run off the UI thread: the Scenario Editor's checks over the merged text, with the position ids by
    // the editor's rule once the pickers have loaded (none before, which skips the position checks). No ground layouts: the
    // TAXI-route checks run in the editor's live checks once the copy reloads, not at Apply, so their not-checked findings are
    // dropped here.
    private static IReadOnlyList<ScenarioEditIssue> CheckAtApply(ScenarioMergeRequest request, string merged, IReadOnlySet<string>? positionIds)
    {
        var inputs = new ScenarioEditInputs(() => NavigationDatabase.InstanceOrNull is not null, positionIds, null);
        return [.. ScenarioMergeViewModel.CheckMerged(request, merged, inputs).Where(i => i.Severity != ScenarioEditIssueSeverity.NotChecked)];
    }

    // The pickers' load once the window has closed and its token is cancelled; a failure is logged, never left unobserved.
    private static async Task AwaitPickersAsync(Task pickers)
    {
        try
        {
            await pickers.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Merge window: loading the pickers failed after the window closed");
            }
        }
    }

    private static string UnexportedBody(IReadOnlyList<string> names) => UnexportedMessage + "\n\n" + string.Join("\n", names);

    // A box with the given buttons in order and then Cancel, the default and the Esc answer; the pressed button's label.
    private Task<string> ShowChoicesAsync(string title, string message, IReadOnlyList<string> buttons)
    {
        var parameters = new MessageBoxCustomParams
        {
            ContentTitle = title,
            ContentMessage = message,
            ButtonDefinitions =
            [
                .. buttons.Select(b => new ButtonDefinition { Name = b }),
                new ButtonDefinition
                {
                    Name = CancelButton,
                    IsDefault = true,
                    IsCancel = true,
                },
            ],
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        return MessageBoxPresenter.ShowCustomAsync(owner, parameters);
    }
}
