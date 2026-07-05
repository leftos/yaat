namespace Yaat.Sim;

/// <summary>
/// The altitude filed in / assigned to a flight plan — the <em>notation</em> axis of the plan
/// (single, block, VFR, VFR-on-top, or above), distinct from <see cref="ControlTargets.AssignedAltitude"/>
/// (the current ATC clearance the aircraft flies toward) and from <see cref="AircraftFlightPlan.FlightRules"/>
/// (the IFR/VFR rules axis). Altitudes are in <strong>feet</strong> (the sim convention); the CRC wire
/// (<c>ParsedAltitude</c>) and training-hub DTO divide by 100. Mirrors vNAS
/// <c>common/ParsedAltitude.cs</c> without its raw-string field.
/// </summary>
/// <param name="CruiseFeet">Single altitude, block <em>ceiling</em>, or VFR/OTP-with-altitude; null = no altitude.</param>
/// <param name="BlockFloorFeet">Block floor; non-null iff this is a block altitude.</param>
/// <param name="IsVfr">VFR notation (e.g. "VFR" / "VFR/065").</param>
/// <param name="IsVfrOnTop">VFR-on-top notation (e.g. "OTP" / "OTP/065").</param>
/// <param name="IsAbove">Above notation (e.g. "A050"); wire parity only — no input path yet.</param>
public sealed record PlannedAltitude(int? CruiseFeet, int? BlockFloorFeet, bool IsVfr, bool IsVfrOnTop, bool IsAbove)
{
    /// <summary>No filed altitude (IFR with nothing filed).</summary>
    public static readonly PlannedAltitude None = new(null, null, false, false, false);

    /// <summary>Single IFR altitude in feet.</summary>
    public static PlannedAltitude Ifr(int feet) => new(feet, null, false, false, false);

    /// <summary>Block altitude between <paramref name="floorFeet"/> and <paramref name="ceilingFeet"/>, inclusive.</summary>
    public static PlannedAltitude Block(int floorFeet, int ceilingFeet) => new(ceilingFeet, floorFeet, false, false, false);

    /// <summary>VFR, with an optional filed altitude.</summary>
    public static PlannedAltitude Vfr(int? feet) => new(feet, null, true, false, false);

    /// <summary>VFR-on-top, with an optional filed altitude.</summary>
    public static PlannedAltitude Otp(int? feet) => new(feet, null, false, true, false);

    /// <summary>Above a given altitude (e.g. "A050").</summary>
    public static PlannedAltitude Above(int feet) => new(feet, null, false, false, true);

    /// <summary>True when this is a block altitude (<see cref="BlockFloorFeet"/> set).</summary>
    public bool IsBlock => BlockFloorFeet is not null;

    /// <summary>True when this is a plain single altitude (no block/VFR/OTP/above flags).</summary>
    public bool IsSingle => (CruiseFeet is not null) && (BlockFloorFeet is null) && !IsVfr && !IsVfrOnTop && !IsAbove;

    /// <summary>True when the notation implies VFR operation (VFR or VFR-on-top) — for "is this a VFR plan" readers.</summary>
    public bool IsVfrRules => IsVfr || IsVfrOnTop;
}
