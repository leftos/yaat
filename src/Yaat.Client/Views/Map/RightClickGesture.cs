using Avalonia;

namespace Yaat.Client.Views.Map;

/// <summary>
/// Tells a right-click apart from a right-drag on a map canvas, so the same button can both open a
/// context menu and pan the view.
/// </summary>
/// <remarks>
/// The two gestures are indistinguishable at press time — only movement separates them — so a canvas
/// records the press here, starts panning immediately, and asks <see cref="Release" /> on button-up
/// whether a menu is owed. Deciding on press instead would mean a drag that happens to start on a
/// datablock, aircraft, or node never pans.
/// </remarks>
public sealed class RightClickGesture
{
    /// <summary>Squared pixel distance the pointer may travel and still count as a click, not a drag.</summary>
    private const double DragThresholdSq = 25.0;

    private bool _isDown;
    private bool _hasDragged;
    private Point _pressPosition;

    /// <summary>True between a right press and its release.</summary>
    public bool IsDown => _isDown;

    /// <summary>True once the pointer has moved far enough for this press to be a drag.</summary>
    public bool HasDragged => _hasDragged;

    /// <summary>Records a right-button press at <paramref name="position" />.</summary>
    public void Press(Point position)
    {
        _isDown = true;
        _hasDragged = false;
        _pressPosition = position;
    }

    /// <summary>
    /// Feeds pointer movement in. Once the pointer has travelled past the threshold the press is latched
    /// as a drag and cannot go back to being a click, so jitter back toward the origin won't resurrect
    /// a menu mid-pan.
    /// </summary>
    public void Move(Point position)
    {
        if (!_isDown || _hasDragged)
        {
            return;
        }

        var dx = position.X - _pressPosition.X;
        var dy = position.Y - _pressPosition.Y;
        if ((dx * dx) + (dy * dy) > DragThresholdSq)
        {
            _hasDragged = true;
        }
    }

    /// <summary>
    /// Ends the gesture.
    /// </summary>
    /// <returns>
    /// The press position when this was a click and a context menu is owed; null when it was a drag, or
    /// when no press was active. The press position is returned rather than the release position so a
    /// pixel of jitter cannot slide the menu off a small target like a ground node.
    /// </returns>
    public Point? Release()
    {
        if (!_isDown)
        {
            return null;
        }

        _isDown = false;
        return _hasDragged ? null : _pressPosition;
    }

    /// <summary>Abandons the gesture without owing a menu, e.g. when the pointer leaves the canvas.</summary>
    public void Cancel()
    {
        _isDown = false;
        _hasDragged = false;
    }
}
