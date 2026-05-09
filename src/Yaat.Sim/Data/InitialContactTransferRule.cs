using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

public sealed class InitialContactTransferRule
{
    [JsonIgnore]
    public string ArtccId { get; set; } = "";

    [JsonPropertyName("airportId")]
    public string AirportId { get; set; } = "";

    [JsonPropertyName("fromPositionType")]
    public string FromPositionType { get; set; } = "";

    [JsonPropertyName("toPositionType")]
    public string ToPositionType { get; set; } = "";

    [JsonPropertyName("allowsWithoutTrackHandoff")]
    public bool AllowsWithoutTrackHandoff { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
