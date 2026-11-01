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
/// Downloads stream to <c>&lt;installRoot&gt;/.partial/</c>; the finished <c>native/</c> folder replaces the old one
/// and the sentinel is written last, so <see cref="IsInstalledOnDisk"/> never reports a half-built set. The installer is
/// single-flight per instance.
/// </summary>
/// <param name="http">The client the archives are downloaded with.</param>
/// <param name="installRoot">The folder the set is installed into; production passes <see cref="InstallRootDefault"/>.</param>
/// <param name="downloads">The archives the set is built from, usually <see cref="ForCurrentPlatform"/>'s list.</param>
public sealed partial class SherpaCudaInstaller(HttpClient http, string installRoot, IReadOnlyList<SherpaCudaDownload> downloads) : ObservableObject
{
    private static readonly ILogger Log = AppLog.CreateLogger<SherpaCudaInstaller>();

    private const string InstalledSentinelFileName = ".installed";
    private const string PartialDirName = ".partial";

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
    /// <exception cref="InvalidDataException">The manifest lists no CUDA native for the platform.</exception>
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
    /// Downloads and installs the set. Single-flight; a second concurrent call throws
    /// <see cref="InvalidOperationException"/>. A failure leaves no <c>.partial</c> folder and no sentinel.
    /// </summary>
    /// <param name="ct">Cancels the install.</param>
    /// <exception cref="InvalidDataException">An archive failed its size or SHA-256 check at every URL, or lacks an entry.</exception>
    /// <exception cref="HttpRequestException">Every URL of an archive failed, the last with an HTTP error.</exception>
    public async Task InstallAsync(CancellationToken ct)
    {
        if (!_lock.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("Install already in progress.");
        }
        try
        {
            IsBusy = true;
            StatusMessage = "Preparing…";
            Progress = 0;
            await RunInstallAsync(ct).ConfigureAwait(false);
            IsInstalled = IsInstalledOnDisk(_installRoot, _downloads);
            StatusMessage = IsInstalled ? "Installed. Restart YAAT to use the GPU." : "Install failed — see yaat-client.log.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
            SafeDeletePartial();
            throw;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Sherpa CUDA native install failed");
            StatusMessage = $"Failed: {ex.Message}";
            SafeDeletePartial();
            throw;
        }
        finally
        {
            IsBusy = false;
            _lock.Release();
        }
    }

    /// <summary>Removes the installed set. Harmless if nothing is installed; the running process keeps what it loaded.</summary>
    public void Uninstall()
    {
        if (!_lock.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("Install in progress; cannot uninstall.");
        }
        try
        {
            if (Directory.Exists(_installRoot))
            {
                Directory.Delete(_installRoot, recursive: true);
                Log.LogInformation("Removed the sherpa CUDA native set at {Root}", _installRoot);
            }
            IsInstalled = false;
            StatusMessage = "Removed. Restart YAAT to fall back to the CPU.";
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string SentinelPath(string installRoot) => Path.Combine(installRoot, InstalledSentinelFileName);

    private static string ExpectedSentinelContent(IReadOnlyList<SherpaCudaDownload> downloads) =>
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

    private static SherpaCudaDownload ReadOurZip(string rid, string manifestPath, string[] entries)
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

    private async Task RunInstallAsync(CancellationToken ct)
    {
        SafeDeletePartial();
        string sentinelPath = SentinelPath(_installRoot);
        if (File.Exists(sentinelPath))
        {
            File.Delete(sentinelPath);
        }

        string partialRoot = Path.Combine(_installRoot, PartialDirName);
        string partialNative = Path.Combine(partialRoot, NativeDirName);
        Directory.CreateDirectory(partialNative);

        double totalBytes = Math.Max(1, ApproxDownloadBytes);
        long doneBytes = 0;
        for (int i = 0; i < _downloads.Count; i++)
        {
            SherpaCudaDownload download = _downloads[i];
            StatusMessage = $"Downloading {download.Name} ({i + 1} of {_downloads.Count})…";
            string archivePath = Path.Combine(partialRoot, $"_{download.Name}.part");
            double phaseStart = doneBytes / totalBytes;
            double phaseEnd = (doneBytes + download.Size) / totalBytes;
            string url = await DownloadFromFirstGoodUrlAsync(download, archivePath, phaseStart, phaseEnd, ct).ConfigureAwait(false);
            ExtractEntries(download, archivePath, url, partialNative, ct);
            File.Delete(archivePath);
            doneBytes += download.Size;
        }

        string nativeDir = Path.Combine(_installRoot, NativeDirName);
        if (Directory.Exists(nativeDir))
        {
            Directory.Delete(nativeDir, recursive: true);
        }
        Directory.Move(partialNative, nativeDir);
        Directory.Delete(partialRoot, recursive: true);
        if (OperatingSystem.IsLinux())
        {
            MarkSharedObjectsExecutable(nativeDir);
        }

        File.WriteAllText(sentinelPath, ExpectedSentinelContent(_downloads));
        SetProgress(1.0);
    }

    private async Task<string> DownloadFromFirstGoodUrlAsync(
        SherpaCudaDownload download,
        string archivePath,
        double phaseStart,
        double phaseEnd,
        CancellationToken ct
    )
    {
        List<string> failures = [];
        Exception? lastFailure = null;
        foreach (string url in download.Urls)
        {
            try
            {
                await DownloadVerifiedAsync(download, url, archivePath, phaseStart, phaseEnd, ct).ConfigureAwait(false);
                return url;
            }
            catch (Exception ex) when (ex is HttpRequestException or HttpIOException or InvalidDataException)
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

    private async Task DownloadVerifiedAsync(
        SherpaCudaDownload download,
        string url,
        string archivePath,
        double phaseStart,
        double phaseEnd,
        CancellationToken ct
    )
    {
        Log.LogInformation("Downloading {Name} from {Url}", download.Name, url);
        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytesRead = 0;
        await using (Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (FileStream file = File.Create(archivePath))
        {
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                bytesRead += read;
                SetProgress(phaseStart + ((phaseEnd - phaseStart) * Math.Min(1.0, (double)bytesRead / Math.Max(1, download.Size))));
            }
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

    private void SafeDeletePartial()
    {
        string partialRoot = Path.Combine(_installRoot, PartialDirName);
        if (!Directory.Exists(partialRoot))
        {
            return;
        }
        try
        {
            Directory.Delete(partialRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Failed to clean the partial sherpa CUDA install at {Path}", partialRoot);
        }
    }

    private void SetProgress(double value) => Progress = Math.Round(Math.Clamp(value, 0.0, 1.0), 3);
}
