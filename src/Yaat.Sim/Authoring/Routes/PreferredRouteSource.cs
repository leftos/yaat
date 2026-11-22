using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// The FAA's published preferred IFR routes, from the NASR 28-day subscription's <c>PFR_CSV.zip</c> for the current AIRAC
/// cycle. The file's <c>PFR_BASE.csv</c> is downloaded on the first query and cached as <c>&lt;cycle&gt;.csv</c>, one file
/// per cycle. When the download fails, a file cached for one of the two previous cycles is served with a failure message;
/// with none, the source answers no routes and says the routes are unavailable. Whatever a cycle's load gave (routes, a
/// stale copy or a failure) is kept for the life of the source, so a failed download is not retried until a new source is built.
/// A row answers only its own origin-to-destination pair, matched by FAA airport id; an airport with no FAA id gets no
/// routes and no failure. A tower en route (TEC) row is offered to an aircraft of a known type only when the row admits
/// its class (see <see cref="TecAdmits"/>); an unknown or missing type gets every row. A route text listed on several rows
/// is offered once, with the first admitted row's notes. Each route's count is 0 and its
/// <see cref="FoundRoute.Preferred"/> holds the FAA's notes.
/// </summary>
/// <param name="http">The client to download with, or null for one of its own (30 s timeout).</param>
/// <param name="cacheDir">Where each cycle's file is cached, or null for <c>cache/prefroutes</c> under the YAAT app-data folder.</param>
/// <param name="navDb">The navdata that resolves each query airport to its FAA id.</param>
/// <param name="currentCycleId">The current AIRAC cycle id ("YYNN"), or null for <see cref="AiracCycle.GetCurrentCycleId"/>.</param>
public sealed partial class PreferredRouteSource(HttpClient? http, string? cacheDir, NavigationDatabase navDb, Func<string>? currentCycleId)
    : IRouteSource,
        IDisposable
{
    private const string UrlPrefix = "https://nfdc.faa.gov/webContent/28DaySub/extra/";

    private const string UnreadableMessage = "Preferred routes file unreadable";

    private const string TecType = "TEC";

    private const int MaxStaleCycles = 2;

    private static readonly ILogger Log = SimLog.CreateLogger<PreferredRouteSource>();

    private static readonly AircraftCategory[] NonJets = [AircraftCategory.Turboprop, AircraftCategory.Piston, AircraftCategory.Helicopter];

    /// <summary>The AIRCRAFT texts on TEC rows that clearly limit the route to a class, and the classes each admits.</summary>
    private static readonly Dictionary<string, AircraftCategory[]> AircraftPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JETS ONLY"] = [AircraftCategory.Jet],
        ["JETS"] = [AircraftCategory.Jet],
        ["TURBOJETS ONLY"] = [AircraftCategory.Jet],
        ["PROPS ONLY"] = NonJets,
        ["PROPS"] = NonJets,
        ["NON-JETS ONLY"] = NonJets,
        ["NON-TURBOJETS ONLY"] = NonJets,
        ["NON-JETS/NON-TURBOPROPS ONLY"] = [AircraftCategory.Piston, AircraftCategory.Helicopter],
        ["PISTON ONLY"] = [AircraftCategory.Piston],
    };

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly bool _ownsHttp = http is null;
    private readonly string _cacheDir = cacheDir ?? YaatPaths.Combine("cache", "prefroutes");
    private readonly NavigationDatabase _navDb = navDb;
    private readonly Func<string> _currentCycleId = currentCycleId ?? AiracCycle.GetCurrentCycleId;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private (string CycleId, PreferredRouteTable? Table, string? Failure)? _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreferredRouteSource"/> class with its own HTTP client, the user cache
    /// and the current AIRAC cycle.
    /// </summary>
    /// <param name="navDb">The navdata that resolves each query airport to its FAA id.</param>
    public PreferredRouteSource(NavigationDatabase navDb)
        : this(http: null, cacheDir: null, navDb, currentCycleId: null) { }

    /// <inheritdoc />
    public string Name => "preferred";

    /// <inheritdoc />
    public async Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_navDb.TryResolveFaaId(query.Departure, out string origin) || !_navDb.TryResolveFaaId(query.Destination, out string destination))
        {
            return new RouteSourceResult([], FailureMessage: null);
        }

        (PreferredRouteTable? table, string? failure) = await LoadAsync(ct);
        return new RouteSourceResult(table is null ? [] : SelectRoutes(table, origin, destination, query.AircraftType), failure);
    }

    /// <summary>Disposes the HTTP client when this source created it.</summary>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }

        _loadLock.Dispose();
    }

    /// <summary>The NASR PFR CSV zip for <paramref name="cycleId"/>, e.g. <c>…/26_Nov_2026_PFR_CSV.zip</c> for 2612.</summary>
    internal static string DownloadUrl(string cycleId) =>
        UrlPrefix + AiracCycle.GetCycleDate(cycleId).ToString("dd_MMM_yyyy", CultureInfo.InvariantCulture) + "_PFR_CSV.zip";

    /// <summary>
    /// The category of <paramref name="aircraftType"/> when the aircraft tables know the type, else null (no filter).
    /// <see cref="AircraftCategorization.Categorize"/> alone would call an unknown type a jet.
    /// </summary>
    internal static AircraftCategory? KnownCategory(string? aircraftType) =>
        (!string.IsNullOrWhiteSpace(aircraftType) && AircraftCategorization.IsKnownType(aircraftType))
            ? AircraftCategorization.Categorize(aircraftType)
            : null;

    /// <summary>
    /// Whether a row may be offered to an aircraft of <paramref name="category"/> (null: unknown, every row). Non-TEC rows
    /// always are. A TEC row is decided by the first of: its ALT_DESCRIP aircraft-class letters (a jet needs J, a turboprop
    /// M or P, a piston or helicopter P or Q; the numbers are not read); its AIRCRAFT text when that clearly names a class;
    /// else the AIM 4-1-19 default that TEC is for non-turbojets. A turboprop is admitted by M or P only, so a slow
    /// turboprop misses Q-only rows: there is no cruise-speed data to place it under 190 KT.
    /// </summary>
    internal static bool TecAdmits(PreferredRouteInfo info, AircraftCategory? category)
    {
        if ((category is not { } aircraft) || (info.Type != TecType))
        {
            return true;
        }

        string code = info.Altitude?.Trim().ToUpperInvariant() ?? "";
        return TecClassCode().IsMatch(code) ? ClassCodeAdmits(code, aircraft) : PhraseAdmits(info.Aircraft, aircraft);
    }

    private static bool ClassCodeAdmits(string code, AircraftCategory aircraft) =>
        aircraft switch
        {
            AircraftCategory.Jet => code.Contains('J', StringComparison.Ordinal),
            AircraftCategory.Turboprop => (code.Contains('M', StringComparison.Ordinal)) || (code.Contains('P', StringComparison.Ordinal)),
            _ => (code.Contains('P', StringComparison.Ordinal)) || (code.Contains('Q', StringComparison.Ordinal)),
        };

    /// <summary>A recognised AIRCRAFT phrase's classes; any other text (or none) leaves the AIM default: no jets.</summary>
    private static bool PhraseAdmits(string? aircraftText, AircraftCategory aircraft) =>
        AircraftPhrases.TryGetValue(aircraftText?.Trim() ?? "", out AircraftCategory[]? admitted)
            ? admitted.Contains(aircraft)
            : (aircraft != AircraftCategory.Jet);

    private List<FoundRoute> SelectRoutes(PreferredRouteTable table, string origin, string destination, string? aircraftType)
    {
        AircraftCategory? category = KnownCategory(aircraftType);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new List<FoundRoute>();
        foreach (PreferredRouteRow row in table.For(origin, destination))
        {
            if ((!TecAdmits(row.Info, category)) || (!seen.Add(row.RouteText)))
            {
                continue;
            }

            routes.Add(new FoundRoute(row.RouteText, Count: 0, [Name], Published: null, row.Info));
        }

        return routes;
    }

    /// <summary>The current cycle's load: done on the first query, then kept, whatever it gave.</summary>
    private async Task<(PreferredRouteTable? Table, string? Failure)> LoadAsync(CancellationToken ct)
    {
        string cycleId = _currentCycleId();
        await _loadLock.WaitAsync(ct);
        try
        {
            if ((_loaded is { } loaded) && (loaded.CycleId == cycleId))
            {
                return (loaded.Table, loaded.Failure);
            }

            (PreferredRouteTable? table, string? failure) = await LoadCycleAsync(cycleId, ct);
            _loaded = (cycleId, table, failure);
            return (table, failure);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>The cycle's cached or downloaded table, else a recent stale one with its failure, else none with the failure.</summary>
    private async Task<(PreferredRouteTable? Table, string? Failure)> LoadCycleAsync(string cycleId, CancellationToken ct)
    {
        string path = CachePath(cycleId);
        string? reason = File.Exists(path) ? null : await DownloadAsync(cycleId, path, ct);
        if (reason is not null)
        {
            return LoadStale(cycleId, reason);
        }

        PreferredRouteTable? table = ReadCached(path);
        return table is null ? (null, UnreadableMessage) : (table, null);
    }

    /// <summary>The most recent readable cached cycle within the limit (unreadable ones are deleted on the way), else unavailable.</summary>
    private (PreferredRouteTable? Table, string? Failure) LoadStale(string cycleId, string reason)
    {
        foreach (string staleId in StaleCycles(cycleId))
        {
            if (ReadCached(CachePath(staleId)) is { } table)
            {
                return (table, $"Preferred routes from cycle {staleId}; the current file could not be downloaded.");
            }
        }

        return (null, $"Preferred routes unavailable: {reason}");
    }

    /// <summary>The cached cycles one or two cycles before <paramref name="cycleId"/>, most recent first.</summary>
    private List<string> StaleCycles(string cycleId)
    {
        if (!Directory.Exists(_cacheDir))
        {
            return [];
        }

        return
        [
            .. Directory
                .EnumerateFiles(_cacheDir, "*.csv")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(id => (id.Length == 4) && id.All(char.IsAsciiDigit))
                .Select(id => (Id: id, Back: AiracCycle.CyclesBetween(id, cycleId)))
                .Where(c => (c.Back >= 1) && (c.Back <= MaxStaleCycles))
                .OrderBy(c => c.Back)
                .Select(c => c.Id),
        ];
    }

    private string CachePath(string cycleId) => Path.Combine(_cacheDir, cycleId + ".csv");

    /// <summary>Parses a cached file; one whose every row is malformed is deleted and gives null.</summary>
    private static PreferredRouteTable? ReadCached(string path)
    {
        PreferredRouteTable table;
        using (StreamReader reader = File.OpenText(path))
        {
            table = PreferredRouteParser.Parse(reader);
        }

        if (!table.IsUnreadable)
        {
            return table;
        }

        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("Preferred routes file {Path} has no readable row; deleting it", path);
        }

        File.Delete(path);
        return null;
    }

    /// <summary>Downloads the cycle's zip and caches its <c>PFR_BASE.csv</c> at <paramref name="path"/>; returns why it failed, or null.</summary>
    private async Task<string?> DownloadAsync(string cycleId, string path, CancellationToken ct)
    {
        if (_ownsHttp && IsDownloadSkipped(Environment.GetEnvironmentVariable("YAAT_SKIP_CIFP_DOWNLOAD")))
        {
            return "downloads are turned off (YAAT_SKIP_CIFP_DOWNLOAD)";
        }

        string url = DownloadUrl(cycleId);
        LogDownloading(cycleId, url);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(url, ct);
            return FailureReason(response) ?? CacheBaseCsv(await response.Content.ReadAsByteArrayAsync(ct), path);
        }
        catch (Exception ex) when (IsDownloadFailure(ex, ct))
        {
            return DescribeFailure(ex, url);
        }
    }

    private static void LogDownloading(string cycleId, string url)
    {
        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation("Downloading preferred routes for AIRAC cycle {Cycle} from {Url}", cycleId, url);
        }
    }

    /// <summary>Why a reply cannot be used, or null for a success status.</summary>
    private static string? FailureReason(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "the FAA file for this cycle was not found (HTTP 404)";
        }

        return response.IsSuccessStatusCode ? null : $"the FAA answered HTTP {(int)response.StatusCode}";
    }

    private static string DescribeFailure(Exception ex, string url)
    {
        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning(ex, "Downloading preferred routes from {Url} failed", url);
        }

        return (ex is TaskCanceledException) ? "the download timed out" : ex.Message;
    }

    private static bool IsDownloadFailure(Exception ex, CancellationToken ct) =>
        (ex is HttpRequestException) || ((ex is TaskCanceledException) && !ct.IsCancellationRequested);

    /// <summary>Writes the zip's <c>PFR_BASE.csv</c> to <paramref name="path"/> atomically; returns why it could not, or null.</summary>
    private static string? CacheBaseCsv(byte[] zipBytes, string path)
    {
        string? csv;
        try
        {
            csv = ReadBaseCsv(zipBytes);
        }
        catch (InvalidDataException)
        {
            return "the FAA download is not a zip file";
        }

        if (csv is null)
        {
            return "the FAA download holds no PFR_BASE.csv";
        }

        try
        {
            AtomicTextFile.WriteAllText(path, csv);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Saving the preferred routes download to {Path} failed", path);
            }

            return $"couldn't save the download: {ex.Message}";
        }
    }

    /// <summary>The text of the zip's <c>PFR_BASE.csv</c>, or null when it holds none.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a zip archive.</exception>
    private static string? ReadBaseCsv(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, "PFR_BASE.csv", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>The test and CI guard <c>CifpPathResolver</c> obeys: no FAA download when YAAT_SKIP_CIFP_DOWNLOAD is "1" or "true".</summary>
    /// <param name="value">The variable's value, null when it is not set.</param>
    internal static bool IsDownloadSkipped(string? value) =>
        (string.Equals(value, "1", StringComparison.Ordinal)) || (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>ALT_DESCRIP written as aircraft-class letter groups, each optionally followed by a number: "J30", "JM120PQ100", "PQ".</summary>
    [GeneratedRegex(@"^(?:[JMPQ]+\d+)*[JMPQ]+\d*$")]
    private static partial Regex TecClassCode();
}
