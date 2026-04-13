using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

public enum GpuRuntimeStatus
{
    NotInstalled,
    Downloading,
    Installed,
    Failed,
}

/// <summary>
/// Snapshot of a CUDA Toolkit 12.x installation the user has on their system. Required for any
/// runtime that links against <c>cudart64_12.dll</c> / <c>cublas64_12.dll</c> / <c>cublasLt64_12.dll</c>,
/// which are part of the CUDA Toolkit and not shipped in any LLamaSharp / Whisper.net NuGet
/// backend package. End users with just the NVIDIA display driver won't have these; they need a
/// separate toolkit install (Custom → runtime libraries only is ~300 MB).
/// </summary>
public sealed record CudaToolkitInfo(string InstallPath, string BinPath, int MinorVersion);

/// <summary>
/// Option B end-user GPU acceleration: fetches GPU-specific native libraries for LLamaSharp and
/// Whisper.net from nuget.org at user opt-in, extracts them into <c>%LOCALAPPDATA%/yaat/runtime/{llama,whisper}/</c>,
/// and lets the app point the two managed loaders at those folders on next launch.
///
/// <para><b>Supported backends this session:</b></para>
/// <list type="bullet">
///   <item><description><b>LLamaSharp Vulkan</b> — works with any modern GPU driver, no toolkit needed.</description></item>
///   <item><description><b>LLamaSharp CUDA 12</b> — NVIDIA only, requires CUDA Toolkit 12.x installed
///     (gated on <see cref="CudaToolkit"/> detection).</description></item>
///   <item><description><b>Whisper.net Vulkan</b> — any GPU driver.</description></item>
///   <item><description><b>Whisper.net CUDA</b> — NVIDIA + CUDA Toolkit.</description></item>
/// </list>
///
/// <para><b>Native library loader hooks:</b></para>
/// <list type="bullet">
///   <item><description>LLamaSharp: <see cref="LLama.Native.NativeLibraryConfig.WithSearchDirectory"/>
///     tells the loader to probe an extra root for <c>runtimes/{os}/native/{backend}/llama.dll</c>.</description></item>
///   <item><description>Whisper.net: <see cref="Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath"/>
///     — setting this to <i>any file</i> under a search root causes Whisper.net to probe
///     <c>{GetDirectoryName(LibraryPath)}/runtimes/{runtime}/{os}-{arch}/whisper.dll</c>. We set it
///     at app startup in <c>Program.Main</c> to <see cref="WhisperSearchRoot"/><c>/whisper.placeholder</c>.</description></item>
/// </list>
///
/// <para><b>Version pinning:</b></para>
/// <see cref="LlamaSharpVersion"/> and <see cref="WhisperNetVersion"/> must track the managed
/// LLamaSharp / Whisper.net <c>PackageReference</c> versions in <c>Yaat.Client.csproj</c>. Bumping
/// the managed package without bumping these constants leaves users with stale GPU natives that
/// may not be ABI-compatible with the managed library.
/// </summary>
public sealed class GpuRuntimeDownloader
{
    private static readonly ILogger Log = AppLog.CreateLogger<GpuRuntimeDownloader>();

    // These MUST match the Yaat.Client.csproj PackageReference versions.
    private const string LlamaSharpVersion = "0.26.0";
    private const string WhisperNetVersion = "1.9.0";

    // NuGet package IDs for the GPU backends we fetch on demand.
    private const string LlamaVulkanWindowsPackageId = "llamasharp.backend.vulkan.windows";
    private const string LlamaCuda12WindowsPackageId = "llamasharp.backend.cuda12.windows";
    private const string WhisperVulkanPackageId = "whisper.net.runtime.vulkan";
    private const string WhisperCudaWindowsPackageId = "whisper.net.runtime.cuda.windows";

    public static readonly string RuntimeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "yaat",
        "runtime"
    );

    public static readonly string LlamaSearchRoot = Path.Combine(RuntimeRoot, "llama");
    public static readonly string WhisperSearchRoot = Path.Combine(RuntimeRoot, "whisper");

    // ---------- CUDA Toolkit detection ----------

    /// <summary>
    /// Scans for a CUDA Toolkit 12.x installation under the standard Windows install root
    /// (<c>C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.*</c>) and returns the highest
    /// installed minor version. Returns <c>null</c> on non-Windows or when no 12.x install is
    /// present. End users with just the NVIDIA display driver will get <c>null</c> here — they
    /// need to install the CUDA Toolkit runtime libraries before any CUDA backend will load.
    /// </summary>
    public static CudaToolkitInfo? FindCuda12Toolkit()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        const string windowsCudaRoot = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
        if (!Directory.Exists(windowsCudaRoot))
        {
            return null;
        }

        string? best = null;
        var bestMinor = -1;
        foreach (var dir in Directory.GetDirectories(windowsCudaRoot, "v12.*"))
        {
            var name = Path.GetFileName(dir);
            if (name.Length < 5)
            {
                continue;
            }

            var minorPart = name[4..];
            if (int.TryParse(minorPart, out var minor) && minor > bestMinor)
            {
                best = dir;
                bestMinor = minor;
            }
        }

        if (best is null)
        {
            return null;
        }

        var binPath = Path.Combine(best, "bin");
        if (!Directory.Exists(binPath))
        {
            return null;
        }

        return new CudaToolkitInfo(best, binPath, bestMinor);
    }

    /// <summary>
    /// Applies a detected CUDA Toolkit to the current process: overrides <c>CUDA_PATH</c> so
    /// LLamaSharp's <c>SystemInfo.GetCudaMajorVersion</c> picks the v12 folder, and prepends the
    /// toolkit's <c>bin/</c> directory to <c>PATH</c> so Windows' <c>LoadLibrary</c> can resolve
    /// <c>cudart64_12.dll</c> / <c>cublas64_12.dll</c> / <c>cublasLt64_12.dll</c> when it loads
    /// <c>ggml-cuda.dll</c>.
    ///
    /// Process-level only — no system env var changes, no effect on other running tools. Safe to
    /// call from <c>Program.Main</c> before any LLamaSharp weight load.
    /// </summary>
    public static bool ApplyCudaToolkitToProcess(CudaToolkitInfo toolkit)
    {
        try
        {
            Environment.SetEnvironmentVariable("CUDA_PATH", toolkit.InstallPath);
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Contains(toolkit.BinPath, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", toolkit.BinPath + Path.PathSeparator + currentPath);
            }

            Log.LogInformation("CUDA Toolkit 12.{Minor} applied to process: CUDA_PATH={Path}", toolkit.MinorVersion, toolkit.InstallPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to apply CUDA Toolkit {Path} to process", toolkit.InstallPath);
            return false;
        }
    }

    // ---------- LLamaSharp backends ----------

    public GpuRuntimeStatus GetLlamaVulkanStatus()
    {
        return File.Exists(Path.Combine(LlamaSearchRoot, "runtimes", "win-x64", "native", "vulkan", "llama.dll"))
            ? GpuRuntimeStatus.Installed
            : GpuRuntimeStatus.NotInstalled;
    }

    public GpuRuntimeStatus GetLlamaCudaStatus()
    {
        return File.Exists(Path.Combine(LlamaSearchRoot, "runtimes", "win-x64", "native", "cuda12", "llama.dll"))
            ? GpuRuntimeStatus.Installed
            : GpuRuntimeStatus.NotInstalled;
    }

    public Task<bool> DownloadLlamaVulkanRuntimeAsync(IProgress<double> progress, CancellationToken ct)
    {
        return DownloadLlamaBackendAsync(LlamaVulkanWindowsPackageId, "Vulkan", progress, ct);
    }

    public Task<bool> DownloadLlamaCudaRuntimeAsync(IProgress<double> progress, CancellationToken ct)
    {
        return DownloadLlamaBackendAsync(LlamaCuda12WindowsPackageId, "CUDA 12", progress, ct);
    }

    public bool DeleteLlamaVulkanRuntime()
    {
        return DeleteDirIfExists(Path.Combine(LlamaSearchRoot, "runtimes", "win-x64", "native", "vulkan"), "LLamaSharp Vulkan runtime");
    }

    public bool DeleteLlamaCudaRuntime()
    {
        return DeleteDirIfExists(Path.Combine(LlamaSearchRoot, "runtimes", "win-x64", "native", "cuda12"), "LLamaSharp CUDA runtime");
    }

    // ---------- Whisper.net backends ----------

    public GpuRuntimeStatus GetWhisperVulkanStatus()
    {
        return File.Exists(Path.Combine(WhisperSearchRoot, "runtimes", "vulkan", "win-x64", "whisper.dll"))
            ? GpuRuntimeStatus.Installed
            : GpuRuntimeStatus.NotInstalled;
    }

    public GpuRuntimeStatus GetWhisperCudaStatus()
    {
        return File.Exists(Path.Combine(WhisperSearchRoot, "runtimes", "cuda", "win-x64", "whisper.dll"))
            ? GpuRuntimeStatus.Installed
            : GpuRuntimeStatus.NotInstalled;
    }

    public Task<bool> DownloadWhisperVulkanRuntimeAsync(IProgress<double> progress, CancellationToken ct)
    {
        return DownloadWhisperBackendAsync(WhisperVulkanPackageId, "vulkan", "Vulkan", progress, ct);
    }

    public Task<bool> DownloadWhisperCudaRuntimeAsync(IProgress<double> progress, CancellationToken ct)
    {
        return DownloadWhisperBackendAsync(WhisperCudaWindowsPackageId, "cuda", "CUDA", progress, ct);
    }

    public bool DeleteWhisperVulkanRuntime()
    {
        return DeleteDirIfExists(Path.Combine(WhisperSearchRoot, "runtimes", "vulkan"), "Whisper.net Vulkan runtime");
    }

    public bool DeleteWhisperCudaRuntime()
    {
        return DeleteDirIfExists(Path.Combine(WhisperSearchRoot, "runtimes", "cuda"), "Whisper.net CUDA runtime");
    }

    // ---------- Shared download + extract plumbing ----------

    private async Task<bool> DownloadLlamaBackendAsync(string packageId, string label, IProgress<double> progress, CancellationToken ct)
    {
        var tempNupkg = Path.Combine(LlamaSearchRoot, $".{packageId}-{LlamaSharpVersion}.nupkg.partial");
        var url = $"https://api.nuget.org/v3-flatcontainer/{packageId}/{LlamaSharpVersion}/{packageId}.{LlamaSharpVersion}.nupkg";
        try
        {
            Directory.CreateDirectory(LlamaSearchRoot);
            await StreamNupkgAsync(url, tempNupkg, $"LLamaSharp {label} runtime {LlamaSharpVersion}", progress, ct).ConfigureAwait(false);
            ExtractLlamaBackendNatives(tempNupkg, LlamaSearchRoot);
            progress.Report(1.0);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("LLamaSharp {Label} runtime download cancelled", label);
            return false;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to download LLamaSharp {Label} runtime", label);
            return false;
        }
        finally
        {
            TryDelete(tempNupkg);
        }
    }

    private async Task<bool> DownloadWhisperBackendAsync(
        string packageId,
        string runtimeFolderName,
        string label,
        IProgress<double> progress,
        CancellationToken ct
    )
    {
        var tempNupkg = Path.Combine(WhisperSearchRoot, $".{packageId}-{WhisperNetVersion}.nupkg.partial");
        var url = $"https://api.nuget.org/v3-flatcontainer/{packageId}/{WhisperNetVersion}/{packageId}.{WhisperNetVersion}.nupkg";
        try
        {
            Directory.CreateDirectory(WhisperSearchRoot);
            await StreamNupkgAsync(url, tempNupkg, $"Whisper.net {label} runtime {WhisperNetVersion}", progress, ct).ConfigureAwait(false);
            ExtractWhisperBackendNatives(tempNupkg, WhisperSearchRoot, runtimeFolderName);
            progress.Report(1.0);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("Whisper.net {Label} runtime download cancelled", label);
            return false;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to download Whisper.net {Label} runtime", label);
            return false;
        }
        finally
        {
            TryDelete(tempNupkg);
        }
    }

    private static async Task StreamNupkgAsync(string url, string tempPath, string logLabel, IProgress<double> progress, CancellationToken ct)
    {
        Log.LogInformation("Downloading {Label} from {Url}", logLabel, url);
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var downloaded = 0L;
        var buffer = new byte[81920];

        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

        int read;
        while ((read = await httpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            if (totalBytes > 0)
            {
                // Reserve the last 5% for the extraction step; 0..0.95 for the download itself.
                progress.Report(0.95 * downloaded / totalBytes);
            }
            else
            {
                progress.Report(double.NaN);
            }
        }

        Log.LogInformation("{Label}: downloaded {Bytes} bytes", logLabel, downloaded);
    }

    /// <summary>
    /// Extracts LLamaSharp backend native files from a .nupkg, remapping the 0.26.0+
    /// <c>LLamaSharpRuntimes/</c> top-level folder onto the standard <c>runtimes/</c> layout that
    /// <see cref="LLama.Native.NativeLibraryConfig.WithSearchDirectory"/> expects. Older backend
    /// packages (≤0.25.0) already use <c>runtimes/</c> and pass through unchanged.
    /// </summary>
    private static void ExtractLlamaBackendNatives(string nupkgPath, string destRoot)
    {
        const string customPrefix = "LLamaSharpRuntimes/";
        const string standardPrefix = "runtimes/";

        ExtractZipEntries(
            nupkgPath,
            destRoot,
            entryPath =>
            {
                if (entryPath.StartsWith(customPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return standardPrefix + entryPath[customPrefix.Length..];
                }

                if (entryPath.StartsWith(standardPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return entryPath;
                }

                return null;
            }
        );
    }

    /// <summary>
    /// Extracts Whisper.net backend native files from a .nupkg, remapping the package's
    /// <c>build/{os}-{arch}/</c> layout onto the <c>runtimes/{runtimeFolder}/{os}-{arch}/</c>
    /// layout that Whisper.net's <c>NativeLibraryLoader.GetRuntimePaths</c> expects. The
    /// mapping mirrors what Whisper.net's <c>.targets</c> MSBuild file does at compile time —
    /// we're just replicating it at download time without touching MSBuild.
    /// </summary>
    private static void ExtractWhisperBackendNatives(string nupkgPath, string destRoot, string runtimeFolder)
    {
        const string buildPrefix = "build/";

        ExtractZipEntries(
            nupkgPath,
            destRoot,
            entryPath =>
            {
                if (!entryPath.StartsWith(buildPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // Only take .dll files (plus linux .so / macos .dylib for completeness, though we
                // only exercise Windows at runtime today).
                if (
                    !entryPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !entryPath.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                    && !entryPath.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
                )
                {
                    return null;
                }

                // Entries look like: build/win-x64/whisper.dll → runtimes/{runtimeFolder}/win-x64/whisper.dll
                var rest = entryPath[buildPrefix.Length..];
                return $"runtimes/{runtimeFolder}/{rest}";
            }
        );
    }

    private static void ExtractZipEntries(string nupkgPath, string destRoot, Func<string, string?> pathRemapper)
    {
        using var archive = ZipFile.OpenRead(nupkgPath);
        var extracted = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var rawName = entry.FullName.Replace('\\', '/');
            var relative = pathRemapper(rawName);
            if (relative is null)
            {
                continue;
            }

            var destPath = Path.Combine(destRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            entry.ExtractToFile(destPath, overwrite: true);
            extracted++;
        }

        Log.LogInformation("Extracted {Count} files from {Nupkg}", extracted, Path.GetFileName(nupkgPath));
    }

    private static bool DeleteDirIfExists(string dir, string logLabel)
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
            Log.LogInformation("Deleted {Label} at {Path}", logLabel, dir);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to delete {Label} at {Path}", logLabel, dir);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Log.LogWarning(ex, "Failed to delete temporary file {Path}", path);
        }
    }
}
