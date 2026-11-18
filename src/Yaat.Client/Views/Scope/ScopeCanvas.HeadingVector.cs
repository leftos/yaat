using Avalonia;
using Avalonia.Input;
using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope canvas's elastic heading vector: a press on a EuroScope AHDG field starts it and the pointer is a cross while it
/// is up. A drag past the threshold confirms at the release; a release short of it arms the vector, and any left press then
/// confirms the heading toward its press point, whatever is under it, while an Alt+left press and the armed measuring tool do
/// not intercept it. A right press or Esc cancels without sending.
/// </summary>
public sealed partial class ScopeCanvas
{
    /// <summary>The pointer while a heading vector is up.</summary>
    internal static readonly Cursor HeadingVectorCursor = new(StandardCursorType.Cross);

    private ScopeHeadingVectorState? _headingVector;

    /// <summary>True while a heading vector is up.</summary>
    internal bool IsHeadingVectorActive => _headingVector is not null;

    /// <summary>Fired with the callsign and the confirmed magnetic heading, in [1, 360], when the vector is confirmed.</summary>
    public event Action<string, int>? HeadingVectorConfirmed;

    /// <summary>Starts a heading vector for <paramref name="callsign"/> at the canvas point the AHDG field was pressed at.</summary>
    internal void EnterHeadingVector(string callsign, Point press)
    {
        _headingVector = ScopeHeadingVectorState.Begin(callsign, PointAt(press), press);
        RefreshHeadingVectorCursor();
        Focus();
        MarkDirty();
    }

    /// <summary>Takes the heading vector down without sending anything.</summary>
    internal void CancelHeadingVector()
    {
        if (_headingVector is not null)
        {
            LeaveHeadingVector();
        }
    }

    partial void PressHeadingVector(in ScopePress press, ref bool handled)
    {
        PointerUpdateKind kind = press.Props.PointerUpdateKind;
        if (
            handled
            || (kind is not (PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.RightButtonPressed))
            || (_headingVector is not { ButtonHeld: false } vector)
        )
        {
            return;
        }

        handled = true;
        ApplyHeadingVector(vector.Pressed(kind == PointerUpdateKind.LeftButtonPressed, PointAt(press.Pos)));
    }

    partial void ReleaseHeadingVector(in ScopeRelease release, ref bool handled)
    {
        if (handled || (_headingVector is not { ButtonHeld: true } vector) || (release.Event.InitialPressMouseButton != MouseButton.Left))
        {
            return;
        }

        handled = true;
        ApplyHeadingVector(vector.Released(PointAt(release.Pos)));
    }

    partial void MoveHeadingVector(in ScopeMove move)
    {
        if (_headingVector is not { } vector)
        {
            return;
        }

        var moved = new ScopeHeadingVectorTransition(vector.MovedTo(PointAt(move.Pos), move.Pos.X, move.Pos.Y), ScopeHeadingVectorOutcome.None);
        ApplyHeadingVector(moved);
    }

    partial void EscapeHeadingVector(ref bool handled)
    {
        if (handled || (_headingVector is null))
        {
            return;
        }

        handled = true;
        LeaveHeadingVector();
    }

    partial void CollectHeadingVector(ScopeFrame frame, ref ScopeOverlays overlays)
    {
        if (
            (_headingVector is not { } vector)
            || (Aircraft?.FirstOrDefault(a => string.Equals(a.Callsign, vector.Callsign, StringComparison.OrdinalIgnoreCase)) is not { } ac)
        )
        {
            return;
        }

        overlays = overlays with
        {
            HeadingVector = new ScopeHeadingVectorOverlay(
                ac.Callsign,
                ac.Position,
                ac.Heading.Degrees,
                ac.GroundSpeed,
                vector.Cursor,
                AircraftPerformance.TurnRate(ac.AircraftType, ac.Category)
            ),
        };
    }

    // Takes the transition's state and does what it asks: a confirm sends the heading and leaves, nothing else redraws.
    private void ApplyHeadingVector(in ScopeHeadingVectorTransition transition)
    {
        if (transition.Outcome == ScopeHeadingVectorOutcome.Confirm)
        {
            _headingVector = transition.State;
            ConfirmHeadingVector();
            return;
        }

        _headingVector = transition.State;
        RefreshHeadingVectorCursor();
        MarkDirty();
    }

    // Sends the heading the cursor names, as seen from the aircraft's position, and leaves the vector.
    private void ConfirmHeadingVector()
    {
        ScopeHeadingVectorState? ended = _headingVector;
        LeaveHeadingVector();
        if (ended is not { } vector)
        {
            return;
        }

        AircraftModel? ac = Aircraft?.FirstOrDefault(a => string.Equals(a.Callsign, vector.Callsign, StringComparison.OrdinalIgnoreCase));
        if (ac is not null)
        {
            HeadingVectorConfirmed?.Invoke(ac.Callsign, ScopeHeadingVectorState.MagneticHeading(ac.Position, vector.Cursor));
        }
    }

    private void LeaveHeadingVector()
    {
        _headingVector = null;
        RefreshHeadingVectorCursor();
        MarkDirty();
    }

    // The pointer the modes still up ask for: the heading vector's cross, else the route's, else the default.
    private void RefreshHeadingVectorCursor()
    {
        Cursor =
            IsHeadingVectorActive ? HeadingVectorCursor
            : IsDrawingRoute ? DrawRouteCursor
            : null;
    }
}
