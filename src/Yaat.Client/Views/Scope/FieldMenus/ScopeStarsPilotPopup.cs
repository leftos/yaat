using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>Which STARS field <see cref="ScopeStarsPilotPopup"/> opens for.</summary>
internal enum StarsPilotPopupKind
{
    /// <summary>The altitude: the PILOT Maintain altitude panel.</summary>
    Altitude,

    /// <summary>The ground speed: the PILOT Assign speed panel.</summary>
    Speed,
}

/// <summary>
/// The popup a click on a STARS block's altitude or ground speed opens in the Tabbed style of the STARS field menus: the ERAM
/// tabbed popup's frame with one PILOT tab, holding the same pilot panel its PILOT tab shows (the Maintain altitude panel, or
/// the Assign speed panel), placed below the block as that popup is. STARS has no scope-data column, so there is no ERAM tab.
/// The panel sends through the host; the chrome, Shift, Esc and the light dismiss are <see cref="ScopeDataPopupBase"/>'s.
/// </summary>
internal sealed class ScopeStarsPilotPopup : ScopeDataPopupBase
{
    // The altitude popup when `altitudePanel` is given, else the speed popup.
    private ScopeStarsPilotPopup(
        AircraftModel aircraft,
        IMenuHost host,
        ScopeDataPopupHooks hooks,
        Func<Func<string, Task>, MaintainAltitudePanel>? altitudePanel
    )
        : base(aircraft.Callsign, hooks)
    {
        Func<string, Task> choose = Chooser(host);
        Kind = (altitudePanel is null) ? StarsPilotPopupKind.Speed : StarsPilotPopupKind.Altitude;
        AltitudePanel = altitudePanel?.Invoke(choose);
        SpeedPanel = (altitudePanel is null) ? SpeedFlyout.AssignPanelFactory(aircraft)(choose, Close) : null;
        Control panel = (Control?)AltitudePanel ?? SpeedPanel!;
        Attach(PilotOnlyPanel(PilotColumn([panel])), hooks.RecordShift, host, [(IFlyoutPanel)panel]);
    }

    /// <summary>
    /// The popup of <paramref name="kind"/> for <paramref name="aircraft"/>; null for the altitude when no altitude is listed for
    /// it, as the catalog's Maintain altitude entry then has no panel either.
    /// </summary>
    /// <param name="aircraft">The clicked track.</param>
    /// <param name="host">The host the PILOT panel sends through and reads the field elevation and the clock from.</param>
    /// <param name="hooks">Shift; the popup has no SCOPE DATA rows to pick and remembers no column.</param>
    /// <param name="kind">Which panel the popup holds.</param>
    public static ScopeStarsPilotPopup? Create(AircraftModel aircraft, IMenuHost host, ScopeDataPopupHooks hooks, StarsPilotPopupKind kind)
    {
        if (kind == StarsPilotPopupKind.Speed)
        {
            return new ScopeStarsPilotPopup(aircraft, host, hooks, null);
        }

        return (AltitudeFlyout.MaintainPanelFactory(aircraft, host) is { } altitudePanel)
            ? new ScopeStarsPilotPopup(aircraft, host, hooks, altitudePanel)
            : null;
    }

    /// <summary>Which panel the popup holds.</summary>
    public StarsPilotPopupKind Kind { get; }

    /// <summary>The PILOT Maintain altitude panel; null in the speed popup.</summary>
    public MaintainAltitudePanel? AltitudePanel { get; }

    /// <summary>The PILOT Assign speed panel; null in the altitude popup.</summary>
    public SpeedFlyoutPanel? SpeedPanel { get; }

    /// <inheritdoc/>
    protected override TextBox? FocusBox => null;

    // The PILOT tab focuses the Maintain altitude panel's box; the speed panel has none, so the frame takes the focus.
    protected override Control? PilotFocus => AltitudePanel?.Box;

    // The altitude list centres on the selected altitude once the popup has the focus.
    protected override void AfterFocus() => AltitudePanel?.CentreSelected();
}
