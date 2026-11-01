using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;

namespace Yaat.Client.Services.Speech;

/// <summary>Which sherpa-onnx native libraries the process runs on.</summary>
public enum SherpaNativeSet
{
    /// <summary>The CPU set shipped beside the app.</summary>
    Cpu,

    /// <summary>The CUDA set <see cref="SherpaCudaInstaller"/> installs.</summary>
    Cuda,
}

/// <summary>
/// Points sherpa-onnx at its native set before anything touches it. The CPU set needs nothing: default probing finds
/// it beside the app. The CUDA set is preloaded by absolute path, dependencies first, so a missing cuBLAS or cuDNN fails
/// here where the caller falls back to the CPU set. The provider's file is checked at this load, but the provider itself
/// is loaded by ONNX Runtime when the recognizer is created, since its DllMain needs ONNX Runtime's provider host; its
/// import closure (cuBLAS, cuBLASLt) and cuDNN are preloaded here and bind to the installed copies by base name (Windows)
/// or soname (glibc). The sherpa-onnx imports of both managed assemblies resolve to the CUDA set, and no process-wide
/// search path is changed.
/// </summary>
public static class SherpaNativeLoader
{
    private const string CApiLibraryName = "sherpa-onnx-c-api";

    private static readonly ILogger Log = AppLog.CreateLogger("SherpaNativeLoader");

    private static readonly Lock Gate = new();
    private static bool _cudaSetUsed;

    /// <summary>The set this process loaded; <see cref="SherpaNativeSet.Cpu"/> until <see cref="UseCudaSet"/> runs.</summary>
    public static SherpaNativeSet LoadedSet { get; private set; } = SherpaNativeSet.Cpu;

    /// <summary>The set to load: CUDA only when it is installed and the user chose the GPU.</summary>
    /// <param name="cudaInstalled">Whether the CUDA set is installed and matches the pinned downloads.</param>
    /// <param name="device">The user's speech-recognition device.</param>
    public static SherpaNativeSet Choose(bool cudaInstalled, SttDevice device) =>
        (cudaInstalled && (device == SttDevice.Gpu)) ? SherpaNativeSet.Cuda : SherpaNativeSet.Cpu;

    /// <summary>
    /// Loads the CUDA set from <paramref name="nativeDir"/> and routes sherpa-onnx's imports to it. Call before any
    /// sherpa-onnx code runs, at most once per process. A failed call frees what it loaded and leaves the loader as if it
    /// had never been called, so the CPU set stays usable.
    /// </summary>
    /// <param name="nativeDir">The installed set's <c>native</c> folder.</param>
    /// <exception cref="InvalidOperationException">The CUDA set was already loaded in this process.</exception>
    /// <exception cref="FileNotFoundException">A library the set needs is missing from <paramref name="nativeDir"/>.</exception>
    public static void UseCudaSet(string nativeDir)
    {
        lock (Gate)
        {
            if (_cudaSetUsed)
            {
                throw new InvalidOperationException("The sherpa CUDA native set is already loaded in this process.");
            }

            RequiredPath(nativeDir, OperatingSystem.IsWindows() ? "onnxruntime_providers_cuda.dll" : "libonnxruntime_providers_cuda.so");
            List<string> paths = PreloadOrder(nativeDir);
            IntPtr cApi = LoadAllOrNone(paths);

            nint resolver(string libraryName, System.Reflection.Assembly _1, DllImportSearchPath? _2) =>
                (libraryName == CApiLibraryName) ? cApi : IntPtr.Zero;
            NativeLibrary.SetDllImportResolver(typeof(SherpaSttEngine).Assembly, resolver);
            NativeLibrary.SetDllImportResolver(typeof(SherpaOnnx.OfflineTts).Assembly, resolver);

            _cudaSetUsed = true;
            LoadedSet = SherpaNativeSet.Cuda;
        }

        LogManifestHead("CUDA", [Path.Combine(nativeDir, "MANIFEST.txt")]);
    }

    /// <summary>
    /// Loads every library in order and returns the sherpa-onnx C API's handle. When a load throws, frees the libraries
    /// already loaded, last first, so no CUDA library is left for the CPU set to bind to by name, and rethrows.
    /// </summary>
    private static IntPtr LoadAllOrNone(List<string> paths)
    {
        List<(string Path, IntPtr Handle)> loaded = [];
        try
        {
            foreach (string path in paths)
            {
                loaded.Add((path, NativeLibrary.Load(path)));
            }
        }
        catch (Exception)
        {
            Log.LogWarning("Loading the sherpa CUDA native set failed after {Count} libraries; freeing them", loaded.Count);
            FreeInReverse(loaded);
            throw;
        }

        return loaded.Last(l => Path.GetFileNameWithoutExtension(l.Path).EndsWith(CApiLibraryName, StringComparison.Ordinal)).Handle;
    }

    private static void FreeInReverse(List<(string Path, IntPtr Handle)> loaded)
    {
        for (int i = loaded.Count - 1; i >= 0; i--)
        {
            try
            {
                NativeLibrary.Free(loaded[i].Handle);
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Failed to free {Path} after a failed sherpa CUDA load", loaded[i].Path);
            }
        }
    }

    /// <summary>Logs which CPU set the app ships, from its <c>sherpa-native-MANIFEST.txt</c>.</summary>
    public static void LogCpuSet()
    {
        const string ManifestName = "sherpa-native-MANIFEST.txt";
        LogManifestHead(
            "CPU",
            [
                Path.Combine(AppContext.BaseDirectory, ManifestName),
                Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", ManifestName),
            ]
        );
    }

    /// <summary>Every library of the CUDA set in load order, dependencies first.</summary>
    private static List<string> PreloadOrder(string nativeDir)
    {
        if (OperatingSystem.IsWindows())
        {
            return OrderedPaths(
                nativeDir,
                ["cublasLt64_13.dll", "cublas64_13.dll", "cudnn_graph64_9.dll", "cudnn_ops64_9.dll"],
                "cudnn_*64_9.dll",
                ["cudnn64_9.dll", "onnxruntime_providers_shared.dll", "onnxruntime.dll", "sherpa-onnx-c-api.dll"]
            );
        }

        return OrderedPaths(
            nativeDir,
            ["libcudart.so.13", "libcublasLt.so.13", "libcublas.so.13", "libcurand.so.10", "libcudnn_graph.so.9", "libcudnn_ops.so.9"],
            "libcudnn_*.so.9",
            ["libcudnn.so.9", "libonnxruntime_providers_shared.so", "libonnxruntime.so", "libsherpa-onnx-c-api.so"]
        );
    }

    /// <summary>The named libraries before, the globbed cuDNN sub-libraries not already named, then the named libraries after.</summary>
    private static List<string> OrderedPaths(string nativeDir, string[] before, string subLibraryPattern, string[] after)
    {
        List<string> paths = [.. before.Select(name => RequiredPath(nativeDir, name))];
        HashSet<string> named = new(before.Concat(after), StringComparer.OrdinalIgnoreCase);
        paths.AddRange(
            Directory
                .EnumerateFiles(nativeDir, subLibraryPattern)
                .Where(path => !named.Contains(Path.GetFileName(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
        );
        paths.AddRange(after.Select(name => RequiredPath(nativeDir, name)));
        return paths;
    }

    private static string RequiredPath(string nativeDir, string name)
    {
        string path = Path.Combine(nativeDir, name);
        return File.Exists(path) ? path : throw new FileNotFoundException($"The sherpa CUDA native set lacks {path}; reinstall it.", path);
    }

    private static void LogManifestHead(string setName, string[] candidates)
    {
        foreach (string path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }
            string? head = File.ReadLines(path).FirstOrDefault(line => line.StartsWith("HEAD:", StringComparison.Ordinal));
            Log.LogInformation("sherpa-onnx {Set} native set loaded: {Head} ({Path})", setName, head ?? "no HEAD line", path);
            return;
        }

        Log.LogWarning("sherpa-onnx {Set} native set has no manifest at {Paths}", setName, string.Join(" or ", candidates));
    }
}
