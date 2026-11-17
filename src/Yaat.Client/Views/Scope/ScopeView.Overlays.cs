using System.ComponentModel;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>The Scope view's overlay hooks: a pointed menu row's cue and its highlight reach the canvas, and a change repaints it.</summary>
public partial class ScopeView
{
    /// <summary>How many overlay changes have asked the canvas to repaint. Test seam.</summary>
    internal int OverlayRepaints { get; private set; }

    partial void AttachOverlays(ScopeViewModel vm)
    {
        vm.PropertyChanged += OnOverlayPropertyChanged;
        vm.DataBlockState.HighlightsChanged += OnHighlightsChanged;
        Canvas.MenuCue = vm.MenuCue;
        Canvas.GroundShownAirportIdProvider = () => vm.Main.GroundShownAirportId;
    }

    partial void DetachOverlays(ScopeViewModel vm)
    {
        vm.PropertyChanged -= OnOverlayPropertyChanged;
        vm.DataBlockState.HighlightsChanged -= OnHighlightsChanged;
        Canvas.MenuCue = null;
        Canvas.GroundShownAirportIdProvider = null;
    }

    private void OnOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName != nameof(ScopeViewModel.MenuCue)) || (sender is not ScopeViewModel vm))
        {
            return;
        }

        Canvas.MenuCue = vm.MenuCue;
        RepaintOverlays();
    }

    private void OnHighlightsChanged() => RepaintOverlays();

    private void RepaintOverlays()
    {
        OverlayRepaints++;
        Canvas.MarkDirty();
    }
}
