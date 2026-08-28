using Avalonia.Controls;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// The context-menu items every surface (aircraft list, radar, ground) offers for a live-traffic shadow. A shadow
/// is read-only until assumed, so these are the only maneuver-like items it gets; the callers skip their
/// phase-aware command groups for it. Surface shadows are never assumable (<see cref="AircraftCommandApplicability.CanAssume"/>),
/// so for them nothing is added and the menu carries only track / display / delete items.
/// </summary>
public static class LiveTrafficMenuItems
{
    /// <summary>Appends "Assume control" and "Assume and track" when the shadow is assumable; returns whether anything was added.</summary>
    public static bool Add(ContextMenu menu, AircraftModel ac, Func<string, Task> sendCommand)
    {
        if (!AircraftCommandApplicability.CanAssume(ac))
        {
            return false;
        }

        var assume = new MenuItem { Header = "Assume control" };
        assume.Click += async (_, _) => await sendCommand("ASSUME");
        menu.Items.Add(assume);

        // Two commands on purpose: the server does not couple them, and TRACK is the same track command
        // the Track submenu sends, so a refused ASSUME leaves the track state untouched.
        var assumeAndTrack = new MenuItem { Header = "Assume and track" };
        assumeAndTrack.Click += async (_, _) =>
        {
            await sendCommand("ASSUME");
            await sendCommand("TRACK");
        };
        menu.Items.Add(assumeAndTrack);
        return true;
    }
}
