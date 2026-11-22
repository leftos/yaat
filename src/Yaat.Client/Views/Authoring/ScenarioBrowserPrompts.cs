using Avalonia.Controls;
using MsBox.Avalonia.Enums;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Scenario Editor window's answers to the browser's questions: a Yes/No box before a delete, the file pickers for
/// Import and Save copy, and the import id-conflict dialog, each over the window.
/// </summary>
/// <param name="owner">The window the boxes and dialogs show over.</param>
/// <param name="picker">The window's file picker.</param>
internal sealed class ScenarioBrowserPrompts(Window owner, IFilePickerService picker) : IScenarioBrowserPrompts
{
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
    public Task<ImportConflictAnswer> AskImportConflictAsync(ImportConflictQuestion question) => new ImportConflictDialog(question).ShowAsync(owner);
}
