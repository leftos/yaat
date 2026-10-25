namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// One entry of <c>list_windows</c>: a window, an owned window, an open popup, or an in-window overlay (a <c>Border</c>
/// carrying <c>ListWindowsHandler.InWindowOverlayClass</c>, listed while it shows).
/// </summary>
/// <param name="NodeId">The element id, stable across calls for as long as the element lives.</param>
/// <param name="Title">
/// The window title, or an in-window overlay's <c>AutomationProperties.Name</c>; null for an overlay popup or an untitled window.
/// </param>
/// <param name="TypeName">The element's runtime type name (e.g. <c>MainWindow</c>, <c>OverlayPopupHost</c>, <c>Border</c>).</param>
/// <param name="IsPopup">
/// True for a popup, whether a popup window or a popup in the window's overlay layer, and for an in-window overlay.
/// </param>
/// <param name="OwnerId">
/// The node id of the owning window (a popup's or in-window overlay's window, an owned window's owner); null for a top window.
/// </param>
/// <param name="Bounds">
/// Size in device-independent pixels. A window or popup window reports its client area at the origin; an overlay popup or
/// in-window overlay reports its rectangle in its owner window.
/// </param>
/// <param name="IsActive">Whether the window is the active (foreground) window.</param>
/// <param name="IsVisible">Whether the element is visible.</param>
/// <param name="Hwnd">
/// The native window handle (an HWND on Windows), for capturing the window from outside the client; 0 when the platform gives
/// none, as for an overlay popup or in-window overlay, which lives inside its window.
/// </param>
public sealed record WindowInfo(
    int NodeId,
    string? Title,
    string TypeName,
    bool IsPopup,
    int? OwnerId,
    BoundsInfo Bounds,
    bool IsActive,
    bool IsVisible,
    long Hwnd
);
