using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// A partial, authoritative correction to an <see cref="AircraftProfile"/>, loaded from
/// AircraftProfileOverrides.json. Every performance field is nullable: only the fields a
/// contributor actually specifies override the base profile (or the category baseline for a
/// type with no base profile). A null field is left unset — it is NOT an explicit 0, so a
/// contributor can correct one value without disturbing the rest.
///
/// Overridden fields are <b>authoritative</b>: <see cref="OverrideAwareProfileCorrectionAdapter"/>
/// returns them verbatim, bypassing the runtime <see cref="EurocontrolProfileCorrectionAdapter"/>
/// rescaling. This is required for cases like the SF50, whose real ~170 kt initial climb would
/// otherwise be capped to ~122 kt (FAA ACD Vref 87 × the jet climb-speed multiplier).
/// </summary>
public sealed record AircraftProfileOverride
{
    [JsonPropertyName("typeCode")]
    public required string TypeCode { get; init; }

    /// <summary>Free-text note explaining the correction and its source. Not used at runtime.</summary>
    [JsonPropertyName("note")]
    public string Note { get; init; } = "";

    [JsonPropertyName("isProp")]
    public bool? IsProp { get; init; }

    [JsonPropertyName("isHelo")]
    public bool? IsHelo { get; init; }

    [JsonPropertyName("isHeavy")]
    public bool? IsHeavy { get; init; }

    [JsonPropertyName("isSpeedLimitWaived")]
    public bool? IsSpeedLimitWaived { get; init; }

    [JsonPropertyName("airborneAccelRate")]
    public double? AirborneAccelRate { get; init; }

    [JsonPropertyName("airborneDecelRate")]
    public double? AirborneDecelRate { get; init; }

    [JsonPropertyName("groundAccelRate")]
    public double? GroundAccelRate { get; init; }

    [JsonPropertyName("takeoffDistance")]
    public double? TakeoffDistance { get; init; }

    [JsonPropertyName("rotateSpeed")]
    public double? RotateSpeed { get; init; }

    [JsonPropertyName("climbSpeedInitial")]
    public double? ClimbSpeedInitial { get; init; }

    [JsonPropertyName("climbSpeedFl150")]
    public double? ClimbSpeedFl150 { get; init; }

    [JsonPropertyName("climbSpeedFl240")]
    public double? ClimbSpeedFl240 { get; init; }

    [JsonPropertyName("climbSpeedFinal")]
    public double? ClimbSpeedFinal { get; init; }

    [JsonPropertyName("climbRateInitial")]
    public double? ClimbRateInitial { get; init; }

    [JsonPropertyName("climbRateFl150")]
    public double? ClimbRateFl150 { get; init; }

    [JsonPropertyName("climbRateFl240")]
    public double? ClimbRateFl240 { get; init; }

    [JsonPropertyName("climbRateFinal")]
    public double? ClimbRateFinal { get; init; }

    [JsonPropertyName("cruiseSpeed")]
    public double? CruiseSpeed { get; init; }

    [JsonPropertyName("cruiseAltitude")]
    public double? CruiseAltitude { get; init; }

    [JsonPropertyName("ceiling")]
    public double? Ceiling { get; init; }

    [JsonPropertyName("descentSpeedInitial")]
    public double? DescentSpeedInitial { get; init; }

    [JsonPropertyName("descentSpeedFl100")]
    public double? DescentSpeedFl100 { get; init; }

    [JsonPropertyName("initialApproachSpeed")]
    public double? InitialApproachSpeed { get; init; }

    [JsonPropertyName("descentRateInitial")]
    public double? DescentRateInitial { get; init; }

    [JsonPropertyName("descentRateFl100")]
    public double? DescentRateFl100 { get; init; }

    [JsonPropertyName("descentRateApproach")]
    public double? DescentRateApproach { get; init; }

    [JsonPropertyName("finalApproachSpeed")]
    public double? FinalApproachSpeed { get; init; }

    [JsonPropertyName("landingSpeed")]
    public double? LandingSpeed { get; init; }

    [JsonPropertyName("landingDistance")]
    public double? LandingDistance { get; init; }

    [JsonPropertyName("patternSpeed")]
    public double? PatternSpeed { get; init; }

    [JsonPropertyName("holdingSpeed")]
    public double? HoldingSpeed { get; init; }

    [JsonPropertyName("length")]
    public double? Length { get; init; }

    [JsonPropertyName("standardTurnRateOverride")]
    public double? StandardTurnRateOverride { get; init; }

    /// <summary>
    /// Merge this override onto <paramref name="baseProfile"/>. Returns the merged profile and
    /// the set of <see cref="AircraftProfile"/> property names that were overridden (so the
    /// correction adapter can treat exactly those fields as authoritative).
    /// </summary>
    public (AircraftProfile Merged, IReadOnlySet<string> OverriddenFields) ApplyTo(AircraftProfile baseProfile)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);

        var merged = baseProfile with
        {
            IsProp = ResolveBool(IsProp, baseProfile.IsProp, nameof(AircraftProfile.IsProp), fields),
            IsHelo = ResolveBool(IsHelo, baseProfile.IsHelo, nameof(AircraftProfile.IsHelo), fields),
            IsHeavy = ResolveBool(IsHeavy, baseProfile.IsHeavy, nameof(AircraftProfile.IsHeavy), fields),
            IsSpeedLimitWaived = ResolveBool(IsSpeedLimitWaived, baseProfile.IsSpeedLimitWaived, nameof(AircraftProfile.IsSpeedLimitWaived), fields),
            AirborneAccelRate = Resolve(AirborneAccelRate, baseProfile.AirborneAccelRate, nameof(AircraftProfile.AirborneAccelRate), fields),
            AirborneDecelRate = Resolve(AirborneDecelRate, baseProfile.AirborneDecelRate, nameof(AircraftProfile.AirborneDecelRate), fields),
            GroundAccelRate = Resolve(GroundAccelRate, baseProfile.GroundAccelRate, nameof(AircraftProfile.GroundAccelRate), fields),
            TakeoffDistance = Resolve(TakeoffDistance, baseProfile.TakeoffDistance, nameof(AircraftProfile.TakeoffDistance), fields),
            RotateSpeed = Resolve(RotateSpeed, baseProfile.RotateSpeed, nameof(AircraftProfile.RotateSpeed), fields),
            ClimbSpeedInitial = Resolve(ClimbSpeedInitial, baseProfile.ClimbSpeedInitial, nameof(AircraftProfile.ClimbSpeedInitial), fields),
            ClimbSpeedFl150 = Resolve(ClimbSpeedFl150, baseProfile.ClimbSpeedFl150, nameof(AircraftProfile.ClimbSpeedFl150), fields),
            ClimbSpeedFl240 = Resolve(ClimbSpeedFl240, baseProfile.ClimbSpeedFl240, nameof(AircraftProfile.ClimbSpeedFl240), fields),
            ClimbSpeedFinal = Resolve(ClimbSpeedFinal, baseProfile.ClimbSpeedFinal, nameof(AircraftProfile.ClimbSpeedFinal), fields),
            ClimbRateInitial = Resolve(ClimbRateInitial, baseProfile.ClimbRateInitial, nameof(AircraftProfile.ClimbRateInitial), fields),
            ClimbRateFl150 = Resolve(ClimbRateFl150, baseProfile.ClimbRateFl150, nameof(AircraftProfile.ClimbRateFl150), fields),
            ClimbRateFl240 = Resolve(ClimbRateFl240, baseProfile.ClimbRateFl240, nameof(AircraftProfile.ClimbRateFl240), fields),
            ClimbRateFinal = Resolve(ClimbRateFinal, baseProfile.ClimbRateFinal, nameof(AircraftProfile.ClimbRateFinal), fields),
            CruiseSpeed = Resolve(CruiseSpeed, baseProfile.CruiseSpeed, nameof(AircraftProfile.CruiseSpeed), fields),
            CruiseAltitude = Resolve(CruiseAltitude, baseProfile.CruiseAltitude, nameof(AircraftProfile.CruiseAltitude), fields),
            Ceiling = Resolve(Ceiling, baseProfile.Ceiling, nameof(AircraftProfile.Ceiling), fields),
            DescentSpeedInitial = Resolve(DescentSpeedInitial, baseProfile.DescentSpeedInitial, nameof(AircraftProfile.DescentSpeedInitial), fields),
            DescentSpeedFl100 = Resolve(DescentSpeedFl100, baseProfile.DescentSpeedFl100, nameof(AircraftProfile.DescentSpeedFl100), fields),
            InitialApproachSpeed = Resolve(
                InitialApproachSpeed,
                baseProfile.InitialApproachSpeed,
                nameof(AircraftProfile.InitialApproachSpeed),
                fields
            ),
            DescentRateInitial = Resolve(DescentRateInitial, baseProfile.DescentRateInitial, nameof(AircraftProfile.DescentRateInitial), fields),
            DescentRateFl100 = Resolve(DescentRateFl100, baseProfile.DescentRateFl100, nameof(AircraftProfile.DescentRateFl100), fields),
            DescentRateApproach = Resolve(DescentRateApproach, baseProfile.DescentRateApproach, nameof(AircraftProfile.DescentRateApproach), fields),
            FinalApproachSpeed = Resolve(FinalApproachSpeed, baseProfile.FinalApproachSpeed, nameof(AircraftProfile.FinalApproachSpeed), fields),
            LandingSpeed = Resolve(LandingSpeed, baseProfile.LandingSpeed, nameof(AircraftProfile.LandingSpeed), fields),
            LandingDistance = Resolve(LandingDistance, baseProfile.LandingDistance, nameof(AircraftProfile.LandingDistance), fields),
            PatternSpeed = Resolve(PatternSpeed, baseProfile.PatternSpeed, nameof(AircraftProfile.PatternSpeed), fields),
            HoldingSpeed = Resolve(HoldingSpeed, baseProfile.HoldingSpeed, nameof(AircraftProfile.HoldingSpeed), fields),
            Length = Resolve(Length, baseProfile.Length, nameof(AircraftProfile.Length), fields),
            StandardTurnRateOverride = Resolve(
                StandardTurnRateOverride,
                baseProfile.StandardTurnRateOverride,
                nameof(AircraftProfile.StandardTurnRateOverride),
                fields
            ),
        };

        return (merged, fields);
    }

    private static double Resolve(double? overrideValue, double baseValue, string fieldName, HashSet<string> overridden)
    {
        if (overrideValue is { } v)
        {
            overridden.Add(fieldName);
            return v;
        }

        return baseValue;
    }

    private static bool ResolveBool(bool? overrideValue, bool baseValue, string fieldName, HashSet<string> overridden)
    {
        if (overrideValue is { } v)
        {
            overridden.Add(fieldName);
            return v;
        }

        return baseValue;
    }
}
