using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// One airport's spoken forms for its gates and spots, plus its named ground places (FBOs, ramps, bays), read from
/// <c>{ARTCC}/GatePronunciations/*.json</c>. The speech-to-text context layer reads them; pilot speech does not.
/// </summary>
/// <example>
/// <c>{"airportId": "KOAK", "gates": [{"name": "SIG1", "pronunciations": ["signature one"]}],
/// "places": [{"name": "SIGNATURE", "pronunciations": ["signature"], "stands": ["SIG*"]}]}</c>
/// </example>
public sealed record GatePronunciationFile
{
    /// <summary>The airport the file describes, FAA or ICAO form ("OAK" or "KOAK").</summary>
    [JsonPropertyName("airportId")]
    public string AirportId { get; init; } = "";

    /// <summary>Spoken forms of layout gate (parking) names.</summary>
    [JsonPropertyName("gates")]
    public IReadOnlyList<NamePronunciation> Gates { get; init; } = [];

    /// <summary>Spoken forms of layout spot names.</summary>
    [JsonPropertyName("spots")]
    public IReadOnlyList<NamePronunciation> Spots { get; init; } = [];

    /// <summary>Named ground places a controller says as a landmark ("signature", "kaiser").</summary>
    [JsonPropertyName("places")]
    public IReadOnlyList<GroundPlaceDefinition> Places { get; init; } = [];
}

/// <summary>The spoken forms of one gate or spot name.</summary>
public sealed record NamePronunciation
{
    /// <summary>The layout name ("SIG1", "6B").</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>How a controller says it, lowercase words ("signature one").</summary>
    [JsonPropertyName("pronunciations")]
    public IReadOnlyList<string> Pronunciations { get; init; } = [];
}

/// <summary>A named ground place: an FBO, ramp or bay a controller names instead of a stand.</summary>
public sealed record GroundPlaceDefinition
{
    /// <summary>The place's name, which becomes the landmark's value ("SIGNATURE").</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>How a controller says it ("signature", "signature ramp").</summary>
    [JsonPropertyName("pronunciations")]
    public IReadOnlyList<string> Pronunciations { get; init; } = [];

    /// <summary>The layout stands the place covers: exact names or a prefix ending in <c>*</c>. Informational.</summary>
    [JsonPropertyName("stands")]
    public IReadOnlyList<string> Stands { get; init; } = [];

    /// <summary>Free-text provenance. Informational.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}
