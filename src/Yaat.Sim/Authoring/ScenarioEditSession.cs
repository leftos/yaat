using Microsoft.Extensions.Logging;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring;

/// <summary>
/// One open working copy being edited: the document, its undo and redo steps, and an explicit save. <see cref="Edit"/>
/// is the only way to change the document, each call one undo step; undo and redo replace the document, so a view binds
/// to the session and re-reads <see cref="Document"/> on <see cref="DocumentReplaced"/>, never holding a
/// <see cref="Scenario"/>. UI-free and single-threaded: the caller runs every member on one thread.
/// </summary>
public sealed class ScenarioEditSession
{
    private static readonly ILogger Log = SimLog.CreateLogger<ScenarioEditSession>();

    private readonly LocalScenarioStore _store;

    private readonly UndoStack<string> _undo = new();

    // The document as last saved (or as opened), in Write() form, so a fresh open reads clean whatever the file's spelling.
    private string _lastSavedText;

    // The document's Write() text, kept current by every change the session makes (Edit, Undo, Redo).
    private string _currentText;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScenarioEditSession"/> class over a working copy's stored text.
    /// </summary>
    /// <param name="store">The store holding the working copy.</param>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="scenarioId">The working copy's id.</param>
    /// <exception cref="FileNotFoundException">There is no working copy with that id.</exception>
    /// <exception cref="System.Text.Json.JsonException">The working copy is not a readable scenario.</exception>
    public ScenarioEditSession(LocalScenarioStore store, string artccId, string scenarioId)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        ArtccId = artccId;
        ScenarioId = scenarioId;
        Document = ScenarioDocument.Read(store.ReadWorkingCopyText(artccId, scenarioId));
        _currentText = Document.Write();
        _lastSavedText = _currentText;
    }

    /// <summary>Raised after undo or redo replaced <see cref="Document"/> with a new instance.</summary>
    public event EventHandler? DocumentReplaced;

    /// <summary>Gets the ARTCC the working copy belongs to.</summary>
    public string ArtccId { get; }

    /// <summary>Gets the working copy's id.</summary>
    public string ScenarioId { get; }

    /// <summary>Gets the document being edited; undo and redo replace it.</summary>
    public ScenarioDocument Document { get; private set; }

    /// <summary>Gets whether the document differs from what was last saved (or opened).</summary>
    public bool IsDirty => !string.Equals(_currentText, _lastSavedText, StringComparison.Ordinal);

    /// <summary>Gets whether there is an edit to undo.</summary>
    public bool CanUndo => _undo.CanUndo;

    /// <summary>Gets whether there is an undone edit to redo.</summary>
    public bool CanRedo => _undo.CanRedo;

    /// <summary>Gets the description of the edit <see cref="Undo"/> would revert, or null when there is none.</summary>
    public string? UndoDescription => _undo.NextUndoDescription;

    /// <summary>Gets the description of the edit <see cref="Redo"/> would reapply, or null when there is none.</summary>
    public string? RedoDescription => _undo.NextRedoDescription;

    /// <summary>
    /// Applies one edit to the scenario as one undo step, all or nothing: an edit that leaves the document as it was
    /// records no step, and one that throws leaves the document, the undo steps and the dirty state as they were (the
    /// document is restored, <see cref="DocumentReplaced"/> is raised, and the exception is rethrown).
    /// </summary>
    /// <param name="description">The edit's name, as an Undo menu item shows it.</param>
    /// <param name="change">Changes the scenario in place.</param>
    public void Edit(string description, Action<Scenario> change)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(change);
        string before = _currentText;
        string after;
        try
        {
            change(Document.Scenario);
            after = Document.Write();
        }
        catch
        {
            _ = Replace(before);
            throw;
        }

        if (!string.Equals(after, before, StringComparison.Ordinal))
        {
            _undo.Record(description, before);
            _currentText = after;
        }
    }

    /// <summary>Reverts the last edit, replacing the document.</summary>
    /// <returns>Whether there was an edit to undo.</returns>
    public bool Undo() => Replace(_undo.Undo(_currentText));

    /// <summary>Reapplies the last undone edit, replacing the document.</summary>
    /// <returns>Whether there was an edit to redo.</returns>
    public bool Redo() => Replace(_undo.Redo(_currentText));

    /// <summary>
    /// Writes the document to the working copy when it changed since the last save; a scenario the store would call
    /// corrupt (no name) is refused and nothing is written. Undo and redo steps are kept.
    /// </summary>
    /// <returns>Whether the document was saved, had nothing to save, or was refused and why.</returns>
    public ScenarioSaveResult Save()
    {
        string text = _currentText;
        if (string.Equals(text, _lastSavedText, StringComparison.Ordinal))
        {
            return new ScenarioSaveResult(ScenarioSaveOutcome.Unchanged, null);
        }

        string? reason = LocalScenarioStore.CheckScenario(text, out _);
        if (reason is not null)
        {
            if (Log.IsEnabled(LogLevel.Information))
            {
                Log.LogInformation("Save of scenario {Id} refused: {Reason}", ScenarioId, reason);
            }

            return new ScenarioSaveResult(ScenarioSaveOutcome.Refused, reason);
        }

        _store.SaveWorkingCopy(ArtccId, ScenarioId, text);
        _lastSavedText = text;
        return new ScenarioSaveResult(ScenarioSaveOutcome.Saved, null);
    }

    private bool Replace(string? snapshot)
    {
        if (snapshot is null)
        {
            return false;
        }

        Document = ScenarioDocument.Read(snapshot);
        _currentText = snapshot;
        DocumentReplaced?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

/// <summary>What one <see cref="ScenarioEditSession.Save"/> did.</summary>
public enum ScenarioSaveOutcome
{
    /// <summary>The document was written to the working copy.</summary>
    Saved,

    /// <summary>The document had not changed since the last save; nothing was written.</summary>
    Unchanged,

    /// <summary>The document is not a scenario the store keeps (no name); nothing was written and the result says why.</summary>
    Refused,
}

/// <summary>The outcome of <see cref="ScenarioEditSession.Save"/>.</summary>
/// <param name="Outcome">What the save did.</param>
/// <param name="Reason">Why the save was refused; null otherwise.</param>
public sealed record ScenarioSaveResult(ScenarioSaveOutcome Outcome, string? Reason);
