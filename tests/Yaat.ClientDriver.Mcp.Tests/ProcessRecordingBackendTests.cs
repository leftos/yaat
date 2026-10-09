extern alias mcp;

using System.Diagnostics;
using mcp::Yaat.ClientDriver.Mcp.Recording;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The real pipeline under cmd, with Windows' own tools standing in for the recorder and ffmpeg: findstr or ping as the first
/// stage, sort as the last, in a folder whose name holds every character cmd would otherwise expand or split on.
/// </summary>
public sealed class ProcessRecordingBackendTests : IDisposable
{
    /// <summary>How long cmd is given to spawn the recorder, its second process, before a test fails.</summary>
    private static readonly TimeSpan RecorderSpawnWait = TimeSpan.FromSeconds(20);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"yaat-rec %USERNAME% !x! & ^ {Guid.NewGuid():N}");
    private readonly ListLogger<ProcessRecordingBackend> _logger = new();
    private readonly ProcessRecordingBackend _backend;

    public ProcessRecordingBackendTests()
    {
        Directory.CreateDirectory(_folder);
        _backend = new ProcessRecordingBackend(_logger);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // findstr reads the deadline file by its path and sort writes the output by its: both arrive literally, %USERNAME% unexpanded.
    [Fact]
    public async Task Start_OddFolderName_PathsReachTheStagesLiterally()
    {
        string deadline = Path.Combine(_folder, "clip.mp4.deadline");
        string output = Path.Combine(_folder, "clip.mp4");
        await File.WriteAllTextAsync(deadline, "2026-10-03T12:00:00.0000000Z\n", Ct);
        var command = new PipelineCommand(
            new PipelineStage(System32("findstr.exe"), ["/r", ".*", deadline], Path.Combine(_folder, "clip-recorder.log"), deadline),
            new PipelineStage(System32("sort.exe"), ["/o", output], Path.Combine(_folder, "clip-ffmpeg.log"), output)
        );

        using IRecordingProcess pipeline = _backend.Start(FfmpegPipeline.CommandLine(command));
        try
        {
            Assert.True(await pipeline.WaitForExitAsync(TimeSpan.FromSeconds(20), Ct));
            Assert.Equal(0, pipeline.ExitCode);
            Assert.Equal("2026-10-03T12:00:00.0000000Z", (await File.ReadAllTextAsync(output, Ct)).Trim());
        }
        finally
        {
            await EndPipelineAsync(pipeline);
        }
    }

    // The recorder is found among cmd's children by its image: a running one is waited for and killed alone, and the last stage,
    // its input ended, finishes on its own.
    [Fact]
    public async Task KillRecorder_EndsOnlyTheRecorder_AndTheLastStageFinishes()
    {
        string output = Path.Combine(_folder, "ping.txt");
        using IRecordingProcess pipeline = _backend.Start(FfmpegPipeline.CommandLine(PingIntoSort(output)));
        try
        {
            await WaitForRecorderAsync(pipeline);

            Assert.False(await pipeline.WaitForRecorderExitAsync(TimeSpan.FromMilliseconds(500), Ct));
            pipeline.KillRecorder();

            Assert.True(await pipeline.WaitForExitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.Equal(0, pipeline.ExitCode);
            // The recorder may be killed before it writes a single line, so the output's content is not asserted here: this
            // test is the kill's reach, and the pipeline's data flow is Start_OddFolderName_PathsReachTheStagesLiterally's.
            Assert.True(File.Exists(output));
            Assert.DoesNotContain(_logger.Entries, entry => entry.Level == LogLevel.Warning);
        }
        finally
        {
            await EndPipelineAsync(pipeline);
        }
    }

    // A running cmd with no child of the recorder's image is warned of, and taken as a recorder that has ended.
    [Fact]
    public async Task WaitForRecorderExit_NoRecorderChild_WarnsAndTakesItAsEnded()
    {
        PipelineCommandLine line = FfmpegPipeline.CommandLine(PingIntoSort(Path.Combine(_folder, "ping.txt"))) with
        {
            RecorderExecutable = @"C:\nowhere\Yaat.NoSuchRecorder.exe",
        };
        using IRecordingProcess pipeline = _backend.Start(line);
        try
        {
            Assert.True(await pipeline.WaitForRecorderExitAsync(TimeSpan.FromSeconds(1), Ct));

            Assert.Contains(
                _logger.Entries,
                entry => (entry.Level == LogLevel.Warning) && entry.Message.Contains("no Yaat.NoSuchRecorder child", StringComparison.Ordinal)
            );
        }
        finally
        {
            await EndPipelineAsync(pipeline);
        }
    }

    private static string System32(string executable) => Path.Combine(Environment.SystemDirectory, executable);

    /// <summary>
    /// ping as a recorder that runs until stopped, into sort; ping's host is its path argument, as the deadline file is the
    /// recorder's, so it goes through a variable like any path.
    /// </summary>
    private PipelineCommand PingIntoSort(string output) =>
        new(
            new PipelineStage(System32("PING.EXE"), ["-n", "30", "127.0.0.1"], Path.Combine(_folder, "ping.log"), "127.0.0.1"),
            new PipelineStage(System32("sort.exe"), ["/o", output], Path.Combine(_folder, "sort.log"), output)
        );

    /// <summary>
    /// Waits for cmd to have spawned the recorder, so a later wait for its end is not answered by a recorder that is simply
    /// not there yet. A bare delay races the spawn under load: the missing recorder reads as one that has ended, and the
    /// warning that goes with it is what this class's tests also assert against.
    /// </summary>
    private static async Task WaitForRecorderAsync(IRecordingProcess pipeline)
    {
        var waited = Stopwatch.StartNew();
        while (!pipeline.IsRecorderRunning)
        {
            // The exit code is read only once the pipeline has ended: reading it on a running cmd throws, and a message is
            // built whichever way the assert goes.
            if (pipeline.HasExited)
            {
                Assert.Fail($"cmd exited (code {pipeline.ExitCode}) before its recorder was seen");
            }

            Assert.True(waited.Elapsed < RecorderSpawnWait, $"cmd did not spawn its recorder within {RecorderSpawnWait.TotalSeconds:0} s");
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        }
    }

    private static async Task EndPipelineAsync(IRecordingProcess pipeline)
    {
        if (!pipeline.HasExited)
        {
            pipeline.KillTree();
            _ = await pipeline.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }
}
