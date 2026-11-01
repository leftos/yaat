using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;

namespace Yaat.Client.Services.Speech;

/// <summary>
/// One archive the CUDA native set is built from, and the files taken from it.
/// </summary>
/// <param name="Name">The archive's name, unique within the set; it names the download in logs and in the sentinel.</param>
/// <param name="Urls">Where the archive is served, tried in order until one serves the pinned bytes.</param>
/// <param name="Size">The archive's size in bytes.</param>
/// <param name="Sha256">The archive's SHA-256, lower-case hex.</param>
/// <param name="Entries">
/// Zip entry paths to take. A last segment of the form <c>*&lt;suffix&gt;</c> (<c>nvidia/cudnn/bin/*.dll</c>) takes every
/// entry directly in that folder whose name ends with the suffix.
/// </param>
public sealed record SherpaCudaDownload(string Name, IReadOnlyList<string> Urls, long Size, string Sha256, IReadOnlyList<string> Entries);

/// <summary>
/// Downloads and manages sherpa-onnx's CUDA native set: our CUDA build of the sherpa-onnx C API and onnxruntime, plus
/// the NVIDIA cuBLAS and cuDNN libraries it needs (and, on Linux, the CUDA runtime and cuRAND), each archive pinned by
/// size and SHA-256. Windows x64 and Linux x64 only.
///
/// Layout after a successful install:
/// <code>
///   &lt;installRoot&gt;/
///       .installed      (sentinel: one "&lt;Name&gt; &lt;Sha256&gt;" line per download)
///       native/         (every taken file, flat under its file name)
/// </code>
/// Downloads stream to a fresh <c>&lt;installRoot&gt;/.staging-&lt;id&gt;/</c> folder, and the installed set stays in place
/// until the new one is complete. The swap then moves the old <c>native/</c> aside to <c>.old-&lt;id&gt;/</c>, deletes the
/// sentinel, moves the new <c>native/</c> in and writes the sentinel last, so <see cref="IsInstalledOnDisk"/> never reports
/// a half-built set. A failed swap puts the old set and its sentinel back.
///
/// One writer per install root, across instances and processes: install and uninstall hold the lock file
/// <c>&lt;installRoot&gt;.lock</c> beside the root (<see cref="TryLockRoot"/>). On Windows neither replaces nor removes a set
/// whose libraries a process has loaded.
/// </summary>
/// <param name="http">The client the archives are downloaded with.</param>
/// <param name="installRoot">The folder the set is installed into; production passes <see cref="InstallRootDefault"/>.</param>
/// <param name="downloads">The archives the set is built from, usually <see cref="ForCurrentPlatform"/>'s list.</param>
/// <param name="timeProvider">The clock the body idle timeout runs on; production passes <see cref="TimeProvider.System"/>.</param>
public sealed partial class SherpaCudaInstaller(
    HttpClient http,
    string installRoot,
    IReadOnlyList<SherpaCudaDownload> downloads,
    TimeProvider timeProvider
) : ObservableObject
{
    /// <summary>How long one read of a download body may wait for bytes before that URL counts as failed.</summary>
    public static readonly TimeSpan IdleReadTimeout = TimeSpan.FromSeconds(60);

    private static readonly ILogger Log = AppLog.CreateLogger<SherpaCudaInstaller>();

    private const string InstalledSentinelFileName = ".installed";
    private const string LegacyPartialDirName = ".partial";
    private const string StagingDirPrefix = ".staging-";
    private const string OldDirPrefix = ".old-";
    private const string BusyMessage = "Another YAAT is installing or removing the GPU runtime.";
    private const string InUseMessage = "The GPU runtime is in use. Switch Speech > Device to CPU, restart YAAT, then try again.";
    private const string RefusalKey = "SherpaCudaInstaller.Refusal";
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int LinuxEWouldBlock = 11;
    private const int MacOsEWouldBlock = 35;

    /// <summary>The folder under the install root that holds the native libraries.</summary>
    public const string NativeDirName = "native";

    private const string CublasVersion = "13.7.0.27";
    private const string CudnnVersion = "9.27.0.42";
    private const string CudaRuntimeVersion = "13.4.49";
    private const string CurandVersion = "10.4.4.49";

    private const string WinCublasUrl =
        "https://files.pythonhosted.org/packages/f5/90/549ebae2524f3b7a695537f111c4c6a9e926ebc703c57371aa5558489e1b/nvidia_cublas-13.7.0.27-py3-none-win_amd64.whl";
    private const long WinCublasSize = 424483695;
    private const string WinCublasSha256 = "15d3963ae9de7292348ced320c849ff27f76b0f433b81589e9eb87f5407841c9";

    private const string WinCudnnUrl =
        "https://files.pythonhosted.org/packages/87/6a/e55ff0ac26a5c6e2b21f41c9d04ad096b4ed6da593fba7e25845c61b0532/nvidia_cudnn_cu13-9.27.0.42-py3-none-win_amd64.whl";
    private const long WinCudnnSize = 436469905;
    private const string WinCudnnSha256 = "7d96f634adafd55c72231eb0500ca77ab109ec8ebff7b33000b76e081bc4558e";

    private const string LinuxCublasUrl =
        "https://files.pythonhosted.org/packages/da/b3/358ee3e2237d4d663cd7a50e4dae3b07bbc5c93bf1eab034c17d66b7e428/nvidia_cublas-13.7.0.27-py3-none-manylinux_2_27_x86_64.whl";
    private const long LinuxCublasSize = 440472026;
    private const string LinuxCublasSha256 = "481adf76a6b7585a7a4cd5f53298589abe207415dd584887751b99baa85491d4";

    private const string LinuxCudaRuntimeUrl =
        "https://files.pythonhosted.org/packages/e6/cb/b5959bff298d114338aaf4bface538ca9f2a721c439618fb252162f6192e/nvidia_cuda_runtime-13.4.49-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl";
    private const long LinuxCudaRuntimeSize = 2495545;
    private const string LinuxCudaRuntimeSha256 = "0b228cc646acda27e9b512b8b94417c3ff8b3d24a0ad9b1d6603dc6c2a908046";

    private const string LinuxCurandUrl =
        "https://files.pythonhosted.org/packages/b9/c5/fc1b11443428fee82b239bf5ad1f6f7917509560714f7549321a5d34dd40/nvidia_curand-10.4.4.49-py3-none-manylinux_2_27_x86_64.whl";
    private const long LinuxCurandSize = 61498481;
    private const string LinuxCurandSha256 = "5992fea10ff452dc4f0357d2c76ab8ac7e31f5fa99938a019abc18545ab5e3ce";

    private const string LinuxCudnnUrl =
        "https://files.pythonhosted.org/packages/af/75/96ea5c5368eb595c39d629cde08a66227a864e71ccb0e593add2612bb952/nvidia_cudnn_cu13-9.27.0.42-py3-none-manylinux_2_27_x86_64.whl";
    private const long LinuxCudnnSize = 536771498;
    private const string LinuxCudnnSha256 = "9677e76f21862eb5da7ee5ed69d544738b2d8b5c3ce7e5ec125c5592e6cdbdc8";

    private static readonly SherpaCudaDownload[] WindowsWheels =
    [
        new(
            $"nvidia-cublas-{CublasVersion}",
            [WinCublasUrl],
            WinCublasSize,
            WinCublasSha256,
            ["nvidia/cu13/bin/x86_64/cublas64_13.dll", "nvidia/cu13/bin/x86_64/cublasLt64_13.dll"]
        ),
        new($"nvidia-cudnn-cu13-{CudnnVersion}", [WinCudnnUrl], WinCudnnSize, WinCudnnSha256, ["nvidia/cudnn/bin/*.dll"]),
    ];

    private static readonly SherpaCudaDownload[] LinuxWheels =
    [
        new(
            $"nvidia-cublas-{CublasVersion}",
            [LinuxCublasUrl],
            LinuxCublasSize,
            LinuxCublasSha256,
            ["nvidia/cu13/lib/libcublas.so.13", "nvidia/cu13/lib/libcublasLt.so.13"]
        ),
        new(
            $"nvidia-cuda-runtime-{CudaRuntimeVersion}",
            [LinuxCudaRuntimeUrl],
            LinuxCudaRuntimeSize,
            LinuxCudaRuntimeSha256,
            ["nvidia/cu13/lib/libcudart.so.13"]
        ),
        new($"nvidia-curand-{CurandVersion}", [LinuxCurandUrl], LinuxCurandSize, LinuxCurandSha256, ["nvidia/cu13/lib/libcurand.so.10"]),
        new($"nvidia-cudnn-cu13-{CudnnVersion}", [LinuxCudnnUrl], LinuxCudnnSize, LinuxCudnnSha256, ["nvidia/cudnn/lib/*.so.9"]),
    ];

    private static readonly string[] WindowsZipEntries =
    [
        "sherpa-onnx-c-api.dll",
        "onnxruntime.dll",
        "onnxruntime_providers_cuda.dll",
        "onnxruntime_providers_shared.dll",
        "MANIFEST.txt",
    ];

    private static readonly string[] LinuxZipEntries =
    [
        "libsherpa-onnx-c-api.so",
        "libonnxruntime.so",
        "libonnxruntime_providers_cuda.so",
        "libonnxruntime_providers_shared.so",
        "MANIFEST.txt",
    ];

    private readonly HttpClient _http = http;
    private readonly string _installRoot = installRoot;
    private readonly IReadOnlyList<SherpaCudaDownload> _downloads = downloads;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);

    [ObservableProperty]
    private bool _isInstalled = IsInstalledOnDisk(installRoot, downloads);

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>0.0 to 1.0 across all downloads, weighted by size. Set from the install worker thread.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>Short status text for the UI. Never null after first use.</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>The production install root, outside the Velopack-managed app folder so upgrades keep it.</summary>
    public static string InstallRootDefault => YaatPaths.Combine("backends", "sherpa-cuda13");

    /// <summary>The bytes the install downloads: the sum of every archive's size.</summary>
    public long ApproxDownloadBytes => _downloads.Sum(d => d.Size);

    /// <summary>
    /// The archives of the CUDA native set for the running platform, or null on a platform without one (anything but
    /// Windows x64 and Linux x64).
    /// </summary>
    /// <param name="manifestPath">The <c>stt-downloads.json</c> that pins our CUDA native zips.</param>
    /// <exception cref="InvalidDataException">The manifest is not a valid STT download manifest, or lists no CUDA native for the platform.</exception>
    /// <exception cref="IOException">The manifest could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The manifest could not be read.</exception>
    public static IReadOnlyList<SherpaCudaDownload>? ForCurrentPlatform(string manifestPath)
    {
        string? rid = HostRid();
        return (rid is null) ? null : ForPlatform(rid, manifestPath);
    }

    /// <summary>The archives of the CUDA native set for <paramref name="rid"/>, or null for a RID without one.</summary>
    internal static IReadOnlyList<SherpaCudaDownload>? ForPlatform(string rid, string manifestPath)
    {
        return rid switch
        {
            "win-x64" => [ReadOurZip(rid, manifestPath, WindowsZipEntries), .. WindowsWheels],
            "linux-x64" => [ReadOurZip(rid, manifestPath, LinuxZipEntries), .. LinuxWheels],
            _ => null,
        };
    }

    /// <summary>
    /// True when <paramref name="installRoot"/>'s sentinel names exactly <paramref name="downloads"/>' archives and
    /// hashes, so a changed pin invalidates the install. Safe from any thread; it reads one small file.
    /// </summary>
    public static bool IsInstalledOnDisk(string installRoot, IReadOnlyList<SherpaCudaDownload> downloads)
    {
        string sentinelPath = SentinelPath(installRoot);
        try
        {
            return File.Exists(sentinelPath)
                && string.Equals(File.ReadAllText(sentinelPath), ExpectedSentinelContent(downloads), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Failed to read the sherpa CUDA sentinel at {Path}", sentinelPath);
            return false;
        }
    }

    /// <summary>
    /// Takes the install root's writer lock: the file <c>&lt;installRoot&gt;.lock</c> beside the root, opened exclusively,
    /// so one writer at a time installs or removes the set, across instances and processes. The lock file is never deleted.
    /// </summary>
    /// <param name="installRoot">The install root to lock.</param>
    /// <returns>The open lock, which the caller disposes to release it; null when another holder has it.</returns>
    public static FileStream? TryLockRoot(string installRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        string parent = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(parent);
        string lockPath = Path.Combine(parent, Path.GetFileName(root) + ".lock");
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            Log.LogInformation("The sherpa CUDA install lock {Path} is held by another writer", lockPath);
            return null;
        }
    }

    /// <summary>
    /// Downloads and installs the set. Single-flight per instance, and one writer per install root. A failed or cancelled
    /// install leaves no staging folder and the installed set and its sentinel as they were.
    /// </summary>
    /// <param name="ct">Cancels the install.</param>
    /// <exception cref="InvalidOperationException">
    /// An install is already running, another writer holds the root, or (Windows) the installed set is in use.
    /// </exception>
    /// <exception cref="InvalidDataException">An archive failed its size or SHA-256 check at every URL, or lacks an entry.</exception>
    /// <exception cref="HttpRequestException">Every URL of an archive failed, the last with an HTTP error or a timeout.</exception>
    public async Task InstallAsync(CancellationToken ct)
    {
        if (!_lock.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("Install already in progress.");
        }
        string stagingRoot = Path.Combine(_installRoot, StagingDirPrefix + Guid.NewGuid().ToString("N"));
        FileStream? rootLock = null;
        try
        {
            IsBusy = true;
            StatusMessage = "Preparing…";
            Progress = 0;
            rootLock = LockRootOrRefuse();
            RefuseIfSetInUse();
            await RunInstallAsync(stagingRoot, ct).ConfigureAwait(false);
            IsInstalled = IsInstalledOnDisk(_installRoot, _downloads);
            StatusMessage = IsInstalled ? "Installed. Restart YAAT to use the GPU." : "Install failed — see yaat-client.log.";
        }
        catch (InvalidOperationException ex) when (ex.Data.Contains(RefusalKey))
        {
            DeleteDirBestEffort(stagingRoot);
            throw;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("Sherpa CUDA native install cancelled");
            StatusMessage = "Cancelled.";
            DeleteDirBestEffort(stagingRoot);
            throw;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Sherpa CUDA native install failed");
            StatusMessage = $"Failed: {ex.Message}";
            DeleteDirBestEffort(stagingRoot);
            throw;
        }
        finally
        {
            rootLock?.Dispose();
            IsBusy = false;
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes the installed set, sentinel first. Harmless if nothing is installed; the running process keeps what it loaded.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// An install is running, another writer holds the root, or (Windows) the installed set is in use.
    /// </exception>
    /// <exception cref="IOException">A file of the set could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">A file of the set could not be deleted.</exception>
    public void Uninstall()
    {
        if (!_lock.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("Install in progress; cannot uninstall.");
        }
        try
        {
            using FileStream rootLock = LockRootOrRefuse();
            RefuseIfSetInUse();
            RemoveSet();
            StatusMessage = "Removed. Restart YAAT to fall back to the CPU.";
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The sentinel file <paramref name="installRoot"/> holds once its set is installed.</summary>
    /// <param name="installRoot">The install root.</param>
    public static string SentinelPath(string installRoot) => Path.Combine(installRoot, InstalledSentinelFileName);

    /// <summary>The sentinel's content for <paramref name="downloads"/>: one name and SHA-256 per archive.</summary>
    /// <param name="downloads">The archives a set is installed from.</param>
    public static string ExpectedSentinelContent(IReadOnlyList<SherpaCudaDownload> downloads) =>
        string.Join('\n', downloads.Select(d => $"{d.Name} {d.Sha256}"));

    private static string? HostRid()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return null;
        }
        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }
        return OperatingSystem.IsLinux() ? "linux-x64" : null;
    }

    /// <summary>Reads our zip's entry from the manifest, reporting anything but a read failure as the invalid manifest it is.</summary>
    private static SherpaCudaDownload ReadOurZip(string rid, string manifestPath, string[] entries)
    {
        try
        {
            return ParseOurZip(rid, manifestPath, entries);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException($"{manifestPath} is not a valid STT download manifest: {ex.Message}", ex);
        }
    }

    private static SherpaCudaDownload ParseOurZip(string rid, string manifestPath, string[] entries)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;
        string baseUrl = root.GetProperty("baseUrl").GetString() ?? throw new InvalidDataException($"{manifestPath} has a null baseUrl");
        JsonElement natives = root.GetProperty("natives");
        foreach (JsonElement file in natives.GetProperty("files").EnumerateArray())
        {
            if ((file.GetProperty("rid").GetString() != rid) || (file.GetProperty("variant").GetString() != "cuda"))
            {
                continue;
            }

            string key = file.GetProperty("key").GetString() ?? throw new InvalidDataException($"{manifestPath} has a null key for {rid} cuda");
            string fileName = key[(key.LastIndexOf('/') + 1)..];
            List<string> urls = [baseUrl + key];
            foreach (JsonElement mirror in natives.GetProperty("mirrors").EnumerateArray())
            {
                urls.Add(mirror.GetString() + fileName);
            }
            string sha256 = file.GetProperty("sha256").GetString() ?? throw new InvalidDataException($"{manifestPath} has a null sha256 for {key}");
            return new SherpaCudaDownload($"sherpa-onnx-{rid}-cuda", urls, file.GetProperty("size").GetInt64(), sha256, entries);
        }

        throw new InvalidDataException($"{manifestPath} lists no cuda native for {rid}");
    }

    private static bool IsSharingViolation(IOException ex)
    {
        if (OperatingSystem.IsWindows())
        {
            int code = ex.HResult & 0xFFFF;
            return (code == ErrorSharingViolation) || (code == ErrorLockViolation);
        }
        return ex.HResult == (OperatingSystem.IsMacOS() ? MacOsEWouldBlock : LinuxEWouldBlock);
    }

    /// <summary>
    /// Whether a process has the set's libraries loaded: this process loaded the CUDA set, or a <c>*.dll</c> directly in
    /// <paramref name="nativeDir"/> refuses an exclusive write-open, as a mapped image does.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool NativeSetInUse(string nativeDir)
    {
        if (SherpaNativeLoader.LoadedSet == SherpaNativeSet.Cuda)
        {
            return true;
        }
        if (!Directory.Exists(nativeDir))
        {
            return false;
        }
        foreach (string path in Directory.EnumerateFiles(nativeDir, "*.dll"))
        {
            try
            {
                new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
            }
            catch (IOException ex)
            {
                Log.LogInformation(ex, "{Path} refuses an exclusive open, so a process has it loaded", path);
                return true;
            }
        }
        return false;
    }

    private static InvalidOperationException Refusal(string message)
    {
        var ex = new InvalidOperationException(message);
        ex.Data[RefusalKey] = true;
        return ex;
    }

    private FileStream LockRootOrRefuse()
    {
        FileStream? rootLock = TryLockRoot(_installRoot);
        if (rootLock is not null)
        {
            return rootLock;
        }
        Log.LogWarning("Another writer holds the sherpa CUDA install root {Root}; refusing", _installRoot);
        StatusMessage = BusyMessage;
        throw Refusal(BusyMessage);
    }

    private void RefuseIfSetInUse()
    {
        if (!OperatingSystem.IsWindows() || !NativeSetInUse(Path.Combine(_installRoot, NativeDirName)))
        {
            return;
        }
        Log.LogWarning("The sherpa CUDA native set at {Root} is in use; refusing to replace or remove it", _installRoot);
        StatusMessage = InUseMessage;
        throw Refusal(InUseMessage);
    }

    private void RemoveSet()
    {
        try
        {
            string sentinelPath = SentinelPath(_installRoot);
            if (File.Exists(sentinelPath))
            {
                File.Delete(sentinelPath);
            }
            IsInstalled = false;
            if (!Directory.Exists(_installRoot))
            {
                return;
            }
            foreach (string dir in Directory.GetDirectories(_installRoot))
            {
                Directory.Delete(dir, recursive: true);
            }
            foreach (string file in Directory.GetFiles(_installRoot))
            {
                File.Delete(file);
            }
            Directory.Delete(_installRoot);
            Log.LogInformation("Removed the sherpa CUDA native set at {Root}", _installRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(ex, "Failed to remove the sherpa CUDA native set at {Root}", _installRoot);
            StatusMessage = $"Failed: {ex.Message}";
            IsInstalled = IsInstalledOnDisk(_installRoot, _downloads);
            throw;
        }
    }

    private async Task RunInstallAsync(string stagingRoot, CancellationToken ct)
    {
        SweepLeftovers();
        string stagingNative = Path.Combine(stagingRoot, NativeDirName);
        Directory.CreateDirectory(stagingNative);

        double totalBytes = Math.Max(1, ApproxDownloadBytes);
        long doneBytes = 0;
        for (int i = 0; i < _downloads.Count; i++)
        {
            SherpaCudaDownload download = _downloads[i];
            StatusMessage = $"Downloading {download.Name} ({i + 1} of {_downloads.Count})…";
            string archivePath = Path.Combine(stagingRoot, $"_{download.Name}.part");
            var phase = new ProgressPhase(doneBytes / totalBytes, (doneBytes + download.Size) / totalBytes);
            string url = await DownloadFromFirstGoodUrlAsync(download, archivePath, phase, ct).ConfigureAwait(false);
            ExtractEntries(download, archivePath, url, stagingNative, ct);
            File.Delete(archivePath);
            doneBytes += download.Size;
        }

        RefuseIfSetInUse();
        SwapIn(stagingRoot);
        SetProgress(1.0);
    }

    /// <summary>Deletes the staging, old-set and legacy partial folders an earlier run left under the root.</summary>
    private void SweepLeftovers()
    {
        if (!Directory.Exists(_installRoot))
        {
            return;
        }
        List<string> leftovers =
        [
            .. Directory.EnumerateDirectories(_installRoot, StagingDirPrefix + "*"),
            .. Directory.EnumerateDirectories(_installRoot, OldDirPrefix + "*"),
            .. Directory.EnumerateDirectories(_installRoot, LegacyPartialDirName),
        ];
        foreach (string dir in leftovers)
        {
            DeleteDirBestEffort(dir);
        }
    }

    /// <summary>
    /// Replaces <c>native/</c> with the staged set and writes the sentinel last. A failure puts the old set and its sentinel
    /// back and rethrows; a failure to delete the replaced set afterwards only logs.
    /// </summary>
    private void SwapIn(string stagingRoot)
    {
        string nativeDir = Path.Combine(_installRoot, NativeDirName);
        string stagingNative = Path.Combine(stagingRoot, NativeDirName);
        string oldDir = Path.Combine(_installRoot, OldDirPrefix + Guid.NewGuid().ToString("N"));
        string sentinelPath = SentinelPath(_installRoot);
        string? oldSentinel = File.Exists(sentinelPath) ? File.ReadAllText(sentinelPath) : null;
        bool oldMoved = false;
        bool newInPlace = false;
        try
        {
            if (Directory.Exists(nativeDir))
            {
                Directory.Move(nativeDir, oldDir);
                oldMoved = true;
            }
            if (File.Exists(sentinelPath))
            {
                File.Delete(sentinelPath);
            }
            Directory.Move(stagingNative, nativeDir);
            newInPlace = true;
            if (OperatingSystem.IsLinux())
            {
                MarkSharedObjectsExecutable(nativeDir);
            }
            File.WriteAllText(sentinelPath, ExpectedSentinelContent(_downloads));
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Swapping in the sherpa CUDA native set at {Root} failed; restoring the old set", _installRoot);
            RestoreAfterFailedSwap(new SwapUndo(stagingNative, oldDir, oldSentinel, oldMoved, newInPlace));
            throw;
        }

        DeleteDirBestEffort(oldDir);
        DeleteDirBestEffort(stagingRoot);
    }

    private void RestoreAfterFailedSwap(SwapUndo undo)
    {
        string nativeDir = Path.Combine(_installRoot, NativeDirName);
        string sentinelPath = SentinelPath(_installRoot);
        RestoreStep(
            "delete the new sentinel",
            () =>
            {
                if (File.Exists(sentinelPath))
                {
                    File.Delete(sentinelPath);
                }
            }
        );
        if (undo.NewInPlace)
        {
            RestoreStep("move the new set back to staging", () => Directory.Move(nativeDir, undo.StagingNative));
        }
        if (undo.OldMoved)
        {
            RestoreStep("move the old set back", () => Directory.Move(undo.OldDir, nativeDir));
        }
        if (undo.OldSentinel is string oldSentinel)
        {
            RestoreStep("rewrite the old sentinel", () => File.WriteAllText(sentinelPath, oldSentinel));
        }
    }

    private static void RestoreStep(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(ex, "Failed to restore the sherpa CUDA native set: {Step}", step);
        }
    }

    private async Task<string> DownloadFromFirstGoodUrlAsync(
        SherpaCudaDownload download,
        string archivePath,
        ProgressPhase phase,
        CancellationToken ct
    )
    {
        List<string> failures = [];
        Exception? lastFailure = null;
        foreach (string url in download.Urls)
        {
            try
            {
                await DownloadVerifiedAsync(download, url, archivePath, phase, ct).ConfigureAwait(false);
                return url;
            }
            catch (Exception ex)
                when ((ex is HttpRequestException or HttpIOException or InvalidDataException)
                    || ((ex is OperationCanceledException) && !ct.IsCancellationRequested)
                )
            {
                Log.LogWarning(ex, "{Name}: download from {Url} failed", download.Name, url);
                failures.Add($"{url}: {ex.Message}");
                lastFailure = ex;
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
        }

        string message = $"{download.Name}: every URL failed. {string.Join(" | ", failures)}";
        throw (lastFailure is InvalidDataException) ? new InvalidDataException(message, lastFailure) : new HttpRequestException(message, lastFailure);
    }

    private async Task DownloadVerifiedAsync(SherpaCudaDownload download, string url, string archivePath, ProgressPhase phase, CancellationToken ct)
    {
        Log.LogInformation("Downloading {Name} from {Url}", download.Name, url);
        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? length = response.Content.Headers.ContentLength;
        if ((length is not null) && (length != download.Size))
        {
            throw new InvalidDataException($"{download.Name}: {url} announces {length} bytes, expected {download.Size}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var idle = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        long bytesRead = 0;
        await using (Stream stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
        await using (FileStream file = File.Create(archivePath))
        {
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                idle.CancelAfter(IdleReadTimeout);
                int read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                bytesRead += read;
                if (bytesRead > download.Size)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                SetProgress(phase.Start + ((phase.End - phase.Start) * Math.Min(1.0, (double)bytesRead / Math.Max(1, download.Size))));
            }
        }

        if (bytesRead > download.Size)
        {
            File.Delete(archivePath);
            throw new InvalidDataException($"{download.Name}: {url} sent more than the expected {download.Size} bytes");
        }
        if (bytesRead != download.Size)
        {
            File.Delete(archivePath);
            throw new InvalidDataException($"{download.Name}: {url} sent {bytesRead} bytes, expected {download.Size}");
        }

        string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, download.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(archivePath);
            throw new InvalidDataException($"{download.Name}: SHA-256 of {url} is {actual}, expected {download.Sha256}");
        }
    }

    private static void ExtractEntries(SherpaCudaDownload download, string archivePath, string url, string destDir, CancellationToken ct)
    {
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        foreach (string entry in download.Entries)
        {
            List<ZipArchiveEntry> matches = MatchEntries(zip, entry);
            if (matches.Count == 0)
            {
                throw new InvalidDataException($"{download.Name}: {url} has no entry {entry}");
            }
            foreach (ZipArchiveEntry match in matches)
            {
                ct.ThrowIfCancellationRequested();
                match.ExtractToFile(Path.Combine(destDir, match.Name), overwrite: true);
            }
        }
    }

    private static List<ZipArchiveEntry> MatchEntries(ZipArchive zip, string entry)
    {
        int lastSlash = entry.LastIndexOf('/');
        string lastSegment = entry[(lastSlash + 1)..];
        if (!lastSegment.StartsWith('*'))
        {
            return [.. zip.Entries.Where(e => string.Equals(e.FullName, entry, StringComparison.Ordinal))];
        }

        string folder = entry[..(lastSlash + 1)];
        string suffix = lastSegment[1..];
        return
        [
            .. zip
                .Entries.Where(e =>
                    e.FullName.StartsWith(folder, StringComparison.Ordinal)
                    && !e.FullName.AsSpan(folder.Length).Contains('/')
                    && e.Name.EndsWith(suffix, StringComparison.Ordinal)
                    && (e.Name.Length > suffix.Length)
                )
                .OrderBy(e => e.Name, StringComparer.Ordinal),
        ];
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void MarkSharedObjectsExecutable(string nativeDir)
    {
        const UnixFileMode Mode =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute;
        foreach (string path in Directory.EnumerateFiles(nativeDir))
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".so", StringComparison.Ordinal) || name.Contains(".so.", StringComparison.Ordinal))
            {
                File.SetUnixFileMode(path, Mode);
            }
        }
    }

    private static void DeleteDirBestEffort(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Failed to clean the sherpa CUDA install folder {Path}; the next install sweeps it", path);
        }
    }

    private void SetProgress(double value) => Progress = Math.Round(Math.Clamp(value, 0.0, 1.0), 3);

    /// <summary>One download's share of the overall progress, as fractions of the whole.</summary>
    private readonly record struct ProgressPhase(double Start, double End);

    /// <summary>What a failed swap had done, so the restore undoes exactly that.</summary>
    private sealed record SwapUndo(string StagingNative, string OldDir, string? OldSentinel, bool OldMoved, bool NewInPlace);
}
