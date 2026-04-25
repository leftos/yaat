namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftClearanceDto
{
    public string? Expect { get; init; }
    public string? Sid { get; init; }
    public string? Transition { get; init; }
    public string? Climbout { get; init; }
    public string? Climbvia { get; init; }
    public string? InitialAlt { get; init; }
    public string? ContactInfo { get; init; }
    public string? LocalInfo { get; init; }
    public string? DepFreq { get; init; }
}
