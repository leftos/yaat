using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// CRC-side hold annotation drawn over the radar target. Populated by HOLD
/// commands; cleared when the aircraft is no longer holding.
/// </summary>
public class AircraftHoldAnnotation
{
    public string? Fix { get; set; }
    public int Direction { get; set; }
    public int Turns { get; set; }
    public int? LegLength { get; set; }
    public bool LegLengthInNm { get; set; }
    public int Efc { get; set; }

    public AircraftHoldAnnotationDto ToSnapshot() =>
        new()
        {
            Fix = Fix,
            Direction = Direction,
            Turns = Turns,
            LegLength = LegLength,
            LegLengthInNm = LegLengthInNm,
            Efc = Efc,
        };

    public static AircraftHoldAnnotation FromSnapshot(AircraftHoldAnnotationDto dto) =>
        new()
        {
            Fix = dto.Fix,
            Direction = dto.Direction,
            Turns = dto.Turns,
            LegLength = dto.LegLength,
            LegLengthInNm = dto.LegLengthInNm,
            Efc = dto.Efc,
        };
}
