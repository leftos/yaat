using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Speech;

/// <summary>
/// Static bidirectional map between airline ICAO codes and their telephony (callsign) designators.
/// Data source: OpenFlights airlines.dat (ODbL 1.0). See Speech/Data/LICENSE-OPENFLIGHTS.txt.
/// </summary>
/// <remarks>
/// ICAO → telephony is unique (each ICAO has exactly one telephony).
/// Telephony → ICAO can return multiple ICAOs: ~27 telephonies are shared by multiple airlines.
/// The caller disambiguates against active aircraft in the scenario — two airlines sharing a
/// telephony *and* the same flight number on the same scenario is vanishingly rare.
/// </remarks>
public static class AirlineTelephony
{
    private static readonly ILogger Log = SimLog.CreateLogger("AirlineTelephony");

    private static readonly Lazy<Data> _data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Number of ICAO entries loaded. Forces initialization.</summary>
    public static int Count => _data.Value.IcaoToTelephony.Count;

    /// <summary>
    /// Look up the telephony designator for an ICAO code.
    /// </summary>
    /// <param name="icao">3-letter ICAO airline code (case-insensitive).</param>
    /// <param name="telephony">Telephony designator in uppercase (e.g. "AMERICAN", "SPEEDBIRD").</param>
    public static bool TryGetTelephony(string icao, out string telephony)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            telephony = "";
            return false;
        }
        return _data.Value.IcaoToTelephony.TryGetValue(icao.ToUpperInvariant(), out telephony!);
    }

    /// <summary>
    /// Look up all ICAO codes that use a given telephony designator.
    /// Most telephonies map to a single ICAO; a handful are shared.
    /// </summary>
    /// <param name="telephony">Telephony designator (case-insensitive).</param>
    /// <param name="icaos">List of matching ICAOs, in insertion order. Empty if no match.</param>
    public static bool TryGetIcaos(string telephony, out IReadOnlyList<string> icaos)
    {
        if (string.IsNullOrWhiteSpace(telephony))
        {
            icaos = [];
            return false;
        }
        if (_data.Value.TelephonyToIcaos.TryGetValue(telephony.ToUpperInvariant(), out var list))
        {
            icaos = list;
            return true;
        }
        icaos = [];
        return false;
    }

    /// <summary>
    /// Reset the cached data. Intended for tests that override the data file location.
    /// </summary>
    internal static void ResetForTest()
    {
        // Lazy<T> can't be reset; tests that need isolation should use a separate test class
        // or rely on the immutable data being consistent. This method is a no-op placeholder
        // in case a mutable override is added later.
    }

    private static Data Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Speech", "Data", "airlines.tsv");
        if (!File.Exists(path))
        {
            Log.LogWarning("airlines.tsv not found at {Path}; airline telephony map will be empty", path);
            return new Data(new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<string>>());
        }

        var icaoToTelephony = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var telephonyToIcaos = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

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

            var icao = fields[0].Trim().ToUpperInvariant();
            var telephony = fields[1].Trim().ToUpperInvariant();
            if (icao.Length == 0 || telephony.Length == 0)
            {
                continue;
            }

            if (!icaoToTelephony.ContainsKey(icao))
            {
                icaoToTelephony[icao] = telephony;
            }

            if (!telephonyToIcaos.TryGetValue(telephony, out var icaos))
            {
                icaos = [];
                telephonyToIcaos[telephony] = icaos;
            }
            icaos.Add(icao);
        }

        Log.LogInformation(
            "Loaded {IcaoCount} airline ICAOs covering {TelephonyCount} distinct telephonies",
            icaoToTelephony.Count,
            telephonyToIcaos.Count
        );

        var frozenTelephony = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in telephonyToIcaos)
        {
            frozenTelephony[kvp.Key] = kvp.Value.AsReadOnly();
        }

        return new Data(icaoToTelephony, frozenTelephony);
    }

    private sealed record Data(
        IReadOnlyDictionary<string, string> IcaoToTelephony,
        IReadOnlyDictionary<string, IReadOnlyList<string>> TelephonyToIcaos
    );
}
