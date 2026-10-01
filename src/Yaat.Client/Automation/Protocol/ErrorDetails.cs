namespace Yaat.Client.Automation.Protocol;

/// <summary><see cref="AutomationError.Details"/> of an error about one request parameter: <c>{"param": ...}</c>.</summary>
public sealed record ParamErrorDetails(string Param);

/// <summary><see cref="AutomationError.Details"/> of a <c>STALE_NODE</c> error: <c>{"nodeId": ...}</c>.</summary>
public sealed record NodeErrorDetails(int NodeId);

/// <summary><see cref="AutomationError.Details"/> of an <c>UNSUPPORTED_OPERATION</c> error.</summary>
public sealed record UnsupportedOperationDetails(string Operation, string ElementType);

/// <summary><see cref="AutomationError.Details"/> of a <c>NO_MATCH</c> error: the selector that matched nothing.</summary>
public sealed record SelectorErrorDetails(string Selector);

/// <summary><see cref="AutomationError.Details"/> of an <c>INVALID_SELECTOR</c> error: the zero-based offset of the fault.</summary>
public sealed record InvalidSelectorDetails(string Selector, int Position);

/// <summary><see cref="AutomationError.Details"/> of an <c>AMBIGUOUS_SELECTOR</c> error: how many elements matched.</summary>
public sealed record AmbiguousSelectorDetails(string Selector, int MatchCount);

/// <summary><see cref="AutomationError.Details"/> of an error about one element: <c>ELEMENT_DISABLED</c>, <c>NOT_FOCUSABLE</c>.</summary>
public sealed record ElementErrorDetails(int NodeId, string ElementType);

/// <summary><see cref="AutomationError.Details"/> of an <c>OUT_OF_BOUNDS</c> error: the point and the window's client size, in DIPs.</summary>
public sealed record PointErrorDetails(double X, double Y, double Width, double Height);

/// <summary><see cref="AutomationError.Details"/> of a malformed <c>send_keys</c> string: the zero-based position of the fault.</summary>
public sealed record KeysErrorDetails(string Keys, int Position);
