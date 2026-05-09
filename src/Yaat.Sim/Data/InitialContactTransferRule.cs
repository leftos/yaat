using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

public enum InitialContactTransferTiming
{
    HandoffInitiated,
    HandoffAccepted,
    NoHandoffNecessary,
}

public sealed class InitialContactTransferRule
{
    [JsonIgnore]
    public string ArtccId { get; set; } = "";

    [JsonPropertyName("airportId")]
    public string? AirportId { get; set; }

    [JsonPropertyName("fromCallsign")]
    public string? FromCallsign { get; set; }

    [JsonPropertyName("fromPositionType")]
    public string? FromPositionType { get; set; }

    [JsonPropertyName("toCallsign")]
    public string? ToCallsign { get; set; }

    [JsonPropertyName("toPositionType")]
    public string? ToPositionType { get; set; }

    [JsonPropertyName("contactAllowedWhen")]
    public string ContactAllowedWhen { get; set; } = "";

    [JsonPropertyName("allowsWithoutTrackHandoff")]
    public bool? AllowsWithoutTrackHandoff { get; set; }

    [JsonIgnore]
    public InitialContactTransferTiming Timing { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
