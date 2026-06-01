using Yaat.Client.Models;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views;

/// <summary>
/// Shared logic for the aircraft right-click menus that act on a holding-short
/// aircraft. Keeps the ground-map menu and the track-list menu deriving their
/// "Cross"/"Line up and wait" entries from the same source of truth.
/// </summary>
public static class HoldShortMenuHelper
{
    /// <summary>
    /// Resolves the runway an aircraft is holding short of from its phase name.
    /// The phase is <c>"Holding Short {runway}"</c> (e.g. <c>"Holding Short 28L/10R"</c>);
    /// the first end of a compound id is returned (<c>"28L/10R"</c> → <c>"28L"</c>).
    /// Falls back to the aircraft's assigned runway when the phase carries no runway,
    /// or null when neither is available.
    /// </summary>
    public static string? HeldRunway(string phase, AircraftModel? ac)
    {
        const string prefix = "Holding Short ";
        if (phase.StartsWith(prefix, StringComparison.Ordinal) && phase.Length > prefix.Length)
        {
            var rwyPart = phase[prefix.Length..];
            return RunwayIdentifier.Parse(rwyPart).End1;
        }

        return !string.IsNullOrEmpty(ac?.AssignedRunway) ? ac.AssignedRunway : null;
    }
}
