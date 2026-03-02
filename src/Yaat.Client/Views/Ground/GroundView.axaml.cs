using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data.Airport;

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
            var fromNodeId = vm.GetAircraftNearestNodeId(vm.SelectedAircraft);

            if (fromNodeId is not null)
            {
                AddTaxiRouteItems(menu, vm, callsign, initials, fromNodeId.Value, nodeId);
            }

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
        var phase = ac?.CurrentPhase ?? "";

        var headerText = ac is not null
            ? $"{callsign} — {ac.AircraftType}"
            : callsign;
        menu.Items.Add(new MenuItem
        {
            Header = headerText,
            IsEnabled = false,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        });
        menu.Items.Add(new Separator());

        if (phase == "At Parking")
        {
            menu.Items.Add(CreateMenuItem("Push back",
                () => vm.PushbackAsync(callsign, initials)));

            if (ac is not null)
            {
                foreach (var (label, heading) in vm.GetPushbackDirections(ac))
                {
                    var h = heading;
                    menu.Items.Add(CreateMenuItem($"Push back, {label}",
                        () => vm.PushbackHeadingAsync(callsign, initials, h)));
                }
            }
        }

        if (phase is "Pushback" or "Taxiing")
        {
            menu.Items.Add(CreateMenuItem("Hold position",
                () => vm.HoldPositionAsync(callsign, initials)));
        }

        if (phase == "Taxiing" && ac is not null)
        {
            AddHoldShortSubmenu(menu, vm, ac, callsign, initials);
        }

        if (phase.StartsWith("Following", StringComparison.Ordinal))
        {
            menu.Items.Add(CreateMenuItem("Hold position",
                () => vm.HoldPositionAsync(callsign, initials)));
        }

        if (phase.StartsWith("Holding Short", StringComparison.Ordinal))
        {
            var rwyId = ExtractHoldingShortRunway(phase, ac);

            menu.Items.Add(CreateMenuItem("Resume taxi",
                () => vm.ResumeAsync(callsign, initials)));

            if (rwyId is not null)
            {
                menu.Items.Add(CreateMenuItem($"Cross {rwyId}",
                    () => vm.CrossRunwayAsync(callsign, initials, rwyId)));
                menu.Items.Add(CreateMenuItem($"Line up and wait {rwyId}",
                    () => vm.LineUpAndWaitAsync(callsign, initials, rwyId)));
                menu.Items.Add(CreateMenuItem($"Cleared for takeoff {rwyId}",
                    () => vm.ClearedForTakeoffAsync(callsign, initials, rwyId)));
            }

            AddNearbyRunwayCrossings(menu, vm, ac, callsign, initials, rwyId);
        }

        if (phase == "Holding After Exit")
        {
            menu.Items.Add(CreateMenuItem("Resume taxi",
                () => vm.ResumeAsync(callsign, initials)));
        }

        if (phase == "LinedUpAndWaiting")
        {
            var rwyId = ac?.AssignedRunway;
            if (!string.IsNullOrEmpty(rwyId))
            {
                menu.Items.Add(CreateMenuItem($"Cleared for takeoff {rwyId}",
                    () => vm.ClearedForTakeoffAsync(callsign, initials, rwyId)));
            }

            menu.Items.Add(CreateMenuItem("Cancel takeoff clearance",
                () => vm.CancelTakeoffClearanceAsync(callsign, initials)));
        }

        if (phase == "FinalApproach")
        {
            menu.Items.Add(CreateMenuItem("Cleared to land",
                () => vm.ClearedToLandAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Touch and go",
                () => vm.TouchAndGoAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Stop and go",
                () => vm.StopAndGoAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Low approach",
                () => vm.LowApproachAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Cleared for the option",
                () => vm.ClearedForOptionAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Go around",
                () => vm.GoAroundAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Cancel landing clearance",
                () => vm.CancelLandingClearanceAsync(callsign, initials)));
        }

        if (phase == "Landing")
        {
            menu.Items.Add(CreateMenuItem("Exit left",
                () => vm.ExitLeftAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Exit right",
                () => vm.ExitRightAsync(callsign, initials)));
        }

        if (phase == "Takeoff")
        {
            menu.Items.Add(CreateMenuItem("Cancel takeoff clearance",
                () => vm.CancelTakeoffClearanceAsync(callsign, initials)));
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
        var phase = vm.SelectedAircraft.CurrentPhase ?? "";

        if (phase == "At Parking")
        {
            menu.Items.Add(CreateMenuItem("Push back",
                () => vm.PushbackAsync(callsign, initials)));

            foreach (var (label, heading) in vm.GetPushbackDirections(vm.SelectedAircraft))
            {
                var h = heading;
                menu.Items.Add(CreateMenuItem($"Push back, {label}",
                    () => vm.PushbackHeadingAsync(callsign, initials, h)));
            }
        }
        else
        {
            var fromNodeId = vm.GetAircraftNearestNodeId(vm.SelectedAircraft);
            if (fromNodeId is not null)
            {
                AddTaxiRouteItems(menu, vm, callsign, initials,
                    fromNodeId.Value, nearestNodeId.Value);
            }
        }

        if (menu.Items.Count > 0)
        {
            ShowContextMenu(menu, screenPos);
        }
    }

    private static void AddTaxiRouteItems(
        ContextMenu menu, GroundViewModel vm,
        string callsign, string initials,
        int fromNodeId, int toNodeId)
    {
        var routes = vm.FindRoutesToNode(fromNodeId, toNodeId);

        if (routes.Count == 0)
        {
            var disabled = new MenuItem { Header = "No route found", IsEnabled = false };
            menu.Items.Add(disabled);
            return;
        }

        if (routes.Count == 1)
        {
            var route = routes[0];
            var displayName = vm.GetTaxiwayDisplayName(route);
            var command = vm.BuildTaxiCommandWithCrossings(route);

            var item = CreateMenuItem($"Taxi {displayName}",
                () =>
                {
                    vm.ActiveRoute = route;
                    return vm.SendRawCommandAsync(callsign, initials, command);
                });
            AttachPreviewHover(item, vm, route);
            menu.Items.Add(item);
        }
        else
        {
            var parent = new MenuItem { Header = "Taxi here" };
            foreach (var route in routes)
            {
                var r = route;
                var displayName = vm.GetTaxiwayDisplayName(r);
                var command = vm.BuildTaxiCommandWithCrossings(r);

                var item = CreateMenuItem(displayName,
                    () =>
                    {
                        vm.ActiveRoute = r;
                        return vm.SendRawCommandAsync(callsign, initials, command);
                    });
                AttachPreviewHover(item, vm, r);
                parent.Items.Add(item);
            }

            menu.Items.Add(parent);
        }
    }

    private static void AttachPreviewHover(
        MenuItem item, GroundViewModel vm, TaxiRoute route)
    {
        item.PointerEntered += (_, _) => vm.PreviewRoute = route;
        item.PointerExited += (_, _) =>
        {
            if (vm.PreviewRoute == route)
            {
                vm.PreviewRoute = null;
            }
        };
    }

    private static string? ExtractHoldingShortRunway(
        string phase, AircraftModel? ac)
    {
        // Phase name: "Holding Short 28L/10R" → extract runway after "Holding Short "
        const string prefix = "Holding Short ";
        if (phase.StartsWith(prefix, StringComparison.Ordinal)
            && phase.Length > prefix.Length)
        {
            var rwyPart = phase[prefix.Length..];
            // Use first part of compound ID: "28L/10R" → "28L"
            var slashIdx = rwyPart.IndexOf('/');
            return slashIdx > 0 ? rwyPart[..slashIdx] : rwyPart;
        }

        return !string.IsNullOrEmpty(ac?.AssignedRunway)
            ? ac.AssignedRunway : null;
    }

    private static void AddNearbyRunwayCrossings(
        ContextMenu menu, GroundViewModel vm, AircraftModel? ac,
        string callsign, string initials, string? excludeRunway)
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

            if (dist >= 0.1)
            {
                continue;
            }

            // Extract first part for comparison and display
            var rwyId = node.RunwayId.Split('/')[0];
            if (excludeRunway is not null
                && string.Equals(rwyId, excludeRunway, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            menu.Items.Add(CreateMenuItem($"Cross {rwyId}",
                () => vm.CrossRunwayAsync(callsign, initials, rwyId)));
        }
    }

    private static void AddHoldShortSubmenu(
        ContextMenu menu, GroundViewModel vm, AircraftModel ac,
        string callsign, string initials)
    {
        var targets = vm.GetHoldShortTargets(ac);
        if (targets.Count == 0)
        {
            return;
        }

        var parent = new MenuItem { Header = "Hold short of..." };
        foreach (var (displayName, target) in targets)
        {
            var t = target;
            var item = CreateMenuItem(displayName,
                () => vm.HoldShortAsync(callsign, initials, t));

            var previewRoute = vm.FindHoldShortPreviewRoute(ac, t);
            if (previewRoute is not null)
            {
                AttachPreviewHover(item, vm, previewRoute);
            }

            parent.Items.Add(item);
        }

        menu.Items.Add(parent);
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

            if (DataContext is GroundViewModel vm)
            {
                vm.PreviewRoute = null;
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
