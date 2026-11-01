using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// The CUDA native set installer, against in-memory archives served by a fake HTTP handler into a fresh install root per test.
/// </summary>
public sealed class SherpaCudaInstallerTests : IDisposable
{
    private const string ZipUrl = "https://r2.example/sherpa/win-x64-cuda.zip";
    private const string MirrorUrl = "https://mirror.example/win-x64-cuda.zip";
    private const string WheelUrl = "https://pypi.example/nvidia_cudnn.whl";

    private const string InUseMessage = "The GPU runtime is in use. Switch Speech > Device to CPU, restart YAAT, then try again.";
    private const string BusyMessage = "Another YAAT is installing or removing the GPU runtime.";

    /// <summary>A backstop for a hung install; a passing run never waits it out.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-sherpa-cuda-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        File.Delete(_root + ".lock");
    }

    [Fact]
    public async Task Install_Writes_Sentinel_Last_And_Flattens_Entries()
    {
        (SherpaCudaDownload zip, byte[] zipBytes) = Download(
            "ours",
            [ZipUrl],
            ["a/b/x.dll", "MANIFEST.txt"],
            ("a/b/x.dll", "x"),
            ("MANIFEST.txt", "HEAD: abc")
        );
        (SherpaCudaDownload wheel, byte[] wheelBytes) = CudnnWheel();
        var handler = new FakeHandler(new() { [ZipUrl] = zipBytes, [WheelUrl] = wheelBytes }, Path.Combine(_root, ".installed"));
        SherpaCudaDownload[] downloads = [zip, wheel];

        await NewInstaller(handler, downloads).InstallAsync(TestContext.Current.CancellationToken);

        string[] native = [.. Directory.GetFiles(Path.Combine(_root, "native")).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
        Assert.Equal(["MANIFEST.txt", "cudnn64_9.dll", "cudnn_ops64_9.dll", "x.dll"], native);
        Assert.Empty(LeftoverDirs());
        Assert.True(SherpaCudaInstaller.IsInstalledOnDisk(_root, downloads));
        Assert.Equal([ZipUrl, WheelUrl], handler.Requests);
        Assert.False(handler.WatchedFileExisted[^1], "The sentinel existed before the last download began streaming.");
    }

    [Fact]
    public async Task Falls_Through_To_The_Next_Url()
    {
        (SherpaCudaDownload zip, byte[] zipBytes) = Download("ours", [ZipUrl, MirrorUrl], ["x.dll"], ("x.dll", "x"));
        (SherpaCudaDownload wheel, byte[] wheelBytes) = CudnnWheel();
        byte[] corrupt = [.. zipBytes];
        corrupt[^1] ^= 0xFF;
        var handler = new FakeHandler(
            new()
            {
                [ZipUrl] = corrupt,
                [MirrorUrl] = zipBytes,
                [WheelUrl] = wheelBytes,
            },
            null
        );
        SherpaCudaDownload[] downloads = [zip, wheel];

        await NewInstaller(handler, downloads).InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal([ZipUrl, MirrorUrl, WheelUrl], handler.Requests);
        Assert.True(SherpaCudaInstaller.IsInstalledOnDisk(_root, downloads));
    }

    [Fact]
    public async Task Sha_Mismatch_Refuses_And_Cleans_Partial()
    {
        (SherpaCudaDownload zip, byte[] zipBytes) = Download("ours", [ZipUrl], ["x.dll"], ("x.dll", "x"));
        (SherpaCudaDownload wheel, byte[] wheelBytes) = CudnnWheel();
        string wrong = new('0', 64);
        string actual = Sha256Hex(wheelBytes);
        SherpaCudaDownload[] downloads = [zip, wheel with { Sha256 = wrong }];
        var handler = new FakeHandler(new() { [ZipUrl] = zipBytes, [WheelUrl] = wheelBytes }, null);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewInstaller(handler, downloads).InstallAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains(WheelUrl, ex.Message, StringComparison.Ordinal);
        Assert.Contains(actual, ex.Message, StringComparison.Ordinal);
        Assert.Contains(wrong, ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, ".installed")));
        Assert.Empty(LeftoverDirs());
        Assert.False(SherpaCudaInstaller.IsInstalledOnDisk(_root, downloads));
    }

    [Fact]
    public async Task Missing_Entry_Refuses()
    {
        (SherpaCudaDownload zip, byte[] zipBytes) = Download("ours", [ZipUrl], ["x.dll", "missing/y.dll"], ("x.dll", "x"));
        var handler = new FakeHandler(new() { [ZipUrl] = zipBytes }, null);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewInstaller(handler, [zip]).InstallAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("missing/y.dll", ex.Message, StringComparison.Ordinal);
        Assert.False(SherpaCudaInstaller.IsInstalledOnDisk(_root, [zip]));
    }

    [Fact]
    public async Task Changed_Pin_Invalidates_Install()
    {
        (SherpaCudaDownload zip, byte[] zipBytes) = Download("ours", [ZipUrl], ["x.dll"], ("x.dll", "x"));
        (SherpaCudaDownload wheel, byte[] wheelBytes) = CudnnWheel();
        SherpaCudaDownload[] downloads = [zip, wheel];
        var handler = new FakeHandler(new() { [ZipUrl] = zipBytes, [WheelUrl] = wheelBytes }, null);
        await NewInstaller(handler, downloads).InstallAsync(TestContext.Current.CancellationToken);

        SherpaCudaDownload[] repinned = [zip, wheel with { Sha256 = new string('f', 64) }];

        Assert.True(SherpaCudaInstaller.IsInstalledOnDisk(_root, downloads));
        Assert.False(SherpaCudaInstaller.IsInstalledOnDisk(_root, repinned));
    }

    [Fact]
    public async Task Install_And_Uninstall_Refuse_While_A_Set_File_Is_Mapped()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a mapped library.");
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] dll = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "version.dll"));
        (SherpaCudaDownload zip, byte[] zipBytes) = DownloadOf(
            "ours",
            [ZipUrl],
            ["x.dll", "MANIFEST.txt"],
            ("x.dll", dll),
            ("MANIFEST.txt", Encoding.UTF8.GetBytes("HEAD: abc"))
        );
        var handler = new FakeHandler(new() { [ZipUrl] = zipBytes }, null);
        SherpaCudaInstaller installer = NewInstaller(handler, [zip]);
        await installer.InstallAsync(ct);
        List<string> before = SetSnapshot();

        IntPtr library = NativeLibrary.Load(Path.Combine(_root, "native", "x.dll"));
        try
        {
            InvalidOperationException uninstall = Assert.Throws<InvalidOperationException>(installer.Uninstall);
            Assert.Equal(InUseMessage, uninstall.Message);
            InvalidOperationException install = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(ct));
            Assert.Equal(InUseMessage, install.Message);
            Assert.Equal([ZipUrl], handler.Requests);
            Assert.Equal(before, SetSnapshot());
        }
        finally
        {
            NativeLibrary.Free(library);
        }

        installer.Uninstall();
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("every url 404")]
    [InlineData("caller cancel")]
    [InlineData("swap fails")]
    public async Task Failed_Or_Cancelled_Repair_Keeps_The_Working_Set(string failure)
    {
        Assert.SkipWhen((failure == "swap fails") && !OperatingSystem.IsWindows(), "Only Windows refuses to move a folder with an open file.");
        CancellationToken ct = TestContext.Current.CancellationToken;
        (SherpaCudaDownload zip, byte[] zipBytes) = Download(
            "ours",
            [ZipUrl],
            ["x.dll", "MANIFEST.txt"],
            ("x.dll", "x"),
            ("MANIFEST.txt", "HEAD: abc")
        );
        SherpaCudaDownload[] downloads = [zip];
        await NewInstaller(new FakeHandler(new() { [ZipUrl] = zipBytes }, null), downloads).InstallAsync(ct);
        List<string> before = SetSnapshot();

        Exception thrown = failure switch
        {
            "every url 404" => await Assert.ThrowsAsync<HttpRequestException>(() =>
                NewInstaller(new FakeHandler([], null), downloads).InstallAsync(ct)
            ),
            "caller cancel" => await CancelledRepairAsync(downloads),
            _ => await HeldManifestRepairAsync(downloads, zipBytes),
        };

        Assert.NotNull(thrown);
        Assert.Equal(before, SetSnapshot());
        Assert.True(SherpaCudaInstaller.IsInstalledOnDisk(_root, downloads));
        Assert.Empty(LeftoverDirs());
    }

    [Fact]
    public async Task Uninstall_Removes_The_Sentinel_First_And_Logs()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file another handle holds.");
        (SherpaCudaDownload zip, byte[] zipBytes) = Download(
            "ours",
            [ZipUrl],
            ["x.dll", "MANIFEST.txt"],
            ("x.dll", "x"),
            ("MANIFEST.txt", "HEAD: abc")
        );
        SherpaCudaInstaller installer = NewInstaller(new FakeHandler(new() { [ZipUrl] = zipBytes }, null), [zip]);
        await installer.InstallAsync(TestContext.Current.CancellationToken);

        FileStream held = new(Path.Combine(_root, "native", "MANIFEST.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Exception ex = Assert.ThrowsAny<Exception>(installer.Uninstall);
            Assert.True(ex is IOException or UnauthorizedAccessException, ex.ToString());
            Assert.False(File.Exists(Path.Combine(_root, ".installed")));
            Assert.False(installer.IsInstalled);
            Assert.StartsWith("Failed: ", installer.StatusMessage, StringComparison.Ordinal);
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public async Task Second_Installer_On_The_Same_Root_Is_Refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (SherpaCudaDownload zip, byte[] zipBytes) = Download("ours", [ZipUrl], ["x.dll"], ("x.dll", "x"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerA = new FakeHandler([], null);
        handlerA.Responders[ZipUrl] = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Ok(new ByteArrayContent(zipBytes));
        };
        var handlerB = new FakeHandler(new() { [ZipUrl] = zipBytes }, null);
        SherpaCudaInstaller a = NewInstaller(handlerA, [zip]);
        SherpaCudaInstaller b = NewInstaller(handlerB, [zip]);

        Task installA = a.InstallAsync(ct);
        await entered.Task.WaitAsync(HangGuard, ct);
        try
        {
            InvalidOperationException install = await Assert.ThrowsAsync<InvalidOperationException>(() => b.InstallAsync(ct));
            Assert.Equal(BusyMessage, install.Message);
            InvalidOperationException uninstall = Assert.Throws<InvalidOperationException>(b.Uninstall);
            Assert.Equal(BusyMessage, uninstall.Message);
            Assert.Empty(handlerB.Requests);
        }
        finally
        {
            release.TrySetResult();
        }

        await installA.WaitAsync(HangGuard, ct);
        Assert.True(a.IsInstalled);
    }

    [Theory]
    [InlineData("header timeout")]
    [InlineData("body idle")]
    [InlineData("wrong length")]
    [InlineData("endless body")]
    public async Task Url_Failures_Fall_Through_And_Only_The_Caller_Cancels(string failure)
    {
        (SherpaCudaDownload zip, byte[] zipBytes) = Download("ours", [ZipUrl, MirrorUrl], ["x.dll"], ("x.dll", "x"));
        var clock = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endless = new EndlessStream(zip.Size + (1024 * 1024));
        var handler = new FakeHandler(new() { [MirrorUrl] = zipBytes }, null);
        handler.Responders[ZipUrl] = failure switch
        {
            "header timeout" => (_, _) => throw new TaskCanceledException("timeout", new TimeoutException()),
            "body idle" => (_, _) => Task.FromResult(Ok(new StreamContent(new StallStream(entered)))),
            "wrong length" => (_, _) => Task.FromResult(Ok(WithLength(zipBytes, zip.Size + 1))),
            _ => (_, _) => Task.FromResult(Ok(new StreamContent(endless))),
        };
        var installer = new SherpaCudaInstaller(new HttpClient(handler), _root, [zip], clock);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        try
        {
            Task install = installer.InstallAsync(cts.Token);
            if (failure == "body idle")
            {
                await entered.Task.WaitAsync(HangGuard, cts.Token);
                clock.Advance(SherpaCudaInstaller.IdleReadTimeout + TimeSpan.FromSeconds(1));
            }
            await install.WaitAsync(HangGuard, cts.Token);
        }
        finally
        {
            await cts.CancelAsync();
        }

        Assert.Equal([ZipUrl, MirrorUrl], handler.Requests.Take(2));
        Assert.True(installer.IsInstalled);
        Assert.True(endless.Served <= zip.Size + (64 * 1024), $"The endless body served {endless.Served} bytes.");
    }

    [Fact]
    public async Task Only_The_Caller_Cancel_Reports_Cancelled()
    {
        (SherpaCudaDownload zip, byte[] zipBytes) = Download("ours", [ZipUrl, MirrorUrl], ["x.dll"], ("x.dll", "x"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(new() { [MirrorUrl] = zipBytes }, null);
        handler.Responders[ZipUrl] = (_, _) => Task.FromResult(Ok(new StreamContent(new StallStream(entered))));
        SherpaCudaInstaller installer = NewInstaller(handler, [zip]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task install = installer.InstallAsync(cts.Token);
        await entered.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
        Assert.Equal("Cancelled.", installer.StatusMessage);
        Assert.Equal([ZipUrl], handler.Requests);
    }

    [Fact]
    public void ForCurrentPlatform_Lists_The_Measured_Closure()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows() && (RuntimeInformation.ProcessArchitecture == Architecture.X64),
            "The Windows closure is only listed on win-x64."
        );

        IReadOnlyList<SherpaCudaDownload>? downloads = SherpaCudaInstaller.ForCurrentPlatform(ManifestPath);

        Assert.NotNull(downloads);
        Assert.Equal(
            [
                "sherpa-onnx-c-api.dll",
                "onnxruntime.dll",
                "onnxruntime_providers_cuda.dll",
                "onnxruntime_providers_shared.dll",
                "MANIFEST.txt",
                "nvidia/cu13/bin/x86_64/cublas64_13.dll",
                "nvidia/cu13/bin/x86_64/cublasLt64_13.dll",
                "nvidia/cudnn/bin/*.dll",
            ],
            downloads.SelectMany(d => d.Entries)
        );
        Assert.Equal(
            [
                "https://dl.leftos.dev/sherpa-native/804a1d0c29/win-x64-cuda.zip",
                "https://github.com/leftos/sherpa-onnx/releases/download/yaat-natives-804a1d0c29/win-x64-cuda.zip",
            ],
            downloads[0].Urls
        );
    }

    [Fact]
    public void ForPlatform_Linux_Lists_The_Measured_Closure()
    {
        IReadOnlyList<SherpaCudaDownload>? downloads = SherpaCudaInstaller.ForPlatform("linux-x64", ManifestPath);

        Assert.NotNull(downloads);
        Assert.Equal(
            [
                "libsherpa-onnx-c-api.so",
                "libonnxruntime.so",
                "libonnxruntime_providers_cuda.so",
                "libonnxruntime_providers_shared.so",
                "MANIFEST.txt",
                "nvidia/cu13/lib/libcublas.so.13",
                "nvidia/cu13/lib/libcublasLt.so.13",
                "nvidia/cu13/lib/libcudart.so.13",
                "nvidia/cu13/lib/libcurand.so.10",
                "nvidia/cudnn/lib/*.so.9",
            ],
            downloads.SelectMany(d => d.Entries)
        );
        Assert.Equal("dd38177cdc9504048b877fc827146f24d0e8f75713bbf91d3c7d46da8cd1cabc", downloads[0].Sha256);
    }

    [Fact]
    public void Manifest_Without_Natives_Throws_InvalidData()
    {
        Directory.CreateDirectory(_root);
        string manifestPath = Path.Combine(_root, "stt-downloads.json");
        File.WriteAllText(manifestPath, """{"baseUrl":"x"}""");

        Assert.Throws<InvalidDataException>(() => SherpaCudaInstaller.ForPlatform("win-x64", manifestPath));
    }

    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "Speech", "stt-downloads.json");

    private SherpaCudaInstaller NewInstaller(FakeHandler handler, IReadOnlyList<SherpaCudaDownload> downloads) =>
        new(new HttpClient(handler), _root, downloads, TimeProvider.System);

    private async Task<Exception> CancelledRepairAsync(IReadOnlyList<SherpaCudaDownload> downloads)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler([], null);
        handler.Responders[ZipUrl] = (_, _) => Task.FromResult(Ok(new StreamContent(new StallStream(entered))));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task install = NewInstaller(handler, downloads).InstallAsync(cts.Token);
        await entered.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        return await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
    }

    private async Task<Exception> HeldManifestRepairAsync(IReadOnlyList<SherpaCudaDownload> downloads, byte[] zipBytes)
    {
        var handler = new FakeHandler(new() { [ZipUrl] = zipBytes }, null);
        FileStream held = new(Path.Combine(_root, "native", "MANIFEST.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            return await Assert.ThrowsAnyAsync<Exception>(() => NewInstaller(handler, downloads).InstallAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            held.Dispose();
        }
    }

    /// <summary>The sentinel and every <c>native/</c> file, each as its name and SHA-256, in name order.</summary>
    private List<string> SetSnapshot()
    {
        string sentinel = Path.Combine(_root, ".installed");
        string nativeDir = Path.Combine(_root, "native");
        string[] files = Directory.Exists(nativeDir) ? Directory.GetFiles(nativeDir) : [];
        return
        [
            $".installed {(File.Exists(sentinel) ? Sha256Hex(File.ReadAllBytes(sentinel)) : "missing")}",
            .. files.Order(StringComparer.Ordinal).Select(path => $"{Path.GetFileName(path)} {Sha256Hex(File.ReadAllBytes(path))}"),
        ];
    }

    /// <summary>The staging, old-set and legacy partial folders left under the root.</summary>
    private List<string> LeftoverDirs()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }
        return
        [
            .. Directory.GetDirectories(_root, ".staging-*"),
            .. Directory.GetDirectories(_root, ".old-*"),
            .. Directory.GetDirectories(_root, ".partial"),
        ];
    }

    private static HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };

    private static ByteArrayContent WithLength(byte[] bytes, long length)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentLength = length;
        return content;
    }

    private static (SherpaCudaDownload Download, byte[] Bytes) CudnnWheel() =>
        Download(
            "wheel",
            [WheelUrl],
            ["nvidia/cudnn/bin/*.dll"],
            ("nvidia/cudnn/bin/cudnn64_9.dll", "cudnn"),
            ("nvidia/cudnn/bin/cudnn_ops64_9.dll", "ops"),
            ("nvidia/cudnn/lib/notes.txt", "not taken")
        );

    private static (SherpaCudaDownload Download, byte[] Bytes) Download(
        string name,
        string[] urls,
        string[] entries,
        params (string Path, string Content)[] files
    ) => DownloadOf(name, urls, entries, [.. files.Select(f => (f.Path, Encoding.UTF8.GetBytes(f.Content)))]);

    private static (SherpaCudaDownload Download, byte[] Bytes) DownloadOf(
        string name,
        string[] urls,
        string[] entries,
        params (string Path, byte[] Content)[] files
    )
    {
        byte[] bytes = ZipOf(files);
        return (new SherpaCudaDownload(name, urls, bytes.Length, Sha256Hex(bytes), entries), bytes);
    }

    private static byte[] ZipOf((string Path, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] content) in files)
            {
                using Stream entry = zip.CreateEntry(path).Open();
                entry.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Serves a URL's responder when it has one, else its fixed body (404 when it has neither), recording each request and
    /// whether a watched file existed then.
    /// </summary>
    private sealed class FakeHandler(Dictionary<string, byte[]> bodies, string? watchedPath) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<bool> WatchedFileExisted { get; } = [];

        public Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Responders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            WatchedFileExisted.Add((watchedPath is not null) && File.Exists(watchedPath));
            if (Responders.TryGetValue(url, out Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder))
            {
                return responder(request, cancellationToken);
            }
            HttpResponseMessage response = bodies.TryGetValue(url, out byte[]? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }

    /// <summary>A read-only body stream whose reads are the only thing the subclasses define.</summary>
    private abstract class BodyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read the body asynchronously.");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>A body whose first read signals <paramref name="entered"/> and then waits for its token.</summary>
    private sealed class StallStream(TaskCompletionSource entered) : BodyStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    /// <summary>A body of zeros that never ends, counting what it served; past <paramref name="cap"/> bytes it throws.</summary>
    private sealed class EndlessStream(long cap) : BodyStream
    {
        public long Served { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Served >= cap)
            {
                throw new IOException("served past the cap");
            }
            buffer.Span.Clear();
            Served += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }
    }
}
