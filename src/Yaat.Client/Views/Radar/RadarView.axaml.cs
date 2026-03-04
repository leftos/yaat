using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Views.Radar;

public partial class RadarView : UserControl
{
    private RadarCanvas? _canvas;
    private ContextMenu? _activeContextMenu;
    private Func<string, Task>? _pendingInputAction;
    private Func<object, Task>? _pendingListAction;
    private bool _listPopupInitializing;
    private Func<string, Task>? _pendingFilteredListAction;
    private string[]? _filteredListAllNames;

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
        _canvas.EmptySpaceClicked += OnEmptySpaceClicked;
        _canvas.RoutePointPlaced += OnRoutePointPlaced;
        _canvas.RoutePointUndo += OnRoutePointUndo;
        _canvas.RouteConfirmed += OnRouteConfirmed;
        _canvas.RouteCancelled += OnRouteCancelled;
        _canvas.RoutePointConditionRequested += OnRoutePointConditionRequested;
        _canvas.RouteWaypointRightClicked += OnRouteWaypointRightClicked;
        _canvas.PointerPressed += OnCanvasPointerPressed;
        _canvas.RangeRingPlaced += OnRangeRingPlaced;

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
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        if (_canvas is not null)
        {
            _canvas.AircraftRightClicked -= OnAircraftRightClicked;
            _canvas.MapRightClicked -= OnMapRightClicked;
            _canvas.AircraftLeftClicked -= OnAircraftLeftClicked;
            _canvas.EmptySpaceClicked -= OnEmptySpaceClicked;
            _canvas.RoutePointPlaced -= OnRoutePointPlaced;
            _canvas.RoutePointUndo -= OnRoutePointUndo;
            _canvas.RouteConfirmed -= OnRouteConfirmed;
            _canvas.RouteCancelled -= OnRouteCancelled;
            _canvas.RoutePointConditionRequested -= OnRoutePointConditionRequested;
            _canvas.RouteWaypointRightClicked -= OnRouteWaypointRightClicked;
            _canvas.PointerPressed -= OnCanvasPointerPressed;
            _canvas.RangeRingPlaced -= OnRangeRingPlaced;
        }
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
            this.FindControl<Button>("PtlLnthButton")?.Classes.Set("active", false);
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
            var rangeBtn = this.FindControl<Button>("RangeButton");
            rangeBtn?.Classes.Set("active", false);
            this.FindControl<Button>("PtlLnthButton")?.Classes.Set("active", false);
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
            this.FindControl<Button>("RangeButton")?.Classes.Set("active", false);
            this.FindControl<Button>("RrButton")?.Classes.Set("active", false);
        }

        this.FindControl<Button>("PtlLnthButton")?.Classes.Set("active", vm.IsAdjustingPtlLength);
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
        }
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
        ShowInputPopup(
            $"Command at {wp.ResolvedName}",
            input =>
            {
                vm.SetWaypointCondition(waypointIndex, input);
                return Task.CompletedTask;
            }
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

        ShowContextMenu(menu, screenPos);
    }

    // --- Canvas interaction ---

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_canvas!).Properties;
        if (props.IsLeftButtonPressed)
        {
            CloseActiveContextMenu();
            CloseInputPopup();
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
