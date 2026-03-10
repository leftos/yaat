using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data.Faa;

/// <summary>
/// Downloads and caches FAA Aircraft Characteristics Database (ACD) data.
/// Extracts per-type approach speeds from the xlsx file, caches as JSON
/// per AIRAC cycle. Falls back to previous cycle cache on download failure.
/// </summary>
public sealed class FaaAircraftDataService : IDisposable
{
    private const string AcdUrl = "https://www.faa.gov/airports/engineering/aircraft_char_database/aircraft_data";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly string _cacheDir;

    public FaaAircraftDataService(ILogger? logger = null)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDir = Path.Combine(localAppData, "yaat", "cache", "faa-acd");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_cacheDir);

        var cycleId = AiracCycle.GetCurrentCycleId();
        var cachePath = Path.Combine(_cacheDir, $"faa-acd-{cycleId}.json");

        if (File.Exists(cachePath))
        {
            if (TryLoadFromJson(cachePath))
            {
                _logger?.LogInformation("FAA ACD data cached for cycle {Cycle}", cycleId);
                return;
            }
        }

        try
        {
            _logger?.LogInformation("Downloading FAA ACD data from {Url}", AcdUrl);

            var bytes = await _http.GetByteArrayAsync(AcdUrl);
            var lookup = ParseXlsx(bytes);

            if (lookup.Count > 0)
            {
                var json = JsonSerializer.Serialize(lookup);
                await File.WriteAllTextAsync(cachePath, json);

                AircraftApproachSpeed.Initialize(lookup);
                _logger?.LogInformation("FAA ACD data cached for cycle {Cycle}: {Count} approach speeds", cycleId, lookup.Count);
                return;
            }

            _logger?.LogWarning("FAA ACD xlsx contained no valid entries");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to download FAA ACD data; trying previous cache");
        }

        // Fallback: try any previous cycle's JSON
        if (TryLoadFallbackCache())
        {
            return;
        }

        _logger?.LogWarning("No FAA ACD data available; category defaults will be used");
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private bool TryLoadFromJson(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var lookup = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions);
            if (lookup is { Count: > 0 })
            {
                AircraftApproachSpeed.Initialize(lookup);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse FAA ACD cache at {Path}", path);
        }

        return false;
    }

    private bool TryLoadFallbackCache()
    {
        try
        {
            var files = Directory.GetFiles(_cacheDir, "faa-acd-*.json");
            // Sort descending to try newest first
            Array.Sort(files);
            Array.Reverse(files);

            foreach (var file in files)
            {
                if (TryLoadFromJson(file))
                {
                    _logger?.LogInformation("Loaded FAA ACD fallback from {File}", Path.GetFileName(file));
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to scan FAA ACD cache directory");
        }

        return false;
    }

    private Dictionary<string, int> ParseXlsx(byte[] xlsxBytes)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var stream = new MemoryStream(xlsxBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            // Read shared strings table
            var sharedStrings = ReadSharedStrings(archive);

            // Read sheet1 data
            var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml");
            if (sheetEntry is null)
            {
                _logger?.LogWarning("FAA ACD xlsx missing sheet1.xml");
                return result;
            }

            using var sheetStream = sheetEntry.Open();
            var sheetDoc = XDocument.Load(sheetStream);
            var ns = sheetDoc.Root?.Name.Namespace ?? XNamespace.None;

            var rows = sheetDoc.Descendants(ns + "row").ToList();
            if (rows.Count < 2)
            {
                return result;
            }

            // Find column indices from header row
            var headerRow = rows[0];
            int icaoCol = -1;
            int approachSpeedCol = -1;

            int colIdx = 0;
            foreach (var cell in headerRow.Elements(ns + "c"))
            {
                string cellValue = GetCellValue(cell, sharedStrings, ns);
                if (cellValue.Equals("ICAO_Code", StringComparison.OrdinalIgnoreCase))
                {
                    icaoCol = colIdx;
                }
                else if (cellValue.Equals("Approach_Speed_knot", StringComparison.OrdinalIgnoreCase))
                {
                    approachSpeedCol = colIdx;
                }

                colIdx++;
            }

            if (icaoCol < 0 || approachSpeedCol < 0)
            {
                _logger?.LogWarning("FAA ACD xlsx missing expected columns (ICAO_Code={Icao}, Approach_Speed_knot={Spd})", icaoCol, approachSpeedCol);
                return result;
            }

            // Parse data rows
            for (int i = 1; i < rows.Count; i++)
            {
                var cells = rows[i].Elements(ns + "c").ToList();

                string icaoCode = icaoCol < cells.Count ? GetCellValue(cells[icaoCol], sharedStrings, ns) : "";
                string speedStr = approachSpeedCol < cells.Count ? GetCellValue(cells[approachSpeedCol], sharedStrings, ns) : "";

                if (!string.IsNullOrWhiteSpace(icaoCode) && double.TryParse(speedStr, out double speedVal) && speedVal > 0)
                {
                    result.TryAdd(icaoCode.Trim(), (int)Math.Round(speedVal));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse FAA ACD xlsx");
        }

        return result;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var strings = new List<string>();

        var ssEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (ssEntry is null)
        {
            return strings;
        }

        using var ssStream = ssEntry.Open();
        var ssDoc = XDocument.Load(ssStream);
        var ns = ssDoc.Root?.Name.Namespace ?? XNamespace.None;

        foreach (var si in ssDoc.Descendants(ns + "si"))
        {
            // Concatenate all <t> elements within <si> (handles rich text)
            var text = string.Concat(si.Descendants(ns + "t").Select(t => t.Value));
            strings.Add(text);
        }

        return strings;
    }

    private static string GetCellValue(XElement cell, List<string> sharedStrings, XNamespace ns)
    {
        var typeAttr = cell.Attribute("t");
        var valueElement = cell.Element(ns + "v");

        if (valueElement is null)
        {
            return "";
        }

        string raw = valueElement.Value;

        // Type "s" means shared string reference
        if (typeAttr?.Value == "s" && int.TryParse(raw, out int ssIndex) && ssIndex < sharedStrings.Count)
        {
            return sharedStrings[ssIndex];
        }

        return raw;
    }
}
