using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.ContextMenus;
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
    private void AddMeasureMenuItems(ContextMenu menu, GroundViewModel vm, Point screenPos)
    {
        if (vm.Measure is not { } measure || _canvas is null)
        {
            return;
        }

        RblEndpoint endpoint = _canvas.MeasureEndpointAt(screenPos);
        string startLabel = measure.Anchor is null ? "Measure from here" : "Measure to here";
        menu.Items.Add(
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
            menu.Items.Add(
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
            menu.Items.Add(
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

        menu.Items.Add(new Separator());
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

        if (e.Key == Key.D && PlatformHelper.HasActionModifier(e.KeyModifiers) && _canvas is not null)
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
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        GroundNodeDto? node = vm.GetNode(nodeId);
        if (node is null)
        {
            return;
        }

        var menu = new ContextMenu();

        // Measuring is available with nothing selected, so these come before the aircraft-only items.
        AddMeasureMenuItems(menu, vm, screenPos);

        if (vm.SelectedAircraft is not null)
        {
            string callsign = vm.SelectedAircraft.Callsign;
            string initials = GetInitials();
            int? fromNodeId = vm.GetAircraftNearestNodeId(vm.SelectedAircraft);

            if (fromNodeId is not null)
            {
                TaxiSpotDestination? spotDest = node.Type switch
                {
                    "Spot" when node.Name is not null => new TaxiSpotDestination(node.Name, IsTaxiSpot: true),
                    "Parking" or "Helipad" when node.Name is not null => new TaxiSpotDestination(node.Name, IsTaxiSpot: false),
                    _ => null,
                };
                string? destRunway = node.Type == "RunwayHoldShort" && node.RunwayId is not null ? RunwayIdentifier.Parse(node.RunwayId).End1 : null;
                AddTaxiRouteItems(menu, vm, callsign, initials, fromNodeId.Value, nodeId, spotDest, destRunway);
            }

            // Same gate as the aircraft context menu's pushback items: PUSH to a spot is accepted from a
            // stand and from a completed pushback, so an aircraft resting on a ramp spot can be pushed on.
            bool canPush = AircraftCommandApplicability.CanPushBack(vm.SelectedAircraft);
            if (node.Type is "Parking" or "Spot" && node.Name is not null && canPush)
            {
                string spotName = node.Name;
                char pushPrefix = node.Type == "Spot" ? '$' : '@';
                menu.Items.Add(
                    CreateMenuItem($"Push to {spotName}", () => vm.SendRawCommandAsync(callsign, initials, $"PUSH {pushPrefix}{spotName}"))
                );
            }

            int nid = nodeId;
            menu.Items.Add(
                CreateMenuItem(
                    "Draw taxi route...",
                    () =>
                    {
                        vm.StartDrawRoute(vm.SelectedAircraft!);
                        vm.AddDrawWaypoint(nid);
                        return Task.CompletedTask;
                    }
                )
            );

            if (canPush)
            {
                menu.Items.Add(
                    CreateMenuItem(
                        "Push route...",
                        () =>
                        {
                            vm.StartPushRoute(vm.SelectedAircraft!);
                            vm.AddPushWaypoint(nid);
                            return Task.CompletedTask;
                        }
                    )
                );
            }

            (string? prefill, int caretPos) = BuildCustomTaxiPrefill(vm, node, nodeId);
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
            menu.Items.Add(CreateMenuItem("Warp here", () => vm.WarpToNodeAsync(callsign, initials, nodeId)));
        }

        // With no aircraft selected the menu is just the measuring items, so drop the divider they add to
        // separate themselves from the aircraft items that would normally follow.
        while (menu.Items.Count > 0 && menu.Items[^1] is Separator)
        {
            menu.Items.RemoveAt(menu.Items.Count - 1);
        }

        if (menu.Items.Count > 0)
        {
            ShowContextMenu(menu);
        }
    }

    private void OnAircraftRightClicked(string callsign, Point screenPos)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        AircraftModel? ac = FindMainViewModel()?.Aircraft.FirstOrDefault(a => a.Callsign == callsign);

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

        ShowContextMenu(BuildAircraftContextMenu(vm, new GroundMenuTarget(ac, prevSelected, callsign, GetInitials())));
    }

    /// <summary>
    /// The whole aircraft context menu a right-click shows, built without opening it: the header, the favorites
    /// block, then either the phase-aware ground command groups and the display items or, for a surface live-traffic
    /// shadow, the read-only shadow tree every view shares (SharedMenuGroups.AddSurfaceShadow), for the aircraft
    /// <paramref name="target"/> resolves. Touches no canvas, popup or pointer state.
    /// </summary>
    internal ContextMenu BuildAircraftContextMenu(GroundViewModel vm, GroundMenuTarget target)
    {
        (AircraftModel? ac, AircraftModel? _, string callsign, string _) = target;
        MenuContext context = MenuContextFor(target);
        var host = new GroundMenuHost(this, vm, FindMainViewModel(), ac);
        var menu = new ContextMenu();

        SharedMenuGroups.AddHeader(menu.Items, ac, context, host);
        menu.Items.Add(SharedMenuGroups.Favorites(ac, context, host));
        menu.Items.Add(new Separator());

        if (ac is { IsLiveTraffic: true } && !AircraftCommandApplicability.CanAssume(ac))
        {
            // A surface shadow is never assumable, so it stays read-only: the shared shadow tree every view gives it
            // (SharedMenuGroups.AddSurfaceShadow), then the foot. No ground command group is offered for it.
            SharedMenuGroups.AddSurfaceShadow(menu.Items, ac, context, host, BuildCanvasDisplay(vm, context, host));
        }
        else
        {
            if (ac is { IsLiveTraffic: true })
            {
                // An assumable shadow takes the two assume items and then the same ground command groups a simulated
                // aircraft gets: a command sent to it auto-assumes it server-side, so the groups apply as they are.
                SharedMenuGroups.AddLiveTrafficAssume(menu.Items, ac, context, host);
                menu.Items.Add(new Separator());
            }

            AddSimulatedAircraftItems(menu, vm, target);
            SharedMenuGroups.AddGroundDisplay(menu.Items, BuildCanvasItems(vm, context));
        }

        SharedMenuGroups.AddFoot(menu.Items, ac, context, host);

        // RPO control
        FindMainViewModel()?.BuildRpoMenuItems(menu, [callsign]);

        return menu;
    }

    /// <summary>
    /// The catalog context for <paramref name="target"/> on the ground view: the solo-training flag and the
    /// "VFR commands for IFR aircraft" setting come from the main view model, the setting falling back to
    /// <see cref="VfrCommandsForIfr.None"/> when none is attached.
    /// </summary>
    private MenuContext MenuContextFor(GroundMenuTarget target)
    {
        MainViewModel? main = FindMainViewModel();
        return new MenuContext(
            new MenuClick(target.Callsign, target.PrevSelected, []),
            new MenuSession(target.Initials, main?.SessionSoloTrainingMode ?? false, main?.VfrCommandsForIfr ?? VfrCommandsForIfr.None),
            MenuView.Ground
        );
    }

    /// <summary>
    /// The ground canvas's display items, built from the ground view model's own state: the taxi-route submenu, show
    /// or hide datablock, then reset datablock position while the data block sits away from its position, then the
    /// measure item, which latches the measurement to the aircraft so the line follows it as it taxis. An item the
    /// state hides is null. The ground's flat menu places them as they are; the shadow's Display submenu wraps them.
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
    /// The ground canvas's Display submenu for a live-traffic shadow: the ground's own display items, then the
    /// leader-direction, J-ring and cone overlays, then blank and unblank.
    /// </summary>
    internal MenuItem BuildCanvasDisplay(GroundViewModel vm, MenuContext context, IMenuHost host) =>
        CanvasMenuItems.Display([
            BuildCanvasItems(vm, context),
            [CanvasMenuItems.LeaderDirection(context, host), CanvasMenuItems.JRing(context, host), CanvasMenuItems.Cone(context, host)],
            [CanvasMenuItems.Blank(context, host), CanvasMenuItems.Unblank(context, host)],
        ]);

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

    /// <summary>
    /// The phase-aware ground command items, for a simulated aircraft and for an assumable live-traffic shadow:
    /// release checks, the relative items, pushback, taxi holds, hold-short / crossing, takeoff and landing
    /// clearances, runway exits, preset taxi routes and taxi-route drawing, all catalog entries and ground groups from
    /// <see cref="SharedMenuGroups"/>, in the ground's order. An airborne shadow has no
    /// ground phase, so the state-gated predicates inside yield nothing for it, and a surface shadow never reaches
    /// here, being unassumable — it takes the shared shadow tree instead.
    /// </summary>
    internal void AddSimulatedAircraftItems(ContextMenu menu, GroundViewModel vm, GroundMenuTarget target)
    {
        AircraftModel? ac = target.Aircraft;
        MenuContext context = MenuContextFor(target);
        var host = new GroundMenuHost(this, vm, FindMainViewModel(), ac);

        SharedMenuGroups.AddGroundAircraftCommands(menu.Items, ac, context, host);
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
        if (DataContext is not GroundViewModel vm || vm.SelectedAircraft is null)
        {
            return;
        }

        string callsign = vm.SelectedAircraft.Callsign;
        string initials = GetInitials();
        int? fromNodeId = vm.GetAircraftNearestNodeId(vm.SelectedAircraft);
        if (fromNodeId is null)
        {
            return;
        }

        int? holdShortNodeId = vm.FindNearestHoldShortNodeForRunwayEnd(vm.SelectedAircraft, runwayEnd);
        if (holdShortNodeId is null)
        {
            return;
        }

        var menu = new ContextMenu();
        AddTaxiRouteItems(menu, vm, callsign, initials, fromNodeId.Value, holdShortNodeId.Value, spot: null, destRunway: runwayEnd);

        // Mirror the hold-short node menu — give the controller the same draw /
        // custom / warp escape hatches when clicking the threshold marker.
        int nid = holdShortNodeId.Value;
        GroundNodeDto? node = vm.GetNode(nid);
        menu.Items.Add(
            CreateMenuItem(
                "Draw taxi route...",
                () =>
                {
                    vm.StartDrawRoute(vm.SelectedAircraft!);
                    vm.AddDrawWaypoint(nid);
                    return Task.CompletedTask;
                }
            )
        );

        if (node is not null)
        {
            (string? prefill, int caretPos) = BuildCustomTaxiPrefill(vm, node, nid);
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
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Warp here", () => vm.WarpToNodeAsync(callsign, initials, nid)));

        if (menu.Items.Count == 0)
        {
            return;
        }

        ShowContextMenu(menu);
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

    private static void AddTaxiRouteItems(
        ContextMenu menu,
        GroundViewModel vm,
        string callsign,
        string initials,
        int fromNodeId,
        int toNodeId,
        TaxiSpotDestination? spot,
        string? destRunway
    )
    {
        // Preview with the aircraft's real category so route options match command execution.
        // Both callers derive `callsign` from vm.SelectedAircraft, so it is the routed aircraft.
        AircraftCategory category = vm.SelectedAircraft is { } ac ? GroundViewModel.CategoryFor(ac) : AircraftCategory.Jet;
        WakeTurbulenceData.WakeClass wakeClass = vm.SelectedAircraft is { } wc
            ? GroundViewModel.WakeClassFor(wc)
            : WakeTurbulenceData.WakeClass.Large;
        List<TaxiRoute> routes = vm.FindRoutesToNode(fromNodeId, toNodeId, category, wakeClass);

        if (routes.Count == 0)
        {
            var disabled = new MenuItem { Header = "No route found", IsEnabled = false };
            menu.Items.Add(disabled);
            return;
        }

        if (routes.Count == 1)
        {
            AddSingleRouteItems(menu, vm, callsign, initials, routes[0], spot, destRunway);
        }
        else
        {
            var parent = new MenuItem { Header = "Taxi here" };
            foreach (TaxiRoute route in routes)
            {
                AddSingleRouteItems(parent, vm, callsign, initials, route, spot, destRunway);
            }

            menu.Items.Add(parent);
        }
    }

    private static void AddSingleRouteItems(
        ItemsControl parent,
        GroundViewModel vm,
        string callsign,
        string initials,
        TaxiRoute route,
        TaxiSpotDestination? spot,
        string? destRunway
    )
    {
        string displayName = spot is not null ? $"to {spot.Name} {vm.GetTaxiwayDisplayName(route)}" : vm.GetTaxiwayDisplayName(route);
        List<(string Label, string Command, TaxiRoute Preview)> variants = vm.BuildTaxiCrossingVariants(route, spot, pathOverride: null);

        // When destination is a runway hold-short, offer RWY and non-RWY variants
        // with progressive crossing options for each.
        if (destRunway is not null)
        {
            List<(string Label, string Command, TaxiRoute Preview)?> destVariants = vm.BuildTaxiDestVariants(route, destRunway, spot);
            if (destVariants.Count == 0)
            {
                return;
            }

            var sub = new MenuItem { Header = $"Taxi {displayName}" };
            AttachPreviewHover(sub, vm, route);

            foreach ((string Label, string Command, TaxiRoute Preview)? entry in destVariants)
            {
                if (entry is null)
                {
                    sub.Items.Add(new Separator());
                    continue;
                }

                (string? label, string? command, TaxiRoute? preview) = entry.Value;
                string cmd = command;
                MenuItem child = CreateMenuItem(label, () => vm.SendRawCommandAsync(callsign, initials, cmd));
                AttachPreviewHover(child, vm, preview);
                sub.Items.Add(child);
            }

            parent.Items.Add(sub);
            return;
        }

        if (variants.Count <= 1)
        {
            string command = variants.Count == 1 ? variants[0].Command : "";
            TaxiRoute preview = variants.Count == 1 ? variants[0].Preview : route;
            MenuItem item = CreateMenuItem($"Taxi {displayName}", () => vm.SendRawCommandAsync(callsign, initials, command));
            AttachPreviewHover(item, vm, preview);
            parent.Items.Add(item);
            return;
        }

        var defaultSub = new MenuItem { Header = $"Taxi {displayName}" };
        AttachPreviewHover(defaultSub, vm, route);

        foreach ((string? label, string? command, TaxiRoute? preview) in variants)
        {
            string cmd = command;
            MenuItem child = CreateMenuItem(label, () => vm.SendRawCommandAsync(callsign, initials, cmd));
            AttachPreviewHover(child, vm, preview);
            defaultSub.Items.Add(child);
        }

        parent.Items.Add(defaultSub);
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

    private static (string Text, int CaretIndex) BuildCustomTaxiPrefill(GroundViewModel vm, GroundNodeDto node, int nodeId)
    {
        const string taxiPrefix = "TAXI ";

        switch (node.Type)
        {
            case "Parking" or "Helipad" when node.Name is not null:
                // "TAXI  @STAND" — cursor between TAXI and @STAND
                string parkingSuffix = $"@{node.Name}";
                return ($"{taxiPrefix} {parkingSuffix}", taxiPrefix.Length);

            case "Spot" when node.Name is not null:
                // "TAXI  $SPOT" — cursor between TAXI and $SPOT
                string spotSuffixToken = $"${node.Name}";
                return ($"{taxiPrefix} {spotSuffixToken}", taxiPrefix.Length);

            case "RunwayHoldShort" when node.RunwayId is not null:
                // "RWY 30 TAXI " — cursor at end for user to add taxiway route
                string rwyEnd1 = RunwayIdentifier.ToDisplayDesignator(RunwayIdentifier.Parse(node.RunwayId).End1);
                string rwyText = $"RWY {rwyEnd1} {taxiPrefix}";
                return (rwyText, rwyText.Length);

            default:
                // Taxiway intersection or spot: "TAXI  E" — cursor between TAXI and taxiway name
                List<string> names = vm.GetNodeTaxiwayNames(nodeId);
                if (names.Count > 0)
                {
                    string twySuffix = names[0];
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

/// <summary>The aircraft a ground context menu is being built for, with the selection it may act relative to.</summary>
internal readonly record struct GroundMenuTarget(AircraftModel? Aircraft, AircraftModel? PrevSelected, string Callsign, string Initials);
