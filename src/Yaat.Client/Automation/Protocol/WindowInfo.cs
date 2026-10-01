namespace Yaat.Client.Automation.Protocol;

/// <summary>One entry of <c>list_windows</c>: a window, an owned window or an open popup.</summary>
/// <param name="NodeId">The element id, stable across calls for as long as the element lives.</param>
/// <param name="Title">The window title; null for a popup or an untitled window.</param>
/// <param name="TypeName">The element's runtime type name (e.g. <c>MainWindow</c>, <c>PopupRoot</c>).</param>
/// <param name="IsPopup">True for a popup, whether a popup window or a popup in the window's overlay layer.</param>
/// <param name="OwnerId">The node id of the owning window (a popup's window, an owned window's owner); null for a top window.</param>
/// <param name="Bounds">
/// Size in device-independent pixels. A window or popup window reports its client area at the origin; an overlay popup
/// reports its rectangle in its owner window.
/// </param>
/// <param name="IsActive">Whether the window is the active (foreground) window.</param>
/// <param name="IsVisible">Whether the element is visible.</param>
public sealed record WindowInfo(
    int NodeId,
    string? Title,
    string TypeName,
    bool IsPopup,
    int? OwnerId,
    BoundsInfo Bounds,
    bool IsActive,
    bool IsVisible
);
