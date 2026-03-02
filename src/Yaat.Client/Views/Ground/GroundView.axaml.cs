using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Ground;

public partial class GroundView : UserControl
{
    private GroundCanvas? _canvas;
    private ContextMenu? _activeContextMenu;

    public GroundView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _canvas = this.FindControl<GroundCanvas>("Canvas");
        if (_canvas is null)
        {
            return;
        }

        _canvas.NodeRightClicked += OnNodeRightClicked;
        _canvas.AircraftRightClicked += OnAircraftRightClicked;
        _canvas.MapRightClicked += OnMapRightClicked;
        _canvas.AircraftLeftClicked += OnAircraftLeftClicked;
        _canvas.PointerPressed += OnCanvasPointerPressed;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        if (_canvas is not null)
        {
            _canvas.NodeRightClicked -= OnNodeRightClicked;
            _canvas.AircraftRightClicked -= OnAircraftRightClicked;
            _canvas.MapRightClicked -= OnMapRightClicked;
            _canvas.AircraftLeftClicked -= OnAircraftLeftClicked;
            _canvas.PointerPressed -= OnCanvasPointerPressed;
        }
    }

    private void OnAircraftLeftClicked(string callsign)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        var mainVm = FindMainViewModel();
        if (mainVm is null)
        {
            return;
        }

        var ac = mainVm.Aircraft.FirstOrDefault(
            a => a.Callsign == callsign);
        if (ac is not null)
        {
            mainVm.SelectedAircraft = ac;
            vm.SelectedAircraft = ac;
        }
    }

    private void OnNodeRightClicked(int nodeId, Point screenPos)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        var node = vm.GetNode(nodeId);
        if (node is null)
        {
            return;
        }

        var menu = new ContextMenu();

        if (vm.SelectedAircraft is not null)
        {
            var callsign = vm.SelectedAircraft.Callsign;
            var initials = GetInitials();

            menu.Items.Add(CreateMenuItem(
                $"Taxi to {node.Name ?? $"node {nodeId}"}",
                () => vm.TaxiToNodeAsync(callsign, initials, nodeId)));

            if (node.Type == "RunwayHoldShort" && node.RunwayId is not null)
            {
                menu.Items.Add(CreateMenuItem(
                    $"Hold short {node.RunwayId}",
                    () => vm.TaxiToNodeAsync(callsign, initials, nodeId)));
            }

            if (node.Type == "Parking")
            {
                menu.Items.Add(CreateMenuItem(
                    $"Park at {node.Name ?? "spot"}",
                    () => vm.TaxiToNodeAsync(callsign, initials, nodeId)));
            }
        }

        if (menu.Items.Count > 0)
        {
            ShowContextMenu(menu, screenPos);
        }
    }

    private void OnAircraftRightClicked(string callsign, Point screenPos)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        // Select the aircraft
        var mainVm = FindMainViewModel();
        var ac = mainVm?.Aircraft.FirstOrDefault(
            a => a.Callsign == callsign);
        if (ac is not null && mainVm is not null)
        {
            mainVm.SelectedAircraft = ac;
            vm.SelectedAircraft = ac;
        }

        var initials = GetInitials();
        var menu = new ContextMenu();

        // State-dependent items
        var phase = ac?.CurrentPhase ?? "";

        if (phase == "AtParking")
        {
            menu.Items.Add(CreateMenuItem("Pushback",
                () => vm.PushbackAsync(callsign, initials)));
        }

        if (phase is "TaxiingPhase" or "Taxiing")
        {
            menu.Items.Add(CreateMenuItem("Hold position",
                () => vm.HoldPositionAsync(callsign, initials)));
        }

        if (phase is "HoldingShort" or "HoldingAfterExit")
        {
            menu.Items.Add(CreateMenuItem("Resume taxi",
                () => vm.ResumeAsync(callsign, initials)));

            AddRunwayCrossingItems(menu, vm, ac, callsign, initials);
            AddRunwayItems(menu, vm, ac, callsign, initials);
        }

        if (phase is "FinalApproach" or "FinalApproachPhase")
        {
            menu.Items.Add(CreateMenuItem("Go around",
                () => vm.GoAroundAsync(callsign, initials)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Delete",
            () => vm.DeleteAsync(callsign, initials)));

        ShowContextMenu(menu, screenPos);
    }

    private void OnMapRightClicked(
        double lat, double lon, Point screenPos)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        if (vm.SelectedAircraft is null)
        {
            return;
        }

        var nearestNodeId = vm.FindNearestNodeId(lat, lon);
        if (nearestNodeId is null)
        {
            return;
        }

        var callsign = vm.SelectedAircraft.Callsign;
        var initials = GetInitials();
        var menu = new ContextMenu();

        menu.Items.Add(CreateMenuItem("Taxi here",
            () => vm.TaxiToNodeAsync(callsign, initials, nearestNodeId.Value)));

        ShowContextMenu(menu, screenPos);
    }

    private void AddRunwayCrossingItems(
        ContextMenu menu, GroundViewModel vm, AircraftModel? ac,
        string callsign, string initials)
    {
        if (ac is null || vm.Layout is null)
        {
            return;
        }

        // Find nearby runway hold-short nodes
        foreach (var node in vm.Layout.Nodes)
        {
            if (node.Type != "RunwayHoldShort" || node.RunwayId is null)
            {
                continue;
            }

            var dist = Yaat.Sim.GeoMath.DistanceNm(
                ac.Latitude, ac.Longitude,
                node.Latitude, node.Longitude);

            if (dist < 0.1)
            {
                var rwyId = node.RunwayId;
                menu.Items.Add(CreateMenuItem(
                    $"Cross runway {rwyId}",
                    () => vm.CrossRunwayAsync(callsign, initials, rwyId)));
            }
        }
    }

    private void AddRunwayItems(
        ContextMenu menu, GroundViewModel vm, AircraftModel? ac,
        string callsign, string initials)
    {
        if (ac is null || vm.Layout is null)
        {
            return;
        }

        foreach (var node in vm.Layout.Nodes)
        {
            if (node.Type != "RunwayHoldShort" || node.RunwayId is null)
            {
                continue;
            }

            var dist = Yaat.Sim.GeoMath.DistanceNm(
                ac.Latitude, ac.Longitude,
                node.Latitude, node.Longitude);

            if (dist < 0.1)
            {
                var rwyId = node.RunwayId;
                menu.Items.Add(CreateMenuItem(
                    $"Line up and wait {rwyId}",
                    () => vm.LineUpAndWaitAsync(
                        callsign, initials, rwyId)));
                menu.Items.Add(CreateMenuItem(
                    $"Cleared for takeoff {rwyId}",
                    () => vm.ClearedForTakeoffAsync(
                        callsign, initials, rwyId)));
            }
        }
    }

    private static MenuItem CreateMenuItem(
        string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_canvas!).Properties;
        if (props.IsLeftButtonPressed)
        {
            CloseActiveContextMenu();
        }
    }

    private void CloseActiveContextMenu()
    {
        if (_activeContextMenu is not null)
        {
            _activeContextMenu.Close();
            _activeContextMenu = null;
        }
    }

    private void ShowContextMenu(ContextMenu menu, Point pos)
    {
        if (_canvas is null)
        {
            return;
        }

        CloseActiveContextMenu();
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (_activeContextMenu == menu)
            {
                _activeContextMenu = null;
            }
        };
        menu.PlacementTarget = _canvas;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(_canvas);
    }

    private string GetInitials()
    {
        var mainVm = FindMainViewModel();
        return mainVm?.Preferences.UserInitials ?? "";
    }

    private MainViewModel? FindMainViewModel()
    {
        // Walk up the visual tree to find a MainViewModel DataContext
        var parent = this.Parent;
        while (parent is not null)
        {
            if (parent.DataContext is MainViewModel vm)
            {
                return vm;
            }

            parent = (parent as Control)?.Parent;
        }

        return null;
    }
}
