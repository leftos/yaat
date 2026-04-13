using Avalonia.Input;
using Microsoft.Extensions.Logging;
using SharpHook;
using SharpHook.Data;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Process-wide keyboard hook backed by SharpHook / libuiohook. Converts the raw SharpHook
/// events into Avalonia <see cref="Key"/> + <see cref="KeyModifiers"/> so consumers don't need
/// to know about SharpHook's native key code vocabulary. Runs in passthrough mode: the hook
/// observes events but never suppresses them, so the focused application still sees the key.
///
/// The primary consumer is PTT — <see cref="MainWindow"/> subscribes to <see cref="KeyDown"/>
/// / <see cref="KeyUp"/> so holding the configured PTT key works even when another application
/// has focus (CRC, a browser, a PDF viewer, etc.). The in-window <c>OnKeyDown</c>/<c>OnKeyUp</c>
/// handlers remain as a backup path for platforms where libuiohook fails to load.
/// </summary>
public sealed class GlobalKeyHookService : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<GlobalKeyHookService>();

    private readonly SimpleGlobalHook _hook;
    private Task? _hookTask;
    private bool _disposed;

    // Modifier state tracked manually — SharpHook's event data doesn't carry an aggregated
    // modifier bitmask, so we watch for left/right Ctrl/Shift/Alt press/release events and
    // maintain the current state to synthesize an Avalonia KeyModifiers value on each event.
    private bool _leftCtrl;
    private bool _rightCtrl;
    private bool _leftShift;
    private bool _rightShift;
    private bool _leftAlt;
    private bool _rightAlt;

    /// <summary>Raised on every keyboard press system-wide, on a background thread.</summary>
    public event Action<Key, KeyModifiers>? KeyDown;

    /// <summary>Raised on every keyboard release system-wide, on a background thread.</summary>
    public event Action<Key, KeyModifiers>? KeyUp;

    public GlobalKeyHookService()
    {
        _hook = new SimpleGlobalHook();
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;
    }

    /// <summary>
    /// Starts the hook on a background thread. Safe to call once; subsequent calls are no-ops.
    /// Failures (missing native lib, permission denial) are logged and swallowed — the service
    /// degrades to a no-op so the in-window capture path still works.
    /// </summary>
    public void Start()
    {
        if (_hookTask is not null)
        {
            return;
        }

        try
        {
            _hookTask = _hook.RunAsync();
            Log.LogInformation("Global keyboard hook started");
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to start global keyboard hook; falling back to in-window PTT capture");
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        UpdateModifierState(e.Data.KeyCode, pressed: true);
        var avaloniaKey = SharpHookKeyMap.ToAvaloniaKey(e.Data.KeyCode);
        if (avaloniaKey == Key.None)
        {
            return;
        }

        KeyDown?.Invoke(avaloniaKey, CurrentModifiers());
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        // Compute modifiers BEFORE updating state, so a Ctrl-release event still reports
        // modifiers as they were at the moment of release — important if a consumer wants to
        // match "Ctrl+F12" on the release of F12 but Ctrl was already let go first.
        var modifiers = CurrentModifiers();
        UpdateModifierState(e.Data.KeyCode, pressed: false);

        var avaloniaKey = SharpHookKeyMap.ToAvaloniaKey(e.Data.KeyCode);
        if (avaloniaKey == Key.None)
        {
            return;
        }

        KeyUp?.Invoke(avaloniaKey, modifiers);
    }

    private void UpdateModifierState(KeyCode code, bool pressed)
    {
        switch (code)
        {
            case KeyCode.VcLeftControl:
                _leftCtrl = pressed;
                break;
            case KeyCode.VcRightControl:
                _rightCtrl = pressed;
                break;
            case KeyCode.VcLeftShift:
                _leftShift = pressed;
                break;
            case KeyCode.VcRightShift:
                _rightShift = pressed;
                break;
            case KeyCode.VcLeftAlt:
                _leftAlt = pressed;
                break;
            case KeyCode.VcRightAlt:
                _rightAlt = pressed;
                break;
            default:
                break;
        }
    }

    private KeyModifiers CurrentModifiers()
    {
        var mods = KeyModifiers.None;
        if (_leftCtrl || _rightCtrl)
        {
            mods |= KeyModifiers.Control;
        }

        if (_leftShift || _rightShift)
        {
            mods |= KeyModifiers.Shift;
        }

        if (_leftAlt || _rightAlt)
        {
            mods |= KeyModifiers.Alt;
        }

        return mods;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.KeyReleased -= OnKeyReleased;
            _hook.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Error disposing global keyboard hook");
        }
    }
}
