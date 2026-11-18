using Avalonia;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope canvas's datablock gestures: a drag on a block that moves it, and a Shift+click on a block or a symbol that hides
/// or shows it. Both write the scope's own <c>DataBlockState</c>, so a block moved or hidden from a menu draws the same way.
/// </summary>
public sealed partial class ScopeCanvas
{
    /// <summary>The squared pointer travel a block drag needs before the block follows it (the Radar's 4 px).</summary>
    private const double BlockDragThresholdSq = 16.0;

    private bool _isDraggingBlock;
    private string? _dragBlockCallsign;
    private SKPoint _dragBlockStartOffset;
    private Point _dragBlockStartMouse;
    private bool _dragBlockThresholdMet;

    /// <summary>
    /// Asked at each datablock gesture: true suspends the drag and the Shift+click hide, as the armed Rings tool does, so its
    /// own press places a ring and leaves the block as it is. Null never suspends.
    /// </summary>
    internal Func<bool>? BlockGesturesSuspended { get; set; }

    // Whether another tool owns the left press, so neither the drag nor the hide acts on it.
    private bool IsBlockGestureSuspended => BlockGesturesSuspended?.Invoke() == true;

    /// <summary>
    /// Drops the kept frame, whose blocks and hit-test read the display state from before the change, so the next one is built
    /// fresh.
    /// </summary>
    internal void InvalidateDataBlockFrame()
    {
        _lastFrame = null;
        MarkDirty();
    }

    // Shift+left on a block or a symbol, without the action modifier, hides the block or shows it again: no selection, no
    // menu and no drag. A press on an indicator cell, an action field or an ERAM block's Field A keeps its own click.
    partial void PressBlockHide(in ScopePress press, ref bool handled)
    {
        if (handled || IsBlockGestureSuspended || !IsShiftClickWithoutAction(press.Event.KeyModifiers))
        {
            return;
        }

        ScopeFrame frame = CurrentFrame;
        if ((HitAt(frame, press.Pos) is not { } hit) || !IsDatablockGestureAt(frame, hit, press.Pos, allowEramFieldA: false))
        {
            return;
        }

        if (DataBlockState is { } state)
        {
            state.ToggleHidden(hit.Callsign);
        }

        _lastFrame = null;
        MarkDirty();
        handled = true;
    }

    // A left press on a block arms a drag; the block follows the pointer once it travels past the threshold, so a plain click
    // writes nothing. The press goes on to the aircraft rung, which selects the track and toggles an ERAM dwell lock as usual.
    partial void PressBlockDrag(in ScopePress press, ref bool handled)
    {
        if (handled || IsBlockGestureSuspended || (press.Props.PointerUpdateKind is not PointerUpdateKind.LeftButtonPressed))
        {
            return;
        }

        ScopeFrame frame = CurrentFrame;
        float x = (float)press.Pos.X;
        float y = (float)press.Pos.Y;
        if ((frame.BlockAt(x, y) is not { } hit) || !IsDatablockGestureAt(frame, hit, press.Pos, allowEramFieldA: true))
        {
            return;
        }

        _isDraggingBlock = true;
        _dragBlockCallsign = hit.Callsign;
        _dragBlockStartOffset = frame.Tracks.First(t => ReferenceEquals(t.Aircraft, hit)).BlockOffset;
        _dragBlockStartMouse = press.Pos;
        _dragBlockThresholdMet = false;
    }

    // Whether a left press at the point belongs to the datablock gestures: not on an indicator cell toggled ahead of the block,
    // not on a field with an action, and, unless the ERAM callsign field's dwell lock is allowed, not on it. A press near a
    // symbol is on no field at all, so the Shift+click hides from the symbol.
    private static bool IsDatablockGestureAt(ScopeFrame frame, AircraftModel hit, Point pos, bool allowEramFieldA)
    {
        float x = (float)pos.X;
        float y = (float)pos.Y;
        if (frame.IndicatorPickAt(x, y) is not null)
        {
            return false;
        }

        if (!allowEramFieldA && IsEramCallsignOf(frame, hit, x, y))
        {
            return false;
        }

        return FieldActionOf(frame, hit, x, y) == ScopeFieldAction.None;
    }

    // Shift held without the action modifier (Ctrl, Cmd on macOS, which belongs to the flight-plan editor's click).
    private static bool IsShiftClickWithoutAction(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift) && !PlatformHelper.HasActionModifier(modifiers);

    // The drag follows the pointer while the left button is down; a move with the button up ends it, since a release that went
    // to an overlay never reaches the canvas.
    partial void MoveBlockDrag(in ScopeMove move)
    {
        if (!_isDraggingBlock)
        {
            return;
        }

        if (!move.Event.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndBlockDrag();
            return;
        }

        float dx = (float)(move.Pos.X - _dragBlockStartMouse.X);
        float dy = (float)(move.Pos.Y - _dragBlockStartMouse.Y);
        if (!_dragBlockThresholdMet && (((dx * dx) + (dy * dy)) > BlockDragThresholdSq))
        {
            _dragBlockThresholdMet = true;
        }

        if (_dragBlockThresholdMet && (_dragBlockCallsign is { } callsign) && (DataBlockState is { } state))
        {
            state.SetManualOffset(callsign, new SKPoint(_dragBlockStartOffset.X + dx, _dragBlockStartOffset.Y + dy));
            _lastFrame = null;
            MarkDirty();
        }

        move.Event.Handled = true;
    }

    // A release ends the drag; it moves the block no further, so the last move's offset is the one it keeps.
    partial void ReleaseBlockDrag(in ScopeRelease release, ref bool handled)
    {
        if (!_isDraggingBlock)
        {
            return;
        }

        EndBlockDrag();
        handled = true;
    }

    private void EndBlockDrag()
    {
        _isDraggingBlock = false;
        _dragBlockCallsign = null;
        _dragBlockThresholdMet = false;
    }
}
