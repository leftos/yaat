using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

public sealed class CustomFixDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = [];

    [JsonPropertyName("lat")]
    public double? Lat { get; set; }

    [JsonPropertyName("lon")]
    public double? Lon { get; set; }

    [JsonPropertyName("frd")]
    public string? Frd { get; set; }
}
