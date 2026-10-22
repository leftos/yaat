namespace Yaat.Sim.Data.Airport;

/// <summary>Which runway crossings on a taxi route the scenario pre-clears, so the aircraft crosses without a <c>CROSS</c>.</summary>
public enum AutoCrossRunwayMode
{
    /// <summary>No crossing is pre-cleared: every runway crossing needs a <c>CROSS</c>.</summary>
    Off,

    /// <summary>Every runway crossing is pre-cleared.</summary>
    All,

    /// <summary>
    /// Only crossings of runways not in the airport's active-runway list, for any use, are pre-cleared; with no active
    /// runways named, every runway counts as inactive.
    /// </summary>
    InactiveOnly,
}
