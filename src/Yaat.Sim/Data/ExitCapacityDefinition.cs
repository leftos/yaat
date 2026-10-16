using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// On-disk shape of one entry in the sidecar's <c>exitCapacity</c> section, in one of two forms. The one-runway form
/// (<see cref="Runway"/> and <see cref="Taxiway"/>) caps the stretch of the taxiway between that runway's exit
/// hold-short and the parallel runway's hold-short reached along it, for arrivals on that runway. The crossovers form
/// (<see cref="Runways"/>, a pair of parallel runways, and <see cref="Crossovers"/>, a taxiway list) caps each listed
/// crossover between the two runways as one stretch shared by both runways' arrivals, counted in both directions.
/// <see cref="MaxAircraft"/> applies while the arrival and every occupant are at or below <see cref="CwtThreshold"/> (a
/// CWT letter at or after it, A heaviest to I lightest); <see cref="MaxAircraftAboveCwt"/> applies as soon as one of
/// them is above it or has no known CWT.
/// </summary>
public sealed class ExitCapacityEntry
{
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("runway")]
    public string Runway { get; set; } = "";

    [JsonPropertyName("taxiway")]
    public string Taxiway { get; set; } = "";

    [JsonPropertyName("runways")]
    public List<string> Runways { get; set; } = [];

    [JsonPropertyName("crossovers")]
    public List<string> Crossovers { get; set; } = [];

    [JsonPropertyName("maxAircraft")]
    public int MaxAircraft { get; set; }

    [JsonPropertyName("maxAircraftAboveCwt")]
    public int MaxAircraftAboveCwt { get; set; }

    [JsonPropertyName("cwtThreshold")]
    public string CwtThreshold { get; set; } = "";
}

/// <summary>
/// A parsed, validated exit-capacity rule produced by <see cref="AirportSidecarLoader"/>, one per taxiway.
/// <see cref="Runway"/> is the zero-pad-normalized landing runway end, <see cref="Taxiway"/> and
/// <see cref="CwtThreshold"/> are upper-cased. <see cref="CrossoverRunway"/> is null for a one-runway entry; for a
/// crossovers entry it is the pair's second runway (normalized), and the rule governs arrivals on both runways over
/// one shared stretch. Resolved against a concrete layout into an <see cref="Airport.ExitCapacitySegment"/> by
/// <see cref="Airport.ExitCapacityResolver"/>.
/// </summary>
public sealed record ExitCapacityRule(
    string Runway,
    string? CrossoverRunway,
    string Taxiway,
    int MaxAircraft,
    int MaxAircraftAboveCwt,
    string CwtThreshold,
    string? Notes
)
{
    /// <summary>True for a rule from a crossovers entry, shared by <see cref="Runway"/> and <see cref="CrossoverRunway"/>.</summary>
    public bool IsCrossover => CrossoverRunway is not null;
}
