using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// The vNAS ARTCCs, by id and name. A committed list ships with YAAT; <see cref="RefreshAsync"/> replaces it on demand
/// with data-api's <c>/api/artccs</c>, cached at <c>scenarios/artccs.json</c>. That endpoint returns every ARTCC's full
/// configuration (about 14 MB), so its body is read as a stream and only each ARTCC's <c>id</c> and
/// <c>facility.name</c> are kept. A failed refresh keeps the old cache and says why.
/// </summary>
/// <param name="cachePath">The cache file, or null for <c>scenarios/artccs.json</c> under the YAAT app-data folder.</param>
public sealed class VnasArtccList(string? cachePath)
{
    /// <summary>The data-api URL that lists every ARTCC with its full configuration.</summary>
    public const string Url = "https://data-api.vnas.vatsim.net/api/artccs";

    // The whole body must arrive in this time; it is far larger than the training reads.
    private const int RefreshTimeoutSeconds = 120;

    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(RefreshTimeoutSeconds);

    private static readonly ILogger Log = SimLog.CreateLogger<VnasArtccList>();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Initializes a new instance of the <see cref="VnasArtccList"/> class over <c>scenarios/artccs.json</c> under the
    /// YAAT app-data folder.
    /// </summary>
    public VnasArtccList()
        : this(cachePath: null) { }

    /// <summary>Gets the committed ARTCC list: the vNAS ids, with no names.</summary>
    public static IReadOnlyList<VnasArtcc> Committed { get; } =
    [
        .. new[]
        {
            "ZAB",
            "ZAN",
            "ZAU",
            "ZBW",
            "ZDC",
            "ZDV",
            "ZFW",
            "ZHN",
            "ZHU",
            "ZID",
            "ZJX",
            "ZKC",
            "ZLA",
            "ZLC",
            "ZMA",
            "ZME",
            "ZMP",
            "ZNY",
            "ZOA",
            "ZOB",
            "ZSE",
            "ZSU",
            "ZTL",
            "ZUA",
        }.Select(id => new VnasArtcc(id, "")),
    ];

    /// <summary>Gets the cache file a refresh writes.</summary>
    public string CachePath { get; } = cachePath ?? YaatPaths.Combine("scenarios", "artccs.json");

    /// <summary>The cached list when it is present and valid, else the committed one.</summary>
    /// <returns>The list, whether it came from the cache, and why the cache was not used when it is unreadable.</returns>
    public ArtccListLoad Load()
    {
        if (!File.Exists(CachePath))
        {
            return new ArtccListLoad(Committed, false, null);
        }

        string? problem = TryReadCache(out List<VnasArtcc>? cached);
        if (cached is not null)
        {
            return new ArtccListLoad(cached, true, null);
        }

        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("ARTCC list cache {Path} is not usable ({Problem}); using the committed list", CachePath, problem);
        }

        return new ArtccListLoad(Committed, false, $"{Path.GetFileName(CachePath)}: {problem}; the committed list is used");
    }

    /// <summary>
    /// Reads data-api's ARTCC list and writes its ids and names to the cache. On failure the cache is left as it was.
    /// </summary>
    /// <param name="http">The client to call data-api with; no request carries a token.</param>
    /// <param name="ct">Cancels the refresh; a cancel is thrown, not reported as a failure.</param>
    /// <returns>The ARTCCs read, or why the refresh failed.</returns>
    public async Task<VnasFetchResult<IReadOnlyList<VnasArtcc>>> RefreshAsync(HttpClient http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RefreshTimeout);
        try
        {
            using HttpResponseMessage response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Failed("data-api answered HTTP " + (int)response.StatusCode + " (" + response.ReasonPhrase + ")", null);
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
            List<VnasArtcc> artccs = UsableEntries(await ArtccStreamParser.ParseAsync(body, timeout.Token));
            if (artccs.Count == 0)
            {
                return Failed("data-api listed no ARTCCs", null);
            }

            StoreFiles.WriteAtomically(CachePath, JsonSerializer.Serialize(artccs, JsonOptions));
            return new VnasFetchResult<IReadOnlyList<VnasArtcc>>(artccs, null);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Failed("data-api did not send the ARTCC list within " + RefreshTimeoutSeconds + " s", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or UnauthorizedAccessException)
        {
            return Failed(ex is JsonException ? "data-api sent an unreadable ARTCC list: " + ex.Message : ex.Message, ex);
        }
    }

    // The read rule applied before a write: an entry whose id is not a valid folder id, or repeats an earlier one, is
    // dropped and logged, so the cache a refresh writes is one Load accepts.
    private static List<VnasArtcc> UsableEntries(List<VnasArtcc> artccs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<VnasArtcc>(artccs.Count);
        foreach (VnasArtcc artcc in artccs)
        {
            if (StoreFiles.IsValidId(artcc.Id) && seen.Add(artcc.Id))
            {
                kept.Add(artcc);
            }
            else if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning("data-api listed ARTCC id '{Id}', which is not a valid id or repeats an earlier one; skipped", artcc.Id);
            }
        }

        return kept;
    }

    private string? TryReadCache(out List<VnasArtcc>? artccs)
    {
        artccs = null;
        try
        {
            List<VnasArtcc>? read = JsonSerializer.Deserialize<List<VnasArtcc>>(File.ReadAllText(CachePath), JsonOptions);
            if ((read is null) || (read.Count == 0))
            {
                return "it holds no ARTCCs";
            }

            if (read.Any(a => (a is null) || !StoreFiles.IsValidId(a.Id) || (a.Name is null)))
            {
                return "an entry has no valid id or name";
            }

            artccs = read;
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private VnasFetchResult<IReadOnlyList<VnasArtcc>> Failed(string reason, Exception? ex)
    {
        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning(ex, "Refreshing the ARTCC list from {Url} failed: {Reason}; {Path} is kept", Url, reason, CachePath);
        }

        return new VnasFetchResult<IReadOnlyList<VnasArtcc>>(null, reason);
    }

    // Walks the /api/artccs array token by token over a growing window of the stream, keeping each top-level object's
    // "id" and its "facility" object's "name"; every other value is read past and dropped.
    private sealed class ArtccStreamParser
    {
        private const int InitialBufferBytes = 64 * 1024;

        private readonly List<VnasArtcc> _artccs = [];
        private JsonReaderState _state;
        private Pending _pending;
        private bool _inFacility;
        private string? _id;
        private string? _name;

        private enum Pending
        {
            None,
            Id,
            Facility,
            Name,
        }

        public static async Task<List<VnasArtcc>> ParseAsync(Stream stream, CancellationToken ct)
        {
            var parser = new ArtccStreamParser();
            byte[] buffer = new byte[InitialBufferBytes];
            int filled = 0;
            while (true)
            {
                if (filled == buffer.Length)
                {
                    // One token is larger than the window: grow it.
                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                int read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
                filled += read;
                int consumed = parser.Feed(buffer.AsSpan(0, filled), isFinalBlock: read == 0);
                buffer.AsSpan(consumed, filled - consumed).CopyTo(buffer);
                filled -= consumed;
                if (read == 0)
                {
                    return parser._artccs;
                }
            }
        }

        private int Feed(ReadOnlySpan<byte> data, bool isFinalBlock)
        {
            var reader = new Utf8JsonReader(data, isFinalBlock, _state);
            while (reader.Read())
            {
                OnToken(ref reader);
            }

            _state = reader.CurrentState;
            return (int)reader.BytesConsumed;
        }

        private void OnToken(ref Utf8JsonReader reader)
        {
            int depth = reader.CurrentDepth;
            if ((depth == 0) && (reader.TokenType is not (JsonTokenType.StartArray or JsonTokenType.EndArray)))
            {
                throw new JsonException("the ARTCC list is not a JSON array");
            }

            Pending pending = _pending;
            _pending = Pending.None;
            if (pending != Pending.None)
            {
                OnValue(ref reader, pending);
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _pending = PendingFor(ref reader, depth);
            }
            else
            {
                OnStructure(reader.TokenType, depth);
            }
        }

        private Pending PendingFor(ref Utf8JsonReader reader, int depth) =>
            (depth, _inFacility) switch
            {
                (2, false) when reader.ValueTextEquals("id"u8) => Pending.Id,
                (2, false) when reader.ValueTextEquals("facility"u8) => Pending.Facility,
                (3, true) when reader.ValueTextEquals("name"u8) => Pending.Name,
                _ => Pending.None,
            };

        private void OnValue(ref Utf8JsonReader reader, Pending pending)
        {
            JsonTokenType type = reader.TokenType;
            if ((pending == Pending.Facility) && (type == JsonTokenType.StartObject))
            {
                _inFacility = true;
            }
            else if (type == JsonTokenType.String)
            {
                string? text;
                try
                {
                    text = reader.GetString();
                }
                catch (InvalidOperationException ex)
                {
                    // GetString throws this for invalid UTF-8 inside the string token.
                    throw new JsonException("an ARTCC id or name is not valid UTF-8", ex);
                }

                _id = pending == Pending.Id ? text : _id;
                _name = pending == Pending.Name ? text : _name;
            }
            else
            {
                OnStructure(type, reader.CurrentDepth);
            }
        }

        private void OnStructure(JsonTokenType type, int depth)
        {
            switch ((type, depth))
            {
                case (JsonTokenType.EndObject, 2):
                    _inFacility = false;
                    break;
                case (JsonTokenType.StartObject, 1):
                    (_id, _name, _inFacility) = (null, null, false);
                    break;
                case (JsonTokenType.EndObject, 1) when _id is not null:
                    _artccs.Add(new VnasArtcc(_id, _name ?? ""));
                    break;
            }
        }
    }
}

/// <summary>One vNAS ARTCC.</summary>
/// <param name="Id">The ARTCC id, e.g. ZOA.</param>
/// <param name="Name">The ARTCC's name, e.g. Oakland ARTCC; empty when the committed list is in use.</param>
public sealed record VnasArtcc(string Id, string Name);

/// <summary>What <see cref="VnasArtccList.Load"/> found.</summary>
/// <param name="Artccs">The ARTCCs, from the cache or the committed list.</param>
/// <param name="FromCache">Whether the list came from the cache a refresh wrote.</param>
/// <param name="Problem">Why the cache was present but not used; null otherwise.</param>
public sealed record ArtccListLoad(IReadOnlyList<VnasArtcc> Artccs, bool FromCache, string? Problem);
