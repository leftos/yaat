using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

/// <summary>
/// On-disk shape of one entry in the sidecar's <c>exitCapacity</c> section: how many aircraft may stand at once on the
/// stretch of <see cref="Taxiway"/> between <see cref="Runway"/>'s exit hold-short and the parallel runway's hold-short
/// reached along it. <see cref="MaxAircraft"/> applies while the arrival and every occupant are at or below
/// <see cref="CwtThreshold"/> (a CWT letter at or after it, A heaviest to I lightest); <see cref="MaxAircraftAboveCwt"/>
/// applies as soon as one of them is above it or has no known CWT.
/// </summary>
public sealed class ExitCapacityEntry
{
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("runway")]
    public string Runway { get; set; } = "";

    [JsonPropertyName("taxiway")]
    public string Taxiway { get; set; } = "";

    [JsonPropertyName("maxAircraft")]
    public int MaxAircraft { get; set; }

    [JsonPropertyName("maxAircraftAboveCwt")]
    public int MaxAircraftAboveCwt { get; set; }

    [JsonPropertyName("cwtThreshold")]
    public string CwtThreshold { get; set; } = "";
}

/// <summary>
/// A parsed, validated exit-capacity rule produced by <see cref="AirportSidecarLoader"/>. <see cref="Runway"/> is the
/// zero-pad-normalized landing runway end, <see cref="Taxiway"/> and <see cref="CwtThreshold"/> are upper-cased.
/// Resolved against a concrete layout into an <see cref="Airport.ExitCapacitySegment"/> by
/// <see cref="Airport.ExitCapacityResolver"/>.
/// </summary>
public sealed record ExitCapacityRule(string Runway, string Taxiway, int MaxAircraft, int MaxAircraftAboveCwt, string CwtThreshold, string? Notes);
