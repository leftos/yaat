using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One row of the Hold short of… submenu: a bar along the aircraft's remaining taxi route, the finished <c>HS</c>
/// command that arms it, and the route up to it the surface previews while the pointer is over the row.
/// </summary>
/// <param name="Label">What the row shows of its bar.</param>
/// <param name="Command">The finished command the row sends for the menu's aircraft (<c>HS S1</c>, <c>HS X@A</c>, <c>HS 30</c>).</param>
/// <param name="Preview">The remaining route up to the bar; no segments when the bar is the node the route starts at.</param>
public sealed record HoldShortChoice(HoldShortRowLabel Label, string Command, TaxiRoute Preview)
{
    /// <summary>The badge of a row that holds short of a taxiway.</summary>
    public const string TaxiwayBadge = "TW";

    /// <summary>The badge of a row that holds short of a runway.</summary>
    public const string RunwayBadge = "RW";
}

/// <summary>What a Hold short of… row shows of its bar.</summary>
/// <param name="Badge"><see cref="HoldShortChoice.TaxiwayBadge"/> or <see cref="HoldShortChoice.RunwayBadge"/>.</param>
/// <param name="Name">What the row holds short of: a taxiway name ("S1") or a runway ("Runway 30", "Runway 12/30").</param>
/// <param name="Where">Where along the route the bar is: "crossing on S", "before turning onto T", "at W4, end of route".</param>
/// <param name="DistanceFt">The distance from the aircraft to the bar along the route, rounded to the nearest 50 ft.</param>
public sealed record HoldShortRowLabel(string Badge, string Name, string Where, double DistanceFt);

/// <summary>
/// The Hold short of… submenu's content: the line naming the route the rows lie along, and the rows nearest first.
/// </summary>
/// <param name="RouteLine">"route S T V W4 · RWY 30" (no " · RWY" part without a runway); null with no route.</param>
/// <param name="Rows">The bars along the remaining route, nearest first.</param>
public sealed record HoldShortMenu(string? RouteLine, IReadOnlyList<HoldShortChoice> Rows)
{
    /// <summary>No route and no rows.</summary>
    public static HoldShortMenu Empty { get; } = new(null, []);
}
