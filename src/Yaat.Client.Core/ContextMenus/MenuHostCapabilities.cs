namespace Yaat.Client.ContextMenus;

/// <summary>
/// What a surface's menu host can serve. A host declares a flag exactly when it implements every member of the family
/// the flag names — the ones that throw <see cref="NotSupportedException"/> on that surface leave the flag out. A
/// catalog entry whose <see cref="MenuCatalogEntry.Requires"/> names a flag the host does not declare is hidden from the
/// menu, never shown disabled.
/// </summary>
[Flags]
public enum MenuHostCapabilities
{
    /// <summary>No family: the host serves only the members every host serves.</summary>
    None = 0,

    /// <summary>The free-text input popup (<see cref="IMenuHost.ShowInputPopup"/>).</summary>
    InputPopup = 1 << 0,

    /// <summary>The list popup over a fixed set of values (<see cref="IMenuHost.ShowListPopup"/>).</summary>
    ListPicker = 1 << 1,

    /// <summary>The type-to-filter picker (<see cref="IMenuHost.ShowFilteredListPopup"/>, <see cref="IMenuHost.FixNames"/>).</summary>
    FilteredListPicker = 1 << 2,

    /// <summary>The warp popup (<see cref="IMenuHost.ShowWarpPopup"/>).</summary>
    Warp = 1 << 3,

    /// <summary>The flight-plan editor (<see cref="IMenuHost.OpenFlightPlanEditor"/>).</summary>
    FlightPlanEditor = 1 << 4,

    /// <summary>Route drawing (<see cref="IMenuHost.EnterDrawRoute"/>), which the ground map serves for the ground's Draw taxi route item.</summary>
    DrawRoute = 1 << 11,

    /// <summary>
    /// The ground-movement submenus (<see cref="IMenuHost.GetGroundTrafficCallsigns"/>,
    /// <see cref="IMenuHost.GetHoldShortChoices"/>, <see cref="IMenuHost.SetRoutePreview"/>,
    /// <see cref="IMenuHost.GetPushbackFaceChoices"/>, <see cref="IMenuHost.GetPushbackToChoices"/>,
    /// <see cref="IMenuHost.GetPresetTaxiChoices"/>, <see cref="IMenuHost.EnterPushRoute"/>).
    /// </summary>
    GroundMovement = 1 << 12,

    /// <summary>The multi-selection assume of live-traffic shadows (<see cref="IMenuHost.AssumeSelectedLiveTrafficAsync"/>).</summary>
    MultiSelectAssume = 1 << 13,
}
