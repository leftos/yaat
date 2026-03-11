using System.Text.Json.Serialization;

namespace Yaat.Sim.Data.Faa;

/// <summary>
/// One row from the FAA Aircraft Characteristics Database (ACD).
/// All columns preserved; nullable where the source spreadsheet may have blanks.
/// </summary>
public sealed record FaaAircraftRecord
{
    [JsonPropertyName("icaoCode")]
    public required string IcaoCode { get; init; }

    [JsonPropertyName("faaDesignator")]
    public string? FaaDesignator { get; init; }

    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; init; }

    [JsonPropertyName("modelFaa")]
    public string? ModelFaa { get; init; }

    [JsonPropertyName("modelBada")]
    public string? ModelBada { get; init; }

    [JsonPropertyName("physicalClassEngine")]
    public string? PhysicalClassEngine { get; init; }

    [JsonPropertyName("numEngines")]
    public int? NumEngines { get; init; }

    [JsonPropertyName("aac")]
    public string? Aac { get; init; }

    [JsonPropertyName("aacMinimum")]
    public string? AacMinimum { get; init; }

    [JsonPropertyName("aacMaximum")]
    public string? AacMaximum { get; init; }

    [JsonPropertyName("adg")]
    public string? Adg { get; init; }

    [JsonPropertyName("tdg")]
    public string? Tdg { get; init; }

    [JsonPropertyName("approachSpeedKnot")]
    public int? ApproachSpeedKnot { get; init; }

    [JsonPropertyName("approachSpeedMinimumKnot")]
    public int? ApproachSpeedMinimumKnot { get; init; }

    [JsonPropertyName("approachSpeedMaximumKnot")]
    public int? ApproachSpeedMaximumKnot { get; init; }

    [JsonPropertyName("wingspanFtWithoutWinglets")]
    public double? WingspanFtWithoutWinglets { get; init; }

    [JsonPropertyName("wingspanFtWithWinglets")]
    public double? WingspanFtWithWinglets { get; init; }

    [JsonPropertyName("lengthFt")]
    public double? LengthFt { get; init; }

    [JsonPropertyName("tailHeightAtOewFt")]
    public double? TailHeightAtOewFt { get; init; }

    [JsonPropertyName("wheelbaseFt")]
    public double? WheelbaseFt { get; init; }

    [JsonPropertyName("cockpitToMainGearFt")]
    public double? CockpitToMainGearFt { get; init; }

    [JsonPropertyName("mainGearWidthFt")]
    public double? MainGearWidthFt { get; init; }

    [JsonPropertyName("mtowLb")]
    public double? MtowLb { get; init; }

    [JsonPropertyName("malwLb")]
    public double? MalwLb { get; init; }

    [JsonPropertyName("mainGearConfig")]
    public string? MainGearConfig { get; init; }

    [JsonPropertyName("icaoWtc")]
    public string? IcaoWtc { get; init; }

    [JsonPropertyName("parkingAreaFt2")]
    public double? ParkingAreaFt2 { get; init; }

    [JsonPropertyName("class")]
    public string? Class { get; init; }

    [JsonPropertyName("faaWeight")]
    public string? FaaWeight { get; init; }

    [JsonPropertyName("cwt")]
    public string? Cwt { get; init; }

    [JsonPropertyName("oneHalfWakeCategory")]
    public string? OneHalfWakeCategory { get; init; }

    [JsonPropertyName("twoWakeCategoryAppxA")]
    public string? TwoWakeCategoryAppxA { get; init; }

    [JsonPropertyName("twoWakeCategoryAppxB")]
    public string? TwoWakeCategoryAppxB { get; init; }

    [JsonPropertyName("rotorDiameterFt")]
    public double? RotorDiameterFt { get; init; }

    [JsonPropertyName("srs")]
    public string? Srs { get; init; }

    [JsonPropertyName("lahso")]
    public string? Lahso { get; init; }

    [JsonPropertyName("faaRegistry")]
    public string? FaaRegistry { get; init; }

    [JsonPropertyName("registrationCount")]
    public int? RegistrationCount { get; init; }

    [JsonPropertyName("tmfsOperationsFy")]
    public int? TmfsOperationsFy { get; init; }

    [JsonPropertyName("remarks")]
    public string? Remarks { get; init; }

    [JsonPropertyName("lastUpdate")]
    public string? LastUpdate { get; init; }

    /// <summary>
    /// Effective wingspan in feet: prefers with-winglets value, falls back to without-winglets.
    /// </summary>
    [JsonIgnore]
    public double? WingspanFt => WingspanFtWithWinglets ?? WingspanFtWithoutWinglets;
}
