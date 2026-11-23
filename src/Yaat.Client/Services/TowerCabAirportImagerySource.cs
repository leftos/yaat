namespace Yaat.Client.Services;

/// <summary>
/// The vNAS tower-cab image, high-res, through the app's shared <see cref="TowerCabImageService"/> (one HTTP client, one
/// disk cache) from the base URL the vNAS config gives; null while the config has no base URL.
/// </summary>
/// <remarks>
/// One request per (ARTCC, airport) runs at a time: a second caller for the same airport waits on the first one's task
/// with its own token, so reopening a map or toggling imagery never starts a second download or decode, and every waiter
/// hears the download's progress. The entry goes once its request has finished; a later call starts a new one.
/// </remarks>
public sealed class TowerCabAirportImagerySource : IAirportImagerySource
{
    private readonly Func<string?> _baseUrl;
    private readonly Func<string, string, string, IProgress<TowerCabDownloadProgress>, Task<TowerCabImage?>> _fetch;
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Artcc, string Airport), SharedRequest> _inFlight = [];

    /// <summary>Initializes a new instance of the <see cref="TowerCabAirportImagerySource"/> class.</summary>
    /// <param name="images">The app's tower-cab image service.</param>
    /// <param name="config">The app's vNAS config, read at each request.</param>
    public TowerCabAirportImagerySource(TowerCabImageService images, VnasConfigService config)
        : this(
            () => ((config.IsInitialized) && (!string.IsNullOrEmpty(config.TowerCabImagesBaseUrl))) ? config.TowerCabImagesBaseUrl : null,
            (baseUrl, artccId, airportId, progress) => images.GetImageAsync(baseUrl, artccId, airportId, highRes: true, progress)
        )
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(config);
    }

    /// <summary>Creates a source over a base-URL reader and a fetch (base URL, ARTCC, airport, progress). Test seam.</summary>
    internal TowerCabAirportImagerySource(
        Func<string?> baseUrl,
        Func<string, string, string, IProgress<TowerCabDownloadProgress>, Task<TowerCabImage?>> fetch
    )
    {
        _baseUrl = baseUrl;
        _fetch = fetch;
    }

    /// <inheritdoc />
    public async Task<TowerCabImage?> GetAsync(string artccId, string airportId, IProgress<TowerCabDownloadProgress>? progress, CancellationToken ct)
    {
        if (_baseUrl() is not { } baseUrl)
        {
            return null;
        }

        SharedRequest shared = Join((artccId, airportId), baseUrl, progress);
        try
        {
            return await shared.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            shared.Leave(progress);
        }
    }

    // The request in flight for the key, or a new one started off the UI thread (the decode of a high-res image is
    // synchronous and slow); the caller's progress joins it.
    private SharedRequest Join((string Artcc, string Airport) key, string baseUrl, IProgress<TowerCabDownloadProgress>? progress)
    {
        lock (_lock)
        {
            if (!_inFlight.TryGetValue(key, out SharedRequest? shared))
            {
                shared = new SharedRequest();
                _inFlight[key] = shared;
                SharedRequest started = shared;
                _ = Task.Run(() => RunAsync(key, baseUrl, started));
            }

            shared.Join(progress);
            return shared;
        }
    }

    // Runs the fetch and completes the shared task with its outcome, once the entry is gone (a caller that sees the task
    // done and asks again starts a new request).
    private async Task RunAsync((string Artcc, string Airport) key, string baseUrl, SharedRequest shared)
    {
        TowerCabImage? image = null;
        Exception? failure = null;
        try
        {
            image = await _fetch(baseUrl, key.Artcc, key.Airport, shared).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        lock (_lock)
        {
            if ((_inFlight.TryGetValue(key, out SharedRequest? current)) && (ReferenceEquals(current, shared)))
            {
                _inFlight.Remove(key);
            }
        }

        shared.Complete(image, failure);
    }

    // One download and its waiters' progress sinks; a report goes to every sink waiting when it is made.
    private sealed class SharedRequest : IProgress<TowerCabDownloadProgress>
    {
        private readonly Lock _lock = new();
        private readonly List<IProgress<TowerCabDownloadProgress>> _waiters = [];

        private readonly TaskCompletionSource<TowerCabImage?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TowerCabImage?> Task => _done.Task;

        public void Complete(TowerCabImage? image, Exception? failure)
        {
            if (failure is null)
            {
                _done.TrySetResult(image);
            }
            else
            {
                _done.TrySetException(failure);
            }
        }

        public void Join(IProgress<TowerCabDownloadProgress>? progress)
        {
            if (progress is null)
            {
                return;
            }

            lock (_lock)
            {
                _waiters.Add(progress);
            }
        }

        public void Leave(IProgress<TowerCabDownloadProgress>? progress)
        {
            if (progress is null)
            {
                return;
            }

            lock (_lock)
            {
                _waiters.Remove(progress);
            }
        }

        public void Report(TowerCabDownloadProgress value)
        {
            IProgress<TowerCabDownloadProgress>[] waiters;
            lock (_lock)
            {
                waiters = [.. _waiters];
            }

            foreach (IProgress<TowerCabDownloadProgress> waiter in waiters)
            {
                waiter.Report(value);
            }
        }
    }
}
