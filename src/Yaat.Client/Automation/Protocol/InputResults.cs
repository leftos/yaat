namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// Where pointer input landed: the window's node id and name (its title, or its type name when it has none, as for a popup
/// window), the point in DIPs from the top-left of its client area, and its DIP-to-pixel scale.
/// </summary>
public sealed record PointerSite(int WindowNodeId, string Window, double X, double Y, double RenderScaling);

/// <summary>
/// The result of <c>click</c>: the element clicked (a TextBlock's interactive ancestor when it has one), what ran —
/// <c>command</c>, <c>toggle</c>, <c>flyout</c>, <c>click_event</c>, <c>menu_item</c>, <c>select</c> or <c>pointer</c> —
/// and the element's centre, where a real click would have landed.
/// </summary>
public sealed record ClickResult(int NodeId, string Action, PointerSite Site);

/// <summary>The result of <c>click_point</c>: the element the point hit, which received the pointer events, and the point.</summary>
public sealed record ClickPointResult(int NodeId, string ElementType, PointerSite Site);

/// <summary>The result of <c>hover</c>: the element under the point, the point, and how long the pointer rested there.</summary>
public sealed record HoverResult(int NodeId, string ElementType, PointerSite Site, int DurationMs);

/// <summary>
/// The result of <c>drag</c>: the element the press hit, the press point, the release point in the same window, the button,
/// the moves between them, the rest before the release, and the whole gesture's time from the press to the release.
/// </summary>
public sealed record DragResult(
    int NodeId,
    string ElementType,
    PointerSite From,
    double ToX,
    double ToY,
    string Button,
    int Steps,
    int HoldMs,
    int DurationMs
);

/// <summary>The result of <c>send_keys</c>: the element that held the focus when the first stroke went out, and the stroke count.</summary>
public sealed record SendKeysResult(int NodeId, int Strokes);

/// <summary>The result of <c>set_text</c>: the text box written and its text.</summary>
public sealed record SetTextResult(int NodeId, string Text);

/// <summary>The result of <c>focus</c>: the element that took the focus.</summary>
public sealed record FocusResult(int NodeId);
