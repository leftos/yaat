using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One choice the host answers for a taxi flyout: the text it shows, the finished command it sends or the choices under
/// it, and the taxi route the surface previews while it is pointed at or focused. In the point menu's taxi flyouts
/// (<see cref="PointTaxiFlyout"/>) a choice with children is a title line over them, one with a command a row that sends
/// it, one with neither a disabled row, and <see cref="Separator"/> a separator line.
/// </summary>
/// <param name="Label">The text the item shows.</param>
/// <param name="Command">The finished command the choice sends for the menu's aircraft; null for a choice with children or a disabled row.</param>
/// <param name="Preview">The route the surface previews on hover; null when the choice has nothing to preview.</param>
/// <param name="Children">The choices nested under this one, <c>[]</c> for a leaf.</param>
public sealed record MenuCommandChoice(string Label, string? Command, TaxiRoute? Preview, IReadOnlyList<MenuCommandChoice> Children)
{
    /// <summary>The sentinel a choice list carries where a separator goes; recognised by reference, never by value.</summary>
    public static MenuCommandChoice Separator { get; } = new("", null, null, []);
}
