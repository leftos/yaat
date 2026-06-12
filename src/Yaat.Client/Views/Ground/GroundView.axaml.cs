using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Radar.Flyouts;
using Yaat.Sim;
using Yaat.Sim.Data;
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
        if (_resetButton is not null)
        {
            _resetButton.AddHandler(PointerPressedEvent, OnResetButtonPointerPressed, RoutingStrategies.Tunnel);
        }

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

        if (_resetButton is not null)
        {
            _resetButton.RemoveHandler(PointerPressedEvent, OnResetButtonPointerPressed);
        }

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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

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

    private void OnToggleTaxiwayLabels(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.ShowTaxiwayLabels = !vm.ShowTaxiwayLabels;
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

    private void OnResetView(object? sender, RoutedEventArgs e)
    {
        _canvas?.ResetView();
    }

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
            FlightPlanEditorManager.Open(ac, mainVm, TopLevel.GetTopLevel(this) as Window);
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
                TaxiSpotDestination? spotDest = node.Type switch
                {
                    "Spot" when node.Name is not null => new TaxiSpotDestination(node.Name, IsTaxiSpot: true),
                    "Parking" or "Helipad" when node.Name is not null => new TaxiSpotDestination(node.Name, IsTaxiSpot: false),
                    _ => null,
                };
                string? destRunway = node.Type == "RunwayHoldShort" && node.RunwayId is not null ? RunwayIdentifier.Parse(node.RunwayId).End1 : null;
                AddTaxiRouteItems(menu, vm, callsign, initials, fromNodeId.Value, nodeId, spotDest, destRunway);
            }

            if (node.Type is "Parking" or "Spot" && node.Name is not null && vm.SelectedAircraft.CurrentPhase == "At Parking")
            {
                var spotName = node.Name;
                var pushPrefix = node.Type == "Spot" ? '$' : '@';
                menu.Items.Add(
                    CreateMenuItem($"Push to {spotName}", () => vm.SendRawCommandAsync(callsign, initials, $"PUSH {pushPrefix}{spotName}"))
                );
            }

            var nid = nodeId;
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
            menu.Items.Add(CreateMenuItem("Warp here", () => vm.WarpToNodeAsync(callsign, initials, nodeId)));
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

        var ac = FindMainViewModel()?.Aircraft.FirstOrDefault(a => a.Callsign == callsign);

        // Keep the previously-selected aircraft as the command recipient when the
        // controller right-clicks a DIFFERENT aircraft, so selected→right-clicked
        // relative actions (give way / follow) target the selected aircraft. Only adopt
        // the right-clicked aircraft as the selection when nothing was selected or the
        // same aircraft was re-clicked. Left-click remains the way to change selection.
        var prevSelected = vm.SelectedAircraft;
        if (ac is not null && (prevSelected is null || string.Equals(prevSelected.Callsign, callsign, StringComparison.OrdinalIgnoreCase)))
        {
            vm.SelectedAircraft = ac;
        }

        // When a different on-ground aircraft is selected, the direct "give way / follow"
        // items replace the candidate Follow…/Give way to… submenus.
        var isRelative =
            ac is not null
            && RelativeTrafficActions.HasRelativeContext(prevSelected, callsign)
            && RelativeTrafficActions.ShouldOfferGroundActions(prevSelected!, ac);

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
        menu.Items.Add(
            CreateMenuItem(
                "Command…",
                () =>
                {
                    CommandFlyout.Open(_canvas!, callsign, cmd => vm.SendRawCommandAsync(callsign, initials, cmd));
                    return Task.CompletedTask;
                }
            )
        );
        menu.Items.Add(
            CreateMenuItem(
                "Note…",
                () =>
                {
                    NoteFlyout.Open(_canvas!, callsign, ac?.Note ?? "", cmd => vm.SendRawCommandAsync(callsign, initials, cmd));
                    return Task.CompletedTask;
                }
            )
        );
        menu.Items.Add(new Separator());

        menu.Items.Add(FavoritesContextMenu.Build(FindMainViewModel(), ac, callsign, initials));
        menu.Items.Add(new Separator());

        AddRelativeGroundItems(menu, vm, prevSelected, callsign, initials, isRelative);

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

                var pushSubmenu = BuildPushbackToSpotSubmenu(vm, ac, callsign, initials);
                if (pushSubmenu is not null)
                {
                    menu.Items.Add(pushSubmenu);
                }
            }
        }

        if (phase is "Pushback" or "Pushback to Spot" or "Taxiing")
        {
            menu.Items.Add(CreateMenuItem("Hold position", () => vm.HoldPositionAsync(callsign, initials)));
        }

        if (phase == "Taxiing" && ac is not null)
        {
            AddHoldShortSubmenu(menu, vm, ac, callsign, initials);
            if (!isRelative)
            {
                AddFollowBehindSubmenus(menu, ac, callsign, initials);
            }

            // BREAK overrides the ground-conflict speed limit for 15 seconds.
            // Useful when two aircraft are mutually stopped by the conflict
            // detector and the controller needs one of them to push through.
            menu.Items.Add(CreateMenuItem("Break conflict", () => vm.SendRawCommandAsync(callsign, initials, "BREAK")));

            // CTO during taxi is accepted by the dispatcher and stored as a
            // deferred clearance — applied when the aircraft reaches the runway.
            // Only meaningful when a runway is already assigned to taxi to.
            if (!string.IsNullOrEmpty(ac.AssignedRunway))
            {
                AddCtoSubmenu(menu, vm, ac, callsign, initials, ac.AssignedRunway);
            }
        }

        if (phase.StartsWith("Following", StringComparison.Ordinal))
        {
            menu.Items.Add(CreateMenuItem("Hold position", () => vm.HoldPositionAsync(callsign, initials)));
        }

        if (phase.StartsWith("Holding Short", StringComparison.Ordinal))
        {
            AddHoldShortCrossingItems(menu, vm, ac, phase, callsign, initials);
        }

        if (phase == "Holding In Position")
        {
            // Holding In Position always has a paused route (the aircraft was
            // mid-taxi when HOLDPOSITION was issued).
            menu.Items.Add(CreateMenuItem("Resume taxi", () => vm.ResumeAsync(callsign, initials)));
        }

        if (phase is "Holding After Exit" or "Holding After Pushback")
        {
            // After-pushback / after-exit only have a route to resume if the
            // controller had already issued a TAXI command before something
            // halted the aircraft. Hide the menu item otherwise — RES wouldn't
            // do anything useful, the controller needs to issue TAXI.
            if (ac is { HasActiveTaxiRoute: true })
            {
                menu.Items.Add(CreateMenuItem("Resume taxi", () => vm.ResumeAsync(callsign, initials)));
            }
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

        if (
            AircraftCommandApplicability.CanClearToLand(ac)
            || AircraftCommandApplicability.CanGoAround(ac)
            || AircraftCommandApplicability.CanCancelLandingClearance(ac)
        )
        {
            var rwy = !string.IsNullOrEmpty(ac?.AssignedRunway) ? $" {RunwayIdentifier.ToDisplayDesignator(ac.AssignedRunway)}" : "";
            if (AircraftCommandApplicability.CanClearToLand(ac))
            {
                menu.Items.Add(CreateMenuItem($"Cleared to land{rwy}", () => vm.ClearedToLandAsync(callsign, initials)));
                // Force landing (CLANDF) is RPO-only — hidden in solo training.
                if (FindMainViewModel()?.SessionSoloTrainingMode != true)
                {
                    menu.Items.Add(CreateMenuItem($"Force landing{rwy}", () => vm.ForceLandingAsync(callsign, initials)));
                }
                if (AircraftCommandApplicability.CanIssueVfrOption(ac))
                {
                    menu.Items.Add(CreateMenuItem($"Touch and go{rwy}", () => vm.TouchAndGoAsync(callsign, initials)));
                    menu.Items.Add(CreateMenuItem($"Stop and go{rwy}", () => vm.StopAndGoAsync(callsign, initials)));
                    menu.Items.Add(CreateMenuItem($"Low approach{rwy}", () => vm.LowApproachAsync(callsign, initials)));
                    menu.Items.Add(CreateMenuItem($"Cleared for the option{rwy}", () => vm.ClearedForOptionAsync(callsign, initials)));
                }
            }
            if (AircraftCommandApplicability.CanGoAround(ac))
            {
                menu.Items.Add(CreateMenuItem($"Go around{rwy}", () => vm.GoAroundAsync(callsign, initials)));
            }
            if (AircraftCommandApplicability.CanCancelLandingClearance(ac))
            {
                menu.Items.Add(CreateMenuItem("Cancel landing clearance", () => vm.CancelLandingClearanceAsync(callsign, initials)));
            }
        }

        if (AircraftCommandApplicability.CanExitRunway(ac))
        {
            menu.Items.Add(CreateMenuItem("Exit left", () => vm.ExitLeftAsync(callsign, initials)));
            menu.Items.Add(CreateMenuItem("Exit right", () => vm.ExitRightAsync(callsign, initials)));
        }

        if (phase == "Takeoff")
        {
            menu.Items.Add(CreateMenuItem("Cancel takeoff clearance", () => vm.CancelTakeoffClearanceAsync(callsign, initials)));
        }

        if (
            phase is "At Parking" or "Pushback" or "Pushback to Spot" or "Taxiing" or "Holding After Exit" or "Holding After Pushback"
            || phase.StartsWith("Holding Short", StringComparison.Ordinal)
        )
        {
            menu.Items.Add(new Separator());
            var presetSubmenu = BuildPresetTaxiSubmenu(vm, ac, callsign, initials);
            if (presetSubmenu is not null)
            {
                menu.Items.Add(presetSubmenu);
            }

            menu.Items.Add(
                CreateMenuItem(
                    "Draw taxi route...",
                    () =>
                    {
                        vm.StartDrawRoute(ac!);
                        return Task.CompletedTask;
                    }
                )
            );
        }

        var isRouteShown = vm.IsPathShown(callsign);
        menu.Items.Add(
            CreateMenuItem(
                isRouteShown ? "Hide taxi route" : "Show taxi route",
                () =>
                {
                    vm.ToggleShowTaxiRoute(callsign);
                    return Task.CompletedTask;
                }
            )
        );

        var isDbHidden = _canvas?.IsDataBlockHidden(callsign) ?? false;
        menu.Items.Add(
            CreateMenuItem(
                isDbHidden ? "Show datablock" : "Hide datablock",
                () =>
                {
                    _canvas?.ToggleHiddenDataBlock(callsign);
                    return Task.CompletedTask;
                }
            )
        );

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Delete", () => vm.DeleteAsync(callsign, initials)));

        // RPO control
        FindMainViewModel()?.BuildRpoMenuItems(menu, [callsign]);

        ShowContextMenu(menu);
    }

    /// <summary>
    /// When a different on-ground aircraft is selected, adds direct "give way to" /
    /// "follow" items issued to that selected aircraft referencing the right-clicked
    /// aircraft. No-op otherwise. When active these REPLACE the candidate
    /// "Follow…/Give way to…" submenus (see <see cref="AddFollowBehindSubmenus"/>).
    /// </summary>
    private void AddRelativeGroundItems(
        ContextMenu menu,
        GroundViewModel vm,
        AircraftModel? selected,
        string callsign,
        string initials,
        bool isRelative
    )
    {
        if (!isRelative)
        {
            return;
        }

        var a = selected!.Callsign;
        menu.Items.Add(
            new MenuItem
            {
                Header = $"↪ {a}:",
                IsEnabled = false,
                FontWeight = Avalonia.Media.FontWeight.Bold,
            }
        );
        menu.Items.Add(CreateMenuItem($"{a}: give way to {callsign}", () => vm.SendRawCommandAsync(a, initials, $"GW {callsign}")));
        menu.Items.Add(CreateMenuItem($"{a}: follow {callsign}", () => vm.SendRawCommandAsync(a, initials, $"FOLLOWG {callsign}")));
        menu.Items.Add(new Separator());
    }

    /// <summary>
    /// Adds "Follow..." and "Give way to..." submenus listing other ground aircraft
    /// (sorted by distance, capped at 12). Skips when no other ground aircraft are present.
    /// </summary>
    private void AddFollowBehindSubmenus(ContextMenu menu, AircraftModel ac, string callsign, string initials)
    {
        var mainVm = FindMainViewModel();
        if (mainVm is null || DataContext is not GroundViewModel vm)
        {
            return;
        }

        var candidates = new List<(AircraftModel Other, double DistNm)>();
        foreach (var other in mainVm.Aircraft)
        {
            if (other.Callsign == callsign || !other.IsOnGround)
            {
                continue;
            }

            var dist = GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, other.Position.Lat, other.Position.Lon);
            candidates.Add((other, dist));
        }

        if (candidates.Count == 0)
        {
            return;
        }

        candidates.Sort((a, b) => a.DistNm.CompareTo(b.DistNm));
        const int maxItems = 12;
        if (candidates.Count > maxItems)
        {
            candidates = candidates.GetRange(0, maxItems);
        }

        var followSub = new MenuItem { Header = "Follow..." };
        var giveSub = new MenuItem { Header = "Give way to..." };
        foreach (var (other, _) in candidates)
        {
            var target = other.Callsign;
            followSub.Items.Add(CreateMenuItem(target, () => vm.SendRawCommandAsync(callsign, initials, $"FOLLOWG {target}")));
            giveSub.Items.Add(CreateMenuItem(target, () => vm.SendRawCommandAsync(callsign, initials, $"GW {target}")));
        }

        menu.Items.Add(followSub);
        menu.Items.Add(giveSub);
    }

    /// <summary>
    /// Builds the "Push back to..." submenu listing the closest Parking/Spot/Helipad nodes
    /// to the aircraft. Sends the canonical PUSH command (`@name` for parking/helipad,
    /// `$name` for spot). Returns null when the layout is unavailable or there are no candidates.
    /// </summary>
    private static MenuItem? BuildPushbackToSpotSubmenu(GroundViewModel vm, AircraftModel ac, string callsign, string initials)
    {
        var layout = vm.DomainLayout;
        if (layout is null)
        {
            return null;
        }

        var currentNodeId = vm.GetAircraftNearestNodeId(ac);
        var candidates = new List<(GroundNode Node, double DistNm)>();
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type is not (GroundNodeType.Parking or GroundNodeType.Spot or GroundNodeType.Helipad))
            {
                continue;
            }

            if (string.IsNullOrEmpty(node.Name))
            {
                continue;
            }

            if (currentNodeId.HasValue && node.Id == currentNodeId.Value)
            {
                continue;
            }

            var dist = GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, node.Position.Lat, node.Position.Lon);
            candidates.Add((node, dist));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        candidates.Sort((a, b) => a.DistNm.CompareTo(b.DistNm));
        const int maxItems = 30;
        if (candidates.Count > maxItems)
        {
            candidates = candidates.GetRange(0, maxItems);
        }

        var submenu = new MenuItem { Header = "Push back to..." };
        foreach (var (node, _) in candidates)
        {
            var name = node.Name!;
            var prefix = node.Type == GroundNodeType.Spot ? '$' : '@';
            var cmd = $"PUSH {prefix}{name}";
            submenu.Items.Add(CreateMenuItem(name, () => vm.SendRawCommandAsync(callsign, initials, cmd)));
        }

        return submenu;
    }

    /// <summary>
    /// Builds the "Preset taxi route" submenu — one click per route from the loaded
    /// per-ARTCC catalog. Routes are filtered against the aircraft's current ground node:
    /// any route whose path can't be walked from here is silently dropped. Returns null
    /// when no routes are applicable so the caller can omit the empty submenu.
    /// </summary>
    private static MenuItem? BuildPresetTaxiSubmenu(GroundViewModel vm, AircraftModel? ac, string callsign, string initials)
    {
        if (ac is null)
        {
            return null;
        }

        var layout = vm.DomainLayout;
        if (layout is null)
        {
            return null;
        }

        AirportSidecarCatalog catalog;
        try
        {
            catalog = NavigationDatabase.Instance.AirportSidecars;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var routes = catalog.GetTaxiRoutes(layout.AirportId);
        if (routes.Count == 0)
        {
            return null;
        }

        int? fromNodeId = vm.GetAircraftNearestNodeId(ac);
        if (fromNodeId is null)
        {
            return null;
        }

        var submenu = new MenuItem { Header = "Preset taxi route" };

        foreach (var route in routes)
        {
            var resolved = TaxiPathfinder.ResolveExplicitPath(
                layout,
                fromNodeId.Value,
                route.GetPathTokens(),
                out _,
                new ExplicitPathOptions { DestinationRunway = route.DestinationRunway, AirportId = layout.AirportId },
                AircraftCategory.Jet
            );
            if (resolved is null)
            {
                continue;
            }

            string command = route.ToCanonicalCommand();
            submenu.Items.Add(CreateMenuItem(route.Name, () => vm.SendRawCommandAsync(callsign, initials, command)));
        }

        return submenu.Items.Count > 0 ? submenu : null;
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

        var callsign = vm.SelectedAircraft.Callsign;
        var initials = GetInitials();
        var fromNodeId = vm.GetAircraftNearestNodeId(vm.SelectedAircraft);
        if (fromNodeId is null)
        {
            return;
        }

        var holdShortNodeId = vm.FindNearestHoldShortNodeForRunwayEnd(vm.SelectedAircraft, runwayEnd);
        if (holdShortNodeId is null)
        {
            return;
        }

        var menu = new ContextMenu();
        AddTaxiRouteItems(menu, vm, callsign, initials, fromNodeId.Value, holdShortNodeId.Value, spot: null, destRunway: runwayEnd);

        // Mirror the hold-short node menu — give the controller the same draw /
        // custom / warp escape hatches when clicking the threshold marker.
        var nid = holdShortNodeId.Value;
        var node = vm.GetNode(nid);
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
            var (prefill, caretPos) = BuildCustomTaxiPrefill(vm, node, nid);
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

    private void OnDrawNodeClicked(int nodeId)
    {
        if (DataContext is GroundViewModel vm)
        {
            vm.AddDrawWaypoint(nodeId);
        }
    }

    private void OnDrawNodeFinished(int nodeId, Point screenPos)
    {
        if (DataContext is not GroundViewModel vm)
        {
            return;
        }

        vm.AddDrawWaypoint(nodeId);
        var result = vm.FinishDrawRoute();
        if (result is null)
        {
            return;
        }

        var (route, nodeRefPath, spot) = result.Value;
        var callsign = vm.SelectedAircraft?.Callsign;
        if (callsign is null)
        {
            return;
        }

        var initials = GetInitials();
        var menu = new ContextMenu();

        var variants = vm.BuildTaxiCrossingVariants(route, spot: spot, pathOverride: nodeRefPath);
        // The committed command is a dense node-ref path (precise but unreadable); show the
        // controller a readable taxiway summary instead while the Send items carry the dense path.
        var friendlyHeader = $"TAXI {vm.BuildTaxiCommand(route)}{(spot is not null ? $" {spot.Token}" : "")}";
        if (variants.Count <= 1)
        {
            var command = variants.Count == 1 ? variants[0].Command : "";
            var preview = variants.Count == 1 ? variants[0].Preview : route;
            menu.Items.Add(
                new MenuItem
                {
                    Header = friendlyHeader,
                    IsEnabled = false,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                }
            );
            var sendItem = CreateMenuItem("Send", () => vm.SendRawCommandAsync(callsign, initials, command));
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
            foreach (var (label, command, preview) in variants)
            {
                var cmd = command;
                var item = CreateMenuItem(string.IsNullOrEmpty(label) ? cmd : label, () => vm.SendRawCommandAsync(callsign, initials, cmd));
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
                    var taxi = vm.BuildDrawRouteCopyCommand(route, spot);
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
        var category = vm.SelectedAircraft is { } ac ? GroundViewModel.CategoryFor(ac) : AircraftCategory.Jet;
        var routes = vm.FindRoutesToNode(fromNodeId, toNodeId, category);

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
            foreach (var route in routes)
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
        var displayName = spot is not null ? $"to {spot.Name} {vm.GetTaxiwayDisplayName(route)}" : vm.GetTaxiwayDisplayName(route);
        var variants = vm.BuildTaxiCrossingVariants(route, spot, pathOverride: null);

        // When destination is a runway hold-short, offer RWY and non-RWY variants
        // with progressive crossing options for each.
        if (destRunway is not null)
        {
            var destVariants = vm.BuildTaxiDestVariants(route, destRunway, spot);
            if (destVariants.Count == 0)
            {
                return;
            }

            var sub = new MenuItem { Header = $"Taxi {displayName}" };
            AttachPreviewHover(sub, vm, route);

            foreach (var entry in destVariants)
            {
                if (entry is null)
                {
                    sub.Items.Add(new Separator());
                    continue;
                }

                var (label, command, preview) = entry.Value;
                var cmd = command;
                var child = CreateMenuItem(label, () => vm.SendRawCommandAsync(callsign, initials, cmd));
                AttachPreviewHover(child, vm, preview);
                sub.Items.Add(child);
            }

            parent.Items.Add(sub);
            return;
        }

        if (variants.Count <= 1)
        {
            var command = variants.Count == 1 ? variants[0].Command : "";
            var preview = variants.Count == 1 ? variants[0].Preview : route;
            var item = CreateMenuItem($"Taxi {displayName}", () => vm.SendRawCommandAsync(callsign, initials, command));
            AttachPreviewHover(item, vm, preview);
            parent.Items.Add(item);
            return;
        }

        var defaultSub = new MenuItem { Header = $"Taxi {displayName}" };
        AttachPreviewHover(defaultSub, vm, route);

        foreach (var (label, command, preview) in variants)
        {
            var cmd = command;
            var child = CreateMenuItem(label, () => vm.SendRawCommandAsync(callsign, initials, cmd));
            AttachPreviewHover(child, vm, preview);
            defaultSub.Items.Add(child);
        }

        parent.Items.Add(defaultSub);
    }

    private static void AttachPreviewHover(MenuItem item, GroundViewModel vm, TaxiRoute route)
    {
        item.PointerEntered += (_, _) => vm.PreviewRoute = route;
    }

    internal static void AddHoldShortCrossingItems(
        ContextMenu menu,
        GroundViewModel vm,
        AircraftModel? ac,
        string phase,
        string callsign,
        string initials
    )
    {
        var rwyId = HoldShortMenuHelper.HeldRunway(phase, ac);

        if (ac is { HasActiveTaxiRoute: true })
        {
            menu.Items.Add(CreateMenuItem("Resume taxi", () => vm.ResumeAsync(callsign, initials)));
        }

        if (rwyId is not null)
        {
            var rwyIdDisplay = RunwayIdentifier.ToDisplayDesignator(rwyId);
            menu.Items.Add(CreateMenuItem($"Cross {rwyIdDisplay}", () => vm.CrossRunwayAsync(callsign, initials, rwyId)));
            menu.Items.Add(CreateMenuItem($"Line up and wait {rwyIdDisplay}", () => vm.LineUpAndWaitAsync(callsign, initials, rwyId)));
            AddCtoSubmenu(menu, vm, ac, callsign, initials, rwyId);
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
        bool isVfr = AircraftCommandApplicability.ShowVfrTakeoffModifiers(ac);

        var parent = new MenuItem { Header = $"Cleared for takeoff {RunwayIdentifier.ToDisplayDesignator(rwyId)}" };
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
                var parkingSuffix = $"@{node.Name}";
                return ($"{taxiPrefix} {parkingSuffix}", taxiPrefix.Length);

            case "Spot" when node.Name is not null:
                // "TAXI  $SPOT" — cursor between TAXI and $SPOT
                var spotSuffixToken = $"${node.Name}";
                return ($"{taxiPrefix} {spotSuffixToken}", taxiPrefix.Length);

            case "RunwayHoldShort" when node.RunwayId is not null:
                // "RWY 30 TAXI " — cursor at end for user to add taxiway route
                var rwyEnd1 = RunwayIdentifier.ToDisplayDesignator(RunwayIdentifier.Parse(node.RunwayId).End1);
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
    }
}
