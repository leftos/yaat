using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// The SWIM route history: the routes flight plans filed between two airports, kept as per-day buckets of counts
/// (<see cref="RouteHistoryRecord"/>). It records only an airline prefix and an aircraft type per flight, never a callsign;
/// trims by a window of days and by a byte budget, oldest day first; answers <see cref="RouteQuery"/> searches summed over
/// its days; and saves and reloads itself as one JSON object per line. Safe to use from several threads at once.
/// </summary>
public sealed partial class RouteHistoryIndex
{
    private static readonly ILogger Log = SimLog.CreateLogger<RouteHistoryIndex>();

    private static readonly JsonSerializerOptions LineOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<BucketKey, int> _counts = [];

    /// <summary>Gets the size in bytes of the index written by <see cref="WriteLines"/>: each line's UTF-8 bytes and its newline.</summary>
    public long SerializedBytes
    {
        get
        {
            lock (_gate)
            {
                return _counts.Sum(pair => LineBytes(pair.Key.ToRecord(pair.Value)));
            }
        }
    }

    /// <summary>
    /// The airline prefix of <paramref name="callsign"/>: its leading three letters, upper-cased, when a digit follows them
    /// (UAL123 gives UAL); else null, as for an N-number or a callsign spelled out (REACH1).
    /// </summary>
    public static string? AirlinePrefixOf(string? callsign) =>
        ((callsign is not null) && AirlineCallsign().IsMatch(callsign)) ? callsign[..3].ToUpperInvariant() : null;

    /// <summary>One record as a line of the saved index (a JSON object, no newline).</summary>
    public static string ToLine(RouteHistoryRecord record) => JsonSerializer.Serialize(record, LineOptions);

    /// <summary>
    /// Records one flight plan in its day's bucket. Nothing is recorded when the departure, the destination or the route is
    /// missing or empty, or the route holds nothing but those airports and "DCT". The callsign is reduced to its airline
    /// prefix (<see cref="AirlinePrefixOf"/>) and not kept.
    /// The index counts every call and filters nothing by flight rules. The caller records each flight once, by its GUFI,
    /// so an amended route on the same flight is not counted twice, and leaves VFR plans out.
    /// </summary>
    /// <param name="observation">The plan as observed; its time is UTC or unspecified, never local.</param>
    /// <returns>The increment recorded (its Count 1), for the caller to append as a line; null when nothing was recorded.</returns>
    /// <exception cref="ArgumentException">The observation's time is local.</exception>
    public RouteHistoryRecord? Add(RouteObservation observation)
    {
        if (observation.ObservedUtc.Kind == DateTimeKind.Local)
        {
            throw new ArgumentException("The observation time must be UTC, not local.", nameof(observation));
        }

        string? departure = Clean(observation.Departure);
        string? destination = Clean(observation.Destination);
        if ((departure is null) || (destination is null) || string.IsNullOrWhiteSpace(observation.RouteText))
        {
            return null;
        }

        string route = RouteFinder.NormalizedKey(observation.RouteText, new RouteQuery(departure, destination, [], [], null));
        if (route.Length == 0)
        {
            return null;
        }

        var increment = new RouteHistoryRecord(
            DateOnly.FromDateTime(observation.ObservedUtc),
            departure,
            destination,
            route,
            Clean(observation.AircraftType),
            AirlinePrefixOf(observation.Callsign),
            Count: 1
        );
        Merge(increment);
        return increment;
    }

    /// <summary>Adds a whole bucket, summing its count into an existing bucket with the same key.</summary>
    /// <exception cref="ArgumentException">
    /// The record is not valid: a count below 1, an empty departure, destination or route, or an airline prefix that is not
    /// three letters A to Z (a full callsign never enters the index).
    /// </exception>
    public void Merge(RouteHistoryRecord record)
    {
        if (!IsValid(record))
        {
            throw new ArgumentException("The route history record is not valid.", nameof(record));
        }

        var key = BucketKey.Of(record);
        lock (_gate)
        {
            _counts[key] = _counts.GetValueOrDefault(key) + record.Count;
        }
    }

    /// <summary>Every bucket, ordered by day, airports, route, type and prefix.</summary>
    public IReadOnlyList<RouteHistoryRecord> Records()
    {
        lock (_gate)
        {
            return
            [
                .. _counts
                    .Select(pair => pair.Key.ToRecord(pair.Value))
                    .OrderBy(r => r.Day)
                    .ThenBy(r => r.Departure, StringComparer.Ordinal)
                    .ThenBy(r => r.Destination, StringComparer.Ordinal)
                    .ThenBy(r => r.RouteText, StringComparer.Ordinal)
                    .ThenBy(r => r.AircraftType, StringComparer.Ordinal)
                    .ThenBy(r => r.AirlinePrefix, StringComparer.Ordinal),
            ];
        }
    }

    /// <summary>
    /// Drops the days older than the window, then, while <see cref="SerializedBytes"/> exceeds <paramref name="maxBytes"/>,
    /// the oldest remaining day. A day exactly <paramref name="maxAgeDays"/> before <paramref name="today"/> is kept, and so
    /// is the newest day, even alone over the budget.
    /// </summary>
    /// <param name="today">The current UTC day.</param>
    /// <param name="maxAgeDays">How many days before today the window reaches back.</param>
    /// <param name="maxBytes">The byte budget for the saved index.</param>
    /// <returns>The days dropped, oldest first, so the caller can delete their files.</returns>
    public IReadOnlyList<DateOnly> Trim(DateOnly today, int maxAgeDays, long maxBytes)
    {
        DateOnly cutoff = today.AddDays(-maxAgeDays);
        lock (_gate)
        {
            SortedDictionary<DateOnly, long> dayBytes = DayBytes();
            var dropped = new HashSet<DateOnly>();
            long total = dayBytes.Values.Sum();
            foreach ((DateOnly day, long bytes) in dayBytes.SkipLast(1))
            {
                if ((day >= cutoff) && (total <= maxBytes))
                {
                    break;
                }

                dropped.Add(day);
                total -= bytes;
            }

            if ((dayBytes.Count > 0) && (dayBytes.Keys.Last() < cutoff))
            {
                dropped.Add(dayBytes.Keys.Last());
            }

            foreach (BucketKey key in _counts.Keys.Where(key => dropped.Contains(key.Day)).ToList())
            {
                _counts.Remove(key);
            }

            return [.. dropped.Order()];
        }
    }

    /// <summary>
    /// The routes recorded between the query's airports, each summed over its days and buckets, most flown first. The query's
    /// fix must be a whole token of the route, its airline prefix equal the bucket's (case-insensitive), and its aircraft type
    /// equal the bucket's designator; its other fields are ignored. Each route carries its most-flown prefix and type.
    /// </summary>
    public List<FoundRoute> Query(RouteQuery query)
    {
        List<RouteHistoryRecord> matches;
        lock (_gate)
        {
            matches = [.. _counts.Select(pair => pair.Key.ToRecord(pair.Value)).Where(record => Matches(record, query))];
        }

        return
        [
            .. matches
                .GroupBy(record => record.RouteText, StringComparer.Ordinal)
                .Select(ToFoundRoute)
                .OrderByDescending(route => route.Count)
                .ThenBy(route => route.RouteText, StringComparer.Ordinal),
        ];
    }

    /// <summary>Writes every bucket as one line (see <see cref="ToLine"/>); every line ends in '\n', whatever the platform.</summary>
    public void WriteLines(TextWriter writer)
    {
        foreach (RouteHistoryRecord record in Records())
        {
            writer.Write(ToLine(record));
            writer.Write('\n');
        }
    }

    /// <summary>
    /// Merges every line <paramref name="reader"/> holds into the index (see <see cref="Merge"/>), so appended increments sum.
    /// A line that is not a readable record, such as a last line torn by a crash, is skipped with a logged warning.
    /// </summary>
    /// <returns>The number of lines merged.</returns>
    public int ReadLines(TextReader reader)
    {
        int merged = 0;
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            if (ParseLine(line, lineNumber) is { } record)
            {
                Merge(record);
                merged++;
            }
        }

        return merged;
    }

    private static RouteHistoryRecord? ParseLine(string line, int lineNumber)
    {
        try
        {
            RouteHistoryRecord? record = JsonSerializer.Deserialize<RouteHistoryRecord>(line, LineOptions);
            if (IsValid(record))
            {
                return record;
            }
        }
        catch (JsonException ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Route history line {Line} is not readable; skipping it", lineNumber);
            }

            return null;
        }

        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("Route history line {Line} is not a valid record; skipping it", lineNumber);
        }

        return null;
    }

    private static bool IsValid(RouteHistoryRecord? record) =>
        (record is not null)
        && (record.Count >= 1)
        && (!string.IsNullOrWhiteSpace(record.Departure))
        && (!string.IsNullOrWhiteSpace(record.Destination))
        && (!string.IsNullOrWhiteSpace(record.RouteText))
        && ((record.AirlinePrefix is null) || AirlinePrefixShape().IsMatch(record.AirlinePrefix));

    private static long LineBytes(RouteHistoryRecord record) => Encoding.UTF8.GetByteCount(ToLine(record)) + 1L;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static bool Matches(RouteHistoryRecord record, RouteQuery query) =>
        (string.Equals(record.Departure, query.Departure, StringComparison.OrdinalIgnoreCase))
        && (string.Equals(record.Destination, query.Destination, StringComparison.OrdinalIgnoreCase))
        && (HasToken(record.RouteText, query.Fix))
        && (FilterAdmits(record.AirlinePrefix, query.AirlinePrefix))
        && (FilterAdmits(record.AircraftType, query.AircraftType));

    /// <summary>Whether a bucket's value passes a filter: an unset filter admits anything, a set one only its own value.</summary>
    private static bool FilterAdmits(string? value, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || string.Equals(value, filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool HasToken(string routeText, string? fix) =>
        string.IsNullOrWhiteSpace(fix)
        || routeText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(fix.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>One route's row: its total count and the prefix-and-type pair with the most flights (ties: ordinal order).</summary>
    private static FoundRoute ToFoundRoute(IGrouping<string, RouteHistoryRecord> buckets)
    {
        (string? AirlinePrefix, string? AircraftType, int Count) sample = buckets
            .GroupBy(record => (record.AirlinePrefix, record.AircraftType))
            .Select(pair => (pair.Key.AirlinePrefix, pair.Key.AircraftType, Count: pair.Sum(record => record.Count)))
            .OrderByDescending(pair => pair.Count)
            .ThenBy(pair => pair.AirlinePrefix ?? "", StringComparer.Ordinal)
            .ThenBy(pair => pair.AircraftType ?? "", StringComparer.Ordinal)
            .First();
        return new FoundRoute(
            buckets.Key,
            buckets.Sum(record => record.Count),
            [RouteHistorySource.SourceName],
            Published: null,
            Preferred: null,
            new ObservedRouteSample(sample.AirlinePrefix, sample.AircraftType)
        );
    }

    private SortedDictionary<DateOnly, long> DayBytes()
    {
        var bytes = new SortedDictionary<DateOnly, long>();
        foreach ((BucketKey key, int count) in _counts)
        {
            bytes[key.Day] = bytes.GetValueOrDefault(key.Day) + LineBytes(key.ToRecord(count));
        }

        return bytes;
    }

    [GeneratedRegex("^[A-Z]{3}[0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AirlineCallsign();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex AirlinePrefixShape();

    private readonly record struct BucketKey(
        DateOnly Day,
        string Departure,
        string Destination,
        string RouteText,
        string? AircraftType,
        string? AirlinePrefix
    )
    {
        public static BucketKey Of(RouteHistoryRecord record) =>
            new(record.Day, record.Departure, record.Destination, record.RouteText, record.AircraftType, record.AirlinePrefix);

        public RouteHistoryRecord ToRecord(int count) => new(Day, Departure, Destination, RouteText, AircraftType, AirlinePrefix, count);
    }
}
