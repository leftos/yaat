using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// The spoken base name of a SID or STAR whose CIFP code is a truncated name, read from
/// <c>{ARTCC}/ProcedurePronunciations/*.json</c>: <c>{"procedure": "NIMI", "pronunciations": ["nimitz"]}</c> makes
/// <c>NIMI5</c> "nimitz five". Pilot speech says the first pronunciation; the speech-to-text context hears every one.
/// </summary>
public sealed record ProcedurePronunciationDefinition
{
    /// <summary>The procedure's base name, letters only, without the version digits ("NIMI").</summary>
    [JsonPropertyName("procedure")]
    public string Procedure { get; init; } = "";

    /// <summary>How the base name is said, lowercase words ("nimitz").</summary>
    [JsonPropertyName("pronunciations")]
    public IReadOnlyList<string> Pronunciations { get; init; } = [];
}
