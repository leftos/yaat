using System.Text.Json.Serialization;

namespace Yaat.Sim;

public class WeatherPeriod
{
    [JsonPropertyName("startMinutes")]
    public double StartMinutes { get; set; }

    [JsonPropertyName("transitionMinutes")]
    public double TransitionMinutes { get; set; }

    [JsonPropertyName("precipitation")]
    public string? Precipitation { get; set; }

    [JsonPropertyName("windLayers")]
    public List<WindLayer> WindLayers { get; set; } = [];

    [JsonPropertyName("metars")]
    public List<string> Metars { get; set; } = [];
}
