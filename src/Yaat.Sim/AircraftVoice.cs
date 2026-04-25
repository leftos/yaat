using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// CRC voice configuration for a track. Voice type maps to CRC's voice display
/// (Unknown / Full / ReceiveOnly / TextOnly); TdlsDumped marks aircraft whose
/// TDLS clearance has been dumped from the strip.
/// </summary>
public class AircraftVoice
{
    /// <summary>0=Unknown, 1=Full, 2=ReceiveOnly, 3=TextOnly. Defaults to Full.</summary>
    public int Type { get; set; } = 1;

    public bool TdlsDumped { get; set; }

    public AircraftVoiceDto ToSnapshot() => new() { Type = Type, TdlsDumped = TdlsDumped };

    public static AircraftVoice FromSnapshot(AircraftVoiceDto dto) => new() { Type = dto.Type, TdlsDumped = dto.TdlsDumped };
}
