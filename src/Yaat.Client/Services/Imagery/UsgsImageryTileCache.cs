using System.Globalization;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.Logging;
using Yaat.Sim;
using Yaat.Sim.Authoring.Imagery;

namespace Yaat.Client.Services.Imagery;

/// <summary>One decoded USGS tile and its edges in degrees, ready to draw.</summary>
/// <param name="Key">The tile.</param>
/// <param name="Image">The decoded raster image; never disposed once handed out, since a render snapshot may hold it.</param>
/// <param name="South">South edge latitude.</param>
/// <param name="West">West edge longitude.</param>
/// <param name="North">North edge latitude.</param>
/// <param name="East">East edge longitude.</param>
public sealed record ImageryTile(TileKey Key, SKImage Image, double South, double West, double North, double East);

/// <summary>How far a wanted set of tiles is from being drawn.</summary>
public enum ImageryTileState
{
    /// <summary>Every wanted tile is drawn, or has no imagery to draw.</summary>
    AllShown,

    /// <summary>Some wanted tile is still being read or downloaded.</summary>
    Loading,

    /// <summary>Some wanted tile failed for a network reason and has no disk copy.</summary>
    Offline,
}

/// <summary>
/// The USGS National Map NAIPPlus tiles under the Airport Editor: one <c>exportImage</c> request per Web Mercator tile, at
/// most four in flight, a disk cache with a 90 day freshness window, a memory cache of decoded images, and negative
/// entries for tiles that failed (60 s after a network failure, for the cache's lifetime when the service has no image).
/// </summary>
/// <remarks>
/// The UI thread asks with <see cref="GetVisible"/>; tiles arrive on fetch tasks and are announced through the post
/// seam, at most one announcement pending at a time. Turning the cache off or disposing it cancels every fetch, and
/// nothing a cancelled fetch got is published or announced.
/// </remarks>
public sealed class UsgsImageryTileCache : IDisposable
{
    /// <summary>The credit line USGS asks for wherever its tiles are shown.</summary>
    public const string Credit = "Map services and data available from U.S. Geological Survey, National Geospatial Program.";

    internal const string UserAgent = "yaat-imagery";

    private const int MaxInFlight = 4;

    private const int ExtraHeld = 64;

    private const string ExportImageUrl = "https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer/exportImage";

    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan NetworkRetryAfter = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)];

    // One HTTP client and one request gate for every editor the app opens, never disposed: closing a window cannot
    // dispose them under another window's fetch.
    private static readonly Lazy<HttpClient> SharedHttp = new(() => CreateHttpClient(new SocketsHttpHandler()));

    private static readonly SemaphoreSlim SharedGate = new(MaxInFlight, MaxInFlight);

    private readonly ILogger _log = AppLog.CreateLogger<UsgsImageryTileCache>();
    private readonly Lock _lock = new();
    private readonly Transport _transport;
    private readonly UsgsTileStore _store;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Action _onTileArrived;
    private readonly Dictionary<TileKey, Fetch> _inFlight = [];
    private readonly Dictionary<TileKey, LinkedListNode<ImageryTile>> _held = [];
    private readonly LinkedList<ImageryTile> _lru = new();
    private readonly Dictionary<TileKey, NegativeEntry> _negative = [];
    private HashSet<TileKey> _wanted = [];
    private CancellationTokenSource _session = new();
    private int _generation;
    private bool _active = true;
    private bool _disposed;
    private int _running;
    private TaskCompletionSource? _idle;
    private int _postPending;

    /// <summary>Creates a cache with its own HTTP client over <paramref name="handler"/> and its own request gate.</summary>
    /// <param name="handler">The HTTP handler the cache's client sends through; disposed with the cache.</param>
    /// <param name="root">The disk cache's root folder.</param>
    /// <param name="time">The clock for file ages, negative entries, retry waits and attempt timeouts.</param>
    /// <param name="post">Runs an announcement on the UI thread (production: <c>Dispatcher.UIThread.Post</c>).</param>
    /// <param name="onTileArrived">Called through <paramref name="post"/> when a wanted tile arrived or failed.</param>
    public UsgsImageryTileCache(HttpMessageHandler handler, string root, TimeProvider time, Action<Action> post, Action onTileArrived)
        : this(new Transport(CreateHttpClient(handler), new SemaphoreSlim(MaxInFlight, MaxInFlight), OwnsHttp: true), root, time, post, onTileArrived)
    { }

    /// <summary>Creates a cache over a given client and request gate (tests hold the gate to drive its permits).</summary>
    internal UsgsImageryTileCache(Transport transport, string root, TimeProvider time, Action<Action> post, Action onTileArrived)
    {
        _transport = transport;
        _store = new UsgsTileStore(root, time);
        _time = time;
        _post = post;
        _onTileArrived = onTileArrived;
    }

    /// <summary>Called when a failed attempt's retry wait has been scheduled, before it is awaited (tests advance the clock).</summary>
    internal Action<TileKey>? RetryScheduled { get; set; }

    /// <summary>Completes when no fetch is running.</summary>
    internal Task Idle
    {
        get
        {
            lock (_lock)
            {
                if (_running == 0)
                {
                    return Task.CompletedTask;
                }

                _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _idle.Task;
            }
        }
    }

    /// <summary>The number of negative entries, expired ones included.</summary>
    internal int NegativeCount
    {
        get
        {
            lock (_lock)
            {
                return _negative.Count;
            }
        }
    }

    /// <summary>
    /// The app's cache: the shared HTTP client and request gate, the per-user disk cache, the system clock, and
    /// announcements posted to the UI thread.
    /// </summary>
    public static UsgsImageryTileCache CreateForApp(Action onTileArrived) =>
        new(
            new Transport(SharedHttp.Value, SharedGate, OwnsHttp: false),
            YaatPaths.Combine("cache", "imagery", "usgs-naipplus"),
            TimeProvider.System,
            action => Dispatcher.UIThread.Post(action),
            onTileArrived
        );

    /// <summary>The <c>exportImage</c> request for the tile.</summary>
    internal static string TileUrl(TileKey key)
    {
        MercatorBox box = WebMercatorTile.Box(key);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ExportImageUrl}?bbox={box.XMin:F2},{box.YMin:F2},{box.XMax:F2},{box.YMax:F2}&bboxSR=3857&imageSR=3857&size=256,256&format=jpg&f=image"
        );
    }

    /// <summary>
    /// The decoded tiles held for <paramref name="wanted"/>, and fetches started for the missing ones that have no live
    /// negative entry. A changed wanted set cancels the fetches still waiting to send whose tile is outside it and the
    /// one-tile ring around it. Never calls back inline.
    /// </summary>
    public IReadOnlyList<ImageryTile> GetVisible(IReadOnlyList<TileKey> wanted)
    {
        var tiles = new List<ImageryTile>(wanted.Count);
        var toCancel = new List<CancellationTokenSource>();
        var toStart = new List<(TileKey Key, Fetch Fetch)>();
        lock (_lock)
        {
            if (_disposed)
            {
                return tiles;
            }

            UpdateWanted(wanted, toCancel);
            DateTimeOffset now = _time.GetUtcNow();
            foreach (TileKey key in wanted)
            {
                if (_held.TryGetValue(key, out LinkedListNode<ImageryTile>? node))
                {
                    Touch(node);
                    tiles.Add(node.Value);
                }
                else if ((_active) && (TryBeginFetch(key, now) is { } fetch))
                {
                    toStart.Add((key, fetch));
                }
            }
        }

        CancelQuietly(toCancel);
        foreach ((TileKey key, Fetch fetch) in toStart)
        {
            _ = Task.Run(() => RunFetchAsync(key, fetch));
        }
        return tiles;
    }

    /// <summary>
    /// Turns fetching on or off. Off cancels every fetch, and nothing they got is published or announced; the tiles
    /// already held stay held.
    /// </summary>
    public void SetActive(bool active)
    {
        CancellationTokenSource? cancelled = null;
        lock (_lock)
        {
            if ((_disposed) || (_active == active))
            {
                return;
            }

            _active = active;
            if (!active)
            {
                cancelled = EndSession();
                _session = new CancellationTokenSource();
            }
        }

        CancelQuietly(cancelled);
    }

    /// <summary>
    /// <see cref="ImageryTileState.Offline"/> when a wanted tile failed for a network reason and is not held, else
    /// <see cref="ImageryTileState.Loading"/> when a wanted tile is neither held nor known to have no imagery, else
    /// <see cref="ImageryTileState.AllShown"/>.
    /// </summary>
    public ImageryTileState State(IReadOnlyList<TileKey> wanted)
    {
        bool loading = false;
        lock (_lock)
        {
            foreach (TileKey key in wanted)
            {
                if (_held.ContainsKey(key))
                {
                    continue;
                }

                if (_negative.TryGetValue(key, out NegativeEntry entry))
                {
                    if (entry.Network)
                    {
                        return ImageryTileState.Offline;
                    }
                    continue;
                }
                loading = true;
            }
        }
        return loading ? ImageryTileState.Loading : ImageryTileState.AllShown;
    }

    /// <summary>Cancels every fetch and drops the held tiles without disposing them (a render snapshot may hold one).</summary>
    public void Dispose()
    {
        CancellationTokenSource? cancelled;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancelled = EndSession();
            _held.Clear();
            _lru.Clear();
        }

        CancelQuietly(cancelled);
        if (_transport.OwnsHttp)
        {
            _transport.Http.Dispose();
        }
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    private static HashSet<TileKey> WithRing(HashSet<TileKey> keys)
    {
        var ring = new HashSet<TileKey>();
        foreach (TileKey key in keys)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    ring.Add(key with { X = key.X + dx, Y = key.Y + dy });
                }
            }
        }
        return ring;
    }

    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The fetch ended and disposed its source between being collected and cancelled.
        }
    }

    private static void CancelQuietly(List<CancellationTokenSource> sources)
    {
        foreach (CancellationTokenSource cts in sources)
        {
            CancelQuietly(cts);
        }
    }

    // Under the lock: a new generation, so nothing an older fetch got is published; returns the session to cancel.
    private CancellationTokenSource EndSession()
    {
        _generation++;
        _inFlight.Clear();
        Volatile.Write(ref _postPending, 0);
        return _session;
    }

    // Under the lock.
    private void UpdateWanted(IReadOnlyList<TileKey> wanted, List<CancellationTokenSource> toCancel)
    {
        if (_wanted.SetEquals(wanted))
        {
            return;
        }

        _wanted = [.. wanted];
        HashSet<TileKey> keep = WithRing(_wanted);
        var dropped = new List<TileKey>();
        foreach ((TileKey key, Fetch fetch) in _inFlight)
        {
            if ((!fetch.Started) && (!keep.Contains(key)))
            {
                dropped.Add(key);
                toCancel.Add(fetch.Cts);
            }
        }

        foreach (TileKey key in dropped)
        {
            _inFlight.Remove(key);
        }
        Trim();
    }

    // Under the lock.
    private Fetch? TryBeginFetch(TileKey key, DateTimeOffset now)
    {
        if (_inFlight.ContainsKey(key))
        {
            return null;
        }

        if (_negative.TryGetValue(key, out NegativeEntry entry))
        {
            if (now < entry.Until)
            {
                return null;
            }
            _negative.Remove(key);
        }

        var fetch = new Fetch(CancellationTokenSource.CreateLinkedTokenSource(_session.Token), _generation);
        _inFlight[key] = fetch;
        _running++;
        return fetch;
    }

    private async Task RunFetchAsync(TileKey key, Fetch fetch)
    {
        try
        {
            await LoadAsync(key, fetch).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Cancelled: a changed wanted set, the toggle or Dispose. Never a negative entry.
        }
        catch (Exception ex)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.LogWarning(ex, "Loading the imagery tile {Key} failed", key);
            }
        }
        finally
        {
            EndFetch(key, fetch);
        }
    }

    // Disk first: a fresh file is used as it is, an old one is shown at once and then refreshed, keeping it if that fails.
    private async Task LoadAsync(TileKey key, Fetch fetch)
    {
        fetch.Cts.Token.ThrowIfCancellationRequested();
        string path = _store.PathFor(key);
        StoredTile? stored = _store.Read(path);
        bool shownStored = false;
        if ((stored is not null) && (UsgsTileDecoder.Decode(stored.Bytes) is { } storedImage))
        {
            if ((!Publish(key, fetch, storedImage)) || (stored.IsFresh))
            {
                return;
            }
            shownStored = true;
        }

        DownloadResult result = await DownloadAsync(key, fetch).ConfigureAwait(false);
        fetch.Cts.Token.ThrowIfCancellationRequested();
        if ((result.Bytes is { } bytes) && (UsgsTileDecoder.Decode(bytes) is { } image))
        {
            // Any file already there is replaced: an old tile being refreshed, or one that is corrupt or one colour.
            _store.Write(path, bytes, overwrite: stored is not null);
            Publish(key, fetch, image);
            return;
        }

        if (!shownStored)
        {
            MarkNegative(key, fetch, network: result.Kind == FetchFailure.Network);
        }
    }

    private async Task<DownloadResult> DownloadAsync(TileKey key, Fetch fetch)
    {
        for (int attempt = 0; ; attempt++)
        {
            DownloadResult result = await AttemptAsync(key, fetch).ConfigureAwait(false);
            if (result.Kind != FetchFailure.Retry)
            {
                return result;
            }

            if (attempt >= RetryDelays.Length)
            {
                return new DownloadResult(null, FetchFailure.Network);
            }

            // Waited outside the request gate, so a failing tile does not hold a slot.
            var wait = Task.Delay(RetryDelays[attempt], _time, fetch.Cts.Token);
            RetryScheduled?.Invoke(key);
            await wait.ConfigureAwait(false);
        }
    }

    private async Task<DownloadResult> AttemptAsync(TileKey key, Fetch fetch)
    {
        CancellationToken token = fetch.Cts.Token;
        await _transport.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // SemaphoreSlim can hand a permit to a waiter already cancelled; the finally passes it on.
            token.ThrowIfCancellationRequested();
            MarkStarted(fetch);
            using var timeout = new CancellationTokenSource(AttemptTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            try
            {
                return await SendAsync(key, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                LogAttemptFailure(key, "timed out", null);
                return new DownloadResult(null, FetchFailure.Retry);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                LogAttemptFailure(key, "failed", ex);
                return new DownloadResult(null, FetchFailure.Retry);
            }
        }
        finally
        {
            _transport.Gate.Release();
        }
    }

    private async Task<DownloadResult> SendAsync(TileKey key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TileUrl(key));
        using HttpResponseMessage response = await _transport
            .Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        FetchFailure failure = Classify((int)response.StatusCode, mediaType);
        if (failure != FetchFailure.None)
        {
            LogAttemptFailure(key, $"answered {(int)response.StatusCode} {mediaType}", null);
            return new DownloadResult(null, failure);
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        return new DownloadResult(bytes, FetchFailure.None);
    }

    // 408, 429, 502, 503 and 504 are retried; any other 4xx and a 2xx that is not an image mean the service has no tile to give.
    private static FetchFailure Classify(int status, string? mediaType) =>
        status switch
        {
            408 or 429 or 502 or 503 or 504 => FetchFailure.Retry,
            >= 400 and < 500 => FetchFailure.Permanent,
            < 200 or >= 300 => FetchFailure.Network,
            _ when mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true => FetchFailure.Permanent,
            _ => FetchFailure.None,
        };

    private void LogAttemptFailure(TileKey key, string what, Exception? ex)
    {
        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug(ex, "The imagery tile request for {Key} {What}", key, what);
        }
    }

    private void MarkStarted(Fetch fetch)
    {
        lock (_lock)
        {
            fetch.Started = true;
        }
    }

    // Under the lock: the fetch is still its tile's own and of the current generation.
    private bool IsCurrent(TileKey key, Fetch fetch) =>
        (!_disposed) && (fetch.Generation == _generation) && (_inFlight.TryGetValue(key, out Fetch? owner)) && (ReferenceEquals(owner, fetch));

    // An image that does not reach the memory cache is disposed here: nothing else ever saw it.
    private bool Publish(TileKey key, Fetch fetch, SKImage image)
    {
        bool kept;
        int generation;
        lock (_lock)
        {
            kept = IsCurrent(key, fetch);
            generation = _generation;
            if (kept)
            {
                Hold(key, image);
            }
        }

        if (!kept)
        {
            image.Dispose();
            return false;
        }

        Announce(generation);
        return true;
    }

    private void MarkNegative(TileKey key, Fetch fetch, bool network)
    {
        bool marked;
        int generation;
        lock (_lock)
        {
            marked = IsCurrent(key, fetch);
            generation = _generation;
            if (marked)
            {
                DateTimeOffset until = network ? _time.GetUtcNow() + NetworkRetryAfter : DateTimeOffset.MaxValue;
                _negative[key] = new NegativeEntry(until, network);
            }
        }

        if (marked)
        {
            Announce(generation);
        }
    }

    // Under the lock. A replaced image is dropped, never disposed.
    private void Hold(TileKey key, SKImage image)
    {
        TileBounds bounds = WebMercatorTile.Bounds(key);
        var tile = new ImageryTile(key, image, bounds.South, bounds.West, bounds.North, bounds.East);
        if (_held.TryGetValue(key, out LinkedListNode<ImageryTile>? node))
        {
            node.Value = tile;
            Touch(node);
        }
        else
        {
            _held[key] = _lru.AddLast(tile);
        }

        _negative.Remove(key);
        Trim();
    }

    // Under the lock: drops the least recently used tiles outside the wanted set until the cache fits.
    private void Trim()
    {
        LinkedListNode<ImageryTile>? node = _lru.First;
        while ((_held.Count > _wanted.Count + ExtraHeld) && (node is not null))
        {
            LinkedListNode<ImageryTile>? next = node.Next;
            if (!_wanted.Contains(node.Value.Key))
            {
                _lru.Remove(node);
                _held.Remove(node.Value.Key);
            }
            node = next;
        }
    }

    // Under the lock.
    private void Touch(LinkedListNode<ImageryTile> node)
    {
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    // One announcement pending at a time per generation: a new generation (EndSession) clears the flag, and an
    // announcement of an older one neither calls back nor clears the flag of a newer one.
    private void Announce(int generation)
    {
        if (Interlocked.CompareExchange(ref _postPending, 1, 0) != 0)
        {
            return;
        }

        _post(() => RunAnnouncement(generation));
    }

    private void RunAnnouncement(int generation)
    {
        bool current;
        lock (_lock)
        {
            current = (!_disposed) && (generation == _generation);
            if (current)
            {
                Volatile.Write(ref _postPending, 0);
            }
        }

        if (current)
        {
            _onTileArrived();
        }
    }

    private void EndFetch(TileKey key, Fetch fetch)
    {
        TaskCompletionSource? idle = null;
        lock (_lock)
        {
            if ((_inFlight.TryGetValue(key, out Fetch? owner)) && (ReferenceEquals(owner, fetch)))
            {
                _inFlight.Remove(key);
            }

            _running--;
            if (_running == 0)
            {
                idle = _idle;
                _idle = null;
            }
        }

        fetch.Cts.Dispose();
        idle?.TrySetResult();
    }

    private enum FetchFailure
    {
        None,
        Retry,
        Network,
        Permanent,
    }

    private readonly record struct DownloadResult(byte[]? Bytes, FetchFailure Kind);

    private readonly record struct NegativeEntry(DateTimeOffset Until, bool Network);

    /// <summary>The HTTP client and the request gate a cache sends through, and whether the cache disposes the client.</summary>
    internal sealed record Transport(HttpClient Http, SemaphoreSlim Gate, bool OwnsHttp);

    private sealed class Fetch(CancellationTokenSource cts, int generation)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public int Generation { get; } = generation;

        // Set once the fetch first passed the request gate; a started fetch is never cancelled by a changed wanted set.
        public bool Started { get; set; }
    }
}
