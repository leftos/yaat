using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The YAAT Scope: a scope canvas drawing traffic with the datablock template engine. A left click on a target or block
/// selects that aircraft; a right click opens the shared aircraft menu. A field click sends no command.
/// </summary>
public partial class ScopeView : UserControl
{
    private ContextMenu? _activeContextMenu;
    private ScopeViewModel? _vm;

    public ScopeView()
    {
        InitializeComponent();
        Canvas.AircraftLeftClicked += OnAircraftLeftClicked;
        Canvas.AircraftRightClicked += OnAircraftRightClicked;
    }

    /// <summary>The aircraft menu a right click opened, while it is open. Test seam.</summary>
    internal ContextMenu? ActiveContextMenu => _activeContextMenu;

    /// <summary>
    /// The aircraft menu for <paramref name="callsign"/>: the shared aircraft menu, with relative actions aimed at
    /// <paramref name="prevSelected"/>, with no view section of the scope's own.
    /// </summary>
    internal ContextMenu BuildAircraftMenu(ScopeViewModel vm, AircraftModel? ac, AircraftModel? prevSelected, string callsign)
    {
        var host = new ClientMenuHost(vm.Main, ac, Canvas);
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
        Canvas.MapBrightnessA = _vm.MapBrightnessA;
        Canvas.MapBrightnessB = _vm.MapBrightnessB;
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

    private void OnAircraftLeftClicked(string callsign) => (DataContext as ScopeViewModel)?.SelectAircraft(callsign);

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
