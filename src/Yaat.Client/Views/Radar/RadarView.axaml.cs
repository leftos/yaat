using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Radar.Flyouts;
using Yaat.Sim;

namespace Yaat.Client.Views.Radar;

public partial class RadarView : UserControl
{
    private RadarCanvas? _canvas;
    private ContextMenu? _activeContextMenu;
    private Popup? _activeFieldPopup;
    private Func<string, Task>? _pendingInputAction;
    private Func<object, Task>? _pendingListAction;
    private bool _listPopupInitializing;
    private Func<string, Task>? _pendingFilteredListAction;
    private string[]? _filteredListAllNames;
    private Action<string, int, int, int>? _pendingWarpAction;

    public static readonly FuncValueConverter<DcbMenuMode, bool> IsDcbModeMain = new(v => v == DcbMenuMode.Main);
    public static readonly FuncValueConverter<DcbMenuMode, bool> IsDcbModeAux = new(v => v == DcbMenuMode.Aux);
    public static readonly FuncValueConverter<DcbMenuMode, bool> IsDcbModeBrite = new(v => v == DcbMenuMode.Brite);

    public static readonly FuncValueConverter<bool, string> BoolToLockLabel = new(v => v ? "LOCK" : "UNLK");

    public RadarView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _canvas = this.FindControl<RadarCanvas>("Canvas");
        if (_canvas is null)
        {
            return;
        }

        _canvas.AircraftRightClicked += OnAircraftRightClicked;
        _canvas.MapRightClicked += OnMapRightClicked;
        _canvas.AircraftLeftClicked += OnAircraftLeftClicked;
        _canvas.AircraftCtrlClicked += OnAircraftCtrlClicked;
        _canvas.EmptySpaceClicked += OnEmptySpaceClicked;
        _canvas.RoutePointPlaced += OnRoutePointPlaced;
        _canvas.RoutePointUndo += OnRoutePointUndo;
        _canvas.RouteConfirmed += OnRouteConfirmed;
        _canvas.RouteCancelled += OnRouteCancelled;
        _canvas.RoutePointConditionRequested += OnRoutePointConditionRequested;
        _canvas.RouteWaypointRightClicked += OnRouteWaypointRightClicked;
        _canvas.PointerPressed += OnCanvasPointerPressed;
        _canvas.RangeRingPlaced += OnRangeRingPlaced;
        _canvas.EuroScopeFieldClicked += OnEuroScopeFieldClicked;
        _canvas.EuroScopeFieldRightClicked += OnEuroScopeFieldRightClicked;
        _canvas.HeadingModeConfirmed += OnHeadingModeConfirmed;

        var filteredText = this.FindControl<TextBox>("FilteredListText");
        if (filteredText is not null)
        {
            filteredText.TextChanged += OnFilteredListTextChanged;
        }

        // Sync brightness and button states from ViewModel
        if (DataContext is RadarViewModel vm)
        {
            _canvas.SetBrightnessLookup(vm.BrightnessLookup);
            SyncCanvasBrightness(vm);
        }

        SyncAssignmentTint();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        if (_canvas is not null)
        {
            _canvas.AircraftRightClicked -= OnAircraftRightClicked;
            _canvas.MapRightClicked -= OnMapRightClicked;
            _canvas.AircraftLeftClicked -= OnAircraftLeftClicked;
            _canvas.AircraftCtrlClicked -= OnAircraftCtrlClicked;
            _canvas.EmptySpaceClicked -= OnEmptySpaceClicked;
            _canvas.RoutePointPlaced -= OnRoutePointPlaced;
            _canvas.RoutePointUndo -= OnRoutePointUndo;
            _canvas.RouteConfirmed -= OnRouteConfirmed;
            _canvas.RouteCancelled -= OnRouteCancelled;
            _canvas.RoutePointConditionRequested -= OnRoutePointConditionRequested;
            _canvas.RouteWaypointRightClicked -= OnRouteWaypointRightClicked;
            _canvas.PointerPressed -= OnCanvasPointerPressed;
            _canvas.RangeRingPlaced -= OnRangeRingPlaced;
            _canvas.EuroScopeFieldClicked -= OnEuroScopeFieldClicked;
            _canvas.EuroScopeFieldRightClicked -= OnEuroScopeFieldRightClicked;
            _canvas.HeadingModeConfirmed -= OnHeadingModeConfirmed;
        }
    }

    private void OnEuroScopeFieldClicked(AircraftModel ac, TagFieldId field, Point pos)
    {
        var mainVm = FindMainViewModel();
        if (mainVm is null)
        {
            return;
        }
        var initials = mainVm.Preferences.UserInitials;

        switch (field)
        {
            case TagFieldId.Owner:
                _ = mainVm.TakeControlAsync(ac.Callsign);
                break;
            case TagFieldId.AssignedAltitude:
            case TagFieldId.CurrentAltitude:
                ShowContextMenu(AltitudeFlyout.Build(ac, mainVm.Radar, initials));
                break;
            case TagFieldId.AssignedHeading:
                _canvas?.EnterHeadingMode(ac.Callsign, pos);
                break;
            case TagFieldId.AssignedSpeed:
            case TagFieldId.CurrentSpeed:
                ShowContextMenu(SpeedFlyout.Build(ac, mainVm.Radar, initials));
                break;
            case TagFieldId.Destination:
                mainVm.Radar.EnterDrawRoute(ac.Callsign);
                break;
            case TagFieldId.Scratchpad1:
                if (_canvas is not null)
                {
                    OpenFieldPopup(ScratchpadFlyout.Build(_canvas, ac, mainVm.Radar, initials, slot: 1));
                }
                break;
            case TagFieldId.Scratchpad2:
                if (_canvas is not null)
                {
                    OpenFieldPopup(ScratchpadFlyout.Build(_canvas, ac, mainVm.Radar, initials, slot: 2));
                }
                break;
            case TagFieldId.Note:
                if (_canvas is not null)
                {
                    var radarVm = mainVm.Radar;
                    OpenFieldPopup(NoteFlyout.Build(_canvas, ac.Callsign, ac.Note, cmd => radarVm.SendRawCommandAsync(ac.Callsign, initials, cmd)));
                }
                break;
            case TagFieldId.Squawk:
                ShowContextMenu(SquawkFlyout.Build(ac, mainVm.Radar, initials));
                break;
            case TagFieldId.AssignedRunway:
                ShowContextMenu(RunwayFlyout.Build(ac, mainVm.Radar, initials));
                break;
            case TagFieldId.Handoff:
                if (_canvas is not null)
                {
                    OpenFieldPopup(HandoffFlyout.Build(_canvas, ac, mainVm.Radar, initials));
                }
                break;
        }
    }

    private bool OnEuroScopeFieldRightClicked(AircraftModel ac, TagFieldId field, Point pos)
    {
        var mainVm = FindMainViewModel();
        if (mainVm is null)
        {
            return false;
        }

        if (field == TagFieldId.Owner)
        {
            ShowContextMenu(BuildOwnerContextMenu(ac, mainVm));
            return true;
        }

        return false;
    }

    private ContextMenu BuildOwnerContextMenu(AircraftModel ac, MainViewModel mainVm)
    {
        var menu = new ContextMenu();
        FlyoutAppearance.ApplyFontSize(menu);
        menu.Items.Add(
            new MenuItem
            {
                Header = $"RPO control — {ac.Callsign}",
                IsEnabled = false,
                FontWeight = Avalonia.Media.FontWeight.Bold,
            }
        );

        // Reuse the room-aware RPO menu builder — it inserts a leading Separator and renders
        // Take control / Give up control / Give control submenu / Unassign based on current
        // assignment + room membership state.
        mainVm.BuildRpoMenuItems(menu, [ac.Callsign]);

        return menu;
    }

    private async void OnHeadingModeConfirmed(string callsign, int magneticHeading)
    {
        var mainVm = FindMainViewModel();
        if (mainVm is null)
        {
            return;
        }
        await mainVm.Radar.FlyHeadingAsync(callsign, mainVm.Preferences.UserInitials, magneticHeading);
    }

    // --- DCB button handlers ---

    private void OnMapShortcutClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MapShortcutItem shortcut } btn)
        {
            return;
        }

        if (DataContext is RadarViewModel vm)
        {
            vm.ToggleMapShortcut(shortcut);
            btn.Classes.Set("active", shortcut.IsEnabled);
        }
    }

    private void OnMapButtonClick(object? sender, RoutedEventArgs e)
    {
        var popup = this.FindControl<Popup>("MapPopup");
        if (popup is not null)
        {
            popup.IsOpen = !popup.IsOpen;
            if (popup.IsOpen)
            {
                if (DataContext is RadarViewModel vm)
                {
                    vm.MapSearchText = "";
                    vm.SortMapTogglesEnabledFirst();
                }
            }
        }
    }

    private void OnMapSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not RadarViewModel vm)
        {
            return;
        }

        var text = vm.MapSearchText.Trim();
        if (int.TryParse(text, CultureInfo.InvariantCulture, out var starsId))
        {
            vm.ToggleMapByStarsId(starsId);
        }

        vm.MapSearchText = "";
        e.Handled = true;
    }

    private void OnMapToggleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        if (sender is CheckBox { DataContext: VideoMapToggleItem toggle })
        {
            vm.SyncShortcutState(toggle.StarsId, toggle.IsEnabled);
        }
    }

    private void OnRangeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.IsAdjustingRange = !vm.IsAdjustingRange;
        if (vm.IsAdjustingRange)
        {
            vm.IsAdjustingRangeRingSize = false;
            vm.IsAdjustingPtlLength = false;
            vm.IsAdjustingHistory = false;
            this.FindControl<Button>("PtlLnthButton")?.Classes.Set("active", false);
            this.FindControl<Button>("HistoryButton")?.Classes.Set("active", false);
            var btn = this.FindControl<Button>("RangeButton");
            btn?.Classes.Set("active", true);
        }
        else
        {
            var btn = this.FindControl<Button>("RangeButton");
            btn?.Classes.Set("active", false);
        }
    }

    private void OnRrSizeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.IsAdjustingRangeRingSize = !vm.IsAdjustingRangeRingSize;
        if (vm.IsAdjustingRangeRingSize)
        {
            vm.IsAdjustingRange = false;
            vm.IsAdjustingPtlLength = false;
            vm.IsAdjustingHistory = false;
            var rangeBtn = this.FindControl<Button>("RangeButton");
            rangeBtn?.Classes.Set("active", false);
            this.FindControl<Button>("PtlLnthButton")?.Classes.Set("active", false);
            this.FindControl<Button>("HistoryButton")?.Classes.Set("active", false);
            _canvas?.Focus();
        }

        var rrBtn = this.FindControl<Button>("RrButton");
        rrBtn?.Classes.Set("active", vm.IsAdjustingRangeRingSize);
    }

    private void OnPtlLnthClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.IsAdjustingPtlLength = !vm.IsAdjustingPtlLength;
        if (vm.IsAdjustingPtlLength)
        {
            vm.IsAdjustingRange = false;
            vm.IsAdjustingRangeRingSize = false;
            vm.IsAdjustingHistory = false;
            this.FindControl<Button>("RangeButton")?.Classes.Set("active", false);
            this.FindControl<Button>("RrButton")?.Classes.Set("active", false);
            this.FindControl<Button>("HistoryButton")?.Classes.Set("active", false);
        }

        this.FindControl<Button>("PtlLnthButton")?.Classes.Set("active", vm.IsAdjustingPtlLength);
    }

    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.IsAdjustingHistory = !vm.IsAdjustingHistory;
        if (vm.IsAdjustingHistory)
        {
            vm.IsAdjustingRange = false;
            vm.IsAdjustingRangeRingSize = false;
            vm.IsAdjustingPtlLength = false;
            this.FindControl<Button>("RangeButton")?.Classes.Set("active", false);
            this.FindControl<Button>("RrButton")?.Classes.Set("active", false);
            this.FindControl<Button>("PtlLnthButton")?.Classes.Set("active", false);
        }

        this.FindControl<Button>("HistoryButton")?.Classes.Set("active", vm.IsAdjustingHistory);
    }

    private void OnPtlOwnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.TogglePtlOwnCommand.Execute(null);
        }
    }

    private void OnPtlAllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.TogglePtlAllCommand.Execute(null);
        }
    }

    private void OnFixClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.ToggleFixesCommand.Execute(null);
    }

    private void OnTopDownClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.ToggleTopDownCommand.Execute(null);
    }

    private void OnLockClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.TogglePanZoomLockCommand.Execute(null);
    }

    private static readonly (BriteTarget Target, string Label, string TextBlockName)[] BriteButtons =
    [
        (BriteTarget.Dcb, "DCB", "BriteDcbText"),
        (BriteTarget.Bkc, "BKC", "BriteBkcText"),
        (BriteTarget.MapA, "MPA", "BriteMpaText"),
        (BriteTarget.MapB, "MPB", "BriteMpbText"),
        (BriteTarget.Fdb, "FDB", "BriteFdbText"),
        (BriteTarget.Lst, "LST", "BriteLstText"),
        (BriteTarget.Pos, "POS", "BritePosText"),
        (BriteTarget.Ldb, "LDB", "BriteLdbText"),
        (BriteTarget.Oth, "OTH", "BriteOthText"),
        (BriteTarget.Tls, "TLS", "BriteTlsText"),
        (BriteTarget.RangeRing, "RR", "BriteRrText"),
        (BriteTarget.Cmp, "CMP", "BriteCmpText"),
        (BriteTarget.Bcn, "BCN", "BriteBcnText"),
        (BriteTarget.Pri, "PRI", "BritePriText"),
        (BriteTarget.Hst, "HST", "BriteHstText"),
        (BriteTarget.Wx, "WX", "BriteWxText"),
        (BriteTarget.Wxc, "WXC", "BriteWxcText"),
    ];

    private void OnBriteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.OpenBriteMenuCommand.Execute(null);
            UpdateAllBriteButtons(vm);
        }
    }

    private void OnBriteButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BriteTarget target } || DataContext is not RadarViewModel vm)
        {
            return;
        }

        // Toggle latch: clicking the active target unlatches, clicking a new one latches it
        vm.ActiveBriteTarget = vm.ActiveBriteTarget == target ? null : target;

        // Update active class on all brite buttons
        var briteMenu = this.FindControl<Grid>("DcbBriteMenu");
        if (briteMenu is null)
        {
            return;
        }

        foreach (var child in briteMenu.Children)
        {
            if (child is Button btn && btn.Tag is BriteTarget btnTarget)
            {
                btn.Classes.Set("active", btnTarget == vm.ActiveBriteTarget);
            }
        }
    }

    private void UpdateBriteButtonText(RadarViewModel vm, BriteTarget target, string label, string textBlockName)
    {
        var tb = this.FindControl<TextBlock>(textBlockName);
        if (tb is not null)
        {
            tb.Text = $"{label} {vm.GetBrightnessPercent(target)}";
        }
    }

    private void UpdateAllBriteButtons(RadarViewModel vm)
    {
        foreach (var (target, label, name) in BriteButtons)
        {
            UpdateBriteButtonText(vm, target, label, name);
        }
    }

    private void SyncCanvasBrightness(RadarViewModel vm)
    {
        if (_canvas is not null)
        {
            _canvas.BrightnessA = vm.MapBrightnessA;
            _canvas.BrightnessB = vm.MapBrightnessB;
            _canvas.RangeRingBrightness = vm.RangeRingBrightness;
            _canvas.HistoryBrightness = vm.GetBrightnessPercent(BriteTarget.Hst) / 100f;
        }
    }

    public void SyncAssignmentTint()
    {
        if (_canvas is null)
        {
            return;
        }

        var mainVm = FindMainViewModel();
        var prefs = mainVm?.Preferences;
        if (prefs is null)
        {
            return;
        }

        _canvas.LocalUserInitials = prefs.UserInitials;
        _canvas.AssignmentTintColor = prefs.AssignmentTintEnabled ? ParseHexColor(prefs.AssignmentTintColor) : null;
        _canvas.UnassignedTintColor = prefs.UnassignedTintEnabled ? ParseHexColor(prefs.UnassignedTintColor) : null;
        _canvas.SelectedOverrideColor = ParseHexColor(prefs.SelectedColor);
        _canvas.EuroScopeMode = prefs.EuroScopeMode;
        _canvas.FlashNoLandingClearance = prefs.FlashNoLandingClearance;
        _canvas.ShowSpeechBubbles = prefs.ShowSpeechBubbles;
        _canvas.ShowMvaAltitudeTint = prefs.ShowMvaAltitudeTint;
        _canvas.AlwaysShowGroundBubblesOnRadar = prefs.AlwaysShowGroundBubblesOnRadar;
        _canvas.SyncStudentColors = prefs.SyncStudentDatablockColors;
        _canvas.MarkStudentLimitedDatablocks = prefs.MarkStudentLimitedDatablocks;
        _canvas.CollapseStudentDatablocks = prefs.CollapseStudentDatablocks;
        _canvas.SyncStudentLeaderDirection = prefs.SyncStudentLeaderDirection;
        _canvas.DatablockTextSize = prefs.RadarDatablockFontSize;
        _canvas.TpaConeHalfAngleDegrees = prefs.TpaConeHalfAngleDegrees;
        Flyouts.FlyoutAppearance.FontSize = prefs.RadarFlyoutFontSize;
    }

    private static SKColor? ParseHexColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        if (SKColor.TryParse(hex, out var color))
        {
            return color;
        }

        return null;
    }

    private void OnDcbPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        var delta = e.Delta.Y > 0 ? 1 : -1;

        if (vm.IsAdjustingRange)
        {
            vm.AdjustRange(-delta);
            e.Handled = true;
        }
        else if (vm.IsAdjustingRangeRingSize)
        {
            vm.RangeRingSizeNm = RadarViewModel.CycleRangeRingSize(vm.RangeRingSizeNm, delta);
            e.Handled = true;
        }
        else if (vm.IsAdjustingPtlLength)
        {
            vm.AdjustPtlLength(delta);
            e.Handled = true;
        }
        else if (vm.IsAdjustingHistory)
        {
            vm.AdjustHistoryCount(delta);
            e.Handled = true;
        }
        else if (vm.ActiveBriteTarget is { } briteTarget)
        {
            vm.AdjustBrightness(briteTarget, delta * 5);
            SyncCanvasBrightness(vm);

            // Update just the affected button text
            foreach (var (target, label, name) in BriteButtons)
            {
                if (target == briteTarget)
                {
                    UpdateBriteButtonText(vm, target, label, name);
                    break;
                }
            }

            e.Handled = true;
        }
        else
        {
            // No spinner latched — horizontal scroll the DCB
            var scroller = this.FindControl<ScrollViewer>("DcbScroller");
            if (scroller is not null)
            {
                scroller.Offset = scroller.Offset.WithX(scroller.Offset.X - delta * 40);
                e.Handled = true;
            }
        }
    }

    private void OnRangeRingPlaced(double lat, double lon)
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.PlaceRangeRing(lat, lon);
        }
    }

    // --- Draw route events ---

    private void OnRoutePointPlaced(double lat, double lon)
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.PlaceRouteWaypoint(lat, lon);
        }
    }

    private void OnRoutePointUndo()
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.UndoRouteWaypoint();
        }
    }

    private void OnRouteConfirmed()
    {
        if (DataContext is RadarViewModel vm)
        {
            _ = vm.ConfirmDrawRouteAsync(GetInitials());
        }
    }

    private void OnRouteCancelled()
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.CancelDrawRoute();
        }
    }

    private void OnRoutePointConditionRequested(int waypointIndex, Point screenPos)
    {
        if (DataContext is not RadarViewModel vm || vm.DrawnWaypoints is null || waypointIndex >= vm.DrawnWaypoints.Count)
        {
            return;
        }

        var wp = vm.DrawnWaypoints[waypointIndex];
        var existing = vm.GetWaypointCondition(waypointIndex);
        ShowWaypointConditionPopup(
            wp.ResolvedName,
            existing?.Altitude,
            existing?.Commands,
            (altitude, commands) => vm.SetWaypointCondition(waypointIndex, altitude, commands)
        );
    }

    private void OnRouteWaypointRightClicked(int waypointIndex, Point screenPos)
    {
        if (DataContext is not RadarViewModel vm || vm.DrawnWaypoints is null || waypointIndex >= vm.DrawnWaypoints.Count)
        {
            return;
        }

        var wp = vm.DrawnWaypoints[waypointIndex];
        var initials = GetInitials();
        var menu = new ContextMenu();

        menu.Items.Add(
            new MenuItem
            {
                Header = wp.ResolvedName,
                IsEnabled = false,
                FontWeight = Avalonia.Media.FontWeight.Bold,
            }
        );
        menu.Items.Add(new Separator());

        menu.Items.Add(CreateMenuItem("Confirm route", () => vm.ConfirmDrawRouteAsync(initials)));
        menu.Items.Add(
            CreateMenuItem(
                "Delete waypoint",
                () =>
                {
                    vm.RemoveRouteWaypoint(waypointIndex);
                    return Task.CompletedTask;
                }
            )
        );

        if (waypointIndex < vm.DrawnWaypoints.Count - 1)
        {
            menu.Items.Add(
                CreateMenuItem(
                    "Delete waypoints after",
                    () =>
                    {
                        vm.RemoveRouteWaypointsAfter(waypointIndex);
                        return Task.CompletedTask;
                    }
                )
            );
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(
            CreateMenuItem(
                "Cancel route",
                () =>
                {
                    vm.CancelDrawRoute();
                    return Task.CompletedTask;
                }
            )
        );

        ShowContextMenu(menu);
    }

    // --- Canvas interaction ---

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_canvas!).Properties;
        if (props.IsLeftButtonPressed)
        {
            CloseActiveContextMenu();
            CloseInputPopup();
            CloseWaypointConditionPopup();
            CloseListPopup();
            CloseFilteredListPopup();
        }
    }

    private void CloseActiveContextMenu()
    {
        if (_activeContextMenu is not null)
        {
            _activeContextMenu.Close();
            _activeContextMenu = null;
        }
        CloseActiveFieldPopup();
    }

    private void CloseActiveFieldPopup()
    {
        if (_activeFieldPopup is not null)
        {
            _activeFieldPopup.Close();
            _activeFieldPopup = null;
        }
    }

    /// <summary>
    /// Open a EuroScope tag-field popup (text entry for scratchpad/handoff). The popup is
    /// attached to the canvas's overlay layer so it floats above other UI; we track it in
    /// _activeFieldPopup so the next outside click or canvas press closes it.
    /// </summary>
    private void OpenFieldPopup(Popup popup)
    {
        if (_canvas is null)
        {
            return;
        }
        CloseActiveContextMenu();

        var overlay = OverlayLayer.GetOverlayLayer(_canvas);
        if (overlay is null)
        {
            return;
        }
        overlay.Children.Add(popup);
        _activeFieldPopup = popup;
        popup.Closed += (s, _) =>
        {
            if (s is Popup p)
            {
                overlay.Children.Remove(p);
            }
            if (_activeFieldPopup == popup)
            {
                _activeFieldPopup = null;
            }
        };
        popup.IsOpen = true;
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

/// <summary>
/// Wraps an int value with a display label for the list popup.
/// </summary>
internal sealed record LabeledValue(string Label, int Value)
{
    public override string ToString() => Label;
}
