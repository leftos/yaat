using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The questions the scenario browser asks the user, answered by the window's dialogs and pickers, so the view model
/// needs no UI to run.
/// </summary>
public interface IScenarioBrowserPrompts
{
    /// <summary>Asks whether to delete a working copy.</summary>
    /// <param name="row">The working copy's row.</param>
    /// <returns>True to delete it.</returns>
    Task<bool> ConfirmDeleteAsync(ScenarioBrowserRowViewModel row);

    /// <summary>Asks for the scenario files to import.</summary>
    /// <returns>The picked files' paths; empty when the user cancelled.</returns>
    Task<IReadOnlyList<string>> PickImportFilesAsync();

    /// <summary>Asks where to save a copy of a scenario.</summary>
    /// <param name="suggestedFileName">The file name to offer: the scenario's name with <c>.json</c>.</param>
    /// <returns>The path to write, or null when the user cancelled.</returns>
    Task<string?> PickSavePathAsync(string suggestedFileName);

    /// <summary>Asks where to write a working copy's export as vNAS JSON.</summary>
    /// <param name="suggestedFileName">The file name to offer: the scenario's name with <c>.json</c>.</param>
    /// <returns>The path to write, or null when the user cancelled.</returns>
    Task<string?> PickExportPathAsync(string suggestedFileName);

    /// <summary>Asks for the folder Export all unexported writes its files into.</summary>
    /// <returns>The folder's path, or null when the user cancelled.</returns>
    Task<string?> PickExportFolderAsync();

    /// <summary>Asks what to do with an imported file whose scenario id is already taken.</summary>
    /// <param name="question">The file, its id and what holds the id.</param>
    /// <returns>The choice, and whether it applies to the remaining files of the import.</returns>
    Task<ImportConflictAnswer> AskImportConflictAsync(ImportConflictQuestion question);

    /// <summary>Asks what to do with the open scenario's unsaved edits before it is closed or exported.</summary>
    /// <param name="scenarioName">The open scenario's name.</param>
    /// <param name="context">What is about to happen to the scenario, which the question names.</param>
    /// <returns>Save them, discard them, or keep the scenario open with them and stop.</returns>
    Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string scenarioName, UnsavedChangesContext context);

    /// <summary>Asks whether an import may replace the working copy open with unsaved edits, losing them.</summary>
    /// <param name="scenarioName">The open scenario's name.</param>
    /// <returns>True to replace it; false to skip that file of the import.</returns>
    Task<bool> ConfirmReplaceOpenCopyAsync(string scenarioName);

    /// <summary>Asks whether Test in Room may replace the scenario the room runs now.</summary>
    /// <param name="scenarioName">The working copy about to load.</param>
    /// <returns>True to load it in the running scenario's place; false to leave the room alone.</returns>
    Task<bool> ConfirmReplaceRunningScenarioAsync(string scenarioName);
}

/// <summary>The answer to <see cref="IScenarioBrowserPrompts.AskUnsavedChangesAsync"/>.</summary>
public enum UnsavedChangesChoice
{
    /// <summary>Save the edits, then go on.</summary>
    Save,

    /// <summary>Drop the edits and go on.</summary>
    Discard,

    /// <summary>Keep the scenario open with its edits; do not go on.</summary>
    Cancel,
}

/// <summary>What <see cref="IScenarioBrowserPrompts.AskUnsavedChangesAsync"/> asks before.</summary>
public enum UnsavedChangesContext
{
    /// <summary>Closing the scenario: another opens, the window closes, the ARTCC switches, or a save over an import.</summary>
    Close,

    /// <summary>Exporting the scenario as vNAS JSON.</summary>
    Export,
}

/// <summary>An imported file whose scenario id is already taken.</summary>
/// <param name="FileName">The file's name, without its folder.</param>
/// <param name="ScenarioId">The scenario id the file holds.</param>
/// <param name="ScenarioName">The scenario name the file holds, or its id when it has none.</param>
/// <param name="Conflict">What already holds the id: a working copy, a server scenario or both.</param>
/// <param name="RemainingFiles">How many files of the import come after this one.</param>
public sealed record ImportConflictQuestion(string FileName, string ScenarioId, string ScenarioName, ScenarioIdConflict Conflict, int RemainingFiles);

/// <summary>The answer to an <see cref="ImportConflictQuestion"/>.</summary>
/// <param name="Choice">New id, keep id or skip.</param>
/// <param name="ApplyToRemaining">Whether the choice answers every later clash of the same import without asking.</param>
public sealed record ImportConflictAnswer(ImportIdChoice Choice, bool ApplyToRemaining);
