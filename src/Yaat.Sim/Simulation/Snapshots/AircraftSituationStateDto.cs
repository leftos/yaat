using System.Text.Json.Serialization;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>Snapshot of <see cref="Yaat.Sim.Situation.AircraftSituationState"/>; every field defaults cleanly.</summary>
public sealed class AircraftSituationStateDto
{
    /// <summary>The stored <see cref="Yaat.Sim.Situation.AircraftSituation"/> as its fixed number.</summary>
    public int Current { get; init; }

    /// <summary>Sim time (seconds) of the most recent liftoff; null when none has been seen.</summary>
    public double? AirborneAtSeconds { get; init; }

    /// <summary>Whether the last <c>Situation</c> step saw the aircraft on the ground; false when absent.</summary>
    public bool WasOnGround { get; init; }

    /// <summary>The stored <see cref="Yaat.Sim.Situation.SituationFlags"/> as their fixed bits; 0 (none) when absent.</summary>
    public int Flags { get; init; }

    /// <summary>The runway to cross next, as the end to name in <c>CROSS</c>; null when none or absent.</summary>
    public string? NextCrossingRunway { get; init; }

    /// <summary>
    /// The named exits ahead the last <c>Situation</c> step found; written only when there is a list, so a snapshot without one
    /// keeps its old bytes, and null (no list) when absent.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExitAheadDto>? ExitsAhead { get; init; }
}
