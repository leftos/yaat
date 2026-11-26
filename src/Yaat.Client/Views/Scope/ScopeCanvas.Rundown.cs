using Avalonia;
using Avalonia.Input;
using SkiaSharp;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The RUNDOWN list's drag: a left press on any of its lines takes the pointer, each move with the left button held moves the
/// list by the pointer's travel, kept on the canvas, and the left release, a move with the button up or a lost capture ends
/// it. The frame lays the list out at the same clamped origin, so one saved in a larger window still draws on the canvas.
/// </summary>
public sealed partial class ScopeCanvas
{
    // How much of the list's title stays on the canvas: its origin never passes the right or bottom edge less these.
    private const double RundownKeepOnCanvasX = 40;
    private const double RundownKeepOnCanvasY = 20;

    // The canvas size below which it is not laid out yet, so nothing clamps.
    private const double RundownClampMinWidth = 40;
    private const double RundownClampMinHeight = 20;

    private bool _isDraggingRundown;
    private Point _dragRundownStartOrigin;
    private Point _dragRundownStartMouse;

    /// <summary>
    /// Fired on a right press on a RUNDOWN row, with the row and the press position: the entry's menu opens there. A right press
    /// on the title or past a line's text is the map's, as before.
    /// </summary>
    public event Action<RundownRow, Point>? RundownRowRightClicked;

    // A left press on the list starts its drag and is taken, so it neither selects, pans nor clears the selection. A press
    // where the list draws nothing (no rows, past a line's text) falls through.
    partial void PressRundownDrag(in ScopePress press, ref bool handled)
    {
        if (handled || (press.Props.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed))
        {
            return;
        }

        if (!CurrentFrame.Rundown.Contains((float)press.Pos.X, (float)press.Pos.Y))
        {
            return;
        }

        _isDraggingRundown = true;
        _dragRundownStartOrigin = ClampRundownOrigin(RundownOrigin);
        _dragRundownStartMouse = press.Pos;
        handled = true;
    }

    // A right press on a row opens its entry menu and is taken, so it neither pans nor opens the map's or an aircraft's menu.
    partial void PressRundownMenu(in ScopePress press, ref bool handled)
    {
        if (handled || (press.Props.PointerUpdateKind != PointerUpdateKind.RightButtonPressed))
        {
            return;
        }

        if (CurrentFrame.Rundown.RowAt((float)press.Pos.X, (float)press.Pos.Y) is not { } row)
        {
            return;
        }

        RundownRowRightClicked?.Invoke(row, press.Pos);
        handled = true;
    }

    // The list follows the pointer while the left button is down; a move with the button up ends the drag, since a release
    // that went to an overlay never reaches the canvas.
    partial void MoveRundownDrag(in ScopeMove move)
    {
        if (!_isDraggingRundown)
        {
            return;
        }

        if (!move.Event.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDraggingRundown = false;
            return;
        }

        RundownOrigin = ClampRundownOrigin(_dragRundownStartOrigin + (move.Pos - _dragRundownStartMouse));
        _lastFrame = null;
        MarkDirty();
        move.Event.Handled = true;
    }

    // The left release ends the drag; it moves the list no further, so the last move's origin is the one it keeps. Another
    // button's release during the drag belongs to that button's gesture.
    partial void ReleaseRundownDrag(in ScopeRelease release, ref bool handled)
    {
        if ((!_isDraggingRundown) || (release.Event.InitialPressMouseButton != MouseButton.Left))
        {
            return;
        }

        _isDraggingRundown = false;
        handled = true;
    }

    private static SKPoint ToSkPoint(Point point) => new((float)point.X, (float)point.Y);

    // A lost capture (another control took the pointer) ends the drag where the last move left it.
    private void EndRundownDrag() => _isDraggingRundown = false;

    // Keeps the origin inside [0, width - 40] x [0, height - 20]; before the canvas is laid out (under 40 x 20) no clamp
    // applies. The drag and the frame's layout share it.
    private Point ClampRundownOrigin(Point origin)
    {
        Size size = Bounds.Size;
        if ((size.Width < RundownClampMinWidth) || (size.Height < RundownClampMinHeight))
        {
            return origin;
        }

        return new Point(Math.Clamp(origin.X, 0, size.Width - RundownKeepOnCanvasX), Math.Clamp(origin.Y, 0, size.Height - RundownKeepOnCanvasY));
    }
}
