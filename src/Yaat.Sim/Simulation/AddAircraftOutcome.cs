using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

/// <summary>
/// What an <c>ADD</c> produced: the aircraft now in the world and its snapshot as of the spawn — the value a live run
/// bakes onto the <see cref="RecordedCommand"/> so a later run can hold its own derivation against it — or the
/// refusal. <see cref="Aircraft"/> and <see cref="Spawned"/> are set together, and <see cref="Error"/> only when
/// they are null.
/// </summary>
public sealed record AddAircraftOutcome(AircraftState? Aircraft, AircraftSnapshotDto? Spawned, string? Error)
{
    public static AddAircraftOutcome Refused(string? error) => new(null, null, error ?? "ADD failed");
}
