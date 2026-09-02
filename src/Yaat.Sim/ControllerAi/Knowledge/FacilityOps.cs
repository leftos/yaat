using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Sim.Data;

namespace Yaat.Sim.ControllerAi.Knowledge;

/// <summary>
/// One facility's codified SOP knowledge (the layer above generic 7110.65 that a controller working the facility is
/// expected to know), as checked-in JSON. Every entry cites its SOP paragraph so a brain decision can be traced to the
/// line that produced it. Sections absent from a file simply do not apply; the brains fall back to their generic rules.
/// </summary>
public sealed class FacilityOps
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>The vNAS facility id (e.g. <c>OAK</c>).</summary>
    [JsonPropertyName("facilityId")]
    public required string FacilityId { get; init; }

    /// <summary>The airport the facility's runways belong to (e.g. <c>KOAK</c>).</summary>
    [JsonPropertyName("airportId")]
    public required string AirportId { get; init; }

    /// <summary>Provenance of the whole file — the SOP edition it was transcribed from.</summary>
    [JsonPropertyName("sourceDocument")]
    public required string SourceDocument { get; init; }

    [JsonPropertyName("runwayConfigurations")]
    public List<RunwayConfiguration> RunwayConfigurations { get; init; } = [];

    [JsonPropertyName("runwaySelection")]
    public RunwaySelectionPolicy? RunwaySelection { get; init; }

    [JsonPropertyName("runwayAssignmentPolicy")]
    public List<RunwayAssignmentRule> RunwayAssignmentPolicy { get; init; } = [];

    public RunwayConfiguration? Configuration(string name) =>
        RunwayConfigurations.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The named configuration's runway sets at <paramref name="airportId"/> (FAA or ICAO form), or null.</summary>
    public ConfigurationRunways? RunwaysAt(string configurationName, string airportId) => Configuration(configurationName)?.RunwaysAt(airportId);
}

/// <summary>
/// A named runway configuration: per-airport departure and arrival runway sets (a plan may span airports, e.g. OAK's
/// configurations name SFO's runways too).
/// </summary>
public sealed class RunwayConfiguration
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Airport id → runway sets.</summary>
    [JsonPropertyName("runways")]
    public required Dictionary<string, ConfigurationRunways> Runways { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    public ConfigurationRunways? RunwaysAt(string airportId) =>
        Runways
            .Where(kv => NavigationDatabase.AirportIdsMatch(kv.Key, airportId))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value)
            .FirstOrDefault();
}

public sealed class ConfigurationRunways
{
    [JsonPropertyName("departure")]
    public required List<string> Departure { get; init; }

    [JsonPropertyName("arrival")]
    public required List<string> Arrival { get; init; }
}

/// <summary>
/// How the facility picks its configuration: partner coupling first, then the calm-wind configuration, then the
/// wind-aligned candidate.
/// </summary>
public sealed class RunwaySelectionPolicy
{
    /// <summary>Reported wind below this (kt) uses <see cref="CalmConfiguration"/>.</summary>
    [JsonPropertyName("calmWindBelowKt")]
    public required double CalmWindBelowKt { get; init; }

    [JsonPropertyName("calmConfiguration")]
    public required string CalmConfiguration { get; init; }

    /// <summary>The configurations the wind may select, in the SOP's order (ties resolve to the earlier one, after the calm configuration).</summary>
    [JsonPropertyName("windAlignedCandidates")]
    public required List<string> WindAlignedCandidates { get; init; }

    [JsonPropertyName("partnerCouplings")]
    public List<PartnerCoupling> PartnerCouplings { get; init; } = [];

    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

/// <summary>"When the partner airport is in configuration X, use configuration Y."</summary>
public sealed class PartnerCoupling
{
    [JsonPropertyName("partnerAirportId")]
    public required string PartnerAirportId { get; init; }

    [JsonPropertyName("partnerConfiguration")]
    public required string PartnerConfiguration { get; init; }

    [JsonPropertyName("useConfiguration")]
    public required string UseConfiguration { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

/// <summary>
/// A runway-assignment constraint: aircraft matching <see cref="Applies"/> are kept off
/// (<see cref="RunwayAssignmentEffect.Exclude"/>) the listed runways — a request the assigner drops when nothing else fits.
/// </summary>
public sealed class RunwayAssignmentRule
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("runways")]
    public required List<string> Runways { get; init; }

    [JsonPropertyName("effect")]
    public required RunwayAssignmentEffect Effect { get; init; }

    [JsonPropertyName("applies")]
    public required AircraftPredicate Applies { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

/// <summary>Which aircraft a rule applies to; every stated field must match.</summary>
public sealed class AircraftPredicate
{
    [JsonPropertyName("category")]
    public AircraftCategory? Category { get; init; }

    [JsonPropertyName("sopClass")]
    public SopAircraftClass? SopClass { get; init; }

    [JsonPropertyName("mtowOverLb")]
    public double? MtowOverLb { get; init; }

    [JsonPropertyName("engineCount")]
    public int? EngineCount { get; init; }

    public bool IsEmpty => (Category is null) && (SopClass is null) && (MtowOverLb is null) && (EngineCount is null);
}

public enum RunwayAssignmentEffect
{
    Exclude,
}

/// <summary>
/// The P / T / J aircraft classes the ZOA-area SOPs define (NCT SOP 1-7): prop ≤ 179 kt, turboprop ≥ 180 kt, jet or
/// four-engine turboprop.
/// </summary>
public enum SopAircraftClass
{
    P,
    T,
    J,
}

/// <summary>Strict JSON for knowledge files: unknown properties and unknown enum values are errors, not silent skips.</summary>
public static class FacilityOpsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
