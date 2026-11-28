using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring.Spawns;

/// <summary>The kind of start an aircraft's spawn belongs to, as a spawn filter names it.</summary>
public enum SpawnKind
{
    /// <summary>An on-runway or parking start.</summary>
    Departure,

    /// <summary>An expected approach set, or an on-final start.</summary>
    Arrival,

    /// <summary>Neither a departure nor an arrival start.</summary>
    Overflight,
}

/// <summary>What the planner does about a spawn later than the scenario's length.</summary>
public enum SpawnOverrunRemedy
{
    /// <summary>Nothing: the scenario keeps its length and the overrun stays reported.</summary>
    None,

    /// <summary>Lengthen the scenario to hold the last spawn.</summary>
    ExtendScenario,

    /// <summary>Scale every slot so the last one lands on the scenario's length.</summary>
    CompressIntervals,
}

/// <summary>One time band of a band-based schedule: slots every <paramref name="IntervalSec"/> from the start, through
/// the end inclusive, each jittered by one draw in [-(<paramref name="MinusSec"/>), <paramref name="PlusSec"/>].</summary>
public sealed record SpawnBand(int StartSec, int EndSec, int IntervalSec, int PlusSec, int MinusSec);

/// <summary>Which aircraft a schedule takes. A null or empty member restricts nothing; text matches case-insensitively.</summary>
public sealed record SpawnFilter(
    string? FlightRules = null,
    SpawnKind? Kind = null,
    string? Departure = null,
    string? Destination = null,
    string? Approach = null,
    string? ExcludeDeparture = null,
    string? ExcludeDestination = null,
    IReadOnlySet<string>? ExcludeCallsigns = null
);

/// <summary>The least time between two spawns of the same origin; <see cref="SpawnSchedule.Assign"/> takes a gap of zero
/// or less as off.</summary>
public sealed record SpawnDeconfliction(int MinGapSec);

/// <summary>The interval limits of a workload-driven schedule: a busier sample spawns every
/// <paramref name="MinIntervalSec"/> and an idle one every <paramref name="MaxIntervalSec"/>, each slot jittered by
/// <paramref name="JitterSec"/> either way.</summary>
public sealed record WorkloadSettings(int MinIntervalSec, int MaxIntervalSec, int JitterSec);

/// <summary>One aircraft's place in a schedule: the delay to write, and the origin it shares with other aircraft.</summary>
public sealed record SpawnAssignment(ScenarioAircraft Aircraft, int SpawnDelaySec, string OriginKey);

/// <summary>Every spawn time of a schedule, recomputed the other way an overrun can be answered.</summary>
/// <param name="CompressedMinGapSec">The smallest gap between two spawns of one origin where at least one of the pair is
/// a compressed scheduled spawn; the requested gap when there is no such pair, and zero with no deconfliction.</param>
/// <param name="CompressBreaksGap">True when that gap is below the gap the caller asked for.</param>
public sealed record SpawnOverrun(
    int LengthSec,
    int LastSpawnSec,
    int OverrunSec,
    int ExtendedLengthSec,
    double CompressFactor,
    IReadOnlyList<int> CompressedSpawnSec,
    int CompressedMinGapSec,
    bool CompressBreaksGap
);

/// <summary>What <see cref="SpawnSchedule.Assign"/> produced: the assigned aircraft, the ones left out, the length the
/// schedule was planned over, and an overrun when a spawn falls later than it.</summary>
/// <param name="GapWarning">True when a compress remedy shrank the same-origin gap below the one the caller asked for;
/// the overrun that carried the figure is cleared once the remedy is applied.</param>
public sealed record SpawnScheduleResult(
    IReadOnlyList<SpawnAssignment> Assignments,
    IReadOnlyList<ScenarioAircraft> Unassigned,
    int UnusedSlots,
    int LengthSec,
    SpawnOverrun? Overrun,
    bool GapWarning
);
