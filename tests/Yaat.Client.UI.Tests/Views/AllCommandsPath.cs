using Avalonia.Controls;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>Walks a built aircraft menu to All Commands and to one of its task groups (<see cref="MenuCategory"/>).</summary>
internal static class AllCommandsPath
{
    /// <summary>The menu's top-level All Commands submenu.</summary>
    public static MenuItem AllCommands(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == AircraftMenuBuilder.AllCommandsHeader);

    /// <summary>
    /// All Commands' group for <paramref name="category"/>: the submenu headed with the category's label, or with the label
    /// followed by its state text in parentheses (<c>Approach (exp: I28R)</c>).
    /// </summary>
    public static MenuItem Group(ContextMenu menu, MenuCategory category) =>
        FindGroup(menu, category) ?? throw new InvalidOperationException($"All Commands has no '{MenuCategories.Label(category)}' group.");

    /// <summary>All Commands' group for <paramref name="category"/> as <see cref="Group"/> finds it, or null when the menu leaves it out.</summary>
    public static MenuItem? FindGroup(ContextMenu menu, MenuCategory category)
    {
        string label = MenuCategories.Label(category);
        return AllCommands(menu)
            .Items.OfType<MenuItem>()
            .SingleOrDefault(item =>
                (item.Header is string header) && ((header == label) || header.StartsWith(label + " (", StringComparison.Ordinal))
            );
    }
}
