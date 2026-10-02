using Yaat.Client.Automation.Protocol;

namespace Yaat.ClientDriver.Mcp.Pipe;

/// <summary>
/// The input tools' work on a YAAT client over its automation pipe: each method builds the host method's params, sends it
/// through <see cref="PipeCalls"/> (so failures read as they do for the inspect tools) and words the result, ending in
/// <c>(pipe)</c>. An element is described by its <c>dump_tree</c> row, read before the input acts, so a control the input
/// removes is still named.
/// </summary>
public static class PipeInput
{
    /// <summary>Clicks <paramref name="element"/>; <paramref name="verb"/> is how the result opens (<c>clicked</c>, <c>invoked</c>).</summary>
    public static async Task<string> ClickAsync(PipeDirectory pipes, PipeElement element, PipePointer pointer, string verb, CancellationToken ct)
    {
        string described = await DescribeAsync(pipes, element, ct).ConfigureAwait(false);
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
        return $"{verb} ({result.Action}) on {described} (pipe)";
    }

    /// <summary>Clicks at (<paramref name="x"/>, <paramref name="y"/>) DIPs from the top-left of <paramref name="window"/>'s client area.</summary>
    public static async Task<string> ClickPointAsync(PipeDirectory pipes, PipeElement window, int x, int y, PipePointer pointer, CancellationToken ct)
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
        return $"{verb} {pointer.Describe()} at window point ({x},{y}) on {result.ElementType} (pipe)";
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
        return $"sent '{keys}' to {described} ({Strokes(result.Strokes)}, pipe)";
    }

    /// <summary>Sends <paramref name="keys"/> to the focused element of <paramref name="pid"/>'s active window.</summary>
    public static async Task<string> SendKeysToFocusedAsync(PipeDirectory pipes, int pid, string keys, CancellationToken ct)
    {
        SendKeysResult result = await PipeCalls
            .SendForPidAsync<SendKeysResult>(pipes, pid, ProtocolMethods.SendKeys, new { keys }, ct)
            .ConfigureAwait(false);
        return $"sent '{keys}' to the focused element ({Strokes(result.Strokes)}, pipe)";
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
    private static async Task<string> DescribeAsync(PipeDirectory pipes, PipeElement element, CancellationToken ct)
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
