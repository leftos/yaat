using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// One ARTCC's full vNAS config, read from data-api's <c>/api/artccs/{ID}</c> into <c>{cacheRoot}/{ID}.json</c>, the
/// file the client's ArtccAirportResolver also writes. A disk copy younger than the TTL is read without a download; an
/// older one is refreshed, and kept for offline use whatever its age when the refresh fails or sends a body that is not
/// a readable config. Ids are trimmed and upper-cased, so every spelling reads one file and one URL, and an id that is
/// not plain letters and digits is never turned into a path. No network, disk or JSON failure throws: it is reported in
/// the <see cref="ArtccConfigLoad"/>; a cancel is thrown.
/// </summary>
/// <param name="http">The client to call data-api with; no request carries a token.</param>
/// <param name="cacheRoot">The folder holding one <c>{ID}.json</c> per ARTCC.</param>
/// <param name="utcNow">The clock a disk copy's age is counted on.</param>
public sealed class ArtccConfigSource(HttpClient http, string cacheRoot, Func<DateTime> utcNow)
{
    /// <summary>The data-api URL an ARTCC id is appended to.</summary>
    public const string DataApiBase = "https://data-api.vnas.vatsim.net/api/artccs";

    /// <summary>How long a disk copy of the ARTCC being edited is read without a download.</summary>
    public static readonly TimeSpan OwnConfigTtl = TimeSpan.FromHours(6);

    /// <summary>How long a disk copy of a neighbouring ARTCC's config is read without a download.</summary>
    public static readonly TimeSpan NeighbourConfigTtl = TimeSpan.FromDays(1);

    private static readonly ILogger Log = SimLog.CreateLogger<ArtccConfigSource>();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Each ARTCC's parsed config with the mtime of the file it was read from; an entry whose file has changed is re-read.
    private readonly Dictionary<string, (ArtccConfigRoot Config, DateTime WrittenUtc)> _parsed = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtccConfigSource"/> class over <c>cache/artcc</c> under the YAAT
    /// app-data folder and the wall clock.
    /// </summary>
    /// <param name="http">The client to call data-api with.</param>
    public ArtccConfigSource(HttpClient http)
        : this(http, YaatPaths.Combine("cache", "artcc"), () => DateTime.UtcNow) { }

    /// <summary>The id an ARTCC is loaded, cached and asked for under: trimmed and upper-cased.</summary>
    /// <param name="artccId">The id as given.</param>
    /// <returns>The normalised id, or null when it is blank or holds anything but ASCII letters and digits.</returns>
    public static string? NormalizeId(string? artccId)
    {
        string? id = artccId?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(id) || !id.All(char.IsAsciiLetterOrDigit) ? null : id;
    }

    /// <summary>Loads an ARTCC's config with the <see cref="OwnConfigTtl"/>; never blocks its caller on I/O or parsing.</summary>
    /// <param name="artccId">The ARTCC id, in any case.</param>
    /// <param name="ct">Cancels the load; a cancel is thrown.</param>
    /// <returns>The config and where it came from, or why there is none.</returns>
    public Task<ArtccConfigLoad> LoadAsync(string artccId, CancellationToken ct) => LoadAsync(artccId, OwnConfigTtl, ct);

    /// <summary>
    /// Loads an ARTCC's config: from disk when its copy is younger than <paramref name="diskTtl"/> and readable, else
    /// from data-api, falling back to the disk copy of any age when the refresh fails. LoadAsync never blocks its caller
    /// on I/O or parsing: the file is read asynchronously and the config is parsed on the thread pool.
    /// </summary>
    /// <param name="artccId">The ARTCC id, in any case.</param>
    /// <param name="diskTtl">How old a disk copy may be and still be read without a download.</param>
    /// <param name="ct">Cancels the load; a cancel is thrown.</param>
    /// <returns>The config and where it came from, or why there is none.</returns>
    public async Task<ArtccConfigLoad> LoadAsync(string artccId, TimeSpan diskTtl, CancellationToken ct)
    {
        if (NormalizeId(artccId) is not { } id)
        {
            string given = artccId?.Trim().ToUpperInvariant() ?? "";
            return new ArtccConfigLoad(given, null, ArtccConfigStatus.NotFound, $"'{given}' is not an ARTCC id", null);
        }

        string path = CachePathFor(id);
        if ((WrittenUtc(path) is { } written) && (utcNow() - written < diskTtl))
        {
            ArtccConfigRoot? cached = Memo(id, written) ?? (await ReadDiskCopyAsync(id, path, ct).ConfigureAwait(false))?.Config;
            if (cached is not null)
            {
                return new ArtccConfigLoad(id, cached, ArtccConfigStatus.Fresh, null, null);
            }
        }

        return await RefreshAsync(id, path, ct).ConfigureAwait(false);
    }

    /// <summary>The disk copy of an ARTCC's config, whatever its age, with no network call.</summary>
    /// <param name="artccId">The ARTCC id, in any case.</param>
    /// <param name="ct">Cancels the read; a cancel is thrown.</param>
    /// <returns>The config, or null when there is no readable copy.</returns>
    public async Task<ArtccConfigRoot?> TryGetCachedAsync(string artccId, CancellationToken ct)
    {
        if (NormalizeId(artccId) is not { } id)
        {
            return null;
        }

        string path = CachePathFor(id);
        if (WrittenUtc(path) is not { } written)
        {
            return null;
        }

        return Memo(id, written) ?? (await ReadDiskCopyAsync(id, path, ct).ConfigureAwait(false))?.Config;
    }

    private static DateTime? WrittenUtc(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    // The parse of a ~1 MB config runs on the thread pool, so no caller thread waits on it.
    private static Task<(ArtccConfigRoot? Config, string? Problem)> ParseAsync(string json, CancellationToken ct) => Task.Run(() => Parse(json), ct);

    private static (ArtccConfigRoot? Config, string? Problem) Parse(string json)
    {
        try
        {
            ArtccConfigRoot? config = JsonSerializer.Deserialize<ArtccConfigRoot>(json, JsonOptions);
            return config?.Facility is null ? (null, "it has no facility") : (config, null);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
    }

    // A normalised id only: NormalizeId admits letters and digits, so the file is always directly under the root.
    private string CachePathFor(string artccId) => Path.Combine(cacheRoot, $"{artccId}.json");

    private async Task<ArtccConfigLoad> RefreshAsync(string id, string path, CancellationToken ct)
    {
        // The last good copy, kept so a body that is not a config cannot replace it.
        DiskCopy? good = await ReadDiskCopyAsync(id, path, ct).ConfigureAwait(false);
        string url = $"{DataApiBase}/{id}";
        HttpCacheResult result;
        try
        {
            // A zero TTL makes the shared cache refetch whatever the copy's age; this class has already applied its own.
            result = await HttpFileCache
                .GetOrRefreshAsync(http, url, path, HttpCacheFreshness.AlwaysRefetch, TimeSpan.Zero, Log, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Refreshing the ARTCC config for {Id} in {Path} failed on disk", id, path);
            }

            string problem = $"the saved ARTCC config for {id} could not be refreshed: {ex.Message}";
            return good is not null
                ? new ArtccConfigLoad(id, good.Config, ArtccConfigStatus.FromCache, problem, good.WrittenUtc)
                : new ArtccConfigLoad(id, null, ArtccConfigStatus.Unreachable, problem, null);
        }

        if (result.Content is null)
        {
            return result.NotFound
                ? new ArtccConfigLoad(id, null, ArtccConfigStatus.NotFound, $"data-api has no ARTCC {id} (HTTP 404)", null)
                : new ArtccConfigLoad(id, null, ArtccConfigStatus.Unreachable, $"data-api could not be reached for {id}", null);
        }

        return await ClassifyAsync(id, path, result, good, ct).ConfigureAwait(false);
    }

    // The text HttpFileCache served: the new body when the refresh worked, the disk copy (stale) when it did not.
    private static async Task<ArtccConfigLoad> ClassifyAsync(string id, string path, HttpCacheResult result, DiskCopy? good, CancellationToken ct)
    {
        string? staleProblem = StaleProblem(id, result);
        if ((good is not null) && string.Equals(good.Text, result.Content, StringComparison.Ordinal))
        {
            // The body is the good copy's text: a failed refresh served it, or data-api sent it again unchanged.
            return Served(id, good.Config, staleProblem, good.WrittenUtc);
        }

        (ArtccConfigRoot? config, string? parseProblem) = await ParseAsync(result.Content ?? "", ct).ConfigureAwait(false);
        if (config is not null)
        {
            return Served(id, config, staleProblem, WrittenUtc(path));
        }

        string problem = $"the ARTCC config for {id} is not readable: {parseProblem}";
        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("The ARTCC config for {Id} is not readable: {Problem}", id, parseProblem);
        }

        return good is null
            ? new ArtccConfigLoad(id, null, ArtccConfigStatus.Unparseable, problem, null)
            : new ArtccConfigLoad(id, good.Config, ArtccConfigStatus.FromCache, problem + "; the saved copy is used", Restore(path, good));
    }

    // Why a served copy is the saved one rather than a new body; null when the refresh read a new body.
    private static string? StaleProblem(string id, HttpCacheResult result) =>
        result.NotFound ? $"data-api has no ARTCC {id} (HTTP 404); the saved copy is used"
        : result.RefreshFailed ? $"data-api could not be reached for {id}; the saved copy is used"
        : null;

    private static ArtccConfigLoad Served(string id, ArtccConfigRoot config, string? staleProblem, DateTime? writtenUtc) =>
        staleProblem is null
            ? new ArtccConfigLoad(id, config, ArtccConfigStatus.Fresh, null, null)
            : new ArtccConfigLoad(id, config, ArtccConfigStatus.FromCache, staleProblem, writtenUtc);

    // Puts the good copy back over a body that was not a config, with its own write time, so the offline copy survives
    // and the next load still counts it as old. Returns that write time.
    private static DateTime Restore(string path, DiskCopy good)
    {
        try
        {
            AtomicTextFile.WriteAllText(path, good.Text);
            File.SetLastWriteTimeUtc(path, good.WrittenUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Putting the saved ARTCC config back in {Path} failed", path);
            }
        }

        return good.WrittenUtc;
    }

    // Reads the file and its config, from the memo when the file is unchanged since it was parsed, else parsed off the
    // caller's thread and remembered. Null when there is no file or it is not a readable config.
    private async Task<DiskCopy?> ReadDiskCopyAsync(string id, string path, CancellationToken ct)
    {
        if (WrittenUtc(path) is not { } written)
        {
            return null;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "The saved ARTCC config {Path} could not be read", path);
            }

            return null;
        }

        ArtccConfigRoot? config = Memo(id, written) ?? (await ParseAsync(text, ct).ConfigureAwait(false)).Config;
        if (config is null)
        {
            return null;
        }

        lock (_lock)
        {
            _parsed[id] = (config, written);
        }

        return new DiskCopy(config, text, written);
    }

    private ArtccConfigRoot? Memo(string id, DateTime writtenUtc)
    {
        lock (_lock)
        {
            return _parsed.TryGetValue(id, out (ArtccConfigRoot Config, DateTime WrittenUtc) entry) && (entry.WrittenUtc == writtenUtc)
                ? entry.Config
                : null;
        }
    }

    private sealed record DiskCopy(ArtccConfigRoot Config, string Text, DateTime WrittenUtc);
}
