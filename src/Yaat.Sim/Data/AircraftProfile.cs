using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// Per-type aircraft performance profile from AircraftProfiles.json.
/// Speed fields may contain Mach numbers (values &lt; 1.0) for high-altitude entries.
/// Zero values in altitude-banded fields mean the aircraft cannot reach that altitude.
/// </summary>
public sealed record AircraftProfile
{
    [JsonPropertyName("typeCode")]
    public required string TypeCode { get; init; }

    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; init; } = "";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("isProp")]
    public bool IsProp { get; init; }

    [JsonPropertyName("isHelo")]
    public bool IsHelo { get; init; }

    [JsonPropertyName("isHeavy")]
    public bool IsHeavy { get; init; }

    [JsonPropertyName("isSpeedLimitWaived")]
    public bool IsSpeedLimitWaived { get; init; }

    [JsonPropertyName("airborneAccelRate")]
    public double AirborneAccelRate { get; init; }

    [JsonPropertyName("airborneDecelRate")]
    public double AirborneDecelRate { get; init; }

    [JsonPropertyName("groundAccelRate")]
    public double GroundAccelRate { get; init; }

    [JsonPropertyName("groundDecelRate")]
    public double GroundDecelRate { get; init; }

    [JsonPropertyName("takeoffDistance")]
    public double TakeoffDistance { get; init; }

    [JsonPropertyName("rotateSpeed")]
    public double RotateSpeed { get; init; }

    [JsonPropertyName("climbSpeedInitial")]
    public double ClimbSpeedInitial { get; init; }

    [JsonPropertyName("climbSpeedFl150")]
    public double ClimbSpeedFl150 { get; init; }

    [JsonPropertyName("climbSpeedFl240")]
    public double ClimbSpeedFl240 { get; init; }

    /// <summary>Climb speed above FL240. Often a Mach number (e.g. 0.78).</summary>
    [JsonPropertyName("climbSpeedFinal")]
    public double ClimbSpeedFinal { get; init; }

    [JsonPropertyName("climbRateInitial")]
    public double ClimbRateInitial { get; init; }

    [JsonPropertyName("climbRateFl150")]
    public double ClimbRateFl150 { get; init; }

    [JsonPropertyName("climbRateFl240")]
    public double ClimbRateFl240 { get; init; }

    [JsonPropertyName("climbRateFinal")]
    public double ClimbRateFinal { get; init; }

    [JsonPropertyName("cruiseSpeed")]
    public double CruiseSpeed { get; init; }

    [JsonPropertyName("ceiling")]
    public double Ceiling { get; init; }

    /// <summary>Descent speed at high altitude. Often a Mach number (e.g. 0.78).</summary>
    [JsonPropertyName("descentSpeedInitial")]
    public double DescentSpeedInitial { get; init; }

    [JsonPropertyName("descentSpeedFl100")]
    public double DescentSpeedFl100 { get; init; }

    [JsonPropertyName("initialApproachSpeed")]
    public double InitialApproachSpeed { get; init; }

    [JsonPropertyName("descentRateInitial")]
    public double DescentRateInitial { get; init; }

    [JsonPropertyName("descentRateFl100")]
    public double DescentRateFl100 { get; init; }

    [JsonPropertyName("descentRateApproach")]
    public double DescentRateApproach { get; init; }

    [JsonPropertyName("finalApproachSpeed")]
    public double FinalApproachSpeed { get; init; }

    [JsonPropertyName("landingSpeed")]
    public double LandingSpeed { get; init; }

    [JsonPropertyName("landingDistance")]
    public double LandingDistance { get; init; }

    [JsonPropertyName("patternSpeed")]
    public double PatternSpeed { get; init; }

    [JsonPropertyName("holdingSpeed")]
    public double HoldingSpeed { get; init; }

    [JsonPropertyName("length")]
    public double Length { get; init; }

    /// <summary>Override for standard turn rate (deg/sec). 0 means use category default.</summary>
    [JsonPropertyName("standardTurnRateOverride")]
    public double StandardTurnRateOverride { get; init; }
}
