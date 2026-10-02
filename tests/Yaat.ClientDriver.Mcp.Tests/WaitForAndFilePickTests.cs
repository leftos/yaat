extern alias mcp;

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using mcp::Yaat.ClientDriver.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// <c>wait_for</c> and <c>queue_file_pick</c> against a real <see cref="AutomationHost"/> in this process: each goes to the
/// pid given or else the client the last pipe call reached, sends the params its condition or answer needs, and surfaces
/// the host's coded errors as they are. The file-pick queue is process-wide, so every test clears it and restores the mode.
/// </summary>
public sealed class WaitForAndFilePickTests : AutomationHostFixture
{
    private static int Pid => Environment.ProcessId;

    protected override void Dispose(bool disposing)
    {
        FilePickQueue.Clear();
        AutomationMode.IsEnabled = false;
        base.Dispose(disposing);
    }

    [AvaloniaFact]
    public async Task WaitFor_ExistingElement_Holds()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Form(), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            string result = await new PipeTools(directory).WaitForAsync("#Box", "exists", CancellationToken.None, Pid);

            Assert.StartsWith("exists held on '#Box' after ", result, StringComparison.Ordinal);
            Assert.EndsWith(" ms (1 matches, pipe)", result, StringComparison.Ordinal);
            Assert.Equal(Pid, directory.LastTargetPid);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task WaitFor_NoPid_UsesLastTarget()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Form(), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            directory.RememberTarget(Pid);

            string result = await new PipeTools(directory).WaitForAsync("#Box", "visible", CancellationToken.None);

            Assert.StartsWith("visible held on '#Box' after ", result, StringComparison.Ordinal);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [Fact]
    public async Task WaitFor_NoTarget_Fails()
    {
        var tools = new PipeTools(NewPipeDirectory());

        McpException failure = await Assert.ThrowsAsync<McpException>(() => tools.WaitForAsync("#Box", "exists", CancellationToken.None));

        Assert.Equal("No YAAT client to target: pass pid, or call a tool on a YAAT client first (launch_yaat, list_windows).", failure.Message);
    }

    [AvaloniaFact]
    public async Task WaitFor_Timeout_SurfacesHostTimeout()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Form(), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            var tools = new PipeTools(directory);

            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                tools.WaitForAsync("#Box", "not_exists", CancellationToken.None, Pid, timeoutMs: 200)
            );

            Assert.StartsWith(
                "TIMEOUT: Condition 'not_exists' on selector '#Box' did not hold within 200 ms",
                failure.Message,
                StringComparison.Ordinal
            );
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task WaitFor_TextConditionSendsText()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Form(), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            string result = await new PipeTools(directory).WaitForAsync("#Box", "text_contains", CancellationToken.None, Pid, "koak");

            Assert.StartsWith("text_contains held on '#Box' after ", result, StringComparison.Ordinal);
            Assert.EndsWith(" ms (1 matches, pipe)", result, StringComparison.Ordinal);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    // A real wait past the 30 s default would make this test slow, so the timeout arithmetic is checked through its seam, and the
    // per-call timeout it feeds is shown to reach the pipe client: a short one cuts a late answer, a long one lets it through.
    [Fact]
    public async Task WaitFor_LongTimeout_NotCutByClientTimeout()
    {
        Assert.Equal((30000, TimeSpan.FromSeconds(35)), PipeTools.WaitForTimeouts(30000));
        Assert.Equal((30000, TimeSpan.FromSeconds(35)), PipeTools.WaitForTimeouts(90000));
        Assert.Equal((100, TimeSpan.FromMilliseconds(5100)), PipeTools.WaitForTimeouts(10));
        Assert.True(PipeTools.WaitForTimeouts(30000).Request > PipeClient.RequestTimeout, "the longest wait outlives the default timeout");

        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        object waitParams = new { selector = "#Box", condition = "exists" };
        await using var client = new PipeClient(pipeName, null, NullLogger<PipeClient>.Instance);

        Task cutServer = ServeLateAsync(pipeName);
        McpException cut = await Assert.ThrowsAsync<McpException>(() =>
            client.SendAsync<WaitForResult>(ProtocolMethods.WaitFor, waitParams, TimeSpan.FromMilliseconds(200), CancellationToken.None)
        );
        await cutServer;
        Task lateServer = ServeLateAsync(pipeName);
        WaitForResult late = await client.SendAsync<WaitForResult>(
            ProtocolMethods.WaitFor,
            waitParams,
            TimeSpan.FromSeconds(10),
            CancellationToken.None
        );
        await client.DisposeAsync();
        await lateServer;

        Assert.Contains("closed or timed out", cut.Message, StringComparison.Ordinal);
        Assert.Equal(new WaitForResult(1000, 1), late);
    }

    [AvaloniaFact]
    public async Task QueueFilePick_Path_QueuesAndPickerTakesIt()
    {
        FilePickQueue.Clear();
        AutomationMode.IsEnabled = true;
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Form(), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            string result = await new PipeTools(directory).QueueFilePickAsync(CancellationToken.None, @"C:\temp\scenario.json", false, Pid);

            Assert.Equal(@"queued C:\temp\scenario.json (1 waiting, pipe)", result);
            IFilePickerService picker = FilePickerFactory.Create(new Window());
            Assert.Equal(@"C:\temp\scenario.json", await picker.OpenFileAsync(new OpenFileOptions("Open scenario", [])));
            Assert.Equal(0, FilePickQueue.Count);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task QueueFilePick_Cancel_Queues()
    {
        FilePickQueue.Clear();
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Form(), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            string result = await new PipeTools(directory).QueueFilePickAsync(CancellationToken.None, "", true, Pid);

            Assert.Equal("queued a cancel (1 waiting, pipe)", result);
            Assert.True(FilePickQueue.TryDequeue(out FilePickAnswer? answer));
            Assert.True(answer.IsCancel);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task QueueFilePick_BothPathAndCancel_ReportsInvalidParam()
    {
        FilePickQueue.Clear();
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Form(), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            var tools = new PipeTools(directory);

            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                tools.QueueFilePickAsync(CancellationToken.None, @"C:\temp\scenario.json", true, Pid)
            );

            Assert.StartsWith("INVALID_PARAM: Give either 'path' or 'cancel', not both.", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, FilePickQueue.Count);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    /// <summary>Serves one connection, answering every wait_for one second late with 1000 ms elapsed and one match.</summary>
    private static async Task ServeLateAsync(string pipeName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await StubPipeHost.ServeAsync(pipeName, (_, _) => new WaitForResult(1000, 1), TimeSpan.FromSeconds(1), timeout.Token);
    }

    /// <summary>A panel holding one text box named Box that reads "KOAK 123".</summary>
    private static StackPanel Form() =>
        new()
        {
            Children =
            {
                new TextBox { Name = "Box", Text = "KOAK 123" },
            },
        };

    private PipeDirectory NewPipeDirectory() =>
        new(DiscoveryDirectory, Process.GetCurrentProcess().ProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);
}
