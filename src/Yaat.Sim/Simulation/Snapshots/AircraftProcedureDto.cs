using System.Text.Json.Serialization;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftProcedureDto
{
    public string? ActiveSidId { get; init; }
    public string? ActiveStarId { get; init; }
    public string? DepartureRunway { get; init; }
    public string? DestinationRunway { get; init; }
    public required bool SidViaMode { get; init; }
    public required bool StarViaMode { get; init; }
    public int? SidViaCeiling { get; init; }
    public int? StarViaFloor { get; init; }
    public required bool SpeedRestrictionsDeleted { get; init; }
    public required bool IsExpediting { get; init; }
    public double? LastProcedureSpeedKts { get; init; }
    public int? SidInitialAltitudeFt { get; init; }

    // The last crossed published speed. Additive, no schema bump: omitted while null, and absent in older snapshots
    // restores no memory, so a resume holds no ceiling until the next published speed is crossed.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LastCrossedPublishedSpeedKts { get; init; }

    // The climb via target. Additive, no schema bump: omitted while null, so snapshots without a CVIA in force stay
    // byte-identical, and absent in older snapshots restores no climb via target.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ClimbViaTargetFt { get; init; }

    // The transmissions whose DVIA and CVIA applied the via clearances in force. Additive, no schema bump: omitted while
    // null, and absent in older snapshots restores no transmission, so a later CFIX captures its reverts.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StarViaTransmissionId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ClimbViaTransmissionId { get; init; }

    // Whether the climb via in force is the TDLS default. Additive, no schema bump: written only when true, so older
    // snapshots (and their hashes) are unchanged and restore false, a controller's via whose top is kept.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ClimbViaTopFromTdls { get; init; }

    // What the sent PDC clears the aircraft to fly after takeoff. Additive, no schema bump: written only when a PDC with
    // an altitude was sent, so older snapshots (and their hashes) are unchanged and restore none, the facility default.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TdlsDepartureAltitudeKind SentPdcAltitudeKind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? SentPdcAltitudeFt { get; init; }
}
