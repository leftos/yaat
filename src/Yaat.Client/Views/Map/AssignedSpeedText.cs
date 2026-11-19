using Yaat.Client.Models;

namespace Yaat.Client.Views.Map;

/// <summary>Formats the assigned-speed field of an aircraft's datablock tag, for every tag renderer that shows one.</summary>
internal static class AssignedSpeedText
{
    /// <summary>
    /// The assigned-speed field: <c>SMAX</c> while maximum forward speed is in force, <c>S{nnn}</c> when a speed is
    /// assigned, and <c>ASP</c> when none is.
    /// </summary>
    public static string Format(AircraftModel ac)
    {
        if (ac.SpeedMax)
        {
            return "SMAX";
        }

        if (ac.AssignedSpeed is double spd)
        {
            return $"S{(int)spd:D3}";
        }
        return "ASP";
    }
}
