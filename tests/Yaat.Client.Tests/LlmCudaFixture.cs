using System.Collections.Concurrent;
using LLama.Native;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// One-time initializer for LLamaSharp's <see cref="NativeLibraryConfig"/> plus a shared
/// <see cref="LocalLlmService"/> backed by the opt-in test GGUF. Two reasons to share:
/// <list type="number">
///   <item><description><see cref="NativeLibraryConfig"/> throws if touched after the native library
///     has loaded, so the WithCuda/WithLogs calls must happen exactly once, before any test.</description></item>
///   <item><description>Loading the 1.1 GB Qwen 1.5B weights takes ~3–10 s even on GPU. Loading once
///     and reusing across every test in the <c>LLM</c> collection turns a ~100 s suite into ~5 s.</description></item>
/// </list>
///
/// The fixture registers <see cref="NativeLibraryConfig.WithLogCallback"/> so native-layer messages
/// (including which backend won the load race) are captured in <see cref="NativeLogLines"/> —
/// individual tests can <c>ITestOutputHelper.WriteLine</c> them to prove CUDA is actually being used.
///
/// Model loading is deferred: <see cref="SharedServiceOrNull"/> returns null when the GGUF file is
/// absent, so the existing "silently skip" behaviour still works in CI and on fresh checkouts.
/// </summary>
public sealed class LlmCudaFixture : IDisposable
{
    // Matches the pattern used by LiveWeatherRealDataTests: walk back from the test bin directory
    // to the source TestData/ folder so we don't need to copy the multi-GB GGUF on every build.
    public static readonly string ModelPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "llm", "test-model.gguf");

    public static bool ModelAvailable => File.Exists(ModelPath);

    /// <summary>Ring buffer of the most recent native-layer log lines. Tests can snapshot this to prove which backend loaded.</summary>
    public ConcurrentQueue<string> NativeLogLines { get; } = new();

    private readonly LocalLlmService? _sharedService;

    public LlmCudaFixture()
    {
        // The Cuda12 backend in LLamaSharp 0.26.0 expects CUDA 12.x runtime libraries. LLamaSharp
        // decides which native folder to load (e.g. cuda12/ vs cuda13/) by reading CUDA_PATH and
        // parsing its version.json — so on a machine where CUDA 13 is the primary install, the
        // loader picks cuda13/ which doesn't exist as a NuGet package yet. Probe for a side-by-side
        // CUDA 12.x install and point the PROCESS env at it before LLamaSharp runs its detection.
        // This override is in-process only; it does not touch the user's system env vars or
        // affect other tools running on the machine.
        RepointToCuda12IfAvailable();

        try
        {
            NativeLibraryConfig
                .All.WithCuda(true)
                .WithAutoFallback(true)
                .WithLogCallback(
                    (level, message) =>
                    {
                        // Llama.cpp sends log lines as separate callback invocations; trim and skip empties.
                        var trimmed = message?.TrimEnd('\n', '\r');
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            NativeLogLines.Enqueue($"[{level}] {trimmed}");
                        }
                    }
                );
        }
        catch (InvalidOperationException)
        {
            // Another test (or a previous run in the same host) already loaded the native library.
            // Swallow — whichever backend was picked first is the one we use.
        }

        if (ModelAvailable)
        {
            var config = new FixtureLlmConfig(ModelPath, gpuLayers: 999);
            _sharedService = new LocalLlmService(config);
        }
    }

    private void RepointToCuda12IfAvailable()
    {
        // Standard NVIDIA install root on Windows. On Linux the equivalent is /usr/local/cuda-12*,
        // but CI runners don't have GPUs so the Linux path is deliberately unhandled here —
        // Linux tests silently fall back to CPU via WithAutoFallback.
        const string WindowsCudaRoot = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
        if (!OperatingSystem.IsWindows() || !Directory.Exists(WindowsCudaRoot))
        {
            return;
        }

        // Prefer the highest installed 12.x — 12.9, 12.6, 12.4, etc.
        string? best = null;
        var bestMinor = -1;
        foreach (var dir in Directory.GetDirectories(WindowsCudaRoot, "v12.*"))
        {
            var name = Path.GetFileName(dir); // e.g. "v12.9"
            var minorPart = name[4..]; // "9"
            if (int.TryParse(minorPart, out var minor) && minor > bestMinor)
            {
                best = dir;
                bestMinor = minor;
            }
        }

        if (best is null)
        {
            NativeLogLines.Enqueue(
                $"[Fixture] No CUDA 12.x install found under {WindowsCudaRoot}; CUDA offload may use the primary CUDA_PATH version."
            );
            return;
        }

        // LLamaSharp reads CUDA_PATH when computing cuda{N}/ — override it to our v12 install.
        Environment.SetEnvironmentVariable("CUDA_PATH", best);

        // The LLamaSharp native loader then resolves ggml-cuda.dll inside runtimes/...native/cuda12/.
        // That DLL in turn needs cudart64_12.dll / cublas64_12.dll / cublasLt64_12.dll from the CUDA
        // toolkit's bin directory. LLamaSharp calls AddDllDirectory on that path, but it only works
        // if the caller uses LOAD_LIBRARY_SEARCH_USER_DIRS — more reliable to prepend the bin dir
        // to PATH since LoadLibrary walks PATH as part of its default search.
        var binDir = Path.Combine(best, "bin");
        if (Directory.Exists(binDir))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Contains(binDir, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", binDir + Path.PathSeparator + currentPath);
            }
        }

        NativeLogLines.Enqueue($"[Fixture] Repointed CUDA_PATH to {best} and prepended {binDir} to PATH");
    }

    /// <summary>Shared <see cref="LocalLlmService"/> — null when no GGUF is present.</summary>
    public LocalLlmService? SharedServiceOrNull => _sharedService;

    public void Dispose()
    {
        _sharedService?.Dispose();
    }

    private sealed class FixtureLlmConfig : ILlmRuntimeConfig
    {
        public FixtureLlmConfig(string modelPath, int gpuLayers)
        {
            ModelPath = modelPath;
            GpuLayers = gpuLayers;
        }

        public bool Enabled => true;
        public string ModelPath { get; }
        public int GpuLayers { get; }
    }
}

[CollectionDefinition("LLM")]
public sealed class LlmCollection : ICollectionFixture<LlmCudaFixture>
{
    // xUnit marker class — no code, just the type declaration.
}
