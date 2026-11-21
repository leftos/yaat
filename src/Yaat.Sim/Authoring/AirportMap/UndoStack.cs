namespace Yaat.Sim.Authoring.AirportMap;

/// <summary>
/// Undo and redo over immutable snapshots: the owner records the state each edit replaces, and undo hands it back.
/// Pure: no I/O, no clock, no UI.
/// </summary>
/// <typeparam name="TState">The snapshot type, an immutable value such as <see cref="AirportMapDocument"/>.</typeparam>
/// <param name="maxDepth">How many undo steps are kept; recording past it drops the oldest.</param>
public sealed class UndoStack<TState>(int maxDepth = 200)
    where TState : class
{
    private readonly int _maxDepth =
        maxDepth >= 1 ? maxDepth : throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "must be at least 1");

    private readonly LinkedList<(string Description, TState State)> _undo = new();

    private readonly Stack<(string Description, TState State)> _redo = new();

    /// <summary>
    /// Gets whether there is an edit to undo.
    /// </summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>
    /// Gets whether there is an undone edit to redo.
    /// </summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Gets the description of the edit <see cref="Undo"/> would revert, or <c>null</c> when there is none.
    /// </summary>
    public string? NextUndoDescription => _undo.Last?.Value.Description;

    /// <summary>
    /// Gets the description of the edit <see cref="Redo"/> would reapply, or <c>null</c> when there is none.
    /// </summary>
    public string? NextRedoDescription => _redo.TryPeek(out (string Description, TState State) top) ? top.Description : null;

    /// <summary>
    /// Records an edit about to be applied; forgets every undone edit, and the oldest step past the depth limit.
    /// </summary>
    /// <param name="description">The edit's name, as an Undo menu item shows it.</param>
    /// <param name="before">The state the edit replaces.</param>
    public void Record(string description, TState before)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(before);
        _redo.Clear();
        _undo.AddLast((description, before));
        if (_undo.Count > _maxDepth)
        {
            _undo.RemoveFirst();
        }
    }

    /// <summary>
    /// Reverts the last recorded edit.
    /// </summary>
    /// <param name="current">The state now shown, which <see cref="Redo"/> hands back.</param>
    /// <returns>The state before the edit, or <c>null</c> when there is nothing to undo.</returns>
    public TState? Undo(TState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_undo.Last is not { } last)
        {
            return null;
        }

        _undo.RemoveLast();
        _redo.Push((last.Value.Description, current));
        return last.Value.State;
    }

    /// <summary>
    /// Reapplies the last undone edit.
    /// </summary>
    /// <param name="current">The state now shown, which <see cref="Undo"/> hands back.</param>
    /// <returns>The state after the edit, or <c>null</c> when there is nothing to redo.</returns>
    public TState? Redo(TState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!_redo.TryPop(out (string Description, TState State) top))
        {
            return null;
        }

        _undo.AddLast((top.Description, current));
        return top.State;
    }

    /// <summary>
    /// Forgets every undo and redo step, as after opening another map.
    /// </summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
