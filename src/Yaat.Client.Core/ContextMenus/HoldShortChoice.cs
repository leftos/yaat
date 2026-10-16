using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One stop of the Hold short of… ladder: a bar along the aircraft's remaining taxi route, the finished <c>HS</c>
/// command that arms it, the route up to it and where the bar is, which the ground views preview while the row is
/// pointed at or focused.
/// </summary>
/// <param name="Label">What the row shows of its bar.</param>
/// <param name="Command">
/// The finished command the row sends for the menu's aircraft (<c>HS S1</c>, <c>HS X@A</c>, <c>HS 30</c>); also the row's
/// identity across a live refill.
/// </param>
/// <param name="Preview">The remaining route up to the bar; no segments when the bar is the node the route starts at.</param>
/// <param name="BarPoint">Where the bar is, which the ground views box.</param>
public sealed record HoldShortChoice(HoldShortRowLabel Label, string Command, TaxiRoute Preview, LatLon BarPoint)
{
    /// <summary>The badge of a row that holds short of a taxiway.</summary>
    public const string TaxiwayBadge = "TW";

    /// <summary>The badge of a row that holds short of a runway.</summary>
    public const string RunwayBadge = "RW";
}

/// <summary>What a Hold short of… row shows of its bar.</summary>
/// <param name="Badge"><see cref="HoldShortChoice.TaxiwayBadge"/> or <see cref="HoldShortChoice.RunwayBadge"/>.</param>
/// <param name="Name">What the row holds short of: a taxiway name ("S1") or a runway ("30", "12/30").</param>
/// <param name="Where">Where along the route the bar is: "crossing on S", "before turning onto T", "at W4, end of route".</param>
/// <param name="DistanceFt">The distance from the aircraft to the bar along the route, rounded to the nearest 50 ft.</param>
public sealed record HoldShortRowLabel(string Badge, string Name, string Where, double DistanceFt);

/// <summary>
/// Where the aircraft's route ends, shown dimmed and unclickable as the Hold short of… ladder's last row
/// (<c>Spot 5 · route ends here</c>).
/// </summary>
/// <param name="Name">The end: <c>Runway 30</c>, <c>Spot 5</c>, <c>Gate B22</c> or <c>Helipad H1</c>.</param>
/// <param name="IsRunway">Whether the route ends at a runway's bar (drawn with the runway bar mark) rather than a stand.</param>
/// <param name="DistanceFt">The distance from the aircraft to the end along the route, rounded to the nearest 50 ft.</param>
public sealed record HoldShortDestination(string Name, bool IsRunway, double DistanceFt)
{
    /// <summary>The note after the destination row's name.</summary>
    public const string RouteEndsHere = "route ends here";

    /// <inheritdoc />
    public override string ToString() => $"{Name} · {RouteEndsHere}";
}

/// <summary>
/// The Hold short of… ladder's content: the line naming the route, the line saying where the aircraft is now, the stops
/// nearest first and where the route ends.
/// </summary>
/// <param name="RouteLine">"route S T V W4 · RWY 30" or "route B W · spot 5" (no destination part without one); null without a route.</param>
/// <param name="MeLine">
/// Where the aircraft is now, <c>now, taxiing on D</c> or <c>now, holding short of 28R on B</c>; null for any other phase.
/// </param>
/// <param name="Rows">The stops along the remaining route, nearest first.</param>
/// <param name="Destination">Where the route ends, when it ends at a runway or a stand with at least one stop before it; else null.</param>
public sealed record HoldShortMenu(string? RouteLine, string? MeLine, IReadOnlyList<HoldShortChoice> Rows, HoldShortDestination? Destination)
{
    /// <summary>No route and no rows.</summary>
    public static HoldShortMenu Empty { get; } = new(null, null, [], null);
}
