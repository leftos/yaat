using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Phases;

/// <summary>
/// Active approach clearance stored on PhaseList. Set when the controller
/// clears the aircraft for an approach (JFAC, CAPP, JAPP, PTAC).
/// </summary>
public sealed class ApproachClearance
{
    public required string ApproachId { get; init; }
    public required string AirportCode { get; init; }
    public required string RunwayId { get; init; }
    public required TrueHeading FinalApproachCourse { get; init; }

    /// <summary>True when the aircraft is on a straight-in approach (no hold-in-lieu).</summary>
    public bool StraightIn { get; init; }

    /// <summary>True when the controller forced the clearance (skip intercept validation).</summary>
    public bool Force { get; init; }

    /// <summary>Resolved CIFP procedure data, if available.</summary>
    public CifpApproachProcedure? Procedure { get; init; }

    /// <summary>Pre-built missed approach fix sequence from CIFP data. Empty if no MAP data.</summary>
    public IReadOnlyList<ApproachFix> MissedApproachFixes { get; init; } = [];

    /// <summary>Hold parameters for the final MAP fix (from HA/HF/HM leg), or null if no hold.</summary>
    public MissedApproachHold? MapHold { get; init; }

    /// <summary>
    /// Altitude (MSL) at the missed approach point, extracted from the MAHP leg's
    /// altitude restriction. Represents DA for precision approaches, MDA for non-precision.
    /// Null for visual approaches and non-CIFP cases; FinalApproachPhase falls back
    /// to threshold elevation + 200ft when null.
    /// </summary>
    public int? MapAltitudeFt { get; init; }

    /// <summary>
    /// Distance (nm) from the MAHP fix to the runway threshold. Derived from the MAHP
    /// fix position in CIFP data. Null when CIFP data is unavailable or the MAHP fix
    /// can't be resolved; FinalApproachPhase falls back to 0.5nm.
    /// </summary>
    public double? MapDistanceNm { get; init; }

    /// <summary>
    /// Distance from threshold (nm) at which InterceptCoursePhase captured the localizer.
    /// Set by InterceptCoursePhase.Capture(); used by FinalApproachPhase to record the
    /// true intercept distance instead of the stricter establishment distance.
    /// </summary>
    public double? InterceptCaptureDistanceNm { get; set; }

    /// <summary>
    /// Intercept angle (degrees) at the moment InterceptCoursePhase captured the localizer.
    /// At establishment time the aircraft is already aligned (< 15°), making the heading diff
    /// meaningless for scoring. This records the true intercept angle.
    /// </summary>
    public double? InterceptCaptureAngleDeg { get; set; }
}

/// <summary>
/// Approach clearance issued while the aircraft is still on a STAR en route to the
/// approach connecting fix. Activated when the aircraft reaches the connecting fix via
/// normal navigation. Analogous to DepartureClearanceInfo for pre-issued departure clearances.
/// </summary>
public sealed class PendingApproachInfo
{
    public required ApproachClearance Clearance { get; init; }
    public required RunwayInfo AssignedRunway { get; init; }
}

/// <summary>Holding pattern parameters extracted from a missed approach HA/HF/HM leg.</summary>
public sealed record MissedApproachHold(
    string FixName,
    double FixLat,
    double FixLon,
    int InboundCourse,
    double LegLength,
    bool IsMinuteBased,
    TurnDirection Direction
);
