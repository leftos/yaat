using System.Globalization;
using Yaat.Client.Automation.Protocol;

namespace Yaat.ClientDriver.Mcp.Pipe;

/// <summary>A pipe input's result text, and where it landed, for the action log.</summary>
public sealed record PipeAction(string Text, PointerSite Site);

/// <summary>How a pipe click's result opens, <c>clicked</c> or <c>invoked</c>, and the element's <c>dump_tree</c> row it names.</summary>
public sealed record PipeClickWording(string Verb, string Described);

/// <summary>A pipe hover: <see cref="Selector"/> names the element, or, empty, <see cref="X"/> and <see cref="Y"/> are window DIPs.</summary>
public sealed record PipeHover(string Selector, int X, int Y, int DurationMs);

/// <summary>A pipe drag in one window: the press and release points in its DIPs, the button, the moves and the rest before the release.</summary>
public sealed record PipeDrag(int FromX, int FromY, int ToX, int ToY, string Button, int Steps, int HoldMs);

/// <summary>
/// The input tools' work on a YAAT client over its automation pipe: each method builds the host method's params, sends it
/// through <see cref="PipeCalls"/> (so failures read as they do for the inspect tools) and words the result, ending in
/// <c>(pipe)</c>. An element is described by its <c>dump_tree</c> row, read before the input acts, so a control the input
/// removes is still named.
/// </summary>
public static class PipeInput
{
    /// <summary>Clicks <paramref name="element"/>; <paramref name="verb"/> is how the result opens (<c>clicked</c>, <c>invoked</c>).</summary>
    public static async Task<PipeAction> ClickAsync(PipeDirectory pipes, PipeElement element, PipePointer pointer, string verb, CancellationToken ct)
    {
        string described = await DescribeAsync(pipes, element, ct).ConfigureAwait(false);
        return await ClickDescribedAsync(pipes, element, pointer, new PipeClickWording(verb, described), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clicks <paramref name="element"/>, already described by <see cref="DescribeAsync"/>, so a caller can stamp the click
    /// after the describe round trip; the result opens with the wording's verb and names the element as it describes it.
    /// </summary>
    public static async Task<PipeAction> ClickDescribedAsync(
        PipeDirectory pipes,
        PipeElement element,
        PipePointer pointer,
        PipeClickWording wording,
        CancellationToken ct
    )
    {
        object parameters = new
        {
            nodeId = element.Node.NodeId,
            button = pointer.Button,
            modifiers = pointer.Modifiers,
            clickCount = pointer.ClickCount,
        };
        ClickResult result = await PipeCalls
            .SendForElementAsync<ClickResult>(pipes, element, ProtocolMethods.Click, parameters, ct)
            .ConfigureAwait(false);
        return new PipeAction($"{wording.Verb} ({result.Action}) on {wording.Described} (pipe)", result.Site);
    }

    /// <summary>Clicks at (<paramref name="x"/>, <paramref name="y"/>) DIPs from the top-left of <paramref name="window"/>'s client area.</summary>
    public static async Task<PipeAction> ClickPointAsync(
        PipeDirectory pipes,
        PipeElement window,
        int x,
        int y,
        PipePointer pointer,
        CancellationToken ct
    )
    {
        object parameters = new
        {
            windowNodeId = window.Node.NodeId,
            x,
            y,
            button = pointer.Button,
            modifiers = pointer.Modifiers,
            clickCount = pointer.ClickCount,
        };
        ClickPointResult result = await PipeCalls
            .SendForElementAsync<ClickPointResult>(pipes, window, ProtocolMethods.ClickPoint, parameters, ct)
            .ConfigureAwait(false);
        string verb = (pointer.ClickCount == 2) ? "double-clicked" : "clicked";
        return new PipeAction($"{verb} {pointer.Describe()} at window point ({x},{y}) on {result.ElementType} (pipe)", result.Site);
    }

    /// <summary>
    /// Hovers the centre of the one element <paramref name="selector"/> matches, or, with it empty, (<paramref name="x"/>,
    /// <paramref name="y"/>) DIPs in <paramref name="window"/>, which also routes the call to its client.
    /// </summary>
    public static async Task<(string Text, HoverResult Result)> HoverAsync(
        PipeDirectory pipes,
        PipeElement window,
        PipeHover hover,
        CancellationToken ct
    )
    {
        object parameters =
            (hover.Selector.Length > 0)
                ? new { selector = hover.Selector, durationMs = hover.DurationMs }
                : new
                {
                    windowNodeId = window.Node.NodeId,
                    x = hover.X,
                    y = hover.Y,
                    durationMs = hover.DurationMs,
                };
        HoverResult result = await PipeCalls
            .SendForElementAsync<HoverResult>(pipes, window, ProtocolMethods.Hover, parameters, ct)
            .ConfigureAwait(false);
        PointerSite site = result.Site;
        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"hovered {result.ElementType} at window point ({site.X},{site.Y}) in '{site.Window}' for {result.DurationMs} ms (pipe)"
        );
        return (text, result);
    }

    /// <summary>Drags in <paramref name="window"/> as <paramref name="drag"/> describes.</summary>
    public static async Task<(string Text, DragResult Result)> DragAsync(PipeDirectory pipes, PipeElement window, PipeDrag drag, CancellationToken ct)
    {
        object parameters = new
        {
            windowNodeId = window.Node.NodeId,
            fromX = drag.FromX,
            fromY = drag.FromY,
            toX = drag.ToX,
            toY = drag.ToY,
            button = drag.Button,
            steps = drag.Steps,
            holdMs = drag.HoldMs,
        };
        DragResult result = await PipeCalls
            .SendForElementAsync<DragResult>(pipes, window, ProtocolMethods.Drag, parameters, ct)
            .ConfigureAwait(false);
        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"dragged {result.Button} from window point ({drag.FromX},{drag.FromY}) to ({drag.ToX},{drag.ToY}) in {result.Steps} steps, "
                + $"held {result.HoldMs} ms, {result.DurationMs} ms in all, pressed on {result.ElementType} (pipe)"
        );
        return (text, result);
    }

    /// <summary>Writes <paramref name="text"/> into the text box <paramref name="element"/> is or holds, without keystrokes.</summary>
    public static async Task<string> SetTextAsync(PipeDirectory pipes, PipeElement element, string text, CancellationToken ct)
    {
        string described = await DescribeAsync(pipes, element, ct).ConfigureAwait(false);
        object parameters = new { nodeId = element.Node.NodeId, text };
        await PipeCalls.SendForElementAsync<SetTextResult>(pipes, element, ProtocolMethods.SetText, parameters, ct).ConfigureAwait(false);
        return $"set '{text}' on {described} (pipe)";
    }

    /// <summary>Focuses <paramref name="element"/>, then sends <paramref name="keys"/> (SendKeys syntax) to it.</summary>
    public static async Task<string> SendKeysAsync(PipeDirectory pipes, PipeElement element, string keys, CancellationToken ct)
    {
        string described = await DescribeAsync(pipes, element, ct).ConfigureAwait(false);
        object parameters = new { nodeId = element.Node.NodeId, keys };
        SendKeysResult result = await PipeCalls
            .SendForElementAsync<SendKeysResult>(pipes, element, ProtocolMethods.SendKeys, parameters, ct)
            .ConfigureAwait(false);
        return $"sent '{keys}' to {described}, {Strokes(result.Strokes)} (pipe)";
    }

    /// <summary>Sends <paramref name="keys"/> to the focused element of <paramref name="pid"/>'s active window.</summary>
    public static async Task<string> SendKeysToFocusedAsync(PipeDirectory pipes, int pid, string keys, CancellationToken ct)
    {
        SendKeysResult result = await PipeCalls
            .SendForPidAsync<SendKeysResult>(pipes, pid, ProtocolMethods.SendKeys, new { keys }, PipeClient.RequestTimeout, ct)
            .ConfigureAwait(false);
        return $"sent '{keys}' to the focused element, {Strokes(result.Strokes)} (pipe)";
    }

    /// <summary>Gives <paramref name="element"/> the keyboard focus.</summary>
    public static async Task<string> FocusAsync(PipeDirectory pipes, PipeElement element, CancellationToken ct)
    {
        string described = await DescribeAsync(pipes, element, ct).ConfigureAwait(false);
        await PipeCalls
            .SendForElementAsync<FocusResult>(pipes, element, ProtocolMethods.Focus, new { nodeId = element.Node.NodeId }, ct)
            .ConfigureAwait(false);
        return $"focused {described} (pipe)";
    }

    /// <summary>The element's <c>dump_tree</c> row, from a <c>get_tree</c> of the node alone.</summary>
    public static async Task<string> DescribeAsync(PipeDirectory pipes, PipeElement element, CancellationToken ct)
    {
        NodeInfo node = await PipeCalls.GetNodeAsync(pipes, element, new { nodeId = element.Node.NodeId, depth = 0 }, ct).ConfigureAwait(false);
        return PipeDescribe.Node(node, element.Id);
    }

    /// <summary>The stroke count as a result words it: <c>1 stroke</c>, <c>4 strokes</c>.</summary>
    private static string Strokes(int count) => (count == 1) ? "1 stroke" : $"{count} strokes";
}

/// <summary>
/// A pipe click's button and modifiers as the tool was given them (the host validates both), and whether it is a double click.
/// </summary>
/// <param name="Button">left, right or middle.</param>
/// <param name="Modifiers">Keys joined by '+', each ctrl, shift, alt or meta; empty for none.</param>
/// <param name="DoubleClick">True for two clicks.</param>
public sealed record PipePointer(string Button, string Modifiers, bool DoubleClick)
{
    /// <summary>A plain left click, which runs an element's own action.</summary>
    public static PipePointer PlainLeft { get; } = new("left", "", false);

    /// <summary>The host's <c>clickCount</c>: 1, or 2 for a double click.</summary>
    public int ClickCount => DoubleClick ? 2 : 1;

    /// <summary>The button as a result names it, behind the modifiers held: <c>left</c>, <c>ctrl+shift+left</c>.</summary>
    public string Describe()
    {
        string button = Button.ToLowerInvariant();
        string modifiers = Modifiers.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return (modifiers.Length == 0) ? button : $"{modifiers}+{button}";
    }
}
