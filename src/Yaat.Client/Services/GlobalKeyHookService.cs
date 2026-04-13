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

    /// <summary>
    /// Whether the native libuiohook hook is actually running. This is <c>false</c> until
    /// libuiohook fires <c>HookEnabled</c>, even if <see cref="Start"/> returned successfully —
    /// <c>RunAsync</c> only guarantees the thread launched, not that the native hook installed.
    /// </summary>
    public bool IsRunning => _hook.IsRunning;

    public GlobalKeyHookService()
    {
        _hook = new SimpleGlobalHook();
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;
        _hook.HookEnabled += OnHookEnabled;
        _hook.HookDisabled += OnHookDisabled;
    }

    /// <summary>
    /// Starts the hook on a background thread. Safe to call once; subsequent calls are no-ops.
    /// Failures (missing native lib, permission denial) are logged and swallowed — the service
    /// degrades to a no-op so the in-window capture path still works. Note that a successful
    /// return only means the background thread launched; the authoritative "hook is installed"
    /// signal is <c>HookEnabled</c>, logged separately in <see cref="OnHookEnabled"/>.
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
            Log.LogInformation("Global keyboard hook thread launched (awaiting HookEnabled)");

            // RunAsync only throws synchronously if the thread fails to start. If libuiohook
            // fails to install the native hook on the spawned thread, the exception lands on
            // the returned Task — observe it here so silent failures surface in the log.
            _ = _hookTask.ContinueWith(
                t => Log.LogError(t.Exception?.Flatten(), "Global keyboard hook task faulted"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to start global keyboard hook; falling back to in-window PTT capture");
        }
    }

    private void OnHookEnabled(object? sender, HookEventArgs e)
    {
        Log.LogInformation("Global keyboard hook enabled (libuiohook is receiving events system-wide)");
    }

    private void OnHookDisabled(object? sender, HookEventArgs e)
    {
        Log.LogInformation("Global keyboard hook disabled");
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        // Use the rawCode-aware overload so libuiohook's Windows-side modifier mislabeling
        // (both Ctrls reported as VcLeftControl, etc.) gets corrected before we track state
        // or dispatch the event.
        var avaloniaKey = SharpHookKeyMap.ToAvaloniaKey(e.Data.KeyCode, e.Data.RawCode);
        UpdateModifierState(avaloniaKey, pressed: true);
        if (avaloniaKey == Key.None)
        {
            return;
        }

        var mods = CurrentModifiers();
        if (IsPttCandidateKey(avaloniaKey))
        {
            Log.LogDebug(
                "Global hook KeyPressed: code={KeyCode} raw=0x{RawCode:X} avalonia={Key} mods={Modifiers}",
                e.Data.KeyCode,
                e.Data.RawCode,
                avaloniaKey,
                mods
            );
        }

        KeyDown?.Invoke(avaloniaKey, mods);
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        var avaloniaKey = SharpHookKeyMap.ToAvaloniaKey(e.Data.KeyCode, e.Data.RawCode);

        // Compute modifiers BEFORE updating state, so a Ctrl-release event still reports
        // modifiers as they were at the moment of release — important if a consumer wants to
        // match "Ctrl+F12" on the release of F12 but Ctrl was already let go first.
        var modifiers = CurrentModifiers();
        UpdateModifierState(avaloniaKey, pressed: false);

        if (avaloniaKey == Key.None)
        {
            return;
        }

        if (IsPttCandidateKey(avaloniaKey))
        {
            Log.LogDebug(
                "Global hook KeyReleased: code={KeyCode} raw=0x{RawCode:X} avalonia={Key} mods={Modifiers}",
                e.Data.KeyCode,
                e.Data.RawCode,
                avaloniaKey,
                modifiers
            );
        }

        KeyUp?.Invoke(avaloniaKey, modifiers);
    }

    /// <summary>
    /// Gates the per-event debug log to keys a user is likely to bind as PTT (or has bound).
    /// Currently hard-coded to the Ctrl variants — widen if the default PTT binding changes
    /// or the log stops telling us what we need.
    /// </summary>
    private static bool IsPttCandidateKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl;
    }

    private void UpdateModifierState(Key key, bool pressed)
    {
        // Driven by the post-raw-code-correction Avalonia Key so Left/Right tracking stays
        // accurate on Windows despite libuiohook's KeyCode mislabeling.
        switch (key)
        {
            case Key.LeftCtrl:
                _leftCtrl = pressed;
                break;
            case Key.RightCtrl:
                _rightCtrl = pressed;
                break;
            case Key.LeftShift:
                _leftShift = pressed;
                break;
            case Key.RightShift:
                _rightShift = pressed;
                break;
            case Key.LeftAlt:
                _leftAlt = pressed;
                break;
            case Key.RightAlt:
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
            _hook.HookEnabled -= OnHookEnabled;
            _hook.HookDisabled -= OnHookDisabled;
            _hook.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Error disposing global keyboard hook");
        }
    }
}
