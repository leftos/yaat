using System.Text.Json.Serialization;

namespace Yaat.Sim.Data.Vnas;

public class AircraftSpecEntry
{
    [JsonPropertyName("Designator")]
    public string Designator { get; set; } = "";

    [JsonPropertyName("Manufacturer")]
    public string Manufacturer { get; set; } = "";

    [JsonPropertyName("TypeDescription")]
    public string TypeDescription { get; set; } = "";

    [JsonPropertyName("EngineType")]
    public string EngineType { get; set; } = "";

    [JsonPropertyName("EngineCount")]
    public string EngineCount { get; set; } = "";

    [JsonPropertyName("WTC")]
    public string Wtc { get; set; } = "";

    [JsonPropertyName("WTG")]
    public string Wtg { get; set; } = "";
}
