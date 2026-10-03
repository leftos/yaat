extern alias mcp;

using System.Diagnostics;
using System.Text.Json;
using mcp::Yaat.ClientDriver.Mcp;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using mcp::Yaat.ClientDriver.Mcp.Recording;
using mcp::Yaat.ClientDriver.Mcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// <c>record_start</c>, <c>record_mark</c> and <c>record_stop</c> over a fake recording backend: what start refuses before
/// anything runs, the marks file each mark rewrites, and what stop reads back from the recorder's log. A pid with no
/// discovery file has no pipe, as CRC has none; the stub pipe host stands in for a YAAT client where a test needs one.
/// </summary>
public sealed class RecordToolsTests : AutomationHostFixture
{
    /// <summary>A pid no discovery file names, so it routes as a process without an automation pipe.</summary>
    private const int NoPipePid = 4242;

    private readonly FakeRecordingBackend _backend = new();
    private readonly ManualClock _clock = new(RecordingFakes.Start);
    private readonly RecordingSession _session;
    private readonly PipeDirectory _pipes;
    private readonly RecordTools _tools;

    public RecordToolsTests()
    {
        _session = RecordingFakes.NewSession(_backend, _clock);
        _pipes = new PipeDirectory(
            DiscoveryDirectory,
            Process.GetCurrentProcess().ProcessName,
            NullLogger<PipeDirectory>.Instance,
            NullLogger<PipeClient>.Instance
        );
        _tools = new RecordTools(
            _session,
            _backend,
            new ElementRegistry(NullLogger<ElementRegistry>.Instance),
            _pipes,
            NullLogger<RecordTools>.Instance
        );
    }

    private static int Pid => Environment.ProcessId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string OutPath => Path.Combine(DiscoveryDirectory, "recordings", "clip.mp4");

    private RecordingPaths Paths => RecordingPaths.For(OutPath);

    [Fact]
    public async Task RecordStart_FfmpegMissing_FailsWithTheWingetHint()
    {
        _backend.Ffmpeg = null;

        McpException thrown = await Assert.ThrowsAsync<McpException>(StartNoPipeAsync);

        Assert.Equal(
            "ffmpeg is not on PATH. Fix: winget install Gyan.FFmpeg, then restart Claude Code so the client-driver MCP server starts "
                + "with the new PATH.",
            thrown.Message
        );
        Assert.Empty(_backend.CommandLines);
        Assert.False(_session.IsRecording);
    }

    [Fact]
    public async Task RecordStart_MinimizedWindow_Refuses()
    {
        _backend.Geometry = _backend.Geometry with { IsMinimized = true };

        McpException thrown = await Assert.ThrowsAsync<McpException>(StartNoPipeAsync);

        Assert.Contains("0x1234 is minimized", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(_backend.CommandLines);
        Assert.False(_session.IsRecording);
    }

    [Fact]
    public async Task RecordStart_WhileRecording_RefusesNamingTheRunningClip()
    {
        await StartNoPipeAsync();
        string second = Path.Combine(DiscoveryDirectory, "recordings", "second.mp4");

        McpException thrown = await Assert.ThrowsAsync<McpException>(() => _tools.RecordStartAsync(Ct, NoPipePid, "", second, 30, null));

        Assert.Contains(OutPath, thrown.Message, StringComparison.Ordinal);
        Assert.Single(_backend.CommandLines);
    }

    [Fact]
    public async Task RecordStart_ExistingOut_Refuses()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OutPath)!);
        await File.WriteAllBytesAsync(OutPath, [1, 2, 3], Ct);

        McpException thrown = await Assert.ThrowsAsync<McpException>(StartNoPipeAsync);

        Assert.Contains("already exists", thrown.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(OutPath, Ct));
        Assert.Empty(_backend.CommandLines);
    }

    [Fact]
    public async Task RecordStart_BothPidAndElementId_IsInvalidParam()
    {
        McpException both = await Assert.ThrowsAsync<McpException>(() => _tools.RecordStartAsync(Ct, NoPipePid, "e1", OutPath, 30, null));
        McpException neither = await Assert.ThrowsAsync<McpException>(() => _tools.RecordStartAsync(Ct, 0, "", OutPath, 30, null));

        Assert.StartsWith("INVALID_PARAM:", both.Message, StringComparison.Ordinal);
        Assert.StartsWith("INVALID_PARAM:", neither.Message, StringComparison.Ordinal);
        Assert.Empty(_backend.CommandLines);
    }

    // A YAAT client answers get_sim_time over its pipe, and has audio on by default: the recorder is asked for its process audio.
    [Fact]
    public async Task RecordMark_WritesTheMarkWithSimTimeFromThePipe()
    {
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        await StubPipeHost.AdvertiseAsync(DiscoveryDirectory, pipeName, Ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task host = StubPipeHost.ServeAsync(pipeName, AnswerAsClient, TimeSpan.Zero, timeout.Token);
        try
        {
            string started = await _tools.RecordStartAsync(Ct, Pid, "", OutPath, 30, null);
            RecordingFakes.WriteRecorderLog(OutPath, RecorderLog.FirstFrameLine(_clock.Now.UtcDateTime));
            _clock.Now += TimeSpan.FromSeconds(2.5);

            string marked = await _tools.RecordMarkAsync("taxi", Ct);

            Assert.Contains("audio on", started, StringComparison.Ordinal);
            Assert.Contains($"--audio-pid {Pid} --audio-pipe yaat-rec-", _backend.CommandLines.Single(), StringComparison.Ordinal);
            Assert.Equal($"mark 1 'taxi': clip 2.50 s, sim 123.5 s; marks: {Paths.Marks}", marked);
            JsonElement marks = ReadMarks();
            Assert.Equal(Pid, marks.GetProperty("pid").GetInt32());
            Assert.Equal(0x1234, marks.GetProperty("hwnd").GetInt64());
            JsonElement mark = marks.GetProperty("marks")[0];
            Assert.Equal("taxi", mark.GetProperty("label").GetString());
            Assert.Equal(123.5, mark.GetProperty("simSeconds").GetDouble());
            Assert.Equal(2.5, mark.GetProperty("clipSeconds").GetDouble());
        }
        finally
        {
            await _pipes.ForgetAsync(Pid);
            await timeout.CancelAsync();
            try
            {
                await host;
            }
            catch (OperationCanceledException)
            {
                // The stub's serve ends with this test's own timeout; there is nothing left to wait for.
            }
        }
    }

    [Fact]
    public async Task RecordMark_NoPipe_SimSecondsIsNull()
    {
        string started = await StartNoPipeAsync();
        RecordingFakes.WriteRecorderLog(OutPath, RecorderLog.FirstFrameLine(_clock.Now.UtcDateTime));

        string marked = await _tools.RecordMarkAsync("pushback", Ct);

        Assert.Contains("audio off", started, StringComparison.Ordinal);
        Assert.DoesNotContain("--audio-pid", _backend.CommandLines.Single(), StringComparison.Ordinal);
        Assert.Contains("sim unknown", marked, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, ReadMarks().GetProperty("marks")[0].GetProperty("simSeconds").ValueKind);
    }

    [Fact]
    public async Task RecordMark_BeforeFirstFrame_ClipSecondsIsNull()
    {
        await StartNoPipeAsync();

        string marked = await _tools.RecordMarkAsync("early", Ct);

        Assert.Contains("clip unknown", marked, StringComparison.Ordinal);
        JsonElement marks = ReadMarks();
        Assert.Equal(JsonValueKind.Null, marks.GetProperty("startedUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, marks.GetProperty("marks")[0].GetProperty("clipSeconds").ValueKind);
    }

    // The exit code and the last five recorder lines say why; the session ends, so a stop then finds nothing recording.
    [Fact]
    public async Task RecordMark_HelperDied_ReportsTheExitAndLogTail()
    {
        await StartNoPipeAsync();
        RecordingFakes.WriteRecorderLog(
            OutPath,
            "line 1",
            "line 2",
            "line 3",
            "line 4",
            "line 5",
            "line 6",
            "Yaat.WindowRecorder: the window closed"
        );
        _backend.Process.HasExited = true;
        _backend.Process.ExitCode = 3;

        McpException died = await Assert.ThrowsAsync<McpException>(() => _tools.RecordMarkAsync("late", Ct));

        Assert.Contains("exited on its own with code 3", died.Message, StringComparison.Ordinal);
        Assert.Contains("line 3 | line 4 | line 5 | line 6 | Yaat.WindowRecorder: the window closed", died.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("line 2", died.Message, StringComparison.Ordinal);
        Assert.False(_session.IsRecording);
        McpException stop = await Assert.ThrowsAsync<McpException>(() => _tools.RecordStopAsync(Ct));
        Assert.Equal(RecordingSession.NothingRecordingMessage, stop.Message);
    }

    // Stop moves the deadline to now; the recorder's end line gives the length and frames, and 30 fps x 10 s - 290 = 10 dropped.
    [Fact]
    public async Task RecordStop_ReturnsPathDurationFramesDroppedAndMarks()
    {
        await StartNoPipeAsync();
        RecordingFakes.WriteRecorderLog(OutPath, RecorderLog.FirstFrameLine(_clock.Now.UtcDateTime));
        _clock.Now += TimeSpan.FromSeconds(1);
        await _tools.RecordMarkAsync("a", Ct);
        _clock.Now += TimeSpan.FromSeconds(9);
        _backend.Process.OnStop = () =>
        {
            RecordingFakes.WriteRecorderLog(OutPath, "Yaat.WindowRecorder: 290 frames in 10.0 s (29.0 fps of 30)");
            File.WriteAllBytes(OutPath, [0]);
        };

        string stopped = await _tools.RecordStopAsync(Ct);

        Assert.Equal($"stopped {OutPath}: 10.0 s, 290 frames, 10 dropped; marks: {Paths.Marks}", stopped);
        Assert.Equal(RecordingFakes.Start.UtcDateTime.AddHours(1), _backend.Deadlines[0].EndUtc);
        Assert.Equal((Paths.Deadline, _clock.Now.UtcDateTime), _backend.Deadlines[^1]);
        Assert.False(_session.IsRecording);
        Assert.Single(ReadMarks().GetProperty("marks").EnumerateArray());
    }

    [Fact]
    public async Task RecordStop_NothingRecording_Refuses()
    {
        McpException thrown = await Assert.ThrowsAsync<McpException>(() => _tools.RecordStopAsync(Ct));

        Assert.Equal(RecordingSession.NothingRecordingMessage, thrown.Message);
    }

    [Fact]
    public async Task Start_OutNotMp4_Refused()
    {
        string avi = Path.Combine(DiscoveryDirectory, "recordings", "clip.avi");

        McpException thrown = await Assert.ThrowsAsync<McpException>(() => _tools.RecordStartAsync(Ct, NoPipePid, "", avi, 30, null));

        Assert.StartsWith("INVALID_PARAM: out must name an .mp4 file", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(_backend.CommandLines);
        await _tools.RecordStartAsync(Ct, NoPipePid, "", Path.Combine(DiscoveryDirectory, "recordings", "CLIP.MP4"), 30, null);
        Assert.Single(_backend.CommandLines);
    }

    [Fact]
    public async Task Start_OutIsAFolder_Refused()
    {
        string folder = Path.Combine(DiscoveryDirectory, "recordings", "folder.mp4");
        Directory.CreateDirectory(folder);

        McpException thrown = await Assert.ThrowsAsync<McpException>(() => _tools.RecordStartAsync(Ct, NoPipePid, "", folder, 30, null));

        Assert.StartsWith("INVALID_PARAM: out names an existing folder", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(_backend.CommandLines);
    }

    // The pipeline is checked a moment after it starts: one that already died is reported, and nothing is left recording.
    [Fact]
    public async Task Start_PipelineDiesAtOnce_ReportsItAndIsNotRecording()
    {
        _backend.Process.HasExited = true;
        _backend.Process.ExitCode = 1;

        McpException thrown = await Assert.ThrowsAsync<McpException>(StartNoPipeAsync);

        Assert.Contains("exited on its own with code 1", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("(the recorder log is empty)", thrown.Message, StringComparison.Ordinal);
        Assert.Single(_backend.CommandLines);
        Assert.False(_session.IsRecording);
        Assert.False(File.Exists(Paths.Deadline));
    }

    // The marks file is written before anything starts, so a folder it cannot be written in leaves no pipeline behind.
    [Fact]
    public async Task Start_MarksFileUnwritable_LeavesNoPipelineRunning()
    {
        Directory.CreateDirectory(Paths.Marks + ".tmp");

        McpException thrown = await Assert.ThrowsAsync<McpException>(StartNoPipeAsync);

        Assert.Contains(Paths.Marks, thrown.Message, StringComparison.Ordinal);
        Assert.Empty(_backend.CommandLines);
        Assert.False(_session.IsRecording);
    }

    // The one-hour cap or a closed window ends a pipeline with nobody stopping it; the next start reaps it instead of refusing.
    [Fact]
    public async Task Start_AfterPipelineEndedOnItsOwn_IsAccepted()
    {
        await StartNoPipeAsync();
        _backend.Process.HasExited = true;
        string second = Path.Combine(DiscoveryDirectory, "recordings", "second.mp4");

        string started = await _tools.RecordStartAsync(Ct, NoPipePid, "", second, 30, null);

        Assert.StartsWith($"recording {second} ", started, StringComparison.Ordinal);
        Assert.Equal(2, _backend.CommandLines.Count);
        Assert.Equal(second, _session.RecordingMp4);
        Assert.False(File.Exists(Paths.Deadline));
    }

    [Fact]
    public async Task Stop_PipelineEndedCleanlyOnItsOwn_IsNotAFailure()
    {
        await StartNoPipeAsync();
        RecordingFakes.WriteRecorderLog(OutPath, "Yaat.WindowRecorder: 108000 frames in 3600.0 s (30.0 fps of 30)");
        await File.WriteAllBytesAsync(OutPath, [0], Ct);
        _backend.Process.HasExited = true;

        string stopped = await _tools.RecordStopAsync(Ct);

        Assert.Equal(
            $"stopped {OutPath}: 3600.0 s, 108000 frames, 0 dropped; marks: {Paths.Marks}{Environment.NewLine}"
                + "the recording ended on its own (the one-hour cap or a window change) before the stop",
            stopped
        );
        Assert.False(_session.IsRecording);
    }

    // The recorder gets 15 s to stop; past that only it is killed, which ends ffmpeg's input, and ffmpeg still finishes the MP4.
    [Fact]
    public async Task Stop_RecorderMissesItsBudget_KillsOnlyTheRecorderThenWaitsForFfmpeg()
    {
        await StartNoPipeAsync();
        FakeRecordingProcess process = _backend.Process;
        process.RecorderMissesStop = true;
        process.OnStop = () => File.WriteAllBytes(OutPath, [0]);

        RecordingStopResult stopped = await _session.StopAsync(Ct);

        Assert.Equal([TimeSpan.FromSeconds(15)], process.RecorderWaits);
        Assert.True(process.RecorderKilled);
        Assert.False(process.Killed);
        Assert.Equal([TimeSpan.FromSeconds(120)], process.PipelineWaits);
        Assert.NotNull(stopped.Problem);
        Assert.Equal("the recorder did not stop within 15 s of the stop and was killed, so the clip runs up to 15 s past the stop", stopped.Problem);
        Assert.DoesNotContain("unplayable", stopped.Problem, StringComparison.Ordinal);
        Assert.True(stopped.Mp4Exists);
    }

    [Fact]
    public async Task Stop_FfmpegMissesItsBudget_KillsTheTreeAndWarnsUnplayable()
    {
        await StartNoPipeAsync();
        FakeRecordingProcess process = _backend.Process;

        RecordingStopResult stopped = await _session.StopAsync(Ct);

        Assert.Equal(TimeSpan.FromSeconds(120), process.PipelineWaits[0]);
        Assert.False(process.RecorderKilled);
        Assert.True(process.Killed);
        Assert.NotNull(stopped.Problem);
        Assert.Contains("within 120 s", stopped.Problem, StringComparison.Ordinal);
        Assert.Contains($"{OutPath} may be unplayable", stopped.Problem, StringComparison.Ordinal);
        Assert.False(_session.IsRecording);
    }

    // A caller that gives up mid-stop does not cut the encode short: the stop runs on, and the recording is forgotten (and its
    // deadline file deleted) only once the pipeline has exited.
    [Fact]
    public async Task Stop_CallerCancels_PipelineStillEndsAndIsNotForgotten()
    {
        await StartNoPipeAsync();
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _backend.Process.PendingExit = exit;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<RecordingStopResult> stop = _session.StopAsync(caller.Token);
        await caller.CancelAsync();
        await Task.WhenAny(stop, Task.Delay(TimeSpan.FromMilliseconds(200), Ct));

        Assert.False(stop.IsCompleted);
        Assert.True(_session.IsRecording);
        Assert.True(File.Exists(Paths.Deadline));
        exit.SetResult();
        await stop;
        Assert.False(_session.IsRecording);
        Assert.False(File.Exists(Paths.Deadline));
    }

    // The server stopping ends the recording the way record_stop does, and waits for the MP4 rather than abandoning it.
    [Fact]
    public async Task DisposeAsync_WhileRecording_WritesDeadlineAndWaitsForExit()
    {
        await StartNoPipeAsync();
        FakeRecordingProcess process = _backend.Process;
        process.OnStop = () => File.WriteAllBytes(OutPath, [0]);
        _clock.Now += TimeSpan.FromSeconds(5);

        await _session.DisposeAsync();

        Assert.Equal((Paths.Deadline, _clock.Now.UtcDateTime), _backend.Deadlines[^1]);
        Assert.Equal([TimeSpan.FromSeconds(15)], process.RecorderWaits);
        Assert.Equal([TimeSpan.FromSeconds(120)], process.PipelineWaits);
        Assert.False(process.Killed);
        Assert.False(_session.IsRecording);
    }

    // A client still opening has no main window and answers get_sim_time with UNSUPPORTED_OPERATION: the mark has no sim time.
    [Fact]
    public async Task Mark_ClientHasNoMainWindow_SimIsNull()
    {
        var noWindow = new AutomationError("the client has no main window yet", AutomationErrorCodes.UnsupportedOperation, null, null);
        await WithClientAsync(
            (id, method, _) =>
                (method == ProtocolMethods.GetSimTime) ? AutomationResponse.Failure(id, noWindow) : ClientAnswer(id, AnswerAsClient(method, null)),
            async () =>
            {
                await _tools.RecordStartAsync(Ct, Pid, "", OutPath, 30, null);

                string marked = await _tools.RecordMarkAsync("early", Ct);

                Assert.Contains("sim unknown", marked, StringComparison.Ordinal);
                Assert.Equal(JsonValueKind.Null, ReadMarks().GetProperty("marks")[0].GetProperty("simSeconds").ValueKind);
            }
        );
    }

    // A wait_until in flight holds the client's pipe, so get_sim_time queues behind it: the 2 s limit covers that queue, the mark
    // is written without a sim time, and a record_stop called meanwhile is not held behind the mark.
    [Fact]
    public async Task Mark_SimClockBlocked_ReturnsWithinTheWaitAndNullSim()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await WithClientAsync(
            (id, method, _) =>
            {
                if (method == ProtocolMethods.WaitUntil)
                {
                    arrived.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(20), Ct);
                    return ClientAnswer(id, new { });
                }

                return ClientAnswer(id, AnswerAsClient(method, null));
            },
            async () =>
            {
                try
                {
                    await _tools.RecordStartAsync(Ct, Pid, "", OutPath, 30, null);
                    _backend.Process.OnStop = () => File.WriteAllBytes(OutPath, [0]);
                    _ = PipeCalls.SendForPidAsync<JsonElement>(_pipes, Pid, ProtocolMethods.WaitUntil, null, TimeSpan.FromSeconds(20), Ct);
                    await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                    var elapsed = Stopwatch.StartNew();

                    Task<string> mark = _tools.RecordMarkAsync("blocked", Ct);
                    Task<string> stop = _tools.RecordStopAsync(Ct);

                    Assert.Same(stop, await Task.WhenAny(stop, mark).WaitAsync(TimeSpan.FromSeconds(10), Ct));
                    string marked = await mark.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                    Assert.Contains("sim unknown", marked, StringComparison.Ordinal);
                    Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(6));
                    Assert.Equal("blocked", ReadMarks().GetProperty("marks")[0].GetProperty("label").GetString());
                }
                finally
                {
                    release.Set();
                }
            }
        );
    }

    // A deadline that cannot be moved leaves the pipeline running, so the recording stays held, its one-hour deadline intact.
    [Fact]
    public async Task Stop_DeadlineWriteFails_KeepsTheRecordingHeld()
    {
        await StartNoPipeAsync();
        _backend.DeadlineFailure = new IOException("the disk is full");

        await Assert.ThrowsAsync<IOException>(() => _session.StopAsync(Ct));

        Assert.True(_session.IsRecording);
        Assert.True(File.Exists(Paths.Deadline));
        Assert.Equal(RecordingFakes.Start.UtcDateTime.AddHours(1), _backend.Deadlines.Single().EndUtc);
    }

    [Fact]
    public async Task Start_AfterDispose_Refused()
    {
        await _session.DisposeAsync();

        McpException thrown = await Assert.ThrowsAsync<McpException>(StartNoPipeAsync);

        Assert.Equal("The client-driver server is stopping; record_start is refused.", thrown.Message);
        Assert.Empty(_backend.CommandLines);
    }

    // A pipeline that died on its own with an error is a warning when the next start closes it, and its marks file gets the
    // first frame's instant, as a stop would write it.
    [Fact]
    public async Task Start_AfterPipelineDiedWithAnError_WarnsAndFinishesItsMarks()
    {
        var logger = new ListLogger<RecordingSession>();
        RecordingSession session = RecordingFakes.NewSession(_backend, _clock, logger);
        await session.StartAsync(Request(OutPath), Ct);
        RecordingFakes.WriteRecorderLog(OutPath, RecorderLog.FirstFrameLine(_clock.Now.UtcDateTime));
        _backend.Process.HasExited = true;
        _backend.Process.ExitCode = 3;

        await session.StartAsync(Request(SecondPath), Ct);

        Assert.Equal(_clock.Now.UtcDateTime, ReadMarks().GetProperty("startedUtc").GetDateTime());
        Assert.Contains(
            logger.Entries,
            entry => (entry.Level == LogLevel.Warning) && entry.Message.Contains("ended on its own with code 3", StringComparison.Ordinal)
        );
    }

    // A start cancelled just after its pipeline started kills it; a kill that has not taken leaves the pipeline held, its handle
    // kept, and the next start reaps it once it has exited.
    [Fact]
    public async Task Start_CancelledWhileTheKillDoesNotTake_StaysHeldUntilReaped()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        FakeRecordingProcess first = _backend.Process;
        first.IgnoresKill = true;
        _backend.OnStart = caller.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _session.StartAsync(Request(OutPath), caller.Token));

        Assert.True(first.Killed);
        Assert.Equal(OutPath, _session.RecordingMp4);
        Assert.True(File.Exists(Paths.Deadline));
        _backend.OnStart = null;
        first.HasExited = true;
        await _session.StartAsync(Request(SecondPath), Ct);
        Assert.Equal(SecondPath, _session.RecordingMp4);
        Assert.False(File.Exists(Paths.Deadline));
    }

    // An answer that is not a sim time (neither result nor error, or a result of another shape) gives a mark without one.
    [Fact]
    public async Task Mark_ClientAnswersNeitherResultNorError_SimIsNull()
    {
        string marked = await MarkWithSimTimeAnswerAsync(id => new AutomationResponse { Id = id });

        Assert.Contains("sim unknown", marked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mark_ClientAnswersAResultThatIsNotASimTime_SimIsNull()
    {
        string marked = await MarkWithSimTimeAnswerAsync(id => ClientAnswer(id, "not a sim time"));

        Assert.Contains("sim unknown", marked, StringComparison.Ordinal);
    }

    // At shutdown a deadline that cannot be written kills the recorder, and ffmpeg still gets its whole budget to finish the MP4.
    [Fact]
    public async Task DisposeAsync_DeadlineUnwritable_KillsRecorderThenWaitsForFfmpeg()
    {
        await StartNoPipeAsync();
        FakeRecordingProcess process = _backend.Process;
        process.OnStop = () => File.WriteAllBytes(OutPath, [0]);
        _backend.DeadlineFailure = new IOException("the disk is full");

        await _session.DisposeAsync();

        Assert.True(process.RecorderKilled);
        Assert.Equal([TimeSpan.FromSeconds(120)], process.PipelineWaits);
        Assert.False(process.Killed);
        Assert.False(_session.IsRecording);
    }

    private string SecondPath => Path.Combine(DiscoveryDirectory, "recordings", "second.mp4");

    private static RecordingRequest Request(string mp4) =>
        new(new RecordingTarget(NoPipePid, 0x1234, false), @"C:\tools\ffmpeg\ffmpeg.exe", mp4, 30, false);

    /// <summary>Starts a recording of a stub YAAT client that answers get_sim_time with <paramref name="answer"/>, and marks it once.</summary>
    private async Task<string> MarkWithSimTimeAnswerAsync(Func<string, AutomationResponse> answer)
    {
        string marked = string.Empty;
        await WithClientAsync(
            (id, method, _) => (method == ProtocolMethods.GetSimTime) ? answer(id) : ClientAnswer(id, AnswerAsClient(method, null)),
            async () =>
            {
                await _tools.RecordStartAsync(Ct, Pid, "", OutPath, 30, null);
                marked = await _tools.RecordMarkAsync("odd answer", Ct);
            }
        );
        return marked;
    }

    private Task<string> StartNoPipeAsync() => _tools.RecordStartAsync(Ct, NoPipePid, "", OutPath, 30, null);

    /// <summary>
    /// Serves a stub YAAT client for this process's pid, answering with <paramref name="respond"/>, while <paramref name="body"/>
    /// runs.
    /// </summary>
    private async Task WithClientAsync(Func<string, string, JsonElement?, AutomationResponse> respond, Func<Task> body)
    {
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        await StubPipeHost.AdvertiseAsync(DiscoveryDirectory, pipeName, Ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task host = StubPipeHost.ServeResponsesAsync(pipeName, respond, TimeSpan.Zero, timeout.Token);
        try
        {
            await body();
        }
        finally
        {
            await _pipes.ForgetAsync(Pid);
            await timeout.CancelAsync();
            try
            {
                await host;
            }
            catch (OperationCanceledException)
            {
                // The stub's serve ends with this test's own timeout; there is nothing left to wait for.
            }
        }
    }

    private static AutomationResponse ClientAnswer(string id, object result) => AutomationResponse.Success(id, ProtocolSerializer.ToElement(result));

    private JsonElement ReadMarks()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Paths.Marks));
        return document.RootElement.Clone();
    }

    private static object AnswerAsClient(string method, JsonElement? parameters) =>
        method switch
        {
            ProtocolMethods.ListWindows => new List<WindowInfo>
            {
                new(1, "YAAT - test", "MainWindow", false, null, new BoundsInfo(), true, true, 0x1234),
            },
            ProtocolMethods.GetSimTime => new GetSimTimeResult(123.5, false, 1),
            _ => throw new InvalidOperationException($"The stub client has no answer for {method}"),
        };
}
