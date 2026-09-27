using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// The hold a controller has recorded for the flight: CRC's flight-plan editor (<c>SetHoldAnnotations</c>) or an ERAM
/// <c>HM</c> / <c>QH</c> entry. Display data only; it changes nothing the aircraft flies. <see cref="Direction"/> and
/// <see cref="Turns"/> carry CRC's <c>CompassDirection</c> / <c>TurnDirection</c> ordinals and are null when the
/// controller gave no holding instructions (a fix-only hold). <see cref="Radial"/> is the inbound radial as entered
/// when the instructions named one; <see cref="Direction"/> then holds its nearest compass point. <see cref="Efc"/> is the
/// expect-further-clearance time as an <c>HHMM</c> integer, null when none was given (0 is 0000).
/// </summary>
public class AircraftHoldAnnotation
{
    public string? Fix { get; set; }
    public int? Direction { get; set; }
    public int? Turns { get; set; }
    public int? LegLength { get; set; }
    public bool LegLengthInNm { get; set; }
    public int? Efc { get; set; }
    public int? Radial { get; set; }

    public AircraftHoldAnnotationDto ToSnapshot() =>
        new()
        {
            Fix = Fix,
            Direction = Direction,
            Turns = Turns,
            LegLength = LegLength,
            LegLengthInNm = LegLengthInNm,
            Efc = Efc,
            Radial = Radial,
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
            Radial = dto.Radial,
        };
}
