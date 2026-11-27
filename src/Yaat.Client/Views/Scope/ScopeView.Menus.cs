using Avalonia;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope view's menu gestures, as the canvas raises them: the action modifier's flight-plan editor, the empty map's
/// deselect, the map's right click and a RUNDOWN entry's right click.
/// </summary>
public partial class ScopeView
{
    /// <summary>
    /// Sends a RUNDOWN entry menu's command for a callsign in place of the server send, when set. Test seam.
    /// </summary>
    internal Func<string, string, Task>? RundownCommandSender { get; set; }

    /// <summary>Wires the canvas's menu gestures to the view, once, at construction.</summary>
    partial void WireMenus()
    {
        Canvas.AircraftCtrlClicked += OnAircraftCtrlClicked;
        Canvas.EmptySpaceClicked += OnEmptySpaceClicked;
        // A right click on the empty map opens the point menu at the point it landed on.
        Canvas.MapRightClicked += OnMapRightClicked;
        // A right click on a RUNDOWN row opens that entry's coordination menu.
        Canvas.RundownRowRightClicked += OnRundownRowRightClicked;
    }

    // The entry the row names, found in the room's lists; one that is gone by now opens nothing.
    private void OnRundownRowRightClicked(RundownRow row, Point at)
    {
        if (DataContext is not ScopeViewModel vm)
        {
            return;
        }

        CoordinationListHubDto? list = vm.Main.CoordinationLists.FirstOrDefault(l => l.ListId == row.ListId);
        CoordinationItemHubDto? item = list?.Items.FirstOrDefault(i => i.Id == row.ItemId);
        if ((list is null) || (item is null))
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("RUNDOWN row {ListId}/{ItemId} is no longer in the room's lists", row.ListId, row.ItemId);
            }

            return;
        }

        ShowContextMenu(
            RundownEntryMenu.Build(
                RundownEntryMenu.Items(list, item),
                command => _ = SendRundownCommand(vm, item.Callsign, command),
                (placeholder, initialText) => RundownTextPrompt.AskAsync(Canvas, at, placeholder, initialText)
            )
        );
    }

    // A RUNDOWN entry menu's command, sent for the entry's aircraft.
    private Task SendRundownCommand(ScopeViewModel vm, string callsign, string command)
    {
        if (RundownCommandSender is { } sender)
        {
            return sender(callsign, command);
        }

        var host = new ClientMenuHost(vm.Main, vm.FindAircraft(callsign), Canvas);
        return host.SendAsync(callsign, command, host.Session.Initials);
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
