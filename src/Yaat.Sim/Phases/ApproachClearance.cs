using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation.Snapshots;

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

    /// <summary>
    /// Optional lateral anchor (latitude) for parallel-offset approaches whose published
    /// MAP is laterally offset from the runway threshold (e.g. KDCA LDA-X 19). When set,
    /// FinalApproachPhase uses this point as the cross-track reference instead of the
    /// runway threshold. Null for ordinary approaches that terminate at the threshold.
    /// </summary>
    public double? FinalApproachAnchorLat { get; init; }

    /// <summary>Lateral anchor longitude; see <see cref="FinalApproachAnchorLat"/>.</summary>
    public double? FinalApproachAnchorLon { get; init; }

    /// <summary>True when the aircraft is on a straight-in approach (no hold-in-lieu).</summary>
    public bool StraightIn { get; init; }

    /// <summary>True when the controller forced the clearance (skip intercept validation).</summary>
    public bool Force { get; init; }

    /// <summary>
    /// True when the clearance authorizes only a LATERAL intercept of the final approach
    /// course / localizer (JFAC/JLOC) — the aircraft turns to intercept and tracks the course
    /// but HOLDS its assigned altitude and is NOT cleared to descend on the glideslope. An
    /// approach clearance (CAPP) sets this false, which authorizes the glideslope descent.
    /// Mutable so CAPP can upgrade an established lateral intercept in place. See
    /// <see cref="Approach.FinalApproachPhase"/>. Defaults false (fully cleared approach), so
    /// pre-feature snapshots round-trip to legacy descend-on-intercept behavior.
    /// 7110.65 §5-9-3 NOTE / §5-9-4.c.2; AIM §5-4-7.a.6.
    /// </summary>
    public bool LateralInterceptOnly { get; set; }

    /// <summary>Resolved CIFP procedure data, if available.</summary>
    public CifpApproachProcedure? Procedure { get; init; }

    /// <summary>Pre-built missed approach fix sequence from CIFP data. Empty if no MAP data.</summary>
    public IReadOnlyList<ApproachFix> MissedApproachFixes { get; init; } = [];

    /// <summary>Hold parameters for the final MAP fix (from HA/HF/HM leg), or null if no hold.</summary>
    public MissedApproachHold? MapHold { get; init; }

    /// <summary>
    /// Altitude (MSL) at the missed approach point, extracted from the MAP leg's
    /// altitude restriction. Represents DA for precision approaches, MDA for non-precision.
    /// Null for visual approaches and non-CIFP cases; FinalApproachPhase falls back
    /// to threshold elevation + 200ft when null.
    /// </summary>
    public int? MapAltitudeFt { get; init; }

    /// <summary>
    /// Distance (nm) from the MAP fix to the runway threshold. Derived from the MAP
    /// fix position in CIFP data. Null when CIFP data is unavailable or the MAP fix
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

    public ApproachClearanceDto ToSnapshot() =>
        new()
        {
            ApproachId = ApproachId,
            AirportCode = AirportCode,
            RunwayId = RunwayId,
            FinalApproachCourseDeg = FinalApproachCourse.Degrees,
            FinalApproachAnchorLat = FinalApproachAnchorLat,
            FinalApproachAnchorLon = FinalApproachAnchorLon,
            StraightIn = StraightIn,
            Force = Force,
            LateralInterceptOnly = LateralInterceptOnly,
            MapAltitudeFt = MapAltitudeFt,
            MapDistanceNm = MapDistanceNm,
            InterceptCaptureDistanceNm = InterceptCaptureDistanceNm,
            InterceptCaptureAngleDeg = InterceptCaptureAngleDeg,
            MapHold = MapHold?.ToSnapshot(),
            MissedApproachFixes = MissedApproachFixes.Count > 0 ? MissedApproachFixes.Select(f => f.ToSnapshot()).ToList() : null,
        };

    public static ApproachClearance FromSnapshot(ApproachClearanceDto dto) =>
        new()
        {
            ApproachId = dto.ApproachId,
            AirportCode = dto.AirportCode,
            RunwayId = dto.RunwayId,
            FinalApproachCourse = new TrueHeading(dto.FinalApproachCourseDeg),
            FinalApproachAnchorLat = dto.FinalApproachAnchorLat,
            FinalApproachAnchorLon = dto.FinalApproachAnchorLon,
            StraightIn = dto.StraightIn,
            Force = dto.Force,
            LateralInterceptOnly = dto.LateralInterceptOnly,
            MapAltitudeFt = dto.MapAltitudeFt,
            MapDistanceNm = dto.MapDistanceNm,
            InterceptCaptureDistanceNm = dto.InterceptCaptureDistanceNm,
            InterceptCaptureAngleDeg = dto.InterceptCaptureAngleDeg,
            MapHold = dto.MapHold is not null ? MissedApproachHold.FromSnapshot(dto.MapHold) : null,
            MissedApproachFixes = dto.MissedApproachFixes?.Select(ApproachFix.FromSnapshot).ToList() ?? [],
        };
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

    public PendingApproachDto ToSnapshot() => new() { Clearance = Clearance.ToSnapshot(), AssignedRunway = AssignedRunway.ToSnapshot() };

    public static PendingApproachInfo FromSnapshot(PendingApproachDto dto) =>
        new() { Clearance = ApproachClearance.FromSnapshot(dto.Clearance), AssignedRunway = RunwayInfo.FromSnapshot(dto.AssignedRunway) };
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
)
{
    public MissedApproachHoldDto ToSnapshot() =>
        new()
        {
            FixName = FixName,
            FixLat = FixLat,
            FixLon = FixLon,
            InboundCourse = InboundCourse,
            LegLength = LegLength,
            IsMinuteBased = IsMinuteBased,
            Direction = (int)Direction,
        };

    public static MissedApproachHold FromSnapshot(MissedApproachHoldDto dto) =>
        new(dto.FixName, dto.FixLat, dto.FixLon, dto.InboundCourse, dto.LegLength, dto.IsMinuteBased, (TurnDirection)dto.Direction);
}
