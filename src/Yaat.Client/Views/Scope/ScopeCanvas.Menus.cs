using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope canvas's pointer menus: the action modifier opens an aircraft's flight plan in place of the select, a press
/// on the empty map clears the selection, a right press on stacked targets offers a picker, and a right click on the map
/// raises <see cref="MapRightClicked"/> through a <see cref="RightClickGesture"/> so the same button still pans.
/// </summary>
public sealed partial class ScopeCanvas
{
    private readonly RightClickGesture _rightClick = new();
    private readonly RightClickPicker _rightClickPicker = new();

    /// <summary>
    /// Fired in place of <see cref="AircraftLeftClicked"/> when the left click on a target or its block holds the platform's
    /// action modifier: the aircraft's flight plan opens and nothing is selected, no field runs and no lock toggles.
    /// </summary>
    public event Action<string>? AircraftCtrlClicked;

    /// <summary>
    /// Fired on a left press that landed on no aircraft, block or indicator, after the fill and the measuring tool had
    /// their chance: the selection clears.
    /// </summary>
    public event Action? EmptySpaceClicked;

    /// <summary>Fired on a right click on the map, at the press position, once the pan the press may have started ends it.</summary>
    public event Action<LatLon, Point>? MapRightClicked;

    /// <summary>The right-click target picker while it is open, or null.</summary>
    public ContextMenu? ActiveRightClickPicker => _rightClickPicker.Menu;

    /// <summary>
    /// Every aircraft a right click at <paramref name="pos"/> lands on, in the order the picker lists them: those whose block
    /// contains the point (topmost first), then those whose symbol is within <see cref="ScopeFrame.TargetHitRadiusPx"/>
    /// (nearest first). An aircraft hit both ways is listed once, as its block hit.
    /// </summary>
    public IReadOnlyList<RightClickTarget> FindRightClickTargets(Point pos)
    {
        ScopeFrame frame = CurrentFrame;
        float x = (float)pos.X;
        float y = (float)pos.Y;
        var targets = new List<RightClickTarget>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (AircraftModel ac in frame.BlocksAt(x, y))
        {
            if (listed.Add(ac.Callsign))
            {
                targets.Add(RightClickTarget.ForAircraft(ac.Callsign, ac.DisplayAircraftType, viaDataBlock: true));
            }
        }

        foreach (AircraftModel ac in frame.TargetsNear(x, y))
        {
            if (listed.Add(ac.Callsign))
            {
                targets.Add(RightClickTarget.ForAircraft(ac.Callsign, ac.DisplayAircraftType, viaDataBlock: false));
            }
        }

        return targets;
    }

    // A right press on two or more targets opens the picker in place of the nearest one's menu. A left press with the
    // action modifier opens the flight plan of the target or block under it. Shift keeps its own press: the block-hide rung
    // sits above this one, and an entry the press fills took it before either. Every right press abandons a gesture whose
    // release never came, so a lost capture cannot open the map menu later.
    partial void PressMenuGesture(in ScopePress press, ref bool handled)
    {
        if (press.Props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            _rightClick.Cancel();
        }

        if (handled)
        {
            return;
        }

        if (press.Props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            handled = OpensFlightPlan(press);
            return;
        }

        if (press.Props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            handled = PicksStackedTarget(press);
        }
    }

    // A right press on two or more targets offers them in the picker; one or none stays with the aircraft rung above and the
    // map's own press below.
    private bool PicksStackedTarget(in ScopePress press)
    {
        Point pressPos = press.Pos;
        IReadOnlyList<RightClickTarget> targets = FindRightClickTargets(pressPos);
        if (targets.Count < 2)
        {
            return false;
        }

        _rightClickPicker.Open(this, pressPos, targets, target => RaiseRightClickTarget(target, pressPos));
        return true;
    }

    // A left press with the platform's action modifier on a target, its block or an indicator over it: the flight plan
    // opens instead of the select, the field's action, the indicator's toggle or the dwell lock. Shift belongs to the
    // block-hide rung above, so Ctrl+Shift leaves the press to it.
    private bool OpensFlightPlan(in ScopePress press)
    {
        KeyModifiers modifiers = press.Event.KeyModifiers;
        if ((!PlatformHelper.HasActionModifier(modifiers)) || modifiers.HasFlag(KeyModifiers.Shift))
        {
            return false;
        }

        ScopeFrame frame = CurrentFrame;
        if (frame.IndicatorPickAt((float)press.Pos.X, (float)press.Pos.Y) is { } pick)
        {
            AircraftCtrlClicked?.Invoke(pick.Aircraft.Callsign);
            return true;
        }

        if (HitAt(frame, press.Pos) is not { } hit)
        {
            return false;
        }

        AircraftCtrlClicked?.Invoke(hit.Callsign);
        return true;
    }

    // A right press no rung above took landed on the map: the gesture is recorded so its release may open the map menu, and
    // the base canvas starts the pan. A left press no rung above took and no aircraft, block, indicator or bubble under it
    // clears the selection.
    partial void PressEmptyMap(in ScopePress press, ref bool handled)
    {
        if (handled)
        {
            return;
        }

        if (press.Props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            _rightClick.Press(press.Pos);
            return;
        }

        // A press on a bubble dismisses it on release, so it must not clear the selection first.
        if ((press.Props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed) && (FindBubbleAt(press.Pos) is null))
        {
            EmptySpaceClicked?.Invoke();
        }
    }

    // A right press on the map that did not drag owes it the map menu, at the press position so a pixel of jitter cannot
    // move it; the release still ends the base canvas's pan. A release another rung took abandons the press.
    partial void ReleaseMenuGesture(in ScopeRelease release, ref bool handled)
    {
        if (handled)
        {
            _rightClick.Cancel();
            return;
        }

        if (release.Event.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        if (_rightClick.Release() is { } pressPos)
        {
            MapRightClicked?.Invoke(PointAt(pressPos), pressPos);
        }
    }

    /// <summary>Raises <see cref="AircraftRightClicked"/> for an aircraft chosen in the picker, at the original click position.</summary>
    private void RaiseRightClickTarget(RightClickTarget target, Point screenPos)
    {
        if (target.Callsign is not { } callsign)
        {
            return;
        }

        AircraftRightClicked?.Invoke(callsign, screenPos);
    }

    // A move between a right press and its release: past the drag threshold the press latches as a pan, so its release
    // opens no menu.
    partial void MoveMenuGesture(in ScopeMove move) => _rightClick.Move(move.Pos);

    partial void DisposeFeatures() => _rightClickPicker.Close();
}
