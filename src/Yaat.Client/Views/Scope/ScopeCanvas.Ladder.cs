using Avalonia;
using Avalonia.Input;

namespace Yaat.Client.Views.Scope;

/// <summary>One pointer press on the Scope canvas, as each rung of the press ladder reads it.</summary>
/// <param name="Event">The press event.</param>
/// <param name="Props">The pointer's properties at the press.</param>
/// <param name="Pos">Where the press landed, in canvas coordinates.</param>
internal readonly record struct ScopePress(PointerPressedEventArgs Event, PointerPointProperties Props, Point Pos);

/// <summary>One pointer release on the Scope canvas, as each rung of the release ladder reads it.</summary>
/// <param name="Event">The release event.</param>
/// <param name="Pos">Where the release landed, in canvas coordinates.</param>
internal readonly record struct ScopeRelease(PointerReleasedEventArgs Event, Point Pos);

/// <summary>One pointer move on the Scope canvas, as each move hook reads it.</summary>
/// <param name="Event">The move event; a hook that consumes the move marks it handled.</param>
/// <param name="Pos">Where the pointer is, in canvas coordinates.</param>
internal readonly record struct ScopeMove(PointerEventArgs Event, Point Pos);

/// <summary>
/// The Scope canvas's pointer ladders: each press, release and move offered to its rungs in a fixed order, the first rung that
/// takes a press or a release ending it.
/// </summary>
public sealed partial class ScopeCanvas
{
    /// <summary>
    /// Offers <paramref name="press"/> to each rung in turn: the gestures that own the pointer first, then the middle button,
    /// the measuring tool, the ring tool, the entry's fill, the block and menu gestures, the aircraft, the empty map and the
    /// bubbles. Returns whether a rung took it.
    /// </summary>
    private bool RunPressLadder(in ScopePress press)
    {
        bool handled = false;
        PressHeadingVector(in press, ref handled);
        PressDrawRoute(in press, ref handled);
        PressMiddle(in press, ref handled);
        PressMeasure(in press, ref handled);
        PressRangeRing(in press, ref handled);
        PressFill(in press, ref handled);
        PressBlockHide(in press, ref handled);
        PressMenuGesture(in press, ref handled);
        PressBlockDrag(in press, ref handled);
        PressAircraftRung(in press, ref handled);
        PressEmptyMap(in press, ref handled);
        PressBubble(in press, ref handled);
        return handled;
    }

    partial void PressHeadingVector(in ScopePress press, ref bool handled);

    partial void PressDrawRoute(in ScopePress press, ref bool handled);

    // A middle press: on an indicator, an aircraft or the map (OnMiddlePressed).
    private void PressMiddle(in ScopePress press, ref bool handled)
    {
        if (handled)
        {
            return;
        }

        if (press.Props.IsMiddleButtonPressed)
        {
            OnMiddlePressed(press.Pos);
            handled = true;
        }
    }

    // The measuring tool's presses (TryMeasurePress).
    private void PressMeasure(in ScopePress press, ref bool handled)
    {
        if (handled)
        {
            return;
        }

        if (TryMeasurePress(press.Props, press.Event.KeyModifiers, press.Pos))
        {
            handled = true;
        }
    }

    // A left press the entry takes as a fill (TryFillPress).
    private void PressFill(in ScopePress press, ref bool handled)
    {
        if (handled)
        {
            return;
        }

        if (press.Props.IsLeftButtonPressed && TryFillPress(press.Pos))
        {
            handled = true;
        }
    }

    partial void PressBlockHide(in ScopePress press, ref bool handled);

    partial void PressMenuGesture(in ScopePress press, ref bool handled);

    partial void PressBlockDrag(in ScopePress press, ref bool handled);

    partial void PressRangeRing(in ScopePress press, ref bool handled);

    // A left or right press on an aircraft (PressAircraft).
    private void PressAircraftRung(in ScopePress press, ref bool handled)
    {
        if (handled)
        {
            return;
        }

        PointerPointProperties props = press.Props;
        if (
            (props.IsLeftButtonPressed || props.IsRightButtonPressed)
            && PressAircraft(props.IsRightButtonPressed, press.Pos, press.Event.KeyModifiers)
        )
        {
            handled = true;
        }
    }

    partial void PressEmptyMap(in ScopePress press, ref bool handled);

    partial void PressBubble(in ScopePress press, ref bool handled);

    /// <summary>
    /// Offers <paramref name="release"/> to each release rung in turn, ahead of the measuring tool's release. Returns whether a
    /// rung took it.
    /// </summary>
    private bool RunReleaseLadder(in ScopeRelease release)
    {
        bool handled = false;
        ReleaseHeadingVector(in release, ref handled);
        ReleaseBlockDrag(in release, ref handled);
        ReleaseMenuGesture(in release, ref handled);
        ReleaseBubble(in release, ref handled);
        return handled;
    }

    partial void ReleaseHeadingVector(in ScopeRelease release, ref bool handled);

    partial void ReleaseBlockDrag(in ScopeRelease release, ref bool handled);

    partial void ReleaseMenuGesture(in ScopeRelease release, ref bool handled);

    partial void ReleaseBubble(in ScopeRelease release, ref bool handled);

    /// <summary>
    /// Offers <paramref name="move"/> to every move hook, ahead of the hover hit-test; a hook that consumes the move marks its
    /// event handled.
    /// </summary>
    private void RunMoveHooks(in ScopeMove move)
    {
        MoveHeadingVector(in move);
        MoveDrawRoute(in move);
        MoveBlockDrag(in move);
        MoveMapTools(in move);
        MoveMenuGesture(in move);
    }

    partial void MoveHeadingVector(in ScopeMove move);

    partial void MoveDrawRoute(in ScopeMove move);

    partial void MoveBlockDrag(in ScopeMove move);

    partial void MoveMapTools(in ScopeMove move);

    partial void MoveMenuGesture(in ScopeMove move);
}
