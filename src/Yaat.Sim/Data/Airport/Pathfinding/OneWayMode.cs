namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// How a search enforces per-airport one-way taxiway constraints.
/// <list type="bullet">
/// <item><see cref="Off"/> — the airport has no one-way constraints; no enforcement.</item>
/// <item><see cref="HardExclude"/> — auto-routes: forbidden directed moves are skipped outright so the
/// router never travels the wrong way. <see cref="Yaat.Sim.Data.Airport.TaxiPathfinder"/> relaxes this
/// to <see cref="Warn"/> on a second pass when a destination is otherwise unreachable.</item>
/// <item><see cref="Warn"/> — explicit named-taxiway clearances (and the auto fallback): the route is
/// allowed to traverse a one-way span the wrong way, but <see cref="RouteMaterialiser"/> emits a
/// "against one-way direction" warning.</item>
/// </list>
/// </summary>
public enum OneWayMode
{
    Off,
    HardExclude,
    Warn,
}
