using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Services;

/// <summary>
/// Fetches live METARs and Winds Aloft from aviationweather.gov and assembles
/// a <see cref="WeatherProfile"/> compatible with the existing weather pipeline.
/// </summary>
public sealed class LiveWeatherService
{
    private const string AwcBase = "https://aviationweather.gov/api/data";

    private readonly ILogger _log = AppLog.CreateLogger<LiveWeatherService>();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Builds a WeatherProfile from live aviationweather.gov data.
    /// Returns null if both METAR and FD fetches fail.
    /// </summary>
    public async Task<WeatherProfile?> BuildLiveWeatherAsync(string artccId, IReadOnlyList<string> airportIds, IFixLookup fixes, bool includeTafs)
    {
        if (airportIds.Count == 0)
        {
            _log.LogWarning("No airport IDs provided for live weather");
            return null;
        }

        // Fetch METARs and FD winds in parallel
        var icaoIds = airportIds.Select(id => id.Length == 3 ? "K" + id : id).ToList();
        var metarTask = FetchMetarsAsync(icaoIds);
        var fdTask = FetchWindsAloftAsync(artccId);
        var tafTask = includeTafs ? FetchTafsAsync(icaoIds) : Task.FromResult<List<string>?>(null);

        await Task.WhenAll(metarTask, fdTask, tafTask);

        var metars = metarTask.Result;
        var fdStations = fdTask.Result;
        var tafs = tafTask.Result;

        if ((metars is null || metars.Count == 0) && (fdStations is null || fdStations.Count == 0))
        {
            _log.LogWarning("Both METAR and FD fetches failed or returned empty");
            return null;
        }

        // Build wind layers from FD data
        var windLayers = new List<WindLayer>();

        if (fdStations is { Count: > 0 })
        {
            windLayers.AddRange(BuildWindLayersFromFd(fdStations, artccId, fixes));
        }

        // Add surface wind from METARs
        if (metars is { Count: > 0 })
        {
            var surfaceWind = BuildSurfaceWindLayer(metars);
            if (surfaceWind is not null)
            {
                windLayers.Insert(0, surfaceWind);
            }
        }

        // Assemble METAR strings
        var allMetarStrings = new List<string>();
        if (metars is not null)
        {
            allMetarStrings.AddRange(metars.Select(m => m.RawOb).Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        // TAFs: extract only the initial forecast group (before any FM/BECMG/TEMPO)
        // to avoid pulling ceiling/visibility from future forecast periods
        if (tafs is not null)
        {
            var metarStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in allMetarStrings)
            {
                var parsed = MetarParser.Parse(raw);
                if (parsed is not null)
                {
                    metarStations.Add(parsed.StationId);
                }
            }

            foreach (var taf in tafs)
            {
                var initial = ExtractTafInitialGroup(taf);
                if (initial is not null)
                {
                    var parsed = MetarParser.Parse(initial);
                    if (parsed is not null && !metarStations.Contains(parsed.StationId))
                    {
                        allMetarStrings.Add(initial);
                    }
                }
            }
        }

        var timestamp = DateTime.UtcNow.ToString("HH:mm");
        return new WeatherProfile
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            ArtccId = artccId,
            Name = $"Live Weather ({timestamp}Z)",
            WindLayers = windLayers,
            Metars = allMetarStrings,
        };
    }

    private async Task<List<MetarJson>?> FetchMetarsAsync(IReadOnlyList<string> icaoIds)
    {
        try
        {
            var ids = string.Join(",", icaoIds);
            var url = $"{AwcBase}/metar?ids={ids}&format=json";
            var json = await _http.GetStringAsync(url);
            return JsonSerializer.Deserialize<List<MetarJson>>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch METARs");
            return null;
        }
    }

    private async Task<List<StationWinds>?> FetchWindsAloftAsync(string artccId)
    {
        var region = FdRegionMapping.GetRegion(artccId);
        if (region is null)
        {
            _log.LogWarning("No FD region mapping for ARTCC {Artcc}", artccId);
            return null;
        }

        try
        {
            var url = $"{AwcBase}/windtemp?region={region}&level=low&fcst=06";
            var text = await _http.GetStringAsync(url);
            return WindsAloftParser.Parse(text);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch winds aloft for region {Region}", region);
            return null;
        }
    }

    private async Task<List<string>?> FetchTafsAsync(IReadOnlyList<string> icaoIds)
    {
        try
        {
            var ids = string.Join(",", icaoIds);
            var url = $"{AwcBase}/taf?ids={ids}&format=json";
            var json = await _http.GetStringAsync(url);
            var tafs = JsonSerializer.Deserialize<List<TafJson>>(json, JsonOpts);
            return tafs?.Select(t => t.RawTaf).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch TAFs");
            return null;
        }
    }

    private List<WindLayer> BuildWindLayersFromFd(List<StationWinds> stations, string artccId, IFixLookup fixes)
    {
        // Get ARTCC center for magnetic declination
        var artccCenter = GetArtccCenter(artccId, fixes);
        double centerLat = artccCenter.Lat;
        double centerLon = artccCenter.Lon;

        // Collect all altitude levels present
        var allLevels = new HashSet<int>();
        foreach (var station in stations)
        {
            foreach (var wind in station.Winds)
            {
                allLevels.Add(wind.AltitudeFt);
            }
        }

        var layers = new List<WindLayer>();

        foreach (int level in allLevels.Order())
        {
            // Collect all non-light-variable reports at this level
            double sinSum = 0,
                cosSum = 0,
                speedSum = 0;
            int count = 0;

            foreach (var station in stations)
            {
                foreach (var wind in station.Winds)
                {
                    if (wind.AltitudeFt != level || wind.IsLightVariable)
                    {
                        continue;
                    }

                    double rad = wind.DirectionTrue * Math.PI / 180.0;
                    sinSum += Math.Sin(rad);
                    cosSum += Math.Cos(rad);
                    speedSum += wind.SpeedKts;
                    count++;
                }
            }

            if (count == 0)
            {
                // All reports were light/variable — add a calm layer
                layers.Add(
                    new WindLayer
                    {
                        Id = $"fd-{level}",
                        Altitude = level,
                        Direction = 0,
                        Speed = 0,
                    }
                );
                continue;
            }

            double avgDirection = Math.Atan2(sinSum / count, cosSum / count) * 180.0 / Math.PI;
            if (avgDirection < 0)
            {
                avgDirection += 360.0;
            }

            double avgSpeed = speedSum / count;

            // Convert true → magnetic
            double magDirection = MagneticDeclination.TrueToMagnetic(avgDirection, centerLat, centerLon);

            layers.Add(
                new WindLayer
                {
                    Id = $"fd-{level}",
                    Altitude = level,
                    Direction = Math.Round(magDirection, 1),
                    Speed = Math.Round(avgSpeed, 1),
                }
            );
        }

        return layers;
    }

    private static WindLayer? BuildSurfaceWindLayer(List<MetarJson> metars)
    {
        double sinSum = 0,
            cosSum = 0,
            speedSum = 0;
        int count = 0;

        foreach (var m in metars)
        {
            if (m.Wdir is not { } dir || m.Wspd is not { } spd)
            {
                continue;
            }

            if (dir == 0 && spd == 0)
            {
                continue;
            }

            double rad = dir * Math.PI / 180.0;
            sinSum += Math.Sin(rad);
            cosSum += Math.Cos(rad);
            speedSum += spd;
            count++;
        }

        if (count == 0)
        {
            return null;
        }

        double avgDir = Math.Atan2(sinSum / count, cosSum / count) * 180.0 / Math.PI;
        if (avgDir < 0)
        {
            avgDir += 360.0;
        }

        // METAR wind directions are already magnetic — no conversion needed
        return new WindLayer
        {
            Id = "surface",
            Altitude = 0,
            Direction = Math.Round(avgDir, 1),
            Speed = Math.Round(speedSum / count, 1),
        };
    }

    private static (double Lat, double Lon) GetArtccCenter(string artccId, IFixLookup fixes)
    {
        // Try to resolve the primary airport as a proxy for ARTCC center
        var primaryAirport = artccId.ToUpperInvariant() switch
        {
            "ZOA" => "SFO",
            "ZLA" => "LAX",
            "ZNY" => "JFK",
            "ZBW" => "BOS",
            "ZDC" => "IAD",
            "ZTL" => "ATL",
            "ZJX" => "JAX",
            "ZMA" => "MIA",
            "ZHU" => "IAH",
            "ZAU" => "ORD",
            "ZMP" => "MSP",
            "ZKC" => "MCI",
            "ZID" => "CVG",
            "ZCL" => "CLE",
            "ZFW" => "DFW",
            "ZME" => "MEM",
            "ZAB" => "ABQ",
            "ZDV" => "DEN",
            "ZLC" => "SLC",
            "ZSE" => "SEA",
            "ZAN" => "ANC",
            "ZOB" => "CLE",
            _ => null,
        };

        if (primaryAirport is not null)
        {
            var pos = fixes.GetFixPosition(primaryAirport);
            if (pos is not null)
            {
                return (pos.Value.Lat, pos.Value.Lon);
            }
        }

        // Fallback: middle of CONUS
        return (39.0, -98.0);
    }

    /// <summary>
    /// Extracts the initial forecast group from a raw TAF string.
    /// Truncates at the first FM/BECMG/TEMPO keyword to avoid pulling
    /// ceiling/visibility from future forecast periods.
    /// Returns a pseudo-METAR string parseable by MetarParser.
    /// </summary>
    public static string? ExtractTafInitialGroup(string rawTaf)
    {
        if (string.IsNullOrWhiteSpace(rawTaf))
        {
            return null;
        }

        // Find the end of the initial group: first occurrence of FM, BECMG, or TEMPO
        // that appears as a word boundary (space before it)
        var span = rawTaf.AsSpan();
        string[] groupMarkers = [" FM", " BECMG", " TEMPO"];
        int cutoff = rawTaf.Length;
        foreach (var marker in groupMarkers)
        {
            int idx = rawTaf.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0 && idx < cutoff)
            {
                cutoff = idx;
            }
        }

        var initial = rawTaf[..cutoff].Trim();
        if (initial.Length == 0)
        {
            return null;
        }

        // Strip the TAF prefix and validity period to produce a parseable string
        // TAF format: "TAF [AMD] KSFO 061720Z 0618/0724 28012KT P6SM FEW250"
        // We want: "KSFO 061720Z 28012KT P6SM FEW250"
        var result = initial;
        if (result.StartsWith("TAF ", StringComparison.OrdinalIgnoreCase))
        {
            result = result[4..].TrimStart();
        }
        if (result.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase))
        {
            result = result[4..].TrimStart();
        }

        // Remove the validity period (e.g., "0618/0724") — a token matching DDDD/DDDD
        var tokens = result.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        tokens.RemoveAll(t => t.Length >= 9 && t[4] == '/' && t.All(c => c is (>= '0' and <= '9') or '/'));

        return tokens.Count >= 2 ? string.Join(' ', tokens) : null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Minimal DTOs for aviationweather.gov JSON responses
    private sealed class MetarJson
    {
        public string RawOb { get; set; } = "";
        public int? Wdir { get; set; }
        public int? Wspd { get; set; }
    }

    private sealed class TafJson
    {
        public string RawTaf { get; set; } = "";
    }
}
