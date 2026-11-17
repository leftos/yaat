using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>The view's map-tools wiring: Control for the MVA readout, and the view model's LOCK on the canvas's pan and zoom.</summary>
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

    partial void AttachMapTools(ScopeViewModel vm)
    {
        vm.PropertyChanged += OnMapToolsPropertyChanged;
        SyncPanZoomLock(vm);
    }

    partial void DetachMapTools(ScopeViewModel vm)
    {
        vm.PropertyChanged -= OnMapToolsPropertyChanged;
        Canvas.IsPanZoomEnabled = true;
    }

    private void OnMapToolsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((sender is ScopeViewModel vm) && (e.PropertyName == nameof(ScopeViewModel.IsPanZoomLocked)))
        {
            SyncPanZoomLock(vm);
        }
    }

    private void SyncPanZoomLock(ScopeViewModel vm) => Canvas.IsPanZoomEnabled = !vm.IsPanZoomLocked;
}
