using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The YAAT Scope: a scope canvas drawing traffic with the datablock template engine. A left click on a target or block
/// selects that aircraft; a right click opens the shared aircraft menu. A left click on an ERAM block's callsign (Field A)
/// also toggles the instructor's local dwell lock (<see cref="ScopeViewModel.ToggleDwellLock"/>); no field click sends a command.
/// While the Rings tool is armed a left click places a ring instead (<see cref="ScopeViewModel.PlaceRing"/>), selecting
/// nothing and toggling no lock, and Esc disarms it. Ctrl+T (Cmd+T on macOS) turns top-down mode on or off through
/// <c>.scope tdm</c>, as the TDM button does. Both keys are taken by a window-level handler that runs in the tunnel, so it
/// acts before any control inside the window sees the key.
/// </summary>
public partial class ScopeView : UserControl
{
    private ContextMenu? _activeContextMenu;
    private ScopeViewModel? _vm;
    private TopLevel? _topLevel;

    public ScopeView()
    {
        InitializeComponent();
        Canvas.AircraftLeftClicked += OnAircraftLeftClicked;
        Canvas.AircraftRightClicked += OnAircraftRightClicked;
        Canvas.AircraftDwellLockToggleRequested += OnAircraftDwellLockToggleRequested;
    }

    /// <summary>The aircraft menu a right click opened, while it is open. Test seam.</summary>
    internal ContextMenu? ActiveContextMenu => _activeContextMenu;

    /// <summary>
    /// The aircraft menu for <paramref name="callsign"/>: the shared aircraft menu, with relative actions aimed at
    /// <paramref name="prevSelected"/>, with no view section of the scope's own.
    /// </summary>
    internal ContextMenu BuildAircraftMenu(ScopeViewModel vm, AircraftModel? ac, AircraftModel? prevSelected, string callsign)
    {
        var host = new ClientMenuHost(vm.Main, ac, Canvas, vm.Main.Radar.MvaFacilityId);
        return AircraftMenuBuilder.Build(ac, new MenuClick(callsign, prevSelected, null, []), host, _ => []);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm?.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = DataContext as ScopeViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Canvas.Preferences = _vm.Preferences;
        Canvas.DataBlockState = _vm.DataBlockState;
        PushMaps(_vm);
    }

    // The maps arrive later when the scope opened before the radar had any (its one-time seed).
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((sender is ScopeViewModel vm) && (e.PropertyName is nameof(ScopeViewModel.VideoMaps) or nameof(ScopeViewModel.BrightnessLookup)))
        {
            PushMaps(vm);
        }
    }

    private void PushMaps(ScopeViewModel vm)
    {
        Canvas.VideoMaps = vm.VideoMaps;
        Canvas.BrightnessLookup = vm.BrightnessLookup;
        Canvas.MarkDirty();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    // Esc disarms an armed Rings tool and Ctrl+T flips top-down mode; any other key, or Esc with the tool disarmed, passes on.
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ScopeViewModel vm)
        {
            return;
        }

        if ((e.Key == Key.Escape) && vm.DisarmRingTool())
        {
            e.Handled = true;
        }
        else if ((e.Key == Key.T) && (e.KeyModifiers == (PlatformHelper.IsMacOS ? KeyModifiers.Meta : KeyModifiers.Control)))
        {
            vm.RunCommand(ScopeToolbarButtons.TdmCommand(!vm.IsTdm));
            e.Handled = true;
        }
    }

    private void OnAircraftLeftClicked(string callsign)
    {
        if (DataContext is not ScopeViewModel vm)
        {
            return;
        }

        if (vm.IsRingToolArmed)
        {
            vm.PlaceRing(callsign);
        }
        else
        {
            vm.SelectAircraft(callsign);
        }
    }

    private void OnAircraftDwellLockToggleRequested(string callsign)
    {
        if ((DataContext is ScopeViewModel vm) && !vm.IsRingToolArmed)
        {
            vm.ToggleDwellLock(callsign);
        }
    }

    private void OnAircraftRightClicked(string callsign, Point _)
    {
        if (DataContext is not ScopeViewModel vm)
        {
            return;
        }

        // As on the radar: keep the selected aircraft as the recipient of relative actions when a different aircraft is
        // right-clicked; adopt the right-clicked one only when nothing is selected or it is the selected one.
        AircraftModel? ac = vm.FindAircraft(callsign);
        AircraftModel? prevSelected = vm.SelectedAircraft;
        if ((ac is not null) && ((prevSelected is null) || string.Equals(prevSelected.Callsign, callsign, StringComparison.OrdinalIgnoreCase)))
        {
            vm.SelectedAircraft = ac;
        }

        ShowContextMenu(BuildAircraftMenu(vm, ac, prevSelected, callsign));
    }

    private void ShowContextMenu(ContextMenu menu)
    {
        _activeContextMenu?.Close();
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (_activeContextMenu == menu)
            {
                _activeContextMenu = null;
            }
        };
        menu.PlacementTarget = Canvas;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(Canvas);
    }
}
