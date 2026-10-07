namespace Yaat.Client.ContextMenus;

/// <summary>
/// The values a picker menu item (<see cref="List"/>, <see cref="FilteredList"/>, <see cref="Input"/> or
/// <see cref="Grouped"/>) would offer if it were clicked, carried on the item's <see cref="Avalonia.Controls.Control.Tag"/>
/// so a menu-tree walker can read them without opening a popup. <see cref="Items"/> holds the display texts in the order
/// the popup lists them, and is empty for an input; a grouped picker's choices are its own child items, and its
/// <see cref="Items"/> name each group on one line instead.
/// </summary>
internal sealed record MenuPickerDescriptor(string Kind, IReadOnlyList<string> Items)
{
    /// <summary>A popup that lists every value at once.</summary>
    public const string List = "list";

    /// <summary>A type-to-filter popup, whose <see cref="Items"/> are the values it lists before the controller types.</summary>
    public const string FilteredList = "filteredList";

    /// <summary>A free-text input, which offers no values.</summary>
    public const string Input = "input";

    /// <summary>
    /// A submenu whose choices are its own child items, grouped under disabled header items and nested submenus (the
    /// approach pickers). <see cref="Items"/> holds one line per group, a heading and what it holds
    /// (<c>Runway 30 · assigned: I30, L30</c>, <c>Other runways: 10L, 12</c>), or the runways alone with no default runway.
    /// </summary>
    public const string Grouped = "grouped";
}
