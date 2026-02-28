using System.Text.Json.Serialization;

namespace Yaat.Sim.Data.Vnas;

public class AircraftCwtEntry
{
    [JsonPropertyName("typeCode")]
    public string TypeCode { get; set; } = "";

    [JsonPropertyName("weightCode")]
    public string WeightCode { get; set; } = "";

    [JsonPropertyName("cwtCode")]
    public string CwtCode { get; set; } = "";
}
