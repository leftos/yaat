using System.Text.Json.Serialization;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Root configuration from
/// https://configuration.vnas.vatsim.net/
/// </summary>
public class VnasConfig
{
    [JsonPropertyName("navDataSerial")]
    public long NavDataSerial { get; set; }

    [JsonPropertyName("navDataUrl")]
    public string NavDataUrl { get; set; } = "";

    [JsonPropertyName("aircraftSpecsSerial")]
    public long AircraftSpecsSerial { get; set; }

    [JsonPropertyName("aircraftSpecsUrl")]
    public string AircraftSpecsUrl { get; set; } = "";

    [JsonPropertyName("aircraftCwtSerial")]
    public long AircraftCwtSerial { get; set; }

    [JsonPropertyName("aircraftCwtUrl")]
    public string AircraftCwtUrl { get; set; } = "";
}
