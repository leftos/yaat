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
/// <param name="IsAbove">Above notation (e.g. "A050"), entered with ERAM <c>AM ALT ABV/050</c>.</param>
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

    /// <summary>
    /// ERAM field 08's fix-qualified form <c>(d)dd/Fix/(d)dd</c> (SRS p.411, p.633-634): the fix after which
    /// <see cref="AfterFixFeet"/> replaces <see cref="CruiseFeet"/>, as typed (a fix name, an FRD or a lat/long). Null for
    /// every other form. Data only: the record of a 7110.65 §4-5-7 "maintain until … then" clearance, never flown.
    /// </summary>
    public string? AltitudeFix { get; init; }

    /// <summary>The altitude in feet that holds after <see cref="AltitudeFix"/>; non-null iff <see cref="AltitudeFix"/> is.</summary>
    public int? AfterFixFeet { get; init; }

    /// <summary>
    /// The fix-qualified altitude <c>(d)dd/Fix/(d)dd</c>: <paramref name="feet"/> until <paramref name="fix"/> (as typed),
    /// <paramref name="afterFixFeet"/> after it.
    /// </summary>
    public static PlannedAltitude UntilFix(int feet, string fix, int afterFixFeet) =>
        Ifr(feet) with
        {
            AltitudeFix = fix,
            AfterFixFeet = afterFixFeet,
        };

    /// <summary>True when this is the fix-qualified form (<see cref="AltitudeFix"/> set).</summary>
    public bool IsFixQualified => AltitudeFix is not null;

    /// <summary>True when this is a block altitude (<see cref="BlockFloorFeet"/> set).</summary>
    public bool IsBlock => BlockFloorFeet is not null;

    /// <summary>True when this is a plain single altitude (no block/VFR/OTP/above flags).</summary>
    public bool IsSingle => (CruiseFeet is not null) && (BlockFloorFeet is null) && !IsVfr && !IsVfrOnTop && !IsAbove;
}
