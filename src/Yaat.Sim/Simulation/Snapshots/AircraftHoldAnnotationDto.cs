namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftHoldAnnotationDto
{
    public string? Fix { get; init; }
    public int? Direction { get; init; }
    public int? Turns { get; init; }
    public int? LegLength { get; init; }
    public required bool LegLengthInNm { get; init; }
    public int? Efc { get; set; }
    public int? Radial { get; init; }
}
