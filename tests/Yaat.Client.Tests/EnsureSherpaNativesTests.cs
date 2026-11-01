using System.Collections;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using Xunit;

namespace Yaat.Client.Tests;

/// <summary>
/// Runs the sherpa native overlay task's own source (build/sherpa/EnsureSherpaNatives.cs, linked into this project)
/// against a loopback HTTP server: the mirror fallback, a corrupt primary, the two no-source outcomes, and the per-RID
/// lock that keeps a waiting build from re-extracting the native another build just finished.
/// </summary>
public sealed class EnsureSherpaNativesTests
{
    private const string LibraryA = "sherpa-onnx-c-api.dll";
    private const string LibraryB = "onnxruntime.dll";
    private const string ZipManifest = "MANIFEST.txt";
    private const string OverlayManifest = "sherpa-native-MANIFEST.txt";
    private const string KeptFileName = "keep.txt";

    // A hang guard, never a deadline a passing run comes near: the task's own lock wait tops out at 10 minutes and a
    // passing test releases the lock within milliseconds of seeing it.
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    [Fact]
    public void Mirror_Serves_When_The_Primary_404s()
    {
        using LoopbackSource source = new();
        byte[] zip = ZipOfNatives();
        string key = NewKey(out _);
        source.Serve("/mirror/win-x64-cpu.zip", zip);
        FakeBuildEngine engine = new();

        EnsureSherpaNatives task = NewTask(WriteManifest(source, key, zip), required: false, engine);
        Assert.True(task.Execute());

        ITaskItem overlay = Assert.Single(task.Overlays);
        Assert.Equal("win-x64", overlay.ItemSpec);
        string extractDir = overlay.GetMetadata("ExtractDir");
        foreach (string name in ExtractedFiles())
        {
            Assert.True(File.Exists(Path.Combine(extractDir, name)), Path.Combine(extractDir, name) + " is missing after the extraction.");
        }

        string[] expected = ["/primary/" + key, "/mirror/win-x64-cpu.zip"];
        Assert.Equal(expected, source.Requests);
        Assert.StartsWith(AppDataRoot(), extractDir, StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_Primary_Falls_Through_To_The_Mirror()
    {
        using LoopbackSource source = new();
        byte[] zip = ZipOfNatives();
        byte[] corrupt = (byte[])zip.Clone();
        corrupt[^1] = (byte)(corrupt[^1] ^ 0xFF);
        string key = NewKey(out _);
        source.Serve("/primary/" + key, corrupt);
        source.Serve("/mirror/win-x64-cpu.zip", zip);
        FakeBuildEngine engine = new();

        EnsureSherpaNatives task = NewTask(WriteManifest(source, key, zip), required: false, engine);
        Assert.True(task.Execute());

        ITaskItem overlay = Assert.Single(task.Overlays);
        Assert.True(File.Exists(Path.Combine(overlay.GetMetadata("ExtractDir"), LibraryA)));

        string[] expected = ["/primary/" + key, "/mirror/win-x64-cpu.zip"];
        Assert.Equal(expected, source.Requests);
    }

    [Fact]
    public void No_Source_Warns_YAATSHERPA001_And_Falls_Back()
    {
        using LoopbackSource source = new();
        byte[] zip = ZipOfNatives();
        string key = NewKey(out _);
        FakeBuildEngine engine = new();

        EnsureSherpaNatives task = NewTask(WriteManifest(source, key, zip), required: false, engine);
        Assert.True(task.Execute());

        Assert.Empty(task.Overlays);
        ITaskItem fallback = Assert.Single(task.Fallbacks);
        Assert.Equal("win-x64", fallback.ItemSpec);
        string reason = fallback.GetMetadata("Reason");
        Assert.Contains("/primary/" + key, reason);
        Assert.Contains("/mirror/win-x64-cpu.zip", reason);
        Assert.Contains(engine.Warnings, warning => warning.Code == "YAATSHERPA001");
    }

    [Fact]
    public void No_Source_Refuses_A_Publish()
    {
        using LoopbackSource source = new();
        byte[] zip = ZipOfNatives();
        string key = NewKey(out _);
        FakeBuildEngine engine = new();

        EnsureSherpaNatives task = NewTask(WriteManifest(source, key, zip), required: true, engine);
        Assert.False(task.Execute());

        Assert.Empty(task.Fallbacks);
        Assert.Contains(engine.Errors, error => error.Message.Contains("a publish never ships NuGet's stock native", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Waiting_Build_Keeps_The_Other_Builds_Extraction()
    {
        using LoopbackSource source = new();
        byte[] zip = ZipOfNatives();
        string key = NewKey(out string folder);

        // Seeded by "the other build": the cached zip verifies, so the waiting build needs no download.
        string zipPath = Path.Combine(CacheRoot(), "sherpa", folder, "win-x64-cpu.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        File.WriteAllBytes(zipPath, zip);
        string extractDir = Path.Combine(CacheRoot(), "sherpa", folder, "win-x64-cpu");
        string lockPath = Path.Combine(Path.GetDirectoryName(zipPath)!, ".lock");
        FakeBuildEngine engine = new();
        EnsureSherpaNatives task = NewTask(WriteManifest(source, key, zip), required: false, engine);

        Task<bool> run;
        using (FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            run = Task.Run(task.Execute);
            await Task.WhenAny(engine.WaitingObserved, run).WaitAsync(HangGuard, TestContext.Current.CancellationToken);
            if (!engine.WaitingObserved.IsCompleted)
            {
                // Fails at once with the product's own outcome rather than at the hang guard.
                await run.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
            }

            bool waited = engine.WaitingObserved.IsCompleted;
            if (waited)
            {
                SeedExtraction(extractDir);
            }

            Assert.True(waited, $"The build never waited for {lockPath} before preparing the native.");
        }

        Assert.True(await run.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        ITaskItem overlay = Assert.Single(task.Overlays);
        Assert.Equal(extractDir, overlay.GetMetadata("ExtractDir"));
        Assert.True(File.Exists(Path.Combine(extractDir, KeptFileName)), $"The waiting build re-extracted {extractDir} and dropped {KeptFileName}.");
    }

    private static EnsureSherpaNatives NewTask(string manifestPath, bool required, FakeBuildEngine engine) =>
        new()
        {
            ManifestPath = manifestPath,
            Rids = ["win-x64"],
            Required = required,
            BuildEngine = engine,
        };

    private static string NewKey(out string folder)
    {
        folder = Guid.NewGuid().ToString("N");
        return "sherpa/" + folder + "/win-x64-cpu.zip";
    }

    private static string AppDataRoot()
    {
        string? root = Environment.GetEnvironmentVariable("YAAT_APPDATA_DIR");
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidOperationException("ModuleInit did not point YAAT_APPDATA_DIR at this process's test directory.");
        }

        return root;
    }

    private static string CacheRoot() => Path.Combine(AppDataRoot(), "cache");

    private static string[] ExtractedFiles() => [LibraryA, LibraryB, ZipManifest, OverlayManifest];

    private static string WriteManifest(LoopbackSource source, string key, byte[] zip)
    {
        string dir = Path.Combine(AppDataRoot(), "manifests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        JsonObject file = new()
        {
            ["rid"] = "win-x64",
            ["variant"] = "cpu",
            ["key"] = key,
            ["sha256"] = Sha256Of(zip),
            ["size"] = zip.Length,
        };
        JsonObject manifest = new()
        {
            ["baseUrl"] = source.BaseUrl + "primary/",
            ["natives"] = new JsonObject
            {
                ["commit"] = "abcdef1234567",
                ["mirrors"] = new JsonArray { source.BaseUrl + "mirror/" },
                ["files"] = new JsonArray { file },
            },
        };
        string path = Path.Combine(dir, "stt-downloads.json");
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static byte[] ZipOfNatives()
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, LibraryA, "stub native");
            WriteEntry(archive, LibraryB, "stub runtime");
            WriteEntry(archive, ZipManifest, "HEAD: abc");
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        using StreamWriter writer = new(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    private static void SeedExtraction(string extractDir)
    {
        Directory.CreateDirectory(extractDir);
        foreach (string name in ExtractedFiles())
        {
            File.WriteAllText(Path.Combine(extractDir, name), "seeded");
        }

        File.WriteAllText(Path.Combine(extractDir, KeptFileName), "keep");
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Logged(string? Code, string Message);

    private sealed class FakeBuildEngine : IBuildEngine
    {
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set when the task says it is waiting for another build's lock.</summary>
        public ManualResetEventSlim Waiting { get; } = new(false);

        public Task WaitingObserved => _waiting.Task;

        public List<Logged> Errors { get; } = [];

        public List<Logged> Warnings { get; } = [];

        public List<string> Messages { get; } = [];

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => string.Empty;

        bool IBuildEngine.BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) =>
            false;

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(new Logged(e.Code, e.Message ?? string.Empty));

        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(new Logged(e.Code, e.Message ?? string.Empty));

        public void LogMessageEvent(BuildMessageEventArgs e)
        {
            string message = e.Message ?? string.Empty;
            Messages.Add(message);
            if (message.Contains("waiting for another build", StringComparison.Ordinal))
            {
                Waiting.Set();
                _waiting.TrySetResult();
            }
        }

        public void LogCustomEvent(CustomBuildEventArgs e) => Messages.Add(e.Message ?? string.Empty);
    }

    /// <summary>A loopback HTTP server serving chosen paths, 404 for everything else, and recording each request in order.</summary>
    private sealed class LoopbackSource : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
        private readonly List<string> _requested = [];
        private readonly Task _serving;

        public LoopbackSource()
        {
            BaseUrl = $"http://localhost:{FreePort()}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serving = Task.Run(Serve);
        }

        public string BaseUrl { get; }

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requested];
                }
            }
        }

        public void Serve(string path, byte[] body)
        {
            lock (_gate)
            {
                _routes[path] = body;
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _serving.Wait(HangGuard);
        }

        private static int FreePort()
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private void Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                string path = context.Request.Url!.AbsolutePath;
                lock (_gate)
                {
                    _requested.Add(path);
                }

                Respond(context, path);
            }
        }

        private void Respond(HttpListenerContext context, string path)
        {
            byte[]? body;
            lock (_gate)
            {
                _routes.TryGetValue(path, out body);
            }

            if (body is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentLength64 = body.Length;
                context.Response.OutputStream.Write(body);
            }

            context.Response.Close();
        }
    }
}
