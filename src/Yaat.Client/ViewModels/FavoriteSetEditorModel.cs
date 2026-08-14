namespace Yaat.Client.ViewModels;

/// <summary>
/// Pure list-surgery helpers behind the Favorites Editor window: reordering a multi-selection
/// within one container's ordered id list. All methods mutate the passed list in place; the
/// window commits the result back through <see cref="Yaat.Client.Services.FavoriteStore"/>.
/// </summary>
public static class FavoriteSetEditorModel
{
    /// <summary>
    /// Moves every selected index up by one, preserving the selection's relative order. A selected
    /// block already at the top stays put. Returns the new indices of the moved items.
    /// </summary>
    public static List<int> MoveUp<T>(List<T> list, IReadOnlyCollection<int> selectedIndices)
    {
        var moved = new List<int>();
        var blocked = -1;
        foreach (var index in selectedIndices.Distinct().Order())
        {
            if (index < 0 || index >= list.Count)
            {
                continue;
            }

            // An item can't move up when it's at the top or the slot above is held by a
            // selected item that itself couldn't move (a contiguous blocked run).
            if (index == 0 || index - 1 == blocked)
            {
                blocked = index;
                moved.Add(index);
                continue;
            }

            (list[index - 1], list[index]) = (list[index], list[index - 1]);
            moved.Add(index - 1);
        }
        return moved;
    }

    /// <summary>
    /// Moves every selected index down by one, preserving the selection's relative order. A selected
    /// block already at the bottom stays put. Returns the new indices of the moved items.
    /// </summary>
    public static List<int> MoveDown<T>(List<T> list, IReadOnlyCollection<int> selectedIndices)
    {
        var moved = new List<int>();
        var blocked = list.Count;
        foreach (var index in selectedIndices.Distinct().OrderDescending())
        {
            if (index < 0 || index >= list.Count)
            {
                continue;
            }

            if (index == list.Count - 1 || index + 1 == blocked)
            {
                blocked = index;
                moved.Add(index);
                continue;
            }

            (list[index + 1], list[index]) = (list[index], list[index + 1]);
            moved.Add(index + 1);
        }
        moved.Reverse();
        return moved;
    }
}
