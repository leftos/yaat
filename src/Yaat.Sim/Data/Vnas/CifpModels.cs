namespace Yaat.Sim.Data.Vnas;

public enum CifpAltitudeRestrictionType
{
    At,
    AtOrAbove,
    AtOrBelow,
    Between,
    GlideSlopeIntercept,
}

public sealed record CifpAltitudeRestriction(CifpAltitudeRestrictionType Type, int Altitude1Ft, int? Altitude2Ft = null);

public sealed record CifpSpeedRestriction(int SpeedKts, bool IsMaximum);

public enum CifpFixRole
{
    None,
    IAF,
    IF,
    FAF,

    /// <summary>
    /// Missed Approach Point. ARINC 424 §5.17 Waypoint Description Code position 4
    /// character 'M' identifies the fix at which the FAS ends and the missed-approach
    /// segment begins. This is NOT the missed-approach holding point (MAHP), which is
    /// a separate concept further along the missed-approach procedure.
    /// </summary>
    MAP,
}

public enum CifpPathTerminator
{
    IF,
    TF,
    CF,
    DF,
    RF,
    AF,
    HA,
    HF,
    HM,
    PI,
    CA,
    FA,
    VA,
    VM,
    VI,
    CI,
    Other,
}

public sealed record CifpLeg(
    string FixIdentifier,
    CifpPathTerminator PathTerminator,
    char? TurnDirection,
    CifpAltitudeRestriction? Altitude,
    CifpSpeedRestriction? Speed,
    CifpFixRole FixRole,
    int Sequence,
    double? OutboundCourse,
    double? LegDistanceNm,
    double? VerticalAngle,
    double? ArcRadiusNm = null,
    double? ArcCenterLat = null,
    double? ArcCenterLon = null,
    string? RecommendedNavaidId = null,
    double? Theta = null,
    double? Rho = null,
    bool IsFlyOver = false
);

public sealed record CifpTransition(string Name, IReadOnlyList<CifpLeg> Legs)
{
    /// <summary>
    /// True when the transition contains no course-reversal legs (PI/HM/HF/HA), i.e. it
    /// delivers the aircraft to the inbound segment without requiring a procedure turn or
    /// hold-in-lieu. Equivalent to a "NoPT" feeder route depicted on FAA charts: when an
    /// aircraft enters via a NoPT transition, AIM 5-4-9.1 exempts it from the procedure
    /// turn even when one is published for the approach.
    /// </summary>
    public bool IsNoPt =>
        !Legs.Any(l => l.PathTerminator is CifpPathTerminator.PI or CifpPathTerminator.HM or CifpPathTerminator.HF or CifpPathTerminator.HA);
}

public sealed record CifpSidProcedure(
    string Airport,
    string ProcedureId,
    IReadOnlyList<CifpLeg> CommonLegs,
    IReadOnlyDictionary<string, CifpTransition> RunwayTransitions,
    IReadOnlyDictionary<string, CifpTransition> EnrouteTransitions
);

public sealed record CifpStarProcedure(
    string Airport,
    string ProcedureId,
    IReadOnlyList<CifpLeg> CommonLegs,
    IReadOnlyDictionary<string, CifpTransition> EnrouteTransitions,
    IReadOnlyDictionary<string, CifpTransition> RunwayTransitions
);

public sealed record CifpApproachProcedure(
    string Airport,
    string ApproachId,
    char TypeCode,
    string ApproachTypeName,
    string? Runway,
    IReadOnlyList<CifpLeg> CommonLegs,
    IReadOnlyDictionary<string, CifpTransition> Transitions,
    IReadOnlyList<CifpLeg> MissedApproachLegs,
    bool HasHoldInLieu,
    CifpLeg? HoldInLieuLeg,
    CifpLeg? ProcedureTurnLeg = null
);
