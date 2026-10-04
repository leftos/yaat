extern alias mcp;

using System.Diagnostics;
using System.Windows.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;
using mcp::Yaat.ClientDriver.Mcp;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using mcp::Yaat.ClientDriver.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The input tools against a real <see cref="AutomationHost"/> in this process: an id from the pipe is clicked, invoked,
/// written, typed into and focused over the pipe, <c>click_point</c> takes a pipe window and window-relative DIPs, and
/// <c>send_keys</c> without an element goes to the client the last pipe-routed call reached.
/// </summary>
public sealed class InputToolsPipeTests : AutomationHostFixture
{
    private static int Pid => Environment.ProcessId;

    [AvaloniaFact]
    public async Task Click_PipeButton_RunsItsCommandAndNamesTheAction()
    {
        using AutomationHost host = StartHost(() => Windows);
        int executed = 0;
        var button = new Button
        {
            Name = "Run",
            Content = "Run",
            Command = new RelayCommand(() => executed++),
        };
        ShowWindow("PipeWindow", Column(button), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Run");

            string result = await tools.Input.ClickAsync(id, CancellationToken.None);

            Assert.StartsWith($"clicked (command) on {id} | Button | Run | id=Run | enabled=True | rect=(", result, StringComparison.Ordinal);
            Assert.EndsWith(" (pipe)", result, StringComparison.Ordinal);
            Assert.Equal(1, executed);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task Click_PipeId_ModifiersAndDoubleClickReachPointer()
    {
        using AutomationHost host = StartHost(() => Windows);
        Border pad = Pad();
        List<(KeyModifiers Modifiers, int ClickCount)> presses = [];
        pad.PointerPressed += (_, e) => presses.Add((e.KeyModifiers, e.ClickCount));
        ShowWindow("PipeWindow", Column(pad), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Pad");

            string result = await tools.Input.ClickAsync(id, CancellationToken.None, "left", true, "ctrl+shift");

            Assert.StartsWith($"clicked (pointer) on {id} | Border |", result, StringComparison.Ordinal);
            Assert.Equal([(KeyModifiers.Control | KeyModifiers.Shift, 1), (KeyModifiers.Control | KeyModifiers.Shift, 2)], presses);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task Click_PipeDisabledElement_ReportsDisabled()
    {
        using AutomationHost host = StartHost(() => Windows);
        var button = new Button
        {
            Name = "Run",
            Content = "Run",
            IsEnabled = false,
        };
        ShowWindow("PipeWindow", Column(button), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Run");

            McpException failure = await Assert.ThrowsAsync<McpException>(() => tools.Input.ClickAsync(id, CancellationToken.None));

            Assert.StartsWith("ELEMENT_DISABLED: ", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task Click_PipeNodeGone_ReportsElementIsGone()
    {
        using AutomationHost host = StartHost(() => Windows);
        var button = new Button { Name = "Run", Content = "Run" };
        StackPanel column = Column(button);
        ShowWindow("PipeWindow", column, null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Run");
            column.Children.Remove(button);

            McpException failure = await Assert.ThrowsAsync<McpException>(() => tools.Input.ClickAsync(id, CancellationToken.None));

            Assert.StartsWith($"Element '{id}' is gone", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task Invoke_PipeButton_RoutesToClick()
    {
        using AutomationHost host = StartHost(() => Windows);
        int executed = 0;
        var button = new Button
        {
            Name = "Run",
            Content = "Run",
            Command = new RelayCommand(() => executed++),
        };
        ShowWindow("PipeWindow", Column(button), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Run");

            string result = await tools.Input.InvokeAsync(id, CancellationToken.None);

            Assert.StartsWith($"invoked (command) on {id} | Button | Run |", result, StringComparison.Ordinal);
            Assert.EndsWith(" (pipe)", result, StringComparison.Ordinal);
            Assert.Equal(1, executed);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task SetText_PipeTextBox_ReplacesText()
    {
        using AutomationHost host = StartHost(() => Windows);
        var box = new TextBox { Name = "Box", Text = "old" };
        ShowWindow("PipeWindow", Column(box), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Box");

            string result = await tools.Input.SetTextAsync(id, "N123AB", CancellationToken.None);

            Assert.StartsWith($"set 'N123AB' on {id} | TextBox |", result, StringComparison.Ordinal);
            Assert.EndsWith(" (pipe)", result, StringComparison.Ordinal);
            Assert.Equal("N123AB", box.Text);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task SetText_PipeReadOnly_ReportsDisabled()
    {
        using AutomationHost host = StartHost(() => Windows);
        var box = new TextBox
        {
            Name = "Box",
            Text = "old",
            IsReadOnly = true,
        };
        ShowWindow("PipeWindow", Column(box), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Box");

            McpException failure = await Assert.ThrowsAsync<McpException>(() => tools.Input.SetTextAsync(id, "new", CancellationToken.None));

            Assert.StartsWith("ELEMENT_DISABLED: ", failure.Message, StringComparison.Ordinal);
            Assert.Equal("old", box.Text);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task SendKeys_PipeId_TypesCharactersAndChord()
    {
        using AutomationHost host = StartHost(() => Windows);
        var box = new TextBox { Name = "Box" };
        List<(Key Key, KeyModifiers Modifiers)> keyDowns = [];
        box.AddHandler(InputElement.KeyDownEvent, (_, e) => keyDowns.Add((e.Key, e.KeyModifiers)), RoutingStrategies.Tunnel, true);
        ShowWindow("PipeWindow", Column(box), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Box");

            string typed = await tools.Input.SendKeysAsync("abc{ENTER}", CancellationToken.None, id);
            string chord = await tools.Input.SendKeysAsync("^a", CancellationToken.None, id);

            Assert.StartsWith($"sent 'abc{{ENTER}}' to {id} | TextBox |", typed, StringComparison.Ordinal);
            Assert.EndsWith(", 4 strokes (pipe)", typed, StringComparison.Ordinal);
            Assert.EndsWith(", 1 stroke (pipe)", chord, StringComparison.Ordinal);
            Assert.Equal("abc", box.Text);
            Assert.Equal([(Key.Enter, KeyModifiers.None), (Key.A, KeyModifiers.Control)], keyDowns);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task SendKeys_NoElement_GoesToLastPipePid()
    {
        using AutomationHost host = StartHost(() => Windows);
        var box = new TextBox { Name = "Box" };
        ShowWindow("PipeWindow", Column(box), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            await tools.Inspect.ListWindowsAsync(Pid, CancellationToken.None);
            box.Focus();

            string result = await tools.Input.SendKeysAsync("xy", CancellationToken.None, "");

            Assert.Equal("sent 'xy' to the focused element, 2 strokes (pipe)", result);
            Assert.Equal("xy", box.Text);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task SendKeys_NoElement_AfterForget_ClearsRememberedPid()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Column(new TextBox { Name = "Box" }), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            await tools.Inspect.ListWindowsAsync(Pid, CancellationToken.None);
            Assert.Equal(Pid, directory.LastTargetPid);

            await directory.ForgetAsync(Pid);

            Assert.Null(directory.LastTargetPid);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task SendKeys_PipeMalformedKeys_SurfacesInvalidParam()
    {
        using AutomationHost host = StartHost(() => Windows);
        var box = new TextBox { Name = "Box" };
        ShowWindow("PipeWindow", Column(box), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Box");

            McpException failure = await Assert.ThrowsAsync<McpException>(() => tools.Input.SendKeysAsync("ab{ENTER", CancellationToken.None, id));

            Assert.StartsWith("INVALID_PARAM: Malformed 'keys'", failure.Message, StringComparison.Ordinal);
            Assert.True(string.IsNullOrEmpty(box.Text), "a malformed string sends no stroke at all");
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task Focus_PipeFocusable_Focuses()
    {
        using AutomationHost host = StartHost(() => Windows);
        var box = new TextBox { Name = "Box" };
        ShowWindow("PipeWindow", Column(new Button { Content = "Other" }, box), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Box");

            string result = await tools.Input.FocusAsync(id, CancellationToken.None);

            Assert.StartsWith($"focused {id} | TextBox |", result, StringComparison.Ordinal);
            Assert.EndsWith(" (pipe)", result, StringComparison.Ordinal);
            Assert.True(box.IsFocused);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task Focus_PipeNotFocusable_Reports()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Column(new TextBlock { Name = "Label", Text = "Ready" }), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Label");

            McpException failure = await Assert.ThrowsAsync<McpException>(() => tools.Input.FocusAsync(id, CancellationToken.None));

            Assert.StartsWith("NOT_FOCUSABLE: ", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task ClickPoint_PipeWindowDips_ClicksReceiver()
    {
        using AutomationHost host = StartHost(() => Windows);
        Border pad = Pad();
        List<Point> pressedAt = [];
        pad.PointerPressed += (_, e) => pressedAt.Add(e.GetPosition(pad));
        ShowWindow("PipeWindow", Column(pad), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string windowId = IdOf(await tools.Inspect.ListWindowsAsync(Pid, CancellationToken.None));

            string result = await tools.Input.ClickPointAsync(30, 20, CancellationToken.None, windowElementId: windowId);

            Assert.Equal("clicked left at window point (30,20) on Border (pipe)", result);
            Assert.Equal([new Point(30, 20)], pressedAt);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task ClickPoint_PipeDoubleClickWithModifiers_NamesBoth()
    {
        using AutomationHost host = StartHost(() => Windows);
        Border pad = Pad();
        List<(KeyModifiers Modifiers, int ClickCount)> presses = [];
        pad.PointerPressed += (_, e) => presses.Add((e.KeyModifiers, e.ClickCount));
        ShowWindow("PipeWindow", Column(pad), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string windowId = IdOf(await tools.Inspect.ListWindowsAsync(Pid, CancellationToken.None));

            string result = await tools.Input.ClickPointAsync(30, 20, CancellationToken.None, "left", true, "ctrl", windowId);

            Assert.Equal("double-clicked ctrl+left at window point (30,20) on Border (pipe)", result);
            Assert.Equal([(KeyModifiers.Control, 1), (KeyModifiers.Control, 2)], presses);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task Click_PipeId_RemembersThePid()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Column(new Button { Name = "Run", Content = "Run" }), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string id = await FindIdAsync(tools, "Run");
            await directory.ForgetAsync(Pid);
            Assert.Null(directory.LastTargetPid);

            await tools.Input.ClickAsync(id, CancellationToken.None);

            Assert.Equal(Pid, directory.LastTargetPid);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [AvaloniaFact]
    public async Task ClickPoint_PipeOutOfBounds_Reports()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Column(Pad()), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            Tools tools = NewTools(directory);
            string windowId = IdOf(await tools.Inspect.ListWindowsAsync(Pid, CancellationToken.None));

            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                tools.Input.ClickPointAsync(500, 20, CancellationToken.None, windowElementId: windowId)
            );

            Assert.StartsWith("OUT_OF_BOUNDS: ", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    // Untargeted keys follow whatever the agent touched last, YAAT or CRC: a UI Automation call after a pipe call stops
    // send_keys from typing into the YAAT client. The desktop root is walked without any input being driven at it.
    [AvaloniaFact]
    public async Task UiaCall_AfterPipeCall_ClearsRememberedPid()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("PipeWindow", Column(new TextBox { Name = "Box" }), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            var registry = new ElementRegistry(NullLogger<ElementRegistry>.Instance);
            var inspect = new InspectTools(registry, directory, NullLogger<InspectTools>.Instance);
            await inspect.ListWindowsAsync(Pid, CancellationToken.None);
            Assert.Equal(Pid, directory.LastTargetPid);
            string rootId = registry.Register(AutomationElement.RootElement);

            await inspect.DumpTreeAsync(rootId, CancellationToken.None, 0);

            Assert.Null(directory.LastTargetPid);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    // The desktop root is registered as a UI Automation element without any input being driven at it.
    [Fact]
    public async Task ClickPoint_UiaWindowId_Refused()
    {
        var registry = new ElementRegistry(NullLogger<ElementRegistry>.Instance);
        string uiaId = registry.Register(AutomationElement.RootElement);
        var input = new InputTools(registry, NewPipeDirectory(), NullLogger<InputTools>.Instance);

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            input.ClickPointAsync(10, 10, CancellationToken.None, windowElementId: uiaId)
        );

        Assert.Equal(
            $"windowElementId '{uiaId}' must be a window from a YAAT client driven over its automation pipe; leave it empty to click at "
                + "screen coordinates",
            failure.Message
        );
    }

    [Fact]
    public void SetInputMode_NotesPipeIgnoresIt()
    {
        string result = InputTools.SetInputMode("virtual");

        Assert.Equal("input mode: virtual (pipe-routed calls ignore it)", result);
    }

    /// <summary>A panel at the window's top-left holding <paramref name="children"/>, each left-aligned at its own size.</summary>
    private static StackPanel Column(params Control[] children)
    {
        var panel = new StackPanel { Name = "Column" };
        foreach (Control child in children)
        {
            child.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(child);
        }

        return panel;
    }

    /// <summary>The id of the one element under the first window whose automation id (or x:Name) is <paramref name="automationId"/>.</summary>
    private static async Task<string> FindIdAsync(Tools tools, string automationId)
    {
        string windowId = IdOf(await tools.Inspect.ListWindowsAsync(Pid, CancellationToken.None));
        return IdOf(await tools.Inspect.FindElementsAsync(windowId, CancellationToken.None, "", automationId, ""));
    }

    private static string IdOf(string row) => row.TrimStart().Split(" | ")[0];

    private static Tools NewTools(PipeDirectory directory)
    {
        var registry = new ElementRegistry(NullLogger<ElementRegistry>.Instance);
        return new Tools(
            new InspectTools(registry, directory, NullLogger<InspectTools>.Instance),
            new InputTools(registry, directory, NullLogger<InputTools>.Instance)
        );
    }

    private PipeDirectory NewPipeDirectory() =>
        new(DiscoveryDirectory, Process.GetCurrentProcess().ProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);

    /// <summary>The inspect and input tools over one registry and one pipe directory, as the server wires them.</summary>
    private sealed record Tools(InspectTools Inspect, InputTools Input);
}
