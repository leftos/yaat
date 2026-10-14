using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Yaat.Client.ContextMenus;

/// <summary>Brings a live flyout's or submenu's items to a wanted list in place, so a surviving row keeps its pointer-over and focus.</summary>
internal static class FlyoutItemSync
{
    /// <summary>
    /// Moves <paramref name="items"/> to read <paramref name="wanted"/>, in order, touching only what differs: an item in
    /// both stays where it is unless the order moved it, so a surviving row keeps its pointer-over and its focus. An item
    /// collection has no move, and an item taken out loses its focus, so <paramref name="anchor"/> is never taken out:
    /// when it is due further up than it sits, the items in front of it are taken out instead, each going back in at its
    /// own turn.
    /// </summary>
    /// <param name="items">The flyout's or submenu's items.</param>
    /// <param name="wanted">The items it should hold, in order.</param>
    /// <param name="anchor">The item holding keyboard focus, which keeps it; null when none does.</param>
    public static void Sync(ItemCollection items, IReadOnlyList<Control> wanted, Control? anchor)
    {
        var keep = new HashSet<object?>(wanted, ReferenceEqualityComparer.Instance);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(items[i]))
            {
                items.RemoveAt(i);
            }
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            if ((i < items.Count) && ReferenceEquals(items[i], wanted[i]))
            {
                continue;
            }

            Place(items, wanted[i], i, anchor);
        }
    }

    /// <summary>Brings <paramref name="item"/> to <paramref name="index"/>, every item above it already in its wanted place.</summary>
    private static void Place(ItemCollection items, Control item, int index, Control? anchor)
    {
        int at = items.IndexOf(item);
        if (at < 0)
        {
            items.Insert(index, item);
            return;
        }

        if (ReferenceEquals(item, anchor))
        {
            // Every item between here and the anchor is wanted further down, so each goes back in at its own turn.
            for (int ahead = at - index; ahead > 0; ahead--)
            {
                items.RemoveAt(index);
            }

            return;
        }

        items.RemoveAt(at);
        items.Insert(index, item);
    }

    /// <summary>The item among <paramref name="wanted"/> holding keyboard focus, or within which it is; null when none does.</summary>
    public static Control? FocusedIn(IReadOnlyList<Control> wanted) => wanted.FirstOrDefault(item => item.IsKeyboardFocusWithin);

    /// <summary>
    /// Moves a panel's <paramref name="children"/> to read <paramref name="wanted"/>, in order, touching only what differs:
    /// a child in both is moved in place, never taken out of the panel, so it keeps its focus even when the order moved it.
    /// </summary>
    public static void Sync(Controls children, IReadOnlyList<Control> wanted)
    {
        var keep = new HashSet<Control>(wanted, ReferenceEqualityComparer.Instance);
        for (int i = children.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(children[i]))
            {
                children.RemoveAt(i);
            }
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            if ((i < children.Count) && ReferenceEquals(children[i], wanted[i]))
            {
                continue;
            }

            int at = children.IndexOf(wanted[i]);
            if (at >= 0)
            {
                children.Move(at, i);
            }
            else
            {
                children.Insert(i, wanted[i]);
            }
        }
    }
}

/// <summary>Keyboard focus for a flyout row holding a text box.</summary>
internal static class FlyoutBoxFocus
{
    /// <summary>
    /// Passes keyboard focus that lands on <paramref name="item"/> itself on to the text box in its header. A menu selects
    /// and focuses the row under the pointer or the arrow keys, and focuses it again once a click in its box is over,
    /// which would leave the box unable to take typing; handed on, the caret goes into the box instead.
    /// </summary>
    public static void HandFocusToTheBox(MenuItem item) =>
        item.AddHandler(
            InputElement.GotFocusEvent,
            (_, e) =>
            {
                if (ReferenceEquals(e.Source, item) && (item.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box))
                {
                    box.Focus(e.NavigationMethod);
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true
        );
}
