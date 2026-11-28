using Avalonia.Controls;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Scenario Editor window's answers to the browser's questions: a Yes/No box before a delete, the file pickers for
/// Import, Save copy and the exports, the import id-conflict dialog, a Yes/No/Cancel box before unsaved edits are left or
/// exported, a Replace/Cancel box before an import replaces the copy open with unsaved edits, and a Yes/No box before Test
/// in Room replaces the running scenario, and, before the window closes or the ARTCC switches, an unexported-changes box
/// or, when the open copy also has unsaved edits, one combined box, each over the window.
/// </summary>
/// <param name="owner">The window the boxes and dialogs show over.</param>
/// <param name="picker">The window's file picker.</param>
internal sealed class ScenarioBrowserPrompts(Window owner, IFilePickerService picker) : IScenarioBrowserPrompts
{
    private const string ReplaceButton = "Replace";
    private const string CancelButton = "Cancel";
    private const string ExportAllButton = "Export all";
    private const string SaveAndExportAllButton = "Save and export all";
    private const string UnexportedMessage = "These scenarios have changes you have not exported:";

    private static readonly IReadOnlyList<FilePickerFilter> JsonFilter = [new FilePickerFilter("Scenario JSON", ["*.json"])];

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
        string before = (context == UnsavedChangesContext.Export) ? "exporting" : "closing";
        ButtonResult result = await MessageBoxPresenter.ShowStandardAsync(
            owner,
            "Unsaved changes",
            $"Save your changes to \"{scenarioName}\" before {before} it?",
            ButtonEnum.YesNoCancel
        );
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
