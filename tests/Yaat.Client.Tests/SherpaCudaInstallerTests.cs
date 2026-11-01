using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-sherpa-cuda-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
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
        Assert.False(Directory.Exists(Path.Combine(_root, ".partial")));
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
        Assert.False(Directory.Exists(Path.Combine(_root, ".partial")));
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

    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "Speech", "stt-downloads.json");

    private SherpaCudaInstaller NewInstaller(FakeHandler handler, IReadOnlyList<SherpaCudaDownload> downloads) =>
        new(new HttpClient(handler), _root, downloads);

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
    )
    {
        byte[] bytes = ZipOf(files);
        return (new SherpaCudaDownload(name, urls, bytes.Length, Sha256Hex(bytes), entries), bytes);
    }

    private static byte[] ZipOf((string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string content) in files)
            {
                using Stream entry = zip.CreateEntry(path).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return stream.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Serves fixed bodies by URL (404 otherwise), recording each request and whether a watched file existed then.</summary>
    private sealed class FakeHandler(Dictionary<string, byte[]> bodies, string? watchedPath) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<bool> WatchedFileExisted { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            WatchedFileExisted.Add((watchedPath is not null) && File.Exists(watchedPath));
            HttpResponseMessage response = bodies.TryGetValue(url, out byte[]? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
