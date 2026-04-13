using System.IO.Compression;
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
/// Option B end-user GPU acceleration: fetches the GPU-specific native DLLs for LLamaSharp from
/// nuget.org at user opt-in, extracts them into <c>%LOCALAPPDATA%/yaat/runtime/llama/runtimes/</c>,
/// and lets the app point <see cref="LLama.Native.NativeLibraryConfig.WithSearchDirectory"/> at
/// that folder so LLamaSharp picks up the GPU backend on next launch.
///
/// Current scope: <b>Vulkan only.</b> Vulkan works with any modern GPU driver (NVIDIA, AMD,
/// Intel Arc) and doesn't require a separate toolkit install — the <c>vulkan-1.dll</c> needed
/// at load time ships with the GPU vendor's graphics driver, which users already have.
///
/// CUDA is intentionally not offered here because the LLamaSharp.Backend.Cuda12 native DLLs have
/// a runtime dependency on <c>cudart64_12.dll</c> / <c>cublas64_12.dll</c> / <c>cublasLt64_12.dll</c>
/// which live in the CUDA Toolkit's <c>bin</c> directory — not in the NuGet package. End users
/// typically have the NVIDIA display driver (which provides <c>nvcuda.dll</c>) but NOT the CUDA
/// Toolkit. Supporting CUDA would require either bundling the runtime DLLs (license-murky) or
/// auto-detecting a matching CUDA install on the user's system. A follow-up session can add CUDA
/// as a power-user option once that detection is sorted.
///
/// Whisper.net is also intentionally not offered here because Whisper.net's native loading
/// convention (<c>build/win-x64/</c> instead of the standard NuGet <c>runtimes/win-x64/native/</c>)
/// differs from LLamaSharp and deserves its own investigation. Whisper on CPU is already near
/// real-time for the <c>base.en</c> model, so the GPU story is less urgent there.
///
/// Version pinning: the nuget download URL hardcodes version <see cref="LlamaSharpVersion"/> which
/// must track whatever the main Yaat.Client.csproj compiles against. Update both in lockstep.
/// </summary>
public sealed class GpuRuntimeDownloader
{
    private static readonly ILogger Log = AppLog.CreateLogger<GpuRuntimeDownloader>();

    // Must match the Yaat.Client.csproj LLamaSharp PackageReference version. Bumping LLamaSharp
    // in the csproj without bumping this constant will leave users with stale GPU natives that
    // may not be ABI-compatible with the managed library.
    private const string LlamaSharpVersion = "0.26.0";

    // nuget.org flat-container URL pattern. The response is a .nupkg file which is really a ZIP.
    private const string LlamaVulkanWindowsPackageId = "llamasharp.backend.vulkan.windows";

    /// <summary>Root for all downloaded GPU runtime files.</summary>
    public static readonly string RuntimeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "yaat",
        "runtime"
    );

    /// <summary>
    /// Root that <see cref="LLama.Native.NativeLibraryConfig.WithSearchDirectory"/> is pointed at.
    /// Native libraries land at <c>{LlamaSearchRoot}/runtimes/win-x64/native/{backend}/{*.dll}</c>
    /// — the same relative path LLamaSharp uses when loading from the app bin directory.
    /// </summary>
    public static readonly string LlamaSearchRoot = Path.Combine(RuntimeRoot, "llama");

    public GpuRuntimeStatus GetLlamaVulkanStatus()
    {
        var markerDll = Path.Combine(LlamaSearchRoot, "runtimes", "win-x64", "native", "vulkan", "llama.dll");
        return File.Exists(markerDll) ? GpuRuntimeStatus.Installed : GpuRuntimeStatus.NotInstalled;
    }

    /// <summary>
    /// Downloads <c>LLamaSharp.Backend.Vulkan.Windows</c> <see cref="LlamaSharpVersion"/> from
    /// nuget.org, extracts its native DLLs into <see cref="LlamaSearchRoot"/>, and returns true
    /// on success. Existing files are replaced atomically.
    /// </summary>
    public async Task<bool> DownloadLlamaVulkanRuntimeAsync(IProgress<double> progress, CancellationToken ct)
    {
        var tempNupkg = Path.Combine(LlamaSearchRoot, $".llama-vulkan-{LlamaSharpVersion}.nupkg.partial");
        try
        {
            Directory.CreateDirectory(LlamaSearchRoot);
            var url =
                $"https://api.nuget.org/v3-flatcontainer/{LlamaVulkanWindowsPackageId}/{LlamaSharpVersion}/{LlamaVulkanWindowsPackageId}.{LlamaSharpVersion}.nupkg";

            Log.LogInformation("Downloading LLamaSharp Vulkan runtime {Version} from {Url}", LlamaSharpVersion, url);

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var downloaded = 0L;
            var buffer = new byte[81920];

            if (File.Exists(tempNupkg))
            {
                File.Delete(tempNupkg);
            }

            await using (var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tempNupkg, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = await httpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;
                    if (totalBytes > 0)
                    {
                        // Reserve the last 5% of the progress bar for extraction so the UI shows
                        // something moving during the (fast but visible) zip unpack.
                        progress.Report(0.95 * downloaded / totalBytes);
                    }
                    else
                    {
                        progress.Report(double.NaN);
                    }
                }
            }

            Log.LogInformation("Downloaded {Bytes} bytes, extracting native DLLs", downloaded);
            ExtractLlamaBackendNatives(tempNupkg, LlamaSearchRoot);
            progress.Report(1.0);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("LLamaSharp Vulkan runtime download cancelled");
            return false;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to download LLamaSharp Vulkan runtime");
            return false;
        }
        finally
        {
            if (File.Exists(tempNupkg))
            {
                try
                {
                    File.Delete(tempNupkg);
                }
                catch (IOException ex)
                {
                    Log.LogWarning(ex, "Failed to delete temporary nupkg {Path}", tempNupkg);
                }
            }
        }
    }

    public bool DeleteLlamaVulkanRuntime()
    {
        var dir = Path.Combine(LlamaSearchRoot, "runtimes", "win-x64", "native", "vulkan");
        if (!Directory.Exists(dir))
        {
            return false;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
            Log.LogInformation("Deleted LLamaSharp Vulkan runtime at {Path}", dir);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to delete LLamaSharp Vulkan runtime at {Path}", dir);
            return false;
        }
    }

    /// <summary>
    /// Extracts native DLLs from a LLamaSharp backend nupkg into the search root, remapping the
    /// 0.26.0+ <c>LLamaSharpRuntimes/</c> top-level folder onto the standard <c>runtimes/</c> layout
    /// that LLamaSharp's <see cref="LLama.Native.NativeLibraryConfig"/> expects when <c>WithSearchDirectory</c>
    /// is pointed at our root.
    ///
    /// Older LLamaSharp backend packages (≤0.25.0) shipped DLLs under <c>runtimes/win-x64/native/{backend}/</c>
    /// which matches NuGet's standard native-library convention. Starting with 0.26.0, the
    /// LLamaSharp packaging was changed to <c>LLamaSharpRuntimes/win-x64/native/{backend}/</c>,
    /// presumably to avoid MSBuild's automatic <c>runtimes</c>-folder handling — but the managed
    /// loader still looks for <c>runtimes/...</c> when <c>WithSearchDirectory</c> is in effect.
    /// We bridge the two by stripping <c>LLamaSharpRuntimes/</c> and re-emitting as <c>runtimes/</c>.
    /// Entries from the package's other top-level folders (<c>build/</c>, <c>_rels/</c>, etc.) are
    /// skipped — we only want the platform natives.
    /// </summary>
    private static void ExtractLlamaBackendNatives(string nupkgPath, string destRoot)
    {
        const string customPrefix = "LLamaSharpRuntimes/";
        const string standardPrefix = "runtimes/";

        using var archive = ZipFile.OpenRead(nupkgPath);
        var extracted = 0;
        foreach (var entry in archive.Entries)
        {
            // Skip directories (zero-length, ends with '/')
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var rawName = entry.FullName.Replace('\\', '/');
            string? relative = null;
            if (rawName.StartsWith(customPrefix, StringComparison.OrdinalIgnoreCase))
            {
                relative = standardPrefix + rawName[customPrefix.Length..];
            }
            else if (rawName.StartsWith(standardPrefix, StringComparison.OrdinalIgnoreCase))
            {
                relative = rawName;
            }

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

        Log.LogInformation("Extracted {Count} native files from {Nupkg}", extracted, Path.GetFileName(nupkgPath));
    }
}
