using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data.Faa;

/// <summary>
/// Downloads and caches FAA Aircraft Characteristics Database (ACD) data.
/// Extracts all columns from the xlsx file, caches as JSON per AIRAC cycle.
/// Falls back to previous cycle cache on download failure.
/// </summary>
public sealed class FaaAircraftDataService : IDisposable
{
    private const string AcdUrl = "https://www.faa.gov/airports/engineering/aircraft_char_database/aircraft_data";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    private static readonly ILogger Log = SimLog.CreateLogger<FaaAircraftDataService>();

    private readonly HttpClient _http;
    private readonly string _cacheDir;

    public FaaAircraftDataService()
    {
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
                Log.LogInformation("FAA ACD data cached for cycle {Cycle}", cycleId);
                return;
            }
        }

        try
        {
            Log.LogInformation("Downloading FAA ACD data from {Url}", AcdUrl);

            var bytes = await _http.GetByteArrayAsync(AcdUrl);
            var records = ParseXlsx(bytes);

            if (records.Count > 0)
            {
                var json = JsonSerializer.Serialize(records, JsonOptions);
                await File.WriteAllTextAsync(cachePath, json);

                ApplyRecords(records);
                Log.LogInformation("FAA ACD data cached for cycle {Cycle}: {Count} aircraft types", cycleId, records.Count);
                return;
            }

            Log.LogWarning("FAA ACD xlsx contained no valid entries");
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to download FAA ACD data; trying previous cache");
        }

        // Fallback: try any previous cycle's JSON
        if (TryLoadFallbackCache())
        {
            return;
        }

        Log.LogWarning("No FAA ACD data available; category defaults will be used");
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private static void ApplyRecords(Dictionary<string, FaaAircraftRecord> records)
    {
        FaaAircraftDatabase.Initialize(records);

        // Also populate approach speed lookup used by approach phase logic
        var approachSpeeds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (icao, record) in records)
        {
            if (record.ApproachSpeedKnot is { } speed)
            {
                approachSpeeds[icao] = speed;
            }
        }

        AircraftApproachSpeed.Initialize(approachSpeeds);
    }

    private static bool TryLoadFromJson(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var records = JsonSerializer.Deserialize<Dictionary<string, FaaAircraftRecord>>(json, JsonOptions);
            if (records is { Count: > 0 })
            {
                ApplyRecords(records);
                return true;
            }
        }
        catch (JsonException)
        {
            Log.LogInformation("Deleting legacy-format FAA ACD cache at {Path}", path);
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to parse FAA ACD cache at {Path}", path);
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
                    Log.LogInformation("Loaded FAA ACD fallback from {File}", Path.GetFileName(file));
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to scan FAA ACD cache directory");
        }

        return false;
    }

    private static Dictionary<string, FaaAircraftRecord> ParseXlsx(byte[] xlsxBytes)
    {
        var result = new Dictionary<string, FaaAircraftRecord>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var stream = new MemoryStream(xlsxBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var sharedStrings = ReadSharedStrings(archive);

            var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml");
            if (sheetEntry is null)
            {
                Log.LogWarning("FAA ACD xlsx missing sheet1.xml");
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

            // Build column name → index mapping from header row
            var headerRow = rows[0];
            var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int colIdx = 0;
            foreach (var cell in headerRow.Elements(ns + "c"))
            {
                string cellValue = GetCellValue(cell, sharedStrings, ns);
                if (!string.IsNullOrWhiteSpace(cellValue))
                {
                    // Normalize TMFS_Operations_FY24 (year changes each release)
                    if (cellValue.StartsWith("TMFS_Operations_FY", StringComparison.OrdinalIgnoreCase))
                    {
                        colMap["TMFS_Operations_FY"] = colIdx;
                    }
                    else
                    {
                        colMap[cellValue] = colIdx;
                    }
                }

                colIdx++;
            }

            if (!colMap.ContainsKey("ICAO_Code"))
            {
                Log.LogWarning("FAA ACD xlsx missing ICAO_Code column");
                return result;
            }

            // Parse data rows
            for (int i = 1; i < rows.Count; i++)
            {
                var cells = rows[i].Elements(ns + "c").ToList();

                string icaoCode = GetCol(cells, colMap, "ICAO_Code", sharedStrings, ns);
                if (string.IsNullOrWhiteSpace(icaoCode))
                {
                    continue;
                }

                icaoCode = icaoCode.Trim();

                var record = new FaaAircraftRecord
                {
                    IcaoCode = icaoCode,
                    FaaDesignator = GetColOrNull(cells, colMap, "FAA_Designator", sharedStrings, ns),
                    Manufacturer = GetColOrNull(cells, colMap, "Manufacturer", sharedStrings, ns),
                    ModelFaa = GetColOrNull(cells, colMap, "Model_FAA", sharedStrings, ns),
                    ModelBada = GetColOrNull(cells, colMap, "Model_BADA", sharedStrings, ns),
                    PhysicalClassEngine = GetColOrNull(cells, colMap, "Physical_Class_Engine", sharedStrings, ns),
                    NumEngines = GetIntOrNull(cells, colMap, "Num_Engines", sharedStrings, ns),
                    Aac = GetColOrNull(cells, colMap, "AAC", sharedStrings, ns),
                    AacMinimum = GetColOrNull(cells, colMap, "AAC_minimum", sharedStrings, ns),
                    AacMaximum = GetColOrNull(cells, colMap, "AAC_maximum", sharedStrings, ns),
                    Adg = GetColOrNull(cells, colMap, "ADG", sharedStrings, ns),
                    Tdg = GetColOrNull(cells, colMap, "TDG", sharedStrings, ns),
                    ApproachSpeedKnot = GetIntOrNull(cells, colMap, "Approach_Speed_knot", sharedStrings, ns),
                    ApproachSpeedMinimumKnot = GetIntOrNull(cells, colMap, "Approach_Speed_minimum_knot", sharedStrings, ns),
                    ApproachSpeedMaximumKnot = GetIntOrNull(cells, colMap, "Approach_Speed_maximum_knot", sharedStrings, ns),
                    WingspanFtWithoutWinglets = GetDoubleOrNull(cells, colMap, "Wingspan_ft_without_winglets_sharklets", sharedStrings, ns),
                    WingspanFtWithWinglets = GetDoubleOrNull(cells, colMap, "Wingspan_ft_with_winglets_sharklets", sharedStrings, ns),
                    LengthFt = GetDoubleOrNull(cells, colMap, "Length_ft", sharedStrings, ns),
                    TailHeightAtOewFt = GetDoubleOrNull(cells, colMap, "Tail_Height_at_OEW_ft", sharedStrings, ns),
                    WheelbaseFt = GetDoubleOrNull(cells, colMap, "Wheelbase_ft", sharedStrings, ns),
                    CockpitToMainGearFt = GetDoubleOrNull(cells, colMap, "Cockpit_to_Main_Gear_ft", sharedStrings, ns),
                    MainGearWidthFt = GetDoubleOrNull(cells, colMap, "Main_Gear_Width_ft", sharedStrings, ns),
                    MtowLb = GetDoubleOrNull(cells, colMap, "MTOW_lb", sharedStrings, ns),
                    MalwLb = GetDoubleOrNull(cells, colMap, "MALW_lb", sharedStrings, ns),
                    MainGearConfig = GetColOrNull(cells, colMap, "Main_Gear_Config", sharedStrings, ns),
                    IcaoWtc = GetColOrNull(cells, colMap, "ICAO_WTC", sharedStrings, ns),
                    ParkingAreaFt2 = GetDoubleOrNull(cells, colMap, "Parking_Area_ft2", sharedStrings, ns),
                    Class = GetColOrNull(cells, colMap, "Class", sharedStrings, ns),
                    FaaWeight = GetColOrNull(cells, colMap, "FAA_Weight", sharedStrings, ns),
                    Cwt = GetColOrNull(cells, colMap, "CWT", sharedStrings, ns),
                    OneHalfWakeCategory = GetColOrNull(cells, colMap, "One_Half_Wake_Category", sharedStrings, ns),
                    TwoWakeCategoryAppxA = GetColOrNull(cells, colMap, "Two_Wake_Category_Appx_A", sharedStrings, ns),
                    TwoWakeCategoryAppxB = GetColOrNull(cells, colMap, "Two_Wake_Category_Appx_B", sharedStrings, ns),
                    RotorDiameterFt = GetDoubleOrNull(cells, colMap, "Rotor_Diameter_ft", sharedStrings, ns),
                    Srs = GetColOrNull(cells, colMap, "SRS", sharedStrings, ns),
                    Lahso = GetColOrNull(cells, colMap, "LAHSO", sharedStrings, ns),
                    FaaRegistry = GetColOrNull(cells, colMap, "FAA_Registry", sharedStrings, ns),
                    RegistrationCount = GetIntOrNull(cells, colMap, "Registration_Count", sharedStrings, ns),
                    TmfsOperationsFy = GetIntOrNull(cells, colMap, "TMFS_Operations_FY", sharedStrings, ns),
                    Remarks = GetColOrNull(cells, colMap, "Remarks", sharedStrings, ns),
                    LastUpdate = GetColOrNull(cells, colMap, "LastUpdate", sharedStrings, ns),
                };

                result.TryAdd(icaoCode, record);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to parse FAA ACD xlsx");
        }

        return result;
    }

    private static string GetCol(List<XElement> cells, Dictionary<string, int> colMap, string colName, List<string> sharedStrings, XNamespace ns)
    {
        if (!colMap.TryGetValue(colName, out int idx) || idx >= cells.Count)
        {
            return "";
        }

        return GetCellValue(cells[idx], sharedStrings, ns);
    }

    private static string? GetColOrNull(
        List<XElement> cells,
        Dictionary<string, int> colMap,
        string colName,
        List<string> sharedStrings,
        XNamespace ns
    )
    {
        var val = GetCol(cells, colMap, colName, sharedStrings, ns);
        if (string.IsNullOrWhiteSpace(val) || val.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return val.Trim();
    }

    private static int? GetIntOrNull(List<XElement> cells, Dictionary<string, int> colMap, string colName, List<string> sharedStrings, XNamespace ns)
    {
        var val = GetCol(cells, colMap, colName, sharedStrings, ns);
        return double.TryParse(val, out double d) && d > 0 ? (int)Math.Round(d) : null;
    }

    private static double? GetDoubleOrNull(
        List<XElement> cells,
        Dictionary<string, int> colMap,
        string colName,
        List<string> sharedStrings,
        XNamespace ns
    )
    {
        var val = GetCol(cells, colMap, colName, sharedStrings, ns);
        return double.TryParse(val, out double d) && d > 0 ? Math.Round(d, 1) : null;
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
