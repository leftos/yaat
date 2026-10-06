extern alias mcp;

using System.Diagnostics;
using System.Text.Json;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using mcp::Yaat.ClientDriver.Mcp.Recording;
using mcp::Yaat.ClientDriver.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// <c>batch_drive</c>'s pointer steps in a recording's action log: a click or drag step sent while a recording of the batch's
/// client runs is logged with the fields the standalone tools write, each at the instant its own step was sent; an
/// <c>invoke</c> step, which has no pointer position, writes nothing.
/// </summary>
public sealed class BatchActionLogTests : AutomationHostFixture
{
    private readonly string _recordings = Path.Combine(Path.GetTempPath(), $"yaat-batch-action-log-test-{Guid.NewGuid():N}");
    private readonly FakeRecordingBackend _backend = new();
    private readonly ManualClock _clock = new(RecordingFakes.Start);
    private readonly RecordingSession _session;

    public BatchActionLogTests() => _session = RecordingFakes.NewSession(_backend, _clock);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Mp4 => Path.Combine(_recordings, "clip.mp4");

    private static PointerSite Site(double x, double y) => new(7, "YAAT", x, y, 1.5);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_recordings))
        {
            Directory.Delete(_recordings, recursive: true);
        }
    }

    [Fact]
    public async Task ClickAndDragSteps_WhileRecording_AreLoggedAsTheStandaloneToolsLogThem()
    {
        await StartRecordingAsync();
        RecordingFakes.WriteRecorderLog(Mp4, RecorderLog.FirstFrameLine(_clock.Now.UtcDateTime));
        string steps = """
            [{"method":"click","params":{"selector":"#Box","button":"right","clickCount":2}},
             {"method":"drag","params":{"windowSelector":"#Main","fromX":30,"fromY":20,"toX":90,"toY":25,"button":"left","steps":4,"holdMs":200}}]
            """;

        string result = await RunBatchAsync(
            steps,
            method =>
                method switch
                {
                    ProtocolMethods.Click => new ClickResult(5, "pointer", Site(12.5, 40)),
                    ProtocolMethods.Drag => new DragResult(9, "Border", Site(30, 20), 90, 25, "left", 4, 200, 270),
                    _ => throw new InvalidOperationException($"unexpected method {method}"),
                }
        );

        Assert.True(JsonDocument.Parse(result).RootElement.GetProperty("passed").GetBoolean(), result);
        string[] lines = await ReadActionLinesAsync();
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("{\"kind\":\"header\",\"startedUtc\":\"2026-10-03T12:00:00Z\",\"fps\":30,", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(",\"renderScaling\":1.5}", lines[0], StringComparison.Ordinal);
        Assert.Equal(
            "{\"kind\":\"click\",\"wallUtc\":\"2026-10-03T12:00:00Z\",\"clipSeconds\":0,\"window\":\"YAAT\",\"x\":12.5,\"y\":40,"
                + "\"toX\":null,\"toY\":null,\"button\":\"right\",\"clickCount\":2,\"holdMs\":null,\"durationMs\":null,\"steps\":null}",
            lines[1]
        );
        // The click's answer moved the clock a second on, so the drag carries its own later send time.
        Assert.Equal(
            "{\"kind\":\"drag\",\"wallUtc\":\"2026-10-03T12:00:01Z\",\"clipSeconds\":1,\"window\":\"YAAT\",\"x\":30,\"y\":20,"
                + "\"toX\":90,\"toY\":25,\"button\":\"left\",\"clickCount\":null,\"holdMs\":200,\"durationMs\":270,\"steps\":4}",
            lines[2]
        );
    }

    [Fact]
    public async Task InvokeStep_WhileRecording_WritesNoLine()
    {
        await StartRecordingAsync();

        string result = await RunBatchAsync("""[{"method":"invoke","params":{"selector":"#Box"}}]""", method => new { ok = method });

        Assert.True(JsonDocument.Parse(result).RootElement.GetProperty("passed").GetBoolean(), result);
        Assert.False(File.Exists(RecordingPaths.For(Mp4).Actions), "an invoke step has no pointer position to log");
    }

    private Task<RecordingStarted> StartRecordingAsync() =>
        _session.StartAsync(
            new RecordingRequest(new RecordingTarget(Environment.ProcessId, 0x1234, true), @"C:\tools\ffmpeg\ffmpeg.exe", Mp4, 30, false),
            Ct
        );

    private async Task<string[]> ReadActionLinesAsync()
    {
        string text = await File.ReadAllTextAsync(RecordingPaths.For(Mp4).Actions, Ct);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Runs <paramref name="stepsJson"/> through <see cref="BatchTools.RunAsync"/> against a scripted host answering each
    /// method with <paramref name="answer"/>'s result; every answer moves the session's clock one second on.
    /// </summary>
    private async Task<string> RunBatchAsync(string stepsJson, Func<string, object> answer)
    {
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        await StubPipeHost.AdvertiseAsync(DiscoveryDirectory, pipeName, Ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task host = StubPipeHost.ServeAsync(
            pipeName,
            (method, _) =>
            {
                object answered = answer(method);
                _clock.Now += TimeSpan.FromSeconds(1);
                return answered;
            },
            TimeSpan.Zero,
            timeout.Token
        );
        var directory = new PipeDirectory(
            DiscoveryDirectory,
            Process.GetCurrentProcess().ProcessName,
            NullLogger<PipeDirectory>.Instance,
            NullLogger<PipeClient>.Instance
        );
        try
        {
            using var document = JsonDocument.Parse(stepsJson);
            return await new BatchTools(directory, _session).RunAsync(document.RootElement, Environment.ProcessId, TimeSpan.FromSeconds(30), Ct);
        }
        finally
        {
            await directory.ForgetAsync(Environment.ProcessId);
            await timeout.CancelAsync();
            try
            {
                await host;
            }
            catch (OperationCanceledException)
            {
                // The stub was still waiting for a request when this test's own timeout cancelled it; there is nothing left to wait for.
            }
        }
    }
}
