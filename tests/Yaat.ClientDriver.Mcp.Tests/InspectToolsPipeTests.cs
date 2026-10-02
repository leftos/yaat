extern alias mcp;

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
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
/// The inspect tools against a real <see cref="AutomationHost"/> in this process: <c>list_windows</c> answers over the pipe
/// (and through UI Automation when the cached client was disposed), and an id from the pipe is walked, searched and read
/// over the pipe, in the UI Automation rows' shape with Avalonia type names and window-relative rectangles.
/// </summary>
public sealed class InspectToolsPipeTests : AutomationHostFixture
{
    private const string ConnectRowTail = "| Button | Connect | id=ConnectButton | enabled=True | rect=(20,50 80x30)";
    private const string NoMatch = "No match. Widen the criteria, or dump_tree the root to see what is actually there.";

    private static int Pid => Environment.ProcessId;

    [AvaloniaFact]
    public async Task ListWindows_PipePid_ReportsTheWindowFromThePipe()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", BuildForm(new Button()), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);

            string rows = await tools.ListWindowsAsync(Pid, CancellationToken.None);

            string row = Assert.Single(Lines(rows));
            Assert.Equal($"{IdOf(row)} | Window | PipeWindow | id= | visible=True | active=True | rect=(0,0 400x300)", row);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    // The headless window has no HWND, so UI Automation finds no top-level window for this process: the fallback answers
    // with the UI Automation path's empty-result text.
    [AvaloniaFact]
    public async Task ListWindows_PipeClientEvicted_FallsBackToUia()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", BuildForm(new Button()), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            PipeClient cached = (await directory.TryGetAsync(Pid, CancellationToken.None))!;
            await cached.DisposeAsync();
            InspectTools tools = NewTools(directory);

            string rows = await tools.ListWindowsAsync(Pid, CancellationToken.None);

            Assert.Equal($"No top-level windows for pid {Pid} — it may still be starting, or it has none.", rows);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task DumpTree_PipeId_UsesTheRowShapeAndWindowRelativeRects()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", BuildForm(ConnectButton()), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));

            string tree = await tools.DumpTreeAsync(windowId, CancellationToken.None, 20);

            string[] lines = Lines(tree);
            Assert.Equal($"{windowId} | Window | PipeWindow | id=PipeWindow | enabled=True | rect=(0,0 400x300)", lines[0]);
            string connect = Assert.Single(lines, line => line.EndsWith(ConnectRowTail, StringComparison.Ordinal)).TrimStart();
            Assert.Equal($"{IdOf(connect)} {ConnectRowTail}", connect);
            Assert.Contains(
                lines,
                line => line.TrimStart().EndsWith("| TextBox | KOAK | id=Callsign | enabled=True | rect=(20,10 100x40)", StringComparison.Ordinal)
            );
            Assert.Contains(
                lines,
                line => line.Contains("| TextBlock | Ready | id=Status | enabled=True | rect=(20,80 380x", StringComparison.Ordinal)
            );
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task DumpTree_PipeId_TruncatesAt400Lines()
    {
        using AutomationHost host = StartHost(() => Windows);
        var panel = new StackPanel { Name = "Many" };
        for (int i = 0; i < 450; i++)
        {
            panel.Children.Add(new Border { Width = 1, Height = 1 });
        }

        ShowWindow("PipeWindow", panel, null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));

            string tree = await tools.DumpTreeAsync(windowId, CancellationToken.None, 20);

            string[] lines = Lines(tree);
            Assert.Equal(401, lines.Length);
            Assert.Equal("… truncated at 400 lines — narrow the walk with a deeper element id or a smaller maxDepth", lines[^1]);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task FindElements_PipeRoot_FiltersByAvaloniaTypeAndAutomationId()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", BuildForm(ConnectButton()), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));

            string button = await tools.FindElementsAsync(windowId, CancellationToken.None, "", "ConnectButton", "Button");
            string textBox = await tools.FindElementsAsync(windowId, CancellationToken.None, "", "", "TextBox");
            string uiaAlias = await tools.FindElementsAsync(windowId, CancellationToken.None, "", "", "Edit");

            string buttonRow = Assert.Single(Lines(button));
            Assert.Equal($"{IdOf(buttonRow)} {ConnectRowTail}", buttonRow);
            string textBoxRow = Assert.Single(Lines(textBox));
            Assert.Equal($"{IdOf(textBoxRow)} | TextBox | KOAK | id=Callsign | enabled=True | rect=(20,10 100x40)", textBoxRow);
            Assert.Equal(NoMatch, uiaAlias);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task FindElements_PipeRoot_ExcludesTheRoot()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", BuildForm(ConnectButton()), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));
            string formId = IdOf(await tools.FindElementsAsync(windowId, CancellationToken.None, "Form", "", ""));
            string buttonId = IdOf(await tools.FindElementsAsync(windowId, CancellationToken.None, "", "ConnectButton", ""));

            string panels = await tools.FindElementsAsync(formId, CancellationToken.None, "", "", "StackPanel");
            string buttons = await tools.FindElementsAsync(buttonId, CancellationToken.None, "", "", "Button");

            string panelRow = Assert.Single(Lines(panels));
            Assert.Contains("| StackPanel | Inner | id=Inner |", panelRow, StringComparison.Ordinal);
            Assert.Equal(NoMatch, buttons);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task GetValue_PipeTextBox_ReturnsItsText()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", BuildForm(ConnectButton()), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));
            string textBoxId = IdOf(await tools.FindElementsAsync(windowId, CancellationToken.None, "", "", "TextBox"));

            string value = await tools.GetValueAsync(textBoxId, CancellationToken.None);

            Assert.Equal("KOAK", value);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task GetValue_PipeElementWithoutText_ThrowsTheNoTextMessage()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", BuildForm(ConnectButton()), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));
            string borderId = IdOf(await tools.FindElementsAsync(windowId, CancellationToken.None, "Nested", "", ""));

            McpException failure = await Assert.ThrowsAsync<McpException>(() => tools.GetValueAsync(borderId, CancellationToken.None));

            Assert.Equal($"Element '{borderId}' (Border) has no readable text; use dump_tree or screenshot to inspect it.", failure.Message);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task DumpTree_StalePipeId_ReadsElementIsGone()
    {
        using AutomationHost host = StartHost(() => Windows);
        Button button = ConnectButton();
        StackPanel form = BuildForm(button);
        ShowWindow("PipeWindow", form, null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));
            string buttonId = IdOf(await tools.FindElementsAsync(windowId, CancellationToken.None, "", "ConnectButton", ""));
            form.Children.Remove(button);

            McpException failure = await Assert.ThrowsAsync<McpException>(() => tools.DumpTreeAsync(buttonId, CancellationToken.None, 4));

            Assert.Equal(
                $"Element '{buttonId}' is gone — its window or process has exited; call list_windows or find_elements again for a fresh id",
                failure.Message
            );
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task FindElements_PipeRoot_AutomationIdFallsBackToName()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", BuildForm(ConnectButton()), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));

            string rows = await tools.FindElementsAsync(windowId, CancellationToken.None, "", "Callsign", "");

            string row = Assert.Single(Lines(rows));
            Assert.Equal($"{IdOf(row)} | TextBox | KOAK | id=Callsign | enabled=True | rect=(20,10 100x40)", row);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task FindElements_PipeRoot_NameMatchesATextBlocksText()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", BuildForm(ConnectButton()), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));

            string rows = await tools.FindElementsAsync(windowId, CancellationToken.None, "Ready", "", "");

            string row = Assert.Single(Lines(rows));
            Assert.StartsWith($"{IdOf(row)} | TextBlock | Ready | id=Status | enabled=True | rect=(20,80 380x", row, StringComparison.Ordinal);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task FindElements_PipeRoot_DisabledControlReadsEnabledFalse()
    {
        using AutomationHost host = StartHost(() => Windows);
        Button button = ConnectButton();
        button.IsEnabled = false;
        ShowWindow("PipeWindow", BuildForm(button), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));

            string rows = await tools.FindElementsAsync(windowId, CancellationToken.None, "", "ConnectButton", "");

            string row = Assert.Single(Lines(rows));
            Assert.Equal($"{IdOf(row)} | Button | Connect | id=ConnectButton | enabled=False | rect=(20,50 80x30)", row);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task GetValue_PipeEmptyTextBox_ReturnsEmpty()
    {
        using AutomationHost host = StartHost(() => Windows);
        var textBox = new TextBox { Name = "Empty" };
        AutomationProperties.SetName(textBox, "Callsign label");
        ShowWindow("PipeWindow", new StackPanel { Children = { textBox } }, null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));
            string row = await tools.FindElementsAsync(windowId, CancellationToken.None, "", "Empty", "");

            string value = await tools.GetValueAsync(IdOf(row), CancellationToken.None);

            Assert.Contains("| TextBox | Callsign label | id=Empty |", row, StringComparison.Ordinal);
            Assert.Equal(string.Empty, value);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task ListWindows_OverlayPopup_RectMatchesItsDumpTreeRow()
    {
        using AutomationHost host = StartHost(() => Windows);
        var popup = new Popup
        {
            Child = new Border
            {
                Name = "PopupContent",
                Width = 40,
                Height = 30,
            },
        };
        StackPanel form = BuildForm(ConnectButton());
        form.Children.Add(popup);
        Window window = ShowWindow("PipeWindow", form, null);
        popup.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            InspectTools tools = NewTools(directory);

            string[] rows = Lines(await tools.ListWindowsAsync(Pid, CancellationToken.None));
            string popupRow = Assert.Single(rows, row => row.Contains(" | popup | ", StringComparison.Ordinal));
            string popupTree = await tools.DumpTreeAsync(IdOf(popupRow), CancellationToken.None, 0);

            Assert.StartsWith(
                $"{IdOf(popupRow)} | OverlayPopupHost |  | id= | visible=True | active=False | popup | rect=(",
                popupRow,
                StringComparison.Ordinal
            );
            Assert.StartsWith($"{IdOf(popupRow)} | OverlayPopupHost |", popupTree, StringComparison.Ordinal);
            Assert.Equal(RectOf(popupRow), RectOf(popupTree));
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    // A real host answers list_windows with no coded error, so a scripted pipe server stands in for one.
    [Fact]
    public async Task ListWindows_HostAnswersWithACodedError_ThrowsTheHostMessage()
    {
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        Directory.CreateDirectory(DiscoveryDirectory);
        var discovery = new DiscoveryInfo
        {
            Pid = Pid,
            PipeName = pipeName,
            ProcessName = Process.GetCurrentProcess().ProcessName,
            StartTime = DateTimeOffset.Now,
            ProtocolVersion = ProtocolVersion.Current,
        };
        await File.WriteAllTextAsync(
            Path.Combine(DiscoveryDirectory, $"{Pid}.json"),
            ProtocolSerializer.Serialize(discovery),
            TestContext.Current.CancellationToken
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task server = ServeListWindowsErrorAsync(pipeName, timeout.Token);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            McpException failure = await Assert.ThrowsAsync<McpException>(() => NewTools(directory).ListWindowsAsync(Pid, CancellationToken.None));

            Assert.Equal("HOST_BUSY: The host could not list its windows. Hint: try again", failure.Message);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }

        await server;
    }

    /// <summary>Answers a ping with this process's pid and every other request with a coded error, until the client hangs up.</summary>
    private static async Task ServeListWindowsErrorAsync(string pipeName, CancellationToken ct)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(ct);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, utf8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            using var request = JsonDocument.Parse(line);
            string id = request.RootElement.GetProperty("id").GetString()!;
            bool isPing = request.RootElement.GetProperty("method").GetString() == ProtocolMethods.Ping;
            AutomationResponse response = isPing
                ? AutomationResponse.Success(id, ProtocolSerializer.ToElement(new PingResult(Pid, ProtocolVersion.Current)))
                : AutomationResponse.Failure(id, new AutomationError("The host could not list its windows.", "HOST_BUSY", "try again", null));
            await writer.WriteLineAsync(ProtocolSerializer.Serialize(response).AsMemory(), ct);
        }
    }

    private static string RectOf(string row) => row[row.LastIndexOf(" | rect=", StringComparison.Ordinal)..];

    /// <summary>
    /// A form at (20,10) in the window: a 100 x 40 text box reading KOAK, then <paramref name="button"/> below it, then a
    /// border named Nested holding a panel named Inner holding a text block reading Ready.
    /// </summary>
    private static StackPanel BuildForm(Button button) =>
        new()
        {
            Name = "Form",
            Margin = new Thickness(20, 10, 0, 0),
            Children =
            {
                new TextBox
                {
                    Name = "Callsign",
                    Text = "KOAK",
                    Width = 100,
                    Height = 40,
                    HorizontalAlignment = HorizontalAlignment.Left,
                },
                button,
                new Border
                {
                    Name = "Nested",
                    Child = new StackPanel
                    {
                        Name = "Inner",
                        Children =
                        {
                            new TextBlock { Name = "Status", Text = "Ready" },
                        },
                    },
                },
            },
        };

    /// <summary>An 80 x 30 button named Connect with the explicit automation id ConnectButton.</summary>
    private static Button ConnectButton()
    {
        var button = new Button
        {
            Name = "Connect",
            Content = "Connect",
            Width = 80,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetAutomationId(button, "ConnectButton");
        return button;
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine);

    private static string IdOf(string row) => row.TrimStart().Split(" | ")[0];

    private static InspectTools NewTools(PipeDirectory directory) =>
        new(new ElementRegistry(NullLogger<ElementRegistry>.Instance), directory, NullLogger<InspectTools>.Instance);

    private PipeDirectory NewPipeDirectory() =>
        new(DiscoveryDirectory, Process.GetCurrentProcess().ProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);
}
