using System.Text;

namespace Yaat.Sim.Simulation.Coast;

/// <summary>
/// The ERAM radar's 12 s sweep grid: each callsign sweeps at its own phase within the period, so the position part of
/// its ERAM target and track moves once per sweep. Pure functions of the callsign and sim time, so every run kind and
/// every server agree on when an aircraft was swept.
/// </summary>
public static class EramSweepGrid
{
    /// <summary>The ERAM radar sweep period in seconds.</summary>
    public const int SweepSeconds = 12;

    /// <summary>
    /// The callsign's sweep phase in seconds (0-11): FNV-1a 32-bit over the callsign's UTF-8 bytes, mod
    /// <see cref="SweepSeconds"/>. Stable across processes, unlike <see cref="string.GetHashCode()"/>, so replays and
    /// every server agree.
    /// </summary>
    public static int OffsetSeconds(string callsign)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(callsign))
        {
            hash = unchecked((hash ^ b) * 16777619);
        }
        return (int)(hash % SweepSeconds);
    }

    /// <summary>The number of the sweep <paramref name="simElapsedSeconds"/> falls in for a callsign whose sweep phase is
    /// <paramref name="offsetSeconds"/> (<see cref="OffsetSeconds"/>).</summary>
    public static long Index(int offsetSeconds, double simElapsedSeconds) => (long)Math.Floor((simElapsedSeconds - offsetSeconds) / SweepSeconds);

    /// <summary>The sim time of <paramref name="callsign"/>'s last grid sweep at or before <paramref name="simElapsedSeconds"/>.</summary>
    public static double LastSweepSimSeconds(string callsign, double simElapsedSeconds)
    {
        int offset = OffsetSeconds(callsign);
        return offset + ((double)Index(offset, simElapsedSeconds) * SweepSeconds);
    }
}
