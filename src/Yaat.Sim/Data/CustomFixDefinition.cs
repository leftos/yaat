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

    /// <summary>
    /// Optional natural-language phrases that should be recognized as this fix by the speech
    /// recognition pipeline. Each string is tokenized at load time and matched greedily (longest
    /// first) against spoken transcripts — when matched, those tokens are replaced with the
    /// first alias so downstream <c>{fix}</c> rule captures work unchanged.
    ///
    /// Example: <c>["runway 30 numbers", "the runway 30 numbers", "30 numbers"]</c> — each variant
    /// is matched independently, so you can include prefixed and unprefixed forms. Numbers should
    /// be written as digits (the phraseology normalizer converts spoken numbers to digits before
    /// this step runs).
    /// </summary>
    [JsonPropertyName("spokenPatterns")]
    public List<string>? SpokenPatterns { get; set; }
}
