using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// ARINC 424 path terminator preserved at runtime. Fix-bearing legs (IF/TF/DF/CF) carry
/// <see cref="FixName"/> + <see cref="FixPosition"/>; pure heading/course legs (VA/VI/VM/CA)
/// carry only <see cref="CourseMagnetic"/> (plus <see cref="TargetAltitudeFt"/> for the
/// altitude-terminated ones). Course tracking onto a fix and the next-leg intercept
/// relationship are both expressed here so a phase can fly the coded path rather than
/// collapsing every leg to a fix position.
/// </summary>
public enum ProcedureLegType
{
    /// <summary>IF — initial fix; fly direct to the fix.</summary>
    InitialFix,

    /// <summary>TF — track to fix between the previous and this fix.</summary>
    TrackToFix,

    /// <summary>DF — direct to fix from the current position.</summary>
    DirectToFix,

    /// <summary>CF — course to fix: intercept and track the published course onto the fix.</summary>
    CourseToFix,

    /// <summary>VA — fly the heading until reaching the target altitude.</summary>
    HeadingToAltitude,

    /// <summary>CA — fly the ground track (course) until reaching the target altitude.</summary>
    CourseToAltitude,

    /// <summary>VI — fly the heading until intercepting the next leg's course.</summary>
    HeadingToIntercept,

    /// <summary>CI — fly the ground track until intercepting the next leg's course.</summary>
    CourseToIntercept,

    /// <summary>VM / FM — fly the heading/course until a manual termination (ATC vectors).</summary>
    HeadingToManual,

    /// <summary>Pre-expanded RF/AF arc waypoint; fly direct to the position.</summary>
    Arc,
}

/// <summary>
/// A single resolved procedure leg flown by <c>DepartureProcedurePhase</c> /
/// <c>ArrivalProcedurePhase</c>. Built from <see cref="CifpLeg"/> by <c>ProcedureLegResolver</c>.
/// </summary>
public sealed class ProcedureLeg
{
    public required ProcedureLegType Type { get; init; }

    /// <summary>Terminating fix name (IF/TF/DF/CF/Arc). Null for VA/VI/VM/CA.</summary>
    public string? FixName { get; init; }

    /// <summary>Terminating fix position. Null for VA/VI/VM/CA.</summary>
    public LatLon? FixPosition { get; init; }

    /// <summary>Published course/heading (magnetic degrees), from <see cref="CifpLeg.OutboundCourse"/>.</summary>
    public double? CourseMagnetic { get; init; }

    /// <summary>Climb/descent target (ft MSL) for VA/CA legs, derived from the altitude restriction.</summary>
    public double? TargetAltitudeFt { get; init; }

    /// <summary>VI/CI: the leg terminates when the next leg's course is intercepted.</summary>
    public bool TerminatesOnNextLegIntercept { get; init; }

    /// <summary>Coded turn direction (ARINC position 43). Null means shortest turn.</summary>
    public TurnDirection? Turn { get; init; }

    /// <summary>Crossing altitude restriction at the terminating fix (e.g. CF ≥16000).</summary>
    public CifpAltitudeRestriction? AltitudeRestriction { get; init; }

    /// <summary>Crossing speed restriction at the terminating fix.</summary>
    public CifpSpeedRestriction? SpeedRestriction { get; init; }

    /// <summary>Overfly the fix before turning to the next leg.</summary>
    public bool IsFlyOver { get; init; }

    public ProcedureLegDto ToSnapshot() =>
        new()
        {
            Type = (int)Type,
            FixName = FixName,
            FixPosition = FixPosition,
            CourseMagnetic = CourseMagnetic,
            TargetAltitudeFt = TargetAltitudeFt,
            TerminatesOnNextLegIntercept = TerminatesOnNextLegIntercept,
            Turn = Turn switch
            {
                TurnDirection.Left => "L",
                TurnDirection.Right => "R",
                _ => null,
            },
            AltitudeRestriction = AltitudeRestriction is not null
                ? new AltitudeRestrictionDto
                {
                    Type = (int)AltitudeRestriction.Type,
                    Altitude1 = AltitudeRestriction.Altitude1Ft,
                    Altitude2 = AltitudeRestriction.Altitude2Ft,
                }
                : null,
            SpeedRestriction = SpeedRestriction is not null
                ? new SpeedRestrictionDto { Type = SpeedRestrictionDto.ToTypeCode(SpeedRestriction.Type), Speed = SpeedRestriction.SpeedKts }
                : null,
            IsFlyOver = IsFlyOver,
        };

    public static ProcedureLeg FromSnapshot(ProcedureLegDto dto) =>
        new()
        {
            Type = (ProcedureLegType)dto.Type,
            FixName = dto.FixName,
            FixPosition = dto.FixPosition,
            CourseMagnetic = dto.CourseMagnetic,
            TargetAltitudeFt = dto.TargetAltitudeFt,
            TerminatesOnNextLegIntercept = dto.TerminatesOnNextLegIntercept,
            Turn = dto.Turn switch
            {
                "L" => TurnDirection.Left,
                "R" => TurnDirection.Right,
                _ => null,
            },
            AltitudeRestriction = dto.AltitudeRestriction is not null
                ? new CifpAltitudeRestriction(
                    (CifpAltitudeRestrictionType)dto.AltitudeRestriction.Type,
                    dto.AltitudeRestriction.Altitude1,
                    dto.AltitudeRestriction.Altitude2
                )
                : null,
            SpeedRestriction = dto.SpeedRestriction is not null
                ? new CifpSpeedRestriction(dto.SpeedRestriction.Speed, SpeedRestrictionDto.FromTypeCode(dto.SpeedRestriction.Type))
                : null,
            IsFlyOver = dto.IsFlyOver,
        };
}
