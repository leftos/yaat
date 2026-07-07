namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftTransponderDto
{
    public required string Mode { get; init; }
    public required uint AssignedCode { get; init; }
    public required uint Code { get; init; }
    public required bool IsIdenting { get; init; }
    public double? IdentStartedAt { get; init; }
    public bool CommandedSquawkVfr { get; init; }
}
