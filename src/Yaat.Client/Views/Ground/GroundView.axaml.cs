using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Ground;

public partial class GroundView : UserControl
{
    private GroundCanvas? _canvas;
    private ContextMenu? _activeContextMenu;
    private Border? _taxiInputOverlay;
    private TextBox? _taxiInputBox;
    private string? _pendingCallsign;
    private string? _pendingInitials;

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
        _canvas.AircraftLeftClicked += OnAircraftLeftClicked;
        _canvas.AircraftCtrlClicked += OnAircraftCtrlClicked;
        _canvas.EmptySpaceClicked += OnEmptySpaceClicked;
        _canvas.PointerPressed += OnCanvasPointerPressed;

        _taxiInputOverlay = this.FindControl<Border>("TaxiInputOverlay");
        _taxiInputBox = this.FindControl<TextBox>("TaxiInputBox");
        if (_taxiInputBox is not null)
        {
            _taxiInputBox.KeyDown += OnTaxiInputKeyDown;
            _taxiInputBox.LostFocus += OnTaxiInputLostFocus;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        if (_canvas is not null)
        {
            _canvas.NodeRightClicked -= OnNodeRightClicked;
            _canvas.AircraftRightClicked -= OnAircraftRightClicked;
            _canvas.AircraftLeftClicked -= OnAircraftLeftClicked;
            _canvas.AircraftCtrlClicked -= OnAircraftCtrlClicked;
            _canvas.EmptySpaceClicked -= OnEmptySpaceClicked;
            _canvas.PointerPressed -= OnCanvasPointerPressed;
        }

        if (_taxiInputBox is not null)
        {
            _taxiInputBox.KeyDown -= OnTaxiInputKeyDown;
            _taxiInputBox.LostFocus -= OnTaxiInputLostFocus;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.D && Services.PlatformHelper.HasActionModifier(e.KeyModifiers) && _canvas is not null)
        {
            _canvas.ShowDebugInfo = !_canvas.ShowDebugInfo;
            e.Handled = true;
        }
    }

    private void OnAircraftLeftClicked(string callsign)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        var ac = FindMainViewModel()?.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null)
        {
            vm.SelectedAircraft = ac;
        }
    }

    private void OnAircraftCtrlClicked(string callsign)
    {
        var mainVm = FindMainViewModel();
        if (mainVm is null)
        {
            return;
        }

        var ac = mainVm.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null)
        {
            DataGridView.OpenFlightPlanEditor(ac, mainVm, TopLevel.GetTopLevel(this) as Window);
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
                menu.Items.Add(CreateMenuItem($"Hold short {node.RunwayId}", () => vm.TaxiToNodeAsync(callsign, initials, nodeId)));
            }

            var (prefill, caretPos) = BuildCustomTaxiPrefill(vm, node, nodeId);
            menu.Items.Add(
                CreateMenuItem(
                    "Custom taxi...",
                    () =>
                    {
                        ShowTaxiInput(callsign, initials, prefill, caretPos);
                        return Task.CompletedTask;
                    }
                )
            );

            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Warp here", () => vm.WarpToNodeAsync(callsign, nodeId)));
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

        var ac = FindMainViewModel()?.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null)
        {
            vm.SelectedAircraft = ac;
        }

        var initials = GetInitials();
        var menu = new ContextMenu();
        var phase = ac?.CurrentPhase ?? "";

        var headerText = ac is not null ? $"{callsign} — {ac.AircraftType}" : callsign;
        menu.Items.Add(
            new MenuItem
            {
                Header = headerText,
                IsEnabled = false,
                FontWeight = Avalonia.Media.FontWeight.Bold,
            }
        );
        menu.Items.Add(new Separator());

        if (phase == "At Parking")
        {
            menu.Items.Add(CreateMenuItem("Push back", () => vm.PushbackAsync(callsign, initials)));

            if (ac is not null)
            {
                foreach (var (label, heading) in vm.GetPushbackDirections(ac))
                {
                    var h = heading;
                    menu.Items.Add(CreateMenuItem($"Push back, {label}", () => vm.PushbackHeadingAsync(callsign, initials, h)));
                }
            }
        }

        if (phase is "Pushback" or "Taxiing")
        {
            menu.Items.Add(CreateMenuItem("Hold position", () => vm.HoldPositionAsync(callsign, initials)));
        }

        if (phase == "Taxiing" && ac is not null)
        {
            AddHoldShortSubmenu(menu, vm, ac, callsign, initials);
        }

        if (phase.StartsWith("Following", StringComparison.Ordinal))
        {
            menu.Items.Add(CreateMenuItem("Hold position", () => vm.HoldPositionAsync(callsign, initials)));
        }

        if (phase.StartsWith("Holding Short", StringComparison.Ordinal))
        {
            var rwyId = ExtractHoldingShortRunway(phase, ac);

            menu.Items.Add(CreateMenuItem("Resume taxi", () => vm.ResumeAsync(callsign, initials)));

            if (rwyId is not null)
            {
                menu.Items.Add(CreateMenuItem($"Cross {rwyId}", () => vm.CrossRunwayAsync(callsign, initials, rwyId)));
                menu.Items.Add(CreateMenuItem($"Line up and wait {rwyId}", () => vm.LineUpAndWaitAsync(callsign, initials, rwyId)));
                AddCtoSubmenu(menu, vm, ac, callsign, initials, rwyId);
            }

            AddNearbyRunwayCrossings(menu, vm, ac, callsign, initials, rwyId);
        }

        if (phase is "Holding After Exit" or "Holding After Pushback")
        {
            menu.Items.Add(CreateMenuItem("Resume taxi", () => vm.ResumeAsync(callsign, initials)));
        }

        if (phase == "LinedUpAndWaiting")
        {
            var rwyId = ac?.AssignedRunway;
            if (!string.IsNullOrEmpty(rwyId))
            {
                AddCtoSubmenu(menu, vm, ac, callsign, initials, rwyId);
            }

            menu.Items.Add(CreateMenuItem("Cancel takeoff clearance", () => vm.CancelTakeoffClearanceAsync(callsign, initials)));
        }

        if (phase == "FinalApproach")
        {
            menu.Items.Add(CreateMenuItem("Cleared to land", () => vm.ClearedToLandAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Touch and go", () => vm.TouchAndGoAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Stop and go", () => vm.StopAndGoAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Low approach", () => vm.LowApproachAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Cleared for the option", () => vm.ClearedForOptionAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Go around", () => vm.GoAroundAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Cancel landing clearance", () => vm.CancelLandingClearanceAsync(callsign, initials)));
        }

        if (phase == "Landing")
        {
            menu.Items.Add(CreateMenuItem("Exit left", () => vm.ExitLeftAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Exit right", () => vm.ExitRightAsync(callsign, initials)));
        }

        if (phase == "Takeoff")
        {
            menu.Items.Add(CreateMenuItem("Cancel takeoff clearance", () => vm.CancelTakeoffClearanceAsync(callsign, initials)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Delete", () => vm.DeleteAsync(callsign, initials)));

        ShowContextMenu(menu, screenPos);
    }

    private void OnEmptySpaceClicked()
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.SelectedAircraft = null;
        }
    }

    private static void AddTaxiRouteItems(ContextMenu menu, GroundViewModel vm, string callsign, string initials, int fromNodeId, int toNodeId)
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
            AddSingleRouteItems(menu, vm, callsign, initials, routes[0]);
        }
        else
        {
            var parent = new MenuItem { Header = "Taxi here" };
            foreach (var route in routes)
            {
                AddSingleRouteItems(parent, vm, callsign, initials, route);
            }

            menu.Items.Add(parent);
        }
    }

    private static void AddSingleRouteItems(ItemsControl parent, GroundViewModel vm, string callsign, string initials, TaxiRoute route)
    {
        var displayName = vm.GetTaxiwayDisplayName(route);
        var variants = vm.BuildTaxiCrossingVariants(route);

        if (variants.Count <= 1)
        {
            var command = variants.Count == 1 ? variants[0].Command : "";
            var item = CreateMenuItem(
                $"Taxi {displayName}",
                () =>
                {
                    vm.ActiveRoute = route;
                    return vm.SendRawCommandAsync(callsign, initials, command);
                }
            );
            AttachPreviewHover(item, vm, route);
            parent.Items.Add(item);
            return;
        }

        var sub = new MenuItem { Header = $"Taxi {displayName}" };
        AttachPreviewHover(sub, vm, route);

        foreach (var (label, command) in variants)
        {
            var cmd = command;
            sub.Items.Add(
                CreateMenuItem(
                    label,
                    () =>
                    {
                        vm.ActiveRoute = route;
                        return vm.SendRawCommandAsync(callsign, initials, cmd);
                    }
                )
            );
        }

        parent.Items.Add(sub);
    }

    private static void AttachPreviewHover(MenuItem item, GroundViewModel vm, TaxiRoute route)
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

    private static string? ExtractHoldingShortRunway(string phase, AircraftModel? ac)
    {
        // Phase name: "Holding Short 28L/10R" → extract runway after "Holding Short "
        const string prefix = "Holding Short ";
        if (phase.StartsWith(prefix, StringComparison.Ordinal) && phase.Length > prefix.Length)
        {
            var rwyPart = phase[prefix.Length..];
            // Use first part of compound ID: "28L/10R" → "28L"
            return RunwayIdentifier.Parse(rwyPart).End1;
        }

        return !string.IsNullOrEmpty(ac?.AssignedRunway) ? ac.AssignedRunway : null;
    }

    private static void AddNearbyRunwayCrossings(
        ContextMenu menu,
        GroundViewModel vm,
        AircraftModel? ac,
        string callsign,
        string initials,
        string? excludeRunway
    )
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

            var dist = Yaat.Sim.GeoMath.DistanceNm(ac.Latitude, ac.Longitude, node.Latitude, node.Longitude);

            if (dist >= 0.1)
            {
                continue;
            }

            // Extract first part for comparison and display
            var rwyId = RunwayIdentifier.Parse(node.RunwayId).End1;
            if (excludeRunway is not null && string.Equals(rwyId, excludeRunway, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            menu.Items.Add(CreateMenuItem($"Cross {rwyId}", () => vm.CrossRunwayAsync(callsign, initials, rwyId)));
        }
    }

    private static void AddHoldShortSubmenu(ContextMenu menu, GroundViewModel vm, AircraftModel ac, string callsign, string initials)
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
            var item = CreateMenuItem(displayName, () => vm.HoldShortAsync(callsign, initials, t));

            var previewRoute = vm.FindHoldShortPreviewRoute(ac, t);
            if (previewRoute is not null)
            {
                AttachPreviewHover(item, vm, previewRoute);
            }

            parent.Items.Add(item);
        }

        menu.Items.Add(parent);
    }

    private static void AddCtoSubmenu(ContextMenu menu, GroundViewModel vm, AircraftModel? ac, string callsign, string initials, string rwyId)
    {
        bool isVfr = ac is not null && string.Equals(ac.FlightRules, "VFR", StringComparison.OrdinalIgnoreCase);

        var parent = new MenuItem { Header = $"Cleared for takeoff {rwyId}" };
        parent.Items.Add(CreateMenuItem("Default (SID/on course)", () => vm.ClearedForTakeoffAsync(callsign, initials, rwyId)));

        if (isVfr)
        {
            // VFR: full modifier menu
            parent.Items.Add(new Separator());
            parent.Items.Add(CreateMenuItem("Make left traffic", () => vm.ClearedForTakeoffModifierAsync(callsign, initials, "MLT")));
            parent.Items.Add(CreateMenuItem("Make right traffic", () => vm.ClearedForTakeoffModifierAsync(callsign, initials, "MRT")));
            parent.Items.Add(new Separator());
            parent.Items.Add(CreateMenuItem("Runway heading", () => vm.ClearedForTakeoffModifierAsync(callsign, initials, "RH")));
            parent.Items.Add(CreateMenuItem("On course", () => vm.ClearedForTakeoffModifierAsync(callsign, initials, "OC")));
            parent.Items.Add(CreateMenuItem("Right crosswind (90° right)", () => vm.ClearedForTakeoffModifierAsync(callsign, initials, "MRC")));
            parent.Items.Add(CreateMenuItem("Right downwind (180° right)", () => vm.ClearedForTakeoffModifierAsync(callsign, initials, "MRD")));
            parent.Items.Add(CreateMenuItem("Left crosswind (90° left)", () => vm.ClearedForTakeoffModifierAsync(callsign, initials, "MLC")));
            parent.Items.Add(CreateMenuItem("Left downwind (180° left)", () => vm.ClearedForTakeoffModifierAsync(callsign, initials, "MLD")));
        }
        else
        {
            // IFR: only runway heading (headings via text input)
            parent.Items.Add(new Separator());
            parent.Items.Add(CreateMenuItem("Runway heading", () => vm.ClearedForTakeoffModifierAsync(callsign, initials, "RH")));
        }

        menu.Items.Add(parent);
    }

    private static MenuItem CreateMenuItem(string header, Func<Task> action)
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
            HideTaxiInput();
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

    private static (string Text, int CaretIndex) BuildCustomTaxiPrefill(GroundViewModel vm, GroundNodeDto node, int nodeId)
    {
        const string taxiPrefix = "TAXI ";

        switch (node.Type)
        {
            case "Parking" when node.Name is not null:
                // "TAXI  @SPOT" — cursor between TAXI and @SPOT
                var parkingSuffix = $"@{node.Name}";
                return ($"{taxiPrefix} {parkingSuffix}", taxiPrefix.Length);

            case "RunwayHoldShort" when node.RunwayId is not null:
                // "RWY 30 TAXI " — cursor at end for user to add taxiway route
                var rwyEnd1 = RunwayIdentifier.Parse(node.RunwayId).End1;
                var rwyText = $"RWY {rwyEnd1} {taxiPrefix}";
                return (rwyText, rwyText.Length);

            default:
                // Taxiway intersection or spot: "TAXI  E" — cursor between TAXI and taxiway name
                var names = vm.GetNodeTaxiwayNames(nodeId);
                if (names.Count > 0)
                {
                    var twySuffix = names[0];
                    return ($"{taxiPrefix} {twySuffix}", taxiPrefix.Length);
                }

                return (taxiPrefix, taxiPrefix.Length);
        }
    }

    private void ShowTaxiInput(string callsign, string initials, string prefill, int caretIndex)
    {
        if (_taxiInputOverlay is null || _taxiInputBox is null)
        {
            return;
        }

        _pendingCallsign = callsign;
        _pendingInitials = initials;

        _taxiInputBox.Text = prefill;
        _taxiInputOverlay.IsVisible = true;
        _taxiInputBox.Focus();
        _taxiInputBox.CaretIndex = caretIndex;
    }

    private void HideTaxiInput()
    {
        if (_taxiInputOverlay is not null)
        {
            _taxiInputOverlay.IsVisible = false;
        }

        _pendingCallsign = null;
        _pendingInitials = null;
    }

    private async void OnTaxiInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideTaxiInput();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = _taxiInputBox?.Text?.Trim();
            var callsign = _pendingCallsign;
            var initials = _pendingInitials;
            HideTaxiInput();

            if (!string.IsNullOrEmpty(text) && callsign is not null && initials is not null && DataContext is GroundViewModel vm)
            {
                await vm.SendRawCommandAsync(callsign, initials, text);
            }
        }
    }

    private void OnTaxiInputLostFocus(object? sender, RoutedEventArgs e)
    {
        HideTaxiInput();
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
