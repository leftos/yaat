using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// A submenu whose items are built only when it first opens, for an entry too costly to build with every menu: it holds
/// one disabled placeholder until then. It fills when it opens as a submenu (<see cref="MenuItem.SubmenuOpened"/>), and a
/// surface that moves its items elsewhere, as the quick-command strip moves them into a flyout, fills that list through
/// <see cref="Fill"/> when it opens. Either way the items are built once. A lazy submenu may sit inside another one's
/// items; a surface that wires every item it shows registers <see cref="WhenFilled"/> for those built later.
/// </summary>
public static class LazySubmenu
{
    private sealed class PendingFill(Func<IReadOnlyList<Control>> fill)
    {
        public Func<IReadOnlyList<Control>> Fill { get; } = fill;

        public List<Action<IReadOnlyList<Control>>> AfterFill { get; } = [];
    }

    private static readonly ConditionalWeakTable<MenuItem, PendingFill> PendingFills = [];

    /// <summary>
    /// A submenu headed <paramref name="header"/> showing the disabled <paramref name="placeholder"/> until it first opens,
    /// when <paramref name="fill"/>'s items replace it.
    /// </summary>
    public static MenuItem Create(string header, string placeholder, Func<IReadOnlyList<Control>> fill)
    {
        var item = new MenuItem { Header = header };
        item.Items.Add(new MenuItem { Header = placeholder, IsEnabled = false });
        PendingFills.Add(item, new PendingFill(fill));
        item.SubmenuOpened += (_, e) =>
        {
            if (ReferenceEquals(e.Source, item))
            {
                Fill(item, item.Items);
            }
        };
        return item;
    }

    /// <summary>
    /// Builds <paramref name="entry"/>'s items into <paramref name="target"/> in place of what it holds, the first time
    /// only, hands them to every <see cref="WhenFilled"/> callback, and returns them; returns none for an entry already
    /// filled or not built by <see cref="Create"/>.
    /// </summary>
    public static IReadOnlyList<Control> Fill(MenuItem entry, ItemCollection target)
    {
        if (!PendingFills.TryGetValue(entry, out PendingFill? pending))
        {
            return [];
        }

        PendingFills.Remove(entry);
        IReadOnlyList<Control> items = pending.Fill();
        target.Clear();
        foreach (Control item in items)
        {
            target.Add(item);
        }

        foreach (Action<IReadOnlyList<Control>> afterFill in pending.AfterFill)
        {
            afterFill(items);
        }

        return items;
    }

    /// <summary>
    /// Runs <paramref name="afterFill"/> over <paramref name="entry"/>'s items once they are built, and returns true, for an
    /// entry still waiting to fill; returns false, running nothing, for any other item.
    /// </summary>
    public static bool WhenFilled(MenuItem entry, Action<IReadOnlyList<Control>> afterFill)
    {
        if (!PendingFills.TryGetValue(entry, out PendingFill? pending))
        {
            return false;
        }

        pending.AfterFill.Add(afterFill);
        return true;
    }
}
