using System.ComponentModel;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Services.Speech;

/// <summary>
/// The app-lifetime owner of the speech-recognition downloads: one <see cref="SttModelInstaller"/> per model of the download
/// manifest, the GPU runtime's <see cref="SherpaCudaInstaller"/>, the GPU check, and whether an engine swap is loading. Settings
/// reads them while it is open; a download keeps running after Settings closes, and a reopened Settings shows its progress.
/// The installers raise their changes on worker threads; a view reads them through <see cref="UiThreadMirror"/>.
/// </summary>
public sealed class SttDownloads : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<SttDownloads>();

    private readonly HttpClient _http;
    private readonly Dictionary<string, SttModelInstaller> _installers = new(StringComparer.Ordinal);
    private int _engineLoads;

    /// <summary>
    /// Builds an installer for every model <paramref name="manifestPath"/> lists and starts the GPU check. A missing or
    /// unreadable manifest gives no model installers and no GPU runtime installer, and logs a warning.
    /// </summary>
    /// <param name="manifestPath">The <c>stt-downloads.json</c> that pins the models and the GPU runtime.</param>
    /// <param name="modelRoot">The folder models install under; production passes <see cref="SttModelInstaller.InstallRootDefault"/>.</param>
    /// <param name="timeProvider">The clock the downloads' idle timeout runs on.</param>
    public SttDownloads(string manifestPath, string modelRoot, TimeProvider timeProvider)
        : this(
            manifestPath,
            modelRoot,
            timeProvider,
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            () => Task.Run(SherpaNativeLoader.NbestAvailability)
        ) { }

    /// <summary>
    /// As the three-argument constructor, downloading with <paramref name="http"/>, which this instance owns and disposes, and
    /// reading the n-best check from the sherpa-onnx native in <paramref name="nativeDir"/> instead of the one this process loads.
    /// </summary>
    /// <param name="manifestPath">The <c>stt-downloads.json</c> that pins the models and the GPU runtime.</param>
    /// <param name="modelRoot">The folder models install under.</param>
    /// <param name="timeProvider">The clock the downloads' idle timeout runs on.</param>
    /// <param name="http">The client every download uses.</param>
    /// <param name="nativeDir">The folder holding the sherpa-onnx C API library the n-best check reads.</param>
    public SttDownloads(string manifestPath, string modelRoot, TimeProvider timeProvider, HttpClient http, string nativeDir)
        : this(manifestPath, modelRoot, timeProvider, http, () => Task.Run(() => SherpaNativeLoader.NbestAvailability(nativeDir))) { }

    private SttDownloads(
        string manifestPath,
        string modelRoot,
        TimeProvider timeProvider,
        HttpClient http,
        Func<Task<(bool Available, string? Reason)>> startNbestCheck
    )
    {
        _http = http;
        ModelRoot = modelRoot;
        Models = ReadModelsOrNone(manifestPath);
        foreach (SttModelDownload model in Models)
        {
            var installer = new SttModelInstaller(http, modelRoot, model, timeProvider);
            installer.PropertyChanged += OnModelInstallerChanged;
            _installers[model.Id] = installer;
        }
        IReadOnlyList<SherpaCudaDownload>? gpuDownloads = GpuDownloadsOrNone(manifestPath);
        Gpu = (gpuDownloads is null) ? null : new SherpaCudaInstaller(http, SherpaCudaInstaller.InstallRootDefault, gpuDownloads, timeProvider);
        GpuSupport = CudaDriverProbe.ProbeAsync();
        NbestAvailability = startNbestCheck();
    }

    /// <summary>The download manifest shipped beside the app.</summary>
    public static string DefaultManifestPath => Path.Combine(AppContext.BaseDirectory, "Speech", "stt-downloads.json");

    /// <summary>
    /// Raised with the model's id when a model installer's <see cref="SttModelInstaller.IsInstalled"/> turns true, on the
    /// installer's thread.
    /// </summary>
    public event Action<string>? ModelInstalled;

    /// <summary>Raised when <see cref="IsEngineLoading"/> changes, on the thread that changed it.</summary>
    public event Action? EngineLoadingChanged;

    /// <summary>The manifest's models, in manifest order; empty when the manifest could not be read.</summary>
    public IReadOnlyList<SttModelDownload> Models { get; }

    /// <summary>The folder the models install under.</summary>
    public string ModelRoot { get; }

    /// <summary>The GPU runtime installer; null on a platform without a GPU runtime or when the manifest could not be read.</summary>
    public SherpaCudaInstaller? Gpu { get; }

    /// <summary>The GPU check, started once at construction on the thread pool; it never faults.</summary>
    public Task<GpuSupport> GpuSupport { get; }

    /// <summary>
    /// Whether the sherpa-onnx native can run Accurate mode, and the reason when it cannot; checked once at construction on
    /// the thread pool (<see cref="SherpaNativeLoader.NbestAvailability()"/>).
    /// </summary>
    public Task<(bool Available, string? Reason)> NbestAvailability { get; }

    /// <summary>Whether a speech-recognition engine swap is loading a model.</summary>
    public bool IsEngineLoading => Volatile.Read(ref _engineLoads) > 0;

    /// <summary>The installer of model <paramref name="modelId"/>, or null when the manifest lists no such model.</summary>
    /// <param name="modelId">The model's manifest id.</param>
    public SttModelInstaller? ModelInstaller(string modelId) => _installers.GetValueOrDefault(modelId);

    /// <summary>
    /// Runs <paramref name="load"/> with <see cref="IsEngineLoading"/> true from its start until its task completes, faulted
    /// or not; overlapping loads keep it true until the last one completes.
    /// </summary>
    /// <param name="load">Starts the engine swap.</param>
    public async Task TrackEngineLoadAsync(Func<Task> load)
    {
        if (Interlocked.Increment(ref _engineLoads) == 1)
        {
            EngineLoadingChanged?.Invoke();
        }
        try
        {
            await load().ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Decrement(ref _engineLoads) == 0)
            {
                EngineLoadingChanged?.Invoke();
            }
        }
    }

    /// <summary>Whether <see cref="Dispose"/> ran.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Cancels any download still running, so an exit mid-download logs a cancel rather than a failure, stops listening to
    /// the installers and disposes the download client.
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }
        IsDisposed = true;
        foreach (SttModelInstaller installer in _installers.Values)
        {
            installer.Cancel();
            installer.PropertyChanged -= OnModelInstallerChanged;
        }
        Gpu?.Cancel();
        _http.Dispose();
    }

    private void OnModelInstallerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(SttModelInstaller.IsInstalled)) && (sender is SttModelInstaller { IsInstalled: true } installer))
        {
            string? id = _installers.FirstOrDefault(pair => ReferenceEquals(pair.Value, installer)).Key;
            if (id is not null)
            {
                ModelInstalled?.Invoke(id);
            }
        }
    }

    private static IReadOnlyList<SttModelDownload> ReadModelsOrNone(string manifestPath)
    {
        try
        {
            return SttModelInstaller.ReadModels(manifestPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "The STT download manifest {Path} could not be read; no speech-recognition model can be downloaded", manifestPath);
            return [];
        }
    }

    private static IReadOnlyList<SherpaCudaDownload>? GpuDownloadsOrNone(string manifestPath)
    {
        try
        {
            return SherpaCudaInstaller.ForCurrentPlatform(manifestPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.LogInformation(ex, "The STT download manifest {Path} pins no GPU runtime for this platform", manifestPath);
            return null;
        }
    }
}
