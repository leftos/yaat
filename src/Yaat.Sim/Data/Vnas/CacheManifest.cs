using System.Text.Json.Serialization;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Tracks which serial numbers are cached locally in
/// %LOCALAPPDATA%/yaat/cache/. Compared against the
/// live VNAS config to decide whether to re-download.
/// </summary>
public class CacheManifest
{
    [JsonPropertyName("navDataSerial")]
    public long NavDataSerial { get; set; }

    [JsonPropertyName("aircraftSpecsSerial")]
    public long AircraftSpecsSerial { get; set; }

    [JsonPropertyName("aircraftCwtSerial")]
    public long AircraftCwtSerial { get; set; }

    [JsonPropertyName("airacCycle")]
    public string AiracCycle { get; set; } = "";

    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; }
}
