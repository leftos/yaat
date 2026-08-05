using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace Yaat.Client.UI.Tests.Helpers;

internal static class HeadlessWindowExtensions
{
    /// <summary>
    /// Drives a full left-button drag with the headless mouse device:
    /// press at <paramref name="from"/>, interpolated moves to
    /// <paramref name="to"/> (the first hop already exceeds the 5px drag
    /// threshold when the distance allows), release at the destination.
    /// Dispatcher jobs are flushed between steps so per-move handlers
    /// (ghost, drop preview) run like they would under a real pointer.
    /// </summary>
    public static void MouseDrag(this Window window, Point from, Point to, int steps = 4)
    {
        window.MouseDown(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            // LeftMouseButton modifier is load-bearing: the drag handlers
            // read IsLeftButtonPressed from the point properties, and the
            // headless device does not infer held buttons on plain moves.
            window.MouseMove(new Point(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t)), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public static void ShowAndRunLayout(this Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        // Trigger a layout pass so controls wire their OnLoaded handlers (CommandInputView
        // attaches its KeyDown handlers in OnLoaded, so this must run before we simulate
        // keyboard input).
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public static void PumpDispatcher()
    {
        Dispatcher.UIThread.RunJobs();
    }

    public static void DispatchKey(this Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(ToPhysicalKey(key), modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    // Headless KeyPressQwerty takes a PhysicalKey. Map the common logical keys we simulate
    // in tests; extend as tests grow rather than up-front (per the zero-speculation rule).
    private static PhysicalKey ToPhysicalKey(Key key) =>
        key switch
        {
            Key.Enter => PhysicalKey.Enter,
            Key.Tab => PhysicalKey.Tab,
            Key.Escape => PhysicalKey.Escape,
            Key.Up => PhysicalKey.ArrowUp,
            Key.Down => PhysicalKey.ArrowDown,
            Key.Left => PhysicalKey.ArrowLeft,
            Key.Right => PhysicalKey.ArrowRight,
            Key.Space => PhysicalKey.Space,
            Key.Back => PhysicalKey.Backspace,
            Key.Add => PhysicalKey.NumPadAdd,
            Key.OemTilde => PhysicalKey.Backquote,
            Key.L => PhysicalKey.L,
            Key.T => PhysicalKey.T,
            _ => throw new System.ArgumentOutOfRangeException(nameof(key), key, "Add mapping in HeadlessWindowExtensions.ToPhysicalKey"),
        };
}
