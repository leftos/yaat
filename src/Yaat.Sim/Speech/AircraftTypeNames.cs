using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Speech;

/// <summary>
/// Maps ICAO aircraft type designators (e.g. "C25C") to spoken manufacturer and family names
/// for use as alternate callsigns on the radio. Pilots substitute the type family or manufacturer
/// for "november" when referring to a GA aircraft — "Citation three four five" or "Cessna three
/// four five" instead of "November one two three four five".
/// </summary>
/// <remarks>
/// Data is pre-processed by <c>tools/refresh-aircraft-types.py</c> from vNAS AircraftSpecs.json
/// (which sources ICAO Doc 8643). The runtime just reads the small TSV output.
/// </remarks>
public static class AircraftTypeNames
{
    private static readonly ILogger Log = SimLog.CreateLogger("AircraftTypeNames");

    private static readonly Lazy<Data> _data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Number of ICAO type designators loaded. Forces initialization.</summary>
    public static int Count => _data.Value.Manufacturer.Count;

    /// <summary>
    /// Look up the manufacturer spoken word for an ICAO type designator (e.g. "C172" → "cessna",
    /// "BE20" → "beech"). Returns false if the type is unknown or has no manufacturer in the data.
    /// </summary>
    public static bool TryGetManufacturer(string? icaoType, out string manufacturer)
    {
        manufacturer = "";
        if (string.IsNullOrWhiteSpace(icaoType))
        {
            return false;
        }
        return _data.Value.Manufacturer.TryGetValue(icaoType.ToUpperInvariant(), out manufacturer!);
    }

    /// <summary>
    /// Look up the model family spoken word/bigram (e.g. "C172" → "skyhawk", "C25C" → "citation",
    /// "BE20" → "king air"). Returns false if no family word was extracted for this type.
    /// </summary>
    public static bool TryGetFamily(string? icaoType, out string family)
    {
        family = "";
        if (string.IsNullOrWhiteSpace(icaoType))
        {
            return false;
        }
        return _data.Value.Family.TryGetValue(icaoType.ToUpperInvariant(), out family!);
    }

    /// <summary>
    /// Return all distinct spoken names for an ICAO type — both manufacturer and family if
    /// available — in preference order. Empty list if the type is unknown. The caller uses
    /// this to seed Whisper's <c>initial_prompt</c> with every form pilots might use.
    /// </summary>
    public static IReadOnlyList<string> GetSpokenNames(string? icaoType)
    {
        if (string.IsNullOrWhiteSpace(icaoType))
        {
            return [];
        }
        var key = icaoType.ToUpperInvariant();
        var names = new List<string>(2);
        if (_data.Value.Family.TryGetValue(key, out var family) && family.Length > 0)
        {
            names.Add(family);
        }
        if (_data.Value.Manufacturer.TryGetValue(key, out var mfr) && mfr.Length > 0 && !names.Contains(mfr))
        {
            names.Add(mfr);
        }
        return names;
    }

    private static Data Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Speech", "Data", "aircraft-types.tsv");
        if (!File.Exists(path))
        {
            Log.LogWarning("aircraft-types.tsv not found at {Path}; type-name map will be empty", path);
            return new Data(new Dictionary<string, string>(), new Dictionary<string, string>());
        }

        var mfr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fam = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }
            var fields = line.Split('\t');
            if (fields.Length < 2)
            {
                continue;
            }
            var designator = fields[0].Trim().ToUpperInvariant();
            if (designator.Length == 0)
            {
                continue;
            }

            var manufacturer = fields[1].Trim();
            if (manufacturer.Length > 0)
            {
                mfr[designator] = manufacturer;
            }

            if (fields.Length >= 3)
            {
                var family = fields[2].Trim();
                if (family.Length > 0)
                {
                    fam[designator] = family;
                }
            }
        }

        Log.LogInformation("Loaded {ManufacturerCount} ICAO type manufacturers and {FamilyCount} families", mfr.Count, fam.Count);
        return new Data(mfr, fam);
    }

    private sealed record Data(IReadOnlyDictionary<string, string> Manufacturer, IReadOnlyDictionary<string, string> Family);
}
