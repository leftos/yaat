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
}
