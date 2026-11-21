using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Yaat.Client.Views.Scope.FieldMenus.Crc;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// A CRC FDB menu (<see cref="CrcFdbMenu"/>) as the Scope's open field surface: a light-dismiss popup in the canvas's overlay
/// layer, placed as CRC's BaseFdbMenu.GetLocationOptions does: its left edge at the block's right, its list's middle level
/// with the middle of the block's FDB line <see cref="CrcFdbMenu.FdbAlignLine"/>; flipped to put its right edge at the
/// block's left where the right has no room, and slid inside the window. At the pointer when no block is drawn. The menu's X
/// cell closes it.
/// </summary>
internal sealed class CrcMenuPopup : IScopeFieldSurface
{
    private readonly double? _lineMiddleY;
    private OverlayLayer? _overlay;

    /// <param name="menu">The menu to show.</param>
    /// <param name="lineMiddleY">
    /// The middle of the block's FDB line the menu aligns its list with, in the anchor's coordinates; null when no block is drawn.
    /// </param>
    public CrcMenuPopup(CrcFdbMenu menu, double? lineMiddleY)
    {
        Menu = menu;
        _lineMiddleY = lineMiddleY;
        Popup = new Popup
        {
            Placement = PlacementMode.Pointer,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = true,
            ShouldUseOverlayLayer = true,
            Child = menu,
        };
        menu.CloseRequested += Close;
    }

    /// <summary>The menu shown.</summary>
    public CrcFdbMenu Menu { get; }

    /// <summary>The popup holding it.</summary>
    public Popup Popup { get; }

    /// <inheritdoc/>
    public bool IsOpen => Popup.IsOpen;

    /// <inheritdoc/>
    public event EventHandler<EventArgs>? Closed
    {
        add => Popup.Closed += value;
        remove => Popup.Closed -= value;
    }

    /// <inheritdoc/>
    public bool Open(Control anchor, Rect? block)
    {
        if (OverlayLayer.GetOverlayLayer(anchor) is not { } overlay)
        {
            return false;
        }

        if ((block is { } bounds) && (_lineMiddleY is { } middle))
        {
            Popup.Placement = PlacementMode.AnchorAndGravity;
            Popup.PlacementRect = new Rect(bounds.X, middle - Menu.ListMiddleY, bounds.Width, 1);
            Popup.PlacementAnchor = PopupAnchor.TopRight;
            Popup.PlacementGravity = PopupGravity.BottomRight;
            Popup.PlacementConstraintAdjustment =
                PopupPositionerConstraintAdjustment.FlipX | PopupPositionerConstraintAdjustment.SlideY | PopupPositionerConstraintAdjustment.SlideX;
        }

        Popup.PlacementTarget = anchor;
        overlay.Children.Add(Popup);
        _overlay = overlay;
        Popup.Closed += LeaveOverlay;
        Popup.IsOpen = true;
        return true;
    }

    /// <inheritdoc/>
    public void Close() => Popup.IsOpen = false;

    // Once closed, the popup leaves the overlay it opened in, and this handler unsubscribes itself.
    private void LeaveOverlay(object? sender, EventArgs e)
    {
        Popup.Closed -= LeaveOverlay;
        _overlay?.Children.Remove(Popup);
        _overlay = null;
    }
}
