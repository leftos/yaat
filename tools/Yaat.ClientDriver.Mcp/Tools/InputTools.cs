using System.ComponentModel;
using System.Text;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Rect = System.Windows.Rect;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>Acting on the screen: UI Automation patterns where they work, and real mouse and keyboard input where they do not.</summary>
/// <param name="registry">Shared element registry; ids come from the inspect tools.</param>
/// <param name="logger">Server logger, writing to stderr.</param>
[McpServerToolType]
public sealed class InputTools(ElementRegistry registry, ILogger<InputTools> logger)
{
    private const string SendKeysSpecialCharacters = "+^%~(){}[]";

    [McpServerTool]
    [Description("Invokes an element through its InvokePattern — the fast, focus-free path for buttons and other controls that support it.")]
    public string Invoke([Description("Element id from find_elements, list_windows or dump_tree.")] string elementId)
    {
        AutomationElement element = registry.Resolve(elementId);
        return UiaQuery.Guarded(logger, "invoke", elementId, () => InvokeElement(element, elementId));
    }

    [McpServerTool]
    [Description(
        "Clicks an element with a real mouse click at the centre of its rectangle, after bringing its window to the front. Avalonia menus "
            + "support neither InvokePattern nor ExpandCollapsePattern, so this is the only way to open one — and the only way to pick an "
            + "item from the popup it opens."
    )]
    public string Click(
        [Description("Element id from find_elements, list_windows or dump_tree.")] string elementId,
        [Description("Which button: left, right or middle.")] string button = "left",
        [Description("True to send two clicks in quick succession.")] bool doubleClick = false
    )
    {
        AutomationElement element = registry.Resolve(elementId);
        MouseButton mouseButton = ParseButton(button);
        return UiaQuery.Guarded(logger, "click", elementId, () => ClickElement(element, elementId, mouseButton, doubleClick));
    }

    [McpServerTool]
    [Description(
        "Clicks at absolute screen coordinates, for surfaces UI Automation cannot see into — CRC's OpenGL scopes, where a track or "
            + "datablock exists only as pixels. Take a screenshot first and read the coordinates off the window's rectangle."
    )]
    public string ClickPoint(
        [Description("Screen X in physical pixels.")] int x,
        [Description("Screen Y in physical pixels.")] int y,
        [Description("Which button: left, right or middle.")] string button = "left",
        [Description("True to send two clicks in quick succession.")] bool doubleClick = false
    )
    {
        MouseButton mouseButton = ParseButton(button);
        NativeInput.Click(x, y, mouseButton, doubleClick);
        return $"{ClickVerb(doubleClick)} {mouseButton.ToString().ToLowerInvariant()} at screen point ({x},{y})";
    }

    [McpServerTool]
    [Description(
        "Replaces an element's text: through its ValuePattern when it has a writable one, otherwise by focusing it, selecting all and typing."
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
        "Sends keystrokes in SendKeys syntax to whatever has focus: {ENTER}, {ESC}, {TAB}, {F4}; ^a for Ctrl+A, %{F4} for Alt+F4, +a for "
            + "Shift+A; a literal + ^ % ~ ( ) { } [ ] must be wrapped in braces. This also drives the native file dialog, which never appears "
            + "in a process's window list: send the full path followed by {ENTER}."
    )]
    public string SendKeys(
        [Description("The keystrokes, in SendKeys syntax.")] string keys,
        [Description("Element id to focus first, or empty to type into whatever is focused now.")] string focusElementId = ""
    )
    {
        lock (NativeInput.InputGate)
        {
            string target = "the focused element";
            if (!string.IsNullOrEmpty(focusElementId))
            {
                AutomationElement element = registry.Resolve(focusElementId);
                target = UiaQuery.Guarded(logger, "send_keys", focusElementId, () => FocusAndDescribe(element, focusElementId));
            }

            System.Windows.Forms.SendKeys.SendWait(keys);
            return $"sent '{keys}' to {target}";
        }
    }

    [McpServerTool]
    [Description("Gives an element the keyboard focus, so a following send_keys goes to it.")]
    public string Focus([Description("Element id to focus.")] string elementId)
    {
        AutomationElement element = registry.Resolve(elementId);
        return UiaQuery.Guarded(logger, "focus", elementId, () => $"focused {FocusAndDescribe(element, elementId)}");
    }

    private static string ClickVerb(bool doubleClick) => doubleClick ? "double-clicked" : "clicked";

    private static MouseButton ParseButton(string button) =>
        button.ToLowerInvariant() switch
        {
            "left" => MouseButton.Left,
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => throw new McpException($"Unknown button '{button}' — use left, right or middle"),
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
                $"Element '{elementId}' has no InvokePattern — Avalonia menus are the common case. Use click, which sends a real mouse click at its rectangle"
            );
        }

        invokePattern.Invoke();
        return $"invoked {UiaQuery.Describe(element, elementId)}";
    }

    private string ClickElement(AutomationElement element, string elementId, MouseButton button, bool doubleClick)
    {
        AutomationElement window = UiaQuery.TopLevelWindowOf(element);
        nint handle = UiaQuery.WindowHandle(window);
        if (NativeInput.IsMinimised(handle))
        {
            throw new McpException($"The window '{window.Current.Name}' is minimised — restore it before clicking element '{elementId}'");
        }

        lock (NativeInput.InputGate)
        {
            if (!NativeInput.Foreground(handle))
            {
                logger.LogDebug("SetForegroundWindow was refused for '{Window}'; clicking its rectangle anyway", window.Current.Name);
            }

            Thread.Sleep(120);
            Rect rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty || double.IsInfinity(rect.X) || (rect.Width < 1) || (rect.Height < 1))
            {
                throw new McpException(
                    $"Element '{elementId}' has no on-screen rectangle to click — it is offscreen, collapsed or scrolled out of view"
                );
            }

            int x = (int)Math.Round(rect.X + (rect.Width / 2));
            int y = (int)Math.Round(rect.Y + (rect.Height / 2));
            NativeInput.Click(x, y, button, doubleClick);
            return $"{ClickVerb(doubleClick)} {button.ToString().ToLowerInvariant()} at ({x},{y}) on {UiaQuery.Describe(element, elementId)}";
        }
    }

    private string WriteText(AutomationElement element, string elementId, string text)
    {
        if (
            element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern)
            && (pattern is ValuePattern valuePattern)
            && !valuePattern.Current.IsReadOnly
        )
        {
            valuePattern.SetValue(text);
            return $"set '{text}' through ValuePattern on {UiaQuery.Describe(element, elementId)}";
        }

        lock (NativeInput.InputGate)
        {
            FocusElement(element, elementId);
            Thread.Sleep(80);
            System.Windows.Forms.SendKeys.SendWait("^a");
            if (text.Length == 0)
            {
                System.Windows.Forms.SendKeys.SendWait("{DEL}");
                return $"cleared (select-all then delete, no writable ValuePattern) {UiaQuery.Describe(element, elementId)}";
            }

            System.Windows.Forms.SendKeys.SendWait(Escape(text));
            return $"typed '{text}' after select-all (no writable ValuePattern) into {UiaQuery.Describe(element, elementId)}";
        }
    }

    private string FocusAndDescribe(AutomationElement element, string elementId)
    {
        FocusElement(element, elementId);
        return UiaQuery.Describe(element, elementId);
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
                $"Windows refused focus for element '{elementId}' — its window may be minimised, or another process holds the foreground. Click the window first"
            );
        }
    }
}
