using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// The STT model installer, against in-memory files served by a fake HTTP handler into a fresh install root per test.
/// </summary>
public sealed class SttModelInstallerTests : IDisposable
{
    private const string ModelId = "test-model";
    private const string BaseUrl = "https://r2.example/stt/test-model/";
    private const string RefusalMessage = "Another model is downloading; try again when it finishes.";

    /// <summary>A backstop for a hung install; a passing run never waits it out.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-stt-model-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        // A test that never installs leaves no lock, and its parent folder may not exist yet; File.Delete throws on a missing folder.
        if (File.Exists(_root + ".lock"))
        {
            File.Delete(_root + ".lock");
        }
    }

    [Fact]
    public void ReadModels_ReadsBothRasrModelsFromTheShippedManifest()
    {
        IReadOnlyList<SttModelDownload> models = SttModelInstaller.ReadModels(ManifestPath);

        Assert.Equal(["rasr-parakeet-v2-fp32", "rasr-parakeet-v2-int8"], models.Select(m => m.Id));
        SttModelDownload fp32 = models[0];
        Assert.Contains("encoder.weights", fp32.Files.Select(f => f.Name));
        Assert.Contains("encoder.onnx", fp32.Files.Select(f => f.Name));
        Assert.All(
            models.SelectMany(m => m.Files),
            f =>
            {
                string url = Assert.Single(f.Urls);
                Assert.StartsWith("https://dl.leftos.dev/stt/rasr-parakeet-v2/", url, StringComparison.Ordinal);
                Assert.Equal(64, f.Sha256.Length);
                Assert.True(f.Size > 0);
            }
        );
        Assert.Equal(fp32.Files.Sum(f => f.Size), fp32.TotalBytes);
    }

    [Fact]
    public void ReadModel_UnknownId_Throws()
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SttModelInstaller.ReadModel(ManifestPath, "no-such-model"));

        Assert.Contains("no-such-model", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ManifestPath, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_WritesEveryFileFlatAndTheSentinel_ThenIsInstalled()
    {
        (SttModelDownload model, Dictionary<string, byte[]> bodies) = Model(
            ("encoder.onnx", "graph"),
            ("encoder.weights", "weights"),
            ("tokens.txt", "a b")
        );
        var handler = new FakeHandler(bodies);
        SttModelInstaller installer = NewInstaller(handler, model);
        Assert.False(installer.IsInstalled);

        await installer.InstallAsync(TestContext.Current.CancellationToken);

        string modelDir = SttModelInstaller.ModelDir(_root, ModelId);
        Assert.Equal(
            [".installed", "encoder.onnx", "encoder.weights", "tokens.txt"],
            Directory.GetFiles(modelDir).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)
        );
        Assert.Empty(Directory.GetDirectories(modelDir));
        Assert.Equal("weights", File.ReadAllText(Path.Combine(modelDir, "encoder.weights")));
        Assert.Equal([Path.GetFileName(modelDir)], Directory.GetDirectories(_root).Select(path => Path.GetFileName(path)));
        Assert.True(SttModelInstaller.IsInstalledOnDisk(_root, model));
        Assert.True(installer.IsInstalled);
        Assert.Equal("Ready", installer.StatusMessage);
        Assert.Equal(1.0, installer.Progress);
        Assert.Equal(model.Files.Select(f => f.Urls[0]), handler.Requests);
    }

    [Fact]
    public async Task Install_HashMismatch_FailsAndLeavesNoModelFolder()
    {
        (SttModelDownload model, Dictionary<string, byte[]> bodies) = Model(("encoder.onnx", "graph"), ("tokens.txt", "a b"));
        SttModelFile wrong = model.Files[1] with { Sha256 = new string('0', 64) };
        SttModelDownload repinned = model with { Files = [model.Files[0], wrong] };
        SttModelInstaller installer = NewInstaller(new FakeHandler(bodies), repinned);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("Failed: ", installer.StatusMessage, StringComparison.Ordinal);
        Assert.False(installer.IsInstalled);
        Assert.False(Directory.Exists(SttModelInstaller.ModelDir(_root, ModelId)));
        Assert.Empty(Directory.Exists(_root) ? Directory.GetDirectories(_root) : []);
    }

    [Fact]
    public async Task Install_Cancelled_LeavesThePreviousInstallIntact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (SttModelDownload model, Dictionary<string, byte[]> bodies) = Model(("encoder.onnx", "graph"), ("tokens.txt", "a b"));
        await NewInstaller(new FakeHandler(bodies), model).InstallAsync(ct);
        List<string> before = Snapshot();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(bodies);
        handler.Responders[model.Files[1].Urls[0]] = _ => Ok(new StreamContent(new StallStream(entered)));
        SttModelInstaller installer = NewInstaller(handler, model);
        Task install = installer.InstallAsync(ct);
        await entered.Task.WaitAsync(HangGuard, ct);
        installer.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install.WaitAsync(HangGuard, ct));
        Assert.Equal("Cancelled.", installer.StatusMessage);
        Assert.True(installer.IsInstalled);
        Assert.True(SttModelInstaller.IsInstalledOnDisk(_root, model));
        Assert.Equal(before, Snapshot());
        Assert.Equal([ModelId], Directory.GetDirectories(_root).Select(path => Path.GetFileName(path)));
    }

    [Fact]
    public async Task Install_ReportsProgressOverTheWholeModel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] first = new byte[1000];
        byte[] second = new byte[1000];
        Random.Shared.NextBytes(first);
        Random.Shared.NextBytes(second);
        (SttModelDownload model, Dictionary<string, byte[]> bodies) = ModelOf(("encoder.onnx", first), ("decoder.onnx", second));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(bodies);
        handler.Responders[model.Files[1].Urls[0]] = _ => Ok(new StreamContent(new HalfThenWaitStream(second, entered, release.Task)));
        SttModelInstaller installer = NewInstaller(handler, model);
        Assert.Equal("Not downloaded (~0 MB)", installer.StatusMessage);

        Task install = installer.InstallAsync(ct);
        await entered.Task.WaitAsync(HangGuard, ct);
        double midway = installer.Progress;
        string midwayStatus = installer.StatusMessage;
        release.TrySetResult();
        await install.WaitAsync(HangGuard, ct);

        Assert.Equal(0.75, midway);
        Assert.Equal("Downloading 75% (0 / 0 MB)", midwayStatus);
        Assert.Equal(1.0, installer.Progress);
        Assert.Equal("Ready", installer.StatusMessage);
    }

    [Fact]
    public async Task Delete_RemovesTheSentinelFirstThenTheFolder()
    {
        (SttModelDownload model, Dictionary<string, byte[]> bodies) = Model(("encoder.onnx", "graph"), ("tokens.txt", "a b"));
        SttModelInstaller installer = NewInstaller(new FakeHandler(bodies), model);
        await installer.InstallAsync(TestContext.Current.CancellationToken);
        string modelDir = SttModelInstaller.ModelDir(_root, ModelId);

        if (OperatingSystem.IsWindows())
        {
            FileStream held = new(Path.Combine(modelDir, "tokens.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
            try
            {
                Exception ex = await Assert.ThrowsAnyAsync<Exception>(installer.DeleteAsync);
                Assert.True(ex is IOException or UnauthorizedAccessException, ex.ToString());
                Assert.False(File.Exists(Path.Combine(modelDir, ".installed")));
                Assert.True(File.Exists(Path.Combine(modelDir, "tokens.txt")));
                Assert.False(installer.IsInstalled);
                Assert.StartsWith("Failed: ", installer.StatusMessage, StringComparison.Ordinal);
            }
            finally
            {
                held.Dispose();
            }
        }

        await installer.DeleteAsync();

        Assert.False(Directory.Exists(modelDir));
        Assert.False(installer.IsInstalled);
        Assert.False(SttModelInstaller.IsInstalledOnDisk(_root, model));
        Assert.Equal("Removed.", installer.StatusMessage);
    }

    [Fact]
    public async Task IsInstalledOnDisk_FalseWhenAFileHasTheWrongSize()
    {
        (SttModelDownload model, Dictionary<string, byte[]> bodies) = Model(("encoder.onnx", "graph"), ("tokens.txt", "a b"));
        await NewInstaller(new FakeHandler(bodies), model).InstallAsync(TestContext.Current.CancellationToken);
        Assert.True(SttModelInstaller.IsInstalledOnDisk(_root, model));

        File.WriteAllText(Path.Combine(SttModelInstaller.ModelDir(_root, ModelId), "tokens.txt"), "a b c");

        Assert.False(SttModelInstaller.IsInstalledOnDisk(_root, model));
    }

    [Fact]
    public async Task Install_RefusesWhileAnotherHolderHasTheRootLock()
    {
        (SttModelDownload model, Dictionary<string, byte[]> bodies) = Model(("encoder.onnx", "graph"));
        var handler = new FakeHandler(bodies);
        SttModelInstaller installer = NewInstaller(handler, model);

        using (FileStream? held = SttModelInstaller.TryLockRoot(_root))
        {
            Assert.NotNull(held);
            InvalidOperationException install = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                installer.InstallAsync(TestContext.Current.CancellationToken)
            );
            Assert.Equal(RefusalMessage, install.Message);
            Assert.Equal(RefusalMessage, installer.StatusMessage);
            Assert.False(installer.IsBusy);
            InvalidOperationException delete = await Assert.ThrowsAsync<InvalidOperationException>(installer.DeleteAsync);
            Assert.Equal(RefusalMessage, delete.Message);
            Assert.Equal(RefusalMessage, installer.StatusMessage);
        }

        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(SttModelInstaller.ModelDir(_root, ModelId)));
    }

    [Theory]
    [InlineData("C:x")]
    [InlineData("a:b")]
    [InlineData("NUL")]
    [InlineData("con.txt")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void ReadModels_RejectsUnsafeNames(string name)
    {
        string asFileName = WriteManifest("as-file-name.json", "good-model", name);
        string asId = WriteManifest("as-id.json", name, "tokens.txt");

        InvalidDataException fileEx = Assert.Throws<InvalidDataException>(() => SttModelInstaller.ReadModels(asFileName));
        InvalidDataException idEx = Assert.Throws<InvalidDataException>(() => SttModelInstaller.ReadModels(asId));

        Assert.Contains($"'{name}'", fileEx.Message, StringComparison.Ordinal);
        Assert.Contains($"'{name}'", idEx.Message, StringComparison.Ordinal);
        Assert.Single(SttModelInstaller.ReadModels(WriteManifest("control.json", "good-model", "tokens.txt")));
    }

    [Fact]
    public async Task Install_OverAnExistingModel_ReplacesItAndLeavesOnlyTheModelFolder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (SttModelDownload first, Dictionary<string, byte[]> firstBodies) = Model(("encoder.onnx", "graph A"), ("tokens.txt", "a"));
        await NewInstaller(new FakeHandler(firstBodies), first).InstallAsync(ct);
        (SttModelDownload second, Dictionary<string, byte[]> secondBodies) = Model(("encoder.onnx", "graph B, longer"), ("tokens.txt", "b c"));
        SttModelInstaller installer = NewInstaller(new FakeHandler(secondBodies), second);
        Assert.False(installer.IsInstalled);

        await installer.InstallAsync(ct);

        string modelDir = SttModelInstaller.ModelDir(_root, ModelId);
        Assert.All(second.Files, f => Assert.Equal(secondBodies[f.Urls[0]], File.ReadAllBytes(Path.Combine(modelDir, f.Name))));
        Assert.True(installer.IsInstalled);
        Assert.True(SttModelInstaller.IsInstalledOnDisk(_root, second));
        Assert.False(SttModelInstaller.IsInstalledOnDisk(_root, first));
        Assert.Equal([modelDir], Directory.GetFileSystemEntries(_root));
        Assert.True(File.Exists(_root + ".lock"));
    }

    [Fact]
    public async Task Install_StaleStagingIsSwept_OldFolderRestoredWhenModelMissing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (SttModelDownload model, Dictionary<string, byte[]> bodies) = Model(("encoder.onnx", "graph"), ("tokens.txt", "a b"));
        await NewInstaller(new FakeHandler(bodies), model).InstallAsync(ct);
        string modelDir = SttModelInstaller.ModelDir(_root, ModelId);
        List<string> before = Snapshot();
        SttModelInstaller installer = NewInstaller(new FakeHandler([]), model);
        Directory.Move(modelDir, modelDir + ".old-orphan");
        string stale = modelDir + ".staging-stale";
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "encoder.onnx"), "partial");

        await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync(ct));

        Assert.Equal(before, Snapshot());
        Assert.True(SttModelInstaller.IsInstalledOnDisk(_root, model));
        Assert.Equal([modelDir], Directory.GetFileSystemEntries(_root));

        Directory.Move(modelDir, modelDir + ".old-orphan");
        SttModelInstaller fresh = NewInstaller(new FakeHandler([]), model);

        Assert.True(fresh.IsInstalled);
        Assert.Equal("Ready", fresh.StatusMessage);
        Assert.Equal(before, Snapshot());
        Assert.Equal([modelDir], Directory.GetFileSystemEntries(_root));
    }

    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "Speech", "stt-downloads.json");

    /// <summary>Writes a one-model, one-file manifest under the test root and returns its path.</summary>
    private string WriteManifest(string fileName, string id, string name)
    {
        var manifest = new
        {
            baseUrl = "https://r2.example/",
            models = new[]
            {
                new
                {
                    id,
                    files = new[]
                    {
                        new
                        {
                            name,
                            key = "stt/x",
                            size = 1,
                            sha256 = new string('0', 64),
                        },
                    },
                },
            },
        };
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    private SttModelInstaller NewInstaller(FakeHandler handler, SttModelDownload model) =>
        new(new HttpClient(handler), _root, model, TimeProvider.System);

    /// <summary>Every file under the model folder, each as its name and SHA-256, in name order.</summary>
    private List<string> Snapshot()
    {
        string modelDir = SttModelInstaller.ModelDir(_root, ModelId);
        return
        [
            .. Directory
                .GetFiles(modelDir)
                .Order(StringComparer.Ordinal)
                .Select(path => $"{Path.GetFileName(path)} {Sha256Hex(File.ReadAllBytes(path))}"),
        ];
    }

    private static (SttModelDownload Model, Dictionary<string, byte[]> Bodies) Model(params (string Name, string Content)[] files) =>
        ModelOf([.. files.Select(f => (f.Name, Encoding.UTF8.GetBytes(f.Content)))]);

    private static (SttModelDownload Model, Dictionary<string, byte[]> Bodies) ModelOf(params (string Name, byte[] Content)[] files)
    {
        Dictionary<string, byte[]> bodies = [];
        List<SttModelFile> modelFiles = [];
        foreach ((string name, byte[] content) in files)
        {
            string url = BaseUrl + name;
            bodies[url] = content;
            modelFiles.Add(new SttModelFile(name, [url], content.Length, Sha256Hex(content)));
        }
        return (new SttModelDownload(ModelId, modelFiles), bodies);
    }

    private static HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Serves a URL's responder when it has one, else its fixed body (404 when it has neither), recording each request.</summary>
    private sealed class FakeHandler(Dictionary<string, byte[]> bodies) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Responders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (Responders.TryGetValue(url, out Func<HttpRequestMessage, HttpResponseMessage>? responder))
            {
                return Task.FromResult(responder(request));
            }
            HttpResponseMessage response = bodies.TryGetValue(url, out byte[]? body)
                ? Ok(new ByteArrayContent(body))
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

    /// <summary>
    /// Serves the first half of <paramref name="body"/>, then signals <paramref name="entered"/> and waits for
    /// <paramref name="release"/> before serving the rest.
    /// </summary>
    private sealed class HalfThenWaitStream(byte[] body, TaskCompletionSource entered, Task release) : BodyStream
    {
        private int _served;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int half = body.Length / 2;
            if (_served == half)
            {
                entered.TrySetResult();
                await release.WaitAsync(cancellationToken);
            }
            int end = (_served < half) ? half : body.Length;
            int count = Math.Min(buffer.Length, end - _served);
            body.AsMemory(_served, count).CopyTo(buffer);
            _served += count;
            return count;
        }
    }
}
