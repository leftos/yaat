using System.Text.Json.Serialization;

namespace Yaat.LayoutInspector.Tick;

/// <summary>
/// Top-level structure of a TickRecorder JSON file (matches
/// <c>Yaat.Sim.Tests.Helpers.TickRecording</c>). Bumped on incompatible
/// schema changes — the reader rejects unknown major versions.
/// </summary>
public sealed class TickRecording
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("airportId")]
    public string? AirportId { get; init; }

    [JsonPropertyName("aircraft")]
    public List<AircraftMetadata> Aircraft { get; init; } = [];

    [JsonPropertyName("ticks")]
    public List<TickEvent> Ticks { get; init; } = [];
}

public sealed class AircraftMetadata
{
    [JsonPropertyName("callsign")]
    public string Callsign { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("wingspanFt")]
    public double? WingspanFt { get; init; }

    [JsonPropertyName("lengthFt")]
    public double? LengthFt { get; init; }

    [JsonPropertyName("color")]
    public string Color { get; init; } = "#1e88e5";
}

public sealed class TickEvent
{
    [JsonPropertyName("t")]
    public int T { get; init; }

    [JsonPropertyName("callsign")]
    public string Callsign { get; init; } = "";

    [JsonPropertyName("lat")]
    public double Lat { get; init; }

    [JsonPropertyName("lon")]
    public double Lon { get; init; }

    [JsonPropertyName("hdg")]
    public double Hdg { get; init; }

    [JsonPropertyName("gs")]
    public double Gs { get; init; }

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "";

    [JsonPropertyName("twy")]
    public string? Twy { get; init; }

    [JsonPropertyName("speedLimit")]
    public double? SpeedLimit { get; init; }

    [JsonPropertyName("nav")]
    public NavTickDto? Nav { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed class NavTickDto
{
    [JsonPropertyName("targetNodeId")]
    public int TargetNodeId { get; init; }

    [JsonPropertyName("distNm")]
    public double DistNm { get; init; }

    [JsonPropertyName("brgDeg")]
    public double BrgDeg { get; init; }

    [JsonPropertyName("angleDiffDeg")]
    public double AngleDiffDeg { get; init; }

    [JsonPropertyName("targetSpdKts")]
    public double TargetSpdKts { get; init; }

    [JsonPropertyName("brakeLimitKts")]
    public double BrakeLimitKts { get; init; }

    [JsonPropertyName("arcLimitKts")]
    public double ArcLimitKts { get; init; }

    [JsonPropertyName("onArc")]
    public bool OnArc { get; init; }

    [JsonPropertyName("nodeReqSpdKts")]
    public double NodeReqSpdKts { get; init; }

    [JsonPropertyName("pathDevFt")]
    public double PathDevFt { get; init; }

    [JsonPropertyName("segFromLat")]
    public double SegFromLat { get; init; }

    [JsonPropertyName("segFromLon")]
    public double SegFromLon { get; init; }
}
