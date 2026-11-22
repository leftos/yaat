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

    /// <summary>Asks what to do with an imported file whose scenario id is already taken.</summary>
    /// <param name="question">The file, its id and what holds the id.</param>
    /// <returns>The choice, and whether it applies to the remaining files of the import.</returns>
    Task<ImportConflictAnswer> AskImportConflictAsync(ImportConflictQuestion question);
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
