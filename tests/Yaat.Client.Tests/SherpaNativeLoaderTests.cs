using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// The first library of the set loads and every later one fails, so the set is half loaded: the libraries already
    /// mapped are freed, the folder is deletable, and the loader stays on the CPU.
    /// </summary>
    [Fact]
    public void Partial_Load_Frees_Everything()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Freeing a partially loaded set is only proven where a module can be freed by name.");
        string dir = NewTempDir();
        foreach (string name in RequiredFiles())
        {
            string path = Path.Combine(dir, name);
            if (name == "cublasLt64_13.dll")
            {
                File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), path);
            }
            else
            {
                File.WriteAllBytes(path, []);
            }
        }

        Assert.ThrowsAny<Exception>(() => SherpaNativeLoader.UseCudaSet(dir, new FakeRegistrar()));

        Assert.DoesNotContain(LoadedModules(), module => IsUnder(module.FileName, dir));
        Directory.Delete(dir, recursive: true);
        Assert.Equal(SherpaNativeSet.Cpu, SherpaNativeLoader.LoadedSet);
    }

    /// <summary>
    /// A registration that throws leaves nothing mapped and the loader unset, and the retry reports the same failure
    /// rather than "already loaded". The fake owns the registered set, so no other test in this class sees this one's
    /// registration.
    /// </summary>
    [Fact]
    public void Resolver_Registration_Failure_Loads_Nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registration seam is only exercised where the set can be loaded.");
        string dir = NewTempDir();
        foreach (string name in RequiredFiles())
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(dir, name));
        }

        Assembly clientAssembly = typeof(SherpaSttEngine).Assembly;
        Assembly sherpaAssembly = typeof(SherpaOnnx.OfflineTts).Assembly;
        var registrar = new FakeRegistrar(sherpaAssembly);

        InvalidOperationException first = Assert.Throws<InvalidOperationException>(() => SherpaNativeLoader.UseCudaSet(dir, registrar));

        Assert.Equal("registrar failed", first.Message);
        Assert.Equal([clientAssembly, sherpaAssembly], registrar.Calls);
        Assert.Equal([clientAssembly], registrar.RegisteredAssemblies);
        Assert.DoesNotContain(LoadedModules(), module => IsUnder(module.FileName, dir));
        Assert.Equal(SherpaNativeSet.Cpu, SherpaNativeLoader.LoadedSet);

        registrar.Calls.Clear();

        InvalidOperationException second = Assert.Throws<InvalidOperationException>(() => SherpaNativeLoader.UseCudaSet(dir, registrar));

        Assert.Equal("registrar failed", second.Message);
        Assert.Equal([sherpaAssembly], registrar.Calls);
        Assert.DoesNotContain(LoadedModules(), module => IsUnder(module.FileName, dir));
        Assert.Equal(SherpaNativeSet.Cpu, SherpaNativeLoader.LoadedSet);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ChooseAtStartup_Cpu_Never_Reads_The_Manifest()
    {
        string missingManifest = Path.Combine(NewTempDir(), "stt-downloads.json");

        SherpaStartupChoice choice = SherpaNativeLoader.ChooseAtStartup(SttDevice.Cpu, missingManifest, NewTempDir());

        Assert.Equal(SherpaNativeSet.Cpu, choice.Set);
        Assert.Null(choice.CpuReason);
        Assert.Null(choice.Downloads);
    }

    [Fact]
    public void ChooseAtStartup_Gpu_Not_Installed_Says_So()
    {
        SkipUnlessGpuRuntimeIsListed();
        string root = NewTempDir();

        SherpaStartupChoice choice = SherpaNativeLoader.ChooseAtStartup(SttDevice.Gpu, ManifestPath, root);

        Assert.Equal(SherpaNativeSet.Cpu, choice.Set);
        Assert.NotNull(choice.CpuReason);
        Assert.Contains("not installed", choice.CpuReason);
        Assert.Null(choice.Downloads);
    }

    [Fact]
    public void ChooseAtStartup_Gpu_Out_Of_Date_Says_So()
    {
        SkipUnlessGpuRuntimeIsListed();
        string root = NewTempDir();
        File.WriteAllText(SherpaCudaInstaller.SentinelPath(root), "old");

        SherpaStartupChoice choice = SherpaNativeLoader.ChooseAtStartup(SttDevice.Gpu, ManifestPath, root);

        Assert.Equal(SherpaNativeSet.Cpu, choice.Set);
        Assert.NotNull(choice.CpuReason);
        Assert.Contains("out of date", choice.CpuReason);
        Assert.Null(choice.Downloads);
    }

    [Fact]
    public void ChooseAtStartup_Gpu_Installed_Chooses_Cuda()
    {
        SkipUnlessGpuRuntimeIsListed();
        string root = NewTempDir();
        IReadOnlyList<SherpaCudaDownload> downloads = SherpaCudaInstaller.ForCurrentPlatform(ManifestPath)!;
        File.WriteAllText(SherpaCudaInstaller.SentinelPath(root), SherpaCudaInstaller.ExpectedSentinelContent(downloads));

        SherpaStartupChoice choice = SherpaNativeLoader.ChooseAtStartup(SttDevice.Gpu, ManifestPath, root);

        Assert.Equal(SherpaNativeSet.Cuda, choice.Set);
        Assert.Null(choice.CpuReason);
        Assert.NotNull(choice.Downloads);
        Assert.Equivalent(downloads, choice.Downloads, strict: true);
    }

    [Fact]
    public void ChooseAtStartup_Gpu_Unreadable_Manifest_Says_So()
    {
        SkipUnlessGpuRuntimeIsListed();
        string root = NewTempDir();
        string manifestPath = Path.Combine(NewTempDir(), "stt-downloads.json");
        File.WriteAllText(manifestPath, """{"baseUrl":"x"}""");

        SherpaStartupChoice choice = SherpaNativeLoader.ChooseAtStartup(SttDevice.Gpu, manifestPath, root);

        Assert.Equal(SherpaNativeSet.Cpu, choice.Set);
        Assert.NotNull(choice.CpuReason);
        Assert.Contains("could not be read", choice.CpuReason);
        Assert.Null(choice.Downloads);
    }

    [Fact]
    public void LogManifestHead_Unreadable_Manifest_Does_Not_Throw()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to read a file another handle holds exclusively.");
        string manifest = Path.Combine(NewTempDir(), "MANIFEST.txt");
        File.WriteAllText(manifest, "HEAD: abc");
        using (new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            SherpaNativeLoader.LogManifestHead("CUDA", [manifest]);
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

    /// <summary>The manifest the app ships, which <c>ChooseAtStartup</c> reads for a GPU pick.</summary>
    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "Speech", "stt-downloads.json");

    private static void SkipUnlessGpuRuntimeIsListed() =>
        Assert.SkipUnless(SherpaCudaInstaller.ForCurrentPlatform(ManifestPath) is not null, "No GPU runtime is listed for this host.");

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "yaat-sherpa-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// A registrar that records every assembly it is asked about and refuses the ones it was built with, keeping an
    /// assembly registered only once its registration returns, so each test owns the registration state it sees.
    /// </summary>
    private sealed class FakeRegistrar(params Assembly[] refuse) : IDllImportResolverRegistrar
    {
        private readonly HashSet<Assembly> _refused = [.. refuse];
        private readonly HashSet<Assembly> _registered = [];

        /// <summary>Every assembly this registrar was asked to register, in the order it was asked.</summary>
        public List<Assembly> Calls { get; } = [];

        /// <summary>The assemblies whose registration returned.</summary>
        public IReadOnlyCollection<Assembly> RegisteredAssemblies => _registered;

        public bool IsRegistered(Assembly assembly) => _registered.Contains(assembly);

        public void Register(Assembly assembly, DllImportResolver resolver)
        {
            Calls.Add(assembly);
            if (_refused.Contains(assembly))
            {
                throw new InvalidOperationException("registrar failed");
            }
            _registered.Add(assembly);
        }
    }

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
                await new SherpaCudaInstaller(http, root!, downloads, TimeProvider.System).InstallAsync(TestContext.Current.CancellationToken);
            }
            string nativeDir = Path.GetFullPath(Path.Combine(root!, SherpaCudaInstaller.NativeDirName));

            SherpaNativeLoader.UseCudaSet(nativeDir);
            using var engine = new SherpaSttEngine(int8Dir!, "cuda", SherpaDecoding.Greedy, SherpaOfflineNativeApi.Instance);
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
