using System.Diagnostics;
using SherpaOnnx;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// Which sherpa-onnx native set the process loads, and (opt-in, on this PC's GPU) that the installed CUDA set is the one used.
/// </summary>
public sealed class SherpaNativeLoaderTests
{
    [Theory]
    [InlineData(false, SttDevice.Cpu, SherpaNativeSet.Cpu)]
    [InlineData(false, SttDevice.Gpu, SherpaNativeSet.Cpu)]
    [InlineData(true, SttDevice.Cpu, SherpaNativeSet.Cpu)]
    [InlineData(true, SttDevice.Gpu, SherpaNativeSet.Cuda)]
    public void Choose_Picks_Cuda_Only_When_Installed_And_Gpu_Chosen(bool cudaInstalled, SttDevice device, SherpaNativeSet expected) =>
        Assert.Equal(expected, SherpaNativeLoader.Choose(cudaInstalled, device));

    [Fact]
    public void Failed_Load_Leaves_The_Loader_As_If_Never_Called()
    {
        string dir = Path.Combine(Path.GetTempPath(), "yaat-sherpa-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string name in RequiredFiles())
            {
                File.WriteAllBytes(Path.Combine(dir, name), []);
            }

            Exception first = Assert.ThrowsAny<Exception>(() => SherpaNativeLoader.UseCudaSet(dir));
            Exception second = Assert.ThrowsAny<Exception>(() => SherpaNativeLoader.UseCudaSet(dir));

            Assert.IsNotType<InvalidOperationException>(first);
            Assert.Equal(first.GetType(), second.GetType());
            Assert.Equal(SherpaNativeSet.Cpu, SherpaNativeLoader.LoadedSet);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_Cuda_Provider_Fails_Before_Any_Load()
    {
        string provider = OperatingSystem.IsWindows() ? "onnxruntime_providers_cuda.dll" : "libonnxruntime_providers_cuda.so";
        string dir = Path.Combine(Path.GetTempPath(), "yaat-sherpa-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string name in RequiredFiles().Where(name => !string.Equals(name, provider, StringComparison.Ordinal)))
            {
                File.WriteAllBytes(Path.Combine(dir, name), []);
            }

            FileNotFoundException thrown = Assert.Throws<FileNotFoundException>(() => SherpaNativeLoader.UseCudaSet(dir));

            Assert.EndsWith(provider, thrown.FileName);
            Assert.Equal(SherpaNativeSet.Cpu, SherpaNativeLoader.LoadedSet);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Every library file the CUDA set must hold, the CUDA provider included, so both load tests read one list.</summary>
    private static string[] RequiredFiles() =>
        OperatingSystem.IsWindows()
            ?
            [
                "cublasLt64_13.dll",
                "cublas64_13.dll",
                "cudnn_graph64_9.dll",
                "cudnn_ops64_9.dll",
                "cudnn64_9.dll",
                "onnxruntime_providers_shared.dll",
                "onnxruntime.dll",
                "onnxruntime_providers_cuda.dll",
                "sherpa-onnx-c-api.dll",
            ]
            :
            [
                "libcudart.so.13",
                "libcublasLt.so.13",
                "libcublas.so.13",
                "libcurand.so.10",
                "libcudnn_graph.so.9",
                "libcudnn_ops.so.9",
                "libcudnn.so.9",
                "libonnxruntime_providers_shared.so",
                "libonnxruntime.so",
                "libonnxruntime_providers_cuda.so",
                "libsherpa-onnx-c-api.so",
            ];

    /// <summary>
    /// Installs the real CUDA set into <c>YAAT_TEST_SHERPA_CUDA_ROOT</c> when it is not there yet, loads it and decodes on
    /// the GPU. For its duration it strips every CUDA runtime directory from <c>PATH</c>, so it stands in for a machine with
    /// only the NVIDIA driver installed. Loading a native set is process-wide, so this runs alone:
    /// <c>--filter-method "*Installed_Cuda_Set_Decodes_On_Gpu"</c>.
    /// </summary>
    [Fact]
    public async Task Installed_Cuda_Set_Decodes_On_Gpu()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The CUDA set is only proven on this Windows PC's GPU.");
        string? root = Environment.GetEnvironmentVariable("YAAT_TEST_SHERPA_CUDA_ROOT");
        string? int8Dir = Environment.GetEnvironmentVariable("YAAT_TEST_RASR_INT8_DIR");
        Assert.SkipUnless(!string.IsNullOrEmpty(root), "YAAT_TEST_SHERPA_CUDA_ROOT is not set.");
        Assert.SkipUnless(!string.IsNullOrEmpty(int8Dir) && Directory.Exists(int8Dir), "YAAT_TEST_RASR_INT8_DIR names no rasr int8 model directory.");
        Assert.False(
            LoadedModules().Any(m => string.Equals(m.ModuleName, "sherpa-onnx-c-api.dll", StringComparison.OrdinalIgnoreCase)),
            "sherpa-onnx-c-api.dll is already loaded: run this test alone with --filter-method \"*Installed_Cuda_Set_Decodes_On_Gpu\"."
        );

        string? savedPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", StripCudaRuntimeDirs(savedPath ?? string.Empty));
        try
        {
            foreach (string file in new[] { "cudart64_13.dll", "nvrtc64_130_0.dll", "curand64_10.dll", "cudart_hybrid64.dll" })
            {
                Assert.False(
                    File.Exists(Path.Combine(Environment.SystemDirectory, file)),
                    $"{file} is in System32, so this PC cannot stand in for a driver-only user."
                );
            }

            IReadOnlyList<SherpaCudaDownload> downloads =
                SherpaCudaInstaller.ForCurrentPlatform(Path.Combine(AppContext.BaseDirectory, "Speech", "stt-downloads.json"))
                ?? throw new InvalidOperationException("No CUDA native set for this platform.");
            if (!SherpaCudaInstaller.IsInstalledOnDisk(root!, downloads))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                await new SherpaCudaInstaller(http, root!, downloads).InstallAsync(TestContext.Current.CancellationToken);
            }
            string nativeDir = Path.GetFullPath(Path.Combine(root!, SherpaCudaInstaller.NativeDirName));

            SherpaNativeLoader.UseCudaSet(nativeDir);
            using var engine = new SherpaSttEngine(int8Dir!, "cuda", SherpaDecoding.Greedy);
            engine.Transcribe(new float[AudioCaptureService.SampleRate / 2], AudioCaptureService.SampleRate, string.Empty);

            Assert.Equal(SherpaNativeSet.Cuda, SherpaNativeLoader.LoadedSet);
            List<ProcessModule> modules = LoadedModules();
            foreach (string name in new[] { "onnxruntime_providers_cuda.dll", "cublasLt64_13.dll", "onnxruntime.dll" })
            {
                List<ProcessModule> matching = [.. modules.Where(m => string.Equals(m.ModuleName, name, StringComparison.OrdinalIgnoreCase))];
                Assert.True(matching.Count > 0, $"{name} is not loaded.");
                Assert.All(matching, m => Assert.True(IsUnder(m.FileName, nativeDir), $"{name} was loaded from {m.FileName}, not {nativeDir}."));
            }
            Assert.All(
                modules.Where(m => IsCudaRuntimeModule(m.ModuleName)),
                m => Assert.True(IsUnder(m.FileName, nativeDir), $"{m.ModuleName} was loaded from {m.FileName}, not {nativeDir}.")
            );

            string? voiceDir = Environment.GetEnvironmentVariable("YAAT_TEST_PIPER_VOICE_DIR");
            if (!string.IsNullOrEmpty(voiceDir))
            {
                Assert.NotEmpty(GeneratePiper(voiceDir));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    /// <summary>
    /// <paramref name="path"/> without empty or missing entries and without every directory that holds a CUDA runtime
    /// library, so the process looks driver-only.
    /// </summary>
    private static string StripCudaRuntimeDirs(string path)
    {
        string[] patterns =
        [
            "cudart64_*.dll",
            "cudart_hybrid64*.dll",
            "nvrtc64_*.dll",
            "curand64_*.dll",
            "cublas64_*.dll",
            "cublasLt64_*.dll",
            "cudnn*64_*.dll",
        ];
        List<string> kept = [];
        foreach (string entry in path.Split(Path.PathSeparator))
        {
            if ((entry.Length > 0) && Directory.Exists(entry) && !patterns.Any(pattern => Directory.EnumerateFiles(entry, pattern).Any()))
            {
                kept.Add(entry);
            }
        }

        return string.Join(Path.PathSeparator, kept);
    }

    private static bool IsCudaRuntimeModule(string name) =>
        name.StartsWith("cudart64_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("nvrtc64_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("curand64_", StringComparison.OrdinalIgnoreCase);

    private static float[] GeneratePiper(string voiceDir)
    {
        Assert.True(PilotVoicePack.IsComplete(voiceDir), $"YAAT_TEST_PIPER_VOICE_DIR {voiceDir} is not a complete Piper voice.");
        using OfflineTts tts = SherpaOnnxPilotVoiceSynthesizer.CreateOfflineTts(
            Path.Combine(voiceDir, PilotVoicePack.ModelFileName),
            Path.Combine(voiceDir, PilotVoicePack.TokensFileName)
        );
        var gen = new OfflineTtsGenerationConfig
        {
            Sid = 0,
            Speed = 1.0f,
            PhonemeCodepoints = EspeakPhonemizer.Phonemize(
                "cleared to land",
                EspeakPhonemizer.VoiceFor(tts.Lang),
                Path.Combine(AppContext.BaseDirectory, "espeak-ng-data")
            ),
        };
        return tts.GenerateWithConfig("", gen, null).Samples;
    }

    private static List<ProcessModule> LoadedModules()
    {
        using var process = Process.GetCurrentProcess();
        return [.. process.Modules.Cast<ProcessModule>()];
    }

    private static bool IsUnder(string path, string dir) =>
        Path.GetFullPath(path).StartsWith(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
