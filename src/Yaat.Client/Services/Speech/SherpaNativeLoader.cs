using System.Reflection;
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

/// <summary>The native set startup picked, and why it kept the CPU when the user asked for the GPU.</summary>
/// <param name="Set">The set to load.</param>
/// <param name="CpuReason">Why the CPU was kept instead of the GPU set; null when the user asked for the CPU or the GPU set is usable.</param>
public sealed record SherpaStartupChoice(SherpaNativeSet Set, string? CpuReason);

/// <summary>Registers one <see cref="DllImportResolver"/> per assembly, remembering which assemblies it already did.</summary>
public interface IDllImportResolverRegistrar
{
    /// <summary>Whether this registrar already set <paramref name="assembly"/>'s resolver.</summary>
    /// <param name="assembly">The assembly whose resolver is asked about.</param>
    bool IsRegistered(Assembly assembly);

    /// <summary>Sets <paramref name="assembly"/>'s resolver to <paramref name="resolver"/>, and records it once that returns.</summary>
    /// <param name="assembly">The assembly whose imports the resolver takes over.</param>
    /// <param name="resolver">The resolver.</param>
    void Register(Assembly assembly, DllImportResolver resolver);
}

/// <summary>The process-wide registrar: one <see cref="NativeLibrary.SetDllImportResolver"/> call per assembly.</summary>
public sealed class NativeDllImportResolverRegistrar : IDllImportResolverRegistrar
{
    private readonly Lock _gate = new();
    private readonly HashSet<Assembly> _registered = [];

    /// <summary>The registrar for this process.</summary>
    public static NativeDllImportResolverRegistrar Instance { get; } = new();

    private NativeDllImportResolverRegistrar() { }

    /// <inheritdoc />
    public bool IsRegistered(Assembly assembly)
    {
        lock (_gate)
        {
            return _registered.Contains(assembly);
        }
    }

    /// <inheritdoc />
    public void Register(Assembly assembly, DllImportResolver resolver)
    {
        lock (_gate)
        {
            NativeLibrary.SetDllImportResolver(assembly, resolver);
            _registered.Add(assembly);
        }
    }
}

/// <summary>
/// Points sherpa-onnx at its native set before anything touches it. The CPU set needs nothing: default probing finds
/// it beside the app. The CUDA set is preloaded by absolute path, dependencies first, so a missing cuBLAS or cuDNN fails
/// here where the caller falls back to the CPU set. The provider's file is checked at this load, but the provider itself
/// is loaded by ONNX Runtime when the recognizer is created, since its DllMain needs ONNX Runtime's provider host; its
/// import closure (cuBLAS, cuBLASLt) and cuDNN are preloaded here and bind to the installed copies by base name (Windows)
/// or soname (glibc). The sherpa-onnx imports of both managed assemblies resolve to the CUDA set, and no process-wide
/// search path is changed.
///
/// Both assemblies' resolvers are registered before the set loads and return nothing until it has loaded, so a failed
/// load leaves the CPU set in place for default probing. Every failure is logged, and a GPU pick that ends on the CPU
/// records why in <see cref="CpuFallbackReason"/>.
/// </summary>
public static class SherpaNativeLoader
{
    private const string CApiLibraryName = "sherpa-onnx-c-api";

    private static readonly ILogger Log = AppLog.CreateLogger("SherpaNativeLoader");

    private static readonly Lock Gate = new();
    private static IntPtr _cApi;
    private static bool _cudaSetUsed;

    /// <summary>The set this process loaded; <see cref="SherpaNativeSet.Cpu"/> until <see cref="UseCudaSet"/> runs.</summary>
    public static SherpaNativeSet LoadedSet { get; private set; } = SherpaNativeSet.Cpu;

    /// <summary>Why a GPU pick ended on the CPU, for the Settings and Speech Debug surfaces; null until one does.</summary>
    public static string? CpuFallbackReason { get; private set; }

    /// <summary>Records why a GPU pick ended on the CPU and logs it.</summary>
    /// <param name="reason">The reason, shown to the user.</param>
    public static void RecordCpuFallback(string reason)
    {
        CpuFallbackReason = reason;
        Log.LogWarning("Speech recognition runs on the CPU: {Reason}", reason);
    }

    /// <summary>
    /// The set startup should load, with the reason the CPU was kept when the user asked for the GPU. A CPU choice reads
    /// no file; a GPU choice reads the download manifest and the install root's sentinel. Nothing is loaded.
    /// </summary>
    /// <param name="device">The user's speech-recognition device.</param>
    /// <param name="manifestPath">The <c>stt-downloads.json</c> that pins the CUDA native archives.</param>
    /// <param name="installRoot">The folder the CUDA set is installed into.</param>
    public static SherpaStartupChoice ChooseAtStartup(SttDevice device, string manifestPath, string installRoot)
    {
        if (device == SttDevice.Cpu)
        {
            return new SherpaStartupChoice(SherpaNativeSet.Cpu, null);
        }

        IReadOnlyList<SherpaCudaDownload>? downloads;
        try
        {
            downloads = SherpaCudaInstaller.ForCurrentPlatform(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new SherpaStartupChoice(SherpaNativeSet.Cpu, $"The download manifest {manifestPath} could not be read: {ex.Message}");
        }

        if (downloads is null)
        {
            return new SherpaStartupChoice(SherpaNativeSet.Cpu, "There is no GPU runtime for this platform.");
        }

        if (SherpaCudaInstaller.IsInstalledOnDisk(installRoot, downloads))
        {
            return new SherpaStartupChoice(SherpaNativeSet.Cuda, null);
        }

        string reason = File.Exists(SherpaCudaInstaller.SentinelPath(installRoot))
            ? "The installed GPU runtime is out of date; reinstall it in Settings > Speech."
            : "The GPU runtime is not installed; install it in Settings > Speech.";
        return new SherpaStartupChoice(SherpaNativeSet.Cpu, reason);
    }

    /// <summary>
    /// Loads the CUDA set from <paramref name="nativeDir"/> and routes sherpa-onnx's imports to it. Call before any
    /// sherpa-onnx code runs, at most once per process. A failed call frees what it loaded and leaves the loader as if it
    /// had never been called, so the CPU set stays usable.
    /// </summary>
    /// <param name="nativeDir">The installed set's <c>native</c> folder.</param>
    public static void UseCudaSet(string nativeDir) => UseCudaSet(nativeDir, NativeDllImportResolverRegistrar.Instance);

    /// <summary>
    /// Loads the CUDA set from <paramref name="nativeDir"/>, registering the set's resolver with <paramref name="registrar"/>
    /// before any library is mapped. A failed call leaves no library mapped and publishes nothing, so the CPU set stays
    /// usable and a retry fails the same way instead of reporting the set already loaded.
    /// </summary>
    /// <param name="nativeDir">The installed set's <c>native</c> folder.</param>
    /// <param name="registrar">Where the resolvers go; the tests pass a fake.</param>
    /// <exception cref="InvalidOperationException">The CUDA set was already loaded in this process.</exception>
    /// <exception cref="FileNotFoundException">A library the set needs is missing from <paramref name="nativeDir"/>.</exception>
    public static void UseCudaSet(string nativeDir, IDllImportResolverRegistrar registrar)
    {
        lock (Gate)
        {
            if (_cudaSetUsed)
            {
                throw new InvalidOperationException("The sherpa CUDA native set is already loaded in this process.");
            }

            RequiredPath(nativeDir, OperatingSystem.IsWindows() ? "onnxruntime_providers_cuda.dll" : "libonnxruntime_providers_cuda.so");
            RegisterResolvers(registrar);
            List<string> paths = PreloadOrder(nativeDir);
            IntPtr cApi = LoadAllOrNone(paths);

            Volatile.Write(ref _cApi, cApi);
            _cudaSetUsed = true;
            LoadedSet = SherpaNativeSet.Cuda;
        }

        LogManifestHead("CUDA", [Path.Combine(nativeDir, "MANIFEST.txt")]);
    }

    /// <summary>Registers the set's resolver for both sherpa-onnx assemblies, in load order, skipping any already done.</summary>
    private static void RegisterResolvers(IDllImportResolverRegistrar registrar)
    {
        RegisterResolver(registrar, typeof(SherpaSttEngine).Assembly);
        RegisterResolver(registrar, typeof(SherpaOnnx.OfflineTts).Assembly);
    }

    private static void RegisterResolver(IDllImportResolverRegistrar registrar, Assembly assembly)
    {
        if (registrar.IsRegistered(assembly))
        {
            return;
        }

        try
        {
            registrar.Register(assembly, Resolve);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to register the sherpa-native resolver for {Assembly}", assembly.GetName().Name);
            throw;
        }
    }

    /// <summary>The set's resolver: the C API comes from the CUDA set once it has loaded, every other library from default probing.</summary>
    private static IntPtr Resolve(string libraryName, Assembly _1, DllImportSearchPath? _2) =>
        (libraryName == CApiLibraryName) ? Volatile.Read(ref _cApi) : IntPtr.Zero;

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
