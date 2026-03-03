using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases;

/// <summary>
/// Determines holding pattern entry type (Direct, Teardrop, Parallel) per AIM 5-3-8.
/// Uses the 70-degree sector rule relative to inbound course and holding direction.
/// </summary>
public static class HoldingEntryCalculator
{
    /// <summary>
    /// Compute the recommended holding entry type.
    /// </summary>
    /// <param name="aircraftHeading">Current aircraft heading (degrees true).</param>
    /// <param name="inboundCourse">Published inbound course to the fix (degrees true).</param>
    /// <param name="holdDirection">Holding turn direction (Right = standard, Left = nonstandard).</param>
    public static HoldingEntry ComputeEntry(double aircraftHeading, double inboundCourse, TurnDirection holdDirection)
    {
        double theta = ((aircraftHeading - inboundCourse) % 360 + 360) % 360;

        if (holdDirection == TurnDirection.Right)
        {
            return theta switch
            {
                < 110 => HoldingEntry.Direct,
                < 250 => HoldingEntry.Teardrop,
                _ => HoldingEntry.Parallel,
            };
        }

        return theta switch
        {
            < 110 => HoldingEntry.Parallel,
            < 250 => HoldingEntry.Teardrop,
            _ => HoldingEntry.Direct,
        };
    }
}
