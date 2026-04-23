using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace Yaat.Client.UI.Tests.Helpers;

internal static class HeadlessWindowExtensions
{
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
            _ => throw new System.ArgumentOutOfRangeException(nameof(key), key, "Add mapping in HeadlessWindowExtensions.ToPhysicalKey"),
        };
}
