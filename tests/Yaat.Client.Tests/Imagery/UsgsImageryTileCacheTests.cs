using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Xunit;
using Yaat.Client.Services.Imagery;
using Yaat.Sim.Authoring.Imagery;

namespace Yaat.Client.Tests.Imagery;

/// <summary>
/// The USGS tile cache over a stub HTTP handler, a temp disk cache, a fake clock and a test-drained post queue: disk
/// first, retries, negative entries, the request gate, cancellation, the toggle, Dispose, two writers and the memory cache.
/// </summary>
public sealed class UsgsImageryTileCacheTests : IDisposable
{
    private static readonly TileKey Koak = new(17, 21038, 50690);

    private readonly string _root = Directory.CreateTempSubdirectory("usgs-tiles-").FullName;
    private readonly FakeTimeProvider _clock = new();
    private readonly StubTileHandler _http = new();
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly SemaphoreSlim _retries = new(0);
    private int _arrivals;

    private string KoakPath => Path.Combine(_root, "17", "21038", "50690.jpg");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_Downloaded_Tile_Is_Decoded_Written_And_Then_Served_From_Memory()
    {
        byte[] jpeg = StubTileHandler.NoiseJpeg(1);
        _http.AutoRespond = _ => StubTileHandler.Jpeg(jpeg);
        using UsgsImageryTileCache cache = NewCache();

        Assert.Empty(cache.GetVisible([Koak]));
        await IdleAsync(cache);

        Assert.Equal(1, Drain());
        ImageryTile tile = Assert.Single(cache.GetVisible([Koak]));
        Assert.Equal(256, tile.Image.Width);
        Assert.False(tile.Image.IsLazyGenerated);
        Assert.Equal(jpeg, File.ReadAllBytes(KoakPath));
        cache.GetVisible([Koak]);
        HttpRequestMessage request = Assert.Single(_http.Requests);
        Assert.Equal(
            "https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer/exportImage?bbox=-13605179.54,4538830.74,"
                + "-13604873.79,4539136.49&bboxSR=3857&imageSR=3857&size=256,256&format=jpg&f=image",
            request.RequestUri!.ToString()
        );
        Assert.Equal("yaat-imagery", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task A_Disk_Tile_Younger_Than_90_Days_Is_Used_Without_A_Request()
    {
        WriteKoakFile(StubTileHandler.NoiseJpeg(2), TimeSpan.FromDays(89));
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        await IdleAsync(cache);

        Assert.Single(cache.GetVisible([Koak]));
        Assert.Equal(0, _http.RequestCount);
    }

    [Fact]
    public async Task A_Disk_Tile_Older_Than_90_Days_Is_Shown_And_Refreshed()
    {
        byte[] fresh = StubTileHandler.NoiseJpeg(4);
        WriteKoakFile(StubTileHandler.NoiseJpeg(3), TimeSpan.FromDays(91));
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        PendingTileRequest refresh = await _http.NextAsync();
        Assert.Single(cache.GetVisible([Koak]));
        refresh.Answer(StubTileHandler.Jpeg(fresh));
        await IdleAsync(cache);

        Assert.Equal(fresh, File.ReadAllBytes(KoakPath));
        Assert.Single(cache.GetVisible([Koak]));
    }

    [Fact]
    public async Task A_Failed_Refresh_Keeps_The_Old_File_And_Tile()
    {
        byte[] old = StubTileHandler.NoiseJpeg(5);
        WriteKoakFile(old, TimeSpan.FromDays(120));
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        (await _http.NextAsync()).Answer(StubTileHandler.Status(HttpStatusCode.NotFound));
        await IdleAsync(cache);

        Assert.Equal(old, File.ReadAllBytes(KoakPath));
        Assert.Single(cache.GetVisible([Koak]));
        Assert.Equal(0, cache.NegativeCount);
    }

    [Fact]
    public async Task A_502_Then_A_200_Is_Shown_After_One_Retry()
    {
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        (await _http.NextAsync()).Answer(StubTileHandler.Status(HttpStatusCode.BadGateway));
        await RetryScheduledAsync();
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        (await _http.NextAsync()).Answer(StubTileHandler.Jpeg(StubTileHandler.NoiseJpeg(6)));
        await IdleAsync(cache);

        Assert.Single(cache.GetVisible([Koak]));
        Assert.Equal(2, _http.RequestCount);
    }

    [Fact]
    public async Task Three_502s_Leave_The_Tile_Empty_And_Unasked_Until_60_Seconds_Pass()
    {
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        await FailThreeTimesAsync(cache, pending => pending.Answer(StubTileHandler.Status(HttpStatusCode.BadGateway)));
        Assert.Empty(cache.GetVisible([Koak]));
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(cache.GetVisible([Koak]));
        Assert.Equal(3, _http.RequestCount);

        _clock.Advance(TimeSpan.FromSeconds(1));
        cache.GetVisible([Koak]);
        (await _http.NextAsync()).Answer(StubTileHandler.Status(HttpStatusCode.NotFound));
        await IdleAsync(cache);
        Assert.Equal(4, _http.RequestCount);
    }

    [Fact]
    public async Task A_404_Is_Never_Asked_Again()
    {
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        (await _http.NextAsync()).Answer(StubTileHandler.Status(HttpStatusCode.NotFound));
        await IdleAsync(cache);
        _clock.Advance(TimeSpan.FromDays(1));

        Assert.Empty(cache.GetVisible([Koak]));
        await IdleAsync(cache);
        Assert.Equal(1, _http.RequestCount);
        Assert.Equal(ImageryTileState.AllShown, cache.State([Koak]));
    }

    [Fact]
    public async Task A_200_With_A_Json_Body_Is_Negative_And_Not_Written()
    {
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        (await _http.NextAsync()).Answer(StubTileHandler.Json());
        await IdleAsync(cache);

        Assert.Empty(cache.GetVisible([Koak]));
        Assert.Equal(1, _http.RequestCount);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_Uniform_Colour_Tile_Is_Negative_And_Not_Written()
    {
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        (await _http.NextAsync()).Answer(StubTileHandler.Jpeg(StubTileHandler.UniformJpeg()));
        await IdleAsync(cache);

        Assert.Empty(cache.GetVisible([Koak]));
        Assert.Equal(1, _http.RequestCount);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task At_Most_Four_Requests_Are_In_Flight_With_Ten_Tiles_Wanted()
    {
        byte[] jpeg = StubTileHandler.NoiseJpeg(7);
        TileKey[] wanted = Row(21038, 10);
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible(wanted);
        var held = new Queue<PendingTileRequest>();
        for (int received = 0; received < wanted.Length; received++)
        {
            if (held.Count == 4)
            {
                held.Dequeue().Answer(StubTileHandler.Jpeg(jpeg));
            }
            held.Enqueue(await _http.NextAsync());
        }

        while (held.TryDequeue(out PendingTileRequest? pending))
        {
            pending.Answer(StubTileHandler.Jpeg(jpeg));
        }
        await IdleAsync(cache);

        Assert.Equal(4, _http.MaxOutstanding);
        Assert.Equal(10, cache.GetVisible(wanted).Count);
    }

    [Fact]
    public async Task A_New_Wanted_Set_Cancels_Only_The_Queued_Tiles_Outside_Its_Ring()
    {
        byte[] jpeg = StubTileHandler.NoiseJpeg(8);
        TileKey[] first = Row(21038, 6);
        TileKey[] second = Row(21100, 2);
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible(first);
        var running = new List<PendingTileRequest>();
        for (int i = 0; i < 4; i++)
        {
            running.Add(await _http.NextAsync());
        }

        cache.GetVisible(second);
        running.ForEach(pending => pending.Answer(StubTileHandler.Jpeg(jpeg)));
        (await _http.NextAsync()).Answer(StubTileHandler.Jpeg(jpeg));
        (await _http.NextAsync()).Answer(StubTileHandler.Jpeg(jpeg));
        await IdleAsync(cache);

        AssertRequestedExactly(running, second);
        cache.SetActive(false);
        Assert.Equal(4, cache.GetVisible(first).Count);
        Assert.Equal(2, cache.GetVisible(second).Count);
    }

    [Fact]
    public async Task A_Cancelled_Queued_Fetch_That_Wins_A_Permit_Never_Reaches_The_Handler()
    {
        byte[] jpeg = StubTileHandler.NoiseJpeg(11);
        TileKey[] first = Row(21038, 6);
        TileKey[] second = Row(21100, 2);
        var gate = new SemaphoreSlim(4);
        using var cache = new UsgsImageryTileCache(
            new UsgsImageryTileCache.Transport(new HttpClient(_http), gate, OwnsHttp: true),
            _root,
            _clock,
            _posted.Enqueue,
            () => { }
        );

        cache.GetVisible(first);
        var running = new List<PendingTileRequest>();
        for (int i = 0; i < 4; i++)
        {
            running.Add(await _http.NextAsync());
        }

        // The two queued fetches are cancelled; a permit freed at once goes to the head of the gate's queue.
        cache.GetVisible(second);
        gate.Release();
        running.ForEach(pending => pending.Answer(StubTileHandler.Jpeg(jpeg)));
        (await _http.NextAsync()).Answer(StubTileHandler.Jpeg(jpeg));
        (await _http.NextAsync()).Answer(StubTileHandler.Jpeg(jpeg));
        await IdleAsync(cache);

        AssertRequestedExactly(running, second);
    }

    [Fact]
    public async Task A_Corrupt_Disk_File_Is_Replaced_By_The_Next_Good_Download()
    {
        byte[] good = StubTileHandler.NoiseJpeg(12);
        WriteKoakFile([1, 2, 3, 4], TimeSpan.FromDays(1));
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        (await _http.NextAsync()).Answer(StubTileHandler.Jpeg(good));
        await IdleAsync(cache);

        Assert.Equal(good, File.ReadAllBytes(KoakPath));
        Assert.Single(cache.GetVisible([Koak]));
    }

    [Fact]
    public async Task Turning_Off_Between_A_Publish_And_Its_Announcement_Runs_No_Callback()
    {
        _http.AutoRespond = _ => StubTileHandler.Jpeg(StubTileHandler.NoiseJpeg(13));
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        await IdleAsync(cache);
        Assert.False(_posted.IsEmpty);
        cache.SetActive(false);

        Assert.Equal(0, Drain());
    }

    [Fact]
    public async Task An_Image_Content_Type_Is_Accepted_In_Any_Case()
    {
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        HttpResponseMessage response = StubTileHandler.Jpeg(StubTileHandler.NoiseJpeg(14));
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("Image/JPEG");
        (await _http.NextAsync()).Answer(response);
        await IdleAsync(cache);

        Assert.Single(cache.GetVisible([Koak]));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_408_Or_429_Is_Retried_Twice_Then_Held_Off_For_60_Seconds(HttpStatusCode status)
    {
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        await FailThreeTimesAsync(cache, pending => pending.Answer(StubTileHandler.Status(status)));

        Assert.Equal(3, _http.RequestCount);
        Assert.Equal(ImageryTileState.Offline, cache.State([Koak]));
        _clock.Advance(TimeSpan.FromSeconds(60));
        cache.GetVisible([Koak]);
        (await _http.NextAsync()).Answer(StubTileHandler.Status(HttpStatusCode.NotFound));
        await IdleAsync(cache);
    }

    [Fact]
    public async Task Turning_Off_Mid_Fetch_Publishes_And_Announces_Nothing_And_Leaves_No_Negative_Entry()
    {
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        await _http.NextAsync();
        cache.SetActive(false);
        await IdleAsync(cache);

        Assert.Equal(0, Drain());
        Assert.Equal(0, cache.NegativeCount);
        Assert.Empty(cache.GetVisible([Koak]));
        Assert.Equal(1, _http.RequestCount);
        cache.SetActive(true);
        Assert.Empty(cache.GetVisible([Koak]));
        (await _http.NextAsync()).Answer(StubTileHandler.Status(HttpStatusCode.NotFound));
        await IdleAsync(cache);
    }

    [Fact]
    public async Task Disposing_Mid_Fetch_Publishes_And_Announces_Nothing_And_Leaves_No_Negative_Entry()
    {
        UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        await _http.NextAsync();
        cache.Dispose();
        await IdleAsync(cache);

        Assert.Equal(0, Drain());
        Assert.Equal(0, cache.NegativeCount);
        Assert.Empty(cache.GetVisible([Koak]));
        Assert.Equal(1, _http.RequestCount);
    }

    [Fact]
    public async Task Two_Caches_On_One_Root_Writing_The_Same_Tile_Leave_One_Valid_File()
    {
        byte[] jpeg = StubTileHandler.NoiseJpeg(9);
        var otherHttp = new StubTileHandler();
        using UsgsImageryTileCache cache = NewCache();
        using var other = new UsgsImageryTileCache(otherHttp, _root, _clock, _posted.Enqueue, () => { });

        cache.GetVisible([Koak]);
        other.GetVisible([Koak]);
        PendingTileRequest mine = await _http.NextAsync();
        PendingTileRequest theirs = await otherHttp.NextAsync();
        mine.Answer(StubTileHandler.Jpeg(jpeg));
        theirs.Answer(StubTileHandler.Jpeg(jpeg));
        await IdleAsync(cache);
        await IdleAsync(other);

        string file = Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Equal(KoakPath, file);
        Assert.Equal(jpeg, File.ReadAllBytes(file));
        Assert.Single(cache.GetVisible([Koak]));
        Assert.Single(other.GetVisible([Koak]));
    }

    [Fact]
    public async Task The_Memory_Cache_Never_Evicts_A_Wanted_Tile()
    {
        byte[] jpeg = StubTileHandler.NoiseJpeg(10);
        _http.AutoRespond = _ => StubTileHandler.Jpeg(jpeg);
        TileKey[] all = Row(21000, 70);
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible(all);
        await IdleAsync(cache);
        Assert.Equal(70, cache.GetVisible(all).Count);
        cache.GetVisible([all[0]]);

        cache.SetActive(false);
        IReadOnlyList<ImageryTile> kept = cache.GetVisible(all);
        Assert.Equal(65, kept.Count);
        Assert.Contains(kept, tile => tile.Key == all[0]);
    }

    [Fact]
    public async Task The_State_Is_Loading_While_Fetching_And_Offline_After_Network_Failures_With_No_Disk_Copy()
    {
        using UsgsImageryTileCache cache = NewCache();

        cache.GetVisible([Koak]);
        Assert.Equal(ImageryTileState.Loading, cache.State([Koak]));
        await FailThreeTimesAsync(cache, pending => pending.Fail());

        Assert.Equal(ImageryTileState.Offline, cache.State([Koak]));
        Assert.Equal(1, Drain());
    }

    private static TileKey[] Row(int firstX, int count) => [.. Enumerable.Range(firstX, count).Select(x => new TileKey(17, x, 50690))];

    private static Task IdleAsync(UsgsImageryTileCache cache) => cache.Idle.WaitAsync(StubTileHandler.Guard);

    private void AssertRequestedExactly(List<PendingTileRequest> running, TileKey[] second)
    {
        IEnumerable<string> expected = running
            .Select(pending => pending.Request.RequestUri!.ToString())
            .Concat(second.Select(UsgsImageryTileCache.TileUrl));
        Assert.Equal(expected.Order(StringComparer.Ordinal), _http.Requests.Select(r => r.RequestUri!.ToString()).Order(StringComparer.Ordinal));
    }

    private UsgsImageryTileCache NewCache()
    {
        var cache = new UsgsImageryTileCache(_http, _root, _clock, _posted.Enqueue, () => Interlocked.Increment(ref _arrivals))
        {
            RetryScheduled = _ => _retries.Release(),
        };
        return cache;
    }

    private async Task FailThreeTimesAsync(UsgsImageryTileCache cache, Action<PendingTileRequest> fail)
    {
        fail(await _http.NextAsync());
        await RetryScheduledAsync();
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        fail(await _http.NextAsync());
        await RetryScheduledAsync();
        _clock.Advance(TimeSpan.FromSeconds(2));
        fail(await _http.NextAsync());
        await IdleAsync(cache);
    }

    private async Task RetryScheduledAsync() => Assert.True(await _retries.WaitAsync(StubTileHandler.Guard));

    private int Drain()
    {
        while (_posted.TryDequeue(out Action? action))
        {
            action();
        }
        return Volatile.Read(ref _arrivals);
    }

    private void WriteKoakFile(byte[] bytes, TimeSpan age)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KoakPath)!);
        File.WriteAllBytes(KoakPath, bytes);
        File.SetLastWriteTimeUtc(KoakPath, (_clock.GetUtcNow() - age).UtcDateTime);
    }
}

/// <summary>One request the stub holds until the test answers it.</summary>
internal sealed class PendingTileRequest(HttpRequestMessage request)
{
    public HttpRequestMessage Request { get; } = request;

    public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Answer(HttpResponseMessage response) => Response.TrySetResult(response);

    public void Fail() => Response.TrySetException(new HttpRequestException("stub network failure"));
}

/// <summary>
/// An HTTP handler answering each request from its own <see cref="TaskCompletionSource{T}"/>, or at once through
/// <see cref="AutoRespond"/>; it counts the requests and the most it held unanswered at one time.
/// </summary>
internal sealed class StubTileHandler : HttpMessageHandler
{
    // A hang guard only: a passing run never waits it out, so it is sized in minutes for a loaded builder.
    public static readonly TimeSpan Guard = TimeSpan.FromMinutes(2);

    private readonly Channel<PendingTileRequest> _pending = Channel.CreateUnbounded<PendingTileRequest>();
    private int _count;
    private int _outstanding;
    private int _maxOutstanding;

    public Func<HttpRequestMessage, HttpResponseMessage>? AutoRespond { get; set; }

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public int RequestCount => Volatile.Read(ref _count);

    public int MaxOutstanding => Volatile.Read(ref _maxOutstanding);

    public static HttpResponseMessage Jpeg(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    public static HttpResponseMessage Json() =>
        new(HttpStatusCode.OK) { Content = new StringContent("{\"error\":{\"code\":400}}", null, "application/json") };

    /// <summary>A 256 px JPEG of varied pixels, as a real tile is; <paramref name="seed"/> varies the bytes.</summary>
    public static byte[] NoiseJpeg(int seed)
    {
        using var bitmap = new SKBitmap(256, 256);
        var random = new Random(seed);
        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }
        return Encode(bitmap);
    }

    /// <summary>A 256 px JPEG of one colour, as the service answers where it has no imagery.</summary>
    public static byte[] UniformJpeg()
    {
        using var bitmap = new SKBitmap(256, 256);
        bitmap.Erase(SKColors.White);
        return Encode(bitmap);
    }

    /// <summary>The next request the stub holds, in arrival order.</summary>
    public Task<PendingTileRequest> NextAsync() => _pending.Reader.ReadAsync().AsTask().WaitAsync(Guard);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        Requests.Enqueue(request);

        // As a real handler does: a request sent on a cancelled token is refused before it goes out.
        cancellationToken.ThrowIfCancellationRequested();
        RecordOutstanding(Interlocked.Increment(ref _outstanding));
        try
        {
            if (AutoRespond is { } respond)
            {
                return respond(request);
            }

            var pending = new PendingTileRequest(request);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => pending.Response.TrySetCanceled(cancellationToken));
            _pending.Writer.TryWrite(pending);
            return await pending.Response.Task;
        }
        finally
        {
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private void RecordOutstanding(int outstanding)
    {
        int max = Volatile.Read(ref _maxOutstanding);
        while ((outstanding > max) && (Interlocked.CompareExchange(ref _maxOutstanding, outstanding, max) != max))
        {
            max = Volatile.Read(ref _maxOutstanding);
        }
    }
}
