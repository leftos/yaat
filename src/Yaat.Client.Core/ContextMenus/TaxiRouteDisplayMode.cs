namespace Yaat.Client.ContextMenus;

/// <summary>Per-aircraft override for how its taxi route is drawn on the ground view.</summary>
public enum TaxiRouteDisplayMode
{
    /// <summary>Track the global "show all taxiing routes" setting (the default, no override).</summary>
    Follow,

    /// <summary>Always draw this aircraft's route, regardless of the global setting.</summary>
    AlwaysShow,

    /// <summary>Never draw this aircraft's route, regardless of the global setting.</summary>
    AlwaysHide,
}
