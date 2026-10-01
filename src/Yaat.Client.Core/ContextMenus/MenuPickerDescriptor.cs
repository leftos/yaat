namespace Yaat.Client.ContextMenus;

/// <summary>
/// The values a picker menu item (<see cref="List"/>, <see cref="FilteredList"/> or <see cref="Input"/>)
/// would offer if it were clicked, carried on the item's <see cref="Avalonia.Controls.Control.Tag"/> so a
/// menu-tree walker can read them without opening a popup. <see cref="Items"/> holds the display texts in
/// the order the popup lists them, and is empty for an input.
/// </summary>
internal sealed record MenuPickerDescriptor(string Kind, IReadOnlyList<string> Items)
{
    /// <summary>A popup that lists every value at once.</summary>
    public const string List = "list";

    /// <summary>A type-to-filter popup, whose <see cref="Items"/> are the values it lists before the controller types.</summary>
    public const string FilteredList = "filteredList";

    /// <summary>A free-text input, which offers no values.</summary>
    public const string Input = "input";
}
