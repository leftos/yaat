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
    MAHP,
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
    double? VerticalAngle
);

public sealed record CifpTransition(string Name, IReadOnlyList<CifpLeg> Legs);

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
    CifpLeg? HoldInLieuLeg
);
