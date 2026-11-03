using System.Collections;
using System.Diagnostics.Tracing;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using Xunit;

namespace Yaat.Client.Tests;

/// <summary>
/// Runs the sherpa native overlay task's own source (build/sherpa/EnsureSherpaNatives.cs, linked into this project)
/// against a loopback HTTP server: the mirror fallback, a corrupt primary, the two no-source outcomes, and the per-RID
/// lock that keeps a waiting build from re-extracting the native another build just finished. Its loopback server's
/// Dispose must end the serve loop even when Stop cannot abort the receive that loop already entered.
/// </summary>
public sealed class EnsureSherpaNativesTests
{
    private const string LibraryA = "sherpa-onnx-c-api.dll";
    private const string LibraryB = "onnxruntime.dll";
    private const string ZipManifest = "MANIFEST.txt";
    private const string OverlayManifest = "sherpa-native-MANIFEST.txt";
    private const string KeptFileName = "keep.txt";

    // A hang guard, never a deadline a passing run comes near: the task's own lock wait tops out at 10 minutes and a
    // passing test releases the lock within milliseconds of seeing it. It sits under tools/test-all.ps1's 90 s no-CPU
    // stall watchdog, so a LoopbackSource.Dispose that hangs is reported by the guard, naming this test, rather than by
    // the watchdog, which kills the host.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    // Dispose's own HangGuard throws first; this only catches a Dispose that never returns at all, and it too stays under
    // the watchdog's 90 s so the failure is named rather than killed.
    private static readonly TimeSpan DisposeBackstop = HangGuard + TimeSpan.FromSeconds(10);

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

    // xunit.runner.json sets parallelizeAssembly:false and parallelizeTestCollections:false, so this assembly runs one
    // test at a time and the hold below can only catch this test's own HttpListener: no other test's receive can be the
    // one it holds.
    [Fact]
    public async Task Loopback_Dispose_Ends_A_Serve_Loop_Whose_Receive_Stop_Cannot_Abort()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The deferred close of a request queue and CancelIoEx are Windows kernel behaviour.");
        using ReceiveHold hold = new();
        LoopbackSource source = new();
        Task? disposing = null;
        SafeHandle? queue = null;
        bool holding = false;
        try
        {
            // Times out only if the hold never saw a receive, which means the trace names in ReceiveHold are stale.
            await hold.Reached.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

            // Stands in for the P/Invoke stub's own reference, which is what defers Stop's close of the queue.
            queue = RequestQueueHandle(source.Listener);
            queue.DangerousAddRef(ref holding);
            Assert.True(holding, "The request queue refused a reference, so Stop's close could not be deferred.");

            disposing = Task.Run(source.Dispose, TestContext.Current.CancellationToken);

            // Stop marks the listener stopped only after it has closed the queue handle and called CancelIoEx, so from
            // here nothing can abort the receive: letting the held stub go on now pends it for good.
            Assert.True(WaitUntil(() => !source.Listener.IsListening, DisposeBackstop), $"Dispose never stopped {source.BaseUrl}'s listener.");
            hold.Release();
            await disposing.WaitAsync(DisposeBackstop, TestContext.Current.CancellationToken);

            Assert.True(
                source.ServeEnded,
                $"Dispose returned from {source.BaseUrl} with its serve loop still blocked in a receive Stop could not abort."
            );
        }
        finally
        {
            bool stranded = (hold.Held) && (!source.ServeEnded);
            hold.Release();
            if ((stranded) && (holding))
            {
                // The kernel call Stop could not cancel: the receive that pended once the hold let the stub go on.
                CancelIoEx(queue!.DangerousGetHandle(), IntPtr.Zero);
            }

            if (disposing is null)
            {
                source.Dispose();
            }

            if (holding)
            {
                queue!.DangerousRelease();
            }
        }
    }

    // A sleeping poll on a state the product sets, with a hang guard as its deadline: a passing run leaves at once.
    private static bool WaitUntil(Func<bool> condition, TimeSpan guard)
    {
        using CancellationTokenSource deadline = new(guard);
        while (!condition())
        {
            if (deadline.IsCancellationRequested)
            {
                return false;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(1));
        }

        return true;
    }

    // Stop's close of the request queue is deferred while any reference to its SafeHandle is held, so the test holds
    // one of its own. HttpListener keeps the session it started in the private _currentSession field.
    private static SafeHandle RequestQueueHandle(HttpListener listener)
    {
        FieldInfo sessionField =
            typeof(HttpListener).GetField("_currentSession", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HttpListener._currentSession is gone, so the receive hold cannot reach the request queue.");
        object session =
            sessionField.GetValue(listener)
            ?? throw new InvalidOperationException("HttpListener._currentSession was null, so the listener had not started.");

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo? property = session.GetType().GetProperty("RequestQueueHandle", Any);
        if (property?.GetValue(session) is SafeHandle fromProperty)
        {
            return fromProperty;
        }

        FieldInfo? field = session.GetType().GetField("RequestQueueHandle", Any);
        return field?.GetValue(session) as SafeHandle
            ?? throw new InvalidOperationException(
                $"{session.GetType().FullName} has no RequestQueueHandle, so the receive hold cannot reach the request queue."
            );
    }

    // Cancels every I/O still pending on the handle, whoever issued it, so a red run frees the thread the hold stranded.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);

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

    /// <summary>
    /// A loopback HTTP server serving chosen paths, 404 for everything else, and recording each request in order. Its
    /// Dispose ends the serve loop through the loop's own cancellation, because Stop alone cannot abort a receive the
    /// P/Invoke stub had taken the queue's reference for but not yet issued.
    /// </summary>
    private sealed class LoopbackSource : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _closing = new();
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
        private readonly List<string> _requested = [];
        private readonly Task _serving;

        public LoopbackSource()
        {
            BaseUrl = $"http://localhost:{FreePort()}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        /// <summary>The listener, so the receive hold can reach the request queue Stop closes.</summary>
        public HttpListener Listener => _listener;

        /// <summary>Set once the serve loop has ended, whichever way it ended.</summary>
        public bool ServeEnded => _serving.IsCompleted;

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
            _closing.Cancel();
            _listener.Stop();
            _listener.Close();

            if (!_serving.Wait(HangGuard))
            {
                throw new TimeoutException($"LoopbackSource on {BaseUrl} hung: its serve loop had not ended {HangGuard} after Dispose.");
            }

            _serving.GetAwaiter().GetResult();
        }

        private static int FreePort()
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                Task<HttpListenerContext> receive;
                try
                {
                    receive = _listener.GetContextAsync();
                }
                catch (Exception) when (!_listener.IsListening)
                {
                    return;
                }

                HttpListenerContext context;
                try
                {
                    context = await receive.WaitAsync(_closing.Token);
                }
                catch (OperationCanceledException) when (_closing.IsCancellationRequested)
                {
                    // Stop may leave this receive pended for good, so observe whatever it eventually faults with.
                    _ = receive.ContinueWith(
                        task =>
                        {
                            _ = task.Exception;
                        },
                        TaskContinuationOptions.OnlyOnFaulted
                    );
                    return;
                }
                catch (Exception) when (!_listener.IsListening)
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

    /// <summary>
    /// Holds the process's first <c>HttpReceiveHttpRequest</c> between the P/Invoke stub taking the request queue's
    /// reference and the kernel call itself, so that Stop's deferred close and its no-op CancelIoEx happen while the
    /// receive is not yet pending. The held thread goes on when the listener emits Close, which Dispose reaches right
    /// after Stop, and only then does the receive pend where nothing can cancel it.
    /// </summary>
    private sealed class ReceiveHold : EventListener
    {
        private const string SourceName = "Private.InternalDiagnostics.System.Net.HttpListener";
        private const string ReceiveMessage = "Calling Interop.HttpApi.HttpReceiveHttpRequest";
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _letGo = new(false);
        private int _held;
        private int _released;

        /// <summary>The held receive arrived.</summary>
        public Task Reached => _reached.Task;

        /// <summary>Set once a receive is being held, so the test knows to cancel it on the way out.</summary>
        public bool Held => Volatile.Read(ref _held) != 0;

        /// <summary>Lets the held thread, and every later receive, pass, so a failed test strands nothing.</summary>
        public void Release()
        {
            Interlocked.Exchange(ref _released, 1);
            _letGo.Set();
        }

        // Runs from the EventListener constructor too, so it reads no instance field.
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == SourceName)
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if ((Volatile.Read(ref _released) != 0) || !IsReceiveCall(eventData))
            {
                return;
            }

            if (Interlocked.Exchange(ref _held, 1) != 0)
            {
                return;
            }

            _reached.TrySetResult();
            _letGo.Wait(HangGuard);
        }

        // NetEventSource.Info carries its text as a payload string: the event's own name is Info for every call site.
        private static bool IsReceiveCall(EventWrittenEventArgs eventData)
        {
            if (eventData.Message?.StartsWith(ReceiveMessage, StringComparison.Ordinal) == true)
            {
                return true;
            }

            if (eventData.Payload is { } payload)
            {
                foreach (object? value in payload)
                {
                    if (value is string text && text.StartsWith(ReceiveMessage, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
