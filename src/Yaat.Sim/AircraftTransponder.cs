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

    /// <summary>
    /// The squawked beacon code. A change of code clears <see cref="SpcStartedAt"/>, so the next <see cref="Tick"/>
    /// restamps it when the new code is also special (7700 to 7600 restarts the SPC blink).
    /// </summary>
    public uint Code
    {
        get;
        set
        {
            if (value != field)
            {
                SpcStartedAt = null;
            }
            field = value;
        }
    }

    public bool IsIdenting { get; set; }
    public double? IdentStartedAt { get; set; }

    /// <summary>
    /// Scenario time the current special-purpose code (<see cref="IsSpecialPurposeCode"/>) was first observed by
    /// <see cref="Tick"/>, or null while a normal code is squawked. ERAM blinks the Field-E SPC label for a fixed interval
    /// from this time.
    /// </summary>
    public double? SpcStartedAt { get; set; }

    /// <summary>
    /// A special-purpose beacon code (ADIZ 1276, UAS lost link 7400, hijack 7500, radio failure 7600, emergency 7700,
    /// AFIO 7777) whose ERAM Field-E label blinks on the data block (vNAS <c>EramSpecialPurposeCode</c>).
    /// </summary>
    public static bool IsSpecialPurposeCode(uint code) => code is 1276 or 7400 or 7500 or 7600 or 7700 or 7777;

    /// <summary>
    /// Latched true the first tick the transponder is observed in an altitude-reporting mode; never
    /// clears. CRC's ERAM data blocks render the recently-lost-Mode-C <c>X</c>/<c>XXX</c> forms only when
    /// the target reports no altitude but this flag is set (<c>EramTargetDto.WasModeCPreviouslyReceived</c>).
    /// </summary>
    public bool HasReportedModeC { get; set; }

    /// <summary>
    /// Latched true when the pilot has been told to squawk VFR (<c>SQVFR</c>/<c>SQV</c>, or <c>SQ 1200</c>). While set, the
    /// YAAT Radar View suppresses the assigned-vs-reported beacon-code mismatch flash — the stale assigned
    /// discrete code is noise the RPO should ignore. Released when a new beacon code is assigned (see
    /// <see cref="AssignCode"/>), when another code is squawked (<c>SQ &lt;code&gt;</c>), or when the pilot is put back on
    /// the assigned code (<c>SQ</c> with no code). This is an RPO-display latch only; it does not affect
    /// pilot/transponder behavior.
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
    /// elapsed. Stamps <see cref="SpcStartedAt"/> the first tick a special code is observed and clears it on a normal
    /// code. <paramref name="nowSeconds"/> is the scenario's current <c>ElapsedSeconds</c>.
    /// </summary>
    public void Tick(double nowSeconds)
    {
        if (Mode.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            HasReportedModeC = true;
        }

        if (!IsSpecialPurposeCode(Code))
        {
            SpcStartedAt = null;
        }
        else if (!SpcStartedAt.HasValue)
        {
            SpcStartedAt = nowSeconds;
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
            SpcStartedAt = SpcStartedAt,
            CommandedSquawkVfr = CommandedSquawkVfr,
            HasReportedModeC = HasReportedModeC,
            AssignedByFacilityId = AssignedByFacilityId,
            AssignedBySectorId = AssignedBySectorId,
        };

    // Code is assigned before SpcStartedAt: the Code setter clears the stamp.
    public static AircraftTransponder FromSnapshot(AircraftTransponderDto dto) =>
        new()
        {
            Mode = dto.Mode,
            AssignedCode = dto.AssignedCode,
            Code = dto.Code,
            IsIdenting = dto.IsIdenting,
            IdentStartedAt = dto.IdentStartedAt,
            SpcStartedAt = dto.SpcStartedAt,
            CommandedSquawkVfr = dto.CommandedSquawkVfr,
            HasReportedModeC = dto.HasReportedModeC,
            AssignedByFacilityId = dto.AssignedByFacilityId,
            AssignedBySectorId = dto.AssignedBySectorId,
        };
}
