using System.ComponentModel;
using System.Text;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Rect = System.Windows.Rect;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>
/// Acting on the screen: UI Automation patterns where they work, and mouse and keyboard input where they do not. Input goes one of
/// two ways, chosen server-wide by set_input_mode: virtual (the default) posts window messages to a YAAT window and never touches
/// the real cursor or the foreground; real sends SendInput and SendKeys after bringing the window to the foreground.
/// </summary>
/// <param name="registry">Shared element registry; ids come from the inspect tools.</param>
/// <param name="logger">Server logger, writing to stderr.</param>
[McpServerToolType]
public sealed class InputTools(ElementRegistry registry, ILogger<InputTools> logger)
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
            + "for modifier keys. Returns the mode now in effect."
    )]
    public static string SetInputMode([Description("virtual or real.")] string mode)
    {
        NativeInput.Mode = mode.Trim().ToLowerInvariant() switch
        {
            "virtual" => InputMode.Virtual,
            "real" => InputMode.Real,
            _ => throw new McpException($"Unknown input mode '{mode}' — use virtual or real"),
        };
        return $"input mode: {ModeName(NativeInput.Mode)}";
    }

    [McpServerTool]
    [Description("Invokes an element through its InvokePattern — the fast, focus-free path for buttons and other controls that support it.")]
    public string Invoke([Description("Element id from find_elements, list_windows or dump_tree.")] string elementId)
    {
        AutomationElement element = registry.Resolve(elementId);
        return UiaQuery.Guarded(logger, "invoke", elementId, () => InvokeElement(element, elementId));
    }

    [McpServerTool]
    [Description(
        "Clicks an element at the centre of its rectangle. Avalonia menus support neither InvokePattern nor ExpandCollapsePattern, so "
            + "this is the only way to open one — and the only way to pick an item from the popup it opens. The result ends with the "
            + "input mode used, (virtual) or (real); see set_input_mode. A virtual click reaches YAAT windows only and cannot carry modifiers."
    )]
    public string Click(
        [Description("Element id from find_elements, list_windows or dump_tree.")] string elementId,
        [Description("Which button: left, right or middle.")] string button = "left",
        [Description("True to send two clicks in quick succession.")] bool doubleClick = false,
        [Description("Keys held during the click: shift, ctrl or shift+ctrl; empty for none. Real input only.")] string modifiers = ""
    )
    {
        AutomationElement element = registry.Resolve(elementId);
        MouseButton mouseButton = ParseButton(button);
        KeyModifiers keyModifiers = ParseModifiers(modifiers);
        return UiaQuery.Guarded(logger, "click", elementId, () => ClickElement(element, elementId, mouseButton, doubleClick, keyModifiers));
    }

    [McpServerTool]
    [Description(
        "Clicks at absolute screen coordinates, for surfaces UI Automation cannot see into — YAAT's radar and ground views, and CRC's "
            + "OpenGL scopes, where a track or datablock exists only as pixels. Take a screenshot first and read the coordinates off the "
            + "window's rectangle. In virtual mode the click goes to the topmost YAAT window under the point, even a covered one; CRC "
            + "needs real mode. The result ends with (virtual) or (real)."
    )]
    public string ClickPoint(
        [Description("Screen X in physical pixels.")] int x,
        [Description("Screen Y in physical pixels.")] int y,
        [Description("Which button: left, right or middle.")] string button = "left",
        [Description("True to send two clicks in quick succession.")] bool doubleClick = false,
        [Description("Keys held during the click: shift, ctrl or shift+ctrl; empty for none. Real input only.")] string modifiers = ""
    )
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

    [McpServerTool]
    [Description(
        "Replaces an element's text. Virtual mode (the default) focuses it with a posted click, clears it with End and one Backspace "
            + "per character it holds, and types the text as posted characters — never through ValuePattern, whose SetValue brings the "
            + "window to the foreground. Real mode uses a writable ValuePattern when there is one, otherwise select-all and keystrokes. "
            + "The result ends with (virtual) or (real)."
    )]
    public string SetText(
        [Description("Element id of the text box or other value control.")] string elementId,
        [Description("The text to put in it. Typed verbatim; SendKeys special characters are escaped for you when the typing path is used.")]
            string text
    )
    {
        AutomationElement element = registry.Resolve(elementId);
        return UiaQuery.Guarded(logger, "set_text", elementId, () => WriteText(element, elementId, text));
    }

    [McpServerTool]
    [Description(
        "Sends keystrokes in SendKeys syntax to the focused element: {ENTER}, {ESC}, {TAB}, {F4}; a literal + ^ % ~ ( ) { } [ ] must be "
            + "wrapped in braces. Virtual mode (the default) sends characters, braced literals, {ENTER} {ESC} {TAB} {BACKSPACE} {DEL} "
            + "{HOME} {END} the arrows and {F1}–{F12} to the YAAT window last targeted, or to focusElementId's window. Real mode also "
            + "sends modifiers — ^a for Ctrl+A, %{F4} for Alt+F4, +a for Shift+A — and drives the native file dialog, which never "
            + "appears in a process's window list: send the full path followed by {ENTER}. The result ends with (virtual) or (real)."
    )]
    public string SendKeys(
        [Description("The keystrokes, in SendKeys syntax.")] string keys,
        [Description("Element id to focus first, or empty to type into whatever is focused now.")] string focusElementId = ""
    )
    {
        lock (NativeInput.InputGate)
        {
            if (string.IsNullOrEmpty(focusElementId))
            {
                return NativeInput.Mode == InputMode.Real ? RealKeysToFocused(keys) : VirtualKeysToFocused(keys);
            }

            AutomationElement element = registry.Resolve(focusElementId);
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

    [McpServerTool]
    [Description(
        "Gives an element the keyboard focus, so a following send_keys goes to it: a posted click at its centre in virtual mode (YAAT "
            + "windows only), UI Automation's SetFocus — which brings its window to the foreground — in real mode. The result ends with "
            + "(virtual) or (real)."
    )]
    public string Focus([Description("Element id to focus.")] string elementId)
    {
        AutomationElement element = registry.Resolve(elementId);
        return UiaQuery.Guarded(logger, "focus", elementId, () => FocusAndRemember(element, elementId));
    }

    private static string ModeName(InputMode mode) => mode == InputMode.Real ? "real" : "virtual";

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
