using System.ComponentModel;
using System.Globalization;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Yaat.Client.Automation.Protocol;
using Yaat.ClientDriver.Mcp.Pipe;
using Rect = System.Windows.Rect;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>
/// Reading the screen: a YAAT client's own automation tree over its pipe where it has one, the UI Automation tree where
/// it is exposed, and pixels where neither is.
/// </summary>
/// <param name="registry">Shared element registry; ids handed out here resolve in the input tools.</param>
/// <param name="pipes">Finds and caches the YAAT clients' automation pipes.</param>
/// <param name="logger">Server logger, writing to stderr.</param>
[McpServerToolType]
public sealed class InspectTools(ElementRegistry registry, PipeDirectory pipes, ILogger<InspectTools> logger)
{
    private const int MaxRows = 50;
    private const int MaxTreeLines = 400;
    private const int PipeFindDepth = 50;
    private const string ShotDirectory = ".tmp/client-driver/shots";

    [McpServerTool]
    [Description(
        "Lists every top-level window of a process, with the element id each later tool takes. A YAAT client with an automation pipe "
            + "answers over the pipe, its open overlay popups included, with Avalonia type names and window-relative rectangles in DIPs; "
            + "any other process answers through UI Automation."
    )]
    public async Task<string> ListWindowsAsync(
        [Description("The process id, as returned by launch_yaat or list_processes.")] int pid,
        CancellationToken cancellationToken
    )
    {
        PipeClient? client = await PipeCalls.TryRouteAsync(pipes, pid, cancellationToken).ConfigureAwait(false);
        List<WindowInfo>? pipeWindows = (client is null) ? null : await TryListPipeWindowsAsync(client, pid, cancellationToken).ConfigureAwait(false);
        if (pipeWindows is not null)
        {
            if (pipeWindows.Count == 0)
            {
                return NoWindowsMessage(pid);
            }

            return string.Join(Environment.NewLine, pipeWindows.Select(window => PipeDescribe.Window(window, registry.Register(pid, window.NodeId))));
        }

        List<AutomationElement> windows = UiaRouted(UiaQuery.Guarded(logger, "list_windows", $"pid {pid}", () => UiaQuery.TopLevelWindows(pid)));
        if (windows.Count == 0)
        {
            return NoWindowsMessage(pid);
        }

        return string.Join(Environment.NewLine, windows.Select(window => UiaQuery.Describe(window, registry.Register(window))));
    }

    [McpServerTool]
    [Description(
        "Walks the tree under an element, one indented line per node, and registers an id for each: a YAAT client's visual tree when "
            + "the id came from its automation pipe, otherwise the UI Automation control view. CRC's scopes are an OpenGL surface: its "
            + "windows, menus and dialogs appear here, but tracks and datablocks never do — use screenshot for those."
    )]
    public async Task<string> DumpTreeAsync(
        [Description("Element id from list_windows, find_elements or an earlier dump_tree.")] string elementId,
        CancellationToken cancellationToken,
        [Description("How many levels below the element to walk.")] int maxDepth = 4
    )
    {
        if (registry.Resolve(elementId) is PipeNodeRef node)
        {
            return await DumpPipeTreeAsync(new PipeElement(elementId, node), maxDepth, cancellationToken).ConfigureAwait(false);
        }

        AutomationElement element = registry.ResolveUia(elementId);
        return UiaRouted(UiaQuery.Guarded(logger, "dump_tree", elementId, () => UiaQuery.DumpTree(element, maxDepth, MaxTreeLines, registry)));
    }

    [McpServerTool]
    [Description(
        "Finds descendants of an element matching every criterion given (exact matches, ANDed). Through UI Automation, Avalonia maps "
            + "x:Name to AutomationId, so the client's CommandInput, ConnectMenuItem and friends are found by automationId; other controls "
            + "by name. Under a root from a YAAT client's automation pipe the search goes 50 levels deep: name matches the control's Name "
            + "or its text, automationId matches its AutomationProperties.AutomationId or else its x:Name (the id= a dump_tree row shows), "
            + "and controlType is the Avalonia type name (TextBox, Button, MenuItem)."
    )]
    public async Task<string> FindElementsAsync(
        [Description("Element id to search under, from list_windows or an earlier find_elements/dump_tree.")] string rootElementId,
        CancellationToken cancellationToken,
        [Description("Exact Name, or empty to ignore the name.")] string name = "",
        [Description("Exact AutomationId (x:Name when the control sets none), or empty to ignore it.")] string automationId = "",
        [Description(
            "ControlType suffix such as Button, MenuItem, Edit or Window (UI Automation), or the Avalonia type name such as TextBox "
                + "(automation pipe), or empty to ignore the type."
        )]
            string controlType = ""
    )
    {
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(automationId) && string.IsNullOrEmpty(controlType))
        {
            throw new McpException("Give at least one of name, automationId or controlType — an unfiltered search walks the whole window");
        }

        if (registry.Resolve(rootElementId) is PipeNodeRef node)
        {
            var criteria = new PipeCriteria(name, automationId, controlType);
            return await FindPipeElementsAsync(new PipeElement(rootElementId, node), criteria, cancellationToken).ConfigureAwait(false);
        }

        AutomationElement root = registry.ResolveUia(rootElementId);
        List<AutomationElement> matches = UiaQuery.Guarded(
            logger,
            "find_elements",
            rootElementId,
            () => UiaQuery.FindDescendants(root, name, automationId, controlType, MaxRows)
        );
        return UiaRouted(FormatMatches([.. matches.Select(match => UiaQuery.Describe(match, registry.Register(match)))]));
    }

    [McpServerTool]
    [Description(
        "Returns a PNG of an element's own rectangle. In virtual input mode (the default, see set_input_mode) the window renders itself "
            + "into the image (PrintWindow), so it captures correctly even when covered and is never brought to the front. In real mode "
            + "the window is brought to the foreground and copied off the screen, and the result says when it could not be. This is the "
            + "only way to read surfaces UI Automation cannot see into, such as CRC's radar scopes. An id from a YAAT client's automation "
            + "pipe is rendered by the client itself over the pipe, in any input mode and whatever covers it: a window's client area or "
            + "one element's bounds, at the client's render scale; a hidden element is refused, and the result says (pipe)."
    )]
    public async Task<IEnumerable<ContentBlock>> ScreenshotAsync(
        [Description("Element id of the window (or any element) to capture.")] string windowElementId,
        CancellationToken cancellationToken,
        [Description("Widest the returned image may be, in pixels; wider captures are downscaled.")] int maxWidth = 1280
    )
    {
        CaptureResult capture;
        if (registry.Resolve(windowElementId) is PipeNodeRef node)
        {
            capture = await CapturePipeElementAsync(new PipeElement(windowElementId, node), maxWidth, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            AutomationElement element = registry.ResolveUia(windowElementId);
            capture = UiaRouted(UiaQuery.Guarded(logger, "screenshot", windowElementId, () => CaptureElement(element, maxWidth)));
        }

        string captured = $"{capture.Path} — captured {capture.SourceWidth}x{capture.SourceHeight} from {capture.Source}";
        string summary = $"{captured}, returned {capture.Width}x{capture.Height} ({capture.Png.Length} bytes)";
        return [new TextContentBlock { Text = summary }, ImageContentBlock.FromBytes(capture.Png, "image/png")];
    }

    [McpServerTool]
    [Description(
        "Reads an element's current value — the text of a TextBox, the content of a document — to confirm what set_text or "
            + "send_keys actually did."
    )]
    public async Task<string> GetValueAsync([Description("Element id whose value to read.")] string elementId, CancellationToken cancellationToken)
    {
        if (registry.Resolve(elementId) is PipeNodeRef node)
        {
            NodeInfo info = await PipeCalls
                .GetNodeAsync(pipes, new PipeElement(elementId, node), new { nodeId = node.NodeId, depth = 0 }, cancellationToken)
                .ConfigureAwait(false);
            // A text box's own text, empty when it has none, never the automation name its Text falls back to.
            return info.Value
                ?? info.Text
                ?? throw new McpException($"Element '{elementId}' ({info.Type}) has no readable text; use dump_tree or screenshot to inspect it.");
        }

        AutomationElement element = registry.ResolveUia(elementId);
        return UiaRouted(UiaQuery.Guarded(logger, "get_value", elementId, () => ReadValue(element)));
    }

    /// <summary>
    /// A UI Automation call's <paramref name="result"/>, once it succeeded: the remembered pipe pid no longer names what was
    /// touched last.
    /// </summary>
    private T UiaRouted<T>(T result)
    {
        pipes.ForgetLastTarget();
        return result;
    }

    private static string NoWindowsMessage(int pid) => $"No top-level windows for pid {pid} — it may still be starting, or it has none.";

    private static string FormatMatches(List<string> rows)
    {
        if (rows.Count == 0)
        {
            return "No match. Widen the criteria, or dump_tree the root to see what is actually there.";
        }

        string note = rows.Count >= MaxRows ? $"{Environment.NewLine}… stopped at {MaxRows} matches — narrow the criteria" : string.Empty;
        return string.Join(Environment.NewLine, rows) + note;
    }

    /// <summary>The client's windows over its pipe, or null when another caller disposed the cached client and UI Automation must answer.</summary>
    private async Task<List<WindowInfo>?> TryListPipeWindowsAsync(PipeClient client, int pid, CancellationToken cancellationToken)
    {
        try
        {
            List<WindowInfo> windows = await client
                .SendAsync<List<WindowInfo>>(ProtocolMethods.ListWindows, null, PipeClient.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            pipes.RememberTarget(pid);
            return windows;
        }
        catch (ObjectDisposedException ex)
        {
            logger.LogDebug(ex, "The automation pipe client for pid {Pid} was disposed mid-call; listing its windows through UI Automation", pid);
            return null;
        }
        catch (PipeRemoteException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private async Task<string> DumpPipeTreeAsync(PipeElement element, int maxDepth, CancellationToken cancellationToken)
    {
        object parameters = new
        {
            nodeId = element.Node.NodeId,
            treeKind = "Visual",
            depth = maxDepth,
        };
        NodeInfo root = await PipeCalls.GetNodeAsync(pipes, element, parameters, cancellationToken).ConfigureAwait(false);
        List<string> lines = [];
        if (AppendPipeLines(root, 0, element.Node.Pid, lines))
        {
            lines.Add(UiaQuery.TruncatedTreeLine(MaxTreeLines));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Appends <paramref name="node"/> and its subtree pre-order; true when the line cap stopped the walk with nodes left.</summary>
    private bool AppendPipeLines(NodeInfo node, int depth, int pid, List<string> lines)
    {
        if (lines.Count >= MaxTreeLines)
        {
            return true;
        }

        lines.Add(new string(' ', depth * 2) + PipeDescribe.Node(node, registry.Register(pid, node.NodeId)));
        foreach (NodeInfo child in node.Children ?? [])
        {
            if (AppendPipeLines(child, depth + 1, pid, lines))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> FindPipeElementsAsync(PipeElement element, PipeCriteria criteria, CancellationToken cancellationToken)
    {
        object parameters = new
        {
            nodeId = element.Node.NodeId,
            treeKind = "Visual",
            depth = PipeFindDepth,
        };
        NodeInfo root = await PipeCalls.GetNodeAsync(pipes, element, parameters, cancellationToken).ConfigureAwait(false);
        List<NodeInfo> matches = [];
        CollectPipeMatches(root.Children ?? [], criteria, matches);
        return FormatMatches([.. matches.Select(match => PipeDescribe.Node(match, registry.Register(element.Node.Pid, match.NodeId)))]);
    }

    /// <summary>Collects the matching nodes among <paramref name="nodes"/> and their descendants, pre-order, up to <see cref="MaxRows"/>.</summary>
    private static void CollectPipeMatches(List<NodeInfo> nodes, PipeCriteria criteria, List<NodeInfo> matches)
    {
        foreach (NodeInfo node in nodes)
        {
            if (matches.Count >= MaxRows)
            {
                return;
            }

            if (criteria.Matches(node))
            {
                matches.Add(node);
            }

            CollectPipeMatches(node.Children ?? [], criteria, matches);
        }
    }

    private static string ReadValue(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern) && (valuePattern is ValuePattern value))
        {
            return value.Current.Value ?? string.Empty;
        }

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out object textPattern) && (textPattern is TextPattern text))
        {
            return text.DocumentRange.GetText(4096);
        }

        throw new McpException("That element exposes neither ValuePattern nor TextPattern — read its Name, or take a screenshot");
    }

    /// <summary>
    /// Has the client render the element (always named by <c>nodeId</c>: a window's node renders its client area) and saves the
    /// PNG it sends to the shots folder, downscaled when wider than <paramref name="maxWidth"/>.
    /// </summary>
    private async Task<CaptureResult> CapturePipeElementAsync(PipeElement element, int maxWidth, CancellationToken cancellationToken)
    {
        ScreenshotResult shot = await PipeCalls
            .SendForElementAsync<ScreenshotResult>(
                pipes,
                element,
                ProtocolMethods.Screenshot,
                new { nodeId = element.Node.NodeId },
                cancellationToken
            )
            .ConfigureAwait(false);
        byte[] png;
        try
        {
            png = Convert.FromBase64String(shot.PngBase64);
        }
        catch (FormatException ex)
        {
            throw new McpException($"The client's screenshot of element '{element.Id}' is not valid base64: {ex.Message}", ex);
        }

        string source = $"the client's render at scale {shot.Scale.ToString(CultureInfo.InvariantCulture)} (pipe)";
        return WindowCapture.FromPng(new PipeShot(png, shot.Width, shot.Height), maxWidth, Path.GetFullPath(ShotDirectory), source);
    }

    private CaptureResult CaptureElement(AutomationElement element, int maxWidth)
    {
        AutomationElement window = UiaQuery.TopLevelWindowOf(element);
        nint handle = UiaQuery.WindowHandle(window);
        if (NativeInput.IsMinimised(handle))
        {
            throw new McpException($"The window '{window.Current.Name}' is minimised — restore it before taking a screenshot");
        }

        string directory = Path.GetFullPath(ShotDirectory);
        if (NativeInput.Mode == InputMode.Virtual)
        {
            return CaptureVirtually(element, handle, maxWidth, directory);
        }

        if (NativeInput.TryForeground(handle))
        {
            Thread.Sleep(150);
            return WindowCapture.Capture(element.Current.BoundingRectangle, maxWidth, directory);
        }

        string reason = NativeInput.DescribeForeground();
        logger.LogDebug("SetForegroundWindow was refused for '{Window}'; capturing whatever is on screen at its rectangle", window.Current.Name);
        CaptureResult capture = WindowCapture.Capture(element.Current.BoundingRectangle, maxWidth, directory);
        return capture with { Source = $"{capture.Source} — the window did not take the foreground ({reason}), so another window may cover it" };
    }

    /// <summary>
    /// Captures without touching the foreground. A YAAT element is rendered by the window really showing it — the topmost window of
    /// its process whose frame holds the whole element, so a menu item comes from its popup even though UI Automation files the
    /// popup under the main window. Anything else (CRC) is copied off the screen, since PrintWindow on CRC's OpenGL scopes is unverified.
    /// </summary>
    private static CaptureResult CaptureVirtually(AutomationElement element, nint handle, int maxWidth, string directory)
    {
        Rect rect = element.Current.BoundingRectangle;
        WindowCapture.EnsureArea(rect);
        int processId = element.Current.ProcessId;
        if (!NativeInput.IsClientProcess(processId))
        {
            CaptureResult copy = WindowCapture.Capture(rect, maxWidth, directory);
            return copy with
            {
                Source =
                    $"{copy.Source} — not a YAAT window, so it was copied off the screen without taking the foreground; "
                    + "another window may cover it",
            };
        }

        nint window = NativeInput.ProcessWindowCovering(rect, processId);
        if (window != 0)
        {
            return WindowCapture.CaptureWindow(window, rect, maxWidth, directory);
        }

        if (handle == 0)
        {
            throw new McpException(
                "UI Automation reports no window handle for this element's window, and no shown window of its process holds the element — "
                    + "it is hidden or scrolled out of view"
            );
        }

        return WindowCapture.CaptureWindow(handle, rect, maxWidth, directory);
    }

    /// <summary>
    /// The <c>find_elements</c> filters over pipe nodes, each ignored when empty and ANDed otherwise: <paramref name="Name"/>
    /// against the node's name or text, <paramref name="AutomationId"/> against its automation id or else its name (as UI
    /// Automation maps x:Name to AutomationId, so an id read from a row finds the element on both backends), and
    /// <paramref name="ControlType"/> against its Avalonia type name, all exact and ordinal.
    /// </summary>
    private sealed record PipeCriteria(string Name, string AutomationId, string ControlType)
    {
        public bool Matches(NodeInfo node) =>
            (
                string.IsNullOrEmpty(Name)
                || string.Equals(Name, node.Name, StringComparison.Ordinal)
                || string.Equals(Name, node.Text, StringComparison.Ordinal)
            )
            && (string.IsNullOrEmpty(AutomationId) || string.Equals(AutomationId, node.AutomationId ?? node.Name, StringComparison.Ordinal))
            && (string.IsNullOrEmpty(ControlType) || string.Equals(ControlType, node.Type, StringComparison.Ordinal));
    }
}
