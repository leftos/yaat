using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Faa;

/// <summary>
/// Static lookup for FAA Aircraft Characteristics Database records by ICAO type designator.
/// Initialized once at startup from the downloaded/cached FAA ACD data.
/// </summary>
public static class FaaAircraftDatabase
{
    private static readonly ILogger Log = SimLog.CreateLogger("FaaAircraftDatabase");

    private static Dictionary<string, FaaAircraftRecord> _lookup = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The override layer's gear-width corrections by type, re-applied on every load so a reload in either order keeps them.</summary>
    private static Dictionary<string, double> _mainGearWidthCorrections = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsInitialized => _lookup.Count > 0;

    public static int Count => _lookup.Count;

    public static void Initialize(Dictionary<string, FaaAircraftRecord> lookup)
    {
        _lookup = new Dictionary<string, FaaAircraftRecord>(lookup, StringComparer.OrdinalIgnoreCase);
        ApplyStoredCorrections();
    }

    /// <summary>
    /// Record the aircraft-profile override layer's ACD data corrections — currently the main-gear width, for a type
    /// whose ACD track is anomalous — and apply them to the loaded records. Order-independent with
    /// <see cref="Initialize"/>: the corrections are stored and re-applied on every load. A blank type code is skipped,
    /// a non-positive width is rejected, and a correction for a type the database has no record of its own for is
    /// warned about and left unapplied — never pushed onto a sibling.
    /// </summary>
    public static void ApplyOverrides(IReadOnlyList<AircraftProfileOverride> overrides)
    {
        var corrections = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (AircraftProfileOverride ov in overrides)
        {
            if (string.IsNullOrWhiteSpace(ov.TypeCode))
            {
                Log.LogWarning("Skipping FAA-data override with empty typeCode");
                continue;
            }

            if (ov.MainGearWidthFt is not { } gearWidthFt)
            {
                continue;
            }

            if (gearWidthFt <= 0)
            {
                Log.LogWarning("{Type}: mainGearWidthFt override {Value} is not a positive width; correction not applied", ov.TypeCode, gearWidthFt);
                continue;
            }

            corrections[ov.TypeCode.Trim().ToUpperInvariant()] = gearWidthFt;
        }

        _mainGearWidthCorrections = corrections;
        ApplyStoredCorrections();
    }

    /// <summary>
    /// Apply the stored gear-width corrections to the loaded records, warning for a correction with no record of its own
    /// to land on.
    /// </summary>
    private static void ApplyStoredCorrections()
    {
        if (_mainGearWidthCorrections.Count == 0)
        {
            return;
        }

        var corrected = new Dictionary<string, FaaAircraftRecord>(_lookup, StringComparer.OrdinalIgnoreCase);
        foreach ((string type, double gearWidthFt) in _mainGearWidthCorrections)
        {
            if (corrected.TryGetValue(type, out FaaAircraftRecord? record))
            {
                corrected[type] = record with { MainGearWidthFt = gearWidthFt };
                continue;
            }

            Log.LogWarning(
                "Override for {Type} sets mainGearWidthFt but the FAA aircraft characteristics database has no {Type} record; correction not applied",
                type,
                type
            );
        }

        _lookup = corrected;
    }

    /// <summary>
    /// Get the full FAA ACD record for an ICAO type designator.
    /// Strips prefixes like "H/" and suffixes like "/L" automatically.
    /// </summary>
    public static FaaAircraftRecord? Get(string? aircraftType)
    {
        if (string.IsNullOrEmpty(aircraftType))
        {
            return null;
        }

        string baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        if (_lookup.TryGetValue(baseType, out FaaAircraftRecord? record))
        {
            return record;
        }

        if (AircraftSiblingMap.TryResolve(baseType, out string? sibling) && _lookup.TryGetValue(sibling, out FaaAircraftRecord? sibRecord))
        {
            return sibRecord;
        }

        return null;
    }
}
