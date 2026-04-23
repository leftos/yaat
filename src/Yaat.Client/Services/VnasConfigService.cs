using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Services;

/// <summary>
/// Fetches and caches the vNAS configuration JSON to provide base URLs
/// for tower cab images and video maps. Falls back to cached config
/// if the network fetch fails.
/// </summary>
public sealed class VnasConfigService : IDisposable
{
    private const string ConfigUrl = "https://configuration.vnas.vatsim.net/";

    private static readonly string CachePath = YaatPaths.Combine("cache", "vnas-config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger _log = AppLog.CreateLogger<VnasConfigService>();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string VideoMapBaseUrl { get; private set; } = "";
    public string TowerCabImagesBaseUrl { get; private set; } = "";
    public bool IsInitialized { get; private set; }

    public async Task InitializeAsync()
    {
        VnasConfig? config = null;

        try
        {
            var json = await _http.GetStringAsync(ConfigUrl);
            config = JsonSerializer.Deserialize<VnasConfig>(json, JsonOptions);

            if (config is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, json);
                _log.LogDebug("vNAS config fetched and cached");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch vNAS config; trying cache");
        }

        if (config is null && File.Exists(CachePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(CachePath);
                config = JsonSerializer.Deserialize<VnasConfig>(json, JsonOptions);
                _log.LogDebug("Loaded vNAS config from cache");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load cached vNAS config");
            }
        }

        if (config is not null)
        {
            VideoMapBaseUrl = config.VideoMapBaseUrl;
            TowerCabImagesBaseUrl = config.TowerCabImagesBaseUrl;
            IsInitialized = true;

            _log.LogInformation(
                "vNAS config: VideoMapBaseUrl={VideoMapBase}, TowerCabImagesBaseUrl={TowerCabBase}",
                VideoMapBaseUrl,
                TowerCabImagesBaseUrl
            );
        }
        else
        {
            _log.LogWarning("No vNAS config available; tower cab layers will be unavailable");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
