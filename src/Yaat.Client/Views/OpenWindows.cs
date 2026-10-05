using Avalonia.Controls;

namespace Yaat.Client.Views;

/// <summary>
/// The app's windows that are open now: each is added as it opens and removed as it closes, as the desktop lifetime keeps
/// its own list. It works where there is no desktop lifetime too (the headless UI tests), which is why Settings reads it.
/// </summary>
/// <remarks>
/// <see cref="Register"/> runs from <c>App.Initialize</c>, so every window the app opens is seen. The list holds its
/// windows weakly, so a window hidden and dropped without being closed is not kept alive. UI thread only.
/// </remarks>
public static class OpenWindows
{
    private static readonly List<WeakReference<Window>> Windows = [];

    private static bool _registered;

    /// <summary>Starts following windows as they open and close; later calls do nothing.</summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) => Add((Window)sender!));
        Window.WindowClosedEvent.AddClassHandler(typeof(Window), (sender, _) => Remove((Window)sender!));
    }

    /// <summary>The windows open now, in the order they opened.</summary>
    public static IReadOnlyList<Window> All
    {
        get
        {
            List<Window> open = [];
            foreach (WeakReference<Window> reference in Windows)
            {
                if (reference.TryGetTarget(out Window? window))
                {
                    open.Add(window);
                }
            }

            return open;
        }
    }

    private static void Add(Window window)
    {
        Prune();
        if (!Windows.Exists(reference => reference.TryGetTarget(out Window? open) && ReferenceEquals(open, window)))
        {
            Windows.Add(new WeakReference<Window>(window));
        }
    }

    private static void Remove(Window window)
    {
        Prune();
        Windows.RemoveAll(reference => reference.TryGetTarget(out Window? open) && ReferenceEquals(open, window));
    }

    // Drops the windows collected without being closed.
    private static void Prune() => Windows.RemoveAll(reference => !reference.TryGetTarget(out _));
}
