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

    /// <summary>Asks what to do with the open scenario's unsaved edits before it is closed, exported or merged.</summary>
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

    /// <summary>
    /// Asks what to do with the working copies whose changes were never exported before the window closes or the ARTCC
    /// switches.
    /// </summary>
    /// <param name="names">The copies' names: the first five, then one entry "and N more" when there are more.</param>
    /// <param name="context">What is about to happen: the window closing or the ARTCC switching.</param>
    /// <returns>Export them all and go on, go on without exporting, or cancel.</returns>
    Task<UnexportedChangesChoice> AskUnexportedChangesAsync(IReadOnlyList<string> names, UnexportedChangesContext context);

    /// <summary>
    /// Asks one question when the open copy has unsaved edits and the window closes or the ARTCC switches: what to do with
    /// those edits and with the copies whose changes were never exported.
    /// </summary>
    /// <param name="openName">The open copy's name as edited.</param>
    /// <param name="names">The copies' names, the open one among them: the first five, then one entry "and N more".</param>
    /// <param name="context">What is about to happen: the window closing or the ARTCC switching.</param>
    /// <returns>Save and export all, save and go on, discard and go on, or cancel.</returns>
    Task<UnsavedAndUnexportedChoice> AskUnsavedAndUnexportedAsync(string openName, IReadOnlyList<string> names, UnexportedChangesContext context);

    /// <summary>Opens the merge window over a working copy and the version it is merged with.</summary>
    /// <param name="request">The texts to compare, the window's captions, and the write Apply runs.</param>
    /// <returns>What Apply wrote, or null when the author cancelled and nothing was written.</returns>
    Task<ScenarioMergeResult?> ShowMergeAsync(ScenarioMergeRequest request);
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
    /// <summary>Closing the scenario: another copy opens, or a save writes over an import.</summary>
    Close,

    /// <summary>Exporting the scenario as vNAS JSON.</summary>
    Export,

    /// <summary>Merging server changes into the scenario: mine is read from its saved file.</summary>
    Merge,
}

/// <summary>The answer to <see cref="IScenarioBrowserPrompts.AskUnexportedChangesAsync"/>.</summary>
public enum UnexportedChangesChoice
{
    /// <summary>Export every copy with unexported changes, then go on when all were written.</summary>
    ExportAll,

    /// <summary>Go on without exporting.</summary>
    Leave,

    /// <summary>Cancel: do not close or switch.</summary>
    Cancel,
}

/// <summary>The answer to <see cref="IScenarioBrowserPrompts.AskUnsavedAndUnexportedAsync"/>.</summary>
public enum UnsavedAndUnexportedChoice
{
    /// <summary>Save the open copy, then export every copy with unexported changes; go on when all were written.</summary>
    SaveAndExportAll,

    /// <summary>Save the open copy and go on without exporting.</summary>
    SaveAndLeave,

    /// <summary>Drop the open copy's edits and go on without exporting.</summary>
    DiscardAndLeave,

    /// <summary>Change nothing: do not close or switch.</summary>
    Cancel,
}

/// <summary>
/// What <see cref="IScenarioBrowserPrompts.AskUnexportedChangesAsync"/> and the combined
/// <see cref="IScenarioBrowserPrompts.AskUnsavedAndUnexportedAsync"/> ask before.
/// </summary>
public enum UnexportedChangesContext
{
    /// <summary>The Scenario Editor window closing.</summary>
    Close,

    /// <summary>The browser switching to another ARTCC.</summary>
    SwitchArtcc,
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
