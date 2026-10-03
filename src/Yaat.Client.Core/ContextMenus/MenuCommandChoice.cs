using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One item of a submenu whose choices the host answers: the text the item shows, the finished command it sends or the
/// choices nested under it, and the taxi route the surface previews while the pointer is over it. A choice with
/// children renders as a submenu, one with a command as an item that sends it, one with neither as a disabled row, and
/// <see cref="Separator"/> as a menu separator.
/// </summary>
/// <param name="Label">The text the item shows.</param>
/// <param name="Command">The finished command the item sends for the menu's aircraft; null for a submenu or a disabled row.</param>
/// <param name="Preview">The route the surface previews on hover; null when the choice has nothing to preview.</param>
/// <param name="Children">The choices nested under this one, <c>[]</c> for a leaf.</param>
public sealed record MenuCommandChoice(string Label, string? Command, TaxiRoute? Preview, IReadOnlyList<MenuCommandChoice> Children)
{
    /// <summary>The sentinel a choice list carries where a separator goes; recognised by reference, never by value.</summary>
    public static MenuCommandChoice Separator { get; } = new("", null, null, []);
}
