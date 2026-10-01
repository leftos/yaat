using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The one source of the runway the aircraft right-click menus name for a departure: the held runway at a
/// hold-short, else the assigned one. Every view's "Cross" and "Line up and wait" items read it, and so do the
/// ground and list "Cleared for takeoff" labels.
/// </summary>
public static class HoldShortMenuHelper
{
    /// <summary>
    /// The runway <paramref name="ac"/> is holding short of, else its assigned runway
    /// (<see cref="HeldRunway(string, IMenuAircraft?)"/> over its current phase).
    /// </summary>
    public static string? HeldRunway(IMenuAircraft? ac) => HeldRunway(ac?.CurrentPhase ?? "", ac);

    /// <summary>
    /// Resolves the runway an aircraft is holding short of from its phase name.
    /// The phase is <c>"Holding Short {runway}"</c> (e.g. <c>"Holding Short 28L/10R"</c>);
    /// the first end of a compound id is returned (<c>"28L/10R"</c> → <c>"28L"</c>).
    /// Falls back to the aircraft's assigned runway when the phase carries no runway,
    /// or null when neither is available.
    /// </summary>
    public static string? HeldRunway(string phase, IMenuAircraft? ac)
    {
        const string prefix = "Holding Short ";
        if (phase.StartsWith(prefix, StringComparison.Ordinal) && phase.Length > prefix.Length)
        {
            string rwyPart = phase[prefix.Length..];
            return RunwayIdentifier.Parse(rwyPart).End1;
        }

        return !string.IsNullOrEmpty(ac?.AssignedRunway) ? ac.AssignedRunway : null;
    }
}
