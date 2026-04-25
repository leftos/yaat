using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Transponder state — mode (A/C/S/etc.), assigned vs reported beacon code,
/// and IDENT timer (set by pilot's ident command, auto-clears after a few seconds).
/// </summary>
public class AircraftTransponder
{
    public string Mode { get; set; } = "C";
    public uint AssignedCode { get; set; }
    public uint Code { get; set; }
    public bool IsIdenting { get; set; }
    public double? IdentStartedAt { get; set; }

    public AircraftTransponderDto ToSnapshot() =>
        new()
        {
            Mode = Mode,
            AssignedCode = AssignedCode,
            Code = Code,
            IsIdenting = IsIdenting,
            IdentStartedAt = IdentStartedAt,
        };

    public static AircraftTransponder FromSnapshot(AircraftTransponderDto dto) =>
        new()
        {
            Mode = dto.Mode,
            AssignedCode = dto.AssignedCode,
            Code = dto.Code,
            IsIdenting = dto.IsIdenting,
            IdentStartedAt = dto.IdentStartedAt,
        };
}
