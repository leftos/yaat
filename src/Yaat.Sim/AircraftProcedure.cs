using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// SID/STAR procedure state: which procedure is active, its runway, via-mode flags
/// (DSR-style "via" climb/descent restrictions), plus the per-flight DSR speed/expedite flags.
/// </summary>
public class AircraftProcedure
{
    public string? ActiveSidId { get; set; }
    public string? ActiveStarId { get; set; }

    /// <summary>Runway designator snapshot from when the SID was activated.</summary>
    public string? DepartureRunway { get; set; }

    /// <summary>Runway designator snapshot for arrival (set by approach or pattern assignment).</summary>
    public string? DestinationRunway { get; set; }

    public bool SidViaMode { get; set; }
    public bool StarViaMode { get; set; }
    public int? SidViaCeiling { get; set; }
    public int? StarViaFloor { get; set; }

    /// <summary>DSR flag: when true, suppresses via-mode speed constraints at waypoints. Cleared by new SPD, CVIA, or DVIA.</summary>
    public bool SpeedRestrictionsDeleted { get; set; }

    /// <summary>When true, climb/descent rate is multiplied by 1.5. Cleared on altitude reached or by NORM/CM/DM.</summary>
    public bool IsExpediting { get; set; }

    public AircraftProcedureDto ToSnapshot() =>
        new()
        {
            ActiveSidId = ActiveSidId,
            ActiveStarId = ActiveStarId,
            DepartureRunway = DepartureRunway,
            DestinationRunway = DestinationRunway,
            SidViaMode = SidViaMode,
            StarViaMode = StarViaMode,
            SidViaCeiling = SidViaCeiling,
            StarViaFloor = StarViaFloor,
            SpeedRestrictionsDeleted = SpeedRestrictionsDeleted,
            IsExpediting = IsExpediting,
        };

    public static AircraftProcedure FromSnapshot(AircraftProcedureDto dto) =>
        new()
        {
            ActiveSidId = dto.ActiveSidId,
            ActiveStarId = dto.ActiveStarId,
            DepartureRunway = dto.DepartureRunway,
            DestinationRunway = dto.DestinationRunway,
            SidViaMode = dto.SidViaMode,
            StarViaMode = dto.StarViaMode,
            SidViaCeiling = dto.SidViaCeiling,
            StarViaFloor = dto.StarViaFloor,
            SpeedRestrictionsDeleted = dto.SpeedRestrictionsDeleted,
            IsExpediting = dto.IsExpediting,
        };
}
