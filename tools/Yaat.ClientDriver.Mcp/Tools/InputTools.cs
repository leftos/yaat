using System.ComponentModel;
using System.Text;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Yaat.Client.Automation.Protocol;
using Yaat.ClientDriver.Mcp.Pipe;
using Yaat.ClientDriver.Mcp.Recording;
using Rect = System.Windows.Rect;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>
/// Acting on the screen. An id from a YAAT client's automation pipe is acted on over that pipe (<see cref="PipeInput"/>),
/// whatever the input mode. Anything else goes through UI Automation patterns where they work, and mouse and keyboard input
/// where they do not, one of two ways chosen server-wide by set_input_mode: virtual (the default) posts window messages to a
/// YAAT window and never touches the real cursor or the foreground; real sends SendInput and SendKeys after bringing the
/// window to the foreground.
/// </summary>
/// <param name="registry">Shared element registry; ids come from the inspect tools.</param>
/// <param name="pipes">Finds and caches the YAAT clients' automation pipes, and remembers the client last reached over one.</param>
/// <param name="session">The server's one recording, whose action log every pipe click, hover and drag is written to while it runs.</param>
/// <param name="logger">Server logger, writing to stderr.</param>
[McpServerToolType]
public sealed class InputTools(ElementRegistry registry, PipeDirectory pipes, RecordingSession session, ILogger<InputTools> logger)
{
    private const string SendKeysSpecialCharacters = "+^%~(){}[]";
    private const string SwitchToReal = "switch to real input with set_input_mode real";
    private const int FocusSettleMilliseconds = 250;

    [McpServerTool]
    [Description(
        "Chooses how click, click_point, send_keys and set_text's typing deliver input, for every later call. 'virtual' (the default) "
            + "posts window messages to the YAAT window: the real mouse pointer never moves, the foreground never changes, and a covered "
            + "or background window still gets the input — but it reaches YAAT windows only and cannot hold Ctrl, Alt or Shift. 'real' "
            + "moves the real cursor and brings the window to the foreground (SendInput, SendKeys): for recording a video, for CRC, and "
            + "for modifier keys. Calls on an id from a YAAT client's automation pipe ignore the mode. Returns the mode now in effect."
    )]
    public static string SetInputMode([Description("virtual or real.")] string mode)
    {
        NativeInput.Mode = mode.Trim().ToLowerInvariant() switch
        {
            "virtual" => InputMode.Virtual,
            "real" => InputMode.Real,
            _ => throw new McpException($"Unknown input mode '{mode}' — use virtual or real"),
        };
        return $"input mode: {ModeName(NativeInput.Mode)} (pipe-routed calls ignore it)";
    }

    [McpServerTool]
    [Description(
        "Invokes an element through its InvokePattern — the fast, focus-free path for buttons and other controls that support it. An id "
            + "from a YAAT client's automation pipe gets a plain left click over the pipe, which runs the control's own action."
    )]
    public async Task<string> InvokeAsync(
        [Description("Element id from find_elements, list_windows or dump_tree.")] string elementId,
        CancellationToken cancellationToken
    )
    {
        if (registry.Resolve(elementId) is PipeNodeRef node)
        {
            PipeAction invoked = await PipeInput
                .ClickAsync(pipes, new PipeElement(elementId, node), PipePointer.PlainLeft, "invoked", cancellationToken)
                .ConfigureAwait(false);
            return invoked.Text;
        }

        AutomationElement element = registry.ResolveUia(elementId);
        return UiaRouted(UiaQuery.Guarded(logger, "invoke", elementId, () => InvokeElement(element, elementId)));
    }

    [McpServerTool]
    [Description(
        "Clicks an element at the centre of its rectangle. Avalonia menus support neither InvokePattern nor ExpandCollapsePattern, so "
            + "this is the only way to open one — and the only way to pick an item from the popup it opens. The result ends with the "
            + "input mode used, (virtual) or (real); see set_input_mode. A virtual click reaches YAAT windows only and cannot carry modifiers. "
            + "An id from a YAAT client's automation pipe is clicked over the pipe in any mode, with any of ctrl, shift, alt and meta held; "
            + "the result names what ran (command, toggle, menu_item, pointer, …) and ends with (pipe)."
    )]
    public async Task<string> ClickAsync(
        [Description("Element id from find_elements, list_windows or dump_tree.")] string elementId,
        CancellationToken cancellationToken,
        [Description("Which button: left, right or middle.")] string button = "left",
        [Description("True to send two clicks in quick succession.")] bool doubleClick = false,
        [Description(
            "Keys held during the click: shift, ctrl or shift+ctrl, real input only; over a YAAT client's automation pipe, any of ctrl, "
                + "shift, alt and meta joined by '+'. Empty for none."
        )]
            string modifiers = ""
    )
    {
        if (registry.Resolve(elementId) is PipeNodeRef node)
        {
            var pointer = new PipePointer(button, modifiers, doubleClick);
            var target = new PipeElement(elementId, node);
            string described = await PipeInput.DescribeAsync(pipes, target, cancellationToken).ConfigureAwait(false);
            // Stamped after the describe round trip, so the logged instant is the click's own.
            DateTime sentUtc = session.UtcNow;
            PipeAction clicked = await PipeInput
                .ClickDescribedAsync(pipes, target, pointer, new PipeClickWording("clicked", described), cancellationToken)
                .ConfigureAwait(false);
            return await LoggedAsync(
                    clicked.Text,
                    RecordedAction.Click(node.Pid, sentUtc, clicked.Site, button, pointer.ClickCount),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        AutomationElement element = registry.ResolveUia(elementId);
        MouseButton mouseButton = ParseButton(button);
        KeyModifiers keyModifiers = ParseModifiers(modifiers);
        return UiaRouted(
            UiaQuery.Guarded(logger, "click", elementId, () => ClickElement(element, elementId, mouseButton, doubleClick, keyModifiers))
        );
    }

    [McpServerTool]
    [Description(
        "Clicks at a point, for surfaces UI Automation cannot see into — YAAT's radar and ground views, and CRC's OpenGL scopes, where a "
            + "track or datablock exists only as pixels. With windowElementId empty, x and y are absolute screen coordinates in physical "
            + "pixels: take a screenshot first and read them off the window's rectangle. In virtual mode that click goes to the topmost "
            + "YAAT window under the point, even a covered one; CRC needs real mode; the result ends with (virtual) or (real). With "
            + "windowElementId a window from a YAAT client's automation pipe (list_windows), x and y are DIPs from the top-left of that "
            + "window's client area, the click goes over the pipe in any mode to the element under the point, and the result ends with (pipe)."
    )]
    public async Task<string> ClickPointAsync(
        [Description("Screen X in physical pixels, or window X in DIPs from the client area's left edge when windowElementId is given.")] int x,
        [Description("Screen Y in physical pixels, or window Y in DIPs from the client area's top edge when windowElementId is given.")] int y,
        CancellationToken cancellationToken,
        [Description("Which button: left, right or middle.")] string button = "left",
        [Description("True to send two clicks in quick succession.")] bool doubleClick = false,
        [Description(
            "Keys held during the click: shift, ctrl or shift+ctrl, real input only; over a YAAT client's automation pipe, any of ctrl, "
                + "shift, alt and meta joined by '+'. Empty for none."
        )]
            string modifiers = "",
        [Description(
            "Element id of a window from a YAAT client's automation pipe, to click at window-relative DIPs over the pipe; empty to click "
                + "at screen coordinates."
        )]
            string windowElementId = ""
    )
    {
        if (string.IsNullOrEmpty(windowElementId))
        {
            return UiaRouted(ClickScreenPoint(x, y, button, doubleClick, modifiers));
        }

        if (registry.Resolve(windowElementId) is not PipeNodeRef window)
        {
            throw new McpException(
                $"windowElementId '{windowElementId}' must be a window from a YAAT client driven over its automation pipe; leave it empty "
                    + "to click at screen coordinates"
            );
        }

        var pointer = new PipePointer(button, modifiers, doubleClick);
        DateTime sentUtc = session.UtcNow;
        PipeAction clicked = await PipeInput
            .ClickPointAsync(pipes, new PipeElement(windowElementId, window), x, y, pointer, cancellationToken)
            .ConfigureAwait(false);
        return await LoggedAsync(clicked.Text, RecordedAction.Click(window.Pid, sentUtc, clicked.Site, button, pointer.ClickCount), cancellationToken)
            .ConfigureAwait(false);
    }

    [McpServerTool]
    [Description(
        "Pipe only. Moves the mouse onto a point of a YAAT window and rests there durationMs, so hover effects fire as for a real mouse "
            + "(PointerEntered, IsPointerOver, a menu item's route preview, a tooltip); the pointer stays there afterwards. With selector, "
            + "the point is the centre of the one element it matches (the pipe's selector syntax: #Name, a type name, A > B, :nth(N)), "
            + "searched across the client's windows; with selector empty, x and y are DIPs from the top-left of windowElementId's client "
            + "area. Raw mouse input through the window's own input path: the real cursor never moves and the foreground never changes. "
            + "While a recording of that client runs, the hover is logged to <clip>-actions.jsonl. The result names the element under the "
            + "point and ends with (pipe)."
    )]
    public async Task<string> HoverAsync(
        [Description(
            "Element id of a window from a YAAT client's automation pipe (list_windows): the client to drive and the window x and y are in."
        )]
            string windowElementId,
        [Description("Selector of the element to hover at the centre of; empty to hover at x, y.")] string selector,
        [Description("Window X in DIPs from the client area's left edge; ignored when selector is given.")] int x,
        [Description("Window Y in DIPs from the client area's top edge; ignored when selector is given.")] int y,
        [Description("How long the pointer rests on the point before the call answers, 0–10000 ms.")] int durationMs,
        CancellationToken cancellationToken
    )
    {
        PipeNodeRef window = RequirePipeWindow(windowElementId, "hover");
        DateTime sentUtc = session.UtcNow;
        (string text, HoverResult result) = await PipeInput
            .HoverAsync(pipes, new PipeElement(windowElementId, window), new PipeHover(selector, x, y, durationMs), cancellationToken)
            .ConfigureAwait(false);
        return await LoggedAsync(text, RecordedAction.Hover(window.Pid, sentUtc, result.Site, result.DurationMs), cancellationToken)
            .ConfigureAwait(false);
    }

    [McpServerTool]
    [Description(
        "Pipe only. Drags with a mouse button held in a YAAT window: presses at (fromX, fromY), moves to (toX, toY) in steps equal "
            + "moves 16 ms apart with the button held, rests holdMs, and releases there, so a control that captures the pointer on the "
            + "press (a list's drag handle, a data block) gets every move and its drag threshold is passed as by a real mouse. Both points "
            + "are DIPs from the top-left of windowElementId's client area and must be inside it. Raw mouse input through the window's own "
            + "input path: the real cursor never moves and the foreground never changes. While a recording of that client runs, the drag "
            + "is logged to <clip>-actions.jsonl. The result names the element pressed and ends with (pipe)."
    )]
    public async Task<string> DragAsync(
        [Description("Element id of a window from a YAAT client's automation pipe (list_windows).")] string windowElementId,
        [Description("Press X in DIPs from the client area's left edge.")] int fromX,
        [Description("Press Y in DIPs from the client area's top edge.")] int fromY,
        [Description("Release X in DIPs from the client area's left edge.")] int toX,
        [Description("Release Y in DIPs from the client area's top edge.")] int toY,
        [Description("The button held: left, right or middle.")] string button,
        [Description("How many equal moves take the pointer from the press to the release, 1–100.")] int steps,
        [Description("How long the button stays held at the release point before it is released, 0–10000 ms.")] int holdMs,
        CancellationToken cancellationToken
    )
    {
        PipeNodeRef window = RequirePipeWindow(windowElementId, "drag");
        DateTime sentUtc = session.UtcNow;
        var drag = new PipeDrag(fromX, fromY, toX, toY, button, steps, holdMs);
        (string text, DragResult result) = await PipeInput
            .DragAsync(pipes, new PipeElement(windowElementId, window), drag, cancellationToken)
            .ConfigureAwait(false);
        return await LoggedAsync(text, RecordedAction.Drag(window.Pid, sentUtc, result), cancellationToken).ConfigureAwait(false);
    }

    private PipeNodeRef RequirePipeWindow(string windowElementId, string tool) =>
        (registry.Resolve(windowElementId) as PipeNodeRef)
        ?? throw new McpException(
            $"{tool} drives a YAAT client over its automation pipe only: windowElementId '{windowElementId}' must be a window from that "
                + "client's list_windows"
        );

    /// <summary><paramref name="text"/>, after logging <paramref name="action"/>; a failed write is noted after the text.</summary>
    private async Task<string> LoggedAsync(string text, RecordedAction action, CancellationToken ct)
    {
        string? problem = await session.LogActionAsync(action, ct).ConfigureAwait(false);
        return (problem is null) ? text : $"{text}{Environment.NewLine}warning: {problem}";
    }

    [McpServerTool]
    [Description(
        "Replaces an element's text. Virtual mode (the default) focuses it with a posted click, clears it with End and one Backspace "
            + "per character it holds, and types the text as posted characters — never through ValuePattern, whose SetValue brings the "
            + "window to the foreground. Real mode uses a writable ValuePattern when there is one, otherwise select-all and keystrokes. "
            + "The result ends with (virtual) or (real). An id from a YAAT client's automation pipe has its TextBox's text written "
            + "directly over the pipe, with no keystrokes, in any mode; the result ends with (pipe)."
    )]
    public async Task<string> SetTextAsync(
        [Description("Element id of the text box or other value control.")] string elementId,
        [Description("The text to put in it. Typed verbatim; SendKeys special characters are escaped for you when the typing path is used.")]
            string text,
        CancellationToken cancellationToken
    )
    {
        if (registry.Resolve(elementId) is PipeNodeRef node)
        {
            return await PipeInput.SetTextAsync(pipes, new PipeElement(elementId, node), text, cancellationToken).ConfigureAwait(false);
        }

        AutomationElement element = registry.ResolveUia(elementId);
        return UiaRouted(UiaQuery.Guarded(logger, "set_text", elementId, () => WriteText(element, elementId, text)));
    }

    [McpServerTool]
    [Description(
        "Sends keystrokes in SendKeys syntax to the focused element: {ENTER}, {ESC}, {TAB}, {F4}; a literal + ^ % ~ ( ) { } [ ] must be "
            + "wrapped in braces. Virtual mode (the default) sends characters, braced literals, {ENTER} {ESC} {TAB} {BACKSPACE} {DEL} "
            + "{HOME} {END} the arrows and {F1}–{F12} to the YAAT window last targeted, or to focusElementId's window. Real mode also "
            + "sends modifiers — ^a for Ctrl+A, %{F4} for Alt+F4, +a for Shift+A. It cannot fill the native file dialog, which runs "
            + "outside the client; launch with launch_yaat and answer dialogs with queue_file_pick. The result ends with (virtual) or (real). "
            + "Over a YAAT client's automation pipe the keys, modifiers included, go in any mode: to focusElementId when it is an id "
            + "from the pipe, or, with focusElementId empty, to the focused element of the client the last pipe call reached, unless a UI "
            + "Automation call came since; the result ends with (pipe)."
    )]
    public async Task<string> SendKeysAsync(
        [Description("The keystrokes, in SendKeys syntax.")] string keys,
        CancellationToken cancellationToken,
        [Description(
            "Element id to focus first, or empty to type into whatever is focused now — or, after a call over a YAAT client's automation "
                + "pipe, the focused element of that client, unless a UI Automation call came since."
        )]
            string focusElementId = ""
    )
    {
        if (string.IsNullOrEmpty(focusElementId))
        {
            if (pipes.LastTargetPid is int pid)
            {
                return await PipeInput.SendKeysToFocusedAsync(pipes, pid, keys, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (registry.Resolve(focusElementId) is PipeNodeRef node)
        {
            return await PipeInput.SendKeysAsync(pipes, new PipeElement(focusElementId, node), keys, cancellationToken).ConfigureAwait(false);
        }

        return UiaRouted(SendUiaKeys(keys, focusElementId));
    }

    [McpServerTool]
    [Description(
        "Gives an element the keyboard focus, so a following send_keys goes to it: a posted click at its centre in virtual mode (YAAT "
            + "windows only), UI Automation's SetFocus — which brings its window to the foreground — in real mode. The result ends with "
            + "(virtual) or (real). An id from a YAAT client's automation pipe is focused over the pipe without activating its window, in "
            + "any mode; the result ends with (pipe)."
    )]
    public async Task<string> FocusAsync([Description("Element id to focus.")] string elementId, CancellationToken cancellationToken)
    {
        if (registry.Resolve(elementId) is PipeNodeRef node)
        {
            return await PipeInput.FocusAsync(pipes, new PipeElement(elementId, node), cancellationToken).ConfigureAwait(false);
        }

        AutomationElement element = registry.ResolveUia(elementId);
        return UiaRouted(UiaQuery.Guarded(logger, "focus", elementId, () => FocusAndRemember(element, elementId)));
    }

    private static string ModeName(InputMode mode) => mode == InputMode.Real ? "real" : "virtual";

    private static string ClickScreenPoint(int x, int y, string button, bool doubleClick, string modifiers)
    {
        MouseButton mouseButton = ParseButton(button);
        KeyModifiers keyModifiers = ParseModifiers(modifiers);
        string what = $"{ClickVerb(doubleClick)} {DescribeButton(mouseButton, keyModifiers)} at screen point ({x},{y})";
        lock (NativeInput.InputGate)
        {
            if (NativeInput.Mode == InputMode.Real)
            {
                NativeInput.Click(x, y, mouseButton, doubleClick, keyModifiers);
                return $"{what} (real)";
            }

            NativeInput.RefuseVirtualModifiers(keyModifiers);
            nint handle = NativeInput.ClientWindowAt(x, y);
            if (handle == 0)
            {
                throw new McpException(
                    $"No YAAT window is at screen point ({x},{y}); virtual input reaches only YAAT windows — for CRC, {SwitchToReal}"
                );
            }

            EnsureEnabled(handle, $"Screen point ({x},{y})");
            NativeInput.PostClick(handle, x, y, mouseButton, doubleClick);
            NativeInput.RememberTarget(handle);
            return $"{what} (virtual)";
        }
    }

    /// <summary>
    /// send_keys through virtual or real input, to <paramref name="focusElementId"/>'s UI Automation element or, when it is
    /// empty, to whatever has the focus.
    /// </summary>
    private string SendUiaKeys(string keys, string focusElementId)
    {
        lock (NativeInput.InputGate)
        {
            if (string.IsNullOrEmpty(focusElementId))
            {
                return NativeInput.Mode == InputMode.Real ? RealKeysToFocused(keys) : VirtualKeysToFocused(keys);
            }

            AutomationElement element = registry.ResolveUia(focusElementId);
            return UiaQuery.Guarded(
                logger,
                "send_keys",
                focusElementId,
                () =>
                    NativeInput.Mode == InputMode.Real
                        ? RealKeysToElement(element, focusElementId, keys)
                        : VirtualKeysToElement(element, focusElementId, keys)
            );
        }
    }

    /// <summary>
    /// A call's <paramref name="result"/> once it went through UI Automation or native input and succeeded: untargeted keys follow
    /// what was touched last, so the client remembered from the last pipe call is forgotten.
    /// </summary>
    private string UiaRouted(string result)
    {
        pipes.ForgetLastTarget();
        return result;
    }

    private static string ClickVerb(bool doubleClick) => doubleClick ? "double-clicked" : "clicked";

    private static string DescribeButton(MouseButton button, KeyModifiers modifiers)
    {
        string name = button.ToString().ToLowerInvariant();
        return modifiers switch
        {
            KeyModifiers.None => name,
            KeyModifiers.Shift => $"shift+{name}",
            KeyModifiers.Control => $"ctrl+{name}",
            _ => $"shift+ctrl+{name}",
        };
    }

    private static MouseButton ParseButton(string button) =>
        button.ToLowerInvariant() switch
        {
            "left" => MouseButton.Left,
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => throw new McpException($"Unknown button '{button}' — use left, right or middle"),
        };

    private static KeyModifiers ParseModifiers(string modifiers) =>
        modifiers.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "" => KeyModifiers.None,
            "shift" => KeyModifiers.Shift,
            "ctrl" => KeyModifiers.Control,
            "shift+ctrl" or "ctrl+shift" => KeyModifiers.Shift | KeyModifiers.Control,
            _ => throw new McpException($"Unknown modifiers '{modifiers}' — use shift, ctrl, shift+ctrl, or leave it empty"),
        };

    private static string Escape(string text)
    {
        StringBuilder escaped = new(text.Length);
        foreach (char character in text)
        {
            if (SendKeysSpecialCharacters.Contains(character))
            {
                escaped.Append('{').Append(character).Append('}');
            }
            else
            {
                escaped.Append(character);
            }
        }

        return escaped.ToString();
    }

    private static string InvokeElement(AutomationElement element, string elementId)
    {
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern) || (pattern is not InvokePattern invokePattern))
        {
            throw new McpException(
                $"Element '{elementId}' has no InvokePattern — Avalonia menus are the common case. Use click, which clicks its rectangle"
            );
        }

        invokePattern.Invoke();
        return $"invoked {UiaQuery.Describe(element, elementId)}";
    }

    private static (int X, int Y) CentreOf(AutomationElement element, string elementId)
    {
        Rect rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || double.IsInfinity(rect.X) || (rect.Width < 1) || (rect.Height < 1))
        {
            throw new McpException($"Element '{elementId}' has no rectangle on screen to click: it is offscreen, collapsed or scrolled away");
        }

        return ((int)Math.Round(rect.X + (rect.Width / 2)), (int)Math.Round(rect.Y + (rect.Height / 2)));
    }

    /// <summary>
    /// The element's UI Automation top-level window, for real input; an element UI Automation gives no window handle for falls back
    /// to the YAAT window under its centre.
    /// </summary>
    private static nint WindowOf(AutomationElement element, string elementId)
    {
        nint handle = UiaQuery.WindowHandle(UiaQuery.TopLevelWindowOf(element));
        if (handle != 0)
        {
            return handle;
        }

        (int x, int y) = CentreOf(element, elementId);
        return NativeInput.ClientWindowAt(x, y);
    }

    /// <summary>
    /// Where virtual input for an element goes: the topmost window of the element's own YAAT process under the element's centre.
    /// UI Automation's top-level window is not used — it files an Avalonia menu popup under the main window, and a click posted
    /// there would land on whatever the popup covers. Refuses elements of other processes, and windows a modal dialog disabled.
    /// </summary>
    private static VirtualTarget RequireVirtualTarget(AutomationElement element, string elementId)
    {
        int processId = element.Current.ProcessId;
        if (!NativeInput.IsClientProcess(processId))
        {
            throw new McpException(
                $"Element '{elementId}' belongs to '{NativeInput.ProcessName(processId)}'; virtual input reaches only YAAT windows — {SwitchToReal}"
            );
        }

        (int x, int y) = CentreOf(element, elementId);
        nint window = NativeInput.ProcessWindowAt(x, y, processId);
        if (window == 0)
        {
            throw new McpException(
                $"Element '{elementId}': no shown window of its process is under its centre ({x},{y}) — the window is minimised or hidden"
            );
        }

        EnsureEnabled(window, $"Element '{elementId}'");
        return new VirtualTarget(window, x, y);
    }

    /// <summary>Refuses a window, or the window it takes keyboard input through, that a modal dialog has disabled.</summary>
    private static void EnsureEnabled(nint window, string subject)
    {
        if (!NativeInput.IsEnabled(window) || !NativeInput.IsEnabled(NativeInput.ActivationTarget(window)))
        {
            throw new McpException($"{subject}: a modal dialog owns this window, so it takes no input — close the dialog first");
        }
    }

    /// <summary>The element's text now, from its ValuePattern or TextPattern; null when it exposes neither.</summary>
    private static string? CurrentValue(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern) && (valuePattern is ValuePattern value))
        {
            return value.Current.Value ?? string.Empty;
        }

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out object textPattern) && (textPattern is TextPattern textRange))
        {
            return textRange.DocumentRange.GetText(-1);
        }

        return null;
    }

    private string ClickElement(AutomationElement element, string elementId, MouseButton button, bool doubleClick, KeyModifiers modifiers)
    {
        lock (NativeInput.InputGate)
        {
            if (NativeInput.Mode == InputMode.Virtual)
            {
                NativeInput.RefuseVirtualModifiers(modifiers);
                VirtualTarget target = RequireVirtualTarget(element, elementId);
                NativeInput.PostClick(target.Window, target.X, target.Y, button, doubleClick);
                NativeInput.RememberTarget(target.Window);
                string posted = $"{ClickVerb(doubleClick)} {DescribeButton(button, modifiers)} at ({target.X},{target.Y})";
                return $"{posted} on {UiaQuery.Describe(element, elementId)} (virtual)";
            }

            return RealClickElement(element, elementId, button, doubleClick, modifiers);
        }
    }

    private static string RealClickElement(AutomationElement element, string elementId, MouseButton button, bool doubleClick, KeyModifiers modifiers)
    {
        AutomationElement window = UiaQuery.TopLevelWindowOf(element);
        nint handle = WindowOf(element, elementId);
        if (NativeInput.IsMinimised(handle))
        {
            throw new McpException($"The window '{window.Current.Name}' is minimised — restore it before clicking element '{elementId}'");
        }

        if (!NativeInput.TryForeground(handle))
        {
            throw new McpException(
                $"Not clicking '{elementId}': its window did not take the foreground ({NativeInput.DescribeForeground()}), so a real click "
                    + "would land on whatever covers it. Close or minimise the window that holds it, or use set_input_mode virtual"
            );
        }

        Thread.Sleep(120);
        (int x, int y) = CentreOf(element, elementId);
        NativeInput.Click(x, y, button, doubleClick, modifiers);
        NativeInput.RememberTarget(handle);
        string clicked = $"{ClickVerb(doubleClick)} {DescribeButton(button, modifiers)} at ({x},{y})";
        return $"{clicked} on {UiaQuery.Describe(element, elementId)} (real)";
    }

    private static string VirtualKeysToFocused(string keys)
    {
        nint target = NativeInput.LastTarget;
        if (target == 0)
        {
            throw new McpException(
                $"Not sending '{keys}': no YAAT window has been targeted yet to send virtual keys to. Pass focusElementId, or click or focus "
                    + "an element of the window first"
            );
        }

        EnsureEnabled(target, "The YAAT window last targeted");
        NativeInput.PostKeys(target, keys);
        return $"sent '{keys}' to the focused element of the YAAT window last targeted (virtual)";
    }

    private static string RealKeysToFocused(string keys)
    {
        if (!NativeInput.ForegroundIsDrivenApplication())
        {
            throw new McpException(
                $"Not typing '{keys}': {NativeInput.DescribeForeground()}, not YAAT or CRC, so real keys would go to it. Pass focusElementId, "
                    + "or click an element of the target window first"
            );
        }

        System.Windows.Forms.SendKeys.SendWait(keys);
        return $"sent '{keys}' to the focused element (real)";
    }

    private static string VirtualKeysToElement(AutomationElement element, string elementId, string keys)
    {
        NativeInput.EnsurePostable(keys);
        VirtualTarget focus = RequireVirtualTarget(element, elementId);
        string focusedBy = FocusVirtually(focus);
        nint target = NativeInput.ActivationTarget(focus.Window);
        NativeInput.PostKeys(target, keys);
        NativeInput.RememberTarget(target);
        return $"sent '{keys}' to {UiaQuery.Describe(element, elementId)}, {focusedBy} (virtual)";
    }

    private string RealKeysToElement(AutomationElement element, string elementId, string keys)
    {
        string target = FocusForRealTyping(element, elementId, keys);
        System.Windows.Forms.SendKeys.SendWait(keys);
        return $"sent '{keys}' to {target} (real)";
    }

    /// <summary>
    /// Virtual mode always types, because UI Automation's ValuePattern.SetValue brings an Avalonia window to the foreground (seen
    /// live on the client's command box). Real mode keeps ValuePattern as the first path.
    /// </summary>
    private string WriteText(AutomationElement element, string elementId, string text)
    {
        lock (NativeInput.InputGate)
        {
            if (NativeInput.Mode == InputMode.Virtual)
            {
                return VirtualTypeText(element, elementId, text);
            }

            if (
                element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern)
                && (pattern is ValuePattern valuePattern)
                && !valuePattern.Current.IsReadOnly
            )
            {
                valuePattern.SetValue(text);
                return $"set '{text}' through ValuePattern on {UiaQuery.Describe(element, elementId)} (real)";
            }

            return RealTypeText(element, elementId, text);
        }
    }

    private string RealTypeText(AutomationElement element, string elementId, string text)
    {
        string target = FocusForRealTyping(element, elementId, text);
        Thread.Sleep(80);
        System.Windows.Forms.SendKeys.SendWait("^a");
        if (text.Length == 0)
        {
            System.Windows.Forms.SendKeys.SendWait("{DEL}");
            return $"cleared (select-all then delete, no writable ValuePattern) {target} (real)";
        }

        System.Windows.Forms.SendKeys.SendWait(Escape(text));
        return $"typed '{text}' after select-all (no writable ValuePattern) into {target} (real)";
    }

    private static string VirtualTypeText(AutomationElement element, string elementId, string text)
    {
        NativeInput.EnsureTypeable(text);
        VirtualTarget focus = RequireVirtualTarget(element, elementId);
        string current =
            CurrentValue(element)
            ?? throw new McpException(
                $"set_text on '{elementId}': the element exposes neither ValuePattern nor TextPattern, so virtual input cannot tell how much "
                    + $"to clear — {SwitchToReal}"
            );
        if (current.Contains('\r') || current.Contains('\n'))
        {
            throw new McpException(
                $"set_text on '{elementId}': its current text spans several lines, and End plus Backspaces clears only the caret's line — "
                    + SwitchToReal
            );
        }

        int length = current.Length;
        string focusedBy = FocusVirtually(focus);
        nint target = NativeInput.ActivationTarget(focus.Window);
        NativeInput.PostClear(target, length);
        NativeInput.PostText(target, text);
        NativeInput.RememberTarget(target);
        string what = $"typed '{text}' after End and {length} Backspaces into {UiaQuery.Describe(element, elementId)}";
        return $"{what}, {focusedBy} (virtual)";
    }

    /// <summary>
    /// Focuses an element for real keys and proves its window holds the foreground, refusing rather than typing into another
    /// application's window. Returns the element's description.
    /// </summary>
    private string FocusForRealTyping(AutomationElement element, string elementId, string keys)
    {
        nint handle = WindowOf(element, elementId);
        FocusElement(element, elementId);
        if (!NativeInput.TryForeground(handle))
        {
            throw new McpException(
                $"Not typing '{keys}' into '{elementId}': its window did not take the foreground ({NativeInput.DescribeForeground()}), so real "
                    + "keys would go to another application. Close or minimise the window that holds it, or use set_input_mode virtual"
            );
        }

        NativeInput.RememberTarget(handle);
        return UiaQuery.Describe(element, elementId);
    }

    /// <summary>
    /// Focuses an element with a posted click at its centre. UI Automation's SetFocus is not used: it brings the window to the
    /// foreground whenever Windows allows it, which virtual input must never do.
    /// </summary>
    private static string FocusVirtually(VirtualTarget target)
    {
        NativeInput.PostClick(target.Window, target.X, target.Y, MouseButton.Left, doubleClick: false);

        // Keys posted straight behind the click were seen to act before the click placed the caret (set_text typed ahead of the old
        // text instead of replacing it); a pause lets the click settle first.
        Thread.Sleep(FocusSettleMilliseconds);
        return $"focused with a posted click at ({target.X},{target.Y})";
    }

    private string FocusAndRemember(AutomationElement element, string elementId)
    {
        lock (NativeInput.InputGate)
        {
            if (NativeInput.Mode == InputMode.Virtual)
            {
                VirtualTarget target = RequireVirtualTarget(element, elementId);
                string focusedBy = FocusVirtually(target);
                NativeInput.RememberTarget(target.Window);
                return $"focused {UiaQuery.Describe(element, elementId)}, {focusedBy} (virtual)";
            }

            FocusElement(element, elementId);
            NativeInput.RememberTarget(WindowOf(element, elementId));
            return $"focused {UiaQuery.Describe(element, elementId)} (real)";
        }
    }

    private void FocusElement(AutomationElement element, string elementId)
    {
        try
        {
            element.SetFocus();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "SetFocus was refused for {ElementId}", elementId);
            throw new McpException(
                $"Windows refused focus for element '{elementId}' — its window may be minimised, or another process holds the foreground. "
                    + "Click the window first"
            );
        }
    }

    /// <summary>A window virtual input is posted to, and the screen point on it the input aims at.</summary>
    private readonly record struct VirtualTarget(nint Window, int X, int Y);
}
