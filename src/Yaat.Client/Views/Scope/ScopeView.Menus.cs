using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope view's menu gestures, as the canvas raises them: the action modifier's flight-plan editor, the empty map's
/// deselect and the map's right click.
/// </summary>
public partial class ScopeView
{
    /// <summary>Wires the canvas's menu gestures to the view, once, at construction.</summary>
    partial void WireMenus()
    {
        Canvas.AircraftCtrlClicked += OnAircraftCtrlClicked;
        Canvas.EmptySpaceClicked += OnEmptySpaceClicked;
        // A right click on the empty map opens the point menu at the point it landed on.
        Canvas.MapRightClicked += OnMapRightClicked;
    }

    // Ctrl(Cmd)+click on a target, its block or an indicator over it: the aircraft's flight plan opens instead of the
    // click selecting, running a field's action, toggling an indicator or taking the dwell lock.
    private void OnAircraftCtrlClicked(string callsign)
    {
        if ((DataContext is ScopeViewModel vm) && (vm.FindAircraft(callsign) is { } ac))
        {
            FlightPlanEditorManager.Open(ac, vm.Main);
        }
    }

    // A left press on the empty map clears the selection; the armed Rings tool places nothing there and keeps it.
    private void OnEmptySpaceClicked()
    {
        if ((DataContext is ScopeViewModel vm) && (!vm.IsRingToolArmed))
        {
            vm.SelectedAircraft = null;
        }
    }
}
