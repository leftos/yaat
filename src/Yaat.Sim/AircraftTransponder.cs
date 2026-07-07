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

    /// <summary>
    /// Latched true when the pilot has been told to squawk VFR (<c>SQVFR</c>/<c>SQV</c>). While set, the
    /// YAAT Radar View suppresses the assigned-vs-reported beacon-code mismatch flash — the stale assigned
    /// discrete code is noise the RPO should ignore. Released only when a new beacon code is assigned (see
    /// <see cref="AssignCode"/>). This is an RPO-display latch only; it does not affect pilot/transponder behavior.
    /// </summary>
    public bool CommandedSquawkVfr { get; set; }

    /// <summary>
    /// Assigns an ATC beacon code, releasing the squawk-VFR flash-suppress latch. A fresh assignment is a
    /// new "assigned but not squawked yet" alert for the RPO, so the mismatch flash resumes.
    /// </summary>
    public void AssignCode(uint code)
    {
        AssignedCode = code;
        CommandedSquawkVfr = false;
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
        };
}
