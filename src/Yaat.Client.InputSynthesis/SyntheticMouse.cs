using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Yaat.Client.InputSynthesis;

/// <summary>
/// A drag in one top level: the press and release points in its DIPs, the button held, the moves and the rest before the release.
/// </summary>
/// <param name="From">The press point.</param>
/// <param name="To">The release point.</param>
/// <param name="Button">The button held.</param>
/// <param name="Steps">How many equal moves take the pointer from the press to the release.</param>
/// <param name="HoldMs">How long the button stays held at the release point before it is released.</param>
public sealed record MouseDrag(Point From, Point To, MouseButton Button, int Steps, int HoldMs);

/// <summary>
/// Hover and drag as raw mouse input, raised through the window's own platform input callback (<see cref="ITopLevelImpl.Input"/>),
/// the entry point the platform's mouse uses. Avalonia's input manager then does what it does for a real mouse: hit-tests the
/// point, tracks pointer-over (PointerEntered and PointerExited, IsPointerOver), routes moves to a pointer's capture, and
/// releases the capture after the release. Each top level gets one synthetic <see cref="MouseDevice"/> of its own, kept for its
/// life, so a hover's pointer stays where it was left and the next gesture moves on from there; the real cursor never moves.
/// Points are DIPs from the top-left of the top level's client area. Call on the UI thread; the awaits come back to it.
/// </summary>
/// <remarks>
/// The raw-input types are Avalonia private API, which is why this class lives alone in a project that opts into them; its own
/// signatures use public Avalonia types only. Re-check it on every Avalonia upgrade.
/// </remarks>
public static class SyntheticMouse
{
    /// <summary>The time between a drag's moves, one frame at 60 Hz, so a recording shows the drag travel.</summary>
    public static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(16);

    private static readonly ConditionalWeakTable<TopLevel, MouseDevice> Devices = [];

    /// <summary>The gesture, <c>hover</c> or <c>drag</c>, under way in each top level that has one.</summary>
    private static readonly ConditionalWeakTable<TopLevel, string> InProgress = [];

    /// <summary>The gesture, <c>hover</c> or <c>drag</c>, under way in <paramref name="topLevel"/>, or null when none is.</summary>
    public static string? GestureInProgress(TopLevel topLevel) => InProgress.TryGetValue(topLevel, out string? gesture) ? gesture : null;

    /// <summary>
    /// Moves the pointer onto <paramref name="point"/> in one move from wherever it was (from outside the top level the first
    /// time), then rests there <paramref name="durationMs"/>; the pointer stays on the point afterwards, as a resting mouse does.
    /// </summary>
    /// <exception cref="InvalidOperationException">A hover or drag is already under way in <paramref name="topLevel"/>.</exception>
    public static async Task HoverAsync(TopLevel topLevel, Point point, int durationMs, CancellationToken ct)
    {
        Begin(topLevel, "hover");
        try
        {
            Raise(topLevel, RawPointerEventType.Move, point, RawInputModifiers.None);
            await Task.Delay(durationMs, ct);
        }
        finally
        {
            InProgress.Remove(topLevel);
        }
    }

    /// <summary>
    /// Moves onto the drag's press point, presses its button, moves to its release point in its steps, equal moves
    /// <see cref="StepInterval"/> apart with the button held, rests its hold, and releases where the pointer is. The release
    /// goes out on a cancellation too, so no button is left held.
    /// </summary>
    /// <exception cref="InvalidOperationException">A hover or drag is already under way in <paramref name="topLevel"/>.</exception>
    public static async Task DragAsync(TopLevel topLevel, MouseDrag drag, CancellationToken ct)
    {
        Begin(topLevel, "drag");
        RawInputModifiers held = ButtonFlag(drag.Button);
        Point at = drag.From;
        try
        {
            Raise(topLevel, RawPointerEventType.Move, drag.From, RawInputModifiers.None);
            Raise(topLevel, DownType(drag.Button), drag.From, held);
            for (int step = 1; step <= drag.Steps; step++)
            {
                await Task.Delay(StepInterval, ct);
                at = (step == drag.Steps) ? drag.To : drag.From + ((drag.To - drag.From) * ((double)step / drag.Steps));
                Raise(topLevel, RawPointerEventType.Move, at, held);
            }

            await Task.Delay(drag.HoldMs, ct);
        }
        finally
        {
            InProgress.Remove(topLevel);
            // A top level closed mid-drag has no input path left, and nothing in it holds the button.
            if (topLevel.PlatformImpl is not null)
            {
                Raise(topLevel, UpType(drag.Button), at, RawInputModifiers.None);
            }
        }
    }

    /// <summary>Marks <paramref name="gesture"/> under way in <paramref name="topLevel"/>, refusing a second one there.</summary>
    private static void Begin(TopLevel topLevel, string gesture)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (GestureInProgress(topLevel) is { } current)
        {
            throw new InvalidOperationException(
                $"A {current} is still in progress in the {topLevel.GetType().Name}; start the {gesture} once it has ended."
            );
        }

        InProgress.Add(topLevel, gesture);
    }

    private static void Raise(TopLevel topLevel, RawPointerEventType type, Point point, RawInputModifiers modifiers)
    {
        Dispatcher.UIThread.VerifyAccess();
        ITopLevelImpl platform =
            topLevel.PlatformImpl ?? throw new InvalidOperationException($"The {topLevel.GetType().Name} is closed and takes no input.");
        Action<RawInputEventArgs> input =
            platform.Input ?? throw new InvalidOperationException($"The {topLevel.GetType().Name} has no input callback yet.");
        // Avalonia 12 routes a top level's input through its presentation source, which is the IInputRoot the input manager and the
        // pointer-over tracking compare raw events against.
        IInputRoot root =
            (topLevel.GetPresentationSource() as IInputRoot)
            ?? throw new InvalidOperationException($"The {topLevel.GetType().Name} has no input root; it is not shown.");
        MouseDevice device = Devices.GetValue(topLevel, _ => new MouseDevice(new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true)));
        ulong timestamp = unchecked((ulong)Environment.TickCount64);
        input(new RawPointerEventArgs(device, timestamp, root, type, point, modifiers));
    }

    private static RawInputModifiers ButtonFlag(MouseButton button) =>
        button switch
        {
            MouseButton.Right => RawInputModifiers.RightMouseButton,
            MouseButton.Middle => RawInputModifiers.MiddleMouseButton,
            _ => RawInputModifiers.LeftMouseButton,
        };

    private static RawPointerEventType DownType(MouseButton button) =>
        button switch
        {
            MouseButton.Right => RawPointerEventType.RightButtonDown,
            MouseButton.Middle => RawPointerEventType.MiddleButtonDown,
            _ => RawPointerEventType.LeftButtonDown,
        };

    private static RawPointerEventType UpType(MouseButton button) =>
        button switch
        {
            MouseButton.Right => RawPointerEventType.RightButtonUp,
            MouseButton.Middle => RawPointerEventType.MiddleButtonUp,
            _ => RawPointerEventType.LeftButtonUp,
        };
}
