using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;

namespace Yaat.Client.Services;

/// <summary>
/// Downloads and manages the LM-Kit CUDA 13 backend at runtime. The Yaat.Client.csproj used to
/// pin <c>LM-Kit.NET.Backend.Cuda13.Windows</c> directly, which bundled ~700 MB of native CUDA
/// libraries (<c>cublasLt64_13.dll</c> alone is 458 MB) into the single-file exe and blew the
/// Windows Velopack installer past 2 GB. We now ship only the base <c>LM-Kit.NET</c> package
/// (CPU / Vulkan / AVX) and let the user opt in to CUDA from Settings → Speech, at which point
/// this class streams three NuGet packages from nuget.org and stitches them into the layout
/// LM-Kit expects under <c>%LOCALAPPDATA%/yaat/backends/cuda13/</c>.
///
/// Directory layout after a successful install (empirically verified by the layout probe in
/// <c>tools/probe-lmkit-backend.ps1</c> before this class existed):
/// <code>
///   &lt;InstallRoot&gt;/
///       .installed                                  (sentinel: "BackendPackageId/BackendVersion; DepsVersion")
///       runtimes/win-x64/native/cuda13/
///           LM-Kit.llama.cuda13.dll
///           LM-Kit.ggml.cuda13.dll
///           LM-Kit.ggml.base.cuda13.dll
///           LM-Kit.ggml.backend.cpu.cuda13.dll
///           LM-Kit.ggml.backend.cuda13.dll          (~154 MB; the actual CUDA kernels)
///           cudart64_13.dll
///           cublas64_13.dll
///           cublasLt64_13.dll                       (~458 MB; stitched from Part0 + Part1)
/// </code>
/// <see cref="Program"/> points <see cref="LMKit.Global.Runtime.BackendDirectory"/> at
/// <see cref="InstallRoot"/> before <see cref="LMKit.Global.Runtime.Initialize"/> runs, which
/// causes LM-Kit to pick the Cuda13 backend over Vulkan on machines with an NVIDIA GPU. On
/// Linux and macOS the installer is inert — <see cref="IsPlatformSupported"/> returns false and
/// the Settings section hides itself.
///
/// The installer is single-flight: only one download can run at a time per process. Downloads
/// stream to <c>&lt;InstallRoot&gt;/.partial/</c> and are atomically promoted once every file
/// has been written — a crashed download leaves only the .partial tree, which gets cleaned up
/// at the start of the next install attempt.
/// </summary>
public sealed partial class CudaBackendInstaller : ObservableObject
{
    private static readonly ILogger Log = AppLog.CreateLogger<CudaBackendInstaller>();

    // Version pinning. The backend and the deps ship as separate packages with independent
    // cadences — LM-Kit's deps haven't had a new release since 2026.1.1 while the backend tracks
    // the main package version. We pin backend 2026.7.3 (lockstep with the LM-Kit.NET package
    // reference) + deps 2026.1.1; both are CUDA 13 so the deps' cublas/cudart runtime libs stay
    // compatible with the newer backend DLLs. When a newer deps release appears we'll bump both
    // together.
    public const string BackendPackageId = "LM-Kit.NET.Backend.Cuda13.Windows";
    public const string BackendVersion = "2026.7.3";
    public const string DepsPart0PackageId = "LM-Kit.NET.Backend.Cuda13.Deps.Windows.Part0";
    public const string DepsPart1PackageId = "LM-Kit.NET.Backend.Cuda13.Deps.Windows.Part1";
    public const string DepsVersion = "2026.1.1";

    private const string InstalledSentinelFileName = ".installed";
    private const string PartialDirName = ".partial";
    private const string NativeSubPath = "runtimes/win-x64/native/cuda13";
    private const string StitchedCublasLtName = "cublasLt64_13.dll";
    private const string CublasLtPart0Name = "cublasLt64_13.part0.dll";
    private const string CublasLtPart1Name = "cublasLt64_13.part1.dll";

    // Approximate bytes-on-disk after install, shown to the user before they click download so
    // they know the damage. Computed from the actual nupkg contents measured during the probe.
    public const long ApproxDownloadBytes = 534L * 1024 * 1024;
    public const long ApproxDiskBytes = 700L * 1024 * 1024;

    /// <summary>Root of the backend install. Lives outside the Velopack-managed app dir so
    /// installer upgrades don't nuke the user's downloaded backend.</summary>
    public static string InstallRoot => YaatPaths.Combine("backends", "cuda13");

    /// <summary>Directory where LM-Kit expects the native DLLs after extraction.</summary>
    public static string NativeDir => Path.Combine(InstallRoot, NativeSubPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Sentinel file written last after a successful install. Its presence is the
    /// authoritative signal that the backend is ready to use. Contents describe the installed
    /// versions so we can invalidate the install when the pinned versions change.</summary>
    public static string SentinelPath => Path.Combine(InstallRoot, InstalledSentinelFileName);

    /// <summary>Expected sentinel content for the currently-pinned version pair. When the
    /// installer is upgraded to newer versions this string changes, which invalidates existing
    /// installs and prompts the user to re-download.</summary>
    public static string ExpectedSentinelContent => $"{BackendPackageId}/{BackendVersion}; deps/{DepsVersion}";

    /// <summary>True on Windows, false elsewhere. LM-Kit only ships a CUDA 13 package for
    /// Windows; Linux and macOS fall back to Vulkan / Metal via the base <c>LM-Kit.NET</c>
    /// package and have nothing to download.</summary>
    public static bool IsPlatformSupported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>True when the sentinel file exists and matches the currently-pinned versions.
    /// Called at startup by <see cref="Program"/> to decide whether to set
    /// <see cref="LMKit.Global.Runtime.BackendDirectory"/>, and by Settings to render the initial
    /// button state. Safe to call from any thread — it just stats a file.</summary>
    public static bool IsInstalledOnDisk()
    {
        if (!IsPlatformSupported)
        {
            return false;
        }
        try
        {
            if (!File.Exists(SentinelPath))
            {
                return false;
            }
            var content = File.ReadAllText(SentinelPath).Trim();
            return string.Equals(content, ExpectedSentinelContent, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to read CUDA backend sentinel at {Path}", SentinelPath);
            return false;
        }
    }

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public CudaBackendInstaller(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _isInstalled = IsInstalledOnDisk();
    }

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>0.0 to 1.0 across the three-package download + stitch sequence. Updated from the
    /// install worker thread via <see cref="SetProgress"/>; bindings marshal to the UI thread.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>Short status text for the UI — "Downloading Part 1 of 3…", "Stitching…",
    /// "Installed", "Failed: &lt;reason&gt;". Never null after first use.</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>
    /// Downloads and installs the backend. Single-flight; a second concurrent call throws
    /// <see cref="InvalidOperationException"/> rather than queuing behind the first. Observable
    /// state is set from the background thread — bind with <c>Binding</c> defaults to get the
    /// marshalling to the UI thread.
    /// </summary>
    public async Task InstallAsync(CancellationToken ct)
    {
        if (!IsPlatformSupported)
        {
            throw new InvalidOperationException("CUDA backend install is Windows x64 only.");
        }
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
            IsInstalled = IsInstalledOnDisk();
            StatusMessage = IsInstalled ? "Installed. Restart YAAT to activate CUDA acceleration." : "Install failed — see yaat-client.log.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
            SafeDeletePartial();
            throw;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "CUDA backend install failed");
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

    /// <summary>Removes the installed backend. Harmless if nothing is installed. Current
    /// runtime keeps using whatever backend LM-Kit selected at startup — the user needs to
    /// restart for Vulkan / CPU to take over.</summary>
    public void Uninstall()
    {
        if (!_lock.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("Install in progress; cannot uninstall.");
        }
        try
        {
            if (Directory.Exists(InstallRoot))
            {
                Directory.Delete(InstallRoot, recursive: true);
                Log.LogInformation("Removed CUDA backend install at {Root}", InstallRoot);
            }
            IsInstalled = false;
            StatusMessage = "Removed. Restart YAAT to fall back to Vulkan / CPU.";
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RunInstallAsync(CancellationToken ct)
    {
        // Clean any half-finished partial from a previous crashed run. We don't try to resume
        // because the three-package stream costs a few minutes and the user already clicked
        // download knowing the size.
        SafeDeletePartial();

        var partialRoot = Path.Combine(InstallRoot, PartialDirName);
        var nativeDirInPartial = Path.Combine(partialRoot, NativeSubPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(nativeDirInPartial);

        // Phase 1/4: main backend nupkg (~148 MB). Contains 5 LM-Kit DLLs under
        // runtimes/win-x64/native/cuda13/. Extract directly into the partial tree.
        StatusMessage = "Downloading LM-Kit CUDA backend (1 of 3)…";
        await DownloadAndExtractAsync(
                packageId: BackendPackageId,
                version: BackendVersion,
                destRoot: partialRoot,
                filter: entry => entry.FullName.StartsWith($"{NativeSubPath}/", StringComparison.OrdinalIgnoreCase),
                phaseStart: 0.00,
                phaseEnd: 0.30,
                ct: ct
            )
            .ConfigureAwait(false);

        // Phase 2/4: Deps.Part0 (~192 MB). Contains cublas64_13.dll + cudart64_13.dll under
        // runtimes/…/cuda13/, plus cublasLt64_13.part0.dll under part/…/cuda13/. We need both
        // trees — the runtimes bits drop in alongside the LM-Kit DLLs, the .part0 lands in a
        // scratch dir until we stitch it with Part1.
        StatusMessage = "Downloading CUDA runtime (2 of 3)…";
        var stitchDir = Path.Combine(partialRoot, ".stitch");
        Directory.CreateDirectory(stitchDir);
        await DownloadAndExtractAsync(
                packageId: DepsPart0PackageId,
                version: DepsVersion,
                destRoot: partialRoot,
                filter: entry => entry.FullName.StartsWith($"{NativeSubPath}/", StringComparison.OrdinalIgnoreCase),
                phaseStart: 0.30,
                phaseEnd: 0.55,
                ct: ct
            )
            .ConfigureAwait(false);
        await DownloadAndExtractAsync(
                packageId: DepsPart0PackageId,
                version: DepsVersion,
                destRoot: stitchDir,
                filter: entry => entry.FullName.EndsWith(CublasLtPart0Name, StringComparison.OrdinalIgnoreCase),
                phaseStart: 0.55,
                phaseEnd: 0.70,
                ct: ct
            )
            .ConfigureAwait(false);

        // Phase 3/4: Deps.Part1 (~194 MB). Only contains the second half of cublasLt64_13.dll.
        StatusMessage = "Downloading CUDA runtime (3 of 3)…";
        await DownloadAndExtractAsync(
                packageId: DepsPart1PackageId,
                version: DepsVersion,
                destRoot: stitchDir,
                filter: entry => entry.FullName.EndsWith(CublasLtPart1Name, StringComparison.OrdinalIgnoreCase),
                phaseStart: 0.70,
                phaseEnd: 0.95,
                ct: ct
            )
            .ConfigureAwait(false);

        // Phase 4/4: stitch the 458 MB cublasLt DLL back together. The .targets file inside the
        // Deps.Windows nupkg does this at build time in production NuGet consumers; we replicate
        // the same concat here.
        StatusMessage = "Stitching CUDA runtime…";
        SetProgress(0.95);
        StitchCublasLt(stitchDir, nativeDirInPartial);
        Directory.Delete(stitchDir, recursive: true);
        SetProgress(0.99);

        // Sentinel is the last write so IsInstalledOnDisk() cannot accidentally report success
        // on a partial install. Writing it to the partial tree and then moving the whole tree
        // atomically would be ideal, but directory rename across existing dirs is fiddly on
        // Windows. Instead, materialize the final layout under InstallRoot and then write the
        // sentinel.
        MaterializeFromPartial(partialRoot);
        File.WriteAllText(SentinelPath, ExpectedSentinelContent);
        SetProgress(1.00);
    }

    private async Task DownloadAndExtractAsync(
        string packageId,
        string version,
        string destRoot,
        Func<ZipArchiveEntry, bool> filter,
        double phaseStart,
        double phaseEnd,
        CancellationToken ct
    )
    {
        var url = $"https://www.nuget.org/api/v2/package/{packageId}/{version}";
        Log.LogInformation("Downloading {PackageId} {Version} from {Url}", packageId, version, url);

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        // ZipArchive needs a seekable stream, so stream the nupkg to a temp file first. The
        // halfway point of each phase is reserved for the download; the other half for the
        // extract.
        var nupkgPath = Path.Combine(destRoot, $"_{packageId}.nupkg.part");
        long bytesRead = 0;
        var mid = (phaseStart + phaseEnd) / 2.0;
        using (var file = File.Create(nupkgPath))
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                bytesRead += read;
                if (total.HasValue && total.Value > 0)
                {
                    var fraction = (double)bytesRead / total.Value;
                    SetProgress(phaseStart + ((mid - phaseStart) * fraction));
                }
            }
        }

        using (var zip = ZipFile.OpenRead(nupkgPath))
        {
            var matching = zip.Entries.Where(filter).ToList();
            var totalExtractBytes = matching.Sum(e => e.Length);
            long extractedBytes = 0;
            foreach (var entry in matching)
            {
                ct.ThrowIfCancellationRequested();
                // Normalize the zip path to local separators before Path.Combine — otherwise we
                // build `<root>\runtimes/win-x64/…`, which Avalonia later rejects with a
                // platform mismatch when it tries to open it.
                var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destPath = Path.Combine(destRoot, normalized);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using var src = entry.Open();
                using var dst = File.Create(destPath);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                extractedBytes += entry.Length;
                if (totalExtractBytes > 0)
                {
                    var fraction = (double)extractedBytes / totalExtractBytes;
                    SetProgress(mid + ((phaseEnd - mid) * fraction));
                }
            }
        }

        File.Delete(nupkgPath);
    }

    private static void StitchCublasLt(string stitchDir, string nativeDir)
    {
        // Find the two .part files under whatever subpath ZipArchive wrote them. Part0's path
        // inside the nupkg is part/win-x64/native/cuda13/cublasLt64_13.part0.dll; Part1 is the
        // same with .part1.
        var part0 =
            Directory.EnumerateFiles(stitchDir, CublasLtPart0Name, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"{CublasLtPart0Name} missing after download");
        var part1 =
            Directory.EnumerateFiles(stitchDir, CublasLtPart1Name, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"{CublasLtPart1Name} missing after download");

        var output = Path.Combine(nativeDir, StitchedCublasLtName);
        using var outStream = File.Create(output);
        foreach (var partPath in new[] { part0, part1 })
        {
            using var inStream = File.OpenRead(partPath);
            inStream.CopyTo(outStream);
        }
    }

    private static void MaterializeFromPartial(string partialRoot)
    {
        // Move every file from <InstallRoot>/.partial/** to <InstallRoot>/**. We deliberately
        // do NOT use Directory.Move on the partial tree as a whole because InstallRoot might
        // already exist from a prior partial install. Instead, walk the tree and move each file,
        // overwriting any stale file at the destination.
        foreach (var src in Directory.EnumerateFiles(partialRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(partialRoot, src);
            var dst = Path.Combine(InstallRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (File.Exists(dst))
            {
                File.Delete(dst);
            }
            File.Move(src, dst);
        }
        Directory.Delete(partialRoot, recursive: true);
    }

    private void SafeDeletePartial()
    {
        var partialRoot = Path.Combine(InstallRoot, PartialDirName);
        if (!Directory.Exists(partialRoot))
        {
            return;
        }
        try
        {
            Directory.Delete(partialRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to clean partial install dir at {Path}", partialRoot);
        }
    }

    private void SetProgress(double value)
    {
        // Quantize to 0.1% so a 500 MB download doesn't fire 10,000 PropertyChanged events
        // into Avalonia's binding pipeline. [ObservableProperty] compares with
        // EqualityComparer<double>.Default so an unchanged rounded value is a no-op.
        Progress = Math.Round(Math.Clamp(value, 0.0, 1.0), 3);
    }
}
