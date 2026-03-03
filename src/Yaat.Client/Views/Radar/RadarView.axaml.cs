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
        _canvas.PointerPressed += OnCanvasPointerPressed;
        _canvas.RangeRingPlaced += OnRangeRingPlaced;

        // Sync brightness from ViewModel
        if (DataContext is RadarViewModel vm)
        {
            _canvas.SetBrightnessLookup(vm.BrightnessLookup);
            _canvas.BrightnessA = vm.MapBrightnessA;
            _canvas.BrightnessB = vm.MapBrightnessB;
            _canvas.RangeRingBrightness = vm.RangeRingBrightness;
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
            var rangeBtn = this.FindControl<Button>("RangeButton");
            rangeBtn?.Classes.Set("active", false);
            _canvas?.Focus();
        }

        var rrBtn = this.FindControl<Button>("RrButton");
        rrBtn?.Classes.Set("active", vm.IsAdjustingRangeRingSize);
    }

    private void OnFixClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.ToggleFixesCommand.Execute(null);
        var btn = this.FindControl<Button>("FixButton");
        btn?.Classes.Set("active", vm.ShowFixes);
    }

    private void OnTopDownClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.ToggleTopDownCommand.Execute(null);
        var btn = this.FindControl<Button>("TopDownButton");
        btn?.Classes.Set("active", vm.ShowTopDown);
    }

    private void OnLockClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.TogglePanZoomLockCommand.Execute(null);
        var btn = this.FindControl<Button>("LockButton");
        btn?.Classes.Set("active", vm.IsPanZoomLocked);
    }

    private void OnBriteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.OpenBriteMenuCommand.Execute(null);
            UpdateBriteDisplay(vm);
        }
    }

    private void OnBriteMpaUp(object? sender, RoutedEventArgs e) => AdjustBrite(BriteTarget.MapA, 5);

    private void OnBriteMpaDown(object? sender, RoutedEventArgs e) => AdjustBrite(BriteTarget.MapA, -5);

    private void OnBriteMpbUp(object? sender, RoutedEventArgs e) => AdjustBrite(BriteTarget.MapB, 5);

    private void OnBriteMpbDown(object? sender, RoutedEventArgs e) => AdjustBrite(BriteTarget.MapB, -5);

    private void OnBriteRrUp(object? sender, RoutedEventArgs e) => AdjustBrite(BriteTarget.RangeRing, 5);

    private void OnBriteRrDown(object? sender, RoutedEventArgs e) => AdjustBrite(BriteTarget.RangeRing, -5);

    private void AdjustBrite(BriteTarget target, int delta)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.AdjustBrightness(target, delta);

        if (_canvas is not null)
        {
            _canvas.BrightnessA = vm.MapBrightnessA;
            _canvas.BrightnessB = vm.MapBrightnessB;
            _canvas.RangeRingBrightness = vm.RangeRingBrightness;
        }

        UpdateBriteDisplay(vm);
    }

    private void UpdateBriteDisplay(RadarViewModel vm)
    {
        var mpaText = this.FindControl<TextBlock>("BriteMpaValue");
        var mpbText = this.FindControl<TextBlock>("BriteMpbValue");
        var rrText = this.FindControl<TextBlock>("BriteRrValue");
        if (mpaText is not null)
        {
            mpaText.Text = ((int)(vm.MapBrightnessA * 100)).ToString(CultureInfo.InvariantCulture);
        }
        if (mpbText is not null)
        {
            mpbText.Text = ((int)(vm.MapBrightnessB * 100)).ToString(CultureInfo.InvariantCulture);
        }
        if (rrText is not null)
        {
            rrText.Text = ((int)(vm.RangeRingBrightness * 100)).ToString(CultureInfo.InvariantCulture);
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
    }

    private void OnRangeRingPlaced(double lat, double lon)
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.PlaceRangeRing(lat, lon);
        }
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
