extern alias mcp;

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using mcp::Yaat.ClientDriver.Mcp;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using mcp::Yaat.ClientDriver.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// <c>launch_yaat</c> with a scripted <see cref="IProcessStarter"/>: the launch always points YAAT_APPDATA_DIR and
/// YAAT_AUTOMATION at the started process, waits for its automation pipe to answer <c>list_windows</c> rather than for a
/// top-level window, reports the pipe's own rows, and on failure reports the exit code, the deadline or the cancellation
/// while keeping the app-data directory. The pipe the wait polls is the one a real <see cref="AutomationHost"/> answers in
/// this process, or a stub that never answers or answers with a coded error. Every test that starts a child stops it through a handle of its own.
/// </summary>
public sealed class LaunchYaatTests : AutomationHostFixture
{
    private static int Pid => Environment.ProcessId;

    [Fact]
    public async Task LaunchYaat_SetsAutomationAndAppDataVariables()
    {
        string appDataDir = NewDirectory("appdata");
        string exePath = NewDummyClientExe();
        var starter = new ScriptedStarter(() => throw new InvalidOperationException("this test never lets the launch start"));
        ProcessTools tools = NewTools(NewPipeDirectory(), starter);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => tools.LaunchYaatAsync(CancellationToken.None, appDataDir, exePath, 1));

            ProcessStartInfo startInfo = starter.LastStartInfo!;
            Assert.Equal(exePath, startInfo.FileName);
            Assert.Equal("1", startInfo.Environment["YAAT_AUTOMATION"]);
            Assert.Equal(Path.GetFullPath(appDataDir), startInfo.Environment["YAAT_APPDATA_DIR"]);
        }
        finally
        {
            Delete(appDataDir);
            Delete(Path.GetDirectoryName(exePath)!);
        }
    }

    [Fact]
    public async Task LaunchYaat_WaitSecondsBelowOne_IsRejected()
    {
        string appDataDir = NewDirectory("appdata");
        string exePath = NewDummyClientExe();
        var starter = new ScriptedStarter(() => throw new InvalidOperationException("a rejected wait must not start anything"));
        ProcessTools tools = NewTools(NewPipeDirectory(), starter);
        try
        {
            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                tools.LaunchYaatAsync(CancellationToken.None, appDataDir, exePath, 0)
            );

            Assert.Equal("waitSeconds must be at least 1 second, not 0", failure.Message);
            Assert.Null(starter.LastStartInfo);
        }
        finally
        {
            Delete(appDataDir);
            Delete(Path.GetDirectoryName(exePath)!);
        }
    }

    [AvaloniaFact]
    public async Task LaunchYaat_WaitsForPipeWindows_ReturnsPipeRows()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", new StackPanel { Children = { new Button() } }, null);
        string appDataDir = NewDirectory("appdata");
        string exePath = NewDummyClientExe();
        PipeDirectory directory = NewPipeDirectory();
        var starter = new ScriptedStarter(() => Process.GetCurrentProcess());
        ProcessTools tools = NewTools(directory, starter);
        try
        {
            string result = await tools.LaunchYaatAsync(CancellationToken.None, appDataDir, exePath, 15);

            string[] lines = Lines(result);
            Assert.Equal(
                $"pid={Pid} appDataDir={Path.GetFullPath(appDataDir)} log={Path.Combine(Path.GetFullPath(appDataDir), "yaat-client.log")}",
                lines[0]
            );
            Assert.Equal("windows (1):", lines[1]);
            Assert.EndsWith("| Window | PipeWindow | id= | visible=True | active=True | rect=(0,0 400x300)", lines[2], StringComparison.Ordinal);
            Assert.Equal(Pid, directory.LastTargetPid);
        }
        finally
        {
            // The launch succeeded, so it keeps this pid as launched; the test host is not a client to drive.
            ProcessTools.Forget(Pid);
            await directory.ForgetAsync(Pid);
            Delete(appDataDir);
            Delete(Path.GetDirectoryName(exePath)!);
        }
    }

    [Fact]
    public async Task LaunchYaat_ProcessExitsEarly_ReportsExitCode()
    {
        string appDataDir = NewDirectory("appdata");
        string exePath = NewDummyClientExe();
        Process? child = null;
        var starter = new ScriptedStarter(() =>
        {
            child = Process.Start(Shell("cmd.exe", "/c exit 3"))!;
            return child!;
        });
        ProcessTools tools = NewTools(NewPipeDirectory(), starter);
        try
        {
            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                tools.LaunchYaatAsync(CancellationToken.None, appDataDir, exePath, 10)
            );

            Assert.Contains("exited with code 3", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            StopChild(child);
            Delete(appDataDir);
            Delete(Path.GetDirectoryName(exePath)!);
        }
    }

    [Fact]
    public async Task LaunchYaat_NoPipeBeforeDeadline_KillsAndKeepsAppData()
    {
        string appDataDir = NewDirectory("appdata");
        string exePath = NewDummyClientExe();
        // The launch is given its own handle: it disposes that one when it kills the child, and this test keeps its own.
        using Process child = Process.Start(Shell("ping.exe", "-n 30 127.0.0.1"))!;
        PipeDirectory directory = NewPipeDirectory();
        var starter = new ScriptedStarter(() => Process.GetProcessById(child.Id));
        ProcessTools tools = NewTools(directory, starter);
        try
        {
            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                tools.LaunchYaatAsync(CancellationToken.None, appDataDir, exePath, 1)
            );

            Assert.StartsWith($"no automation pipe answered within 1 s (pid {child.Id});", failure.Message, StringComparison.Ordinal);
            Assert.Contains("is Yaat.Client built from this branch? See tail_yaat_log.", failure.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(appDataDir));
            Assert.True(child.HasExited);
        }
        finally
        {
            StopChild(child);
            Delete(appDataDir);
            Delete(Path.GetDirectoryName(exePath)!);
        }
    }

    [Fact]
    public async Task LaunchYaat_Cancelled_StopsWaitingAndKillsTheChild()
    {
        string appDataDir = NewDirectory("appdata");
        string exePath = NewDummyClientExe();
        using Process child = Process.Start(Shell("ping.exe", "-n 30 127.0.0.1"))!;
        var starter = new ScriptedStarter(() => Process.GetProcessById(child.Id));
        ProcessTools tools = NewTools(NewPipeDirectory(), starter);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            // A cancelled wait reports the cancellation itself, not a coded MCP error, as the pipe calls do.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.LaunchYaatAsync(cancellation.Token, appDataDir, exePath, 30));

            Assert.True(child.HasExited);
        }
        finally
        {
            StopChild(child);
            Delete(appDataDir);
            Delete(Path.GetDirectoryName(exePath)!);
        }
    }

    [Fact]
    public async Task LaunchYaat_PipeThatNeverAnswers_FailsAtTheDeadline()
    {
        string appDataDir = NewDirectory("appdata");
        string exePath = NewDummyClientExe();
        // This test starts the child itself, so the discovery file and the pipe stub are ready before the launch runs.
        using Process child = Process.Start(Shell("ping.exe", "-n 30 127.0.0.1"))!;
        string pipeName = $"yaat-stub-host-{Guid.NewGuid():N}";
        WriteDiscovery(child.Id, pipeName, child.ProcessName);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task server = ServeUnansweringAsync(pipeName, ceiling.Token);
        var directory = new PipeDirectory(DiscoveryDirectory, child.ProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);
        var starter = new ScriptedStarter(() => Process.GetProcessById(child.Id));
        ProcessTools tools = NewTools(directory, starter);
        try
        {
            var elapsed = Stopwatch.StartNew();
            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                tools.LaunchYaatAsync(CancellationToken.None, appDataDir, exePath, 1)
            );
            elapsed.Stop();

            Assert.StartsWith($"no automation pipe answered within 1 s (pid {child.Id});", failure.Message, StringComparison.Ordinal);
            // Without the deadline linked into the pipe calls, the ping waits out its own connect and request timeouts.
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"the deadline did not bound the stalled pipe call: {elapsed.Elapsed}");
            Assert.True(child.HasExited);
        }
        finally
        {
            StopChild(child);
            await ceiling.CancelAsync();
            await server;
            Delete(appDataDir);
            Delete(Path.GetDirectoryName(exePath)!);
        }
    }

    // A host error is the host's own answer, not a pipe still coming up, so the wait reports it at once instead of polling on.
    [Fact]
    public async Task LaunchYaat_HostCodedError_FailsAtOnce()
    {
        string appDataDir = NewDirectory("appdata");
        string exePath = NewDummyClientExe();
        // This test starts the child itself, so the discovery file and the pipe stub are ready before the launch runs.
        using Process child = Process.Start(Shell("ping.exe", "-n 30 127.0.0.1"))!;
        string pipeName = $"yaat-stub-host-{Guid.NewGuid():N}";
        WriteDiscovery(child.Id, pipeName, child.ProcessName);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task server = ServeCodedErrorAfterPingAsync(pipeName, child.Id, ceiling.Token);
        var directory = new PipeDirectory(DiscoveryDirectory, child.ProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);
        var starter = new ScriptedStarter(() => Process.GetProcessById(child.Id));
        ProcessTools tools = NewTools(directory, starter);
        const int waitSeconds = 10;
        try
        {
            var elapsed = Stopwatch.StartNew();
            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                tools.LaunchYaatAsync(CancellationToken.None, appDataDir, exePath, waitSeconds)
            );
            elapsed.Stop();

            Assert.Equal("HOST_BUSY: The host could not list its windows. Hint: try again", failure.Message);
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(waitSeconds / 2.0),
                $"the launch polled past the host's coded error: {elapsed.Elapsed}"
            );
        }
        finally
        {
            StopChild(child);
            await ceiling.CancelAsync();
            await server;
            Delete(appDataDir);
            Delete(Path.GetDirectoryName(exePath)!);
        }
    }

    // A host that answers the ping but lists no window: the launch runs out its deadline, and the kill path's self-guard
    // leaves the test host running, so the pid the test remembered has to be dropped by the launch's own forget.
    [AvaloniaFact]
    public async Task LaunchYaat_DeadlineFailure_ForgetsTheRememberedPid()
    {
        using AutomationHost host = StartHost(() => []);
        string appDataDir = NewDirectory("appdata");
        string exePath = NewDummyClientExe();
        PipeDirectory directory = NewPipeDirectory();
        directory.RememberTarget(Pid);
        var starter = new ScriptedStarter(() => Process.GetCurrentProcess());
        ProcessTools tools = NewTools(directory, starter);
        try
        {
            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                tools.LaunchYaatAsync(CancellationToken.None, appDataDir, exePath, 1)
            );

            Assert.StartsWith($"no automation pipe answered within 1 s (pid {Pid});", failure.Message, StringComparison.Ordinal);
            Assert.Null(directory.LastTargetPid);
        }
        finally
        {
            ProcessTools.Forget(Pid);
            await directory.ForgetAsync(Pid);
            Delete(appDataDir);
            Delete(Path.GetDirectoryName(exePath)!);
        }
    }

    // The stub answers the ping and then drops the connection on list_windows; the child exits with 7 while the wait is
    // still polling, so the failure has to name that exit code rather than the pipe's own closed-or-timed-out text.
    [Fact]
    public async Task LaunchYaat_PipeClosesMidLaunch_ReportsTheExitCode()
    {
        string appDataDir = NewDirectory("appdata");
        string exePath = NewDummyClientExe();
        string childExecutable = "cmd.exe";
        string pipeName = $"yaat-stub-host-{Guid.NewGuid():N}";
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task? server = null;
        Process? child = null;
        int childPid = 0;
        // The launcher starts the child, so the handle it reads the exit code from is one Process.Start made, as the real
        // starter makes it; the discovery file and the stub go up in the same factory, before the wait's first poll.
        var starter = new ScriptedStarter(() =>
        {
            child = Process.Start(Shell(childExecutable, "/c ping -n 3 127.0.0.1 > nul & exit 7"))!;
            childPid = child.Id;
            WriteDiscovery(childPid, pipeName, child.ProcessName);
            server = ServeClosingAfterPingAsync(pipeName, childPid, ceiling.Token);
            return child!;
        });
        var directory = new PipeDirectory(
            DiscoveryDirectory,
            Path.GetFileNameWithoutExtension(childExecutable),
            NullLogger<PipeDirectory>.Instance,
            NullLogger<PipeClient>.Instance
        );
        ProcessTools tools = NewTools(directory, starter);
        try
        {
            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                tools.LaunchYaatAsync(CancellationToken.None, appDataDir, exePath, 15)
            );

            Assert.StartsWith($"The client (pid {childPid}) exited with code 7 before opening a window", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("closed or timed out", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            StopChild(child);
            if (server is not null)
            {
                await ceiling.CancelAsync();
                await server;
            }

            Delete(appDataDir);
            Delete(Path.GetDirectoryName(exePath)!);
        }
    }

    /// <summary>A process a test makes itself, hidden and started without the shell, standing in for the client.</summary>
    private static ProcessStartInfo Shell(string executable, string arguments) =>
        new(executable, arguments) { UseShellExecute = false, CreateNoWindow = true };

    private static string NewDirectory(string prefix) => Path.Combine(Path.GetTempPath(), $"yaat-launch-{prefix}-{Guid.NewGuid():N}");

    /// <summary>A dummy executable carrying the client's file name, which is all <c>launch_yaat</c> checks of the path.</summary>
    private static string NewDummyClientExe()
    {
        string directory = NewDirectory("exe");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Yaat.Client.exe");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Stops a test's own child when the launch left it running, then releases the handle. The launch's kill-and-forget
    /// disposes the handle it was given, which for a shared one leaves nothing to stop and nothing to read.
    /// </summary>
    private static void StopChild(Process? child)
    {
        if (child is null)
        {
            return;
        }

        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException ex)
        {
            // The child's handle is spent, so the process it named is gone with it.
            Report($"The launch already stopped the child: {ex.Message}");
        }
    }

    private static ProcessTools NewTools(PipeDirectory directory, IProcessStarter starter) =>
        new(new ElementRegistry(NullLogger<ElementRegistry>.Instance), directory, starter, NullLogger<ProcessTools>.Instance);

    private static string[] Lines(string text) => text.Split(Environment.NewLine);

    private PipeDirectory NewPipeDirectory() =>
        new(DiscoveryDirectory, Process.GetCurrentProcess().ProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);

    /// <summary>Writes the client-style discovery file that makes <paramref name="pid"/>'s pipe the one the directory finds.</summary>
    private void WriteDiscovery(int pid, string pipeName, string processName)
    {
        Directory.CreateDirectory(DiscoveryDirectory);
        var discovery = new DiscoveryInfo
        {
            Pid = pid,
            PipeName = pipeName,
            ProcessName = processName,
            StartTime = DateTimeOffset.Now,
            ProtocolVersion = ProtocolVersion.Current,
        };
        File.WriteAllText(Path.Combine(DiscoveryDirectory, $"{pid}.json"), ProtocolSerializer.Serialize(discovery));
    }

    /// <summary>Accepts one client and reads its requests without ever answering, until it hangs up or <paramref name="ct"/> cancels.</summary>
    private static async Task ServeUnansweringAsync(string pipeName, CancellationToken ct)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            await pipe.WaitForConnectionAsync(ct);
            using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            while (await reader.ReadLineAsync(ct) is not null)
            {
                // A real host answers each request; this stub is testing what happens when one never does.
            }
        }
        catch (IOException ex)
        {
            Report($"The client hung up on the unanswering stub pipe: {ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            Report($"The unanswering stub pipe's ceiling passed: {ex.Message}");
        }
    }

    /// <summary>Answers one ping for <paramref name="pid"/>, then drops the connection on the next request and stops.</summary>
    private static async Task ServeClosingAfterPingAsync(string pipeName, int pid, CancellationToken ct)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            await pipe.WaitForConnectionAsync(ct);
            using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, utf8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                using var request = JsonDocument.Parse(line);
                string id = request.RootElement.GetProperty("id").GetString()!;
                if (request.RootElement.GetProperty("method").GetString() != ProtocolMethods.Ping)
                {
                    // The client is left hanging while the pipe goes, so it reads a broken connection, not an answer.
                    break;
                }

                var response = AutomationResponse.Success(id, ProtocolSerializer.ToElement(new PingResult(pid, ProtocolVersion.Current)));
                await writer.WriteLineAsync(ProtocolSerializer.Serialize(response).AsMemory(), ct);
            }
        }
        catch (IOException ex)
        {
            Report($"The client hung up on the closing stub pipe: {ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            Report($"The closing stub pipe's ceiling passed: {ex.Message}");
        }
    }

    /// <summary>
    /// Answers a ping for <paramref name="pid"/> and every other request with a coded host error, until the client hangs up
    /// or <paramref name="ct"/> cancels.
    /// </summary>
    private static async Task ServeCodedErrorAfterPingAsync(string pipeName, int pid, CancellationToken ct)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var busy = new AutomationError("The host could not list its windows.", "HOST_BUSY", "try again", null);
        try
        {
            await pipe.WaitForConnectionAsync(ct);
            using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var writer = new StreamWriter(pipe, utf8, bufferSize: 1024, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    using var request = JsonDocument.Parse(line);
                    string id = request.RootElement.GetProperty("id").GetString()!;
                    AutomationResponse response =
                        (request.RootElement.GetProperty("method").GetString() == ProtocolMethods.Ping)
                            ? AutomationResponse.Success(id, ProtocolSerializer.ToElement(new PingResult(pid, ProtocolVersion.Current)))
                            : AutomationResponse.Failure(id, busy);
                    await writer.WriteLineAsync(ProtocolSerializer.Serialize(response).AsMemory(), ct);
                    await writer.FlushAsync(ct);
                }
            }
            finally
            {
                await CloseAfterHangUpAsync(writer);
            }
        }
        catch (OperationCanceledException ex)
        {
            Report($"The coded-error stub pipe's ceiling passed: {ex.Message}");
        }
    }

    /// <summary>Disposes the stub's writer, whose closing flush may find the pipe already broken by the client.</summary>
    private static async Task CloseAfterHangUpAsync(StreamWriter writer)
    {
        try
        {
            await writer.DisposeAsync();
        }
        catch (IOException ex)
        {
            // The client closes the pipe after a coded error, so the closing flush can hit a broken pipe; every answer was
            // already flushed when it was written, so nothing is lost.
            Report($"The client hung up before the stub writer closed: {ex.Message}");
        }
    }

    /// <summary>Reports an expected stub failure without failing the test; a report the test did not expect propagates.</summary>
    private static void Report(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    /// <summary>Records the start info it is handed and returns whatever its factory makes, so a test scripts the launched process.</summary>
    private sealed class ScriptedStarter(Func<Process> start) : IProcessStarter
    {
        public ProcessStartInfo? LastStartInfo { get; private set; }

        public Process Start(ProcessStartInfo startInfo)
        {
            LastStartInfo = startInfo;
            return start();
        }
    }
}
