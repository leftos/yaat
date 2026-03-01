using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Downloads and caches FAA CIFP (Coded Instrument Flight
/// Procedures) data. One zip file per AIRAC cycle, extracted
/// to %LOCALAPPDATA%/yaat/cache/cifp/.
/// </summary>
public sealed class CifpDataService : IDisposable
{
    private const string CifpBaseUrl =
        "https://aeronav.faa.gov/Upload_313-d/cifp/";

    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly string _cacheDir;

    /// <summary>
    /// Path to the extracted FAACIFP18 text file, or null if
    /// download/extraction failed.
    /// </summary>
    public string? CifpFilePath { get; private set; }

    public CifpDataService(ILogger? logger = null)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        _cacheDir = Path.Combine(localAppData, "yaat", "cache", "cifp");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_cacheDir);

        var cycleId = AiracCycle.GetCurrentCycleId();
        var cachePath = Path.Combine(
            _cacheDir, $"FAACIFP18-{cycleId}");

        if (File.Exists(cachePath))
        {
            _logger?.LogInformation(
                "CIFP data cached for cycle {Cycle}", cycleId);
            CifpFilePath = cachePath;
            return;
        }

        var cycleDate = AiracCycle.GetCycleDate(cycleId);
        var dateStr = cycleDate.ToString("yyMMdd");
        var url = $"{CifpBaseUrl}CIFP_{dateStr}.zip";

        try
        {
            _logger?.LogInformation(
                "Downloading CIFP data from {Url}", url);

            var zipBytes = await _http.GetByteArrayAsync(url);

            using var zipStream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            var cifpEntry = archive.Entries.FirstOrDefault(
                e => e.Name.StartsWith("FAACIFP", StringComparison.Ordinal));

            if (cifpEntry is null)
            {
                _logger?.LogWarning(
                    "FAACIFP file not found in zip archive");
                return;
            }

            await using var entryStream = cifpEntry.Open();
            await using var fileStream = File.Create(cachePath);
            await entryStream.CopyToAsync(fileStream);

            CifpFilePath = cachePath;
            _logger?.LogInformation(
                "CIFP data cached for cycle {Cycle} ({Size:N0} bytes)",
                cycleId, new FileInfo(cachePath).Length);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to download CIFP data; "
                + "approach gate warnings will use defaults");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
