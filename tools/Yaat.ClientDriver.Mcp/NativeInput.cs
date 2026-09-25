using System.Diagnostics;
using System.Runtime.InteropServices;
using ModelContextProtocol;

namespace Yaat.ClientDriver.Mcp;

/// <summary>The mouse button a click tool drives.</summary>
internal enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>Keys held down around a click.</summary>
[Flags]
internal enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
}

/// <summary>How the input tools deliver input, server-wide.</summary>
internal enum InputMode
{
    /// <summary>Window messages posted to a YAAT window: the real cursor and the foreground are never touched.</summary>
    Virtual,

    /// <summary>SendInput and SendKeys: the real cursor moves and the target window is brought to the foreground.</summary>
    Real,
}

/// <summary>
/// Input to the driven applications, by one of two routes. Virtual input posts window messages to a YAAT window: UIPI allows those
/// between processes of the same integrity, they need no foreground, and the user's own cursor and keyboard are left alone. Real
/// input (SendInput, SendKeys) goes to whichever window holds the foreground, so it is sent only once the target holds it.
/// </summary>
internal static partial class NativeInput
{
    /// <summary>Held around every foreground-then-act sequence: two concurrent tool calls must not interleave their input.</summary>
    internal static readonly Lock InputGate = new();

    private static InputMode _mode = InputMode.Virtual;

    internal const string ClientProcessName = "Yaat.Client";
    private const string CrcProcessName = "CRC";

    private const nint PerMonitorAwareV2 = -4;
    private const uint InputTypeMouse = 0;
    private const uint InputTypeKeyboard = 1;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint KeyEventKeyUp = 0x0002;

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmRightButtonDown = 0x0204;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmMiddleButtonDown = 0x0207;
    private const uint WmMiddleButtonUp = 0x0208;
    private const nint MkLeftButton = 0x0001;
    private const nint MkRightButton = 0x0002;
    private const nint MkMiddleButton = 0x0010;

    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkReturn = 0x0D;
    private const ushort VkBack = 0x08;
    private const ushort VkEnd = 0x23;
    private const uint MapVkToScanCode = 0;
    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x00000080;
    private const uint GwHwndNext = 2;
    private const uint GwOwner = 4;
    private const int MaxWindowsWalked = 4096;
    private const uint DwmwaExtendedFrameBounds = 9;
    private const uint DwmwaCloaked = 14;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;
    private const int DoubleClickMarginMilliseconds = 50;

    private const string ModifierPrefixes = "^%+";
    private const string ReservedCharacters = "(){}[]";
    private const string BracedLiterals = "+^%~(){}[]";

    private static readonly Dictionary<string, ushort> NamedKeys = BuildNamedKeys();

    /// <summary>Navigation keys that sit on the extended part of the keyboard; their posted lParam carries the extended-key bit.</summary>
    private static readonly HashSet<ushort> ExtendedKeys = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E];

    private static nint _lastTarget;
    private static PostedClick _lastClick;

    /// <summary>The YAAT window the last input tool aimed at: where send_keys without an element posts its keys.</summary>
    internal static nint LastTarget
    {
        get
        {
            lock (InputGate)
            {
                return _lastTarget;
            }
        }
    }

    /// <summary>The input mode every input tool uses; virtual until set_input_mode changes it.</summary>
    internal static InputMode Mode
    {
        get
        {
            lock (InputGate)
            {
                return _mode;
            }
        }
        set
        {
            lock (InputGate)
            {
                _mode = value;
            }
        }
    }

    /// <summary>Records a YAAT window as the one send_keys without an element posts to; windows of other processes are ignored.</summary>
    internal static void RememberTarget(nint windowHandle)
    {
        nint target = ActivationTarget(windowHandle);
        if (IsClientWindow(target))
        {
            lock (InputGate)
            {
                _lastTarget = target;
            }
        }
    }

    internal static bool EnablePerMonitorDpiAwareness() => SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    /// <summary>
    /// Asks for the foreground for the window that takes keyboard input on the given window's behalf, and reports whether it
    /// actually holds it afterwards — SetForegroundWindow's own result is not proof.
    /// </summary>
    internal static bool TryForeground(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return false;
        }

        nint target = ActivationTarget(windowHandle);
        if (GetForegroundWindow() == target)
        {
            return true;
        }

        _ = SetForegroundWindow(target);
        Thread.Sleep(100);
        return GetForegroundWindow() == target;
    }

    /// <summary>
    /// The window that is activated, and receives keyboard input, for a given top-level window. An Avalonia popup (a menu, a
    /// flyout) is an owned tool window that never activates, so its owner stands in for it.
    /// </summary>
    internal static nint ActivationTarget(nint windowHandle)
    {
        nint current = windowHandle;
        for (int depth = 0; (depth < 16) && IsOwnedToolWindow(current); depth++)
        {
            current = GetWindow(current, GwOwner);
        }

        return current;
    }

    /// <summary>Which process holds the foreground, for the "(posted: …)" note a tool result carries.</summary>
    internal static string DescribeForeground()
    {
        nint foreground = GetForegroundWindow();
        return foreground == 0 ? "no window holds the foreground" : $"the foreground window belongs to '{ProcessNameOf(foreground)}'";
    }

    /// <summary>True when the foreground window belongs to YAAT or CRC — the only applications real input is meant for.</summary>
    internal static bool ForegroundIsDrivenApplication()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return false;
        }

        string name = ProcessNameOf(foreground);
        return string.Equals(name, ClientProcessName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, CrcProcessName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsClientWindow(nint windowHandle) => (windowHandle != 0) && IsClientProcess(WindowProcessId(windowHandle));

    internal static bool IsClientProcess(int processId) =>
        string.Equals(ProcessName(processId), ClientProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>False for a window a modal dialog has disabled: it takes no input until the dialog closes.</summary>
    internal static bool IsEnabled(nint windowHandle) => IsWindowEnabled(windowHandle);

    /// <summary>
    /// The topmost visible YAAT top-level window containing a screen point, walking the z-order from the top. Foreign windows
    /// above it are skipped; a CRC window above it wins, and yields 0, because input to CRC is only ever real input.
    /// </summary>
    internal static nint ClientWindowAt(int x, int y) =>
        TopmostWindowOver(
            new Win32Rect
            {
                Left = x,
                Top = y,
                Right = x + 1,
                Bottom = y + 1,
            },
            ClassifyForClientPoint
        );

    /// <summary>
    /// The topmost visible top-level window of one process containing a screen point; windows of every other process are skipped.
    /// This is how an element is mapped to the window really showing it: UI Automation files an Avalonia menu popup under its
    /// main window, but the popup is a window of its own.
    /// </summary>
    internal static nint ProcessWindowAt(int x, int y, int processId) =>
        TopmostWindowOver(
            new Win32Rect
            {
                Left = x,
                Top = y,
                Right = x + 1,
                Bottom = y + 1,
            },
            window => MatchProcess(window, processId)
        );

    /// <summary>The topmost visible top-level window of one process whose frame holds the whole rectangle, or 0.</summary>
    internal static nint ProcessWindowCovering(System.Windows.Rect rect, int processId)
    {
        Win32Rect region = new()
        {
            Left = (int)Math.Round(rect.Left),
            Top = (int)Math.Round(rect.Top),
            Right = (int)Math.Round(rect.Right),
            Bottom = (int)Math.Round(rect.Bottom),
        };
        return TopmostWindowOver(region, window => MatchProcess(window, processId));
    }

    internal static bool IsMinimised(nint windowHandle) => (windowHandle != 0) && IsIconic(windowHandle);

    /// <summary>Refuses a shift or ctrl click in virtual mode, before any window is looked up.</summary>
    internal static void RefuseVirtualModifiers(KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.None)
        {
            throw new McpException(
                "A shift or ctrl click cannot be sent as virtual input: Avalonia reads Shift and Ctrl from the keyboard state "
                    + "(GetKeyboardState), not from the message, so it would arrive unmodified. Switch to real input with set_input_mode real"
            );
        }
    }

    /// <summary>
    /// Refuses text a posted WM_CHAR cannot deliver: a control character, which Avalonia drops (it ignores WM_CHAR below 32), or
    /// half of a surrogate pair. Nothing has been posted when this throws.
    /// </summary>
    internal static void EnsureTypeable(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character < ' ')
            {
                throw new McpException(
                    $"The text holds control character U+{(int)character:X4} at position {index}, which virtual input cannot type. "
                        + "Send {ENTER} or {TAB} through send_keys, or switch to real input with set_input_mode real"
                );
            }

            if (char.IsHighSurrogate(character) && (index + 1 < text.Length) && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(character))
            {
                throw new McpException($"The text holds an unpaired surrogate U+{(int)character:X4} at position {index}, which is not a character");
            }
        }
    }

    internal static void Click(int x, int y, MouseButton button, bool doubleClick, KeyModifiers modifiers)
    {
        lock (InputGate)
        {
            if (!SetCursorPos(x, y))
            {
                throw new McpException(
                    $"SetCursorPos to ({x},{y}) was refused (Win32 error {Marshal.GetLastWin32Error()}) — the desktop may be locked, "
                        + "or a higher-integrity window owns the cursor"
                );
            }

            Thread.Sleep(60);
            SendModifiers(modifiers, down: true);
            try
            {
                SendButton(button, down: true);
                SendButton(button, down: false);
                if (doubleClick)
                {
                    Thread.Sleep(40);
                    SendButton(button, down: true);
                    SendButton(button, down: false);
                }
            }
            finally
            {
                SendModifiers(modifiers, down: false);
            }
        }
    }

    /// <summary>
    /// Posts a mouse move and button press and release to a window at a screen point. A double click is two posted clicks:
    /// Avalonia's window class has no CS_DBLCLKS and it counts clicks itself, so it never reads WM_xBUTTONDBLCLK. Modifiers are
    /// the caller's to refuse (RefuseVirtualModifiers).
    /// </summary>
    internal static void PostClick(nint windowHandle, int screenX, int screenY, MouseButton button, bool doubleClick)
    {
        Win32Point point = new() { X = screenX, Y = screenY };
        if (!ScreenToClient(windowHandle, ref point))
        {
            throw new McpException($"ScreenToClient failed for window 0x{windowHandle:X} — it has closed; find the element again");
        }

        nint position = ((point.Y & 0xFFFF) << 16) | (point.X & 0xFFFF);
        (uint downMessage, uint upMessage, nint buttonFlag) = ButtonMessages(button);
        lock (InputGate)
        {
            WaitOutDoubleClick(windowHandle, screenX, screenY);

            // Every press follows a move of its own, posted back to back: Avalonia answers a move with TrackMouseEvent, which posts
            // WM_MOUSELEAVE at once because the real cursor is elsewhere, and a press handled after that leave has no pointer over it.
            int clicks = doubleClick ? 2 : 1;
            for (int click = 0; click < clicks; click++)
            {
                Post(windowHandle, WmMouseMove, 0, position);
                Post(windowHandle, downMessage, buttonFlag, position);
                Post(windowHandle, upMessage, 0, position);
            }

            _lastClick = new PostedClick(windowHandle, screenX, screenY, Environment.TickCount64);
            Thread.Sleep(40);
        }
    }

    /// <summary>
    /// Waits out the double-click time when a click would land near the previous posted click on the same window, which Avalonia
    /// would count as its second click: a text box then selects a word, and keys that follow act on the selection (seen live as
    /// set_text typing ahead of the old text right after send_keys had focused the same box).
    /// </summary>
    private static void WaitOutDoubleClick(nint windowHandle, int screenX, int screenY)
    {
        PostedClick last = _lastClick;
        bool near =
            (last.Window == windowHandle)
            && (Math.Abs(screenX - last.X) <= GetSystemMetrics(SmCxDoubleClick))
            && (Math.Abs(screenY - last.Y) <= GetSystemMetrics(SmCyDoubleClick));
        long remaining = GetDoubleClickTime() + DoubleClickMarginMilliseconds - (Environment.TickCount64 - last.Tick);
        if (near && (remaining > 0))
        {
            Thread.Sleep((int)remaining);
        }
    }

    /// <summary>
    /// Posts keystrokes in the SendKeys subset send_keys documents. Literal characters become WM_CHAR; named keys become
    /// WM_KEYDOWN and WM_KEYUP. The whole string is checked before anything is posted, so a refusal types nothing.
    /// </summary>
    internal static void PostKeys(nint windowHandle, string keys) => PostStrokes(windowHandle, ParseKeys(keys));

    /// <summary>Throws the refusal PostKeys would, without posting anything.</summary>
    internal static void EnsurePostable(string keys) => _ = ParseKeys(keys);

    /// <summary>Posts text verbatim, one WM_CHAR per UTF-16 unit; refuses text EnsureTypeable refuses, before posting anything.</summary>
    internal static void PostText(nint windowHandle, string text)
    {
        EnsureTypeable(text);
        PostStrokes(windowHandle, [.. text.Select(character => new PostedStroke(character, 0))]);
    }

    /// <summary>Posts End followed by one Backspace per character, clearing a single-line field of the given length.</summary>
    internal static void PostClear(nint windowHandle, int length)
    {
        List<PostedStroke> strokes = [new PostedStroke('\0', VkEnd)];
        strokes.AddRange(Enumerable.Repeat(new PostedStroke('\0', VkBack), length));
        PostStrokes(windowHandle, strokes);
    }

    private static void PostStrokes(nint windowHandle, List<PostedStroke> strokes)
    {
        lock (InputGate)
        {
            foreach (PostedStroke stroke in strokes)
            {
                if (stroke.VirtualKey == 0)
                {
                    Post(windowHandle, WmChar, stroke.Character, 1);
                }
                else
                {
                    Post(windowHandle, WmKeyDown, stroke.VirtualKey, KeyLParam(stroke.VirtualKey, keyUp: false));
                    Post(windowHandle, WmKeyUp, stroke.VirtualKey, KeyLParam(stroke.VirtualKey, keyUp: true));
                }

                Thread.Sleep(15);
            }

            Thread.Sleep(80);
        }
    }

    private static List<PostedStroke> ParseKeys(string keys)
    {
        EnsureTypeable(keys);
        List<PostedStroke> strokes = [];
        int index = 0;
        while (index < keys.Length)
        {
            char character = keys[index];
            if (character == '{')
            {
                index = ParseBraced(keys, index, strokes);
                continue;
            }

            if (ModifierPrefixes.Contains(character))
            {
                throw new McpException(
                    $"'{keys}' holds a modifier key ('{character}'), which virtual input cannot send: the target reads Ctrl, Alt and Shift from "
                        + "the keyboard state, which posted messages do not change. Switch to real input with set_input_mode real"
                );
            }

            if (ReservedCharacters.Contains(character))
            {
                throw new McpException($"'{keys}': a literal '{character}' must be wrapped in braces, as {{{character}}}");
            }

            strokes.Add(character == '~' ? new PostedStroke('\0', VkReturn) : new PostedStroke(character, 0));
            index++;
        }

        return strokes;
    }

    private static int ParseBraced(string keys, int start, List<PostedStroke> strokes)
    {
        // "{}}" is the escaped closing brace: the one token whose content is itself a '}'.
        bool escapedClose = (start + 2 < keys.Length) && (keys[start + 1] == '}') && (keys[start + 2] == '}');
        int close = escapedClose ? start + 2 : keys.IndexOf('}', start + 1);
        if (close < 0)
        {
            throw new McpException($"'{keys}': the '{{' at position {start} is never closed");
        }

        string name = keys[(start + 1)..close];
        if ((name.Length == 1) && BracedLiterals.Contains(name[0]))
        {
            strokes.Add(new PostedStroke(name[0], 0));
        }
        else if (NamedKeys.TryGetValue(name, out ushort virtualKey))
        {
            strokes.Add(new PostedStroke('\0', virtualKey));
        }
        else
        {
            throw new McpException(
                $"'{{{name}}}' cannot be sent as virtual input. Virtual keys are {{ENTER}} {{ESC}} {{TAB}} {{BACKSPACE}}/{{BS}} {{DEL}}/{{DELETE}} "
                    + "{HOME} {END} {LEFT} {RIGHT} {UP} {DOWN} {F1}–{F12} and braced literals such as {+}; anything else needs set_input_mode real"
            );
        }

        return close + 1;
    }

    private static Dictionary<string, ushort> BuildNamedKeys()
    {
        Dictionary<string, ushort> keys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ENTER"] = VkReturn,
            ["ESC"] = 0x1B,
            ["TAB"] = 0x09,
            ["BACKSPACE"] = VkBack,
            ["BS"] = VkBack,
            ["DEL"] = 0x2E,
            ["DELETE"] = 0x2E,
            ["HOME"] = 0x24,
            ["END"] = VkEnd,
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28,
        };
        for (int function = 1; function <= 12; function++)
        {
            keys[$"F{function}"] = (ushort)(0x70 + function - 1);
        }

        return keys;
    }

    /// <summary>A WM_KEYDOWN/WM_KEYUP lParam: repeat count 1, the key's scan code, the extended bit, and on key-up the transition bits.</summary>
    private static nint KeyLParam(ushort virtualKey, bool keyUp)
    {
        uint scanCode = MapVirtualKeyW(virtualKey, MapVkToScanCode) & 0xFF;
        uint value = 1u | (scanCode << 16);
        if (ExtendedKeys.Contains(virtualKey))
        {
            value |= 1u << 24;
        }

        if (keyUp)
        {
            value |= (1u << 30) | (1u << 31);
        }

        return unchecked((int)value);
    }

    private static (uint Down, uint Up, nint Flag) ButtonMessages(MouseButton button) =>
        button switch
        {
            MouseButton.Right => (WmRightButtonDown, WmRightButtonUp, MkRightButton),
            MouseButton.Middle => (WmMiddleButtonDown, WmMiddleButtonUp, MkMiddleButton),
            _ => (WmLeftButtonDown, WmLeftButtonUp, MkLeftButton),
        };

    private static void Post(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if (!PostMessageW(windowHandle, message, wParam, lParam))
        {
            throw new McpException(
                $"PostMessage 0x{message:X4} to window 0x{windowHandle:X} failed (Win32 error {Marshal.GetLastWin32Error()}) — "
                    + "the window has closed; find the element again"
            );
        }
    }

    private static bool IsOwnedToolWindow(nint windowHandle) =>
        ((GetWindowLongPtrW(windowHandle, GwlExStyle) & WsExToolWindow) != 0) && (GetWindow(windowHandle, GwOwner) != 0);

    /// <summary>
    /// Walks the top-level windows from the top of the z-order; the first shown window over the region that the match takes, or
    /// stops on, decides the answer.
    /// </summary>
    private static nint TopmostWindowOver(Win32Rect region, Func<nint, WindowMatch> match)
    {
        nint window = GetTopWindow(0);
        for (int visited = 0; (window != 0) && (visited < MaxWindowsWalked); visited++)
        {
            if (IsWindowVisible(window) && !IsIconic(window) && !IsCloaked(window) && Covers(window, region))
            {
                WindowMatch verdict = match(window);
                if (verdict != WindowMatch.Skip)
                {
                    return verdict == WindowMatch.Take ? window : 0;
                }
            }

            window = GetWindow(window, GwHwndNext);
        }

        return 0;
    }

    private static WindowMatch ClassifyForClientPoint(nint window)
    {
        string name = ProcessName(WindowProcessId(window));
        if (string.Equals(name, ClientProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return WindowMatch.Take;
        }

        return string.Equals(name, CrcProcessName, StringComparison.OrdinalIgnoreCase) ? WindowMatch.Stop : WindowMatch.Skip;
    }

    private static WindowMatch MatchProcess(nint window, int processId) => WindowProcessId(window) == processId ? WindowMatch.Take : WindowMatch.Skip;

    /// <summary>
    /// True when the window's visible frame holds the region. The DWM extended frame bounds leave out the invisible resize border
    /// that GetWindowRect includes; GetWindowRect is the fallback when DWM does not answer.
    /// </summary>
    private static bool Covers(nint window, Win32Rect region)
    {
        if (
            (DwmGetWindowAttributeRect(window, DwmwaExtendedFrameBounds, out Win32Rect frame, Marshal.SizeOf<Win32Rect>()) != 0)
            && !GetWindowRect(window, out frame)
        )
        {
            return false;
        }

        return (region.Left >= frame.Left) && (region.Right <= frame.Right) && (region.Top >= frame.Top) && (region.Bottom <= frame.Bottom);
    }

    /// <summary>True for a window DWM keeps off screen although it is "visible": another virtual desktop, a suspended app.</summary>
    private static bool IsCloaked(nint window) =>
        (DwmGetWindowAttributeInt(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0) && (cloaked != 0);

    private static int WindowProcessId(nint windowHandle)
    {
        _ = GetWindowThreadProcessId(windowHandle, out uint processId);
        return (int)processId;
    }

    internal static string ProcessNameOf(nint windowHandle) => ProcessName(WindowProcessId(windowHandle));

    internal static string ProcessName(int processId)
    {
        if (processId == 0)
        {
            return "(unknown process)";
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when ((ex is ArgumentException) || (ex is InvalidOperationException))
        {
            return $"pid {processId} (exited)";
        }
    }

    private static void SendModifiers(KeyModifiers modifiers, bool down)
    {
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            SendKey(VkShift, down);
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            SendKey(VkControl, down);
        }
    }

    private static void SendKey(ushort virtualKey, bool down)
    {
        Input input = new()
        {
            Type = InputTypeKeyboard,
            Data = new InputData
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = 0,
                    Flags = down ? 0 : KeyEventKeyUp,
                    Time = 0,
                    ExtraInfo = 0,
                },
            },
        };
        Send(input);
    }

    private static void SendButton(MouseButton button, bool down)
    {
        uint flags = button switch
        {
            MouseButton.Right => down ? MouseEventRightDown : MouseEventRightUp,
            MouseButton.Middle => down ? MouseEventMiddleDown : MouseEventMiddleUp,
            _ => down ? MouseEventLeftDown : MouseEventLeftUp,
        };

        Input input = new()
        {
            Type = InputTypeMouse,
            Data = new InputData
            {
                Mouse = new MouseInput
                {
                    Dx = 0,
                    Dy = 0,
                    MouseData = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = 0,
                },
            },
        };
        Send(input);
    }

    private static void Send(Input input)
    {
        Input[] inputs = [input];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new McpException(
                $"SendInput was blocked (Win32 error {Marshal.GetLastWin32Error()}) — the target window is probably elevated; "
                    + "run this server elevated too"
            );
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint windowHandle);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint windowHandle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint windowHandle);

    [LibraryImport("user32.dll")]
    private static partial nint GetTopWindow(nint windowHandle);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint windowHandle, uint command);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint windowHandle, int index);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint windowHandle, out Win32Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowEnabled(nint windowHandle);

    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttributeRect(nint windowHandle, uint attribute, out Win32Rect value, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttributeInt(nint windowHandle, uint attribute, out int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint windowHandle, ref Win32Point point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint windowHandle, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint code, uint mapType);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, Input[] inputs, int structSize);

    /// <summary>One posted keystroke: a WM_CHAR character when VirtualKey is 0, otherwise a key press and release.</summary>
    private readonly record struct PostedStroke(char Character, ushort VirtualKey);

    /// <summary>The last posted click: its window, screen point and Environment.TickCount64 time.</summary>
    private readonly record struct PostedClick(nint Window, int X, int Y, long Tick);

    /// <summary>What a z-order walk does with a shown window over the region: skip it, take it, or stop with none.</summary>
    private enum WindowMatch
    {
        Skip,
        Take,
        Stop,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
