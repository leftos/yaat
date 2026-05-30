using System.Collections.Generic;
using Yaat.Client.Models;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Pushes the live "Auto Cleared-to-Land" session setting onto every aircraft model so the radar /
/// tower-cab "NoLndgClnc" datablock suppression tracks the toggle. Used by
/// <see cref="MainViewModel"/> from the in-session flyout toggle, the cross-RPO session-settings
/// broadcast, and scenario-load wiring. The Aircraft List Info-column status is computed server-side
/// (<see cref="Yaat.Sim.AircraftStatusDescriber"/>, which already honours the session auto-clear
/// setting), so this only updates the client-only datablock flag.
/// </summary>
internal static class AutoClearedToLandSync
{
    internal static void ApplyToAircraft(IEnumerable<AircraftModel> aircraft, bool value)
    {
        foreach (var ac in aircraft)
        {
            ac.IsAutoClearedToLand = value;
        }
    }
}
