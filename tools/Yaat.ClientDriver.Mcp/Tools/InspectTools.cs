using System.ComponentModel;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rect = System.Windows.Rect;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>Reading the screen: the UI Automation tree where it is exposed, and pixels where it is not.</summary>
/// <param name="registry">Shared element registry; ids handed out here resolve in the input tools.</param>
/// <param name="logger">Server logger, writing to stderr.</param>
[McpServerToolType]
public sealed class InspectTools(ElementRegistry registry, ILogger<InspectTools> logger)
{
    private const int MaxRows = 50;
    private const int MaxTreeLines = 400;
    private const string ShotDirectory = ".tmp/client-driver/shots";

    [McpServerTool]
    [Description("Lists every top-level UI Automation window of a process, with the element id each later tool takes.")]
    public string ListWindows([Description("The process id, as returned by launch_yaat or list_processes.")] int pid)
    {
        List<AutomationElement> windows = UiaQuery.Guarded(logger, "list_windows", $"pid {pid}", () => UiaQuery.TopLevelWindows(pid));
        if (windows.Count == 0)
        {
            return $"No top-level windows for pid {pid} — it may still be starting, or it has none.";
        }

        return string.Join(Environment.NewLine, windows.Select(window => UiaQuery.Describe(window, registry.Register(window))));
    }

    [McpServerTool]
    [Description(
        "Walks the control view under an element, one indented line per node, and registers an id for each. CRC's scopes are an OpenGL "
            + "surface: its windows, menus and dialogs appear here, but tracks and datablocks never do — use screenshot for those."
    )]
    public string DumpTree(
        [Description("Element id from list_windows, find_elements or an earlier dump_tree.")] string elementId,
        [Description("How many levels below the element to walk.")] int maxDepth = 4
    )
    {
        AutomationElement element = registry.Resolve(elementId);
        return UiaQuery.Guarded(logger, "dump_tree", elementId, () => UiaQuery.DumpTree(element, maxDepth, MaxTreeLines, registry));
    }

    [McpServerTool]
    [Description(
        "Finds descendants of an element matching every criterion given (exact matches, ANDed). Avalonia maps x:Name to AutomationId, "
            + "so the client's CommandInput, ConnectMenuItem and friends are found by automationId; other controls by name."
    )]
    public string FindElements(
        [Description("Element id to search under, from list_windows or an earlier find_elements/dump_tree.")] string rootElementId,
        [Description("Exact Name, or empty to ignore the name.")] string name = "",
        [Description("Exact AutomationId, or empty to ignore it.")] string automationId = "",
        [Description("ControlType suffix such as Button, MenuItem, Edit or Window, or empty to ignore the type.")] string controlType = ""
    )
    {
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(automationId) && string.IsNullOrEmpty(controlType))
        {
            throw new McpException("Give at least one of name, automationId or controlType — an unfiltered search walks the whole window");
        }

        AutomationElement root = registry.Resolve(rootElementId);
        List<AutomationElement> matches = UiaQuery.Guarded(
            logger,
            "find_elements",
            rootElementId,
            () => UiaQuery.FindDescendants(root, name, automationId, controlType, MaxRows)
        );
        if (matches.Count == 0)
        {
            return "No match. Widen the criteria, or dump_tree the root to see what is actually there.";
        }

        string rows = string.Join(Environment.NewLine, matches.Select(match => UiaQuery.Describe(match, registry.Register(match))));
        string note = matches.Count >= MaxRows ? $"{Environment.NewLine}… stopped at {MaxRows} matches — narrow the criteria" : string.Empty;
        return rows + note;
    }

    [McpServerTool]
    [Description(
        "Returns a PNG of an element's own rectangle. In virtual input mode (the default, see set_input_mode) the window renders itself "
            + "into the image (PrintWindow), so it captures correctly even when covered and is never brought to the front. In real mode "
            + "the window is brought to the foreground and copied off the screen, and the result says when it could not be. This is the "
            + "only way to read surfaces UI Automation cannot see into, such as CRC's radar scopes."
    )]
    public IEnumerable<ContentBlock> Screenshot(
        [Description("Element id of the window (or any element) to capture.")] string windowElementId,
        [Description("Widest the returned image may be, in pixels; wider captures are downscaled.")] int maxWidth = 1280
    )
    {
        AutomationElement element = registry.Resolve(windowElementId);
        CaptureResult capture = UiaQuery.Guarded(logger, "screenshot", windowElementId, () => CaptureElement(element, maxWidth));
        string captured = $"{capture.Path} — captured {capture.SourceWidth}x{capture.SourceHeight} from {capture.Source}";
        string summary = $"{captured}, returned {capture.Width}x{capture.Height} ({capture.Png.Length} bytes)";
        return [new TextContentBlock { Text = summary }, ImageContentBlock.FromBytes(capture.Png, "image/png")];
    }

    [McpServerTool]
    [Description(
        "Reads an element's current value — the text of a TextBox, the content of a document — to confirm what set_text or "
            + "send_keys actually did."
    )]
    public string GetValue([Description("Element id whose value to read.")] string elementId)
    {
        AutomationElement element = registry.Resolve(elementId);
        return UiaQuery.Guarded(logger, "get_value", elementId, () => ReadValue(element));
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
}
