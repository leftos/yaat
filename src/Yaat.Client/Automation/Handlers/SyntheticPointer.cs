// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// Raises a synthetic mouse click on one element: a press and a release per click with one <see cref="IPointer"/>, the
/// button's pressed state, the keyboard modifiers and the click count, positioned in the <see cref="TopLevel"/>'s
/// coordinates. The release carries the pressed button, so handlers reading <c>InitialPressMouseButton</c> see it.
/// </summary>
public static class SyntheticPointer
{
    public static void Click(Interactive target, TopLevel topLevel, Point rootPosition, PointerClick click)
    {
        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        RawInputModifiers keys = ToRaw(click.Modifiers);
        ulong timestamp = unchecked((ulong)Environment.TickCount64);
        for (int count = 1; count <= click.ClickCount; count++)
        {
            // Both event-args constructors are [Unstable] in Avalonia 12.1: re-check their signatures on every Avalonia upgrade.
            var pressed = new PointerPointProperties(keys | ButtonFlag(click.Button), PressedKind(click.Button));
            target.RaiseEvent(new PointerPressedEventArgs(target, pointer, topLevel, rootPosition, timestamp++, pressed, click.Modifiers, count));
            var released = new PointerPointProperties(keys, ReleasedKind(click.Button));
            target.RaiseEvent(
                new PointerReleasedEventArgs(target, pointer, topLevel, rootPosition, timestamp++, released, click.Modifiers, click.Button)
            );
            // A real mouse device releases its capture after the release event; a control that captured on press lets go here.
            pointer.Capture(null);
        }
    }

    private static RawInputModifiers ToRaw(KeyModifiers modifiers)
    {
        RawInputModifiers raw = RawInputModifiers.None;
        raw |= modifiers.HasFlag(KeyModifiers.Alt) ? RawInputModifiers.Alt : RawInputModifiers.None;
        raw |= modifiers.HasFlag(KeyModifiers.Control) ? RawInputModifiers.Control : RawInputModifiers.None;
        raw |= modifiers.HasFlag(KeyModifiers.Shift) ? RawInputModifiers.Shift : RawInputModifiers.None;
        raw |= modifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.None;
        return raw;
    }

    private static RawInputModifiers ButtonFlag(MouseButton button) =>
        button switch
        {
            MouseButton.Right => RawInputModifiers.RightMouseButton,
            MouseButton.Middle => RawInputModifiers.MiddleMouseButton,
            _ => RawInputModifiers.LeftMouseButton,
        };

    private static PointerUpdateKind PressedKind(MouseButton button) =>
        button switch
        {
            MouseButton.Right => PointerUpdateKind.RightButtonPressed,
            MouseButton.Middle => PointerUpdateKind.MiddleButtonPressed,
            _ => PointerUpdateKind.LeftButtonPressed,
        };

    private static PointerUpdateKind ReleasedKind(MouseButton button) =>
        button switch
        {
            MouseButton.Right => PointerUpdateKind.RightButtonReleased,
            MouseButton.Middle => PointerUpdateKind.MiddleButtonReleased,
            _ => PointerUpdateKind.LeftButtonReleased,
        };
}
