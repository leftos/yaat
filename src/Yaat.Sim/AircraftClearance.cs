using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Departure clearance fields originating from CRC. All strings; null when the
/// clearance hasn't been issued yet for that field.
/// </summary>
public class AircraftClearance
{
    public string? Expect { get; set; }
    public string? Sid { get; set; }
    public string? Transition { get; set; }
    public string? Climbout { get; set; }
    public string? Climbvia { get; set; }
    public string? InitialAlt { get; set; }
    public string? ContactInfo { get; set; }
    public string? LocalInfo { get; set; }
    public string? DepFreq { get; set; }

    public AircraftClearanceDto ToSnapshot() =>
        new()
        {
            Expect = Expect,
            Sid = Sid,
            Transition = Transition,
            Climbout = Climbout,
            Climbvia = Climbvia,
            InitialAlt = InitialAlt,
            ContactInfo = ContactInfo,
            LocalInfo = LocalInfo,
            DepFreq = DepFreq,
        };

    public static AircraftClearance FromSnapshot(AircraftClearanceDto dto) =>
        new()
        {
            Expect = dto.Expect,
            Sid = dto.Sid,
            Transition = dto.Transition,
            Climbout = dto.Climbout,
            Climbvia = dto.Climbvia,
            InitialAlt = dto.InitialAlt,
            ContactInfo = dto.ContactInfo,
            LocalInfo = dto.LocalInfo,
            DepFreq = dto.DepFreq,
        };
}
