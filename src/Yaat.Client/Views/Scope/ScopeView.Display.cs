using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope aircraft menu's view section: its Display submenu, built from the Scope's own state — the hidden datablock and its
/// position reset, the pilot lens's intent path as the track's nav route, the measure tool and the Scope-local J-ring and
/// cone, then the shared send rows (leader direction, blank and unblank) — and the menu's "Settings for this view…".
/// </summary>
public partial class ScopeView
{
    partial void AddDisplaySection(List<Control> items, ScopeViewModel vm, AircraftModel? ac, MenuContext context) =>
        items.Add(BuildCanvasDisplay(vm, ac, context));

    /// <summary>
    /// The Display submenu for <paramref name="ac"/>, the aircraft of <paramref name="context"/>: the block's hide row and
    /// position reset, the nav route and the measurement in progress, then leader direction, J-ring and cone, then blank and
    /// unblank. Display shows for every aircraft, live-traffic shadows included.
    /// </summary>
    internal MenuItem BuildCanvasDisplay(ScopeViewModel vm, AircraftModel? ac, MenuContext context)
    {
        string callsign = context.Callsign;
        bool hidden = vm.IsDataBlockHidden(callsign);
        var host = new ClientMenuHost(vm.Main, ac, Canvas, vm.Main.ScopeMaps.MvaFacilityId);
        return CanvasMenuItems.Display([
            [
                CanvasMenuItems.HideDataBlock(hidden, () => SetDataBlockHidden(vm, callsign, !hidden)),
                CanvasMenuItems.ResetDataBlockPosition(vm.HasManualDataBlockOffset(callsign), () => vm.ResetDataBlockPosition(callsign)),
                CanvasMenuItems.NavRoute(vm.IsNavRouteShown(callsign), () => vm.ToggleNavRoute(callsign)),
                CanvasMenuItems.Measure(MeasureState(vm), callsign, () => vm.PickMeasurePoint(RblEndpoint.OnAircraft(callsign))),
            ],
            [
                CanvasMenuItems.LeaderDirection(context, host),
                RingToggle("J-ring", vm, callsign, ScopeRingTool.JRing),
                RingToggle("Cone", vm, callsign, ScopeRingTool.Cone),
            ],
            [CanvasMenuItems.Blank(context, host), CanvasMenuItems.Unblank(context, host)],
        ]);
    }

    // The row acts on the state it was built from: a "Hide datablock" row clicked after the block was hidden elsewhere (a
    // Shift+click, the automation) hides it again rather than showing it.
    private static void SetDataBlockHidden(ScopeViewModel vm, string callsign, bool hide)
    {
        if (hide)
        {
            vm.HideDataBlock(callsign);
        }
        else
        {
            vm.ShowDataBlock(callsign);
        }
    }

    // The J-ring and Cone rows are on or off: a checked row removes the ring at whatever size the track carries, an unchecked
    // one places it at the Rings tool's current SIZE. Resizing stays the Rings tool's job.
    private static MenuItem RingToggle(string header, ScopeViewModel vm, string callsign, ScopeRingTool tool)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = vm.HasRing(callsign, tool),
        };
        item.Click += (_, _) => vm.ToggleRing(callsign, tool);
        return item;
    }

    // The Scope's measure tool is never absent, so the row offers "Measure to" once something is anchored and "Measure from" before;
    // an anchor another Scope window or the Ground view holds counts as none here.
    private static MenuMeasureState MeasureState(ScopeViewModel vm) =>
        vm.Measure.HasAnchorFor(ScopeViewModel.MeasureView, vm) ? MenuMeasureState.HasAnchor : MenuMeasureState.NoAnchor;

    /// <summary>Ends <paramref name="menu"/> with "Settings for this view…", which opens Settings at the Scope's section.</summary>
    private static void AppendViewSettings(ContextMenu menu, ScopeViewModel vm) => ViewSettingsMenu.Append(menu, vm.Main, SettingsSectionId.Radar);
}
