using System.Text.Json.Serialization;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Corpus schema for the ouroboros round-trip harness. Each case names a canonical command and
/// the minimum context needed to render its readback; the harness builds a synthetic
/// <c>AircraftState</c> from these fields, calls <c>PilotResponder.BuildReadback</c>, synthesizes
/// audio, and runs the audio through the full STT pipeline. <see cref="OuroborosCase.Context"/>
/// is intentionally open-ended — add fields as new cases need them.
/// </summary>
public sealed record OuroborosCorpus([property: JsonPropertyName("cases")] IReadOnlyList<OuroborosCase> Cases);

public sealed record OuroborosCase(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("callsign")] string Callsign,
    [property: JsonPropertyName("canonical")] string Canonical,
    [property: JsonPropertyName("aircraft_type")] string? AircraftType = null,
    [property: JsonPropertyName("context")] OuroborosCaseContext? Context = null
);

public sealed record OuroborosCaseContext(
    [property: JsonPropertyName("assigned_runway")] string? AssignedRunway = null,
    [property: JsonPropertyName("destination")] string? Destination = null,
    [property: JsonPropertyName("airport_id")] string? AirportId = null
);

[JsonSerializable(typeof(OuroborosCorpus))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class OuroborosCorpusJsonContext : JsonSerializerContext;
