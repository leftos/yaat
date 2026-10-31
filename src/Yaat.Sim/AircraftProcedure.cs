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

    /// <summary>Whether a descend via or climb via clearance is in force.</summary>
    public bool IsViaClearanceInForce => StarViaMode || SidViaMode;

    /// <summary>
    /// The ATC-assigned "except maintain" altitude of the climb via in force: <c>CVIA nnn</c>'s, or, for a bare CVIA, the
    /// TDLS climb-via text's "EXC MAINT" altitude. Set and cleared with <see cref="ClimbViaTargetFt"/>; the check-in names
    /// it. Null when no climb via is in force or its top is not ATC-assigned.
    /// </summary>
    public int? SidViaCeiling { get; set; }
    public int? StarViaFloor { get; set; }

    /// <summary>DSR flag: when true, suppresses via-mode speed constraints at waypoints. Cleared by new SPD, CVIA, DVIA, or RPS.</summary>
    public bool SpeedRestrictionsDeleted { get; set; }

    /// <summary>When true, climb/descent rate is multiplied by 1.5. Cleared on altitude reached or by NORM/CM/DM.</summary>
    public bool IsExpediting { get; set; }

    /// <summary>
    /// IAS (knots) of the most recently applied published speed restriction from a SID/STAR
    /// fix. Used to enforce AIM 5-4-1 NOTE 2: after sequencing past a procedure's terminating
    /// fix without further ATC instruction, pilots maintain the last published speed. When
    /// the route empties, this value is published as <see cref="ControlTargets.SpeedCeiling"/>
    /// so the auto speed schedule cannot accelerate the aircraft above it. Cleared by an
    /// explicit ATC speed command, DRS, RFAS, or when procedure state is reset.
    /// </summary>
    public double? LastProcedureSpeedKts { get; set; }

    /// <summary>
    /// IAS (knots) of the "at" or "at or below" speed published on the last procedure fix the aircraft crossed, recorded
    /// whatever speed it was flying. "Resume normal speed" and "resume published speed" given on a STAR hold it as
    /// <see cref="ControlTargets.SpeedCeiling"/> until the next published speed restriction (AIM 5-4-1 NOTE 2). Null
    /// before the first such crossing; it ends with the procedure: cleared when procedure state is reset, by a vector
    /// off the procedure, by joining a STAR, when a SID becomes active, and by an approach clearance.
    /// </summary>
    public double? LastCrossedPublishedSpeedKts { get; set; }

    /// <summary>
    /// Published initial ("maintain") altitude in feet for this aircraft's departure SID transition,
    /// from the airport's TDLS config (e.g. KIAH 4000, KHOU 5000). When set, an IFR departure with no
    /// explicit clearance altitude climbs to and maintains it until ATC issues a climb (7110.65 4-3-2).
    /// Null when off a SID, VFR, or the airport publishes no initial altitude.
    /// </summary>
    public int? SidInitialAltitudeFt { get; set; }

    /// <summary>
    /// The altitude a climb via clearance (<c>CVIA</c>) climbs to, resolved once when it is issued: <c>CVIA nnn</c>'s
    /// altitude; else the TDLS climb-via text's "EXC MAINT" altitude for the SID and filed transition; else that
    /// transition's TDLS initial altitude; else the highest altitude the SID publishes on its flown legs (a stand-in for
    /// the charted top, which CIFP does not code); else present altitude. A TDLS value is never raised by a published
    /// altitude; the TDLS initial-altitude menu and <see cref="SidInitialAltitudeFt"/> never take part. Never below present
    /// altitude rounded up to 100 ft. In flight a ceiling still ahead lowers it; a floor never raises it. While set it
    /// outranks every other departure climb target (the CTO altitude, the procedure phase's ceiling, filed cruise). Cleared
    /// with <see cref="SidViaCeiling"/> by any later altitude clearance, a DVIA or a procedure reset. Null when no CVIA is
    /// in force.
    /// </summary>
    public int? ClimbViaTargetFt { get; set; }

    /// <summary>
    /// The transmission (<see cref="Commands.DispatchContext.TransmissionId"/>) whose DVIA applied the descend via in
    /// force, so a CFIX later in that same transmission knows the crossing is restated with the via clearance and takes
    /// no reverts. Null when no descend via is in force or the one in force came from a transmission without a crossing
    /// restriction (id 0); cleared with <see cref="StarViaMode"/>.
    /// </summary>
    public int? StarViaTransmissionId { get; set; }

    /// <summary>
    /// The climb via counterpart of <see cref="StarViaTransmissionId"/>: the transmission whose CVIA applied the climb via
    /// in force. Cleared with <see cref="ClimbViaTargetFt"/>.
    /// </summary>
    public int? ClimbViaTransmissionId { get; set; }

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
            LastProcedureSpeedKts = LastProcedureSpeedKts,
            LastCrossedPublishedSpeedKts = LastCrossedPublishedSpeedKts,
            SidInitialAltitudeFt = SidInitialAltitudeFt,
            ClimbViaTargetFt = ClimbViaTargetFt,
            StarViaTransmissionId = StarViaTransmissionId,
            ClimbViaTransmissionId = ClimbViaTransmissionId,
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
            LastProcedureSpeedKts = dto.LastProcedureSpeedKts,
            LastCrossedPublishedSpeedKts = dto.LastCrossedPublishedSpeedKts,
            SidInitialAltitudeFt = dto.SidInitialAltitudeFt,
            ClimbViaTargetFt = dto.ClimbViaTargetFt,
            StarViaTransmissionId = dto.StarViaTransmissionId,
            ClimbViaTransmissionId = dto.ClimbViaTransmissionId,
        };
}
