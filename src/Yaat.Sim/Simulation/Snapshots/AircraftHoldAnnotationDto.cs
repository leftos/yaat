namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftHoldAnnotationDto
{
    public string? Fix { get; init; }
    public required int Direction { get; init; }
    public required int Turns { get; init; }
    public int? LegLength { get; init; }
    public required bool LegLengthInNm { get; init; }
    public required int Efc { get; init; }
}
