namespace Yaat.Sim.Training;

/// <summary>
/// Why an aircraft's session ended from the student controller's perspective.
/// Drives the M12.4 per-aircraft debrief and is preserved by
/// <see cref="SimulationWorld"/> after the aircraft is removed from the world.
/// </summary>
public enum CompletionReason
{
    /// <summary>Session still in progress — no completion stamp yet.</summary>
    Active,

    /// <summary>Aircraft touched down on a runway. Set by <see cref="Phases.Tower.LandingPhase"/>.</summary>
    Landed,

    /// <summary>Controller issued <c>CT</c> or <c>FCA</c>. Set by <see cref="Commands.ContactCommandHandler"/>.</summary>
    HandedOff,

    /// <summary>Aircraft was explicitly deleted (DEL command, scenario unload) without a prior completion stamp.</summary>
    Dropped,
}

/// <summary>
/// Per-aircraft debrief metadata captured at the moment of removal from
/// <see cref="SimulationWorld"/> so the M12.4 Aircraft tab can still show
/// completed aircraft after their <see cref="AircraftState"/> is gone.
/// Stored on <see cref="SimulationWorld"/>; cleared on scenario reset.
/// </summary>
public sealed record CompletedAircraftRecord(
    string Callsign,
    string AircraftType,
    string Cid,
    string? FiledDeparture,
    string? FiledDestination,
    double SpawnedAtSeconds,
    double CompletedAtSeconds,
    CompletionReason Reason,
    string? Detail
);
