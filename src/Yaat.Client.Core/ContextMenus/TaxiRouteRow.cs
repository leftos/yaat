using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One taxi route a ground submenu offers as a command row: a preset taxi route, or a runway entry point of Taxi to runway.
/// It shows its badge, name, why it is offered, the taxiways it follows and its distance from the aircraft, sends
/// <see cref="Command"/> (a leaf) or opens <see cref="Variants"/> (a runway entry), and previews <see cref="Preview"/> on hover.
/// </summary>
/// <param name="Badge"><see cref="RunwayEntryBadge"/> for a runway entry point, <see cref="PresetBadge"/> for a preset route.</param>
/// <param name="Name">What the row names: the entry's hold short (<c>At W4</c>) or the preset's name (<c>TERMINAL to 30</c>).</param>
/// <param name="Reason">
/// Why an entry is offered (<c>nearest</c>, <c>full length</c>, <c>nearest, full length</c>); null for a preset and for an
/// intersection entry that is not the nearest.
/// </param>
/// <param name="AvailableFt">
/// The runway an intersection entry leaves ahead of a departure, rounded down to 50 ft, shown as <c>~7,600 ft avail</c>;
/// null for a full-length entry and a preset.
/// </param>
/// <param name="IsHighlighted">Whether the row is drawn bold: an end's full-length entry and its nearest one.</param>
/// <param name="Via">The taxiways the route follows (<c>via S T V W4</c>), or <c>direct</c>.</param>
/// <param name="DistanceFt">The distance from the aircraft along the route to its end, rounded to the nearest 50 ft.</param>
/// <param name="Command">
/// The finished command the row shows: what a preset sends, or the first of a runway entry's <see cref="Variants"/>.
/// </param>
/// <param name="Preview">The route from the aircraft the surface previews while the pointer is over the row.</param>
/// <param name="Variants">
/// The commands a runway entry opens (For departure, Hold short of the runway, and their crossing variants, with
/// <see cref="MenuCommandChoice.Separator"/> between the two kinds); <c>[]</c> for a preset, which sends <see cref="Command"/>.
/// </param>
public sealed record TaxiRouteRow(
    string Badge,
    string Name,
    string? Reason,
    double? AvailableFt,
    bool IsHighlighted,
    string Via,
    double DistanceFt,
    string Command,
    TaxiRoute Preview,
    IReadOnlyList<MenuCommandChoice> Variants
)
{
    /// <summary>The badge of a runway entry point's row.</summary>
    public const string RunwayEntryBadge = "RW";

    /// <summary>The badge of a preset taxi route's row.</summary>
    public const string PresetBadge = "PR";
}

/// <summary>One runway end's rows in the Taxi to runway submenu, under its title.</summary>
/// <param name="Title">
/// The group's title: <c>Runway 30 · assigned runway</c>, <c>Runway 28L · departure runway</c>, or <c>Runway 30</c> for an
/// end offered for neither reason (inline when the room names no active runway, or under Other runways).
/// </param>
/// <param name="Rows">
/// The end's entry points, the full-length one first and then the intersections from the threshold inward, then the
/// presets that end at it.
/// </param>
public sealed record TaxiToRunwayGroup(string Title, IReadOnlyList<TaxiRouteRow> Rows);

/// <summary>
/// The Taxi to runway submenu's content: the groups shown inline, then those under Other runways, found already or
/// searched only when Other runways opens.
/// </summary>
/// <param name="Inline">
/// The assigned runway's end first, then the room's active departure ends in the room's order; with no active departure
/// runway at the airport, the ends nearest the aircraft, up to three counting the assigned one.
/// </param>
/// <param name="Other">Every other end with a row, in runway order, when already found; empty when <paramref name="FindOther"/> is set.</param>
/// <param name="FindOther">
/// Searches the other ends' rows (in runway order) when Other runways opens, for a menu whose inline ends come from the
/// room's active departure runways; null when <paramref name="Other"/> already holds them or no runway end is left.
/// </param>
public sealed record TaxiToRunwayMenu(
    IReadOnlyList<TaxiToRunwayGroup> Inline,
    IReadOnlyList<TaxiToRunwayGroup> Other,
    Func<IReadOnlyList<TaxiToRunwayGroup>>? FindOther
)
{
    /// <summary>No group at all.</summary>
    public static TaxiToRunwayMenu Empty { get; } = new([], [], null);
}
