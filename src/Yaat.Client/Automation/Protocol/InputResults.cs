namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// The result of <c>click</c>: the element clicked (a TextBlock's interactive ancestor when it has one) and what ran —
/// <c>command</c>, <c>toggle</c>, <c>flyout</c>, <c>click_event</c>, <c>menu_item</c>, <c>select</c> or <c>pointer</c>.
/// </summary>
public sealed record ClickResult(int NodeId, string Action);

/// <summary>The result of <c>click_point</c>: the element the point hit, which received the pointer events.</summary>
public sealed record ClickPointResult(int NodeId, string ElementType);

/// <summary>The result of <c>send_keys</c>: the element that held the focus when the first stroke went out, and the stroke count.</summary>
public sealed record SendKeysResult(int NodeId, int Strokes);

/// <summary>The result of <c>set_text</c>: the text box written and its text.</summary>
public sealed record SetTextResult(int NodeId, string Text);

/// <summary>The result of <c>focus</c>: the element that took the focus.</summary>
public sealed record FocusResult(int NodeId);
