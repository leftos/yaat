namespace Yaat.Client.ContextMenus;

/// <summary>
/// One entry of a situation's quick-command list: a catalog action (<see cref="CatalogQuickCommandEntry"/>) or a command
/// the controller typed (<see cref="CustomQuickCommandEntry"/>).
/// </summary>
public abstract record QuickCommandEntry
{
    private protected QuickCommandEntry() { }
}

/// <summary>
/// A catalog action in a quick-command list, and the flight rules it is offered under when they differ from the action's
/// catalog default.
/// </summary>
/// <param name="CatalogId">The catalog action's stable identifier (see <see cref="MenuIds"/>).</param>
/// <param name="FlightRules">
/// The flight rules this entry is offered under; null takes the catalog entry's <see cref="MenuCatalogEntry.DefaultFlightRules"/>.
/// </param>
public sealed record CatalogQuickCommandEntry(string CatalogId, MenuFlightRules? FlightRules) : QuickCommandEntry;

/// <summary>
/// A command the controller typed into a quick-command list: it shows as text, never in the icon strip, and sends its
/// command text, or its ground command text when the aircraft is on the ground and one is set, as a favorite does.
/// </summary>
/// <param name="Label">The text the menu item shows.</param>
/// <param name="CommandText">The command sent for the menu's aircraft.</param>
/// <param name="GroundCommandText">The command sent instead while the aircraft is on the ground; null or blank for none.</param>
/// <param name="FlightRules">The flight rules this entry is offered under.</param>
public sealed record CustomQuickCommandEntry(string Label, string CommandText, string? GroundCommandText, MenuFlightRules FlightRules)
    : QuickCommandEntry
{
    /// <summary>
    /// The menu identifier every resolved custom entry carries. No catalog action and no strip glyph has it, so a custom
    /// entry always lists as text.
    /// </summary>
    public const string MenuId = "quick.custom";

    /// <summary>
    /// The command this entry sends for <paramref name="aircraft"/>, trimmed: the ground command text while the aircraft
    /// is on the ground and one is set, else the command text.
    /// </summary>
    public string CommandFor(IMenuAircraft aircraft) =>
        (aircraft.IsOnGround && !string.IsNullOrWhiteSpace(GroundCommandText)) ? GroundCommandText.Trim() : CommandText.Trim();
}
