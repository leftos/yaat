using System.Collections.Generic;
using Yaat.Client.Models;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Pushes the live "Auto Cleared-to-Land" session setting onto every aircraft
/// model so the in-list red "No landing clnc" alert (see
/// <see cref="AircraftModel.CheckAlerts"/>) tracks the toggle. Used by
/// <see cref="MainViewModel"/> from the in-session flyout toggle, the cross-RPO
/// session-settings broadcast, and scenario-load wiring.
/// </summary>
internal static class AutoClearedToLandSync
{
    internal static void ApplyToAircraft(IEnumerable<AircraftModel> aircraft, bool value)
    {
        foreach (var ac in aircraft)
        {
            ac.IsAutoClearedToLand = value;
            ac.ComputeSmartStatus();
        }
    }
}
