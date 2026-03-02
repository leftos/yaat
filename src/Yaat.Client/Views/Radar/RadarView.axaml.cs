using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    public static readonly FuncValueConverter<bool, IBrush> BoolToMapColor = new(v => v ? Brushes.Lime : new SolidColorBrush(Color.Parse("#888")));

    public static readonly FuncValueConverter<bool, string> BoolToLockLabel = new(v => v ? "LOCK" : "UNLK");

    public static readonly FuncValueConverter<bool, IBrush> BoolToLockColor = new(v => v ? new SolidColorBrush(Color.Parse("#888")) : Brushes.Yellow);

    public static readonly FuncValueConverter<bool, IBrush> BoolToLatchColor = new(v => v ? Brushes.Cyan : new SolidColorBrush(Color.Parse("#CCC")));

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
        if (sender is not Button { Tag: MapShortcutItem shortcut })
        {
            return;
        }

        if (DataContext is RadarViewModel vm)
        {
            vm.ToggleMapShortcut(shortcut);
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

    private void OnMapIdSubmit(object? sender, RoutedEventArgs e)
    {
        var input = this.FindControl<TextBox>("MapIdInput");
        var popup = this.FindControl<Popup>("MapPopup");

        if (input?.Text is not null && int.TryParse(input.Text, CultureInfo.InvariantCulture, out var starsId) && DataContext is RadarViewModel vm)
        {
            vm.ToggleMapByStarsId(starsId);
            input.Text = "";
        }

        if (popup is not null)
        {
            popup.IsOpen = false;
        }
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

    private void OnRrSizeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.IsAdjustingRangeRingSize = !vm.IsAdjustingRangeRingSize;
        if (vm.IsAdjustingRangeRingSize)
        {
            _canvas?.Focus();
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
