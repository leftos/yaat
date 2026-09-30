using System.Collections.Frozen;

namespace Yaat.Sim.Simulation.Eram;

/// <summary>
/// The room's entered weather reports: the text a <c>WX</c> entry (FAA ERAM EDSM SRS §C.2, Weather Data) gave a station,
/// one per station, the last entry winning. A station is keyed by its METAR id (<see cref="MetarParser.ToIcao"/>), so
/// <c>WX OAK</c> and <c>WX KOAK</c> name one report. The reports stay inside ERAM: no client broadcast carries them, and
/// physics never reads them.
///
/// <para>
/// Written only by <see cref="TryApply"/>, from a <see cref="RecordedEramRoomEntry"/> of the absolute shape
/// <c>WX {station} {hhmm} {text}</c>, stamped with the session-clock instant the entry was made. A report expires at the
/// first routine observation instant (:53) after its entry, by sim time (<see cref="EramWeatherReport.ExpiresAtUtc"/>),
/// so a replay reproduces it. The typed HHMM is carried for display only.
/// </para>
///
/// <para>
/// Copy-on-write, like <see cref="EramSectorMessages"/>: every write builds a new immutable dictionary and publishes it
/// with one volatile reference swap, so a reader outside the room gate sees one consistent state.
/// </para>
/// </summary>
public sealed class EramWeatherReports
{
    private const string Prefix = "WX ";

    /// <summary>The shortest station id field 13 allows (<c>aa(a)(a)(a)</c>).</summary>
    private const int MinStationLength = 2;

    /// <summary>The longest station id field 13 allows.</summary>
    private const int MaxStationLength = 5;

    private FrozenDictionary<string, EramWeatherReport> _reports = FrozenDictionary<string, EramWeatherReport>.Empty;

    /// <summary>Every stored report, current or not yet removed, in no particular order.</summary>
    public IReadOnlyList<EramWeatherReport> Reports => Volatile.Read(ref _reports).Values;

    /// <summary>
    /// Applies one recorded <c>WX {station} {hhmm} {text}</c> entry made at <paramref name="enteredAtUtc"/>, replacing
    /// any report the station held. Returns false, changing nothing, for any other shape: a station that is not 2–5
    /// letters, a time that is not a valid HHMM, an empty text, or text with leading or trailing blanks (the recorder
    /// writes it trimmed).
    /// </summary>
    public bool TryApply(string entry, DateTime enteredAtUtc)
    {
        if (!entry.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = entry[Prefix.Length..].Split(' ', 3);
        if ((parts.Length != 3) || !IsStation(parts[0]) || !IsHhmm(parts[1]))
        {
            return false;
        }

        string text = parts[2];
        if ((text.Length == 0) || (text.Trim().Length != text.Length))
        {
            return false;
        }

        string station = MetarParser.ToIcao(parts[0]).ToUpperInvariant();
        var next = new Dictionary<string, EramWeatherReport>(Volatile.Read(ref _reports), StringComparer.Ordinal)
        {
            [station] = new EramWeatherReport(station, parts[1], text, enteredAtUtc),
        };
        Publish(next);
        return true;
    }

    /// <summary>
    /// Removes every report whose <see cref="EramWeatherReport.ExpiresAtUtc"/> is at or before <paramref name="simUtc"/>;
    /// publishes nothing when none has expired.
    /// </summary>
    public void RemoveExpired(DateTime simUtc)
    {
        FrozenDictionary<string, EramWeatherReport> current = Volatile.Read(ref _reports);
        if (!current.Values.Any(r => r.ExpiresAtUtc <= simUtc))
        {
            return;
        }

        Publish(current.Where(kv => kv.Value.ExpiresAtUtc > simUtc).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
    }

    /// <summary>Replaces every report with <paramref name="reports"/>; an empty sequence leaves none.</summary>
    public void Replace(IEnumerable<EramWeatherReport> reports)
    {
        var next = new Dictionary<string, EramWeatherReport>(StringComparer.Ordinal);
        foreach (EramWeatherReport report in reports)
        {
            next[report.StationId] = report;
        }

        Publish(next);
    }

    /// <summary>Deletes every report.</summary>
    public void Clear() => Publish([]);

    /// <summary>A field 13 station id: 2–5 ASCII letters.</summary>
    private static bool IsStation(string station) =>
        (station.Length >= MinStationLength) && (station.Length <= MaxStationLength) && station.All(char.IsAsciiLetter);

    /// <summary>A field 35 time: four digits, hours at most 23 and minutes at most 59.</summary>
    private static bool IsHhmm(string time) =>
        (time.Length == 4) && time.All(char.IsAsciiDigit) && (TwoDigits(time, 0) <= 23) && (TwoDigits(time, 2) <= 59);

    /// <summary>The two ASCII digits of <paramref name="text"/> starting at <paramref name="start"/>, as a number.</summary>
    private static int TwoDigits(string text, int start) => ((text[start] - '0') * 10) + (text[start + 1] - '0');

    /// <summary>Publishes <paramref name="next"/> as the reports readers see.</summary>
    private void Publish(Dictionary<string, EramWeatherReport> next) => Volatile.Write(ref _reports, next.ToFrozenDictionary(StringComparer.Ordinal));
}

/// <summary>
/// One station's entered weather report: the station as its METAR id (a typed three-letter FAA id gains its <c>K</c>,
/// <see cref="MetarParser.ToIcao"/>), the typed HHMM, the text without the clear-weather symbol, and the session-clock
/// instant it was entered.
/// </summary>
public sealed record EramWeatherReport(string StationId, string ObservationTime, string Text, DateTime EnteredAtUtc)
{
    /// <summary>The first routine observation instant (:53, <see cref="MetarIssuer.RoutineObservationMinute"/>) strictly after the entry.</summary>
    public DateTime ExpiresAtUtc
    {
        get
        {
            var thisHour = new DateTime(
                EnteredAtUtc.Year,
                EnteredAtUtc.Month,
                EnteredAtUtc.Day,
                EnteredAtUtc.Hour,
                MetarIssuer.RoutineObservationMinute,
                0,
                DateTimeKind.Utc
            );
            return EnteredAtUtc < thisHour ? thisHour : thisHour.AddHours(1);
        }
    }
}
