using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>The app-lifetime STT download owner: its installers per manifest model, and the events it raises.</summary>
public sealed class SttDownloadsTests : IDisposable
{
    private const string ModelId = "test-model";
    private const string FileUrl = "https://r2.example/stt/test-model/tokens.txt";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-stt-downloads-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        if (File.Exists(ModelRoot + ".lock"))
        {
            File.Delete(ModelRoot + ".lock");
        }
    }

    private string ModelRoot => Path.Combine(_root, "models");

    private static string ShippedManifestPath => Path.Combine(AppContext.BaseDirectory, "Speech", "stt-downloads.json");

    [Fact]
    public void MissingManifest_GivesNoInstallers_AndDoesNotThrow()
    {
        using var downloads = new SttDownloads(Path.Combine(_root, "no-such-manifest.json"), ModelRoot, TimeProvider.System);

        Assert.Empty(downloads.Models);
        Assert.Null(downloads.ModelInstaller("rasr-parakeet-v2-fp32"));
        Assert.Null(downloads.Gpu);
    }

    [Fact]
    public void ModelInstaller_ReturnsOnePerManifestModel_AndNullForAnUnknownId()
    {
        using var downloads = new SttDownloads(ShippedManifestPath, ModelRoot, TimeProvider.System);

        Assert.Equal(["rasr-parakeet-v2-fp32", "rasr-parakeet-v2-int8"], downloads.Models.Select(m => m.Id));
        SttModelInstaller? fp32 = downloads.ModelInstaller("rasr-parakeet-v2-fp32");
        SttModelInstaller? int8 = downloads.ModelInstaller("rasr-parakeet-v2-int8");
        Assert.NotNull(fp32);
        Assert.NotNull(int8);
        Assert.NotSame(fp32, int8);
        Assert.Same(fp32, downloads.ModelInstaller("rasr-parakeet-v2-fp32"));
        Assert.Null(downloads.ModelInstaller("no-such-model"));
    }

    [Fact]
    public async Task ModelInstalled_IsRaisedOnceWhenAModelInstalls()
    {
        byte[] body = Encoding.UTF8.GetBytes("a b");
        string manifestPath = WriteManifest(body);
        using var downloads = new SttDownloads(
            manifestPath,
            ModelRoot,
            TimeProvider.System,
            new HttpClient(new FakeHandler(body)),
            AppContext.BaseDirectory
        );
        List<string> installed = [];
        downloads.ModelInstalled += id =>
        {
            lock (installed)
            {
                installed.Add(id);
            }
        };
        SttModelInstaller installer = downloads.ModelInstaller(ModelId)!;

        await installer.InstallAsync(TestContext.Current.CancellationToken);
        await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.True(installer.IsInstalled);
        Assert.Equal([ModelId], installed);
    }

    [Fact]
    public async Task TrackEngineLoad_IsLoadingUntilTheLastOverlappingLoadCompletes_AndRaisesOnEachEdge()
    {
        using var downloads = new SttDownloads(Path.Combine(_root, "no-such-manifest.json"), ModelRoot, TimeProvider.System);
        List<bool> seen = [];
        downloads.EngineLoadingChanged += () => seen.Add(downloads.IsEngineLoading);
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();

        Task firstLoad = downloads.TrackEngineLoadAsync(() => first.Task);
        Task secondLoad = downloads.TrackEngineLoadAsync(() => second.Task);
        Assert.True(downloads.IsEngineLoading);
        first.SetException(new InvalidOperationException("the model failed to load"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstLoad);
        Assert.True(downloads.IsEngineLoading);
        second.SetResult();
        await secondLoad;

        Assert.False(downloads.IsEngineLoading);
        Assert.Equal([true, false], seen);
    }

    /// <summary>Writes a one-model, one-file manifest whose file is <paramref name="body"/> and returns its path.</summary>
    private string WriteManifest(byte[] body)
    {
        var manifest = new
        {
            baseUrl = "https://r2.example/",
            models = new[]
            {
                new
                {
                    id = ModelId,
                    files = new[]
                    {
                        new
                        {
                            name = "tokens.txt",
                            key = "stt/test-model/tokens.txt",
                            size = body.Length,
                            sha256 = Convert.ToHexStringLower(SHA256.HashData(body)),
                        },
                    },
                },
            },
        };
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    /// <summary>Serves <paramref name="body"/> at <see cref="FileUrl"/> and 404 everywhere else.</summary>
    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(
                (request.RequestUri!.ToString() == FileUrl)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
            );
    }
}
