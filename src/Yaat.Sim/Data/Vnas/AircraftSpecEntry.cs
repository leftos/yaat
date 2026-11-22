using System.Text.Json.Serialization;

namespace Yaat.Sim.Data.Vnas;

public class AircraftSpecEntry
{
    [JsonPropertyName("Designator")]
    public string Designator { get; set; } = "";

    /// <summary>The manufacturer's code, e.g. <c>BOEING</c>.</summary>
    [JsonPropertyName("ManufacturerCode")]
    public string Manufacturer { get; set; } = "";

    /// <summary>The ICAO type description, e.g. <c>L2J</c> (land plane, two engines, jet).</summary>
    [JsonPropertyName("Description")]
    public string TypeDescription { get; set; } = "";

    /// <summary>The model's full name, e.g. <c>737-800</c>; several rows of one designator name its variants.</summary>
    [JsonPropertyName("ModelFullName")]
    public string Model { get; set; } = "";

    [JsonPropertyName("EngineType")]
    public string EngineType { get; set; } = "";

    [JsonPropertyName("EngineCount")]
    public string EngineCount { get; set; } = "";

    [JsonPropertyName("WTC")]
    public string Wtc { get; set; } = "";

    [JsonPropertyName("WTG")]
    public string Wtg { get; set; } = "";

    [JsonPropertyName("AircraftDescription")]
    public string AircraftDescription { get; set; } = "";
}
