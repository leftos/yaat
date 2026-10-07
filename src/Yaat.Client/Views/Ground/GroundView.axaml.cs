using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Ground;

public partial class GroundView : UserControl
{
    public static readonly FuncValueConverter<bool, string> BoolToLockLabel = new(v => v ? "LOCK" : "UNLK");
    public static readonly FuncValueConverter<GroundFilterMode, bool> FilterIsActive = new(v => v == GroundFilterMode.LabelsAndIcons);
    public static readonly FuncValueConverter<GroundFilterMode, bool> FilterIsPartial = new(v => v == GroundFilterMode.IconsOnly);
    private static readonly ILogger MenuLog = AppLog.CreateLogger("GroundView");
    private GroundCanvas? _canvas;
    private Button? _resetButton;
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

        _resetButton = this.FindControl<Button>("ResetButton");
        _resetButton?.AddHandler(PointerPressedEvent, OnResetButtonPointerPressed, RoutingStrategies.Tunnel);

        _canvas.NodeRightClicked += OnNodeRightClicked;
        _canvas.AircraftRightClicked += OnAircraftRightClicked;
        _canvas.AircraftLeftClicked += OnAircraftLeftClicked;
        _canvas.AircraftCtrlClicked += OnAircraftCtrlClicked;
        _canvas.EmptySpaceClicked += OnEmptySpaceClicked;
        _canvas.RunwayThresholdClicked += OnRunwayThresholdClicked;
        _canvas.RunwayThresholdRightClicked += OnRunwayThresholdClicked;
        _canvas.RunwaySurfaceRightClicked += OnRunwaySurfaceRightClicked;
        _canvas.PointerPressed += OnCanvasPointerPressed;
        _canvas.DrawNodeClicked += OnDrawNodeClicked;
        _canvas.DrawNodeFinished += OnDrawNodeFinished;
        _canvas.DrawNodeHovered += OnDrawNodeHovered;
        _canvas.PushRouteRightClicked += OnPushRouteRightClicked;
        _canvas.DrawFreePointPlaced += OnDrawFreePointPlaced;
        _canvas.PushMarkerDragged += OnPushMarkerDragged;
        _canvas.HoveredAircraftChanged += OnAircraftHovered;
        _canvas.MeasurePointPicked += OnMeasurePointPicked;
        _canvas.MeasureDragCompleted += OnMeasureDragCompleted;
        _canvas.MeasureCancelled += OnMeasureCancelled;

        if (DataContext is GroundViewModel vm && vm.Preferences is not null)
        {
            _canvas.SetStartWithAllHidden(vm.Preferences.GroundHideDataBlocksByDefault);
            ApplyFontSizePreferences(vm.Preferences);
            vm.Preferences.FontSizesChanged += OnPreferencesFontSizesChanged;
        }

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

        _resetButton?.RemoveHandler(PointerPressedEvent, OnResetButtonPointerPressed);

        if (_canvas is not null)
        {
            _canvas.NodeRightClicked -= OnNodeRightClicked;
            _canvas.AircraftRightClicked -= OnAircraftRightClicked;
            _canvas.AircraftLeftClicked -= OnAircraftLeftClicked;
            _canvas.AircraftCtrlClicked -= OnAircraftCtrlClicked;
            _canvas.EmptySpaceClicked -= OnEmptySpaceClicked;
            _canvas.RunwayThresholdClicked -= OnRunwayThresholdClicked;
            _canvas.RunwayThresholdRightClicked -= OnRunwayThresholdClicked;
            _canvas.RunwaySurfaceRightClicked -= OnRunwaySurfaceRightClicked;
            _canvas.PointerPressed -= OnCanvasPointerPressed;
            _canvas.DrawNodeClicked -= OnDrawNodeClicked;
            _canvas.DrawNodeFinished -= OnDrawNodeFinished;
            _canvas.DrawNodeHovered -= OnDrawNodeHovered;
            _canvas.PushRouteRightClicked -= OnPushRouteRightClicked;
            _canvas.DrawFreePointPlaced -= OnDrawFreePointPlaced;
            _canvas.PushMarkerDragged -= OnPushMarkerDragged;
            _canvas.HoveredAircraftChanged -= OnAircraftHovered;
            _canvas.MeasurePointPicked -= OnMeasurePointPicked;
            _canvas.MeasureDragCompleted -= OnMeasureDragCompleted;
            _canvas.MeasureCancelled -= OnMeasureCancelled;
        }

        if (DataContext is GroundViewModel vm && vm.Preferences is not null)
        {
            vm.Preferences.FontSizesChanged -= OnPreferencesFontSizesChanged;
        }

        if (_taxiInputBox is not null)
        {
            _taxiInputBox.KeyDown -= OnTaxiInputKeyDown;
            _taxiInputBox.LostFocus -= OnTaxiInputLostFocus;
        }
    }

    // --- Distance measuring tool ---

    /// <summary>
    /// Adds the measuring tool's items to a ground context menu: start or finish a measurement at the
    /// clicked spot, remove the one under the cursor, and clear them all.
    /// </summary>
    /// <remarks>
    /// The endpoint comes from the raw cursor position rather than the snapped node the rest of this menu
    /// is built around — measuring a wingtip-to-hold-bar gap means using the exact point clicked.
    /// </remarks>
    private void AddMeasureMenuItems(List<Control> items, GroundViewModel vm, Point screenPos)
    {
        if (vm.Measure is not { } measure || _canvas is null)
        {
            return;
        }

        RblEndpoint endpoint = _canvas.MeasureEndpointAt(screenPos);
        string startLabel = measure.Anchor is null ? "Measure from here" : "Measure to here";
        items.Add(
            CreateMenuItem(
                startLabel,
                () =>
                {
                    measure.Pick(endpoint, GroundViewModel.MeasureView, vm.MeasureTrackLookup, GroundViewModel.MeasureUnits);
                    return Task.CompletedTask;
                }
            )
        );

        if (_canvas.MeasurementSlotAt(screenPos) is { } slot)
        {
            items.Add(
                CreateMenuItem(
                    $"Remove measurement {slot}",
                    () =>
                    {
                        measure.Remove(slot);
                        return Task.CompletedTask;
                    }
                )
            );
        }

        if (measure.HasLines)
        {
            items.Add(
                CreateMenuItem(
                    "Clear measurements",
                    () =>
                    {
                        measure.Clear();
                        return Task.CompletedTask;
                    }
                )
            );
        }

        items.Add(new Separator());
    }

    private void OnMeasurePointPicked(RblEndpoint endpoint)
    {
        if (DataContext is GroundViewModel { Measure: { } measure } vm)
        {
            measure.Pick(endpoint, GroundViewModel.MeasureView, vm.MeasureTrackLookup, GroundViewModel.MeasureUnits);
        }
    }

    private void OnMeasureDragCompleted(RblEndpoint from, RblEndpoint to)
    {
        if (DataContext is GroundViewModel { Measure: { } measure } vm)
        {
            measure.Place(from, to, GroundViewModel.MeasureView, vm.MeasureTrackLookup, GroundViewModel.MeasureUnits);
        }
    }

    private void OnMeasureCancelled() => (DataContext as GroundViewModel)?.Measure?.Cancel();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (DataContext is GroundViewModel { Measure.IsMeasuring: true } measuringVm && e.Key == Key.Escape)
        {
            measuringVm.Measure?.Cancel();
            e.Handled = true;
            return;
        }

        if (DataContext is GroundViewModel vm && vm.IsDrawingRoute)
        {
            if (e.Key == Key.Escape)
            {
                vm.CancelDrawRoute();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Back)
            {
                vm.UndoDrawWaypoint();
                e.Handled = true;
                return;
            }
        }

        if ((e.Key == Key.D) && (e.KeyModifiers == (PlatformHelper.IsMacOS ? KeyModifiers.Meta : KeyModifiers.Control)) && (_canvas is not null))
        {
            _canvas.ShowDebugInfo = !_canvas.ShowDebugInfo;
            e.Handled = true;
        }
    }

    private void OnToggleSatelliteImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.ShowSatelliteImage = !vm.ShowSatelliteImage;
            vm.SaveLayerSettings();
        }
    }

    private void OnToggleVideoMapOverlay(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.ShowVideoMapOverlay = !vm.ShowVideoMapOverlay;
            vm.SaveLayerSettings();
        }
    }

    private void OnToggleYaatLayout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.ShowYaatLayout = !vm.ShowYaatLayout;
            vm.SaveLayerSettings();
        }
    }

    private void OnToggleRunwayLabels(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.ShowRunwayLabels = !vm.ShowRunwayLabels;
            vm.SaveLabelAndLockSettings();
        }
    }

    private void OnToggleDeconflict(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.CycleDeconflictMode();
        }
    }

    private void OnToggleTaxiwayLabels(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.ShowTaxiwayLabels = !vm.ShowTaxiwayLabels;
            vm.SaveLabelAndLockSettings();
        }
    }

    private void OnToggleAdwMarkings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.ShowAdwMarkings = !vm.ShowAdwMarkings;
            vm.SaveLabelAndLockSettings();
        }
    }

    private void OnToggleHoldShort(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.ShowHoldShort = CycleFilterMode(vm.ShowHoldShort);
            vm.SaveLabelAndLockSettings();
        }
    }

    private void OnToggleParking(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.ShowParking = CycleFilterMode(vm.ShowParking);
            vm.SaveLabelAndLockSettings();
        }
    }

    private void OnToggleSpot(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.ShowSpot = CycleFilterMode(vm.ShowSpot);
            vm.SaveLabelAndLockSettings();
        }
    }

    private static GroundFilterMode CycleFilterMode(GroundFilterMode current)
    {
        return current switch
        {
            GroundFilterMode.LabelsAndIcons => GroundFilterMode.IconsOnly,
            GroundFilterMode.IconsOnly => GroundFilterMode.Off,
            _ => GroundFilterMode.LabelsAndIcons,
        };
    }

    private void OnResetButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        _canvas?.ResetViewIncludingRotation();
        e.Handled = true;
    }

    private void OnResetView(object? sender, RoutedEventArgs e) => _canvas?.ResetView();

    private void OnToggleLock(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.IsPanZoomLocked = !vm.IsPanZoomLocked;
            vm.SaveLabelAndLockSettings();
        }
    }

    private void OnAircraftLeftClicked(string callsign)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        AircraftModel? ac = FindMainViewModel()?.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null)
        {
            vm.SelectedAircraft = ac;
        }
    }

    private void OnAircraftCtrlClicked(string callsign)
    {
        MainViewModel? mainVm = FindMainViewModel();
        if (mainVm is null)
        {
            return;
        }

        AircraftModel? ac = mainVm.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null)
        {
            FlightPlanEditorManager.Open(ac, mainVm);
        }
    }

    private void OnNodeRightClicked(int nodeId, Point screenPos)
    {
        if (BuildNodeContextMenu(nodeId, screenPos) is { } menu)
        {
            ShowContextMenu(menu);
        }
    }

    /// <summary>
    /// The right-click menu at a ground node (an empty-space click resolves to the nearest one): the node point menu
    /// (<see cref="BuildNodePointMenu"/>), ending with "Settings for this view…"; null without a ground view model or the
    /// node, or when the menu would be empty.
    /// </summary>
    internal ContextMenu? BuildNodeContextMenu(int nodeId, Point screenPos)
    {
        if ((DataContext is not GroundViewModel vm) || (vm.GetNode(nodeId) is null))
        {
            return null;
        }

        return WithViewSettings(BuildNodePointMenu(vm, nodeId, screenPos));
    }

    /// <summary>
    /// <paramref name="menu"/> (a new one when null) ending with "Settings for this view…", which opens Settings at Ground;
    /// null when it is still empty, as it is without a main view model to ask.
    /// </summary>
    private ContextMenu? WithViewSettings(ContextMenu? menu)
    {
        menu ??= new ContextMenu();
        ViewSettingsMenu.Append(menu, FindMainViewModel(), SettingsSectionId.Ground);
        return (menu.Items.Count > 0) ? menu : null;
    }

    /// <summary>
    /// The menu a right-click on taxi node <paramref name="nodeId"/> shows, built without opening it. With an aircraft
    /// selected, the shared point menu at the node (<see cref="BuildPointMenu"/>) carries the aircraft's point items, then
    /// the ground's node section: the measuring items, then Draw taxi route… from the node and Push route… when the tug
    /// planner finds a move there (<see cref="GroundViewModel.CanPushRouteTo"/>); with nothing selected, the measuring
    /// items alone. Null when the node is not in the layout or there is nothing to show.
    /// </summary>
    internal ContextMenu? BuildNodePointMenu(GroundViewModel vm, int nodeId, Point screenPos)
    {
        GroundNodeDto? node = vm.GetNode(nodeId);
        if (node is null)
        {
            return null;
        }

        List<Control> section = [];
        AddMeasureMenuItems(section, vm, screenPos);
        if (vm.SelectedAircraft is not { } selected)
        {
            return SectionMenu(section);
        }

        AddDrawTaxiRouteFrom(section, vm, selected, nodeId);

        // The aircraft context menu's pushback gate, and a tug move the planner finds to this node.
        if (AircraftCommandApplicability.CanPushBack(selected) && vm.CanPushRouteTo(selected, nodeId))
        {
            section.Add(
                CreateMenuItem(
                    "Push route…",
                    () =>
                    {
                        vm.StartPushRoute(selected);
                        vm.AddPushWaypoint(nodeId);
                        return Task.CompletedTask;
                    }
                )
            );
        }

        return BuildPointMenu(selected, new MenuPoint(new LatLon(node.Latitude, node.Longitude), node, null, [], null), section);
    }

    /// <summary>
    /// Adds Draw taxi route…, which starts drawing a taxi route for <paramref name="aircraft"/> from
    /// <paramref name="nodeId"/>, when the aircraft can be given one (the shared Taxi here and Custom taxi… gate).
    /// </summary>
    private static void AddDrawTaxiRouteFrom(List<Control> items, GroundViewModel vm, AircraftModel aircraft, int nodeId)
    {
        if (!AircraftCommandApplicability.CanDrawTaxiRoute(aircraft))
        {
            return;
        }

        items.Add(
            CreateMenuItem(
                "Draw taxi route…",
                () =>
                {
                    vm.StartDrawRoute(aircraft);
                    vm.AddDrawWaypoint(nodeId);
                    return Task.CompletedTask;
                }
            )
        );
    }

    /// <summary>
    /// The shared point menu for <paramref name="selected"/> at <paramref name="point"/> with the ground's
    /// <paramref name="section"/>, clearing the Taxi here hover preview on every ground view when it closes. Without a
    /// main view model it logs and returns the section alone (<see cref="SectionMenu"/>).
    /// </summary>
    private ContextMenu? BuildPointMenu(AircraftModel selected, MenuPoint point, List<Control> section)
    {
        if (FindMainViewModel() is not { } main)
        {
            MenuLog.LogWarning(
                "Point right-click with {Callsign} selected: the ground view has no main view model, so only the view's items open",
                selected.Callsign
            );
            return SectionMenu(section);
        }

        TrimTrailingSeparators(section);
        var host = new ClientMenuHost(main, selected, Canvas);
        ContextMenu menu = AircraftMenuBuilder.Build(selected, new MenuClick(selected.Callsign, null, point, []), host, _ => section);
        menu.Closed += (_, _) => host.SetRoutePreview(null);
        return menu;
    }

    /// <summary><paramref name="section"/> as the whole menu, after dropping its trailing separators; null when it is empty.</summary>
    private static ContextMenu? SectionMenu(List<Control> section)
    {
        TrimTrailingSeparators(section);
        if (section.Count == 0)
        {
            return null;
        }

        var menu = new ContextMenu();
        foreach (Control item in section)
        {
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>Drops the separators that end <paramref name="items"/>, which divided them from items that do not follow.</summary>
    private static void TrimTrailingSeparators(List<Control> items)
    {
        while ((items.Count > 0) && (items[^1] is Separator))
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    private void OnAircraftRightClicked(string callsign, Point screenPos)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        if (FindMainViewModel() is not { } main)
        {
            MenuLog.LogWarning("Right-click on {Callsign}: the ground view has no main view model, so no aircraft menu opens", callsign);
            return;
        }

        AircraftModel? ac = main.Aircraft.FirstOrDefault(a => a.Callsign == callsign);

        // Keep the previously-selected aircraft as the command recipient when the
        // controller right-clicks a DIFFERENT aircraft, so selected→right-clicked
        // relative actions (give way / follow) target the selected aircraft. Only adopt
        // the right-clicked aircraft as the selection when nothing was selected or the
        // same aircraft was re-clicked. Left-click remains the way to change selection.
        AircraftModel? prevSelected = vm.SelectedAircraft;
        if (ac is not null && (prevSelected is null || string.Equals(prevSelected.Callsign, callsign, StringComparison.OrdinalIgnoreCase)))
        {
            vm.SelectedAircraft = ac;
        }

        ShowContextMenu(BuildAircraftRightClickMenu(vm, ac, prevSelected, callsign));
    }

    /// <summary>
    /// The menu a right-click on an aircraft opens: the aircraft menu (<see cref="BuildAircraftContextMenu"/>), ending with
    /// "Settings for this view…".
    /// </summary>
    internal ContextMenu BuildAircraftRightClickMenu(GroundViewModel vm, AircraftModel? ac, AircraftModel? prevSelected, string callsign)
    {
        ContextMenu menu = BuildAircraftContextMenu(vm, ac, prevSelected, callsign);
        ViewSettingsMenu.Append(menu, FindMainViewModel(), SettingsSectionId.Ground);
        return menu;
    }

    /// <summary>
    /// The whole aircraft context menu a right-click shows, built without opening it through
    /// <see cref="AircraftMenuBuilder"/>: the right-clicked <paramref name="callsign"/> and its model
    /// <paramref name="ac"/>, the selected aircraft <paramref name="prevSelected"/> that relative actions target, and
    /// the ground's view section (<see cref="BuildViewSection"/>). Touches no canvas, popup or pointer state.
    /// </summary>
    internal ContextMenu BuildAircraftContextMenu(GroundViewModel vm, AircraftModel? ac, AircraftModel? prevSelected, string callsign)
    {
        MainViewModel main =
            FindMainViewModel()
            ?? throw new InvalidOperationException("The ground aircraft menu needs the main view model; the ground view is not hosted by one");
        var host = new ClientMenuHost(main, ac, Canvas);
        return AircraftMenuBuilder.Build(ac, new MenuClick(callsign, prevSelected, null, []), host, context => BuildViewSection(vm, context));
    }

    /// <summary>
    /// The ground's view section: its Display submenu (<see cref="BuildCanvasDisplay"/>), the same for a surface
    /// live-traffic shadow as for an aircraft the controller commands.
    /// </summary>
    internal IReadOnlyList<Control> BuildViewSection(GroundViewModel vm, MenuContext context) => [BuildCanvasDisplay(vm, context)];

    /// <summary>
    /// The ground canvas's display items, built from the ground view model's own state: the taxi-route submenu, show
    /// or hide datablock, then reset datablock position while the data block sits away from its position, then the
    /// measure item, which latches the measurement to the aircraft so the line follows it as it taxis. An item the
    /// state hides is null; the Display submenu (<see cref="BuildCanvasDisplay"/>) wraps them.
    /// </summary>
    internal IReadOnlyList<MenuItem?> BuildCanvasItems(GroundViewModel vm, MenuContext context)
    {
        string callsign = context.Callsign;
        return
        [
            CanvasMenuItems.TaxiRoute(vm.GetTaxiRouteMode(callsign), mode => vm.SetTaxiRouteMode(callsign, mode)),
            CanvasMenuItems.HideDataBlock(Canvas.IsDataBlockHidden(callsign), () => Canvas.ToggleHiddenDataBlock(callsign)),
            CanvasMenuItems.ResetDataBlockPosition(Canvas.HasManualDataBlockOffset(callsign), () => Canvas.ResetDataBlockOffset(callsign)),
            CanvasMenuItems.Measure(MeasureState(vm), callsign, () => MeasurePickOnAircraft(vm, callsign)),
        ];
    }

    /// <summary>
    /// The ground canvas's Display submenu: the ground's own display items (<see cref="BuildCanvasItems"/>). The
    /// ground renderer draws no leader direction, J-ring, cone or blanking, so it offers none of them.
    /// </summary>
    internal MenuItem BuildCanvasDisplay(GroundViewModel vm, MenuContext context) => CanvasMenuItems.Display([BuildCanvasItems(vm, context)]);

    /// <summary>What the ground view's measure tool is doing, which decides whether the display items offer a measure item.</summary>
    private static MenuMeasureState MeasureState(GroundViewModel vm) =>
        vm.Measure switch
        {
            null => MenuMeasureState.None,
            { Anchor: null } => MenuMeasureState.NoAnchor,
            _ => MenuMeasureState.HasAnchor,
        };

    /// <summary>Latches the ground view's pending measurement to <paramref name="callsign"/>, so the line follows it.</summary>
    private static void MeasurePickOnAircraft(GroundViewModel vm, string callsign)
    {
        if (vm.Measure is { } measure)
        {
            measure.Pick(RblEndpoint.OnAircraft(callsign), GroundViewModel.MeasureView, vm.MeasureTrackLookup, GroundViewModel.MeasureUnits);
        }
    }

    private void OnEmptySpaceClicked()
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.SelectedAircraft = null;
        }
    }

    private void OnRunwayThresholdClicked(string runwayEnd, Point screenPos)
    {
        // A threshold click is a left-click: it opens a menu only when there is a point menu, never one of Settings alone.
        if ((DataContext is GroundViewModel vm) && (BuildRunwayThresholdMenu(vm, runwayEnd) is { } menu))
        {
            ViewSettingsMenu.Append(menu, FindMainViewModel(), SettingsSectionId.Ground);
            ShowContextMenu(menu);
        }
    }

    /// <summary>
    /// The menu a click on the <paramref name="runwayEnd"/> threshold shows, built without opening it: the point menu at
    /// the end's nearest hold-short node reachable by the selected aircraft (<see cref="BuildThresholdPointMenu"/>).
    /// Null with nothing selected, or when the aircraft has no node to start from or the end has no hold-short node.
    /// </summary>
    internal ContextMenu? BuildRunwayThresholdMenu(GroundViewModel vm, string runwayEnd)
    {
        if (vm.SelectedAircraft is not { } selected)
        {
            return null;
        }

        if (vm.GetAircraftNearestNodeId(selected) is null)
        {
            return null;
        }

        return (vm.FindNearestHoldShortNodeForRunwayEnd(selected, runwayEnd) is { } holdShortNodeId)
            ? BuildThresholdPointMenu(vm, selected, runwayEnd, holdShortNodeId)
            : null;
    }

    /// <summary>
    /// The shared point menu for <paramref name="selected"/> at hold-short node <paramref name="holdShortNodeId"/>,
    /// naming the clicked <paramref name="runwayEnd"/> so Taxi here routes to it and Custom taxi… seeds it, then the
    /// ground's section, Draw taxi route… from that node. Null, with a logged warning, when the node is not in the layout.
    /// </summary>
    internal ContextMenu? BuildThresholdPointMenu(GroundViewModel vm, AircraftModel selected, string runwayEnd, int holdShortNodeId)
    {
        if (vm.GetNode(holdShortNodeId) is not { } holdShort)
        {
            MenuLog.LogWarning(
                "Threshold click on {RunwayEnd}: hold-short node {NodeId} is not in the ground layout, so no menu opens",
                runwayEnd,
                holdShortNodeId
            );
            return null;
        }

        List<Control> section = [];
        AddDrawTaxiRouteFrom(section, vm, selected, holdShortNodeId);
        return BuildPointMenu(selected, new MenuPoint(new LatLon(holdShort.Latitude, holdShort.Longitude), holdShort, runwayEnd, [], null), section);
    }

    private void OnRunwaySurfaceRightClicked(IReadOnlyList<string> runways, Point screenPos)
    {
        if ((DataContext is not GroundViewModel vm) || (_canvas is null))
        {
            return;
        }

        (double lat, double lon) = _canvas.Viewport.ScreenToLatLon((float)screenPos.X, (float)screenPos.Y);
        if (WithViewSettings(BuildRunwaySurfaceMenu(vm, runways, new LatLon(lat, lon), _canvas.FindNearestNode(screenPos), screenPos)) is { } menu)
        {
            ShowContextMenu(menu);
        }
    }

    /// <summary>
    /// The menu a right-click on the surface of <paramref name="runways"/> at <paramref name="click"/> shows, built
    /// without opening it. With an aircraft selected, the shared point menu at the click carries Taxi to runway for each
    /// runway and Warp here to <paramref name="nearestNode"/> (the node the nearest-node fallback would have opened), then
    /// the ground's section: the measuring items, then Draw taxi route… from that node; with nothing selected, the
    /// measuring items alone. Null when there is nothing to show.
    /// </summary>
    internal ContextMenu? BuildRunwaySurfaceMenu(
        GroundViewModel vm,
        IReadOnlyList<string> runways,
        LatLon click,
        GroundNodeDto? nearestNode,
        Point screenPos
    )
    {
        List<Control> section = [];
        AddMeasureMenuItems(section, vm, screenPos);
        if (vm.SelectedAircraft is not { } selected)
        {
            return SectionMenu(section);
        }

        if (nearestNode is not null)
        {
            AddDrawTaxiRouteFrom(section, vm, selected, nearestNode.Id);
        }

        return BuildPointMenu(selected, new MenuPoint(click, null, null, runways, nearestNode), section);
    }

    private void OnDrawNodeHovered(int? nodeId)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.UpdateDrawHoverPreview(nodeId);
        }
    }

    private void OnAircraftHovered(string? callsign)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.SetHoveredAircraft(callsign);
        }
    }

    /// <summary>
    /// Commits the drawn tug move: the right-clicked node is the last target, and the PUSH (one target) or PUSHM
    /// (two or more) command goes straight out. A refused move sends nothing and stays in draw mode with its refusal
    /// on screen.
    /// </summary>
    private void FinishPushRoute(GroundViewModel vm, int nodeId)
    {
        vm.AddPushWaypoint(nodeId);
        SendPushRoute(vm);
    }

    /// <summary>Sends the drawn tug move as it stands; a refused move sends nothing and stays in draw mode.</summary>
    private void SendPushRoute(GroundViewModel vm)
    {
        string? callsign = vm.PushRouteCallsign;
        string? command = vm.FinishPushRoute();
        if (command is null || callsign is null)
        {
            return;
        }

        _ = vm.SendRawCommandAsync(callsign, GetInitials(), command);
    }

    /// <summary>
    /// A right-click on a push route being drawn: on a target's marker it opens that leg's push/pull menu (the last
    /// target's with <c>Send route</c> first), and anywhere else the clicked node (if any) becomes the last target and
    /// the route is sent.
    /// </summary>
    private void OnPushRouteRightClicked(IReadOnlyList<int> markerHits, int? nodeId)
    {
        if ((DataContext is not GroundViewModel vm) || (vm.DrawKind != DrawRouteKind.Push))
        {
            return;
        }

        (PushRightClickTarget target, int? waypointIndex) = vm.ClassifyPushRightClick(markerHits);
        switch (target)
        {
            case PushRightClickTarget.EarlierWaypoint when waypointIndex is { } index:
                ShowPushLegKindMenu(vm, index, offerSend: false);
                break;
            case PushRightClickTarget.LastWaypoint when waypointIndex is { } index:
                ShowPushLegKindMenu(vm, index, offerSend: true);
                break;
            case PushRightClickTarget.NewPoint when nodeId is { } id:
                FinishPushRoute(vm, id);
                break;
        }
    }

    /// <summary>
    /// A Shift+click or Shift+drag placed a marked point: a drag gives it the facing toward where it was released, and
    /// the right button also sends the route.
    /// </summary>
    private void OnDrawFreePointPlaced(LatLon point, LatLon? dragTo, bool finish)
    {
        if ((DataContext is not GroundViewModel vm) || (vm.DrawKind != DrawRouteKind.Push))
        {
            return;
        }

        MagneticHeading? facing = dragTo is { } releasedAt ? GroundViewModel.FreePointFacing(point, releasedAt) : null;
        vm.AddPushFreePoint(point.Lat, point.Lon, facing);
        if (finish)
        {
            SendPushRoute(vm);
        }
    }

    /// <summary>
    /// A left-drag moved a push-route target's marker: a marked point moves to where it was released, and a node target
    /// snaps to the node nearest there.
    /// </summary>
    private void OnPushMarkerDragged(int waypointIndex, LatLon to)
    {
        if ((DataContext is not GroundViewModel vm) || (vm.DrawKind != DrawRouteKind.Push))
        {
            return;
        }

        vm.MovePushTarget(waypointIndex, to);
    }

    /// <summary>
    /// The menu that forces the leg ending at a push-route point: <c>Push</c>, <c>Pull</c>, or back to the planner's own
    /// choice, the current one checked. On the last point it opens with <c>Send route</c>, which sends the route as it
    /// stands.
    /// </summary>
    private void ShowPushLegKindMenu(GroundViewModel vm, int waypointIndex, bool offerSend)
    {
        PushbackLegKind? current = vm.PushTargetForcedKind(waypointIndex);
        var menu = new ContextMenu();
        if (offerSend)
        {
            var send = new MenuItem { Header = "Send route" };
            send.Click += (_, _) => SendPushRoute(vm);
            menu.Items.Add(send);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = $"Leg to point {waypointIndex + 1}", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(LegKindItem("Push", PushbackLegKind.Push));
        menu.Items.Add(LegKindItem("Pull", PushbackLegKind.Pull));
        menu.Items.Add(LegKindItem("Let the planner choose", null));
        ShowContextMenu(menu);

        MenuItem LegKindItem(string header, PushbackLegKind? kind)
        {
            var item = new MenuItem
            {
                Header = header,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "PushLegKind",
                IsChecked = kind == current,
            };
            item.Click += (_, _) => vm.SetPushTargetForcedKind(waypointIndex, kind);
            return item;
        }
    }

    private void OnDrawNodeClicked(int nodeId)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        if (vm.DrawKind == DrawRouteKind.Push)
        {
            vm.AddPushWaypoint(nodeId);
            return;
        }

        vm.AddDrawWaypoint(nodeId);
    }

    private void OnDrawNodeFinished(int nodeId, Point screenPos)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        // Only reached for a push route when the view model publishes no markers (a node missing from the layout).
        if (vm.DrawKind == DrawRouteKind.Push)
        {
            FinishPushRoute(vm, nodeId);
            return;
        }

        vm.AddDrawWaypoint(nodeId);
        (TaxiRoute Route, string NodeRefPath, TaxiSpotDestination? Spot)? result = vm.FinishDrawRoute();
        if (result is null)
        {
            return;
        }

        (TaxiRoute? route, string? nodeRefPath, TaxiSpotDestination? spot) = result.Value;
        string? callsign = vm.SelectedAircraft?.Callsign;
        if (callsign is null)
        {
            return;
        }

        string initials = GetInitials();
        var menu = new ContextMenu();

        List<(string Label, string Command, TaxiRoute Preview)> variants = vm.BuildTaxiCrossingVariants(route, spot: spot, pathOverride: nodeRefPath);
        // The committed command is a dense node-ref path (precise but unreadable); show the
        // controller a readable taxiway summary instead while the Send items carry the dense path.
        string friendlyHeader = $"TAXI {vm.BuildTaxiCommand(route)}{(spot is not null ? $" {spot.Token}" : "")}";
        if (variants.Count <= 1)
        {
            string command = variants.Count == 1 ? variants[0].Command : "";
            TaxiRoute preview = variants.Count == 1 ? variants[0].Preview : route;
            menu.Items.Add(
                new MenuItem
                {
                    Header = friendlyHeader,
                    IsEnabled = false,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                }
            );
            MenuItem sendItem = CreateMenuItem("Send", () => vm.SendRawCommandAsync(callsign, initials, command));
            AttachPreviewHover(sendItem, vm, preview);
            menu.Items.Add(sendItem);
        }
        else
        {
            menu.Items.Add(
                new MenuItem
                {
                    Header = friendlyHeader,
                    IsEnabled = false,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                }
            );
            foreach ((string? label, string? command, TaxiRoute? preview) in variants)
            {
                string cmd = command;
                MenuItem item = CreateMenuItem(string.IsNullOrEmpty(label) ? cmd : label, () => vm.SendRawCommandAsync(callsign, initials, cmd));
                AttachPreviewHover(item, vm, preview);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(
            CreateMenuItem(
                "Copy to command input",
                () =>
                {
                    string taxi = vm.BuildDrawRouteCopyCommand(route, spot);
                    ShowTaxiInput(callsign, initials, taxi, taxi.Length);
                    return Task.CompletedTask;
                }
            )
        );
        menu.Items.Add(CreateMenuItem("Cancel", () => Task.CompletedTask));

        ShowContextMenu(menu);
    }

    private static void AttachPreviewHover(MenuItem item, GroundViewModel vm, TaxiRoute route) =>
        item.PointerEntered += (_, _) => vm.PreviewRoute = route;

    private static MenuItem CreateMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPointProperties props = e.GetCurrentPoint(_canvas!).Properties;
        if (props.IsLeftButtonPressed)
        {
            CloseActiveContextMenu();
            HideTaxiInput();
        }
    }

    private void CloseActiveContextMenu()
    {
        _activeContextMenu?.Close();
        _activeContextMenu = null;
    }

    private void ShowContextMenu(ContextMenu menu)
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
        _taxiInputOverlay?.IsVisible = false;

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
            string? text = _taxiInputBox?.Text?.Trim();
            string? callsign = _pendingCallsign;
            string? initials = _pendingInitials;
            HideTaxiInput();

            if (!string.IsNullOrEmpty(text) && callsign is not null && initials is not null && DataContext is GroundViewModel vm)
            {
                await vm.SendRawCommandAsync(callsign, initials, text);
            }
        }
    }

    private void OnTaxiInputLostFocus(object? sender, RoutedEventArgs e) => HideTaxiInput();

    private string GetInitials()
    {
        MainViewModel? mainVm = FindMainViewModel();
        return mainVm?.Preferences.UserInitials ?? "";
    }

    private MainViewModel? FindMainViewModel()
    {
        StyledElement? parent = this.Parent;
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

    private void OnPreferencesFontSizesChanged()
    {
        if (DataContext is GroundViewModel vm && vm.Preferences is not null)
        {
            ApplyFontSizePreferences(vm.Preferences);
        }
    }

    private void ApplyFontSizePreferences(Yaat.Client.Services.UserPreferences prefs)
    {
        if (_canvas is null)
        {
            return;
        }

        _canvas.DatablockTextSize = prefs.GroundDatablockFontSize;
        _canvas.LabelTextSize = prefs.GroundLabelFontSize;
        _canvas.ShowSpeechBubbles = prefs.ShowSpeechBubbles;
        _canvas.ScrollSensitivity = prefs.ScrollSensitivity;
    }

    /// <summary>
    /// Pushes the speech-bubble display preference to the ground canvas. The radar view syncs this
    /// via <c>SyncAssignmentTint</c>; the ground canvas otherwise only picks it up through the
    /// font-size path, so this gives MainWindow a dedicated seam to keep both views in lockstep
    /// when a speech-bubble setting changes without a font-size change.
    /// </summary>
    public void SyncSpeechBubblePreferences()
    {
        if (_canvas is null)
        {
            return;
        }

        if (DataContext is GroundViewModel vm && vm.Preferences is not null)
        {
            _canvas.ShowSpeechBubbles = vm.Preferences.ShowSpeechBubbles;
            _canvas.ScrollSensitivity = vm.Preferences.ScrollSensitivity;
        }
    }

    /// <summary>
    /// Scrolls the docked controls bar sideways when it is wider than the view (the scrollbar is
    /// hidden, mirroring the radar DCB). Vertical wheel motion maps to horizontal travel; a
    /// trackpad's horizontal delta is honoured directly.
    /// </summary>
    private void OnToolbarPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        ScrollViewer? scroller = this.FindControl<ScrollViewer>("ToolbarScroller");
        if (scroller is null)
        {
            return;
        }

        double delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        double sensitivity = _canvas?.ScrollSensitivity ?? 1.0;
        scroller.Offset = scroller.Offset.WithX(scroller.Offset.X - (Math.Sign(delta) * 40 * sensitivity));
        e.Handled = true;
    }
}
