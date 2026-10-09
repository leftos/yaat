using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>What vNAS publishes now, for a check that also compares the GeoJSON and navdata halves of the key.</summary>
/// <param name="NavDataSerial">The serial of the navdata vNAS publishes now.</param>
/// <param name="GeoJsonMd5ByAirport">
/// The <see cref="GeoJsonMd5"/> of each airport's current vNAS map, by FAA id; null for an airport vNAS has no map for.
/// </param>
public sealed record PrecomputeOnlineFacts(long NavDataSerial, IReadOnlyDictionary<string, string?> GeoJsonMd5ByAirport);

/// <summary>How a listed airport's committed entry stands against what would be computed now.</summary>
public enum PrecomputeCheckStatus
{
    /// <summary>The entry's key matches on every field the check compares.</summary>
    Current,

    /// <summary>No entry file exists.</summary>
    Missing,

    /// <summary>The entry file cannot be read.</summary>
    Unreadable,

    /// <summary>The layout half is stale, and the push targets planned on it with it.</summary>
    LayoutStale,

    /// <summary>The layout is current; the push targets alone are stale.</summary>
    PushTargetsStale,
}

/// <summary>One listed airport's check: its FAA id, how its entry stands, and each key field that differs.</summary>
/// <param name="AirportId">The airport, FAA id.</param>
/// <param name="Status">How its entry stands.</param>
/// <param name="Reasons">One line per differing key field, or the missing path or read error; empty when current.</param>
public sealed record PrecomputeCheckResult(string AirportId, PrecomputeCheckStatus Status, IReadOnlyList<string> Reasons)
{
    /// <summary>The line the tool prints, <c>OAK: push targets stale: the sidecar hash differs ...</c>.</summary>
    /// <returns>The line.</returns>
    public string Describe() =>
        Reasons.Count == 0 ? $"{AirportId}: {StatusText(Status)}" : $"{AirportId}: {StatusText(Status)}: {string.Join("; ", Reasons)}";

    private static string StatusText(PrecomputeCheckStatus status) =>
        status switch
        {
            PrecomputeCheckStatus.Current => "current",
            PrecomputeCheckStatus.Missing => "missing",
            PrecomputeCheckStatus.Unreadable => "unreadable",
            PrecomputeCheckStatus.LayoutStale => "layout stale (push targets with it)",
            PrecomputeCheckStatus.PushTargetsStale => "push targets stale",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown precompute check status"),
        };
}

/// <summary>
/// The maintainer tool's work on the precompute cache, kept here so it is testable without the tool's network: checking
/// committed entries against the build-time source hashes, and refreshing the shipped design-group envelopes with the
/// pinned FAA test copy beside them. Building one airport's entry is <see cref="PrecomputeEntryBuild"/>'s, which is hashed.
/// </summary>
public static class PrecomputeBuilder
{
    private static readonly JsonSerializerOptions PinnedFaaOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Checks each airport's entry in <paramref name="store"/> without writing anything. Offline (<paramref name="online"/>
    /// null) it compares the layout format version, the two build-time source hashes and the sidecar hash; online it also
    /// compares the GeoJSON MD5 and the navdata serial with what vNAS publishes now.
    /// </summary>
    /// <param name="store">The store holding the committed entries.</param>
    /// <param name="airportIds">The airports to check, FAA or ICAO ids.</param>
    /// <param name="artccsDir">The ARTCCs data directory the current sidecar hash is read from.</param>
    /// <param name="online">What vNAS publishes now, or null for the offline check.</param>
    /// <returns>One result per airport, in the order given.</returns>
    public static IReadOnlyList<PrecomputeCheckResult> Check(
        PrecomputeStore store,
        IReadOnlyList<string> airportIds,
        string artccsDir,
        PrecomputeOnlineFacts? online
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(airportIds);
        return [.. airportIds.Select(id => CheckOne(store, AirportLayoutDownloader.ToFaaCode(id), artccsDir, online))];
    }

    /// <summary>
    /// Builds the envelopes from <paramref name="records"/> and writes them to <paramref name="envelopesPath"/>, and writes
    /// the records as the pinned FAA test copy to <paramref name="pinnedFaaPath"/>, so the shipped envelopes and the copy
    /// their test rebuilds them from move together. No record, or no envelope built, refuses and writes neither file.
    /// </summary>
    /// <param name="records">The FAA records by ICAO designator.</param>
    /// <param name="envelopesPath">The envelopes file to write.</param>
    /// <param name="pinnedFaaPath">The pinned FAA copy to write.</param>
    /// <param name="warnings">The build's warnings (<see cref="DesignGroupEnvelopes.Build"/>).</param>
    /// <returns>The envelopes written.</returns>
    /// <exception cref="InvalidOperationException">No record was given, or none builds an envelope.</exception>
    public static DesignGroupEnvelopes RefreshEnvelopes(
        IReadOnlyDictionary<string, FaaAircraftRecord> records,
        string envelopesPath,
        string pinnedFaaPath,
        out IReadOnlyList<string> warnings
    )
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            throw new InvalidOperationException($"No FAA aircraft records were loaded, so {envelopesPath} and {pinnedFaaPath} are left as they are");
        }

        var envelopes = DesignGroupEnvelopes.Build(records.Values, out warnings);
        if (envelopes.Envelopes.Count == 0)
        {
            throw new InvalidOperationException(
                $"None of the {records.Count} FAA aircraft records builds a design-group envelope, "
                    + $"so {envelopesPath} and {pinnedFaaPath} are left as they are"
            );
        }

        DesignGroupEnvelopes.Write(envelopesPath, envelopes);
        File.WriteAllText(pinnedFaaPath, PinnedFaaJson(records), new UTF8Encoding(false));
        return envelopes;
    }

    /// <summary>The pinned FAA copy's text: one JSON object by ICAO designator, ordinal, null fields left out, a final LF.</summary>
    /// <param name="records">The FAA records by ICAO designator.</param>
    /// <returns>The JSON text.</returns>
    public static string PinnedFaaJson(IReadOnlyDictionary<string, FaaAircraftRecord> records)
    {
        var sorted = new SortedDictionary<string, FaaAircraftRecord>(StringComparer.Ordinal);
        foreach ((string icao, FaaAircraftRecord record) in records)
        {
            sorted.Add(icao, record);
        }

        return JsonSerializer.Serialize(sorted, PinnedFaaOptions) + "\n";
    }

    private static PrecomputeCheckResult CheckOne(PrecomputeStore store, string faa, string artccsDir, PrecomputeOnlineFacts? online)
    {
        PrecomputeEntry? entry;
        try
        {
            if (!store.TryRead(faa, out entry))
            {
                return new PrecomputeCheckResult(faa, PrecomputeCheckStatus.Missing, [$"no entry at {store.PathFor(faa)}"]);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new PrecomputeCheckResult(faa, PrecomputeCheckStatus.Unreadable, [ex.Message]);
        }

        if (!string.Equals(entry.AirportId, faa, StringComparison.Ordinal))
        {
            return new PrecomputeCheckResult(
                faa,
                PrecomputeCheckStatus.Unreadable,
                [$"{store.PathFor(faa)} holds the entry of {entry.AirportId}, not {faa}"]
            );
        }

        PrecomputeKey committed = entry.Key;
        PrecomputeKey current = CurrentKeyFor(committed, faa, artccsDir, online);
        bool noVnasMap = (online is not null) && (online.GeoJsonMd5ByAirport.GetValueOrDefault(faa) is null);

        List<string> reasons = [.. LayoutReasons(committed, current, noVnasMap), .. PushReasons(committed, current)];
        PrecomputeCheckStatus status =
            !committed.LayoutMatches(current) ? PrecomputeCheckStatus.LayoutStale
            : !committed.PushTargetsMatch(current) ? PrecomputeCheckStatus.PushTargetsStale
            : PrecomputeCheckStatus.Current;
        return new PrecomputeCheckResult(faa, status, reasons);
    }

    /// <summary>
    /// The key the check compares <paramref name="committed"/> with: offline, the committed GeoJSON MD5 and navdata serial
    /// stand (they cannot be known without vNAS); online, vNAS's now, an empty MD5 for an airport it has no map for.
    /// </summary>
    private static PrecomputeKey CurrentKeyFor(PrecomputeKey committed, string faa, string artccsDir, PrecomputeOnlineFacts? online) =>
        PrecomputeKey.Current(
            online is null ? committed.GeoJsonMd5 : (online.GeoJsonMd5ByAirport.GetValueOrDefault(faa) ?? ""),
            online?.NavDataSerial ?? committed.NavDataSerial,
            AirportSidecarHash.For(artccsDir, faa)
        );

    private static IEnumerable<string> LayoutReasons(PrecomputeKey committed, PrecomputeKey current, bool noVnasMap)
    {
        if (committed.LayoutFormatVersion != current.LayoutFormatVersion)
        {
            yield return $"layout format version {committed.LayoutFormatVersion}, now {current.LayoutFormatVersion}";
        }

        if (committed.LayoutSourceHash != current.LayoutSourceHash)
        {
            yield return "the layout source hash differs (a layout source file changed)";
        }

        if (noVnasMap)
        {
            yield return "vNAS has no ground map for it now";
        }
        else if (committed.GeoJsonMd5 != current.GeoJsonMd5)
        {
            yield return $"the GeoJSON MD5 differs ({committed.GeoJsonMd5}, vNAS now {current.GeoJsonMd5})";
        }

        if (committed.NavDataSerial != current.NavDataSerial)
        {
            yield return $"NavData serial {committed.NavDataSerial}, vNAS now {current.NavDataSerial}";
        }
    }

    private static IEnumerable<string> PushReasons(PrecomputeKey committed, PrecomputeKey current)
    {
        if (committed.PushTargetSourceHash != current.PushTargetSourceHash)
        {
            yield return "the push-target source hash differs (a push-target source file or the envelopes changed)";
        }

        if (committed.PushSidecarHash != current.PushSidecarHash)
        {
            yield return "the sidecar hash differs (an airport sidecar changed)";
        }
    }
}

/// <summary>
/// The limits of a run that computes several airports: at most <see cref="AirportsInFlight"/> airports at once, so as many
/// parsed layouts in memory, and at most <see cref="MaxDegreeOfParallelism"/> compute tasks across all of them. Every
/// airport's own work (fetching, keying, waiting) runs on the default pool and holds no compute slot; its compute, the
/// layout parse and the stand loop's calling thread, runs as a task on one shared limited scheduler
/// (<see cref="RunAsync{T}"/>), and the stand loop puts its other bodies on the same scheduler (<see cref="StandLoop"/>),
/// so the cap holds across airports.
/// </summary>
public sealed class PrecomputeRunLimits
{
    private readonly TaskScheduler _scheduler;

    /// <summary>Creates the limits.</summary>
    /// <param name="maxDegreeOfParallelism">The most compute tasks running at once, across every airport.</param>
    /// <param name="airportsInFlight">The most airports worked on at once.</param>
    public PrecomputeRunLimits(int maxDegreeOfParallelism, int airportsInFlight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(airportsInFlight, 1);
        MaxDegreeOfParallelism = maxDegreeOfParallelism;
        AirportsInFlight = airportsInFlight;
        _scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, maxDegreeOfParallelism).ConcurrentScheduler;
    }

    /// <summary>The most compute tasks running at once, across every airport.</summary>
    public int MaxDegreeOfParallelism { get; }

    /// <summary>The most airports worked on at once.</summary>
    public int AirportsInFlight { get; }

    /// <summary>
    /// The stand loop's options (<see cref="PushTargetPlanner.PlanInParallel{TItem, TResult}"/>): its bodies on the shared
    /// limited scheduler, at most <see cref="MaxDegreeOfParallelism"/> of them. A new instance on each read, since
    /// <see cref="ParallelOptions"/> is mutable and a caller's change must not reach another airport's loop.
    /// </summary>
    public ParallelOptions StandLoop => new() { MaxDegreeOfParallelism = MaxDegreeOfParallelism, TaskScheduler = _scheduler };

    /// <summary>Runs <paramref name="body"/> for each airport, at most <see cref="AirportsInFlight"/> at once.</summary>
    /// <param name="airportIds">The airports.</param>
    /// <param name="body">The work on one airport; its compute goes through <see cref="RunAsync{T}"/>.</param>
    /// <returns>A task that completes when every airport's body has.</returns>
    public Task ForEachAirportAsync(IEnumerable<string> airportIds, Func<string, CancellationToken, ValueTask> body) =>
        Parallel.ForEachAsync(airportIds, new ParallelOptions { MaxDegreeOfParallelism = AirportsInFlight }, body);

    /// <summary>
    /// Runs <paramref name="compute"/> as a task on the shared limited scheduler, for the caller to await from the default
    /// pool; a stand loop inside it is given <see cref="StandLoop"/>.
    /// </summary>
    /// <typeparam name="T">The result's type.</typeparam>
    /// <param name="compute">The compute, such as <see cref="PrecomputeEntryBuild.Build"/> with a context carrying <see cref="StandLoop"/>.</param>
    /// <returns>Its result.</returns>
    public Task<T> RunAsync<T>(Func<T> compute) =>
        Task.Factory.StartNew(compute, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _scheduler);
}
