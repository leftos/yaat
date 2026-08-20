using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Transponder state — mode (A/C/S/etc.), assigned vs reported beacon code,
/// and IDENT timer (set by pilot's ident command, auto-clears after a few seconds).
/// </summary>
public class AircraftTransponder
{
    /// <summary>
    /// IDENT auto-clears this many seconds after it begins. The pilot's ident command sets
    /// <see cref="IsIdenting"/>; the first tick that observes it stamps <see cref="IdentStartedAt"/>,
    /// and the flash clears once this duration has elapsed.
    /// </summary>
    public const double IdentDurationSeconds = 18;

    public string Mode { get; set; } = "C";
    public uint AssignedCode { get; set; }
    public uint Code { get; set; }
    public bool IsIdenting { get; set; }
    public double? IdentStartedAt { get; set; }

    /// <summary>
    /// Latched true the first tick the transponder is observed in an altitude-reporting mode; never
    /// clears. CRC's ERAM data blocks render the recently-lost-Mode-C <c>X</c>/<c>XXX</c> forms only when
    /// the target reports no altitude but this flag is set (<c>EramTargetDto.WasModeCPreviouslyReceived</c>).
    /// </summary>
    public bool HasReportedModeC { get; set; }

    /// <summary>
    /// Latched true when the pilot has been told to squawk VFR (<c>SQVFR</c>/<c>SQV</c>). While set, the
    /// YAAT Radar View suppresses the assigned-vs-reported beacon-code mismatch flash — the stale assigned
    /// discrete code is noise the RPO should ignore. Released only when a new beacon code is assigned (see
    /// <see cref="AssignCode"/>). This is an RPO-display latch only; it does not affect pilot/transponder behavior.
    /// </summary>
    public bool CommandedSquawkVfr { get; set; }

    /// <summary>
    /// The ERAM sector (facility + sector id) that assigned <see cref="AssignedCode"/>, or null when the
    /// code came from a non-ERAM source (STARS, filing auto-assign, scenario spawn). CRC's ERAM CODE view
    /// auto-lists flight plans whose assigner record-equals the viewing sector.
    /// </summary>
    public string? AssignedByFacilityId { get; set; }
    public string? AssignedBySectorId { get; set; }

    /// <summary>
    /// Assigns an ATC beacon code, releasing the squawk-VFR flash-suppress latch. A fresh assignment is a
    /// new "assigned but not squawked yet" alert for the RPO, so the mismatch flash resumes. The assigner
    /// is the acting ERAM sector for ERAM-issued assignments (QB / AM BCN); every other source passes null.
    /// </summary>
    public void AssignCode(uint code, string? assignedByFacilityId, string? assignedBySectorId)
    {
        AssignedCode = code;
        AssignedByFacilityId = assignedByFacilityId;
        AssignedBySectorId = assignedBySectorId;
        CommandedSquawkVfr = false;
    }

    /// <summary>
    /// Per-tick transponder upkeep. Latches <see cref="HasReportedModeC"/> while the transponder is in an
    /// altitude-reporting mode, and advances the IDENT timer: stamps <see cref="IdentStartedAt"/> on the
    /// first tick the ident is observed, then clears the ident once <see cref="IdentDurationSeconds"/> has
    /// elapsed. <paramref name="nowSeconds"/> is the scenario's current <c>ElapsedSeconds</c>.
    /// </summary>
    public void Tick(double nowSeconds)
    {
        if (Mode.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            HasReportedModeC = true;
        }

        if (!IsIdenting)
        {
            return;
        }

        if (!IdentStartedAt.HasValue)
        {
            IdentStartedAt = nowSeconds;
        }
        else if ((nowSeconds - IdentStartedAt.Value) >= IdentDurationSeconds)
        {
            IsIdenting = false;
            IdentStartedAt = null;
        }
    }

    public AircraftTransponderDto ToSnapshot() =>
        new()
        {
            Mode = Mode,
            AssignedCode = AssignedCode,
            Code = Code,
            IsIdenting = IsIdenting,
            IdentStartedAt = IdentStartedAt,
            CommandedSquawkVfr = CommandedSquawkVfr,
            HasReportedModeC = HasReportedModeC,
            AssignedByFacilityId = AssignedByFacilityId,
            AssignedBySectorId = AssignedBySectorId,
        };

    public static AircraftTransponder FromSnapshot(AircraftTransponderDto dto) =>
        new()
        {
            Mode = dto.Mode,
            AssignedCode = dto.AssignedCode,
            Code = dto.Code,
            IsIdenting = dto.IsIdenting,
            IdentStartedAt = dto.IdentStartedAt,
            CommandedSquawkVfr = dto.CommandedSquawkVfr,
            HasReportedModeC = dto.HasReportedModeC,
            AssignedByFacilityId = dto.AssignedByFacilityId,
            AssignedBySectorId = dto.AssignedBySectorId,
        };
}
