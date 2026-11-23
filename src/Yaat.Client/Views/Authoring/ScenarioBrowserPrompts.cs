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
/// exported, and a Replace/Cancel box before an import replaces the copy open with unsaved edits, each over the window.
/// </summary>
/// <param name="owner">The window the boxes and dialogs show over.</param>
/// <param name="picker">The window's file picker.</param>
internal sealed class ScenarioBrowserPrompts(Window owner, IFilePickerService picker) : IScenarioBrowserPrompts
{
    private const string ReplaceButton = "Replace";
    private const string CancelButton = "Cancel";

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
}
