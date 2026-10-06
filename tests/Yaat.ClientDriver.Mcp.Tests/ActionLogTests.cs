extern alias mcp;

using System.Text.Json;
using mcp::Yaat.Client.Automation.Protocol;
using mcp::Yaat.ClientDriver.Mcp.Recording;
using Xunit;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The action log a recording writes beside its MP4, <c>&lt;clip&gt;-actions.jsonl</c>: a header with the first frame's instant,
/// the frame rate, the client-area crop and the window's scale, then one line per input action of the recorded process, with
/// null for the fields its kind does not have; nothing while no recording of that process runs.
/// </summary>
public sealed class ActionLogTests : IDisposable
{
    private const int RecordedPid = 4242;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"yaat-action-log-test-{Guid.NewGuid():N}");
    private readonly FakeRecordingBackend _backend = new();
    private readonly ManualClock _clock = new(RecordingFakes.Start);
    private readonly RecordingSession _session;

    public ActionLogTests() => _session = RecordingFakes.NewSession(_backend, _clock);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Mp4 => Path.Combine(_directory, "clip.mp4");

    private static PointerSite Site(double x, double y) => new(7, "YAAT", x, y, 1.5);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task LogAction_WhileRecording_WritesTheHeaderThenOneLinePerAction()
    {
        RecordingStarted started = await StartAsync();
        RecordingFakes.WriteRecorderLog(Mp4, RecorderLog.FirstFrameLine(_clock.Now.UtcDateTime));
        _clock.Now += TimeSpan.FromSeconds(1.5);
        DateTime clickUtc = _session.UtcNow;
        var drag = new DragResult(9, "Border", Site(30, 20), 90, 25, "left", 4, 200, 270);

        Assert.Null(await _session.LogActionAsync(RecordedAction.Click(RecordedPid, clickUtc, Site(12.5, 40), "Left", 2), Ct));
        Assert.Null(await _session.LogActionAsync(RecordedAction.Hover(RecordedPid, clickUtc.AddSeconds(1), Site(5, 6), 500), Ct));
        Assert.Null(await _session.LogActionAsync(RecordedAction.Drag(RecordedPid, clickUtc.AddSeconds(2), drag), Ct));
        Assert.Null(await _session.LogActionAsync(RecordedAction.Hover(RecordedPid + 1, clickUtc, Site(1, 1), 0), Ct));

        string text = await File.ReadAllTextAsync(RecordingPaths.For(Mp4).Actions, Ct);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        CropBox crop = started.Crop;
        Assert.Equal(
            $"{{\"kind\":\"header\",\"startedUtc\":\"2026-10-03T12:00:00Z\",\"fps\":30,\"cropX\":{crop.X},\"cropY\":{crop.Y},"
                + $"\"cropWidth\":{crop.Width},\"cropHeight\":{crop.Height},\"renderScaling\":1.5}}",
            lines[0]
        );
        Assert.Equal(
            "{\"kind\":\"click\",\"wallUtc\":\"2026-10-03T12:00:01.5Z\",\"clipSeconds\":1.5,\"window\":\"YAAT\",\"x\":12.5,\"y\":40,"
                + "\"toX\":null,\"toY\":null,\"button\":\"left\",\"clickCount\":2,\"holdMs\":null,\"durationMs\":null,\"steps\":null}",
            lines[1]
        );
        Assert.Equal(
            "{\"kind\":\"hover\",\"wallUtc\":\"2026-10-03T12:00:02.5Z\",\"clipSeconds\":2.5,\"window\":\"YAAT\",\"x\":5,\"y\":6,"
                + "\"toX\":null,\"toY\":null,\"button\":null,\"clickCount\":null,\"holdMs\":null,\"durationMs\":500,\"steps\":null}",
            lines[2]
        );
        Assert.Equal(
            "{\"kind\":\"drag\",\"wallUtc\":\"2026-10-03T12:00:03.5Z\",\"clipSeconds\":3.5,\"window\":\"YAAT\",\"x\":30,\"y\":20,"
                + "\"toX\":90,\"toY\":25,\"button\":\"left\",\"clickCount\":null,\"holdMs\":200,\"durationMs\":270,\"steps\":4}",
            lines[3]
        );
        foreach (string line in lines)
        {
            using var parsed = JsonDocument.Parse(line);
        }
    }

    [Fact]
    public async Task Stop_AfterActionsBeforeTheFirstFrame_WritesTheStartAndTheirClipSeconds()
    {
        await StartAsync();
        _clock.Now += TimeSpan.FromSeconds(2);
        Assert.Null(await _session.LogActionAsync(RecordedAction.Hover(RecordedPid, _session.UtcNow, Site(5, 6), 0), Ct));
        string[] early = await File.ReadAllLinesAsync(RecordingPaths.For(Mp4).Actions, Ct);
        Assert.Contains("\"startedUtc\":null", early[0], StringComparison.Ordinal);
        Assert.Contains("\"clipSeconds\":null", early[1], StringComparison.Ordinal);
        RecordingFakes.WriteRecorderLog(Mp4, RecorderLog.FirstFrameLine(RecordingFakes.Start.UtcDateTime.AddSeconds(0.5)));

        await _session.StopAsync(Ct);

        string text = await File.ReadAllTextAsync(RecordingPaths.For(Mp4).Actions, Ct);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("{\"kind\":\"header\",\"startedUtc\":\"2026-10-03T12:00:00.5Z\",\"fps\":30,", lines[0], StringComparison.Ordinal);
        Assert.Equal(
            "{\"kind\":\"hover\",\"wallUtc\":\"2026-10-03T12:00:02Z\",\"clipSeconds\":1.5,\"window\":\"YAAT\",\"x\":5,\"y\":6,"
                + "\"toX\":null,\"toY\":null,\"button\":null,\"clickCount\":null,\"holdMs\":null,\"durationMs\":0,\"steps\":null}",
            lines[1]
        );
    }

    [Fact]
    public async Task LogAction_StampedWhileRecording_IsLoggedAfterTheStop()
    {
        await StartAsync();
        DateTime stampedUtc = _session.UtcNow;
        await _session.StopAsync(Ct);

        Assert.Null(await _session.LogActionAsync(RecordedAction.Hover(RecordedPid, stampedUtc, Site(5, 6), 0), Ct));
        _clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(await _session.LogActionAsync(RecordedAction.Hover(RecordedPid, _session.UtcNow, Site(7, 8), 0), Ct));

        string[] lines = await File.ReadAllLinesAsync(RecordingPaths.For(Mp4).Actions, Ct);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"x\":5,", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogAction_NothingRecording_WritesNothing()
    {
        Directory.CreateDirectory(_directory);

        Assert.Null(await _session.LogActionAsync(RecordedAction.Hover(RecordedPid, _session.UtcNow, Site(5, 6), 0), Ct));

        Assert.False(File.Exists(RecordingPaths.For(Mp4).Actions));
    }

    private Task<RecordingStarted> StartAsync() =>
        _session.StartAsync(new RecordingRequest(new RecordingTarget(RecordedPid, 0x1234, false), @"C:\tools\ffmpeg\ffmpeg.exe", Mp4, 30, false), Ct);
}
