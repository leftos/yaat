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

    /// <summary>The mini and full data-block forms (<see cref="IMenuHost.IsMinified"/>, <see cref="IMenuHost.ToggleMinified"/>).</summary>
    MiniDataBlock = 1 << 5,

    /// <summary>
    /// The data-block position reset (<see cref="IMenuHost.HasManualDataBlockOffset"/>,
    /// <see cref="IMenuHost.ResetDataBlockOffset"/>).
    /// </summary>
    DataBlockOffset = 1 << 6,

    /// <summary>The nav-route display toggle (<see cref="IMenuHost.IsPathShown"/>, <see cref="IMenuHost.ToggleShowPath"/>).</summary>
    NavRoute = 1 << 7,

    /// <summary>The measure tool (<see cref="IMenuHost.GetMeasureState"/>, <see cref="IMenuHost.MeasurePickOnAircraft"/>).</summary>
    Measure = 1 << 8,

    /// <summary>The taxi-route display mode (<see cref="IMenuHost.GetTaxiRouteMode"/>, <see cref="IMenuHost.SetTaxiRouteMode"/>).</summary>
    TaxiRouteDisplay = 1 << 9,

    /// <summary>The data-block hide toggle (<see cref="IMenuHost.IsDataBlockHidden"/>, <see cref="IMenuHost.ToggleHiddenDataBlock"/>).</summary>
    HideDataBlock = 1 << 10,

    /// <summary>Route drawing (<see cref="IMenuHost.EnterDrawRoute"/>), which the radar canvas and the ground map both serve.</summary>
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
