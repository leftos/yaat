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

/// <summary>Real cursor and button input, injected through SendInput so the target sees it as user input.</summary>
internal static partial class NativeInput
{
    /// <summary>Held around every foreground-then-act sequence: two concurrent tool calls must not interleave their input.</summary>
    internal static readonly Lock InputGate = new();

    private const nint PerMonitorAwareV2 = -4;
    private const uint InputTypeMouse = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;

    internal static bool EnablePerMonitorDpiAwareness() => SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    internal static bool Foreground(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return false;
        }

        return SetForegroundWindow(windowHandle);
    }

    internal static bool IsMinimised(nint windowHandle) => (windowHandle != 0) && IsIconic(windowHandle);

    internal static void Click(int x, int y, MouseButton button, bool doubleClick)
    {
        lock (InputGate)
        {
            if (!SetCursorPos(x, y))
            {
                throw new McpException(
                    $"SetCursorPos to ({x},{y}) was refused (Win32 error {Marshal.GetLastWin32Error()}) — the desktop may be locked, or a higher-integrity window owns the cursor"
                );
            }

            Thread.Sleep(60);
            SendButton(button, down: true);
            SendButton(button, down: false);
            if (doubleClick)
            {
                Thread.Sleep(40);
                SendButton(button, down: true);
                SendButton(button, down: false);
            }
        }
    }

    private static void SendButton(MouseButton button, bool down)
    {
        uint flags = button switch
        {
            MouseButton.Right => down ? MouseEventRightDown : MouseEventRightUp,
            MouseButton.Middle => down ? MouseEventMiddleDown : MouseEventMiddleUp,
            _ => down ? MouseEventLeftDown : MouseEventLeftUp,
        };

        Input[] inputs =
        [
            new Input
            {
                Type = InputTypeMouse,
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
        ];

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new McpException(
                $"SendInput was blocked (Win32 error {Marshal.GetLastWin32Error()}) — the target window is probably elevated; run this server elevated too"
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint windowHandle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, Input[] inputs, int structSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
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
}
