using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The one source of the runway the aircraft right-click menus name for a departure: the held runway at a
/// hold-short, else the assigned one. Every view's "Cross", "Line up and wait" and "Cleared for takeoff" items
/// read it.
/// </summary>
public static class HoldShortMenuHelper
{
    private const string HoldingShortPrefix = "Holding Short ";

    /// <summary>
    /// Whether <paramref name="phase"/> holds short of a named target that is not a runway — a taxiway or spot bar
    /// (<c>"Holding Short B"</c>, <c>"Holding Short spot 17"</c>). A bare <c>"Holding Short"</c> names no target and
    /// is not one.
    /// </summary>
    public static bool IsNonRunwayBar(string phase) =>
        phase.StartsWith(HoldingShortPrefix, StringComparison.Ordinal)
        && (phase.Length > HoldingShortPrefix.Length)
        && !IsRunwayTarget(phase[HoldingShortPrefix.Length..]);

    /// <summary>
    /// The runway <paramref name="ac"/> is holding short of, else its assigned runway
    /// (<see cref="HeldRunway(string, IMenuAircraft?)"/> over its current phase).
    /// </summary>
    public static string? HeldRunway(IMenuAircraft? ac) => HeldRunway(ac?.CurrentPhase ?? "", ac);

    /// <summary>
    /// Resolves the runway an aircraft is holding short of from its phase name.
    /// The phase is <c>"Holding Short {runway}"</c> (e.g. <c>"Holding Short 28L/10R"</c>);
    /// the first end of a compound id is returned (<c>"28L/10R"</c> → <c>"28L"</c>).
    /// Falls back to the aircraft's assigned runway when the phase names no runway — a bare hold-short, or a taxiway
    /// or spot bar (<c>"Holding Short B"</c>, <c>"Holding Short spot 17"</c>) — or null when neither is available.
    /// </summary>
    public static string? HeldRunway(string phase, IMenuAircraft? ac)
    {
        if (phase.StartsWith(HoldingShortPrefix, StringComparison.Ordinal) && IsRunwayTarget(phase[HoldingShortPrefix.Length..]))
        {
            return RunwayIdentifier.Parse(phase[HoldingShortPrefix.Length..]).End1;
        }

        return !string.IsNullOrEmpty(ac?.AssignedRunway) ? ac.AssignedRunway : null;
    }

    /// <summary>
    /// Whether a hold-short phase's target reads as a runway: every slash-separated end is a runway number, 1 or 2
    /// digits numbering 01-36 with at most one L, C or R. The same shape rule as the simulation's
    /// <c>HoldingShortPhase.IsRunwayTargetName</c>; a taxiway (<c>B</c>, <c>C</c>, <c>F1</c>) or a spot
    /// (<c>spot 17</c>, the phase's display form) starts with a letter and never matches.
    /// </summary>
    private static bool IsRunwayTarget(string target)
    {
        string[] ends = target.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (ends.Length > 0) && ends.All(IsRunwayEnd);
    }

    private static bool IsRunwayEnd(string end)
    {
        int digits = 0;
        while ((digits < end.Length) && char.IsAsciiDigit(end[digits]))
        {
            digits++;
        }

        if ((digits == 0) || (digits > 2) || (end.Length > digits + 1) || (int.Parse(end[..digits]) is < 1 or > 36))
        {
            return false;
        }

        return (end.Length == digits) || (char.ToUpperInvariant(end[digits]) is 'L' or 'C' or 'R');
    }
}
