using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="SpeechTelemetryUploader"/> — the opt-in drain that POSTs each
/// upload-pending sample to the connected server as a single-sample zip. A scripted
/// <see cref="HttpMessageHandler"/> records the requests and returns canned responses, so every
/// status-code path and transport failure is deterministic. The shared preferences file is
/// restored to its defaults by <see cref="Fixture.Dispose"/>.
/// </summary>
public sealed class SpeechTelemetryUploaderTests
{
    private const string OfficialServer = UserPreferences.OfficialServerUrl;

    private sealed record SentRequest(string Url, string? Authorization, string? ContentType, byte[] Body);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _scripted = new();

        public List<SentRequest> Requests { get; } = [];

        public void Enqueue(Func<HttpResponseMessage> responder) => _scripted.Enqueue(responder);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(
                new SentRequest(
                    request.RequestUri!.ToString(),
                    request.Headers.Authorization?.ToString(),
                    request.Content?.Headers.ContentType?.MediaType,
                    body
                )
            );

            if (_scripted.Count == 0)
            {
                throw new InvalidOperationException($"Unexpected request to {request.RequestUri}");
            }

            return _scripted.Dequeue()();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-stu-" + Guid.NewGuid().ToString("N"));
        private readonly UserPreferences _prefs = new();

        public Fixture()
        {
            Store = new SpeechSampleStore(_prefs, _root, a => a());
            Handler = new FakeHandler();
            Uploader = new SpeechTelemetryUploader(new HttpClient(Handler), Store, _prefs);
            _prefs.SetSpeechTelemetryEnabled(true);
        }

        public SpeechSampleStore Store { get; }

        public FakeHandler Handler { get; }

        public SpeechTelemetryUploader Uploader { get; }

        public string? ServerUrl { get; set; } = OfficialServer;

        public string? Token { get; set; } = "test-token";

        public void DisableTelemetry() => _prefs.SetSpeechTelemetryEnabled(false);

        public string AddQueuedSample(int seconds) => Store.Add(MakeSession(seconds), MakeAudio(0.3), queueForUpload: true)!;

        public string AddLocalSample(int seconds) => Store.Add(MakeSession(seconds), MakeAudio(0.3), queueForUpload: false)!;

        public Task<int> UploadAsync() => Uploader.UploadPendingAsync(ServerUrl, () => Task.FromResult(Token), TestContext.Current.CancellationToken);

        public void Dispose()
        {
            _prefs.SetSpeechTelemetryEnabled(false);
            _prefs.SetSpeechSampleSettings(enabled: false, maxMb: 50);
            if (Directory.Exists(_root))
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                }
                catch (IOException) { }
            }
        }
    }

    private static SpeechSession MakeSession(int seconds) =>
        new(
            TimestampUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds),
            SampleCount: 4800,
            AudioDurationSeconds: 0.3,
            Transcript: "fly heading 270",
            CanonicalCommand: "FH 270",
            UsedLlmFallback: false,
            TranscribeElapsedMs: 200,
            MapElapsedMs: 5,
            TotalElapsedMs: 210,
            Outcome: SpeechSessionOutcome.CommandAccepted,
            ErrorMessage: null
        );

    private static float[] MakeAudio(double seconds) => new float[(int)(seconds * 16000)];

    private static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

    private static string? BundleSampleId(SentRequest sent)
    {
        using var zip = new ZipArchive(new MemoryStream(sent.Body), ZipArchiveMode.Read);
        var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        return manifest.RootElement.GetProperty("samples").EnumerateArray().Single().GetProperty("id").GetString();
    }

    [Fact]
    public async Task UploadPending_Sends_Each_Sample_Oldest_First_And_Clears_Markers()
    {
        using var fixture = new Fixture();
        string first = fixture.AddQueuedSample(0);
        string second = fixture.AddQueuedSample(1);
        fixture.Handler.Enqueue(NoContent);
        fixture.Handler.Enqueue(NoContent);

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(2, uploaded);
        Assert.Empty(fixture.Store.PendingUploadIds());
        Assert.Equal(2, fixture.Handler.Requests.Count);
        foreach (SentRequest sent in fixture.Handler.Requests)
        {
            Assert.Equal("https://yaat1.leftos.dev/telemetry/speech", sent.Url);
            Assert.Equal("Bearer test-token", sent.Authorization);
            Assert.Equal("application/zip", sent.ContentType);
        }

        Assert.Equal([first, second], fixture.Handler.Requests.Select(BundleSampleId));
    }

    [Fact]
    public async Task UploadPending_Stops_On_Server_Failure_And_Keeps_Samples_Pending()
    {
        using var fixture = new Fixture();
        string first = fixture.AddQueuedSample(0);
        string second = fixture.AddQueuedSample(1);
        fixture.Handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(0, uploaded);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal([first, second], fixture.Store.PendingUploadIds());
    }

    [Fact]
    public async Task UploadPending_Stops_On_Transport_Failure_And_Keeps_Samples_Pending()
    {
        using var fixture = new Fixture();
        string first = fixture.AddQueuedSample(0);
        string second = fixture.AddQueuedSample(1);
        fixture.Handler.Enqueue(() => throw new HttpRequestException("connection reset"));

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(0, uploaded);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal([first, second], fixture.Store.PendingUploadIds());
    }

    [Fact]
    public async Task UploadPending_Drops_Rejected_Sample_And_Uploads_The_Rest()
    {
        using var fixture = new Fixture();
        fixture.AddQueuedSample(0);
        fixture.AddQueuedSample(1);
        fixture.Handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge));
        fixture.Handler.Enqueue(NoContent);

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(1, uploaded);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Empty(fixture.Store.PendingUploadIds());
    }

    [Fact]
    public async Task UploadPending_With_Telemetry_Off_Makes_No_Requests()
    {
        using var fixture = new Fixture();
        string queued = fixture.AddQueuedSample(0);
        fixture.DisableTelemetry();

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(0, uploaded);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal([queued], fixture.Store.PendingUploadIds());
    }

    [Fact]
    public async Task UploadPending_With_No_Server_Url_Makes_No_Requests()
    {
        using var fixture = new Fixture();
        string queued = fixture.AddQueuedSample(0);
        fixture.ServerUrl = "  ";

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(0, uploaded);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal([queued], fixture.Store.PendingUploadIds());
    }

    [Fact]
    public async Task UploadPending_With_No_Token_Makes_No_Requests()
    {
        using var fixture = new Fixture();
        string queued = fixture.AddQueuedSample(0);
        fixture.Token = null;

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(0, uploaded);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal([queued], fixture.Store.PendingUploadIds());
    }

    [Fact]
    public async Task UploadPending_Stops_When_Telemetry_Is_Turned_Off_Mid_Pass()
    {
        using var fixture = new Fixture();
        fixture.AddQueuedSample(0);
        string second = fixture.AddQueuedSample(1);
        fixture.Handler.Enqueue(() =>
        {
            fixture.DisableTelemetry();
            return NoContent();
        });
        fixture.Handler.Enqueue(NoContent);

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(1, uploaded);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal([second], fixture.Store.PendingUploadIds());
    }

    [Fact]
    public async Task UploadPending_Skips_Sample_Whose_Marker_Was_Cleared_Mid_Pass()
    {
        using var fixture = new Fixture();
        fixture.AddQueuedSample(0);
        string second = fixture.AddQueuedSample(1);
        fixture.Handler.Enqueue(() =>
        {
            fixture.Store.MarkUploaded(second);
            return NoContent();
        });
        fixture.Handler.Enqueue(NoContent);

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(1, uploaded);
        Assert.Single(fixture.Handler.Requests);
        Assert.Empty(fixture.Store.PendingUploadIds());
    }

    [Fact]
    public async Task UploadPending_Stops_On_Rate_Limit_And_Keeps_Samples_Pending()
    {
        using var fixture = new Fixture();
        string first = fixture.AddQueuedSample(0);
        string second = fixture.AddQueuedSample(1);
        fixture.Handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        fixture.Handler.Enqueue(NoContent);

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(0, uploaded);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal([first, second], fixture.Store.PendingUploadIds());
    }

    [Theory]
    [InlineData("https://example.test")]
    [InlineData("http://yaat1.leftos.dev")]
    [InlineData("https://yaat1.leftos.dev.example.test")]
    [InlineData("http://localhost:5130")]
    public async Task UploadPending_To_A_Server_Other_Than_The_Official_One_Makes_No_Requests(string serverUrl)
    {
        using var fixture = new Fixture();
        string queued = fixture.AddQueuedSample(0);
        fixture.ServerUrl = serverUrl;
        fixture.Handler.Enqueue(NoContent);

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(0, uploaded);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal([queued], fixture.Store.PendingUploadIds());
    }

    [Fact]
    public async Task UploadPending_Treats_Official_Url_Case_And_Trailing_Slash_As_Official()
    {
        using var fixture = new Fixture();
        fixture.AddQueuedSample(0);
        fixture.ServerUrl = "https://YAAT1.leftos.dev/";
        fixture.Handler.Enqueue(NoContent);

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(1, uploaded);
        Assert.Equal("https://yaat1.leftos.dev/telemetry/speech", Assert.Single(fixture.Handler.Requests).Url);
    }

    [Fact]
    public async Task UploadPending_Never_Sends_Sample_Captured_Without_Queue_For_Upload()
    {
        using var fixture = new Fixture();
        fixture.AddLocalSample(0);
        fixture.Handler.Enqueue(NoContent);

        int uploaded = await fixture.UploadAsync();

        Assert.Equal(0, uploaded);
        Assert.Empty(fixture.Handler.Requests);
    }
}
