using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The view's map-tools wiring: Control for the MVA readout, the view model's LOCK on the canvas's pan and zoom, its range
/// ring and PLACE RR's click, and its weather lines, set above the ERAM entry boxes while they show.
/// </summary>
public partial class ScopeView
{
    // The window whose deactivation lets go of Control, while the canvas is in its visual tree.
    private WindowBase? _mapToolsWindow;

    partial void WireMapTools()
    {
        // Seen even when the entry takes the key, so a Control press or release always reaches the readout.
        Canvas.AddHandler(KeyDownEvent, (_, e) => Canvas.TrackMvaKey(e, isDown: true), RoutingStrategies.Bubble, handledEventsToo: true);
        Canvas.AddHandler(KeyUpEvent, (_, e) => Canvas.TrackMvaKey(e, isDown: false), RoutingStrategies.Bubble, handledEventsToo: true);
        Canvas.LostFocus += (_, _) => Canvas.ReleaseMvaKey();
        Canvas.AttachedToVisualTree += OnMapToolsCanvasAttached;
        Canvas.DetachedFromVisualTree += OnMapToolsCanvasDetached;
        Canvas.RangeRingPlaced += OnRangeRingPlaced;
        EntryLayer.EramBoxes.PropertyChanged += OnEramBoxesPropertyChanged;
    }

    // A Control release while another window has the keyboard never reaches the canvas, so a deactivation lets go of it.
    private void OnMapToolsCanvasAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _mapToolsWindow = TopLevel.GetTopLevel(Canvas) as WindowBase;
        _mapToolsWindow?.Deactivated += OnMapToolsWindowDeactivated;
    }

    private void OnMapToolsCanvasDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _mapToolsWindow?.Deactivated -= OnMapToolsWindowDeactivated;
        _mapToolsWindow = null;
    }

    private void OnMapToolsWindowDeactivated(object? sender, EventArgs e) => Canvas.ReleaseMvaKey();

    private void OnRangeRingPlaced(object? sender, LatLon at) => (DataContext as ScopeViewModel)?.PlaceRangeRing(at);

    // The ERAM entry boxes grow with their feedback lines, and the weather sits above them.
    private void OnEramBoxesPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if ((e.Property == BoundsProperty) && (DataContext is ScopeViewModel vm))
        {
            SyncWeather(vm);
        }
    }

    partial void AttachMapTools(ScopeViewModel vm)
    {
        vm.PropertyChanged += OnMapToolsPropertyChanged;
        SyncPanZoomLock(vm);
        SyncRangeRing(vm);
        Canvas.IsPlacingRangeRing = vm.IsPlacingRangeRing;
        SyncWeather(vm);
    }

    partial void DetachMapTools(ScopeViewModel vm)
    {
        vm.PropertyChanged -= OnMapToolsPropertyChanged;
        Canvas.IsPanZoomEnabled = true;
        Canvas.RangeRing = null;
        Canvas.IsPlacingRangeRing = false;
        Canvas.Weather = null;
    }

    private void OnMapToolsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ScopeViewModel vm)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ScopeViewModel.IsPanZoomLocked):
                SyncPanZoomLock(vm);
                break;
            case nameof(ScopeViewModel.ShowRangeRing) or nameof(ScopeViewModel.RangeRingSizeNm) or nameof(ScopeViewModel.RangeRingCenterShown):
                SyncRangeRing(vm);
                break;
            case nameof(ScopeViewModel.IsPlacingRangeRing):
                Canvas.IsPlacingRangeRing = vm.IsPlacingRangeRing;
                break;
            case nameof(ScopeViewModel.ShowWeather) or nameof(ScopeViewModel.WeatherLines) or nameof(ScopeViewModel.ProfileKind):
                SyncWeather(vm);
                break;
        }
    }

    private void SyncPanZoomLock(ScopeViewModel vm) => Canvas.IsPanZoomEnabled = !vm.IsPanZoomLocked;

    private void SyncRangeRing(ScopeViewModel vm) =>
        Canvas.RangeRing = (vm.ShowRangeRing && (vm.RangeRingCenterShown is { } center)) ? new ScopeRangeRing(center, vm.RangeRingSizeNm) : null;

    private void SyncWeather(ScopeViewModel vm) =>
        Canvas.Weather = (vm.ShowWeather && (vm.WeatherLines is { Count: > 0 } lines)) ? new ScopeWeatherText(lines, WeatherBottomInset(vm)) : null;

    /// <summary>
    /// How far above the canvas's bottom edge the weather ends: the margin on a STARS profile, else the margin above the
    /// Response Area and the MCA.
    /// </summary>
    private float WeatherBottomInset(ScopeViewModel vm)
    {
        if (!vm.IsEramEntryLayout)
        {
            return ScopeMapToolsRenderer.WeatherMargin;
        }

        StackPanel boxes = EntryLayer.EramBoxes;
        return (float)(boxes.Bounds.Height + boxes.Margin.Bottom) + ScopeMapToolsRenderer.WeatherMargin;
    }
}
