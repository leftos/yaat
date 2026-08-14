using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Pure list-surgery helpers behind the Favorites Editor window: reordering a multi-selection
/// within one container and copying/moving selections between containers (the base pool and
/// named sets). All methods mutate the passed lists in place; the window commits the results
/// back through <see cref="UserPreferences"/>.
/// </summary>
public static class FavoriteSetEditorModel
{
    /// <summary>
    /// Moves every selected index up by one, preserving the selection's relative order. A selected
    /// block already at the top stays put. Returns the new indices of the moved items.
    /// </summary>
    public static List<int> MoveUp(List<FavoriteCommand> list, IReadOnlyCollection<int> selectedIndices)
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
    public static List<int> MoveDown(List<FavoriteCommand> list, IReadOnlyCollection<int> selectedIndices)
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

    /// <summary>
    /// Appends clones of the selected favorites onto the target list. Clones (not references) so a
    /// later edit in one container cannot bleed into the other. The source list is untouched.
    /// </summary>
    public static void CopyTo(List<FavoriteCommand> source, List<FavoriteCommand> target, IReadOnlyCollection<int> selectedIndices)
    {
        foreach (var index in selectedIndices.Distinct().Order())
        {
            if (index >= 0 && index < source.Count)
            {
                target.Add(source[index].Clone());
            }
        }
    }

    /// <summary>
    /// Transfers the selected favorites from the source list to the end of the target list,
    /// preserving their relative order. References move as-is (no clone needed — each favorite
    /// ends up in exactly one container).
    /// </summary>
    public static void MoveTo(List<FavoriteCommand> source, List<FavoriteCommand> target, IReadOnlyCollection<int> selectedIndices)
    {
        var indices = selectedIndices.Distinct().Where(i => (i >= 0) && (i < source.Count)).Order().ToList();
        foreach (var index in indices)
        {
            target.Add(source[index]);
        }
        foreach (var index in indices.AsEnumerable().Reverse())
        {
            source.RemoveAt(index);
        }
    }
}
